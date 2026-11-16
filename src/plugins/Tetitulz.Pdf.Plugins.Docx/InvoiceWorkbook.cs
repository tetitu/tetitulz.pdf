// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Tetitulz.Pdf.Base.Operations.Forms;

namespace Tetitulz.Pdf.Plugins.Docx
{
    /// <summary>
    /// Writes <see cref="InvoiceDataTable"/>s — the invoices, line items and findings
    /// <see cref="IsraeliInvoiceTables"/> builds — as an Excel workbook, one worksheet per table.
    /// </summary>
    /// <remarks>
    /// <para><b>Cells keep their types.</b> An amount is a number, a date a date (formatted
    /// <c>yyyy-mm-dd</c>), a flag a boolean, so the sheet sums, sorts and filters without
    /// conversion. Text is written as an inline string, never as a formula, whatever it starts
    /// with: it came from a PDF.</para>
    /// <para><b>Made for analysis:</b> the header row is bold, frozen and carries an auto-filter.
    /// Sheets read right to left by default, as a Hebrew reader expects; pass
    /// <c>rightToLeft: false</c> for left to right.</para>
    /// </remarks>
    public static class InvoiceWorkbook
    {
        // Cell format indexes in the stylesheet below.
        private const uint DateFormat = 1;
        private const uint HeaderFormat = 2;

        /// <summary>The workbook as XLSX bytes.</summary>
        public static byte[] Write(IEnumerable<InvoiceDataTable> tables, bool rightToLeft = true)
        {
            using var ms = new MemoryStream();
            Write(ms, tables, rightToLeft);
            return ms.ToArray();
        }

        /// <summary>Writes the workbook to <paramref name="output"/>, which is not closed.</summary>
        /// <exception cref="ArgumentException">No tables were given.</exception>
        public static void Write(Stream output, IEnumerable<InvoiceDataTable> tables, bool rightToLeft = true)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            var list = (tables ?? throw new ArgumentNullException(nameof(tables))).Where(t => t != null).ToList();
            if (list.Count == 0) throw new ArgumentException("A workbook needs at least one table.", nameof(tables));

