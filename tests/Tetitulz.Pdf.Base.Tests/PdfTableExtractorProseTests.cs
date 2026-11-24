// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// The text strategy's edges (GAPS §4a, 2026-10-14): a line of running text above or below a grid
/// shares no column alignment with it — its words cross the separators and none of its gaps sits on
/// one — and was taken into the table as a row split at a column boundary. Such a row, and a row
/// far outside the grid's vertical extent, now ends the table; a row that crosses nothing (a
/// subtotal in one column) or aligns a gap (a header spanning two columns) stays, and so does a
/// prose line between two of the grid's rows, which only the edges are read for.
/// </summary>
/// <remarks>
/// Every case asserts the table's row count <b>and</b> where the words went: the prose absent from
/// every cell, or the kept row present with its text — a count alone cannot tell a trimmed prose
/// row from a trimmed data row.
/// </remarks>
public class PdfTableExtractorProseTests
{
    private static readonly string[][] Rows =
    {
        new[] { "Region", "Units", "Total" },
        new[] { "North", "12", "340" },
        new[] { "South", "45", "678" },
    };

    private static readonly float[] ColumnX = { 60f, 220f, 380f };

    /// <summary>The three-row grid at y 700, 670, 640, plus any extra text operations.</summary>
    private static string Content(string extra = "")
    {
        var text = new StringBuilder("BT /F1 11 Tf\n");
        float y = 700f;
        foreach (string[] row in Rows)
        {
            for (int c = 0; c < row.Length; c++) text.Append(Word(ColumnX[c], y, row[c]));
            y -= 30f;
        }
        return text.Append(extra).Append("ET\n").ToString();
    }

    private static string Word(float x, float y, string text)
        => string.Format(CultureInfo.InvariantCulture, "1 0 0 1 {0} {1} Tm ({2}) Tj\n", x, y, text);

    private static PdfTable Table(string content)
    {
        using var ms = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(contentStream: content));
        var tables = PdfTableExtractor.Extract(ms, new TableExtractionOptions { Strategy = TableStrategy.Text });
        return Assert.Single(tables);
    }

    private static string[] CellTexts(PdfTable table) => table.Rows.SelectMany(r => r.Cells).Select(c => c.Text).ToArray();

    [Fact]
    public void TheGridAlone_IsThreeRowsOfThreeColumns()
    {
        var table = Table(Content());

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(3, table.ColumnCount);
        Assert.Contains("North", CellTexts(table));
    }

    /// <summary>A sentence above the grid, at the first column's x, crossing the first separator.</summary>
    [Fact]
    public void AProseLineAboveTheGrid_IsNotARow()
    {
        var table = Table(Content(Word(60, 740, "An opening sentence of prose above the figures.")));

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Region", table.Rows[0].Cells[0].Text);
        Assert.DoesNotContain(CellTexts(table), t => t.Contains("prose"));
    }

    /// <summary>A sentence 200pt below the grid ("A closing sentence" | "of prose.").</summary>
    [Fact]
    public void AProseLineFarBelowTheGrid_IsNotARow()
    {
        var table = Table(Content(Word(60, 500, "A closing sentence of prose.")));

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("South", table.Rows[2].Cells[0].Text);
        Assert.DoesNotContain(CellTexts(table), t => t.Contains("prose"));
    }

    /// <summary>A sentence directly under the grid, at the grid's pitch, offset right of the first column: it crosses a separator and aligns no gap.</summary>
    [Fact]
    public void AProseLineOffsetIntoTheGrid_IsNotARow()
    {
        var table = Table(Content(Word(140, 610, "A note offset into the second column of the figures.")));

        Assert.Equal(3, table.Rows.Count);
        Assert.DoesNotContain(CellTexts(table), t => t.Contains("offset"));
    }

    /// <summary>A subtotal in the last column alone crosses nothing: it is a row of the table (the control for the crossing rule).</summary>
    [Fact]
    public void ASubtotalInOneColumn_StaysARow()
    {
        var table = Table(Content(Word(380, 610, "1018")));

        Assert.Equal(4, table.Rows.Count);
        var subtotal = Assert.Single(table.Rows[3].Cells);
        Assert.Equal("1018", subtotal.Text);
        Assert.Equal(2, subtotal.Column);
    }

    /// <summary>
    /// A header whose second cell is one word spanning the last two columns crosses a separator,
    /// but its first gap sits on one: a row (the control for the alignment rule). One word, the
    /// plainest crossing; several a word space apart would cross as one phrase.
    /// </summary>
    [Fact]
    public void ASpanningHeaderBesideAnAlignedGap_StaysARow()
    {
        var table = Table(Content(Word(60, 730, "Name") + Word(220, 730, "Quarterlyfigures2026")));

        Assert.Equal(4, table.Rows.Count);
        Assert.Equal("Name", table.Rows[0].Cells[0].Text);
        Assert.Contains(table.Rows[0].Cells, c => c.Text == "Quarterlyfigures2026" && c.ColSpan == 2);
    }

    /// <summary>Only the edges are read: a sentence between two of the grid's rows stays a row, as before (pinned).</summary>
    [Fact]
    public void AProseLineBetweenTheGridsRows_StaysARow()
    {
        var table = Table(Content(Word(60, 655, "A remark between two rows of the figures.")));

        Assert.Equal(4, table.Rows.Count);
        Assert.Contains(CellTexts(table), t => t.Contains("remark"));
    }

    /// <summary>
    /// A row that aligns with the columns but sits 240pt below the grid — eight times its 30pt
    /// pitch — is outside its extent and ends the table; the same row at the pitch is a row.
    /// </summary>
    [Fact]
    public void AnAlignedRowFarBelowTheGrid_EndsTheTable_AtThePitchItIsARow()
    {
        string footer = Word(60, 400, "West") + Word(220, 400, "7") + Word(380, 400, "90");
        string fourth = Word(60, 610, "West") + Word(220, 610, "7") + Word(380, 610, "90");

        var far = Table(Content(footer));
        var near = Table(Content(fourth));

        Assert.Equal(3, far.Rows.Count);
        Assert.DoesNotContain(CellTexts(far), t => t == "West");
        Assert.Equal(4, near.Rows.Count);
        Assert.Equal("West", near.Rows[3].Cells[0].Text);
    }

    /// <summary>Prose at both edges at once: both trimmed, the grid intact between them.</summary>
    [Fact]
    public void ProseAboveAndBelow_AreBothTrimmed()
    {
        var table = Table(Content(Word(60, 740, "An opening sentence of prose above the figures.") + Word(60, 500, "A closing sentence of prose.")));

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal("Region", table.Rows[0].Cells[0].Text);
        Assert.Equal("South", table.Rows[2].Cells[0].Text);
    }
}
