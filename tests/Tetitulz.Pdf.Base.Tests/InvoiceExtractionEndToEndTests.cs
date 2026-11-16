// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// End-to-end invoice flow: a single Hebrew invoice page with labelled header fields (invoice no.,
/// date, total) and a right-to-left line-item table is run through <see cref="PdfFieldExtractor"/>,
/// <see cref="PdfTableExtractor"/> (bidi + RTL columns) and <see cref="InvoiceValueParser"/> together.
/// </summary>
public class InvoiceExtractionEndToEndTests
{
    // Hebrew letters used for labels / descriptions.
    private const int Mem = 0x05DE, Samekh = 0x05E1, Tav = 0x05EA, Resh = 0x05E8;
    private const int He = 0x05D4, Kaf = 0x05DB;
    private const int Alef = 0x05D0, Bet = 0x05D1, Gimel = 0x05D2, Dalet = 0x05D3, Vav = 0x05D5;

    private static string InvoiceNoLabel => InvoicePdfBuilder.Hb(Mem, Samekh);        // "מס"
    private static string DateLabel      => InvoicePdfBuilder.Hb(Tav, Resh);          // "תר"
    private static string TotalLabel     => InvoicePdfBuilder.Hb(Samekh, He, Kaf);    // "סהכ"

    private static byte[] InvoicePdf()
        => new InvoicePdfBuilder()
            // ── Header fields (Hebrew label on the right, value to its left) ──
            // Not a palindrome: "1001" read backwards is "1001", so it could not show a reversal.
            .HebRunRtl(InvoiceNoLabel, xRight: 300, y: 750).AsciiRun("20457", x: 236, y: 750, step: 4)
            .HebRunRtl(DateLabel,      xRight: 300, y: 720).AsciiRun("10/06/2026", x: 190, y: 720, step: 4)
            .HebRunRtl(TotalLabel,     xRight: 300, y: 690).AsciiRun("1,234.56", x: 210, y: 690, step: 4)
            // ── Line-item table: amount | qty | description (right-to-left) ──
            .AsciiRun("20", x: 50, y: 640).AsciiRun("2", x: 150, y: 640)
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), xRight: 270, y: 640)
            .AsciiRun("40", x: 50, y: 620).AsciiRun("4", x: 150, y: 620)
            .HebRunRtl(InvoicePdfBuilder.Hb(Dalet, He, Vav), xRight: 270, y: 620)
            .Build();

    [Fact]
    public void FieldsTableAndValues_AllResolve()
    {
        byte[] pdf = InvoicePdf();

        // 1) Header fields.
        var fields = PdfFieldExtractor.Extract(new MemoryStream(pdf), new[]
        {
            new FieldQuery("invoiceNo", InvoiceNoLabel),
            new FieldQuery("date",      DateLabel),
            new FieldQuery("total",     TotalLabel),
        }).ToDictionary(f => f.Key);

        Assert.Equal("20457", fields["invoiceNo"].Value);

        Assert.True(InvoiceValueParser.TryParseIsraeliDate(fields["date"].Value, out var date));
        Assert.Equal(new System.DateTime(2026, 6, 10), date.Date);

        Assert.True(InvoiceValueParser.TryParseAmount(fields["total"].Value, out var total, out _));
        Assert.Equal(1234.56m, total);

        // 2) Line-item table, isolated to its region, RTL-aware.
        var tables = PdfTableExtractor.FindTables(
            new MemoryStream(pdf), pageIndex: 0,
            area: (40f, 605f, 320f, 50f),
            options: new TableExtractionOptions
            {
                Strategy = TableStrategy.Text,
                ApplyBidi = true,
                RightToLeftColumns = true,
            });

        Assert.NotEmpty(tables);
        var table = tables[0];
        Assert.Equal(3, table.ColumnCount);
        Assert.Equal(2, table.Rows.Count);

        // Column 0 (right-most zone) is the Hebrew description; amount is the last column.
        Assert.Equal(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), table.Rows[0].GetCell(0));
        Assert.Equal("20", table.Rows[0].GetCell(2));

        // 3) A line amount parses.
        Assert.True(InvoiceValueParser.TryParseAmount(table.Rows[0].GetCell(2), out var lineAmt, out _));
        Assert.Equal(20m, lineAmt);
    }
}
