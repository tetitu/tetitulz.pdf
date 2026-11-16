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
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.4.3's stroke geometry — line cap, line join, miter limit and dash — reaches <b>every</b>
/// stroked element in both renderers, not only a <c>&lt;path&gt;</c> and a path stroke.
/// </summary>
/// <remarks>
/// <para><b>Two defects of one shape.</b> The raster text sites built an <c>SKPaint</c> inline and
/// set only style, colour and width, so a stroked run silently took Skia's defaults for the other
/// four however the file had set <c>J</c>, <c>j</c>, <c>M</c> or <c>d</c> — while a path stroke
/// beside it honoured them. The SVG <c>&lt;text&gt;</c> and glyph-outline emitters wrote none of
/// the four either. Both now go through one place per renderer.</para>
///
/// <para><b>And the SVG miter limit was wrong for the default graphics state.</b> The exporter
/// omitted <c>stroke-miterlimit</c> whenever it equalled 10 — PDF's initial value — on a comment's
/// claim that "PDF and SVG both default to 10". SVG's initial value is <b>4</b>, so every ordinary
/// stroke, in every file that never writes an <c>M</c> operator, exported with an effective limit
/// of 4 while the raster path stroked it at 10. Sharp joins came out as spikes in the render and
/// bevels in the export.</para>
///
/// <para>SVG assertions parse the document as an <see cref="XDocument"/>, never as a substring.
/// Raster assertions are ink counts, because which face the platform substitutes for a
/// non-embedded font differs between hosts.</para>
/// </remarks>
public class StrokeGeometryParityTests
{
    private const int Dpi = 144;

    // ====================================================================
    //  The miter limit, against SVG's default rather than PDF's
    // ====================================================================

    /// <summary>
    /// A stroke in the default graphics state emits <c>stroke-miterlimit="10"</c> — it must not be
    /// left implicit, because implicit means 4 in SVG.
    /// </summary>
    [Fact]
    public void ADefaultMiterLimit_IsEmittedRatherThanLeftImplicit()
    {
        XElement path = StrokedPath(Svg("4 w 10 10 m 50 50 l 90 10 l S"));

        Assert.Equal(10f, Attr(path, "stroke-miterlimit"), 3);
    }

    /// <summary>
    /// The value SVG already means by omission — 4 — is the one that may be omitted.
    /// </summary>
    /// <remarks>
    /// The acceptance control for the test above: without it, "always emit the attribute" would
    /// satisfy that one, and the omission optimisation would be deleted rather than corrected.
    /// </remarks>
    [Fact]
    public void SvgsOwnDefaultMiterLimit_IsTheOneLeftImplicit()
    {
        XElement path = StrokedPath(Svg("4 w 4 M 10 10 m 50 50 l 90 10 l S"));

        Assert.Null(path.Attribute("stroke-miterlimit"));
    }

    /// <summary>An explicitly set limit is emitted as written.</summary>
    [Fact]
    public void AnExplicitMiterLimit_IsEmittedAsWritten()
    {
        XElement path = StrokedPath(Svg("4 w 2 M 10 10 m 50 50 l 90 10 l S"));

        Assert.Equal(2f, Attr(path, "stroke-miterlimit"), 3);
    }

    /// <summary>A miter limit below SVG's legal minimum of 1 is clamped, not passed through.</summary>
    /// <remarks>
    /// SVG rejects <c>stroke-miterlimit</c> below 1 outright rather than clamping it, and a
    /// rejected attribute reverts to 4 — so passing a PDF's <c>0 M</c> straight through would
    /// silently re-open the same divergence at the other end of the range. Now that all three
    /// emitters share this line, one test covers all three.
    /// </remarks>
    [Fact]
    public void AMiterLimitBelowOne_IsClampedToSvgsLegalMinimum()
    {
        XElement path = StrokedPath(Svg("4 w 0 M 10 10 m 50 50 l 90 10 l S"));

        Assert.Equal(1f, Attr(path, "stroke-miterlimit"), 3);
    }

