// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A block positioned inside a positioned ancestor is measured from that ancestor's padding box
/// since batch 127 (CSS 2.1 §10.1: the nearest positioned ancestor is the containing block), and is
/// drawn with the ancestor's box — a badge at <c>top: 5px; right: 10px</c> sits in its card's corner.
/// Before, every positioned block was resolved against the page and said so. The page is still the
/// containing block, exactly, where no ancestor is positioned. Since batch 130 every positioned
/// ancestor the mapper walks as blocks keeps a box — a flex or grid container (its positioned
/// children no items), a table, a multi-column container, a form — and a percentage offset inside one
/// is a share of its padding box; those were the page, reported.
/// </summary>
/// <remarks>Letter page, the converter's 28.8pt margins; a pixel is 0.75pt. Read back from the words' positions and the boxes' fill rectangles.</remarks>
public class PositionedInAncestorTests
{
    private const double Margin = 28.8;
    private const double Band = 612 - 2 * Margin;

    /// <summary>
    /// <c>top: 5px; left: 8px</c> inside a bordered, padded, relative card: TAG's left is the card's
    /// padding edge (the margin plus the 2pt border) plus 6pt — not 6pt from the page's edge — and
    /// its line is 3.75pt down from the padding edge, where Body's is 7.5pt down (the padding), so
    /// TAG's baseline is 3.75pt above Body's. Nothing is reported about the containing block. (Body
    /// is indented clear of TAG: two words on one line within a few points of each other extract as one.)
    /// </summary>
    [Fact]
    public void AChildOfAPositionedAncestor_IsOffsetFromItsPaddingBox()
    {
        var laid = Lay("<div style='position:relative;margin-top:20px;padding:10px;border:2pt solid #000'><p style='margin:0 0 0 150px'>Body</p><div style='position:absolute;top:5px;left:8px'>TAG</div></div>");

        Assert.Equal(Margin + 2 + 6, laid["TAG"].X, 1);
        Assert.Equal(3.75, laid["TAG"].Y - laid["Body"].Y, 1);
    }

    /// <summary>A badge at <c>top: 5px; right: 10px</c> with no width ends 7.5pt short of the card's right padding edge, as wide as its text.</summary>
    [Fact]
    public void ATopRightBadge_SitsInTheCorner()
    {
        var laid = Lay("<div style='position:relative;padding:10px;border:2pt solid #000'><p style='margin:0'>Body</p><div style='position:absolute;top:5px;right:10px'>TAG</div></div>");

        double padRight = Margin + Band - 2;
        Assert.InRange(laid["TAG"].X + laid["TAG"].Width, padRight - 7.5 - 1.5, padRight - 7.5 + 0.5);
        Assert.True(laid["TAG"].X > padRight - 60, $"TAG at x={laid["TAG"].X:F1} is wider than its text");
        Assert.DoesNotContain(laid.Reports, r => r.Contains("against the PAGE", StringComparison.Ordinal) || r.Contains("left margin", StringComparison.Ordinal));
    }

