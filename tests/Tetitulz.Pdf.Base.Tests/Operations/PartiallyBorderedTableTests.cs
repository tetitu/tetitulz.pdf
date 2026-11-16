// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The Lines table strategy on a table that is <b>not</b> fully bordered — the shape every existing
/// fixture lacked, and the one every border decision in it is about.
/// </summary>
/// <remarks>
/// <para><b>Six of the Lines strategy's decisions survived mutation, and one missing fixture
/// explains five of them.</b> Every bordered-table fixture in the tree draws all four sides of
/// every cell. On such a grid the confidence fraction genuinely <i>is</i> 1.0, so returning a
/// constant <c>1f</c> changes nothing; no cell has fewer than four borders, so strict mode's
/// <c>continue</c> never runs; and a full-length border always spans the cell it belongs to, so the
/// tests that a ruling must actually <b>reach across</b> the edge it claims are never consulted.
/// <c>LinesStrategy_BorderedTable_HighConfidence</c> and
/// <c>LinesStrict_FullyBorderedTable_KeepsCells</c> both pass with those rules deleted.</para>
///
/// <para><b>Without the span tests a single short tick mark borders a whole row</b>, which is not a
/// cosmetic error: it inflates the confidence a caller uses to decide whether to trust the table,
/// and in strict mode it decides which cells exist at all.</para>
///
/// <para>The grid is the same 2×2 the other fixtures use — columns at x = 50/125/200, rows at
/// y = 700/680/660 — so the only difference under test is which rulings span which edges.</para>
/// </remarks>
public class PartiallyBorderedTableTests
{
    /// <summary>
    /// A 2×2 grid whose bottom-right cell is missing <b>two</b> borders: the right-hand ruling stops
    /// at the first row, and the bottom ruling stops at the first column. Both rulings still exist at
    /// their coordinates, so the grid is still 3×3 lines — only the spans are short.
    /// </summary>
    private static byte[] PartiallyBorderedTable() => BuildPdf(
        // H rulings: top and middle span the table; the bottom one covers the left column only.
        "50 700 m 200 700 l S\n" +
        "50 680 m 200 680 l S\n" +
        "50 660 m 125 660 l S\n" +
        // V rulings: left and middle span both rows; the right one covers the top row only.
        "50 700 m 50 660 l S\n" +
        "125 700 m 125 660 l S\n" +
        "200 700 m 200 680 l S\n" +
        "BT /F1 10 Tf  60 686 Td (A) Tj ET\n" +
        "BT /F1 10 Tf 135 686 Td (B) Tj ET\n" +
        "BT /F1 10 Tf  60 666 Td (C) Tj ET\n" +
        "BT /F1 10 Tf 135 666 Td (D) Tj ET\n");

