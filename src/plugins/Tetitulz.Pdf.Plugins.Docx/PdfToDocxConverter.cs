// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Docx
{
    /// <summary>
    /// Converts PDF to DOCX by extracting text per page and building a Word document.
    /// Uses structured text extraction to preserve paragraphs and approximate layout.
    /// </summary>
    public static class PdfToDocxConverter
    {
        [ThreadStatic] private static string? _lastError;

        /// <summary>
        /// Why the last <see cref="Convert(Stream)"/> on this thread produced less than the caller
        /// probably expected, or <c>null</c> when every page yielded text.
        ///
        /// <para>Conversion returns a well-formed .docx whatever it finds, so a scanned
        /// (image-only) PDF converts to a valid, <b>empty</b> document that is byte-shaped exactly
        /// like a successful conversion. This is the only channel that tells the two apart, and it
        /// is what points a caller at the next step (OCR) rather than leaving them to guess.</para>
        ///
        /// <para>Per-thread (CLAUDE.md), and reset at the start of every conversion on every path,
        /// so a value read here always belongs to the call this thread just made.</para>
        /// </summary>
        public static string? LastError => _lastError;

        /// <summary>
        /// Appends each detected table as a Word table, one row per detected row.
        /// </summary>
        /// <remarks>
        /// Cells are emitted at their detected column, and a gap in the middle of a row is filled
        /// with an empty cell rather than closed up: Word places cells by position in the row, so
        /// closing the gap would shift every later value one column left - the same failure the
        /// spreadsheet path avoids by writing an explicit CellReference.
        /// </remarks>
        private static void AppendTables(Body body, List<PdfTable>? tables)
        {
            if (tables == null) return;

            foreach (PdfTable table in tables)
            {
                var wordTable = new Table(new TableProperties(
                    new TableBorders(
                        new TopBorder    { Val = BorderValues.Single, Size = 4 },
                        new BottomBorder { Val = BorderValues.Single, Size = 4 },
                        new LeftBorder   { Val = BorderValues.Single, Size = 4 },
                        new RightBorder  { Val = BorderValues.Single, Size = 4 },
                        new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                        new InsideVerticalBorder   { Val = BorderValues.Single, Size = 4 })));

                foreach (PdfTableRow tableRow in table.Rows)
                {
                    var row = new TableRow();
                    int next = 0;
                    foreach (PdfTableCell cell in tableRow.Cells.OrderBy(c => c.Column))
                    {
                        for (; next < cell.Column; next++) row.Append(EmptyCell());
                        row.Append(TextCell(cell.Text));
                        next = cell.Column + 1;
                    }
                    for (; next < table.ColumnCount; next++) row.Append(EmptyCell());
                    wordTable.Append(row);
                }

                body.Append(wordTable);
                body.Append(new Paragraph());
            }
        }

        private static TableCell EmptyCell() => new TableCell(new Paragraph(new Run()));

        private static TableCell TextCell(string text)
            => new TableCell(new Paragraph(new Run(
                new Text(XmlTextSanitizer.Sanitize(text ?? string.Empty))
                { Space = SpaceProcessingModeValues.Preserve })));

        /// <summary>Converts PDF to DOCX bytes.</summary>
        public static byte[] Convert(Stream pdfInput)
        {
            // Reset first, on every path: a slot a caller reads *after* an operation must not be
            // able to hand back the previous document's answer.
            _lastError = null;

            if (pdfInput == null) throw new ArgumentNullException(nameof(pdfInput));

            // The table pre-pass and the document each read the stream from its start, so a stream
            // that cannot seek back (a network or a decompressing stream) is copied once and both
            // read the copy; read directly, the pre-pass would drain it and the document would open
            // nothing.
            if (!pdfInput.CanSeek)
            {
                var buffered = new MemoryStream();
                pdfInput.CopyTo(buffered);
                buffered.Position = 0;
                pdfInput = buffered;
            }

            // Before the document is opened, and the position restored afterwards: the extractor
            // makes its own pass over the same stream, and two readers sharing one cursor is the
            // kind of thing that works until a page is large enough to buffer differently.
            var tablesByPage = ConverterTableSupport.ByPage(pdfInput);

            using var doc = PdfDocument.Open(pdfInput);
            Tetitulz.Pdf.Base.Operations.EncryptedRead.RefuseConversion(doc, "DOCX");
            if (doc.Pages.Count == 0)
                _lastError = "The document has no pages, so the DOCX is empty.";

            using var ms = new MemoryStream();
            int blankPages = 0, widePages = 0;
            using (var wordDoc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
            {
                var mainPart = wordDoc.AddMainDocumentPart();
                var body = new Body();

                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    // The break is emitted BEFORE the page's own content, not after it, so that a
                    // page contributing no text still contributes its pagination: emitted after, the
                    // `continue` below would take it with it, and the pages on either side of a
                    // full-page image would run together in one Word page.
                    if (i > 0)
                    {
                        body.Append(new Paragraph(
                            new Run(new Break { Type = BreakValues.Page })));
                    }

                    string pageText = PdfTextExtractor.ExtractText(doc.Pages[i]);
                    if (string.IsNullOrWhiteSpace(pageText))
                    {
                        blankPages++;
                        continue;
                    }

                    // Tables first, as real Word tables, then the lines they do not account for.
                    // A table rendered as paragraphs is not a lesser rendering of a table - it is
                    // the row structure thrown away, and no reader can get it back.
                    // A table wider than Word's own column limit is written as its lines: each row is
                    // padded to the table's columns, and the detector's columns times its rows is the
                    // file's to choose.
                    tablesByPage.TryGetValue(i, out var page);
                    var pageTables = page?.Tables.Where(t => t.ColumnCount <= PdfLimits.MaxDocxTableColumns).ToList();
                    if (page != null && pageTables!.Count < page.Tables.Count)
                        widePages++;
                    AppendTables(body, pageTables);
                    var covered = ConverterTableSupport.CoveredLines(pageTables, page?.Lines);

                    // Split into paragraphs by newlines
                    var lines = pageText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    foreach (string line in lines)
                    {
                        if (covered.Count > 0 && ConverterTableSupport.IsCovered(line, covered))
                            continue;

                        // XML 1.0 forbids most C0 controls and OpenXml's writer enforces that from
                        // Dispose — after this using block — so one such character would abort the
                        // whole conversion. See XmlTextSanitizer.
                        var para = new Paragraph(
                            new Run(new Text(XmlTextSanitizer.Sanitize(line))
                                { Space = SpaceProcessingModeValues.Preserve }));
                        body.Append(para);
                    }
                }

                mainPart.Document = new Document(body);
            }

            if (blankPages > 0)
            {
                _lastError =
                    $"{blankPages} of {doc.Pages.Count} pages yielded no extractable text; the " +
                    "document may be scanned (image-only), in which case OCR is required before " +
                    "text conversion can produce anything.";
            }
            if (widePages > 0)
            {
                string wide = $"A table on {widePages} page(s) has more than {PdfLimits.MaxDocxTableColumns} " +
                              "columns, the most Word lays out; it is written as lines of text.";
                _lastError = _lastError == null ? wide : _lastError + " " + wide;
            }
            return ms.ToArray();
        }

        /// <summary>Converts PDF file to DOCX file.</summary>
        public static void Convert(string pdfPath, string docxPath)
        {
            using var input = File.OpenRead(pdfPath);
            byte[] docx = Convert(input);
            File.WriteAllBytes(docxPath, docx);
        }

        /// <summary>Converts PDF bytes to DOCX bytes.</summary>
        public static byte[] Convert(byte[] pdfBytes)
        {
            using var ms = new MemoryStream(pdfBytes);
            return Convert(ms);
        }
    }

    /// <summary>
    /// The table-detection half the PDF-to-Office converters share: which tables a page has, and
    /// which of its text lines those tables already account for.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation, because the two converters must agree.</b> The Word and Excel
    /// paths make the same two decisions — is this a table worth laying out, and has this line
    /// already been emitted inside one — and a second copy is where they would start answering
    /// differently for the same document.</para>
    ///
    /// <para><b>Only tables with at least two columns count.</b> A one-column "table" is a
    /// paragraph the detector happened to box; laying it out as a table gains nothing and risks the
    /// row split. The threshold is what keeps a false positive from scattering body text.</para>
    /// </remarks>
    internal static class ConverterTableSupport
    {
        /// <summary>A page's tables worth laying out, and the page's structured lines that say where text stands.</summary>
        internal sealed class PageTables
        {
            internal PageTables(List<PdfTable> tables) { Tables = tables; }

            internal List<PdfTable> Tables { get; }

            /// <summary>The page's lines with their positions, for <see cref="CoveredLines"/>; null when unread.</summary>
            internal IReadOnlyList<PdfTextLine>? Lines { get; set; }
        }

        /// <summary>Tables the extractor found, grouped by page index, with each such page's lines.</summary>
        /// <remarks>
        /// Extraction failing is not the conversion failing: it is heuristic and makes its own pass
        /// over the content, so a document it cannot read still converts to the line-per-row form.
        /// An empty map is that fallback, and the stream's position is restored either way. The
        /// structured lines are read only when some page has a table, and kept only for those pages.
        /// </remarks>
        internal static Dictionary<int, PageTables> ByPage(Stream input)
        {
            var byPage = new Dictionary<int, PageTables>();
            if (input == null) return byPage;

            long start = input.CanSeek ? input.Position : 0L;
            try
            {
                // The text once, for the tables and the lines that place them alike: the stream door
                // extracted it again inside, the document's whole text twice.
                var layouts = PdfStructuredTextExtractor.Extract(input);
                if (input.CanSeek) input.Position = start;
                var graphics = PdfGraphicsExtractor.Extract(input);

                foreach (PdfTable table in PdfTableExtractor.Extract(layouts, graphics))
                {
                    if (!IsWorthLayingOut(table)) continue;
                    if (!byPage.TryGetValue(table.PageIndex, out var page))
                        byPage[table.PageIndex] = page = new PageTables(new List<PdfTable>());
                    page.Tables.Add(table);
                }

                foreach (var page in byPage)
                    if (page.Key < layouts.Count) page.Value.Lines = layouts[page.Key].Lines;
            }
            catch (Exception)
            {
                byPage.Clear();
            }
            finally
            {
                if (input.CanSeek) input.Position = start;
            }
            return byPage;
        }

        /// <summary>
        /// Whether a detected table is a grid worth laying out, rather than prose the detector
        /// boxed.
        /// </summary>
        /// <remarks>
        /// <para><b>The column count alone is not the test.</b> The row shape is what this code
        /// relies on, so it is what it checks: an ordinary page of prose must never become a table
        /// and lose its paragraphs.</para>
        ///
        /// <para>So the test is structural: at least <b>two rows that each carry two or more
        /// non-empty cells</b>. A grid has a header and a body; one row that happens to hold two
        /// words side by side does not. This is deliberately stricter than
        /// <see cref="PdfTable.Confidence"/>, which is a float this code would have to pick a
        /// threshold for — the row shape is the thing actually being relied on.</para>
        /// </remarks>
        private static bool IsWorthLayingOut(PdfTable table)
        {
            if (table.ColumnCount < 2) return false;

            int populated = 0;
            foreach (PdfTableRow row in table.Rows)
            {
                int filled = 0;
                foreach (PdfTableCell cell in row.Cells)
                    if (!string.IsNullOrWhiteSpace(cell.Text)) filled++;
                if (filled >= 2 && ++populated >= 2) return true;
            }
            return false;
        }

        /// <summary>
        /// The whitespace-free texts a line may have and be one the emitted <paramref name="tables"/>
        /// already carry: each whole row, and each line of the page's layout that lies inside a row.
        /// </summary>
        /// <remarks>
        /// <para><b>Inside a row is a position, not a substring.</b> Counting a line as covered
        /// whenever its text is contained in some row's would drop a page number "1" under a table
        /// with a "12" in it, or a heading or stand-alone total equal to a cell; and searching every
        /// line against every row is quadratic in the page's lines. The plain text the converters
        /// write carries no position, so the page's structured lines say where text stands: one
        /// whose centre falls in a row's vertical band and inside its table's horizontal extent is
        /// the table's. A line of the plain text is then covered when it equals, whitespace aside, a
        /// whole row or such a line — a set lookup.</para>
        /// <para>Whitespace-insensitive, because the extractor rebuilds cell text from glyph geometry
        /// and may space it differently from the line reader — the word-splitting caveat §4a records
        /// for all structured extraction. A line the two extractors split differently is written
        /// again rather than dropped.</para>
        /// </remarks>
        internal static HashSet<string> CoveredLines(IEnumerable<PdfTable>? tables, IReadOnlyList<PdfTextLine>? lines)
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            if (tables == null) return covered;

            // Each table's rows as disjoint vertical intervals, merged and sorted, and its horizontal extent: a
            // line's question is one binary search a table. The bands of all tables in one list, searched back
            // while a band could still reach the line, walked every band below it whenever one tall band of
            // another table kept the reach up — lines times bands (07's DoS hunt, F8).
            var regions = new List<(float[] Lo, float[] Hi, float Left, float Right)>();
            foreach (PdfTable table in tables)
            {
                float left = float.MaxValue, right = float.MinValue;
                var rows = new List<(float Lo, float Hi)>();
                foreach (PdfTableRow row in table.Rows)
                {
                    string joined = Squeeze(string.Concat(row.Cells.Select(c => c.Text)));
                    if (joined.Length > 0) covered.Add(joined);

                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (PdfTableCell cell in row.Cells)
                    {
                        lo = Math.Min(lo, Math.Min(cell.Y, cell.Y + cell.Height));
                        hi = Math.Max(hi, Math.Max(cell.Y, cell.Y + cell.Height));
                        left = Math.Min(left, cell.X);
                        right = Math.Max(right, cell.X + cell.Width);
                    }
                    if (lo <= hi) rows.Add((lo, hi));
                }
                if (rows.Count == 0 || regions.Count >= MaxPlacedTablesPerPage) continue;

                rows.Sort((a, b) => a.Lo.CompareTo(b.Lo));
                var los = new List<float>(rows.Count);
                var his = new List<float>(rows.Count);
                foreach (var (lo, hi) in rows)
                {
                    if (his.Count > 0 && lo <= his[his.Count - 1])
                        his[his.Count - 1] = Math.Max(his[his.Count - 1], hi);
                    else { los.Add(lo); his.Add(hi); }
                }
                regions.Add((los.ToArray(), his.ToArray(), left, right));
            }

            if (lines == null || regions.Count == 0) return covered;

            foreach (PdfTextLine line in lines)
            {
                if (line == null) continue;
                float cx = line.X + line.Width / 2f, cy = line.Y + line.Height / 2f;

                foreach (var region in regions)
                {
                    CoverageSteps++;
                    if (cx < region.Left || cx > region.Right) continue;
                    // The last interval starting at or below the centre: the intervals are disjoint, so only it can hold it.
                    int k = Array.BinarySearch(region.Lo, cy);
                    if (k < 0) k = ~k - 1;
                    if (k >= 0 && cy <= region.Hi[k])
                    {
                        string squeezed = Squeeze(line.Text);
                        if (squeezed.Length > 0) covered.Add(squeezed);
                        break;
                    }
                }
            }
            return covered;
        }

        /// <summary>
        /// The tables of one page whose position decides coverage; a line beside one past it is covered only by
        /// equalling a whole row, so it may be written again, never lost. Bounds a page's lines times its tables.
        /// </summary>
        internal const int MaxPlacedTablesPerPage = 64;

        /// <summary>
        /// Tables asked about lines in <see cref="CoveredLines"/> on this thread: the evidence a line costs its
        /// page's tables, not its page's rows.
        /// </summary>
        [ThreadStatic] internal static long CoverageSteps;

        /// <summary>
        /// Whether an emitted table already carries <paramref name="line"/>: a blank line, or one
        /// <see cref="CoveredLines"/> holds.
        /// </summary>
        internal static bool IsCovered(string line, HashSet<string> covered)
        {
            string squeezed = Squeeze(line);
            return squeezed.Length == 0 || covered.Contains(squeezed);
        }

        /// <summary>Removes all whitespace, so cell-boundary spacing cannot defeat a match.</summary>
        internal static string Squeeze(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Converts a PDF to a workbook: one sheet per page, with any tables the extractor finds laid
    /// out as <b>real cells</b> and the remaining text one line per row.
    /// </summary>
    /// <remarks>
    /// <para><b>Tables are real cells.</b> One string cell per line, with no
    /// <c>CellReference</c>, would put a table into column A as run-together sentences — the one
    /// shape a spreadsheet is useless for — so <c>PdfTableExtractor</c>'s tables are laid out as a
    /// grid.</para>
    ///
    /// <para><b>Only tables with at least two columns are laid out as grids.</b> A one-column
    /// "table" is a paragraph the detector happened to box, and treating it as a table would gain
    /// nothing while risking the row split. That threshold is what keeps a false positive from
    /// scattering body text across cells.</para>
    ///
    /// <para><b>Nothing is dropped.</b> Lines the tables already account for are not repeated
    /// below them; the test is that the line, whitespace aside, is a whole emitted row or a line
    /// standing inside one (<c>ConverterTableSupport.CoveredLines</c>). A line the two extractors
    /// split differently is written again rather than lost. A table wider than the worksheet
    /// (<see cref="PdfLimits.MaxSpreadsheetColumns"/>) is written as its lines, and a page's rows past
    /// the worksheet's last (<see cref="PdfLimits.MaxSpreadsheetRows"/>) are not written: both are said
    /// in <see cref="LastError"/>.</para>
    /// </remarks>
    public static class PdfToExcelConverter
    {
        [ThreadStatic] private static string? _lastError;

        /// <summary>
        /// Why the last <see cref="Convert(byte[])"/> on this thread wrote less than the document holds, or
        /// <c>null</c> when it wrote everything: a detected table wider than
        /// <see cref="PdfLimits.MaxSpreadsheetColumns"/> is written as lines of text, and a page's rows past
        /// <see cref="PdfLimits.MaxSpreadsheetRows"/> are not written, since a cell outside a worksheet's grid
        /// is one a spreadsheet application repairs or refuses the workbook over. Per thread, and reset at the
        /// start of every conversion.
        /// </summary>
        public static string? LastError => _lastError;

        /// <summary>Converts PDF bytes to XLSX bytes.</summary>
        public static byte[] Convert(byte[] pdfBytes)
        {
            _lastError = null;
            int widePages = 0, cutPages = 0;

            using var ms = new MemoryStream();
            using (var doc = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Create(ms,
                DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook, true))
            {
                var wbPart = doc.AddWorkbookPart();
                wbPart.Workbook = new DocumentFormat.OpenXml.Spreadsheet.Workbook();
                var sheets = wbPart.Workbook.AppendChild(new DocumentFormat.OpenXml.Spreadsheet.Sheets());

                using var pdfMs = new MemoryStream(pdfBytes);
                using var pdfDoc = PdfDocument.Open(pdfMs);
                Tetitulz.Pdf.Base.Operations.EncryptedRead.RefuseConversion(pdfDoc, "XLSX");

                using var tableMs = new MemoryStream(pdfBytes, writable: false);
                var tablesByPage = ConverterTableSupport.ByPage(tableMs);

                // CT_Sheets requires a sheet: a document with no pages gets one, empty, rather
                // than a workbook Excel calls corrupt.
                if (pdfDoc.Pages.Count == 0)
                {
                    var emptyPart = wbPart.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
                    emptyPart.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(
                        new DocumentFormat.OpenXml.Spreadsheet.SheetData());
                    sheets.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                    {
                        Id = wbPart.GetIdOfPart(emptyPart),
                        SheetId = 1U,
                        Name = "Page 1"
                    });
                }

                for (int i = 0; i < pdfDoc.Pages.Count; i++)
                {
                    var wsPart = wbPart.AddNewPart<DocumentFormat.OpenXml.Packaging.WorksheetPart>();
                    var sheetData = new DocumentFormat.OpenXml.Spreadsheet.SheetData();

                    // A table wider than the worksheet is written as its lines, and a row past the
                    // worksheet's last is not written: both are said in LastError.
                    tablesByPage.TryGetValue(i, out var page);
                    var pageTables = page?.Tables.Where(t => t.ColumnCount <= PdfLimits.MaxSpreadsheetColumns).ToList();
                    if (page != null && pageTables!.Count < page.Tables.Count)
                        widePages++;
                    bool rowsCut = false;
                    uint rowIdx = WriteTables(sheetData, pageTables, startRow: 1, ref rowsCut);

                    string text = PdfTextExtractor.ExtractText(pdfDoc.Pages[i]);
                    WriteRemainingLines(sheetData, text, pageTables, page?.Lines, ref rowIdx, ref rowsCut);
                    if (rowsCut) cutPages++;

                    wsPart.Worksheet = new DocumentFormat.OpenXml.Spreadsheet.Worksheet(sheetData);
                    sheets.Append(new DocumentFormat.OpenXml.Spreadsheet.Sheet
                    {
                        Id = wbPart.GetIdOfPart(wsPart),
                        SheetId = (uint)(i + 1),
                        Name = $"Page {i + 1}"
                    });
                }
            }

            if (widePages > 0)
                _lastError = $"A table on {widePages} page(s) has more than {PdfLimits.MaxSpreadsheetColumns} " +
                             "columns, the most a worksheet holds; it is written as lines of text.";
            if (cutPages > 0)
            {
                string cut = $"{cutPages} page(s) hold more than {PdfLimits.MaxSpreadsheetRows} rows, the most " +
                             "a worksheet holds; the rest of each page was not written.";
                _lastError = _lastError == null ? cut : _lastError + " " + cut;
            }
            return ms.ToArray();
        }

        /// <summary>Whether a row at <paramref name="rowIdx"/> is inside the worksheet's grid.</summary>
        private static bool RowFits(uint rowIdx) => rowIdx <= (uint)Math.Max(0, PdfLimits.MaxSpreadsheetRows);

        /// <summary>
        /// Writes each table as a grid, returning the next free row index. A row past the worksheet's
        /// last is not written, and <paramref name="rowsCut"/> says so.
        /// </summary>
        private static uint WriteTables(
            DocumentFormat.OpenXml.Spreadsheet.SheetData sheetData,
            List<PdfTable>? tables,
            uint startRow,
            ref bool rowsCut)
        {
            uint rowIdx = startRow;
            if (tables == null) return rowIdx;

            foreach (PdfTable table in tables)
            {
                foreach (PdfTableRow tableRow in table.Rows)
                {
                    if (!RowFits(rowIdx)) { rowsCut = true; return rowIdx; }
                    var row = new DocumentFormat.OpenXml.Spreadsheet.Row { RowIndex = rowIdx };
                    foreach (PdfTableCell cell in tableRow.Cells)
                    {
                        if (string.IsNullOrEmpty(cell.Text)) continue;
                        row.Append(TextCell(cell.Text, cell.Column, rowIdx));
                    }
                    sheetData.Append(row);
                    rowIdx++;
                }
                rowIdx++; // one blank row between tables
            }
            return rowIdx;
        }

        /// <summary>
        /// Writes the text lines the emitted tables do not already account for — a wide table's among
        /// them, since it was not emitted. A line past the worksheet's last row is not written, and
        /// <paramref name="rowsCut"/> says so.
        /// </summary>
        private static void WriteRemainingLines(
            DocumentFormat.OpenXml.Spreadsheet.SheetData sheetData,
            string text,
            List<PdfTable>? emittedTables,
            IReadOnlyList<PdfTextLine>? lines,
            ref uint rowIdx,
            ref bool rowsCut)
        {
            if (string.IsNullOrEmpty(text)) return;

            var covered = ConverterTableSupport.CoveredLines(emittedTables, lines);

            foreach (string line in text.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) { rowIdx++; continue; }

                string trimmed = line.Trim();
                if (covered.Count > 0 && ConverterTableSupport.IsCovered(trimmed, covered)) continue;

                if (!RowFits(rowIdx)) { rowsCut = true; return; }
                var row = new DocumentFormat.OpenXml.Spreadsheet.Row { RowIndex = rowIdx };
                row.Append(TextCell(trimmed, column: 0, rowIndex: rowIdx));
                sheetData.Append(row);
                rowIdx++;
            }
        }

        /// <summary>A string cell carrying an explicit <c>CellReference</c>.</summary>
        /// <remarks>
        /// The reference is not decoration. Without it a reader places cells by their order in the
        /// row, so a table with an empty cell silently shifts every value after it one column left —
        /// and the previous version, which wrote one cell per row and no reference at all, put
        /// everything in column A.
        /// </remarks>
        private static DocumentFormat.OpenXml.Spreadsheet.Cell TextCell(
            string text, int column, uint rowIndex)
            => new DocumentFormat.OpenXml.Spreadsheet.Cell
            {
                CellReference = ColumnName(column) + rowIndex.ToString(CultureInfo.InvariantCulture),
                DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.String,
                CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(
                    XmlTextSanitizer.ForSpreadsheet(text))
            };

        /// <summary>Zero-based column index to its spreadsheet letters (0 to A, 26 to AA).</summary>
        internal static string ColumnName(int index)
        {
            if (index < 0) index = 0;
            var sb = new System.Text.StringBuilder();
            index++;
            while (index > 0)
            {
                int rem = (index - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Converts PDF to PowerPoint (one slide per page with text).
    /// </summary>
    public static class PdfToPptxConverter
    {
        public static byte[] Convert(byte[] pdfBytes)
        {
            using var ms = new MemoryStream();
            using (var pptDoc = DocumentFormat.OpenXml.Packaging.PresentationDocument.Create(ms,
                DocumentFormat.OpenXml.PresentationDocumentType.Presentation, true))
            {
                var presPart = pptDoc.AddPresentationPart();
                presPart.Presentation = new DocumentFormat.OpenXml.Presentation.Presentation();

                // A slide master and one slide layout, both required by the OPC relationship model:
                // ECMA-376 makes p:sldMasterIdList and a slide->slideLayout->slideMaster chain
                // mandatory, and without them PowerPoint shows "found a problem with content" and
                // drops every slide after the repair — a package of SlideParts alone zips and parses
                // while opening in nothing.
                var layoutPart = BuildMasterAndLayout(presPart, out var masterPart);

                // p:sldMasterIdList must precede p:sldIdList — CT_Presentation is a sequence, so
                // appending them in the other order produces schema-invalid XML that is otherwise
                // indistinguishable from the correct file.
                presPart.Presentation.AppendChild(
                    new DocumentFormat.OpenXml.Presentation.SlideMasterIdList(
                        new DocumentFormat.OpenXml.Presentation.SlideMasterId
                        {
                            Id = 2147483648U,
                            RelationshipId = presPart.GetIdOfPart(masterPart)
                        }));
                var slideIdList = presPart.Presentation.AppendChild(
                    new DocumentFormat.OpenXml.Presentation.SlideIdList());

                using var pdfMs = new MemoryStream(pdfBytes);
                using var pdfDoc = PdfDocument.Open(pdfMs);
                Tetitulz.Pdf.Base.Operations.EncryptedRead.RefuseConversion(pdfDoc, "PPTX");

                uint slideId = 256;
                for (int i = 0; i < pdfDoc.Pages.Count; i++)
                {
                    var slidePart = presPart.AddNewPart<DocumentFormat.OpenXml.Packaging.SlidePart>();
                    string text = PdfTextExtractor.ExtractText(pdfDoc.Pages[i]);

                    // Build a slide with the extracted page text in a text box.
                    var slide = new DocumentFormat.OpenXml.Presentation.Slide(
                        new DocumentFormat.OpenXml.Presentation.CommonSlideData(
                            BuildSlideShapeTree(text)),
                        new DocumentFormat.OpenXml.Presentation.ColorMapOverride(
                            new DocumentFormat.OpenXml.Drawing.MasterColorMapping()));
                    slidePart.Slide = slide;

                    // The slide -> layout relationship. A slide with no layout relationship is the
                    // other half of the repair prompt; the parts existing is not enough.
                    slidePart.AddPart(layoutPart);

                    slideIdList.Append(new DocumentFormat.OpenXml.Presentation.SlideId
                    {
                        Id = slideId++,
                        RelationshipId = presPart.GetIdOfPart(slidePart)
                    });
                }

                // Slide size, in DrawingML English Metric Units (PdfUnits.EmuPerInch = 914400):
                // the standard 4:3 slide, 10 in x 7.5 in. The notes page is the same sheet turned
                // portrait, so its extents are the slide's swapped.
                const long slideWidthEmu  = 9144000; // 10 in
                const long slideHeightEmu = 6858000; // 7.5 in
                // p:sldSz declares cx/cy as Int32Value (NotesSize below uses Int64Value), so the
                // EMU constants — well under int.MaxValue at 9,144,000 — are narrowed explicitly.
                presPart.Presentation.AppendChild(
                    new DocumentFormat.OpenXml.Presentation.SlideSize
                    { Cx = (int)slideWidthEmu, Cy = (int)slideHeightEmu });
                presPart.Presentation.AppendChild(
                    new DocumentFormat.OpenXml.Presentation.NotesSize
                    { Cx = slideHeightEmu, Cy = slideWidthEmu });
            }
            return ms.ToArray();
        }

        /// <summary>
        /// Creates the slide master and the single slide layout every slide relates to, wires the
        /// master-&gt;layout and master-&gt;theme relationships, and returns the layout part.
        /// </summary>
        private static DocumentFormat.OpenXml.Packaging.SlideLayoutPart BuildMasterAndLayout(
            DocumentFormat.OpenXml.Packaging.PresentationPart presPart,
            out DocumentFormat.OpenXml.Packaging.SlideMasterPart masterPart)
        {
            masterPart = presPart.AddNewPart<DocumentFormat.OpenXml.Packaging.SlideMasterPart>();
            var layoutPart = masterPart.AddNewPart<DocumentFormat.OpenXml.Packaging.SlideLayoutPart>();

            layoutPart.SlideLayout = new DocumentFormat.OpenXml.Presentation.SlideLayout(
                new DocumentFormat.OpenXml.Presentation.CommonSlideData(BuildEmptyShapeTree()),
                new DocumentFormat.OpenXml.Presentation.ColorMapOverride(
                    new DocumentFormat.OpenXml.Drawing.MasterColorMapping()))
            { Type = DocumentFormat.OpenXml.Presentation.SlideLayoutValues.Blank };

            masterPart.SlideMaster = new DocumentFormat.OpenXml.Presentation.SlideMaster(
                new DocumentFormat.OpenXml.Presentation.CommonSlideData(BuildEmptyShapeTree()),
                // p:clrMap is required and every one of its twelve attributes is mandatory —
                // omitting any one is a schema violation, not a defaulted value.
                new DocumentFormat.OpenXml.Presentation.ColorMap
                {
                    Background1        = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Light1,
                    Text1              = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Dark1,
                    Background2        = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Light2,
                    Text2              = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Dark2,
                    Accent1            = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Accent1,
                    Accent2            = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Accent2,
                    Accent3            = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Accent3,
                    Accent4            = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Accent4,
                    Accent5            = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Accent5,
                    Accent6            = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Accent6,
                    Hyperlink          = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.Hyperlink,
                    FollowedHyperlink  = DocumentFormat.OpenXml.Drawing.ColorSchemeIndexValues.FollowedHyperlink
                },
                new DocumentFormat.OpenXml.Presentation.SlideLayoutIdList(
                    new DocumentFormat.OpenXml.Presentation.SlideLayoutId
                    {
                        Id = 2147483649U,
                        RelationshipId = masterPart.GetIdOfPart(layoutPart)
                    }));

            // A slide master carries a required relationship to a theme part (ECMA-376
            // §13.3.2 / the sldMaster part's explicit r:id set). PowerPoint repairs a master
            // without one, which is the same visible failure as having no master at all.
            var themePart = masterPart.AddNewPart<DocumentFormat.OpenXml.Packaging.ThemePart>();
            themePart.Theme = BuildMinimalTheme();

            return layoutPart;
        }

        /// <summary>
        /// The smallest DrawingML theme that satisfies a:theme — a full colour, font and format
        /// scheme. Every child is required; a theme part carrying only some of them is invalid.
        /// </summary>
        private static DocumentFormat.OpenXml.Drawing.Theme BuildMinimalTheme()
        {
            var d = new DocumentFormat.OpenXml.Drawing.ColorScheme(
                new DocumentFormat.OpenXml.Drawing.Dark1Color(
                    new DocumentFormat.OpenXml.Drawing.SystemColor
                    { Val = DocumentFormat.OpenXml.Drawing.SystemColorValues.WindowText, LastColor = "000000" }),
                new DocumentFormat.OpenXml.Drawing.Light1Color(
                    new DocumentFormat.OpenXml.Drawing.SystemColor
                    { Val = DocumentFormat.OpenXml.Drawing.SystemColorValues.Window, LastColor = "FFFFFF" }),
                new DocumentFormat.OpenXml.Drawing.Dark2Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "1F497D" }),
                new DocumentFormat.OpenXml.Drawing.Light2Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "EEECE1" }),
                new DocumentFormat.OpenXml.Drawing.Accent1Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "4F81BD" }),
                new DocumentFormat.OpenXml.Drawing.Accent2Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "C0504D" }),
                new DocumentFormat.OpenXml.Drawing.Accent3Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "9BBB59" }),
                new DocumentFormat.OpenXml.Drawing.Accent4Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "8064A2" }),
                new DocumentFormat.OpenXml.Drawing.Accent5Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "4BACC6" }),
                new DocumentFormat.OpenXml.Drawing.Accent6Color(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "F79646" }),
                new DocumentFormat.OpenXml.Drawing.Hyperlink(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "0000FF" }),
                new DocumentFormat.OpenXml.Drawing.FollowedHyperlinkColor(
                    new DocumentFormat.OpenXml.Drawing.RgbColorModelHex { Val = "800080" }))
            { Name = "Office" };

            var fonts = new DocumentFormat.OpenXml.Drawing.FontScheme(
                new DocumentFormat.OpenXml.Drawing.MajorFont(
                    new DocumentFormat.OpenXml.Drawing.LatinFont { Typeface = "Calibri" },
                    new DocumentFormat.OpenXml.Drawing.EastAsianFont { Typeface = "" },
                    new DocumentFormat.OpenXml.Drawing.ComplexScriptFont { Typeface = "" }),
                new DocumentFormat.OpenXml.Drawing.MinorFont(
                    new DocumentFormat.OpenXml.Drawing.LatinFont { Typeface = "Calibri" },
                    new DocumentFormat.OpenXml.Drawing.EastAsianFont { Typeface = "" },
                    new DocumentFormat.OpenXml.Drawing.ComplexScriptFont { Typeface = "" }))
            { Name = "Office" };

            var formats = new DocumentFormat.OpenXml.Drawing.FormatScheme(
                new DocumentFormat.OpenXml.Drawing.FillStyleList(
                    new DocumentFormat.OpenXml.Drawing.SolidFill(
                        new DocumentFormat.OpenXml.Drawing.SchemeColor
                        { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor }),
                    new DocumentFormat.OpenXml.Drawing.SolidFill(
                        new DocumentFormat.OpenXml.Drawing.SchemeColor
                        { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor }),
                    new DocumentFormat.OpenXml.Drawing.SolidFill(
                        new DocumentFormat.OpenXml.Drawing.SchemeColor
                        { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor })),
                new DocumentFormat.OpenXml.Drawing.LineStyleList(
                    new DocumentFormat.OpenXml.Drawing.Outline(
                        new DocumentFormat.OpenXml.Drawing.SolidFill(
                            new DocumentFormat.OpenXml.Drawing.SchemeColor
                            { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor })) { Width = 6350 },
                    new DocumentFormat.OpenXml.Drawing.Outline(
                        new DocumentFormat.OpenXml.Drawing.SolidFill(
                            new DocumentFormat.OpenXml.Drawing.SchemeColor
                            { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor })) { Width = 12700 },
                    new DocumentFormat.OpenXml.Drawing.Outline(
                        new DocumentFormat.OpenXml.Drawing.SolidFill(
                            new DocumentFormat.OpenXml.Drawing.SchemeColor
                            { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor })) { Width = 19050 }),
                new DocumentFormat.OpenXml.Drawing.EffectStyleList(
                    new DocumentFormat.OpenXml.Drawing.EffectStyle(
                        new DocumentFormat.OpenXml.Drawing.EffectList()),
                    new DocumentFormat.OpenXml.Drawing.EffectStyle(
                        new DocumentFormat.OpenXml.Drawing.EffectList()),
                    new DocumentFormat.OpenXml.Drawing.EffectStyle(
                        new DocumentFormat.OpenXml.Drawing.EffectList())),
                new DocumentFormat.OpenXml.Drawing.BackgroundFillStyleList(
                    new DocumentFormat.OpenXml.Drawing.SolidFill(
                        new DocumentFormat.OpenXml.Drawing.SchemeColor
                        { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor }),
                    new DocumentFormat.OpenXml.Drawing.SolidFill(
                        new DocumentFormat.OpenXml.Drawing.SchemeColor
                        { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor }),
                    new DocumentFormat.OpenXml.Drawing.SolidFill(
                        new DocumentFormat.OpenXml.Drawing.SchemeColor
                        { Val = DocumentFormat.OpenXml.Drawing.SchemeColorValues.PhColor })))
            { Name = "Office" };

            return new DocumentFormat.OpenXml.Drawing.Theme(
                new DocumentFormat.OpenXml.Drawing.ThemeElements(d, fonts, formats))
            { Name = "Office Theme" };
        }

        // The group-shape scaffolding every p:spTree needs, with no shapes in it — what a blank
        // master and a blank layout carry.
        private static DocumentFormat.OpenXml.Presentation.ShapeTree BuildEmptyShapeTree() =>
            new DocumentFormat.OpenXml.Presentation.ShapeTree(
                new DocumentFormat.OpenXml.Presentation.NonVisualGroupShapeProperties(
                    new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new DocumentFormat.OpenXml.Presentation.NonVisualGroupShapeDrawingProperties(),
                    new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties()),
                new DocumentFormat.OpenXml.Presentation.GroupShapeProperties());

        // Builds a valid slide ShapeTree (required group properties + a text-box
        // shape) carrying the page's extracted text, one DrawingML paragraph per line.
        private static DocumentFormat.OpenXml.Presentation.ShapeTree BuildSlideShapeTree(string text)
        {
            var tree = new DocumentFormat.OpenXml.Presentation.ShapeTree(
                new DocumentFormat.OpenXml.Presentation.NonVisualGroupShapeProperties(
                    new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new DocumentFormat.OpenXml.Presentation.NonVisualGroupShapeDrawingProperties(),
                    new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties()),
                new DocumentFormat.OpenXml.Presentation.GroupShapeProperties());

            var shape = new DocumentFormat.OpenXml.Presentation.Shape(
                new DocumentFormat.OpenXml.Presentation.NonVisualShapeProperties(
                    new DocumentFormat.OpenXml.Presentation.NonVisualDrawingProperties { Id = 2U, Name = "Text" },
                    new DocumentFormat.OpenXml.Presentation.NonVisualShapeDrawingProperties(),
                    new DocumentFormat.OpenXml.Presentation.ApplicationNonVisualDrawingProperties()),
                new DocumentFormat.OpenXml.Presentation.ShapeProperties(
                    new DocumentFormat.OpenXml.Drawing.Transform2D(
                        new DocumentFormat.OpenXml.Drawing.Offset { X = 457200L, Y = 457200L },
                        new DocumentFormat.OpenXml.Drawing.Extents { Cx = 8229600L, Cy = 5943600L }),
                    new DocumentFormat.OpenXml.Drawing.PresetGeometry(
                        new DocumentFormat.OpenXml.Drawing.AdjustValueList())
                    { Preset = DocumentFormat.OpenXml.Drawing.ShapeTypeValues.Rectangle }),
                BuildTextBody(text));

            tree.AppendChild(shape);
            return tree;
        }

        private static DocumentFormat.OpenXml.Presentation.TextBody BuildTextBody(string text)
        {
            var body = new DocumentFormat.OpenXml.Presentation.TextBody(
                new DocumentFormat.OpenXml.Drawing.BodyProperties(),
                new DocumentFormat.OpenXml.Drawing.ListStyle());

            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            bool wrote = false;
            foreach (var line in lines)
            {
                var para = new DocumentFormat.OpenXml.Drawing.Paragraph();
                if (line.Length > 0)
                    para.Append(new DocumentFormat.OpenXml.Drawing.Run(
                        new DocumentFormat.OpenXml.Drawing.RunProperties { Language = "en-US" },
                        new DocumentFormat.OpenXml.Drawing.Text(XmlTextSanitizer.Sanitize(line))));
                body.Append(para);
                wrote = true;
            }
            if (!wrote) body.Append(new DocumentFormat.OpenXml.Drawing.Paragraph());
            return body;
        }
    }
}
