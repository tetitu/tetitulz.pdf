// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Unit tests for <see cref="ImageSampleLayout"/> — the single place that decides what the
/// channels of a decoded sample buffer mean.
///
/// <para>One decision, so a four-channel image is not read as CMYK by the plugin decode path and
/// as RGB + alpha by the magic-byte path — the same bytes rendering two different ways depending
/// on which decoder happened to run.</para>
/// </summary>
public sealed class ImageSampleLayoutTests
{
    private static PdfDictionary ImageDict(string colorSpace = null, int? sMaskInData = null)
    {
        var d = new PdfDictionary();
        d[PdfName.Type]    = PdfName.XObject;
        d[PdfName.Subtype] = new PdfName("Image");
        if (colorSpace != null) d[PdfName.ColorSpace] = new PdfName(colorSpace);
        if (sMaskInData.HasValue) d[PdfName.SMaskInData] = new PdfNumber(sMaskInData.Value);
        return d;
    }

    // ── channel interpretation ──────────────────────────────────────────────

    [Fact]
    public void FourChannels_NothingDeclared_ReadAsCmyk()
    {
        var layout = ImageSampleLayout.Resolve(ImageDict(), null, 4, null);

        Assert.Equal(4, layout.ColorComponents);
        Assert.True(layout.IsCmyk);
        Assert.False(layout.HasAlpha);
    }

    [Fact]
    public void FourChannels_DeviceRgbColorSpace_DropTheFourthChannel()
    {
        // /SMaskInData is absent, which the spec defines as "ignore any opacity channel in the
        // encoded data" — so the extra channel is dropped, not read as the K of a CMYK pixel.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB"), null, 4, null);

        Assert.Equal(3, layout.ColorComponents);
        Assert.Equal(4, layout.TotalComponents);
        Assert.False(layout.IsCmyk);
        Assert.False(layout.HasAlpha);
    }

    [Fact]
    public void FourChannels_SMaskInData1_SplitAlphaOffTheEnd()
    {
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", 1), null, 4, null);

        Assert.Equal(3, layout.ColorComponents);
        Assert.Equal(ImageAlphaMode.Straight, layout.Alpha);
        Assert.True(layout.HasAlpha);
    }

    [Fact]
    public void FourChannels_SMaskInData2_ReportPremultipliedAlpha()
    {
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", 2), null, 4, null);

        Assert.Equal(3, layout.ColorComponents);
        Assert.Equal(ImageAlphaMode.Premultiplied, layout.Alpha);
    }

    [Fact]
    public void FourChannels_SMaskInData1_NoColorSpace_StillSplitsRgbPlusAlpha()
    {
        // Nothing says the arity, but the PDF does say an opacity channel is present, so the
        // only reading that honours it is 3 colour + 1 alpha.
        var layout = ImageSampleLayout.Resolve(ImageDict(sMaskInData: 1), null, 4, null);

        Assert.Equal(3, layout.ColorComponents);
        Assert.Equal(ImageAlphaMode.Straight, layout.Alpha);
    }

