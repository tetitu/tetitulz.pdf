// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>transform-origin</c> (CSS Transforms 1 §6, batch 104) and the links inside a transformed
/// block. A transform is written about the block's origin — the centre of its border box unless
/// stated — where before batch 104 it was written about the page's origin, so <c>scale(2)</c> pushed
/// a paragraph off the page and <c>rotate(5deg)</c> swung it about the page's corner. The origin
/// reaches the matrix as its translation: under <c>scale(2)</c> about (ox, oy), <c>e = −ox</c> and
/// <c>f = −oy</c>, so moving the origin by a length moves <c>e</c> by that length the other way. And
/// a link inside the block follows the transform, where it kept its untransformed rectangle, reported.
/// </summary>
public class TransformOriginTests
{
    /// <summary>With no <c>transform-origin</c> the matrix is the one <c>50% 50%</c> gives — the centre.</summary>
    [Fact]
    public void TheDefaultOrigin_IsTheCentre()
    {
        double[] plain = MatrixOf(Content("<p style='transform:scale(2)'>Turned</p>"));
        double[] centre = MatrixOf(Content("<p style='transform:scale(2);transform-origin:50% 50%'>Turned</p>"));
        double[] corner = MatrixOf(Content("<p style='transform:scale(2);transform-origin:0 0'>Turned</p>"));

        Assert.Equal(centre[4], plain[4], 2);
        Assert.Equal(centre[5], plain[5], 2);
        Assert.True(Math.Abs(corner[4] - plain[4]) > 50, "the centre and the corner give the same matrix: the origin is not applied");
        // f = −oy under scale(2): the corner's origin is higher on the page, so its f is the smaller.
        Assert.True(plain[5] > corner[5] + 2, "the top-left corner is not above the centre");
    }

    /// <summary>A length moves the origin by itself: <c>20px</c> (15pt) right moves <c>e</c> 15pt down under <c>scale(2)</c>; the vertical offset moves <c>f</c>.</summary>
    [Fact]
    public void ALength_MovesTheOriginByItself()
    {
        double[] corner = MatrixOf(Content("<p style='transform:scale(2);transform-origin:0 0'>Turned</p>"));
        double[] right = MatrixOf(Content("<p style='transform:scale(2);transform-origin:20px 0'>Turned</p>"));
        double[] down = MatrixOf(Content("<p style='transform:scale(2);transform-origin:0 20px'>Turned</p>"));

        Assert.Equal(-15, right[4] - corner[4], 1);
        Assert.Equal(corner[5], right[5], 2);
        // The origin 15pt down the page is 15pt lower in PDF y; f = −oy, so f rises by 15.
        Assert.Equal(15, down[5] - corner[5], 1);
        Assert.Equal(corner[4], down[4], 2);
    }

    /// <summary>Keywords name the edges in either order, one keyword leaves the other axis at the centre, and a percentage is of the box.</summary>
    [Fact]
    public void Keywords_NameTheEdgesInEitherOrder()
    {
        double[] corner = MatrixOf(Content("<p style='transform:scale(2);transform-origin:0 0'>Turned</p>"));
        double[] leftTop = MatrixOf(Content("<p style='transform:scale(2);transform-origin:left top'>Turned</p>"));
        double[] topLeft = MatrixOf(Content("<p style='transform:scale(2);transform-origin:top left'>Turned</p>"));
        double[] right = MatrixOf(Content("<p style='transform:scale(2);transform-origin:right'>Turned</p>"));
        double[] rightCentre = MatrixOf(Content("<p style='transform:scale(2);transform-origin:100% 50%'>Turned</p>"));
        double[] centre = MatrixOf(Content("<p style='transform:scale(2)'>Turned</p>"));

        Assert.Equal(corner[4], leftTop[4], 2); Assert.Equal(corner[5], leftTop[5], 2);
        Assert.Equal(corner[4], topLeft[4], 2); Assert.Equal(corner[5], topLeft[5], 2);
        Assert.Equal(rightCentre[4], right[4], 2); Assert.Equal(rightCentre[5], right[5], 2);
        // right is as far right of the centre as the corner is left of it: the width's halves.
        Assert.Equal(centre[4] - corner[4], right[4] - centre[4], 1);
        Assert.Equal(centre[5], right[5], 2);
    }