    // ====================================================================
    //  SVG: <text> and glyph outlines carry what <path> carries
    // ====================================================================

    /// <summary>A stroked <c>&lt;text&gt;</c> carries the cap, the join and the dash.</summary>
    [Fact]
    public void AStrokedTextElement_CarriesTheStrokeGeometry()
    {
        XElement text = StrokedText(Svg(
            "1 J 1 j [6 3] 0 d 2 w BT /F1 40 Tf 1 Tr 10 20 Td (III) Tj ET"));

        Assert.Equal("round", text.Attribute("stroke-linecap")?.Value);
        Assert.Equal("round", text.Attribute("stroke-linejoin")?.Value);
        Assert.NotNull(text.Attribute("stroke-dasharray"));
    }

    /// <summary>
    /// The dash array on a <c>&lt;text&gt;</c> is divided by the element's own matrix, exactly as
    /// its <c>stroke-width</c> is.
    /// </summary>
    /// <remarks>
    /// A dash array is a set of user-space lengths and the element carries the font size in its
    /// transform, so an undivided array would be as wrong as an undivided width — the trap the
    /// width's own comment already documents. At 40 pt with no CTM the divisor is 40, so
    /// <c>[6 3]</c> becomes <c>0.15,0.075</c>.
    /// </remarks>
    [Fact]
    public void TheDashArrayOnAText_IsDividedByTheElementMatrix()
    {
        XElement text = StrokedText(Svg("[6 3] 0 d 2 w BT /F1 40 Tf 1 Tr 10 20 Td (III) Tj ET"));

        float[] dashes = text.Attribute("stroke-dasharray")!.Value
            .Split(',')
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(2, dashes.Length);
        Assert.Equal(6f / 40f, dashes[0], 4);
        Assert.Equal(3f / 40f, dashes[1], 4);
    }

    /// <summary>The glyph-outline group carries it too, on its own scale.</summary>
    /// <remarks>
    /// A separate emitter from the <c>&lt;text&gt;</c> above, and the one whose divisor includes
    /// the charstring's 1/1000 em — so a fix landing on one and not the other is the divergence
    /// this pair keeps producing.
    /// </remarks>
    [Fact]
    public void AStrokedGlyphOutlineGroup_CarriesTheStrokeGeometry()
    {
        string content = "1 J 1 j [6 3] 0 d 2 w BT /F1 48 Tf 1 Tr 100 400 Td "
                       + $"({(char)EmbeddedCffFixture.GlyphCharCode}) Tj ET";
        XElement group = OutlineGroup(EmbeddedSvg(content));

        Assert.Equal("round", group.Attribute("stroke-linecap")?.Value);
        Assert.Equal("round", group.Attribute("stroke-linejoin")?.Value);
        Assert.NotNull(group.Attribute("stroke-dasharray"));
    }

    // ====================================================================
    //  Raster: the text pen is the path pen
    // ====================================================================

    /// <summary>A dash reaches a stroked text run, so it lays down less ink than a solid pen.</summary>
    /// <remarks>
    /// The measurable form of "the dash was applied at all". The raster text sites set no
    /// <c>PathEffect</c>, so a dashed run painted solid — identical ink to the undashed control,
    /// which is what this separates.
    /// </remarks>
    [Fact]
    public void ADashedTextStroke_LaysDownLessInkThanASolidOne()
    {
        int solid  = DarkPixels("2 w BT /F1 60 Tf 1 Tr 10 20 Td (IIII) Tj ET");
        int dashed = DarkPixels("[6 3] 0 d 2 w BT /F1 60 Tf 1 Tr 10 20 Td (IIII) Tj ET");

        Assert.True(solid > 0, "the control run drew nothing at all");
        Assert.True(dashed < solid * 0.9,
            $"a dashed text stroke should lay down clearly less ink; solid {solid}, dashed {dashed}");
    }

