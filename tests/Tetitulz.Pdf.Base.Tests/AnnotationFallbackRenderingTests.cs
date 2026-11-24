// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Tests.Rendering;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for annotation fallback rendering (gap 28.1) and QuadPoints geometry (gap 28.5).
/// All PDFs are built in memory — no disk files required.
/// </summary>
/// <remarks>
/// These assert on <b>decoded pixels</b>: that the fallback painted, that it painted inside the
/// annotation's /Rect, and that it used the declared colour. <c>NotNull</c>/<c>NotEmpty</c> on
/// the PNG would pass with annotation fallback rendering deleted outright — the page content
/// stream here is <c>q Q</c>, so a blank page encodes to a perfectly valid non-empty PNG.
/// </remarks>
public sealed class AnnotationFallbackRenderingTests
{
    // ------------------------------------------------------------------
    //  Pixel assertions
    // ------------------------------------------------------------------

    private const int Dpi = 72;

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, Dpi);
        Assert.NotNull(png);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>PDF user-space Y (origin bottom-left) → device row (origin top-left).</summary>
    private static int Row(SKBitmap bmp, float userY) => bmp.Height - 1 - (int)(userY * Dpi / 72f);

    private static int Col(float userX) => (int)(userX * Dpi / 72f);

    private static bool IsInk(SKColor c)
        => c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240);

    /// <summary>Counts inked pixels, optionally restricted to a user-space rectangle.</summary>
    private static int CountInk(SKBitmap bmp, float? x0 = null, float? y0 = null,
                                              float? x1 = null, float? y1 = null)
    {
        int cl = x0 is float a ? Math.Max(0, Col(a)) : 0;
        int cr = x1 is float b ? Math.Min(bmp.Width - 1, Col(b)) : bmp.Width - 1;
        int rt = y1 is float c ? Math.Max(0, Row(bmp, c)) : 0;
        int rb = y0 is float d ? Math.Min(bmp.Height - 1, Row(bmp, d)) : bmp.Height - 1;

        int n = 0;
        for (int y = rt; y <= rb; y++)
            for (int x = cl; x <= cr; x++)
                if (IsInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    /// <summary>
    /// Asserts the annotation painted something, and painted it inside its /Rect: the page
    /// content stream is empty, so every inked pixel must come from the annotation.
    /// A margin absorbs antialiasing and the border's outward half-width.
    /// </summary>
    private static SKBitmap AssertPaintedWithin(
        byte[] pdf, float x0, float y0, float x1, float y1, float margin = 6f)
    {
        var bmp = Render(pdf);
        int inside = CountInk(bmp, Math.Min(x0, x1) - margin, Math.Min(y0, y1) - margin,
                                   Math.Max(x0, x1) + margin, Math.Max(y0, y1) + margin);
        int total  = CountInk(bmp);

        Assert.True(inside > 0,
            "The annotation fallback painted nothing — the page is blank.");
        Assert.True(total == inside,
            $"{total - inside} inked pixels fell outside the annotation /Rect " +
            $"[{x0} {y0} {x1} {y1}] (+{margin}pt margin).");
        return bmp;
    }

    /// <summary>Mean colour of the inked pixels inside a user-space rectangle.</summary>
    private static SKColor MeanInk(SKBitmap bmp, float x0, float y0, float x1, float y1)
    {
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = Row(bmp, Math.Max(y0, y1)); y <= Row(bmp, Math.Min(y0, y1)); y++)
            for (int x = Col(Math.Min(x0, x1)); x <= Col(Math.Max(x0, x1)); x++)
            {
                if (y < 0 || y >= bmp.Height || x < 0 || x >= bmp.Width) continue;
                var c = bmp.GetPixel(x, y);
                if (!IsInk(c)) continue;
                r += c.Red; g += c.Green; b += c.Blue; n++;
            }
        Assert.True(n > 0, "No inked pixels to take a mean colour from.");
        return new SKColor((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>
    /// Asserts the painted ink leans towards the declared colour. Deliberately loose: the
    /// fallback blends a border colour with an interior colour and antialiases, so this pins
    /// down the hue relationship (e.g. "red channel dominates") rather than an exact value.
    /// </summary>
    private static void AssertChannelDominates(SKColor ink, char channel, int by = 40)
    {
        int r = ink.Red, g = ink.Green, b = ink.Blue;
        (int lead, int o1, int o2, string name) = channel switch
        {
            'R' => (r, g, b, "red"),
            'G' => (g, r, b, "green"),
            'B' => (b, r, g, "blue"),
            _   => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
        Assert.True(lead >= o1 + by && lead >= o2 + by,
            $"Expected the {name} channel to dominate by ≥{by}; got RGB({r},{g},{b}).");
    }

    // ------------------------------------------------------------------
    //  28.1 — Annotation fallback synthesis (no /AP stream)
    // ------------------------------------------------------------------

    [Fact]
    public void SquareAnnotation_WithoutAP_PaintsRedBorderInsideRect()
    {
        // Square annotation at top of page with red border, no /AP stream.
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Square",
            rect: "100 600 300 700",
            extraKeys: "/C [1 0 0]");

        var bmp = AssertPaintedWithin(pdf, 100, 600, 300, 700);
        AssertChannelDominates(MeanInk(bmp, 100, 600, 300, 700), 'R');

        // Border only: the interior is unpainted without /IC, so the ink must hug the edges.
        Assert.Equal(0, CountInk(bmp, 130, 630, 270, 670));
    }

    [Fact]
    public void SquareAnnotation_WithInteriorColor_FillsTheRect()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Square",
            rect: "100 600 300 700",
            extraKeys: "/C [0 0 1] /IC [0.8 0.9 1]");

        var bmp = AssertPaintedWithin(pdf, 100, 600, 300, 700);

        // /IC fills the interior, so the middle of the rect must be painted (contrast with
        // the border-only case above, which asserts the same region is empty).
        Assert.True(CountInk(bmp, 130, 630, 270, 670) > 0,
            "/IC was ignored — the annotation interior is unpainted.");
        AssertChannelDominates(MeanInk(bmp, 130, 630, 270, 670), 'B', by: 8);
    }

    [Fact]
    public void CircleAnnotation_WithoutAP_PaintsAnEllipseInsideRect()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Circle",
            rect: "150 550 350 650",
            extraKeys: "/C [0 0.5 0] /IC [0.9 1 0.9]");

        var bmp = AssertPaintedWithin(pdf, 150, 550, 350, 650);

        // An inscribed ellipse covers the centre but leaves the rect's corners clear —
        // that is what distinguishes it from the Square fallback.
        Assert.True(CountInk(bmp, 240, 590, 260, 610) > 0, "Ellipse centre is unpainted.");
        Assert.Equal(0, CountInk(bmp, 151, 551, 161, 561));   // bottom-left corner
        Assert.Equal(0, CountInk(bmp, 339, 639, 349, 649));   // top-right corner
    }

    [Fact]
    public void LineAnnotation_WithoutAP_DrawsAlongTheLEndpoints()
    {
        // /L gives line endpoints; the fallback must stroke between them.
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Line",
            rect: "50 500 400 550",
            extraKeys: "/C [0 0 0] /L [50 525 400 525]");

        var bmp = AssertPaintedWithin(pdf, 50, 500, 400, 550);

        // The line runs horizontally at y=525, so ink concentrates in a band around it and
        // the rest of the /Rect stays clear.
        Assert.True(CountInk(bmp, 50, 521, 400, 529) > 0, "No ink on the /L line itself.");
        Assert.Equal(0, CountInk(bmp, 50, 500, 400, 515));
        Assert.Equal(0, CountInk(bmp, 50, 535, 400, 550));
    }

    [Fact]
    public void TextAnnotation_WithoutAP_PaintsAnIconInsideRect()
    {
        // Sticky-note Text annotation — fallback draws a small coloured icon.
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Text",
            rect: "50 700 72 722",
            extraKeys: "/C [1 1 0]");

        var bmp = AssertPaintedWithin(pdf, 50, 700, 72, 722);

        // /C [1 1 0] is yellow: red and green high, blue low.
        var ink = MeanInk(bmp, 50, 700, 72, 722);
        Assert.True(ink.Blue < ink.Red - 40 && ink.Blue < ink.Green - 40,
            $"Expected a yellow icon; got RGB({ink.Red},{ink.Green},{ink.Blue}).");
    }

    [Fact]
    public void SquareAnnotation_WithThickBorder_UsesTheBsWidth()
    {
        // /BS /W 3 — border width 3 pts, versus the 1 pt default.
        byte[] thin = BuildPdfWithAnnotation(
            subtype: "Square", rect: "100 600 300 700", extraKeys: "/C [0.5 0 0.5]");
        byte[] thick = BuildPdfWithAnnotation(
            subtype: "Square", rect: "100 600 300 700",
            extraKeys: "/C [0.5 0 0.5] /BS << /W 3 >>");

        int thinInk  = CountInk(AssertPaintedWithin(thin,  100, 600, 300, 700));
        int thickInk = CountInk(AssertPaintedWithin(thick, 100, 600, 300, 700));

        Assert.True(thickInk > thinInk * 2,
            $"/BS /W 3 did not widen the border: {thickInk} px vs {thinInk} px at the default width.");
    }

    [Fact]
    public void SquareAnnotation_WithOpacity_BlendsTowardsTheBackground()
    {
        // /CA 0.5 — 50 % opacity, so the red border must land midway to the white page.
        byte[] opaque = BuildPdfWithAnnotation(
            subtype: "Square", rect: "100 600 300 700", extraKeys: "/C [1 0 0] /IC [1 0 0]");
        byte[] half = BuildPdfWithAnnotation(
            subtype: "Square", rect: "100 600 300 700",
            extraKeys: "/C [1 0 0] /IC [1 0 0] /CA 0.5");

        var opaqueInk = MeanInk(AssertPaintedWithin(opaque, 100, 600, 300, 700), 130, 630, 270, 670);
        var halfInk   = MeanInk(AssertPaintedWithin(half,   100, 600, 300, 700), 130, 630, 270, 670);

        // Red over white at 50%: green/blue rise from ~0 towards ~128.
        Assert.True(halfInk.Green > opaqueInk.Green + 40,
            $"/CA 0.5 was ignored: green {halfInk.Green} vs opaque {opaqueInk.Green}.");
    }

    [Fact]
    public void AnnotationWithAPStream_UsesAppearanceStreamPath()
    {
        // When /AP /N is present the renderer must execute it rather than synthesising a
        // fallback. The AP stream here paints the whole BBox, so the /Rect fills solid —
        // the Square fallback for the same dict would draw a hollow border.
        byte[] pdf = BuildPdfWithApAnnotation();

        var bmp = AssertPaintedWithin(pdf, 100, 600, 300, 700);
        Assert.True(CountInk(bmp, 130, 630, 270, 670) > 0,
            "The /AP appearance stream was not executed — the annotation interior is empty.");
    }

    // ------------------------------------------------------------------
    //  28.5 — QuadPoints annotation geometry
    // ------------------------------------------------------------------

    [Fact]
    public void HighlightAnnotation_WithQuadPoints_FillsTheQuad()
    {
        // Highlight uses /QuadPoints to define the coloured area.
        string qp = "/QuadPoints [100 700 300 700 100 680 300 680]";
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "100 680 300 700",
            extraKeys: "/C [1 1 0] " + qp);

        var bmp = AssertPaintedWithin(pdf, 100, 680, 300, 700);

        // A highlight washes the whole quad, so its middle must be inked (unlike Underline
        // and StrikeOut below, which only draw a rule).
        Assert.True(CountInk(bmp, 150, 688, 250, 692) > 0, "The highlight quad is unpainted.");
        var ink = MeanInk(bmp, 100, 680, 300, 700);
        Assert.True(ink.Blue < ink.Red - 40 && ink.Blue < ink.Green - 40,
            $"Expected a yellow highlight; got RGB({ink.Red},{ink.Green},{ink.Blue}).");
    }

    /// <summary>
    /// A quad whose corners are the <c>[-32768 … 32768]</c> sentinel paints <b>nothing</b>, in both
    /// renderers.
    /// </summary>
    /// <remarks>
    /// The raster path has skipped these since the geometry was written — a quad that large is a
    /// producer's "unset" marker, not a rectangle, and drawing it covers the page. The SVG exporter
    /// had no such guard, so the identical file rendered as a clean page and exported as one
    /// enormous filled path. That is the repository's recurring defect class in its worst form: not
    /// a wrong pixel, but a confidently wrong page that looks fine in the renderer people check.
    /// <para>Asserted from BOTH sides on one fixture, because either alone passes for the wrong
    /// reason — the raster assertion passes today with the SVG guard missing, and an SVG-only
    /// assertion could be satisfied by an exporter that had stopped emitting annotations at all.
    /// The companion test above is the floor for that second case.</para>
    /// </remarks>
    [Fact]
    public void SentinelQuadPoints_PaintNothing_InBothRenderers()
    {
        string qp = "/QuadPoints [-32768 -32768 32768 -32768 -32768 32768 32768 32768]";
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "100 680 300 700",
            extraKeys: "/C [1 1 0] " + qp);

        using (var bmp = PixelAssert.Render(pdf))
        {
            Assert.True(PixelAssert.IsBlank(bmp),
                "The raster path painted a sentinel quad; it has always skipped these.");
        }

        using var ms = new System.IO.MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // Parsed as XML, never substring-matched: an export that is not well-formed once went
        // unnoticed here for the suite's whole history.
        var painted = System.Xml.Linq.XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path" &&
                        (e.Attribute("fill")?.Value ?? "none") != "none")
            .ToList();

        Assert.True(painted.Count == 0,
            "The SVG export painted a sentinel quad the raster path skips. Its `d` was: " +
            string.Join(" | ", painted.Select(p => p.Attribute("d")?.Value)));
    }

    /// <summary>
    /// A highlight <b>multiplies</b> at full opacity in both renderers (§12.5.6.10) — which is what
    /// keeps the text under it readable.
    /// </summary>
    /// <remarks>
    /// The raster path composites Multiply at <c>/CA</c>. The SVG export emitted a fixed
    /// <c>fill-opacity="0.25"</c> with normal blending, <i>on top of</i> the group's own <c>/CA</c>
    /// opacity — so the same annotation was a quarter-strength wash that greyed the glyphs out
    /// instead of leaving them dark. Both halves are asserted here because each catches a different
    /// half of the defect: the raster one that the model is multiply, the SVG one that the export
    /// says so rather than approximating it with alpha.
    /// </remarks>
    [Fact]
    public void Highlight_MultipliesAtFullOpacity_InBothRenderers()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "100 680 300 700",
            extraKeys: "/C [1 1 0] /QuadPoints [100 700 300 700 100 680 300 680]");

        // Raster: a yellow multiply over white paper is yellow — full strength, not a pale
        // quarter-wash. Blue is the channel multiply drives to zero.
        using (var bmp = PixelAssert.Render(pdf))
        {
            var ink = MeanInk(bmp, 120, 685, 280, 695);
            Assert.True(ink.Red > 200 && ink.Green > 200,
                $"a full-opacity yellow highlight should stay saturated; got {ink}");
            Assert.True(ink.Blue < 80,
                $"multiply should drive blue to zero under a yellow wash; got {ink}");
        }

        using var ms = new System.IO.MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        var path = System.Xml.Linq.XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "path" &&
                         (e.Attribute("fill")?.Value ?? "none") != "none");

        Assert.Contains("mix-blend-mode:multiply", path.Attribute("style")?.Value ?? "",
                        StringComparison.Ordinal);
        Assert.DoesNotContain("0.25", path.Attribute("fill-opacity")?.Value ?? "",
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// The floor for the test above: an ordinary quad still exports a painted path, so
    /// "nothing was painted" cannot pass by the exporter having stopped emitting annotations.
    /// </summary>
    [Fact]
    public void OrdinaryQuadPoints_StillExportAPaintedPath()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "100 680 300 700",
            extraKeys: "/C [1 1 0] /QuadPoints [100 700 300 700 100 680 300 680]");

        using var ms = new System.IO.MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.Contains(System.Xml.Linq.XDocument.Parse(svg).Descendants(),
            e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none");
    }

    [Fact]
    public void UnderlineAnnotation_WithQuadPoints_DrawsAtTheQuadBottom()
    {
        string qp = "/QuadPoints [100 700 300 700 100 680 300 680]";
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Underline",
            rect: "100 680 300 700",
            extraKeys: "/C [0 0 1] " + qp);

        var bmp = AssertPaintedWithin(pdf, 100, 680, 300, 700);

        // An underline sits at the bottom of the quad, not through its middle.
        int low  = CountInk(bmp, 100, 680, 300, 686);
        int high = CountInk(bmp, 100, 694, 300, 700);
        Assert.True(low > 0, "No ink at the bottom of the quad — nothing was underlined.");
        Assert.True(low > high, $"Underline is not at the quad bottom (low={low}, high={high}).");
    }

    [Fact]
    public void StrikeOutAnnotation_WithQuadPoints_DrawsThroughTheQuadMiddle()
    {
        string qp = "/QuadPoints [100 700 300 700 100 680 300 680]";
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "StrikeOut",
            rect: "100 680 300 700",
            extraKeys: "/C [1 0 0] " + qp);

        var bmp = AssertPaintedWithin(pdf, 100, 680, 300, 700);

        // A strike-out runs through the middle — that is what separates it from Underline.
        int mid = CountInk(bmp, 100, 688, 300, 692);
        int bot = CountInk(bmp, 100, 680, 300, 684);
        Assert.True(mid > 0, "No ink through the middle of the quad.");
        Assert.True(mid > bot, $"Strike-out is not centred (mid={mid}, bottom={bot}).");
    }

    /// <summary>
    /// The fallback approximates a squiggly underline as a <b>dashed</b> straight rule
    /// (<c>DashPattern [2 1]</c>) rather than a sine wave. That is a deliberate approximation —
    /// this test pins down the property that actually distinguishes it from
    /// <see cref="UnderlineAnnotation_WithQuadPoints_DrawsAtTheQuadBottom"/>: gaps along the rule.
    /// </summary>
    [Fact]
    public void SquigglyAnnotation_WithQuadPoints_DrawsADashedRuleNotASolidOne()
    {
        const string qp = "/QuadPoints [100 700 300 700 100 680 300 680]";
        byte[] squiggly = BuildPdfWithAnnotation(
            subtype: "Squiggly", rect: "100 680 300 700", extraKeys: "/C [1 0.5 0] " + qp);
        byte[] underline = BuildPdfWithAnnotation(
            subtype: "Underline", rect: "100 680 300 700", extraKeys: "/C [1 0.5 0] " + qp);

        var sq = AssertPaintedWithin(squiggly,  100, 680, 300, 700);
        var un = AssertPaintedWithin(underline, 100, 680, 300, 700);

        // Count SINGLE device columns along the rule that carry no ink. A solid rule has none;
        // a [2 1] dash leaves roughly a third of them clear. Sampling a wider slice would
        // straddle the 1 pt gaps and find none either way.
        int Gaps(SKBitmap b)
        {
            int gaps = 0;
            for (int x = Col(105); x < Col(295); x++)
            {
                bool inked = false;
                for (int y = Row(b, 702); y <= Row(b, 678) && !inked; y++)
                    if (IsInk(b.GetPixel(x, y))) inked = true;
                if (!inked) gaps++;
            }
            return gaps;
        }

        Assert.Equal(0, Gaps(un));
        Assert.True(Gaps(sq) > 20,
            $"Squiggly is not dashed — only {Gaps(sq)} clear columns along the rule.");
    }

    [Fact]
    public void HighlightAnnotation_MultipleQuads_PaintsEachQuadAndTheGapBetween()
    {
        // Two quads — two text runs, with a 10 pt gap at x = 200..210.
        string qp = "/QuadPoints [50 700 200 700 50 680 200 680  210 700 360 700 210 680 360 680]";
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "50 680 360 700",
            extraKeys: "/C [1 1 0] " + qp);

        var bmp = AssertPaintedWithin(pdf, 50, 680, 360, 700);

        Assert.True(CountInk(bmp, 60, 685, 190, 695) > 0, "First quad is unpainted.");
        Assert.True(CountInk(bmp, 220, 685, 350, 695) > 0, "Second quad is unpainted.");
        // The gap proves the quads were honoured individually rather than merged into
        // one bounding box.
        Assert.Equal(0, CountInk(bmp, 202, 685, 208, 695));
    }

    [Fact]
    public void HighlightAnnotation_NoQuadPoints_FallsBackToRect()
    {
        // With /QuadPoints absent the fallback must highlight the whole /Rect instead.
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "100 680 300 700",
            extraKeys: "/C [1 1 0]");

        var bmp = AssertPaintedWithin(pdf, 100, 680, 300, 700);
        Assert.True(CountInk(bmp, 150, 688, 250, 692) > 0,
            "No /QuadPoints and no /Rect fallback — nothing was highlighted.");
    }

    /// <summary>
    /// The export half of the test above: a Highlight with no <c>/QuadPoints</c> falls back to its
    /// <c>/Rect</c> in the SVG exporter too.
    /// </summary>
    /// <remarks>
    /// <para>§12.5.6.10 makes <c>/QuadPoints</c> optional, and both paths fill the <c>/Rect</c> when
    /// a Highlight declares none. Reading the quads with <c>out _</c>, discarding the very flag the
    /// shared reader exposes for this, would close the SVG's
    /// <c>&lt;g data-annot-subtype="Highlight"&gt;</c> <b>empty</b> — the annotation drawing when
    /// rendered and vanishing when exported.</para>
    /// <para>Asserted on the parsed geometry rather than "some element was emitted": the fallback's whole claim is
    /// that it covers the <i>/Rect</i>, so a shape of any other size would satisfy a presence
    /// check while still being the wrong picture.</para>
    /// </remarks>
    [Fact]
    public void HighlightAnnotation_NoQuadPoints_FallsBackToRect_InBothRenderers()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "100 680 300 700",
            extraKeys: "/C [1 1 0]");

        using (var bmp = PixelAssert.Render(pdf))
        {
            Assert.True(CountInk(bmp, 150, 688, 250, 692) > 0,
                "the raster fallback stopped filling the /Rect");
        }

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        var group = System.Xml.Linq.XDocument.Parse(svg).Descendants()
            .Single(e => e.Attribute("data-annot-subtype")?.Value == "Highlight");

        var painted = group.Descendants()
            .Where(e => (e.Attribute("fill")?.Value ?? "none") != "none")
            .ToList();

        var shape = Assert.Single(painted);
        Assert.Equal(100f, Attr(shape, "x"), 2);
        Assert.Equal(680f, Attr(shape, "y"), 2);
        Assert.Equal(200f, Attr(shape, "width"), 2);
        Assert.Equal(20f, Attr(shape, "height"), 2);

        // The same compositing model the quad path uses, so the two ways of describing one
        // highlight do not export as two different pictures.
        Assert.Contains("mix-blend-mode:multiply", shape.Attribute("style")?.Value ?? "",
                        StringComparison.Ordinal);
    }

    private static float Attr(System.Xml.Linq.XElement e, string name) =>
        float.Parse(e.Attribute(name)?.Value ?? "NaN",
                    System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void InkAnnotation_WithoutAP_StrokesTheInkListPolyline()
    {
        // /InkList contains one polyline with four points.
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Ink",
            rect: "50 500 400 700",
            extraKeys: "/C [1 0 0] /InkList [[50 500 150 650 300 600 400 700]]");

        var bmp = AssertPaintedWithin(pdf, 50, 500, 400, 700);

        // The polyline passes near its declared vertices and leaves most of the /Rect clear;
        // a naive "fill the rect" fallback would fail the second assertion.
        Assert.True(CountInk(bmp, 140, 640, 160, 660) > 0, "No ink near the (150,650) vertex.");
        Assert.True(CountInk(bmp) < (350 * 200) / 4, "Ink annotation filled the rect instead of stroking a path.");
    }

    /// <summary>
    /// An annotation without /AP is routed to the fallback renderer, not skipped by an
    /// <c>if (apObj == null) continue;</c> at the top of the loop. A visible annotation must
    /// produce a PNG that differs from an identical but hidden (F=2) one.
    /// </summary>
    [Fact]
    public void AnnotationWithoutAP_WhenVisible_ContributesPixels()
    {
        // Large red-filled Square covering most of the page — hard to miss.
        const string rect = "50 100 560 700";
        byte[] pdfVisible = BuildPdfWithAnnotation("Square", rect, "/C [1 0 0] /IC [1 0 0]");
        byte[] pdfHidden  = BuildPdfWithAnnotation("Square", rect, "/C [1 0 0] /IC [1 0 0] /F 2");

        byte[] pngVisible = PdfPageRenderer.RenderPage(new MemoryStream(pdfVisible), 0, 72);
        byte[] pngHidden  = PdfPageRenderer.RenderPage(new MemoryStream(pdfHidden),  0, 72);

        // Hidden annotation is skipped — PNGs must differ when the annotation is visible.
        Assert.NotEqual(pngVisible, pngHidden);
    }

    // ------------------------------------------------------------------
    //  Rect normalisation (swapped coordinates)
    // ------------------------------------------------------------------

    [Fact]
    public void SquareAnnotation_WithSwappedRect_RendersInTheSamePlace()
    {
        // /Rect has y0 > y1 (non-normalised) — common in some PDF writers. The renderer must
        // normalise it, so the output must be identical to the normalised form.
        byte[] swapped = BuildPdfWithAnnotation(
            subtype: "Square", rect: "100 700 300 600", extraKeys: "/C [1 0 0] /IC [1 0 0]");
        byte[] normal = BuildPdfWithAnnotation(
            subtype: "Square", rect: "100 600 300 700", extraKeys: "/C [1 0 0] /IC [1 0 0]");

        var a = AssertPaintedWithin(swapped, 100, 600, 300, 700);
        var b = AssertPaintedWithin(normal,  100, 600, 300, 700);
        Assert.Equal(CountInk(b), CountInk(a));
    }

    [Fact]
    public void HighlightAnnotation_WithSwappedRect_RendersInTheSamePlace()
    {
        string qp = "/QuadPoints [100 700 300 700 100 680 300 680]";
        byte[] swapped = BuildPdfWithAnnotation(
            subtype: "Highlight", rect: "100 700 300 680", extraKeys: "/C [1 1 0] " + qp);
        byte[] normal = BuildPdfWithAnnotation(
            subtype: "Highlight", rect: "100 680 300 700", extraKeys: "/C [1 1 0] " + qp);

        var a = AssertPaintedWithin(swapped, 100, 680, 300, 700);
        var b = AssertPaintedWithin(normal,  100, 680, 300, 700);
        Assert.Equal(CountInk(b), CountInk(a));
    }

    // ------------------------------------------------------------------
    //  Text annotation — yellow default + standard icon size
    // ------------------------------------------------------------------

    [Fact]
    public void TextAnnotation_WithoutColor_RendersYellowIcon()
    {
        // Text annotation with no /C — should render a yellow sticky-note icon.
        byte[] pdfNoColor = BuildPdfWithAnnotation(
            subtype: "Text",
            rect: "50 700 72 722",
            extraKeys: "");   // no /C

        byte[] pdfWithColor = BuildPdfWithAnnotation(
            subtype: "Text",
            rect: "50 700 72 722",
            extraKeys: "/C [0 0 1]");  // explicit blue

        byte[] pngNoColor   = PdfPageRenderer.RenderPage(new MemoryStream(pdfNoColor),   0, 72);
        byte[] pngWithColor = PdfPageRenderer.RenderPage(new MemoryStream(pdfWithColor), 0, 72);

        // The two renders must differ (different colors → different pixels).
        Assert.NotEqual(pngNoColor, pngWithColor);
    }

    // ------------------------------------------------------------------
    //  Highlight — Multiply blend
    // ------------------------------------------------------------------

    [Fact]
    public void HighlightAnnotation_Multiply_DifferentFromAlphaBlend()
    {
        // A yellow Highlight rendered over a page with black text should NOT look
        // like a 25%-opaque yellow wash — the Multiply path must contribute pixels
        // that differ from a fully-transparent page rendering.
        string qp = "/QuadPoints [50 700 300 700 50 680 300 680]";
        byte[] pdfHighlight = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "50 680 300 700",
            extraKeys: "/C [1 1 0] " + qp);

        byte[] pdfNoAnnot = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "50 680 300 700",
            extraKeys: "/C [1 1 0] " + qp + " /F 2");  // hidden

        byte[] pngHighlight = PdfPageRenderer.RenderPage(new MemoryStream(pdfHighlight), 0, 72);
        byte[] pngNoAnnot   = PdfPageRenderer.RenderPage(new MemoryStream(pdfNoAnnot),   0, 72);

        Assert.NotEqual(pngHighlight, pngNoAnnot);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal PDF with one annotation on page 1, no /AP stream.
    /// </summary>
    private static byte[] BuildPdfWithAnnotation(
        string subtype, string rect, string extraKeys)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Annotation dict (obj 5) — no /AP entry
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /Annot /Subtype /{0} /Rect [{1}] {2} >>\nendobj\n",
            subtype, rect, extraKeys);

        // Page (obj 3) references the annotation
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Annots [5 0 R]" +
            " /Resources << /Font << /F1 6 0 R >> >> >>\nendobj\n");

        // Minimal content stream (obj 4)
        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        // Font (obj 6)
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  AcroForm /DR resource fallback (§12.7.3.3)
    // ------------------------------------------------------------------

    [Fact]
    /// <summary>
    /// When a widget's appearance stream carries no <c>/Resources</c>, the renderer must resolve
    /// its font through the AcroForm <c>/DR</c> — <b>and actually draw the glyph</b>. If the
    /// fallback fails the font is not found and the page comes out blank, which is precisely what
    /// the predecessor's "a PNG came back" assertion could not distinguish.
    /// </summary>
    public void WidgetApStream_WithNoResources_FallsBackToAcroFormDr()
    {
        // Build a PDF where:
        //   • The Widget AP stream uses /ZaDb font but carries no /Resources itself.
        //   • The AcroForm /DR dict defines /ZaDb → ZapfDingbats.
        //   • The page /Resources do NOT define /ZaDb.
        // Falling back to page.Resources, where /ZaDb is absent, would raise a
        // font-not-found diagnostic; the renderer uses /DR.
        byte[] pdf = BuildPdfWithAcroFormDrWidget();

        using var bmp = PixelAssert.Render(pdf);

        // The check-mark glyph really was painted, inside the widget's rectangle.
        int ink = PixelAssert.CountInk(bmp);
        Assert.True(ink > 0, "the /DR fallback should have found /ZaDb and drawn the glyph");

        var bounds = PixelAssert.InkBounds(bmp);
        Assert.NotNull(bounds);
        Assert.InRange(bounds!.Value.right - bounds.Value.left, 1, 40);
        Assert.InRange(bounds.Value.bottom - bounds.Value.top, 1, 40);
    }

    /// <summary>
    /// PDF §9.6.6.4: Symbol and ZapfDingbats <b>always</b> use their built-in encoding, even when
    /// the font dictionary carries an explicit <c>/Encoding</c> — a pattern a common producer and a common viewer both
    /// write. Byte <c>0x33</c> must therefore decode to U+2713 (✓), not to the digit '3'.
    ///
    /// <para>Asserted on the <b>decode</b> rather than on pixels, because the two possibilities can
    /// render identically: ZapfDingbats is not a system font on Windows, so the substituted face
    /// may draw the same shape either way. The control is the identical document with
    /// <c>/BaseFont /Helvetica</c>, which must still honour WinAnsi and yield '3' — without it,
    /// a decoder that returned ✓ for everything would pass.</para>
    /// </summary>
    [Fact]
    public void ZapfDingbats_WithExplicitWinAnsiEncoding_UsesBuiltInEncoding()
    {
        // Regression test for PDF spec §9.6.6.4.
        // Common producers write an explicit /Encoding /WinAnsiEncoding on the ZapfDingbats
        // font dict in widget AP streams.  The spec says Symbol/ZapfDingbats ALWAYS use
        // their built-in encoding regardless of /Encoding.  Before this fix, code 0x33
        // was decoded as '3' (WinAnsiEncoding) instead of U+2713 (✓, ZapfDingbatsEncoding).
        Assert.Equal("\u2713", DecodeApStreamByte(BuildPdfWithZapfDingbatsExplicitEncoding()));

        // Control: same document, a text font — the explicit WinAnsiEncoding governs there.
        Assert.Equal("3", DecodeApStreamByte(BuildPdfWithExplicitWinAnsiFont("Helvetica")));
    }

    /// <summary>
    /// Decodes the single byte 0x33 through the <c>/ZaDb</c> font resource of the appearance
    /// stream (object 6) in a document built by
    /// <see cref="BuildPdfWithExplicitWinAnsiFont"/>.
    /// </summary>
    private static string DecodeApStreamByte(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        var apForm = doc.ResolveObject(new Tetitulz.Pdf.Core.Objects.PdfReference(6, 0))
                     as Tetitulz.Pdf.Core.Objects.PdfDictionary;
        var resources = doc.ResolveObject(apForm?[Tetitulz.Pdf.Core.Objects.PdfName.Resources])
                        as Tetitulz.Pdf.Core.Objects.PdfDictionary;

        using var cache = new FontCache();
        return cache.DecodeText("ZaDb", new byte[] { 0x33 }, resources, doc);
    }

    // ------------------------------------------------------------------
    //  Degenerate QuadPoints guard
    // ------------------------------------------------------------------

    [Fact]
    public void HighlightAnnotation_DegenerateQuadPoints_RendersWithoutException()
    {
        // QuadPoints with sentinel values far outside any real page space should be
        // silently skipped, not cause an overflow or an absurdly-sized draw call.
        string qp = "/QuadPoints [-32768 -32768 32768 -32768 -32768 32768 32768 32768]";
        byte[] pdf = BuildPdfWithAnnotation(
            subtype: "Highlight",
            rect: "0 0 612 792",
            extraKeys: "/C [1 1 0] " + qp);

        using var bmp = PixelAssert.Render(pdf);

        // "Silently skipped" means nothing is painted — not a page-sized yellow wash, and not a
        // draw call sized from the sentinel coordinates. A non-empty PNG says neither way.
        Assert.True(PixelAssert.IsBlank(bmp),
            $"degenerate QuadPoints should paint nothing, but {PixelAssert.CountInk(bmp)} pixels were inked");
        Assert.Equal(0, PixelAssert.CountColor(bmp, new SKColor(255, 255, 0)));
    }

    // ------------------------------------------------------------------
    //  Helpers (continued)
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a PDF with:
    ///   • AcroForm /DR containing /ZaDb (ZapfDingbats).
    ///   • A Widget annotation whose AP stream references /ZaDb but has no /Resources.
    /// </summary>
    private static byte[] BuildPdfWithAcroFormDrWidget()
    {
        // AP stream content — uses /ZaDb which is only in AcroForm /DR, not in AP /Resources.
        const string apCs = "/ZaDb 12 Tf 0 0 Td (3) Tj";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        // obj 1: Catalog → /AcroForm 8 0 R
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>\nendobj\n");

        // obj 2: Pages
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // obj 6: AP stream — Form XObject, deliberately NO /Resources entry
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 14 14]" +
            " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            apCs.Length, apCs);

        // obj 5: Widget annotation referencing AP stream
        int o5 = sb.Length;
        sb.Append(
            "5 0 obj\n<< /Type /Annot /Subtype /Widget /Rect [200 650 214 664]" +
            " /FT /Btn /AP << /N 6 0 R >> >>\nendobj\n");

        // obj 3: Page — /Resources has only Helvetica (no /ZaDb)
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Annots [5 0 R]" +
            " /Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n");

        // obj 4: trivial content stream
        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        // obj 7: Helvetica font (page resource — not /ZaDb)
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // obj 9: ZapfDingbats font (AcroForm /DR resource)
        int o9 = sb.Length;
        sb.Append("9 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>\nendobj\n");

        // obj 8: AcroForm with /DR that maps /ZaDb → obj 9
        int o8 = sb.Length;
        sb.Append(
            "8 0 obj\n<< /Fields [5 0 R]" +
            " /DR << /Font << /ZaDb 9 0 R >> >> >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.AppendFormat("{0:D10} 00000 n \n", o8);
        sb.AppendFormat("{0:D10} 00000 n \n", o9);
        sb.Append("trailer\n<< /Size 10 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a PDF with a Widget AP stream that uses a ZapfDingbats font dict
    /// carrying an explicit /Encoding /WinAnsiEncoding (as a common producer generates it).
    /// This is the §9.6.6.4 regression: built-in encoding must win.
    /// </summary>
    private static byte[] BuildPdfWithZapfDingbatsExplicitEncoding()
        => BuildPdfWithExplicitWinAnsiFont("ZapfDingbats");

    /// <summary>
    /// Same document, parameterised by /BaseFont, so a ZapfDingbats render can be compared against
    /// a text-font render of the identical byte (0x33). Under ZapfDingbats that byte is U+2713 (a
    /// check mark); under a text font it is the digit '3'.
    /// </summary>
    private static byte[] BuildPdfWithExplicitWinAnsiFont(string baseFont)
    {
        // AP stream: byte 0x33 via ZapfDingbats = U+2713 (✓). Must NOT render as '3'.
        const string apCs = "/ZaDb 12 Tf 2 2 Td (3) Tj";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 8 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // obj 7: ZapfDingbats font WITH explicit /Encoding /WinAnsiEncoding — the a common producer pattern.
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /" + baseFont
                  + " /Encoding /WinAnsiEncoding >>\nendobj\n");

        // obj 6: AP stream with /Resources referencing /ZaDb → obj 7
        const string apResStr = "/Font << /ZaDb 7 0 R >>";
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 16 16]"
            + " /Resources << {0} >> /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
            apResStr, apCs.Length, apCs);

        int o5 = sb.Length;
        sb.Append(
            "5 0 obj\n<< /Type /Annot /Subtype /Widget /Rect [200 640 216 656]"
            + " /FT /Btn /AP << /N 6 0 R >> >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
            + " /Contents 4 0 R /Annots [5 0 R]"
            + " /Resources << /Font << /F1 9 0 R >> >> >>\nendobj\n");

        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o9 = sb.Length;
        sb.Append("9 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int o8 = sb.Length;
        sb.Append("8 0 obj\n<< /Fields [5 0 R] /DR << /Font << /ZaDb 7 0 R >> >> >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.AppendFormat("{0:D10} 00000 n \n", o8);
        sb.AppendFormat("{0:D10} 00000 n \n", o9);
        sb.Append("trailer\n<< /Size 10 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Builds a PDF with a Square annotation that HAS an /AP /N appearance stream
    /// (a trivial red-fill rectangle). Used to test that the AP path is not regressed.
    /// </summary>
    private static byte[] BuildPdfWithApAnnotation()
    {
        // Appearance stream content: fill a 200x100 rect in red
        const string apCs = "1 0 0 rg 0 0 200 100 re f";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Appearance XObject (obj 6) — Form XObject
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 100]" +
            " /Resources << >> /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            apCs.Length, apCs);

        // Annotation dict (obj 5) with /AP /N → obj 6
        int o5 = sb.Length;
        sb.Append(
            "5 0 obj\n<< /Type /Annot /Subtype /Square /Rect [100 600 300 700]" +
            " /C [1 0 0] /AP << /N 6 0 R >> >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Annots [5 0 R]" +
            " /Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n");

        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ==================================================================
    //  The SVG arm — the seven subtypes that had a raster fallback and none here
    // ==================================================================
    //
    //  Text, PrinterMark, Caret, FreeText, Stamp, Widget and 3D each had a synthetic no-/AP
    //  fallback in the raster renderer and NONE in the SVG exporter, so all seven fell into its
    //  `default` arm: one dashed grey rectangle over the whole /Rect, plus an "annotation subtype
    //  has no per-subtype fallback" report that was untrue of every one of them.
    //
    //  Everything below asserts on the parsed SVG (XDocument), never on a substring, and on the
    //  GEOMETRY rather than on "an element was emitted" — the placeholder these replace was itself
    //  a rectangle over the /Rect, so a presence check passes with the whole fix reverted.
    //
    //  A previous verifier recorded this file as raster-only, which is exactly why the two halves
    //  drifted: the raster fallbacks have been pinned here since they were written, and the export
    //  half was pinned by nothing.

    /// <summary>The <c>&lt;g data-annot-subtype="…"&gt;</c> group one annotation exported into.</summary>
    private static System.Xml.Linq.XElement AnnotGroup(byte[] pdf, string subtype)
    {
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        return System.Xml.Linq.XDocument.Parse(svg).Descendants()
            .Single(e => e.Attribute("data-annot-subtype")?.Value == subtype);
    }

    /// <summary>The six numbers of an SVG <c>transform="matrix(a,b,c,d,e,f)"</c>.</summary>
    private static float[] Matrix(System.Xml.Linq.XElement e)
    {
        string t = e.Attribute("transform")?.Value ?? "";
        int open = t.IndexOf('(');
        Assert.True(open > 0 && t.EndsWith(")", StringComparison.Ordinal),
            $"not a transform list: '{t}'");
        return t.Substring(open + 1, t.Length - open - 2)
                .Split(',')
                .Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
    }

    /// <summary>
    /// Runs <paramref name="render"/> and returns the <see cref="RenderDiagnostics"/> events it
    /// raised <b>on the calling thread</b>.
    /// </summary>
    /// <remarks>
    /// <c>OnEvent</c> is process-wide, so an unfiltered handler turns any assertion about it into a
    /// claim about every concurrently-running test. This repository has lost three intermittents to
    /// exactly that.
    /// </remarks>
    private static System.Collections.Generic.List<RenderDiagnosticKind> DiagnosticsFrom(
        Action render)
    {
        var seen = new System.Collections.Generic.List<RenderDiagnosticKind>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == ownThread) seen.Add(kind);
        }

        RenderDiagnostics.OnEvent += Sink;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return seen;
    }

    /// <summary>
    /// None of the seven is an unsupported subtype in either renderer, so neither may say it is.
    /// </summary>
    /// <remarks>
    /// The report is the half of this divergence a host could actually see: the same file said
    /// nothing on render and "subtype unsupported" on export. Both renderers are run over one
    /// fixture, because the raster half passes today with the SVG half broken.
    /// </remarks>
    [Theory]
    [InlineData("Text",        "")]
    [InlineData("PrinterMark", "")]
    [InlineData("Caret",       "")]
    [InlineData("FreeText",    "/Contents (Note) /DA (/Helv 12 Tf 0 0 0 rg)")]
    [InlineData("Stamp",       "/Name /Approved")]
    [InlineData("Widget",      "/FT /Tx /V (Hi) /DA (/Helv 12 Tf 0 0 0 rg)")]
    [InlineData("3D",          "")]
    public void SubtypeWithSyntheticFallback_IsNotReportedAsUnsupported_InEitherRenderer(
        string subtype, string extraKeys)
    {
        byte[] pdf = BuildPdfWithAnnotation(subtype, "100 600 300 700", extraKeys);

        var raster = DiagnosticsFrom(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfPageRenderer.RenderPage(ms, 0, 72);
        });
        Assert.DoesNotContain(RenderDiagnosticKind.AnnotationSubtypeUnsupported, raster);

        var svg = DiagnosticsFrom(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        });
        Assert.DoesNotContain(RenderDiagnosticKind.AnnotationSubtypeUnsupported, svg);
    }

    /// <summary>
    /// The exported sticky note is the <b>icon</b> the raster path draws — a fixed 18×20pt at the
    /// rect corner — not a box the size of <c>/Rect</c>.
    /// </summary>
    /// <remarks>
    /// This is the discriminating assertion for the whole arm: the placeholder it replaces was also
    /// a rectangle at <c>x=100 y=600</c> and differed only in being 200×100. The raster render is
    /// the reference, so the exporter and the expectation cannot agree on a wrong number.
    /// </remarks>
    [Fact]
    public void TextAnnotation_WithoutAP_ExportsTheIconTheRasterPathDraws_NotTheWholeRect()
    {
        byte[] pdf = BuildPdfWithAnnotation("Text", "100 600 300 700", "");

        (int left, int top, int right, int bottom) ink;
        using (var bmp = PixelAssert.Render(pdf))
        {
            var bounds = PixelAssert.InkBounds(bmp);
            Assert.NotNull(bounds);
            ink = bounds!.Value;
        }

        var rect = Assert.Single(AnnotGroup(pdf, "Text").Descendants()
            .Where(e => e.Name.LocalName == "rect"));

        // 72 dpi, so one device pixel is one PDF point; ±3 absorbs the border's outward
        // half-width and the antialiasing on each side.
        float inkW = ink.right - ink.left + 1, inkH = ink.bottom - ink.top + 1;
        Assert.InRange(Attr(rect, "width"),  inkW - 3f, inkW + 3f);
        Assert.InRange(Attr(rect, "height"), inkH - 3f, inkH + 3f);
        Assert.InRange(Attr(rect, "x"), ink.left - 1f, ink.left + 2f);

        // …and it is the conventional sticky-note yellow, because this annotation declares no /C.
        Assert.Equal("rgb(255,204,0)", rect.Attribute("fill")?.Value);
        Assert.Equal("rgb(255,204,0)", rect.Attribute("stroke")?.Value);
    }

    /// <summary>A sticky note that declares a <c>/C</c> uses it rather than the default yellow.</summary>
    [Fact]
    public void TextAnnotation_WithColor_ExportsThatColorNotTheDefaultYellow()
    {
        byte[] pdf = BuildPdfWithAnnotation("Text", "100 600 300 700", "/C [1 0 0]");

        var rect = Assert.Single(AnnotGroup(pdf, "Text").Descendants()
            .Where(e => e.Name.LocalName == "rect"));
        Assert.Equal("rgb(255,0,0)", rect.Attribute("fill")?.Value);
    }

    /// <summary>
    /// A PrinterMark exports <b>nothing at all</b>, which is what the raster path draws for one.
    /// </summary>
    /// <remarks>
    /// PDF/X printer marks are prepress artifacts — crop marks, registration targets, colour bars —
    /// with no screen-rendering semantics of their own. The exporter drew a dashed box <i>over</i>
    /// them; the raster path deliberately draws nothing and deliberately does not report. The
    /// unknown-subtype control is what stops "the group is empty" from passing because the
    /// synthetic path stopped running altogether.
    /// </remarks>
    [Fact]
    public void PrinterMarkAnnotation_WithoutAP_DrawsNothingInBothRenderers()
    {
        byte[] pdf = BuildPdfWithAnnotation("PrinterMark", "100 600 300 700", "/C [1 0 0]");

        using (var bmp = PixelAssert.Render(pdf))
            Assert.True(PixelAssert.IsBlank(bmp),
                "the raster path painted a PrinterMark; it has always drawn nothing for one.");

        Assert.Empty(AnnotGroup(pdf, "PrinterMark").Elements());

        byte[] unknown = BuildPdfWithAnnotation("NoSuchSubtype", "100 600 300 700", "/C [1 0 0]");
        Assert.Contains(AnnotGroup(unknown, "NoSuchSubtype").Elements(),
            e => e.Attribute("stroke-dasharray")?.Value == "3 2");
    }

    /// <summary>
    /// §12.5.6.11: a Caret exports the inverted-V the raster path draws, apexed at 60% of the rect
    /// height — not a rectangle.
    /// </summary>
    [Fact]
    public void CaretAnnotation_WithoutAP_ExportsTheApexedTriangle()
    {
        byte[] pdf = BuildPdfWithAnnotation("Caret", "100 600 200 700", "/C [0 0 1]");

        var path = Assert.Single(AnnotGroup(pdf, "Caret").Descendants()
            .Where(e => e.Name.LocalName == "path"));

        // Apex at the horizontal centre, 60% of the way up — the same three points the raster
        // path moves through.
        Assert.Equal("M100,600 L150,660 L200,600 Z", path.Attribute("d")?.Value);
        Assert.Equal("rgb(0,0,255)", path.Attribute("stroke")?.Value);
    }

    /// <summary>
    /// A FreeText exports its <c>/Contents</c> at the size and colour its <c>/DA</c> asks for, at
    /// the baseline the raster path uses.
    /// </summary>
    /// <remarks>
    /// The <c>/DA</c> parse is the decision both renderers share, and asserting the colour is what
    /// separates "the string was read" from "a default was used" — the shape of the defect this
    /// same parser once carried, where every field drew black at the fallback size.
    /// </remarks>
    [Fact]
    public void FreeTextAnnotation_WithoutAP_ExportsItsContentsAtTheDaSizeAndColour()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            "FreeText", "100 600 300 700", "/Contents (Hello) /DA (/Helv 20 Tf 1 0 0 rg)");

        var group = AnnotGroup(pdf, "FreeText");
        var text = Assert.Single(group.Descendants().Where(e => e.Name.LocalName == "text"));

        Assert.Equal("Hello", text.Value);
        Assert.Equal("rgb(255,0,0)", text.Attribute("fill")?.Value);

        float[] m = Matrix(text);
        Assert.Equal(20f, m[0], 2);      // the /DA size, baked into the matrix
        Assert.Equal(-20f, m[3], 2);     // negated to undo the page-level Y-flip
        Assert.Equal(102f, m[4], 2);     // 2pt in from the left edge
        Assert.Equal(678f, m[5], 2);     // one line-height (1.1em) below the rect top

        // …inside its border, which the raster path draws too.
        Assert.Contains(group.Elements(),
            e => e.Name.LocalName == "rect" && e.Attribute("stroke")?.Value == "black");
    }

    /// <summary>
    /// With <c>/Contents</c> absent a FreeText takes its text from <c>/RC</c>, stripped of its
    /// XHTML-lite markup — in the exporter as well as the renderer.
    /// </summary>
    [Fact]
    public void FreeTextAnnotation_WithRcOnly_ExportsTheStrippedRichText()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            "FreeText", "100 600 300 700",
            "/RC (<body><p>Rich <b>words</b></p></body>) /DA (/Helv 12 Tf 0 0 0 rg)");

        var text = Assert.Single(AnnotGroup(pdf, "FreeText").Descendants()
            .Where(e => e.Name.LocalName == "text"));
        Assert.Equal("Rich words", text.Value);
    }

    /// <summary>
    /// A Stamp exports the outline and caption the raster path draws, in the colour its
    /// <c>/Name</c> implies when it declares no <c>/C</c>.
    /// </summary>
    [Fact]
    public void StampAnnotation_WithoutAP_ExportsItsNameInTheNamedPaletteColour()
    {
        byte[] pdf = BuildPdfWithAnnotation("Stamp", "100 600 300 700", "/Name /Approved");

        var group = AnnotGroup(pdf, "Stamp");
        var rect = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "rect"));
        var text = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "text"));

        // "Approved" is one of the approval names, so the palette is green — not the black an
        // absent /C would otherwise give.
        Assert.Equal("rgb(32,128,64)", rect.Attribute("stroke")?.Value);
        Assert.Equal("rgb(32,128,64)", text.Attribute("fill")?.Value);
        Assert.Equal("Approved", text.Value);

        // A stamp's outline is never thinner than 2pt, whatever /BS says.
        Assert.Equal(2f, Attr(rect, "stroke-width"), 2);
    }

    /// <summary>An explicit <c>/C</c> beats the name palette, in the exporter as in the renderer.</summary>
    /// <remarks>
    /// The stroke colour alone is <b>not</b> enough here, and mutation says so: the dashed
    /// placeholder this replaced also strokes in <c>/C</c>, so an assertion naming only the colour
    /// passed with the whole Stamp arm deleted. The caption and the undashed 2pt outline are what
    /// separate a stamp from a placeholder that happens to be the right colour.
    /// </remarks>
    [Fact]
    public void StampAnnotation_WithExplicitColor_UsesItInsteadOfThePalette()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            "Stamp", "100 600 300 700", "/Name /Approved /C [1 0 1]");

        var group = AnnotGroup(pdf, "Stamp");
        var rect = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "rect"));
        Assert.Equal("rgb(255,0,255)", rect.Attribute("stroke")?.Value);
        Assert.Null(rect.Attribute("stroke-dasharray"));
        Assert.Equal(2f, Attr(rect, "stroke-width"), 2);

        var text = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "text"));
        Assert.Equal("rgb(255,0,255)", text.Attribute("fill")?.Value);
    }

    /// <summary>
    /// A text widget with no <c>/AP</c> exports its <c>/V</c>, at the size and colour its
    /// <c>/DA</c> asks for — the same value the raster fallback types into the field.
    /// </summary>
    [Fact]
    public void TextWidget_WithoutAP_ExportsItsValueAtTheDaSizeAndColour()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            "Widget", "100 600 300 700",
            "/FT /Tx /V (Hello) /DA (/Helv 12 Tf 0 0 1 rg) /MK << /BC [0 0 0] >>");

        var group = AnnotGroup(pdf, "Widget");
        var text = Assert.Single(group.Descendants().Where(e => e.Name.LocalName == "text"));

        Assert.Equal("Hello", text.Value);
        Assert.Equal("rgb(0,0,255)", text.Attribute("fill")?.Value);

        float[] m = Matrix(text);
        Assert.Equal(12f, m[0], 2);      // the /DA size
        Assert.Equal(102f, m[4], 2);     // 2pt inset
        Assert.Equal(645.8f, m[5], 2);   // vertically centred in the 100pt-tall rect

        // The field's border, from its /MK /BC — and it is not the dashed placeholder this replaced.
        var rect = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "rect"));
        Assert.Null(rect.Attribute("stroke-dasharray"));
    }

    /// <summary>
    /// A checked check box exports a tick, not the word its <c>/V</c> spells.
    /// </summary>
    /// <remarks>
    /// The <c>/FT</c>-vs-<c>/V</c> distinction is the whole point: <c>/V /Yes</c> on a
    /// <c>/FT /Btn</c> means "ticked", and typesetting "Yes" where a tick belongs is the failure the
    /// raster path's own comment records. Asserted against the unticked control, so a widget that
    /// exported no text at all could not pass.
    /// </remarks>
    [Fact]
    public void CheckBoxWidget_WithoutAP_ExportsATickWhenOn_AndNothingWhenOff()
    {
        byte[] on = BuildPdfWithAnnotation(
            "Widget", "100 600 140 640", "/FT /Btn /V /Yes /DA (/ZaDb 0 Tf 0 0 0 rg)");
        var text = Assert.Single(AnnotGroup(on, "Widget").Descendants()
            .Where(e => e.Name.LocalName == "text"));
        Assert.Equal("\u2713", text.Value);

        byte[] off = BuildPdfWithAnnotation(
            "Widget", "100 600 140 640", "/FT /Btn /V /Off /DA (/ZaDb 0 Tf 0 0 0 rg)");
        Assert.Empty(AnnotGroup(off, "Widget").Descendants()
            .Where(e => e.Name.LocalName == "text"));
    }

    /// <summary>
    /// A widget whose <c>/FT</c> is out of reach draws its border and nothing else, and says so —
    /// in the exporter as well as the renderer.
    /// </summary>
    /// <remarks>
    /// Turning "I could not determine the type" into "the type is text" typesets a value where a
    /// tick may belong, with nothing on the diagnostics channel to say so.
    /// </remarks>
    [Fact]
    public void Widget_WithUnreachableFieldType_ExportsNoValue_AndReportsIt()
    {
        byte[] pdf = BuildPdfWithAnnotation(
            "Widget", "100 600 300 700", "/V (Hello) /DA (/Helv 12 Tf 0 0 0 rg)");

        var kinds = DiagnosticsFrom(() =>
        {
            using var ms = new MemoryStream(pdf);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        });
        Assert.Contains(RenderDiagnosticKind.FormFieldTypeUnresolved, kinds);

        Assert.Empty(AnnotGroup(pdf, "Widget").Descendants()
            .Where(e => e.Name.LocalName == "text"));
    }

    /// <summary>
    /// A 3D annotation with no <c>/AP</c> exports the dashed box, diagonal cross and "3D" caption
    /// the raster path draws, so dropped 3-D content stays visible rather than silent.
    /// </summary>
    [Fact]
    public void ThreeDAnnotation_WithoutAP_ExportsTheCrossedPlaceholderAndCaption()
    {
        byte[] pdf = BuildPdfWithAnnotation("3D", "100 600 300 700", "");

        var group = AnnotGroup(pdf, "3D");

        // The soft blue both renderers fall back to when /C is absent.
        var rect = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "rect"));
        Assert.Equal("rgb(48,96,176)", rect.Attribute("stroke")?.Value);
        Assert.Equal("4 2", rect.Attribute("stroke-dasharray")?.Value);

        // The diagonal cross — two segments corner to corner, which is what makes the box read as
        // a placeholder rather than as a rectangle the file asked for.
        var cross = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "path"));
        Assert.Equal("M100,600 L300,700 M100,700 L300,600", cross.Attribute("d")?.Value);

        var caption = Assert.Single(group.Elements().Where(e => e.Name.LocalName == "text"));
        Assert.Equal("3D", caption.Value);
    }

    /// <summary>
    /// A 3D annotation <b>with</b> an <c>/AP</c> exports its poster normally: the placeholder is
    /// for the case where there is nothing to draw, and must not paint over a real appearance.
    /// </summary>
    [Fact]
    public void ThreeDAnnotation_WithApStream_ExportsTheAppearance_NotThePlaceholder()
    {
        byte[] pdf = ThreeDWithAppearancePdf();

        var group = AnnotGroup(pdf, "3D");
        Assert.Contains(group.Descendants(),
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,255,0)");
        Assert.DoesNotContain(group.Descendants(),
            e => e.Attribute("stroke-dasharray")?.Value == "4 2");
    }

    /// <summary>One page, one 3D annotation whose <c>/AP /N</c> poster fills its BBox green.</summary>
    private static byte[] ThreeDWithAppearancePdf()
    {
        const string ap = "0 1 0 rg 0 0 10 10 re f";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Annots [5 0 R] /Resources << >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Annot /Subtype /3D /Rect [100 600 300 700]" +
            " /AP << /N 6 0 R >> >>\nendobj\n");
        Obj($"6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length {ap.Length} >>" +
            $"\nstream\n{ap}\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
