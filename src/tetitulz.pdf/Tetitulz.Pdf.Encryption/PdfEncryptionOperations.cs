// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Facade for all PDF encryption and decryption operations.
    /// Lives in <c>Tetitulz.Pdf.Encryption</c> so that <c>Tetitulz.Pdf.Base</c>
    /// has no dependency on BouncyCastle.
    /// </summary>
    public static class PdfEncryptionOperations
    {
        /// <summary>Sets a password on a PDF file.</summary>
        public static void SetPassword(string inputPath, string outputPath,
            string userPassword, string ownerPassword,
            PdfPermissions permissions = PdfPermissions.All,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES128)
        {
            var parameters = new EncryptionParameters
            {
                UserPassword  = userPassword ?? "",
                OwnerPassword = ownerPassword ?? userPassword,
                Permissions   = permissions,
                Algorithm     = algorithm
            };
            PdfEncryptor.Encrypt(inputPath, outputPath, parameters);
        }

        /// <summary>Removes the password from an encrypted PDF.</summary>
        public static void RemovePassword(string inputPath, string outputPath, string password)
            => PdfDecryptor.Decrypt(inputPath, outputPath, password);

        /// <summary>
        /// Protects a PDF with specific permissions using an owner password.
        /// The document can be opened without a password but has restricted actions.
        /// </summary>
        public static void Protect(string inputPath, string outputPath,
            PdfPermissions permissions, string ownerPassword,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES128)
        {
            var parameters = new EncryptionParameters
            {
                UserPassword  = "",
                OwnerPassword = ownerPassword,
                Permissions   = permissions,
                Algorithm     = algorithm
            };
            PdfEncryptor.Encrypt(inputPath, outputPath, parameters);
        }

        /// <summary>
        /// Encrypts a PDF using a single X.509 recipient certificate (DER-encoded).
        /// </summary>
        public static void EncryptWithCertificate(
            Stream input, Stream output,
            byte[] recipientCertDer,
            PdfPermissions permissions    = PdfPermissions.All,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
            => PdfPublicKeyEncryptor.Encrypt(input, output, recipientCertDer, permissions, algorithm);

        /// <summary>
        /// Encrypts a PDF for multiple recipients.
        /// </summary>
        public static void EncryptWithCertificates(
            Stream input, Stream output,
            IEnumerable<byte[]> recipientCertsDer,
            PdfPermissions permissions    = PdfPermissions.All,
            EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
            => PdfPublicKeyEncryptor.Encrypt(input, output, recipientCertsDer, permissions, algorithm);

        /// <summary>
        /// Runs a plaintext-only PDF operation over an encrypted document: decrypt → operate →
        /// re-encrypt. See <see cref="PdfEncryptedRoundTrip"/> for the two limits that survive it
        /// (the owner password cannot be preserved; re-encryption invalidates signatures).
        ///
        /// <para>The callable operations are the ones that refuse encrypted input on their own:
        /// <c>PdfSanitizer.Sanitize</c>, <c>PdfOptimizer.Optimize</c>, <c>PdfLinearizer.Linearize</c>,
        /// <c>PdfRedactor.RedactRegions</c> and <c>PdfAccessibility.RemediateWithReport</c>. They are
        /// named as prose rather than referenced, because this assembly must not depend on a plugin
        /// or a feature.</para>
        /// </summary>
        public static RoundTripResult RunOnEncrypted(
            Stream input, Stream output,
            EncryptedRoundTripOptions options,
            System.Action<Stream, Stream> operation)
            => PdfEncryptedRoundTrip.Run(input, output, options, operation);

        /// <summary>
        /// File-path overload of
        /// <see cref="RunOnEncrypted(Stream, Stream, EncryptedRoundTripOptions, System.Action{Stream, Stream})"/>.
        /// The output file is created only if the round trip succeeds — a failed run leaves no
        /// truncated or zero-byte file behind.
        /// </summary>
        public static RoundTripResult RunOnEncrypted(
            string inputPath, string outputPath,
            EncryptedRoundTripOptions options,
            System.Action<Stream, Stream> operation)
        {
            if (inputPath == null) throw new System.ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new System.ArgumentNullException(nameof(outputPath));

            RoundTripResult result;
            var produced = new MemoryStream();
            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                result = PdfEncryptedRoundTrip.Run(input, produced, options, operation);

            if (!result.Succeeded) return result;

            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                produced.Position = 0;
                produced.CopyTo(output);
            }
            return result;
        }

        /// <summary>
        /// Registers the BouncyCastle decryptor as the active implementation so that
        /// <c>PdfPageRenderer</c> can transparently decrypt encrypted PDFs.
        /// Call once at application startup when the Encryption assembly is loaded.
        /// </summary>
        public static void Register()
            => PdfDecryptorHolder.Current = new PdfDecryptorService();

        /// <summary>
        /// A DocMDP verdict for an <b>encrypted</b> certified document — both halves: whether it is
        /// certified and at what <c>/P</c>, and whether it has been modified since.
        /// </summary>
        /// <param name="input">The encrypted document. Not modified.</param>
        /// <param name="password">User or owner password.</param>
        /// <returns>The same analysis <c>PdfMdpVerifier</c> produces for a plaintext document.</returns>
        /// <remarks>
        /// <para><b>Why this cannot live in <c>Base</c>, and why it is not simply "decrypt and
        /// analyse".</b> <c>Base.Operations.Metadata.PdfMdpVerifier</c> refuses an encrypted
        /// document because a DocMDP verdict draws on two views of the file that decryption pulls
        /// apart. Whether it is certified needs the <b>object graph</b>, which an encrypted document
        /// does not expose. Whether it has been modified is decided by comparing cross-reference
        /// <b>offsets</b> against the certification <c>/ByteRange</c> — and a decrypted copy is
        /// re-serialised, so its offsets are its own while the stored <c>/ByteRange</c> still
        /// indexes the original. Analysing the copy alone answers the first correctly and the
        /// second silently wrongly, which is worse than refusing.</para>
        ///
        /// <para><b>So the two views are supplied separately.</b> The object graph comes from a
        /// decrypted copy; every byte-offset question is answered against the <b>original</b>
        /// stream, whose cross-reference table <c>PdfDocument.Load</c> parses before it returns
        /// early on <c>/Encrypt</c> — so no decryption is needed for it at all. The certified
        /// revision is the original's prefix, itself a complete encrypted document carrying the same
        /// <c>/Encrypt</c>, so it is decrypted the same way rather than being given up on.</para>
        ///
        /// <para><b>Three facts hold it together, and none is obvious:</b> the cross-reference table
        /// is available on an encrypted document; <c>/ByteRange</c> is an array of numbers, which
        /// are never encrypted, so it reads the same from either view and always names the
        /// original's bytes; and decryption <b>preserves object numbers</b>, so an xref entry for
        /// object N names object N in the copy.</para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        public static DocMdpAnalysis VerifyMdp(Stream input, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // The original, for offsets. Opened even though it is encrypted: Load parses the
            // cross-reference table before the /Encrypt early return, which is the whole reason
            // this shape works.
            using var original = PdfDocument.Open(input);
            long fileLength = input.Length;

            using var decrypted = DecryptToStream(input, password);
            using var objectGraph = PdfDocument.Open(decrypted);

            return DocMdpAnalyzer.Analyze(
                objectGraph, original, fileLength,
                prefix => OpenPossiblyEncrypted(prefix, password));
        }

        /// <summary>
        /// Signs an <b>encrypted</b> document in place — the signature revision is appended encrypted
        /// under the document's own file key, so the output opens with the same password and the
        /// signature verifies. See <see cref="PdfEncryptedSigner"/> for the composition and for what
        /// it declines (PDF MAC-protected and public-key encrypted documents, visible appearances).
        /// </summary>
        /// <param name="input">The encrypted PDF. Read only.</param>
        /// <param name="output">Receives the signed document; untouched when the signature is refused.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="parameters">The signature to make.</param>
        /// <returns>The plan the signature was completed from: the field name it was written into and the reserve.</returns>
        public static SignaturePlan Sign(Stream input, Stream output, string password, SignatureParameters parameters)
            => PdfEncryptedSigner.Sign(input, output, password, parameters);

        /// <summary>
        /// Verifies every signature in an <b>encrypted</b> document — the two-view analysis
        /// <see cref="VerifyMdp"/> makes, applied to the whole verifier.
        /// </summary>
        /// <param name="input">The encrypted document. Not modified.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="options">Verification policy; <c>null</c> for the defaults.</param>
        /// <returns>The same results <see cref="PdfSignatureVerifier"/> produces for a plaintext document.</returns>
        /// <remarks>
        /// <para><see cref="PdfSignatureVerifier"/> refuses an encrypted document because it can read
        /// neither half: the object graph needs decryption, and a decrypted copy's bytes are a fresh
        /// serialisation the stored <c>/ByteRange</c>s do not describe. So the two are supplied
        /// separately — the graph from a decrypted copy, every byte and offset from the original —
        /// through the verifier's own two-view entry, and a signed revision's prefix is decrypted the
        /// same way when the change analysis needs to read it.</para>
        /// <para>A signature's <c>/Contents</c> is the one string §7.6.2 leaves unencrypted, and
        /// <see cref="PdfDecryptor"/> leaves it alone on the way to the copy — which is what makes
        /// the CMS readable from the graph view at all.</para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
        /// <exception cref="Core.PdfException">The document is not encrypted, or the password is wrong.</exception>
        public static IReadOnlyList<SignatureVerificationResult> VerifySignatures(
            Stream input, string password, SignatureVerificationOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // The original, for bytes and offsets — parsed as far as its cross-reference table.
            using var original = PdfDocument.Open(input);
            if (!original.IsEncrypted)
                throw new Core.PdfException(
                    "The document is not encrypted; verify it with Tetitulz.Pdf.Signing.PdfSignatureVerifier directly.");

            using var decrypted = DecryptToStream(input, password);
            using var objectGraph = PdfDocument.Open(decrypted);

            return new PdfSignatureVerifier().VerifyAll(
                objectGraph, original, prefix => OpenPossiblyEncrypted(prefix, password), options);
        }

        /// <summary>
        /// Checks the document's ISO/TS 32004 PDF MAC token, in whichever placement it uses.
        /// </summary>
        /// <param name="input">The encrypted document. Not modified.</param>
        /// <param name="password">User or owner password — the token is keyed on the file key.</param>
        /// <remarks>
        /// <para><b>A companion to <see cref="VerifySignatures"/>, answering a different question.</b>
        /// A valid MAC says the holder of the file encryption key produced these bytes; <i>who</i>
        /// that was is what a signature answers. §6.6.3 NOTE 2 states the intent for a signed
        /// revision: the token carries both digests so it "can be validated independently of the
        /// underlying signature", protecting the document and the signature "irrespective of the
        /// validation status of said signature".</para>
        /// <para>A document with no token is reported <b>unprotected</b>, not invalid — see
        /// <see cref="PdfMacVerification"/>.</para>
        /// </remarks>
        /// <exception cref="Core.PdfException">The document is not encrypted, or the password is wrong.</exception>
        public static PdfMacVerification VerifyPdfMac(Stream input, string password)
            => PdfMacVerifier.Verify(input, password);

        /// <summary>Decrypts <paramref name="input"/> into a fresh seekable stream.</summary>
        private static MemoryStream DecryptToStream(Stream input, string password)
        {
            long start = input.CanSeek ? input.Position : 0L;
            var decrypted = new MemoryStream();
            try
            {
                if (input.CanSeek) input.Position = 0;
                PdfDecryptor.Decrypt(input, decrypted, password);
                decrypted.Position = 0;
                return decrypted;
            }
            finally
            {
                if (input.CanSeek) input.Position = start;
            }
        }

        /// <summary>
        /// Opens the certified revision's byte prefix, decrypting it first when it is encrypted.
        /// </summary>
        /// <remarks>
        /// The prefix is a prefix of the original file, so it is a complete document of the same
        /// kind. It is opened once to ask whether it is encrypted, because a certified revision of a
        /// plaintext file inside an encrypted one is not a case worth assuming either way.
        /// </remarks>
        private static PdfDocument OpenPossiblyEncrypted(byte[] prefix, string password)
        {
            var probe = PdfDocument.Open(new MemoryStream(prefix, writable: false));
            if (!probe.IsEncrypted) return probe;

            probe.Dispose();
            var plain = new MemoryStream();
            PdfDecryptor.Decrypt(new MemoryStream(prefix, writable: false), plain, password);
            plain.Position = 0;
            return PdfDocument.Open(plain);
        }
    }
}
