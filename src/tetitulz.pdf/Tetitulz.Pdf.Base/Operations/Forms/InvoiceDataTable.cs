// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// A rectangular table of typed values — the shape invoices take for analysis: one row per
    /// invoice, per line item or per finding (<see cref="IsraeliInvoiceTables"/>), written as CSV
    /// here or as a worksheet by <c>Tetitulz.Pdf.Plugins.Docx</c>'s <c>InvoiceWorkbook</c>.
    /// </summary>
    /// <remarks>
    /// A cell holds <c>null</c>, a <see cref="string"/>, a <see cref="decimal"/>, an
    /// <see cref="int"/>, a <see cref="bool"/> or a <see cref="DateTime"/> — typed, so a writer can
    /// put a number in a spreadsheet as a number and a date as a date.
    /// </remarks>
    public sealed class InvoiceDataTable
    {
        /// <param name="name">The table's name — a worksheet's name.</param>
        /// <param name="columns">The column names.</param>
        /// <param name="rows">The rows; each as long as <paramref name="columns"/>.</param>
        /// <exception cref="ArgumentException">A row's length is not the number of columns.</exception>
        public InvoiceDataTable(string name, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object>> rows)
        {
            Name = name ?? string.Empty;
            Columns = columns ?? throw new ArgumentNullException(nameof(columns));
            Rows = rows ?? throw new ArgumentNullException(nameof(rows));
            for (int r = 0; r < rows.Count; r++)
                if (rows[r] == null || rows[r].Count != columns.Count)
                    throw new ArgumentException($"Row {r} has {rows[r]?.Count ?? 0} cells; the table has {columns.Count} columns.", nameof(rows));
        }

        /// <summary>The table's name.</summary>
        public string Name { get; }

        /// <summary>The column names.</summary>
        public IReadOnlyList<string> Columns { get; }

        /// <summary>The rows, each a list of typed cells as long as <see cref="Columns"/>.</summary>
        public IReadOnlyList<IReadOnlyList<object>> Rows { get; }

        /// <summary>
        /// The table as RFC 4180 CSV: a header row, comma-separated, CRLF line ends, fields quoted
        /// where they need it. Numbers are written with a full stop and no grouping, dates as
        /// <c>yyyy-MM-dd</c>, booleans as <c>true</c>/<c>false</c>.
        /// </summary>
        /// <remarks>
        /// <b>A text cell starting with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, a tab or a carriage
        /// return is written with a leading apostrophe</b>, so a spreadsheet shows it rather than
        /// running it as a formula: the text came from a PDF, and whoever wrote the PDF chose it.
        /// Numbers are written as numbers, so a negative amount is not touched.
        /// </remarks>
        public string ToCsv()
        {
            var sb = new StringBuilder();
            AppendRow(sb, Columns.Cast<object>().ToList());
            foreach (var row in Rows) AppendRow(sb, row);
            return sb.ToString();
        }

        /// <summary>
        /// Writes <see cref="ToCsv"/> to <paramref name="output"/> as UTF-8 <b>with</b> a byte-order
        /// mark — without one, Excel reads the file in the system code page and Hebrew comes out as
        /// question marks. The stream is not closed.
        /// </summary>
        public void WriteCsv(Stream output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            byte[] bom = encoding.GetPreamble();
            output.Write(bom, 0, bom.Length);
            byte[] body = encoding.GetBytes(ToCsv());
            output.Write(body, 0, body.Length);
        }

        private static void AppendRow(StringBuilder sb, IReadOnlyList<object> row)
        {
            for (int i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Field(row[i]));
            }
            sb.Append("\r\n");
        }

        /// <summary>One cell as CSV text (see <see cref="ToCsv"/>).</summary>
        internal static string Field(object value)
        {
            switch (value)
            {
                case null: return string.Empty;
                case decimal d: return d.ToString(CultureInfo.InvariantCulture);
                case int n: return n.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "true" : "false";
                case DateTime dt: return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            string s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (s.Length > 0 && (s[0] == '=' || s[0] == '+' || s[0] == '-' || s[0] == '@' || s[0] == '\t' || s[0] == '\r'))
                s = "'" + s;
            bool quote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                         || (s.Length > 0 && (s[0] == ' ' || s[s.Length - 1] == ' '));
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    /// <summary>
    /// Israeli invoices as <see cref="InvoiceDataTable"/>s for analysis: one row per invoice, per
    /// line item, per finding — joined by <c>Source</c> and <c>InvoiceNumber</c>.
    /// </summary>
    public static class IsraeliInvoiceTables
    {
        /// <summary>One row per invoice: its fields, its line-item count and sum, and its finding counts.</summary>
        /// <remarks>
        /// A null line item or finding — the model is settable — is skipped, as the line-item and
        /// finding tables skip it, so the counts here are the rows there. A line-item sum too large
        /// for a decimal is left empty, as one with a line of no amount is.
        /// </remarks>
        public static InvoiceDataTable Invoices(IEnumerable<IsraeliInvoice> invoices)
        {
            var columns = new[]
            {
                "Source", "DocumentType", "DocumentTypeCode", "InvoiceNumber", "Date", "SupplierId",
                "CustomerId", "AllocationNumber", "Currency", "Subtotal", "ExemptAmount", "VatRate", "Vat", "Total",
                "LineItemCount", "LineItemsTotal", "IsDigitallySigned", "TextSource", "Errors", "Warnings",
            };
            var rows = new List<IReadOnlyList<object>>();
            foreach (var inv in invoices ?? Enumerable.Empty<IsraeliInvoice>())
            {
                if (inv == null) continue;
                var items = (inv.LineItems ?? new List<InvoiceLineItem>()).Where(i => i != null).ToList();
                var findings = (inv.Findings ?? new List<InvoiceFinding>()).Where(f => f != null).ToList();
                rows.Add(new object[]
                {
                    inv.Source, inv.DocumentType.ToString(), (int)inv.DocumentType, inv.InvoiceNumber, inv.Date,
                    inv.SupplierId, inv.CustomerId, inv.AllocationNumber, inv.Currency, inv.Subtotal,
                    inv.ExemptAmount, inv.VatRate, inv.Vat, inv.Total, items.Count,
                    LineItemsTotal(items),
                    inv.IsDigitallySigned, inv.TextSource.ToString(),
                    findings.Count(f => f.Severity == InvoiceFindingSeverity.Error),
                    findings.Count(f => f.Severity == InvoiceFindingSeverity.Warning),
                });
            }
            return new InvoiceDataTable("Invoices", columns, rows);
        }

        // The lines' amounts added up: empty when a line has none, or the sum is past a decimal.
        private static decimal? LineItemsTotal(List<InvoiceLineItem> items)
        {
            if (items.Count == 0 || items.Any(i => i.Amount == null)) return null;
            decimal sum = 0m;
            try
            {
                foreach (var item in items) sum += item.Amount.Value;
            }
            catch (OverflowException)
            {
                return null;
            }
            return sum;
        }

        /// <summary>One row per line item, carrying its invoice's source and number.</summary>
        public static InvoiceDataTable LineItems(IEnumerable<IsraeliInvoice> invoices)
        {
            var columns = new[]
            {
                "Source", "InvoiceNumber", "Line", "Page", "CatalogNumber", "Description", "Quantity",
                "UnitPrice", "Discount", "Amount",
            };
            var rows = new List<IReadOnlyList<object>>();
            foreach (var inv in invoices ?? Enumerable.Empty<IsraeliInvoice>())
            {
                if (inv?.LineItems == null) continue;
                foreach (var item in inv.LineItems)
                {
                    if (item == null) continue;
                    rows.Add(new object[]
                    {
                        inv.Source, inv.InvoiceNumber, item.Line,
                        item.PageIndex >= 0 ? item.PageIndex + 1 : (int?)null,
                        item.CatalogNumber, item.Description, item.Quantity, item.UnitPrice, item.Discount, item.Amount,
                    });
                }
            }
            return new InvoiceDataTable("LineItems", columns, rows);
        }

        /// <summary>One row per finding, carrying its invoice's source and number.</summary>
        public static InvoiceDataTable Findings(IEnumerable<IsraeliInvoice> invoices)
        {
            var columns = new[] { "Source", "InvoiceNumber", "Severity", "Code", "Message" };
            var rows = new List<IReadOnlyList<object>>();
            foreach (var inv in invoices ?? Enumerable.Empty<IsraeliInvoice>())
            {
                if (inv?.Findings == null) continue;
                foreach (var f in inv.Findings)
                    if (f != null)
                        rows.Add(new object[] { inv.Source, inv.InvoiceNumber, f.Severity.ToString(), f.Code, f.Message });
            }
            return new InvoiceDataTable("Findings", columns, rows);
        }
    }
}
