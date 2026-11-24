// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Page geometry decided once, and reached from every path that needs it.
///
/// <para><b>Why this suite exists.</b> <c>MeasurePage</c> and <c>ResolveRenderBox</c> are the
/// renderer's geometry, and two other paths open-coded their own: the SVG exporter read
/// <c>CropBox ?? MediaBox</c> directly, and <c>ExtractInkCoverage</c> derived its own extent from
/// the raw box array. Both looked equivalent and neither was. Five divergences were live at once —
/// three in the exporter, two in the ink path — and every one of them is the shape this repository
/// keeps finding: one file, two answers, no test comparing them.</para>
///
/// <para>Each test below therefore asserts the two paths <b>against each other</b> rather than
/// checking either alone, because each of these bugs produced perfectly plausible output on its
/// own terms.</para>
/// </summary>
public class RenderGeometryParityTests
{
    private const int Dpi = 72;

    // ------------------------------------------------------------------
    //  SVG export vs the raster renderer: the page box
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>UseTrimBox</c> selects the <c>/TrimBox</c> for the raster renderer, and must select it
    /// for the exporter too. It did not: the export came out at full <c>/MediaBox</c> size, so a
    /// prepress caller rendering and exporting the same page with the same options got a 100x100
    /// image and a 200x200 SVG.
    /// </summary>
    [Fact]
    public void SvgExport_SelectsTheSameRenderBoxAsTheRaster_UnderUseTrimBox()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200] /TrimBox [50 50 150 150]", "1 0 0 rg 50 50 20 20 re f");
        var options = new PdfRenderOptions { UseTrimBox = true };

        using SKBitmap raster = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));
        var svg = SvgRoot(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, options));

        Assert.Equal(100, raster.Width);                       // the TrimBox extent, not the MediaBox
        Assert.Equal(raster.Width,  SvgLength(svg, "width"));
        Assert.Equal(raster.Height, SvgLength(svg, "height"));
    }

    /// <summary>
    /// §12.2 Table 28: <c>/ViewerPreferences /PrintArea</c> names the box to use under a print
    /// intent. The raster renderer honours it; the exporter ignored it.
    /// </summary>
    [Fact]
    public void SvgExport_HonoursViewerPreferencesPrintArea_LikeTheRaster()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200] /TrimBox [50 50 150 150]",
                         "1 0 0 rg 50 50 20 20 re f",
                         catalogExtras: "/ViewerPreferences << /PrintArea /TrimBox >>");
        var options = new PdfRenderOptions { Intent = RenderIntent.Print };

        using SKBitmap raster = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));
        var svg = SvgRoot(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0, options));

        Assert.Equal(100, raster.Width);
        Assert.Equal(raster.Width,  SvgLength(svg, "width"));
        Assert.Equal(raster.Height, SvgLength(svg, "height"));
    }

    /// <summary>
    /// §14.11.2: every other boundary box "shall be contained within" the <c>/MediaBox</c>, and a
    /// reader finding one that extends outside uses the intersection. The raster renderer clips;
    /// the exporter took the <c>/CropBox</c> at face value and exported a page twice the size.
    /// </summary>
    [Fact]
    public void SvgExport_ClipsAnOversizedCropBoxToTheMediaBox_LikeTheRaster()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200] /CropBox [0 0 400 400]", "1 0 0 rg 50 50 20 20 re f");

        using SKBitmap raster = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        var svg = SvgRoot(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));

        Assert.Equal(200, raster.Width);                       // clipped to the MediaBox
        Assert.Equal(raster.Width,  SvgLength(svg, "width"));
        Assert.Equal(raster.Height, SvgLength(svg, "height"));
    }

    // ------------------------------------------------------------------
    //  SVG export vs the raster renderer: /UserUnit  (§14.11.1)
    // ------------------------------------------------------------------

    /// <summary>
    /// A hostile <c>/UserUnit 0</c>. The exporter guarded the value it read from the file and the
    /// raster path did not, so the same page exported at full size and rendered as a
    /// <b>1x1-pixel</b> image with all its content gone: the device scale went to zero, both
    /// extents collapsed to <c>Math.Max(1, …)</c>, and the resolution clamp never fired because
    /// 1x1 is under every limit. Nothing was reported.
    /// </summary>
    [Fact]
    public void RasterAndSvg_AgreeOnAMalformedZeroUserUnit()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 200 200] /UserUnit 0", "1 0 0 rg 20 20 60 60 re f");

        using SKBitmap raster = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        var svg = SvgRoot(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));

        Assert.Equal(200, raster.Width);                    // /UserUnit 0 is refused, not obeyed
        Assert.Equal(200, raster.Height);
        Assert.Equal(raster.Width,  SvgLength(svg, "width"));
        Assert.Equal(raster.Height, SvgLength(svg, "height"));

        // …and the artwork really is there, not a 200x200 blank agreeing with a 200x200 blank.
        SKColor inSquare = raster.GetPixel(50, 150);
        Assert.True(inSquare.Red > 200 && inSquare.Green < 80,
            $"the red square should be at device (50,150), was {inSquare}");
    }

    /// <summary>
    /// The same for a negative value, and for the <c>EffectiveDpi</c> a caller sizes a buffer from:
    /// <c>m.Scale * 72 / page.UserUnit</c> divided by the raw entry, so <c>/UserUnit 0</c> reported
    /// <c>NaN</c> and <c>/UserUnit -1</c> reported a negative resolution.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0")]
    public void GetRenderSize_ReportsAUsableDpiForAMalformedUserUnit(string userUnit)
    {
        byte[] pdf = Pdf($"/MediaBox [0 0 200 200] /UserUnit {userUnit}", "1 0 0 rg 20 20 60 60 re f");

        var size = PdfPageRenderer.GetRenderSize(new MemoryStream(pdf), 0, Dpi);

        Assert.Equal(200, size.WidthPixels);
        Assert.Equal(200, size.HeightPixels);
        Assert.False(float.IsNaN(size.EffectiveDpi), "EffectiveDpi came back NaN");
        Assert.Equal(Dpi, size.EffectiveDpi, 3);
    }

    /// <summary>
    /// <see cref="PageRenderSize.UserUnit"/> is the unit the renderer applies — a legal one as
    /// written, a malformed one as 1 — from the static door and the session's alike, so a caller
    /// laying a page out from <c>WidthPoints</c> can scale it to what is rendered.
    /// </summary>
    [Theory]
    [InlineData("2", 2f)]
    [InlineData("0", 1f)]
    [InlineData("-1", 1f)]
    public void GetRenderSize_ReportsTheUserUnitTheRendererApplies(string userUnit, float expected)
    {
        byte[] pdf = Pdf($"/MediaBox [0 0 100 100] /UserUnit {userUnit}", "1 0 0 rg 20 20 60 60 re f");

        var size = PdfPageRenderer.GetRenderSize(new MemoryStream(pdf), 0, Dpi);
        using var session = PdfRenderSession.Open(new MemoryStream(pdf));
        var fromSession = session.GetRenderSize(0, Dpi);

        Assert.Equal(expected, size.UserUnit, 3);
        Assert.Equal(expected, fromSession.UserUnit, 3);
        Assert.Equal(size.WidthPoints * size.UserUnit * Dpi / 72f, size.WidthPixels, 0);
    }

    /// <summary>
    /// The control that keeps the three above from passing on a renderer that ignores
    /// <c>/UserUnit</c> altogether: a legal <c>/UserUnit 2</c> must still double both paths.
    /// </summary>
    [Fact]
    public void RasterAndSvg_BothStillHonourALegalUserUnit()
    {
        byte[] pdf = Pdf("/MediaBox [0 0 100 100] /UserUnit 2", "1 0 0 rg 20 20 60 60 re f");

        using SKBitmap raster = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        var svg = SvgRoot(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));

        Assert.Equal(200, raster.Width);
        Assert.Equal(raster.Width, SvgLength(svg, "width"));
    }

    /// <summary>
    /// The refusal is reported, not silent: at the pixel level "the file asked for a scale we
    /// ignored" is indistinguishable from "the file never asked", which is what let a page vanish
    /// with a clean diagnostic log.
    /// </summary>
    [Fact]
    public void AMalformedUserUnit_IsReportedThroughRenderDiagnostics()
    {
        var reported = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Listen(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            // Filtered to this thread: the sink is process-wide.
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.RenderParameterIgnored && detail != null &&
                detail.Contains("/UserUnit")) reported.Add(detail);
        }

        byte[] pdf = Pdf("/MediaBox [0 0 200 200] /UserUnit 0", "1 0 0 rg 20 20 60 60 re f");
        RenderDiagnostics.OnEvent += Listen;
        try { PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi); }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        Assert.NotEmpty(reported);
    }

    // ------------------------------------------------------------------
    //  Ink coverage vs the renderer: extent and origin
    // ------------------------------------------------------------------

    /// <summary>
    /// The resolution clamp every other render path applies. <c>ExtractInkCoverage</c> computed its
    /// own extent and so never called <c>ClampRenderDimensions</c>: an oversized page box allocated
    /// an accumulator of whatever size the file asked for, and <c>InkAccumulator</c> sizes each
    /// channel as <c>width * height</c> in <c>int</c>, which overflows well before the allocation
    /// itself fails.
    /// </summary>
    [Fact]
    public void InkCoverage_AppliesTheSameResolutionClamp_AsTheRenderer_OnAnOversizedPageBox()
    {
        // 17000 pt at 72 dpi = 17000 px, past PdfLimits.MaxImageDimension (16384).
        byte[] pdf = Pdf("/MediaBox [0 0 17000 200]", "1 0 0 rg 10 10 20 20 re f");

        var size = PdfPageRenderer.GetRenderSize(new MemoryStream(pdf), 0, Dpi);
        var ink  = PdfPageRenderer.ExtractInkCoverage(new MemoryStream(pdf), 0, Dpi);

        Assert.True(size.WidthPixels < 17000, $"the renderer should clamp, got {size.WidthPixels}");
        Assert.Equal(size.WidthPixels,  ink.Width);
        Assert.Equal(size.HeightPixels, ink.Height);
    }

    /// <summary>
    /// Ink is attributed to the pixel it was deposited on, which means the page-box origin shift
    /// the renderer applies has to be applied here too. It was computed and then dropped, so the
    /// map was offset by the whole box origin — ink landed on the wrong pixels, and anything near
    /// the far edge fell outside the map and was lost entirely.
    /// </summary>
    [Fact]
    public void InkCoverage_PlacesInkRelativeToThePageBoxOrigin()
    {
        // The same 20x20 square, 10 pt in from the box origin, on two pages of identical size.
        byte[] zeroOrigin  = SeparationPdf("/MediaBox [0 0 200 200]",       "/CS0 cs 1 scn 10 10 20 20 re f");
        byte[] shiftedBox  = SeparationPdf("/MediaBox [100 100 300 300]",   "/CS0 cs 1 scn 110 110 20 20 re f");

        (int x, int y) control = FirstInk(zeroOrigin);
        (int x, int y) shifted = FirstInk(shiftedBox);

        Assert.Equal(control, shifted);
    }

    /// <summary>
    /// The far-edge consequence of the same defect, asserted directly: a square at the top-right of
    /// a shifted box must not map outside the accumulator and vanish, which would report a page that
    /// uses a spot ink as one that does not.
    /// </summary>
    [Fact]
    public void InkCoverage_KeepsInkDrawnAtTheFarEdgeOfAShiftedPageBox()
    {
        // Box [100 100 300 300]; the square sits 20 pt in from the box's top-right corner.
        byte[] pdf = SeparationPdf("/MediaBox [100 100 300 300]", "/CS0 cs 1 scn 260 260 20 20 re f");

        var ink = PdfPageRenderer.ExtractInkCoverage(new MemoryStream(pdf), 0, Dpi);

        Assert.True(ink.Channels.ContainsKey("Spot"), "the Spot channel should exist");
        byte[] map = ink.Channels["Spot"];
        int inked = 0;
        for (int i = 0; i < map.Length; i++) if (map[i] > 0) inked++;
        Assert.True(inked > 0, "ink drawn inside the page box must land inside the coverage map");
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static (int x, int y) FirstInk(byte[] pdf)
    {
        var ink = PdfPageRenderer.ExtractInkCoverage(new MemoryStream(pdf), 0, Dpi);
        Assert.True(ink.Channels.ContainsKey("Spot"), "the Spot channel should exist");
        byte[] map = ink.Channels["Spot"];
        for (int y = 0; y < ink.Height; y++)
            for (int x = 0; x < ink.Width; x++)
                if (map[y * ink.Width + x] > 0) return (x, y);
        throw new Xunit.Sdk.XunitException("no ink was recorded at all");
    }

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png);

    private static XElement SvgRoot(string svg) => XDocument.Parse(svg).Root;

    /// <summary>An SVG root length attribute, as a number. Parsed, never substring-matched.</summary>
    private static int SvgLength(XElement root, string name)
        => (int)Math.Round(double.Parse(root.Attribute(name).Value,
                                        System.Globalization.CultureInfo.InvariantCulture));

    private static byte[] Pdf(string pageExtras, string content, string catalogExtras = "")
        => Build(pageExtras, content, catalogExtras, resources: "", extraObjects: null);

    private static byte[] SeparationPdf(string pageExtras, string content)
        => Build(pageExtras, content, catalogExtras: "",
                 resources: "/Resources << /ColorSpace << /CS0 [/Separation /Spot /DeviceCMYK 5 0 R] >> >>",
                 extraObjects: new[] { "5 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>\nendobj\n" });

    private static byte[] Build(string pageExtras, string content, string catalogExtras,
                                string resources, string[] extraObjects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {catalogExtras} >>\nendobj\n");
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