    /// <summary>
    /// A paragraph split round a late float becomes a box with the paragraph's transform (batch 84):
    /// the box carries its transform origin too, so the matrix is the one the unsplit paragraph writes
    /// — about the same top-left corner, not the box's centre.
    /// </summary>
    [Fact]
    public void ASplitParagraphsBox_KeepsTheOrigin()
    {
        double[] split = MatrixOf(Content("<p style='transform:scale(2);transform-origin:0 0'>First<br>Then <span style='float:right'>Late</span></p>"));
        double[] whole = MatrixOf(Content("<p style='transform:scale(2);transform-origin:0 0'>First<br>Then Late</p>"));

        Assert.Equal(whole[4], split[4], 1);
        Assert.Equal(whole[5], split[5], 1);
    }

    /// <summary>A value this cannot read is reported by name and leaves the centre — the matrix of the default.</summary>
    [Fact]
    public void AnUnreadableOrigin_IsReportedAndLeavesTheCentre()
    {
        var reports = new List<string>();
        double[] odd = MatrixOf(Content("<p style='transform:scale(2);transform-origin:somewhere'>Turned</p>", reports));
        double[] centre = MatrixOf(Content("<p style='transform:scale(2)'>Turned</p>"));

        Assert.Equal(centre[4], odd[4], 2);
        Assert.Equal(centre[5], odd[5], 2);
        Assert.Contains(reports, r => r.Contains("transform-origin 'somewhere' was not read", StringComparison.Ordinal));
    }

    /// <summary>
    /// A link inside a translated block moves with it: 20pt right and 30pt down the page, exactly, and
    /// nothing is reported. Before batch 104 the rectangle stayed where the untransformed text was.
    /// </summary>
    [Fact]
    public void ALink_FollowsTheBlocksTranslation()
    {
        var reports = new List<string>();
        var plain = Links(Content("<p><a href='https://example.com/'>link</a></p>"));
        var moved = Links(Content("<p style='transform:translate(20pt, 30pt)'><a href='https://example.com/'>link</a></p>", reports));

        var a = Assert.Single(plain);
        var b = Assert.Single(moved);
        Assert.Equal(a.X + 20, b.X, 1);
        Assert.Equal(a.Y - 30, b.Y, 1);
        Assert.Equal(a.Width, b.Width, 1);
        Assert.Equal(a.Height, b.Height, 1);
        Assert.Empty(reports);
    }

    /// <summary>A link inside a block scaled about its top-left corner is twice as wide, and starts twice as far from that corner as it did.</summary>
    [Fact]
    public void ALink_FollowsTheBlocksScale()
    {
        var plain = Links(Content("<p><a href='https://example.com/'>link</a> after</p>"));
        var scaled = Links(Content("<p style='transform:scale(2);transform-origin:0 0'><a href='https://example.com/'>link</a> after</p>"));
        double left = PageLeft();

        var a = Assert.Single(plain);
        var b = Assert.Single(scaled);
        Assert.Equal(a.Width * 2, b.Width, 1);
        Assert.Equal(left + (a.X - left) * 2, b.X, 1);
    }

    // ------------------------------------------------------------------

    private static IReadOnlyList<Base.Operations.Extraction.PdfLink> Links(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractLinks(input);
    }

    /// <summary>The left edge of the page's content, read from where a plain paragraph's text starts.</summary>
    private static double PageLeft()
        => Tetitulz.Pdf.TestHelpers.PdfContentProbe.XOf(ManagedTestSupport.DecodeStreams(Content("<p>Margin</p>")), "Margin");

    /// <summary>The six numbers of the first <c>cm</c> in the page's content.</summary>
    private static double[] MatrixOf(byte[] pdf)
    {
        string content = ManagedTestSupport.DecodeStreams(pdf);
        var m = System.Text.RegularExpressions.Regex.Match(content,
            @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm");
        Assert.True(m.Success, "no cm operator was emitted");
        return Enumerable.Range(1, 6).Select(i => double.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static byte[] Content(string html, List<string>? reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { if (reports != null) lock (reports) reports.Add(d.Detail ?? ""); },
        });
        return output.ToArray();
    }
}
