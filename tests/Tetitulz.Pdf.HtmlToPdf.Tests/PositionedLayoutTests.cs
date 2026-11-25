// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>position: absolute</c> / <c>fixed</c> and <c>z-index</c> in the managed engine.
///
/// <para><b>The decline named two requirements and only one was real.</b> "A paint order
/// independent of flow" was the requirement, and the exporter already had the mechanism —
/// <c>RenderParagraphAt(…, paginate: false)</c> draws at a point without moving or breaking, which
/// headers, footers and table cells have always used. What was missing was a way for the model to
/// say <i>where</i>. "A containing-block chain" is a real limit, not a prerequisite: the page is
/// the containing block, which is exactly right for the stamps, watermarks and corner labels this
/// is used for, and wrong for a block positioned inside another positioned block — reported every
/// time.</para>
///
/// <para>Assertions are on measured glyph positions: a positioned block that quietly stayed in the
/// flow produces a valid PDF with the text present, so nothing structural separates the two.</para>
/// </summary>
public class PositionedLayoutTests
{
    /// <summary>
    /// The claim: an absolutely positioned block lands at its stated point, not where flow would
    /// have put it.
    /// </summary>
    [Fact]
    public void AnAbsolutelyPositionedBlock_LandsAtItsStatedPoint()
    {
        var words = Words(
            "<p>Flow</p>" +
            "<div style='position:absolute;left:300pt;top:100pt'><p>Stamp</p></div>");

        var stamp = Word(words, "Stamp");

        // Left is measured from the page's left edge, so 300pt is 300pt — not the content-left
        // origin an in-flow block would take.
        Assert.InRange(stamp.X, 298, 302);
        Assert.True(stamp.X > Word(words, "Flow").X + 100,
            "the positioned block was laid out in the flow");
    }

    /// <summary>
    /// <c>top</c> is measured from the page's TOP, while PDF counts up from the bottom. The
    /// conversion happens once in the exporter; this is what pins it.
    /// </summary>
    [Fact]
    public void TopIsMeasuredFromThePageTop_NotThePdfOrigin()
    {
        var words = Words(
            "<div style='position:absolute;left:100pt;top:50pt'><p>High</p></div>" +
            "<div style='position:absolute;left:100pt;top:400pt'><p>Low</p></div>");

        // A larger `top` is further DOWN the page, which is a smaller PDF y.
        Assert.True(Word(words, "Low").Y < Word(words, "High").Y,
            "a larger top: value did not move the block down the page");
    }

    /// <summary>
    /// A positioned block is taken out of the flow, so the content after it does not move down to
    /// make room. This is the half that "out of flow" actually means, and it is separate from
    /// where the block landed.
    /// </summary>
    [Fact]
    public void APositionedBlock_DoesNotDisplaceTheFlow()
    {
        double withPositioned = Word(Words(
            "<p>First</p>" +
            "<div style='position:absolute;left:300pt;top:100pt'><p>Stamp</p></div>" +
            "<p>Second</p>"), "Second").Y;

        double without = Word(Words("<p>First</p><p>Second</p>"), "Second").Y;

        Assert.InRange(withPositioned, without - 1, without + 1);
    }

    /// <summary>
    /// <c>z-index</c> orders the positioned blocks among themselves. Asserted on the order the
    /// marks reach the content stream — later is on top — because that is what paint order is.
    /// </summary>
    [Fact]
    public void ZIndex_OrdersThePositionedBlocks()
    {
        string content = ContentStreamOf(
            "<div style='position:absolute;left:100pt;top:100pt;z-index:5'><p>Above</p></div>" +
            "<div style='position:absolute;left:100pt;top:100pt;z-index:1'><p>Below</p></div>");

        int below = content.IndexOf("Below", StringComparison.Ordinal);
        int above = content.IndexOf("Above", StringComparison.Ordinal);

        Assert.True(below >= 0 && above >= 0, "both blocks must be drawn");
        Assert.True(below < above,
            "the lower z-index must be painted first, so the higher one lands on top");
    }

    /// <summary>
    /// Equal z-index keeps document order — CSS's own tie-break, and here it comes from the sort
    /// being stable rather than from a secondary key.
    /// </summary>
    [Fact]
    public void EqualZIndex_KeepsDocumentOrder()
    {
        string content = ContentStreamOf(
            "<div style='position:absolute;left:100pt;top:100pt'><p>Alpha</p></div>" +
            "<div style='position:absolute;left:100pt;top:100pt'><p>Beta</p></div>");

        Assert.True(content.IndexOf("Alpha", StringComparison.Ordinal)
                  < content.IndexOf("Beta", StringComparison.Ordinal));
    }

