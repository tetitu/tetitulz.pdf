// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Closes the items GAPS.md §2 "full rendering audit" left open in group 1, plus two it records as
/// closed with no direct coverage.
///
/// <list type="number">
///   <item><c>stroke-width="0"</c> was emitted verbatim. §8.4.3.2 gives a line width of 0 a meaning
///         — the thinnest line the device can draw — and the raster path honours it with a Skia
///         hairline, but SVG defines a zero width as painting <b>nothing</b>. Every zero-width
///         stroke therefore vanished from the export.</item>
///   <item>A tiling pattern whose <c>/BBox</c> is <b>larger</b> than its <c>/XStep</c>/<c>/YStep</c>
///         asks for overlapping cells. An SVG <c>&lt;pattern&gt;</c> clips each tile to its own
///         rectangle, so the overlap is lost — inexpressible, and now reported instead of dropped
///         in silence.</item>
///   <item><c>/Extend</c>: <c>[true true]</c> <i>is</i> expressible — it is exactly
///         <c>spreadMethod="pad"</c> — so it is now stated in the output and no longer reported as
///         an approximation. The other three combinations stay reported.</item>
///   <item>Regression cover for the ExtGState <c>/D</c> dash <b>phase</b> and for Type 3 CharProc
///         <c>/Resources</c> precedence over the page's.</item>
/// </list>
///
/// <para><b>Every assertion here is on the parsed SVG</b> (<see cref="XDocument"/>), following
/// <c>SvgRasterParityTests</c>. Substring checks are what let this whole class of defect survive a
/// green suite — including, at one point, output that was not well-formed XML at all.</para>
/// </summary>
public sealed class SvgGapClosureTests
{
    // ====================================================================
    //  1. stroke-width="0" — §8.4.3.2 asks for the thinnest renderable line
    // ====================================================================

    /// <summary>
    /// <c>0 w … S</c> must still draw. SVG paints nothing for <c>stroke-width="0"</c>, so emitting
    /// the graphics-state value verbatim deleted the line from the export while the raster path
    /// drew a hairline for the same file.
    /// </summary>
    [Fact]
    public void ZeroLineWidth_PathStroke_EmitsAVisibleHairlineWidth()
    {
        string svg = RenderSvg("0 w 0 0 0 RG 10 10 m 200 10 l S");
        var stroked = Assert.Single(StrokedPaths(svg));

        float width = Attr(stroked, "stroke-width");
        Assert.True(width > 0f,
            $"stroke-width=\"{width}\" paints nothing in SVG; §8.4.3.2 asks for the thinnest " +
            "line the device can render.");
        // One CSS pixel expressed in this writer's user unit, which is one PDF point.
        Assert.Equal((float)PdfUnits.PxToPoint, width, 4);
    }

    /// <summary>The control: a real width is passed through untouched, so the substitution above
    /// cannot be a blanket minimum applied to every stroke.</summary>
    [Fact]
    public void PositiveLineWidth_PathStroke_IsEmittedVerbatim()
    {
        string svg = RenderSvg("3 w 0 0 0 RG 10 10 m 200 10 l S");
        var stroked = Assert.Single(StrokedPaths(svg));

        Assert.Equal(3f, Attr(stroked, "stroke-width"), 4);
    }

    /// <summary>
    /// A line width below the substitution threshold but above zero is a legitimate hairline the
    /// author chose, and must not be widened to 0.75.
    /// </summary>
    [Fact]
    public void TinyPositiveLineWidth_IsNotWidenedToTheHairline()
    {
        string svg = RenderSvg("0.05 w 0 0 0 RG 10 10 m 200 10 l S");
        var stroked = Assert.Single(StrokedPaths(svg));

        Assert.Equal(0.05f, Attr(stroked, "stroke-width"), 4);
    }

    /// <summary>
    /// Stroked text (<c>1 Tr</c>) reaches a different emission site, where the width is divided by
    /// the font size because the size is baked into the element's transform. The hairline
    /// substitution is defined in page units like every other line width, so it must be divided
    /// back out too — not applied after the division, and not skipped.
    /// </summary>
    [Fact]
    public void ZeroLineWidth_TextStroke_EmitsAVisibleHairlineWidth()
    {
        string svg = RenderTextSvg("BT /F1 10 Tf 1 Tr 0 w 0 0 0 RG 50 700 Td (Hi) Tj ET");
        var stroked = Assert.Single(StrokedTexts(svg));

        float width = Attr(stroked, "stroke-width");
        Assert.True(width > 0f, $"stroked text emitted stroke-width=\"{width}\", which paints nothing.");
        Assert.Equal((float)PdfUnits.PxToPoint / 10f, width, 5);
    }

