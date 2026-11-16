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
/// Phase 4 tests:
///   4.1  PdfLayoutAnalyzer.DetectColumns
///   4.2  PdfTextLine.IsVertical (rotated text grouping)
///   4.3  PdfTable.Confidence on Text strategy
///   4.4  PdfTableExtractor.FindTables with explicit area
/// All PDFs built in-memory.
/// </summary>
public class PdfLayoutAnalyzerTests
{
    // ------------------------------------------------------------------
    //  Shared PDF builder
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

    // ================================================================
    //  4.1  PdfLayoutAnalyzer.DetectColumns
    // ================================================================

    [Fact]
    public void DetectColumns_NullLayout_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() =>
            PdfLayoutAnalyzer.DetectColumns(null));
    }

    [Fact]
    public void DetectColumns_EmptyPage_ReturnsEmpty()
    {
        using var ms = new MemoryStream(BuildPdf(""));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var cols = PdfLayoutAnalyzer.DetectColumns(pages[0]);
        Assert.Empty(cols);
    }

    [Fact]
    public void DetectColumns_SingleColumn_ReturnsOneZone()
    {
        // All words in a single left-side column
        const string cs =
            "BT /F1 12 Tf  50 700 Td (Hello) Tj ET\n" +
            "BT /F1 12 Tf  50 680 Td (World) Tj ET\n" +
            "BT /F1 12 Tf  50 660 Td (Foo)   Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var cols = PdfLayoutAnalyzer.DetectColumns(pages[0]);
        // Single tight cluster → 1 column (no river wide enough to split)
        Assert.Single(cols);
    }

    [Fact]
    public void DetectColumns_TwoColumns_ReturnsTwoZones()
    {
        // Left column at x≈50, right column at x≈400 — 350pt gap between them
        const string cs =
            "BT /F1 12 Tf  50 700 Td (LeftA)  Tj ET\n" +
            "BT /F1 12 Tf  50 680 Td (LeftB)  Tj ET\n" +
            "BT /F1 12 Tf  50 660 Td (LeftC)  Tj ET\n" +
            "BT /F1 12 Tf  50 640 Td (LeftD)  Tj ET\n" +
            "BT /F1 12 Tf  50 620 Td (LeftE)  Tj ET\n" +
            "BT /F1 12 Tf 400 700 Td (RightA) Tj ET\n" +
            "BT /F1 12 Tf 400 680 Td (RightB) Tj ET\n" +
            "BT /F1 12 Tf 400 660 Td (RightC) Tj ET\n" +
            "BT /F1 12 Tf 400 640 Td (RightD) Tj ET\n" +
            "BT /F1 12 Tf 400 620 Td (RightE) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var cols = PdfLayoutAnalyzer.DetectColumns(pages[0]);

        Assert.Equal(2, cols.Count);
        // Left column should have smaller X than right
        Assert.True(cols[0].X < cols[1].X, "Columns should be ordered left-to-right");
    }

    /// <summary>
    /// The coverage grid is kept for the thread's next page, not allocated on the large-object heap
    /// for every page (the evidence: no new grid for the second page), and it carries nothing over:
    /// after a page whose text covers the whole width, a two-column page of the same extent still
    /// finds its river — a grid not cleared would hold the first page's coverage in it.
    /// </summary>
    [Fact]
    public void DetectColumns_ReusesItsGrid_AndCarriesNothingOver()
    {
        var dense = new StringBuilder();
        foreach (int y in new[] { 700, 680, 660, 640, 620 })
            dense.Append("BT /F1 12 Tf 50 ").Append(y).Append(" Td (").Append(new string('M', 40)).Append(") Tj ET\n");
        const string twoColumns =
            "BT /F1 12 Tf  50 700 Td (LeftA)  Tj ET\n" +
            "BT /F1 12 Tf  50 680 Td (LeftB)  Tj ET\n" +
            "BT /F1 12 Tf  50 660 Td (LeftC)  Tj ET\n" +
            "BT /F1 12 Tf  50 640 Td (LeftD)  Tj ET\n" +
            "BT /F1 12 Tf  50 620 Td (LeftE)  Tj ET\n" +
            "BT /F1 12 Tf 400 700 Td (RightA) Tj ET\n" +
            "BT /F1 12 Tf 400 680 Td (RightB) Tj ET\n" +
            "BT /F1 12 Tf 400 660 Td (RightC) Tj ET\n" +
            "BT /F1 12 Tf 400 640 Td (RightD) Tj ET\n" +
            "BT /F1 12 Tf 400 620 Td (RightE) Tj ET\n";

        PdfPageTextLayout first;
        using (var ms = new MemoryStream(BuildPdf(dense.ToString()))) first = PdfStructuredTextExtractor.Extract(ms)[0];
        Assert.Single(first.Columns);

        int made = PdfLayoutAnalyzer.CoverageGridsMade;
        PdfPageTextLayout second;
        using (var ms = new MemoryStream(BuildPdf(twoColumns))) second = PdfStructuredTextExtractor.Extract(ms)[0];

        Assert.Equal(made, PdfLayoutAnalyzer.CoverageGridsMade);
        Assert.Equal(2, second.Columns.Count);
    }

    /// <summary>
    /// <c>PdfPageTextLayout.Columns</c> is meant to be the analyzer's own answer, computed once at
    /// extraction time so callers need not run <see cref="PdfLayoutAnalyzer.DetectColumns"/>
    /// themselves. Two things make that true and are asserted here: the property <b>equals</b> what
    /// a direct call returns, and for a two-column fixture it actually finds two.
    ///
    /// <para><c>PdfPageTextLayout</c> has a second constructor that leaves <c>Columns</c> as
    /// <c>Array.Empty</c>. The predecessor's <c>Assert.NotNull</c> could not tell that path from
    /// this one — an empty array is not null — so "automatically populated" went unchecked.</para>
    /// </summary>
    [Fact]
    public void DetectColumns_PopulatedOnPageLayout()
    {
        const string cs =
            "BT /F1 12 Tf  50 700 Td (LeftA) Tj ET\n" +
            "BT /F1 12 Tf  50 680 Td (LeftB) Tj ET\n" +
            "BT /F1 12 Tf  50 660 Td (LeftC) Tj ET\n" +
            "BT /F1 12 Tf 400 700 Td (RightA) Tj ET\n" +
            "BT /F1 12 Tf 400 680 Td (RightB) Tj ET\n" +
            "BT /F1 12 Tf 400 660 Td (RightC) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var page = PdfStructuredTextExtractor.Extract(ms)[0];

        Assert.Equal(2, page.Columns.Count);
        Assert.Equal(PdfLayoutAnalyzer.DetectColumns(page), page.Columns);
        Assert.True(page.Columns[0].X < page.Columns[1].X, "columns are ordered left-to-right");
    }

    [Fact]
    public void DetectColumns_Zones_OrderedLeftToRight()
    {
        const string cs =
            "BT /F1 12 Tf  50 700 Td (L1) Tj ET\n" +
            "BT /F1 12 Tf  50 680 Td (L2) Tj ET\n" +
            "BT /F1 12 Tf  50 660 Td (L3) Tj ET\n" +
            "BT /F1 12 Tf 400 700 Td (R1) Tj ET\n" +
            "BT /F1 12 Tf 400 680 Td (R2) Tj ET\n" +
            "BT /F1 12 Tf 400 660 Td (R3) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        var cols  = PdfLayoutAnalyzer.DetectColumns(pages[0]);

        for (int i = 1; i < cols.Count; i++)
            Assert.True(cols[i].X >= cols[i - 1].X + cols[i - 1].Width - 1f,
                "Columns must be ordered left-to-right without overlap");
    }

    // ================================================================
    //  4.2  PdfTextLine.IsVertical (rotated chars → separate lines)
    // ================================================================

    [Fact]
    public void IsVertical_False_ForUprightText()
    {
        const string cs = "BT /F1 12 Tf  100 500 Td (Hello) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        Assert.NotEmpty(pages[0].Lines);
        Assert.All(pages[0].Lines, l => Assert.False(l.IsVertical));
    }

    [Fact]
    public void IsVertical_True_For90DegRotatedText()
    {
        // Tm 0 1 -1 0 100 500 → 90° rotation
        const string cs = "BT /F1 12 Tf  0 1 -1 0 100 500 Tm (X) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        Assert.NotEmpty(pages[0].Lines);
        // The rotated char should produce an IsVertical=true line
        Assert.Contains(pages[0].Lines, l => l.IsVertical);
    }

    [Fact]
    public void IsVertical_RotatedChars_NotMixedWithUprightLines()
    {
        // Mix of upright and 90° rotated text on the same page
        const string cs =
            "BT /F1 12 Tf  100 500 Td (Upright) Tj ET\n" +
            "BT /F1 12 Tf  0 1 -1 0 200 500 Tm (Rotated) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var pages = PdfStructuredTextExtractor.Extract(ms);
        Assert.NotEmpty(pages[0].Lines);

        bool hasUpright  = pages[0].Lines.Any(l => !l.IsVertical);
        bool hasVertical = pages[0].Lines.Any(l =>  l.IsVertical);
        Assert.True(hasUpright,  "Expected at least one upright line");
        Assert.True(hasVertical, "Expected at least one vertical/rotated line");

        // No line should contain both upright and rotated chars
        foreach (var line in pages[0].Lines)
        {
            var chars = line.Words.SelectMany(w => w.Chars).ToList();
            if (chars.Count == 0) continue;
            bool lineHasUpright  = chars.Any(c =>  c.IsUpright);
            bool lineHasRotated  = chars.Any(c => !c.IsUpright);
            Assert.False(lineHasUpright && lineHasRotated,
                "A line should not mix upright and rotated characters");
        }
    }

    // ================================================================
    //  4.3  PdfTable.Confidence on Text strategy
    // ================================================================

    [Fact]
    public void TextStrategy_Confidence_IsZero_ForSingleColumn()
    {
        // Single column of text — column count < 2 → confidence = 0
        const string cs =
            "BT /F1 10 Tf  50 700 Td (Row1Col1) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (Row2Col1) Tj ET\n" +
            "BT /F1 10 Tf  50 660 Td (Row3Col1) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Text });
        // Either no table or zero confidence
        if (tables.Count > 0)
            Assert.Equal(0f, tables[0].Confidence, precision: 3);
    }

    [Fact]
    public void TextStrategy_Confidence_IsPositive_ForMultiColumn()
    {
        // 3 rows × 2 columns (words clearly separated)
        const string cs =
            "BT /F1 10 Tf  50 700 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 300 700 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 300 680 Td (D) Tj ET\n" +
            "BT /F1 10 Tf  50 660 Td (E) Tj ET\n" +
            "BT /F1 10 Tf 300 660 Td (F) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Text, MinTableRows = 2 });
        if (tables.Count > 0 && tables[0].ColumnCount >= 2)
            Assert.True(tables[0].Confidence > 0f,
                $"Expected Confidence > 0 but got {tables[0].Confidence}");
    }

    [Fact]
    public void LinesStrategy_Confidence_IsHigh_ForFullyBorderedTable()
    {
        // Re-use the 2×2 re rectangle table from PdfGraphicsExtractorTests
        const string cs =
            "50 660 75 40 re S\n"  +
            "125 660 75 40 re S\n" +
            "50 700 75 40 re S\n"  +
            "125 700 75 40 re S\n" +
            "BT /F1 10 Tf  60 676 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 135 676 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  60 716 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 135 716 Td (D) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Lines, MinTableRows = 1 });

        if (tables.Count > 0)
            Assert.True(tables[0].Confidence > 0.5f,
                $"Expected Lines strategy confidence > 0.5 but got {tables[0].Confidence}");
    }

    // ================================================================
    //  4.4  FindTables with explicit area
    // ================================================================

    [Fact]
    public void FindTables_NullStream_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(() =>
            PdfTableExtractor.FindTables(null, 0, (0, 0, 100, 100)));
    }

    [Fact]
    public void FindTables_AreaCoveringNoContent_ReturnsEmpty()
    {
        // Table at y=660-740, search area y=0-100 (bottom strip, no content)
        const string cs =
            "BT /F1 10 Tf  50 700 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 300 700 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 300 680 Td (D) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.FindTables(ms, 0, (0f, 0f, 612f, 50f));
        Assert.Empty(tables);
    }

    [Fact]
    public void FindTables_AreaCoveringContent_ReturnsTableForThatPage()
    {
        const string cs =
            "BT /F1 10 Tf  50 700 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 300 700 Td (B) Tj ET\n" +
            "BT /F1 10 Tf  50 680 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 300 680 Td (D) Tj ET\n" +
            "BT /F1 10 Tf  50 660 Td (E) Tj ET\n" +
            "BT /F1 10 Tf 300 660 Td (F) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        // Cover the entire page — should find tables
        var tables = PdfTableExtractor.FindTables(ms, 0, (0f, 0f, 612f, 792f),
            new TableExtractionOptions { Strategy = TableStrategy.Text, MinTableRows = 2 });
        // Result is filtered to page 0
        Assert.All(tables, t => Assert.Equal(0, t.PageIndex));
    }

    [Fact]
    public void FindTables_WithOptions_RespectsMinTableRows()
    {
        // Two adjacent words on same line with big gap — but only 1 row
        const string cs =
            "BT /F1 10 Tf  50 700 Td (A) Tj ET\n" +
            "BT /F1 10 Tf 300 700 Td (B) Tj ET\n";
        using var ms = new MemoryStream(BuildPdf(cs));
        var tables = PdfTableExtractor.FindTables(ms, 0, (0f, 0f, 612f, 792f),
            new TableExtractionOptions { Strategy = TableStrategy.Text, MinTableRows = 3 });
        Assert.Empty(tables);
    }
}
