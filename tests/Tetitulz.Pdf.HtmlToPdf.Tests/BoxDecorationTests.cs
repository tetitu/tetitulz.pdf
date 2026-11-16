// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>border-radius</c>, <c>box-shadow</c> and <c>transform</c> on a block — and the corrected
/// reason for <c>overflow</c>.
///
/// <para><b>The decline said "each needs a per-box graphics-state stack".</b> The content builder
/// has had one all along: <c>SaveState</c>, <c>RestoreState</c>, <c>Transform</c>, <c>CurveTo</c>
/// and <c>ClipNonzero</c> are all on <c>PdfContentStreamBuilder</c>, and the exporter already
/// brackets its box fills with save/restore. What was missing was a model field and the drawing to
/// read it.</para>
///
/// <para>Assertions are on the emitted operators, because that is what these properties are: a
/// rounded box is Bézier corners instead of <c>re</c>, a shadow is a second fill, a transform is a
/// <c>cm</c>. None of it changes the text, so nothing textual could tell them apart.</para>
/// </summary>
public class BoxDecorationTests
{
    /// <summary>
    /// A radius replaces the rectangle operator with a curved path. <c>re</c> is a single operator
    /// and <c>c</c> is the curve, so their presence is the whole distinction.
    /// </summary>
    [Fact]
    public void BorderRadius_DrawsCurvedCornersInsteadOfARectangle()
    {
        string square = Content("<p style='background:#ccc'>Box</p>");
        string round = Content("<p style='background:#ccc;border-radius:8pt'>Box</p>");

        Assert.DoesNotContain(" c\n", square);
        Assert.Contains(" c\n", round);
    }

    /// <summary>
    /// An over-large radius is clamped to half the box. Unclamped, opposite corners overlap and
    /// the path self-intersects, which fills as a bow tie — a failure that looks like a rendering
    /// bug rather than a bad stylesheet.
    /// </summary>
    [Fact]
    public void AnOversizedRadius_IsClampedRatherThanSelfIntersecting()
    {
        // 500pt of radius on a box a few points high. Unclamped, the corner arcs overlap and the
        // path self-intersects, which fills as a bow tie.
        string content = Content("<p style='background:#ccc;border-radius:500pt'>Box</p>");

        Assert.Contains(" c\n", content);

        // The top edge must run LEFT TO RIGHT. That is precisely what an unclamped radius breaks:
        // the path starts at (x + r) and lines to (x1 - r), so once r exceeds half the width those
        // two cross and the edge doubles back — the self-intersection that fills as a bow tie.
        //
        // Two earlier versions of this assertion did not catch it. Counting curves passes, because
        // an unclamped radius still emits curves. Bounding the coordinates to the page passes too,
        // because 570 and 38 are both on a 612pt page. Only their ORDER carries the defect.
        var xs = PathXCoordinates(content);
        Assert.True(xs.Count >= 2, "the box path was not emitted");
        Assert.True(xs[1] > xs[0],
            $"the box path's top edge runs backwards ({xs[0]} to {xs[1]}), so the corner radius " +
            "was not clamped to half the box");
    }

    /// <summary>
    /// A shadow is a second filled box behind the first. Counted, because "a fill exists" is true
    /// of the background alone.
    /// </summary>
    [Fact]
    public void BoxShadow_AddsAFillBehindTheBackground()
    {
        int plain = PdfContentProbe.RectangleFillCount(
            Content("<p style='background:#ccc'>Box</p>"));
        int shadowed = PdfContentProbe.RectangleFillCount(
            Content("<p style='background:#ccc;box-shadow:4pt 4pt #999'>Box</p>"));

        Assert.Equal(plain + 1, shadowed);
    }

    /// <summary>
    /// The shadow sits at its stated offset: right and <b>down</b>, as CSS states a positive Y.
    ///
    /// <para>Added after a mutation survived. The count above is satisfied by a shadow drawn
    /// anywhere at all — including on top of the box, or offset the wrong way up the page — so it
    /// pinned that a second fill happened and nothing about where.</para>
    /// </summary>
    [Fact]
    public void BoxShadow_IsOffsetRightAndDown()
    {
        var rects = FilledRectangles(
            Content("<p style='background:#ccc;box-shadow:4pt 4pt #999'>Box</p>"));

        Assert.True(rects.Count >= 2, "expected a shadow fill and a background fill");
        var shadow = rects[0];      // drawn first, so it is behind
        var box = rects[1];

        Assert.InRange(shadow.x - box.x, 3, 5);
        // Down the page is a SMALLER PDF y, so the shadow's y is below the box's.
        Assert.InRange(box.y - shadow.y, 3, 5);
    }

