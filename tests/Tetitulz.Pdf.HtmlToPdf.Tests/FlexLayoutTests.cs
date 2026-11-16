// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>display: flex</c> in the managed engine: a flex <b>row</b> places its items side by side.
/// </summary>
/// <remarks>
/// <para><b>The assertion is on measured glyph positions</b>, read back from the produced PDF —
/// specifically, whether two items share a baseline (side by side) or sit at different heights
/// (stacked). Nothing else can tell the two apart: before this, a flex container fell through to
/// ordinary block flow and produced a perfectly valid PDF with the items stacked, so every
/// structural assertion — element present, text present, page non-empty — passed.</para>
/// <para><b>A flex column needs the opposite assertion</b>, and it is here for the same reason: it
/// is the case that was <i>already</i> correct, so a change that broke it would otherwise be
/// invisible.</para>
/// </remarks>
public class FlexLayoutTests
{
    // The abutting fixtures carry a column-gap: since batch 121 auto items keep their content widths,
    // so two gapless items print as one word ("AAABBB") and structured extraction reads them as one.
    [Fact]
    public void FlexRow_PlacesItemsSideBySide_NotStacked()
    {
        var (left, right) = TwoItemPositions(
            "<div style='display:flex;column-gap:10px'><div>AAA</div><div>BBB</div></div>");

        // Same line: their vertical positions match. This is the whole claim.
        Assert.True(Math.Abs(left.Y - right.Y) < 2.0,
            $"items are at y={left.Y:F1} and y={right.Y:F1}, so they were stacked, not laid side by side");
        Assert.True(right.X > left.X,
            $"second item is at x={right.X:F1}, not to the right of the first at x={left.X:F1}");
    }

    [Fact]
    public void FlexColumn_KeepsItemsStacked()
    {
        var (first, second) = TwoItemPositions(
            "<div style='display:flex;flex-direction:column'><div>AAA</div><div>BBB</div></div>");

        Assert.True(Math.Abs(first.Y - second.Y) > 2.0,
            $"column items are both at y={first.Y:F1}, so they were laid side by side");
    }

    [Fact]
    public void FlexRowReverse_PutsTheFirstItemOnTheRight()
    {
        var (a, b) = TwoItemPositions(
            "<div style='display:flex;flex-direction:row-reverse;column-gap:10px'><div>AAA</div><div>BBB</div></div>");

        // Still one line...
        Assert.True(Math.Abs(a.Y - b.Y) < 2.0, "row-reverse stacked its items");
        // ...but "AAA", found first in the text, now sits to the RIGHT of "BBB".
        Assert.True(a.X > b.X,
            $"AAA is at x={a.X:F1} and BBB at x={b.X:F1}; row-reverse did not reverse the order");
    }

    /// <summary>
    /// A flex item that is itself a block container keeps its children — the mapping must recurse,
    /// not flatten. A cell holds arbitrary blocks, so this should compose for free; it is asserted
    /// because "should compose for free" is how silent content loss ships.
    /// </summary>
    [Fact]
    public void FlexItem_WithNestedBlocks_KeepsAllOfItsContent()
    {
        string text = AllText(
            "<div style='display:flex'>" +
            "<div><p>One</p><p>Two</p></div>" +
            "<div><p>Three</p></div>" +
            "</div>");

        Assert.Contains("One", text, StringComparison.Ordinal);
        Assert.Contains("Two", text, StringComparison.Ordinal);
        Assert.Contains("Three", text, StringComparison.Ordinal);
    }

    /// <summary>An empty flex item still occupies its track, or the items after it shift left.</summary>
    [Fact]
    public void FlexRow_WithAnEmptyItem_KeepsTheFollowingItemsInTheirTracks()
    {
        var (a, b) = TwoItemPositions(
            "<div style='display:flex'><div>AAA</div><div></div><div>BBB</div></div>");

        Assert.True(Math.Abs(a.Y - b.Y) < 2.0, "the empty item broke the row");
        // With three equal tracks, BBB starts in the last third -- clearly past the midpoint.
        Assert.True(b.X > a.X, $"BBB at x={b.X:F1} did not follow AAA at x={a.X:F1}");
    }

