// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The cells a table's grid holds and the walk across its rows are bounded by the cell budget, and the
/// grid keeps the columns a browser draws. Every cell the grid materialises — a mapped one, a rowspan's
/// continuation, the fill of a short row or of a gap — is charged to
/// <see cref="HtmlToBuilderMapper.MaxTableCells"/>; under automatic layout a column no cell starts in is
/// no width and is not kept; a row reaches a far rowspan in one step.
/// </summary>
/// <remarks>
/// A row of a hundred <c>colspan="1000"</c> cells over fifty thousand one-cell rows padded five billion
/// cells, the fill charged to nothing: 3.9 KB of markup allocated 8.4 GB and wrote a 92 MB file
/// (2026-10-22). And <c>&lt;td colspan="100"&gt;</c> across a two-column table — the "span every column"
/// idiom — drew ninety-eight empty boxes beside the two real columns; at 1000 the real ones were slivers.
/// Chrome draws both as two columns.
/// </remarks>
[Collection("HtmlMapperLimits")]
public class TableGridBoundsTests
{
    private static Document Model(string html, List<string>? reports = null) =>
        HtmlToBuilderMapper.MapToModel(html, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null ? null : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });

    private static Table TableOf(Document model) => model.Sections[0].Children.OfType<Table>().Single();

    private static int Cells(Table t) => t.Rows.Sum(r => r.Cells.Count);

    private static int Columns(TableRow row) => row.Cells.Sum(c => Math.Max(1, c.GridSpan));

    private static string Text(TableCell cell) => string.Concat(
        cell.Blocks.OfType<Paragraph>().SelectMany(p => p.Runs).OfType<TextRun>().Select(r => r.Text));

    private static List<string> BudgetReports(List<string> reports) =>
        reports.FindAll(r => r.Contains("cell mapping budget", StringComparison.Ordinal));

    // ------------------------------------------------------------------ the columns kept

    /// <summary>
    /// <c>colspan="100"</c> across a two-column table is two columns wide: under automatic layout a
    /// column is as wide as the cells that start in it (CSS 2.1 §17.5.2.2), and Chrome draws two. The
    /// other ninety-eight were kept and drawn as empty boxes.
    /// </summary>
    [Theory]
    [InlineData("100")]
    [InlineData("1000")]
    public void TheSpanEveryColumnIdiom_IsAsWideAsTheColumnsCellsStartIn(string span)
    {
        Table t = TableOf(Model("<table border=\"1\" width=\"100%\"><tr><td>alpha</td><td>beta</td></tr>" +
                                "<tr><td colspan=\"" + span + "\">footer</td></tr></table>"));

        Assert.Equal(new[] { "alpha", "beta" }, t.Rows[0].Cells.Select(Text));
        TableCell footer = Assert.Single(t.Rows[1].Cells);
        Assert.Equal(2, footer.GridSpan);
        Assert.Equal("footer", Text(footer));
    }

    /// <summary>
    /// The control: under <c>table-layout: fixed</c> the columns are the table's own and keep their widths
    /// whether a cell starts in them or not — Chrome draws ten, a tenth of the table each.
    /// </summary>
    [Fact]
    public void UnderFixedLayout_EveryColumnStays()
    {
        Table t = TableOf(Model("<table style=\"table-layout:fixed\"><tr><td>alpha</td><td>beta</td></tr>" +
                                "<tr><td colspan=\"10\">footer</td></tr></table>"));

        Assert.Equal(10, Columns(t.Rows[0]));
        Assert.Equal(10, Assert.Single(t.Rows[1].Cells).GridSpan);
    }

    /// <summary>
    /// A column a <c>&lt;col&gt;</c> gives a width is kept though no cell starts in it, as a browser keeps
    /// it; the widths follow the columns kept — the middle column, which neither a cell nor a width
    /// keeps, takes its width with it, so the third column's 300px is the second width.
    /// </summary>
    [Fact]
    public void TheColumnsAColElementSizes_AreKept_AndTheirWidthsFollowThem()
    {
        Table kept = TableOf(Model("<table><col width=\"50\"><col width=\"200\">" +
                                   "<tr><td colspan=\"2\">wide</td></tr></table>"));
        Assert.Equal(2, Assert.Single(kept.Rows[0].Cells).GridSpan);

        Table widths = TableOf(Model("<table><col width=\"100\"><col><col width=\"300\">" +
                                     "<tr><td colspan=\"2\">a</td><td>b</td></tr></table>"));
        Assert.Equal(new[] { 1, 1 }, widths.Rows[0].Cells.Select(c => c.GridSpan));
        Assert.Equal(new[] { 75.0, 225.0 }, widths.ColumnWidths);
    }

    /// <summary>
    /// Column widths are read only as far as the widest table is laid out: twenty thousand
    /// <c>&lt;col span="1000"&gt;</c> declared twenty million widths, a list of 160 MB before a cell was
    /// mapped. The evidence is what the conversion allocates on this thread, which the parsed markup's
    /// own share keeps well under the list's.
    /// </summary>
    [Fact]
    public void AdversarialColElementsPastTheCap_AreNotRead()
    {
        var sb = new StringBuilder("<table><colgroup>");
        for (int i = 0; i < 20_000; i++) sb.Append("<col span=\"1000\" width=\"10\">");
        sb.Append("</colgroup><tr><td>a</td></tr></table>");
        string html = sb.ToString();

        long before = GC.GetAllocatedBytesForCurrentThread();
        Table t = TableOf(Model(html));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("a", Text(Assert.Single(t.Rows[0].Cells)));
        Assert.InRange(allocated, 1, 100L * 1024 * 1024);
    }

    /// <summary>The control: with no <c>&lt;col&gt;</c> width a span past the cells is one column.</summary>
    [Fact]
    public void ASpanNoOtherCellStartsWithin_IsOneColumn()
    {
        Table t = TableOf(Model("<table><tr><td colspan=\"2\">wide</td></tr></table>"));

        Assert.Equal(1, Assert.Single(t.Rows[0].Cells).GridSpan);
    }

    /// <summary>
    /// A row that ends short of a rowspan further right reaches it across a gap the grid fills, so the
    /// continuation lies under the cell it continues. It sat straight after the row's own cells, a
    /// column to the left, merging with the wrong cell above.
    /// </summary>
    [Fact]
    public void ARowShortOfAFarRowspan_IsFilledUpToIt()
    {
        Table t = TableOf(Model("<table><tr><td>a</td><td>b</td><td rowspan=\"2\">c</td></tr>" +
                                "<tr><td>d</td></tr></table>"));

        var second = t.Rows[1].Cells;
        Assert.Equal(3, second.Count);
        Assert.Equal("d", Text(second[0]));
        Assert.Equal(VerticalMerge.None, second[1].VMerge);
        Assert.Equal(VerticalMerge.Continue, second[2].VMerge);
        Assert.Equal(VerticalMerge.Restart, t.Rows[0].Cells[2].VMerge);
    }

    // ------------------------------------------------------------------ the cell budget

    /// <summary>
    /// The fill of short rows is charged to the cell budget and stops when it is spent, reported once;
    /// every row is still mapped. Fixed layout keeps the thousand columns the first row spans, so each
    /// of the three hundred rows below asked for 999 fill cells.
    /// </summary>
    [Fact]
    public void AdversarialFillOfShortRows_IsChargedToTheCellBudget()
    {
        int saved = HtmlToBuilderMapper.MaxTableCells;
        try
        {
            HtmlToBuilderMapper.MaxTableCells = 2000;
            var sb = new StringBuilder("<table style=\"table-layout:fixed\"><tr><td colspan=\"1000\">wide</td></tr>");
            for (int r = 0; r < 300; r++) sb.Append("<tr><td>n").Append(r).Append("</td></tr>");
            sb.Append("</table>");

            var reports = new List<string>();
            Table t = TableOf(Model(sb.ToString(), reports));

            Assert.Equal(301, t.Rows.Count);
            Assert.Equal("n299", Text(t.Rows[300].Cells[0]));
            Assert.InRange(Cells(t), 301, HtmlToBuilderMapper.MaxTableCells);
            Assert.Single(BudgetReports(reports));
        }
        finally { HtmlToBuilderMapper.MaxTableCells = saved; }
    }

    /// <summary>The control: a ragged table under the budget is filled whole, and nothing is reported.</summary>
    [Fact]
    public void TheFillOfARaggedTableUnderTheBudget_IsWhole()
    {
        var reports = new List<string>();
        Table t = TableOf(Model("<table><tr><td>a</td><td>b</td><td>c</td></tr><tr><td>d</td></tr>" +
                                "<tr><td>e</td><td>f</td></tr></table>", reports));

        Assert.All(t.Rows, r => Assert.Equal(3, Columns(r)));
        Assert.Empty(BudgetReports(reports));
    }

    /// <summary>
    /// A rowspan's continuation is a cell the grid holds and is charged as one: twenty cells spanning
    /// every row below them over two hundred empty rows asked for four thousand.
    /// </summary>
    [Fact]
    public void AdversarialRowspanContinuations_AreChargedToTheCellBudget()
    {
        int saved = HtmlToBuilderMapper.MaxTableCells;
        try
        {
            HtmlToBuilderMapper.MaxTableCells = 500;
            var sb = new StringBuilder("<table><tr>");
            for (int c = 0; c < 20; c++) sb.Append("<td rowspan=\"65534\">r").Append(c).Append("</td>");
            sb.Append("</tr>");
            for (int r = 0; r < 200; r++) sb.Append("<tr></tr>");
            sb.Append("</table>");

            var reports = new List<string>();
            Table t = TableOf(Model(sb.ToString(), reports));

            Assert.Equal(20, t.Rows[0].Cells.Count);
            Assert.Equal(VerticalMerge.Continue, t.Rows[1].Cells[0].VMerge);
            Assert.InRange(Cells(t), 21, HtmlToBuilderMapper.MaxTableCells);
            Assert.Single(BudgetReports(reports));
        }
        finally { HtmlToBuilderMapper.MaxTableCells = saved; }
    }

    /// <summary>
    /// A rowspan ends where it says: <c>rowspan="2"</c> continues into one row and no further, the row
    /// after it holding only its own cell — a closed rowspan is not met again.
    /// </summary>
    [Fact]
    public void ARowspanEndsWhereItSays()
    {
        Table t = TableOf(Model("<table><tr><td rowspan=\"2\">a</td><td>b</td></tr><tr><td>c</td></tr>" +
                                "<tr><td>d</td><td>e</td></tr></table>"));

        Assert.Equal(VerticalMerge.Continue, t.Rows[1].Cells[0].VMerge);
        Assert.Equal("c", Text(t.Rows[1].Cells[1]));
        Assert.Equal(new[] { "d", "e" }, t.Rows[2].Cells.Select(Text));
        Assert.All(t.Rows[2].Cells, c => Assert.Equal(VerticalMerge.None, c.VMerge));
    }

    /// <summary>
    /// A CSS Grid's cells are charged to the same document budget an HTML table's are (HtmlToPdf-2:1).
    /// The grid read a 1024×64 rectangle off its occupancy array per container with nothing billed, so
    /// a page of grids bypassed the cap tables respect. A grid of ten columns and a hundred rows under
    /// a 200-cell budget stops at twenty rows, reported once.
    /// </summary>
    [Fact]
    public void AGridsCells_AreChargedToTheCellBudget()
    {
        int saved = HtmlToBuilderMapper.MaxTableCells;
        try
        {
            HtmlToBuilderMapper.MaxTableCells = 200;
            var sb = new StringBuilder("<div style=\"display:grid;grid-template-columns:repeat(10,1fr)\">");
            for (int i = 0; i < 1000; i++) sb.Append("<div>c").Append(i).Append("</div>");
            sb.Append("</div>");

            var reports = new List<string>();
            Table t = TableOf(Model(sb.ToString(), reports));

            // Ten columns a row, so twenty rows exhaust the 200-cell budget and the rest are dropped.
            Assert.InRange(Cells(t), 10, HtmlToBuilderMapper.MaxTableCells);
            Assert.True(t.Rows.Count <= 20, "the grid kept more rows than the budget allowed: " + t.Rows.Count);
            Assert.Single(BudgetReports(reports));
        }
        finally { HtmlToBuilderMapper.MaxTableCells = saved; }
    }

    /// <summary>The control: the same grid under the full budget keeps all hundred rows and reports nothing.</summary>
    [Fact]
    public void TheSameGridUnderTheFullBudget_KeepsEveryRow()
    {
        var sb = new StringBuilder("<div style=\"display:grid;grid-template-columns:repeat(10,1fr)\">");
        for (int i = 0; i < 1000; i++) sb.Append("<div>c").Append(i).Append("</div>");
        sb.Append("</div>");

        var reports = new List<string>();
        Table t = TableOf(Model(sb.ToString(), reports));

        Assert.Equal(100, t.Rows.Count);
        Assert.Empty(BudgetReports(reports));
    }

    // ------------------------------------------------------------------ the walk

    /// <summary>
    /// A row that ends short of a rowspan far to its right reaches it in one step. It walked there a
    /// column at a time, scanning every rowspan at each: the distance times the rowspans, a row.
    /// </summary>
    [Fact]
    public void AdversarialGapToAFarRowspan_IsCrossedInOneStep()
    {
        var sb = new StringBuilder("<table><tr><td colspan=\"1000\">a</td><td colspan=\"1000\">b</td>" +
                                   "<td rowspan=\"65534\">c</td></tr>");
        for (int r = 0; r < 100; r++) sb.Append("<tr><td>x</td></tr>");
        sb.Append("</table>");

        Table t = TableOf(Model(sb.ToString()));

        Assert.Equal(101, t.Rows.Count);
        Assert.Equal(VerticalMerge.Continue, t.Rows[100].Cells[2].VMerge);
        Assert.InRange(HtmlToBuilderMapper.TableGridSteps, 101, 10 * 101);
    }

    // ------------------------------------------------------------------ the column cap

    /// <summary>
    /// No table is laid out wider than <see cref="HtmlToBuilderMapper.MaxTableColumns"/>: a cell that
    /// starts past it is dropped and the drop reported once. Under fixed layout a row of
    /// <c>colspan="1000"</c> cells reaches it at its second cell.
    /// </summary>
    [Fact]
    public void AdversarialColumnsPastTheCap_AreDroppedAndReported()
    {
        var reports = new List<string>();
        Table t = TableOf(Model("<table style=\"table-layout:fixed\"><tr><td colspan=\"1000\">a</td>" +
                                "<td colspan=\"1000\">b</td><td>c</td></tr></table>", reports));

        Assert.Equal(HtmlToBuilderMapper.MaxTableColumns, Columns(t.Rows[0]));
        Assert.Equal(new[] { "a" }, t.Rows[0].Cells.Select(Text));
        Assert.Single(reports, r => r.Contains(HtmlToBuilderMapper.MaxTableColumns + " columns", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control: the same row under automatic layout keeps the three columns its cells start in, all
    /// three cells, and reports nothing.
    /// </summary>
    [Fact]
    public void TheSameRowUnderAutomaticLayout_KeepsItsThreeColumns()
    {
        var reports = new List<string>();
        Table t = TableOf(Model("<table><tr><td colspan=\"1000\">a</td><td colspan=\"1000\">b</td><td>c</td></tr></table>",
                                reports));

        Assert.Equal(new[] { "a", "b", "c" }, t.Rows[0].Cells.Select(Text));
        Assert.Equal(3, Columns(t.Rows[0]));
        Assert.DoesNotContain(reports, r => r.Contains(HtmlToBuilderMapper.MaxTableColumns + " columns", StringComparison.Ordinal));
    }
}
