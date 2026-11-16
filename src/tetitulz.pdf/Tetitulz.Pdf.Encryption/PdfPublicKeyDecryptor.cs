// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Encryption;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Opens a document encrypted with the <c>/Adobe.PubSec</c> public-key security handler
    /// (§7.6.5) — the reading half of <see cref="PdfPublicKeyEncryptor"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The read side of <c>PdfPublicKeyEncryptor</c>.</b> <c>PdfDecryptor</c> handles
    /// <c>/Standard</c> only, so without this CMS unwrap every read path — decrypt, render, extract,
    /// forms — would refuse a document this library writes under <c>/Adobe.PubSec</c>.</para>
    /// <para><b>Only the key derivation is new.</b> Once the file key is recovered, the document is
    /// decrypted by <c>PdfDecryptor.WriteDecrypted</c> — the same object walk, metadata exemption
    /// and trailer rebuild the password handler uses. Two copies of that would be two copies of
    /// security logic.</para>
    /// <para><b>The private key comes from a PKCS#12 store or from a device.</b> Recovering the
    /// key needs an RSA private-key operation, and this assembly reaches it through BouncyCastle,
    /// which needs key material rather than a handle to it — so a key that never leaves a smart
    /// card or HSM cannot be used that way. <see cref="IExternalDecryptor"/> is the seam for
    /// those: the mirror of <c>IExternalSigner</c> on the signing side, reached through the
    /// <see cref="Decrypt(Stream, Stream, IExternalDecryptor)"/> overload.</para>
    /// </remarks>
    public static class PdfPublicKeyDecryptor
    {
        /// <summary>The handler this class opens.</summary>
        private const string PubSecFilter = "Adobe.PubSec";

        /// <summary>Length of the random seed the recipients' CMS blobs wrap.</summary>
        internal const int SeedLength = 20;

        /// <summary>Length of the big-endian permissions mask that follows the seed in the CMS content.</summary>
        internal const int PermissionsLength = 4;

        /// <summary>
        /// Where the seed starts in the enveloped content, and where the permissions start:
        /// <b>the seed first, the permissions after it</b> — ISO 32000-2 Table 27 ("a 20-byte seed
        /// value … followed by 4 bytes of permissions settings"), the layout producers
        /// write and read.
        /// </summary>
        /// <remarks>
        /// Two sides holding the two the other way round — permissions first, then the seed — would
        /// agree with each other, pass every round-trip test, and hash bytes 4–24 as the file key:
        /// sixteen bytes of seed and the four permission bytes. A document written that way opens in
        /// nothing else, and one any other producer wrote decrypts to noise. The one layout is stated
        /// here, and the encryptor reads it from here.
        /// </remarks>
        internal const int SeedOffset = 0;

        /// <summary>See <see cref="SeedOffset"/>.</summary>
        internal const int PermissionsOffset = SeedLength;

        /// <summary>Total enveloped content: the seed followed by the permissions mask.</summary>
        internal const int EnvelopeContentLength = SeedLength + PermissionsLength;

        /// <summary>Digest length of SHA-1, the boundary at which the derivation switches to SHA-256.</summary>
        private const int Sha1LengthBytes = 20;

        /// <summary>
        /// The four bytes ISO 32000-2 §7.6.5.3 step c) adds to the file-key digest "if the key being
        /// generated is intended for use in document-level encryption and the document metadata is
        /// being left as plaintext" — the public-key handler's counterpart of Algorithm 2's step f).
        /// </summary>
        private static readonly byte[] PlaintextMetadataDigestSuffix = { 0xFF, 0xFF, 0xFF, 0xFF };

        /// <summary>
        /// Upper bound on <c>/Recipients</c> entries. The array is attacker-controlled and every
        /// entry costs a CMS parse plus an attempted RSA unwrap. The encryptor refuses to write more.
        /// </summary>
        internal const int MaxRecipients = 1024;

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Decrypts a public-key-encrypted PDF using a PKCS#12 (.pfx) store holding the
        /// recipient's certificate and private key.
        /// </summary>
        /// <param name="input">The encrypted PDF. Not disposed.</param>
        /// <param name="output">Where the decrypted PDF is written. Not disposed.</param>
        /// <param name="pkcs12">The recipient's PKCS#12 store. Not disposed.</param>
        /// <param name="password">The store's password, or null when it has none.</param>
        /// <exception cref="PdfException">
        /// The document is not encrypted, does not use <c>/Adobe.PubSec</c>, or none of its
        /// recipients matches the supplied key.
        /// </exception>
        public static void Decrypt(Stream input, Stream output, Stream pkcs12, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (pkcs12 == null) throw new ArgumentNullException(nameof(pkcs12));

            var (cert, key) = LoadRecipient(pkcs12, password);

            MemoryStream ms = SeekableStream.Buffered(input);
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(ms);
            }
            catch (Exception ex) when (!(ex is PdfException))
            {
                throw new PdfException("Failed to parse the input PDF for decryption.", ex);
            }

            using (doc)
                PdfDecryptor.WriteDecrypted(doc, output, Resolve(doc, cert, (_, recipient) => recipient.GetContent(key)));
        }

        /// <summary>
        /// Decrypts a public-key-encrypted PDF using a PKCS#12 file on disk.
        /// </summary>
        /// <param name="inputPath">Path to the encrypted PDF.</param>
        /// <param name="outputPath">Path to write the decrypted PDF to.</param>
        /// <param name="pkcs12Path">Path to the recipient's .pfx / .p12 file.</param>
        /// <param name="password">The store's password, or null when it has none.</param>
        public static void Decrypt(string inputPath, string outputPath, string pkcs12Path, string password)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            if (pkcs12Path == null) throw new ArgumentNullException(nameof(pkcs12Path));

            using var input = File.OpenRead(inputPath);
            using var store = File.OpenRead(pkcs12Path);
            using var output = File.Create(outputPath);
            Decrypt(input, output, store, password);
        }

        /// <summary>
        /// Decrypts a public-key-encrypted PDF with a private key that stays on a device — a smart
        /// card, a PKCS#11 token, an HSM, or a non-exportable key in the OS certificate store.
        /// </summary>
        /// <remarks>
        /// <b>The device is asked to open exactly one envelope</b>, the one whose recipient
        /// identifiers name <see cref="IExternalDecryptor.GetRecipientCertificate"/>. The rest of
        /// the derivation — hashing that envelope's seed together with every <c>/Recipients</c>
        /// entry in file order — happens here, so a hostile document cannot steer it.
        /// </remarks>
        /// <param name="input">The encrypted PDF. Not disposed.</param>
        /// <param name="output">Where the decrypted PDF is written. Not disposed.</param>
        /// <param name="decryptor">The device holding the recipient's private key.</param>
        /// <exception cref="PdfException">
        /// The document is not encrypted, does not use <c>/Adobe.PubSec</c>, or names no recipient
        /// matching the device's certificate.
        /// </exception>
        public static void Decrypt(Stream input, Stream output, IExternalDecryptor decryptor)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (decryptor == null) throw new ArgumentNullException(nameof(decryptor));

            X509Certificate cert = ExternalCertificate(decryptor);

            MemoryStream ms = SeekableStream.Buffered(input);
            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(ms);
            }
            catch (Exception ex) when (!(ex is PdfException))
            {
                throw new PdfException("Failed to parse the input PDF for decryption.", ex);
            }

            using (doc)
                PdfDecryptor.WriteDecrypted(doc, output, Resolve(doc, cert, (der, _) => decryptor.UnwrapEnvelope(der)));
        }

        /// <summary>
        /// The file encryption key of an open <c>/Adobe.PubSec</c> document, from a PKCS#12 store.
        /// </summary>
        /// <remarks>
        /// <b>The same two steps <see cref="Decrypt(Stream, Stream, Stream, string)"/> takes</b>,
        /// stopping before the rewrite. A caller that needs the <i>key</i> rather than a decrypted
        /// copy — checking an ISO/TS 32004 PDF MAC token, which is keyed on it — would otherwise
        /// have to decrypt the whole document and then derive the key from the copy, whose own
        /// <c>/Encrypt</c> dictionary is gone.
        /// </remarks>
        internal static PdfDecryptor.ResolvedEncryption Resolve(
            PdfDocument doc, Stream pkcs12, string password)
        {
            var (cert, key) = LoadRecipient(pkcs12, password);
            return Resolve(doc, cert, (_, recipient) => recipient.GetContent(key));
        }

        /// <summary>
        /// The file encryption key of an open <c>/Adobe.PubSec</c> document, from a device that
        /// holds the recipient's private key.
        /// </summary>
        internal static PdfDecryptor.ResolvedEncryption Resolve(
            PdfDocument doc, IExternalDecryptor decryptor)
        {
            X509Certificate cert = ExternalCertificate(decryptor);
            return Resolve(doc, cert, (der, _) => decryptor.UnwrapEnvelope(der));
        }

        /// <summary>
        /// Whether <paramref name="input"/> is encrypted with the public-key handler, so a caller
        /// knows which of the two decryptors to reach for.
        /// </summary>
        /// <param name="input">A PDF. Not disposed; its position is restored.</param>
        public static bool IsPublicKeyEncrypted(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            long start = input.CanSeek ? input.Position : 0;
            try
            {
                MemoryStream ms = SeekableStream.Buffered(input);
                using var doc = PdfDocument.Open(ms);
                return doc.IsEncrypted && FilterName(doc) == PubSecFilter;
            }
            catch (PdfException) { return false; }
            catch (IOException) { return false; }
            finally
            {
                if (input.CanSeek) input.Position = start;
            }
        }

        private static string FilterName(PdfDocument doc) =>
            (doc.ResolveObject(doc.EncryptionDictionary?[PdfName.Filter]) as PdfName)?.Value;

        /// <summary>
        /// Whether an already-open document uses <c>/Adobe.PubSec</c> — the same reading
        /// <see cref="IsPublicKeyEncrypted"/> does, for a caller that has already parsed the file.
        /// </summary>
        internal static bool UsesPublicKeyHandler(PdfDocument doc) =>
            doc != null && FilterName(doc) == PubSecFilter;

        // ----------------------------------------------------------------
        //  Key recovery
        // ----------------------------------------------------------------

        /// <summary>
        /// Recovers the file encryption key from the <c>/Recipients</c> array and reports it in the
        /// shape <c>PdfDecryptor</c>'s object walk expects.
        /// </summary>
        /// <param name="doc">The open, encrypted document.</param>
        /// <param name="cert">The recipient's certificate, for matching a CMS recipient.</param>
        /// <param name="open">
        /// Opens the matched envelope, given its DER bytes and the matching recipient.
        /// <b>The only thing that differs between a PKCS#12 store and a hardware device</b> —
        /// everything else on this path, including which envelope is chosen and how the file key
        /// is derived from it, is shared by construction.
        /// </param>
        internal static PdfDecryptor.ResolvedEncryption Resolve(
            PdfDocument doc, X509Certificate cert, Func<byte[], RecipientInformation, byte[]> open)
        {
            if (!doc.IsEncrypted)
                throw new PdfException("The input PDF is not encrypted.");

            var encryptDict = doc.EncryptionDictionary
                ?? throw new PdfException("Cannot find /Encrypt dictionary in encrypted PDF.");

            string filter = FilterName(doc);
            if (filter != PubSecFilter)
                throw new PdfException(
                    $"This document uses the /{filter ?? "(unnamed)"} security handler, not " +
                    "/Adobe.PubSec. Use PdfDecryptor for a password-protected document.");

            int v = Int(doc, encryptDict, PdfName.V) ?? 0;
            int r = Int(doc, encryptDict, PdfName.R) ?? 0;
            int keyLengthBits = Int(doc, encryptDict, PdfName.Length) ?? 128;

            // The per-document diagnostic slots WriteDecrypted reads and writes are cleared here, as
            // the password handler clears them, so a public-key decrypt never shows the previous
            // document's answers.
            PdfDecryptor.BeginDocument(v);

            (EncryptionAlgorithm algorithm, int keyBytes) = Algorithm(v, r, keyLengthBits);

            // §7.6.5: the recipient blobs live on the /Encrypt dictionary for V<4 and on the crypt
            // filter for V>=4. Both are read, because a producer may write either and this is the
            // one input the whole derivation depends on.
            List<byte[]> recipients = ReadRecipients(doc, encryptDict);
            if (recipients.Count == 0)
                throw new PdfException(
                    "The /Encrypt dictionary declares no /Recipients, so no key can be recovered.");

            byte[] seed = RecoverSeed(recipients, cert, open, out int envelopePermissions)
                ?? throw new PdfException(
                    "None of the document's recipients matches the supplied certificate, so the " +
                    "document was not encrypted for this key.");

            // Read BEFORE the key is derived: §7.6.5.3 step c) folds "metadata left as plaintext"
            // into the file key itself, so a wrong reading here is a wrong key, not a wrongly
            // treated stream.
            bool encryptMetadata = ReadEncryptMetadata(doc, encryptDict);

            byte[] fileKey = DeriveEncryptionKey(seed, recipients, keyBytes, encryptMetadata);

            // Only the key recovery is this handler's. Which cipher each half of the document is
            // under is the crypt filters' answer (Tables 20, 25 and 26): /StmF, /StrF and /EFF may
            // name a filter whose /CFM is V2 (RC4) under /V 4, or /Identity, and a stream may name
            // its own (§7.4.10) — so from /V 4 on they are resolved exactly as the password handler
            // resolves them, and /V alone decides only the key's length.
            EncryptionAlgorithm streamAlgorithm = algorithm, stringAlgorithm = algorithm;
            EncryptionAlgorithm? embeddedFileAlgorithm = null;
            Func<PdfName, EncryptionAlgorithm?> streamCryptFilter =
                name => name == PdfName.Identity ? EncryptionAlgorithm.Identity : (EncryptionAlgorithm?)null;
            if (v >= 4)
            {
                streamAlgorithm = PdfDecryptor.ResolveCryptFilter(doc, encryptDict, PdfName.StmF);
                stringAlgorithm = PdfDecryptor.ResolveCryptFilter(doc, encryptDict, PdfName.StrF);
                if (doc.ResolveObject(encryptDict[PdfName.EFF]) != null)
                    embeddedFileAlgorithm = PdfDecryptor.ResolveCryptFilter(doc, encryptDict, PdfName.EFF);
                streamCryptFilter = name => PdfDecryptor.ResolveStreamCryptFilter(doc, encryptDict, name);
            }

            return new PdfDecryptor.ResolvedEncryption
            {
                Key = fileKey,
                StreamAlgorithm = streamAlgorithm,
                StringAlgorithm = stringAlgorithm,
                EmbeddedFileAlgorithm = embeddedFileAlgorithm ?? streamAlgorithm,
                StreamCryptFilter = streamCryptFilter,
                Version = v,
                Revision = r,
                EncryptMetadata = encryptMetadata,
                EnvelopePermissions = envelopePermissions,
            };
        }

        /// <summary>
        /// Whether the document-level metadata stream is encrypted (default true).
        /// </summary>
        /// <remarks>
        /// <b>Two places, the crypt filter's first.</b> For the public-key handler ISO 32000-2
        /// Table 27 puts <c>/EncryptMetadata</c> on the crypt filter dictionary <c>/StmF</c> names
        /// — "used only by crypt filters that are referenced from StmF in an encryption dictionary"
        /// — which is where some readers look; the standard handler's Table 21 puts it on the
        /// <c>/Encrypt</c> dictionary, which is where others read it for both
        /// handlers. A producer may have followed either table, so both are
        /// read, the one the standard assigns to this handler deciding.
        /// </remarks>
        private static bool ReadEncryptMetadata(PdfDocument doc, PdfDictionary encryptDict)
        {
            if (StreamCryptFilter(doc, encryptDict) is PdfDictionary filter
                && doc.ResolveObject(filter[PdfName.EncryptMetadata]) is PdfBoolean onFilter)
                return onFilter.Value;

            if (doc.ResolveObject(encryptDict[PdfName.EncryptMetadata]) is PdfBoolean onDictionary)
                return onDictionary.Value;

            return true;
        }

        /// <summary>The crypt filter dictionary <c>/StmF</c> names (<c>/StdCF</c> when it names none), or null.</summary>
        private static PdfDictionary StreamCryptFilter(PdfDocument doc, PdfDictionary encryptDict)
        {
            if (!(doc.ResolveObject(encryptDict[PdfName.CF]) is PdfDictionary cf)) return null;
            var stmF = (doc.ResolveObject(encryptDict[PdfName.StmF]) as PdfName) ?? PdfName.StdCF;
            return doc.ResolveObject(cf[stmF]) as PdfDictionary;
        }

        /// <summary>
        /// The <c>/Recipients</c> array, from the crypt filter when the document has one and from
        /// the <c>/Encrypt</c> dictionary otherwise.
        /// </summary>
        /// <remarks>
        /// <b>The order is the derivation's input, so it is preserved exactly.</b> The file key is
        /// <c>H(seed ‖ recipient₁ ‖ recipient₂ ‖ …)</c>; re-ordering the blobs, or reading them from
        /// a different one of the two places the writer put them, yields a different key and a
        /// document that will not open — with no indication of why.
        /// </remarks>
        private static List<byte[]> ReadRecipients(PdfDocument doc, PdfDictionary encryptDict)
        {
            var recipientsKey = new PdfName("Recipients");
            PdfArray array = null;

            if (StreamCryptFilter(doc, encryptDict) is PdfDictionary filter)
                array = doc.ResolveObject(filter[recipientsKey]) as PdfArray;

            array ??= doc.ResolveObject(encryptDict[recipientsKey]) as PdfArray;

            var result = new List<byte[]>();
            if (array == null) return result;

            // Refused, not truncated. The file key hashes EVERY recipient, so reading only the first
            // MaxRecipients derived a wrong key and "decrypted" the document into noise without a
            // word of explanation.
            if (array.Count > MaxRecipients)
                throw new PdfException(
                    $"The /Encrypt dictionary lists {array.Count} recipients, more than the " +
                    $"{MaxRecipients} examined, so the document key cannot be derived.");

            for (int i = 0; i < array.Count; i++)
                if (doc.ResolveObject(array[i]) is PdfString s && s.RawBytes != null)
                    result.Add(s.RawBytes);

            return result;
        }

        /// <summary>
        /// Unwraps the first recipient blob the supplied key can open, and returns the 20-byte seed
        /// inside it.
        /// </summary>
        /// <remarks>
        /// <para>A blob that does not parse, or that no recipient in it matches, is skipped rather
        /// than fatal: a document encrypted for several people carries one blob each, and all but
        /// one of them are expected to fail for any given key.</para>
        /// <para>The CMS content is the seed followed by a 4-byte permissions mask
        /// (<see cref="SeedOffset"/>). <b>The mask is deliberately not honoured here</b> — it is the
        /// recipient's copy of <c>/P</c>, and this class decrypts rather than enforcing permissions,
        /// exactly as the password handler does. A caller that enforces <c>/P</c> reads it from the
        /// decrypted document.</para>
        /// <para><b>The certificate match happens before <paramref name="open"/> is called</b>, and
        /// that ordering is a requirement rather than an optimisation: <c>/Recipients</c> is
        /// attacker-controlled, and a device asked to open envelopes not addressed to it prompts
        /// for a PIN each time — enough refusals lock a card.</para>
        /// </remarks>
        /// <param name="recipients">Every <c>/Recipients</c> entry, in file order.</param>
        /// <param name="cert">The recipient certificate to match envelopes against.</param>
        /// <param name="open">Opens the matched envelope and returns its plaintext content.</param>
        /// <param name="permissions">
        /// The four permission bytes that follow the seed, big-endian — reported, not enforced:
        /// the PDF MAC check reads bit 13 from them (ISO/TS 32004 Table 4).
        /// </param>
        private static byte[] RecoverSeed(
            List<byte[]> recipients, X509Certificate cert,
            Func<byte[], RecipientInformation, byte[]> open, out int permissions)
        {
            permissions = 0;
            foreach (byte[] der in recipients)
            {
                CmsEnvelopedData enveloped;
                try
                {
                    enveloped = new CmsEnvelopedData(der);
                }
                catch (Exception) { continue; }   // not a CMS blob, or not one we can read

                foreach (RecipientInformation recipient in enveloped.GetRecipientInfos().GetRecipients())
                {
                    if (!recipient.RecipientID.Match(cert)) continue;

                    byte[] content;
                    try
                    {
                        content = open(der, recipient);
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        // The certificate matched but the key did not open it — a mismatched
                        // key/certificate pair, which is worth naming rather than reporting as
                        // "no recipient matched".
                        throw new PdfException(
                            "The certificate matches a recipient of this document, but the private " +
                            "key supplied with it could not unwrap the key: " + ex.Message, ex);
                    }

                    if (content == null)
                        throw new PdfException(
                            "The certificate matches a recipient of this document, but the key " +
                            "declined to unwrap it, so the document was not encrypted for it.");

                    if (content.Length < EnvelopeContentLength)
                        throw new PdfException(
                            "A recipient's CMS content is shorter than the 20-byte seed plus the " +
                            "4-byte permissions mask the handler requires.");

                    byte[] seed = new byte[SeedLength];
                    Array.Copy(content, SeedOffset, seed, 0, SeedLength);
                    permissions = (content[PermissionsOffset] << 24) | (content[PermissionsOffset + 1] << 16)
                                | (content[PermissionsOffset + 2] << 8) | content[PermissionsOffset + 3];
                    return seed;
                }
            }

            return null;
        }

        /// <summary>
        /// ISO 32000-2 §7.6.5.3: <c>H(seed ‖ recipient₁ ‖ recipient₂ ‖ … [‖ FF FF FF FF])</c>,
        /// truncated to the key length — <b>the one implementation, for the encryptor too</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>The digest depends on the key length.</b> §7.6.5.3 specifies SHA-1 for the
        /// 128-bit algorithms and SHA-256 for AESV3, because SHA-1 cannot supply the 32 bytes
        /// AES-256 needs; SHA-1 clamped to 32 bytes cannot encrypt at AES-256 at all.</para>
        /// <para><b>Plaintext metadata is in the key.</b> Step c) appends four <c>0xFF</c> bytes when
        /// the document's metadata is left unencrypted. Two sides omitting them would agree with each
        /// other and with nobody else: a third party's <c>/EncryptMetadata false</c> document would
        /// open to noise. It is one function, called from both sides, because two copies of a digest
        /// recipe can agree on the wrong one.</para>
        /// </remarks>
        /// <param name="seed">The 20-byte seed the matched envelope held.</param>
        /// <param name="recipients">Every <c>/Recipients</c> entry, in file order.</param>
        /// <param name="keyBytes">The file key's length — 16 for the 128-bit algorithms, 32 for AESV3.</param>
        /// <param name="encryptMetadata">
        /// Whether the metadata stream is encrypted — the crypt filter's <c>/EncryptMetadata</c>,
        /// true by default.
        /// </param>
        internal static byte[] DeriveEncryptionKey(
            byte[] seed, List<byte[]> recipients, int keyBytes, bool encryptMetadata)
        {
            using HashAlgorithm sha = keyBytes > Sha1LengthBytes ? SHA256.Create() : SHA1.Create();

            sha.TransformBlock(seed, 0, seed.Length, null, 0);
            foreach (byte[] der in recipients)
                sha.TransformBlock(der, 0, der.Length, null, 0);
            if (!encryptMetadata)
                sha.TransformBlock(PlaintextMetadataDigestSuffix, 0, PlaintextMetadataDigestSuffix.Length, null, 0);
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            byte[] hash = sha.Hash;
            if (hash == null || hash.Length < keyBytes)
                throw new PdfException(
                    $"Cannot derive a {keyBytes * 8}-bit public-key document key: the digest " +
                    $"supplies only {(hash?.Length ?? 0)} bytes.");

            byte[] key = new byte[keyBytes];
            Array.Copy(hash, key, keyBytes);
            return key;
        }

        /// <summary>
        /// The content algorithm and key length a <c>/V</c>, <c>/R</c> and <c>/Length</c> select —
        /// the inverse of <c>PdfPublicKeyEncryptor.GetAlgorithmParameters</c>.
        /// </summary>
        private static (EncryptionAlgorithm, int) Algorithm(int v, int r, int keyLengthBits)
        {
            switch (v)
            {
                case 2 when r == 3: return (EncryptionAlgorithm.RC4_128, 16);
                case 4: return (EncryptionAlgorithm.AES128, 16);
                case 5: return (EncryptionAlgorithm.AES256, 32);
                default:
                    throw new PdfException(
                        $"Unsupported public-key encryption: V={v}, R={r}, /Length={keyLengthBits}.");
            }
        }

        // ----------------------------------------------------------------
        //  PKCS#12
        // ----------------------------------------------------------------

        /// <summary>
        /// The device's certificate, parsed once so every envelope can be matched against it.
        /// </summary>
        /// <remarks>
        /// A device that cannot name its own certificate cannot be matched against anything, so
        /// this fails loudly rather than falling through to "no recipient matched" — which would
        /// blame the document for the implementation's omission.
        /// </remarks>
        private static X509Certificate ExternalCertificate(IExternalDecryptor decryptor)
        {
            byte[] der = decryptor.GetRecipientCertificate();
            if (der == null || der.Length == 0)
                throw new PdfException(
                    "The external decryptor returned no recipient certificate, so no envelope in " +
                    "the document can be matched to it.");

            X509Certificate parsed;
            try
            {
                parsed = new X509CertificateParser().ReadCertificate(der);
            }
            catch (Exception ex)
            {
                throw new PdfException(
                    "The external decryptor's recipient certificate is not a DER-encoded X.509 " +
                    "certificate: " + ex.Message, ex);
            }

            // ReadCertificate answers NULL for input it cannot make a certificate of; it does not
            // throw. Written as a catch alone this would return null, every RecipientID.Match would
            // say no, and the caller be told the document is encrypted for somebody else — blaming
            // the file for the implementation's broken certificate.
            return parsed ?? throw new PdfException(
                "The external decryptor's recipient certificate is not a DER-encoded X.509 " +
                "certificate, so no envelope in the document can be matched to it.");
        }

        /// <summary>
        /// The first certificate in the store that has a private key beside it.
        /// </summary>
        /// <remarks>
        /// Loaded through BouncyCastle's <see cref="Pkcs12Store"/> rather than
        /// <c>X509Certificate2</c>, for the reason the signing side records: constructing an
        /// <c>X509Certificate2</c> from PFX bytes writes a key file to disk on Windows and leaks it
        /// when the object is not disposed. The store hands back key material directly and touches
        /// no disk.
        /// </remarks>
        private static (X509Certificate cert, AsymmetricKeyParameter key) LoadRecipient(
            Stream pkcs12, string password)
        {
            var store = new Pkcs12StoreBuilder().Build();
            try
            {
                store.Load(pkcs12, password?.ToCharArray() ?? Array.Empty<char>());
            }
            catch (Exception ex)
            {
                throw new PdfException(
                    "The recipient's PKCS#12 store could not be opened — wrong password, or not a " +
                    "PKCS#12 file: " + ex.Message, ex);
            }

            foreach (string alias in store.Aliases)
            {
                if (!store.IsKeyEntry(alias)) continue;

                var entry = store.GetKey(alias);
                if (entry?.Key == null || !entry.Key.IsPrivate) continue;

                var chain = store.GetCertificateChain(alias);
                if (chain == null || chain.Length == 0) continue;

                return (chain[0].Certificate, entry.Key);
            }

            throw new PdfException(
                "The PKCS#12 store holds no certificate with a private key, so it cannot open a " +
                "public-key-encrypted document.");
        }

        /// <summary>
        /// A file-supplied integer, saturated outside the <see cref="int"/> range — never the
        /// unspecified result of casting an out-of-range double (<c>/V 99999999999</c> reported
        /// itself as <c>V=-2147483648</c>).
        /// </summary>
        private static int? Int(PdfDocument doc, PdfDictionary dict, PdfName key)
            => (doc.ResolveObject(dict[key]) as PdfNumber) is PdfNumber n ? n.IntValue : (int?)null;
    }
}
