// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Tests for PdfGraphicsExtractor and the Lines/Auto table strategies.
/// All PDFs are built in-memory; no files on disk.
/// </summary>
public class PdfGraphicsExtractorTests
{
    // ------------------------------------------------------------------
    //  In-memory PDF builder
    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  Data model unit tests
    // ------------------------------------------------------------------

    [Fact]
    public void HRuling_X1_AlwaysLessThanOrEqualX2()
    {
        var r = new HRuling(300f, 100f, 500f, 1f); // x1 > x2 — should normalise
        Assert.True(r.X1 <= r.X2);
        Assert.Equal(100f, r.X1);
        Assert.Equal(300f, r.X2);
    }

    [Fact]
    public void VRuling_Y1_AlwaysLessThanOrEqualY2()
    {
        var r = new VRuling(200f, 700f, 400f, 1f); // y1 > y2 — should normalise
        Assert.True(r.Y1 <= r.Y2);
        Assert.Equal(400f, r.Y1);
        Assert.Equal(700f, r.Y2);
    }

    [Fact]
    public void HRuling_Length_IsX2MinusX1()
    {
        var r = new HRuling(50f, 200f, 500f, 1f);
        Assert.Equal(150f, r.Length, precision: 2);
    }

    [Fact]
    public void PdfPageGraphics_EmptyPage_HasEmptyLists()
    {
        var g = new PdfPageGraphics(0, null, null, null);
        Assert.Empty(g.HorizontalRulings);
        Assert.Empty(g.VerticalRulings);
        Assert.Empty(g.Images);
    }

