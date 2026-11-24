// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The four capabilities added on top of basic replacement: an alpha channel written as a
/// <c>/SMask</c>, Adobe CMYK JPEG input, <see cref="ImageFit.Contain"/> letterboxing, and
/// document-wide selection by object number (which reaches inside form XObjects).
///
/// <para>Assertions are on decoded pixels or on the re-parsed output object — never on "a
/// non-empty PDF came back", which every one of these would produce with the feature deleted.</para>
/// </summary>
public sealed class PdfImageReplacerAdvancedTests
{
    private const int Dpi = 72;

    // ================================================================
    //  Helpers
    // ================================================================

    private static byte[] Replace(byte[] pdf, ReplacementImage image,
        ImageReplacementOptions? options, out ImageReplacementResult result)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        result = PdfImageReplacer.Replace(input, output, 0, 0, image, options);
        return output.ToArray();
    }

    private static byte[] ReplaceByObject(byte[] pdf, int objectNumber, ReplacementImage image,
        out ImageReplacementResult result)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        result = PdfImageReplacer.ReplaceByObjectNumber(input, output, objectNumber, image);
        return output.ToArray();
    }

    private static SKColor PixelAt(byte[] pdf, double fx, double fy)
    {
        using var bmp = PixelAssert.Render(pdf, Dpi);
        return bmp.GetPixel((int)(bmp.Width * fx), (int)(bmp.Height * fy));
    }

    private static SKColor CenterPixel(byte[] pdf) => PixelAt(pdf, 0.5, 0.5);

    private static PdfStream ImageObject(byte[] pdf, int objectNumber)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var stream = doc.ResolveObject(new PdfReference(objectNumber, 0)) as PdfStream;
        Assert.NotNull(stream);
        return stream!;
    }

    private static void AssertIsBlue(SKColor c, string what) =>
        Assert.True(c.Blue > 200 && c.Red < 80 && c.Green < 80, $"{what}: expected blue, got {c}");

    private static void AssertIsWhite(SKColor c, string what) =>
        Assert.True(c.Red > 200 && c.Green > 200 && c.Blue > 200, $"{what}: expected white, got {c}");

    // ================================================================
    //  1. Alpha — the replacement carries its own /SMask
    // ================================================================

    [Fact]
    public void Rgba32_HalfTransparentReplacement_BlendsTowardsThePageBackground()
    {
        // Opaque red original on a white page; replaced by blue at 50% alpha. A dropped alpha
        // channel would give saturated blue, so the assertion is on the blend, not on "it's blue".
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        byte[] replaced = Replace(pdf,
            ReplacementImage.Rgba32(ImageFixtures.SolidRgba(1, 1, 0, 0, 255, 128), 1, 1),
            null, out var result);

        Assert.True(result.SoftMaskObjectNumber > 0, "expected a soft-mask object to be written");
        Assert.NotEqual(result.ObjectNumber, result.SoftMaskObjectNumber);

        SKColor c = CenterPixel(replaced);
        Assert.True(c.Blue > 150, $"blue channel should dominate, got {c}");
        Assert.True(c.Red > 80 && c.Red < 200,
            $"50% alpha over a white page should leave the red channel mid-range, got {c}");
    }

    [Fact]
    public void Rgba32_FullyTransparentReplacement_LeavesThePageBlank()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        byte[] replaced = Replace(pdf,
            ReplacementImage.Rgba32(ImageFixtures.SolidRgba(1, 1, 0, 0, 255, 0), 1, 1),
            null, out _);

        AssertIsWhite(CenterPixel(replaced), "alpha 0 must hide the replacement entirely");
    }

    [Fact]
    public void Rgba32_OpaqueAlpha_WritesNoSoftMaskAtAll()
    {
        // A mask of all-255 has no effect, so writing one would only bloat the file.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        byte[] replaced = Replace(pdf,
            ReplacementImage.Rgba32(ImageFixtures.SolidRgba(1, 1, 0, 0, 255, 255), 1, 1),
            null, out var result);

        Assert.Equal(0, result.SoftMaskObjectNumber);
        Assert.Null(ImageObject(replaced, result.ObjectNumber)[PdfName.SMask]);
        AssertIsBlue(CenterPixel(replaced), "opaque RGBA replacement");
    }

    [Fact]
    public void WithSoftMask_WritesAConformingDeviceGrayMaskObject()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        var image = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(2, 2, 0, 0, 255), 2, 2)
                                    .WithSoftMask(new byte[] { 0, 64, 128, 255 }, 2, 2);

        byte[] replaced = Replace(pdf, image, null, out var result);

        var mask = ImageObject(replaced, result.SoftMaskObjectNumber);
        Assert.Equal("Image", ((PdfName)mask[PdfName.Subtype]).Value);
        Assert.Equal("DeviceGray", ((PdfName)mask[PdfName.ColorSpace]).Value);
        Assert.Equal(8, (int)(mask.GetDouble(PdfName.BitsPerComponent) ?? 0));
        Assert.Equal(2, (int)(mask.GetDouble(PdfName.Width) ?? 0));
        Assert.Equal(new byte[] { 0, 64, 128, 255 }, mask.GetDecodedData());
        // The masked image must point at it, or the mask is dead weight.
        var pointer = ImageObject(replaced, result.ObjectNumber)[PdfName.SMask] as PdfReference;
        Assert.NotNull(pointer);
        Assert.Equal(result.SoftMaskObjectNumber, pointer!.ReferenceObjectNumber);
    }

    [Fact]
    public void WithSoftMask_WrongByteCount_Throws()
    {
        var image = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(2, 2, 0, 0, 255), 2, 2);
        var ex = Assert.Throws<ArgumentException>(() => image.WithSoftMask(new byte[3], 2, 2));
        Assert.Contains("Expected exactly 4 bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rgba32_WrongByteCount_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => ReplacementImage.Rgba32(new byte[15], 2, 2));
        Assert.Contains("Expected exactly 16 bytes", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    //  1b. /Matte — pre-blended colour samples (§11.6.5.3)
    // ================================================================

    // A half-transparent blue pre-blended against a WHITE matte:
    //   c' = m + α(c − m) = 255 + 0.502·(0 − 255) = 127 on red and green, 255 on blue.
    // Both renderers invert that (c = m + (c' − m)/α), so a matted image and the equivalent
    // straight one must composite to the same pixel — and the pre-blended samples *without* the
    // /Matte must not.
    private static readonly byte[] PreblendedOverWhite = { 127, 127, 255 };
    private static readonly byte[] StraightBlue        = { 0, 0, 255 };
    private static readonly byte[] HalfAlpha           = { 128 };
    private static readonly float[] WhiteMatte         = { 1f, 1f, 1f };

    /// <summary>
    /// The round trip: an image written with <c>/Matte</c> renders as the same colour as the
    /// equivalent straight-alpha image, and the same pre-blended samples written <i>without</i> the
    /// matte render visibly lighter.
    /// </summary>
    /// <remarks>
    /// The third render is the load-bearing one. Asserting only that the two matted/straight
    /// results agree would also pass if the writer emitted no <c>/Matte</c> and the reader ignored
    /// it — the "dictionary-shape test wearing a rendering claim" this entry exists to avoid. The
    /// gap is large (≈64 levels on a 0..255 channel), which is exactly the halo a caller sees when
    /// pre-blended samples are published as straight ones.
    /// </remarks>
    [Fact]
    public void WithSoftMask_Matte_RendersTheSameColourAsTheEquivalentStraightAlphaImage()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        byte[] matted = Replace(pdf,
            ReplacementImage.Rgb24(PreblendedOverWhite, 1, 1)
                            .WithSoftMask(HalfAlpha, 1, 1, WhiteMatte),
            null, out _);
        byte[] straight = Replace(pdf,
            ReplacementImage.Rgb24(StraightBlue, 1, 1).WithSoftMask(HalfAlpha, 1, 1),
            null, out _);
        byte[] unmatted = Replace(pdf,
            ReplacementImage.Rgb24(PreblendedOverWhite, 1, 1).WithSoftMask(HalfAlpha, 1, 1),
            null, out _);

        SKColor withMatte = CenterPixel(matted);
        SKColor asStraight = CenterPixel(straight);
        SKColor noMatte = CenterPixel(unmatted);

        Assert.True(Math.Abs(withMatte.Red - asStraight.Red) <= 4,
            $"/Matte must un-blend back to the straight colour: matted {withMatte}, straight {asStraight}");
        Assert.True(Math.Abs(withMatte.Green - asStraight.Green) <= 4,
            $"/Matte must un-blend back to the straight colour: matted {withMatte}, straight {asStraight}");
        Assert.True(noMatte.Red - withMatte.Red > 40,
            $"without /Matte the same samples must stay pre-blended and render lighter: " +
            $"unmatted {noMatte}, matted {withMatte}");
    }

    /// <summary>
    /// <c>/Matte</c> is written on the soft mask, with the caller's components — and is absent
    /// when none was supplied, since its mere presence changes how every sample is interpreted.
    /// </summary>
    [Fact]
    public void WithSoftMask_Matte_IsWrittenOnTheMaskObject_AndOnlyWhenSupplied()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        byte[] matted = Replace(pdf,
            ReplacementImage.Rgb24(PreblendedOverWhite, 1, 1)
                            .WithSoftMask(HalfAlpha, 1, 1, new float[] { 0.25f, 0.5f, 0.75f }),
            null, out var mattedResult);
        byte[] plain = Replace(pdf,
            ReplacementImage.Rgb24(StraightBlue, 1, 1).WithSoftMask(HalfAlpha, 1, 1),
            null, out var plainResult);

        var matte = ImageObject(matted, mattedResult.SoftMaskObjectNumber)[PdfName.Matte] as PdfArray;
        Assert.NotNull(matte);
        Assert.Equal(3, matte!.Count);
        Assert.Equal(0.25, ((PdfNumber)matte[0]).DoubleValue, 5);
        Assert.Equal(0.5,  ((PdfNumber)matte[1]).DoubleValue, 5);
        Assert.Equal(0.75, ((PdfNumber)matte[2]).DoubleValue, 5);

        Assert.Null(ImageObject(plain, plainResult.SoftMaskObjectNumber)[PdfName.Matte]);
    }

    /// <summary>
    /// The component count must match the base image's colour space. A reader derives the matte's
    /// colour space from the array's length alone, so a mismatch is not caught downstream — it is
    /// read as a different space and produces a wrong colour with no error anywhere.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void WithSoftMask_MatteWithWrongComponentCountForTheColorSpace_Throws(int components)
    {
        var image = ReplacementImage.Rgb24(PreblendedOverWhite, 1, 1);   // DeviceRGB: 3 components

        var ex = Assert.Throws<ArgumentException>(
            () => image.WithSoftMask(HalfAlpha, 1, 1, new float[components]));
        Assert.Contains("takes 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSoftMask_MatteComponentOutsideZeroToOne_Throws()
    {
        var image = ReplacementImage.Rgb24(PreblendedOverWhite, 1, 1);

        // 0..255 byte values are the usual mistake; 255 would otherwise be written verbatim and
        // read as a wildly out-of-gamut backdrop.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => image.WithSoftMask(HalfAlpha, 1, 1, new float[] { 255f, 255f, 255f }));
        Assert.Contains("0..1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// §11.6.5.3 requires a matted mask to have the image's own dimensions — the scaling tolerance
    /// the plain overload documents does not carry over, because un-blending divides by the alpha
    /// that produced each sample.
    /// </summary>
    [Fact]
    public void WithSoftMask_MatteWithMismatchedMaskDimensions_Throws()
    {
        var image = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(2, 2, 127, 127, 255), 2, 2);

        var ex = Assert.Throws<ArgumentException>(
            () => image.WithSoftMask(HalfAlpha, 1, 1, WhiteMatte));
        Assert.Contains("11.6.5.3", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fitting rebuilds the image from straight RGBA and re-emits a plain <c>/SMask</c>, so it
    /// cannot carry a matte through. It refuses rather than publishing pre-blended samples with the
    /// entry that explains them removed.
    /// </summary>
    [Fact]
    public void Replace_MattedImage_WithContainFit_RefusesRatherThanDroppingTheMatte()
    {
        // A 2x1 replacement into a 1x1 original, so Contain really does have to letterbox.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        var image = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(2, 1, 127, 127, 255), 2, 1)
                                    .WithSoftMask(new byte[] { 128, 128 }, 2, 1, WhiteMatte);

        var ex = Assert.Throws<PdfException>(() => Replace(pdf, image,
            new ImageReplacementOptions { Fit = ImageFit.Contain }, out _));
        Assert.Contains("/Matte", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    //  2. CMYK / YCCK JPEG
    // ================================================================

    /// <summary>
    /// A JPEG frame header only — SOI, optionally APP14 "Adobe", then SOF0 declaring
    /// <paramref name="components"/> channels at 16x8. Nothing decodes it: the replacement embeds
    /// the codestream verbatim and only the header is parsed, so this exercises exactly the code
    /// under test without needing an encoder that can emit CMYK (Skia cannot).
    /// </summary>
    private static byte[] JpegFrameHeader(int components, bool adobe, int transform = 2)
    {
        var bytes = new System.Collections.Generic.List<byte> { 0xFF, 0xD8 };
        if (adobe)
        {
            bytes.AddRange(new byte[] { 0xFF, 0xEE, 0x00, 0x0E });                 // APP14, length 14
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("Adobe"));
            bytes.AddRange(new byte[] { 0x00, 0x64, 0x00, 0x00, 0x00, 0x00 });     // version + flags
            bytes.Add((byte)transform);
        }
        bytes.AddRange(new byte[]
        {
            0xFF, 0xC0, 0x00, 0x11, 0x08,   // SOF0, length 17, precision 8
            0x00, 0x08,                     // height 8
            0x00, 0x10,                     // width 16
            (byte)components,
        });
        bytes.AddRange(new byte[12]);       // component spec padding
        return bytes.ToArray();
    }

    [Fact]
    public void Jpeg_AdobeCmyk_EmitsDeviceCmykWithTheInvertedDecodeArray()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] cmyk = JpegFrameHeader(components: 4, adobe: true);

        byte[] replaced = Replace(pdf, ReplacementImage.Jpeg(cmyk), null, out var result);

        var image = ImageObject(replaced, result.ObjectNumber);
        Assert.Equal("DeviceCMYK", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Equal("DCTDecode", ((PdfName)image[PdfName.Filter]).Value);
        Assert.Equal(16, result.NewWidth);
        Assert.Equal(8, result.NewHeight);

        // Adobe writes CMYK inverted; without /Decode [1 0 …] the image renders as a colour negative.
        var decode = image[PdfName.Decode] as PdfArray;
        Assert.NotNull(decode);
        Assert.Equal(8, decode!.Count);
        for (int i = 0; i < 8; i++)
            Assert.Equal(i % 2 == 0 ? 1d : 0d, ((PdfNumber)decode[i]).DoubleValue);

        // The codestream is embedded byte-for-byte — nothing re-encoded it.
        Assert.Equal(cmyk, image.RawData);
    }

    [Fact]
    public void Jpeg_FourComponentsWithoutAdobeMarker_IsRefusedRatherThanGuessed()
    {
        byte[] noMarker = JpegFrameHeader(components: 4, adobe: false);

        var ex = Assert.Throws<ArgumentException>(() => ReplacementImage.Jpeg(noMarker));
        Assert.Contains("Adobe APP14", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_ThreeComponentsWithAdobeMarker_StaysDeviceRgbWithNoDecode()
    {
        // The Adobe marker is common on ordinary RGB JPEGs too; it must not drag them to CMYK.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] rgb = JpegFrameHeader(components: 3, adobe: true, transform: 1);

        byte[] replaced = Replace(pdf, ReplacementImage.Jpeg(rgb), null, out var result);

        var image = ImageObject(replaced, result.ObjectNumber);
        Assert.Equal("DeviceRGB", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Null(image[PdfName.Decode]);
    }

    [Fact]
    public void Jpeg_FiveComponents_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ReplacementImage.Jpeg(JpegFrameHeader(components: 5, adobe: true)));
        Assert.Contains("5 components", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    //  3. Fit.Contain — letterboxing instead of stretching
    // ================================================================

    [Fact]
    public void Contain_PadsAWideReplacementIntoASquareOriginal()
    {
        // Original 1:1; replacement 4:1. Stretched it would fill the page; contained it occupies
        // a centre band, leaving the top and bottom quarters showing the white page.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var wide = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(32, 8, 0, 0, 255), 32, 8);

        byte[] contained = Replace(pdf, wide, new ImageReplacementOptions { Fit = ImageFit.Contain },
            out var result);

        Assert.True(result.Letterboxed);
        Assert.False(result.AspectRatioChanged, "letterboxing is what removes the distortion");
        Assert.Equal(32, result.NewWidth);
        Assert.Equal(32, result.NewHeight);

        AssertIsBlue(CenterPixel(contained), "centre band");
        AssertIsWhite(PixelAt(contained, 0.5, 0.05), "top padding must be transparent");
        AssertIsWhite(PixelAt(contained, 0.5, 0.95), "bottom padding must be transparent");
    }

    [Fact]
    public void Stretch_IsTheDefault_AndFillsTheWholeRectangle()
    {
        // The same input under the default policy: no padding, so the top strip is blue too.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var wide = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(32, 8, 0, 0, 255), 32, 8);

        byte[] stretched = Replace(pdf, wide, null, out var result);

        Assert.False(result.Letterboxed);
        Assert.True(result.AspectRatioChanged);
        AssertIsBlue(PixelAt(stretched, 0.5, 0.05), "stretched fill reaches the top edge");
        Assert.Contains(result.Warnings, w => w.Contains("stretched", StringComparison.Ordinal));
    }

    [Fact]
    /// <summary>
    /// <c>AspectRatioChanged</c>'s own XML doc says it is "Always false when Letterboxed is true —
    /// that is what letterboxing fixes", and the <c>Warnings</c> list is an else-if chain that
    /// omits the stretch note whenever a fit ran. Recomputing the ratio from the FITTED dimensions
    /// contradicted both.
    ///
    /// <para><b>The size is the test.</b> A padded canvas is integer-rounded, so only a small
    /// original can miss the 1% tolerance: 7x3 (2.3333) padded from a 10x10 replacement becomes
    /// 23x10 (2.30), and |2.30 − 2.3333| = 0.0333 against a tolerance of 0.0233. Every existing
    /// letterbox test uses images large enough that the rounding disappears, which is why the
    /// contradiction survived them.</para>
    /// </summary>
    public void Contain_OnASmallOriginal_DoesNotReportLetterboxedAndStretchedAtOnce()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(7, 3, 255, 0, 0), 7, 3);
        var square = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(10, 10, 0, 0, 255), 10, 10);

        Replace(pdf, square, new ImageReplacementOptions { Fit = ImageFit.Contain }, out var result);

        // The fixture really does land in the rounding gap — without this the assertion below
        // would also pass for a replacement whose ratio matched exactly.
        Assert.True(result.Letterboxed);
        Assert.Equal(23, result.NewWidth);
        Assert.Equal(10, result.NewHeight);
        Assert.NotEqual(
            (double)result.NewWidth / result.NewHeight,
            (double)result.OriginalWidth / result.OriginalHeight);

        Assert.False(result.AspectRatioChanged,
            "letterboxing is what removes the distortion; the two flags contradict each other");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("stretched", StringComparison.Ordinal));
    }

    [Fact]
    public void Contain_PadsATallReplacementOnTheLeftAndRight()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var tall = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(8, 32, 0, 0, 255), 8, 32);

        byte[] contained = Replace(pdf, tall, new ImageReplacementOptions { Fit = ImageFit.Contain },
            out var result);

        Assert.Equal(32, result.NewWidth);
        Assert.Equal(32, result.NewHeight);
        AssertIsBlue(CenterPixel(contained), "centre band");
        AssertIsWhite(PixelAt(contained, 0.05, 0.5), "left padding must be transparent");
        AssertIsWhite(PixelAt(contained, 0.95, 0.5), "right padding must be transparent");
    }

    // ------------------------------------------------------------------ Cover (crop to fill)

    /// <summary>
    /// A <paramref name="w"/>×<paramref name="h"/> RGB image whose left and right thirds are green
    /// and whose centre third is blue. A solid colour cannot show a crop — every pixel survives it —
    /// so the fixture has to vary across the axis being cropped or the test measures nothing.
    /// </summary>
    private static byte[] GreenBlueGreenRgb(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool centre = x >= w / 3 && x < w - w / 3;
                int i = (y * w + x) * 3;
                rgb[i]     = 0;
                rgb[i + 1] = centre ? (byte)0 : (byte)255;
                rgb[i + 2] = centre ? (byte)255 : (byte)0;
            }
        return rgb;
    }

    private static void AssertIsGreen(SKColor c, string what) =>
        Assert.True(c.Green > 200 && c.Red < 80 && c.Blue < 80, $"{what}: expected green, got {c}");

    /// <summary>The RGB samples as a real, decodable JPEG.</summary>
    /// <remarks>
    /// <b>Every other JPEG fixture here is a header without an image</b>, which is right for the
    /// tests that ask what the header <i>says</i> — and means no test ever decoded one. Fitting a
    /// JPEG is the only path that reads a decoded JPEG's pixels, and reading them is where a
    /// channel order can be wrong.
    /// </remarks>
    private static byte[] JpegOf(byte[] rgb, int w, int h)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque);

        var pixels = new SKColor[w * h];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new SKColor(rgb[i * 3], rgb[(i * 3) + 1], rgb[(i * 3) + 2], 255);
        bmp.Pixels = pixels;

        using SKData data = bmp.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    /// <summary>A JPEG replacement keeps its own colours through a Cover crop.</summary>
    /// <remarks>
    /// <para><b>The only fixture in the suite that decodes a JPEG.</b> `ImageFit` rebuilds the
    /// image from straight RGBA, and for a JPEG that means decoding it — a path the header-only
    /// fixtures never enter and the CMYK one refuses before reaching. Swapping red and blue there
    /// produced a page that was still a valid PDF with a correctly cropped image in the wrong
    /// colours, and nothing said so.</para>
    ///
    /// <para>Not incidental: a decoded JPEG arrives in the platform's preferred byte order, which
    /// on Windows is Bgra8888 — so this is a path where red and blue really are at different
    /// offsets from the Rgba8888 the rest of the fixtures produce.</para>
    /// </remarks>
    [Fact]
    public void Cover_OnADecodableJpeg_KeepsTheReplacementsColours()
    {
        // Original 1:1, replacement 3:1 with green flanks around a blue centre. Cover keeps the
        // centred 1:1 rectangle, which is the blue third, so the page comes back blue -- and would
        // come back RED if the decode read the channels in the wrong order.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var jpeg = ReplacementImage.Jpeg(JpegOf(GreenBlueGreenRgb(48, 16), 48, 16));

        byte[] covered = Replace(pdf, jpeg, new ImageReplacementOptions { Fit = ImageFit.Cover },
            out var result);

        Assert.True(result.Cropped);
        Assert.Equal(16, result.NewWidth);
        Assert.Equal(16, result.NewHeight);

        AssertIsBlue(CenterPixel(covered), "centre of a JPEG cropped by Cover");
    }

    /// <summary>
    /// A CMYK JPEG fitted with Contain keeps its inks: the canvas is <c>/DeviceCMYK</c>, with no
    /// <c>/Decode</c> (the inks come back with Adobe's inversion undone), the image centred on it and
    /// the padding transparent in the <c>/SMask</c> — not Skia's naive RGB of the inks, the black
    /// plate spread over three. Fixture: 32×16, left half 100 % cyan, right
    /// 153/128/102/77 (<see cref="Rendering.CmykJpegSamplesTests"/>).
    /// </summary>
    [Fact]
    public void Contain_OnACmykJpeg_KeepsItsInks()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var cmyk = ReplacementImage.Jpeg(Convert.FromBase64String(Rendering.CmykJpegSamplesTests.Fixture));

        byte[] contained = Replace(pdf, cmyk, new ImageReplacementOptions { Fit = ImageFit.Contain }, out var result);

        Assert.True(result.Letterboxed);
        var image = ImageObject(contained, result.ObjectNumber);
        Assert.Equal("DeviceCMYK", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Null(image[PdfName.Decode]);
        byte[] inks = image.GetDecodedData();
        Assert.Equal(32 * 32 * 4, inks.Length);
        // The 16 rows sit at canvas rows 8..23.
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, inks.Skip((12 * 32 + 8) * 4).Take(4).ToArray());
        Assert.Equal(new byte[] { 153, 128, 102, 77 }, inks.Skip((12 * 32 + 24) * 4).Take(4).ToArray());
        byte[] alpha = ImageObject(contained, result.SoftMaskObjectNumber).GetDecodedData();
        Assert.Equal(0, alpha[2 * 32 + 8]);
        Assert.Equal(255, alpha[12 * 32 + 8]);
    }

    /// <summary>A CMYK JPEG cropped by Cover keeps its inks: the centred 16×16 is half cyan, half the rich ink, in <c>/DeviceCMYK</c>, opaque.</summary>
    [Fact]
    public void Cover_OnACmykJpeg_KeepsItsInks()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var cmyk = ReplacementImage.Jpeg(Convert.FromBase64String(Rendering.CmykJpegSamplesTests.Fixture));

        byte[] covered = Replace(pdf, cmyk, new ImageReplacementOptions { Fit = ImageFit.Cover }, out var result);

        Assert.True(result.Cropped);
        var image = ImageObject(covered, result.ObjectNumber);
        Assert.Equal("DeviceCMYK", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Null(image[PdfName.SMask]);
        byte[] inks = image.GetDecodedData();
        Assert.Equal(16 * 16 * 4, inks.Length);
        // Columns 8..23 of the source: the boundary falls between crop columns 7 and 8.
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, inks.Take(4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 0 }, inks.Skip(7 * 4).Take(4).ToArray());
        Assert.Equal(new byte[] { 153, 128, 102, 77 }, inks.Skip(8 * 4).Take(4).ToArray());
        Assert.Equal(new byte[] { 153, 128, 102, 77 }, inks.Skip(15 * 4).Take(4).ToArray());
    }

    /// <summary>Cover crops the soft mask with the image: a mask of 0 / 128 / 255 thirds leaves the centred third's 128 everywhere.</summary>
    [Fact]
    public void Cover_CropsTheSoftMaskWithTheImage()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        byte[] mask = new byte[24 * 8];
        for (int i = 0; i < mask.Length; i++) mask[i] = (i % 24) < 8 ? (byte)0 : (i % 24) < 16 ? (byte)128 : (byte)255;
        var image = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(24, 8, 0, 0, 255), 24, 8).WithSoftMask(mask, 24, 8);

        byte[] covered = Replace(pdf, image, new ImageReplacementOptions { Fit = ImageFit.Cover }, out var result);

        byte[] alpha = ImageObject(covered, result.SoftMaskObjectNumber).GetDecodedData();
        Assert.Equal(8 * 8, alpha.Length);
        Assert.All(alpha, a => Assert.Equal(128, a));
    }

    /// <summary>A soft mask smaller than its image is scaled to it before fitting: a 2×1 mask [0 255] over a 4×2 image makes its left half clear and its right half opaque.</summary>
    [Fact]
    public void Contain_ScalesASmallerSoftMaskToTheImage()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var image = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(4, 2, 0, 0, 255), 4, 2).WithSoftMask(new byte[] { 0, 255 }, 2, 1);

        byte[] contained = Replace(pdf, image, new ImageReplacementOptions { Fit = ImageFit.Contain }, out var result);

        byte[] alpha = ImageObject(contained, result.SoftMaskObjectNumber).GetDecodedData();
        // A 4×4 canvas, the image on rows 1 and 2.
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, alpha.Skip(1 * 4).Take(4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, alpha.Skip(2 * 4).Take(4).ToArray());
    }

    /// <summary>A JPEG 2000 replacement cannot be fitted — its codestream is no sample buffer — and is refused, not read as samples.</summary>
    [Fact]
    public void Contain_OnAJpeg2000_IsRefused_NotReadAsSamples()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        byte[] jp2 = Core.Imaging.Jpx.JpxImageEncoding.Encode(ImageFixtures.SolidRgb(2, 1, 0, 0, 255), 2, 1, 3, 8,
            new Core.Imaging.Jpx.JpxEncodingOptions { Lossless = true, DecompositionLevels = 0, Jp2Container = true });
        var jpx = ReplacementImage.Jpeg2000(jp2);

        var ex = Assert.Throws<Core.PdfException>(() => Replace(pdf, jpx, new ImageReplacementOptions { Fit = ImageFit.Contain }, out _));
        Assert.Contains("could not be decoded", ex.Message, StringComparison.Ordinal);
        // The acceptance control: the same request on raw samples fits.
        Replace(pdf, ReplacementImage.Rgb24(ImageFixtures.SolidRgb(2, 1, 0, 0, 255), 2, 1),
            new ImageReplacementOptions { Fit = ImageFit.Contain }, out var fitted);
        Assert.True(fitted.Letterboxed);
    }

    /// <summary>A grey replacement fitted with Contain stays <c>/DeviceGray</c> — one byte a pixel, not three.</summary>
    [Fact]
    public void Contain_OnAGreyImage_StaysGrey()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var grey = ReplacementImage.Gray8(Enumerable.Repeat((byte)90, 4 * 2).ToArray(), 4, 2);

        byte[] contained = Replace(pdf, grey, new ImageReplacementOptions { Fit = ImageFit.Contain }, out var result);

        var image = ImageObject(contained, result.ObjectNumber);
        Assert.Equal("DeviceGray", ((PdfName)image[PdfName.ColorSpace]).Value);
        byte[] samples = image.GetDecodedData();
        Assert.Equal(4 * 4, samples.Length);
        Assert.Equal(90, samples[1 * 4 + 1]);
    }

    [Fact]
    public void Cover_CropsAWideReplacementToFillASquareOriginal()
    {
        // Original 1:1, replacement 3:1 with green flanks. Cover keeps the centred 1:1 rectangle,
        // which is exactly the blue third — so the green must be gone from BOTH edges. Stretch
        // would squeeze all three bands into the frame and leave green at the edges, which is what
        // the sibling assertion below pins.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var wide = ReplacementImage.Rgb24(GreenBlueGreenRgb(24, 8), 24, 8);

        byte[] covered = Replace(pdf, wide, new ImageReplacementOptions { Fit = ImageFit.Cover },
            out var result);

        Assert.True(result.Cropped);
        Assert.False(result.Letterboxed, "Cover crops; it must not also report letterboxing");
        Assert.False(result.AspectRatioChanged, "cropping is what removes the distortion");
        Assert.Equal(8, result.NewWidth);
        Assert.Equal(8, result.NewHeight);

        AssertIsBlue(CenterPixel(covered), "centre");
        AssertIsBlue(PixelAt(covered, 0.05, 0.5), "left edge — the green flank must be cropped away");
        AssertIsBlue(PixelAt(covered, 0.95, 0.5), "right edge — the green flank must be cropped away");
    }

    [Fact]
    public void Stretch_KeepsTheFlanksThatCoverCropsAway()
    {
        // The contrast that makes the test above about CROPPING rather than about the fixture:
        // the identical replacement under the default policy still shows green at the edges.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var wide = ReplacementImage.Rgb24(GreenBlueGreenRgb(24, 8), 24, 8);

        byte[] stretched = Replace(pdf, wide, null, out var result);

        Assert.False(result.Cropped);
        AssertIsGreen(PixelAt(stretched, 0.05, 0.5), "left flank survives a stretch");
        AssertIsBlue(CenterPixel(stretched), "centre band is still blue");
    }

    [Fact]
    public void Cover_CropsATallReplacementTopAndBottom()
    {
        // The other axis. Rotate the fixture by building it wide and swapping the dimensions:
        // a 8×24 image whose top and bottom thirds are green.
        byte[] rgb = new byte[8 * 24 * 3];
        for (int y = 0; y < 24; y++)
            for (int x = 0; x < 8; x++)
            {
                bool centre = y >= 8 && y < 16;
                int i = (y * 8 + x) * 3;
                rgb[i + 1] = centre ? (byte)0 : (byte)255;
                rgb[i + 2] = centre ? (byte)255 : (byte)0;
            }

        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        byte[] covered = Replace(pdf, ReplacementImage.Rgb24(rgb, 8, 24),
            new ImageReplacementOptions { Fit = ImageFit.Cover }, out var result);

        Assert.True(result.Cropped);
        Assert.Equal(8, result.NewWidth);
        Assert.Equal(8, result.NewHeight);
        AssertIsBlue(PixelAt(covered, 0.5, 0.05), "top flank must be cropped away");
        AssertIsBlue(PixelAt(covered, 0.5, 0.95), "bottom flank must be cropped away");
    }

    [Fact]
    public void Cover_NeverWritesASoftMask()
    {
        // The structural difference from Contain: padding introduces transparency and therefore an
        // /SMask, cropping introduces none. Asserting this stops Cover being implemented as
        // "Contain with a different rectangle", which would add an alpha channel nothing needs.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var wide = ReplacementImage.Rgb24(GreenBlueGreenRgb(24, 8), 24, 8);

        Replace(pdf, wide, new ImageReplacementOptions { Fit = ImageFit.Cover }, out var result);

        Assert.Equal(0, result.SoftMaskObjectNumber);
        Assert.Contains(result.Warnings, w => w.Contains("Cropped", StringComparison.Ordinal));
    }

    [Fact]
    public void Cover_WithAMatchingAspectRatio_ChangesNothing()
    {
        // The control: no crop is needed, so the pixel-reading path must not run at all.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(4, 2, 255, 0, 0), 4, 2);
        var same = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(8, 4, 0, 0, 255), 8, 4);

        byte[] replaced = Replace(pdf, same, new ImageReplacementOptions { Fit = ImageFit.Cover },
            out var result);

        Assert.False(result.Cropped);
        Assert.Equal(8, result.NewWidth);
        Assert.Equal(4, result.NewHeight);
        AssertIsBlue(CenterPixel(replaced), "same-ratio replacement");
    }

    [Fact]
    public void Contain_WithAMatchingAspectRatio_ChangesNothing()
    {
        // No padding is needed, so the JPEG-preserving fast path must be taken: no re-encode,
        // no soft mask, no letterbox report.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(4, 2, 255, 0, 0), 4, 2);
        var same = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(8, 4, 0, 0, 255), 8, 4);

        byte[] replaced = Replace(pdf, same, new ImageReplacementOptions { Fit = ImageFit.Contain },
            out var result);

        Assert.False(result.Letterboxed);
        Assert.Equal(8, result.NewWidth);
        Assert.Equal(0, result.SoftMaskObjectNumber);
        AssertIsBlue(CenterPixel(replaced), "same-ratio replacement");
    }

    [Fact]
    public void Contain_ExtremeAspectMismatch_IsRefusedBeforeAllocating()
    {
        // Padding a 20000x1 strip to a 1:1 original needs a 20000x20000 canvas — 4x10^8 pixels,
        // past PdfLimits.MaxImagePixelArea. The ceiling must be checked against the *computed
        // canvas*, not just against the replacement, or a legal input allocates 1.6 GB.
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var strip = ReplacementImage.Rgb24(ImageFixtures.SolidRgb(20000, 1, 0, 0, 255), 20000, 1);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var ex = Assert.Throws<Core.PdfException>(() => PdfImageReplacer.Replace(
            input, output, 0, 0, strip, new ImageReplacementOptions { Fit = ImageFit.Contain }));
        Assert.Contains("exceeds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contain_OnAnUndecodableJpeg_ThrowsInsteadOfSilentlyStretching()
    {
        // A frame header and nothing after it: no decoder reads it, so letterboxing cannot read
        // its pixels. Falling back to Stretch would silently ignore what the caller asked for.
        // (A real CMYK JPEG is read: Contain_OnACmykJpeg_KeepsItsInks.)
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(8, 8, 255, 0, 0), 8, 8);
        var cmyk = ReplacementImage.Jpeg(JpegFrameHeader(components: 4, adobe: true));

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var ex = Assert.Throws<Core.PdfException>(() => PdfImageReplacer.Replace(
            input, output, 0, 0, cmyk, new ImageReplacementOptions { Fit = ImageFit.Contain }));
        Assert.Contains("Contain", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    //  4. Document-wide find + replace by object number
    // ================================================================

    [Fact]
    public void FindImages_ReachesAnImageInsideAFormXObject_WhichThePageScanCannotSee()
    {
        byte[] pdf = ImageFixtures.ImageInsideFormPdf(ImageFixtures.Red);

        using (var ms = new MemoryStream(pdf))
            Assert.Equal(0, PdfOperations.CountImagesOnPage(ms, 0));

        using var input = new MemoryStream(pdf);
        var found = PdfImageReplacer.FindImages(input);

        var image = Assert.Single(found);
        Assert.Equal(5, image.ObjectNumber);
        Assert.Equal(new[] { 0 }, image.Pages);
        Assert.Equal(new[] { "Logo" }, image.ResourceNames);
        Assert.True(image.OnlyInsideFormXObject);
    }

    [Fact]
    public void ReplaceByObjectNumber_ChangesAnImageDrawnFromInsideAForm()
    {
        byte[] pdf = ImageFixtures.ImageInsideFormPdf(ImageFixtures.Red);

        byte[] replaced = ReplaceByObject(pdf, 5,
            ReplacementImage.Rgb24(ImageFixtures.Blue, 1, 1), out var result);

        Assert.Equal(5, result.ObjectNumber);
        Assert.Equal("Logo", result.ResourceName);
        AssertIsBlue(CenterPixel(replaced), "image inside a form XObject");
    }

    [Fact]
    public void FindImages_ReportsEveryPageAndNameAOneSharedObjectIsReachedBy()
    {
        byte[] pdf = ImageFixtures.SharedImagePdf(ImageFixtures.Red);

        using var input = new MemoryStream(pdf);
        var image = Assert.Single(PdfImageReplacer.FindImages(input));

        Assert.Equal(5, image.ObjectNumber);
        Assert.Equal(new[] { 0, 1 }, image.Pages);
        Assert.False(image.OnlyInsideFormXObject);
    }

    [Fact]
    public void FindImages_ListsEachObjectOnce_NotOncePerReference()
    {
        byte[] pdf = ImageFixtures.TwoImagePdf(ImageFixtures.Red, ImageFixtures.Green);

        using var input = new MemoryStream(pdf);
        var found = PdfImageReplacer.FindImages(input);

        Assert.Equal(2, found.Count);
        Assert.Equal(new[] { 5, 6 }, found.Select(i => i.ObjectNumber).ToArray());
        Assert.All(found, i => Assert.Equal(1, i.Width));
    }

    [Fact]
    public void FindImages_SelfReferencingForm_DoesNotHangAndStillFindsTheImage()
    {
        // Adversarial input: the form lists itself in its own /Resources. Without the cycle guard
        // the walk recurses to the depth cap on every page; without a cap it never returns.
        byte[] pdf = ImageFixtures.SelfReferencingFormPdf(ImageFixtures.Red);

        using var input = new MemoryStream(pdf);
        var found = PdfImageReplacer.FindImages(input);

        var image = Assert.Single(found);
        Assert.Equal(5, image.ObjectNumber);
    }

    [Fact]
    public void ReplaceByObjectNumber_UnknownObject_Throws()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfImageReplacer.ReplaceByObjectNumber(input, output, 4,
                ReplacementImage.Rgb24(ImageFixtures.Blue, 1, 1)));
        Assert.Contains("not an image XObject", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplaceByObjectNumber_SharedImage_ReportsBothPages()
    {
        byte[] pdf = ImageFixtures.SharedImagePdf(ImageFixtures.Red);

        ReplaceByObject(pdf, 5, ReplacementImage.Rgb24(ImageFixtures.Blue, 1, 1), out var result);

        Assert.Equal(new[] { 0, 1 }, result.Pages);
        Assert.Equal(2, result.PagesSharingImage);
    }

    // ================================================================
    //  JPEG 2000 sources — embedded verbatim, the dictionary the file wants
    // ================================================================

    private static byte[] Jp2(int width, int height, int components, bool container = true)
    {
        var samples = new byte[width * height * components];
        for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(i * 3 + 40);
        return Core.Imaging.Jpx.JpxImageEncoding.Encode(samples, width, height, components, 8,
            new Core.Imaging.Jpx.JpxEncodingOptions { Jp2Container = container });
    }

    [Fact]
    public void Jpeg2000_Jp2File_IsEmbeddedByteForByte_WithItsSizeAndTheSpaceItsColourBoxNames()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1, extraImageKeys: "/SMaskInData 1 ");
        byte[] jp2 = Jp2(16, 8, 3);

        byte[] replaced = Replace(pdf, ReplacementImage.Jpeg2000(jp2), null, out var result);

        var image = ImageObject(replaced, result.ObjectNumber);
        Assert.Equal("JPXDecode", ((PdfName)image[PdfName.Filter]).Value);
        Assert.Equal("DeviceRGB", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Equal(8d, ((PdfNumber)image[PdfName.BitsPerComponent]).DoubleValue);
        Assert.Equal((16, 8), (result.NewWidth, result.NewHeight));
        Assert.Null(image[PdfName.Decode]);
        Assert.Null(image[PdfName.SMask]);
        // The original's /SMaskInData is not inherited: the file declares no opacity channel.
        Assert.Null(image[PdfName.SMaskInData]);
        Assert.Equal(jp2, image.RawData);
    }

    [Fact]
    public void Jpeg2000_RawGreyCodestream_IsDeviceGray()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] j2k = Jp2(6, 5, 1, container: false);

        byte[] replaced = Replace(pdf, ReplacementImage.Jpeg2000(j2k), null, out var result);

        var image = ImageObject(replaced, result.ObjectNumber);
        Assert.Equal("JPXDecode", ((PdfName)image[PdfName.Filter]).Value);
        Assert.Equal("DeviceGray", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Equal(j2k, image.RawData);
    }

    [Fact]
    public void Jpeg2000_NotAJpeg2000File_OrTwoComponents_IsRefusedWithTheReason()
    {
        var notJpx = Assert.Throws<ArgumentException>(() => ReplacementImage.Jpeg2000(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }));
        Assert.Contains("not a JPEG 2000 file", notJpx.Message, StringComparison.Ordinal);

        var planes = new[] { new int[4], new int[4] };
        byte[] twoComponents = Core.Imaging.Jpx.JpxEncoder.EncodeLossless(planes, 2, 2, 8, false, null);
        var two = Assert.Throws<ArgumentException>(() => ReplacementImage.Jpeg2000(twoComponents));
        Assert.Contains("2 colour channels", two.Message, StringComparison.Ordinal);
    }

    /// <summary>The opacity mode the file declares (Table 89): premultiplied writes 2; per-colour opacity, which the key cannot express, writes none.</summary>
    [Fact]
    public void Jpeg2000_APremultipliedOpacityChannel_IsWrittenAsSMaskInData2_AndAPerColourOne_GetsNoKey()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        byte[] premultiplied = Replace(pdf, ReplacementImage.Jpeg2000(TestHelpers.JpxEmbedFixtures.GreyWithPremultipliedOpacityJp2()), null, out var result);
        var image = ImageObject(premultiplied, result.ObjectNumber);
        Assert.Equal(2d, ((PdfNumber)image[PdfName.SMaskInData]).DoubleValue);

        byte[] perColour = Replace(pdf, ReplacementImage.Jpeg2000(TestHelpers.JpxEmbedFixtures.RgbWithPerColourOpacityJp2()), null, out result);
        image = ImageObject(perColour, result.ObjectNumber);
        Assert.Equal("DeviceRGB", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Null(image[PdfName.SMaskInData]);
    }

    [Fact]
    public void Jpeg2000_ADeclaredOpacityChannel_IsWrittenAsSMaskInData_AndAnSYccFileNamesNoColourSpace()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        byte[] withOpacity = Replace(pdf, ReplacementImage.Jpeg2000(TestHelpers.JpxEmbedFixtures.GreyWithOpacityJp2()), null, out var result);
        var image = ImageObject(withOpacity, result.ObjectNumber);
        Assert.Equal("DeviceGray", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Equal(1d, ((PdfNumber)image[PdfName.SMaskInData]).DoubleValue);
        Assert.Null(image[PdfName.SMask]);
        Assert.Equal((40, 24), (result.NewWidth, result.NewHeight));

        byte[] sYcc = Replace(pdf, ReplacementImage.Jpeg2000(TestHelpers.JpxEmbedFixtures.SYccJp2()), null, out result);
        image = ImageObject(sYcc, result.ObjectNumber);
        Assert.Equal("JPXDecode", ((PdfName)image[PdfName.Filter]).Value);
        Assert.Null(image[PdfName.ColorSpace]);
        Assert.Null(image[PdfName.SMaskInData]);
        Assert.Equal(8d, ((PdfNumber)image[PdfName.BitsPerComponent]).DoubleValue);
    }
}
