// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
// Both Asn1.Cms and Asn1.X509 declare an AttributeTable; the CMS one is the signed-attribute
// set this file works in, and the ambiguity is resolved once here rather than at each use.
using AttributeTable = Org.BouncyCastle.Asn1.Cms.AttributeTable;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Builds a detached CMS in two steps for a signer that returns a <b>raw signature value</b>
    /// rather than a finished CMS — a CSC/eIDAS service, a browser holding a smart card, an HSM
    /// behind HTTP.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a raw signature value is not enough on its own.</b> A CAdES/PAdES signature does
    /// not sign the document: it signs the DER of the <i>signed attributes</i>, one of which is the
    /// document's digest (RFC 5652 §5.4). So a service that will sign whatever bytes it is handed
    /// needs those attribute bytes, and the result has to be assembled back into a SignerInfo around
    /// exactly the attribute set that was presented. Handing such a service the document digest
    /// produces a signature over the wrong thing, which is a real failure mode:
    /// <c>CertStoreExternalSigner</c> and <c>ISignPlugin.ComputeDigest</c> both record it from the
    /// other side.</para>
    ///
    /// <para><b>The attribute bytes come back, and they are not rebuilt.</b>
    /// <c>GetSignedAttributes</c> runs the real CMS build with a signature factory that
    /// records what it was asked to sign; <see cref="AssembleDetachedSignature"/> takes those exact
    /// bytes back and embeds them. So what the SignerInfo carries is, by construction, what the
    /// service signed.</para>
    ///
    /// <para><b>Rebuilding them would be wrong in a way only a flaky test shows.</b> The attributes
    /// are not deterministic: BouncyCastle's <see cref="DefaultSignedAttributeTableGenerator"/> adds
    /// a <c>signingTime</c> attribute of <b>now</b>. Two calls landing in the same second agree and
    /// the CMS verifies; two calls either side of a tick produce a signature over an attribute set
    /// that no longer exists, and the self-check below rejects it — roughly one run in four.
    /// <b>"The two paths are one path" is a claim about the code and says nothing about the data
    /// flowing through it.</b></para>
    ///
    /// <para><b>The assembled CMS is verified before it is returned.</b> A signature value computed
    /// over something else assembles into a perfectly well-formed CMS, and the failure would
    /// otherwise surface as "the PDF's signature is broken" — which reads as a signing defect rather
    /// than as a service that signed the wrong bytes.</para>
    ///
    /// <para><b>The algorithm is named, not identified by OID.</b> An OID does not determine a
    /// signature algorithm: RSASSA-PSS carries mandatory parameters (hash, MGF, salt length) that
    /// live beside the OID, so <c>1.2.840.113549.1.1.10</c> alone is not a complete
    /// <c>AlgorithmIdentifier</c> and writing one with absent parameters produces a signature no
    /// verifier can reproduce. <c>SignatureParameters.SignatureAlgorithmOid</c> takes an OID because
    /// there BouncyCastle holds the private key and fills the parameters in from it; here there is
    /// no key to ask, so the name is what carries the missing half.</para>
    /// </remarks>
    public static class CmsDeferredSignatureBuilder
    {
        /// <summary>
        /// The exact bytes a signing service must sign: the DER <c>SET OF Attribute</c> of the
        /// signed attributes, per RFC 5652 §5.4.
        /// </summary>
        /// <param name="dataToSign">
        /// The content the signature covers — for a PDF, <c>PreparedSignature.SignedBytes</c>. It is
        /// digested into the <c>messageDigest</c> attribute; it is never signed directly.
        /// </param>
        /// <param name="certificate">The signer's certificate.</param>
        /// <param name="digestAlgorithm">Digest algorithm name, e.g. <c>"SHA-256"</c>.</param>
        /// <param name="signatureAlgorithmName">
        /// BouncyCastle signature-algorithm name (<c>"SHA256withRSA"</c>,
        /// <c>"SHA256withRSAandMGF1"</c>, <c>"SHA256withECDSA"</c>, <c>"Ed25519"</c>). Defaults to
        /// the plain scheme for <paramref name="certificate"/>'s own key type — which is a default,
        /// not a detection: a certificate whose key is used with RSA-PSS looks exactly like one used
        /// with PKCS#1, so a PSS signer must say so here.
        /// </param>
        /// <returns>
        /// The DER to sign. Typically a few hundred bytes — this is what crosses the wire, and it
        /// must be handed back to <see cref="AssembleDetachedSignature"/> along with the signature,
        /// because it carries a <c>signingTime</c> that cannot be reproduced by calling this again.
        /// </returns>
        /// <remarks>
        /// The attributes carry a <c>signing-time</c> of the current time, which a PAdES
        /// (<c>ETSI.CAdES.detached</c>) signature must not (ETSI EN 319 142-1 §6.3, Table 1). For a
        /// document from <see cref="PdfDeferredSigner.Prepare"/> use
        /// <see cref="GetSignedAttributes(PreparedSignature, X509Certificate, string)"/>, which reads
        /// the prepared signature's own <c>/SubFilter</c> and <c>/M</c>.
        /// </remarks>
        /// <exception cref="PdfSigningException">The algorithm could not be resolved.</exception>
        public static byte[] GetSignedAttributes(
            byte[] dataToSign, X509Certificate certificate, string digestAlgorithm,
            string? signatureAlgorithmName = null)
        {
            if (dataToSign == null) throw new ArgumentNullException(nameof(dataToSign));
            if (certificate == null) throw new ArgumentNullException(nameof(certificate));

            return Present(dataToSign, certificate, digestAlgorithm, signatureAlgorithmName, DateTime.UtcNow);
        }

        /// <summary>
        /// The exact bytes a signing service must sign for a document
        /// <see cref="PdfDeferredSigner.Prepare"/> made: the signed attributes over
        /// <see cref="PreparedSignature.SignedBytes"/> in <see cref="PreparedSignature.DigestAlgorithm"/>,
        /// with the signing time the prepared signature dictionary calls for.
        /// </summary>
        /// <param name="prepared">What <see cref="PdfDeferredSigner.Prepare"/> returned.</param>
        /// <param name="certificate">The signer's certificate.</param>
        /// <param name="signatureAlgorithmName">As in the other overload.</param>
        /// <remarks>
        /// No <c>signing-time</c> attribute when <see cref="PreparedSignature.SubFilter"/> is
        /// <c>ETSI.CAdES.detached</c> (ETSI EN 319 142-1 §6.3, Table 1; ISO 32000-2 §12.8.3.4.2);
        /// otherwise the instant the dictionary's <c>/M</c> claims, so the two agree. Hand the result
        /// back to <see cref="AssembleDetachedSignature"/> as with the other overload.
        /// </remarks>
        /// <exception cref="PdfSigningException">The algorithm could not be resolved.</exception>
        public static byte[] GetSignedAttributes(
            PreparedSignature prepared, X509Certificate certificate, string? signatureAlgorithmName = null)
        {
            if (prepared == null) throw new ArgumentNullException(nameof(prepared));
            if (certificate == null) throw new ArgumentNullException(nameof(certificate));

            DateTime? signingTime = prepared.SigningTime.HasValue
                ? CmsSignatureGenerator.SigningTimeAttributeFor(prepared.SubFilter, prepared.SigningTime.Value.UtcDateTime)
                : null;
            return Present(prepared.SignedBytes, certificate, prepared.DigestAlgorithm,
                           signatureAlgorithmName, signingTime);
        }

        /// <summary>
        /// Runs the build with a one-byte stub signature, only to observe the attribute bytes the
        /// generator presents. The stub's length does not size anything here — the reserve is
        /// <see cref="PdfDeferredSigner"/>'s business, one level up.
        /// </summary>
        private static byte[] Present(
            byte[] dataToSign, X509Certificate certificate, string digestAlgorithm,
            string? signatureAlgorithmName, DateTime? signingTime)
        {
            Build(dataToSign, certificate, new List<X509Certificate> { certificate },
                  digestAlgorithm, signatureAlgorithmName, new byte[] { 0 }, null,
                  verify: false, attributes: null, signingTime, out byte[] presented);
            return presented;
        }

        /// <summary>
        /// Assembles the finished detached CMS around a signature value computed over what
        /// <c>GetSignedAttributes</c> returned.
        /// </summary>
        /// <param name="dataToSign">The same content that was passed to <c>GetSignedAttributes</c>.</param>
        /// <param name="signedAttributes">
        /// Exactly what <c>GetSignedAttributes</c> returned. <b>Not rebuilt here</b>: it
        /// carries a <c>signingTime</c> of the moment it was produced, so a second build would
        /// differ from what the service actually signed.
        /// </param>
        /// <param name="certificate">The signer's certificate.</param>
        /// <param name="chain">The chain to embed. The signer's own certificate is added if absent.</param>
        /// <param name="digestAlgorithm">The same digest algorithm.</param>
        /// <param name="signatureValue">What the signing service returned.</param>
        /// <param name="signatureAlgorithmName">The same algorithm name, or <c>null</c> for the same default.</param>
        /// <param name="timestampToken">
        /// Optional DER-encoded RFC 3161 token, embedded as the <c>signatureTimeStampToken</c>
        /// unsigned attribute — the same attachment the local-key path uses.
        /// </param>
        /// <exception cref="PdfSigningException">
        /// The signature value does not verify against the attributes that were presented, or the
        /// algorithm could not be resolved.
        /// </exception>
        public static byte[] AssembleDetachedSignature(
            byte[] dataToSign, byte[] signedAttributes,
            X509Certificate certificate, IList<X509Certificate> chain,
            string digestAlgorithm, byte[] signatureValue,
            string? signatureAlgorithmName = null, byte[]? timestampToken = null)
        {
            if (dataToSign == null) throw new ArgumentNullException(nameof(dataToSign));
            if (signedAttributes == null) throw new ArgumentNullException(nameof(signedAttributes));
            if (certificate == null) throw new ArgumentNullException(nameof(certificate));
            if (chain == null) throw new ArgumentNullException(nameof(chain));
            if (signatureValue == null) throw new ArgumentNullException(nameof(signatureValue));
            if (signatureValue.Length == 0)
                throw new PdfSigningException("The signature value is empty.");

            AttributeTable table;
            try
            {
                table = new AttributeTable(
                    Asn1Set.GetInstance(Asn1Object.FromByteArray(signedAttributes)));
            }
            catch (Exception ex)
            {
                throw new PdfSigningException(
                    "The signed attributes are not a DER SET OF Attribute, so they are not what " +
                    "GetSignedAttributes returned: " + ex.Message, ex);
            }

            return Build(dataToSign, certificate, chain, digestAlgorithm, signatureAlgorithmName,
                         signatureValue, timestampToken, verify: true, table, signingTime: null, out byte[] _);
        }

        // ------------------------------------------------------------------

        private static byte[] Build(
            byte[] dataToSign, X509Certificate certificate, IList<X509Certificate> chain,
            string digestAlgorithm, string? signatureAlgorithmName,
            byte[] signatureValue, byte[]? timestampToken, bool verify,
            AttributeTable? attributes, DateTime? signingTime, out byte[] presentedAttributes)
        {
            AlgorithmIdentifier algorithm =
                ResolveAlgorithm(certificate, digestAlgorithm, signatureAlgorithmName);

            var factory = new PrecomputedSignatureFactory(algorithm, signatureValue);

            // Two generators, and which one is used is the whole correctness argument.
            //
            // Producing the bytes to sign (attributes == null): the generator the local-key path
            // uses - CmsSignatureGenerator's own method, not a copy of it. A second implementation
            // would put the ESS signing-certificate-v2 binding on one path and not the other, and
            // both signatures would verify while only one of them was PAdES. It adds contentType and
            // messageDigest, and signingTime exactly when `signingTime` names one.
            //
            // Assembling around a returned signature: the SIMPLE generator, over the attributes the
            // caller handed back. Re-running the other one here would mint a fresh signingTime
            // and embed an attribute set the service never saw.
            CmsAttributeTableGenerator attributeGenerator = attributes != null
                ? (CmsAttributeTableGenerator)new SimpleAttributeTableGenerator(attributes)
                : CmsSignatureGenerator.SignedAttributeGenerator(certificate, signingTime);

            var signerInfo = new SignerInfoGeneratorBuilder()
                .WithSignedAttributeGenerator(attributeGenerator)
                .Build(factory, certificate);

            var generator = new CmsSignedDataGenerator();
            generator.AddSignerInfoGenerator(signerInfo);

            var certList = new List<X509Certificate>(chain);
            if (!certList.Contains(certificate))
                certList.Insert(0, certificate);
            generator.AddCertificates(CollectionUtilities.CreateStore(certList));

            CmsSignedData signedData;
            try
            {
                signedData = generator.Generate(new CmsProcessableByteArray(dataToSign), false);
            }
            catch (Exception ex) when (!(ex is PdfSigningException))
            {
                throw new PdfSigningException(
                    "The CMS could not be assembled: " + ex.Message, ex);
            }

            presentedAttributes = factory.Presented
                ?? throw new PdfSigningException(
                    "The CMS generator never presented any signed attributes, so there is nothing " +
                    "for a signing service to sign.");

            if (verify) VerifySigners(signedData, certificate);

            if (timestampToken != null)
                signedData = CmsSignatureGenerator.AddTimestampUnsignedAttribute(
                    signedData, timestampToken);

            return CmsSignatureGenerator.EncodeForContents(signedData);
        }

        /// <summary>
        /// Checks the assembled CMS against the certificate before it is handed back.
        /// </summary>
        /// <remarks>
        /// A value signed over something else — the document digest instead of the attributes, or a
        /// stale attribute set — assembles into a well-formed CMS that simply does not verify. Left
        /// undetected it becomes a broken PDF signature, which is the same symptom as a corrupted
        /// file and points at entirely the wrong place.
        /// </remarks>
        private static void VerifySigners(CmsSignedData signedData, X509Certificate certificate)
        {
            foreach (SignerInformation signer in signedData.GetSignerInfos().GetSigners())
            {
                bool ok;
                try { ok = signer.Verify(certificate); }
                catch (Exception ex)
                {
                    throw new PdfSigningException(
                        "The assembled CMS could not be verified against the signer's certificate, " +
                        "so the signature value does not match the attributes that were presented: " +
                        ex.Message, ex);
                }

                if (!ok)
                    throw new PdfSigningException(
                        "The signature value does not verify over the signed attributes that " +
                        "GetSignedAttributes returned. The usual cause is signing the document " +
                        "digest instead of those attribute bytes, or signing an attribute set " +
                        "built with different parameters.");
            }
        }

        /// <summary>
        /// Resolves the signature <see cref="AlgorithmIdentifier"/> — parameters included, which is
        /// the half an OID cannot carry.
        /// </summary>
        private static AlgorithmIdentifier ResolveAlgorithm(
            X509Certificate certificate, string digestAlgorithm, string? signatureAlgorithmName)
        {
            string name = signatureAlgorithmName ?? DeriveName(certificate, digestAlgorithm);
            try
            {
                return Org.BouncyCastle.Operators.Utilities.DefaultSignatureAlgorithmFinder.Instance.Find(name);
            }
            catch (Exception ex)
            {
                throw new PdfSigningException(
                    $"'{name}' is not a signature algorithm this build can name. Pass " +
                    "signatureAlgorithmName explicitly, e.g. \"SHA256withRSA\", " +
                    "\"SHA256withRSAandMGF1\", \"SHA256withECDSA\" or \"Ed25519\".", ex);
            }
        }

        /// <summary>
        /// The plain scheme for the certificate's own key type. <b>A default, not a detection</b> —
        /// an RSA certificate signed with PSS is indistinguishable from one signed with PKCS#1, so a
        /// PSS signer has to name it.
        /// </summary>
        private static string DeriveName(X509Certificate certificate, string digestAlgorithm)
        {
            AsymmetricKeyParameter key = certificate.GetPublicKey();

            // Ed25519/Ed448 name no digest: the scheme fixes it (RFC 8032), and BouncyCastle's
            // finder rejects a "SHAxxxwithEd25519" spelling outright.
            if (key is Ed25519PublicKeyParameters) return "Ed25519";
            if (key is Ed448PublicKeyParameters) return "Ed448";

            string digest = NormaliseDigest(digestAlgorithm);
            if (key is RsaKeyParameters) return digest + "withRSA";
            if (key is ECPublicKeyParameters) return digest + "withECDSA";

            throw new PdfSigningException(
                $"The certificate's key type ({key.GetType().Name}) has no default signature " +
                "algorithm here. Pass signatureAlgorithmName explicitly.");
        }

        /// <summary>
        /// This library's digest spellings, in the one BouncyCastle's signature-algorithm finder
        /// accepts.
        /// </summary>
        /// <remarks>
        /// <b>The SHA-2 and SHA-3 families are separated differently, and stripping both breaks
        /// SHA-3.</b> Measured against the finder rather than assumed: it knows
        /// <c>SHA256withECDSA</c> and not <c>SHA-256withECDSA</c>, and <c>SHA3-256withRSA</c> and
        /// not <c>SHA3256withRSA</c>. Removing every hyphen would make every SHA-3 deferred
        /// signature unnameable — refused with a message blaming the caller's algorithm name — and a
        /// suite of SHA-256 tests alone would not see it.
        /// <c>DigestAlgorithms.ResolveOidForNewSignature</c> is the source of the accepted set, and
        /// it folds the underscore spellings onto hyphens the same way.
        /// </remarks>
        private static string NormaliseDigest(string digestAlgorithm)
        {
            if (string.IsNullOrEmpty(digestAlgorithm)) return "SHA256";

            string name = digestAlgorithm.Replace('_', '-').ToUpperInvariant();
            return name.StartsWith("SHA-", StringComparison.Ordinal)
                ? "SHA" + name.Substring(4)
                : name;
        }

        /// <summary>
        /// An <see cref="ISignatureFactory"/> that records what it is asked to sign and returns a
        /// value decided in advance.
        /// </summary>
        /// <remarks>
        /// This is the whole trick: BouncyCastle's generator builds the SignerInfo exactly as it
        /// always does, and the one thing it cannot do — reach the key — is supplied from outside.
        /// The bytes it hands the calculator are the DER <c>SET OF Attribute</c> of RFC 5652 §5.4,
        /// not the content and not a digest.
        /// </remarks>
        private sealed class PrecomputedSignatureFactory : ISignatureFactory
        {
            private readonly AlgorithmIdentifier _algorithm;
            private readonly byte[] _signature;

            internal PrecomputedSignatureFactory(AlgorithmIdentifier algorithm, byte[] signature)
            {
                _algorithm = algorithm;
                _signature = signature;
            }

            internal byte[]? Presented { get; private set; }

            public object AlgorithmDetails => _algorithm;

            public IStreamCalculator<IBlockResult> CreateCalculator() => new Calculator(this);

            private sealed class Calculator : IStreamCalculator<IBlockResult>
            {
                private readonly PrecomputedSignatureFactory _owner;
                private readonly MemoryStream _presented = new MemoryStream();

                internal Calculator(PrecomputedSignatureFactory owner) { _owner = owner; }

                public Stream Stream => _presented;

                public IBlockResult GetResult()
                {
                    _owner.Presented = _presented.ToArray();
                    return new SimpleBlockResult(_owner._signature);
                }
            }
        }
    }
}
