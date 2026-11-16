// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The pixels the SVG exporter actually embeds, decoded back out of its <c>data:</c> URI.
/// </summary>
/// <remarks>
/// <para><b>This path had no pixel-level coverage of any kind.</b> Swapping red and blue in
/// <c>SvgContentWriter</c>'s 8-bpc <c>/DeviceRGB</c> loop left the entire Base suite green — 3 107
/// tests — because an SVG whose embedded PNG has its channels transposed is still well-formed XML
/// containing a valid PNG of the right dimensions. Every existing assertion about exported images
/// checks structure or presence; none decodes one.</para>
///
/// <para>These are the raster paths a producer hits most: an unfiltered image in one of the three
/// device spaces. They are the <i>primary</i> routes, not fallbacks — a JPEG goes to Skia's decoder
/// instead — so a defect here is silently wrong output for ordinary documents, which is the
/// failure class CLAUDE.md records this repository hitting most often.</para>
///
/// <para><b>Why the source pattern is asymmetric on every axis.</b> Equal channels cannot detect a
/// channel swap, and a pattern symmetric in x or y cannot detect a transpose or a flip. Each
/// component here is a different function of both coordinates.</para>
/// </remarks>
public class SvgEmbeddedImagePixelTests
{
    private const int Size = 8;

    /// <summary>The exported PNG must carry the source's channels in the source's order.</summary>
    [Fact]
    public void DeviceRgbImageKeepsItsChannelsAndTheirOrder()
    {
        using var bmp = DecodeEmbedded(ImagePdf(3));
        Assert.Equal(Size, bmp.Width);
        Assert.Equal(Size, bmp.Height);

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                Assert.Equal(Component(x, y, 0), c.Red);
                Assert.Equal(Component(x, y, 1), c.Green);
                Assert.Equal(Component(x, y, 2), c.Blue);
            }
    }

    /// <summary>A grey image's single component must reach all three output channels unchanged.</summary>
    [Fact]
    public void DeviceGrayImageKeepsItsLevels()
    {
        using var bmp = DecodeEmbedded(ImagePdf(1));

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                byte g = Component(x, y, 0);
                SKColor c = bmp.GetPixel(x, y);
                Assert.Equal(g, c.Red);
                Assert.Equal(g, c.Green);
                Assert.Equal(g, c.Blue);
            }
    }

    /// <summary>
    /// CMYK goes through the shared <see cref="PdfColor.CmykToRgb"/>, so the embedded pixels must
    /// equal what that converter returns for the same samples — the assertion that keeps this
    /// exporter from quietly growing a second subtractive model. GAPS §2b.
    /// </summary>
    [Fact]
    public void DeviceCmykImageMatchesTheSharedConverter()
    {
        using var bmp = DecodeEmbedded(ImagePdf(4));

        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                SKColor expected = PdfColor.CmykToRgb(
                    Component(x, y, 0) / 255f, Component(x, y, 1) / 255f,
                    Component(x, y, 2) / 255f, Component(x, y, 3) / 255f);
                SKColor actual = bmp.GetPixel(x, y);
                Assert.Equal(expected.Red, actual.Red);
                Assert.Equal(expected.Green, actual.Green);
                Assert.Equal(expected.Blue, actual.Blue);
            }
    }

    /// <summary>
    /// Different on both axes and different per component, so a channel swap, a transpose and a
    /// flip are each detectable. A constant or an axis-symmetric ramp would hide all three.
    /// </summary>
    private static byte Component(int x, int y, int c) => (byte)(17 + x * 29 + y * 3 + c * 61);

    private static SKBitmap DecodeEmbedded(byte[] pdf)
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf, false), 0);

        // Parsed as XML, never substring-matched: the suite did that for its whole history and an
        // export that was not even well-formed went unnoticed.
        var doc = XDocument.Parse(svg);
        string href = doc.Descendants()
            .Where(e => e.Name.LocalName == "image")
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .FirstOrDefault(v => v.StartsWith("data:image/png;base64,", StringComparison.Ordinal));

        Assert.False(href == null, "the export embedded no PNG data URI");
        byte[] png = Convert.FromBase64String(href.Substring("data:image/png;base64,".Length));
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>An 8×8 unfiltered image in the device space with <paramref name="comps"/> components.</summary>
    private static byte[] ImagePdf(int comps)
    {
        string cs = comps == 1 ? "/DeviceGray" : comps == 4 ? "/DeviceCMYK" : "/DeviceRGB";
        byte[] samples = new byte[Size * Size * comps];
        for (int y = 0, p = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                for (int c = 0; c < comps; c++)
                    samples[p++] = Component(x, y, c);

        var b = new StringBuilder();
        b.Append("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
            + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        const string content = "q 100 0 0 100 0 0 cm /Im0 Do Q\n";
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        offsets.Add(b.Length);
        b.Append($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {Size} /Height {Size} "
                 + $"/ColorSpace {cs} /BitsPerComponent 8 /Length {samples.Length} >>\nstream\n");

        byte[] head = Encoding.ASCII.GetBytes(b.ToString());
        var ms = new MemoryStream();
        ms.Write(head, 0, head.Length);
        ms.Write(samples, 0, samples.Length);

        var tail = new StringBuilder("\nendstream\nendobj\n");
        int xref = (int)ms.Length + tail.Length;
        tail.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) tail.Append(o.ToString("D10")).Append(" 00000 n \n");
        tail.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        byte[] tb = Encoding.ASCII.GetBytes(tail.ToString());
        ms.Write(tb, 0, tb.Length);
        return ms.ToArray();
    }
}
