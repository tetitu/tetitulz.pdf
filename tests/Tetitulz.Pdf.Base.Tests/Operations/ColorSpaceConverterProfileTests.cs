// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>PdfColorSpaceConverter.ConvertRgbToCmyk</c> through a destination profile — the pixels it
/// writes, end to end.
/// </summary>
/// <remarks>
/// <para><b>The converter is <c>IccOutputTransform</c>'s second consumer</b>, after the separation
/// plates: the perceptual table, black-point compensation on, white kept white — a common viewer's and
/// the reference renderer's defaults. <c>B2A0</c> evaluated raw, with neither, would put 100 % K under
/// sRGB black for a press profile whose black is L* 20, and ink under sRGB white wherever its table
/// does.</para>
///
/// <para><b>Each claim beside the answer it rules out.</b> The compensated black is asserted
/// against the uncompensated one, and white's empty inks against a near-white pixel that the same
/// table does ink, so neither holds for an image the converter left alone.</para>
/// </remarks>
public class ColorSpaceConverterProfileTests
{
    /// <summary>sRGB black lands on the profile's own black: less K than the table's answer for L* 0.</summary>
    [Fact]
    public void BlackIsBlackPointCompensatedThroughTheProfile()
    {
        byte[] profile = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.KFromLightness(),
            a2b: SyntheticCmykProfile.LightnessFromKWithToe());

        byte[] cmyk = ConvertOnePixel(profile, 0, 0, 0);

        Assert.Equal(Expected(profile, SKColors.Black, compensated: true)[3], cmyk[3]);
        byte uncompensated = Expected(profile, SKColors.Black, compensated: false)[3];
        Assert.Equal(255, uncompensated);
        Assert.True(cmyk[3] < uncompensated - 5, $"compensated K {cmyk[3]} should sit below {uncompensated}");
    }

    /// <summary>sRGB white takes no ink, though the table inks every colour (the reference colour engine's white fix-up).</summary>
    [Fact]
    public void WhiteTakesNoInkThroughTheProfile()
    {
        byte[] profile = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.ConstantCyan(0x8000));

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, ConvertOnePixel(profile, 255, 255, 255));
        Assert.Equal(128, ConvertOnePixel(profile, 254, 254, 254)[0]);   // the table's cyan
    }

    /// <summary>
    /// A valid output profile over RGB is no CMYK destination: the image converts by the formula,
    /// as without a profile, and the warning says why.
    /// </summary>
    /// <remarks>
    /// The transform answers three channels for it; taking those as C, M and Y would write K = 0
    /// under every colour and present the result as profile-managed.
    /// </remarks>
    [Fact]
    public void AnRgbDestinationProfileFallsBackToTheFormula()
    {
        byte[] rgbProfile = IccB2AConversionTests.BuildB2AProfile(
            "RGB ", inputs: 3, outputs: 3, grid: 2, clut: new int[2 * 2 * 2 * 3], type: "mft2");
        byte[] pdf = ImageFixtures.OneImagePdf(new byte[] { 40, 90, 160 }, 1, 1);

        using var managed = new MemoryStream();
        Assert.Equal(1, PdfColorSpaceConverter.ConvertRgbToCmyk(new MemoryStream(pdf), managed, rgbProfile, gcrFactor: 0.8));
        string warning = PdfColorSpaceConverter.LastConversionWarning;
        Assert.Contains("3 components, not 4", warning, System.StringComparison.Ordinal);

        using var formula = new MemoryStream();
        Assert.Equal(1, PdfColorSpaceConverter.ConvertRgbToCmyk(new MemoryStream(pdf), formula, gcrFactor: 0.8));

        Assert.Equal(Samples(formula), Samples(managed));
        Assert.NotEqual(0, Samples(managed)[3]);   // the formula inks K for this colour
    }

    private static byte[] Samples(MemoryStream output)
    {
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        return Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5)).GetDecodedData();
    }

    private static byte[] Expected(byte[] profile, SKColor colour, bool compensated)
    {
        var transform = IccOutputTransform.TryCreate(profile, IccRenderingIntent.Perceptual, compensated, out string reason);
        Assert.True(transform != null, reason);
        var device = new float[4];
        Assert.True(transform.TryConvert(colour, device));
        return new[] { PdfColor.Channel(device[0]), PdfColor.Channel(device[1]), PdfColor.Channel(device[2]), PdfColor.Channel(device[3]) };
    }

    private static byte[] ConvertOnePixel(byte[] profile, byte r, byte g, byte b)
    {
        byte[] pdf = ImageFixtures.OneImagePdf(new[] { r, g, b }, 1, 1);
        using var output = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToCmyk(new MemoryStream(pdf), output, profile, gcrFactor: 0.8);
        Assert.Equal(1, converted);
        Assert.Null(PdfColorSpaceConverter.LastConversionWarning);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        return image.GetDecodedData();
    }
}