    /// <summary>
    /// <c>order</c> (CSS Flexbox §5.4.1, batch 85) lays the items out by ascending order — AAA at
    /// order 2 sits to the right of BBB at order 1 — with ties in document order, and nothing is
    /// reported. Before batch 85 the document order was kept and the property reported.
    /// </summary>
    [Fact]
    public void FlexOrder_LaysItemsOutByAscendingOrder_TiesInDocumentOrder()
    {
        var ordered = Layout("<div style='display:flex;column-gap:10px'><div style='order:2'>AAA</div><div style='order:1'>BBB</div></div>");
        var tied = Layout("<div style='display:flex;column-gap:10px'><div style='order:1'>AAA</div><div style='order:1'>BBB</div></div>");

        Assert.True(Math.Abs(ordered.A.Y - ordered.B.Y) < 2.0, "order broke the row");
        Assert.True(ordered.A.X > ordered.B.X, $"AAA (order 2) at x={ordered.A.X:F1} is not to the right of BBB (order 1) at x={ordered.B.X:F1}");
        Assert.True(tied.A.X < tied.B.X, "a tie did not keep the document order");
        Assert.DoesNotContain(ordered.Reports, r => r.Contains("order", StringComparison.Ordinal));
    }

    /// <summary>
    /// The order applies in a column — AAA at order 2 below BBB — and before a reversal: in a
    /// <c>row-reverse</c> the ordered sequence BBB, AAA is reversed, so AAA is on the left. A value
    /// that is not an integer is 0, the initial value, so the document order holds.
    /// </summary>
    [Fact]
    public void FlexOrder_AppliesInAColumn_BeforeAReversal_AndANonIntegerIsZero()
    {
        var column = Layout("<div style='display:flex;flex-direction:column'><div style='order:2'>AAA</div><div style='order:1'>BBB</div></div>");
        var reversed = Layout("<div style='display:flex;flex-direction:row-reverse;column-gap:10px'><div style='order:2'>AAA</div><div style='order:1'>BBB</div></div>");
        var invalid = Layout("<div style='display:flex;column-gap:10px'><div style='order:abc'>AAA</div><div style='order:1'>BBB</div></div>");
        var negative = Layout("<div style='display:flex;column-gap:10px'><div>AAA</div><div style='order:-1'>BBB</div></div>");

        Assert.True(column.A.Y < column.B.Y - 2.0, $"in a column AAA (order 2) at y={column.A.Y:F1} is not below BBB (order 1) at y={column.B.Y:F1}");
        Assert.True(reversed.A.X < reversed.B.X, $"in a row-reverse AAA (order 2) at x={reversed.A.X:F1} is not to the left of BBB (order 1) at x={reversed.B.X:F1}");
        Assert.True(invalid.A.X < invalid.B.X, "an invalid order did not count as 0, before 1");
        Assert.True(negative.A.X > negative.B.X, "a negative order did not come before the initial 0");
    }

    /// <summary>
    /// <c>justify-content</c> (§8.2, batch 85) places the line's free space when every item has a
    /// fixed width and none grows: two 100px items leave L free, and <c>flex-end</c> moves both by L,
    /// <c>center</c> by L/2, <c>space-between</c> the second alone by L, <c>space-around</c> the first
    /// by L/4 and the second by 3L/4, <c>space-evenly</c> by L/3 and 2L/3 — through empty filler
    /// cells of those widths, since the table normalises an all-explicit column set to fill. Nothing
    /// is reported. Before batch 85 every value stretched the items across the width, reported.
    /// </summary>
    [Theory]
    [InlineData("flex-end", 1.0, 1.0)]
    [InlineData("center", 0.5, 0.5)]
    [InlineData("space-between", 0.0, 1.0)]
    [InlineData("space-around", 0.25, 0.75)]
    [InlineData("space-evenly", 1.0 / 3, 2.0 / 3)]
    public void JustifyContent_PlacesTheFreeSpace(string justify, double firstShare, double secondShare)
    {
        var start = Layout(FixedRow("flex-start"));
        var end = Layout(FixedRow("flex-end"));
        var placed = Layout(FixedRow(justify));

        double free = end.A.X - start.A.X;
        Assert.True(free > 100, $"the free space measured {free:F1}pt; the items were not kept at their widths");
        Assert.Equal(free, end.B.X - start.B.X, 1);
        Assert.Equal(start.A.X + firstShare * free, placed.A.X, 1);
        Assert.Equal(start.B.X + secondShare * free, placed.B.X, 1);
        Assert.True(Math.Abs(placed.A.Y - placed.B.Y) < 2.0, "the fillers broke the row");
        Assert.DoesNotContain(placed.Reports, r => r.StartsWith("justify-content", StringComparison.Ordinal));
    }

    /// <summary>
    /// Fixed-width items keep their widths at <c>flex-start</c> too: 100px items are 75pt apart, with
    /// the free space after them. Before batch 85 the table stretched the two across the width.
    /// </summary>
    [Fact]
    public void JustifyContent_FlexStart_KeepsTheItemsTheirWidths()
    {
        var start = Layout(FixedRow("flex-start"));
        var none = Layout("<div style='display:flex'><div style='width:100px'>AAA</div><div style='width:100px'>BBB</div></div>");

        Assert.Equal(75, start.B.X - start.A.X, 1);
        Assert.Equal(75, none.B.X - none.A.X, 1);
        Assert.Empty(start.Reports);
    }

