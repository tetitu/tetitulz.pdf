// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Invoices in table shape for analysis — <see cref="IsraeliInvoiceTables"/> and
/// <see cref="InvoiceDataTable"/>'s CSV.
/// </summary>
public class InvoiceDataTableTests
{
    private static IsraeliInvoice Invoice(string source, string number, params decimal?[] amounts)
    {
        var inv = new IsraeliInvoice
        {
            Source = source,
            DocumentType = IsraeliDocumentType.TaxInvoice,
            InvoiceNumber = number,
            Date = new DateTime(2026, 7, 15),
            Subtotal = 100m,
            Vat = 18m,
            Total = 118m,
            LineItems = amounts.Select((a, i) => new InvoiceLineItem
            {
                Line = i + 1, PageIndex = 0, Description = "פריט " + (i + 1), Amount = a,
            }).ToList(),
        };
        IsraeliInvoiceValidator.Validate(inv);
        return inv;
    }

    // ================================================================
    //  The three tables
    // ================================================================

    /// <summary>
    /// One row per invoice, typed: the number of line items and their sum, and the findings
    /// counted by severity.
    /// </summary>
    [Fact]
    public void TheInvoicesTableHasOneTypedRowPerInvoice()
    {
        var table = IsraeliInvoiceTables.Invoices(new[] { Invoice("a.pdf", "1", 60m, 40m), Invoice("b.pdf", "2", 60m, null) });

        Assert.Equal("Invoices", table.Name);
        Assert.Equal(2, table.Rows.Count);
        var row = table.Rows[0];
        object Cell(string column) => row[table.Columns.ToList().IndexOf(column)];

        Assert.Equal("a.pdf", Cell("Source"));
        Assert.Equal("TaxInvoice", Cell("DocumentType"));
        Assert.Equal(305, Cell("DocumentTypeCode"));
        Assert.Equal(new DateTime(2026, 7, 15), Cell("Date"));
        Assert.Equal(118m, Cell("Total"));
        Assert.Equal(2, Cell("LineItemCount"));
        Assert.Equal(100m, Cell("LineItemsTotal"));
        Assert.Equal(false, Cell("IsDigitallySigned"));

        // A line without an amount leaves the sum unknown rather than short.
        Assert.Null(table.Rows[1][table.Columns.ToList().IndexOf("LineItemsTotal")]);
    }

    /// <summary>
    /// A null line item and a null finding — the model is settable — are skipped by all three
    /// tables alike: the invoice row counts what the other two write. The same invoice without them
    /// is the control.
    /// </summary>
    [Fact]
    public void NullItemsAndFindingsAreSkippedByEveryTable()
    {
        var inv = Invoice("a.pdf", "1", 60m, 40m);
        int findings = inv.Findings.Count;
        inv.LineItems.Insert(1, null);
        inv.Findings.Add(null);

        var invoices = IsraeliInvoiceTables.Invoices(new[] { inv });
        object Cell(string column) => invoices.Rows[0][invoices.Columns.ToList().IndexOf(column)];

        Assert.Equal(2, Cell("LineItemCount"));
        Assert.Equal(100m, Cell("LineItemsTotal"));
        Assert.Equal(2, IsraeliInvoiceTables.LineItems(new[] { inv }).Rows.Count);
        Assert.Equal(findings, IsraeliInvoiceTables.Findings(new[] { inv }).Rows.Count);
        Assert.Equal(findings, (int)Cell("Errors") + (int)Cell("Warnings")
                               + inv.Findings.Count(f => f?.Severity == InvoiceFindingSeverity.Info));
    }

