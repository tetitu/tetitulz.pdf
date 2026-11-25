// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Explicit grid placement — <c>grid-column: 2</c>, <c>grid-row: 2</c>, <c>grid-column: 2 / 4</c>,
/// <c>grid-area</c> — applied through CSS Grid's own placement algorithm over an occupancy grid,
/// with a row span as a vertical merge.
/// </summary>
/// <remarks>
/// <para><b>Honouring one item's position means re-flowing every item after it.</b> That re-flow
/// is §8.5 of the Grid specification and it is a dozen lines over a grid of booleans. The table
/// the grid maps to carries column spans and vertical merges, so every cell the algorithm
/// produces has a place to go.</para>
///
/// <para><b>Measured glyph positions, for the same reason as every grid test here.</b> A placed
/// grid and a dealt grid are both valid PDFs full of the same words; only where a word landed
/// relative to its neighbours says which algorithm ran. Each case names the item that would sit
/// somewhere else under source-order dealing.</para>
/// </remarks>
public class GridPlacementTests
{
    /// <summary>
    /// An item naming its column goes there, and the automatic item after it does NOT backfill the
    /// cell it skipped — CSS's default flow is sparse.
    /// </summary>
    /// <remarks>
    /// Under source-order dealing AAA takes the first track and BBB the second, on one row. Placed,
    /// AAA is in the second track; the cursor is then past it, and §8.5's sparse flow never moves
    /// back, so BBB starts the next row in the first track. (A first version of this test expected
    /// BBB beside AAA — that is <c>grid-auto-flow: dense</c>, applied only when asked for, below.)
    /// </remarks>
    [Fact]
    public void AnItemNamingItsColumn_IsPlacedThereAndTheCursorDoesNotBackfill()
    {
        var words = Words(Grid(2, "<div style='grid-column:2'>AAA</div><div>BBB</div>"));
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(b.Y < a.Y - 2.0, $"BBB at y={b.Y:F1} did not start the row below AAA at y={a.Y:F1}");
        Assert.True(a.X > b.X + 10, $"AAA at x={a.X:F1} was not placed in the track right of BBB at x={b.X:F1}");
    }

    /// <summary>An item naming its row goes there; the automatic item takes the row above.</summary>
    /// <remarks>
    /// Dealt, AAA and BBB share row one. Placed, AAA is on row two and BBB — automatic, and placed
    /// by the cursor from the top-left — takes the first cell of row one, above it.
    /// </remarks>
    [Fact]
    public void AnItemNamingItsRow_IsPlacedThere()
    {
        var words = Words(Grid(2, "<div style='grid-row:2'>AAA</div><div>BBB</div>"));
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(b.Y > a.Y + 2.0, $"BBB at y={b.Y:F1} is not above AAA at y={a.Y:F1}");
        Assert.True(Math.Abs(a.X - b.X) < 2.0, $"AAA at x={a.X:F1} and BBB at x={b.X:F1} are not in one column");
    }

    /// <summary>
    /// An item naming a column BEHIND the cursor starts a new row — §8.5's "if this is less than
    /// the previous column position of the cursor, increment the row position".
    /// </summary>
    /// <remarks>
    /// The only case that separates this rule from the overlap check: AAA occupies the second
    /// track, so the first track of row one is free, and only the rule keeps BBB out of it.
    /// </remarks>
    [Fact]
    public void AnItemNamingAColumnBehindTheCursor_StartsANewRow()
    {
        var words = Words(Grid(2, "<div style='grid-column:2'>AAA</div><div style='grid-column:1'>BBB</div>"));
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(b.Y < a.Y - 2.0, $"BBB at y={b.Y:F1} backfilled the row of AAA at y={a.Y:F1}");
        Assert.True(b.X < a.X - 10, "BBB is not in the first track");
    }

