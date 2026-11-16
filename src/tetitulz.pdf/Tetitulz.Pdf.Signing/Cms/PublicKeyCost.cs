// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Whether verifying with a public key costs what a key of its kind should — asked before every
    /// verification whose key a file or a server supplied: a signer's, an issuer's, an OCSP
    /// responder's, a CRL issuer's, a TSA's.
    /// </summary>
    /// <remarks>
    /// <para><b>The exponent is the key's author's.</b> An RSA verification is one exponentiation by
    /// the public exponent, and nothing in a certificate bounds it: with a 16 384-bit modulus a
    /// 16 384-bit exponent takes 8.7 s against 18 ms for 65 537, and one
    /// document's signature, chain and revocation data ask for many verifications. BouncyCastle
    /// refuses a modulus above 16 384 bits itself and no exponent.</para>
    /// <para><b>The bounds are FIPS 186-4's and OpenSSL's.</b> An RSA exponent below 2^256
    /// (FIPS 186-4 §B.3.1), and of at most 64 bits above a 3 072-bit modulus (OpenSSL's
    /// <c>OPENSSL_RSA_MAX_PUBEXP_BITS</c> over <c>OPENSSL_RSA_SMALL_MODULUS_BITS</c>); a DSA
    /// <c>q</c> of at most 256 bits (FIPS 186-4 §4.2; OpenSSL accepts 160, 224 and 256) and a
    /// <c>p</c> of at most 10 000 (<c>OPENSSL_DSA_MAX_MODULUS_BITS</c>); an EC field of at most
    /// 661 bits (<c>OPENSSL_ECC_MAX_FIELD_BITS</c>), which an explicitly parameterised curve
    /// chooses. Ed25519 and Ed448 keys have one size.</para>
    /// </remarks>
    internal static class PublicKeyCost
    {
        /// <summary>The largest RSA public exponent, in bits: FIPS 186-4 §B.3.1's e &lt; 2^256.</summary>
        internal const int MaxRsaExponentBits = 256;

        /// <summary>The modulus, in bits, above which <see cref="MaxRsaExponentBitsAboveSmallModulus"/> applies.</summary>
        internal const int SmallRsaModulusBits = 3072;

        /// <summary>The largest RSA public exponent, in bits, with a modulus above <see cref="SmallRsaModulusBits"/>.</summary>
        internal const int MaxRsaExponentBitsAboveSmallModulus = 64;

        /// <summary>The largest DSA <c>q</c>, in bits.</summary>
        internal const int MaxDsaQBits = 256;

        /// <summary>The largest DSA <c>p</c>, in bits.</summary>
        internal const int MaxDsaPBits = 10000;

        /// <summary>The largest EC field, in bits.</summary>
        internal const int MaxEcFieldBits = 661;

        [ThreadStatic]
        private static int _refusals;

        /// <summary>
        /// How many keys this thread has refused — the guard's evidence, since a verification
        /// refused for its key and one refused for its signature end alike.
        /// </summary>
        internal static int Refusals => _refusals;

        /// <summary>Why verifying with <paramref name="key"/> would cost more than its kind allows, or <see langword="null"/>.</summary>
        internal static string? WhyTooCostly(AsymmetricKeyParameter? key)
        {
            switch (key)
            {
                case RsaKeyParameters rsa:
                {
                    int modulus = rsa.Modulus.BitLength;
                    int exponent = rsa.Exponent.BitLength;
                    int limit = modulus > SmallRsaModulusBits ? MaxRsaExponentBitsAboveSmallModulus : MaxRsaExponentBits;
                    return exponent > limit
                        ? string.Format(CultureInfo.InvariantCulture,
                            "its RSA public exponent is {0} bits, above the {1} a {2}-bit modulus may have", exponent, limit, modulus)
                        : null;
                }
                case DsaPublicKeyParameters dsa when dsa.Parameters != null:
                {
                    int p = dsa.Parameters.P.BitLength;
                    int q = dsa.Parameters.Q.BitLength;
                    if (q > MaxDsaQBits)
                        return string.Format(CultureInfo.InvariantCulture, "its DSA q is {0} bits, above {1}", q, MaxDsaQBits);
                    return p > MaxDsaPBits
                        ? string.Format(CultureInfo.InvariantCulture, "its DSA p is {0} bits, above {1}", p, MaxDsaPBits)
                        : null;
                }
                case ECPublicKeyParameters ec when ec.Parameters?.Curve != null:
                {
                    int field = ec.Parameters.Curve.FieldSize;
                    return field > MaxEcFieldBits
                        ? string.Format(CultureInfo.InvariantCulture, "its EC field is {0} bits, above {1}", field, MaxEcFieldBits)
                        : null;
                }
                default:
                    return null;
            }
        }

        /// <summary>
        /// <paramref name="key"/>, when <see cref="WhyTooCostly"/> has nothing against it; otherwise
        /// an <see cref="InvalidKeyException"/> — what a verification throws for a key it cannot
        /// use, so every site's handling of a failed verification handles this too.
        /// </summary>
        internal static AsymmetricKeyParameter Affordable(AsymmetricKeyParameter key)
        {
            string? why = WhyTooCostly(key);
            if (why == null) return key;
            _refusals++;
            throw new InvalidKeyException("public key refused before verification: " + why + ".");
        }

        /// <summary>The certificate's key, through <see cref="Affordable"/>.</summary>
        internal static AsymmetricKeyParameter KeyOf(X509Certificate certificate)
            => Affordable(certificate.GetPublicKey());
    }
}