    // ------------------------------------------------------------------
    //  Null guard
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PdfGraphicsExtractor.Extract((Stream)null));
    }

    [Fact]
    public void Extract_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PdfGraphicsExtractor.Extract((string)null));
    }

    // ------------------------------------------------------------------
    //  Empty page
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_EmptyContentStream_ReturnsNoRulings()
    {
        byte[] pdf = BuildPdf(string.Empty);
        using var ms = new MemoryStream(pdf);
        var pages = PdfGraphicsExtractor.Extract(ms);
        Assert.Single(pages);
        Assert.Empty(pages[0].HorizontalRulings);
        Assert.Empty(pages[0].VerticalRulings);
    }

    // ------------------------------------------------------------------
    //  Horizontal line via m/l/S
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_HorizontalLine_DetectsHRuling()
    {
        // Horizontal line from (50,500) to (300,500)
        const string cs = "50 500 m 300 500 l S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        Assert.Single(pages[0].HorizontalRulings);
        var r = pages[0].HorizontalRulings[0];
        Assert.Equal(50f,  r.X1, precision: 1);
        Assert.Equal(300f, r.X2, precision: 1);
        Assert.Equal(500f, r.Y,  precision: 1);
    }

    [Fact]
    public void Extract_HorizontalLine_NoVerticalRuling()
    {
        const string cs = "50 500 m 300 500 l S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);
        Assert.Empty(pages[0].VerticalRulings);
    }

    // ------------------------------------------------------------------
    //  Vertical line via m/l/S
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_VerticalLine_DetectsVRuling()
    {
        // Vertical line from (100,400) to (100,700)
        const string cs = "100 400 m 100 700 l S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        Assert.Single(pages[0].VerticalRulings);
        var r = pages[0].VerticalRulings[0];
        Assert.Equal(100f, r.X,  precision: 1);
        Assert.Equal(400f, r.Y1, precision: 1);
        Assert.Equal(700f, r.Y2, precision: 1);
    }

    // ------------------------------------------------------------------
    //  Rectangle via re
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_Rectangle_DetectsTwoHAndTwoVRulings()
    {
        // re x=50 y=660 w=150 h=40 → corners at (50,660),(200,660),(200,700),(50,700)
        const string cs = "50 660 150 40 re S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        Assert.Equal(2, pages[0].HorizontalRulings.Count); // y=660 and y=700
        Assert.Equal(2, pages[0].VerticalRulings.Count);   // x=50 and x=200
    }

    [Fact]
    public void Extract_Rectangle_CorrectHRulingPositions()
    {
        const string cs = "50 660 150 40 re S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        var ys = pages[0].HorizontalRulings.Select(r => r.Y).OrderBy(y => y).ToArray();
        Assert.Equal(660f, ys[0], precision: 1);
        Assert.Equal(700f, ys[1], precision: 1);
    }

    // ------------------------------------------------------------------
    //  Thin filled rectangle treated as ruling
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_ThinFilledRect_DetectedAsHRuling()
    {
        // Filled rect 200 wide × 1 pt tall — should be a horizontal ruling
        const string cs = "50 500 200 1 re f";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        Assert.Single(pages[0].HorizontalRulings);
    }

    [Fact]
    public void Extract_ThickFilledRect_NotDetectedAsRuling()
    {
        // Large background rectangle — should NOT be a ruling
        const string cs = "50 50 500 700 re f";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        Assert.Empty(pages[0].HorizontalRulings);
        Assert.Empty(pages[0].VerticalRulings);
    }

    // ------------------------------------------------------------------
    //  Adversarial: degenerate path (single point moveto then stroke)
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_DegenerateSinglePointPath_NoRulingsNoException()
    {
        const string cs = "100 100 m S"; // moveto only, no lineto
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);  // must not throw

        Assert.Empty(pages[0].HorizontalRulings);
        Assert.Empty(pages[0].VerticalRulings);
    }

    // ------------------------------------------------------------------
    //  Collinear overlap merging
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_OverlappingHLines_MergedIntoOne()
    {
        // Two H lines at the same Y that overlap: [100,300] and [200,400]
        const string cs =
            "100 500 m 300 500 l S\n" +
            "200 500 m 400 500 l S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        // After merging these should collapse to one ruling [100,400]
        Assert.Single(pages[0].HorizontalRulings);
        var r = pages[0].HorizontalRulings[0];
        Assert.Equal(100f, r.X1, precision: 1);
        Assert.Equal(400f, r.X2, precision: 1);
    }

    [Fact]
    public void Extract_TwoDistinctHLines_NotMerged()
    {
        // Two H lines far apart in Y — must stay separate
        const string cs =
            "50 700 m 200 700 l S\n" +
            "50 400 m 200 400 l S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        Assert.Equal(2, pages[0].HorizontalRulings.Count);
    }

    // ------------------------------------------------------------------
    //  Short segment filtered by MinRulingLength
    // ------------------------------------------------------------------

    [Fact]
    public void Extract_ShortLine_FilteredOut()
    {
        // 3 pt line — below MinRulingLength (5 pt)
        const string cs = "100 500 m 103 500 l S";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfGraphicsExtractor.Extract(ms);

        Assert.Empty(pages[0].HorizontalRulings);
    }

    // ------------------------------------------------------------------
    //  Lines strategy — bordered 2×2 table
    // ------------------------------------------------------------------

    internal static byte[] BorderedTablePdf() => Build2x2BorderedTable();

    internal static byte[] RePdf() => BuildPdf(
        "50 660 75 40 re S\n125 660 75 40 re S\n" +
        "BT /F1 10 Tf  60 686 Td (X) Tj ET\nBT /F1 10 Tf 135 686 Td (Y) Tj ET\n");

    /// <summary>Two bordered single-row tables, one at y≈700 and one at y≈400.</summary>
    internal static byte[] TwoStackedTablesPdf() => BuildPdf(
        "50 700 m 200 700 l S\n50 680 m 200 680 l S\n" +
        "50 700 m  50 680 l S\n200 700 m 200 680 l S\n" +
        "50 400 m 200 400 l S\n50 380 m 200 380 l S\n" +
        "50 400 m  50 380 l S\n200 400 m 200 380 l S\n");

    private static byte[] Build2x2BorderedTable()
    {
        // Grid: cols x=[50,125,200], rows y=[700,680,660]
        // 3 H lines + 3 V lines + text in each cell
        const string cs =
            "50 700 m 200 700 l S\n" +
            "50 680 m 200 680 l S\n" +
            "50 660 m 200 660 l S\n" +
            "50 700 m 50  660 l S\n" +
            "125 700 m 125 660 l S\n" +
            "200 700 m 200 660 l S\n" +
            "BT /F1 10 Tf  60 686 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 135 686 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  60 666 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 135 666 Td (D) Tj ET\n";
        return BuildPdf(cs);
    }

    /// <summary>
    /// Exactly one table, and its four cells hold the text that was drawn in them. Its siblings
    /// below assert the dimensions and the confidence; this one asserts the <b>content</b>, which
    /// is what proves cell text was matched to the right cell rather than merely counted.
    /// </summary>
    [Fact]
    public void LinesStrategy_BorderedTable_ReadsEachCellsText()
    {
        using var ms = new MemoryStream(Build2x2BorderedTable());
        var table = Assert.Single(PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Lines }));

        Assert.Equal(new[] { "A", "B" }, table.Rows[0].Cells.Select(c => c.Text).ToArray());
        Assert.Equal(new[] { "C", "D" }, table.Rows[1].Cells.Select(c => c.Text).ToArray());
    }

    [Fact]
    public void LinesStrategy_BorderedTable_CorrectDimensions()
    {
        using var ms = new MemoryStream(Build2x2BorderedTable());
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Lines });

        Assert.NotEmpty(tables);
        var t = tables[0];
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(2, t.ColumnCount);
    }

    [Fact]
    public void LinesStrategy_BorderedTable_HighConfidence()
    {
        using var ms = new MemoryStream(Build2x2BorderedTable());
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Lines });

        Assert.NotEmpty(tables);
        // All 4 cells have all 4 borders → confidence should be 1.0
        Assert.True(tables[0].Confidence > 0.9f,
            $"Expected confidence > 0.9 but got {tables[0].Confidence}");
    }

    [Fact]
    public void LinesStrategy_ReRectangle_FindsTable()
    {
        // Same table built with 're' rectangles instead of individual m/l/S lines
        const string cs =
            "50 660 75 40 re S\n"  +   // left column
            "125 660 75 40 re S\n" +   // right column
            "BT /F1 10 Tf  60 686 Td (X) Tj ET\n" +
            "BT /F1 10 Tf 135 686 Td (Y) Tj ET\n";

        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions
            {
                Strategy    = TableStrategy.Lines,
                MinTableRows = 1  // single-row bordered table is valid in Lines mode
            });

        // Two rectangles sharing a vertical edge at x=125 → 1 row x 2 cols. The comment already
        // stated the expected shape; asserting it is what makes the test able to fail.
        var table = Assert.Single(tables);
        Assert.Single(table.Rows);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(new[] { "X", "Y" }, table.Rows[0].Cells.Select(c => c.Text).ToArray());
    }

    // ------------------------------------------------------------------
    //  Auto strategy
    // ------------------------------------------------------------------

    /// <summary>
    /// Auto's contract when rulings are present is "behave like Lines", so compare the two results
    /// rather than merely checking Auto found something — a fall-through to the Text strategy would
    /// also find a table here, just a different one.
    /// </summary>
    [Fact]
    public void AutoStrategy_WithRulings_MatchesTheLinesStrategy()
    {
        using var autoMs = new MemoryStream(Build2x2BorderedTable());
        var viaAuto = Assert.Single(PdfTableExtractor.Extract(autoMs,
            new TableExtractionOptions { Strategy = TableStrategy.Auto }));

        using var linesMs = new MemoryStream(Build2x2BorderedTable());
        var viaLines = Assert.Single(PdfTableExtractor.Extract(linesMs,
            new TableExtractionOptions { Strategy = TableStrategy.Lines }));

        Assert.Equal(viaLines.Rows.Count, viaAuto.Rows.Count);
        Assert.Equal(viaLines.ColumnCount, viaAuto.ColumnCount);
        Assert.Equal(viaLines.Rows.SelectMany(r => r.Cells).Select(c => c.Text).ToArray(),
                     viaAuto.Rows.SelectMany(r => r.Cells).Select(c => c.Text).ToArray());
    }

    [Fact]
    public void AutoStrategy_NoRulings_FallsBackToTextStrategy()
    {
        // Text-only table (no path operators) — Lines finds nothing; Auto falls back to Text
        const string cs =
            "BT /F1 10 Tf  50 700 Td (Name)  Tj ET\n" +
            "BT /F1 10 Tf 300 700 Td (Score) Tj ET\n" +
            "BT /F1 10 Tf  50 685 Td (Alice) Tj ET\n" +
            "BT /F1 10 Tf 300 685 Td (95)    Tj ET\n";

        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions
            {
                Strategy  = TableStrategy.Auto,
                ColumnGap = 20f
            });

        // Text strategy should find the two-column table
        Assert.NotEmpty(tables);
        Assert.Equal(2, tables[0].ColumnCount);
    }

    // ------------------------------------------------------------------
    //  LinesStrict — drops cells without all 4 borders
    // ------------------------------------------------------------------

    [Fact]
    public void LinesStrict_FullyBorderedTable_KeepsCells()
    {
        using var ms = new MemoryStream(Build2x2BorderedTable());
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.LinesStrict });

        Assert.NotEmpty(tables);
        Assert.Equal(2, tables[0].Rows.Count);
    }

    // ------------------------------------------------------------------
    //  Confidence property
    // ------------------------------------------------------------------

    [Fact]
    public void PdfTable_Confidence_ClampedToZeroOne()
    {
        var t = new PdfTable(0, [], 1, confidence: 1.5f);
        Assert.Equal(1f, t.Confidence);

        var t2 = new PdfTable(0, [], 1, confidence: -0.5f);
        Assert.Equal(0f, t2.Confidence);
    }

    [Fact]
    public void TextStrategy_Confidence_IsPositive_ForAlignedColumns()
    {
        // Phase 4.3: Text strategy now reports a heuristic confidence based on
        // column-alignment consistency. A clean 2x2 aligned grid should produce
        // a positive (non-zero) confidence value.
        const string cs =
            "BT /F1 10 Tf  50 700 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 300 700 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  50 685 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 300 685 Td (D) Tj ET\n";

        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Text, ColumnGap = 20f });

        Assert.NotEmpty(tables);
        Assert.InRange(tables[0].Confidence, 0f, 1f);
        Assert.True(tables[0].Confidence > 0f,
            $"Expected Text-strategy Confidence > 0 for a fully-aligned grid but got {tables[0].Confidence}");
    }

    // ------------------------------------------------------------------
    //  SearchArea option
    // ------------------------------------------------------------------

    [Fact]
    public void LinesStrategy_SearchArea_RestrictsDetection()
    {
        // Two separate bordered single-row tables stacked vertically.
        // Top table: y=[700,680], bottom table: y=[400,380]
        const string cs =
            // top table borders
            "50 700 m 200 700 l S\n" +
            "50 680 m 200 680 l S\n" +
            "50 700 m  50 680 l S\n" +
            "200 700 m 200 680 l S\n" +
            // bottom table borders
            "50 400 m 200 400 l S\n" +
            "50 380 m 200 380 l S\n" +
            "50 400 m  50 380 l S\n" +
            "200 400 m 200 380 l S\n";

        using var ms = new MemoryStream(BuildPdf(cs));
        // Restrict to top half only (y > 500)
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions
            {
                Strategy     = TableStrategy.Lines,
                MinTableRows = 1,
                SearchArea   = (0f, 500f, 612f, 300f)  // x=0,y=500,w=612,h=300
            });

        // The bottom table's rulings must be excluded, so the restricted run sees strictly fewer
        // row bands than the unrestricted one. The predecessor's own comment admitted it was
        // asserting nothing ("just verify no crash"), and `Assert.NotNull` on a returned list is
        // true however the filter behaves — including when SearchArea is ignored entirely.
        using var unrestrictedMs = new MemoryStream(BuildPdf(cs));
        var unrestricted = Assert.Single(PdfTableExtractor.Extract(unrestrictedMs,
            new TableExtractionOptions { Strategy = TableStrategy.Lines, MinTableRows = 1 }));

        var restricted = Assert.Single(tables);

        Assert.True(restricted.Rows.Count < unrestricted.Rows.Count,
            $"SearchArea should have excluded the lower table: {unrestricted.Rows.Count} row bands " +
            $"unrestricted vs {restricted.Rows.Count} restricted");
    }

    // ------------------------------------------------------------------
    //  TableExtractionOptions defaults include new fields
    // ------------------------------------------------------------------

    [Fact]
    public void TableExtractionOptions_NewDefaults_AreReasonable()
    {
        var opts = new TableExtractionOptions();
        Assert.Equal(TableStrategy.Auto, opts.Strategy);
        Assert.True(opts.SnapTolerance > 0f);
        Assert.Null(opts.SearchArea);
    }
}
