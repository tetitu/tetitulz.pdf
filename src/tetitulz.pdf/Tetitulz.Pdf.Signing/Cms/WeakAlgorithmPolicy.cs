// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// The weak-algorithm / key-size rules a verified CMS blob must clear, resolved from
    /// <see cref="SignatureVerificationOptions"/> once and then applied wherever a signature is
    /// examined.
    /// <para>
    /// <b>Why it is its own type.</b> An RFC 3161 TimeStampToken is a CMS blob too — it has a
    /// SignerInfo with its own digest and signature algorithms, and a TSA certificate with its own
    /// key — so the rules apply to it as to the document's CMS signatures. Private to
    /// <see cref="CmsSignatureVerifier"/>, they would leave a SHA-1 <c>/DocTimeStamp</c> verifying as
    /// valid <i>and</i> setting the certificate-validity assessment clock, which is the one thing a
    /// timestamp is for. <see cref="Timestamping.TsaTokenVerifier"/> applies the identical rules.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Deliberately a leaf: it holds <b>no static fields</b> (only <c>const</c>s, which the compiler
    /// inlines and which therefore emit no type initializer), so calling into it from either of its
    /// two callers cannot create a type-initialization edge. See the "share downward into a leaf"
    /// rule in CLAUDE.md.
    /// </remarks>
    internal readonly struct WeakAlgorithmPolicy
    {
        // ---- Signature-algorithm (SignerInfo.signatureAlgorithm) identifiers examined below. ----
        //
        // Read through SignerInformation.SignatureAlgorithm.Algorithm.Id. The obsolete spelling
        // EncryptionAlgOid returned the same field — verified by IL in BouncyCastle 2.6.2 and
        // 2.7.0, where both getters load `encryptionAlgorithm` — so the migration was a rename and
        // not a change of meaning. The switch below has NO default arm and falls through to "not
        // weak", which is why that had to be established rather than assumed: a replacement that
        // normalised the spelling would silently stop flagging these.
        // These name a signature scheme, not a digest, so they are not in PdfDigestOids.

        /// <summary>md5WithRSAEncryption (PKCS#1 v1.5).</summary>
        private const string Md5WithRsaOid = "1.2.840.113549.1.1.4";
        /// <summary>md2WithRSAEncryption (PKCS#1 v1.5).</summary>
        private const string Md2WithRsaOid = "1.2.840.113549.1.1.2";
        /// <summary>sha1WithRSAEncryption (PKCS#1 v1.5).</summary>
        private const string Sha1WithRsaOid = "1.2.840.113549.1.1.5";
        /// <summary>sha1WithRSASignature (the legacy OIW spelling).</summary>
        private const string Sha1WithRsaOiwOid = "1.3.14.3.2.29";
        /// <summary>dsaWithSha1.</summary>
        private const string DsaWithSha1Oid = "1.2.840.10040.4.3";
        /// <summary>ecdsaWithSha1.</summary>
        private const string EcdsaWithSha1Oid = "1.2.840.10045.4.1";
        /// <summary>
        /// id-RSASSA-PSS — its digest lives in the algorithm PARAMETERS, not the OID.
        /// Aliased from <see cref="SignatureAlgorithmOids.RsaPss"/> (the signing side's table) so
        /// the verifier cannot end up matching a different OID than the generator emits.
        /// </summary>
        private const string RsassaPssOid = SignatureAlgorithmOids.RsaPss;

        private WeakAlgorithmPolicy(bool rejectWeak, int minRsaKeyBits, int minEcKeyBits)
        {
            RejectWeak = rejectWeak;
            MinRsaKeyBits = minRsaKeyBits;
            MinEcKeyBits = minEcKeyBits;
        }

        /// <summary>
        /// When true, a weak-but-not-fully-broken finding clears the verdict instead of merely
        /// being reported.
        /// </summary>
        public bool RejectWeak { get; }

        /// <summary>RSA/DSA modulus floor in bits; 0 disables the check.</summary>
        public int MinRsaKeyBits { get; }

        /// <summary>EC field-size floor in bits; 0 disables the check.</summary>
        public int MinEcKeyBits { get; }

        /// <summary>
        /// Resolves the policy for one call. Explicit <paramref name="options"/> win outright; with
        /// none, the legacy process-wide
        /// <see cref="CmsSignatureVerifier.RejectWeakAlgorithms"/> static governs rejection — the
        /// contract that static's own documentation states — and the key floors come from
        /// <see cref="SignatureVerificationOptions.Default"/>.
        /// </summary>
        /// <remarks>
        /// Both branches default to <b>rejecting</b>, so a caller that states no policy fails
        /// closed rather than silently accepting SHA-1.
        /// </remarks>
        internal static WeakAlgorithmPolicy Resolve(SignatureVerificationOptions? options)
            => new WeakAlgorithmPolicy(
                options?.RejectWeakAlgorithms ?? CmsSignatureVerifier.RejectWeakAlgorithms,
                options?.MinimumRsaKeyBits ?? SignatureVerificationOptions.Default.MinimumRsaKeyBits,
                options?.MinimumEcKeyBits ?? SignatureVerificationOptions.Default.MinimumEcKeyBits);

        /// <summary>
        /// Returns a reason when <paramref name="digestOid"/> is a structurally BROKEN digest
        /// (MD5/MD2), which is refused regardless of <see cref="RejectWeak"/>: a "valid" MD5/MD2
        /// signature is forgeable via chosen-prefix collisions. Null otherwise.
        /// </summary>
        internal static string? DetectBrokenDigest(string digestOid)
            => digestOid == PdfDigestOids.Md5 || digestOid == PdfDigestOids.Md2
                ? $"Weak/broken digest algorithm ({digestOid})"
                : null;

        /// <summary>
        /// The reason a digest is weak but not broken — SHA-1 — or null. One answer for the
        /// verifier reading a SignerInfo (<see cref="DetectWeakAlgorithm"/>) and the signer told
        /// which digest to sign with, so what signing warns of is what verification refuses.
        /// </summary>
        internal static string? DetectWeakDigest(string digestOid)
            => digestOid == PdfDigestOids.Sha1 ? "digest algorithm is SHA-1 (collision-broken)." : null;

        /// <summary>
        /// Returns a human-readable reason when the SignerInfo's digest OID is SHA-1, or its
        /// signature-algorithm OID names a hash of MD5/MD2/SHA-1; null when both are strong.
        /// MD5/MD2 <i>digest</i> OIDs are handled by <see cref="DetectBrokenDigest"/> (hard reject)
        /// and are not duplicated here.
        /// </summary>
        internal static string? DetectWeakAlgorithm(SignerInformation signer)
        {
            if (DetectWeakDigest(signer.DigestAlgorithmID.Algorithm.Id) is { } weakDigest)
                return weakDigest;

            switch (signer.SignatureAlgorithm.Algorithm.Id)
            {
                // RSA PKCS#1 v1.5
                case Md5WithRsaOid:     return "signature algorithm is md5WithRSAEncryption.";
                case Md2WithRsaOid:     return "signature algorithm is md2WithRSAEncryption.";
                case Sha1WithRsaOid:    return "signature algorithm is sha1WithRSAEncryption.";
                case Sha1WithRsaOiwOid: return "signature algorithm is sha1WithRSASignature (OIW).";
                // ECDSA / DSA with SHA-1
                case DsaWithSha1Oid:    return "signature algorithm is dsaWithSha1.";
                case EcdsaWithSha1Oid:  return "signature algorithm is ecdsaWithSha1.";
                // RSASSA-PSS carries its hash in the algorithm PARAMETERS, not the OID, so the
                // switch above cannot see it — a PSS signature over SHA-1 would go unflagged.
                case RsassaPssOid:      return DetectWeakPssHash(signer);
            }
            return null;
        }

        // RFC 4055: RSASSA-PSS-params names the digest explicitly; when the parameters are absent
        // the default is SHA-1, which is exactly the case we must flag.
        private static string? DetectWeakPssHash(SignerInformation signer)
        {
            try
            {
                var algId = signer.SignatureAlgorithm;
                if (algId?.Parameters == null)
                    return "signature algorithm is RSASSA-PSS with default parameters (SHA-1).";

                var pss = RsassaPssParameters.GetInstance(algId.Parameters);
                string? hashOid = pss?.HashAlgorithm?.Algorithm?.Id;
                if (hashOid == null || hashOid == PdfDigestOids.Sha1)
                    return "signature algorithm is RSASSA-PSS over SHA-1 (collision-broken).";
                if (hashOid == PdfDigestOids.Md5 || hashOid == PdfDigestOids.Md2)
                    return "signature algorithm is RSASSA-PSS over MD5/MD2 (broken).";
                return null;
            }
            catch (Exception ex)
            {
                return "RSASSA-PSS parameters are unparseable (" + ex.Message + ").";
            }
        }

        /// <summary>
        /// Why an RFC 3161 timestamp token is weak under this policy — its own SignerInfo, then its
        /// message imprint, then the TSA certificate's key — or <see langword="null"/>. The verifier
        /// refuses such a token when <see cref="RejectWeak"/> is set; the TSA client asks the same
        /// question under the default policy and reports the answer at signing time, so a token
        /// verification will refuse is said before the document is written, not discovered after.
        /// </summary>
        /// <param name="tokenSigner">The token's SignerInfo, or null when it cannot be read.</param>
        /// <param name="imprintOid">The <c>TSTInfo</c>'s message-imprint digest algorithm.</param>
        /// <param name="tsaCert">The certificate that signed the token.</param>
        internal string? DetectWeakTimestamp(
            SignerInformation? tokenSigner, string imprintOid, X509Certificate tsaCert)
        {
            string? weak = tokenSigner == null ? null : DetectWeakAlgorithm(tokenSigner);
            // The message imprint is a separate field from the SignerInfo digest and a token may be
            // SHA-256-signed over a SHA-1 imprint, so it gets its own test rather than riding on the
            // one above.
            if (weak == null && imprintOid == PdfDigestOids.Sha1)
                weak = "TimeStampToken message imprint is SHA-1 (collision-broken).";
            return weak ?? DetectWeakKey(tsaCert, "TSA");
        }

        /// <summary>
        /// Key-size floor for a certificate's public key, applied with this policy's limits.
        /// <paramref name="role"/> names the certificate in the message ("signer", "TSA"), because
        /// the same check now runs over two different certificates and a caller reading the reason
        /// has to know which one failed.
        /// </summary>
        internal string? DetectWeakKey(X509Certificate cert, string role)
            => DetectWeakKey(cert, MinRsaKeyBits, MinEcKeyBits, role);

        // Derived from the SubjectPublicKeyInfo, which is why it cannot live in the OID switch above.
        internal static string? DetectWeakKey(
            X509Certificate cert, int minRsaBits, int minEcBits, string role = "signer")
        {
            try
            {
                var key = cert.GetPublicKey();
                switch (key)
                {
                    case RsaKeyParameters rsa when minRsaBits > 0:
                    {
                        int bits = rsa.Modulus.BitLength;
                        return bits < minRsaBits
                            ? $"{role} RSA key is {bits} bits (minimum {minRsaBits})."
                            : null;
                    }
                    case DsaPublicKeyParameters dsa when minRsaBits > 0:
                    {
                        int bits = dsa.Parameters?.P?.BitLength ?? 0;
                        return bits > 0 && bits < minRsaBits
                            ? $"{role} DSA key is {bits} bits (minimum {minRsaBits})."
                            : null;
                    }
                    case ECPublicKeyParameters ec when minEcBits > 0:
                    {
                        int bits = ec.Parameters?.Curve?.FieldSize ?? 0;
                        return bits > 0 && bits < minEcBits
                            ? $"{role} EC key is on a {bits}-bit curve (minimum {minEcBits})."
                            : null;
                    }
                    default:
                        return null;
                }
            }
            catch
            {
                return null; // an unreadable key is caught by signature verification itself
            }
        }
    }
}
