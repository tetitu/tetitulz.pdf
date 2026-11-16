// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Cms
{
    internal static class CmsSignatureVerifier
    {
        /// <summary>
        /// An Ed448 SignerInfo checked as ISO/TS 32002 and RFC 8419 specify it, which BouncyCastle 2.7.0
        /// cannot: it does not resolve the SHAKE256 digest the signer names. With signed attributes the
        /// digest algorithm is the fixed-length <c>id-shake256</c> with no parameters (ISO/TS 32002
        /// Table 4; ISO/TS 32001 forbids any other SHAKE256 identifier in a PDF, RFC 8419 §3.1's
        /// <c>id-shake256-len</c> included), the message-digest attribute
        /// must be SHAKE256 of <paramref name="content"/> at 512 bits and the content type
        /// <c>id-data</c>, and the signature verifies over the DER of the signed attributes; without
        /// them (§3.2) the digest algorithm is <c>id-shake256</c> and the signature verifies over the
        /// content itself. Pure Ed448, empty context, either way.
        /// </summary>
        internal static bool VerifyEd448(SignerInformation signer, X509Certificate certificate, byte[] content)
        {
            var verifier = new Org.BouncyCastle.Crypto.Signers.Ed448Signer(Array.Empty<byte>());
            verifier.Init(false, certificate.GetPublicKey());
            AttributeTable? attributes = signer.SignedAttributes;
            string digestOid = signer.DigestAlgorithmID.Algorithm.Id;
            if (attributes == null)
            {
                if (digestOid != PdfDigestOids.Shake256)
                    throw new CmsException("an Ed448 signer without signed attributes names a digest other than id-shake256 (RFC 8419 §3.2).");
                verifier.BlockUpdate(content, 0, content.Length);
                return verifier.VerifySignature(signer.GetSignature());
            }

            if (digestOid != PdfDigestOids.Shake256 || signer.DigestAlgorithmID.Parameters != null)
                throw new CmsException("an Ed448 signer names a digest other than the fixed-length id-shake256, the one ISO/TS 32002 and 32001 allow a PDF signature.");

            var shake = new Org.BouncyCastle.Crypto.Digests.ShakeDigest(256);
            shake.BlockUpdate(content, 0, content.Length);
            var digest = new byte[64];
            shake.OutputFinal(digest, 0, digest.Length);

            var messageDigest = attributes[CmsAttributes.MessageDigest];
            var contentType = attributes[CmsAttributes.ContentType];
            if (messageDigest == null || messageDigest.AttrValues.Count != 1
                || !(messageDigest.AttrValues[0] is Org.BouncyCastle.Asn1.Asn1OctetString claimed))
                throw new CmsException("an Ed448 signer's signed attributes carry no message digest.");
            if (contentType == null || !CmsObjectIdentifiers.Data.Equals(contentType.AttrValues[0]))
                throw new CmsException("an Ed448 signer's content type is not id-data.");
            if (!Org.BouncyCastle.Utilities.Arrays.FixedTimeEquals(claimed.GetOctets(), digest)) return false;

            byte[] signed = signer.GetEncodedSignedAttributes();
            verifier.BlockUpdate(signed, 0, signed.Length);
            return verifier.VerifySignature(signer.GetSignature());
        }

        // ---- Digest OIDs an ESS signing-certificate attribute may name that the rest of the
        // library never uses, so they are not in PdfDigestOids. ----

        /// <summary>SHA-224 (<c>2.16.840.1.101.3.4.2.4</c>).</summary>
        private const string Sha224Oid = "2.16.840.1.101.3.4.2.4";
        /// <summary>SHA3-224 (<c>2.16.840.1.101.3.4.2.7</c>).</summary>
        private const string Sha3_224Oid = "2.16.840.1.101.3.4.2.7";

        /// <summary>
        /// Verifies the cryptographic <b>integrity</b> of a PKCS#7/CMS detached
        /// signature: that <paramref name="signedData"/> was signed by the private
        /// key matching the certificate embedded in the CMS blob.
        /// <para>
        /// IMPORTANT: this does NOT establish chain-of-trust. The signer
        /// certificate is taken from the (attacker-controlled) CMS, so a tampered
        /// document re-signed with an arbitrary self-signed certificate will still
        /// report <see cref="CmsVerificationResult.IsValid"/> = true here. To
        /// determine whether the signer is <i>trusted</i>, use
        /// <see cref="PdfSignatureValidator"/> with an explicit trust-anchor set,
        /// which walks the X.509 chain and reports a
        /// <c>PdfSignatureChainStatus</c>. "Integrity valid" and "trusted" are
        /// distinct properties; never treat the former as the latter.
        /// </para>
        /// </summary>
        /// <param name="signatureBytes">The DER-encoded PKCS#7 signature.</param>
        /// <param name="signedData">The original signed data (byte range content).</param>
        /// <param name="options">
        /// Per-call verification policy. When <c>null</c>, the legacy process-wide
        /// <see cref="RejectWeakAlgorithms"/> static is consulted instead, so existing callers
        /// keep their behaviour.
        /// </param>
        /// <returns>Verification result with signer info (integrity only).</returns>
        public static CmsVerificationResult VerifyDetachedSignature(
            byte[] signatureBytes, byte[] signedData, SignatureVerificationOptions? options = null)
        {
            var result = new CmsVerificationResult();

            // Policy resolution: explicit options win outright; with no options we fall back to
            // the legacy process-wide static so pre-options callers behave exactly as before.
            // WeakAlgorithmPolicy owns this so the timestamp path applies the identical rules —
            // it never did, and a SHA-1 /DocTimeStamp could set the assessment clock.
            var policy = WeakAlgorithmPolicy.Resolve(options);

            try
            {
                var content = new CmsProcessableByteArray(signedData);
                var cms = new CmsSignedData(content, signatureBytes);
                var signers = cms.GetSignerInfos();
                var certStore = cms.GetCertificates();

                // PDF /Contents signatures are single-signer by spec (ISO 32000 §12.8). A CMS
                // carrying more than one SignerInfo is non-standard, and only the first is
                // examined below — so an attacker who prepends a decoy SignerInfo would otherwise
                // have the reported signer identity silently swapped without any signal. Record
                // the anomaly so callers can refuse it. (Chain validation independently rejects a
                // decoy whose certificate is not under any trust anchor, but the identity reported
                // to the user would still have been the decoy's.)
                int signerCount = signers.Count;

                foreach (SignerInformation signer in signers.GetSigners())
                {
                    var certCollection = certStore.EnumerateMatches(signer.SignerID);
                    foreach (X509Certificate cert in certCollection)
                    {
                        result.SignerCertificate = cert;
                        result.SignerCommonName = ExtractCommonName(cert);
                        result.Certificates = certStore;
                        result.Signer = signer;

                        try
                        {
                            // The cryptographic verdict, recorded once and never touched by
                            // policy — everything below may clear IsValid, and a caller must
                            // still be able to tell tampering from refusal. The key is the
                            // bag's, so its cost is asked first.
                            PublicKeyCost.KeyOf(cert);
                            result.IsCryptographicallyValid = signer.SignatureAlgorithm.Algorithm.Id == SignatureAlgorithmOids.Ed448
                                ? VerifyEd448(signer, cert, signedData)
                                : signer.Verify(cert);
                            result.IsValid = result.IsCryptographicallyValid;
                        }
                        catch (Exception ex)
                        {
                            result.IsValid = false;
                            result.Error = $"Signature verification failed: {ex.Message}";
                        }

                        // Get digest + signature (encryption) algorithm OIDs.
                        result.DigestAlgorithm = signer.DigestAlgorithmID.Algorithm.Id;
                        result.SignatureAlgorithm = signer.SignatureAlgorithm.Algorithm.Id;

                        // Reject structurally-broken digest algorithms. A "valid" MD5/MD2
                        // signature is forgeable via chosen-prefix collisions, so accepting
                        // one as integrity-valid is unsafe.
                        string? broken = WeakAlgorithmPolicy.DetectBrokenDigest(signer.DigestAlgorithmID.Algorithm.Id);
                        if (result.IsValid && broken != null)
                        {
                            result.IsValid = false;
                            result.Error = broken + "; signature rejected.";
                            return result;
                        }

                        result.SignerCount = signerCount;

                        // Flag (but do not by itself reject) weak-but-not-fully-broken algorithms:
                        // a SHA-1 digest, or a signature-algorithm OID whose hash component is
                        // MD5/MD2/SHA-1 (e.g. sha1WithRSAEncryption). SHA-1 is collision-broken;
                        // it is surfaced via WeakAlgorithm so caller policy can refuse it. Existing
                        // strong (SHA-256+) paths are unaffected. Callers wanting a hard failure can
                        // opt in via RejectWeakAlgorithms / SignatureVerificationOptions.
                        string? weak = WeakAlgorithmPolicy.DetectWeakAlgorithm(signer)
                                   ?? policy.DetectWeakKey(cert, "signer");
                        if (weak != null)
                        {
                            result.WeakAlgorithm = true;
                            result.WeakAlgorithmReason = weak;
                            if (result.IsValid && policy.RejectWeak)
                            {
                                // The bytes DID verify; only policy refuses them. Keep the two
                                // answers apart — see IsCryptographicallyValid.
                                result.IsCryptographicallyValid = true;
                                result.IsValid = false;
                                result.Error = "Weak signature algorithm rejected by policy: " + weak;
                                return result;
                            }
                        }

                        // Signer-binding: if the CMS carries an ESS signing-certificate
                        // attribute, the hash it names MUST match the cert we verified with,
                        // otherwise an attacker could present a different cert from the bag as
                        // the signer identity. Only fails on a present-but-mismatched attribute.
                        if (result.IsValid)
                        {
                            string? essError = VerifyEssBinding(signer, cert);
                            if (essError != null)
                            {
                                result.IsValid = false;
                                result.Error = essError;
                            }
                        }

                        // Only the first matching signer is examined; see the signerCount note above.
                        return result;
                    }
                }

                result.IsValid = false;
                result.Error = "No matching certificate found for signer.";
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Error = $"Failed to parse CMS signature: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// Verifies the ESS signing-certificate signed attribute (signingCertificateV2, or
        /// the SHA-1 v1 form) binds to <paramref name="cert"/>. Returns null when the binding
        /// holds or the attribute is absent; returns an error string when a present attribute
        /// matches no listed cert — or cannot be parsed at all.
        /// <para>
        /// Failing closed on an unparseable attribute is deliberate. Catching every exception and
        /// returning null ("binding OK") would let an attacker defeat the whole substitution check
        /// by making the attribute malformed rather than merely wrong.
        /// A legitimate signer emits a well-formed ESS attribute or none at all.
        /// </para>
        /// </summary>
        private static string? VerifyEssBinding(SignerInformation signer, X509Certificate cert)
        {
            AttributeTable attrs;
            try
            {
                attrs = signer.SignedAttributes;
            }
            catch (Exception ex)
            {
                return "ESS signing-certificate attribute could not be read: " + ex.Message;
            }
            if (attrs == null) return null;

            byte[] der;
            try { der = cert.GetEncoded(); }
            catch (Exception ex) { return "Signer certificate could not be re-encoded for ESS binding: " + ex.Message; }

            var v2 = attrs[PkcsObjectIdentifiers.IdAASigningCertificateV2];
            if (v2 != null && v2.AttrValues.Count > 0)
            {
                try
                {
                    var scv2 = SigningCertificateV2.GetInstance(v2.AttrValues[0]);
                    foreach (EssCertIDv2 id in scv2.GetCerts())
                    {
                        // EssCertIDv2 default hash is SHA-256 when no AlgorithmIdentifier is present.
                        string? algOid = id.HashAlgorithm?.Algorithm?.Id;
                        string? algName = algOid == null ? PdfDigestOids.Sha256Name : OidToDigestName(algOid);
                        if (algName == null)
                            return $"ESS signingCertificateV2 uses an unsupported hash algorithm ({algOid}).";
                        byte[] want = id.GetCertHash();
                        byte[] got = DigestUtilities.CalculateDigest(algName, der);
                        if (BytesEqual(want, got)) return null;
                    }
                }
                catch (Exception ex)
                {
                    return "ESS signingCertificateV2 attribute is present but unparseable: " + ex.Message;
                }
                return "ESS signingCertificateV2 attribute does not match the signer certificate (possible identity substitution).";
            }

            var v1 = attrs[PkcsObjectIdentifiers.IdAASigningCertificate];
            if (v1 != null && v1.AttrValues.Count > 0)
            {
                try
                {
                    var sc = SigningCertificate.GetInstance(v1.AttrValues[0]);
                    foreach (EssCertID id in sc.GetCerts())
                    {
                        byte[] want = id.GetCertHash();
                        byte[] got = DigestUtilities.CalculateDigest(PdfDigestOids.Sha1Name, der); // v1 is always SHA-1
                        if (BytesEqual(want, got)) return null;
                    }
                }
                catch (Exception ex)
                {
                    return "ESS signingCertificate (v1) attribute is present but unparseable: " + ex.Message;
                }
                return "ESS signingCertificate (v1) attribute does not match the signer certificate (possible identity substitution).";
            }

            return null; // no ESS attribute present
        }

        /// <summary>
        /// When <c>true</c> — <b>the default</b> — a signature whose digest or
        /// signature-algorithm hash component is SHA-1 (or MD5/MD2 in the signature OID) is
        /// reported as <c>IsValid = false</c> instead of merely flagged via
        /// <see cref="CmsVerificationResult.WeakAlgorithm"/>.
        /// <para>
        /// <b>Legacy.</b> This is a process-wide mutable static: it races across concurrent
        /// verifications, so in a server host one request can tighten — or relax — the policy
        /// another request is running under. Prefer
        /// <see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/>, which scopes the
        /// policy to a single call. This static is consulted only when no options object is passed.
        /// </para>
        /// <para>
        /// "No options object" includes the no-policy overloads of the document-level API:
        /// <c>PdfSignatureVerifier</c> passes the caller's original options reference down, so
        /// setting this hard-rejects SHA-1 through <c>VerifyAll</c>, <c>Verify</c> and the
        /// anchors-only <c>PdfSignatureValidator</c> overloads.
        /// </para>
        /// </summary>
        public static bool RejectWeakAlgorithms { get; set; } = true;

        /// <summary>
        /// Maps a digest OID named by an ESS signing-certificate attribute to the algorithm name
        /// used to re-hash the signer certificate, or <c>null</c> when the OID is not one this
        /// check accepts.
        /// <para>
        /// <b>Returning null matters.</b> The caller turns it into an explicit "unsupported hash
        /// algorithm" failure; a fallback would let a crafted attribute pick the digest under
        /// which the certificate-binding check is evaluated.
        /// </para>
        /// <para>
        /// The SHA-1/2/3 names come from <see cref="PdfDigestOids.ToName"/>, the single OID ->
        /// name table. The accepted set is deliberately narrower than that table: ToName also
        /// knows MD5, MD2 and Streebog, which this check does not admit. SHA-224 / SHA3-224
        /// are the other way round — legitimate in an ESS attribute but used nowhere else in the
        /// library, so they are not in PdfDigestOids and stay local.
        /// </para>
        /// </summary>
        private static string? OidToDigestName(string oid)
        {
            if (oid == Sha224Oid)   return "SHA-224";
            if (oid == Sha3_224Oid) return "SHA3-224";

            // The set the generator signs with (DigestAlgorithms.IsStandard): this list once lacked
            // the SHA-3 family, so the ESS hash fell back to SHA-256 and a signature this very
            // library produced reported a bogus "identity substitution".
            return DigestAlgorithms.IsStandard(oid) ? PdfDigestOids.ToName(oid) : null;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string? ExtractCommonName(X509Certificate cert)
        {
            var subject = cert.SubjectDN;
            var values = subject.GetValueList(Org.BouncyCastle.Asn1.X509.X509Name.CN);
            if (values != null && values.Count > 0)
                return values[0]?.ToString();
            return subject.ToString();
        }
    }

    internal sealed class CmsVerificationResult
    {
        public bool IsValid { get; set; }

        /// <summary>
        /// Whether the signature bytes verified against the content — the <b>cryptographic</b>
        /// question alone, with no policy applied.
        /// </summary>
        /// <remarks>
        /// <para>Distinct from <see cref="IsValid"/>, which is the combined verdict and is cleared
        /// by a policy refusal such as <see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/>.
        /// A SHA-1 signature over unmodified bytes is <c>IsCryptographicallyValid = true</c> and,
        /// under the default policy, <c>IsValid = false</c>.</para>
        /// <para><b>Why the two must not be collapsed.</b> A caller has to tell "this document was
        /// modified after signing" from "this signature uses an algorithm we no longer accept" —
        /// they warrant completely different responses. And internally,
        /// <c>PdfSignatureVerifier</c>'s shadow-attack check asks which signatures actually verified
        /// before letting them vouch for byte coverage; feeding it the policy verdict would silently
        /// disable that check for every SHA-1-signed document.</para>
        /// </remarks>
        public bool IsCryptographicallyValid { get; set; }

        public X509Certificate? SignerCertificate { get; set; }

        /// <summary>The CMS certificate bag, for chain building. Null when parsing failed.</summary>
        public Org.BouncyCastle.Utilities.Collections.IStore<X509Certificate>? Certificates { get; set; }

        /// <summary>
        /// The examined SignerInfo, exposed so callers can read its signed/unsigned attributes
        /// (claimed signing time, CAdES signature timestamp) without re-parsing the blob.
        /// </summary>
        public SignerInformation? Signer { get; set; }

        public string? SignerCommonName { get; set; }
        public string? DigestAlgorithm { get; set; }
        /// <summary>The CMS signature (encryption) algorithm OID, e.g. rsaEncryption / ecdsa.</summary>
        public string? SignatureAlgorithm { get; set; }
        /// <summary>
        /// True when the digest or signature algorithm is weak-but-not-fully-broken (SHA-1, a
        /// signature OID whose hash is MD5/MD2/SHA-1, RSASSA-PSS over SHA-1) or the signer key is
        /// below the configured size floor. Does NOT by itself clear <see cref="IsValid"/> unless
        /// <see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/> is set; surfaced so
        /// caller policy can decide. (MD5/MD2 *digest* OIDs are hard-rejected, clearing IsValid.)
        /// </summary>
        public bool WeakAlgorithm { get; set; }
        /// <summary>Human-readable reason for <see cref="WeakAlgorithm"/>; null when not weak.</summary>
        public string? WeakAlgorithmReason { get; set; }
        /// <summary>
        /// Number of SignerInfos in the CMS. PDF signatures are single-signer per ISO 32000
        /// §12.8; anything above 1 is non-standard and means the reported signer identity is
        /// whichever SignerInfo came first, so callers treat it as a structural error.
        /// </summary>
        public int SignerCount { get; set; }
        public string? Error { get; set; }
    }
}