    /// <summary>The register's own example: <c>2 / 4</c> spans two tracks AND starts at line 2.</summary>
    /// <remarks>
    /// Both halves in one assertion set. AAA starts where an unplaced second-track item starts —
    /// measured from a grid that puts one there by dealing, not from arithmetic here — and its two
    /// tracks fill the row, so BBB, automatic and behind the cursor, opens the next row at the
    /// first track. Dealt, AAA would sit in the first track with BBB beside it.
    /// </remarks>
    [Fact]
    public void ALinePair_PlacesAndSpans()
    {
        var placed = Words(Grid(3, "<div style='grid-column:2 / 4'>AAA</div><div>BBB</div>"));
        var a = Word(placed, "AAA");
        var b = Word(placed, "BBB");

        double secondTrack = Word(Words(Grid(3, "<div>XXX</div><div>YYY</div><div>ZZZ</div>")), "YYY").X;

        Assert.True(Math.Abs(a.X - secondTrack) < 2.0,
            $"AAA starts at x={a.X:F1}, not at the second track's x={secondTrack:F1}");
        Assert.True(b.Y < a.Y - 2.0, $"BBB at y={b.Y:F1} did not start the row below AAA at y={a.Y:F1}");
        Assert.True(b.X < a.X - 10, $"BBB at x={b.X:F1} is not in the first track left of AAA at x={a.X:F1}");
    }

    /// <summary>A backwards pair is swapped (§8.3): <c>3 / 1</c> is <c>1 / 3</c>.</summary>
    [Fact]
    public void ABackwardsLinePair_IsSwapped()
    {
        double backwards = Word(Words(Grid(4, "<div style='grid-column:3 / 1'>AAA</div><div>BBB</div>")), "BBB").X;
        double forwards = Word(Words(Grid(4, "<div style='grid-column:1 / 3'>AAA</div><div>BBB</div>")), "BBB").X;
        double unspanned = Word(Words(Grid(4, "<div>AAA</div><div>BBB</div>")), "BBB").X;

        Assert.True(Math.Abs(backwards - forwards) < 2.0,
            $"'3 / 1' put BBB at x={backwards:F1} where '1 / 3' puts it at x={forwards:F1}");
        Assert.True(backwards > unspanned + 1.0, "neither spelling spanned, so they agree for the wrong reason");
    }

    /// <summary>A row span merges cells downward, and the items after it flow around the merge.</summary>
    /// <remarks>
    /// Two tracks, three items. Dealt, CCC wraps under AAA. With AAA spanning two rows, the second
    /// row's first cell is AAA's continuation, so CCC lands under BBB instead — the only visible
    /// difference between a merge and no merge.
    /// </remarks>
    [Fact]
    public void ARowSpan_MergesDownwardAndLaterItemsFlowAroundIt()
    {
        var words = Words(Grid(2, "<div style='grid-row:span 2'>AAA</div><div>BBB</div><div>CCC</div>"));
        var b = Word(words, "BBB");
        var c = Word(words, "CCC");

        Assert.True(c.Y < b.Y - 2.0, $"CCC at y={c.Y:F1} is not below BBB at y={b.Y:F1}");
        Assert.True(Math.Abs(c.X - b.X) < 2.0,
            $"CCC at x={c.X:F1} is not under BBB at x={b.X:F1}; the row span did not hold its cell");
    }

