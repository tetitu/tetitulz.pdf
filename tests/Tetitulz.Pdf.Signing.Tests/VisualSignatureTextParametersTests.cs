// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="VisualSignatureBuilder.Sign(Stream, Stream, SignatureAppearanceOptions, SignatureParameters, int, float, float)"/>:
/// a visible signature that draws text, under the caller's own parameters — an external signer (a
/// smart card's shape) or a certificate stream — where the text overloads before it took a
/// certificate file alone.
/// </summary>
public sealed class VisualSignatureTextParametersTests
{
    private static readonly Lazy<(SigningTestCrypto.Issued Leaf, SigningTestCrypto.Issued Root)> Keys = new(() =>
    {
        var root = SigningTestCrypto.Ca();
        return (SigningTestCrypto.Leaf(root), root);
    });

    private static VisualSignatureImageParametersTests.CountingSigner Signer()
        => new(Keys.Value.Leaf, Keys.Value.Root.Cert);

    [Fact]
    public void ATextBlock_UnderAnExternalSigner_IsDrawn_AndTheSignatureVerifies()
    {
        var signer = Signer();
        byte[] signed = Sign(
            new SignatureAppearanceOptions { SignerName = "Ada Lovelace", SigningDate = "2026-11-11", Reason = "Drawn reason", Width = 180, Height = 50 },
            new SignatureParameters { ExternalSigner = signer, Reason = "Approved", Location = "Haifa" });

        Assert.Equal(1, signer.Calls);
        var result = Verify(signed);
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        // The dictionary's reason is the parameters'; the drawn one is the appearance's.
        Assert.Equal("Approved", result.Reason);
        Assert.Equal("Haifa", result.Location);

        string content = VisualSignatureTextAppearanceTests.AppearanceContent(signed);
        Assert.Contains("(Ada Lovelace) Tj", content, StringComparison.Ordinal);
        Assert.Contains("Drawn reason", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved", content, StringComparison.Ordinal);
        // The rectangle is the place asked for and the appearance's own size.
        Assert.Matches(@"/Rect \[50\.00 60\.00 230\.00 110\.00\]", Encoding.Latin1.GetString(signed));
    }

    [Fact]
    public void ACertificateStreamInTheParameters_IsUsed()
    {
        var (pfx, password) = SigningTestCrypto.Pfx(Keys.Value.Leaf, Keys.Value.Root.Cert);
        byte[] signed = Sign(
            new SignatureAppearanceOptions { SignerName = "Ada Lovelace", Width = 180, Height = 50 },
            new SignatureParameters { CertificateStream = new MemoryStream(pfx), CertificatePassword = password });

        var result = Verify(signed);
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.Contains("(Ada Lovelace) Tj", VisualSignatureTextAppearanceTests.AppearanceContent(signed), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignatureNamesTheFieldTheWidgetWasWrittenFor()
    {
        var parameters = new SignatureParameters { ExternalSigner = Signer(), SignatureFieldName = "CallersOwnName" };
        byte[] signed = Sign(new SignatureAppearanceOptions { SignerName = "Ada Lovelace", Width = 180, Height = 50 }, parameters);

        Assert.StartsWith("VisualSig", parameters.SignatureFieldName, StringComparison.Ordinal);
        Assert.Equal(parameters.SignatureFieldName, Verify(signed).FieldName);
    }

    [Theory]
    [InlineData(double.NaN, 50.0, 50f, 60f)]
    [InlineData(180.0, 0.0, 50f, 60f)]
    [InlineData(180.0, double.PositiveInfinity, 50f, 60f)]
    [InlineData(180.0, 50.0, float.NaN, 60f)]
    [InlineData(180.0, 50.0, 50f, float.NegativeInfinity)]
    public void ASizeOrAPlaceThatIsNotANumber_IsRefused_BeforeAnythingIsSignedOrWritten(double width, double height, float x, float y)
    {
        var signer = Signer();
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();

        Assert.ThrowsAny<ArgumentException>(() => VisualSignatureBuilder.Sign(input, output,
            new SignatureAppearanceOptions { SignerName = "Ada Lovelace", Width = width, Height = height },
            new SignatureParameters { ExternalSigner = signer }, 1, x, y));

        Assert.Equal(0, signer.Calls);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// Text no font here can draw is a refusal, not a blank block with a valid signature under it:
    /// Hebrew, with no font provider and no system fonts allowed.
    /// </summary>
    [Fact]
    public void ABlockThatCannotBeDrawn_IsRefused_AndNothingIsSigned()
    {
        var signer = Signer();
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();

        Assert.Throws<PdfException>(() => VisualSignatureBuilder.Sign(input, output,
            new SignatureAppearanceOptions { SignerName = "שלום", Width = 180, Height = 50 },
            new SignatureParameters { ExternalSigner = signer }, 1, 50, 60));

        Assert.Equal(0, signer.Calls);
        Assert.Equal(0, output.Length);
    }

    private static byte[] Sign(SignatureAppearanceOptions appearance, SignatureParameters parameters)
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        VisualSignatureBuilder.Sign(input, output, appearance, parameters, pageNumber: 1, x: 50, y: 60);
        return output.ToArray();
    }

    private static SignatureVerificationResult Verify(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { Keys.Value.Root.Cert },
        }).Single();
    }
}
