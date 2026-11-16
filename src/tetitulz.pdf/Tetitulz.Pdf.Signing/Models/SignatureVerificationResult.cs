// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Models
{
    public sealed class SignatureVerificationResult
    {
        /// <summary>Whether the signature's cryptographic integrity check passed.</summary>
        public bool IsIntegrityValid { get; set; }

        /// <summary>Whether the ByteRange covers the entire file (true for the last/only signature).</summary>
        public bool CoversEntireDocument { get; set; }

        /// <summary>Whether the ByteRange is contiguous (no unexpected gaps), covering the document revision at signing time.</summary>
        public bool CoversRevision { get; set; }

        /// <summary>End offset (offset2 + length2) of this signature's ByteRange coverage,
        /// i.e. the file position up to which this signature protects content. Used by
        /// <c>VerifyAll</c> to detect bytes appended after the last signature (shadow attack).</summary>
        public long ByteRangeCoverageEnd { get; set; }

        /// <summary>Common name of the signer certificate (or TSA name for document timestamps).</summary>
        public string? SignerCommonName { get; set; }

        /// <summary>Full subject DN of the signer certificate; null when no certificate was resolved.</summary>
        public string? SignerSubject { get; set; }

        /// <summary>Full issuer DN of the signer certificate; null when no certificate was resolved.</summary>
        public string? SignerIssuer { get; set; }

        /// <summary>Signing time from the /M entry of the signature dictionary.</summary>
        public string? SigningTime { get; set; }

        /// <summary>
        /// The claimed signing time from the CMS <c>Pkcs9AtSigningTime</c> signed attribute.
        /// <b>Informational only</b> — it is asserted by the signer and authenticated by no
        /// trusted clock, so it must never be used to extend a certificate past its NotAfter.
        /// </summary>
        public DateTime? ClaimedSigningTime { get; set; }

        /// <summary>Digest algorithm used (e.g. "SHA-256").</summary>
        public string? DigestAlgorithm { get; set; }

        /// <summary>
        /// True when the signature relies on a cryptographically weak algorithm (e.g. SHA-1 or
        /// MD5 digests, RSASSA-PSS over SHA-1, or an undersized signer key) — see
        /// <see cref="WeakAlgorithmReason"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Under the default policy this clears <see cref="IsValid"/>.</b>
        /// <see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/> defaults to <c>true</c>,
        /// so a SHA-1 signature is refused rather than merely flagged. Set it to <c>false</c> for
        /// the advisory behaviour: the flag is still
        /// raised here and <see cref="IsValid"/> stays true.
        /// </para>
        /// <para>
        /// The cryptographic verdict is kept separately either way. A policy refusal leaves
        /// <see cref="IsIntegrityValid"/> true — it says the bytes verified and the policy declined
        /// them, which a caller must be able to tell from a tampered document.
        /// </para>
        /// </remarks>
        public bool WeakAlgorithm { get; set; }

        /// <summary>
        /// Human-readable description of the weakness when <see cref="WeakAlgorithm"/> is true
        /// (e.g. "digest algorithm is SHA-1 (collision-broken)."); otherwise <c>null</c>.
        /// </summary>
        public string? WeakAlgorithmReason { get; set; }

        /// <summary>Reason for signing (/Reason entry).</summary>
        public string? Reason { get; set; }

        /// <summary>Signing location (/Location entry).</summary>
        public string? Location { get; set; }

        /// <summary>
        /// Fully qualified name of the signature field (ISO 32000-2 §12.7.4.2) — its own <c>/T</c> for a
        /// top-level field, <c>parent.child</c> for a nested one; <c>null</c> when no node carries a
        /// <c>/T</c>. It is the name <c>PdfSignatureVerifier.Verify</c> and the counter-signer accept.
        /// </summary>
        public string? FieldName { get; set; }

        /// <summary>PDF /SubFilter value (e.g. "adbe.pkcs7.detached", "ETSI.CAdES.detached", "ETSI.RFC3161").</summary>
        public string? SubFilter { get; set; }

        /// <summary>For document timestamps (/SubFilter /ETSI.RFC3161): the certified time in the TSA token.</summary>
        public DateTime? TimestampTime { get; set; }

        /// <summary>
        /// True when an RFC 3161 timestamp covering this signature verified AND its TSA
        /// certificate chained to a supplied trust anchor. Only then may
        /// <see cref="TimestampTime"/> be relied on as an authenticated clock — an unanchored
        /// token can assert any time at all.
        /// </summary>
        public bool HasTrustedTimestamp { get; set; }

        /// <summary>
        /// The moment at which certificate validity was assessed: a trusted timestamp when one is
        /// present, otherwise the verification time ("now").
        /// </summary>
        public DateTime? AssessmentTime { get; set; }

        /// <summary>
        /// Trust status of the signer's X.509 chain. <see cref="PdfSignatureChainStatus.Unknown"/>
        /// when no trust anchors were supplied to the verify call.
        /// </summary>
        public PdfSignatureChainStatus ChainStatus { get; set; } = PdfSignatureChainStatus.Unknown;

        /// <summary>
        /// Revocation status resolved from the revocation data embedded in the document's /DSS (LTV
        /// store) and, when <see cref="SignatureVerificationOptions.OnlineRevocation"/> is set, from
        /// OCSP responses and CRLs fetched from the certificates' own URLs. With neither, a
        /// certificate no data covers reports <see cref="PdfRevocationStatus.NotChecked"/>.
        /// </summary>
        public PdfRevocationStatus RevocationStatus { get; set; } = PdfRevocationStatus.NotChecked;

        /// <summary>
        /// Hard errors: structural or cryptographic problems that make the signature untrustworthy.
        /// A non-empty list forces <see cref="IsValid"/> to false.
        /// </summary>
        public List<string> ValidationErrors { get; } = new List<string>();

        /// <summary>
        /// Advisory findings that do <b>not</b> invalidate the signature — e.g. "no verified
        /// timestamp present, validity assessed at the current time", or chain-walk detail.
        /// <para>
        /// These are kept out of <see cref="ValidationErrors"/> deliberately: everything in that
        /// list clears <see cref="IsValid"/>, so mixing advisories in would make an otherwise-good
        /// signature report as invalid.
        /// </para>
        /// </summary>
        public List<string> Warnings { get; } = new List<string>();

        /// <summary>
        /// The <c>/Contents</c> string carried bytes after the CMS object that were not the zero
        /// padding a writer reserves.
        /// </summary>
        /// <remarks>
        /// Those bytes lie in the <c>/ByteRange</c> gap, so they are unsigned and were writable
        /// after signing. Reported unconditionally; whether it invalidates the signature is
        /// <c>SignatureVerificationOptions.RejectNonZeroContentsPadding</c>'s decision, because
        /// nothing in ISO 32000-1 requires the padding to be zeros and refusing would reject other
        /// producers' valid documents. False when there was no tail and when the content could not
        /// be read as ASN.1 — in that case nothing was discarded, so there is nothing to judge.
        /// </remarks>
        public bool HasNonZeroContentsPadding { get; set; }

        /// <summary>True when this signature is the document's DocMDP certification signature.</summary>
        public bool IsCertification { get; set; }

        /// <summary>For a certification signature, the certified permission level P (1/2/3); else 0.</summary>
        public int CertificationPermission { get; set; }

        /// <summary>
        /// DocMDP policy violations: incremental changes made after this certification signature
        /// that exceed its permission level. Non-empty ⇒ the certified document was modified in a
        /// disallowed way (each entry is also added to <see cref="ValidationErrors"/>, so
        /// <see cref="IsValid"/> becomes false).
        /// </summary>
        public List<string> MdpViolations { get; } = new List<string>();

        /// <summary>
        /// Cryptographic integrity passed and no structural errors were recorded
        /// (valid /ByteRange bounds, supported sub-filter, signature bytes match
        /// content). This is <b>not</b> a trust decision: the signer certificate
        /// comes from the (attacker-controlled) CMS and — unless trust anchors were supplied —
        /// its chain is not validated. Use <see cref="IsFullyTrusted"/> for the complete verdict.
        /// </summary>
        public bool IsValid => IsIntegrityValid && ValidationErrors.Count == 0;

        /// <summary>
        /// The complete verdict: the signature is cryptographically intact, structurally sound,
        /// its certificate chains to a caller-supplied trust anchor, and no embedded revocation
        /// data reports it revoked.
        /// <para>
        /// One property, because the correct check is a three-part conjunction across two result
        /// types from two classes, which callers assembling it themselves silently get wrong by
        /// testing <c>IsValid</c> alone.
        /// </para>
        /// </summary>
        public bool IsFullyTrusted =>
            IsValid
            && ChainStatus == PdfSignatureChainStatus.Trusted
            && RevocationStatus != PdfRevocationStatus.Revoked;
    }
}
