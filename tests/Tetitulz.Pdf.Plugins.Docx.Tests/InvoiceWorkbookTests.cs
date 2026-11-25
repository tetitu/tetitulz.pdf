// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Plugins.Docx;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// <see cref="InvoiceWorkbook"/>: invoice tables as worksheets a spreadsheet can sum, sort and
/// filter — read back through the OpenXML SDK, not trusted from the writer.
/// </summary>
public class InvoiceWorkbookTests
{
    private static InvoiceDataTable Table(string name) => new InvoiceDataTable(name,
        new[] { "Source", "Total", "Date", "Signed", "Count", "Note" },
        new List<IReadOnlyList<object>>
        {
            new object[] { "a.pdf", 1180.5m, new DateTime(2026, 7, 15), true, 2, "=1+1" },
            new object[] { "ב.pdf", null, null, false, 0, "סה\"כ" },
        });

    private static SpreadsheetDocument Open(byte[] xlsx) => SpreadsheetDocument.Open(new MemoryStream(xlsx), false);

    private static List<Worksheet> Sheets(SpreadsheetDocument doc)
    {
        var wb = doc.WorkbookPart;
        return wb.Workbook.Sheets.Elements<Sheet>()
                 .Select(s => ((WorksheetPart)wb.GetPartById(s.Id.Value)).Worksheet)
                 .ToList();
    }

    private static Cell CellAt(Worksheet ws, string reference)
        => ws.Descendants<Cell>().Single(c => c.CellReference.Value == reference);

    /// <summary>One worksheet per table, named after it; a name Excel refuses is cleaned and made unique.</summary>
    [Fact]
    public void EachTableIsAWorksheetWithAValidUniqueName()
    {
        byte[] xlsx = InvoiceWorkbook.Write(new[] { Table("Invoices"), Table("Line/Items"), Table("invoices") });

        using var doc = Open(xlsx);
        var names = doc.WorkbookPart.Workbook.Sheets.Elements<Sheet>().Select(s => s.Name.Value).ToList();
        Assert.Equal(new[] { "Invoices", "LineItems", "invoices (2)" }, names);
    }

    /// <summary>
    /// A number is a number, a date a styled serial date, a flag a boolean, an empty value no cell,
    /// and text — even text that looks like a formula — an inline string with no formula.
    /// </summary>
    [Fact]
    public void CellsKeepTheirTypes()
    {
        using var doc = Open(InvoiceWorkbook.Write(new[] { Table("Invoices") }));
        var ws = Sheets(doc)[0];

        var total = CellAt(ws, "B2");
        Assert.Equal(CellValues.Number, total.DataType.Value);
        Assert.Equal("1180.5", total.CellValue.Text);

        var date = CellAt(ws, "C2");
        Assert.Equal(new DateTime(2026, 7, 15).ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture), date.CellValue.Text);
        Assert.Equal(1U, date.StyleIndex.Value);

        Assert.Equal(CellValues.Boolean, CellAt(ws, "D2").DataType.Value);
        Assert.Equal("1", CellAt(ws, "D2").CellValue.Text);

        var note = CellAt(ws, "F2");
        Assert.Equal(CellValues.InlineString, note.DataType.Value);
        Assert.Equal("=1+1", note.InlineString.InnerText);
        Assert.Null(note.CellFormula);