    /// <summary>The same dash on a path stroke, as the control for what "less ink" measures.</summary>
    [Fact]
    public void ADashedPathStroke_LaysDownLessInkThanASolidOne_Control()
    {
        int solid  = DarkPixels("2 w 10 50 m 280 50 l S");
        int dashed = DarkPixels("[6 3] 0 d 2 w 10 50 m 280 50 l S");

        Assert.True(dashed < solid * 0.9,
            $"the control: solid {solid}, dashed {dashed}");
    }

    /// <summary>The line join reaches a stroked text run: a round join eats each glyph corner.</summary>
    /// <remarks>
    /// Join is the parameter of the four that a glyph shows plainly, because a glyph contour is a
    /// closed loop of corners. A round join replaces each square corner with a quarter-disc, which
    /// on a thick pen is a measurable amount of ink.
    /// </remarks>
    [Fact]
    public void TheLineJoinReachesAStrokedTextRun()
    {
        int miter = DarkPixels("0 j 6 w BT /F1 60 Tf 1 Tr 10 20 Td (IIII) Tj ET");
        int round = DarkPixels("1 j 6 w BT /F1 60 Tf 1 Tr 10 20 Td (IIII) Tj ET");

        Assert.True(miter > 0, "the control run drew nothing at all");
        Assert.True(miter > round,
            $"a miter join keeps the corner a round join cuts away; miter {miter}, round {round}");
    }

    /// <summary>
    /// The line cap reaches a stroked text run — visible once a dash gives the closed contour
    /// some ends to cap.
    /// </summary>
    /// <remarks>
    /// <b>A cap is unobservable on an undashed glyph, and asserting otherwise wrote a test that
    /// could not fail.</b> A glyph contour is closed, so it has no stroke ends and
    /// <c>0 J</c> and <c>1 J</c> paint identically — measured at 9016 pixels either way. Dashing
    /// it cuts the loop into segments, and every segment end then takes the cap. So the fixture
    /// that shows the cap is a dashed one, which is also the honest statement of when a PDF's
    /// <c>J</c> can matter to a glyph at all.
    /// </remarks>
    [Fact]
    public void TheLineCapReachesADashedTextRun()
    {
        int butt  = DarkPixels("0 J [6 3] 0 d 6 w BT /F1 60 Tf 1 Tr 10 20 Td (IIII) Tj ET");
        int round = DarkPixels("1 J [6 3] 0 d 6 w BT /F1 60 Tf 1 Tr 10 20 Td (IIII) Tj ET");

        Assert.True(butt > 0, "the control run drew nothing at all");
        Assert.True(round > butt,
            $"a round cap adds a half-disc at each dash end; butt {butt}, round {round}");
    }

    /// <summary>
    /// A <c>2 Tr</c> run — filled <b>and</b> stroked — honours the dash on its stroke half, on the
    /// glyph-blob route.
    /// </summary>
    /// <remarks>
    /// The fill is white and the pen black, so every dark pixel is the pen's. An embedded CFF is
    /// OTF-wrapped and drawn as a glyph blob; the pair below takes the other route.
    /// </remarks>
    [Fact]
    public void ADashReachesTheStrokeHalfOfAFilledAndStrokedRun()
    {
        string glyph = ((char)EmbeddedCffFixture.GlyphCharCode).ToString();
        int solid  = CffInk($"1 1 1 rg 0 0 0 RG 3 w BT /F1 200 Tf 2 Tr 100 300 Td ({glyph}) Tj ET");
        int dashed = CffInk($"1 1 1 rg 0 0 0 RG [6 6] 0 d 3 w BT /F1 200 Tf 2 Tr 100 300 Td ({glyph}) Tj ET");

        Assert.True(solid > 0, "the control run drew no stroke at all");
        Assert.True(dashed < solid,
            $"a dashed stroke on a 2 Tr run should lay down less ink; solid {solid}, dashed {dashed}");
    }

