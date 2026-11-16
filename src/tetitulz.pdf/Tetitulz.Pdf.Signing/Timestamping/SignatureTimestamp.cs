// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Cms;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Signing.Timestamping
{
    /// <summary>
    /// Adds an RFC 3161 signature timestamp to a CMS signature: a token over the SignerInfo's
    /// signature <b>value</b>, carried as its <c>id-aa-signatureTimeStampToken</c> unsigned attribute.
    /// </summary>
    /// <remarks>
    /// <para><b>RFC 3161 Appendix A</b> defines this token's message imprint as the hash of "the value
    /// of the signature field within the SignerInfo", and <c>PdfSignatureVerifier</c> checks it that
    /// way. <c>PdfSigner</c> hashed the whole CMS encoding instead, asked the TSA to timestamp that,
    /// and then built the CMS a second time around the token — with a new signing time, so a new
    /// signature value as well. The embedded timestamp therefore matched nothing: every signature made
    /// with a local key and a TSA carried a token its own verifier reported as not verifying, and a
    /// document meant to be PAdES B-T was assessed at the current time.</para>
    ///
    /// <para><b>Adding the attribute changes no signed byte.</b> Unsigned attributes are outside the
    /// signed attributes the signature covers, which is what lets the token be fetched after the
    /// signature exists and attached without signing again — and what lets an external signer's CMS
    /// be timestamped the same way. A device that already timestamped its own CMS is left alone.</para>
    /// </remarks>
    internal static class SignatureTimestamp
    {
        private static readonly DerObjectIdentifier SignatureTimeStampOid =
            new DerObjectIdentifier(PdfEkuOids.SignatureTimeStampToken);

        /// <summary>
        /// Returns <paramref name="cms"/> with a signature timestamp attached, or unchanged when its
        /// signer already carries one.
        /// </summary>
        /// <param name="cms">A detached CMS <c>SignedData</c> with one SignerInfo.</param>
        /// <param name="digestAlgorithm">The imprint's digest, e.g. "SHA-256".</param>
        /// <param name="requestToken">
        /// Asks a TSA for a token over <c>(imprint, imprintAlgorithmOid)</c> and returns its DER.
        /// </param>
        /// <exception cref="PdfSigningException">The CMS does not carry exactly one SignerInfo.</exception>
        internal static byte[] Add(byte[] cms, string digestAlgorithm, Func<byte[], string, byte[]> requestToken)
        {
            var signedData = new CmsSignedData(SigningDefaults.TrimToAsn1Object(cms));

            SignerInformation? signer = null;
            int count = 0;
            foreach (SignerInformation s in signedData.GetSignerInfos().GetSigners())
            {
                signer = s;
                count++;
            }

            // The imprint names one signature value, and a PDF signature carries one signer — the
            // verifier refuses a CMS with more, because its displayed identity would be ambiguous.
            if (count != 1 || signer == null)
                throw new PdfSigningException(
                    $"A signature timestamp needs a CMS with exactly one SignerInfo; this one has {count}.");

            if (signer.UnsignedAttributes?[SignatureTimeStampOid] != null)
                return cms;

            byte[] imprint;
            using (var hash = DigestAlgorithms.CreateFromName(digestAlgorithm))
                imprint = hash.ComputeHash(signer.GetSignature());

            byte[] token = requestToken(imprint, TsaClient.GetHashOid(digestAlgorithm));
            return CmsSignatureGenerator.EncodeForContents(
                CmsSignatureGenerator.AddTimestampUnsignedAttribute(signedData, token));
        }
    }
}