    private static PdfTable Extract(byte[] pdf, TableStrategy strategy)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return Assert.Single(PdfTableExtractor.Extract(
            ms, new TableExtractionOptions { Strategy = strategy }));
    }

    /// <summary>
    /// <b>The confidence is the fraction of cell borders a ruling actually confirmed</b>, so a table
    /// missing two of its sixteen border slots reports 14/16 rather than 1.
    /// </summary>
    /// <remarks>
    /// Asserted as a value, not as "&gt; 0.9". The existing confidence test uses a fully bordered
    /// table, where the honest answer and a hardcoded <c>1f</c> are the same number — and three
    /// separate mutations hid behind that.
    /// </remarks>
    [Fact]
    public void ThePartiallyBorderedTableReportsTheFractionOfBordersItFound()
    {
        PdfTable table = Extract(PartiallyBorderedTable(), TableStrategy.Lines);

        // Four cells × four sides = 16 slots; the bottom-right cell is missing its right and its
        // bottom, so 14 are confirmed.
        Assert.Equal(14f / 16f, table.Confidence, 3);
    }

    /// <summary>
    /// <b>A ruling only borders a cell if it spans that cell's edge</b> — the horizontal case.
    /// </summary>
    /// <remarks>
    /// The bottom ruling exists at y = 660 and stops at x = 125, so it is the bottom border of the
    /// left cell and not of the right one. Dropping the extent test from <c>HasH</c> would hand it
    /// to both.
    /// </remarks>
    [Fact]
    public void AHorizontalRulingThatStopsShortIsNotTheNextCellsBorder()
    {
        PdfTable table = Extract(PartiallyBorderedTable(), TableStrategy.Lines);

        // With the extent test gone this rises to 15/16 (or 16/16 with the vertical one too), so the
        // number is what distinguishes the two programs.
        Assert.True(table.Confidence < 15f / 16f,
            $"confidence {table.Confidence:F4} means a short ruling was counted as a border");
    }

    /// <summary>
    /// <b>Strict mode keeps only cells with all four borders confirmed</b>, which is the whole of
    /// what distinguishes it from <c>Lines</c>.
    /// </summary>
    /// <remarks>
    /// On a fully bordered fixture strict mode and plain Lines produce identical output, so the
    /// existing strict test passes with the four-border requirement deleted. Here the bottom-right
    /// cell has two borders and must be dropped — and the row it is in must survive with its
    /// remaining cell, which is what proves the rule is per-cell rather than per-table.
    /// </remarks>
    [Fact]
    public void StrictModeDropsTheCellThatIsMissingBorders()
    {
        PdfTable lenient = Extract(PartiallyBorderedTable(), TableStrategy.Lines);
        PdfTable strict = Extract(PartiallyBorderedTable(), TableStrategy.LinesStrict);

        // The control: lenient keeps every cell, so the difference below is strict mode's doing.
        Assert.Equal(new[] { "A", "B" }, lenient.Rows[0].Cells.Select(c => c.Text));
        Assert.Equal(new[] { "C", "D" }, lenient.Rows[1].Cells.Select(c => c.Text));

        Assert.Equal(new[] { "A", "B" }, strict.Rows[0].Cells.Select(c => c.Text));
        Assert.Equal(new[] { "C" }, strict.Rows[1].Cells.Select(c => c.Text));
    }

    /// <summary>
    /// <b>Strict mode's confidence counts only the cells it kept.</b> A skipped cell must not
    /// pollute the denominator, or the number describes a table the caller was not given.
    /// </summary>
    [Fact]
    public void StrictModeConfidenceExcludesTheCellsItSkipped()
    {
        PdfTable strict = Extract(PartiallyBorderedTable(), TableStrategy.LinesStrict);

        // Three cells kept, all four-bordered: 12 of 12.
        Assert.Equal(1f, strict.Confidence, 3);
    }

    /// <summary>
    /// <b>A grid with a single row band is not a table</b> — <c>MinTableRows</c> defaults to 2, and
    /// two horizontal rulings describe one band.
    /// </summary>
    /// <remarks>
    /// Without the check a bare rectangle anywhere on a page becomes a one-row table, and
    /// <c>TableStrategy.Auto</c> stops falling through to the text strategy that would have found
    /// the real one.
    /// </remarks>
    [Fact]
    public void AGridOfOneRowBandIsNotReportedAsATable()
    {
        byte[] pdf = BuildPdf(
            "50 700 m 200 700 l S\n" +
            "50 680 m 200 680 l S\n" +
            "50 700 m 50 680 l S\n" +
            "125 700 m 125 680 l S\n" +
            "200 700 m 200 680 l S\n" +
            "BT /F1 10 Tf 60 686 Td (A) Tj ET\n");

        using var ms = new MemoryStream(pdf, writable: false);
        Assert.Empty(PdfTableExtractor.Extract(
            ms, new TableExtractionOptions { Strategy = TableStrategy.Lines }));
    }

    /// <summary>
    /// <b>A word whose centroid falls just outside a cell is still that cell's text</b>, within
    /// <c>SnapTolerance</c>.
    /// </summary>
    /// <remarks>
    /// Producers place text against a rule, not inside it, and a glyph box that starts a fraction
    /// left of the line puts its centroid outside the band. Without the tolerance the cell comes
    /// back empty and the value is simply lost — the failure mode that looks like a document with
    /// no data rather than like a bug.
    /// </remarks>
    [Fact]
    public void AWordJustOutsideACellIsSnappedIntoIt()
    {
        // The left column runs x = 50..125. "A" at 10 pt Helvetica is ~7 pt wide, so drawing it at
        // x = 45 puts its centroid near 48.5 — outside the cell, inside the 3 pt snap.
        byte[] pdf = BuildPdf(
            "50 700 m 200 700 l S\n" +
            "50 680 m 200 680 l S\n" +
            "50 660 m 200 660 l S\n" +
            "50 700 m 50 660 l S\n" +
            "125 700 m 125 660 l S\n" +
            "200 700 m 200 660 l S\n" +
            "BT /F1 10 Tf  45 686 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 135 686 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  60 666 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 135 666 Td (D) Tj ET\n");

        PdfTable table = Extract(pdf, TableStrategy.Lines);

        Assert.Equal("A", table.Rows[0].Cells.First(c => c.Column == 0).Text);
    }

    /// <summary>
    /// <b>A word near an interior line is in one cell</b>: "A" with its centroid 1.7 pt left of the
    /// line at 125 is the left cell's, and the right cell holds "B" alone.
    /// </summary>
    /// <remarks>
    /// Each cell used to take every word whose centroid fell within <c>SnapTolerance</c> of its box,
    /// so a word that close to a line was both cells' — and an invoice reader asking for the cell
    /// a header word named took the empty sliver beside it. The outer edges still snap, the test
    /// above.
    /// </remarks>
    [Fact]
    public void AWordNearAnInteriorLineIsInOneCell()
    {
        byte[] pdf = BuildPdf(
            "50 700 m 200 700 l S\n" +
            "50 680 m 200 680 l S\n" +
            "50 660 m 200 660 l S\n" +
            "50 700 m 50 660 l S\n" +
            "125 700 m 125 660 l S\n" +
            "200 700 m 200 660 l S\n" +
            "BT /F1 10 Tf 120 686 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 135 686 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  60 666 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 135 666 Td (D) Tj ET\n");

        PdfTable table = Extract(pdf, TableStrategy.Lines);

        Assert.Equal("A", table.Rows[0].Cells.First(c => c.Column == 0).Text);
        Assert.Equal("B", table.Rows[0].Cells.First(c => c.Column == 1).Text);
    }

    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream)
    {
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
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