    /// <summary>
    /// The same claim on the <b>charstring-outline</b> route, where fill and stroke share one paint.
    /// </summary>
    /// <remarks>
    /// <para><b>Fill-and-stroke is drawn with a single paint</b>, because the two halves share one
    /// soft-mask layer and masking them apart would composite the shared interior edge twice. The
    /// stroke's properties are therefore <i>copied onto</i> the fill's paint rather than carried by
    /// a paint of their own, and before this change only the colour and the width were copied —
    /// leaving cap, join, miter limit and dash at Skia's defaults.</para>
    /// <para><b>An embedded CFF cannot reach that code and the test above does not, though its
    /// first version claimed to.</b> Deleting the entire stroke draw inside the shared-paint op
    /// left it green, which is how the mistake surfaced: <c>FontCache</c> OTF-wraps a CFF, so it
    /// goes to the glyph blob. Only a Type 1 program reaches the outline route — see
    /// <see cref="EmbeddedType1Fixture"/>, which exists for exactly this distinction.</para>
    /// <para><b>Which route this fixture takes is host-dependent, and the assertion is written so
    /// that does not matter.</b> The outline route runs when Skia's Skia declines the PFB wrap,
    /// which it does on the SkiaSharp build this repository references; on a host that accepts it
    /// the run goes through the blob instead. A dashed stroke lays down less ink than a solid one
    /// either way, so this is a true statement about the renderer on every host — only which line
    /// it covers changes. <c>Type1EmbeddedFontTests</c> is where the route itself is recorded; do
    /// not assert on it here.</para>
    /// </remarks>
    [Fact]
    public void ADashReachesTheStrokeHalfOfAFilledAndStrokedOutlineRun()
    {
        string glyph = ((char)EmbeddedType1Fixture.GlyphCharCode).ToString();
        int solid  = Type1Ink($"1 1 1 rg 0 0 0 RG 3 w BT /F1 200 Tf 2 Tr 100 300 Td ({glyph}) Tj ET");
        int dashed = Type1Ink($"1 1 1 rg 0 0 0 RG [6 6] 0 d 3 w BT /F1 200 Tf 2 Tr 100 300 Td ({glyph}) Tj ET");

        Assert.True(solid > 0, "the control run drew no stroke at all");
        Assert.True(dashed < solid,
            $"a dashed stroke on a 2 Tr outline run should lay down less ink; solid {solid}, dashed {dashed}");
    }

    // ====================================================================
    //  The CTM half of the dash conversion
    // ====================================================================

    /// <summary>
    /// A path's dash array is scaled by the CTM — §8.4.3.6's lengths are in user space.
    /// </summary>
    /// <remarks>
    /// <b>Written because a mutation survived.</b> Removing the CTM from the shared dash
    /// conversion left the whole suite green: every existing dash fixture runs under an identity
    /// CTM, where that factor is 1. The two text emitters cannot cover it either — their element
    /// matrix carries the CTM as well, so it cancels exactly and the mutation is genuinely
    /// equivalent there. Only a <c>&lt;path&gt;</c> under a <c>cm</c> tells the two apart.
    /// </remarks>
    [Fact]
    public void APathsDashArray_IsScaledByTheCtm()
    {
        XElement path = StrokedPath(Svg("2 0 0 2 0 0 cm 4 w [6 6] 0 d 5 25 m 40 25 l S"));

        float[] dashes = path.Attribute("stroke-dasharray")!.Value
            .Split(',')
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(new[] { 12f, 12f }, dashes);
    }