    /// <summary>A block at <c>bottom: 4px; left: 0</c> ends 3pt above the card's bottom padding edge — its fill's bottom plus the 2pt border — well below the card's last line, which the 40px bottom padding keeps above it.</summary>
    [Fact]
    public void ABottomAnchoredBlock_SitsAboveTheBottomPaddingEdge()
    {
        string html = "<div style='position:relative;padding:10px 10px 40px 10px;border:2pt solid #000;background:#eeeeee'><p style='margin:0'>Body</p><p style='margin:0'>More</p><div style='position:absolute;bottom:4px;left:0'>TAG</div></div>";
        var laid = Lay(html);
        var fill = Regex.Matches(DecodeStreams(ToPdf(html)), @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
            .Select(m => (Y: D(m.Groups[2].Value), W: D(m.Groups[3].Value)))
            .Single(r => r.W > 500);

        double padBottom = fill.Y + 2;
        Assert.InRange(laid["TAG"].Y, padBottom + 3, padBottom + 3 + 6);   // the baseline is a descent above the block's bottom
        Assert.True(laid["TAG"].Y < laid["More"].Y, "the bottom-anchored block is not below the card's last line");
    }

    /// <summary>A bottom-anchored element of two paragraphs ends at the anchor: its last line, not its first, sits 3pt above the padding edge — the group's height measured, not the first paragraph's.</summary>
    [Fact]
    public void ABottomAnchoredElementOfTwoParagraphs_EndsAtTheAnchor()
    {
        string html = "<div style='position:relative;padding:10px 10px 60px 10px;border:2pt solid #000;background:#eeeeee'><p style='margin:0'>Body</p><div style='position:absolute;bottom:4px;left:0'><p style='margin:0'>TAG</p><p style='margin:0'>END</p></div></div>";
        var laid = Lay(html);
        var fill = Regex.Matches(DecodeStreams(ToPdf(html)), @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
            .Select(m => (Y: D(m.Groups[2].Value), W: D(m.Groups[3].Value)))
            .Single(r => r.W > 500);

        double padBottom = fill.Y + 2;
        Assert.InRange(laid["END"].Y, padBottom + 3, padBottom + 3 + 6);
        Assert.InRange(laid["TAG"].Y - laid["END"].Y, 8, 20);
        Assert.InRange(laid["TAG"].X, laid["END"].X - 1, laid["END"].X + 1);
    }

    /// <summary>A stamp with no positioned ancestor is measured from the page, its containing block, and nothing is reported.</summary>
    [Fact]
    public void NoPositionedAncestor_IsThePage_Unreported()
    {
        var laid = Lay("<p>Flow</p><div style='position:absolute;left:300pt;top:100pt'><p>Stamp</p></div>");

        Assert.Equal(300, laid["Stamp"].X, 0);
    }

    /// <summary>
    /// A box between the block and its positioned ancestor is not the containing block: TAG at
    /// <c>top: 0; left: 0</c> sits at the relative outer box's padding corner — the page's margin,
    /// since the outer box has no border — not at the bordered inner box's, where Body sits.
    /// </summary>
    [Fact]
    public void AnUnpositionedBoxBetween_IsNotTheContainingBlock()
    {
        var laid = Lay("<div style='position:relative;padding:10px'><div style='padding:20px;border:1px solid #000'><p style='margin:0'>Body</p><div style='position:absolute;top:0;left:0'>TAG</div></div></div>");

        Assert.Equal(Margin, laid["TAG"].X, 1);
        Assert.True(laid["Body"].X > Margin + 20, $"Body at x={laid["Body"].X:F1} is not inside the inner box");
        Assert.True(laid["TAG"].Y > laid["Body"].Y + 10, $"TAG at y={laid["TAG"].Y:F1} is not at the outer box's top, above Body at y={laid["Body"].Y:F1}");
    }

    /// <summary>
    /// A positioned ancestor with nothing of its own to draw — no border, padding, margin or background
    /// — is still the containing block: TAG at <c>top: 0; left: 20pt</c> shares Body's line (the
    /// ancestor's top), 20pt in from the margin, where a page-relative <c>top: 0</c> would be at the
    /// page's edge, above the flow before the ancestor.
    /// </summary>
    [Fact]
    public void ABareAncestor_IsStillTheContainingBlock()
    {
        var laid = Lay("<p>Flow</p><div style='position:relative'><p style='margin:0 0 0 100px'>Body</p><div style='position:absolute;top:0;left:20pt'>TAG</div></div>");

        Assert.Equal(Margin + 20, laid["TAG"].X, 1);
        Assert.Equal(laid["Body"].Y, laid["TAG"].Y, 1);
        Assert.True(laid["TAG"].Y < laid["Flow"].Y, "TAG was measured from the page's top, above the flow before its ancestor");
    }

    /// <summary>
    /// The nearest positioned ancestor decides, and a table is one since batch 130: TAG at
    /// <c>top: 0; left: 120pt</c> inside a positioned table inside a positioned card sits 120pt in from
    /// the table's left edge — the card's padding edge, where the table begins — on the table's first
    /// line, within the cell's padding of Cell's — not at the page's corner, where it was placed and
    /// reported, nor measured from the card round the table.
    /// </summary>
    [Fact]
    public void ANearerPositionedTable_IsTheContainingBlock()
    {
        var laid = Lay("<p>Flow</p><div style='position:relative;padding:10px'><table style='position:relative'><tr><td>Cell<div style='position:absolute;top:0;left:120pt'>TAG</div></td></tr></table></div>");

        Assert.Equal(Margin + 7.5 + 120, laid["TAG"].X, 1);
        Assert.InRange(laid["Cell"].Y - laid["TAG"].Y, -8, 8);
        Assert.True(laid["TAG"].Y < laid["Flow"].Y, "TAG was measured from the page's top, above the flow before the card");
    }

    /// <summary>
    /// A positioned flex container is the containing block of its positioned child, which takes no
    /// part in the flex layout (CSS Flexbox §4.1): TAG at <c>top: 5px; right: 10px</c> ends 7.5pt
    /// short of the card's right padding edge, and its top is 3.75pt below the padding edge (CSS 2.1
    /// §10.1: the containing block is the ancestor's padding box) — 3.75pt above Body's line, which
    /// starts at the content edge, 7.5pt of padding down, as in a block card — where TAG was laid out
    /// as a second item on Body's line and the card was the page, reported.
    /// </summary>
    [Fact]
    public void APositionedFlexContainer_MeasuresItsPositionedChildFromItsPaddingEdge_NotAsAnItem()
    {
        var laid = Lay("<div style='display:flex;position:relative;padding:10px;border:2pt solid #000'><div style='flex:1'>Body</div><div style='position:absolute;top:5px;right:10px'>TAG</div></div>");

        double padRight = Margin + Band - 2;
        double tagRight = laid["TAG"].X + laid["TAG"].Width;
        Assert.True(tagRight >= padRight - 7.5 - 1.5 && tagRight <= padRight - 7.5 + 0.5, $"TAG ends at {tagRight:F1}, not 7.5pt short of the padding edge {padRight:F1}: {laid.Dump()}");
        Assert.True(laid["TAG"].Y - laid["Body"].Y is >= 2.75 and <= 4.75, $"TAG is {laid["TAG"].Y - laid["Body"].Y:F2}pt above Body's line, not 3.75: {laid.Dump()}");
        Assert.Equal(Margin + 2 + 7.5, laid["Body"].X, 1);
    }

    /// <summary>
    /// A positioned flex container with nothing of its own to draw keeps a bare box: TAG at
    /// <c>top: 0; left: 20pt</c> shares the items' line, 20pt in from the margin, below the flow before
    /// the container — not at the page's top.
    /// </summary>
    [Fact]
    public void ABarePositionedFlexContainer_IsStillTheContainingBlock()
    {
        var laid = Lay("<p>Flow</p><div style='display:flex;position:relative'><div style='margin-left:100px'>Body</div><div style='position:absolute;top:0;left:20pt'>TAG</div></div>");

        Assert.Equal(Margin + 20, laid["TAG"].X, 1);
        Assert.Equal(laid["Body"].Y, laid["TAG"].Y, 1);
        Assert.True(laid["TAG"].Y < laid["Flow"].Y, "TAG was measured from the page's top, above the flow before its container");
    }

    /// <summary>
    /// A positioned grid container is the containing block of its positioned child, which takes no
    /// part in the grid layout (CSS Grid §9): TAG at <c>top: 5px; left: 200px</c> sits 150pt in from
    /// the card's padding edge, its top 3.75pt below that edge (CSS 2.1 §10.1) — 3.75pt above the
    /// items' line, which starts at the content edge; A is at the content edge and B at the second of
    /// two tracks inside the padding — where TAG was a third item on a second row and the card the
    /// page, reported.
    /// </summary>
    [Fact]
    public void APositionedGridContainer_MeasuresItsPositionedChildFromItsPaddingEdge_NotAsAnItem()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;position:relative;padding:10px;border:2pt solid #000'><div>A</div><div>B</div><div style='position:absolute;top:5px;left:200px'>TAG</div></div>");

        Assert.Equal(Margin + 2 + 150, laid["TAG"].X, 1);
        Assert.True(laid["TAG"].Y - laid["A"].Y is >= 2.75 and <= 4.75, $"TAG is {laid["TAG"].Y - laid["A"].Y:F2}pt above A's line, not 3.75: {laid.Dump()}");
        Assert.Equal(Margin + 2 + 7.5, laid["A"].X, 1);
        Assert.Equal(Margin + 2 + 7.5 + (Band - 4 - 15) / 2, laid["B"].X, 0);
    }

    /// <summary>
    /// A positioned multi-column container is the containing block of a positioned block among its
    /// columns' content: TAG at <c>top: 0; right: 0</c> ends at the container's right edge — the
    /// band's — on its first line, where it was the page's, reported.
    /// </summary>
    [Fact]
    public void APositionedColumnContainer_IsTheContainingBlock()
    {
        var laid = Lay("<p>Flow</p><div style='column-count:2;position:relative'><p style='margin:0'>AAA</p><p style='margin:0'>BBB</p><div style='position:absolute;top:0;right:0'>TAG</div></div>");

        Assert.InRange(laid["TAG"].X + laid["TAG"].Width, Margin + Band - 1.5, Margin + Band + 0.5);
        Assert.Equal(laid["AAA"].Y, laid["TAG"].Y, 1);
        Assert.True(laid["TAG"].Y < laid["Flow"].Y, "TAG was measured from the page's top, above the flow before its container");
    }

    /// <summary>
    /// A positioned form or fieldset, walked without a CSS box of its own, keeps a bare one for its
    /// positioned blocks: TAG at <c>top: 0; left: 100pt</c> shares Body's line — the element's top —
    /// 100pt in, below the flow before it.
    /// </summary>
    [Theory]
    [InlineData("form")]
    [InlineData("fieldset")]
    public void APositionedFormOrFieldset_IsTheContainingBlock(string tag)
    {
        var laid = Lay($"<p>Flow</p><{tag} style='position:relative'><p style='margin:0'>Body</p><div style='position:absolute;top:0;left:100pt'>TAG</div></{tag}>");

        Assert.Equal(Margin + 100, laid["TAG"].X, 1);
        Assert.Equal(laid["Body"].Y, laid["TAG"].Y, 1);
        Assert.True(laid["TAG"].Y < laid["Flow"].Y, $"TAG was measured from the page's top, above the flow before the {tag}");
    }

    /// <summary>
    /// A positioned body stands for the page: a stamp inside it is measured from the page, as a
    /// stamp with no positioned ancestor is, and nothing is reported — every positioned block in such
    /// a document was reported as measured against the page instead of an ancestor whose box was not kept.
    /// </summary>
    [Fact]
    public void APositionedBody_IsThePage()
    {
        var laid = Lay("<body style='position:relative'><p>Flow</p><div style='position:absolute;left:300pt;top:100pt'><p>Stamp</p></div></body>");

        Assert.Equal(300, laid["Stamp"].X, 0);
        Assert.True(!laid.Reports.Any(r => r.Contains("position", StringComparison.Ordinal)), string.Join(" | ", laid.Reports));
    }

    /// <summary>A percentage offset with no positioned ancestor is of the page: <c>left: 50%</c> is the page's middle.</summary>
    [Fact]
    public void APercentOffsetWithNoAncestor_IsOfThePage()
    {
        var laid = Lay("<p>Flow</p><div style='position:absolute;left:50%;top:100pt'><p>Stamp</p></div>");

        Assert.Equal(306, laid["Stamp"].X, 0);
    }

    /// <summary>
    /// A percentage offset inside a positioned ancestor is a share of the ancestor's padding box
    /// (batch 130): <c>left: 50%</c> in a card with a 2pt border is half the 550.4pt padding width
    /// in from the padding edge — the band's middle — where it was half the page's width, 336.8pt, reported.
    /// </summary>
    [Fact]
    public void APercentOffsetInsideAnAncestor_IsAShareOfItsPaddingBox()
    {
        var laid = Lay("<div style='position:relative;padding:10px;border:2pt solid #000'><p style='margin:0'>Body</p><div style='position:absolute;top:0;left:50%'>TAG</div></div>");

        Assert.Equal(Margin + 2 + (Band - 4) / 2, laid["TAG"].X, 1);
    }

    /// <summary>A fixed block inside a relative ancestor is measured from the page — its viewport — on every page, and the ancestor is not reported.</summary>
    [Fact]
    public void FixedInsideAnAncestor_IsThePage()
    {
        var laid = Lay("<div style='position:relative;padding:10px'><p>Body</p><div style='position:fixed;left:100pt;top:100pt'><p>Stamp</p></div></div>");

        Assert.Equal(100, laid["Stamp"].X, 0);
    }

    /// <summary>The flow inside the card is not displaced: Body and More sit where they would without the badge.</summary>
    [Fact]
    public void ThePositionedChild_DoesNotDisplaceTheCardsFlow()
    {
        var with = Lay("<div style='position:relative;padding:10px'><p>Body</p><div style='position:absolute;top:0;right:0'>TAG</div><p>More</p></div>");
        var without = Lay("<div style='position:relative;padding:10px'><p>Body</p><p>More</p></div>");

        Assert.Equal(without["More"].Y, with["More"].Y, 1);
    }

    /// <summary>A right-anchored badge of two paragraphs is as wide as the wider: its line ends at the anchor unwrapped, and the shorter starts at the same left edge.</summary>
    [Fact]
    public void ARightAnchoredBadgeOfTwoParagraphs_IsAsWideAsTheWider()
    {
        var laid = Lay("<div style='position:relative;padding:10px;border:2pt solid #000'><p style='margin:0'>Body</p><div style='position:absolute;top:5px;right:10px'><p style='margin:0'>NEW</p><p style='margin:0'>longer caption</p></div></div>");

        double padRight = Margin + Band - 2;
        Assert.InRange(laid["caption"].X + laid["caption"].Width, padRight - 7.5 - 1.5, padRight - 7.5 + 0.5);
        Assert.Equal(laid["longer"].Y, laid["caption"].Y, 1);
        Assert.Equal(laid["NEW"].X, laid["longer"].X, 1);
    }

    /// <summary>A list inside a positioned card: its item's positioned block is measured from the card, exactly as the same block beside the list is.</summary>
    [Fact]
    public void APositionedBlockInAListItem_IsMeasuredFromThePositionedCardRoundTheList()
    {
        var inItem = Lay("<div style='position:relative;padding:10px;border:2pt solid #000'><ul><li>One<div style='position:absolute;top:5px;left:8px'>TAG</div></li></ul></div>");
        var beside = Lay("<div style='position:relative;padding:10px;border:2pt solid #000'><ul><li>One</li></ul><div style='position:absolute;top:5px;left:8px'>TAG</div></div>");

        Assert.Equal(Margin + 2 + 6, inItem["TAG"].X, 1);
        Assert.Equal(beside["TAG"].X, inItem["TAG"].X, 1);
        Assert.Equal(beside["TAG"].Y, inItem["TAG"].Y, 1);
    }

    // ------------------------------------------------------------------

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private sealed class Laid
    {
        public readonly List<(string Text, double X, double Y, double Width)> Words = new();
        public readonly List<string> Reports = new();

        public (string Text, double X, double Y, double Width) this[string word]
        {
            get
            {
                var found = Words.FirstOrDefault(w => w.Text == word);
                Assert.True(found.Text != null, $"'{word}' was not laid out at all: {Dump()}");
                return found;
            }
        }

        public string Dump() => string.Join("; ", Words.Select(w => $"{w.Text}@({w.X:F1},{w.Y:F1})"));
    }

    private static Laid Lay(string html)
    {
        var laid = new Laid();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            laid.Words.Add((word.Text, word.X, word.Y, word.Width));
        return laid;
    }
}
