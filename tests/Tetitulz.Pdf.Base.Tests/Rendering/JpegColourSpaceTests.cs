// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A <c>DCTDecode</c> image in a colour space other than the device space its codec assumes is
/// coloured from its samples (<see cref="JpegSamples"/>), in both renderers and on the plates.
/// The reference renderer is the oracle for the spot image: full tint (255,0,0), half (255,127,127).
/// The fixtures are flat 16-pixel blocks, which a JPEG at quality 100 decodes exactly.
/// </summary>
public class JpegColourSpaceTests
{
    // A spot ink whose alternate is red: tint 1 paints (255,0,0), tint 0 white.
    private const string Spot = "[/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>]";

    // CalRGB with gamma 1: a sample of 128 is linear 0.5, which sRGB encodes near 188 — not the
    // codec's 128.
    private const string LinearCalRgb = "[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [1 1 1] /Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>]";

    /// <summary>Left half full tint, right half 128 — the spot JPEG matches the reference renderer and the same samples stored raw.</summary>
    [Fact]
    public void ASpotJpeg_IsColouredByItsTintTransform_Raster()
    {
        byte[] jpeg = GreyJpeg(255, 128);
        Assert.True(Core.Imaging.RasterImageInspector.TryInspectJpeg(jpeg, out Core.Imaging.RasterImageInfo frame) && frame.Components == 1);

        RenderedPage page = Render(Pdf(Spot, jpeg, dct: true));
        AssertNear((255, 0, 0), Pixel(page, 8, 8), "full tint");
        AssertNear((255, 127, 127), Pixel(page, 24, 8), "tint 128");

        RenderedPage raw = Render(Pdf(Spot, GreySamples(255, 128), dct: false));
        AssertNear(Pixel(raw, 8, 8), Pixel(page, 8, 8), "against the raw samples, full tint");
        AssertNear(Pixel(raw, 24, 8), Pixel(page, 24, 8), "against the raw samples, tint 128");
    }

