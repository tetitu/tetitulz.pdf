// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Models
{
    public sealed class SignatureParameters
    {
        /// <summary>
        /// External signer for PKCS#11 tokens, cloud HSMs, smart cards, or any
        /// signing provider where the private key never leaves the device.
        /// When set, CertificatePath/CertificateStream/CertificatePassword are ignored.
        /// </summary>
        public IExternalSigner? ExternalSigner { get; set; }

        /// <summary>Path to a .pfx/.p12 certificate file.</summary>
        public string? CertificatePath { get; set; }

        /// <summary>Password for the certificate file.</summary>
        public string? CertificatePassword { get; set; }

        /// <summary>Alternative: provide certificate as a stream.</summary>
        public Stream? CertificateStream { get; set; }

        /// <summary>Digest algorithm. Default: SHA-256.  Recognised values:
        /// SHA-1, SHA-256, SHA-384, SHA-512, SHA3-256, SHA3-384, SHA3-512.</summary>
        public string DigestAlgorithm { get; set; } = "SHA-256";

        /// <summary>
        /// Optional signature-algorithm OID to select an algorithm other than
        /// the BouncyCastle default for the supplied key.  When null, the
        /// generator uses RSA + SHA-N for RSA keys and ECDSA-with-SHA-N for
        /// EC keys based on <see cref="DigestAlgorithm"/>.  Use
        /// <see cref="SignatureAlgorithmOids"/> for common values.
        /// </summary>
        public string? SignatureAlgorithmOid { get; set; }

        /// <summary>Reason for signing.</summary>
        public string? Reason { get; set; }

        /// <summary>Location of signing.</summary>
        public string? Location { get; set; }

        /// <summary>Contact info of signer.</summary>
        public string? ContactInfo { get; set; }

        /// <summary>Name of the signature form field. Default: "Signature1".</summary>
        public string SignatureFieldName { get; set; } = "Signature1";

        /// <summary>
        /// Estimated size of the PKCS#7 signature in bytes.
        /// Must be large enough to hold the DER-encoded CMS structure including
        /// any embedded timestamp token and certificate chain.
        /// Default: 16384 bytes (produces 32768 hex chars).
        /// Increase to 32768 when using a TSA or embedding OCSP responses.
        /// </summary>
        public int EstimatedSignatureSize { get; set; } = 16384;

        /// <summary>Signing time. Default: current UTC time.</summary>
        /// <remarks>
        /// The claimed time of signing, written as the signature dictionary's <c>/M</c> — and, under
        /// <c>adbe.pkcs7.detached</c>, as the same instant in the CMS <c>signing-time</c> attribute of a
        /// locally built signature. Under PAdES (<see cref="UseEtsiPadesSubFilter"/>) the CMS carries
        /// no <c>signing-time</c> attribute: ETSI EN 319 142-1 §6.3, Table 1. It is unauthenticated
        /// either way; a timestamp is what proves a time.
        /// </remarks>
        public DateTime? SigningTime { get; set; }

        /// <summary>
        /// URL of an RFC 3161 Timestamp Authority (TSA).
        /// When set, a trusted timestamp token is requested from the TSA and
        /// embedded as an unsigned attribute in the CMS SignerInfo structure.
        /// Example: "http://timestamp.digicert.com"
        /// Leave null to skip timestamping (signing time from local clock only).
        /// </summary>
        public string? TimestampAuthorityUrl { get; set; }

        /// <summary>
        /// Whether the timestamping server a signature field's seed value names
        /// (<c>/SV /TimeStamp /URL</c>, ISO 32000 Table 234) is asked when
        /// <see cref="TimestampAuthorityUrl"/> is not set. Off by default.
        /// </summary>
        /// <remarks>
        /// The URL is the document author's, not the caller's: followed blindly it is a request the
        /// document sends from the signing host to an address of its choosing. Off, a field whose seed
        /// value <i>requires</i> a timestamp (<c>/Ff 1</c>) is refused unless the caller names a server.
        /// On, the request goes out as an OCSP request to a responder a certificate names does: to
        /// an http or https URL whose host the private-address guard passes, connected to an address
        /// it passed (on .NET Framework with credentials or a proxy, the check alone), no redirect
        /// followed, under the revocation fetches' budget; of
        /// <see cref="TimestampNetwork"/>'s credentials only a <c>CredentialCache</c> — scoped to the
        /// hosts it lists — travels with it.
        /// </remarks>
        public bool UseDocumentTimestampAuthority { get; set; }

        /// <summary>
        /// Credentials, proxy and timeout for the TSA request. Null keeps the anonymous,
        /// platform-default behaviour.
        /// </summary>
        /// <remarks>
        /// <b>Most commercial timestamp authorities are authenticated and most enterprises reach
        /// the internet through a proxy</b>, which is the difference between "timestamping works"
        /// and "timestamping works here". It carries plain <c>System.Net</c> types because this
        /// assembly also targets <c>netstandard2.0</c>, where an <c>HttpMessageHandler</c> seam is
        /// not expressible — <see cref="System.Net.ICredentials"/> and
        /// <see cref="System.Net.IWebProxy"/> are, on both legs.
        /// </remarks>
        public SigningNetworkOptions? TimestampNetwork { get; set; }

        /// <summary>
        /// Digest algorithm for the RFC 3161 timestamp request.
        /// Usually matches <see cref="DigestAlgorithm"/>. Default: SHA-256.
        /// </summary>
        public string TimestampDigestAlgorithm { get; set; } = "SHA-256";

        /// <summary>
        /// When true and <see cref="TimestampAuthorityUrl"/> is set,
        /// embed an LTV (Long-Term Validation) /DSS dictionary in the PDF
        /// containing OCSP responses for each certificate in the chain.
        /// Default: false.
        /// </summary>
        public bool EmbedLtv { get; set; } = false;

        // ----------------------------------------------------------------
        //  PAdES (ETSI EN 319 102-1) — gap 13.2
        // ----------------------------------------------------------------

        /// <summary>
        /// When true, sets /SubFilter to /ETSI.CAdES.detached (PAdES) instead of
        /// /adbe.pkcs7.detached (legacy Adobe).
        /// Required for PAdES B-B / B-T / B-LT / B-LTA compliance.
        /// Default: false.
        /// </summary>
        public bool UseEtsiPadesSubFilter { get; set; } = false;

        /// <summary>
        /// Configures this <see cref="SignatureParameters"/> for the requested
        /// PAdES baseline level per ETSI EN 319 142-1.  This is a convenience
        /// over hand-tweaking <see cref="UseEtsiPadesSubFilter"/>,
        /// <see cref="TimestampAuthorityUrl"/>, and <see cref="EmbedLtv"/>:
        /// <list type="bullet">
        ///   <item><b>B-B</b>: PAdES SubFilter only — basic detached signature.</item>
        ///   <item><b>B-T</b>: B-B + RFC 3161 timestamp.</item>
        ///   <item><b>B-LT</b>: B-T + LTV (/DSS) with OCSP/CRL.</item>
        ///   <item><b>B-LTA</b>: B-LT + document timestamp (long-term archival). <c>PdfSigner.Sign</c>
        ///       appends the document timestamp itself, as a third revision over the signature and its
        ///       <c>/DSS</c>, from the same TSA with <see cref="TimestampDigestAlgorithm"/>.
        ///       <c>PdfSigner.Plan</c> refuses it, as it refuses <see cref="EmbedLtv"/>.</item>
        /// </list>
        /// </summary>
        /// <param name="baseline">Target baseline level.</param>
        /// <param name="tsaUrl">RFC 3161 TSA URL — required for B-T and above.</param>
        public SignatureParameters ApplyPadesBaseline(PadesBaseline baseline, string? tsaUrl = null)
        {
            UseEtsiPadesSubFilter = true;
            switch (baseline)
            {
                case PadesBaseline.B_B:
                    TimestampAuthorityUrl = null;
                    EmbedLtv = false;
                    break;
                case PadesBaseline.B_T:
                    if (string.IsNullOrEmpty(tsaUrl))
                        throw new ArgumentException("PAdES B-T requires a TSA URL.", nameof(tsaUrl));
                    TimestampAuthorityUrl = tsaUrl;
                    EmbedLtv = false;
                    break;
                case PadesBaseline.B_LT:
                case PadesBaseline.B_LTA:
                    if (string.IsNullOrEmpty(tsaUrl))
                        throw new ArgumentException(
                            $"PAdES {baseline} requires a TSA URL.", nameof(tsaUrl));
                    TimestampAuthorityUrl = tsaUrl;
                    EmbedLtv = true;
                    // B-LT/B-LTA embeds a timestamp token and revocation data, which does not fit
                    // the 16 KB default; the same reserve the timestamper and signer use.
                    if (EstimatedSignatureSize < SigningDefaults.SignatureReserveBytes)
                        EstimatedSignatureSize = SigningDefaults.SignatureReserveBytes;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(baseline));
            }
            // PdfSigner.Sign reads this level: for B-LTA it appends the document timestamp over
            // the whole signature package (the signature revision and the /DSS) in the same call.
            PadesBaselineLevel = baseline;
            return this;
        }

        /// <summary>
        /// PAdES baseline level set via <see cref="ApplyPadesBaseline"/>, or
        /// <see cref="PadesBaseline.None"/> when none has been requested.
        /// </summary>
        public PadesBaseline PadesBaselineLevel { get; private set; } = PadesBaseline.None;

        // ----------------------------------------------------------------
        //  DocMDP / Certified signatures — gap 13.3
        // ----------------------------------------------------------------

        /// <summary>
        /// When set to a value other than <see cref="DocMdpPermission.None"/>,
        /// produces a <em>certified</em> signature with a /DocMDP /Reference entry.
        /// The first signer must use this; subsequent signers should leave it None.
        /// </summary>
        /// <remarks>
        /// Enforced, not merely advised: signing refuses to certify a document that already carries
        /// a certification or an approval signature, because ISO 32000-2 §12.8.1 allows at most one
        /// certification signature and requires approval signatures to follow it. Document timestamps
        /// place no such constraint.
        /// </remarks>
        public DocMdpPermission DocMdpPermission { get; set; } = DocMdpPermission.None;

        /// <summary>
        /// Sign a document certified with DocMDP <c>/P 1</c> even though the signature invalidates that
        /// certification. Default <c>false</c>, which refuses.
        /// </summary>
        /// <remarks>
        /// ISO 32000-2 §12.8.2.2: under <c>/P 1</c> "no changes to the document shall be permitted; any
        /// change to the document shall invalidate the signature". A new signature is such a change, so
        /// the author's certification reports invalid afterwards — which a relying party reads as the
        /// author's signature having failed. Opt in only when that is the intent, for example to record
        /// that a certified document was deliberately superseded.
        /// </remarks>
        public bool AllowCertificationInvalidation { get; set; }

        /// <summary>
        /// Usage-rights flags. When non-empty, this call writes a <b>usage-rights signature</b>
        /// (ISO 32000-2 §12.8.1) instead of an ordinary approval or certification signature: a
        /// signature dictionary in no field, referenced only from an indirect <c>/Perms /UR3</c>
        /// entry, declaring the listed extended features (form fill-in, annotation, save). The
        /// transform parameters are ISO 32000-2 Table 258; the transform method itself is
        /// §12.8.2.3, <i>UR</i>.
        /// </summary>
        /// <remarks>
        /// <para><b>§12.8.1's own rule, enforced, not merely documented.</b> "At most one usage
        /// rights signature (PDF 1.5, deprecated in PDF 2.0). It shall only be applied if no other
        /// type of signature is already present." This is why <see cref="UsageRights"/> and
        /// <see cref="DocMdpPermission"/> cannot be set together (<see cref="PdfSigningException"/>),
        /// and why signing with <see cref="UsageRights"/> refuses a document that already carries an
        /// approval signature, a certification, or a prior <c>/Perms /UR3</c>. A usage-rights
        /// signature is therefore always the document's only signature — sign it before any
        /// approval signature is added, never after or alongside one.</para>
        ///
        /// <para><b>No field, no <c>/AcroForm</c> change.</b> §12.8.1 is explicit — "not from a
        /// signature field" — so this call adds no signature field and no widget annotation;
        /// <c>PdfUsageRightsReader</c> (the conforming reader) finds it at <c>/Perms /UR3</c>
        /// directly. viewers honour <c>/UR3</c> only from a certificate chaining to a
        /// Reader-recognised root, so a self-signed grant is ignored there regardless.</para>
        ///
        /// <para>This library's own reader also walks signature fields, as a fallback, for documents
        /// whose usage-rights grant sits in a field-based approval signature's <c>/Reference</c> array
        /// with no <c>/Perms /UR3</c> entry, as earlier versions of this signer wrote it. Usage rights
        /// are deprecated in PDF 2.0.</para>
        /// </remarks>
        public UsageRights UsageRights { get; set; } = UsageRights.None;
    }


    /// <summary>
    /// Convenience enum for the most common PDF signature algorithms.  Use
    /// <see cref="SignatureParametersExtensions.UseAlgorithm"/> to set both
    /// <see cref="SignatureParameters.DigestAlgorithm"/> and
    /// <see cref="SignatureParameters.SignatureAlgorithmOid"/> in one call.
    /// </summary>
    public enum SignatureAlgorithm
    {
        /// <summary>Default — let BouncyCastle pick based on key type.</summary>
        Default = 0,
        RsaSha256,
        RsaSha384,
        RsaSha512,
        RsaPssSha256,
        EcdsaSha256,
        EcdsaSha384,
        EcdsaSha512,
        Ed25519,
        Ed448,
        /// <summary>ML-DSA-44 (NIST FIPS 204, security level 2).</summary>
        MlDsa44,
        /// <summary>ML-DSA-65 (NIST FIPS 204, security level 3).</summary>
        MlDsa65,
        /// <summary>ML-DSA-87 (NIST FIPS 204, security level 5).</summary>
        MlDsa87,
        /// <summary>GOST R 34.10-2012 with 256-bit key (Streebog-256 digest).</summary>
        GostR3410_2012_256,
        /// <summary>GOST R 34.10-2012 with 512-bit key (Streebog-512 digest).</summary>
        GostR3410_2012_512,
    }

    /// <summary>Convenience extensions over <see cref="SignatureParameters"/>.</summary>
    public static class SignatureParametersExtensions
    {
        /// <summary>
        /// Sets <see cref="SignatureParameters.DigestAlgorithm"/> and
        /// <see cref="SignatureParameters.SignatureAlgorithmOid"/> to the
        /// canonical pair for <paramref name="alg"/>.  Pass
        /// <see cref="SignatureAlgorithm.Default"/> to clear the explicit OID
        /// and let BouncyCastle pick.
        /// </summary>
        public static SignatureParameters UseAlgorithm(this SignatureParameters p, SignatureAlgorithm alg)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            switch (alg)
            {
                case SignatureAlgorithm.Default:
                    p.SignatureAlgorithmOid = null;
                    break;
                case SignatureAlgorithm.RsaSha256:
                    p.DigestAlgorithm = "SHA-256";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.RsaWithSha256;
                    break;
                case SignatureAlgorithm.RsaSha384:
                    p.DigestAlgorithm = "SHA-384";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.RsaWithSha384;
                    break;
                case SignatureAlgorithm.RsaSha512:
                    p.DigestAlgorithm = "SHA-512";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.RsaWithSha512;
                    break;
                case SignatureAlgorithm.RsaPssSha256:
                    p.DigestAlgorithm = "SHA-256";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.RsaPss;
                    break;
                case SignatureAlgorithm.EcdsaSha256:
                    p.DigestAlgorithm = "SHA-256";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.EcdsaWithSha256;
                    break;
                case SignatureAlgorithm.EcdsaSha384:
                    p.DigestAlgorithm = "SHA-384";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.EcdsaWithSha384;
                    break;
                case SignatureAlgorithm.EcdsaSha512:
                    p.DigestAlgorithm = "SHA-512";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.EcdsaWithSha512;
                    break;
                case SignatureAlgorithm.Ed25519:
                    p.DigestAlgorithm = "SHA-512"; // Ed25519 hashes internally; SHA-512 is the implicit attribute hash
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.Ed25519;
                    break;
                case SignatureAlgorithm.Ed448:
                    p.DigestAlgorithm = "SHA-512";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.Ed448;
                    break;
                case SignatureAlgorithm.MlDsa44:
                    p.DigestAlgorithm = "SHA-256";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.MlDsa44;
                    break;
                case SignatureAlgorithm.MlDsa65:
                    p.DigestAlgorithm = "SHA-384";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.MlDsa65;
                    break;
                case SignatureAlgorithm.MlDsa87:
                    p.DigestAlgorithm = "SHA-512";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.MlDsa87;
                    break;
                case SignatureAlgorithm.GostR3410_2012_256:
                    p.DigestAlgorithm = "GOST3411-2012-256";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.GostR3410_2012_256;
                    break;
                case SignatureAlgorithm.GostR3410_2012_512:
                    p.DigestAlgorithm = "GOST3411-2012-512";
                    p.SignatureAlgorithmOid = SignatureAlgorithmOids.GostR3410_2012_512;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(alg));
            }
            return p;
        }
    }

    /// <summary>
    /// Common signature-algorithm OIDs callers can plug into
    /// <see cref="SignatureParameters.SignatureAlgorithmOid"/>.  These are the
    /// algorithm identifiers the BouncyCastle CMS generator recognises; when
    /// the supplied key doesn't match the algorithm (e.g. ECDSA OID with an
    /// RSA key) the generator throws.
    /// </summary>
    public static class SignatureAlgorithmOids
    {
        /// <summary>RSA-PSS (RFC 4056).</summary>
        public const string RsaPss = "1.2.840.113549.1.1.10";
        /// <summary>RSA + SHA-256.</summary>
        public const string RsaWithSha256 = "1.2.840.113549.1.1.11";
        /// <summary>RSA + SHA-384.</summary>
        public const string RsaWithSha384 = "1.2.840.113549.1.1.12";
        /// <summary>RSA + SHA-512.</summary>
        public const string RsaWithSha512 = "1.2.840.113549.1.1.13";
        /// <summary>ECDSA + SHA-256.</summary>
        public const string EcdsaWithSha256 = "1.2.840.10045.4.3.2";
        /// <summary>ECDSA + SHA-384.</summary>
        public const string EcdsaWithSha384 = "1.2.840.10045.4.3.3";
        /// <summary>ECDSA + SHA-512.</summary>
        public const string EcdsaWithSha512 = "1.2.840.10045.4.3.4";
        /// <summary>Ed25519 (RFC 8032).</summary>
        public const string Ed25519 = "1.3.101.112";
        /// <summary>Ed448 (RFC 8032).</summary>
        public const string Ed448 = "1.3.101.113";
        /// <summary>ML-DSA-44 (NIST FIPS 204, id-ML-DSA-44).</summary>
        public const string MlDsa44 = "2.16.840.1.101.3.4.3.17";
        /// <summary>ML-DSA-65 (NIST FIPS 204, id-ML-DSA-65).</summary>
        public const string MlDsa65 = "2.16.840.1.101.3.4.3.18";
        /// <summary>ML-DSA-87 (NIST FIPS 204, id-ML-DSA-87).</summary>
        public const string MlDsa87 = "2.16.840.1.101.3.4.3.19";
        /// <summary>GOST R 34.10-2012 with 256-bit key (RFC 7091).</summary>
        public const string GostR3410_2012_256 = "1.2.643.7.1.1.1.1";
        /// <summary>GOST R 34.10-2012 with 512-bit key (RFC 7091).</summary>
        public const string GostR3410_2012_512 = "1.2.643.7.1.1.1.2";
    }

    /// <summary>
    /// PAdES (PDF Advanced Electronic Signatures) baseline conformance levels
    /// per ETSI EN 319 142-1.  Each level subsumes the previous: B-T requires
    /// everything in B-B plus a timestamp; B-LT adds revocation; B-LTA adds
    /// long-term archival via a wrapping document timestamp.
    /// </summary>
    public enum PadesBaseline
    {
        /// <summary>No baseline applied (caller hand-tunes individual flags).</summary>
        None = 0,
        /// <summary>Basic — detached CAdES signature, no timestamp.</summary>
        B_B  = 1,
        /// <summary>Time — B-B + RFC 3161 signature timestamp.</summary>
        B_T  = 2,
        /// <summary>Long-Term — B-T + /DSS revocation data (OCSP/CRL).</summary>
        B_LT = 3,
        /// <summary>Long-Term Archival — B-LT + wrapping /DocTimeStamp.
        /// <c>PdfSigner.Sign</c> appends the document timestamp in the same call;
        /// <c>PdfSigner.Plan</c> refuses this level.</summary>
        B_LTA = 4,
    }

    /// <summary>
    /// DocMDP permission levels for certified (locked) signatures.
    /// </summary>
    public enum DocMdpPermission
    {
        /// <summary>No DocMDP restriction — ordinary approval signature.</summary>
        None = 0,
        /// <summary>P=1: No changes allowed after certification.</summary>
        NoChanges = 1,
        /// <summary>P=2: Only form field fill-in and digital signatures.</summary>
        FormsAndSignatures = 2,
        /// <summary>P=3: Annotations, form fill-in, and digital signatures.</summary>
        CommentAnnotations = 3
    }
}
