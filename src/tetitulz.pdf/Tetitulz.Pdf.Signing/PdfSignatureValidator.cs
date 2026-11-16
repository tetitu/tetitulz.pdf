// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Signature-validity status at the chain / revocation level.
    /// </summary>
    public enum PdfSignatureChainStatus
    {
        Unknown,
        Trusted,
        SelfSigned,
        UntrustedRoot,
        Invalid,
    }

    /// <summary>Revocation status of the signer certificate, determined from the revocation data
    /// embedded in the document's /DSS (LTV store) — and, under
    /// <see cref="Models.SignatureVerificationOptions.OnlineRevocation"/>, from responses and lists
    /// fetched from the certificates' own URLs; this validator's doors fetch nothing.</summary>
    public enum PdfRevocationStatus
    {
        /// <summary>No revocation data, embedded or fetched, covered this certificate (status not determined).</summary>
        NotChecked,
        /// <summary>Revocation data was present but did not yield a conclusive status.</summary>
        Unknown,
        /// <summary>A signature-verified, current OCSP response or CRL reports the certificate as not revoked.</summary>
        Good,
        /// <summary>A signature-verified OCSP response or CRL reports the certificate as revoked.</summary>
        Revoked,
    }

    /// <summary>
    /// Result record returned from <see cref="PdfSignatureValidator.Validate(Stream, IEnumerable{X509Certificate})"/>.
    /// </summary>
    public sealed class PdfSignatureValidationResult
    {
        /// <summary>
        /// The signature is cryptographically intact and structurally sound. This is
        /// <b>not</b> a trust decision — the embedded certificate is attacker-controlled. To
        /// decide whether the signature is genuinely <i>trusted</i>, use
        /// <see cref="IsFullyTrusted"/>, or require
        /// <see cref="ChainStatus"/> == <see cref="PdfSignatureChainStatus.Trusted"/>
        /// (which requires supplying trust anchors). Never treat
        /// <c>IsValid</c> alone as "the document is authentically signed".
        /// </summary>
        public bool IsValid                     { get; }
        public bool HasSignature                { get; }
        public string? SignerSubject            { get; }
        public string? SignerIssuer             { get; }
        /// <summary>Claimed signing time from the CMS attributes; unauthenticated, informational.</summary>
        public DateTime? SignedAt               { get; }
        /// <summary>Trust status of the signer's X.509 chain. Combine with
        /// <see cref="IsValid"/>: a fully-trusted signature is
        /// <c>IsValid &amp;&amp; ChainStatus == Trusted</c>, which
        /// <see cref="IsFullyTrusted"/> expresses directly.</summary>
        public PdfSignatureChainStatus ChainStatus { get; }
        /// <summary>Revocation status from embedded /DSS data (LTV). A <c>Revoked</c> result also
        /// forces <see cref="ChainStatus"/> to <c>Invalid</c>. Defaults to <c>NotChecked</c>.</summary>
        public PdfRevocationStatus RevocationStatus { get; set; } = PdfRevocationStatus.NotChecked;
        public IReadOnlyList<string> Warnings   { get; }
        public string? ErrorMessage             { get; }

        /// <summary>Fully qualified name of the signature field this result describes.</summary>
        public string? FieldName                { get; set; }

        /// <summary>
        /// True when a trust-anchored RFC 3161 timestamp fixed the assessment time. Without one,
        /// certificate validity was assessed at the current time so an expired certificate fails.
        /// </summary>
        public bool HasTrustedTimestamp         { get; set; }

        /// <summary>
        /// The complete verdict: intact, structurally sound, chains to a supplied trust anchor,
        /// and not reported revoked by embedded revocation data.
        /// </summary>
        public bool IsFullyTrusted =>
            IsValid
            && ChainStatus == PdfSignatureChainStatus.Trusted
            && RevocationStatus != PdfRevocationStatus.Revoked;

        public PdfSignatureValidationResult(bool isValid, bool hasSignature,
            string? signerSubject, string? signerIssuer, DateTime? signedAt,
            PdfSignatureChainStatus chainStatus, IReadOnlyList<string>? warnings,
            string? errorMessage)
        {
            IsValid = isValid;
            HasSignature = hasSignature;
            SignerSubject = signerSubject;
            SignerIssuer = signerIssuer;
            SignedAt = signedAt;
            ChainStatus = chainStatus;
            Warnings = warnings ?? Array.Empty<string>();
            ErrorMessage = errorMessage;
        }
    }

    /// <summary>
    /// Convenience facade over <see cref="PdfSignatureVerifier"/> for callers that want a single
    /// signature's trust verdict.
    /// <para>
    /// A thin adapter, so there is exactly one verification pipeline. A parallel implementation of
    /// CMS verification, ByteRange checking and chain walking would drift: missing the MD5/MD2 hard
    /// reject, the SHA-1 weak flag or the ESS signer-binding check, a signature
    /// <see cref="PdfSignatureVerifier"/> rejects could be reported valid here; walking only the top
    /// level of /Fields would miss a signature nested under a parent field node.
    /// </para>
    /// </summary>
    public static class PdfSignatureValidator
    {
        /// <summary>Validates a PDF file's first signature field.</summary>
        public static PdfSignatureValidationResult Validate(string inputPath,
            IEnumerable<X509Certificate>? trustAnchors = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Validate(fs, trustAnchors);
        }

        /// <summary>Stream overload.</summary>
        public static PdfSignatureValidationResult Validate(Stream input,
            IEnumerable<X509Certificate>? trustAnchors = null)
            => Validate(input, BuildOptions(trustAnchors));

        /// <summary>
        /// Validates a PDF's first signature field under an explicit policy (trust anchors,
        /// weak-algorithm handling, revocation freshness).
        /// </summary>
        public static PdfSignatureValidationResult Validate(Stream input,
            SignatureVerificationOptions? options)
        {
            var all = ValidateAll(input, options);
            if (all.Count == 0)
                return new PdfSignatureValidationResult(
                    isValid: false, hasSignature: false,
                    signerSubject: null, signerIssuer: null, signedAt: null,
                    chainStatus: PdfSignatureChainStatus.Unknown,
                    warnings: null,
                    errorMessage: "No signature field found");
            return all[0];
        }

        /// <summary>
        /// Validates <b>every</b> signature field, not just the first.
        /// <para>
        /// Document timestamps (/SubFilter /ETSI.RFC3161) are ordered after real signatures, so
        /// <see cref="Validate(Stream, IEnumerable{X509Certificate})"/> still describes the
        /// document's actual signature, and a raw TimeStampToken is never parsed as detached CMS
        /// because a timestamp happens to be listed first.
        /// </para>
        /// </summary>
        public static IReadOnlyList<PdfSignatureValidationResult> ValidateAll(Stream input,
            SignatureVerificationOptions? options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var seekable = EnsureSeekable(input);
            byte[] pdfBytes;
            using (var buf = new MemoryStream())
            {
                seekable.Position = 0;
                seekable.CopyTo(buf);
                pdfBytes = buf.ToArray();
            }

            using var doc = PdfDocument.Open(new MemoryStream(pdfBytes, writable: false));
            SigningHelpers.RefuseEncryptedVerification(doc);
            var results = new PdfSignatureVerifier().VerifyAll(doc, options);

            var signatures = new List<PdfSignatureValidationResult>();
            var timestamps = new List<PdfSignatureValidationResult>();
            foreach (var r in results)
            {
                var mapped = Map(r);
                if (r.SubFilter == "ETSI.RFC3161") timestamps.Add(mapped);
                else signatures.Add(mapped);
            }
            signatures.AddRange(timestamps);
            return signatures;
        }

        /// <summary>Validates every signature field of a file.</summary>
        public static IReadOnlyList<PdfSignatureValidationResult> ValidateAll(string inputPath,
            SignatureVerificationOptions? options = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ValidateAll(fs, options);
        }

        /// <summary>
        /// Adapts the anchors-only overloads onto the options object the engine takes.
        /// <para>
        /// The <c>RejectWeakAlgorithms</c> seeding is not incidental. A freshly-constructed options
        /// object carries <c>RejectWeakAlgorithms = true</c> by default, so naming a trust anchor
        /// cannot silently relax weak-algorithm policy. What the seeding does
        /// is carry a host's deliberate RELAXATION across: a host that set the assembly-internal
        /// <c>CmsSignatureVerifier.RejectWeakAlgorithms</c> to <c>false</c> is not re-tightened by
        /// the mere act of naming a trust anchor. Any caller stating both explicitly uses the
        /// <c>SignatureVerificationOptions</c> overload, which never consults the static at all.
        /// </para>
        /// </summary>
        private static SignatureVerificationOptions? BuildOptions(IEnumerable<X509Certificate>? trustAnchors)
            => trustAnchors == null
                ? null
                : new SignatureVerificationOptions
                {
                    TrustAnchors = trustAnchors,
                    RejectWeakAlgorithms = CmsSignatureVerifier.RejectWeakAlgorithms,
                };

        private static PdfSignatureValidationResult Map(SignatureVerificationResult r)
        {
            // A structural failure with no chain verdict of its own reports Invalid, matching the
            // shape this facade returned before it delegated.
            var chain = r.ChainStatus;
            if (!r.IsValid && chain == PdfSignatureChainStatus.Unknown)
                chain = PdfSignatureChainStatus.Invalid;

            string? error = r.ValidationErrors.Count == 0
                ? null
                : string.Join("; ", r.ValidationErrors);

            return new PdfSignatureValidationResult(
                isValid: r.IsValid,
                hasSignature: true,
                signerSubject: r.SignerSubject,
                signerIssuer: r.SignerIssuer,
                signedAt: r.ClaimedSigningTime,
                chainStatus: chain,
                warnings: r.Warnings.ToArray(),
                errorMessage: error)
            {
                RevocationStatus = r.RevocationStatus,
                FieldName = r.FieldName,
                HasTrustedTimestamp = r.HasTrustedTimestamp,
            };
        }

        // Unlike the signing/timestamping paths, verification must start from the beginning of
        // the stream, hence rewind: true.
        private static Stream EnsureSeekable(Stream s) => SigningHelpers.EnsureSeekable(s, rewind: true);
    }
}