    /// <summary>The SVG writer does not pass the spot JPEG through; its PNG carries the tint transform's colour.</summary>
    [Fact]
    public void ASpotJpeg_IsColouredByItsTintTransform_Svg()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(Spot, GreyJpeg(255, 128), dct: true)), 0);
        Assert.DoesNotContain("image/jpeg", svg);
        using SKBitmap png = JpxTestPdf.DecodeFirstEmbeddedPng(svg);
        SKColor full = png.GetPixel(8, 8), half = png.GetPixel(24, 8);
        AssertNear((255, 0, 0), (full.Red, full.Green, full.Blue), "full tint");
        AssertNear((255, 127, 127), (half.Red, half.Green, half.Blue), "tint 128");
    }

    /// <summary>
    /// A three-component JPEG in a CIE space: its samples go through the space (a linear 0.5 grey is
    /// ~188, not the codec's 128), in both renderers, as the same samples stored raw do — a grey and
    /// a colour, whose channels the samples must keep in order.
    /// </summary>
    [Theory]
    [InlineData(true, 128, 128, 128)]
    [InlineData(false, 128, 128, 128)]
    [InlineData(true, 200, 120, 40)]
    [InlineData(false, 200, 120, 40)]
    public void ACalRgbJpeg_IsColouredByItsSpace(bool raster, int r, int g, int b)
    {
        byte[] jpeg = RgbJpeg((byte)r, (byte)g, (byte)b);
        Assert.True(Core.Imaging.RasterImageInspector.TryInspectJpeg(jpeg, out Core.Imaging.RasterImageInfo frame) && frame.Components == 3);
        byte[] samples = new byte[32 * 16 * 3];
        for (int i = 0; i < samples.Length; i += 3) { samples[i] = (byte)r; samples[i + 1] = (byte)g; samples[i + 2] = (byte)b; }
        var expected = Pixel(Render(Pdf(LinearCalRgb, samples, dct: false)), 8, 8);
        if (r == g && g == b) Assert.InRange(expected.Item1, 180, 195);

        (byte, byte, byte) got;
        if (raster) got = Pixel(Render(Pdf(LinearCalRgb, jpeg, dct: true)), 8, 8);
        else
        {
            using SKBitmap png = JpxTestPdf.DecodeFirstEmbeddedPng(
                PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(LinearCalRgb, jpeg, dct: true)), 0));
            SKColor c = png.GetPixel(8, 8);
            got = (c.Red, c.Green, c.Blue);
        }
        // A chroma-subsampled colour JPEG decodes within a few levels of its source.
        AssertNear(expected, got, raster ? "raster" : "svg", tolerance: 3);
    }

    /// <summary>
    /// A device space keeps the codec — the SVG writer still passes a DeviceGray JPEG through — and
    /// so does a frame whose arity contradicts the space (three components in a one-component spot
    /// space); a matching non-device space does not.
    /// </summary>
    [Fact]
    public void OnlyAMatchingNonDeviceSpace_LeavesTheCodec()
    {
        byte[] grey = GreyJpeg(255, 128), rgb = RgbJpeg(128, 128, 128);
        Assert.True(JpegSamples.CodecColoursAreTheImages(Dict("/DeviceGray"), null, grey));
        Assert.True(JpegSamples.CodecColoursAreTheImages(Dict("/DeviceRGB"), null, rgb));
        Assert.True(JpegSamples.CodecColoursAreTheImages(Dict(Spot), null, rgb));
        Assert.False(JpegSamples.CodecColoursAreTheImages(Dict(Spot), null, grey));
        Assert.False(JpegSamples.CodecColoursAreTheImages(Dict(LinearCalRgb), null, rgb));

        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf("/DeviceGray", grey, dct: true)), 0);
        Assert.Contains("image/jpeg", svg);
    }

    /// <summary>
    /// On the spot plate the JPEG's samples are the ink, exactly — not its picture read back
    /// through the plate's sRGB filter, which would be an approximation.
    /// </summary>
    [Fact]
    public void ASpotJpeg_IsReadFromItsSamples_OnTheSpotPlate()
    {
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new MemoryStream(Pdf(Spot, GreyJpeg(255, 128), dct: true)), 0, 72);
        PdfSeparationPlate spot = s.Plates.Single(p => p.Colorant == "Spot");
        Assert.Equal(0, spot.ApproximatedPaints);
        Assert.InRange(spot.Tints[8 * s.Width + 8], 254, 255);
        Assert.InRange(spot.Tints[8 * s.Width + 24], 127, 129);
    }

    /// <summary>
    /// Decoded samples that happen to open with a codec's signature are still samples: a spot JPEG
    /// whose first columns decode to <c>FF D8 FF</c> (a JPEG's) or <c>89 'P' 'N' 'G'</c> is painted
    /// by its tint transform, not handed back to a codec, which would fail and paint nothing.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 255, 216 })]
    [InlineData(new byte[] { 137, 80, 78, 71 })]
    public void SamplesOpeningWithACodecSignature_AreStillSamples(byte[] cycle)
    {
        byte[] samples = new byte[32 * 16];
        for (int i = 0; i < samples.Length; i++) samples[i] = cycle[i % 32 % cycle.Length];
        RenderedPage page = Render(Pdf(Spot, GreyJpeg(samples), dct: true));
        for (int x = 0; x < cycle.Length; x++)
        {
            int g = PdfColorChannel(1f - cycle[x] / 255f);
            AssertNear((255, g, g), Pixel(page, x, 8), $"column {x}, tint {cycle[x]}");
        }
    }

    /// <summary>
    /// The decoded frame describes the samples, not a dictionary that disagrees with it — a
    /// <c>/Width</c> and <c>/Height</c> of half each, or a <c>/BitsPerComponent</c> of 16: the spot
    /// JPEG still paints its left half full tint and its right half at 128, on every row.
    /// </summary>
    [Theory]
    [InlineData(16, 8, 8)]
    [InlineData(32, 16, 16)]
    public void TheDecodedFrame_DescribesTheSamples_NotTheDictionary(int width, int height, int bpc)
    {
        RenderedPage page = Render(Pdf(Spot, GreyJpeg(255, 128), dct: true, width: width, height: height, bpc: bpc));
        foreach (int y in new[] { 2, 5, 10, 13 })
        {
            AssertNear((255, 0, 0), Pixel(page, 8, y), $"left, row {y}");
            AssertNear((255, 127, 127), Pixel(page, 24, y), $"right, row {y}");
        }
    }

    private static int PdfColorChannel(float v) => (int)Math.Round(v * 255);

    // ------------------------------------------------------------------ helpers

    private static byte[] GreySamples(byte left, byte right)
    {
        var b = new byte[32 * 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++) b[y * 32 + x] = x < 16 ? left : right;
        return b;
    }

    private static byte[] GreyJpeg(byte left, byte right) => GreyJpeg(GreySamples(left, right));

    private static byte[] GreyJpeg(byte[] s)
    {
        using var bmp = new SKBitmap(new SKImageInfo(32, 16, SKColorType.Gray8, SKAlphaType.Opaque));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++) bmp.SetPixel(x, y, new SKColor(s[y * 32 + x], s[y * 32 + x], s[y * 32 + x]));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    private static byte[] RgbJpeg(byte r, byte g, byte b)
    {
        using var bmp = new SKBitmap(new SKImageInfo(32, 16, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bmp.Erase(new SKColor(r, g, b));
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    private static PdfDictionary Dict(string colorSpace)
    {
        var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(Pdf(colorSpace, new byte[1], dct: false)));
        return (PdfDictionary)doc.ResolveObject(new PdfReference(5, 0));
    }

    private static RenderedPage Render(byte[] pdf) => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

    private static (byte, byte, byte) Pixel(RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
    }

    private static void AssertNear((int, int, int) expected, (int, int, int) actual, string what, int tolerance = 1) =>
        Assert.True(Math.Abs(expected.Item1 - actual.Item1) <= tolerance && Math.Abs(expected.Item2 - actual.Item2) <= tolerance
                    && Math.Abs(expected.Item3 - actual.Item3) <= tolerance, $"{what}: {actual}, expected {expected}");

    private static byte[] Pdf(string colorSpace, byte[] imageData, bool dct, int width = 32, int height = 16, int bpc = 8)
    {
        string img = Encoding.Latin1.GetString(imageData);
        const string content = "q 32 0 0 16 0 0 cm /Im0 Do Q";
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 32 16] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width " + width + " /Height " + height + " /BitsPerComponent " + bpc + " /ColorSpace " + colorSpace +
            (dct ? " /Filter /DCTDecode" : "") + " /Length " + imageData.Length + " >>\nstream\n" + img + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++) { offs[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
