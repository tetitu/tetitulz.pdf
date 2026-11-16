// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;
using L = Tetitulz.Pdf.Base.Tests.Operations.InvoiceLayouts;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The Text table strategy's column decisions — which gaps become separators, which cell a word
/// lands in, and what happens to a word that crosses a boundary.
/// </summary>
/// <remarks>
/// <para><b>A mutation sweep left eleven of this strategy's decisions standing, and the fixtures
/// explain why: no test in the tree had a cell spanning two columns.</b> That one absence hid three
/// separate rules. Span detection itself was never exercised; the right-to-left mirroring arithmetic
/// <c>n - Column - ColSpan</c> is <i>literally the same expression</i> as <c>n - Column - 1</c> when
/// every cell has <c>ColSpan == 1</c>; and a spanning cell is the only way a row can hold more cells
/// than the table has columns, which is the only way the confidence clamp can matter.</para>
///
/// <para><b>Every position here is arithmetic, not an approximation.</b> The font declares
/// <c>/Widths</c> of 600, so at 10 pt every glyph is exactly 6 pt wide and each boundary below can
/// be computed rather than eyeballed — which matters because two of these tests turn on a word
/// starting <i>exactly</i> on a separator.</para>
/// </remarks>
public class TextTableColumnTests
{
    private const float FontSize = 10f;
    private const float GlyphWidth = 6f;     // 600/1000 × 10

    /// <summary>
    /// Three rows. The first and last put a word at x = 50 and another at x = 200, so the only
    /// column separator sits at the midpoint of that gap: (62 + 200) / 2 = <b>131</b>. The middle
    /// row is one word, from 68 to 188, which crosses it.
    /// </summary>
    /// <remarks>
    /// The spanning row is the <b>middle</b> one. At an edge, a row whose word crosses a separator
    /// while none of its gaps sits on one reads as a line of prose beside the grid and ends the table
    /// (GAPS §4a, 2026-10-14); between two of the grid's rows it is the grid's, whatever its shape —
    /// which is what these tests are about.
    /// </remarks>
    private static byte[] SpanningCellTable() => BuildPdf(
        "BT /F1 10 Tf  50 700 Td (AA) Tj ET\n" +
        "BT /F1 10 Tf 200 700 Td (BB) Tj ET\n" +
        "BT /F1 10 Tf  68 680 Td (WWWWWWWWWWWWWWWWWWWW) Tj ET\n" +
        "BT /F1 10 Tf  50 660 Td (CC) Tj ET\n" +
        "BT /F1 10 Tf 200 660 Td (DD) Tj ET\n");