    /// <summary>The raster renderer scales it too: a doubled CTM halves the dash count.</summary>
    /// <remarks>
    /// The parity half of the test above, and the reason the export's number is the right one.
    /// Both fixtures draw the <b>same device line</b> — 10 to 80 at y 45 in page units — so the
    /// only thing that differs is the dash period, 12 page units against 24.
    /// </remarks>
    [Fact]
    public void TheRasterDashPeriod_IsScaledByTheCtmToo()
    {
        int plain   = DashRuns("4 w [6 6] 0 d 10 45 m 80 45 l S");
        int doubled = DashRuns("2 0 0 2 0 0 cm 4 w [6 6] 0 d 5 22.5 m 40 22.5 l S");

        Assert.True(plain > 1, $"the control drew {plain} dashes, so it was not dashed at all");
        Assert.True(plain > doubled,
            $"a doubled CTM should draw fewer, longer dashes; plain {plain}, doubled {doubled}");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private static XElement StrokedPath(string svg) => Stroked(svg, "path");
    private static XElement StrokedText(string svg) => Stroked(svg, "text");

    private static XElement Stroked(string svg, string localName)
    {
        XElement[] found = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == localName)
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .ToArray();
        Assert.True(found.Length > 0,
            $"the export drew no stroked <{localName}>:" + Environment.NewLine + svg);
        return found[0];
    }

    private static XElement OutlineGroup(string svg)
    {
        XElement[] groups = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "g")
            .Where(e => (e.Attribute("stroke")?.Value ?? "none") != "none")
            .Where(e => e.Elements().Any(c => c.Name.LocalName is "use" or "path"))
            .ToArray();
        Assert.True(groups.Length > 0,
            "the export drew no stroked glyph-outline group:" + Environment.NewLine + svg);
        return groups[0];
    }

    private static float Attr(XElement e, string name)
    {
        string raw = e.Attribute(name)?.Value;
        Assert.False(raw == null, $"<{e.Name.LocalName}> has no {name} attribute");
        return float.Parse(raw, CultureInfo.InvariantCulture);
    }

    private static string Svg(string content)
    {
        using var ms = new MemoryStream(BuildPdf(content));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string EmbeddedSvg(string content)
    {
        using var ms = new MemoryStream(EmbeddedCffFixture.Page(content));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// Dark pixels on an embedded-CFF page. The fixture's glyph is a solid box, so a run painted
    /// with a white fill and a black pen inks only where the pen went.
    /// </summary>
    private static int CffInk(string content) =>
        InkOf(EmbeddedCffFixture.Page(content));

    /// <summary>The same, on the Type 1 fixture — the one that can reach the outline route.</summary>
    private static int Type1Ink(string content) =>
        InkOf(EmbeddedType1Fixture.Page(content));

    private static int InkOf(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int n = 0;
        for (int y = 0; y < bmp!.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Red + c.Green + c.Blue < 3 * 128) n++;
            }
        return n;
    }

    /// <summary>
    /// How many separate runs of dark pixels the line at page y=45 is broken into — the dash
    /// count, measured rather than inferred from the operands.
    /// </summary>
    private static int DashRuns(string content)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(BuildPdf(content)), 0, Dpi);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        // The 300x120 page at 144 dpi is scaled by 2, and PDF y runs upward: y=45 is row 150.
        int row = (int)((120 - 45) * (Dpi / 72f));
        int runs = 0;
        bool inRun = false;
        for (int x = 0; x < bmp!.Width; x++)
        {
            SKColor c = bmp.GetPixel(x, row);
            bool dark = c.Red + c.Green + c.Blue < 3 * 128;
            if (dark && !inRun) runs++;
            inRun = dark;
        }
        return runs;
    }

    /// <summary>
    /// Pixels darker than mid-grey. A 50% threshold is unbiased over an anti-aliased edge, so the
    /// count tracks covered area rather than area plus a fringe per edge.
    /// </summary>
    private static int DarkPixels(string content)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(BuildPdf(content)), 0, Dpi);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int n = 0;
        for (int y = 0; y < bmp!.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Red + c.Green + c.Blue < 3 * 128) n++;
            }
        return n;
    }

    private static byte[] BuildPdf(string content)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(pdf.Length); pdf.Append(body); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Resources " +
            "<< /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
