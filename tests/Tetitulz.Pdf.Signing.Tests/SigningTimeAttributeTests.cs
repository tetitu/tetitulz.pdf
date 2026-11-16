// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Cms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The time of signing is claimed once: a PAdES signature's CMS carries no <c>signing-time</c>
/// attribute, and any other signature's carries the instant its <c>/M</c> claims.
/// </summary>
/// <remarks>
/// <para>ETSI EN 319 142-1 §6.3, Table 1 (V1.1.1 and V1.2.1): at every PAdES baseline level the
/// <c>/M</c> entry "shall be present" and the signing-time attribute "shall not be present"; ISO
/// 32000-2 §12.8.3.4.2 allows one of the two, "but not both". The signer wrote both, the attribute
/// being BouncyCastle's default of the current time, so a signature whose
/// <see cref="SignatureParameters.SigningTime"/> was set also claimed a second, different time.</para>
/// <para>Every door is asked: <see cref="PdfSigner.Sign(Stream, Stream, SignatureParameters)"/>, the
/// planned form, and the deferred builder over a <see cref="PreparedSignature"/>. Each PAdES
/// assertion has a <c>adbe.pkcs7.detached</c> control that finds the attribute, so "absent" is not
/// an empty table answering.</para>
/// </remarks>
public sealed class SigningTimeAttributeTests
{
    private static readonly DateTime Claimed = new(2025, 11, 2, 9, 30, 15, DateTimeKind.Utc);

    [Fact]
    public void APadesSignature_CarriesNoSigningTimeAttribute_AndClaimsItsTimeInM()
    {
        byte[] signed = Sign(pades: true, Claimed);

        SignerInformation signer = OnlySigner(signed, out string subFilter, out string m);
        Assert.Equal("ETSI.CAdES.detached", subFilter);
        Assert.NotNull(signer.SignedAttributes[CmsAttributes.MessageDigest]);
        Assert.Null(signer.SignedAttributes[CmsAttributes.SigningTime]);
        Assert.Equal("D:20251102093015Z", m);
        Assert.True(Verify(signed).IsIntegrityValid);
    }

    [Fact]
    public void APkcs7Signature_CarriesTheInstantItsMClaims()
    {
        byte[] signed = Sign(pades: false, Claimed);

        SignerInformation signer = OnlySigner(signed, out string subFilter, out string m);
        Assert.Equal("adbe.pkcs7.detached", subFilter);
        Assert.Equal(Claimed, SigningTimeOf(signer));
        Assert.Equal("D:20251102093015Z", m);
        Assert.True(Verify(signed).IsIntegrityValid);
    }

    /// <summary>
    /// With no time given both are "now", and they are one now: the attribute is not minted again
    /// inside BouncyCastle, a moment after <c>/M</c> was written.
    /// </summary>
    [Fact]
    public void APkcs7SignatureWithNoTimeGiven_ClaimsOneInstantInBothPlaces()
    {
        DateTime before = DateTime.UtcNow.AddSeconds(-1);
        byte[] signed = Sign(pades: false, signingTime: null);

        SignerInformation signer = OnlySigner(signed, out _, out string m);
        DateTime attribute = SigningTimeOf(signer);
        Assert.Equal(PdfDate.TryParse(m)!.Value.UtcDateTime, attribute);
        Assert.True(attribute >= before.AddTicks(-(before.Ticks % TimeSpan.TicksPerSecond)));
    }

    /// <summary>
    /// A local time is the same instant in both places, converted rather than relabelled. (On a host
    /// whose zone is UTC the two kinds coincide and this cannot fail.)
    /// </summary>
    [Fact]
    public void APkcs7SignatureGivenALocalTime_ClaimsTheSameUtcInstantInBoth()
    {
        byte[] signed = Sign(pades: false, Claimed.ToLocalTime());

        SignerInformation signer = OnlySigner(signed, out _, out string m);
        Assert.Equal("D:20251102093015Z", m);
        Assert.Equal(Claimed, SigningTimeOf(signer));
    }