        Assert.DoesNotContain(ws.Descendants<Cell>(), c => c.CellReference.Value == "B3");
        Assert.Equal("סה\"כ", CellAt(ws, "F3").InlineString.InnerText);
    }

    /// <summary>
    /// The sheet reads right to left by default, its header row is frozen and filtered; asked for
    /// left to right, it is.
    /// </summary>
    [Fact]
    public void TheSheetIsRightToLeftWithAFrozenFilteredHeader()
    {
        using (var doc = Open(InvoiceWorkbook.Write(new[] { Table("Invoices") })))
        {
            var ws = Sheets(doc)[0];
            var view = ws.Descendants<SheetView>().Single();
            Assert.True(view.RightToLeft.Value);
            var pane = view.Descendants<Pane>().Single();
            Assert.Equal(PaneStateValues.Frozen, pane.State.Value);
            Assert.Equal(1D, pane.VerticalSplit.Value);   // one row frozen
            Assert.Null(pane.HorizontalSplit);            // and no column
            Assert.Equal("A2", pane.TopLeftCell.Value);
            Assert.Equal("A1:F3", ws.Descendants<AutoFilter>().Single().Reference.Value);
            Assert.Equal(2U, CellAt(ws, "A1").StyleIndex.Value);
        }

        using (var doc = Open(InvoiceWorkbook.Write(new[] { Table("Invoices") }, rightToLeft: false)))
            Assert.False(Sheets(doc)[0].Descendants<SheetView>().Single().RightToLeft.Value);
    }

    /// <summary>The invoice tables themselves go through: one sheet each, rows as built.</summary>
    [Fact]
    public void TheInvoiceTablesBecomeAWorkbook()
    {
        var inv = new IsraeliInvoice { Source = "a.pdf", InvoiceNumber = "7", Total = 118m };
        inv.LineItems.Add(new InvoiceLineItem { Line = 1, Description = "פריט", Amount = 100m });
        IsraeliInvoiceValidator.Validate(inv);
        var invoices = new[] { inv };

        byte[] xlsx = InvoiceWorkbook.Write(new[]
        {
            IsraeliInvoiceTables.Invoices(invoices),
            IsraeliInvoiceTables.LineItems(invoices),
            IsraeliInvoiceTables.Findings(invoices),
        });

        using var doc = Open(xlsx);
        var sheets = Sheets(doc);
        Assert.Equal(3, sheets.Count);
        Assert.Equal(2, sheets[1].Descendants<Row>().Count());                 // header + one item
        Assert.Equal(inv.Findings.Count + 1, sheets[2].Descendants<Row>().Count());
    }

    /// <summary>
    /// The names Excel refuses beyond its characters: an apostrophe at either end — the 31-character
    /// cut may expose one — and the reserved "History"; and a control character, which XML cannot
    /// carry at all and must not abort the whole workbook.
    /// </summary>
    [Fact]
    public void SheetNamesFollowExcelsApostropheAndHistoryRules()
    {
        string cutAtApostrophe = new string('a', 30) + "'b";
        byte[] xlsx = InvoiceWorkbook.Write(new[]
        {
            Table("'Q3'"), Table("History"), Table(cutAtApostrophe), Table("Q" + (char)1 + "4"),
        });

        using var doc = Open(xlsx);
        var names = doc.WorkbookPart.Workbook.Sheets.Elements<Sheet>().Select(s => s.Name.Value).ToList();
        Assert.Equal(new[] { "Q3", "History (2)", new string('a', 30), "Q4" }, names);
    }

    /// <summary>
    /// A date Excel's serials cannot show — before year 100 (no serial at all), or before
    /// 1900-03-01 (Excel's leap-year 1900 shifts it) — is written as text; a later one stays a
    /// styled serial date.
    /// </summary>
    [Fact]
    public void ADateBeforeExcelsCalendarIsWrittenAsText()
    {
        var table = new InvoiceDataTable("Dates", new[] { "Date" }, new List<IReadOnlyList<object>>
        {
            new object[] { new DateTime(26, 6, 10) },
            new object[] { new DateTime(1900, 1, 1) },
            new object[] { new DateTime(1900, 3, 1) },
        });

        using var doc = Open(InvoiceWorkbook.Write(new[] { table }));
        var ws = Sheets(doc)[0];

        Assert.Equal(CellValues.InlineString, CellAt(ws, "A2").DataType.Value);
        Assert.Equal("0026-06-10", CellAt(ws, "A2").InlineString.InnerText);
        Assert.Equal("1900-01-01", CellAt(ws, "A3").InlineString.InnerText);
        Assert.Equal("61", CellAt(ws, "A4").CellValue.Text);
        Assert.Equal(1U, CellAt(ws, "A4").StyleIndex.Value);
    }

    /// <summary>
    /// Text a spreadsheet would decode — <c>_x0031_</c> is "1" in a cell's string — is escaped, so
    /// it shows as written; text without such a sequence is written as it is. Read from the part's
    /// XML, since the SDK's text would decode it too.
    /// </summary>
    [Fact]
    public void TextThatLooksLikeAnEscapeIsWrittenAsItIs()
    {
        var table = new InvoiceDataTable("Codes", new[] { "Code" }, new List<IReadOnlyList<object>>
        {
            new object[] { "AB_x0031_" },
            new object[] { "AB_1" },
        });

        using var doc = Open(InvoiceWorkbook.Write(new[] { table }));
        string xml = Sheets(doc)[0].OuterXml;

        Assert.Contains(">AB_x005F_x0031_<", xml, StringComparison.Ordinal);
        Assert.Contains(">AB_1<", xml, StringComparison.Ordinal);
    }

    /// <summary>A workbook needs a table; none is refused.</summary>
    [Fact]
    public void AWorkbookWithNoTablesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => InvoiceWorkbook.Write(Array.Empty<InvoiceDataTable>()));
    }
}