    /// <summary>
    /// Positioned content paints over the flow, whatever its z-index — that is what
    /// <c>position</c> means, and it is why the held-back pass does not need to interleave with
    /// the flow at all.
    /// </summary>
    [Fact]
    public void PositionedContent_PaintsOverTheFlow()
    {
        string content = ContentStreamOf(
            "<div style='position:absolute;left:100pt;top:100pt;z-index:-5'><p>Stamp</p></div>" +
            "<p>Flow</p>");

        Assert.True(content.IndexOf("Flow", StringComparison.Ordinal)
                  < content.IndexOf("Stamp", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>right</c> anchors the far edge, which needs a width to measure back from.
    /// </summary>
    [Fact]
    public void RightWithAWidth_AnchorsTheFarEdge()
    {
        var words = Words(
            "<div style='position:absolute;right:50pt;top:100pt;width:100pt'><p>Edge</p></div>");

        // Page is 612pt wide by default: 612 - 50 - 100 = 462.
        Assert.InRange(Word(words, "Edge").X, 458, 466);
    }

    /// <summary>
    /// <c>right</c> with no width: the block is as wide as its content (CSS 2.1 §10.3.7's
    /// shrink-to-fit), its right edge at the offset — not placed at the left margin and reported.
    /// </summary>
    [Fact]
    public void RightWithNoWidth_ShrinksToItsContentAtTheFarEdge()
    {
        var reports = new List<string>();
        var words = Words(
            "<div style='position:absolute;right:50pt;top:100pt'><p style='margin:0'>Edge</p></div>", reports);

        var edge = Word(words, "Edge");
        Assert.InRange(edge.X + edge.Width, 612 - 50 - 3, 612 - 50 + 0.5);
        Assert.DoesNotContain(reports, r => r.Contains("far edge", StringComparison.Ordinal));
    }

    /// <summary>A right-anchored element of two paragraphs with no width is as wide as the wider one, which ends at the offset unwrapped.</summary>
    [Fact]
    public void RightWithNoWidth_IsAsWideAsItsWiderParagraph()
    {
        var words = Words(
            "<div style='position:absolute;right:50pt;top:100pt'><p style='margin:0'>NEW</p><p style='margin:0'>longer caption</p></div>");

        var caption = Word(words, "caption");
        Assert.InRange(caption.X + caption.Width, 612 - 50 - 3, 612 - 50 + 0.5);
        Assert.Equal(Word(words, "longer").Y, caption.Y, 1);
        Assert.Equal(Word(words, "NEW").X, Word(words, "longer").X, 1);
    }

    /// <summary>
    /// An axis with no offset starts at the page's margin on it — there is no static position here:
    /// with only <c>top</c> the block's left edge is the flow's, with only <c>left</c> its first line
    /// is where the flow's first line is.
    /// </summary>
    [Fact]
    public void AnAxisWithNoOffset_StartsAtThePagesMargin()
    {
        var flow = Word(Words("<p style='margin:0'>Flow</p>"), "Flow");

        Assert.InRange(Word(Words("<div style='position:absolute;top:300pt'><p style='margin:0'>Stamp</p></div>"), "Stamp").X, flow.X - 1, flow.X + 1);
        Assert.InRange(Word(Words("<div style='position:absolute;left:300pt'><p style='margin:0'>Stamp</p></div>"), "Stamp").Y, flow.Y - 1, flow.Y + 1);
    }

    /// <summary><c>left</c> and <c>right</c> together size a block with no width: 612 − 100 − 300 = 212pt, so a 300pt line wraps.</summary>
    [Fact]
    public void LeftAndRight_SizeTheBlock()
    {
        var words = Words(
            "<div style='position:absolute;left:100pt;right:300pt;top:100pt'><p style='margin:0'>" +
            "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo</p></div>");

        var placed = words.Where(w => w.X >= 99).ToList();
        Assert.True(placed.Select(w => Math.Round(w.Y)).Distinct().Count() >= 2, "the block was not narrowed to 212pt");
        Assert.All(placed, w => Assert.True(w.X + w.Width <= 312.5, $"'{w.Text}' ends at {w.X + w.Width}, past the right offset"));
    }

    /// <summary>
    /// <c>bottom</c> against the page is the block's BOTTOM edge (CSS 2.1 §10.6.4): its last line
    /// sits a descent above 100pt, the line before it above that — the block did not hang below
    /// its anchor with its top there.
    /// </summary>
    [Fact]
    public void BottomOnThePage_IsTheBlocksBottomEdge()
    {
        var words = Words(
            "<div style='position:absolute;left:100pt;bottom:100pt'><p style='margin:0'>Foot</p><p style='margin:0'>Last</p></div>");

        Assert.InRange(Word(words, "Last").Y, 100, 108);
        Assert.InRange(Word(words, "Foot").Y - Word(words, "Last").Y, 8, 20);
    }

    /// <summary>
    /// No offsets at all means "leave it where flow put it", which this engine cannot do — so it
    /// falls back to the flow and says why, rather than placing the block at the page corner.
    /// </summary>
    [Fact]
    public void PositionWithNoOffsets_FallsBackToTheFlowAndReports()
    {
        var reports = new List<string>();
        var words = Words("<p>First</p><div style='position:absolute'><p>Stamp</p></div>", reports);

        Assert.InRange(Word(words, "Stamp").X, Word(words, "First").X - 1,
                       Word(words, "First").X + 1);
        Assert.Contains(reports, r => r.Contains("no static position", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing about the containing block is reported (batch 130): a stamp with no positioned ancestor
    /// is resolved against the page, which is its containing block, and a stamp inside a positioned
    /// table against the table — 100pt in from its left edge, the band's, below the flow before it,
    /// its paragraph's 1em top margin (12pt) below the table's top edge, since an absolutely
    /// positioned box's margins do not collapse with its children's (CSS 2.1 §8.3.1): 9.75pt below
    /// Cell, whose line starts the border spacing and the cell's padding (1.5pt + 0.75pt) down — where
    /// a table was an ancestor whose box the mapper did not keep, the page, reported, from batch 127
    /// to 130. (A positioned ancestor that is a box of blocks is the containing block since batch 127:
    /// <c>PositionedInAncestorTests</c>.)
    /// </summary>
    [Fact]
    public void TheContainingBlock_IsNeverReported_AndAPositionedTableIsOne_MeasuredFromItsTopEdge()
    {
        var stamp = new List<string>();
        var alone = Words("<div style='position:absolute;left:100pt;top:100pt'><p>Stamp</p></div>", stamp);
        Assert.DoesNotContain(stamp, r => r.Contains("position", StringComparison.Ordinal));
        Assert.Equal(100, Word(alone, "Stamp").X, 0);

        var inTable = new List<string>();
        var words = Words("<p>Flow</p><table style='position:relative'><tr><td>Cell<div style='position:absolute;left:100pt;top:0'><p>Stamp</p></div></td></tr></table>", inTable);
        Assert.DoesNotContain(inTable, r => r.Contains("position", StringComparison.Ordinal));
        var placed = Word(words, "Stamp");
        Assert.Equal(28.8 + 100, placed.X, 0);
        Assert.True(placed.Y < Word(words, "Flow").Y, "the stamp was measured from the page's top, above the flow before the table");
        Assert.Equal(12 - 1.5 - 0.75, Word(words, "Cell").Y - placed.Y, 1);
    }

    /// <summary>
    /// <c>position: fixed</c> repeats at its point on every page (batch 87), as print media lays it
    /// out: over three pages of flow the stamp is on each, at the same place, while an absolute
    /// block written beside it is on one page only — and nothing about repeating is reported. Before
    /// batch 87 a fixed block was placed once, like absolute, and reported.
    /// </summary>
    [Fact]
    public void PositionFixed_RepeatsAtItsPointOnEveryPage()
    {
        var reports = new List<string>();
        string flow = string.Concat(Enumerable.Range(0, 120).Select(i => $"<p>Line {i}</p>"));
        var pages = Pages("<div style='position:fixed;left:100pt;top:100pt'><p>Stamp</p></div>" +
                          "<div style='position:absolute;left:100pt;top:200pt'><p>Once</p></div>" + flow, reports);

        Assert.True(pages.Count >= 3, $"the flow made {pages.Count} page(s), not enough to see a repeat");
        var stamps = pages.Select(p => p.Lines.SelectMany(l => l.Words).FirstOrDefault(w => w.Text.Contains("Stamp", StringComparison.Ordinal))).ToList();
        Assert.All(stamps, s => Assert.NotNull(s));
        Assert.All(stamps, s => { Assert.Equal(stamps[0]!.X, s!.X, 1); Assert.Equal(stamps[0]!.Y, s.Y, 1); });
        Assert.Equal(1, pages.Count(p => p.Lines.SelectMany(l => l.Words).Any(w => w.Text.Contains("Once", StringComparison.Ordinal))));
        Assert.DoesNotContain(reports, r => r.Contains("repeat", StringComparison.Ordinal));
    }

    /// <summary>
    /// A document with nothing positioned hears nothing — the rule that keeps the reports above
    /// worth reading.
    /// </summary>
    [Fact]
    public void ADocumentWithNothingPositioned_ReportsNothingAboutIt()
    {
        var reports = new List<string>();
        Words("<p>First</p><p>Second</p>", reports);

        Assert.DoesNotContain(reports, r => r.Contains("position", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A positioned element of several paragraphs draws every one of them at its point, each under
    /// the one above, out of the flow — not the first placed and the rest left where the flow
    /// would have put them (at the content's left edge, pushing the next paragraph down).
    /// </summary>
    [Fact]
    public void APositionedElementOfSeveralParagraphs_DrawsEveryOneAtItsPoint()
    {
        var reports = new List<string>();
        var words = Words(
            "<p>Before</p>" +
            "<div style='position:absolute;left:300pt;top:100pt'><p>Alpha</p><p>Bravo</p><p>Charlie</p></div>" +
            "<p>After</p>", reports);

        foreach (string w in new[] { "Alpha", "Bravo", "Charlie" })
            Assert.InRange(Word(words, w).X, 298, 302);
        Assert.True(Word(words, "Alpha").Y > Word(words, "Bravo").Y && Word(words, "Bravo").Y > Word(words, "Charlie").Y,
            "the paragraphs are not stacked downwards in order");
        Assert.InRange(Word(words, "Alpha").Y - Word(words, "Charlie").Y, 20, 80);

        double without = Word(Words("<p>Before</p><p>After</p>"), "After").Y;
        Assert.InRange(Word(words, "After").Y, without - 1, without + 1);
        Assert.DoesNotContain(reports, r => r.Contains("placed only the first", StringComparison.Ordinal));
    }

    /// <summary>
    /// A positioned block inside a list item — and one in a nested item — is placed at its point,
    /// out of the item's text, not collected into the item's line after its words.
    /// </summary>
    [Fact]
    public void APositionedBlockInAListItem_IsPlacedAtItsPoint()
    {
        var reports = new List<string>();
        var words = Words(
            "<ul><li>Item one<div style='position:absolute;left:300pt;top:100pt'>Badge</div></li>" +
            "<li>Item two<ul><li>Inner<div style='position:absolute;left:300pt;top:200pt'>Deep</div></li></ul></li></ul>", reports);

        Assert.InRange(Word(words, "Badge").X, 298, 302);
        Assert.InRange(Word(words, "Deep").X, 298, 302);
        Assert.True(Word(words, "Badge").Y > Word(words, "Deep").Y, "the two were not placed at their own tops");
        Assert.DoesNotContain(reports, r => r.Contains("not supported", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>position: relative</c> list item is the containing block of the positioned blocks in it
    /// rather than staying in the item's text: a badge at <c>left: 100pt; top: 10pt</c>
    /// sits 100 pt in from the item's padding edge — where its text starts — and 10 pt below its top,
    /// nothing reported, and one at <c>bottom: 0</c> on the item's last line — its box ends where the
    /// item does, its gap after it outside; and the list is laid out as it is with the item
    /// unpositioned — every item, its number and the paragraphs round it where they were — though the
    /// model cuts it round the item, first, between or last.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void APositionedBlockInARelativeListItem_IsMeasuredFromTheItem(int which)
    {
        var reports = new List<string>();
        string[] names = { "Itemone", "Itemtwo", "Itemthree" };
        string List(bool positioned)
        {
            // 30pt margins, wider than the paragraphs' 12pt, so a list margin lost is not collapsed away.
            var sb = new System.Text.StringBuilder("<p>Before</p><ol style='margin:30pt 0'>");
            for (int i = 0; i < 3; i++)
                sb.Append(i == which && positioned
                    ? $"<li style='position:relative;margin-bottom:6pt'>{names[i]}<span style='position:absolute;left:100pt;top:10pt'>Badge</span><span style='position:absolute;left:200pt;bottom:0'>Foot</span></li>"
                    : $"<li style='margin-bottom:6pt'>{names[i]}</li>");
            return sb.Append("</ol><p>After</p>").ToString();
        }
        var words = Words(List(true), reports);
        var plain = Words(List(false));

        var item = Word(words, names[which]);
        var badge = Word(words, "Badge");
        Assert.Equal(item.X + 100, badge.X, 0);
        Assert.Equal(item.Y - 10, badge.Y, 0);
        Assert.Equal(item.X + 200, Word(words, "Foot").X, 0);
        Assert.Equal(item.Y, Word(words, "Foot").Y, 0);
        Assert.DoesNotContain(reports, r => r.Contains("not supported", StringComparison.Ordinal));

        foreach (string w in new[] { "Before", "Itemone", "Itemtwo", "Itemthree", "1.", "2.", "3.", "After" })
        {
            Assert.Equal(Word(plain, w).X, Word(words, w).X, 1);
            Assert.Equal(Word(plain, w).Y, Word(words, w).Y, 1);
        }
    }

    /// <summary>A positioned block in a sublist of a <c>position: relative</c> item is measured from that item too — its nearest positioned ancestor — not from the page.</summary>
    [Fact]
    public void APositionedBlockInTheSublistOfARelativeItem_IsMeasuredFromTheItem()
    {
        var reports = new List<string>();
        var words = Words(
            "<p>Before</p><ul><li>Itemone</li><li style='position:relative'>Itemtwo<ul><li>Inner<span style='position:absolute;left:150pt;top:0'>Deep</span></li></ul></li></ul>", reports);

        var item = Word(words, "Itemtwo");
        Assert.Equal(item.X + 150, Word(words, "Deep").X, 0);
        Assert.Equal(item.Y, Word(words, "Deep").Y, 0);
        Assert.DoesNotContain(reports, r => r.Contains("not supported", StringComparison.Ordinal));
    }

    /// <summary>As <see cref="APositionedBlockInARelativeListItem_IsMeasuredFromTheItem"/> right to left: the item's padding edge is on the right, where its text starts, and <c>right: 60pt</c> is measured from it.</summary>
    [Fact]
    public void APositionedBlockInARelativeRtlListItem_IsMeasuredFromTheItemsRightEdge()
    {
        var reports = new List<string>();
        var words = Words(
            "<ul dir='rtl'><li>Itemone</li><li style='position:relative'>Itemtwo<span style='position:absolute;right:60pt;top:0'>Badge</span></li></ul>", reports);

        var item = Word(words, "Itemtwo");
        var badge = Word(words, "Badge");
        Assert.Equal(item.X + item.Width - 60, badge.X + badge.Width, 0);
        Assert.Equal(item.Y, badge.Y, 0);
        // The item's text starts where its unpositioned neighbour's does: its box is the item's.
        var one = Word(words, "Itemone");
        Assert.Equal(one.X + one.Width, item.X + item.Width, 1);
        Assert.DoesNotContain(reports, r => r.Contains("not supported", StringComparison.Ordinal));
    }

    /// <summary>
    /// A positioned block whose containing block is a positioned list nested in an item, or a
    /// positioned item of one, rather than staying in the item's text: a badge at
    /// <c>left: 100pt; top: 10pt</c> is measured from that item's padding edge — where its text starts —
    /// or that sublist's — where its item's text starts — and its top, nothing reported; and the list is
    /// laid out as it is unpositioned — every item, every number and the paragraphs round it where they
    /// were — though the model takes the sublist out of its item. Cases: a positioned item of a
    /// sublist; a positioned sublist; a positioned item two levels down, in the outer list's last item
    /// (whose bottom margin goes after its sublists); a positioned sublist in a positioned item. The
    /// items whose sublists leave them have a bottom margin, which goes after the sublists too.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void APositionedBlockInAPositionedSublist_IsMeasuredFromIt(int shape)
    {
        var reports = new List<string>();
        const string Badge = "<span style='position:absolute;left:100pt;top:10pt'>Badge</span>";
        string Html(bool positioned)
        {
            // An item's or list's style: its bottom margin, and position: relative in its shape.
            string S(int s, string margin) => " style='" + margin + (positioned && shape == s ? "position:relative" : "") + "'";
            string B(int s) => positioned && shape == s ? Badge : "";
            const string Gap = "margin-bottom:9pt;";
            string inner = shape == 2
                ? $"<ul><li{S(-1, Gap)}>Innerone<ul><li{S(2, "")}>Deepone{B(2)}</li><li>Deeptwo</li></ul></li><li>Innertwo</li></ul>"
                : $"<ul{S(shape == 3 ? 3 : 1, "")}><li{S(0, "")}>Innerone{B(0)}{B(1)}{B(3)}</li><li>Innertwo</li></ul>";
            string outerTwo = shape == 2 ? "" : "<li>Outertwo</li>";
            // In the last shape the positioned item holds a block of its own, so it is boxed, its sublist in its box.
            string corner = positioned && shape == 3 ? "<span style='position:absolute;left:200pt;top:0'>Corner</span>" : "";
            string firstOuter = shape == 2
                ? $"<li>Outerone</li><li{S(-1, Gap)}>Outerlast{inner}</li>"
                : $"<li{S(shape == 3 ? 3 : -1, Gap)}>Outerone{corner}{inner}</li>";
            return $"<p>Before</p><ol style='margin:30pt 0'>{firstOuter}{outerTwo}</ol><p>After</p>";
        }
        var words = Words(Html(true), reports);
        var plain = Words(Html(false));

        // The containing block's padding edge: the item's text start, or the sublist's (its item's text start).
        var anchor = shape switch { 0 => Word(words, "Innerone"), 2 => Word(words, "Deepone"), _ => Word(words, "Outerone") };
        var top = Word(words, shape == 2 ? "Deepone" : "Innerone");
        var badge = Word(words, "Badge");
        Assert.Equal(anchor.X + 100, badge.X, 0);
        Assert.Equal(top.Y - 10, badge.Y, 0);
        if (shape == 3)
        {
            var outer = Word(words, "Outerone");
            Assert.Equal(outer.X + 200, Word(words, "Corner").X, 0);
            Assert.Equal(outer.Y, Word(words, "Corner").Y, 0);
        }
        Assert.DoesNotContain(reports, r => r.Contains("not supported", StringComparison.Ordinal));

        string[] laidOut = shape == 2
            ? new[] { "Before", "1.", "2.", "Outerone", "Outerlast", "Innerone", "Deepone", "Deeptwo", "Innertwo", "After" }
            : new[] { "Before", "1.", "2.", "Outerone", "Innerone", "Innertwo", "Outertwo", "After" };
        foreach (string w in laidOut)
        {
            Assert.Equal(Word(plain, w).X, Word(words, w).X, 1);
            Assert.Equal(Word(plain, w).Y, Word(words, w).Y, 1);
        }
    }

    /// <summary>
    /// A list in a table cell is cut the same way: a badge in a positioned item of its sublist is
    /// measured from that item, nothing reported.
    /// </summary>
    [Fact]
    public void APositionedBlockInAPositionedSublistOfACell_IsMeasuredFromIt()
    {
        var reports = new List<string>();
        var words = Words(
            "<table><tr><td><ul><li>Outerone<ul><li style='position:relative'>Innerone<span style='position:absolute;left:100pt;top:10pt'>Badge</span></li></ul></li></ul></td></tr></table>", reports);

        var item = Word(words, "Innerone");
        Assert.Equal(item.X + 100, Word(words, "Badge").X, 0);
        Assert.Equal(item.Y - 10, Word(words, "Badge").Y, 0);
        Assert.DoesNotContain(reports, r => r.Contains("not supported", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>position: relative</c> list is the containing block of the positioned blocks in its items
    /// (a bare box round it): a badge at <c>left: 100pt; top: 20pt</c> sits 100 pt in
    /// from the list's padding edge and 20 pt below its top — under the paragraphs before it, where its
    /// first item's words are — and nothing is reported. Measured from the page it would sit 20 pt down
    /// the sheet, above them.
    /// </summary>
    [Fact]
    public void APositionedBlockInARelativeList_IsMeasuredFromTheList()
    {
        var reports = new List<string>();
        var words = Words(
            "<p>One</p><p>Two</p><p>Three</p>" +
            "<ul style='position:relative;margin:40pt 0'><li>Itemone<span style='position:absolute;left:100pt;top:20pt'>Badge</span></li></ul>" +
            "<p>After</p>", reports);

        var item = Word(words, "Itemone");
        var badge = Word(words, "Badge");
        Assert.Equal(item.Y - 20, badge.Y, 0);
        Assert.Equal(Word(words, "One").X + 100, badge.X, 0);
        Assert.DoesNotContain(reports, r => r.Contains("not supported", StringComparison.Ordinal));
        // The box round the list adds nothing to the flow, and takes the list's margins whole — 40 pt,
        // wider than the paragraphs' own, so neither collapses them away: the list's first item, and the
        // paragraph after it, where they are with the same list unpositioned.
        string Unpositioned(string tail) => "<p>One</p><p>Two</p><p>Three</p><ul style='margin:40pt 0'><li>Itemone</li></ul>" + tail;
        double plain = Word(Words("<p>One</p><p>Two</p><p>Three</p><ul style='position:relative;margin:40pt 0'><li>Itemone</li></ul><p>After</p>"), "After").Y;
        var unpositioned = Words(Unpositioned("<p>After</p>"));
        Assert.Equal(Word(unpositioned, "After").Y, plain, 1);
        Assert.Equal(Word(unpositioned, "After").Y, Word(words, "After").Y, 1);
        Assert.Equal(Word(unpositioned, "Itemone").Y, item.Y, 1);
        Assert.True(Word(words, "Three").Y - item.Y > 40, "the list's top margin was lost");
    }

    /// <summary>
    /// A positioned element inside a positioned one: the inner is measured from the outer's padding box —
    /// 50pt in from its left and 30pt below its top — not from the page, and the outer's own blocks stay placed
    /// with it, nothing left in the flow, nothing reported.
    /// </summary>
    [Fact]
    public void APositionedElementInsideAPositionedOne_IsMeasuredFromIt()
    {
        var reports = new List<string>();
        var words = Words(
            "<p>Before</p>" +
            "<div style='position:absolute;left:100pt;top:200pt;width:200pt'>" +
            "<div>Outerone</div><div style='position:absolute;left:50pt;top:30pt'>Innerword</div><div>Outertwo</div></div>" +
            "<p>After</p>", reports);

        var outer = Word(words, "Outerone");
        var inner = Word(words, "Innerword");
        Assert.InRange(outer.X, 98, 102);
        Assert.Equal(outer.X + 50, inner.X, 0);
        Assert.Equal(outer.Y - 30, inner.Y, 0);
        Assert.InRange(Word(words, "Outertwo").X, 98, 102);
        Assert.True(Word(words, "Outertwo").Y < outer.Y, "the outer's second block is not under its first");
        Assert.DoesNotContain(reports, r => r.Contains("placed only the first", StringComparison.Ordinal));
        double without = Word(Words("<p>Before</p><p>After</p>"), "After").Y;
        Assert.InRange(Word(words, "After").Y, without - 1, without + 1);   // nothing left in the flow
    }

    /// <summary>A <c>fixed</c> block in a <c>position: relative</c> item is still placed: its containing block is the page, whatever its ancestors.</summary>
    [Fact]
    public void AFixedBlockInARelativeListItem_IsPlaced()
    {
        var words = Words(
            "<ul><li style='position:relative'>Item one<div style='position:fixed;left:300pt;top:100pt'>Stamp</div></li></ul>");

        Assert.InRange(Word(words, "Stamp").X, 298, 302);
    }

    /// <summary>
    /// A table after the first paragraph is placed with it (AbsolutePlacement.FollowingBlocks): drawn
    /// under the paragraph at the element's left edge, out of the flow, and not reported.
    /// </summary>
    [Fact]
    public void APositionedElementHoldingATableAfterItsFirstBlock_PlacesTheTable()
    {
        var reports = new List<string>();
        var words = Words(
            "<p>Before</p>" +
            "<div style='position:absolute;left:300pt;top:100pt;width:200pt'><p>Alpha</p><table><tr><td>Cell</td></tr></table><p>Omega</p></div>" +
            "<p>After</p>", reports);

        Assert.InRange(Word(words, "Alpha").X, 298, 302);
        Assert.InRange(Word(words, "Cell").X, 298, 312);
        Assert.True(Word(words, "Alpha").Y > Word(words, "Cell").Y && Word(words, "Cell").Y > Word(words, "Omega").Y,
            "the paragraph, the table and the paragraph after it are not stacked downwards in order");
        double without = Word(Words("<p>Before</p><p>After</p>"), "After").Y;
        Assert.InRange(Word(words, "After").Y, without - 1, without + 1);
        Assert.DoesNotContain(reports, r => r.Contains("placed only the first", StringComparison.Ordinal));
    }

    /// <summary>An element that opens with a table is placed too, by an anchor of no height: the table at its point, the paragraph after it below.</summary>
    [Fact]
    public void APositionedElementOpeningWithATable_IsPlaced()
    {
        var reports = new List<string>();
        var words = Words(
            "<div style='position:absolute;left:300pt;top:100pt;width:200pt'><table><tr><td>Cell</td></tr></table><p>Omega</p></div>", reports);

        Assert.InRange(Word(words, "Cell").X, 298, 312);
        Assert.True(Word(words, "Cell").Y > Word(words, "Omega").Y, "the paragraph is not under the table");
        Assert.DoesNotContain(reports, r => r.Contains("was laid out in the flow", StringComparison.Ordinal));
    }

    /// <summary>
    /// A floated paragraph among the blocks is placed with them and keeps its float — laid out as a
    /// cell's float is, at the element's edge — and nothing is reported.
    /// </summary>
    [Fact]
    public void APositionedElementHoldingAFloatAfterItsFirstBlock_PlacesItWithTheRest()
    {
        var reports = new List<string>();
        var words = Words(
            "<div style='position:absolute;left:300pt;top:100pt;width:250pt'><p>Alpha</p><p style='float:left;width:100pt'>Floaty</p><table><tr><td>Cell</td></tr></table></div>", reports);

        Assert.InRange(Word(words, "Alpha").X, 298, 302);
        Assert.InRange(Word(words, "Floaty").X, 298, 304);
        Assert.True(Word(words, "Cell").X >= 300, "the table after the float was not placed");
        Assert.DoesNotContain(reports, r => r.Contains("placed only the first", StringComparison.Ordinal));
    }

    /// <summary>A list after the first block is placed with it, under it at the element's edge, not left in the flow.</summary>
    [Fact]
    public void APositionedElementHoldingAListAfterItsFirstBlock_PlacesTheList()
    {
        var reports = new List<string>();
        var words = Words(
            "<p>Before</p><div style='position:absolute;left:300pt;top:100pt;width:250pt'><p>Alpha</p><ul><li>Listed</li></ul></div><p>After</p>", reports);

        Assert.InRange(Word(words, "Alpha").X, 298, 302);
        Assert.True(Word(words, "Listed").X >= 300, "the list was not placed");
        Assert.True(Word(words, "Listed").Y < Word(words, "Alpha").Y, "the list is not under the paragraph");
        double without = Word(Words("<p>Before</p><p>After</p>"), "After").Y;
        Assert.InRange(Word(words, "After").Y, without - 1, without + 1);
        Assert.DoesNotContain(reports, r => r.Contains("placed only the first", StringComparison.Ordinal));
    }

    /// <summary>An element opening with a list is placed by an anchor of no height, the list at the element's point.</summary>
    [Fact]
    public void APositionedElementOpeningWithAList_IsPlaced()
    {
        var reports = new List<string>();
        var words = Words("<div style='position:absolute;left:300pt;top:100pt;width:250pt'><ul><li>Listed</li></ul></div>", reports);
        Assert.True(Word(words, "Listed").X >= 300, "the list was not placed");
        Assert.DoesNotContain(reports, r => r.Contains("was laid out in the flow", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>fixed</c> element inside a positioned one is the page's, drawn on every page, not the outer's
    /// (APositionedElementInsideAPositionedOne_IsMeasuredFromIt has the outer's): placed on its own at its
    /// point on the page, and the outer's blocks stay placed together — the block after the fixed one under
    /// the one before it, nothing left in the flow, nothing reported.
    /// </summary>
    [Fact]
    public void APositionedElementHoldingAFixedOne_PlacesTheRestWithIt()
    {
        var reports = new List<string>();
        var words = Words(
            "<p>Before</p><div style='position:absolute;left:300pt;top:100pt'><p>Alpha</p><div style='position:fixed;left:50pt;top:400pt'>Inner</div><p>Omega</p></div><p>After</p>", reports);

        Assert.InRange(Word(words, "Alpha").X, 298, 302);
        Assert.InRange(Word(words, "Inner").X, 48, 52);
        Assert.InRange(Word(words, "Omega").X, 298, 302);
        Assert.True(Word(words, "Omega").Y < Word(words, "Alpha").Y, "the block after the fixed one is not under the one before it");
        Assert.DoesNotContain(reports, r => r.Contains("placed only the first", StringComparison.Ordinal));
        double without = Word(Words("<p>Before</p><p>After</p>"), "After").Y;
        Assert.InRange(Word(words, "After").Y, without - 1, without + 1);   // nothing left in the flow
    }

    // ------------------------------------------------------------------

    private static PdfTextWord Word(List<PdfTextWord> words, string text)
    {
        var hit = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(hit != null, $"'{text}' was not laid out at all");
        return hit!;
    }

    private static byte[] Convert(string html, List<string> reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null
                ? null
                : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });
        return output.ToArray();
    }

    /// <summary>
    /// The page's decoded content stream, for order-of-painting assertions. Decoded rather than
    /// read raw, because the exporter compresses and there is no option here to stop it.
    /// </summary>
    private static string ContentStreamOf(string html)
        => ManagedTestSupport.DecodeStreams(Convert(html));

    private static List<PdfTextWord> Words(string html, List<string> reports = null)
        => Pages(html, reports).SelectMany(p => p.Lines).SelectMany(l => l.Words).ToList();

    private static IReadOnlyList<PdfPageTextLayout> Pages(string html, List<string> reports = null)
    {
        byte[] pdf = Convert(html, reports);
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input);
    }
}