    /// <summary>
    /// A <c>row-reverse</c>'s main-start is its right edge: at <c>flex-start</c> the items pack
    /// right — BBB, first in the reversed row, where the flex-end layout puts the first item — and
    /// at <c>flex-end</c> they pack left.
    /// </summary>
    [Fact]
    public void JustifyContent_InARowReverse_StartsAtTheRight()
    {
        var start = Layout(FixedRow("flex-start"));
        var end = Layout(FixedRow("flex-end"));
        var reversedStart = Layout(FixedRow("flex-start", "row-reverse"));
        var reversedEnd = Layout(FixedRow("flex-end", "row-reverse"));

        Assert.Equal(end.A.X, reversedStart.B.X, 1);
        Assert.Equal(end.B.X, reversedStart.A.X, 1);
        Assert.Equal(start.A.X, reversedEnd.B.X, 1);
        Assert.Equal(start.B.X, reversedEnd.A.X, 1);
    }

    /// <summary>
    /// With no free space to place — an item whose width cannot be measured (a table nested past the
    /// layout's cap, written <c>{CAP}</c>), or one that grows — or a value this engine does not place,
    /// the items are distributed across the width as before and the value is reported, saying which.
    /// (Auto items of measurable width are placed since batch 121: <c>FlexJustifyAutoTests</c>.)
    /// </summary>
    [Theory]
    [InlineData("<div style='display:flex;justify-content:center;column-gap:10px'><div>AAA{CAP}</div><div>BBB</div></div>", "no measurable width")]
    [InlineData("<div style='display:flex;justify-content:center'><div style='width:100px;flex-grow:1'>AAA</div><div style='width:100px'>BBB</div></div>", "grows")]
    [InlineData("<div style='display:flex;justify-content:left'><div style='width:100px'>AAA</div><div style='width:100px'>BBB</div></div>", "not one this engine places")]
    public void JustifyContent_WithoutFreeSpaceOrAnUnplacedValue_IsReported(string html, string reason)
    {
        var laid = Layout(html.Replace("{CAP}", ManagedTestSupport.TablesPastTheNestingCap("")));

        string report = Assert.Single(laid.Reports, r => r.StartsWith("justify-content", StringComparison.Ordinal));
        Assert.Contains(reason, report, StringComparison.Ordinal);
        // One row, the two words within a few points of one height.
        Assert.True(laid.A.X < laid.B.X && Math.Abs(laid.A.Y - laid.B.Y) < 8.0, $"the row was not laid out: A ({laid.A.X:F1}, {laid.A.Y:F1}), B ({laid.B.X:F1}, {laid.B.Y:F1})");
    }

    private static string FixedRow(string justify, string direction = "row")
        => $"<div style='display:flex;flex-direction:{direction};justify-content:{justify}'><div style='width:100px'>AAA</div><div style='width:100px'>BBB</div></div>";

    // ------------------------------------------------------------------

    private static ((double X, double Y) A, (double X, double Y) B, System.Collections.Generic.List<string> Reports) Layout(string html)
    {
        var reports = new System.Collections.Generic.List<string>();
        var words = Words(html, d => { lock (reports) reports.Add(d.Detail ?? ""); });
        var a = words.FirstOrDefault(w => w.Text.Contains("AAA", StringComparison.Ordinal));
        var b = words.FirstOrDefault(w => w.Text.Contains("BBB", StringComparison.Ordinal));
        Assert.True(a != null && b != null, "AAA or BBB was not laid out at all");
        return ((a!.X, a.Y), (b!.X, b.Y), reports);
    }

    private static ((double X, double Y) First, (double X, double Y) Second) TwoItemPositions(string html)
    {
        var words = Words(html);
        var a = words.FirstOrDefault(w => w.Text.Contains("AAA", StringComparison.Ordinal));
        var b = words.FirstOrDefault(w => w.Text.Contains("BBB", StringComparison.Ordinal));

        Assert.True(a != null, "AAA was not laid out at all");
        Assert.True(b != null, "BBB was not laid out at all");

        return ((a!.X, a.Y), (b!.X, b.Y));
    }

    private static string AllText(string html) =>
        string.Concat(Words(html).Select(w => w.Text));

    private static System.Collections.Generic.List<PdfTextWord> Words(string html, Action<Builder.Export.BuilderDiagnostic>? onDiagnostic = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output,
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed, OnDiagnostic = onDiagnostic });

        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
