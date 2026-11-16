// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Encryption;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Encrypts a PDF using CMS public-key (certificate-based / enveloped) encryption
    /// as defined in PDF 1.7 §7.6.5.
    ///
    /// <para>
    /// The PDF /Filter is set to /Adobe.PubSec and /SubFilter to:
    /// <list type="bullet">
    ///   <item>/adbe.pkcs7.s4 for RC4 128-bit (legacy; PDF 1.4 readers).</item>
    ///   <item>/adbe.pkcs7.s5 for AES-128 or AES-256 (PDF 1.6+ readers).</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Each recipient's X.509 certificate is used to wrap a 20-byte random seed followed by
    /// a 4-byte permissions mask inside a CMS KeyTransRecipientInfo (RSA PKCS#1 v1.5) — the
    /// layout <see cref="PdfPublicKeyDecryptor.SeedOffset"/> states. The document encryption key
    /// is then derived by <see cref="PdfPublicKeyDecryptor.DeriveEncryptionKey"/> as
    /// <c>H( seed || recipient1_DER || recipient2_DER || … [|| FF FF FF FF] )[0..keyBytes-1]</c>,
    /// where H is SHA-1 for the 128-bit algorithms and <b>SHA-256</b> for AES-256 (ISO 32000-2
    /// §7.6.5.3) — SHA-1 cannot supply the 32 bytes AES-256 needs — and the four <c>0xFF</c>
    /// bytes are step c)'s, for a document whose metadata stays in plaintext.
    /// </para>
    ///
    /// <para>
    /// <b>Read back by <see cref="PdfPublicKeyDecryptor"/></b>, with the recipient's private key.
    /// <c>PdfDecryptor</c> is the <c>/Standard</c> (password) handler and refuses this one.
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   byte[] cert = File.ReadAllBytes("recipient.cer");
    ///   PdfPublicKeyEncryptor.Encrypt(input, output, new[] { cert },
    ///       PdfPermissions.Print | PdfPermissions.Copy,
    ///       EncryptionAlgorithm.AES256);
    /// </code>
    /// </summary>
    public static class PdfPublicKeyEncryptor
    {
        // /SubFilter names
        private const string SubFilterS4 = "adbe.pkcs7.s4"; // RC4-128
        private const string SubFilterS5 = "adbe.pkcs7.s5"; // AES-128 / AES-256

        /// <summary>Length of the random seed wrapped for each recipient (ISO 32000-2 §7.6.5.1).</summary>
        private const int SeedLength = PdfPublicKeyDecryptor.SeedLength;

        /// <summary>Length of the big-endian permissions mask that follows the seed.</summary>
        private const int PermissionsLength = PdfPublicKeyDecryptor.PermissionsLength;

        /// <summary>
        /// Total CMS content length: the seed followed by the permissions mask — the layout is the
        /// reader's (<see cref="PdfPublicKeyDecryptor.SeedOffset"/>), stated once.
        /// </summary>
        private const int CmsSeedContentLength = PdfPublicKeyDecryptor.EnvelopeContentLength;

        /// <summary>/V = 4 and above use crypt filters, so /CF, /StmF and /StrF must be written.</summary>
        private const int VersionCryptFilters = 4;

        /// <summary>
        /// The <c>/Recipients</c> key. Written twice — once on the /Encrypt dictionary and once on
        /// the /DefaultCryptFilter crypt filter — and there is no <c>PdfName.Recipients</c> static, so it is
        /// interned here rather than re-allocated at each site.
        /// </summary>
        private static readonly PdfName RecipientsKey = new PdfName("Recipients");

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Encrypts a PDF using a single DER-encoded X.509 recipient certificate.
        /// </summary>
        public static void Encrypt(
            Stream input, Stream output,
            byte[] recipientCertDer,
            PdfPermissions permissions   = PdfPermissions.All,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
        {
            if (recipientCertDer == null) throw new ArgumentNullException(nameof(recipientCertDer));
            Encrypt(input, output, new[] { recipientCertDer }, permissions, algorithm);
        }

        /// <summary>
        /// Encrypts a PDF using one or more DER-encoded X.509 recipient certificates.
        /// Each recipient will be able to open the document independently.
        /// </summary>
        public static void Encrypt(
            Stream input, Stream output,
            IEnumerable<byte[]> recipientCertsDer,
            PdfPermissions permissions    = PdfPermissions.All,
            EncryptionAlgorithm algorithm  = EncryptionAlgorithm.AES256)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (recipientCertsDer == null) throw new ArgumentNullException(nameof(recipientCertsDer));

            var certs = recipientCertsDer.ToList();
            if (certs.Count == 0)
                throw new ArgumentException("At least one recipient certificate must be provided.", nameof(recipientCertsDer));

            if (algorithm == EncryptionAlgorithm.RC4_40 || algorithm == EncryptionAlgorithm.RC4_40R3)
                throw new NotSupportedException(
                    "RC4-40 is not supported for public-key encryption. Use RC4_128, AES128, or AES256.");

            EncryptCore(input, output, certs, permissions, algorithm, enablePdfMac: false, encryptMetadata: true);
        }

        /// <summary>
        /// As <see cref="Encrypt(Stream, Stream, IEnumerable{byte[]}, PdfPermissions, EncryptionAlgorithm, bool)"/>,
        /// optionally leaving the document-level metadata stream in plaintext.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="encryptMetadata"/> false writes <c>/EncryptMetadata false</c> on the
        /// crypt filter (ISO 32000-2 Table 27 — the public-key handler's place for it), leaves the
        /// catalog's <c>/Metadata</c> stream as it is, and folds the four <c>0xFF</c> bytes of
        /// §7.6.5.3 step c) into the file key. It needs crypt filters, so it is refused with RC4-128
        /// — Table 21 makes the entry meaningful only from <c>/V 4</c>.</para>
        /// <para>This is what <see cref="PdfEncryptedRoundTrip"/> carries across when it re-encrypts
        /// an <c>/EncryptMetadata false</c> document for recipients, so metadata its author left
        /// searchable stays searchable.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="encryptMetadata"/> is false with RC4-128, or <paramref name="enablePdfMac"/>
        /// is set with an algorithm other than AES-256 or an unsuitable output stream.
        /// </exception>
        public static void Encrypt(
            Stream input, Stream output,
            IEnumerable<byte[]> recipientCertsDer,
            PdfPermissions permissions,
            EncryptionAlgorithm algorithm,
            bool enablePdfMac,
            bool encryptMetadata)
        {
            if (encryptMetadata)
            {
                Encrypt(input, output, recipientCertsDer, permissions, algorithm, enablePdfMac);
                return;
            }

            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (recipientCertsDer == null) throw new ArgumentNullException(nameof(recipientCertsDer));
            if (algorithm == EncryptionAlgorithm.RC4_40 || algorithm == EncryptionAlgorithm.RC4_40R3)
                throw new NotSupportedException(
                    "RC4-40 is not supported for public-key encryption. Use RC4_128, AES128, or AES256.");
            if (algorithm == EncryptionAlgorithm.RC4_128)
                throw new ArgumentException(
                    "Leaving the metadata in plaintext needs crypt filters (/EncryptMetadata is meaningful " +
                    "only from /V 4, ISO 32000-2 Table 21); RC4-128 is written at /V 2. Use AES-128 or AES-256.",
                    nameof(encryptMetadata));
            if (enablePdfMac) RequirePdfMacCapable(output, algorithm);

            var certs = recipientCertsDer.ToList();
            if (certs.Count == 0)
                throw new ArgumentException("At least one recipient certificate must be provided.", nameof(recipientCertsDer));

            EncryptCore(input, output, certs, permissions, algorithm, enablePdfMac, encryptMetadata: false);
        }

        /// <summary>
        /// As <see cref="Encrypt(Stream, Stream, IEnumerable{byte[]}, PdfPermissions, EncryptionAlgorithm)"/>,
        /// optionally protecting the document with an ISO/TS 32004 <b>PDF MAC</b> token.
        /// </summary>
        /// <remarks>
        /// <para>With <paramref name="enablePdfMac"/>: a 32-byte <c>/KDFSalt</c>, permission bit 13
        /// cleared (in <c>/P</c> and in every recipient's permission bytes), and a standalone token in
        /// the trailer's <c>/AuthCode</c> covering the whole file. §6.4 keys it with "the crypt filter
        /// named DefaultCryptFilter" — the name ISO 32000-2 §7.6.4.1 requires a public-key handler to
        /// use, and the one this writer gives its crypt filter.</para>
        /// <para><b>AES-256 only.</b> §5.2.1: "If present, the value of the V entry in the document's
        /// Encrypt dictionary shall be at least 5", and AES-256 is the one algorithm here written at
        /// <c>/V 5</c>.</para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// <paramref name="enablePdfMac"/> is set with an algorithm other than AES-256, or with an output
        /// stream that cannot be sought, read and written from position 0 — the token covers the file it
        /// is written into.
        /// </exception>
        public static void Encrypt(
            Stream input, Stream output,
            IEnumerable<byte[]> recipientCertsDer,
            PdfPermissions permissions,
            EncryptionAlgorithm algorithm,
            bool enablePdfMac)
        {
            if (!enablePdfMac)
            {
                Encrypt(input, output, recipientCertsDer, permissions, algorithm);
                return;
            }

            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (recipientCertsDer == null) throw new ArgumentNullException(nameof(recipientCertsDer));
            RequirePdfMacCapable(output, algorithm);

            var certs = recipientCertsDer.ToList();
            if (certs.Count == 0)
                throw new ArgumentException("At least one recipient certificate must be provided.", nameof(recipientCertsDer));

            EncryptCore(input, output, certs, permissions, algorithm, enablePdfMac: true, encryptMetadata: true);
        }

        /// <summary>What a PDF MAC token needs of the algorithm and the output stream — both overloads that write one.</summary>
        private static void RequirePdfMacCapable(Stream output, EncryptionAlgorithm algorithm)
        {
            // The one rule (PdfEncryptor.CanCarryPdfMac); AES-256 is the algorithm this handler
            // writes at /V 5, and one it does not write at all is refused by name further on.
            if (!PdfEncryptor.CanCarryPdfMac(algorithm))
                throw new ArgumentException(
                    "PDF MAC requires /V 5 or later (ISO/TS 32004 §5.2.1), which is AES-256 for public-key encryption.",
                    nameof(algorithm));
            if (!output.CanSeek || !output.CanRead || !output.CanWrite || output.Position != 0)
                throw new ArgumentException(
                    "PDF MAC needs an output stream that is seekable, readable and writable and positioned at 0, " +
                    "because the token covers the whole file written into it.", nameof(output));
        }

        // ----------------------------------------------------------------
        //  Core
        // ----------------------------------------------------------------

        private static void EncryptCore(
            Stream input, Stream output,
            List<byte[]> certDers,
            PdfPermissions permissions,
            EncryptionAlgorithm algorithm,
            bool enablePdfMac,
            bool encryptMetadata)
        {
            // The reader examines at most this many recipients, and the file key hashes all of
            // them, so a document with more would be written and then never open.
            if (certDers.Count > PdfPublicKeyDecryptor.MaxRecipients)
                throw new ArgumentException(
                    $"{certDers.Count} recipients is more than the {PdfPublicKeyDecryptor.MaxRecipients} " +
                    "a public-key-encrypted document can be opened with.", "recipientCertsDer");

            using var doc = PdfDocument.Open(input);

            if (doc.IsEncrypted)
                throw new PdfException("The input PDF is already encrypted.");

            // Algorithm parameters.
            GetAlgorithmParameters(algorithm, out int v, out int r, out int keyLengthBits);
            int keyBytes = keyLengthBits / PdfEncryptionPrimitives.BitsPerByte;

            // 1. Generate 20-byte random seed.
            byte[] seed = PdfEncryptor.GenerateRandomBytes(SeedLength);

            // 2. Build permissions value (4 bytes, big-endian) for the CMS content.
            //
            // ISO 32000-1 Table 22 is transcribed ONCE, in PdfEncryptor.ComputePermissionValue. A
            // second transcription agrees only while both hand-copy the same table: a mask that ORs
            // in 0x4 grants printing on every document regardless of the caller's flags, and a
            // correction to one copy would not reach the other.
            int pValue = PdfEncryptor.ComputePermissionValue(permissions, enablePdfMac);
            byte[] permBytes = new byte[PermissionsLength];
            permBytes[0] = (byte)(pValue >> 24);
            permBytes[1] = (byte)(pValue >> 16);
            permBytes[2] = (byte)(pValue >>  8);
            permBytes[3] = (byte)(pValue & 0xFF);

            // 3. CMS seed content = seed(20) + perms(4), at the reader's offsets — ISO 32000-2
            //    Table 27, "a 20-byte seed value … followed by 4 bytes of permissions settings".
            byte[] cmsContent = new byte[CmsSeedContentLength];
            Array.Copy(seed, 0, cmsContent, PdfPublicKeyDecryptor.SeedOffset, SeedLength);
            Array.Copy(permBytes, 0, cmsContent, PdfPublicKeyDecryptor.PermissionsOffset, PermissionsLength);

            // 4. Wrap the seed for each recipient using CMS KeyTransport.
            var recipientDers = new List<byte[]>();
            foreach (var certDer in certDers)
            {
                byte[] der = WrapForRecipient(cmsContent, certDer, algorithm);
                recipientDers.Add(der);
            }

            // 5. Derive the document encryption key — the reader's recipe, so the two cannot drift.
            byte[] encKey = PdfPublicKeyDecryptor.DeriveEncryptionKey(seed, recipientDers, keyBytes, encryptMetadata);

            // 6. Collect all objects, encrypt them — content only, one rule with PdfEncryptor (FileLayoutObjects).
            var allObjects = new Dictionary<int, PdfObject>();
            bool inputHasObjectStreams = false;
            foreach (var kvp in doc.Parser.EnumerateAllObjects())
            {
                if (FileLayoutObjects.IsLayout(kvp.Value))
                {
                    if (FileLayoutObjects.IsObjectStream(kvp.Value)) inputHasObjectStreams = true;
                    continue;
                }
                allObjects[kvp.Key] = kvp.Value;
            }

            // The document-level metadata stream's number, to leave as it is when the metadata stays
            // plaintext — the same read PdfEncryptor makes, and the one the compressed layout's writer is
            // told (IPdfDocumentMetadataEncryptor): every other metadata stream is encrypted.
            int metadataObjNum = encryptMetadata ? -1 : PdfIncrementalEncryption.DocumentMetadataObjectNumber(doc);

            byte[] fileId = PdfEncryptor.GenerateOrExtractFileId(doc);

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();
            int encryptDictObjNum = maxObjNum + 1;

            // 7. Build the /Encrypt dictionary.
            byte[] kdfSalt = enablePdfMac ? PdfEncryptor.GenerateRandomBytes(32) : null;
            var encryptDict = BuildEncryptDictionary(
                v, r, keyLengthBits, pValue, recipientDers, algorithm, kdfSalt, encryptMetadata);

            // 8. Build trailer + write PDF.
            // A direct trailer value becomes an object of its own, encrypted with the rest (see the method).
            var newTrailer = PdfEncryptor.CloneTrailerForEncryption(
                doc.Trailer, encryptDictObjNum, fileId, 0, allObjects);

            // PDF MAC's own 2.0 floor needs no argument here: it is AES-256-only, which already sets it.
            PdfVersion version = PdfEncryptor.DetermineMinimumVersion(doc.Version, algorithm);

            // An input in the compressed layout keeps it (one rule with PdfEncryptor): the writer
            // encrypts each object as it writes it, the object streams whole.
            if (inputHasObjectStreams)
            {
                PdfEncryptor.WriteCompressedEncryptedFile(output, version, allObjects, encryptDictObjNum,
                    encryptDict, newTrailer,
                    PdfIncrementalEncryption.ForNewDocument(encKey, algorithm, encryptMetadata, onlyAttachments: false,
                        documentMetadataObjectNumber: metadataObjNum),
                    enablePdfMac ? new PdfMacTokenSeal(encKey, kdfSalt) : null);
                return;
            }

            // Each object keyed under, and written at, the generation it was read at — see ObjectIdentity.
            var encryptedObjects = new Dictionary<int, (PdfObject Object, int Generation)>();
            foreach (var kvp in allObjects)
            {
                int generation = ObjectIdentity.GenerationOf(kvp.Value);
                PdfObject enc = kvp.Key == metadataObjNum
                    ? kvp.Value
                    : PdfEncryptor.EncryptObject(kvp.Value, kvp.Key, generation, encKey, algorithm, r);
                encryptedObjects[kvp.Key] = (enc, generation);
            }

            var writer = new PdfWriter(output);
            writer.WriteHeader(version);

            var xrefEntries = new List<(int objNum, int gen, long offset)>();
            foreach (var kvp in encryptedObjects.OrderBy(x => x.Key))
            {
                long off = writer.WriteIndirectObject(kvp.Key, kvp.Value.Generation, kvp.Value.Object);
                xrefEntries.Add((kvp.Key, kvp.Value.Generation, off));
            }
            {
                long off = writer.WriteIndirectObject(encryptDictObjNum, 0, encryptDict);
                xrefEntries.Add((encryptDictObjNum, 0, off));
            }
            long xrefOff = writer.WriteXrefTable(xrefEntries, 0);   // the overload adds the free head, object 0
            if (enablePdfMac)
                PdfEncryptor.WriteTrailerWithPdfMac(writer, output, newTrailer, xrefOff, encKey, kdfSalt);
            else
                writer.WriteTrailer(newTrailer, xrefOff);
        }

        // ----------------------------------------------------------------
        //  CMS wrapping per recipient
        // ----------------------------------------------------------------

        private static byte[] WrapForRecipient(byte[] contentBytes, byte[] recipientCertDer,
            EncryptionAlgorithm algorithm)
        {
            // Load the X.509 certificate. Disposed as soon as BouncyCastle has copied what it
            // needs: X509Certificate2 holds an unmanaged CERT_CONTEXT on Windows, and this runs
            // once per recipient per call, so an undisposed one leaves the handle to the finalizer
            // — the classic "flat managed heap, climbing handle count" shape under load.
            // X509CertificateLoader is also the non-obsolete way in (SYSLIB0057); netstandard2.0
            // predates it, so that leg keeps the constructor.
#if NETSTANDARD2_0
            using var cert = new X509Certificate2(recipientCertDer);
#else
            using var cert = X509CertificateLoader.LoadCertificate(recipientCertDer);
#endif
            var bcCert = DotNetUtilities.FromX509Certificate(cert);

            // Choose the CMS content-encryption algorithm.
            string contentAlgo = algorithm == EncryptionAlgorithm.AES256
                ? CmsEnvelopedDataGenerator.Aes256Cbc
                : CmsEnvelopedDataGenerator.Aes128Cbc;

            var gen = new CmsEnvelopedDataGenerator();
            gen.AddKeyTransRecipient(bcCert);

            // The typed-data overload with the algorithm as an OID: the string one is obsolete since
            // BouncyCastle 2.x, and was this tree's one CS0618 outside the five deliberate SKPath sites.
            var cmsData = gen.Generate(
                (CmsTypedData)new CmsProcessableByteArray(contentBytes),
                new Org.BouncyCastle.Asn1.DerObjectIdentifier(contentAlgo));

            return cmsData.GetEncoded();
        }

        // ----------------------------------------------------------------
        //  /Encrypt dictionary
        // ----------------------------------------------------------------

        /// <remarks>
        /// The file key is <c>PdfPublicKeyDecryptor.DeriveEncryptionKey</c>'s: this file carried its
        /// own copy of the digest recipe, and the two copies agreed on a layout and a step c) that
        /// nobody else's did — the reader's remarks have the account.
        /// </remarks>
        private static PdfDictionary BuildEncryptDictionary(
            int v, int r, int keyLengthBits, int pValue,
            List<byte[]> recipientDers, EncryptionAlgorithm algorithm, byte[] kdfSalt,
            bool encryptMetadata)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Filter, new PdfName("Adobe.PubSec"));

            string subFilter = algorithm == EncryptionAlgorithm.RC4_128
                ? SubFilterS4 : SubFilterS5;
            dict.Set(PdfName.SubFilter, new PdfName(subFilter));

            dict.Set(PdfName.V, new PdfNumber(v));
            dict.Set(PdfName.R, new PdfNumber(r));
            dict.Set(PdfName.Length, new PdfNumber(keyLengthBits));
            dict.Set(PdfName.P, new PdfNumber(pValue));
            if (kdfSalt != null) dict.Set(PdfName.KDFSalt, new PdfString(kdfSalt));

            // /Recipients array — one CMS EnvelopedData per recipient.
            var recipients = new PdfArray();
            foreach (var der in recipientDers)
                recipients.Add(new PdfString(der)); // binary string
            dict.Set(RecipientsKey, recipients);

            // For V=4 (AES-128 / AES-256) we need /CF, /StmF, /StrF.
            if (v >= VersionCryptFilters)
            {
                PdfName cfm = algorithm == EncryptionAlgorithm.AES256 ? PdfName.AESV3 : PdfName.AESV2;
                var stdCf = new PdfDictionary();
                stdCf.Set(PdfName.AuthEvent, PdfName.DocOpen);
                stdCf.Set(PdfName.CFM, cfm);
                // ISO 32000-2 Table 25: a public-key handler states a crypt filter's /Length as it
                // is, in bits (128 under AESV2, 256 under AESV3); only the standard handler's is in bytes.
                stdCf.Set(PdfName.Length, new PdfNumber(keyLengthBits));
                stdCf.Set(RecipientsKey, recipients);
                // Table 27: the public-key handler's /EncryptMetadata lives on the crypt filter /StmF
                // names, not on /Encrypt (Table 21's place for the standard handler's). Absent means true.
                if (!encryptMetadata) stdCf.Set(PdfName.EncryptMetadata, PdfBoolean.False);

                // ISO 32000-2 §7.6.4.1: public-key handlers "shall use crypt filters named
                // DefaultCryptFilter when all document content is encrypted … in place of StdCF name".
                // Readers follow /StmF and /StrF, so the name changes nothing for them; ISO/TS 32004 §6.4
                // sources a PDF MAC key from the filter by this name.
                var cf = new PdfDictionary();
                cf.Set(PdfName.DefaultCryptFilter, stdCf);
                dict.Set(PdfName.CF, cf);
                dict.Set(PdfName.StmF, PdfName.DefaultCryptFilter);
                dict.Set(PdfName.StrF, PdfName.DefaultCryptFilter);
            }

            return dict;
        }

        // ----------------------------------------------------------------
        //  Algorithm helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// The public-key handler's three algorithms, their parameters from the one table
        /// (<see cref="PdfEncryptor.GetAlgorithmParameters"/>); it kept a copy of the three rows.
        /// </summary>
        private static void GetAlgorithmParameters(EncryptionAlgorithm algorithm,
            out int v, out int r, out int keyLengthBits)
        {
            if (algorithm != EncryptionAlgorithm.RC4_128 && algorithm != EncryptionAlgorithm.AES128
                && algorithm != EncryptionAlgorithm.AES256)
                throw new NotSupportedException($"Algorithm {algorithm} not supported for public-key encryption.");
            PdfEncryptor.GetAlgorithmParameters(algorithm, out v, out r, out keyLengthBits);
        }
    }
}
