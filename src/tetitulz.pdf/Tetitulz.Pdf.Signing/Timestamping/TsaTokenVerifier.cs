// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Timestamping
{
    /// <summary>
    /// Verifies an RFC 3161 TimeStampToken embedded in a PDF /DocTimeStamp signature.
    /// </summary>
    internal static class TsaTokenVerifier
    {
        internal sealed class TsaVerificationResult
        {
            /// <summary>
            /// The token is internally consistent: its message imprint matches the data, its TSA
            /// certificate carries the timeStamping EKU, and its CMS signature verifies against
            /// that certificate.
            /// <para>
            /// <b>This is integrity, not trust.</b> The TSA certificate comes from inside the
            /// token, so it is as attacker-controlled as the signer certificate in a CMS blob.
            /// Anyone can mint a self-signed certificate with the timeStamping EKU and assert any
            /// <c>GenTime</c>. Consult <see cref="IsTrusted"/> before letting
            /// <see cref="TokenTime"/> influence a validity decision.
            /// </para>
            /// </summary>
            public bool IsValid { get; set; }

            /// <summary>
            /// Whether the token's own CMS signature verified against the TSA certificate and its
            /// message imprint matched — the <b>cryptographic</b> question alone, with no
            /// weak-algorithm policy applied.
            /// </summary>
            /// <remarks>
            /// Distinct from <see cref="IsValid"/>, which a policy refusal clears. The pair exists
            /// for the same reason as <c>CmsVerificationResult.IsCryptographicallyValid</c>: a
            /// caller must be able to tell "this token was forged or covers different bytes" from
            /// "this token is genuine and uses an algorithm we no longer accept", and internally
            /// <c>PdfSignatureVerifier</c>'s shadow-attack check asks which signatures actually
            /// verified before letting them vouch for byte coverage — feeding it the policy verdict
            /// would silently disable that check for every SHA-1 timestamp.
            /// </remarks>
            public bool IsCryptographicallyValid { get; set; }

            /// <summary>
            /// True when the token's SignerInfo names a weak-but-not-fully-broken algorithm (SHA-1
            /// digest, a signature OID whose hash is MD5/MD2/SHA-1, RSASSA-PSS over SHA-1), its
            /// message imprint uses SHA-1, or the TSA key is below the configured size floor.
            /// Clears <see cref="IsValid"/> only when
            /// <see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/> is set.
            /// </summary>
            public bool WeakAlgorithm { get; set; }

            /// <summary>Human-readable reason for <see cref="WeakAlgorithm"/>; null when not weak.</summary>
            public string? WeakAlgorithmReason { get; set; }

            /// <summary>
            /// True when the TSA certificate chains to a caller-supplied trust anchor. False when
            /// no anchors were supplied or the chain did not terminate in one.
            /// </summary>
            public bool IsTrusted { get; set; }

            /// <summary>Chain status of the TSA certificate against the supplied anchors.</summary>
            public PdfSignatureChainStatus ChainStatus { get; set; } = PdfSignatureChainStatus.Unknown;

            /// <summary>
            /// True when the TSA certificate also chains to a trust anchor <b>at the current
            /// time</b>. <see cref="IsTrusted"/> is judged at the token's own time, which the token
            /// itself asserts; a caller that lets the token move a validity assessment asks this
            /// too, or finds a later timestamp over the token (see
            /// <see cref="SignatureVerificationOptions.RequireCurrentTimestampAuthority"/>).
            /// </summary>
            public bool IsAuthorityCurrent { get; set; }

            /// <summary>The TSA certificate the token verified against; null when verification stopped before one was found.</summary>
            public X509Certificate? TsaCertificate { get; set; }

            /// <summary>The token's own certificate set, for building the TSA's chain.</summary>
            public Org.BouncyCastle.Utilities.Collections.IStore<X509Certificate>? Certificates { get; set; }

            /// <summary>The path the TSA certificate was anchored through, leaf first; empty when it was not trusted.</summary>
            public List<X509Certificate> TrustedPath { get; set; } = new List<X509Certificate>();

            public string? TsaName { get; set; }
            public string? DigestAlgorithm { get; set; }
            public DateTime? TokenTime { get; set; }
            public string? Error { get; set; }
        }

        /// <summary>
        /// Verifies that the TimeStampToken in <paramref name="tokenBytes"/> covers
        /// the hash of <paramref name="signedData"/>, and — when
        /// <paramref name="trustAnchors"/> is supplied — whether the TSA certificate chains to
        /// one of them.
        /// </summary>
        /// <param name="tokenBytes">DER-encoded RFC 3161 TimeStampToken.</param>
        /// <param name="signedData">Data the token's message imprint must cover.</param>
        /// <param name="trustAnchors">
        /// Anchors the TSA certificate must chain to for <see cref="TsaVerificationResult.IsTrusted"/>
        /// to be set. Pass null for an integrity-only check.
        /// </param>
        /// <param name="warnings">Optional diagnostics sink for chain-walk detail.</param>
        /// <param name="options">
        /// Per-call weak-algorithm / key-size policy, applied to the token's own SignerInfo, its
        /// message imprint and the TSA certificate. <c>null</c> means "the caller stated no
        /// policy", in which case the legacy process-wide
        /// <see cref="Cms.CmsSignatureVerifier.RejectWeakAlgorithms"/> static governs — the same
        /// precedence the CMS path uses, and one that fails closed either way.
        /// <para>
        /// <b>Why this parameter exists.</b> The policy applies to timestamps as to document CMS
        /// signatures; without it a SHA-1 or MD5 <c>/DocTimeStamp</c> would verify as valid and move
        /// the certificate-validity assessment clock — the one thing an attacker wants from a
        /// forgeable timestamp.
        /// </para>
        /// </param>
        internal static TsaVerificationResult VerifyTimestampToken(
            byte[] tokenBytes,
            byte[] signedData,
            IEnumerable<X509Certificate>? trustAnchors = null,
            List<string>? warnings = null,
            SignatureVerificationOptions? options = null)
        {
            var result = new TsaVerificationResult();
            var policy = WeakAlgorithmPolicy.Resolve(options);

            try
            {
                // The CmsSignedData is kept rather than being built inline: the token's own
                // SignerInfo lives in it, and that is what the weak-algorithm policy below has to
                // read. TimeStampToken exposes the TSTInfo but not the SignerInfo that signed it.
                var cms = new CmsSignedData(tokenBytes);
                var tst = new TimeStampToken(cms);

                var tstInfo = tst.TimeStampInfo;
                result.TokenTime = tstInfo.GenTime.ToUniversalTime();

                // Determine hash algorithm from the token.
                string algOid = tstInfo.HashAlgorithm.Algorithm.Id;
                // PdfDigestOids.ToName is the single OID -> name table; it returns null for an
                // OID the library does not know, and this is a display-only field, so an
                // unrecognised OID keeps being reported verbatim as it always was.
                result.DigestAlgorithm = PdfDigestOids.ToName(algOid) ?? algOid;

                // A structurally-broken message imprint is refused regardless of policy, exactly as
                // a broken CMS digest is: the imprint is the ONLY thing binding this token to the
                // bytes, so an MD5 collision moves the token to a document the TSA never saw.
                // DigestAlgorithms.CreateFromOid would throw for these anyway, but as "unsupported
                // digest OID" — a wording that reads like a gap in this library rather than a
                // refusal, which is the difference a caller acts on.
                string? brokenImprint = WeakAlgorithmPolicy.DetectBrokenDigest(algOid);
                if (brokenImprint != null)
                {
                    result.Error = brokenImprint + " on the TimeStampToken message imprint; token rejected.";
                    return result;
                }

                // Compute the hash of the signed byte ranges.
                byte[] expectedHash;
                using (var ha = DigestAlgorithms.CreateFromOid(algOid))
                    expectedHash = ha.ComputeHash(signedData);

                // Compare with the hash stored in the TSTInfo.
                byte[] tokenHash = tstInfo.GetMessageImprintDigest();
                if (tokenHash == null || tokenHash.Length != expectedHash.Length)
                {
                    result.Error = "TimeStampToken hash length mismatch.";
                    return result;
                }
                for (int i = 0; i < expectedHash.Length; i++)
                {
                    if (expectedHash[i] != tokenHash[i])
                    {
                        result.Error = "TimeStampToken hash does not match document content.";
                        return result;
                    }
                }

                // Locate the TSA signer certificate.
                var store = tst.GetCertificates();
                X509Certificate? tsaCert = null;
                foreach (var cert in store.EnumerateMatches(tst.SignerID)) { tsaCert = cert; break; }
                if (tsaCert == null)
                {
                    result.Error = "TimeStampToken signer certificate not found.";
                    return result;
                }

                // RFC 3161 §2.3: the TSA certificate MUST carry the
                // id-kp-timeStamping EKU (1.3.6.1.5.5.7.3.8). Without checking
                // this, any certificate could masquerade as a timestamp authority.
                bool hasTsEku = false;
                var ekuList = tsaCert.GetExtendedKeyUsage();
                if (ekuList != null)
                    foreach (var oid in ekuList)
                        if (oid?.ToString() == PdfEkuOids.TimeStamping) { hasTsEku = true; break; }
                if (!hasTsEku)
                {
                    result.Error = "TimeStampToken signer lacks the id-kp-timeStamping EKU.";
                    return result;
                }

                // RFC 3161 §2.4.2: verify the CMS signature on the token itself
                // against the signer certificate (TimeStampToken.Validate does NOT
                // run if we only read TimeStampInfo). Throws on a forged/invalid
                // token; caught below. The key is the token's, so its cost is asked first.
                PublicKeyCost.KeyOf(tsaCert);
                tst.Validate(tsaCert);

                result.TsaName = tsaCert.SubjectDN.ToString();
                result.IsCryptographicallyValid = true;
                result.IsValid = true;

                // ---- Weak-algorithm / key-size policy. ----
                // A TimeStampToken is a CMS blob with its own SignerInfo, its own message imprint and
                // its own TSA certificate, each measured against the policy the document's CMS
                // signatures are. Unmeasured, a SHA-1 (or MD5-signed) /DocTimeStamp would report
                // IsValid and — through DeriveTrustedAssessmentTime and ResolveDocumentTimestamps — set
                // the clock at which certificate validity is judged, which is precisely what a
                // forgeable timestamp buys an attacker.
                var tokenSigner = cms.GetSignerInfos().GetFirstSigner(tst.SignerID);

                string? brokenSignature =
                    tokenSigner == null ? null : WeakAlgorithmPolicy.DetectBrokenDigest(tokenSigner.DigestAlgorithmID.Algorithm.Id);
                if (brokenSignature != null)
                {
                    // IsCryptographicallyValid stays true: the bytes really did verify, and only
                    // the algorithm is refused. Same split as the CMS path's MD5/MD2 hard reject.
                    result.IsValid = false;
                    result.Error = brokenSignature + " on the TimeStampToken signature; token rejected.";
                    return result;
                }

                // The one rule the TSA client also asks at signing time.
                string? weak = policy.DetectWeakTimestamp(tokenSigner, algOid, tsaCert);

                if (weak != null)
                {
                    result.WeakAlgorithm = true;
                    result.WeakAlgorithmReason = weak;
                    if (policy.RejectWeak)
                    {
                        result.IsValid = false;
                        result.Error = "Weak timestamp algorithm rejected by policy: " + weak;
                        // Returned BEFORE trust anchoring on purpose. A refused token must not come
                        // back carrying IsTrusted / ChainStatus.Trusted, because those are exactly
                        // what the callers consult before letting a token vouch for a time.
                        return result;
                    }
                }

                // Trust anchoring. Everything above proves only that the token is internally
                // consistent — the TSA certificate travels inside the token, so an attacker can
                // supply their own self-signed one bearing the timeStamping EKU and claim any
                // GenTime. Without this step a signer whose certificate has EXPIRED could embed
                // such a token to back-date itself into its old validity window, defeating the
                // "expired certificate fails closed" property the validator promises.
                var chainWarnings = warnings ?? new List<string>();
                result.ChainStatus = CertificateChainValidator.Resolve(
                    tsaCert, store, trustAnchors,
                    // Assess the TSA chain at the token's own GenTime: that is the instant the TSA
                    // asserts it acted, and a long-lived archival timestamp is expected to outlive
                    // its TSA certificate. Falls back to now when GenTime is unreadable.
                    result.TokenTime ?? DateTime.UtcNow,
                    chainWarnings, out List<X509Certificate> trustedPath, leafRole: "TSA");
                result.TrustedPath = trustedPath;
                result.IsTrusted = result.ChainStatus == PdfSignatureChainStatus.Trusted;
                result.TsaCertificate = tsaCert;
                result.Certificates = store;

                // The same chain at the current time. GenTime is the token's own claim, so a chain
                // judged there alone is judged where the token says to look; whether the authority
                // can still be relied on is a question about now. Its warnings are kept out of the
                // caller's: an expired TSA certificate is the ordinary state of an old timestamp,
                // and the caller says what follows from it.
                if (result.IsTrusted)
                    result.IsAuthorityCurrent = CertificateChainValidator.Resolve(
                        tsaCert, store, trustAnchors, DateTime.UtcNow, new List<string>(), leafRole: "TSA")
                        == PdfSignatureChainStatus.Trusted;
            }
            catch (Exception ex)
            {
                result.Error = $"TimeStampToken verification failed: {ex.Message}";
            }

            return result;
        }
    }
}