            // The package is built in memory: SpreadsheetDocument needs a seekable, readable stream.
            using var buffer = new MemoryStream();
            using (var doc = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook, true))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new Workbook();
                var styles = wbPart.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = BuildStylesheet();
                var sheets = wbPart.Workbook.AppendChild(new Sheets());

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int t = 0; t < list.Count; t++)
                {
                    var wsPart = wbPart.AddNewPart<WorksheetPart>();
                    wsPart.Worksheet = BuildWorksheet(list[t], rightToLeft);
                    sheets.Append(new Sheet
                    {
                        Id = wbPart.GetIdOfPart(wsPart),
                        SheetId = (uint)(t + 1),
                        Name = UniqueSheetName(list[t].Name, t, names),
                    });
                }
            }
            buffer.Position = 0;
            buffer.CopyTo(output);
        }

        private static Worksheet BuildWorksheet(InvoiceDataTable table, bool rightToLeft)
        {
            var sheetData = new SheetData();

            var header = new Row { RowIndex = 1 };
            for (int c = 0; c < table.Columns.Count; c++)
            {
                var cell = TextCell(table.Columns[c] ?? string.Empty, c, 1);
                cell.StyleIndex = HeaderFormat;
                header.Append(cell);
            }
            sheetData.Append(header);

            for (int r = 0; r < table.Rows.Count; r++)
            {
                uint rowIndex = (uint)(r + 2);
                var row = new Row { RowIndex = rowIndex };
                var values = table.Rows[r];
                for (int c = 0; c < values.Count; c++)
                {
                    var cell = ValueCell(values[c], c, rowIndex);
                    if (cell != null) row.Append(cell);
                }
                sheetData.Append(row);
            }

            var view = new SheetView { WorkbookViewId = 0U, RightToLeft = rightToLeft };
            view.Append(new Pane
            {
                VerticalSplit = 1D,
                TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft,
                State = PaneStateValues.Frozen,
            });

            var worksheet = new Worksheet(new SheetViews(view), sheetData);
            if (table.Columns.Count > 0)
            {
                string last = PdfToExcelConverter.ColumnName(table.Columns.Count - 1);
                worksheet.Append(new AutoFilter
                {
                    Reference = "A1:" + last + (table.Rows.Count + 1).ToString(CultureInfo.InvariantCulture),
                });
            }
            return worksheet;
        }

        /// <summary>One typed cell, or <c>null</c> for an empty one.</summary>
        private static Cell? ValueCell(object? value, int column, uint rowIndex)
        {
            string reference = PdfToExcelConverter.ColumnName(column) + rowIndex.ToString(CultureInfo.InvariantCulture);
            switch (value)
            {
                case null:
                    return null;
                case decimal d:
                    return new Cell
                    {
                        CellReference = reference,
                        DataType = CellValues.Number,
                        CellValue = new CellValue(d.ToString(CultureInfo.InvariantCulture)),
                    };
                case int n:
                    return new Cell
                    {
                        CellReference = reference,
                        DataType = CellValues.Number,
                        CellValue = new CellValue(n.ToString(CultureInfo.InvariantCulture)),
                    };
                case bool b:
                    return new Cell
                    {
                        CellReference = reference,
                        DataType = CellValues.Boolean,
                        CellValue = new CellValue(b ? "1" : "0"),
                    };
                case DateTime dt:
                    // Excel's serials agree with the calendar only from 1900-03-01 (it keeps 1900 as
                    // a leap year), and none exists before year 100: an earlier date is written as
                    // the CSV writes it, as text.
                    if (dt < FirstSerialDate)
                        return TextCell(dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), column, rowIndex);
                    return new Cell
                    {
                        CellReference = reference,
                        StyleIndex = DateFormat,
                        CellValue = new CellValue(dt.ToOADate().ToString(CultureInfo.InvariantCulture)),
                    };
                default:
                    return TextCell(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, column, rowIndex);
            }
        }

        /// <summary>The first date written as an Excel date serial; an earlier one is text.</summary>
        internal static readonly DateTime FirstSerialDate = new DateTime(1900, 3, 1);

        // An inline string: the cell's text as written, never a formula, and never decoded.
        private static Cell TextCell(string text, int column, uint rowIndex)
            => new Cell
            {
                CellReference = PdfToExcelConverter.ColumnName(column) + rowIndex.ToString(CultureInfo.InvariantCulture),
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(XmlTextSanitizer.ForSpreadsheet(text)) { Space = SpaceProcessingModeValues.Preserve }),
            };

        // Fonts: regular, bold. Fills: the two every stylesheet must start with. Cell formats:
        // 0 default, 1 a date as yyyy-mm-dd, 2 bold for the header.
        private static Stylesheet BuildStylesheet()
            => new Stylesheet(
                new NumberingFormats(new NumberingFormat { NumberFormatId = 164U, FormatCode = "yyyy-mm-dd" }) { Count = 1U },
                new Fonts(new Font(), new Font(new Bold())) { Count = 2U },
                new Fills(
                    new Fill(new PatternFill { PatternType = PatternValues.None }),
                    new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2U },
                new Borders(new Border()) { Count = 1U },
                new CellFormats(
                    new CellFormat(),
                    new CellFormat { NumberFormatId = 164U, ApplyNumberFormat = true },
                    new CellFormat { FontId = 1U, ApplyFont = true }) { Count = 3U });

        /// <summary>The longest sheet name Excel accepts.</summary>
        private const int MaxSheetName = 31;

        // Excel's rules: 1 to 31 characters, none of : \ / ? * [ ], no apostrophe at either end,
        // not "History" (reserved), unique ignoring case — and, as for cell text, only characters
        // XML allows. A cut to 31 never splits a surrogate pair, and is trimmed again: the cut can
        // expose an apostrophe or a space.
        private static string UniqueSheetName(string name, int index, HashSet<string> used)
        {
            var chars = XmlTextSanitizer.Sanitize(name ?? string.Empty).Where(ch => ":\\/?*[]".IndexOf(ch) < 0).ToArray();
            string baseName = TrimName(Cut(new string(chars), MaxSheetName));
            if (baseName.Length == 0) baseName = "Sheet" + (index + 1).ToString(CultureInfo.InvariantCulture);

            string candidate = baseName;
            for (int n = 2; used.Contains(candidate) || IsReserved(candidate); n++)
            {
                string suffix = " (" + n.ToString(CultureInfo.InvariantCulture) + ")";
                candidate = TrimName(Cut(baseName, MaxSheetName - suffix.Length)) + suffix;
            }
            used.Add(candidate);
            return candidate;
        }

        private static bool IsReserved(string name) => string.Equals(name, "History", StringComparison.OrdinalIgnoreCase);

        // Spaces and apostrophes off both ends, until neither is left.
        private static string TrimName(string name)
        {
            string trimmed;
            do
            {
                trimmed = name;
                name = name.Trim().Trim('\'');
            }
            while (name != trimmed);
            return name;
        }

        // At most `length` characters, one fewer when the cut would split a surrogate pair.
        private static string Cut(string name, int length)
        {
            if (name.Length <= length) return name;
            if (length > 0 && char.IsHighSurrogate(name[length - 1])) length--;
            return name.Substring(0, length);
        }
    }
}