    /// <summary>
    /// A transform emits a <c>cm</c>, and <b>does not move the flow</b> — CSS transforms painting,
    /// not layout, so the paragraph after it stays where it was.
    /// </summary>
    [Fact]
    public void Transform_EmitsAMatrixWithoutMovingTheFlow()
    {
        string transformed = Content(
            "<p style='transform:rotate(10deg)'>Turned</p><p>After</p>");
        string plain = Content("<p>Turned</p><p>After</p>");

        Assert.Contains(" cm\n", transformed);
        Assert.DoesNotContain(" cm\n", plain);

        Assert.Equal(PdfContentProbe.YOf(plain, "After"), PdfContentProbe.YOf(transformed, "After"), 3);
    }

    /// <summary>
    /// <c>translateY</c> moves the block DOWN the page, as CSS states it — the axis flip PDF needs.
    /// Asserted on the sign of the matrix's f term, which is the one number that carries it.
    /// </summary>
    [Fact]
    public void TranslateY_MovesDownThePage()
    {
        double[] m = MatrixOf(Content("<p style='transform:translateY(20pt)'>X</p>"));

        Assert.InRange(m[5], -21, -19);
    }

    /// <summary>
    /// Two functions compose, and in CSS's order. A single function cannot tell a correct
    /// composition from a reversed one; a translate-then-scale pair can. The matrix is written
    /// about the block's transform origin (batch 104), which moves its translation by the origin's
    /// share; the same origin under the scale alone isolates it.
    /// </summary>
    [Fact]
    public void TwoFunctions_ComposeInCssOrder()
    {
        // translate(10pt) scale(2): CSS applies scale first to the coordinate, then translate, so
        // the translation is NOT scaled — the pair's e is the scale's e plus 10, not 20.
        double[] both = MatrixOf(Content("<p style='transform:translateX(10pt) scale(2)'>X</p>"));
        double[] scale = MatrixOf(Content("<p style='transform:scale(2)'>X</p>"));

        Assert.InRange(both[0], 1.9, 2.1);
        Assert.InRange(both[4] - scale[4], 9, 11);
    }

    /// <summary>
    /// A transform this engine cannot express is reported and not applied — better than composing
    /// a partial matrix, which would place the block somewhere the author never asked for.
    /// </summary>
    [Fact]
    public void AnUnsupportedTransform_IsReportedAndNotApplied()
    {
        var reports = new List<string>();
        string content = Content("<p style='transform:perspective(400px)'>X</p>", reports);

        Assert.DoesNotContain(" cm\n", content);
        Assert.Contains(reports, r => r.Contains("composes no others", StringComparison.Ordinal));
    }

    /// <summary>
    /// A transform reports nothing (batch 104): the link rectangles inside a transformed block follow
    /// it (<c>TransformOriginTests</c>), so the caveat that every transform used to raise — a
    /// <c>/Rect</c> in default user space left where the untransformed text was — is gone. The
    /// matrix is still written.
    /// </summary>
    [Fact]
    public void ATransform_ReportsNoLinkCaveat()
    {
        var reports = new List<string>();
        string content = Content("<p style='transform:rotate(5deg)'>X</p>", reports);

        Assert.Contains(" cm\n", content);
        Assert.DoesNotContain(reports, r => r.Contains("default user space", StringComparison.Ordinal) || r.Contains("transform", StringComparison.Ordinal));
    }

    /// <summary>
    /// A blur radius is drawn (batch 88): the shadow is a soft-masked image, painted before the
    /// background fill and one radius past the hard shadow's box, and nothing is reported. Before
    /// batch 88 it was reported and the shadow drawn with a hard edge.
    /// </summary>
    [Fact]
    public void BoxShadowBlur_IsDrawnAsASoftMaskedImageAndNotReported()
    {
        var reports = new List<string>();
        string blurred = Content("<p style='background:#ccc;box-shadow:4pt 4pt 6pt #999'>Box</p>", reports);
        string hard = Content("<p style='background:#ccc;box-shadow:4pt 4pt #999'>Box</p>");

        var shadow = FilledRectangles(hard)[0];
        var draw = Assert.Single(ParagraphBoxImageDraws(blurred));
        Assert.Equal(shadow.x - 6, draw.x, 1);
        Assert.Equal(shadow.w + 12, draw.w, 1);
        Assert.True(blurred.IndexOf(" Do", StringComparison.Ordinal) < blurred.IndexOf(" re", StringComparison.Ordinal), "the image is not behind the fill");
        Assert.DoesNotContain(reports, r => r.Contains("blur", StringComparison.Ordinal));
    }