    /// <summary>
    /// <c>/M</c> is written to the second, and so is the attribute — also past 2049, where the
    /// attribute is a GeneralizedTime, which can carry a fraction.
    /// </summary>
    [Fact]
    public void APkcs7SignatureClaimsItsTimeToTheSecond_AsMDoes()
    {
        var past2049 = new DateTime(2051, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        byte[] signed = Sign(pades: false, past2049.AddMilliseconds(700));

        SignerInformation signer = OnlySigner(signed, out _, out string m);
        Assert.Equal("D:20510506070809Z", m);
        Assert.Equal(past2049, SigningTimeOf(signer));
    }

    /// <summary>
    /// The planned form builds its CMS at <see cref="PdfSigner.Complete"/>, after the dictionary was
    /// written at <see cref="PdfSigner.Plan"/>; the time reaches it through the plan.
    /// </summary>
    [Fact]
    public void APlannedSignature_FollowsTheSameRule()
    {
        byte[] pades = SignPlanned(pades: true, Claimed);
        Assert.Null(OnlySigner(pades, out string padesSubFilter, out _).SignedAttributes[CmsAttributes.SigningTime]);
        Assert.Equal("ETSI.CAdES.detached", padesSubFilter);

        byte[] pkcs7 = SignPlanned(pades: false, Claimed);
        Assert.Equal(Claimed, SigningTimeOf(OnlySigner(pkcs7, out _, out _)));
        Assert.True(Verify(pades).IsIntegrityValid);
        Assert.True(Verify(pkcs7).IsIntegrityValid);
    }

    /// <summary>
    /// A deferred signature's attributes are built away from the document, so the prepared
    /// signature reports the <c>/SubFilter</c> and <c>/M</c> it was written with and the builder
    /// takes them from there.
    /// </summary>
    [Fact]
    public void APreparedSignature_ReportsItsSubFilterAndTime_AndItsAttributesFollowTheRule()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = LeafValidAtClaimed(ca);

        PreparedSignature pades = Prepare(leaf, ca, pades: true);
        Assert.Equal("ETSI.CAdES.detached", pades.SubFilter);
        Assert.Equal(Claimed, pades.SigningTime!.Value.UtcDateTime);
        AttributeTable padesAttributes = Table(CmsDeferredSignatureBuilder.GetSignedAttributes(pades, leaf.Cert));
        Assert.NotNull(padesAttributes[CmsAttributes.MessageDigest]);
        Assert.Null(padesAttributes[CmsAttributes.SigningTime]);

        PreparedSignature pkcs7 = Prepare(leaf, ca, pades: false);
        Assert.Equal("adbe.pkcs7.detached", pkcs7.SubFilter);
        AttributeTable pkcs7Attributes = Table(CmsDeferredSignatureBuilder.GetSignedAttributes(pkcs7, leaf.Cert));
        Assert.Equal(Claimed, Time.GetInstance(pkcs7Attributes[CmsAttributes.SigningTime].AttrValues[0]).ToDateTime());
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A signer whose certificate was valid at <see cref="Claimed"/>: BouncyCastle checks the
    /// certificate at the CMS's signing time, so one issued yesterday would not verify at it.
    /// </summary>
    private static SigningTestCrypto.Issued LeafValidAtClaimed(SigningTestCrypto.Issued ca)
        => SigningTestCrypto.Leaf(ca, notBefore: Claimed.AddYears(-1), notAfter: DateTime.UtcNow.AddYears(5));

    private static byte[] Sign(bool pades, DateTime? signingTime)
    {
        var ca = SigningTestCrypto.Ca();
        var (pfx, pw) = SigningTestCrypto.Pfx(LeafValidAtClaimed(ca), ca.Cert);
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, Parameters(pfx, pw, pades, signingTime));
        return output.ToArray();
    }

    private static byte[] SignPlanned(bool pades, DateTime signingTime)
    {
        var ca = SigningTestCrypto.Ca();
        var (pfx, pw) = SigningTestCrypto.Pfx(LeafValidAtClaimed(ca), ca.Cert);
        byte[] pdf = SignedPdfFixtures.MinimalPdf();

        var signer = new PdfSigner();
        var appended = new MemoryStream();
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        {
            SignaturePlan plan = signer.Plan(doc, 0, Parameters(pfx, pw, pades, signingTime));
            IncrementalUpdateWriter.WriteUpdate(pdf, appended, plan.Objects,
                new IncrementalUpdateOptions { Document = doc, Size = plan.NextObjectNumber });
            signer.Complete(plan, appended);
        }
        return appended.ToArray();
    }

    private static PreparedSignature Prepare(SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued ca, bool pades)
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        return PdfDeferredSigner.Prepare(input, output,
            new[] { leaf.Cert.GetEncoded(), ca.Cert.GetEncoded() },
            new SignatureParameters
            {
                EstimatedSignatureSize = 12_000,
                SigningTime = Claimed,
                UseEtsiPadesSubFilter = pades,
            });
    }

    private static SignatureParameters Parameters(byte[] pfx, string pw, bool pades, DateTime? signingTime)
        => new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
            SigningTime = signingTime,
            UseEtsiPadesSubFilter = pades,
        };

    private static SignerInformation OnlySigner(byte[] pdf, out string subFilter, out string m)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var field = doc.AcroForm!.FindSignatureField("Signature1");
        Assert.NotNull(field);
        subFilter = field!.SubFilter;
        m = field.SigningTime;
        byte[] cms = SigningDefaults.TrimToAsn1Object(field.SignatureValue!.GetString(PdfName.Contents)!.RawBytes);
        return new CmsSignedData(cms).GetSignerInfos().GetSigners().Cast<SignerInformation>().Single();
    }

    private static DateTime SigningTimeOf(SignerInformation signer)
    {
        var attribute = signer.SignedAttributes[CmsAttributes.SigningTime];
        Assert.NotNull(attribute);
        return Time.GetInstance(attribute.AttrValues[0]).ToDateTime();
    }

    private static AttributeTable Table(byte[] der)
        => new AttributeTable(Asn1Set.GetInstance(Asn1Object.FromByteArray(der)));

    private static SignatureVerificationResult Verify(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return new PdfSignatureVerifier().VerifyAll(doc).Single();
    }
}