    /// <summary>The model carries the merge as the vertical-merge pair the table renderer draws.</summary>
    /// <remarks>
    /// The liveness seam: a CCC under BBB could also come from an empty placeholder cell, and only
    /// the model says the cell under AAA is a <c>Continue</c> of a <c>Restart</c>.
    /// </remarks>
    [Fact]
    public void TheModelCarriesARowSpanAsAVerticalMerge()
    {
        Table table = HtmlToBuilderMapper.MapToModel(
            Grid(2, "<div style='grid-row:span 2'>AAA</div><div>BBB</div><div>CCC</div>"),
            new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed })
            .Sections[0].Children.OfType<Table>().Single();

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(VerticalMerge.Restart, table.Rows[0].Cells[0].VMerge);
        Assert.Equal(VerticalMerge.Continue, table.Rows[1].Cells[0].VMerge);
        Assert.Equal(VerticalMerge.None, table.Rows[1].Cells[1].VMerge);
    }

    /// <summary><c>grid-area</c> with four lines places both axes at once.</summary>
    [Fact]
    public void AGridArea_PlacesBothAxes()
    {
        var words = Words(Grid(3, "<div style='grid-area:2 / 2 / 3 / 4'>AAA</div><div>BBB</div>"));
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        double secondTrack = Word(Words(Grid(3, "<div>XXX</div><div>YYY</div><div>ZZZ</div>")), "YYY").X;

        Assert.True(b.Y > a.Y + 2.0, $"AAA at y={a.Y:F1} is not on the row below BBB at y={b.Y:F1}");
        Assert.True(Math.Abs(a.X - secondTrack) < 2.0, $"AAA at x={a.X:F1} is not at the second track's x={secondTrack:F1}");
    }

    /// <summary>Placement is applied, so it is no longer reported.</summary>
    [Theory]
    [InlineData("grid-column:2 / 4")]
    [InlineData("grid-row:2")]
    [InlineData("grid-area:2 / 1 / 3 / 3")]
    public void AnAppliedPlacement_IsNotReported(string style)
    {
        var reports = new List<string>();
        Convert(Grid(3, $"<div style='{style}'>AAA</div><div>BBB</div>"), reports);

        Assert.DoesNotContain(reports, r => r.Contains("grid-column", StringComparison.Ordinal)
                                         || r.Contains("grid-row", StringComparison.Ordinal)
                                         || r.Contains("grid-area", StringComparison.Ordinal));
    }

    /// <summary>What placement still cannot resolve is reported, and the item is placed automatically.</summary>
    /// <remarks>
    /// The acceptance control for the silence above: a named line no template defines, a row line
    /// counting back past the 1024 rows this lays out (-1030 with no row template), a column line
    /// counting back past the 64 columns (-70 on three tracks), an area's name with no template, and
    /// a line past the 64 columns each lose something, and each says so. The item is still on the
    /// page — automatic placement, not omission. (A line beyond the template is an implicit track
    /// since batch 112, and a line before the grid since batches 122 and 123:
    /// <c>GridImplicitTracksTests</c>, <c>GridLeadingImplicitTracksTests</c>, <c>GridLeadingImplicitRowsTests</c>.)
    /// </remarks>
    [Theory]
    [InlineData("grid-column:header-start / header-end", "grid-column")]
    [InlineData("grid-column:-70", "grid-column")]
    [InlineData("grid-row:-1030", "grid-row")]
    [InlineData("grid-area:sidebar", "grid-area")]
    [InlineData("grid-column:70", "grid-column")]
    public void AnUnresolvablePlacement_IsReportedAndTheItemStillPlaced(string style, string expected)
    {
        var reports = new List<string>();
        var words = Words(Grid(3, $"<div style='{style}'>AAA</div><div>BBB</div>"), reports);

        Word(words, "AAA");
        Assert.Contains(reports, r => r.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>grid-template-areas</c> (§7.3, batch 86): an item's <c>grid-area: name</c> is the area's
    /// four lines. Under <c>"head head" "side main"</c>, BBB (head) spans the first row, CCC (side) is
    /// at the first track of the second and AAA (main) at the second track of it — whatever order
    /// they are written in — and the area's span holds: head fills its row, so the unnamed DDD lands
    /// on a third row, below AAA, not beside BBB. Nothing is reported. Before batch 86 the names were
    /// reported and the items dealt in source order: AAA first, at the first track.
    /// </summary>
    [Fact]
    public void AGridAreaName_PlacesTheItemInItsTemplateArea()
    {
        var reports = new List<string>();
        var words = Words(Areas("\"head head\" \"side main\"",
            "<div style='grid-area:main'>AAA</div><div style='grid-area:head'>BBB</div><div style='grid-area:side'>CCC</div><div>DDD</div>"), reports);
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");
        var c = Word(words, "CCC");
        var d = Word(words, "DDD");
        double secondTrack = Word(Words(Grid(2, "<div>XXX</div><div>YYY</div>")), "YYY").X;

        Assert.True(b.Y > a.Y + 2.0, $"BBB (head) at y={b.Y:F1} is not on the row above AAA (main) at y={a.Y:F1}");
        Assert.True(Math.Abs(c.Y - a.Y) < 2.0, $"CCC (side) at y={c.Y:F1} is not on AAA's row at y={a.Y:F1}");
        Assert.True(Math.Abs(b.X - c.X) < 2.0, $"BBB (head) at x={b.X:F1} does not start at the first track like CCC at x={c.X:F1}");
        Assert.True(Math.Abs(a.X - secondTrack) < 2.0, $"AAA (main) at x={a.X:F1} is not at the second track's x={secondTrack:F1}");
        Assert.True(d.Y < a.Y - 2.0, $"DDD at y={d.Y:F1} is not on a row below AAA at y={a.Y:F1}: head did not span its row");
        Assert.Empty(reports);
    }

    /// <summary>
    /// A run of dots is a cell no area covers, not an area named "."; under <c>"head ." ". main"</c>
    /// the two null cells are no rectangle, and the template is still valid: head at the first cell,
    /// main at the second track of the second row, nothing reported.
    /// </summary>
    [Fact]
    public void NullCellsAreNoArea()
    {
        var reports = new List<string>();
        var words = Words(Areas("\"head .\" \". main\"", "<div style='grid-area:main'>AAA</div><div style='grid-area:head'>BBB</div>"), reports);
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");
        double secondTrack = Word(Words(Grid(2, "<div>XXX</div><div>YYY</div>")), "YYY").X;

        Assert.True(b.Y > a.Y + 2.0 && Math.Abs(a.X - secondTrack) < 2.0 && b.X < a.X - 2.0,
            $"AAA at ({a.X:F1}, {a.Y:F1}) and BBB at ({b.X:F1}, {b.Y:F1}) are not at main and head");
        Assert.Empty(reports);
    }

    /// <summary>
    /// The lines every area implies (§7.3.2): <c>main-start</c> / <c>main-end</c> on
    /// <c>grid-column</c> put AAA at the second track, and <c>side-start</c> on <c>grid-row</c> on
    /// the second row; nothing is reported. A name no area defines is still reported.
    /// </summary>
    [Fact]
    public void AnAreasImplicitLines_ResolveOnEitherAxis()
    {
        var reports = new List<string>();
        var words = Words(Areas("\"head head\" \"side main\"",
            "<div style='grid-column:main-start / main-end; grid-row:side-start'>AAA</div><div>BBB</div>"), reports);
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");
        double secondTrack = Word(Words(Grid(2, "<div>XXX</div><div>YYY</div>")), "YYY").X;

        Assert.True(Math.Abs(a.X - secondTrack) < 2.0, $"AAA at x={a.X:F1} is not at main's track x={secondTrack:F1}");
        Assert.True(b.Y > a.Y + 2.0, $"AAA at y={a.Y:F1} is not on side's row below BBB at y={b.Y:F1}");
        Assert.Empty(reports);

        var undefined = new List<string>();
        Words(Areas("\"head head\" \"side main\"", "<div style='grid-column:nav-start'>AAA</div><div>BBB</div>"), undefined);
        Assert.Contains(undefined, r => r.Contains("grid-column", StringComparison.Ordinal));
    }

    /// <summary>
    /// A template the standard calls invalid — a name that is not a rectangle, a row with other than
    /// the track count (implicit columns a table has not) — is not applied: the items are dealt as
    /// without one, and the template is reported with the reason.
    /// </summary>
    [Theory]
    [InlineData("\"a b\" \"b a\"", "not a rectangle")]
    [InlineData("\"a b c\"", "other than the template's 2 columns")]
    [InlineData("a b", "no strings")]
    public void AnInvalidTemplate_IsReportedAndTheItemsDealt(string template, string reason)
    {
        var reports = new List<string>();
        var words = Words(Areas(template, "<div style='grid-area:a'>AAA</div><div>BBB</div>"), reports);
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(b.X > a.X && Math.Abs(a.Y - b.Y) < 2.0, "the items were not dealt in source order");
        Assert.Contains(reports, r => r.Contains("grid-template-areas", StringComparison.Ordinal) && r.Contains(reason, StringComparison.Ordinal));
    }

    /// <summary>
    /// A negative line counts back from the explicit grid's end edge (§8.3): on three tracks
    /// <c>1 / -1</c> is <c>1 / 4</c>, the full-width span. AAA, after XXX, opens the second row at the
    /// first track and fills it, so BBB opens a third. Nothing is reported. It was reported as
    /// unresolvable and AAA placed automatically, one track wide, beside XXX.
    /// </summary>
    [Fact]
    public void AFullWidthSpan_CountsBackFromTheEndEdge()
    {
        var reports = new List<string>();
        var words = Words(Grid(3, "<div>XXX</div><div style='grid-column:1 / -1'>AAA</div><div>BBB</div>"), reports);
        var x = Word(words, "XXX");
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(a.Y < x.Y - 2.0 && Math.Abs(a.X - x.X) < 2.0, $"AAA at ({a.X:F1}, {a.Y:F1}) did not open the row below XXX at its track");
        Assert.True(b.Y < a.Y - 2.0, $"BBB at y={b.Y:F1} is not on a row below AAA at y={a.Y:F1}: AAA did not span its row");
        Assert.Empty(reports);

        var spelled = Words(Grid(3, "<div>XXX</div><div style='grid-column:1 / 4'>AAA</div><div>BBB</div>"));
        Assert.True(Math.Abs(Word(spelled, "BBB").Y - b.Y) < 0.5, "'1 / -1' and '1 / 4' disagree");
    }

    /// <summary>
    /// On the row axis the explicit grid is <c>grid-template-areas</c>' rows: under two template rows
    /// <c>grid-row: -2</c> is line 2, so AAA takes the second row and the automatic BBB the first,
    /// above it, nothing reported. Under a <c>grid-template-rows</c> this cannot read (a
    /// <c>clamp()</c> track; <c>minmax()</c> reads since batch 102) the explicit row count is not
    /// known, so even <c>-1</c> — line 1 of a grid with no rows — is reported and AAA placed
    /// automatically, first.
    /// </summary>
    [Fact]
    public void ANegativeRowLine_CountsBackFromTheTemplatesRows()
    {
        var reports = new List<string>();
        var words = Words(Areas("\"a a\" \"b c\"", "<div style='grid-row:-2'>AAA</div><div>BBB</div>"), reports);
        Assert.True(Word(words, "BBB").Y > Word(words, "AAA").Y + 2.0, "AAA is not on the second row, below BBB");
        Assert.Empty(reports);

        var unknown = new List<string>();
        var dealt = Words("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:clamp(20px, 5%, 100px) auto'>" +
                          "<div style='grid-row:-1'>AAA</div><div>BBB</div></div>", unknown);
        Assert.True(Math.Abs(Word(dealt, "AAA").Y - Word(dealt, "BBB").Y) < 2.0, "AAA was placed though the explicit rows are not known");
        Assert.Contains(unknown, r => r.Contains("grid-row: a negative line", StringComparison.Ordinal) && r.Contains("does not define", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>grid-auto-flow: dense</c> (§8.5): the cursor goes back to the grid's start for every item
    /// placed automatically, so a later item fills a hole an earlier one left. On three tracks BBB
    /// spans all three and cannot sit beside AAA, so it opens row two; sparse, CCC follows it onto
    /// row three, dense it fills the hole beside AAA. Nothing is reported, for <c>dense</c> or
    /// <c>row dense</c>.
    /// </summary>
    [Theory]
    [InlineData("dense")]
    [InlineData("row dense")]
    public void DenseFlow_FillsTheHoleAnEarlierItemLeft(string flow)
    {
        const string Items = "<div>AAA</div><div style='grid-column:span 3'>BBB</div><div>CCC</div>";
        var reports = new List<string>();
        var dense = Words(Flowed(flow, Items), reports);
        var a = Word(dense, "AAA");
        var b = Word(dense, "BBB");
        var c = Word(dense, "CCC");

        Assert.True(Math.Abs(c.Y - a.Y) < 2.0 && c.X > a.X + 10, $"CCC at ({c.X:F1}, {c.Y:F1}) did not fill the hole beside AAA at ({a.X:F1}, {a.Y:F1})");
        Assert.True(b.Y < a.Y - 2.0, "BBB did not open the second row");
        Assert.Empty(reports);

        var sparse = Words(Grid(3, Items));
        Assert.True(Word(sparse, "CCC").Y < Word(sparse, "BBB").Y - 2.0, "the sparse control did not put CCC after BBB");
    }

    /// <summary>
    /// An item with a definite column is packed densely too: AAA is in the second track, and BBB,
    /// automatic, goes back to the first track of the first row — sparse, it opens the row below.
    /// </summary>
    [Fact]
    public void DenseFlow_BackfillsBeforeADefinitelyPlacedItem()
    {
        var words = Words(Flowed("dense", "<div style='grid-column:2'>AAA</div><div>BBB</div>", tracks: 2));
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(Math.Abs(a.Y - b.Y) < 2.0 && b.X < a.X - 10, $"BBB at ({b.X:F1}, {b.Y:F1}) did not backfill the first track beside AAA");
    }

    /// <summary>
    /// <c>column dense</c> with no row template is column flow down one implicit row (batch 112):
    /// the two items side by side on one row, nothing reported. Before batch 112 it was reported and
    /// the items packed densely in rows.
    /// </summary>
    [Fact]
    public void ColumnFlowDense_WithNoRows_PlacesTheItemsAlongOneRow()
    {
        var reports = new List<string>();
        var words = Words(Flowed("column dense", "<div>AAA</div><div>BBB</div>"), reports);
        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(Math.Abs(a.Y - b.Y) < 2.0 && b.X > a.X + 10, $"BBB at ({b.X:F1}, {b.Y:F1}) is not beside AAA at ({a.X:F1}, {a.Y:F1})");
        Assert.DoesNotContain(reports, r => r.Contains("grid-auto-flow", StringComparison.Ordinal));
    }

    private static string Flowed(string flow, string items, int tracks = 3)
        => "<div style='display:grid;grid-auto-flow:" + flow + ";grid-template-columns:" +
           string.Join(" ", Enumerable.Repeat("1fr", tracks)) + "'>" + items + "</div>";

    /// <summary>
    /// <c>grid-template-rows</c> (§7.2): a fixed track is the row's height — at least — so under
    /// <c>100px auto</c> the second row starts 75pt below the first, where it sat one line below; an
    /// <c>auto</c> or <c>fr</c> track is sized by its content, as before. The fixed track's being a
    /// least height is reported; a template of content-sized tracks reports nothing.
    /// </summary>
    [Fact]
    public void AFixedRowTrack_IsTheRowsHeight()
    {
        const string Items = "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div>";
        var reports = new List<string>();
        var fixedRows = Words(Rows("100px auto", Items), reports);
        double drop = Word(fixedRows, "AAA").Y - Word(fixedRows, "CCC").Y;
        Assert.InRange(drop, 73, 78);
        Assert.Contains(reports, r => r.Contains("least height", StringComparison.Ordinal));

        var autoReports = new List<string>();
        var autoRows = Words(Rows("auto 1fr", Items), autoReports);
        double natural = Word(autoRows, "AAA").Y - Word(autoRows, "CCC").Y;
        Assert.InRange(natural, 5, 30);
        Assert.DoesNotContain(autoReports, r => r.Contains("grid-template-rows", StringComparison.Ordinal));
    }

    /// <summary>
    /// The row template's tracks are explicit rows: under <c>50px 50px</c>, <c>grid-row: -2</c> is
    /// line 2, so AAA takes the second row and BBB the first. Without a row template it would count
    /// back past the start, and be reported.
    /// </summary>
    [Fact]
    public void TheRowTemplatesTracks_AreExplicitRows()
    {
        var reports = new List<string>();
        var words = Words(Rows("50px 50px", "<div style='grid-row:-2'>AAA</div><div>BBB</div>"), reports);

        Assert.True(Word(words, "BBB").Y > Word(words, "AAA").Y + 2.0, "AAA is not on the second row, below BBB");
        Assert.DoesNotContain(reports, r => r.Contains("grid-row", StringComparison.Ordinal));
    }

    /// <summary>
    /// A track that is a function this engine does not read (<c>clamp()</c>; <c>minmax()</c> and
    /// <c>fit-content()</c> read since batch 102) leaves the template unapplied, reported, and the
    /// rows sized by content.
    /// </summary>
    [Fact]
    public void AFunctionRowTrack_IsReportedAndNotApplied()
    {
        var reports = new List<string>();
        var words = Words(Rows("clamp(100px, 5%, 200px) auto", "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div>"), reports);

        Assert.InRange(Word(words, "AAA").Y - Word(words, "CCC").Y, 5, 30);
        Assert.Contains(reports, r => r.Contains("grid-template-rows: clamp(100px, 5%, 200px) auto is not applied", StringComparison.Ordinal));
    }

    private static string Rows(string template, string items)
        => "<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:" + template + "'>" + items + "</div>";

    private static string Areas(string template, string items)
        => "<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-areas:" + template + "'>" + items + "</div>";

    /// <summary>
    /// Adversarial: a row line is author-supplied and sizes the occupancy grid and the table.
    /// <c>grid-row: 1000000</c> is bounded — asserted on the guard's own evidence, a report and a
    /// converted page with the item on it, not on a wall clock.
    /// </summary>
    [Fact]
    public void AnOversizedRowLine_IsBoundedAndSaysSo()
    {
        var reports = new List<string>();
        var words = Words(Grid(2, "<div style='grid-row:1000000'>AAA</div><div>BBB</div>"), reports);

        var a = Word(words, "AAA");
        var b = Word(words, "BBB");
        Assert.True(Math.Abs(a.Y - b.Y) < 2.0, "the oversized row line was honoured rather than bounded");
        Assert.Contains(reports, r => r.Contains("grid-row", StringComparison.Ordinal));
    }

    /// <summary>Two items placed on the same cells cannot overlap in a table; the later moves down and says so.</summary>
    [Fact]
    public void TwoItemsPlacedOnOneCell_DoNotOverlapAndSaySo()
    {
        var reports = new List<string>();
        var words = Words(Grid(2, "<div style='grid-area:1 / 1 / 2 / 2'>AAA</div>" +
                                  "<div style='grid-area:1 / 1 / 2 / 2'>BBB</div>"), reports);

        var a = Word(words, "AAA");
        var b = Word(words, "BBB");
        Assert.True(b.Y < a.Y - 2.0, $"BBB at y={b.Y:F1} was not moved below AAA at y={a.Y:F1}");
        Assert.Contains(reports, r => r.Contains("same cells", StringComparison.Ordinal));
    }

    // ==================================================================

    private static string Grid(int tracks, string items)
        => "<div style='display:grid;grid-template-columns:" +
           string.Join(" ", Enumerable.Repeat("1fr", tracks)) + "'>" + items + "</div>";

    private static PdfTextWord Word(List<PdfTextWord> words, string text)
    {
        var hit = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(hit != null, $"'{text}' was not laid out at all");
        return hit!;
    }

    private static byte[] Convert(string html, List<string>? reports)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null
                ? null
                : d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) lock (reports) reports.Add(d.Detail ?? ""); },
        });
        return output.ToArray();
    }

    private static List<PdfTextWord> Words(string html, List<string>? reports = null)
    {
        using var input = new MemoryStream(Convert(html, reports), writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
