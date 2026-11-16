// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Adds a CMS counter-signature to an existing digital signature in a PDF.
    ///
    /// <para>
    /// A counter-signature (OID 1.2.840.113549.1.9.6 — id-countersignature) is embedded
    /// as an unsigned attribute within the existing SignerInfo of the original signature.
    /// Unlike a second /Sig field, it directly attests to the original signer's signature
    /// value, confirming that the counter-signer witnessed the original signature at a
    /// specific point in time.
    /// </para>
    ///
    /// <para>
    /// The original /Contents value (PKCS#7 CMS blob) is read, the new counter-signature
    /// unsigned attribute is injected, and the modified /Contents is written back <b>in place</b>,
    /// over the same hex placeholder — the bytes a signature's /ByteRange leaves out of its digest,
    /// so the signature still verifies. No revision is appended; the file's length and every other
    /// byte are unchanged. A signature added <i>after</i> the target signs those placeholder bytes,
    /// which is why counter-signing a covered signature is refused.
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   PdfCounterSigner.AddCounterSignature(
    ///       inputStream, outputStream,
    ///       sigFieldName: "Signature1",
    ///       pfxPath: "countersigner.pfx",
    ///       pfxPassword: "secret");
    /// </code>
    /// </summary>
    public static class PdfCounterSigner
    {
        // The id-countersignature OID (RFC 2985 §5.7.7, PdfEkuOids.CounterSignature) is not
        // named here: BouncyCastle's SignerInformation.AddCounterSigners writes the attribute, so
        // repeating the constant would be a second place for it to drift.

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Adds a CMS counter-signature to the named signature field using a PFX certificate.
        /// </summary>
        /// <param name="input">Source PDF stream containing at least one signed field.</param>
        /// <param name="output">Output PDF stream with the counter-signature embedded.</param>
        /// <param name="sigFieldName">Name of the existing signature field to counter-sign.</param>
        /// <param name="pfxPath">Path to the counter-signer's PFX / PKCS#12 file.</param>
        /// <param name="pfxPassword">Password for the PFX file.</param>
        /// <param name="digestAlgorithm">Digest algorithm (default "SHA-256").</param>
        public static void AddCounterSignature(
            Stream input, Stream output,
            string sigFieldName,
            string pfxPath, string pfxPassword,
            string digestAlgorithm = "SHA-256")
        {
            if (input == null)       throw new ArgumentNullException(nameof(input));
            if (output == null)      throw new ArgumentNullException(nameof(output));
            if (sigFieldName == null) throw new ArgumentNullException(nameof(sigFieldName));
            if (pfxPath == null)     throw new ArgumentNullException(nameof(pfxPath));

            byte[] pfxBytes = File.ReadAllBytes(pfxPath);
            AddCounterSignature(input, output, sigFieldName, pfxBytes, pfxPassword, digestAlgorithm);
        }

        /// <summary>
        /// Adds a CMS counter-signature using PFX bytes already in memory.
        /// </summary>
        public static void AddCounterSignature(
            Stream input, Stream output,
            string sigFieldName,
            byte[] pfxBytes, string pfxPassword,
            string digestAlgorithm = "SHA-256")
        {
            if (input == null)       throw new ArgumentNullException(nameof(input));
            if (output == null)      throw new ArgumentNullException(nameof(output));
            if (sigFieldName == null) throw new ArgumentNullException(nameof(sigFieldName));
            if (pfxBytes == null)    throw new ArgumentNullException(nameof(pfxBytes));

            var ms = EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // ---- 1. Locate the target signature field. ----
            // Encryption first: for an encrypted file PdfDocument.Load never reads the catalog, so
            // AcroForm is ALWAYS null and "Document has no AcroForm." asserts something false about
            // the document. One shared wording with PdfSignatureVerifier and the timestamper.
            if (doc.IsEncrypted)
                throw new PdfException(EncryptedDocumentMessages.AcroFormUnreadable);

            if (doc.AcroForm == null)
                throw new PdfSigningException("Document has no AcroForm.");

            var sigField = doc.AcroForm.FindSignatureField(sigFieldName, out int partialNameMatches);
            if (sigField == null)
                throw new PdfSigningException(
                    SigningHelpers.SignatureFieldNotFound(sigFieldName, partialNameMatches));

            if (!sigField.HasSignature)
                throw new PdfSigningException($"Signature field '{sigFieldName}' contains no signature value.");

            var sigDict = sigField.SignatureValue;
            // Resolved: GetName does not follow a reference, so an indirect /SubFilter read as
            // absent and a legitimate detached-CMS signature was REFUSED — with a message quoting
            // an empty sub-filter that is not what the file says, which a caller cannot tell from a
            // genuinely unsupported signature type.
            var subFilter = (sigDict != null
                ? doc.ResolveObject(sigDict[PdfName.SubFilter]) as PdfName
                : null)?.Value ?? "";
            if (subFilter != "adbe.pkcs7.detached" && subFilter != "ETSI.CAdES.detached")
                throw new PdfSigningException(
                    $"Counter-signing is only supported for adbe.pkcs7.detached / ETSI.CAdES.detached " +
                    $"signatures (found: '{subFilter}').");

            // ---- 2. Extract the existing CMS /Contents. ----
            if (sigDict == null)
                throw new PdfSigningException("No signature dictionary to counter-sign.");
            var contentsStr = sigDict.GetString(PdfName.Contents);
            if (contentsStr == null)
                throw new PdfSigningException("No /Contents in signature dictionary.");

            // Trimmed of the /Contents reserve padding: this array is parsed as CMS, and the
            // counter-signed result is written back into a freshly reserved hole, so dropping the
            // old padding here loses nothing.
            byte[] originalCms = SigningDefaults.TrimToAsn1Object(contentsStr.RawBytes);

            // ---- 3. Load the counter-signer's certificate and key. ----
            var (csPrivateKey, csCertificate, csChain) = LoadPfx(pfxBytes, pfxPassword);

            // ---- 4. Build the counter-signature: sign the existing SignerInfo.signature value. ----
            byte[] updatedCms = InjectCounterSignature(
                originalCms, csPrivateKey!, csCertificate, csChain, digestAlgorithm);

            // ---- 5. Write the updated /Contents back via overwrite + incremental update. ----
            // We need to find the byte offset of the /Contents value in the file.
            // Use the ByteRange to locate the gap (which IS the /Contents hex).
            //
            // ONE reader, in Core's SignedByteRanges — this site was the weakest of the six: it
            // neither resolved nor type-checked. GetArray tests the RAW value's type, so
            // `/ByteRange 9 0 R` (§7.3.10 permits it) read as ABSENT and this method REFUSED a
            // signature PdfSignatureVerifier, 25 lines below a /SubFilter read fixed for exactly
            // that, verifies happily. An indirect ELEMENT was worse: GetLong throws, so the
            // refusal came out as "Element at index 0 is not a number", which names neither
            // /ByteRange nor the document.
            //
            // Element 3 (the length of the trailing range) is not needed: the hole is bounded by
            // the end of the first range and the start of the second.
            if (!SignedByteRanges.TryRead(sigDict, doc, out var byteRange, out string byteRangeError))
                throw new PdfSigningException(byteRangeError);

            long br2 = byteRange.Offset2;

            // The /Contents hex sits between HoleStart (= element 0 + element 1) and br2 - but NOT necessarily at
            // its first byte: a producer may pad the excluded hole with whitespace, which
            // PdfSignatureVerifier explicitly tolerates ("allow a small slack for whitespace some
            // writers add around it"). Assuming '<' is the very first byte wrote the new hex one or
            // more bytes early: the leading '<' was overwritten with a hex digit and the tail spilled
            // past the closing '>' into the following dictionary bytes, destroying the signature this
            // operation exists to witness - and reporting success. Locate the real delimiters.
            long holeStart = byteRange.HoleStart;
            if (holeStart < 0 || br2 <= holeStart || br2 > ms.Length)
                throw new PdfSigningException(
                    "Invalid /ByteRange in signature dictionary: the excluded region is not inside the file.");

            // Counter-signing rewrites this signature's /Contents in place, and every signature appended
            // after it signed those bytes: ISO 32000-2 §12.8.1 has a later signature's range run from
            // "%PDF-" to the end of its own revision, which includes every earlier signature's
            // /Contents. Rewriting them would break each of those signatures while reporting success —
            // so only the signature no other one covers can be counter-signed.
            RefuseIfAnotherSignatureCovers(doc, sigDict, sigFieldName, holeStart, br2);

            long contentsStart, contentsEnd;
            SigningHelpers.FindContentsDelimiters(ms, holeStart, br2, out contentsStart, out contentsEnd);

            long reservedHexCharsLong = contentsEnd - contentsStart;
            if (reservedHexCharsLong <= 0 || reservedHexCharsLong > int.MaxValue)
                throw new PdfSigningException(
                    "Invalid /Contents placeholder: the reserved hex region is empty or absurdly large.");
            int reservedHexChars = (int)reservedHexCharsLong;

            if (updatedCms.Length * 2L > reservedHexChars)
                throw new PdfSigningException(
                    $"Updated CMS ({updatedCms.Length} bytes) does not fit in the reserved " +
                    $"/Contents space ({reservedHexChars / 2} bytes). " +
                    $"Either re-sign the original document with " +
                    $"SignatureParameters.EstimatedSignatureSize >= {updatedCms.Length + 1024}, " +
                    $"or call PdfCounterSigner.EstimateCmsSize() before signing the original " +
                    $"to preview the required size.");

            // Copy original file to output.
            ms.Position = 0;
            ms.CopyTo(output);

            // Overwrite the /Contents hex digits at the known offset, padded to the original length.
            SigningHelpers.WriteContents(output, contentsStart, reservedHexChars, updatedCms);
            output.Flush();
        }

        /// <summary>
        /// Estimates the CMS payload size (in bytes) that would result from
        /// adding a counter-signature with the given certificate to a
        /// signature originally signed with <paramref name="originalCmsBytes"/>.
        /// Use this <em>before</em> signing the original document to choose
        /// a sufficient <c>SignatureParameters.EstimatedSignatureSize</c> so
        /// the later counter-sign does not throw on space exhaustion.
        /// </summary>
        /// <param name="originalCmsBytes">DER-encoded original signature blob.</param>
        /// <param name="counterSignerCert">Counter-signer certificate (for chain size).</param>
        /// <param name="extraOverheadBytes">
        /// Safety margin for ASN.1 length encoding + future timestamp embedding.
        /// Default 1024 bytes is comfortable for typical RSA-2048 + SHA-256.
        /// </param>
        public static int EstimateCmsSize(
            byte[] originalCmsBytes,
            X509Certificate2 counterSignerCert,
            int extraOverheadBytes = 1024)
        {
            if (originalCmsBytes == null) throw new ArgumentNullException(nameof(originalCmsBytes));
            if (counterSignerCert == null) throw new ArgumentNullException(nameof(counterSignerCert));

            // Conservative estimate: original CMS + a counter-signature SignerInfo (~512 bytes
            // for RSA-2048 SHA-256 with cert) + ASN.1 length encoding overhead.
            int certBytes = counterSignerCert.RawData?.Length ?? AssumedCertBytes;
            return originalCmsBytes.Length
                 + certBytes
                 + RsaSignatureValueBytes
                 + MessageDigestBytes
                 + SignedAttributeHeaderBytes
                 + extraOverheadBytes;
        }

        // Per-part contributions to the EstimateCmsSize sum above, for an RSA-2048 + SHA-256
        // counter-signer. Deliberately generous — under-estimating means the counter-sign later
        // throws for lack of reserved /Contents space.
        /// <summary>Assumed DER size of a certificate whose <c>RawData</c> is unavailable.</summary>
        private const int AssumedCertBytes = 1024;
        /// <summary>Signature value of an RSA-2048 key.</summary>
        private const int RsaSignatureValueBytes = 256;
        /// <summary>The SHA-256 messageDigest signed attribute.</summary>
        private const int MessageDigestBytes = 32;
        /// <summary>ASN.1 headers wrapping the signed/unsigned attribute set.</summary>
        private const int SignedAttributeHeaderBytes = 64;

        /// <summary>
        /// Refuses when any other signature's signed bytes overlap the region
        /// <c>[holeStart, holeEnd)</c> that holds the target's <c>/Contents</c>.
        /// </summary>
        /// <remarks>
        /// A signature whose <c>/ByteRange</c> cannot be read is not evidence either way and is passed
        /// over; the verifier reports it on its own.
        /// </remarks>
        /// <exception cref="PdfSigningException">Another signature covers the target's <c>/Contents</c>.</exception>
        private static void RefuseIfAnotherSignatureCovers(
            PdfDocument doc, PdfDictionary target, string targetName, long holeStart, long holeEnd)
        {
            foreach (var (field, signature) in SignatureFieldWalk.DistinctSignatures(doc.AcroForm))
            {
                if (ReferenceEquals(signature, target)) continue;
                if (!SignedByteRanges.TryRead(signature, doc, out var other, out _)) continue;

                bool coversFirst = other.Offset1 < holeEnd && other.Offset1 + other.Length1 > holeStart;
                bool coversSecond = other.Offset2 < holeEnd && other.Offset2 + other.Length2 > holeStart;
                if (!coversFirst && !coversSecond) continue;

                throw new PdfSigningException(
                    $"Signature '{field.FullyQualifiedName}' signs the bytes of '{targetName}''s /Contents, " +
                    "so counter-signing it would rewrite bytes that signature covers and invalidate it. " +
                    "Only a signature no later signature or document timestamp covers can be counter-signed.");
            }
        }

        // ----------------------------------------------------------------
        //  CMS injection
        // ----------------------------------------------------------------

        private static byte[] InjectCounterSignature(
            byte[] originalCmsDer,
            Org.BouncyCastle.Crypto.AsymmetricKeyParameter csPrivKey,
            Org.BouncyCastle.X509.X509Certificate csCert,
            IList<Org.BouncyCastle.X509.X509Certificate> csChain,
            string digestAlgorithm)
        {
            // Parse the original CMS SignedData.
            var originalSd = new CmsSignedData(originalCmsDer);
            var signers = originalSd.GetSignerInfos().GetSigners();

            // Build the counter-signature for each SignerInfo.
            // For each existing signer, we sign their .SignatureValue bytes.
            var newSigners = new List<SignerInformation>();

            foreach (SignerInformation existingSigner in signers)
            {
                var csGen = new CmsSignedDataGenerator();
                string digestOid = GetDigestOid(digestAlgorithm);
                csGen.AddSigner(csPrivKey, csCert, digestOid);

                var csChainList = new List<Org.BouncyCastle.X509.X509Certificate>(csChain);
                if (!csChainList.Contains(csCert)) csChainList.Insert(0, csCert);
                csGen.AddCertificates(CollectionUtilities.CreateStore(csChainList));

                // GenerateCounterSigners signs the existing signer's signature octets and emits a
                // SignerInfo shaped as RFC 5652 §11.4 requires. A detached CMS built over those octets,
                // its SignerInfo lifted out, would carry a content-type signed attribute, and §11.4
                // says a counter-signature's signedAttrs MUST NOT have one — every conforming verifier
                // refuses it, BouncyCastle included: "[For counter signatures,] the signedAttributes
                // field MUST NOT contain a content-type attribute". AddCounterSigners then merges the
                // id-countersignature
                // unsigned attribute in, preserving any unsigned attributes already present (a
                // signature timestamp, most importantly).
                var counterSigners = csGen.GenerateCounterSigners(existingSigner);
                newSigners.Add(SignerInformation.AddCounterSigners(existingSigner, counterSigners));
            }

            // Rebuild the SignedData with the updated SignerInfos.
            var updatedSignerInfos = new SignerInformationStore(newSigners);
            // Also add the counter-signer's certificate to the CertStore.
            var csChainFull = new List<Org.BouncyCastle.X509.X509Certificate>(csChain);
            if (!csChainFull.Contains(csCert)) csChainFull.Insert(0, csCert);
            var allCerts = new List<Org.BouncyCastle.X509.X509Certificate>();
            // Collect existing certs.
            foreach (var c in originalSd.GetCertificates().EnumerateMatches(null))
                allCerts.Add(c);
            foreach (var c in csChainFull)
                if (!allCerts.Contains(c)) allCerts.Add(c);

            var updatedSd = CmsSignedData.ReplaceSigners(originalSd, updatedSignerInfos);

            // ReplaceSigners carries the ORIGINAL certificates field across untouched, so the
            // merged set above has to be written back explicitly. Without this the emitted CMS
            // holds the counter-signature SignerInfo but not the certificate it was made with, and
            // every verifier that resolves a SignerID against the CMS certificate bag — Adobe,
            // Other verifiers, BouncyCastle's own `certStore.EnumerateMatches(signer.SignerID)`, and this
            // library's CmsSignatureVerifier ("No matching certificate found for signer.") — finds
            // nothing. The counter-signature was unverifiable by construction, which is the one
            // thing this class exists to produce.
            //
            // The original CRLs are carried across unchanged: counter-signing revokes nothing, and
            // this call REPLACES the CRL field rather than merging into it, so passing null here
            // strips every CRL the SignedData carried. On a PAdES B-LT / LTV signature that is the
            // embedded revocation material — counter-signing would add its SignerInfo and destroy
            // the long-term validation data in the same call.
            updatedSd = CmsSignedData.ReplaceCertificatesAndCrls(
                updatedSd,
                CollectionUtilities.CreateStore(allCerts),
                originalSd.GetCrls(),
                null);

            return Cms.CmsSignatureGenerator.EncodeForContents(updatedSd);
        }

        // ----------------------------------------------------------------
        //  PFX loader
        // ----------------------------------------------------------------

        private static (Org.BouncyCastle.Crypto.AsymmetricKeyParameter? privateKey,
                        Org.BouncyCastle.X509.X509Certificate certificate,
                        IList<Org.BouncyCastle.X509.X509Certificate> chain)
            LoadPfx(byte[] pfxBytes, string password)
        {
            // Load via the BouncyCastle Pkcs12Store (CertificateHelper) instead of
            // X509Certificate2 with MachineKeySet|Exportable, which would persist the private key
            // into the machine-wide CNG/CAPI key store and mark it exportable — leaking key material
            // to other processes and accumulating orphaned key files. The Pkcs12Store path keeps the
            // key in-memory only and also yields the full certificate chain.
            using var ms = new MemoryStream(pfxBytes);
            var data = CertificateHelper.LoadFromPfx(ms, password);
            return (data.PrivateKey, data.Certificate, data.Chain);
        }

        /// <summary>
        /// Resolves a digest algorithm name to its OID for the counter-signature.
        /// </summary>
        /// <remarks>
        /// <para>ONE implementation, in <c>DigestAlgorithms.ResolveOidForNewSignature</c>, shared
        /// with <c>CmsSignatureGenerator</c>: two copies would drift, one silently downgrading SHA-3
        /// to SHA-256 on the signing path while the other refused the identical string.</para>
        ///
        /// <para>The restriction is to the SHA-1/2/3 families; Streebog is not
        /// accepted for a counter-signature, so <c>allowStreebog</c> stays at its default.</para>
        /// </remarks>
        private static string GetDigestOid(string algorithm)
            => DigestAlgorithms.ResolveOidForNewSignature(algorithm, nameof(algorithm));

        private static Stream EnsureSeekable(Stream s) => SigningHelpers.EnsureSeekable(s);
    }
}
