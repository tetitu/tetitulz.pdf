// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// P2a — RTL awareness in <see cref="PdfTableExtractor"/>: bidi cell text and right-to-left column
/// numbering, exercised on a 2x3 Hebrew line-item table (amount | qty | description, right-to-left).
/// </summary>
public class PdfTableExtractorRtlTests
{
    private const int Alef = 0x05D0, Bet = 0x05D1, Gimel = 0x05D2;
    private const int Dalet = 0x05D3, He = 0x05D4, Vav = 0x05D5;

    // Layout (RTL invoice table): description column on the RIGHT, qty middle, amount on the LEFT.
    private static byte[] TablePdf()
        => new InvoicePdfBuilder()
            // Row 1 (y = 700)
            .AsciiRun("20", x: 50, y: 700)
            .AsciiRun("10", x: 150, y: 700)
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), xRight: 270, y: 700)
            // Row 2 (y = 680)
            .AsciiRun("40", x: 50, y: 680)
            .AsciiRun("30", x: 150, y: 680)
            .HebRunRtl(InvoicePdfBuilder.Hb(Dalet, He, Vav), xRight: 270, y: 680)
            .Build();

    private static PdfTable Extract(bool applyBidi, bool rtlColumns)
    {
        using var ms = new MemoryStream(TablePdf());
        var tables = PdfTableExtractor.Extract(ms, new TableExtractionOptions
        {
            Strategy = TableStrategy.Text,
            ApplyBidi = applyBidi,
            RightToLeftColumns = rtlColumns,
        });
        Assert.NotEmpty(tables);
        return tables[0];
    }

    [Fact]
    public void DetectsThreeColumns()
    {
        var table = Extract(applyBidi: true, rtlColumns: false);
        Assert.Equal(3, table.ColumnCount);
    }

    [Fact]
    public void ApplyBidi_HebrewCell_ReadsInLogicalOrder()
    {
        var table = Extract(applyBidi: true, rtlColumns: false);
        // Description is the right-most column (col 2 in left-to-right numbering).
        Assert.Equal(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), table.Rows[0].GetCell(2));
    }

    [Fact]
    public void ApplyBidi_NumericCells_NotReversed()
    {
        var table = Extract(applyBidi: true, rtlColumns: false);
        Assert.Equal("20", table.Rows[0].GetCell(0));
        Assert.Equal("10", table.Rows[0].GetCell(1));
    }

    [Fact]
    public void RightToLeftColumns_PutsDescriptionInColumnZero()
    {
        var table = Extract(applyBidi: true, rtlColumns: true);
        // With RTL column numbering, column 0 is the right-most zone = the description.
        Assert.Equal(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), table.Rows[0].GetCell(0));
        Assert.Equal("20", table.Rows[0].GetCell(2));
    }
}
