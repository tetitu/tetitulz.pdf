// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// End-to-end tests for two JPEG 2000 defects that only show up in rendered pixels:
/// <list type="bullet">
///   <item><c>/SMaskInData</c> ignored, so an opacity channel carried inside the codestream is
///         dropped (or, on the magic-byte path, written into an Opaque bitmap whose alpha Skia
///         discards).</item>
///   <item>A four-channel image read as CMYK by one decode path and as RGB + alpha by the other,
///         so the same bytes produce different colours.</item>
/// </list>
/// The SVG renderer is covered too: the cat photo comes from the in-house decoder on both paths,
/// so PDF→SVG export cannot silently produce no image while page rendering draws one.
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class Jpeg2000ChannelLayoutRenderTests
{
    /// <summary>
    /// A stand-in JPEG 2000 codec: ignores the payload and returns a fixed sample buffer, so the
    /// test controls exactly what channels reach the renderer.
    /// </summary>
    private sealed class FixedSamplesJpxPlugin : IImageDecodePlugin
    {
        private readonly int _w, _h, _comps;
        private readonly byte[] _samples;

        public FixedSamplesJpxPlugin(int w, int h, int comps, byte[] samples)
        {
            _w = w; _h = h; _comps = comps; _samples = samples;
        }

        public string Name        => "Test.FixedSamplesJpx";
        public string Description => "Returns a fixed JPXDecode sample buffer.";
        public Version Version    => new Version(1, 0, 0);
        public IReadOnlyList<string> SupportedFilters { get; } = new[] { "JPXDecode" };
        public void Initialize() { }
        public void Shutdown() { }

        public bool TryDecode(string filterName, byte[] data, PdfDictionary parameters,
            out int width, out int height, out int components, out byte[] pixels)
        {
            width = _w; height = _h; components = _comps; pixels = _samples;
            return true;
        }
    }

    private static void WithPlugin(IPdfPlugin plugin, Action body)
    {
        bool already = PdfPluginHost.Instance.IsRegistered(plugin.Name);
        if (!already) PdfPluginHost.Instance.Register(plugin);
        try { body(); }
        finally { if (!already) PdfPluginHost.Instance.Unregister(plugin.Name); }
    }

    // ── /SMaskInData ────────────────────────────────────────────────────────

    [Fact]
    public void SMaskInData1_TransparentPixelsLetThePageBackgroundThrough()
    {
        // 2x1 image: opaque red, then a fully transparent pixel. Drawn over a white page, the
        // right half must stay white. Before /SMaskInData was honoured the fourth channel was
        // read as the K of a CMYK pixel, painting the right half black.
        byte[] samples = { 255, 0, 0, 255,   0, 0, 0, 0 };
        byte[] pdf = BuildJpxPdf(2, 1, extraEntries: "/ColorSpace /DeviceRGB /SMaskInData 1");

        WithPlugin(new FixedSamplesJpxPlugin(2, 1, 4, samples), () =>
        {
            using var bmp = RenderFirstPage(pdf);

            SKColor left  = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
            SKColor right = bmp.GetPixel(bmp.Width * 3 / 4, bmp.Height / 2);

            Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60,
                $"left half should be the opaque red sample, was {left}");
            Assert.True(right.Red > 230 && right.Green > 230 && right.Blue > 230,
                $"right half should show the white page through the transparent sample, was {right}");
        });
    }

    [Fact]
    public void SMaskInData2_PremultipliedSamplesKeepTheirColour()
    {
        // Full red at 50% alpha stored premultiplied is (128,0,0,128). Composited over white it
        // is ~(255,127,127). Treating the stored value as straight colour would darken it to
        // ~(191,127,127) — the un-premultiply step is what this pins.
        byte[] samples = { 128, 0, 0, 128 };
        byte[] pdf = BuildJpxPdf(1, 1, extraEntries: "/ColorSpace /DeviceRGB /SMaskInData 2");

        WithPlugin(new FixedSamplesJpxPlugin(1, 1, 4, samples), () =>
        {
            using var bmp = RenderFirstPage(pdf);
            SKColor c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

            Assert.True(c.Red > 235, $"expected the full-strength red to survive, got {c}");
            Assert.InRange(c.Green, 100, 160);
            Assert.InRange(c.Blue,  100, 160);
        });
    }

    [Fact]
    public void NoSMaskInData_FourthChannelIsIgnoredNotPaintedAsBlack()
    {
        // Same RGBA buffer, but the PDF says nothing about an opacity channel. The spec default
        // is "ignore it", and /ColorSpace /DeviceRGB says only three channels are colour — so
        // both pixels render as their RGB values, fully opaque.
        byte[] samples = { 255, 0, 0, 0,   0, 0, 255, 0 };
        byte[] pdf = BuildJpxPdf(2, 1, extraEntries: "/ColorSpace /DeviceRGB");

        WithPlugin(new FixedSamplesJpxPlugin(2, 1, 4, samples), () =>
        {
            using var bmp = RenderFirstPage(pdf);

            SKColor left  = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
            SKColor right = bmp.GetPixel(bmp.Width * 3 / 4, bmp.Height / 2);

            Assert.True(left.Red > 200 && left.Blue < 60, $"left should be red, was {left}");
            Assert.True(right.Blue > 200 && right.Red < 60, $"right should be blue, was {right}");
        });
    }

    // ── four-channel disambiguation ─────────────────────────────────────────

    [Fact]
    public void FourChannelsDeclaredCmyk_StillRenderAsCmyk()
    {
        // Pure cyan: the four-channel buffer must not be mistaken for RGB + alpha now that the
        // opacity reading exists.
        byte[] samples = { 255, 0, 0, 0 };
        byte[] pdf = BuildJpxPdf(1, 1, extraEntries: "/ColorSpace /DeviceCMYK");

        WithPlugin(new FixedSamplesJpxPlugin(1, 1, 4, samples), () =>
        {
            using var bmp = RenderFirstPage(pdf);
            SKColor c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

            Assert.True(c.Red < 40, $"cyan has no red, got {c}");
            Assert.True(c.Green > 200 && c.Blue > 200, $"cyan should be bright in G and B, got {c}");
        });
    }

    [Fact]
    public void PluginPathAndMagicBytePathAgreeOnFourChannels()
    {
        // The same image decoded through the declared-filter dispatch and through the
        // JP2/J2C sniff must produce the same pixel, not one reading CMYK and the other
        // RGB + alpha.
        byte[] samples = { 255, 0, 0, 255 };
        byte[] declared = BuildJpxPdf(1, 1, extraEntries: "/ColorSpace /DeviceRGB /SMaskInData 1");
        byte[] sniffed  = BuildJpxPdf(1, 1, extraEntries: "/ColorSpace /DeviceRGB /SMaskInData 1",
                                      declareJpxFilter: false);

        WithPlugin(new FixedSamplesJpxPlugin(1, 1, 4, samples), () =>
        {
            using var viaFilter = RenderFirstPage(declared);
            using var viaSniff  = RenderFirstPage(sniffed);

            SKColor a = viaFilter.GetPixel(viaFilter.Width / 2, viaFilter.Height / 2);
            SKColor b = viaSniff.GetPixel(viaSniff.Width / 2, viaSniff.Height / 2);

            Assert.True(a.Red > 200 && a.Green < 60, $"declared-filter path gave {a}");
            Assert.Equal(a, b);
        });
    }

    // ── SVG export ──────────────────────────────────────────────────────────

    [Fact]
    public void SvgExport_RendersTheCatPhoto_InHouseWithNoPluginRegistered()
    {
        // The exported SVG must carry a PNG data URI that decodes to the photo — from the
        // in-house decoder, the one codec, as the raster path does.
        byte[] pdf = BuildCatPdf();
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            Assert.Null(PdfPluginHost.Instance.FindDecodePlugin("JPXDecode"));
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

            using SKBitmap image = DecodeFirstEmbeddedPng(svg);
            Assert.NotNull(image);

            int nonWhite = 0, dark = 0;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    var c = image.GetPixel(x, y);
                    if (c.Red < 235 || c.Green < 235 || c.Blue < 235) nonWhite++;
                    if (c.Red < 80 && c.Green < 80 && c.Blue < 80) dark++;
                }

            Assert.True(nonWhite > 1500, $"expected the cat photo in the SVG, only {nonWhite} non-white px");
            Assert.True(dark > 200, $"expected the dark fur, only {dark} dark px");
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }

    [Fact]
    public void SvgExport_HonoursSMaskInData()
    {
        // Transparency has to survive the PNG data URI too, not just the raster page.
        byte[] samples = { 255, 0, 0, 255,   0, 0, 0, 0 };
        byte[] pdf = BuildJpxPdf(2, 1, extraEntries: "/ColorSpace /DeviceRGB /SMaskInData 1");

        WithPlugin(new FixedSamplesJpxPlugin(2, 1, 4, samples), () =>
        {
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

            using SKBitmap image = DecodeFirstEmbeddedPng(svg);
            Assert.NotNull(image);
            Assert.Equal(2, image.Width);

            Assert.Equal(255, image.GetPixel(0, 0).Alpha);
            Assert.True(image.GetPixel(0, 0).Red > 200, $"left pixel was {image.GetPixel(0, 0)}");
            Assert.Equal(0, image.GetPixel(1, 0).Alpha);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static SKBitmap RenderFirstPage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static SKBitmap DecodeFirstEmbeddedPng(string svg)
    {
        const string marker = "data:image/png;base64,";
        int i = svg.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(i >= 0, "the SVG contains no PNG data URI — the image was not decoded");
        int start = i + marker.Length;
        int end = svg.IndexOfAny(new[] { '"', '\'' }, start);
        Assert.True(end > start, "malformed data URI in the SVG");
        return SKBitmap.Decode(Convert.FromBase64String(svg.Substring(start, end - start)));
    }

    /// <summary>
    /// A one-page PDF whose only content is a JPX image scaled over the whole 20x20 page.
    /// The stream payload is a JP2 signature box so the magic-byte path can find it even when
    /// <paramref name="declareJpxFilter"/> hides the filter name.
    /// </summary>
    private static byte[] BuildJpxPdf(int width, int height, string extraEntries,
        bool declareJpxFilter = true)
    {
        // 12-byte JP2 signature box + a token codestream: enough for detection; the test plugin
        // ignores the bytes.
        byte[] payload =
        {
            0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A,
            0xFF, 0x4F, 0xFF, 0x51,
        };
        var hex = new StringBuilder(payload.Length * 2 + 1);
        foreach (byte b in payload) hex.Append(b.ToString("X2"));
        hex.Append('>');

        // Without /JPXDecode the stream is plain ASCIIHex, so DecodeToBitmap sees the JP2 magic
        // bytes and takes the sniffing path instead of the declared-filter dispatch.
        string filter = declareJpxFilter ? "[/ASCIIHexDecode /JPXDecode]" : "/ASCIIHexDecode";
        string content = "q 20 0 0 20 0 0 cm /Im0 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 20]" +
                  " /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width {0} /Height {1} " +
            "/BitsPerComponent 8 {2} /Filter {3} /Length {4} >>\nstream\n{5}\nendstream\nendobj\n",
            width, height, extraEntries, filter, hex.Length, hex);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildCatPdf()
    {
        byte[] jp2 = CatJp2.Bytes;
        var hex = new StringBuilder(jp2.Length * 2 + 1);
        foreach (byte b in jp2) hex.Append(b.ToString("X2"));
        hex.Append('>');

        string content = "q 100 0 0 67 0 0 cm /Im0 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 67]" +
                  " /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width {0} /Height {1} " +
            "/ColorSpace /DeviceRGB /BitsPerComponent 8 " +
            "/Filter [/ASCIIHexDecode /JPXDecode] /Length {2} >>\nstream\n{3}\nendstream\nendobj\n",
            CatJp2.Width, CatJp2.Height,
            hex.Length, hex);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
