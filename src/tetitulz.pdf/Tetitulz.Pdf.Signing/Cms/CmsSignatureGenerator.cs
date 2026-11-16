// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Asn1.Ess;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Generates RFC 3852 / PKCS#7 detached CMS signatures over arbitrary
    /// data, optionally embedding an RFC 3161 timestamp token as an unsigned
    /// attribute.  Public so HSM / smart-card integrations can build custom
    /// signing flows that don't fit the <c>PdfSigner</c> shape.
    /// </summary>
    public static class CmsSignatureGenerator
    {
        // id-aa-signatureTimeStampToken OID (RFC 3161 / RFC 5652)
        private static readonly DerObjectIdentifier TimestampTokenOid =
            new DerObjectIdentifier(PdfEkuOids.SignatureTimeStampToken);

        /// <summary>
        /// Generates a PKCS#7/CMS detached signature.
        /// Optionally embeds an RFC 3161 timestamp token as an unsigned attribute.
        /// </summary>
        /// <param name="dataToSign">The data to sign (the byte range content).</param>
        /// <param name="privateKey">The signer's private key.</param>
        /// <param name="certificate">The signer's certificate.</param>
        /// <param name="chain">Full certificate chain.</param>
        /// <param name="digestAlgorithm">Digest algorithm (e.g., "SHA-256").</param>
        /// <param name="timestampToken">
        /// Optional DER-encoded RFC 3161 TimeStampToken to embed.
        /// Pass null to omit timestamping.
        /// </param>
        /// <returns>DER-encoded PKCS#7 signature bytes.</returns>
        /// <remarks>
        /// The CMS carries a <c>signing-time</c> attribute of the current time, which suits an
        /// <c>adbe.pkcs7.detached</c> signature. A PAdES (<c>ETSI.CAdES.detached</c>) signature must
        /// carry none (ETSI EN 319 142-1 §6.3, Table 1): <c>PdfSigner</c> writes that one itself.
        /// </remarks>
        public static byte[] GenerateDetachedSignature(
            byte[] dataToSign,
            AsymmetricKeyParameter? privateKey,
            X509Certificate certificate,
            IList<X509Certificate> chain,
            string digestAlgorithm,
            byte[]? timestampToken = null)
            => Generate(dataToSign, privateKey, certificate, chain, digestAlgorithm,
                        signatureAlgorithmOid: null, DateTime.UtcNow, timestampToken);

        /// <summary>
        /// Adds the RFC 3161 timestamp token as the id-aa-signatureTimeStampToken
        /// unsigned attribute on each SignerInfo in the CMS structure.
        /// </summary>
        internal static CmsSignedData AddTimestampUnsignedAttribute(
            CmsSignedData signedData, byte[] timestampTokenDer)
            => AddUnsignedAttribute(signedData, TimestampTokenOid, timestampTokenDer);

        /// <summary>
        /// The bytes a CMS signature is written into a signature dictionary's <c>/Contents</c> as:
        /// <b>DER</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>The encoding is a requirement, and BouncyCastle's default is not it.</b>
        /// <c>CmsSignedData.GetEncoded()</c> emits <c>SignedData</c> with <b>indefinite lengths</b>
        /// (<c>30 80 … 00 00</c>), which is valid BER and is not DER. ISO 32000-2 §12.8.3.3.1
        /// requires the value to be a DER-encoded CMS object, and the CAdES/PAdES profiles say the
        /// same; a strict validator is entitled to reject the other form.</para>
        ///
        /// <para><b>Nothing in this library could see the difference, which is why there is one
        /// implementation of it now.</b> Both forms parse to the same <c>SignedData</c>, so every
        /// round trip, every verification and every test that reads the structure agrees under
        /// either — the defect is invisible to any assertion about what the CMS <i>means</i>. Six
        /// sites wrote a CMS into a <c>/Contents</c> and each called <c>GetEncoded()</c> on its own;
        /// converting some of them would have left the rest, and no test in the tree would have
        /// reported which.</para>
        ///
        /// <para><b>DER is also shorter</b>, since a declared length replaces the terminator, so
        /// nothing that fitted its reserve before stops fitting.</para>
        /// </remarks>
        internal static byte[] EncodeForContents(CmsSignedData signedData)
            => signedData.GetEncoded(Asn1Encodable.Der);

        /// <summary>
        /// Adds one DER-encoded value as an unsigned attribute on <b>every</b> SignerInfo in the CMS
        /// structure.
        /// </summary>
        /// <remarks>
        /// <para><b>Two attributes need this and they are otherwise unrelated</b>: RFC 3161's
        /// <c>id-aa-signatureTimeStampToken</c> and ISO/TS 32004 §6.5.2's <c>id-attr-pdfMacData</c>.
        /// The mechanics — replace each signer's unsigned table, mint one when there is none,
        /// rebuild the signer store — are the same for both, and a second copy could only agree or
        /// be a defect.</para>
        ///
        /// <para><b>Unsigned attributes are outside what the signature signs</b>, which is exactly
        /// why both of these live here: a timestamp cannot be produced until the signature exists,
        /// and a PDF MAC token digests the signature value itself. Adding one therefore does not
        /// invalidate the signature — but it does make the CMS longer, so a caller writing into a
        /// fixed-width <c>/Contents</c> placeholder must have reserved for it.</para>
        /// </remarks>
        /// <param name="signedData">The CMS to extend.</param>
        /// <param name="attributeOid">The attribute's type identifier.</param>
        /// <param name="derValue">The attribute's single value, DER-encoded.</param>
        internal static CmsSignedData AddUnsignedAttribute(
            CmsSignedData signedData, DerObjectIdentifier attributeOid, byte[] derValue)
        {
            var value = Asn1Object.FromByteArray(derValue);
            var attribute = new Org.BouncyCastle.Asn1.Cms.Attribute(attributeOid, new DerSet(value));

            var oldSigners = signedData.GetSignerInfos().GetSigners();
            var newSigners = new List<SignerInformation>();

            foreach (SignerInformation signer in oldSigners)
            {
                AttributeTable existingUnsigned = signer.UnsignedAttributes;

                AttributeTable newUnsigned = existingUnsigned == null
                    ? new AttributeTable(new Asn1EncodableVector { attribute })
                    : existingUnsigned.Add(attributeOid, value);

                newSigners.Add(SignerInformation.ReplaceUnsignedAttributes(signer, newUnsigned));
            }

            return CmsSignedData.ReplaceSigners(
                signedData, new SignerInformationStore(newSigners));
        }

        /// <summary>
        /// Builds the CAdES/PAdES signed-attribute table. BouncyCastle adds the
        /// mandatory <c>contentType</c> and <c>messageDigest</c> attributes
        /// automatically when a signed-attribute table is supplied; we add the
        /// ESS <c>signing-certificate-v2</c> attribute (RFC 5035) that binds the
        /// signer certificate's SHA-256 hash into the signed data. Without signed
        /// attributes the signature is computed directly over the content and is
        /// not CAdES/PAdES-conformant (and is open to certificate-substitution
        /// ambiguity), so this is required for the <c>ETSI.CAdES.detached</c> path.
        /// </summary>
        internal static AttributeTable BuildSignedAttributes(X509Certificate certificate)
        {
            byte[] certHash = DigestUtilities.CalculateDigest(
                PdfDigestOids.Sha256Name, certificate.GetEncoded());
            var essCertId = new EssCertIDv2(certHash);
            var signingCert = new SigningCertificateV2(new[] { essCertId });
            var attrs = new Asn1EncodableVector
            {
                new Org.BouncyCastle.Asn1.Cms.Attribute(
                    PkcsObjectIdentifiers.IdAASigningCertificateV2,
                    new DerSet(signingCert))
            };
            return new AttributeTable(attrs);
        }

        /// <summary>
        /// The signing time the CMS of a signature with <paramref name="subFilter"/> carries, given
        /// the time the signature dictionary's <c>/M</c> claims: none for a PAdES signature, the same
        /// instant for any other.
        /// </summary>
        /// <remarks>
        /// <para><b>PAdES: none.</b> ETSI EN 319 142-1 §6.3, Table 1 (V1.1.1 and V1.2.1 alike):
        /// at every baseline level the <c>/M</c> entry "shall be present" and the signing-time
        /// attribute "shall not be present"; ISO 32000-2 §12.8.3.4.2 says the time goes in one of the
        /// two, "but not both". The signer always writes <c>/M</c>.</para>
        ///
        /// <para><b>Anything else: the instant <c>/M</c> claims.</b> ISO 32000-2 Table 255 makes
        /// <c>/M</c> the fallback "when the time of signing is not available in the signature", so
        /// the attribute is the time a reader shows. BouncyCastle's generator adds one of the current
        /// time when the table has none, which put a second, different claimed time in every
        /// signature whose <c>SignatureParameters.SigningTime</c> was set — and BouncyCastle's own
        /// verification checks the certificate against the attribute, so a signature claimed inside
        /// its certificate's period failed to verify once that certificate had expired.</para>
        /// </remarks>
        internal static DateTime? SigningTimeAttributeFor(string subFilter, DateTime claimedSigningTime)
            => subFilter == SeedValueConstraints.PadesSubFilter ? (DateTime?)null : claimedSigningTime;

        /// <summary>
        /// The signed attributes every CMS this library builds goes through:
        /// <see cref="BuildSignedAttributes"/>, BouncyCastle's content type, message digest and
        /// algorithm protection, and a <c>signing-time</c> attribute of
        /// <paramref name="signingTime"/> — or none, when it is <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="DefaultSignedAttributeTableGenerator"/> adds a signing time of <b>now</b> to a
        /// table that has none, so leaving it out takes a removal, and choosing it takes an entry the
        /// generator then keeps. Which of the two a signature gets is
        /// <see cref="SigningTimeAttributeFor"/>'s answer.
        /// </remarks>
        internal static CmsAttributeTableGenerator SignedAttributeGenerator(
            X509Certificate certificate, DateTime? signingTime)
        {
            AttributeTable table = SignedAttributes(certificate, signingTime);
            var generator = new DefaultSignedAttributeTableGenerator(table);
            return signingTime.HasValue ? (CmsAttributeTableGenerator)generator : new WithoutSigningTime(generator);
        }

        /// <summary>
        /// <see cref="BuildSignedAttributes"/>, with a <c>signing-time</c> attribute of
        /// <paramref name="signingTime"/> when there is one: in UTC, converted as <c>/M</c> is
        /// (<c>PdfDate.FormatUtc</c>), and to the second as <c>/M</c> is — which BouncyCastle's
        /// <see cref="Time"/> does itself, a GeneralizedTime past 2049 included.
        /// </summary>
        private static AttributeTable SignedAttributes(X509Certificate certificate, DateTime? signingTime)
        {
            AttributeTable table = BuildSignedAttributes(certificate);
            if (!signingTime.HasValue) return table;

            DateTime utc = signingTime.Value.Kind == DateTimeKind.Utc
                ? signingTime.Value
                : signingTime.Value.ToUniversalTime();
            return table.Add(CmsAttributes.SigningTime, new Time(utc));
        }

        /// <summary>BouncyCastle's default signed attributes, its <c>signing-time</c> of now taken out.</summary>
        private sealed class WithoutSigningTime : CmsAttributeTableGenerator
        {
            private readonly CmsAttributeTableGenerator _inner;

            internal WithoutSigningTime(CmsAttributeTableGenerator inner) { _inner = inner; }

            public AttributeTable GetAttributes(IDictionary<CmsAttributeTableParameter, object> parameters)
                => _inner.GetAttributes(parameters).Remove(CmsAttributes.SigningTime);
        }

        /// <summary>
        /// A detached SignedData signed with Ed448 as a PDF signature must be: the digest algorithm the
        /// fixed-length <c>id-shake256</c> with no parameters (ISO/TS 32002 Table 4 and ISO/TS 32001
        /// §5.1.4, which forbid any other SHAKE256 identifier — over RFC 8419 §3.1's
        /// <c>id-shake256-len</c>), the message-digest attribute SHAKE256 of the content at 512 bits
        /// beside the content-type attribute and the signing-certificate-v2 and (when
        /// <paramref name="signingTime"/> names one) signing-time attributes, the signature
        /// algorithm <c>id-Ed448</c> with no parameters, and the signature pure Ed448 (empty context) over
        /// the DER of the signed attributes (RFC 8419 §3.1). The signer is named by issuer and serial
        /// number; the certificates are the signer's and the chain's.
        /// </summary>
        internal static CmsSignedData Ed448SignedData(byte[] content, AsymmetricKeyParameter? privateKey,
            X509Certificate certificate, IList<X509Certificate> chain, DateTime? signingTime)
        {
            AttributeTable signedAttributes = SignedAttributes(certificate, signingTime);
            if (privateKey == null) throw new System.ArgumentNullException(nameof(privateKey));
            var shake = new Org.BouncyCastle.Crypto.Digests.ShakeDigest(256);
            shake.BlockUpdate(content, 0, content.Length);
            var digest = new byte[64];
            shake.OutputFinal(digest, 0, digest.Length);

            var digestAlgorithm = new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(
                new DerObjectIdentifier(PdfDigestOids.Shake256));
            var signatureAlgorithm = new Org.BouncyCastle.Asn1.X509.AlgorithmIdentifier(
                new DerObjectIdentifier(SignatureAlgorithmOids.Ed448));

            Asn1EncodableVector attributes = signedAttributes.ToAsn1EncodableVector();
            attributes.Add(new Org.BouncyCastle.Asn1.Cms.Attribute(CmsAttributes.ContentType, new DerSet(CmsObjectIdentifiers.Data)));
            attributes.Add(new Org.BouncyCastle.Asn1.Cms.Attribute(CmsAttributes.MessageDigest, new DerSet(new DerOctetString(digest))));
            var signedAttrs = new DerSet(attributes);

            byte[] toSign = signedAttrs.GetEncoded(Asn1Encodable.Der);
            var signer = new Org.BouncyCastle.Crypto.Signers.Ed448Signer(System.Array.Empty<byte>());
            signer.Init(true, privateKey);
            signer.BlockUpdate(toSign, 0, toSign.Length);
            byte[] signature = signer.GenerateSignature();

            var signerInfo = new Org.BouncyCastle.Asn1.Cms.SignerInfo(
                new Org.BouncyCastle.Asn1.Cms.SignerIdentifier(new Org.BouncyCastle.Asn1.Cms.IssuerAndSerialNumber(certificate.IssuerDN, certificate.SerialNumber)),
                digestAlgorithm, signedAttrs, signatureAlgorithm, new DerOctetString(signature), null);

            var certificates = new Asn1EncodableVector { certificate.CertificateStructure };
            foreach (X509Certificate c in chain)
                if (!c.Equals(certificate)) certificates.Add(c.CertificateStructure);

            var signedData = new Org.BouncyCastle.Asn1.Cms.SignedData(new DerSet(digestAlgorithm), new Org.BouncyCastle.Asn1.Cms.ContentInfo(CmsObjectIdentifiers.Data, null),
                new DerSet(certificates), null, new DerSet(signerInfo));
            return new CmsSignedData(new Org.BouncyCastle.Asn1.Cms.ContentInfo(CmsObjectIdentifiers.SignedData, signedData));
        }

        private static bool IsEdDsa(string? signatureAlgorithmOid)
            => signatureAlgorithmOid == SignatureAlgorithmOids.Ed25519
            || signatureAlgorithmOid == SignatureAlgorithmOids.Ed448;

        /// <summary>
        /// Resolves a digest algorithm name to its OID for CMS generation.
        /// </summary>
        /// <remarks>
        /// <para>ONE implementation, in <c>DigestAlgorithms.ResolveOidForNewSignature</c> — a
        /// hand-written copy of the rule here is the one that would drift dangerously.</para>
        ///
        /// <para><b>THE DEFECT THIS FIXES.</b> The <c>default:</c> arm returned
        /// <see cref="PdfDigestOids.Sha256"/> for <i>any</i> unrecognised name, under a doc comment
        /// defending it ("the caller chose the name, and an unknown name has always meant 'use the
        /// default'"). <c>PdfSigner.Sign</c> hands
        /// <c>SignatureParameters.DigestAlgorithm</c> straight to this class with no prior
        /// acceptance check, so <c>DigestAlgorithm = "SHA-224"</c> — or "MD5", or a typo — produced
        /// a <b>SHA-256</b> signature and reported success, while
        /// <c>PdfCounterSigner.AddCounterSignature</c> threw on the identical string and
        /// <c>TsaClient.GetHashOid</c>'s own comment called the behaviour a defect
        /// ("the silent fallback masked crypto-strength errors"). An unrecognised name now throws
        /// <c>ArgumentException</c>, matching the other three.</para>
        ///
        /// <para><b>Why it survived.</b> The substitution leaves no trace: the CMS is well-formed,
        /// it verifies, and its <c>digestAlgorithm</c> field reads SHA-256 — indistinguishable from
        /// a signature the caller really did ask for in SHA-256.</para>
        ///
        /// <para>Both of this call site's genuine policies are kept, as arguments rather than as
        /// drift. Streebog stays accepted, including the aliases
        /// <see cref="PdfDigestOids.FromName"/> does not carry, because
        /// <c>UseAlgorithm(SignatureAlgorithm.GostR3410_2012_256)</c> sets
        /// <c>DigestAlgorithm = "GOST3411-2012-256"</c> and this is the only one of the four that
        /// can sign with it. And a <see langword="null"/>/empty name still means SHA-256: these are
        /// public entry points, and declining to name an algorithm is not the same as naming one
        /// that does not exist.</para>
        /// </remarks>
        private static string GetDigestOid(string algorithm)
            => DigestAlgorithms.ResolveOidForNewSignature(
                algorithm, nameof(algorithm), allowStreebog: true, defaultWhenUnnamed: true);

        /// <summary>
        /// Generates a CMS signature using a caller-supplied signature
        /// algorithm OID (e.g. RSA-PSS, ECDSA, EdDSA).  Falls back to the
        /// BouncyCastle generator's default algorithm-for-key when
        /// <paramref name="signatureAlgorithmOid"/> is null/empty.
        /// </summary>
        /// <param name="dataToSign">Byte-range content to sign.</param>
        /// <param name="privateKey">Signer private key.</param>
        /// <param name="certificate">Signer certificate.</param>
        /// <param name="chain">Full certificate chain for embedding.</param>
        /// <param name="digestAlgorithm">Digest algorithm name ("SHA-256", "SHA3-512", etc.).</param>
        /// <param name="signatureAlgorithmOid">
        /// Explicit signature-algorithm OID.  Common values:
        /// 1.2.840.113549.1.1.10 (RSA-PSS),
        /// 1.2.840.10045.4.3.2 (ECDSA-with-SHA256),
        /// 1.2.840.10045.4.3.3 (ECDSA-with-SHA384),
        /// 1.2.840.10045.4.3.4 (ECDSA-with-SHA512),
        /// 1.3.101.112 (Ed25519),
        /// 1.3.101.113 (Ed448).
        /// Pass null for the BouncyCastle default (RSA-with-SHA-N for RSA keys).
        /// </param>
        /// <param name="timestampToken">Optional RFC 3161 token to embed as unsigned attribute.</param>
        /// <remarks>
        /// The CMS carries a <c>signing-time</c> attribute of the current time, as
        /// <see cref="GenerateDetachedSignature"/>'s does, and for the same reason.
        /// </remarks>
        public static byte[] GenerateDetachedSignatureWithAlgorithm(
            byte[] dataToSign,
            AsymmetricKeyParameter? privateKey,
            X509Certificate certificate,
            IList<X509Certificate> chain,
            string digestAlgorithm,
            string? signatureAlgorithmOid,
            byte[]? timestampToken = null)
            => Generate(dataToSign, privateKey, certificate, chain, digestAlgorithm,
                        signatureAlgorithmOid, DateTime.UtcNow, timestampToken);

        /// <summary>
        /// The detached CMS both public doors and <c>PdfSigner</c> build, with a <c>signing-time</c>
        /// attribute of <paramref name="signingTime"/>, or none when it is <see langword="null"/>
        /// (<see cref="SigningTimeAttributeFor"/>).
        /// </summary>
        internal static byte[] Generate(
            byte[] dataToSign,
            AsymmetricKeyParameter? privateKey,
            X509Certificate certificate,
            IList<X509Certificate> chain,
            string digestAlgorithm,
            string? signatureAlgorithmOid,
            DateTime? signingTime,
            byte[]? timestampToken)
        {
            string digestOid = GetDigestOid(digestAlgorithm);

            if (signatureAlgorithmOid == SignatureAlgorithmOids.Ed448)
            {
                // ISO/TS 32002 over RFC 8419, written out: BouncyCastle 2.7.0's generator cannot
                // resolve the SHAKE256 digest its own Ed448 factory names, so the SignerInfo is built here.
                CmsSignedData ed448 = Ed448SignedData(dataToSign, privateKey, certificate, chain, signingTime);
                if (timestampToken != null) ed448 = AddTimestampUnsignedAttribute(ed448, timestampToken);
                return EncodeForContents(ed448);
            }

            CmsAttributeTableGenerator signedAttrs = SignedAttributeGenerator(certificate, signingTime);

            var generator = new CmsSignedDataGenerator();
            if (IsEdDsa(signatureAlgorithmOid))
            {
                // EdDSA (Ed25519/Ed448) has no separate pre-hash digest to pair with an encryption
                // OID, and BouncyCastle 2.7.0 has no reliable string-based route for it: the
                // two-OID overload composes an algorithm NAME from the digest OID and the
                // encryption OID, and for Ed448 that composition falls through to the literal
                // string "SHA512with1.3.101.113" (no registered name for the OID), which
                // Asn1SignatureFactory then rejects as neither a known name nor a valid OID —
                // Ed25519 only avoids this by coincidence of an older registered name. The
                // single-OID "pick the algorithm from the key type" overload fails a different
                // way for BOTH: BouncyCastle 2.7.0's CmsSignedHelper.GetEncOid does not recognise
                // Ed25519PrivateKeyParameters/Ed448PrivateKeyParameters at all. The
                // SignerInfoGeneratorBuilder path below bypasses every one of these lookups: it
                // takes a pre-built ISignatureFactory, so this code names the algorithm ("Ed25519"
                // / "Ed448") itself, the same string TestCertificateGenerator already uses
                // successfully to self-sign a certificate with these keys.
                string signatureName = signatureAlgorithmOid == SignatureAlgorithmOids.Ed448 ? "Ed448" : "Ed25519";
                var signatureFactory = new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
                    signatureName, privateKey, new SecureRandom());
                var signerInfoGenerator = new SignerInfoGeneratorBuilder()
                    .WithSignedAttributeGenerator(signedAttrs)
                    .Build(signatureFactory, certificate);
                generator.AddSignerInfoGenerator(signerInfoGenerator);
            }
            else if (string.IsNullOrEmpty(signatureAlgorithmOid))
            {
                generator.AddSigner(privateKey, certificate, digestOid, signedAttrs, null);
            }
            else
            {
                generator.AddSigner(privateKey, certificate, signatureAlgorithmOid, digestOid, signedAttrs, null);
            }

            var certList = new List<X509Certificate>(chain);
            if (!certList.Contains(certificate))
                certList.Insert(0, certificate);

            var certStore = CollectionUtilities.CreateStore(certList);
            generator.AddCertificates(certStore);

            var content = new CmsProcessableByteArray(dataToSign);
            CmsSignedData signedData = generator.Generate(content, false);

            if (timestampToken != null)
                signedData = AddTimestampUnsignedAttribute(signedData, timestampToken);

            return EncodeForContents(signedData);
        }
    }
}