    /// <summary>
    /// Line amounts too large to add leave the sum empty rather than throwing: the row is still
    /// written, with its count.
    /// </summary>
    [Fact]
    public void HugeLineAmountsLeaveTheSumEmpty()
    {
        var inv = Invoice("a.pdf", "1", decimal.MaxValue, decimal.MaxValue);

        var table = IsraeliInvoiceTables.Invoices(new[] { inv });

        Assert.Equal(2, table.Rows[0][table.Columns.ToList().IndexOf("LineItemCount")]);
        Assert.Null(table.Rows[0][table.Columns.ToList().IndexOf("LineItemsTotal")]);
    }

    /// <summary>One row per line item, carrying its invoice's source and number, with a one-based page.</summary>
    [Fact]
    public void TheLineItemsTableHasOneRowPerItem()
    {
        var table = IsraeliInvoiceTables.LineItems(new[] { Invoice("a.pdf", "1", 60m, 40m), Invoice("b.pdf", "2", 5m) });

        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new object[] { "b.pdf", "2", 1, 1, null, "פריט 1", null, null, null, 5m }, table.Rows[2]);
    }

    /// <summary>One row per finding, so they can be filtered and counted across invoices.</summary>
    [Fact]
    public void TheFindingsTableHasOneRowPerFinding()
    {
        var inv = Invoice("a.pdf", "1", 100m);
        var table = IsraeliInvoiceTables.Findings(new[] { inv });

        Assert.Equal(inv.Findings.Count, table.Rows.Count);
        Assert.Contains(table.Rows, r => (string)r[3] == InvoiceFindingCodes.NotDigitallySigned && (string)r[2] == "Info");
    }

    // ================================================================
    //  CSV
    // ================================================================

    /// <summary>
    /// Numbers are written with a full stop and no grouping, dates as yyyy-MM-dd, booleans as
    /// words, an empty cell as nothing — whatever the machine's culture.
    /// </summary>
    [Fact]
    public void CsvValuesAreCultureInvariant()
    {
        var table = new InvoiceDataTable("t", new[] { "n", "d", "b", "i", "e" },
            new List<IReadOnlyList<object>> { new object[] { 1234.5m, new DateTime(2026, 7, 15), true, 7, null } });

        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("n,d,b,i,e\r\n1234.5,2026-07-15,true,7,\r\n", table.ToCsv());
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// Text a spreadsheet would run as a formula is written with a leading apostrophe; a negative
    /// number stays a number.
    /// </summary>
    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("ספק", "ספק")]
    public void TextThatWouldRunAsAFormulaIsDefused(string text, string expected)
    {
        Assert.Equal(expected, InvoiceDataTable.Field(text));
        Assert.Equal("-5", InvoiceDataTable.Field(-5m));
    }

    /// <summary>A field holding a comma, a quote, a line break or edge spaces is quoted, the quote doubled.</summary>
    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("סה\"כ", "\"סה\"\"כ\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData(" padded", "\" padded\"")]
    [InlineData("plain", "plain")]
    public void AFieldIsQuotedWhenItNeedsToBe(string text, string expected)
    {
        Assert.Equal(expected, InvoiceDataTable.Field(text));
    }

    /// <summary>
    /// The written file starts with a UTF-8 byte-order mark — without it Excel reads Hebrew in the
    /// system code page — and the Hebrew round-trips.
    /// </summary>
    [Fact]
    public void TheCsvFileCarriesAByteOrderMarkAndKeepsHebrew()
    {
        var table = IsraeliInvoiceTables.LineItems(new[] { Invoice("a.pdf", "1", 60m) });
        using var ms = new MemoryStream();

        table.WriteCsv(ms);

        byte[] bytes = ms.ToArray();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        string text = new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("פריט 1", text);
        Assert.StartsWith("Source,InvoiceNumber,Line,", text, StringComparison.Ordinal);
    }

    /// <summary>A row of the wrong length is refused when the table is built, not when it is written.</summary>
    [Fact]
    public void ARaggedRowIsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new InvoiceDataTable("t", new[] { "a", "b" },
            new List<IReadOnlyList<object>> { new object[] { 1 } }));
        Assert.Contains("Row 0", ex.Message);
    }
}
