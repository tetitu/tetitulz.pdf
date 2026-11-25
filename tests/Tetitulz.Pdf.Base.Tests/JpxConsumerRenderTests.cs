// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Every consumer of a <c>JPXDecode</c> image sees the in-house decoder's pixels, with
/// <b>no plugin registered</b>: the raster renderer, the SVG writer, a soft mask, the plugin
/// fallback order, and the JP2 colour rules a PDF consumer applies (ISO 32000-2 §7.4.9) — each
/// asserted on rendered pixels against the lossless fixture's own source planes, drawn 1:1.
/// </summary>
/// <remarks>
/// Joins <see cref="TestInfrastructure.PluginHostCollection"/>: the constructor resets the
/// process-wide plugin host so that every pixel here is the decoder's, and two tests register a
/// counting plugin to pin the order.
/// </remarks>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class JpxConsumerRenderTests : IDisposable
{
    public JpxConsumerRenderTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
    private static JpxLosslessFixtures.Fixture Gray => JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
    private static JpxLosslessFixtures.Fixture Rgba => JpxLosslessFixtures.Rgba40x24_Mct_OneLevel;

    // ── the raster renderer ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8")]   // the dictionary's space: the raw path
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 16")]  // ignored for JPXDecode (Table 87): the codestream's 8 bits govern
    [InlineData("/ColorSpace /DeviceRGB")]                       // absent, as Table 87 allows
    [InlineData("")]                                             // the codestream's: DeviceRGB by count
    public void Raster_LosslessRgbCodestream_PaintsEverySampleExactly(string extras)
    {
        using SKBitmap page = Render(ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, extras));
        AssertEveryPixel(page, (x, y) => Pixel(Rgb, x, y));
    }

    [Fact]
    public void Raster_GreyCodestream_PaintsEverySampleExactly()
    {
        using SKBitmap page = Render(ImagePdf(Gray.Codestream, Gray.Width, Gray.Height, ""));
        AssertEveryPixel(page, (x, y) => { byte g = Gray.Planes[0][y * Gray.Width + x]; return new SKColor(g, g, g); });
    }

    /// <summary>A cdef numbering the colours 3, 2, 1 puts component 2 in red (T.800 Table I.18) — conformance file2's shape.</summary>
    [Fact]
    public void Raster_ChannelDefinitionReversingTheColours_PaintsTheChannelsInColourOrder()
    {
        byte[] jp2 = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1)));
        using SKBitmap page = Render(ImagePdf(jp2, Rgb.Width, Rgb.Height, ""));
        AssertEveryPixel(page, (x, y) =>
        {
            int i = y * Rgb.Width + x;
            return new SKColor(Rgb.Planes[2][i], Rgb.Planes[1][i], Rgb.Planes[0][i]);
        });
    }

    /// <summary>A palette expands each index to its entry before anything else sees it (T.800 I.5.3.4) — conformance file9's shape.</summary>
    [Fact]
    public void Raster_Palette_PaintsThePaletteEntries()
    {
        byte[] jp2 = Jp2(Gray, Palette(), H.Cmap((0, 1, 0), (0, 1, 1), (0, 1, 2)), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb));
        using SKBitmap page = Render(ImagePdf(jp2, Gray.Width, Gray.Height, ""));
        AssertEveryPixel(page, (x, y) =>
        {
            int index = Gray.Planes[0][y * Gray.Width + x];
            return new SKColor((byte)index, (byte)(255 - index), (byte)((7 * index) & 0xFF));
        });
    }

    /// <summary>
    /// sYCC (EnumCS 18) is converted with IEC 61966-2-1's YCC formula — the ICT's constants — before
    /// it is painted; the expectation is computed here from the source planes.
    /// </summary>
    [Fact]
    public void Raster_SYcc_IsPaintedAsRgbByTheYccFormula()
    {
        byte[] jp2 = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SYcc));
        using SKBitmap page = Render(ImagePdf(jp2, Rgb.Width, Rgb.Height, ""));
        AssertEveryPixel(page, (x, y) =>
        {
            int i = y * Rgb.Width + x;
            double luma = Rgb.Planes[0][i], cb = Rgb.Planes[1][i] - 128.0, cr = Rgb.Planes[2][i] - 128.0;
            return new SKColor(Clip(luma + 1.402 * cr), Clip(luma - 0.34413 * cb - 0.71414 * cr), Clip(luma + 1.772 * cb));
        }, tolerance: 1);
        // Potency: the page is not the source planes painted as RGB.
        int same = 0;
        for (int y = 0; y < Rgb.Height; y++)
            for (int x = 0; x < Rgb.Width; x++)
                if (page.GetPixel(x, y) == Pixel(Rgb, x, y)) same++;
        Assert.True(same < Rgb.Width * Rgb.Height / 4, $"{same} pixels equal the unconverted samples");
    }

    // ── /SMaskInData (ISO 32000-2 Table 87) ─────────────────────────────────

    /// <summary>The opacity channel becomes the alpha the page background shows through.</summary>
    [Fact]
    public void Raster_SMaskInData1_CompositesTheOpacityChannelOverThePage()
    {
        using SKBitmap page = Render(ImagePdf(RgbaJp2(), Rgba.Width, Rgba.Height, "/SMaskInData 1"));
        AssertEveryPixel(page, (x, y) =>
        {
            int i = y * Rgba.Width + x;
            int a = Rgba.Planes[3][i];
            return new SKColor(Over(Rgba.Planes[0][i], a), Over(Rgba.Planes[1][i], a), Over(Rgba.Planes[2][i], a));
        }, tolerance: 3);
        AssertAlphaWasPotent();
    }

    /// <summary>With 2 the colour is premultiplied: the sample is divided out before it is composited, so a half-transparent pixel keeps its full-strength colour.</summary>
    [Fact]
    public void Raster_SMaskInData2_RecoversThePremultipliedColour()
    {
        using SKBitmap page = Render(ImagePdf(RgbaJp2(), Rgba.Width, Rgba.Height, "/SMaskInData 2"));
        AssertEveryPixel(page, (x, y) =>
        {
            int i = y * Rgba.Width + x;
            int a = Rgba.Planes[3][i];
            return new SKColor(OverPremultiplied(Rgba.Planes[0][i], a), OverPremultiplied(Rgba.Planes[1][i], a), OverPremultiplied(Rgba.Planes[2][i], a));
        }, tolerance: 3);

        // Potency: modes 1 and 2 paint different pixels for the same bytes.
        using SKBitmap straight = Render(ImagePdf(RgbaJp2(), Rgba.Width, Rgba.Height, "/SMaskInData 1"));
        int differ = 0;
        for (int y = 0; y < Rgba.Height; y++)
            for (int x = 0; x < Rgba.Width; x++)
                if (!PixelAssert.Near(page.GetPixel(x, y), straight.GetPixel(x, y), 3)) differ++;
        Assert.True(differ > Rgba.Width * Rgba.Height / 4, $"only {differ} pixels differ between /SMaskInData 1 and 2");
    }

    [Fact]
    public void Raster_SMaskInDataAbsent_PaintsTheColourChannelsOpaque()
    {
        using SKBitmap page = Render(ImagePdf(RgbaJp2(), Rgba.Width, Rgba.Height, ""));
        AssertEveryPixel(page, (x, y) => Pixel(Rgba, x, y));
    }

    // ── the image dictionary's colour space (ISO 32000-2 §7.4.9) ────────────

    /// <summary>A three-component /ColorSpace over a four-component codestream reads three channels; with /SMaskInData the fourth is the announced opacity.</summary>
    [Fact]
    public void Raster_DeviceRgbOverFourComponents_DropsTheFourth_OrTakesItAsTheAnnouncedOpacity()
    {
        using SKBitmap opaque = Render(ImagePdf(Rgba.Codestream, Rgba.Width, Rgba.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8"));
        AssertEveryPixel(opaque, (x, y) => Pixel(Rgba, x, y));

        using SKBitmap masked = Render(ImagePdf(Rgba.Codestream, Rgba.Width, Rgba.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8 /SMaskInData 1"));
        AssertEveryPixel(masked, (x, y) =>
        {
            int i = y * Rgba.Width + x;
            int a = Rgba.Planes[3][i];
            return new SKColor(Over(Rgba.Planes[0][i], a), Over(Rgba.Planes[1][i], a), Over(Rgba.Planes[2][i], a));
        }, tolerance: 3);
    }

    /// <summary>A /ColorSpace needing more channels than the codestream has is refused, and the refusal is reported by its reason.</summary>
    [Fact]
    public void Raster_ColourSpaceNeedingMoreChannelsThanTheCodestreamHas_IsRefusedWithTheReason()
    {
        var reported = new List<string>();
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex) { lock (reported) reported.Add(detail ?? ""); }
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using SKBitmap page = Render(ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "/ColorSpace /DeviceCMYK /BitsPerComponent 8"));
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        lock (reported)
            Assert.Contains(reported, d => d.Contains("has 3 ordinary colour channel(s); the image dictionary's 4-component colour space needs 4", StringComparison.Ordinal));

        // The slot, on the thread that decodes.
        var dict = new PdfDictionary();
        dict.Set(PdfName.ColorSpace, PdfName.DeviceCMYK);
        Assert.False(JpxImageDecoder.TryDecodeSamples(Rgb.Codestream, dict, null, out _, out string reason));
        Assert.Contains("needs 4", reason, StringComparison.Ordinal);
        Assert.Equal(reason, JpxImageDecoder.LastDecodeError);
    }

    /// <summary>An Indexed /ColorSpace reads the samples as its indices, through the raw path's own palette lookup.</summary>
    [Fact]
    public void Raster_IndexedColourSpace_ReadsTheSamplesAsIndices()
    {
        var lookup = new StringBuilder();
        for (int i = 0; i < 256; i++) lookup.Append((255 - i).ToString("X2")).Append(i.ToString("X2")).Append((i ^ 0x55).ToString("X2"));
        string cs = $"/ColorSpace [/Indexed /DeviceRGB 255 <{lookup}>] /BitsPerComponent 8";
        using SKBitmap page = Render(ImagePdf(Gray.Codestream, Gray.Width, Gray.Height, cs));
        AssertEveryPixel(page, (x, y) =>
        {
            int index = Gray.Planes[0][y * Gray.Width + x];
            return new SKColor((byte)(255 - index), (byte)index, (byte)(index ^ 0x55));
        });
    }

    /// <summary>§7.4.9: /Decode applies with a /ColorSpace and is ignored without one.</summary>
    [Fact]
    public void Raster_DecodeArray_AppliesOnlyWithAColourSpace()
    {
        using SKBitmap inverted = Render(ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Decode [1 0 1 0 1 0]"));
        AssertEveryPixel(inverted, (x, y) =>
        {
            int i = y * Rgb.Width + x;
            return new SKColor((byte)(255 - Rgb.Planes[0][i]), (byte)(255 - Rgb.Planes[1][i]), (byte)(255 - Rgb.Planes[2][i]));
        });

        using SKBitmap ignored = Render(ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "/Decode [1 0 1 0 1 0]"));
        AssertEveryPixel(ignored, (x, y) => Pixel(Rgb, x, y));
    }

    // ── the plugin fallback order ───────────────────────────────────────────

    /// <summary>The in-house decoder answers first; a registered plugin is asked only when it declines.</summary>
    [Fact]
    public void Plugin_IsAskedOnlyWhenTheDecoderDeclines()
    {
        var plugin = new CountingJpxPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using (SKBitmap page = Render(ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "")))
            AssertEveryPixel(page, (x, y) => Pixel(Rgb, x, y));
        Assert.Equal(0, plugin.CallCount);

        byte[] undecodable = { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        using (Render(ImagePdf(undecodable, 2, 2, "/ColorSpace /DeviceRGB /BitsPerComponent 8"))) { }
        Assert.Equal(1, plugin.CallCount);
    }

    /// <summary>The SVG writer keeps the same order.</summary>
    [Fact]
    public void Plugin_IsAskedOnlyWhenTheDecoderDeclines_InTheSvgWriterToo()
    {
        var plugin = new CountingJpxPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using (var ms = new MemoryStream(ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "")))
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Equal(0, plugin.CallCount);

        byte[] undecodable = { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        using (var ms = new MemoryStream(ImagePdf(undecodable, 2, 2, "/ColorSpace /DeviceRGB /BitsPerComponent 8")))
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Equal(1, plugin.CallCount);
    }

    // ── the SVG writer ──────────────────────────────────────────────────────

    /// <summary>The two renderers must agree: the SVG's embedded PNG is the raster page, pixel for pixel, on both the file-colour and the dictionary-colour paths.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8")]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8 /Decode [1 0 1 0 1 0]")]
    public void Svg_AgreesWithTheRasterRender_PixelForPixel(string extras)
    {
        byte[] pdf = ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, extras);
        using SKBitmap raster = Render(pdf);
        string svg;
        using (var ms = new MemoryStream(pdf)) svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        using SKBitmap embedded = DecodeFirstEmbeddedPng(svg);

        Assert.Equal((Rgb.Width, Rgb.Height), (embedded.Width, embedded.Height));
        AssertEveryPixel(embedded, (x, y) => raster.GetPixel(x, y));
        // Potency: the raster render is the fixture, so the SVG is too.
        AssertEveryPixel(raster, (x, y) => extras.Contains("/Decode", StringComparison.Ordinal)
            ? new SKColor((byte)(255 - Rgb.Planes[0][y * Rgb.Width + x]), (byte)(255 - Rgb.Planes[1][y * Rgb.Width + x]), (byte)(255 - Rgb.Planes[2][y * Rgb.Width + x]))
            : Pixel(Rgb, x, y));
    }

    [Fact]
    public void Svg_ChannelDefinitionAndOpacity_AgreeWithTheRasterRender()
    {
        byte[] reordered = ImagePdf(Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1))), Rgb.Width, Rgb.Height, "");
        using SKBitmap raster = Render(reordered);
        using SKBitmap embedded = DecodeFirstEmbeddedPng(Svg(reordered));
        AssertEveryPixel(embedded, (x, y) => raster.GetPixel(x, y));

        // Alpha survives the PNG: every pixel's alpha is the opacity plane. The colour is checked
        // where it is at least half opaque: Skia premultiplies to encode and divides back out, so
        // a nearly transparent pixel's colour drifts by 255 / alpha on the round trip — a PNG
        // precision matter, not a decoding one.
        using SKBitmap masked = DecodeFirstEmbeddedPng(Svg(ImagePdf(RgbaJp2(), Rgba.Width, Rgba.Height, "/SMaskInData 1")));
        int checkedPixels = 0;
        for (int y = 0; y < Rgba.Height; y++)
            for (int x = 0; x < Rgba.Width; x++)
            {
                int i = y * Rgba.Width + x;
                SKColor c = masked.GetPixel(x, y);
                Assert.True(Math.Abs(c.Alpha - Rgba.Planes[3][i]) <= 1, $"alpha at ({x},{y}) is {c.Alpha}, the opacity sample {Rgba.Planes[3][i]}");
                if (c.Alpha >= 128)
                {
                    Assert.True(Math.Abs(c.Red - Rgba.Planes[0][i]) <= 2, $"red at ({x},{y}) is {c.Red}, the sample {Rgba.Planes[0][i]}");
                    checkedPixels++;
                }
            }
        Assert.True(checkedPixels > Rgba.Width * Rgba.Height / 4, "too few half-opaque pixels to have checked the colour");
    }

    // ── a soft mask encoded as JPEG 2000 ────────────────────────────────────

    /// <summary>An /SMask stream that is itself JPXDecode is decoded by the same chain, on both renderers.</summary>
    [Fact]
    public void SoftMask_EncodedAsJpx_IsAppliedByBothRenderers()
    {
        byte[] red = new byte[Gray.Width * Gray.Height * 3];
        for (int i = 0; i < red.Length; i += 3) red[i] = 255;
        byte[] pdf = Pdf(Gray.Width, Gray.Height, $"q {Gray.Width} 0 0 {Gray.Height} 0 0 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Gray.Width} /Height {Gray.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R", red),
            ($"<< /Type /XObject /Subtype /Image /Width {Gray.Width} /Height {Gray.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", Gray.Codestream));

        // Red over white at alpha a: (255, 255 − a, 255 − a).
        Func<int, int, SKColor> expected = (x, y) =>
        {
            int a = Gray.Planes[0][y * Gray.Width + x];
            return new SKColor(255, (byte)(255 - a), (byte)(255 - a));
        };
        using SKBitmap raster = Render(pdf);
        AssertEveryPixel(raster, expected, tolerance: 2);

        using SKBitmap embedded = DecodeFirstEmbeddedPng(Svg(pdf));
        for (int y = 0; y < Gray.Height; y++)
            for (int x = 0; x < Gray.Width; x++)
            {
                SKColor c = embedded.GetPixel(x, y);
                int a = Gray.Planes[0][y * Gray.Width + x];
                Assert.True(Math.Abs(c.Alpha - a) <= 1 && (c.Alpha <= 8 || c.Red >= 250),
                    $"SVG pixel ({x},{y}) is {c}, the mask sample {a}");
            }
    }

    /// <summary>
    /// A soft mask declared JPXDecode that no codec reads applies no mask — on both renderers the
    /// base image paints as it is. A raw-path fallback reading the mask's bytes as grey samples
    /// would read all zeros here, and the base image would vanish.
    /// </summary>
    [Fact]
    public void SoftMask_ThatNoCodecDecodes_LeavesTheImageUnmaskedOnBothRenderers()
    {
        byte[] red = new byte[Gray.Width * Gray.Height * 3];
        for (int i = 0; i < red.Length; i += 3) red[i] = 255;
        byte[] notAJpx = new byte[Gray.Width * Gray.Height];   // exactly a grey mask's worth of zeros, and no codestream
        byte[] pdf = Pdf(Gray.Width, Gray.Height, $"q {Gray.Width} 0 0 {Gray.Height} 0 0 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Gray.Width} /Height {Gray.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R", red),
            ($"<< /Type /XObject /Subtype /Image /Width {Gray.Width} /Height {Gray.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", notAJpx));

        using SKBitmap raster = Render(pdf);
        AssertEveryPixel(raster, (x, y) => new SKColor(255, 0, 0));

        using SKBitmap embedded = DecodeFirstEmbeddedPng(Svg(pdf));
        for (int y = 0; y < Gray.Height; y++)
            for (int x = 0; x < Gray.Width; x++)
            {
                SKColor c = embedded.GetPixel(x, y);
                Assert.True(c.Alpha == 255 && c.Red >= 250 && c.Green <= 5, $"SVG pixel ({x},{y}) is {c}; the undecodable mask should apply nothing");
            }
    }

    /// <summary>
    /// A plugin hands back one byte per sample whatever the codestream's depth: for a 12-bit
    /// codestream the door says the plugin narrowed it; for
    /// an 8-bit one it says nothing of the kind.
    /// </summary>
    [Theory]
    [InlineData(11, true)]
    [InlineData(7, false)]
    public void Plugin_NarrowingADeeperCodestream_IsReported(int ssiz, bool reported)
    {
        PdfPluginHost.Instance.Register(new TwoByTwoJpxPlugin());
        var deviations = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == ownThread && kind == RenderDiagnosticKind.JpxDecodeDeviation)
                lock (deviations) deviations.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using SKBitmap page = Render(ImagePdf(SizOnlyCodestream(ssiz), 2, 2, ""));
            // The plugin's four samples were painted: the door reached it.
            Assert.Equal(new SKColor(0, 0, 0), page.GetPixel(0, 0));
            Assert.Equal(new SKColor(255, 255, 255), page.GetPixel(1, 1));
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (deviations)
            Assert.Equal(reported, deviations.Exists(d => d.Contains("narrowed by the plugin", StringComparison.Ordinal)));
    }

    /// <summary>SOC, a SIZ of one 2x2 component at the given Ssiz, EOC: a header every reader parses and no decoder decodes.</summary>
    private static byte[] SizOnlyCodestream(int ssiz)
    {
        var bytes = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 41, 0x00, 0x00 };
        foreach (uint v in new uint[] { 2, 2, 0, 0, 2, 2, 0, 0 })
            bytes.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        bytes.AddRange(new byte[] { 0x00, 0x01, (byte)ssiz, 1, 1, 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    private sealed class TwoByTwoJpxPlugin : IImageDecodePlugin
    {
        public string Name => "Test.TwoByTwoGrey";
        public string Description => "answers every JPXDecode image with a 2x2 grey ramp";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public IReadOnlyList<string> SupportedFilters { get; } = new[] { "JPXDecode" };

        public bool TryDecode(string filterName, byte[] data, PdfDictionary imageDict,
            out int width, out int height, out int components, out byte[] pixels)
        {
            width = height = 2;
            components = 1;
            pixels = new byte[] { 0, 85, 170, 255 };
            return true;
        }
    }

    /// <summary>
    /// A colr profile whose data colour space the transform cannot take (a 2CLR profile, here
    /// ranked first by precedence) is passed over for the next lower colour specification, as
    /// §7.4.9 says — the image paints as the sRGB box beneath it — and the plan says why. An RGB
    /// profile in the same place is taken.
    /// </summary>
    [Fact]
    public void Raster_AnIccProfileTheTransformCannotTake_FallsToTheNextColourSpecification()
    {
        byte[] twoColour = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb, precedence: 0), H.ColrIcc(H.IccProfile("2CLR"), precedence: 1));
        using SKBitmap page = Render(ImagePdf(twoColour, Rgb.Width, Rgb.Height, ""));
        AssertEveryPixel(page, (x, y) => Pixel(Rgb, x, y));
        Assert.True(JpxImageDecoder.TryDecodeSamples(twoColour, null, null, out JpxPdfImage passedOver, out string reason), reason);
        Assert.Null(passedOver.IccProfile);
        Assert.Contains(passedOver.Deviations, d => d.Contains("the consumer does not support its ICC profile", StringComparison.Ordinal));

        byte[] rgbProfile = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb, precedence: 0), H.ColrIcc(H.IccProfile("RGB "), precedence: 1));
        Assert.True(JpxImageDecoder.TryDecodeSamples(rgbProfile, null, null, out JpxPdfImage taken, out reason), reason);
        Assert.NotNull(taken.IccProfile);
    }

    // ── tolerated irregularities are reported ───────────────────────────────

    /// <summary>A JP2 whose header cannot be read still paints (as a bare codestream) and says so.</summary>
    [Fact]
    public void Raster_MalformedJp2Header_PaintsTheCodestream_AndReportsTheDeviation()
    {
        byte[] jp2 = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cmap((0, 0, 0), (1, 0, 0), (2, 0, 0)));   // a cmap without a pclr
        var reported = new List<(RenderDiagnosticKind Kind, string Detail)>();
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex) { lock (reported) reported.Add((kind, detail ?? "")); }
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using SKBitmap page = Render(ImagePdf(jp2, Rgb.Width, Rgb.Height, ""));
            AssertEveryPixel(page, (x, y) => Pixel(Rgb, x, y));
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (reported)
            Assert.Contains(reported, r => r.Kind == RenderDiagnosticKind.JpxDecodeDeviation
                && r.Detail.Contains("Component Mapping box without a Palette box", StringComparison.Ordinal));
    }

    // ── the dictionary's colour space and what the raw path cannot carry ──

    private static JpxLosslessFixtures.Fixture GrayAlpha => JpxLosslessFixtures.GrayAlpha40x24_OneLevel;

    /// <summary>A 4-bit codestream under an Indexed space: index 1 paints entry 1, not entry 17 — the samples are indices, never narrowed. The control reads them as grey, narrowed.</summary>
    [Fact]
    public void Raster_FourBitIndexedJpx_ReadsTheIndicesVerbatim()
    {
        const int size = 8;
        var indices = new byte[size * size];
        for (int i = 0; i < indices.Length; i++) indices[i] = (byte)((i % size + i / size) & 15);
        byte[] j2k = JpxImageEncoding.Encode(indices, size, size, 1, 4, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 1, Jp2Container = false });
        var lookup = new StringBuilder();
        for (int i = 0; i < 16; i++) lookup.Append((i * 16).ToString("X2")).Append((255 - i * 16).ToString("X2")).Append((i * 7).ToString("X2"));

        using SKBitmap page = Render(ImagePdf(j2k, size, size, $"/ColorSpace [/Indexed /DeviceRGB 15 <{lookup}>] /BitsPerComponent 4"));
        AssertEveryPixel(page, (x, y) =>
        {
            int i = indices[y * size + x];
            return new SKColor((byte)(i * 16), (byte)(255 - i * 16), (byte)(i * 7));
        });

        using SKBitmap grey = Render(ImagePdf(j2k, size, size, "/ColorSpace /DeviceGray /BitsPerComponent 4"));
        AssertEveryPixel(grey, (x, y) =>
        {
            byte v = (byte)(indices[y * size + x] * 17);
            return new SKColor(v, v, v);
        });
    }

    /// <summary>Indexed colour under /SMaskInData 1: the palette paints the colour on the raw path, then the opacity channel composites it over the page.</summary>
    [Fact]
    public void Raster_IndexedColourSpaceWithSMaskInData1_CompositesThePaletteColourUnderTheOpacity()
    {
        var lookup = new StringBuilder();
        for (int i = 0; i < 256; i++) lookup.Append((255 - i).ToString("X2")).Append(i.ToString("X2")).Append((i ^ 0x55).ToString("X2"));
        string cs = $"/ColorSpace [/Indexed /DeviceRGB 255 <{lookup}>] /BitsPerComponent 8 /SMaskInData 1";

        using SKBitmap page = Render(ImagePdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, cs));
        AssertEveryPixel(page, (x, y) =>
        {
            int i = y * GrayAlpha.Width + x;
            int index = GrayAlpha.Planes[0][i], a = GrayAlpha.Planes[1][i];
            return new SKColor(Over(255 - index, a), Over(index, a), Over(index ^ 0x55, a));
        }, tolerance: 3);
        AssertAlphaWasPotent(GrayAlpha.Planes[1]);
    }

    /// <summary>A Separation space under /SMaskInData 1: the tint transform paints, then the opacity composites — the same rule as Indexed.</summary>
    [Fact]
    public void Raster_SeparationColourSpaceWithSMaskInData1_TintsThenComposites()
    {
        const string cs = "/ColorSpace [/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>] /BitsPerComponent 8 /SMaskInData 1";
        using SKBitmap page = Render(ImagePdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, cs));
        AssertEveryPixel(page, (x, y) =>
        {
            int i = y * GrayAlpha.Width + x;
            int tint = GrayAlpha.Planes[0][i], a = GrayAlpha.Planes[1][i];
            byte gb = (byte)(255 - tint);   // C0 + t (C1 − C0): green and blue fall with the tint
            return new SKColor(Over(255, a), Over(gb, a), Over(gb, a));
        }, tolerance: 3);
        AssertAlphaWasPotent(GrayAlpha.Planes[1]);
    }

    /// <summary>
    /// DeviceCMYK with an opacity channel: built as colour plus alpha, and — §7.4.9, the
    /// dictionary's space is present — the inverting /Decode applies to the four samples before
    /// they are converted (the file-CMYK exception is for a file's own CMYK). The control without
    /// the array paints the samples as they are.
    /// </summary>
    [Fact]
    public void Raster_DeviceCmykWithSMaskInData1AndAnInvertingDecode_AppliesTheDecodeThenTheOpacity()
    {
        const int w = 16, h = 8;
        var samples = new byte[w * h * 5];
        for (int i = 0; i < w * h; i++)
        {
            samples[i * 5] = (byte)(i * 3); samples[i * 5 + 1] = (byte)(255 - i); samples[i * 5 + 2] = (byte)(i * 5);
            samples[i * 5 + 3] = (byte)(i / 2); samples[i * 5 + 4] = (byte)(i * 2);
        }
        byte[] jp2 = JpxImageEncoding.Encode(samples, w, h, 5, 8, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 1, Jp2Container = true, OpacityChannel = true });

        SKColor Expected(int x, int y, bool invert)
        {
            int o = (y * w + x) * 5;
            float c = samples[o] / 255f, m = samples[o + 1] / 255f, yy = samples[o + 2] / 255f, k = samples[o + 3] / 255f;
            if (invert) { c = 1 - c; m = 1 - m; yy = 1 - yy; k = 1 - k; }
            SKColor rgb = PdfColor.CmykToRgb(c, m, yy, k);
            int a = samples[o + 4];
            return new SKColor(Over(rgb.Red, a), Over(rgb.Green, a), Over(rgb.Blue, a));
        }

        using SKBitmap inverted = Render(ImagePdf(jp2, w, h, "/ColorSpace /DeviceCMYK /BitsPerComponent 8 /SMaskInData 1 /Decode [1 0 1 0 1 0 1 0]"));
        AssertEveryPixel(inverted, (x, y) => Expected(x, y, invert: true), tolerance: 3);

        using SKBitmap plain = Render(ImagePdf(jp2, w, h, "/ColorSpace /DeviceCMYK /BitsPerComponent 8 /SMaskInData 1"));
        AssertEveryPixel(plain, (x, y) => Expected(x, y, invert: false), tolerance: 3);
    }

    /// <summary>/SMaskInData 2 on a dictionary space the raw path paints: the opacity is divided out of the samples before the paint, so the tint is the straight one.</summary>
    [Fact]
    public void Raster_SMaskInData2OnADictionaryColourSpace_DividesTheOpacityOutBeforeThePaint()
    {
        const string cs = "/ColorSpace [/Separation /Spot /DeviceGray << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>] /BitsPerComponent 8";
        using SKBitmap premultiplied = Render(ImagePdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, cs + " /SMaskInData 2"));
        AssertEveryPixel(premultiplied, (x, y) =>
        {
            int i = y * GrayAlpha.Width + x;
            int t = GrayAlpha.Planes[0][i], a = GrayAlpha.Planes[1][i];
            int straight = a == 0 ? 0 : Math.Min(255, (t * 255 + a / 2) / a);
            byte g = (byte)(255 - straight);
            return new SKColor(Over(g, a), Over(g, a), Over(g, a));
        }, tolerance: 3);

        // Potency: read straight (/SMaskInData 1) the same bytes paint differently.
        using SKBitmap straightRender = Render(ImagePdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, cs + " /SMaskInData 1"));
        int differ = 0;
        for (int y = 0; y < GrayAlpha.Height; y++)
            for (int x = 0; x < GrayAlpha.Width; x++)
                if (!PixelAssert.Near(premultiplied.GetPixel(x, y), straightRender.GetPixel(x, y), 3)) differ++;
        Assert.True(differ > 100, $"only {differ} pixels differ between /SMaskInData 1 and 2 on the raw path");
    }

    /// <summary>
    /// The two renderers on the dictionary arms: the SVG's PNG composited over white is the
    /// raster page — Indexed, Indexed with opacity, Lab, ICCBased, and a premultiplied channel on
    /// the bitmap path.
    /// </summary>
    [Theory]
    [InlineData("indexed")]
    [InlineData("indexed+opacity")]
    [InlineData("separation+opacity")]
    [InlineData("lab")]
    [InlineData("icc")]
    [InlineData("premultiplied")]
    public void Svg_AgreesWithTheRasterRender_OnTheDictionaryArms(string arm)
    {
        byte[] pdf = ArmPdf(arm);
        using SKBitmap raster = Render(pdf);
        using SKBitmap embedded = DecodeFirstEmbeddedPng(Svg(pdf));
        Assert.Equal((raster.Width, raster.Height), (embedded.Width, embedded.Height));
        AssertEveryPixel(raster, (x, y) =>
        {
            SKColor c = embedded.GetPixel(x, y);
            return new SKColor(Over(c.Red, c.Alpha), Over(c.Green, c.Alpha), Over(c.Blue, c.Alpha));
        }, tolerance: 4);

        // Potency: the page is painted, so agreement is about pixels rather than about blankness.
        int ink = 0;
        for (int y = 0; y < raster.Height; y++)
            for (int x = 0; x < raster.Width; x++)
                if (!PixelAssert.IsWhite(raster.GetPixel(x, y))) ink++;
        Assert.True(ink > raster.Width * raster.Height / 4, $"only {ink} pixels are painted");
    }

    private static byte[] ArmPdf(string arm)
    {
        var lookup = new StringBuilder();
        for (int i = 0; i < 256; i++) lookup.Append((255 - i).ToString("X2")).Append(i.ToString("X2")).Append((i ^ 0x55).ToString("X2"));
        string indexed = $"/ColorSpace [/Indexed /DeviceRGB 255 <{lookup}>] /BitsPerComponent 8";
        switch (arm)
        {
            case "indexed": return ImagePdf(Gray.Codestream, Gray.Width, Gray.Height, indexed);
            case "indexed+opacity": return ImagePdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, indexed + " /SMaskInData 1");
            case "separation+opacity":
                return ImagePdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height,
                    "/ColorSpace [/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0 1] /N 1 >>] /BitsPerComponent 8 /SMaskInData 1");
            case "lab": return ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "/ColorSpace [/Lab << /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>] /BitsPerComponent 8");
            case "icc":
                return Pdf(Rgb.Width, Rgb.Height, $"q {Rgb.Width} 0 0 {Rgb.Height} 0 0 cm /Im0 Do Q",
                    ($"<< /Type /XObject /Subtype /Image /Width {Rgb.Width} /Height {Rgb.Height} /ColorSpace [/ICCBased 6 0 R] /BitsPerComponent 8 /Filter /JPXDecode", Rgb.Codestream),
                    ("<< /N 3", H.IccProfile("RGB ")));
            case "premultiplied": return ImagePdf(RgbaJp2(), Rgba.Width, Rgba.Height, "/SMaskInData 2");
            default: throw new ArgumentOutOfRangeException(nameof(arm), arm, null);
        }
    }

    private static void AssertAlphaWasPotent(byte[] plane)
    {
        int transparent = 0;
        foreach (byte a in plane) if (a < 128) transparent++;
        Assert.True(transparent > 100, $"only {transparent} samples are more than half transparent; the fixture cannot show compositing");
    }

    // ── helpers (the PDFs and readers are JpxTestPdf's, shared with the other JPX suites) ──

    private static SKColor Pixel(JpxLosslessFixtures.Fixture f, int x, int y) => JpxTestPdf.Pixel(f, x, y);
    private static SKBitmap Render(byte[] pdf) => JpxTestPdf.Render(pdf);
    private static string Svg(byte[] pdf) => JpxTestPdf.Svg(pdf);
    private static SKBitmap DecodeFirstEmbeddedPng(string svg) => JpxTestPdf.DecodeFirstEmbeddedPng(svg);
    private static byte[] Jp2(JpxLosslessFixtures.Fixture f, params byte[][] headerBoxes) => JpxTestPdf.Jp2(f, headerBoxes);
    private static byte[] ImagePdf(byte[] jpx, int width, int height, string extras) => JpxTestPdf.ImagePdf(jpx, width, height, extras);
    private static byte[] Pdf(int pageWidth, int pageHeight, string content, params (string dict, byte[] stream)[] images) =>
        JpxTestPdf.Pdf(pageWidth, pageHeight, content, images);
    private static void AssertEveryPixel(SKBitmap page, Func<int, int, SKColor> expected, int tolerance = 0) =>
        JpxTestPdf.AssertEveryPixel(page, expected, tolerance);

    private static byte Clip(double v)
    {
        double r = Math.Round(v, MidpointRounding.AwayFromZero);
        return r < 0 ? (byte)0 : r > 255 ? (byte)255 : (byte)r;
    }

    /// <summary>A straight sample at opacity <paramref name="a"/> composited over the white page.</summary>
    private static byte Over(int c, int a) => (byte)((c * a + 255 * (255 - a) + 127) / 255);

    /// <summary>A premultiplied sample: divided out (clipped at full strength), then composited over white — which is the sample itself, capped at its alpha, plus the page.</summary>
    private static byte OverPremultiplied(int cPremultiplied, int a) => (byte)Math.Min(255, Math.Min(cPremultiplied, a) + 255 - a);

    private static void AssertAlphaWasPotent()
    {
        // The fixture's alpha plane spans the range, so the composite differs from the opaque paint.
        int transparent = 0;
        foreach (byte a in Rgba.Planes[3]) if (a < 128) transparent++;
        Assert.True(transparent > 100, $"only {transparent} samples are more than half transparent; the fixture cannot show compositing");
    }

    /// <summary>The RGBA fixture as sRGB with a cdef typing the fourth channel as whole-image opacity.</summary>
    private static byte[] RgbaJp2() =>
        Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)));

    /// <summary>A 256-entry palette of three 8-bit columns: entry i is (i, 255 − i, (7 i) mod 256).</summary>
    private static byte[] Palette()
    {
        var entries = new long[256][];
        for (int i = 0; i < 256; i++) entries[i] = new long[] { i, 255 - i, (7 * i) & 0xFF };
        return H.Pclr(new byte[] { 7, 7, 7 }, entries);
    }

    private sealed class CountingJpxPlugin : IImageDecodePlugin
    {
        public string Name => "Test.CountingJpxFallback";
        public string Description => "counts how often it is offered an image";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public int CallCount { get; private set; }
        public IReadOnlyList<string> SupportedFilters { get; } = new[] { "JPXDecode" };

        public bool TryDecode(string filterName, byte[] data, PdfDictionary imageDict,
            out int width, out int height, out int components, out byte[] pixels)
        {
            CallCount++;
            width = height = components = 0;
            pixels = null;
            return false;
        }
    }
}
