// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Security;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// ISO/TS 32004 §6.5.2 — the PDF MAC token affixed to a signature, whose
/// <c>PdfMacIntegrityInfo</c> carries a <c>signatureDigest</c> the standalone token does not.
/// </summary>
/// <remarks>
/// <para><b>Why the signed case needs its own placement at all.</b> §6.5.2: putting the MAC in an
/// <c>AuthCode</c> dictionary "affects signature coverage" — the token would cover the signature's
/// own placeholders and stop verifying the moment the CMS is written into them, while the
/// signature's <c>/ByteRange</c> covers the token. Affixing it as an <i>unsigned</i> attribute
/// breaks the circularity, because unsigned attributes are outside what the signature signs.</para>
///
/// <para><b>The structure is decoded, not compared.</b> Every assertion below takes the token apart
/// with BouncyCastle's own ASN.1 reader and checks the field against what §6.2 says it must be —
/// rather than against a second copy of the builder's own construction, which would agree with a
/// mistake. The tagging in particular is asserted explicitly: §6.2 makes <c>signatureDigest</c>
/// <c>[0] IMPLICIT</c>, and an EXPLICIT encoding would decode as a <i>wrapper around</i> an octet
/// string instead of one — a token that looks plausible and verifies nowhere.</para>
/// </remarks>
public class PdfMacAttachedToSigTokenTests
{
    private static byte[] Bytes(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    /// <summary>The encapsulated <c>PdfMacIntegrityInfo</c>, decoded out of a built token.</summary>
    private static Asn1Sequence IntegrityInfoOf(byte[] tokenBytes)
    {
        var contentInfo = ContentInfo.GetInstance(Asn1Object.FromByteArray(tokenBytes));
        var authData = AuthenticatedData.GetInstance(contentInfo.Content);

        var eContent = (Asn1OctetString)authData.EncapsulatedContentInfo.Content;
        return Asn1Sequence.GetInstance(Asn1Object.FromByteArray(eContent.GetOctets()));
    }

    /// <summary>
    /// §6.2 and §6.6.3: version 0, the data digest, and the signature digest as <c>[0] IMPLICIT</c>.
    /// </summary>
    [Fact]
    public void AttachedToken_CarriesVersionDataDigestAndAnImplicitlyTaggedSignatureDigest()
    {
        byte[] dataDigest = Bytes(0xAA);
        byte[] signatureDigest = Bytes(0xBB);

        Asn1Sequence info = IntegrityInfoOf(PdfMacToken.BuildAttachedToSigToken(
            Bytes(0x01), Bytes(0x02), dataDigest, signatureDigest, new SecureRandom()));

        Assert.Equal(3, info.Count);
        Assert.Equal(0, DerInteger.GetInstance(info[0]).Value.IntValue);
        Assert.Equal(dataDigest, Asn1OctetString.GetInstance(info[1]).GetOctets());

        var tagged = Asn1TaggedObject.GetInstance(info[2]);
        Assert.Equal(0, tagged.TagNo);

        // ASSERTED ON THE ENCODED BYTES, because the object model cannot tell the two apart.
        // BouncyCastle's GetInstance(tagged, declaredExplicit: false) returns the same octet string
        // whether the DER was implicit or explicit — a mutation flipping IMPLICIT to EXPLICIT
        // survived that assertion. The difference is only in the identifier octet: [0] IMPLICIT
        // OCTET STRING is PRIMITIVE context-specific 0 (0x80), while [0] EXPLICIT is CONSTRUCTED
        // (0xA0) wrapping a whole OCTET STRING. §6.2 specifies the former.
        byte[] encoded = tagged.GetEncoded();
        Assert.Equal(0x80, encoded[0]);
        Assert.Equal(signatureDigest.Length, encoded[1]);
        Assert.Equal(signatureDigest, encoded.Skip(2).ToArray());
    }

    /// <summary>
    /// The standalone token omits the field entirely — §6.6.2 — which is the one structural
    /// difference between the two placements.
    /// </summary>
    [Fact]
    public void StandaloneToken_OmitsTheSignatureDigest()
    {
        Asn1Sequence info = IntegrityInfoOf(
            PdfMacToken.BuildStandaloneToken(Bytes(0x01), Bytes(0x02), Bytes(0xAA), new SecureRandom()));

        Assert.Equal(2, info.Count);
    }

    /// <summary>
    /// Everything else about the token is the same construction. Asserted because the two entry
    /// points share one private builder: if they stopped doing so, this is what would notice.
    /// </summary>
    [Fact]
    public void BothPlacements_ShareTheSameCmsShape()
    {
        byte[] attached = PdfMacToken.BuildAttachedToSigToken(
            Bytes(0x01), Bytes(0x02), Bytes(0xAA), Bytes(0xBB), new SecureRandom());
        byte[] standalone = PdfMacToken.BuildStandaloneToken(
            Bytes(0x01), Bytes(0x02), Bytes(0xAA), new SecureRandom());

        AuthenticatedData a = AuthenticatedData.GetInstance(
            ContentInfo.GetInstance(Asn1Object.FromByteArray(attached)).Content);
        AuthenticatedData s = AuthenticatedData.GetInstance(
            ContentInfo.GetInstance(Asn1Object.FromByteArray(standalone)).Content);

        Assert.Equal(s.MacAlgorithm.Algorithm, a.MacAlgorithm.Algorithm);
        Assert.Equal(s.DigestAlgorithm.Algorithm, a.DigestAlgorithm.Algorithm);
        Assert.Equal(s.EncapsulatedContentInfo.ContentType, a.EncapsulatedContentInfo.ContentType);
        Assert.Equal(s.AuthAttrs.Count, a.AuthAttrs.Count);
    }

    /// <summary>
    /// A signature digest of the wrong size is refused rather than padded or truncated — a MAC over
    /// the wrong number of bytes is a token that attests to nothing.
    /// </summary>
    [Fact]
    public void AttachedToken_RefusesASignatureDigestOfTheWrongSize()
    {
        Assert.Throws<ArgumentException>(() => PdfMacToken.BuildAttachedToSigToken(
            Bytes(0x01), Bytes(0x02), Bytes(0xAA), new byte[16], new SecureRandom()));

        Assert.Throws<ArgumentException>(() => PdfMacToken.BuildAttachedToSigToken(
            Bytes(0x01), Bytes(0x02), Bytes(0xAA), null, new SecureRandom()));
    }

    /// <summary>
    /// §6.5.2's attribute identifier, pinned as a literal so a transcription slip in the OID cannot
    /// pass — the arc is <c>iso(1) standard(0) iso32004(32004) pdfmac(1) 2</c>.
    /// </summary>
    [Fact]
    public void TheUnsignedAttributeOidIsTheOneTheStandardMints()
    {
        Assert.Equal("1.0.32004.1.2", PdfMacToken.IdAttrPdfMacData);
    }
}
