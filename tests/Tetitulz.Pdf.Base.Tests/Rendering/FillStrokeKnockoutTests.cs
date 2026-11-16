// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// ISO 32000-2 §11.7.4.4: a combined fill and stroke — <c>B</c>, <c>B*</c>, <c>b</c>, <c>b*</c>, and
/// text rendering modes 2 and 6 — is one graphics object, "as if enclosed in a transparency group",
/// and outside the overprint special case that group is <b>non-isolated and knockout</b>: the
/// stroke composites with the backdrop, not with the fill, so a translucent stroke does not show the
/// fill through its inner half (NOTE 2's "double border effect").
/// </summary>
/// <remarks>
/// <para><b>The fixture</b>: an opaque blue square filled and stroked in one <c>B</c>, the stroke red,
/// 20 units wide and at <c>CA 0.5</c>, on a white page. Its inner half, x 100–110, is where the two
/// models part: knocked out, red at 0.5 over the white page is pink (255, 128, 128); painted over the
/// fill it is purple (128, 0, 128). The green channel tells them apart by ~128.</para>
/// </remarks>
public class FillStrokeKnockoutTests
{
    [Fact]
    public void ATranslucentStroke_KnocksOutTheFillBeneathIt()
    {
        using var bmp = PixelAssert.Render(Square("/CA 0.5", "B"));

        SKColor inner = PixelAssert.At(bmp, 105, 150);   // stroke over the fill's edge
        Assert.InRange(inner.Red, 230, 255);
        Assert.InRange(inner.Green, 100, 160);
        Assert.InRange(inner.Blue, 100, 160);

        SKColor outer = PixelAssert.At(bmp, 95, 150);    // stroke over the page alone
        Assert.Equal(inner, outer);

        SKColor interior = PixelAssert.At(bmp, 150, 150);
        Assert.Equal(new SKColor(0, 0, 255), interior);
    }

    /// <summary>
    /// The same for text in rendering mode 2, on both glyph routes and with or without a soft mask:
    /// the embedded CFF goes to a glyph blob (it is OTF-wrapped), the embedded Type 1 to the
    /// library's own outlines where Skia declines it and to a blob where it accepts it. Each
    /// fixture's glyph is a 400-unit box, so at 200 pt it is an 80-point square at the text origin
    /// (100, 300), and the 10-point stroke's inner half is x 100–105.
    /// </summary>
    /// <remarks>
    /// <b><c>/TK false</c>, on purpose.</b> §11.7.4.4's group is "independent of the text knockout
    /// parameter" (NOTE 1), and under the default <c>/TK true</c> the raster renderer's text
    /// knockout layer paints the stroke Src over the fill and so knocks it out by itself — which
    /// hid this rule's absence from a first version of these cases.
    /// </remarks>
    [Theory]
    [InlineData("cff", false)]
    [InlineData("cff", true)]
    [InlineData("type1", false)]
    [InlineData("type1", true)]
    public void TextFilledAndStroked_KeepsTheFillFromUnderATranslucentStroke(string font, bool masked)
    {
        string glyph = ((char)EmbeddedCffFixture.GlyphCharCode).ToString();
        string content = $"/GS0 gs 0 0 1 rg 1 0 0 RG 10 w BT /F1 200 Tf 2 Tr 100 300 Td ({glyph}) Tj ET";
        string gs = "/ExtGState << /GS0 << /CA 0.5 /TK false" +
                    (masked ? " /SMask << /Type /Mask /S /Luminosity /G 8 0 R >>" : "") + " >> >>";
        const string WhiteMask =
            "8 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Group << /S /Transparency /CS /DeviceGray >> /Length 20 >>\nstream\n" +
            "1 g 0 0 612 792 re f\nendstream\nendobj\n";
        byte[] pdf = font == "cff"
            ? EmbeddedCffFixture.Page(content, gs, masked ? WhiteMask : null)
            : EmbeddedType1Fixture.Page(content, gs, masked ? WhiteMask : null, masked ? 1 : 0);

        using var bmp = PixelAssert.Render(pdf);
        SKColor inner = PixelAssert.At(bmp, 102, 340);
        Assert.InRange(inner.Red, 230, 255);
        Assert.InRange(inner.Green, 100, 160);
        Assert.Equal(inner, PixelAssert.At(bmp, 98, 340));
        Assert.Equal(new SKColor(0, 0, 255), PixelAssert.At(bmp, 140, 340));
    }