    [Fact]
    public void FourChannels_DeviceCmykWithSMaskInData0_StaysCmyk()
    {
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceCMYK", 0), null, 4, null);

        Assert.Equal(4, layout.ColorComponents);
        Assert.True(layout.IsCmyk);
        Assert.False(layout.HasAlpha);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    [InlineData(99)]
    public void SMaskInDataOutOfRange_FallsBackToNoAlpha(int value)
    {
        // A malformed value must not be read as "some kind of alpha"; the spec default is 0.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", value), null, 4, null);

        Assert.Equal(ImageAlphaMode.None, layout.Alpha);
        Assert.Equal(3, layout.ColorComponents);
    }

    [Fact]
    public void TwoChannels_ReadAsGreyPlusIgnoredChannel()
    {
        // There is no two-component colour space; two channels is grey + opacity.
        var layout = ImageSampleLayout.Resolve(ImageDict(), null, 2, null);

        Assert.Equal(1, layout.ColorComponents);
        Assert.Equal(2, layout.TotalComponents);
        Assert.False(layout.HasAlpha);
        Assert.True(layout.IsValid);
    }

    [Fact]
    public void TwoChannels_SMaskInData1_ReadAsGreyPlusAlpha()
    {
        var layout = ImageSampleLayout.Resolve(ImageDict(sMaskInData: 1), null, 2, null);

        Assert.Equal(1, layout.ColorComponents);
        Assert.Equal(ImageAlphaMode.Straight, layout.Alpha);
    }

    [Fact]
    public void ZeroChannels_IsNotValid()
    {
        Assert.False(ImageSampleLayout.Resolve(ImageDict(), null, 0, null).IsValid);
    }

    [Fact]
    public void FiveChannels_NothingDeclared_IsNotValid()
    {
        // No colour space this maps to — the caller must fall through to another decoder
        // rather than render five channels as something invented.
        Assert.False(ImageSampleLayout.Resolve(ImageDict(), null, 5, null).IsValid);
    }

    [Fact]
    public void FourChannels_Jp2HeaderSaysSRgb_ReadAsRgbNotCmyk()
    {
        // The dictionary declares nothing; the codestream's own colr box does. Without this the
        // buffer would be read as CMYK and the photo would render in wrong colours.
        byte[] jp2 = Jp2Headers.WithEnumeratedColourSpace(16); // sRGB

        var layout = ImageSampleLayout.Resolve(ImageDict(), null, 4, jp2);

        Assert.Equal(3, layout.ColorComponents);
        Assert.False(layout.IsCmyk);
    }

    [Fact]
    public void FourChannels_DeclaredColorSpaceOverridesTheJp2Header()
    {
        // ISO 32000-1 §7.4.9: /ColorSpace overrides the colour space in the JPX data.
        byte[] jp2 = Jp2Headers.WithEnumeratedColourSpace(16); // sRGB

        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceCMYK"), null, 4, jp2);

        Assert.Equal(4, layout.ColorComponents);
        Assert.True(layout.IsCmyk);
    }

    // ── sample → bitmap ─────────────────────────────────────────────────────

    [Fact]
    public void BuildBitmap_Grey_ExpandsToRgb()
    {
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceGray"), null, 1, null);
        using var bmp = layout.BuildBitmap(new byte[] { 0x40 }, 1, 1, SKAlphaType.Premul);

        Assert.NotNull(bmp);
        Assert.Equal(new SKColor(0x40, 0x40, 0x40, 255), bmp.GetPixel(0, 0));
        Assert.Equal(SKAlphaType.Opaque, bmp.AlphaType);
    }

    [Fact]
    public void BuildBitmap_Cmyk_ConvertsThroughPdfColor()
    {
        // Pure cyan: C=1 M=0 Y=0 K=0 → (1-c)(1-k) = 0 red, full green and blue.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceCMYK"), null, 4, null);
        using var bmp = layout.BuildBitmap(new byte[] { 255, 0, 0, 0 }, 1, 1, SKAlphaType.Premul);

        Assert.NotNull(bmp);
        var c = bmp.GetPixel(0, 0);
        Assert.Equal(0, c.Red);
        Assert.True(c.Green > 250, $"green was {c.Green}");
        Assert.True(c.Blue  > 250, $"blue was {c.Blue}");
    }

    [Fact]
    public void BuildBitmap_StraightAlpha_KeepsTheTransparency()
    {
        // Two pixels: opaque red, then fully transparent. An Opaque bitmap would make Skia
        // ignore the stored alpha entirely, so the alpha type matters as much as the bytes.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", 1), null, 4, null);
        byte[] samples = { 255, 0, 0, 255,   0, 0, 255, 0 };

        using var bmp = layout.BuildBitmap(samples, 2, 1, SKAlphaType.Unpremul);

        Assert.NotNull(bmp);
        Assert.NotEqual(SKAlphaType.Opaque, bmp.AlphaType);
        Assert.Equal(255, bmp.GetPixel(0, 0).Alpha);
        Assert.Equal(255, bmp.GetPixel(0, 0).Red);
        Assert.Equal(0,   bmp.GetPixel(1, 0).Alpha);
    }

    [Fact]
    public void BuildBitmap_PremultipliedAlpha_RecoversStraightColour()
    {
        // Half-transparent full red stored premultiplied is (128,0,0,128); straight it is
        // (255,0,0,128). Writing the premultiplied value into an Unpremul surface unchanged
        // would render the pixel at half brightness.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", 2), null, 4, null);
        byte[] samples = { 128, 0, 0, 128 };

        using var bmp = layout.BuildBitmap(samples, 1, 1, SKAlphaType.Unpremul);

        Assert.NotNull(bmp);
        var c = bmp.GetPixel(0, 0);
        Assert.Equal(128, c.Alpha);
        Assert.True(c.Red >= 250, $"expected the straight colour ~255, got {c.Red}");
    }

    [Fact]
    public void BuildBitmap_BufferShorterThanGeometry_ReturnsNull()
    {
        // Codec output is untrusted: a short buffer must be refused, not read past its end.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB"), null, 3, null);

        Assert.Null(layout.BuildBitmap(new byte[] { 1, 2, 3 }, 4, 4, SKAlphaType.Premul));
    }

    [Fact]
    public void BuildBitmap_AbsurdGeometry_ReturnsNull()
    {
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB"), null, 3, null);

        Assert.Null(layout.BuildBitmap(new byte[3], int.MaxValue, int.MaxValue, SKAlphaType.Premul));
    }

    // ── what the samples look like in memory ────────────────────────────────
    //
    // BuildBitmap writes into the bitmap's own buffer rather than building an SKColor[] and
    // assigning SKBitmap.Pixels. The three tests below pin the conversions that setter was
    // doing, because they are exactly what a future simplification of the loop would drop —
    // and dropping either changes pixels silently. They read the raw buffer rather than
    // GetPixel, which un-premultiplies on the way out and so cannot see the difference.

    [Fact]
    public void BuildBitmap_PremulSurface_StoresPremultipliedSamples()
    {
        // Skia's SkMulDiv255Round, not a truncating (v * a / 255) — and the values are chosen so
        // the two disagree: 200x200/255 is 156.86 and 150x200/255 is 117.65, so truncation would
        // store 156 and 117 where Skia stores 157 and 118.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", 1), null, 4, null);
        byte[] samples = { 200, 150, 100, 200 };

        using var bmp = layout.BuildBitmap(samples, 1, 1, SKAlphaType.Premul);

        Assert.NotNull(bmp);
        Assert.Equal(SKAlphaType.Premul, bmp.AlphaType);
        var stored = bmp.GetPixelSpan();
        Assert.Equal(new byte[] { 157, 118, 78, 200 }, stored.Slice(0, 4).ToArray());
    }

    [Fact]
    public void BuildBitmap_UnpremulSurface_StoresStraightSamples()
    {
        // The same input on the SVG renderer's surface, which declares straight alpha: applying
        // alpha here would darken every semi-transparent pixel of an exported image.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", 1), null, 4, null);
        byte[] samples = { 200, 150, 100, 200 };

        using var bmp = layout.BuildBitmap(samples, 1, 1, SKAlphaType.Unpremul);

        Assert.NotNull(bmp);
        Assert.Equal(SKAlphaType.Unpremul, bmp.AlphaType);
        var stored = bmp.GetPixelSpan();
        Assert.Equal(new byte[] { 200, 150, 100, 200 }, stored.Slice(0, 4).ToArray());
    }

    [Fact]
    public void BuildBitmap_FullyTransparentPixel_StoresTransparentBlack()
    {
        // An invisible pixel renders identically whatever its colour channels hold, so this is
        // not about appearance: keeping the decoded colour would turn a uniform transparent
        // region into noise, and the SVG renderer PNG-encodes this bitmap.
        var layout = ImageSampleLayout.Resolve(ImageDict("DeviceRGB", 1), null, 4, null);
        byte[] samples = { 205, 28, 109, 0 };

        using var bmp = layout.BuildBitmap(samples, 1, 1, SKAlphaType.Unpremul);

        Assert.NotNull(bmp);
        var stored = bmp.GetPixelSpan();
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, stored.Slice(0, 4).ToArray());
    }
}