    /// <summary>A spread (the fourth length) grows the shadow on every side: 3pt makes it 6pt wider than the box.</summary>
    [Fact]
    public void BoxShadowSpread_GrowsTheShadow()
    {
        var rects = FilledRectangles(Content("<p style='background:#ccc;box-shadow:4pt 4pt 0 3pt #999'>Box</p>"));

        Assert.True(rects.Count >= 2, "expected a shadow fill and a background fill");
        Assert.Equal(rects[1].w + 6, rects[0].w, 1);
        Assert.Equal(rects[1].h + 6, rects[0].h, 1);
        Assert.Equal(rects[1].x + 4 - 3, rects[0].x, 1);
    }

    /// <summary>The <c>w 0 0 h x y cm /Name Do</c> image placements in <paramref name="content"/>.</summary>
    private static List<(double w, double h, double x, double y)> ParagraphBoxImageDraws(string content)
        => System.Text.RegularExpressions.Regex.Matches(content, @"([-\d.]+)\s+0\s+0\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+cm\s*/\w+\s+Do\b")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => (D(m, 1), D(m, 2), D(m, 3), D(m, 4))).ToList();

    private static double D(System.Text.RegularExpressions.Match m, int group)
        => double.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>overflow</c> under a stated height is reported, with the reason: a stated height is a
    /// minimum here (html-block-height), so the box grows to hold taller content rather than
    /// clipping it at that height. (Clipping to the box itself: OverflowClipTests.)
    /// </summary>
    [Fact]
    public void Overflow_UnderAStatedHeight_IsReportedAsGrowingRatherThanClipping()
    {
        var reports = new List<string>();
        Content("<p style='overflow:hidden;height:10px'>X</p>", reports);

        Assert.Contains(reports, r => r.Contains("grows to hold", StringComparison.Ordinal));
    }

    /// <summary>
    /// With no height declared nothing can overflow a paragraph, so its <c>overflow</c> loses
    /// nothing and is not reported — normalize.css's <c>textarea { overflow: auto }</c> was, on
    /// every form.
    /// </summary>
    [Theory]
    [InlineData("overflow:hidden")]
    [InlineData("overflow:auto;height:auto")]            // Bootstrap's textarea.form-control
    [InlineData("overflow:auto;max-height:none")]
    public void Overflow_OnAParagraphWithoutAHeight_IsNotReported(string style)
    {
        var reports = new List<string>();
        Content($"<p style='{style}'>X</p>", reports);

        Assert.DoesNotContain(reports, r => r.Contains("overflow", StringComparison.Ordinal));
    }

    /// <summary>A <c>max-height</c> holds a paragraph as a <c>height</c> does: its overflow is reported.</summary>
    [Fact]
    public void Overflow_OnAParagraphWithAMaxHeight_IsReported()
    {
        var reports = new List<string>();
        Content("<p style='overflow:hidden;max-height:10px'>X</p>", reports);

        Assert.Contains(reports, r => r.Contains("grows to hold", StringComparison.Ordinal));
    }

    /// <summary>
    /// A document declaring none of these hears nothing about them.
    /// </summary>
    [Fact]
    public void ADocumentWithoutBoxDecoration_ReportsNothingAboutIt()
    {
        var reports = new List<string>();
        Content("<p style='background:#ccc'>Box</p>", reports);

        Assert.DoesNotContain(reports, r =>
            r.Contains("transform", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("shadow", StringComparison.OrdinalIgnoreCase) ||
            r.Contains("overflow", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------

    /// <summary>Every x coordinate appearing in a path-construction operator.</summary>
    private static List<double> PathXCoordinates(string content)
    {
        var xs = new List<double>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(content,
                     @"(-?[\d.]+) (-?[\d.]+) (m|l)\n"))
            xs.Add(double.Parse(m.Groups[1].Value,
                                System.Globalization.CultureInfo.InvariantCulture));
        return xs;
    }

    /// <summary>Filled rectangles as (x, y, w, h), in the order they are painted.</summary>
    private static List<(double x, double y, double w, double h)> FilledRectangles(string content)
    {
        var rects = new List<(double, double, double, double)>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(content,
                     @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\nf"))
            rects.Add((
                double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture)));
        return rects;
    }

    /// <summary>The six numbers of the first <c>cm</c> in the stream.</summary>
    private static double[] MatrixOf(string content)
    {
        var m = System.Text.RegularExpressions.Regex.Match(content,
            @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm");
        Assert.True(m.Success, "no cm operator was emitted");
        return Enumerable.Range(1, 6)
            .Select(g => double.Parse(m.Groups[g].Value,
                                      System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static string Content(string html, List<string> reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null
                ? null
                : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });

        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);
        return PdfContentProbe.DecodeStreams(pdf);
    }
}
