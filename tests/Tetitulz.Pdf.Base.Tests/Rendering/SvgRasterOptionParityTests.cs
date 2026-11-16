// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Options and paint decisions the two renderers must agree on.
///
/// <para><b>Why this exists.</b> <c>SvgRasterParityTests</c> compares rendered output, which cannot
/// see a divergence in something the export never emits at all. These are the cases where the SVG
/// side silently discarded a <see cref="PdfRenderOptions"/> value, or resolved half of a paint
/// decision that the raster side resolves whole — output that is well-formed, plausible and
/// wrong.</para>
/// </summary>
public class SvgRasterOptionParityTests
{
    private const int Dpi = 72;

    /// <summary>
    /// A transparent background is transparent in both. The SVG path emitted a hardcoded opaque
    /// white full-page <c>&lt;rect&gt;</c> and never read <c>BackgroundArgb</c>, so the same call
    /// returned transparent pixels and an opaque-white export.
    /// </summary>
    [Fact]
    public void SvgExport_HonoursATransparentBackground_LikeTheRaster()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200]", "1 0 0 rg 10 10 20 20 re f");
        var opts = new PdfRenderOptions { BackgroundArgb = PdfRenderOptions.Transparent };

        var pixels = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, Dpi, opts);
        Assert.Equal(0, pixels.Pixels[3]);   // raster: top-left corner is transparent

        var root = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, opts)).Root;
        Assert.DoesNotContain(root.Elements(), IsFullPageBackgroundRect);
    }

    /// <summary>A non-default opaque background reaches the export as that colour, not as white.</summary>
    [Fact]
    public void SvgExport_HonoursACustomBackgroundColour_LikeTheRaster()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200]", "1 0 0 rg 10 10 20 20 re f");
        var opts = new PdfRenderOptions { BackgroundArgb = 0xFF0000FFu };   // opaque blue

        var pixels = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, Dpi, opts);
        // RGBA8888: the corner is the background.
        Assert.Equal(0,   pixels.Pixels[0]);
        Assert.Equal(0,   pixels.Pixels[1]);
        Assert.Equal(255, pixels.Pixels[2]);

        var root = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, opts)).Root;
        var bg = root.Elements().FirstOrDefault(IsFullPageBackgroundRect);
        Assert.NotNull(bg);
        Assert.Equal("rgb(0,0,255)", bg.Attribute("fill").Value);
    }

    /// <summary>
    /// The default background is still an opaque white rect, spelled exactly as before — the
    /// control, so the two tests above cannot pass by the background having been dropped
    /// altogether, and the default export stays byte-identical for callers who diff it.
    /// </summary>
    [Fact]
    public void SvgExport_StillPaintsTheDefaultWhiteBackground()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200]", "1 0 0 rg 10 10 20 20 re f");

        var root = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0)).Root;
        var bg = root.Elements().FirstOrDefault(IsFullPageBackgroundRect);
        Assert.NotNull(bg);
        Assert.Equal("white", bg.Attribute("fill").Value);
    }

    /// <summary>
    /// A pattern-stroked path is painted with the pattern in both renderers. The SVG writer resolved
    /// pattern FILLS only; the stroke branch emitted <c>_gs.StrokeColor</c> unconditionally, and
    /// since <c>/Pattern CS</c> sets no stroke colour and <c>SCN</c> with only a name sets none
    /// either, the export was a plain BLACK line — with no diagnostic, because the pattern name was
    /// present and so "nothing was selected" never fired.
    /// </summary>
    [Fact]
    public void SvgExport_ResolvesAStrokePattern_LikeTheRaster()
    {
        byte[] pdf = PatternStrokePdf();

        using SKBitmap raster = SKBitmap.Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        SKColor onTheLine = raster.GetPixel(150, 100);
        Assert.Equal((byte)255, onTheLine.Red);
        Assert.Equal((byte)0,   onTheLine.Green);
        Assert.Equal((byte)0,   onTheLine.Blue);

        var root = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0)).Root;
        var stroked = root.Descendants()
                          .Where(e => e.Attribute("stroke") != null)
                          .Select(e => e.Attribute("stroke").Value)
                          .Where(v => v != "none")
                          .ToList();

        Assert.NotEmpty(stroked);
        Assert.All(stroked, v => Assert.StartsWith("url(#", v));
    }

    // ------------------------------------------------------------------

    private static bool IsFullPageBackgroundRect(XElement e)
        => e.Name.LocalName == "rect"
        && e.Attribute("width")?.Value == "200"
        && e.Attribute("height")?.Value == "200";

    private static byte[] PatternStrokePdf() =>
        Build("/MediaBox [0 0 300 200]",
              "q /Pattern CS /P1 SCN 20 w 50 100 m 250 100 l S Q",
              "/Resources << /Pattern << /P1 5 0 R >> >>",
              "5 0 obj\n<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB "
              + "/Coords [0 0 300 0] /Function 6 0 R /Extend [true true] >> >>\nendobj\n",
              "6 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >>\nendobj\n");

    private static byte[] Pdf(string pageExtras, string content) => Build(pageExtras, content, "");

    private static byte[] Build(string pageExtras, string content, string resources,
                                params string[] extraObjects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R {pageExtras} /Contents 4 0 R {resources} >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        for (int i = 0; i < (extraObjects?.Length ?? 0); i++) Obj(extraObjects[i]);

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