    private static PdfTable Extract(byte[] pdf, TableExtractionOptions options = null)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return Assert.Single(PdfTableExtractor.Extract(
            ms, options ?? new TableExtractionOptions { Strategy = TableStrategy.Text }));
    }

    /// <summary>
    /// <b>A word that crosses a column boundary becomes one cell spanning both columns</b>, rather
    /// than being filed under whichever column it happened to start in.
    /// </summary>
    [Fact]
    public void AWordCrossingASeparatorBecomesASpanningCell()
    {
        PdfTable table = Extract(SpanningCellTable());

        PdfTableCell spanning = Assert.Single(table.Rows[1].Cells);
        Assert.Equal(2, spanning.ColSpan);
        Assert.Equal(0, spanning.Column);
        Assert.StartsWith("W", spanning.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A word crossing a separator between two cells of its row keeps to the column its centre is
    /// in, with the cell there</b>: "AA WWW… BB", its words 6 pt apart — wider than a word space at
    /// 10 pt, so three phrases — reads "AA WWW…" in the first column and "BB" in the second. The
    /// crossing word may not take a column a one-column phrase holds; taking neither, it keeps its
    /// centre's (128, left of 131).
    /// </summary>
    [Fact]
    public void AWordCrossingASeparatorBetweenTwoCellsKeepsToItsCentresColumn()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf  50 700 Td (AA) Tj ET\n" +
            "BT /F1 10 Tf 200 700 Td (BB) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (AA WWWWWWWWWWWWWWWWWWWW BB) Tj ET\n" +
            "BT /F1 10 Tf  50 660 Td (CC) Tj ET\n" +
            "BT /F1 10 Tf 200 660 Td (DD) Tj ET\n"));

        Assert.Equal(new[] { (0, 1, "AA WWWWWWWWWWWWWWWWWWWW"), (1, 1, "BB") },
                     table.Rows[1].Cells.Select(c => (c.Column, c.ColSpan, c.Text)));
    }

    /// <summary>
    /// <b>No row covers a column twice</b>, so a table's filled slots never outnumber them: the
    /// spanning row fills one cell, and the confidence is 5 of 6.
    /// </summary>
    /// <remarks>
    /// A spanning cell used to be added on top of the row's per-column cells, and a row could hold
    /// three cells in a two-column table — a raw fraction of 7/6, which only the clamps kept in range.
    /// </remarks>
    [Fact]
    public void NoRowCoversAColumnTwice()
    {
        PdfTable table = Extract(SpanningCellTable());

        Assert.Equal(2, table.ColumnCount);
        foreach (var row in table.Rows)
        {
            var columns = row.Cells.SelectMany(c => Enumerable.Range(c.Column, c.ColSpan)).ToList();
            Assert.Equal(columns.Count, columns.Distinct().Count());
        }
        Assert.Equal(5f / 6f, table.Confidence, 3);
    }

    /// <summary>
    /// <b>Right-to-left mirroring moves a spanning cell by its whole width</b>, not by one column.
    /// </summary>
    /// <remarks>
    /// <c>ReverseColumns</c> computes <c>n - Column - ColSpan</c>. For a cell of span 1 that is
    /// arithmetically identical to <c>n - Column - 1</c>, so **no fixture with only single-column
    /// cells can tell the two apart** — which is exactly why every existing right-to-left test
    /// passed with the span term replaced by a constant. A two-column span in a two-column table
    /// mirrors to column 0; the wrong expression puts it at column 1, overlapping its neighbour.
    /// </remarks>
    [Fact]
    public void MirroringAColumnSpanMovesItByItsFullWidth()
    {
        PdfTable table = Extract(SpanningCellTable(), new TableExtractionOptions
        {
            Strategy = TableStrategy.Text,
            RightToLeftColumns = true,
        });

        PdfTableCell spanning = Assert.Single(table.Rows[1].Cells.Where(c => c.ColSpan > 1));
        Assert.Equal(0, spanning.Column);
    }

    /// <summary>
    /// <b>A gap only becomes a column separator if it recurs across rows</b>
    /// (<c>MinColumnBoundaryRows</c>).
    /// </summary>
    /// <remarks>
    /// The third row below has two gaps of its own, at midpoints 81 and 156, that no other row
    /// shares. Accepting them would turn a two-column table into a four-column one and scatter every
    /// value — the failure that looks like data rather than like a bug.
    /// </remarks>
    [Fact]
    public void AGapFoundInOnlyOneRowIsNotAColumnSeparator()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf  50 700 Td (AA) Tj ET\n" +
            "BT /F1 10 Tf 200 700 Td (BB) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (CC) Tj ET\n" +
            "BT /F1 10 Tf 200 680 Td (DD) Tj ET\n" +
            "BT /F1 10 Tf  50 660 Td (EE) Tj ET\n" +
            "BT /F1 10 Tf 100 660 Td (FF) Tj ET\n" +
            "BT /F1 10 Tf 200 660 Td (GG) Tj ET\n"));

        Assert.Equal(2, table.ColumnCount);

        // And the row's two words in the first column, a gap apart, are one cell there.
        Assert.Equal(new[] { (0, "EE FF"), (1, "GG") }, table.Rows[2].Cells.Select(c => (c.Column, c.Text)));
    }

    /// <summary>
    /// <b>A gap of exactly <c>ColumnGap</c> separates two columns</b>: "AA" at 50 and "BB" 8 pt after
    /// it, in every row, are two columns at 66, and each row two cells.
    /// </summary>
    [Fact]
    public void AGapOfExactlyColumnGapSeparatesColumns()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf 50 700 Td (AA) Tj ET\n" +
            "BT /F1 10 Tf 70 700 Td (BB) Tj ET\n" +
            "BT /F1 10 Tf 50 680 Td (CC) Tj ET\n" +
            "BT /F1 10 Tf 70 680 Td (DD) Tj ET\n"));

        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(new[] { "CC", "DD" }, table.Rows[1].Cells.Select(c => c.Text));
    }

    /// <summary>
    /// <b>A phrase goes on across a word space — up to <c>PhraseGapEm</c> of the words' height — and
    /// stops at a wider gap</b>: "XX YY" 4 pt apart in 10 pt words is one cell across the separator
    /// at 131; 5 pt apart, two. A <c>ColumnGap</c> narrower than the word space still ends the
    /// phrase there.
    /// </summary>
    [Fact]
    public void APhraseGoesOnAcrossAWordSpaceAndStopsAtAWiderGap()
    {
        // Words of 6 pt glyphs, 10 pt high, laid where given.
        PdfTextLine Row(float y, params (string Text, float X)[] words)
            => new PdfTextLine(string.Join(" ", words.Select(w => w.Text)), words[0].X, y, 200, L.Height,
                               words.Select(w => L.Word(w.Text, w.X, y)).ToList());
        PdfTable Table(float yy, float columnGap = 8f) => Assert.Single(PdfTableExtractor.ExtractFromLayouts(
            new[] { L.Page(0, Row(700, ("AA", 50), ("BB", 200)), Row(680, ("XX", 111), ("YY", yy)), Row(660, ("CC", 50), ("DD", 200))) },
            null,
            new TableExtractionOptions { Strategy = TableStrategy.Text, ColumnGap = columnGap }));

        var phrase = Assert.Single(Table(127).Rows[1].Cells);
        Assert.Equal((0, 2, "XX YY"), (phrase.Column, phrase.ColSpan, phrase.Text));

        Assert.Equal(new[] { (0, "XX"), (1, "YY") }, Table(128).Rows[1].Cells.Select(c => (c.Column, c.Text)));
        Assert.Equal(new[] { (0, "XX"), (1, "YY") }, Table(127, columnGap: 3f).Rows[1].Cells.Select(c => (c.Column, c.Text)));
    }

    /// <summary>
    /// <b>A single row voting for a separator counts once, however many of its own gaps land near
    /// it</b> — <c>MinColumnBoundaryRows</c> counts distinct <i>rows</i>, not distinct gaps.
    /// </summary>
    /// <remarks>
    /// <para>Row one is three one-letter words: A at 50 (ends 56), B at 64 (ends 70), C at 78. That
    /// makes two gaps of its own, of exactly <c>ColumnGap</c> (8 pt) each, at midpoints 60 and 74 —
    /// 14 pt apart, which is <c>GlyphWidth + ColumnGap</c> and therefore the closest two consecutive
    /// gaps in one row can ever land, however the words are sized, as long as each gap still clears
    /// <c>ColumnGap</c> on its own. Row two is D at 57 (ends 63) and E at 71: one gap, midpoint
    /// exactly 67 — equidistant (7 pt, inside the 8 pt tolerance) from <i>both</i> of row one's
    /// gaps. All three midpoints cluster to one separator at their mean, 67.</para>
    ///
    /// <para><b>The genuine vote count is 2, not 3.</b> Row one's first gap (60) is already within
    /// tolerance of 67 and settles the row's vote; that its second gap (74) also happens to be
    /// within tolerance changes nothing about how many <i>rows</i> support the separator. Only a
    /// scan that keeps counting a row's later gaps after it has already voted can reach 3 — which
    /// is why <c>MinColumnBoundaryRows</c> is set to 3 here: 2 is accepted by both a correct and a
    /// broken count, so it cannot tell them apart, and only 3 draws the line between "two rows
    /// agree" and "one row's gaps were counted twice."</para>
    /// </remarks>
    [Fact]
    public void ARowsOwnSeveralGapsNearOneSeparatorStillCountAsOneVote()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf 50 700 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 64 700 Td (B) Tj ET\n" +
            "BT /F1 10 Tf 78 700 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 57 680 Td (D) Tj ET\n" +
            "BT /F1 10 Tf 71 680 Td (E) Tj ET\n"),
            new TableExtractionOptions { Strategy = TableStrategy.Text, MinColumnBoundaryRows = 3 });

        // Only two rows genuinely support the separator at 67, one short of the three this test
        // requires — so no separator is accepted and the page reads as one column.
        Assert.Equal(1, table.ColumnCount);
    }

    /// <summary>
    /// <b>A row's reported Y is its topmost word's</b>, which is what makes it a baseline rather
    /// than an average of whatever the row happens to contain.
    /// </summary>
    /// <remarks>
    /// Words on one typeset line do not share a Y exactly — a superscript, a different font, or a
    /// rounded matrix moves one of them. The row groups them within <c>RowTolerance</c> and then has
    /// to report one number for the row.
    /// </remarks>
    [Fact]
    public void ARowsYIsItsTopmostWord()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf  50 700 Td (AA) Tj ET\n" +
            "BT /F1 10 Tf 200 698 Td (BB) Tj ET\n" +
            "BT /F1 10 Tf  50 660 Td (CC) Tj ET\n" +
            "BT /F1 10 Tf 200 660 Td (DD) Tj ET\n"));

        // The control: the two words really are one row, 2 pt apart inside the 3 pt tolerance.
        Assert.Equal(2, table.Rows[0].Cells.Count);

        Assert.Equal(700f, table.Rows[0].Y, 3);
    }

    /// <summary>
    /// <b>A word starting exactly on a separator belongs to the column to its right.</b>
    /// </summary>
    /// <remarks>
    /// The separator here is exactly 131.0 — 62 and 200 are both exact in binary and so is their
    /// mean — so this is a real tie rather than a near miss. An inclusive comparison puts the word's
    /// start in the left column while its end stays in the right one, turning an ordinary cell into
    /// a spurious two-column span.
    /// </remarks>
    [Fact]
    public void AWordStartingExactlyOnASeparatorBelongsToTheRightHandColumn()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf  50 700 Td (AA) Tj ET\n" +
            "BT /F1 10 Tf 200 700 Td (BB) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (CC) Tj ET\n" +
            "BT /F1 10 Tf 200 680 Td (DD) Tj ET\n" +
            "BT /F1 10 Tf 131 660 Td (ZZ) Tj ET\n"));

        PdfTableCell cell = Assert.Single(table.Rows[2].Cells);
        Assert.Equal(1, cell.Column);
        Assert.Equal(1, cell.ColSpan);
    }

    /// <summary>
    /// <b>A cluster of candidate separators collapses to their mean, not to the first one seen.</b>
    /// </summary>
    /// <remarks>
    /// Real tables are not pixel-perfect: the same column boundary measures slightly differently on
    /// each row, and the mean is what stays in the middle of them. Here two rows put the boundary at
    /// 131 and 134, so the separator is 132.5 and a word ending at 132 is wholly inside the left
    /// column. Taking the first value instead puts the separator at 131, and that same word becomes
    /// a two-column span.
    /// </remarks>
    [Fact]
    public void ClusteredSeparatorsCollapseToTheirMean()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf  50 700 Td (AA) Tj ET\n" +
            "BT /F1 10 Tf 200 700 Td (BB) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (CC) Tj ET\n" +
            "BT /F1 10 Tf 206 680 Td (DD) Tj ET\n" +
            "BT /F1 10 Tf 120 660 Td (ZZ) Tj ET\n"));

        PdfTableCell cell = Assert.Single(table.Rows[2].Cells);
        Assert.Equal(0, cell.Column);
        Assert.Equal(1, cell.ColSpan);
    }

    /// <summary>
    /// <b>A page with no column separator reports one column, and its cells are in it.</b>
    /// </summary>
    /// <remarks>
    /// <para>This used to answer <c>ColumnCount = 2</c> with column 0 permanently empty and every
    /// cell in column 1, because a missing separator list was backfilled with a boundary at x = 0.
    /// A table that reports a column it never fills is a lie a caller acts on: <c>ToGrid()</c>
    /// returned a leading column of nulls, and <c>PdfToDocxConverter</c> had to judge a table
    /// structurally rather than by its column count, having once turned a page of prose into a
    /// table and lost the paragraphs.</para>
    ///
    /// <para>Two words stacked at the same x is the exact shape that page took — ordinary prose,
    /// not a grid.</para>
    /// </remarks>
    [Fact]
    public void APageWithNoSeparatorReportsOneColumn()
    {
        PdfTable table = Extract(BuildPdf(
            "BT /F1 10 Tf 50 700 Td (AA) Tj ET\n" +
            "BT /F1 10 Tf 50 660 Td (BB) Tj ET\n"));

        Assert.Equal(1, table.ColumnCount);
        Assert.Equal(0, Assert.Single(table.Rows[0].Cells).Column);
        Assert.Equal("AA", table.Rows[0].Cells[0].Text);

        // ToGrid is the shape a caller consumes, and it used to lead with a column of nulls.
        string[][] grid = table.ToGrid();
        Assert.Equal(new[] { "AA" }, grid[0]);
    }

    /// <summary>
    /// <b>A header crossing its numbers' gap goes to its numbers' column</b>: "Total" (128–158) set
    /// left of the amounts it heads (200), across the separator its rows' gaps put at 153. The
    /// separator moves into the free stretch on the header's other side, 106–128, where the
    /// header's row keeps a word on each side; the stretch beyond the header leaves nothing of the
    /// row to its right, so a heading spanning there would stay across. Every cell is one column.
    /// The quantities have no heading: a header left across the gap would take their column.
    /// </summary>
    [Fact]
    public void AHeaderAcrossItsNumbersGap_GoesToItsNumbersColumn()
    {
        var table = Table(HeaderOverNumbers());

        Assert.Equal(new[] { (0, "Item"), (2, "Total") }, table.Rows[0].Cells.Select(c => (c.Column, c.Text)));
        Assert.Equal(2, table.Rows[1].Cells.Single(c => c.Text == "12.00").Column);
        Assert.All(table.Rows.SelectMany(r => r.Cells), c => Assert.Equal(1, c.ColSpan));
    }

    /// <summary>
    /// <b>Only the table's rows say where its gaps are free</b>: a field line two phrases wide above
    /// the table, "ABCDEFGHIJKLMNOPQR" reaching across the whole of the amounts' gap, is not one of
    /// them — its row is farther from the rows that voted than two text heights — and the header
    /// still goes to its numbers' column.
    /// </summary>
    [Fact]
    public void AFieldLineAboveTheTable_LeavesItsHeaderFreeToMove()
    {
        var field = LaidRow(724, ("Ref", 50), ("ABCDEFGHIJKLMNOPQR", 100));
        var table = Table(new[] { field }.Concat(HeaderOverNumbers()).ToArray());

        var header = table.Rows.Single(r => r.Cells.Any(c => c.Text == "Item"));
        Assert.Equal(new[] { (0, "Item"), (2, "Total") }, header.Cells.Select(c => (c.Column, c.Text)));
    }

    /// <summary>
    /// <b>A row of one phrase has no boundary for a separator to keep to</b>: a section's title
    /// between the items, "Writinginstrumentsandinks" across the amounts' gap, does not close the
    /// gap to the header, which still goes to its numbers' column.
    /// </summary>
    [Fact]
    public void ASectionTitleAcrossTheGap_LeavesItsHeaderFreeToMove()
    {
        var rows = HeaderOverNumbers();
        var table = Table(new[] { rows[0], rows[1], LaidRow(670, ("Writinginstrumentsandinks", 50)), rows[2] });

        Assert.Equal(new[] { (0, "Item"), (2, "Total") }, table.Rows[0].Cells.Select(c => (c.Column, c.Text)));
        Assert.Contains(table.Rows, r => r.Cells.Any(c => c.Text == "Writinginstrumentsandinks"));
    }

    /// <summary>
    /// <b>A heading over two columns with nothing of its row beyond it stays across</b>, in reach of
    /// its rows: "Quarterlyfigures" (100–196) crosses the separator at 153, and the one free stretch
    /// beside it, 196–200, would leave its row nothing to the right. The heading spans both
    /// columns; "Total" crossing the same gap with "Item" before it moves, the control
    /// (<see cref="AHeaderAcrossItsNumbersGap_GoesToItsNumbersColumn"/>).
    /// </summary>
    [Fact]
    public void AHeadingOverTwoColumnsInReach_StaysAcross()
    {
        var rows = HeaderOverNumbers();
        var table = Table(new[] { LaidRow(698, ("Item", 50), ("Quarterlyfigures", 100)), rows[1], rows[2] });

        var heading = table.Rows[0].Cells.Single(c => c.Text == "Quarterlyfigures");
        Assert.Equal((1, 2), (heading.Column, heading.ColSpan));
    }

    /// <summary>
    /// <b>Of two free stretches, the one parting more of the table's rows wins over the nearer</b>:
    /// "Total" (125–155) crosses the separator at 153 with free space on both sides. The stretch at
    /// 155–200 is nearer, but would leave the header's row nothing before the next separator (255);
    /// the one at 106–125 parts every row, and "Total" joins its amounts.
    /// </summary>
    [Fact]
    public void TheStretchPartingMoreRows_WinsOverTheNearer()
    {
        var table = Table(new[]
        {
            LaidRow(698, ("Item", 50), ("Qty", 88), ("Total", 125), ("Ref", 268)),
            LaidRow(680, ("Pen", 50), ("2", 100), ("12.00", 200), ("Y", 280)),
            LaidRow(660, ("Ink", 50), ("3", 100), ("30.00", 200), ("Z", 280)),
        });

        Assert.Equal(new[] { (0, "Item"), (1, "Qty"), (2, "Total"), (3, "Ref") }, table.Rows[0].Cells.Select(c => (c.Column, c.Text)));
        Assert.Equal(2, table.Rows[1].Cells.Single(c => c.Text == "12.00").Column);
    }

    // Item | (no heading) | Total, "Total" set left of its amounts: its row is the header's, within
    // two text heights of the rows below.
    private static PdfTextLine[] HeaderOverNumbers() => new[]
    {
        LaidRow(698, ("Item", 50), ("Total", 128)),
        LaidRow(680, ("Pen", 50), ("2", 100), ("12.00", 200)),
        LaidRow(660, ("Ink", 50), ("3", 100), ("30.00", 200)),
    };

    private static PdfTable Table(PdfTextLine[] lines) => Assert.Single(PdfTableExtractor.ExtractFromLayouts(
        new[] { L.Page(0, lines) }, null, new TableExtractionOptions { Strategy = TableStrategy.Text }));

    // Words of 6 pt glyphs, 10 pt high, laid where given.
    private static PdfTextLine LaidRow(float y, params (string Text, float X)[] words)
        => new PdfTextLine(string.Join(" ", words.Select(w => w.Text)), words[0].X, y, 200, L.Height,
                           words.Select(w => L.Word(w.Text, w.X, y)).ToList());

    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream)
    {
        // Every code from space to 'Z' is 600/1000 em, so at 10 pt every glyph is exactly 6 pt and
        // the positions above are arithmetic rather than a reading of the 0.6-em fallback.
        var widths = Enumerable.Repeat("600", 'Z' - ' ' + 1);
        string w = string.Join(" ", widths);

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[5];

        offsets[0] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[1] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[2] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        offsets[3] = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);
        offsets[4] = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
                  $"/FirstChar 32 /LastChar 90 /Widths [{w}] >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