    /// <summary>The control for the text site: a real width still divides by the font size only.</summary>
    [Fact]
    public void PositiveLineWidth_TextStroke_IsDividedByTheFontSizeOnly()
    {
        string svg = RenderTextSvg("BT /F1 10 Tf 1 Tr 2 w 0 0 0 RG 50 700 Td (Hi) Tj ET");
        var stroked = Assert.Single(StrokedTexts(svg));

        Assert.Equal(0.2f, Attr(stroked, "stroke-width"), 5);
    }

    // ====================================================================
    //  2. Tiling pattern: /BBox larger than the step — overlapping cells
    // ====================================================================

    /// <summary>
    /// §8.7.3.1 lets <c>/XStep</c>/<c>/YStep</c> be <i>smaller</i> than the <c>/BBox</c> so that
    /// adjacent cells overlap. The tile is built from every cell copy that reaches into it
    /// (<c>TilingOverlapTests</c>), up to <c>TilingLattice.MaxCopies</c>; past that bound — here 400
    /// copies — the spill-over is dropped, and that must be reported, never dropped quietly.
    /// </summary>
    [Fact]
    public void TilingPattern_BBoxFarLargerThanStep_ReportsTheLostOverlap()
    {
        var events = CaptureDiagnostics(() =>
            RenderTilingPatternSvg(bbox: "[0 0 200 200]", extras: "/XStep 10 /YStep 10"));

        var overlap = Assert.Single(events.Where(e => e.Detail != null &&
                                                      e.Detail.Contains("larger than its step",
                                                          StringComparison.Ordinal)));
        // PatternApproximated, not PatternFillUnresolved: the tile IS painted, minus its overlap.
        Assert.Equal(RenderDiagnosticKind.PatternApproximated, overlap.Kind);
    }

    /// <summary>
    /// The control, and the reason the check is worth having: a cell that exactly fills its step is
    /// the ordinary case and is exported faithfully, so it must produce no diagnostic at all. A
    /// report on every pattern would be noise a host learns to ignore.
    /// </summary>
    [Fact]
    public void TilingPattern_BBoxEqualToStep_ReportsNothing()
    {
        var events = CaptureDiagnostics(() =>
            RenderTilingPatternSvg(bbox: "[0 0 20 20]", extras: "/XStep 20 /YStep 20"));

        Assert.DoesNotContain(events, e => e.Detail != null &&
                                           e.Detail.Contains("larger than its step", StringComparison.Ordinal));
    }

    /// <summary>A step LARGER than the cell (gaps between cells) is expressible and must not report.</summary>
    [Fact]
    public void TilingPattern_StepLargerThanBBox_ReportsNothing()
    {
        var events = CaptureDiagnostics(() =>
            RenderTilingPatternSvg(bbox: "[0 0 10 10]", extras: "/XStep 40 /YStep 40"));

        Assert.DoesNotContain(events, e => e.Detail != null &&
                                           e.Detail.Contains("larger than its step", StringComparison.Ordinal));
    }

