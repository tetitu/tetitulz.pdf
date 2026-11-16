// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.BoxInsetLeft"/> and its siblings: how far a paragraph's box — borders,
/// shading, background image — stands out from its text.
/// </summary>
/// <remarks>
/// Asserted on the stroked edges' coordinates and the text's position, read back from the content
/// stream, because the property's whole contract is <i>which</i> of the two moves.
/// </remarks>
public class ParagraphBoxInsetTests
{
    private static readonly Regex FilledRect = new(
        @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\s*\n?f\b", RegexOptions.Compiled);

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static string Content(Action<Paragraph>? set = null)
    {
        var p = new Paragraph { IndentLeft = 20, IndentRight = 20, SpacingBefore = 20, SpacingAfter = 20 };
        p.Runs.Add(new TextRun { Text = "Inside" });
        var red = new Border(1, "#FF0000");
        p.BorderTop = red; p.BorderBottom = red; p.BorderLeft = red; p.BorderRight = red;
        set?.Invoke(p);

        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    /// <summary>The box's four sides, from its stroked edges.</summary>
    private static (double Left, double Right, double Bottom, double Top) Edges(string content)
    {
        var segments = PdfContentProbe.StrokedSegments(content);
        var vertical = segments.Where(s => s.IsVertical).Select(s => s.X0).ToList();
        var horizontal = segments.Where(s => s.IsHorizontal).Select(s => s.Y0).ToList();
        Assert.Equal(2, vertical.Count);
        Assert.Equal(2, horizontal.Count);
        return (vertical.Min(), vertical.Max(), horizontal.Min(), horizontal.Max());
    }

    [Fact]
    public void AnInsetMovesTheBoxOutward_AndLeavesTheTextWhereItWas()
    {
        string plain = Content();
        string inset = Content(p => { p.BoxInsetLeft = 12; p.BoxInsetRight = 12; p.BoxInsetTop = 12; p.BoxInsetBottom = 12; });

        Assert.Equal(PdfContentProbe.XOf(plain, "Inside"), PdfContentProbe.XOf(inset, "Inside"), 2);
        Assert.Equal(PdfContentProbe.YOf(plain, "Inside"), PdfContentProbe.YOf(inset, "Inside"), 2);

        var before = Edges(plain);
        var after = Edges(inset);
        // 12pt against the default 2pt: every side 10pt further out.
        Assert.Equal(before.Left - 10, after.Left, 2);
        Assert.Equal(before.Right + 10, after.Right, 2);
        Assert.Equal(before.Bottom - 10, after.Bottom, 2);
        Assert.Equal(before.Top + 10, after.Top, 2);
    }

    /// <summary>Each side is its own: an inset on one moves that edge only.</summary>
    [Fact]
    public void EachSideTakesItsOwnInset()
    {
        var before = Edges(Content());

        Assert.Equal(before.Left - 10, Edges(Content(p => p.BoxInsetLeft = 12)).Left, 2);
        Assert.Equal(before.Right + 10, Edges(Content(p => p.BoxInsetRight = 12)).Right, 2);
        Assert.Equal(before.Bottom - 10, Edges(Content(p => p.BoxInsetBottom = 12)).Bottom, 2);
        Assert.Equal(before.Top + 10, Edges(Content(p => p.BoxInsetTop = 12)).Top, 2);

        var leftOnly = Edges(Content(p => p.BoxInsetLeft = 12));
        Assert.Equal(before.Right, leftOnly.Right, 2);
        Assert.Equal(before.Top, leftOnly.Top, 2);
    }

    /// <summary>An inset smaller than the default does not bring the box closer than it always was.</summary>
    [Fact]
    public void AnInsetBelowTheDefault_KeepsTheDefault()
    {
        var before = Edges(Content());
        var small = Edges(Content(p => { p.BoxInsetLeft = 0.5; p.BoxInsetBottom = 0.5; }));

        Assert.Equal(before.Left, small.Left, 2);
        Assert.Equal(before.Bottom, small.Bottom, 2);
        // Potency: the default really is the model's constant, 2pt from the text's band.
        Assert.Equal(20 - Paragraph.DefaultBoxInsetPt, before.Left - PageLeft(), 1);
    }

    /// <summary>The shading fills the whole box, inset included, on the sides the insets name.</summary>
    [Fact]
    public void TheShadingFillsTheInset()
    {
        static (double X, double Y, double W, double H) Fill(string content)
        {
            var m = FilledRect.Matches(content).Cast<Match>().Single();
            return (D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value));
        }

        var plain = Fill(Content(p => p.Shading = "#0000FF"));
        var inset = Fill(Content(p => { p.Shading = "#0000FF"; p.BoxInsetLeft = 12; p.BoxInsetTop = 7; }));

        // Grown 10pt to the left and 5pt upward; the right and bottom sides stay put.
        Assert.Equal(plain.X - 10, inset.X, 2);
        Assert.Equal(plain.W + 10, inset.W, 2);
        Assert.Equal(plain.Y, inset.Y, 2);
        Assert.Equal(plain.H + 5, inset.H, 2);
    }

    /// <summary>
    /// A boxed paragraph that would straddle the page's end moves to the next page with its whole
    /// box — the insets included, or the box's bottom runs past the margin.
    /// </summary>
    [Fact]
    public void TheInsetsAreReservedWhenThePageBreaks()
    {
        static int Pages(int spacer, double inset)
        {
            var push = new Paragraph { SpacingBefore = spacer };
            push.Runs.Add(new TextRun { Text = "Spacer" });
            var boxed = new Paragraph { BoxInsetTop = inset, BoxInsetBottom = inset };
            boxed.Runs.Add(new TextRun { Text = "Inside" });
            boxed.BorderTop = new Border(1, "#FF0000");

            var section = new Section();
            section.Children.Add(push);
            section.Children.Add(boxed);
            var doc = new Document();
            doc.Sections.Add(section);
            return PdfContentProbe.CountPages(doc.ToPdf());
        }

        // The deepest a spacer can push the plain box and still leave it on the first page.
        int fits = 0, spills = 2000;
        Assert.Equal(2, Pages(spills, 0));
        while (spills - fits > 1)
        {
            int mid = (fits + spills) / 2;
            if (Pages(mid, 0) == 1) fits = mid; else spills = mid;
        }

        Assert.Equal(1, Pages(fits, 0));
        // At the same depth, 30pt insets above and below no longer fit.
        Assert.Equal(2, Pages(fits, 30));
    }

    /// <summary>The page's left margin, where an unindented paragraph's band starts.</summary>
    private static double PageLeft()
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Margin" });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.XOf(PdfContentProbe.DecodeStreams(doc.ToPdf()), "Margin");
    }
}
