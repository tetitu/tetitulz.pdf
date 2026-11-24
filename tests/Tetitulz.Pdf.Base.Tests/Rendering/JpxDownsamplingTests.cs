// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>PdfRenderOptions.DownsampleImagesToTarget</c> for a <c>JPXDecode</c> image: the in-house
/// decoder stops as many resolution levels short as the device target allows
/// (<see cref="ImageDownsampling.HalvingsFor"/>), which skips the finer sub-bands' tier-1 work
/// rather than decoding everything and shrinking afterwards. The properties that matter are the
/// ones <see cref="ImageDownsamplingTests"/> holds for JPEG and raw samples: off by default and
/// then unchanged, and when on a reduced decode that still covers the target and still paints the
/// right picture — on the bitmap path and on the raw sample path (a <c>/ColorSpace</c> in the
/// dictionary), whose geometry the reduced decode rewrites.
/// </summary>
/// <remarks>
/// Joins <see cref="TestInfrastructure.PluginHostCollection"/> and resets the plugin host, so the
/// decode is the in-house decoder's and not a registered plugin's, which decodes at native size.
/// </remarks>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class JpxDownsamplingTests : IDisposable
{
    private const int Dpi = 96;
    private const int Native = 512;

    public JpxDownsamplingTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>
    /// A 512-px image drawn 24 pt square at 96 dpi is a 32-px target (33 as the CTM's float
    /// arithmetic ceils it): one level short (256 px) keeps the 2x headroom's floor of 256, two
    /// would not. The reported numbers are parsed so an
    /// absurd size cannot pass; the shared rule pins the exact reduction.
    /// </summary>
    [Theory]
    [InlineData("")]                                             // the codestream's own colour: the bitmap path
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8")]   // the dictionary's: the raw sample path
    public void WhenOn_TheDecodeStopsOneLevelShort_AndStillCoversTheTarget(string extras)
    {
        List<string> reported = Listen(() => Render(TwoHalvesDrawnSmall(extras), new PdfRenderOptions { DownsampleImagesToTarget = true }));

        string detail = Assert.Single(reported);
        Match m = Regex.Match(detail, @"decoded at (\d+)x(\d+) instead of (\d+)x(\d+) for a (\d+)x(\d+)");
        Assert.True(m.Success, $"unexpected diagnostic shape: {detail}");
        Assert.StartsWith("JPEG 2000 image decoded", detail, StringComparison.Ordinal);
        int decoded = int.Parse(m.Groups[1].Value), native = int.Parse(m.Groups[3].Value), target = int.Parse(m.Groups[5].Value);
        Assert.Equal((256, Native), (decoded, native));
        Assert.InRange(target, 32, 33);   // 24 pt at 96 dpi is 32 px; the CTM's float column length can ceil to 33
        Assert.True(decoded >= target, $"decoded {decoded} must still cover the {target}px device target");
    }

    /// <summary>
    /// The picture: the left half red, the right half blue, so a reduced decode that assembled the
    /// wrong tile, the wrong plane, or nothing shows. Both paths, against the full decode as the control.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("/ColorSpace /DeviceRGB /BitsPerComponent 8")]
    public void WhenOn_TheReducedImageStillPaintsBothHalves(string extras)
    {
        byte[] pdf = TwoHalvesDrawnSmall(extras);
        using SKBitmap on = Decode(Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        using SKBitmap off = Decode(Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false }));
        // Drawn at (40, 700), 24 pt square: the left quarter at x = 46 pt, the right at 58 pt.
        foreach (SKBitmap page in new[] { on, off })
        {
            SKColor left = page.GetPixel(X(46), Y(page, 712)), right = page.GetPixel(X(58), Y(page, 712));
            Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60, $"the left half should be red, was {left} ({page == on})");
            Assert.True(right.Blue > 200 && right.Red < 60 && right.Green < 60, $"the right half should be blue, was {right} ({page == on})");
        }
    }

    /// <summary>
    /// Off, and by default, nothing moves: the same bytes as before the option, and no reduction
    /// reported — the same render with the option on reports one, which is the potency of the
    /// empty list.
    /// </summary>
    [Fact]
    public void WhenOff_AJpxRenderIsUnchangedAndNothingIsReported()
    {
        byte[] pdf = TwoHalvesDrawnSmall("");
        byte[] withDefaults = null;
        List<string> off = Listen(() =>
        {
            withDefaults = Render(pdf, null);
            Assert.Equal(withDefaults, Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false }));
        });
        Assert.Empty(off);
        List<string> on = Listen(() => Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        Assert.Single(on);
    }

    /// <summary>
    /// A stencil mask is never reduced: its bits are read by the dictionary's geometry and the raw
    /// path leaves a mask alone too. The mask still paints (its 0 bits, the left half, in the fill
    /// colour) and nothing is reported.
    /// </summary>
    [Fact]
    public void StencilMask_IsNotReduced()
    {
        var bits = new byte[Native * Native];
        for (int y = 0; y < Native; y++)
            for (int x = 0; x < Native; x++) bits[y * Native + x] = x < Native / 2 ? (byte)0 : (byte)1;
        byte[] jpx = JpxImageEncoding.Encode(bits, Native, Native, 1, 1, new JpxEncodingOptions { Lossless = true, Jp2Container = false });
        byte[] pdf = JpxTestPdf.Pdf(612, 792, "q 0 0.8 0 rg 24 0 0 24 40 700 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Native} /Height {Native} /ImageMask true /Filter /JPXDecode", jpx));

        byte[] png = null;
        List<string> reported = Listen(() => png = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        Assert.Empty(reported);
        using SKBitmap page = Decode(png);
        SKColor left = page.GetPixel(X(46), Y(page, 712)), right = page.GetPixel(X(58), Y(page, 712));
        Assert.True(left.Green > 150 && left.Red < 80, $"the mask's painted half should be green, was {left}");
        Assert.True(right.Red > 240 && right.Green > 240 && right.Blue > 240, $"the unpainted half should show the page, was {right}");
    }

    /// <summary>
    /// A JPEG 2000 <c>/SMask</c> goes through the same decode as the base image, so it is reduced
    /// by the same rule — two reductions reported — and the reduced mask is still what shapes the
    /// base: the mask's opaque left half shows the base's red, its transparent right half the page.
    /// </summary>
    [Fact]
    public void WhenOn_AJpxSoftMask_IsReducedWithTheBaseImage_AndStillShapesIt()
    {
        var red = new byte[Native * Native * 3];
        for (int i = 0; i < red.Length; i += 3) red[i] = 255;
        byte[] baseJpx = JpxImageEncoding.Encode(red, Native, Native, 3, 8, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 5, Jp2Container = false });
        var alpha = new byte[Native * Native];
        for (int y = 0; y < Native; y++)
            for (int x = 0; x < Native / 2; x++) alpha[y * Native + x] = 255;
        byte[] maskJpx = JpxImageEncoding.Encode(alpha, Native, Native, 1, 8, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 5, Jp2Container = false });
        byte[] pdf = JpxTestPdf.Pdf(612, 792, "q 24 0 0 24 40 700 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Native} /Height {Native} /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R /Filter /JPXDecode", baseJpx),
            ($"<< /Type /XObject /Subtype /Image /Width {Native} /Height {Native} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", maskJpx));

        byte[] png = null;
        List<string> reported = Listen(() => png = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        Assert.Equal(2, reported.Count);
        Assert.All(reported, d => Assert.Contains($"decoded at 256x256 instead of {Native}x{Native}", d, StringComparison.Ordinal));
        using SKBitmap page = Decode(png);
        SKColor left = page.GetPixel(X(46), Y(page, 712)), right = page.GetPixel(X(58), Y(page, 712));
        Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60, $"under the opaque half the base should show red, was {left}");
        Assert.True(right.Red > 240 && right.Green > 240 && right.Blue > 240, $"under the transparent half the page should show, was {right}");

        // The control: the full decode paints the same picture, and reports nothing.
        byte[] off = null;
        Assert.Empty(Listen(() => off = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false })));
        using SKBitmap control = Decode(off);
        Assert.True(control.GetPixel(X(46), Y(control, 712)).Red > 200 && control.GetPixel(X(58), Y(control, 712)).Green > 240);
    }

    /// <summary>
    /// The colour key (§8.9.6.4) of a reduced decode is evaluated at the decoded geometry: an
    /// Indexed image whose right half is index 1, keyed out by <c>/Mask [1 1]</c>, drawn a
    /// quarter size, shows the page there — handed the XObject's 512x512, the key would decline the
    /// 256x256 bitmap and the keyed half paint blue. The control is the full decode.
    /// </summary>
    [Fact]
    public void WhenOn_AColourKeyOnAnIndexedJpx_IsEvaluatedAtTheReducedGeometry()
    {
        var indices = new byte[Native * Native];
        for (int y = 0; y < Native; y++)
            for (int x = Native / 2; x < Native; x++) indices[y * Native + x] = 1;
        byte[] jpx = JpxImageEncoding.Encode(indices, Native, Native, 1, 8, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 5, Jp2Container = false });
        byte[] pdf = JpxTestPdf.Pdf(612, 792, "q 24 0 0 24 40 700 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Native} /Height {Native} /ColorSpace [/Indexed /DeviceRGB 1 <FF00000000FF>] /BitsPerComponent 8 /Mask [1 1] /Filter /JPXDecode", jpx));

        byte[] png = null;
        List<string> reported = Listen(() => png = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        Assert.Contains(reported, d => d.Contains($"decoded at 256x256 instead of {Native}x{Native}", StringComparison.Ordinal));
        using SKBitmap page = Decode(png);
        SKColor left = page.GetPixel(X(46), Y(page, 712)), right = page.GetPixel(X(58), Y(page, 712));
        Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60, $"the unkeyed half should paint the palette's red, was {left}");
        Assert.True(right.Red > 240 && right.Green > 240 && right.Blue > 240, $"the keyed half should show the page, was {right}");

        byte[] off = null;
        Assert.Empty(Listen(() => off = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false })));
        using SKBitmap control = Decode(off);
        Assert.True(control.GetPixel(X(46), Y(control, 712)).Red > 200 && control.GetPixel(X(58), Y(control, 712)).Green > 240,
            $"the full decode: {control.GetPixel(X(46), Y(control, 712))} / {control.GetPixel(X(58), Y(control, 712))}");
    }

    /// <summary>
    /// An opacity channel the raw path cannot carry (an Indexed image under /SMaskInData 1) is
    /// composited at the reduced decode's geometry, and the reduced samples are not decimated a
    /// second time behind the opacity plane's back: a 1024-px image with one decomposition level
    /// is decoded one level short (512 px), which the raw path's own rule would still halve to
    /// 256 — decimated, the plane no longer matched the bitmap and the image painted opaque,
    /// blue on the transparent half. One reduction is reported, the decoder's.
    /// </summary>
    [Fact]
    public void WhenOn_AnIndexedJpxWithAnOpacityChannel_KeepsTheOpacityAtTheReducedGeometry()
    {
        const int size = 2 * Native;
        var samples = new byte[size * size * 2];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int at = (y * size + x) * 2;
                if (x < size / 2) { samples[at] = 0; samples[at + 1] = 255; }
                else { samples[at] = 1; samples[at + 1] = 0; }
            }
        byte[] jp2 = JpxImageEncoding.Encode(samples, size, size, 2, 8, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 1, Jp2Container = true, OpacityChannel = true });
        byte[] pdf = JpxTestPdf.Pdf(612, 792, "q 24 0 0 24 40 700 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {size} /Height {size} /ColorSpace [/Indexed /DeviceRGB 1 <FF00000000FF>] /BitsPerComponent 8 /SMaskInData 1 /Filter /JPXDecode", jp2));

        byte[] png = null;
        List<string> reported = Listen(() => png = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        string detail = Assert.Single(reported);
        Assert.Contains($"decoded at {Native}x{Native} instead of {size}x{size}", detail, StringComparison.Ordinal);
        using SKBitmap page = Decode(png);
        SKColor left = page.GetPixel(X(46), Y(page, 712)), right = page.GetPixel(X(58), Y(page, 712));
        Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60, $"the opaque half should paint the palette's red, was {left}");
        Assert.True(right.Red > 240 && right.Green > 240 && right.Blue > 240, $"the transparent half should show the page, was {right}");
    }

    /// <summary>
    /// A reduced decode whose build then declines — a /ColorSpace with a dictionary geometry unlike
    /// the codestream's, which the raw path refuses — reports no reduction: nothing was drawn at
    /// the reduced size (the review of 2026-09-26, row 30: it was reported before the build could
    /// decline). The control, at the codestream's geometry, reports the one reduction.
    /// </summary>
    [Fact]
    public void WhenOn_AReducedDecodeWhoseBuildDeclines_ReportsNoReduction()
    {
        byte[] jpx = TwoHalves();
        byte[] wrongSize = JpxTestPdf.Pdf(612, 792, "q 24 0 0 24 40 700 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Native - 12} /Height {Native - 12} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", jpx));
        Assert.Empty(Listen(() => Render(wrongSize, new PdfRenderOptions { DownsampleImagesToTarget = true })));

        byte[] rightSize = JpxTestPdf.Pdf(612, 792, "q 24 0 0 24 40 700 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Native} /Height {Native} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", jpx));
        Assert.Single(Listen(() => Render(rightSize, new PdfRenderOptions { DownsampleImagesToTarget = true })));
    }

    /// <summary>The decoder's own door: a device target reduces the bitmap, none decodes at native size, and the plugin is not what answered.</summary>
    [Fact]
    public void DecodeToBitmap_WithADeviceTarget_DecodesResolutionLevelsShort()
    {
        byte[] jpx = TwoHalves();
        using SKBitmap reduced = JpxImageDecoder.DecodeToBitmap(jpx, null, null, SKAlphaType.Premul, out JpxDecodeOutcome outcome, targetWidth: 32, targetHeight: 32);
        Assert.NotNull(reduced);
        Assert.True(outcome.FromInHouseDecoder);
        Assert.Equal((256, 256), (reduced.Width, reduced.Height));
        Assert.Equal(new SKColor(255, 0, 0), reduced.GetPixel(64, 128));
        Assert.Equal(new SKColor(0, 0, 255), reduced.GetPixel(192, 128));

        using SKBitmap full = JpxImageDecoder.DecodeToBitmap(jpx, null, null, SKAlphaType.Premul, out _);
        Assert.Equal((Native, Native), (full.Width, full.Height));
    }

    // ------------------------------------------------------------------ the SVG writer

    /// <summary>
    /// The SVG writer embeds a JPEG 2000 image decoded for its drawn size when
    /// <c>SvgImageDpi</c> names a resolution: 24 pt at 96 is 32 px, so one level short (256 px), as
    /// the raster path decodes it, reported the same way; and the picture is still both halves.
    /// </summary>
    [Fact]
    public void Svg_WithATargetResolution_EmbedsTheReducedDecode()
    {
        var options = new PdfRenderOptions { DownsampleImagesToTarget = true, SvgImageDpi = Dpi };
        string svg = null;
        List<string> reported = Listen(() => svg = RenderSvg(TwoHalvesDrawnSmall(""), options));

        using SKBitmap embedded = Assert.Single(EmbeddedImages(svg));
        Assert.Equal((256, 256), (embedded.Width, embedded.Height));
        Assert.StartsWith("JPEG 2000 image decoded at 256x256", Assert.Single(reported), StringComparison.Ordinal);
        Assert.Equal(new SKColor(255, 0, 0), embedded.GetPixel(64, 128));
        Assert.Equal(new SKColor(0, 0, 255), embedded.GetPixel(192, 128));
    }

    /// <summary>
    /// Without a target resolution — the default, or the switch off — the SVG writer embeds the
    /// image at its own size, as it always has, and reports nothing.
    /// </summary>
    [Theory]
    [InlineData(false, 96f)]
    [InlineData(true, 0f)]
    public void Svg_WithoutATarget_EmbedsTheImageAtItsOwnSize(bool downsample, float dpi)
    {
        var options = new PdfRenderOptions { DownsampleImagesToTarget = downsample, SvgImageDpi = dpi };
        string svg = null;
        List<string> reported = Listen(() => svg = RenderSvg(TwoHalvesDrawnSmall(""), options));

        using SKBitmap embedded = Assert.Single(EmbeddedImages(svg));
        Assert.Equal((Native, Native), (embedded.Width, embedded.Height));
        Assert.Empty(reported);
    }

    /// <summary>
    /// An image drawn twice at two sizes is decoded for each while the target is in effect — two
    /// embeddings, the larger draw's the larger — rather than defined once at the first draw's size.
    /// </summary>
    [Fact]
    public void Svg_AnImageDrawnTwiceIsDecodedForEachSize()
    {
        byte[] pdf = JpxTestPdf.Pdf(612, 792, "q 24 0 0 24 40 700 cm /Im0 Do Q q 100 0 0 100 40 400 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Native} /Height {Native} /Filter /JPXDecode", TwoHalves()));
        string svg = RenderSvg(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true, SvgImageDpi = Dpi });

        var images = EmbeddedImages(svg);
        try
        {
            Assert.Equal(2, images.Count);
            Assert.Equal(256, images[0].Width);    // 24 pt at 96: a 32 px target, one level short
            Assert.Equal(Native, images[1].Width); // 100 pt at 96: a 134 px target, no level to spare
        }
        finally { foreach (var b in images) b.Dispose(); }
    }

    private static string RenderSvg(byte[] pdf, PdfRenderOptions options)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0, options);
    }

    /// <summary>Every <c>&lt;image&gt;</c> the SVG embeds as a PNG data URI, decoded, in document order.</summary>
    private static List<SKBitmap> EmbeddedImages(string svg)
    {
        var doc = System.Xml.Linq.XDocument.Parse(svg);
        var result = new List<SKBitmap>();
        foreach (var image in doc.Descendants())
        {
            if (image.Name.LocalName != "image") continue;
            string href = null;
            foreach (var a in image.Attributes())
                if (a.Name.LocalName == "href") href = a.Value;
            const string prefix = "data:image/png;base64,";
            if (href == null || !href.StartsWith(prefix, StringComparison.Ordinal)) continue;
            result.Add(Decode(Convert.FromBase64String(href.Substring(prefix.Length))));
        }
        return result;
    }

    private static List<string> Listen(Action render)
    {
        // RenderDiagnostics.OnEvent is process-wide: filtered to the calling thread so the list is
        // about this render, not about every test rendering concurrently (see ImageDownsamplingTests).
        int ownThread = Environment.CurrentManagedThreadId;
        var reported = new List<string>();
        void Handler(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.ImageDownsampled) reported.Add(detail);
        }
        RenderDiagnostics.OnEvent += Handler;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Handler; }
        return reported;
    }

    private static byte[] Render(byte[] pdf, PdfRenderOptions options)
    {
        using var ms = new MemoryStream(pdf);
        return options == null ? PdfPageRenderer.RenderPage(ms, 0, Dpi) : PdfPageRenderer.RenderPage(ms, 0, Dpi, options);
    }

    private static int X(float userX) => (int)(userX * Dpi / 72f);
    private static int Y(SKBitmap b, float userY) => b.Height - 1 - (int)(userY * Dpi / 72f);

    private static SKBitmap Decode(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>A 512x512 lossless RGB codestream, five levels: the left half red, the right half blue.</summary>
    private static byte[] TwoHalves()
    {
        var samples = new byte[Native * Native * 3];
        for (int y = 0; y < Native; y++)
            for (int x = 0; x < Native; x++)
            {
                int at = (y * Native + x) * 3;
                if (x < Native / 2) samples[at] = 255; else samples[at + 2] = 255;
            }
        return JpxImageEncoding.Encode(samples, Native, Native, 3, 8, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 5, Jp2Container = false });
    }

    /// <summary>The two-halves image as an XObject drawn 24 pt square at (40, 700) on a Letter page.</summary>
    private static byte[] TwoHalvesDrawnSmall(string extras) =>
        JpxTestPdf.Pdf(612, 792, "q 24 0 0 24 40 700 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Native} /Height {Native} {extras} /Filter /JPXDecode", TwoHalves()));
}
