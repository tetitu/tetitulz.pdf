// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Digest-algorithm object identifiers used by CMS signing, timestamping and revocation.
    ///
    /// <para><see cref="ToName"/> and <see cref="FromName"/> are the single mapping, for the
    /// timestamp request, the counter-signature, CMS generation, timestamp verification and CMS
    /// verification alike: copies would drift, one knowing four OIDs where the others know seven and
    /// silently falling back to SHA-256 for anything it does not recognise.</para>
    /// </summary>
    public static class PdfDigestOids
    {
        /// <summary>SHA-1 (<c>1.3.14.3.2.26</c>). Considered weak; retained for verifying legacy signatures.</summary>
        public const string Sha1 = "1.3.14.3.2.26";
        /// <summary>SHA-256 (<c>2.16.840.1.101.3.4.2.1</c>).</summary>
        public const string Sha256 = "2.16.840.1.101.3.4.2.1";
        /// <summary>SHA-384 (<c>2.16.840.1.101.3.4.2.2</c>).</summary>
        public const string Sha384 = "2.16.840.1.101.3.4.2.2";
        /// <summary>SHA-512 (<c>2.16.840.1.101.3.4.2.3</c>).</summary>
        public const string Sha512 = "2.16.840.1.101.3.4.2.3";
        /// <summary>SHA3-256 (<c>2.16.840.1.101.3.4.2.8</c>).</summary>
        public const string Sha3_256 = "2.16.840.1.101.3.4.2.8";
        /// <summary>SHA3-384 (<c>2.16.840.1.101.3.4.2.9</c>).</summary>
        public const string Sha3_384 = "2.16.840.1.101.3.4.2.9";
        /// <summary>SHA3-512 (<c>2.16.840.1.101.3.4.2.10</c>).</summary>
        public const string Sha3_512 = "2.16.840.1.101.3.4.2.10";
        /// <summary>
        /// id-shake256, RFC 8419 §2.3 (<c>2.16.840.1.101.3.4.2.12</c>). ISO/TS 32001 requires this
        /// exact OID rather than a variable-length SHAKE256 identifier, which fixes the output at
        /// 512 bits — so it behaves as an ordinary fixed-length digest to every caller here despite
        /// being built on an extendable-output function.
        /// </summary>
        public const string Shake256 = "2.16.840.1.101.3.4.2.12";
        /// <summary>MD5 (<c>1.2.840.113549.2.5</c>). Broken; verification only.</summary>
        public const string Md5 = "1.2.840.113549.2.5";
        /// <summary>MD2 (<c>1.2.840.113549.2.2</c>). Broken; verification only.</summary>
        public const string Md2 = "1.2.840.113549.2.2";
        /// <summary>GOST R 34.11-2012 256-bit / Streebog-256 (<c>1.2.643.7.1.1.2.2</c>).</summary>
        public const string Streebog256 = "1.2.643.7.1.1.2.2";
        /// <summary>GOST R 34.11-2012 512-bit / Streebog-512 (<c>1.2.643.7.1.1.2.3</c>).</summary>
        public const string Streebog512 = "1.2.643.7.1.1.2.3";

        // ---- Canonical .NET / BouncyCastle algorithm names ----
        // Only the SHA families get a constant, because only they are named at a call site
        // (Signing passes them to the hash factories). The MD2/MD5/Streebog names are produced
        // by ToName and never consumed by name, so they stay inline in that switch.
        /// <summary>Canonical name for <see cref="Sha1"/>.</summary>
        public const string Sha1Name = "SHA-1";
        /// <summary>Canonical name for <see cref="Sha256"/>.</summary>
        public const string Sha256Name = "SHA-256";
        /// <summary>Canonical name for <see cref="Sha384"/>.</summary>
        public const string Sha384Name = "SHA-384";
        /// <summary>Canonical name for <see cref="Sha512"/>.</summary>
        public const string Sha512Name = "SHA-512";
        /// <summary>Canonical name for <see cref="Sha3_256"/>.</summary>
        public const string Sha3_256Name = "SHA3-256";
        /// <summary>Canonical name for <see cref="Sha3_384"/>.</summary>
        public const string Sha3_384Name = "SHA3-384";
        /// <summary>Canonical name for <see cref="Sha3_512"/>.</summary>
        public const string Sha3_512Name = "SHA3-512";
        /// <summary>Canonical name for <see cref="Shake256"/> — also BouncyCastle's registered
        /// digest name, whose default digest size for this constructor shape is already the
        /// 512-bit output ISO/TS 32001 requires.</summary>
        public const string Shake256Name = "SHAKE256";

        /// <summary>
        /// Maps a digest OID to its canonical algorithm name, or <c>null</c> when the OID is not
        /// a digest algorithm this library knows.
        ///
        /// <para>Returning <c>null</c> rather than defaulting to SHA-256 is deliberate: silently
        /// assuming a digest algorithm during signature verification can turn an unverifiable
        /// signature into an apparently valid one. Callers must decide explicitly.</para>
        /// </summary>
        public static string ToName(string oid)
        {
            switch (oid)
            {
                case Sha1:        return Sha1Name;
                case Sha256:      return Sha256Name;
                case Sha384:      return Sha384Name;
                case Sha512:      return Sha512Name;
                case Sha3_256:    return Sha3_256Name;
                case Sha3_384:    return Sha3_384Name;
                case Sha3_512:    return Sha3_512Name;
                case Shake256:    return Shake256Name;
                case Md5:         return "MD5";
                case Md2:         return "MD2";
                case Streebog256: return "GOST3411-2012-256";
                case Streebog512: return "GOST3411-2012-512";
                default:          return null;
            }
        }

        /// <summary>
        /// Maps a digest algorithm name to its OID, or <c>null</c> when unrecognised. Accepts both
        /// hyphenated and unhyphenated spellings (<c>SHA-256</c> and <c>SHA256</c>).
        /// </summary>
        public static string FromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            switch (name.Replace("-", string.Empty).ToUpperInvariant())
            {
                case "SHA1":              return Sha1;
                case "SHA256":            return Sha256;
                case "SHA384":            return Sha384;
                case "SHA512":            return Sha512;
                case "SHA3256":           return Sha3_256;
                case "SHA3384":           return Sha3_384;
                case "SHA3512":           return Sha3_512;
                case "SHAKE256":          return Shake256;
                case "MD5":               return Md5;
                case "MD2":               return Md2;
                case "GOST34112012256":   return Streebog256;
                case "GOST34112012512":   return Streebog512;
                default:                  return null;
            }
        }

        /// <summary>
        /// True for digest algorithms that are no longer collision-resistant and must not be
        /// trusted for new signatures.
        /// </summary>
        public static bool IsWeak(string oid) => oid == Sha1 || oid == Md5 || oid == Md2;
    }

    /// <summary>
    /// Non-digest OIDs relied on when validating signing certificates and signature attributes.
    ///
    /// <para>The name is narrower than the contents: <see cref="TimeStamping"/>,
    /// <see cref="OcspSigning"/> and the document-signing purposes from
    /// <see cref="AnyExtendedKeyUsage"/> to <see cref="MicrosoftDocumentSigning"/> are true
    /// extended-key-usage values.
    /// <see cref="AuthorityInfoAccess"/> is a certificate <em>extension</em> OID and
    /// <see cref="AuthorityInfoAccessOcsp"/> an access-method OID within it, while
    /// <see cref="SignatureTimeStampToken"/> and <see cref="CounterSignature"/> are CMS
    /// <em>attribute</em> OIDs. The type name is public API and is left alone; do not read it as a
    /// promise that everything here is an EKU.</para>
    /// </summary>
    public static class PdfEkuOids
    {
        /// <summary><c>id-kp-timeStamping</c> — required on an RFC&#160;3161 TSA certificate.</summary>
        public const string TimeStamping = "1.3.6.1.5.5.7.3.8";
        /// <summary><c>id-kp-OCSPSigning</c> — required on a delegated OCSP responder certificate.</summary>
        public const string OcspSigning = "1.3.6.1.5.5.7.3.9";
        /// <summary><c>id-ad-ocsp</c> — the OCSP access method within an AIA extension.</summary>
        public const string AuthorityInfoAccessOcsp = "1.3.6.1.5.5.7.48.1";
        /// <summary><c>id-pe-authorityInfoAccess</c> — the AIA certificate extension.</summary>
        public const string AuthorityInfoAccess = "1.3.6.1.5.5.7.1.1";
        /// <summary><c>id-aa-signatureTimeStampToken</c> — the CMS signature-timestamp attribute.</summary>
        public const string SignatureTimeStampToken = "1.2.840.113549.1.9.16.2.14";
        /// <summary><c>id-countersignature</c> — the CMS countersignature attribute.</summary>
        public const string CounterSignature = "1.2.840.113549.1.9.6";

        /// <summary><c>anyExtendedKeyUsage</c> (RFC&#160;5280 §4.2.1.12) — the certificate is good for every purpose.</summary>
        public const string AnyExtendedKeyUsage = "2.5.29.37.0";
        /// <summary><c>id-kp-emailProtection</c> — the purpose most personal signing certificates carry.</summary>
        public const string EmailProtection = "1.3.6.1.5.5.7.3.4";
        /// <summary><c>id-kp-codeSigning</c>.</summary>
        public const string CodeSigning = "1.3.6.1.5.5.7.3.3";
        /// <summary><c>id-kp-documentSigning</c> (RFC&#160;9336).</summary>
        public const string DocumentSigning = "1.3.6.1.5.5.7.3.36";
        /// <summary>Adobe's PDF signing purpose (Authentic Documents Trust).</summary>
        public const string AdobeAuthenticDocumentsTrust = "1.2.840.113583.1.1.5";
        /// <summary>Microsoft's document signing purpose.</summary>
        public const string MicrosoftDocumentSigning = "1.3.6.1.4.1.311.10.3.12";

        /// <summary>
        /// Whether <paramref name="purpose"/>, an extended-key-usage OID, is one a document
        /// signature may be made under: every purpose, e-mail protection, code signing, document
        /// signing (RFC&#160;9336), and Adobe's and Microsoft's own document-signing purposes —
        /// what Acrobat accepts, and the two purposes named for the job.
        /// </summary>
        /// <remarks>
        /// One answer for the two sides that ask it: the verifier, which refuses a signer whose
        /// certificate names purposes and none of these, and a picker that offers certificates to
        /// sign with — which must not offer one the verifier will refuse.
        /// </remarks>
        public static bool PermitsDocumentSigning(string purpose)
            => purpose == AnyExtendedKeyUsage || purpose == EmailProtection || purpose == CodeSigning
            || purpose == DocumentSigning || purpose == AdobeAuthenticDocumentsTrust
            || purpose == MicrosoftDocumentSigning;
    }
}