    /// <summary>
    /// An opaque, Normal stroke covers the fill either way, so nothing changes for ordinary paths:
    /// the raster interior and edge are what painting the stroke over the fill gives, and the SVG
    /// export stays one element.
    /// </summary>
    [Fact]
    public void AnOpaqueStroke_IsPaintedAsBefore()
    {
        using var bmp = PixelAssert.Render(Square("", "B"));
        Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(bmp, 105, 150));
        Assert.Equal(new SKColor(0, 0, 255), PixelAssert.At(bmp, 150, 150));

        XDocument svg = Svg(Square("", "B"));
        Assert.Empty(svg.Descendants(SvgNs + "mask"));
        Assert.Single(Painted(svg));
    }

    /// <summary>
    /// The SVG export: the fill masked by its own shape less the stroke's — a <c>&lt;mask&gt;</c>
    /// holding the path filled white and stroked black at the stroke's width — and the stroke as
    /// its own element, at its own opacity.
    /// </summary>
    [Fact]
    public void SvgExport_MasksTheFillWithItsStroke()
    {
        XDocument svg = Svg(Square("/CA 0.5", "B"));

        XElement mask = Assert.Single(svg.Descendants(SvgNs + "mask"));
        XElement[] shapes = mask.Elements(SvgNs + "path").ToArray();
        Assert.Equal(2, shapes.Length);
        Assert.Equal("white", (string)shapes[0].Attribute("fill"));
        Assert.Equal("black", (string)shapes[1].Attribute("stroke"));
        Assert.Equal("20", (string)shapes[1].Attribute("stroke-width"));

        List<XElement> painted = Painted(svg);
        XElement fill = Assert.Single(painted, e => (string)e.Attribute("fill") != "none");
        Assert.Equal("none", (string)fill.Attribute("stroke"));
        Assert.Equal($"url(#{(string)mask.Attribute("id")})", (string)fill.Attribute("mask"));
        XElement stroke = Assert.Single(painted, e => (string)e.Attribute("fill") == "none");
        Assert.Equal("0.5", (string)stroke.Attribute("stroke-opacity"));
        Assert.Null(stroke.Attribute("mask"));
    }

    /// <summary>
    /// Under a soft mask the fill's knockout mask and the soft mask cannot share one attribute:
    /// the fill sits in a group carrying the soft mask, and the stroke carries it itself.
    /// </summary>
    [Fact]
    public void SvgExport_UnderASoftMask_PutsTheSoftMaskOnAGroupRoundTheFill()
    {
        XDocument svg = Svg(Square("/SMask << /Type /Mask /S /Luminosity /G 6 0 R >>", "B", withMaskGroup: true));

        List<XElement> painted = Painted(svg);
        XElement fill = Assert.Single(painted, e => (string)e.Attribute("fill") != "none");
        Assert.StartsWith("url(#fsko", (string)fill.Attribute("mask"));
        Assert.NotNull(fill.Parent.Attribute("mask"));
        Assert.NotNull(Assert.Single(painted, e => (string)e.Attribute("fill") == "none").Attribute("mask"));
    }

    /// <summary>
    /// A soft mask and a blend mode together would put the fill's blend inside an isolated group,
    /// so the path stays one element and that is reported.
    /// </summary>
    [Fact]
    public void SvgExport_UnderASoftMaskAndABlendMode_IsOneElementAndReported()
    {
        byte[] pdf = Square("/BM /Multiply /SMask << /Type /Mask /S /Luminosity /G 6 0 R >>", "B", withMaskGroup: true);

        Assert.Single(Painted(Svg(pdf)));
        Assert.Contains(SvgReports(pdf), d => d.Contains("filled and stroked under both a soft mask"));
    }

    /// <summary>
    /// Mode-2 text in the SVG export is §11.7.4.4's knockout as a path is, on both glyph routes —
    /// the embedded CFF as a glyph-outline <c>&lt;g&gt;</c>, Helvetica as a <c>&lt;text&gt;</c>: the
    /// fill masked by the run filled white and stroked black at the stroke's width, then the
    /// stroke as its own element at its own opacity. An opaque run stays one element.
    /// </summary>
    [Theory]
    [InlineData("outline")]
    [InlineData("text")]
    public void SvgExport_FilledAndStrokedText_MasksTheFillWithItsStroke(string route)
    {
        string name = route == "outline" ? "g" : "text";
        byte[] pdf = TextRun(route, "/CA 0.5 /TK false");
        List<string> reports = SvgReports(pdf);
        XDocument svg = Svg(pdf);

        XElement mask = Assert.Single(svg.Descendants(SvgNs + "mask"));
        List<XElement> inMask = mask.Elements(SvgNs + name).ToList();
        Assert.Equal(2, inMask.Count);
        Assert.Equal("white", (string)inMask[0].Attribute("fill"));
        Assert.Equal("black", (string)inMask[1].Attribute("stroke"));

        List<XElement> painted = PaintedRuns(svg, name);
        Assert.Equal(2, painted.Count);
        Assert.Equal($"url(#{(string)mask.Attribute("id")})", (string)painted[0].Attribute("mask"));
        Assert.Equal("none", (string)painted[0].Attribute("stroke"));
        Assert.Equal("none", (string)painted[1].Attribute("fill"));
        Assert.Equal("0.5", (string)painted[1].Attribute("stroke-opacity"));
        Assert.Equal((string)painted[1].Attribute("stroke-width"), (string)inMask[1].Attribute("stroke-width"));
        Assert.DoesNotContain(reports, d => d.Contains("text filled and stroked"));

        XDocument opaque = Svg(TextRun(route, "/TK false"));
        Assert.Empty(opaque.Descendants(SvgNs + "mask"));
        Assert.Single(PaintedRuns(opaque, name));
    }

    /// <summary>
    /// Mode-2 text in the SVG export's overprint case: a group at the alpha, the run opaque inside it.
    /// </summary>
    [Theory]
    [InlineData("outline")]
    [InlineData("text")]
    public void SvgExport_FilledAndStrokedTextInTheOverprintCase_IsAGroupAtTheAlpha(string route)
    {
        XDocument svg = Svg(TextRun(route, "/OP true /op true /CA 0.5 /ca 0.5 /TK false"));

        XElement run = Assert.Single(PaintedRuns(svg, route == "outline" ? "g" : "text"));
        Assert.Equal("0.5", (string)run.Parent.Attribute("opacity"));
        Assert.Null(run.Attribute("fill-opacity"));
        Assert.Null(run.Attribute("stroke-opacity"));
        Assert.Empty(svg.Descendants(SvgNs + "mask"));
    }

    /// <summary>
    /// Under both a soft mask and a blend mode the run stays one element — a masked group would
    /// isolate the fill's blend — and that is reported; the same run under the soft mask alone is
    /// split, the soft mask on a group round the masked fill and on the stroke.
    /// </summary>
    [Fact]
    public void SvgExport_TextUnderASoftMaskAndABlendMode_IsOneElementAndReported()
    {
        const string Mask = " /SMask << /Type /Mask /S /Luminosity /G 6 0 R >>";
        byte[] both = TextRun("text", "/CA 0.5 /TK false /BM /Multiply" + Mask, withMaskGroup: true);
        byte[] maskOnly = TextRun("text", "/CA 0.5 /TK false" + Mask, withMaskGroup: true);

        Assert.Single(PaintedRuns(Svg(both), "text"));
        Assert.Contains(SvgReports(both), d => d.Contains("text filled and stroked"));
        List<XElement> halves = PaintedRuns(Svg(maskOnly), "text");
        Assert.Equal(2, halves.Count);
        Assert.StartsWith("url(#fsko", (string)halves[0].Attribute("mask"));
        Assert.StartsWith("url(#", (string)halves[0].Parent.Attribute("mask"));   // the soft mask, on a group
        Assert.StartsWith("url(#", (string)halves[1].Attribute("mask"));          // and on the stroke
        Assert.DoesNotContain(SvgReports(maskOnly), d => d.Contains("text filled and stroked"));
    }

    /// <summary>The run's painted elements: <paramref name="name"/> outside any <c>&lt;defs&gt;</c>, painting a fill or a stroke.</summary>
    private static List<XElement> PaintedRuns(XDocument svg, string name) =>
        svg.Descendants(SvgNs + name)
           .Where(e => !e.Ancestors(SvgNs + "defs").Any()
                    && ((string)e.Attribute("fill") is { } f && f != "none" && !f.StartsWith("rgba(0,0,0,0)")
                        || (string)e.Attribute("stroke") is { } k && k != "none"))
           .ToList();

    /// <summary>
    /// A mode-2 run, blue filled and red stroked 10 wide, under <paramref name="gs"/>: the embedded
    /// CFF glyph (<paramref name="route"/> "outline") or Helvetica's "H" ("text").
    /// </summary>
    private static byte[] TextRun(string route, string gs, bool withMaskGroup = false)
    {
        if (route == "outline")
        {
            string glyph = ((char)EmbeddedCffFixture.GlyphCharCode).ToString();
            return EmbeddedCffFixture.Page(
                $"/GS0 gs 0 0 1 rg 1 0 0 RG 10 w BT /F1 200 Tf 2 Tr 100 300 Td ({glyph}) Tj ET",
                $"/ExtGState << /GS0 << {gs} >> >>");
        }
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS0 5 0 R >> " +
            "/Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>",
            FormPdfFixtures.StreamBody(string.Empty,
                "/GS0 gs 0 0 1 rg 1 0 0 RG 10 w BT /F1 150 Tf 2 Tr 50 100 Td (H) Tj ET"),
            $"<< /Type /ExtGState {gs} >>",
        };
        if (withMaskGroup)
            bodies.Add(FormPdfFixtures.StreamBody(
                "/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceGray >>",
                "1 g 0 0 300 300 re f"));
        return FormPdfFixtures.Build(bodies.ToArray());
    }

    /// <summary>
    /// An opaque stroke whose inner edge splits a pixel (21 units wide: the edge at x 110.5) shows
    /// no seam: the fill is not clipped out, so the half-covered pixel is red over blue, with no
    /// white let through between them. Clipping the fill out for every path would leave one.
    /// </summary>
    [Fact]
    public void AnOpaqueStroke_LeavesNoSeamAtAPixelItSplits()
    {
        using var bmp = PixelAssert.Render(Square("", "B", width: "21"));
        SKColor edge = PixelAssert.At(bmp, 110, 150);

        Assert.InRange(edge.Green, 0, 5);
        Assert.InRange(edge.Red, 100, 160);
    }

    /// <summary>
    /// §11.7.4.4's overprint case — both overprint parameters true, equal alphas — is a
    /// non-knockout group painted at alpha 1 and composited at the alpha: the stroke covers the fill
    /// inside it, so the overlap is red at 0.5 over the page, and the interior is blue at 0.5.
    /// Painting the two at 0.5 each put the stroke over the fill.
    /// </summary>
    [Fact]
    public void TheOverprintCase_PaintsBothOpaqueInsideAGroupAtTheAlpha()
    {
        using var bmp = PixelAssert.Render(Square("/OP true /op true /CA 0.5 /ca 0.5", "B"));

        SKColor overlap = PixelAssert.At(bmp, 105, 150);
        Assert.InRange(overlap.Red, 230, 255);
        Assert.InRange(overlap.Green, 100, 160);
        SKColor interior = PixelAssert.At(bmp, 150, 150);
        Assert.InRange(interior.Red, 100, 160);
        Assert.InRange(interior.Blue, 230, 255);
    }

    /// <summary>
    /// On separation plates the overprint case keeps the fill's ink under the stroke: the stroke is
    /// magenta and, overprinting at <c>/OPM 1</c>, leaves cyan alone, so the cyan plate holds the
    /// fill's 0.5 under the stroke as in the interior. A knockout would have cut it out there.
    /// </summary>
    [Fact]
    public void TheOverprintCase_OnPlates_KeepsTheFillUnderTheStroke()
    {
        byte[] pdf = Square("/OP true /op true /OPM 1 /CA 0.5 /ca 0.5", "B", paints: "1 0 0 0 k 0 1 0 0 K");
        using var ms = new MemoryStream(pdf);
        PdfSeparations s = PdfSeparationRenderer.RenderPage(ms, 0, 72);

        int under = Ink(s, "Cyan", 105, 150), interior = Ink(s, "Cyan", 150, 150);
        Assert.InRange(interior, 120, 135);
        Assert.Equal(interior, under);
        Assert.InRange(Ink(s, "Magenta", 105, 150), 120, 135);
    }

    /// <summary>The SVG export of the overprint case: a group at the alpha, the path opaque inside it.</summary>
    [Fact]
    public void SvgExport_TheOverprintCase_IsAGroupAtTheAlpha()
    {
        XDocument svg = Svg(Square("/OP true /op true /CA 0.5 /ca 0.5", "B"));

        XElement path = Assert.Single(Painted(svg));
        Assert.Equal("0.5", (string)path.Parent.Attribute("opacity"));
        Assert.Equal("1", (string)path.Attribute("fill-opacity"));
        Assert.Equal("1", (string)path.Attribute("stroke-opacity"));
        Assert.Empty(svg.Descendants(SvgNs + "mask"));
    }

    private static int Ink(PdfSeparations s, string plate, int x, int y)
    {
        var p = s[plate];
        Assert.NotNull(p);
        return p.Tints[(s.Height - y) * s.Width + x];
    }

    /// <summary>
    /// An opaque stroke under a blend mode is knocked out too: in the group it blends with the
    /// page, not the fill, so red multiplied over white stays red. Multiplied over the blue fill it
    /// would be black.
    /// </summary>
    [Fact]
    public void AnOpaqueStrokeUnderABlendMode_BlendsWithTheBackdropNotTheFill()
    {
        using var bmp = PixelAssert.Render(Square("/BM /Multiply", "B"));

        Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(bmp, 105, 150));
        Assert.Equal(new SKColor(0, 0, 255), PixelAssert.At(bmp, 150, 150));
    }

    /// <summary>
    /// Mode-2 text in the overprint case is §11.7.4.4's group too, on both glyph routes: painted
    /// opaque inside it, the overlap is red at 0.5 over the page and the interior blue at 0.5.
    /// Painting the two halves at 0.5 each put the stroke over the fill (green ~64, not ~128).
    /// </summary>
    [Theory]
    [InlineData("cff")]
    [InlineData("type1")]
    public void TextFilledAndStrokedInTheOverprintCase_PaintsBothOpaqueInsideAGroupAtTheAlpha(string font)
    {
        string glyph = ((char)EmbeddedCffFixture.GlyphCharCode).ToString();
        string content = $"/GS0 gs 0 0 1 rg 1 0 0 RG 10 w BT /F1 200 Tf 2 Tr 100 300 Td ({glyph}) Tj ET";
        const string Gs = "/ExtGState << /GS0 << /OP true /op true /CA 0.5 /ca 0.5 /TK false >> >>";
        byte[] pdf = font == "cff" ? EmbeddedCffFixture.Page(content, Gs) : EmbeddedType1Fixture.Page(content, Gs);

        List<string> reports = Reports(() =>
        {
            using var bmp = PixelAssert.Render(pdf);
            SKColor overlap = PixelAssert.At(bmp, 102, 340);
            Assert.InRange(overlap.Red, 230, 255);
            Assert.InRange(overlap.Green, 100, 160);
            Assert.Equal(overlap, PixelAssert.At(bmp, 98, 340));
            SKColor interior = PixelAssert.At(bmp, 140, 340);
            Assert.InRange(interior.Red, 100, 160);
            Assert.InRange(interior.Blue, 230, 255);
        });
        Assert.DoesNotContain(reports, d => d.Contains("overprint case"));
    }

    /// <summary>
    /// A pattern-painted half of <c>B</c> is §11.7.4.4's knockout too: a coloured tiling pattern
    /// whose cell is solid blue as the fill, or solid red as the translucent stroke, gives what the
    /// solid paint does — pink over the fill's edge, as over the page. The pattern halves used to
    /// paint the stroke over the fill (purple).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APatternPaintedHalf_IsKnockedOutAsASolidOneIs(bool fillIsPattern)
    {
        string paints = fillIsPattern
            ? "/Pattern cs /P0 scn 1 0 0 RG"
            : "0 0 1 rg /Pattern CS /P0 SCN";
        string cell = fillIsPattern ? "0 0 1 rg" : "1 0 0 rg";
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS0 << /CA 0.5 >> >> /Pattern << /P0 5 0 R >> >> >>",
            FormPdfFixtures.StreamBody(string.Empty, $"/GS0 gs {paints} 20 w 100 100 100 100 re B"),
            FormPdfFixtures.StreamBody(
                "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >>",
                $"{cell} 0 0 10 10 re f"));

        using var bmp = PixelAssert.Render(pdf);
        SKColor inner = PixelAssert.At(bmp, 105, 150);
        Assert.InRange(inner.Red, 230, 255);
        Assert.InRange(inner.Green, 100, 160);
        Assert.InRange(inner.Blue, 100, 160);
        Assert.Equal(inner, PixelAssert.At(bmp, 95, 150));
        Assert.Equal(new SKColor(0, 0, 255), PixelAssert.At(bmp, 150, 150));
    }

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    private static XDocument Svg(byte[] pdf) => XDocument.Parse(PdfOperations.RenderPageToSvg(new MemoryStream(pdf), 0));

    /// <summary>The painted <c>&lt;path&gt;</c> elements: those outside any <c>&lt;defs&gt;</c>.</summary>
    private static List<XElement> Painted(XDocument svg) =>
        svg.Descendants(SvgNs + "path").Where(p => !p.Ancestors(SvgNs + "defs").Any()).ToList();

    private static List<string> SvgReports(byte[] pdf) => Reports(() => PdfOperations.RenderPageToSvg(new MemoryStream(pdf), 0));

    /// <summary>The <c>TransparencyGroupApproximated</c> details one render reports, on this thread.</summary>
    private static List<string> Reports(Action render)
    {
        var reports = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != own) return;
            if (kind == RenderDiagnosticKind.TransparencyGroupApproximated)
                lock (reports) reports.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (reports) return new List<string>(reports);
    }

    /// <summary>A page whose content fills and strokes one square with <paramref name="op"/> under an ExtGState.</summary>
    /// <param name="withMaskGroup">Adds object 6, a white luminosity mask group, for a <c>/SMask</c> in <paramref name="gs"/>.</param>
    /// <param name="paints">The fill and stroke colour operators.</param>
    /// <param name="width">The line width.</param>
    private static byte[] Square(string gs, string op, bool withMaskGroup = false,
                                 string paints = "0 0 1 rg 1 0 0 RG", string width = "20")
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS0 5 0 R >> >> >>",
            FormPdfFixtures.StreamBody(string.Empty,
                $"/GS0 gs {paints} {width} w 100 100 100 100 re {op}"),
            $"<< /Type /ExtGState {gs} >>",
        };
        if (withMaskGroup)
            bodies.Add(FormPdfFixtures.StreamBody(
                "/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Group << /S /Transparency /CS /DeviceGray >>",
                "1 g 0 0 300 300 re f"));
        return FormPdfFixtures.Build(bodies.ToArray());
    }
}