    /// <summary>
    /// Reporting is not refusing: the pattern is still exported, tiled at the step. This pins what
    /// the diagnostic claims — that the tile is the step rectangle and the rest of the cell is what
    /// went missing.
    /// </summary>
    [Fact]
    public void TilingPattern_BBoxLargerThanStep_StillExportsATileAtTheStepSize()
    {
        string svg = RenderTilingPatternSvg(bbox: "[0 0 20 20]", extras: "/XStep 10 /YStep 10");
        var pattern = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "pattern"));

        Assert.Equal("10", pattern.Attribute("width")?.Value);
        Assert.Equal("10", pattern.Attribute("height")?.Value);
    }

    // ====================================================================
    //  3. /Extend — every combination expressed
    // ====================================================================

    /// <summary>
    /// <c>/Extend [true true]</c> means the terminal colours continue past both ends of the axis for
    /// ever, which is precisely SVG's <c>spreadMethod="pad"</c>. It is stated rather than left to the
    /// attribute default so the mapping is verifiable in the emitted document.
    /// </summary>
    [Fact]
    public void AxialShading_ExtendBothEnds_EmitsSpreadMethodPad()
    {
        string svg = RenderShadingSvg(ShadingType.Axial, "/Extend [true true]");
        var gradient = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "linearGradient"));

        Assert.Equal("pad", gradient.Attribute("spreadMethod")?.Value);
    }

    [Fact]
    public void RadialShading_ExtendBothEnds_EmitsSpreadMethodPad()
    {
        string svg = RenderShadingSvg(ShadingType.Radial, "/Extend [true true]");
        var gradient = Assert.Single(XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "radialGradient"));

        Assert.Equal("pad", gradient.Attribute("spreadMethod")?.Value);
    }

    /// <summary>
    /// And because that case is exported correctly, it must NOT be reported. The diagnostic used to
    /// fire for the extend-true gradient as well, which is a host being told its output is
    /// approximate when it is exact.
    /// </summary>
    [Fact]
    public void AxialShading_ExtendBothEnds_ReportsNothing()
    {
        var events = CaptureDiagnostics(() => RenderShadingSvg(ShadingType.Axial, "/Extend [true true]"));

        Assert.DoesNotContain(events, e => e.Detail != null &&
                                           e.Detail.Contains("/Extend", StringComparison.Ordinal));
    }

    /// <summary>
    /// The default, <c>[false false]</c>, paints nothing beyond the axis (§8.7.4.5.3). SVG pads the
    /// first and last stop past the ends, so each end that does not extend gets a transparent stop
    /// pinned at its offset — the hard edge the raster path builds the same way — and the export is
    /// exact, not reported as inexpressible.
    /// </summary>
    [Fact]
    public void AxialShading_DefaultExtend_EndsInTransparentStops()
    {
        var stops = Stops(RenderShadingSvg(ShadingType.Axial, ""), "linearGradient");

        Assert.Equal("0", stops[0].Attribute("stop-opacity")?.Value);
        Assert.Equal("0", stops[0].Attribute("offset")?.Value);
        Assert.Equal("0", stops[stops.Length - 1].Attribute("stop-opacity")?.Value);
        Assert.Equal("1", stops[stops.Length - 1].Attribute("offset")?.Value);
        Assert.Null(stops[1].Attribute("stop-opacity"));   // the real colour beside it is opaque
    }

    /// <summary>A partial extend clears only the end that does not extend.</summary>
    [Fact]
    public void AxialShading_PartialExtend_ClearsOnlyTheEndThatDoesNotExtend()
    {
        var stops = Stops(RenderShadingSvg(ShadingType.Axial, "/Extend [true false]"), "linearGradient");

        Assert.Null(stops[0].Attribute("stop-opacity"));
        Assert.Equal("0", stops[stops.Length - 1].Attribute("stop-opacity")?.Value);
    }

    /// <summary>The radial form takes the same stops, and an extend-both gradient takes none.</summary>
    [Fact]
    public void RadialShading_DefaultExtend_EndsInTransparentStops_AndExtendBothDoesNot()
    {
        var cleared = Stops(RenderShadingSvg(ShadingType.Radial, ""), "radialGradient");
        Assert.Equal("0", cleared[0].Attribute("stop-opacity")?.Value);
        Assert.Equal("0", cleared[cleared.Length - 1].Attribute("stop-opacity")?.Value);

        var padded = Stops(RenderShadingSvg(ShadingType.Radial, "/Extend [true true]"), "radialGradient");
        Assert.DoesNotContain(padded, st => st.Attribute("stop-opacity") != null);
    }

    /// <summary>No /Extend value is reported any more — every combination is exported as drawn.</summary>
    [Fact]
    public void AxialShading_NonExtendingGradients_ReportNothing()
    {
        var events = CaptureDiagnostics(() => RenderShadingSvg(ShadingType.Axial, "", repeats: 4));

        Assert.DoesNotContain(events, e => e.Detail != null &&
                                           e.Detail.Contains("/Extend", StringComparison.Ordinal));
    }

    private static XElement[] Stops(string svg, string gradient)
        => Assert.Single(XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == gradient))
                 .Elements().Where(e => e.Name.LocalName == "stop").ToArray();

    // ====================================================================
    //  4a. ExtGState /D — the dash phase (audit lists this closed)
    // ====================================================================

    /// <summary>
    /// <c>/D [[array] phase]</c> and <c>[array] phase d</c> are the same pair, so the two paths must
    /// produce identical markup. They did not: the ExtGState reader kept the array and dropped the
    /// phase, silently moving every dash set that way to phase 0.
    /// </summary>
    [Fact]
    public void ExtGStateDash_AndTheDashOperator_AgreeOnArrayAndPhase()
    {
        var viaOperator = Assert.Single(StrokedPaths(
            RenderSvg("[6 3] 4 d 0 0 0 RG 2 w 10 10 m 200 10 l S")));
        var viaExtGState = Assert.Single(StrokedPaths(
            RenderSvgWithExtGState("/D [[6 3] 4]", "0 0 0 RG 2 w 10 10 m 200 10 l S")));

        Assert.Equal(viaOperator.Attribute("stroke-dasharray")?.Value,
                     viaExtGState.Attribute("stroke-dasharray")?.Value);
        Assert.Equal(viaOperator.Attribute("stroke-dashoffset")?.Value,
                     viaExtGState.Attribute("stroke-dashoffset")?.Value);
        // Pinned, so the two agreeing on nothing at all cannot satisfy the comparison above.
        Assert.Equal("6,3", viaExtGState.Attribute("stroke-dasharray")?.Value);
        Assert.Equal("4",   viaExtGState.Attribute("stroke-dashoffset")?.Value);
    }

    /// <summary>
    /// Phase 0 is SVG's own default, so the attribute is omitted. Asserted because "the phase is
    /// read" and "the phase is always written" are different claims, and only the first is wanted.
    /// </summary>
    [Fact]
    public void ExtGStateDash_ZeroPhase_OmitsTheDashOffsetAttribute()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvgWithExtGState("/D [[6 3] 0]", "0 0 0 RG 2 w 10 10 m 200 10 l S")));

        Assert.Equal("6,3", stroked.Attribute("stroke-dasharray")?.Value);
        Assert.Null(stroked.Attribute("stroke-dashoffset"));
    }

    /// <summary>A later <c>d</c> operator replaces an ExtGState dash outright — array and phase.</summary>
    [Fact]
    public void DashOperator_AfterExtGState_ReplacesBothArrayAndPhase()
    {
        var stroked = Assert.Single(StrokedPaths(
            RenderSvgWithExtGState("/D [[6 3] 4]", "[9 1] 7 d 0 0 0 RG 2 w 10 10 m 200 10 l S")));

        Assert.Equal("9,1", stroked.Attribute("stroke-dasharray")?.Value);
        Assert.Equal("7",   stroked.Attribute("stroke-dashoffset")?.Value);
    }

    // ====================================================================
    //  4b. Type 3 CharProc /Resources precedence (audit lists this closed)
    // ====================================================================

    /// <summary>
    /// §9.6.5: a Type 3 font's own <c>/Resources</c> is what its CharProcs are executed against; the
    /// page's dictionary is only the fallback. Passing the page's unconditionally meant a glyph
    /// referencing a font-local XObject drew nothing — or, as here, drew the page's unrelated
    /// resource of the same name.
    /// </summary>
    [Fact]
    public void Type3CharProc_UsesTheFontsOwnResources_NotThePages()
    {
        // /Fx names a GREEN form in the font's /Resources and a RED one on the page.
        string svg = RenderType3Svg(withFontResources: true);
        var fills = PaintedPaths(svg).Select(p => p.Attribute("fill")?.Value).ToList();

        Assert.Contains("rgb(0,255,0)", fills);
        Assert.DoesNotContain("rgb(255,0,0)", fills);
    }

    /// <summary>
    /// The fallback half of the same rule: a Type 3 font with no <c>/Resources</c> of its own does
    /// resolve against the page's. Without this the test above would pass on a writer that simply
    /// never resolved page resources for a glyph.
    /// </summary>
    [Fact]
    public void Type3CharProc_WithNoFontResources_FallsBackToThePages()
    {
        string svg = RenderType3Svg(withFontResources: false);
        var fills = PaintedPaths(svg).Select(p => p.Attribute("fill")?.Value).ToList();

        Assert.Contains("rgb(255,0,0)", fills);
        Assert.DoesNotContain("rgb(0,255,0)", fills);
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    private enum ShadingType { Axial = 2, Radial = 3 }

    /// <summary>An attribute parsed as a float, so assertions are numeric rather than textual.</summary>
    private static float Attr(XElement e, string name)
    {
        string raw = e.Attribute(name)?.Value;
        Assert.False(raw == null, $"element <{e.Name.LocalName}> has no {name} attribute");
        return float.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static List<XElement> PaintedPaths(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Where(e => (e.Attribute("fill")?.Value ?? "none") != "none")
            .ToList();

    private static List<XElement> StrokedPaths(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .ToList();

    private static List<XElement> StrokedTexts(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "text")
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .ToList();

    /// <summary>
    /// Collects the diagnostics <paramref name="render"/> raises <b>on this thread</b>.
    ///
    /// <para><b>The thread filter is load-bearing.</b> <c>RenderDiagnostics.OnEvent</c> is a
    /// process-wide event, so without it this handler also receives whatever a test running
    /// concurrently in another class reports. A counting assertion — <c>Assert.Single(...)</c>,
    /// which several callers here use to prove a "report once per stream" rule — then passes
    /// alone and fails beside a neighbour that legitimately reported the same kind. That is
    /// exactly the shape CLAUDE.md describes under "a negative assertion that reads shared state
    /// is asserting something about the whole process".</para>
    ///
    /// <para>Filtering the handler is the remedy; serialising the whole collection would hide the
    /// coupling rather than remove it, and would slow every renderer test to do it.</para>
    /// </summary>
    private static List<(RenderDiagnosticKind Kind, string Detail)> CaptureDiagnostics(Action render)
    {
        var seen = new List<(RenderDiagnosticKind Kind, string Detail)>();
        int owner = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != owner) return;
            lock (seen) seen.Add((kind, detail));
        }

        RenderDiagnostics.OnEvent += Sink;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        lock (seen) return seen.ToList();
    }

    private static string RenderSvg(string contentStream)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", contentStream));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderSvgWithExtGState(string gsEntries, string contentStream)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /ExtGState << /G1 5 0 R >> >> >>",
            Stream("", "/G1 gs " + contentStream),
            $"<< /Type /ExtGState {gsEntries} >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>A page with one non-embedded standard-14 font, so text takes the &lt;text&gt; path
    /// on every platform (glyph outlines are emitted only for embedded programs).</summary>
    private static string RenderTextSvg(string contentStream)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("", contentStream),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string RenderTilingPatternSvg(string bbox, string extras)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R >> >> >>",
            Stream("", "/Pattern cs /P1 scn 100 100 200 200 re f"),
            Stream($"/PatternType 1 /PaintType 1 /TilingType 1 /BBox {bbox} {extras}",
                   "1 0 0 rg 0 0 16 16 re f"));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// A page painting <paramref name="repeats"/> shadings with the <c>sh</c> operator, each an
    /// axial or radial gradient carrying <paramref name="extendEntry"/>.
    /// </summary>
    private static string RenderShadingSvg(ShadingType type, string extendEntry, int repeats = 1)
    {
        string coords = type == ShadingType.Axial
            ? "/Coords [100 100 300 100]"
            : "/Coords [200 200 0 200 200 100]";
        string shading =
            $"<< /ShadingType {(int)type} /ColorSpace /DeviceRGB {coords}" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>" +
            $" {extendEntry} >>";

        var content = new StringBuilder();
        var resources = new StringBuilder();
        for (int i = 1; i <= repeats; i++)
        {
            content.Append($"q 100 100 200 200 re W n /Sh{i} sh Q ");
            resources.Append($"/Sh{i} {4 + i} 0 R ");
        }

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            $" /Resources << /Shading << {resources}>> >> >>",
            Stream("", content.ToString()),
        };
        for (int i = 0; i < repeats; i++) objects.Add(shading);

        using var ms = new MemoryStream(Build(objects.ToArray()));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// A Type 3 font whose single CharProc does <c>/Fx Do</c>. <c>/Fx</c> resolves to a GREEN form
    /// in the font's own <c>/Resources</c> and to a RED one in the page's, so which dictionary the
    /// CharProc was executed against is readable straight off the painted fill.
    /// </summary>
    private static string RenderType3Svg(bool withFontResources)
    {
        string fontResources = withFontResources
            ? " /Resources << /XObject << /Fx 7 0 R >> >>"
            : "";

        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /T3 5 0 R >> /XObject << /Fx 8 0 R >> >> >>",
            Stream("", "BT /T3 24 Tf 100 700 Td (A) Tj ET"),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000]" +
            " /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs 6 0 R" +
            " /Encoding << /Type /Encoding /Differences [65 /ga] >>" +
            $" /FirstChar 65 /LastChar 65 /Widths [1000]{fontResources} >>",
            "<< /ga 9 0 R >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 1000 1000]",
                   "0 1 0 rg 0 0 800 800 re f"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 1000 1000]",
                   "1 0 0 rg 0 0 800 800 re f"),
            Stream("", "1000 0 d0 /Fx Do"));
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
