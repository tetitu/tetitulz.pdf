// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Layout
{
    // -------------------------------------------------------------------------
    // TableCellStyle
    // -------------------------------------------------------------------------

    /// <summary>Style options for a single table cell.</summary>
    public sealed class TableCellStyle
    {
        /// <summary>Border line thickness in points. Default 0.5.</summary>
        public float BorderThickness { get; set; } = 0.5f;

        /// <summary>Background fill color — red channel (0–1). Default 1 (white).</summary>
        public float BackgroundR { get; set; } = 1f;

        /// <summary>Background fill color — green channel (0–1). Default 1 (white).</summary>
        public float BackgroundG { get; set; } = 1f;

        /// <summary>Background fill color — blue channel (0–1). Default 1 (white).</summary>
        public float BackgroundB { get; set; } = 1f;

        /// <summary>Text color — red channel (0–1). Default 0 (black).</summary>
        public float TextR { get; set; } = 0f;

        /// <summary>Text color — green channel (0–1). Default 0 (black).</summary>
        public float TextG { get; set; } = 0f;

        /// <summary>Text color — blue channel (0–1). Default 0 (black).</summary>
        public float TextB { get; set; } = 0f;

        /// <summary>Horizontal padding inside the cell in points. Default 4.</summary>
        public float PaddingLeft { get; set; } = 4f;

        /// <summary>Vertical padding inside the cell in points. Default 4.</summary>
        public float PaddingTop { get; set; } = 4f;
    }

    // -------------------------------------------------------------------------
    // TableColumn
    // -------------------------------------------------------------------------

    /// <summary>Defines a single table column.</summary>
    public sealed class TableColumn
    {
        /// <summary>Resolved width in points. For fixed columns equals the declared width; for
        /// flexible columns this is set by <see cref="PdfTableBuilder.ResolveColumnWidths"/> when
        /// <see cref="PdfTableBuilder.TargetTotalWidth"/> is supplied.</summary>
        public float Width { get; private set; }

        /// <summary>Minimum column width in points.</summary>
        public float MinWidth { get; }

        /// <summary>Maximum column width in points.</summary>
        public float MaxWidth { get; }

        /// <summary>Whether the column has a flexible (min/max) width.</summary>
        public bool IsFlexible { get; }

        /// <summary>Creates a fixed-width column.</summary>
        public TableColumn(float widthPts)
        {
            if (widthPts <= 0) throw new ArgumentOutOfRangeException(nameof(widthPts));
            Width = widthPts;
            MinWidth = widthPts;
            MaxWidth = widthPts;
            IsFlexible = false;
        }

        /// <summary>Creates a flexible column with a min/max range.</summary>
        public TableColumn(float minWidth, float maxWidth)
        {
            if (minWidth <= 0) throw new ArgumentOutOfRangeException(nameof(minWidth));
            if (maxWidth < minWidth) throw new ArgumentOutOfRangeException(nameof(maxWidth));
            MinWidth = minWidth;
            MaxWidth = maxWidth;
            Width = minWidth;
            IsFlexible = true;
        }

        internal void SetResolvedWidth(float w) => Width = w;
    }

    // -------------------------------------------------------------------------
    // Cell / Row data models
    // -------------------------------------------------------------------------

    internal sealed class CellData
    {
        public string Text { get; set; } = string.Empty;
        public TableCellStyle Style { get; set; } = new TableCellStyle();
    }

    internal sealed class RowData
    {
        public List<CellData> Cells { get; } = new List<CellData>();
        public float? OverrideHeight { get; set; }
    }

    // -------------------------------------------------------------------------
    // PdfTableRowBuilder
    // -------------------------------------------------------------------------

    /// <summary>Fluent builder for a single table row.</summary>
    public sealed class PdfTableRowBuilder
    {
        private readonly PdfTableBuilder _table;
        private readonly RowData _row;

        internal PdfTableRowBuilder(PdfTableBuilder table, RowData row)
        {
            _table = table;
            _row = row;
        }

        /// <summary>Adds a plain text cell to this row.</summary>
        public PdfTableRowBuilder AddCell(string text)
        {
            _row.Cells.Add(new CellData { Text = text ?? string.Empty, Style = new TableCellStyle() });
            return this;
        }

        /// <summary>Adds a styled text cell to this row.</summary>
        public PdfTableRowBuilder AddCell(string text, TableCellStyle style)
        {
            if (style == null) throw new ArgumentNullException(nameof(style));
            _row.Cells.Add(new CellData { Text = text ?? string.Empty, Style = style });
            return this;
        }

        /// <summary>Overrides the auto-calculated row height.</summary>
        public PdfTableRowBuilder SetRowHeight(float pts)
        {
            if (pts <= 0) throw new ArgumentOutOfRangeException(nameof(pts));
            _row.OverrideHeight = pts;
            return this;
        }

        /// <summary>Returns the parent table builder for chaining.</summary>
        public PdfTableBuilder EndRow() => _table;
    }

    // -------------------------------------------------------------------------
    // PdfTableBuilder
    // -------------------------------------------------------------------------

    /// <summary>Fluent builder for a PDF table.</summary>
    public sealed class PdfTableBuilder
    {
        private readonly List<TableColumn> _columns = new List<TableColumn>();
        private readonly List<RowData> _rows = new List<RowData>();

        /// <summary>The half-inch page margin these defaults are laid out against.</summary>
        public const float DefaultMargin = PdfUnits.PointsPerInch / 2f;

        // The extra height a text row gets above FontSize is TableCellStyle.PaddingTop: the row
        // height takes the largest PaddingTop its text-bearing cells ask for, so the default of 4
        // computes FontSize + 8 and a caller raising the property changes the row.

        /// <summary>Nudge applied to the vertically centred text baseline, in points.</summary>
        private const float BaselineAdjustment = 2f;

        /// <summary>Font size used for cell text.</summary>
        public float FontSize { get; set; } = 14f;

        /// <summary>Default row height when cells have no text.</summary>
        public float DefaultRowHeight { get; set; } = 20f;

        /// <summary>X position where the table begins on the page.</summary>
        public float StartX { get; set; } = DefaultMargin;

        /// <summary>Y position (top of table, in PDF coordinates: 0 = bottom).</summary>
        public float StartY { get; set; } = PdfPageSizes.LetterHeight - DefaultMargin;

        /// <summary>Optional target total width in points. When set, flexible columns are resolved
        /// to fill this width after satisfying their <see cref="TableColumn.MinWidth"/>/<see cref="TableColumn.MaxWidth"/>
        /// constraints. When null, flexible columns fall back to <c>MinWidth</c>.</summary>
        public float? TargetTotalWidth { get; set; }

        /// <summary>
        /// Resolves flexible column widths in-place. Fixed columns keep their width; flexible columns
        /// receive a proportional share of the remaining space, clamped to [MinWidth, MaxWidth].
        /// Safe to call multiple times.
        /// </summary>
        internal void ResolveColumnWidths()
        {
            if (TargetTotalWidth == null || _columns.Count == 0) return;
            float target = TargetTotalWidth.Value;

            float fixedSum = 0f, flexMinSum = 0f, flexMaxSum = 0f;
            int flexCount = 0;
            foreach (var c in _columns)
            {
                if (c.IsFlexible)   { flexMinSum += c.MinWidth; flexMaxSum += c.MaxWidth; flexCount++; }
                else                 fixedSum   += c.Width;
            }
            if (flexCount == 0) return;

            float available = target - fixedSum;
            if (available <= flexMinSum) { foreach (var c in _columns) if (c.IsFlexible) c.SetResolvedWidth(c.MinWidth); return; }
            if (available >= flexMaxSum) { foreach (var c in _columns) if (c.IsFlexible) c.SetResolvedWidth(c.MaxWidth); return; }

            // Distribute `available` across flexible columns proportionally to their Max-Min range,
            // starting from MinWidth and adding `share * (MaxWidth - MinWidth)` clamped.
            float extra = available - flexMinSum;
            float rangeSum = flexMaxSum - flexMinSum;
            foreach (var c in _columns)
            {
                if (!c.IsFlexible) continue;
                float range = c.MaxWidth - c.MinWidth;
                float add = rangeSum > 0 ? extra * (range / rangeSum) : 0f;
                c.SetResolvedWidth(c.MinWidth + add);
            }
        }

        /// <summary>Number of columns added so far.</summary>
        public int ColumnCount => _columns.Count;

        /// <summary>Read-only access to row data (for tests).</summary>
        internal IReadOnlyList<RowData> Rows => _rows;

        /// <summary>Read-only access to columns (for tests).</summary>
        internal IReadOnlyList<TableColumn> Columns => _columns;

        /// <summary>Adds a fixed-width column.</summary>
        public PdfTableBuilder AddColumn(float widthPts)
        {
            _columns.Add(new TableColumn(widthPts));
            return this;
        }

        /// <summary>Adds a flexible column with min/max width.</summary>
        public PdfTableBuilder AddColumn(float minWidth, float maxWidth)
        {
            _columns.Add(new TableColumn(minWidth, maxWidth));
            return this;
        }

        /// <summary>Adds a new row and returns its row builder.</summary>
        public PdfTableRowBuilder AddRow()
        {
            var row = new RowData();
            _rows.Add(row);
            return new PdfTableRowBuilder(this, row);
        }

        /// <summary>
        /// Lays out the table onto the given page, returning the Y coordinate below the last row.
        /// When a row does not fit on the current page a new page is requested from the document.
        /// </summary>
        /// <param name="page">The page to draw the first rows onto.</param>
        /// <param name="document">Supplies continuation pages; when null the table overflows off-page.</param>
        /// <param name="pageHeight">Accepted for source compatibility but not consulted: rows start at
        /// <see cref="StartY"/> on every page, so the break decision needs only
        /// <paramref name="marginBottom"/>.</param>
        /// <param name="marginBottom">Y coordinate below which a row triggers a page break.</param>
        public float Build(PdfPageBuilder page, PdfLayoutDocument document = null, float pageHeight = PdfPageSizes.LetterHeight, float marginBottom = DefaultMargin)
        {
            if (page == null) throw new ArgumentNullException(nameof(page));
            if (_columns.Count == 0) return StartY;

            ResolveColumnWidths();

            float y = StartY; // current top of next row (PDF y = from bottom)
            PdfPageBuilder currentPage = page;

            foreach (var row in _rows)
            {
                float rowHeight = row.OverrideHeight ?? DefaultRowHeight;
                // If there is at least one cell with text, use font-based height.
                //
                // The vertical padding comes from TableCellStyle.PaddingTop, taken as the LARGEST
                // any text-bearing cell in the row asks for — one row has one height, so the
                // tallest request is the only answer that honours every cell.
                //
                // The padding is the cell style's, so a caller raising PaddingTop changes the row; at
                // the default this computes FontSize + 8.
                if (row.OverrideHeight == null)
                {
                    float pad = -1f;
                    foreach (var cell in row.Cells)
                    {
                        if (string.IsNullOrEmpty(cell.Text)) continue;
                        float cellPad = cell.Style?.PaddingTop ?? 0f;
                        if (cellPad > pad) pad = cellPad;
                    }
                    if (pad >= 0f) rowHeight = FontSize + (2f * pad);
                }

                // Check if we need a new page
                if (document != null && (y - rowHeight) < marginBottom)
                {
                    currentPage = document.AddPage();
                    y = StartY;
                }

                float x = StartX;
                int cellIdx = 0;

                foreach (var col in _columns)
                {
                    CellData cell = cellIdx < row.Cells.Count ? row.Cells[cellIdx] : new CellData();
                    var style = cell.Style;

                    float colW = col.Width;
                    float cellBottom = y - rowHeight;

                    // Background fill
                    currentPage.DrawRect(x, cellBottom, colW, rowHeight,
                        style.BackgroundR, style.BackgroundG, style.BackgroundB);

                    // Border
                    currentPage.DrawRectBorder(x, cellBottom, colW, rowHeight, style.BorderThickness);

                    // Text — left-padded, vertically centered
                    if (!string.IsNullOrEmpty(cell.Text))
                    {
                        float textX = x + style.PaddingLeft;
                        float textY = cellBottom + (rowHeight - FontSize) / 2f + BaselineAdjustment;
                        currentPage.DrawText(cell.Text, textX, textY, FontSize,
                            style.TextR, style.TextG, style.TextB);
                    }

                    x += colW;
                    cellIdx++;
                }

                y -= rowHeight;
            }

            return y;
        }
    }

    // -------------------------------------------------------------------------
    // PdfPageBuilder
    // -------------------------------------------------------------------------

    /// <summary>Accumulates drawing commands for a single PDF page and serialises them as a content stream.</summary>
    public sealed class PdfPageBuilder
    {
        /// <summary>The single font resource these pages declare and draw all text with.</summary>
        internal const string FontResourceName = "F1";

        private readonly StringBuilder _cs = new StringBuilder();
        private readonly float _width;
        private readonly float _height;

        internal PdfPageBuilder(float width, float height)
        {
            _width = width;
            _height = height;
        }

        /// <summary>Page width in points.</summary>
        public float Width => _width;

        /// <summary>Page height in points.</summary>
        public float Height => _height;

        // ------------------------------------------------------------------
        // Drawing primitives
        // ------------------------------------------------------------------

        /// <summary>Draws a filled rectangle with the given RGB fill color.</summary>
        public void DrawRect(float x, float y, float w, float h, float r, float g, float b)
        {
            _cs.Append(PdfOperators.Save).Append('\n');
            _cs.Append($"{F(r)} {F(g)} {F(b)} {PdfOperators.SetRgbFill}\n");
            _cs.Append($"{F(x)} {F(y)} {F(w)} {F(h)} {PdfOperators.Rectangle} {PdfOperators.Fill}\n");
            _cs.Append(PdfOperators.Restore).Append('\n');
        }

        /// <summary>Draws a stroked (border-only) rectangle.</summary>
        public void DrawRectBorder(float x, float y, float w, float h, float thickness)
        {
            _cs.Append(PdfOperators.Save).Append('\n');
            _cs.Append($"{F(thickness)} {PdfOperators.SetLineWidth}\n");
            _cs.Append($"0 0 0 {PdfOperators.SetRgbStroke}\n"); // black stroke
            _cs.Append($"{F(x)} {F(y)} {F(w)} {F(h)} {PdfOperators.Rectangle} {PdfOperators.Stroke}\n");
            _cs.Append(PdfOperators.Restore).Append('\n');
        }

        /// <summary>Draws text at the given position using Helvetica.</summary>
        /// <remarks>
        /// The font is <c>/WinAnsiEncoding</c>, so each character is written as its WinAnsi code
        /// (<c>StandardEncodings.TryGetWinAnsiCode</c>), and one the encoding does not hold as '?'.
        /// Through Latin-1 the euro sign, curly quotes and dashes, which WinAnsi keeps at 0x80-0x9F,
        /// would become '?', and U+0080-U+009F come out as the glyphs stored there.
        /// </remarks>
        public void DrawText(string text, float x, float y, float fontSize, float r, float g, float b)
        {
            if (string.IsNullOrEmpty(text)) return;

            var codes = new char[text.Length];
            for (int i = 0; i < text.Length; i++)
                codes[i] = Core.Fonts.StandardEncodings.TryGetWinAnsiCode(text[i], out byte code) ? (char)code : '?';
            string escaped = EscapePdfString(new string(codes));

            _cs.Append(PdfOperators.Save).Append('\n');
            _cs.Append(PdfOperators.BeginText).Append('\n');
            _cs.Append($"/{FontResourceName} {F(fontSize)} {PdfOperators.SetFont}\n");
            _cs.Append($"{F(r)} {F(g)} {F(b)} {PdfOperators.SetRgbFill}\n");
            _cs.Append($"{F(x)} {F(y)} {PdfOperators.TextMove}\n");
            _cs.Append($"({escaped}) {PdfOperators.ShowText}\n");
            _cs.Append(PdfOperators.EndText).Append('\n');
            _cs.Append(PdfOperators.Restore).Append('\n');
        }

        /// <summary>Returns the raw PDF content stream bytes for this page.</summary>
        public byte[] Build() => Core.Text.Latin1Text.Encode(_cs.ToString());

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string F(float v) =>
            // Non-finite goes to "0" instead of the bare keyword `NaN` / `Infinity`, which
            // is not a §7.3.3 number and can cost a strict reader the whole content stream.
            // The PRECISION here is deliberate; only the finiteness rule is shared.
            PdfRealFormat.Format(v, "0.##");

        // One implementation, in Core.Objects.PdfStringEscape (ISO 32000-1 7.3.4.2).
        // Escaping only ( ) and backslash would let a carriage return reach the file raw and
        // come back as a line feed, and a NUL truncate some viewers' parsers.
        private static string EscapePdfString(string s) => PdfStringEscape.EscapeLiteral(s);
    }

    // -------------------------------------------------------------------------
    // PdfLayoutDocument
    // -------------------------------------------------------------------------

    /// <summary>A document container that owns pages and can serialise a complete PDF.</summary>
    public sealed class PdfLayoutDocument
    {
        private sealed class PageEntry
        {
            public PdfPageBuilder Builder;
            public float Width;
            public float Height;
        }

        private readonly List<PageEntry> _pages = new List<PageEntry>();

        /// <summary>Adds a new page and returns its builder.</summary>
        public PdfPageBuilder AddPage(float widthPts = PdfPageSizes.LetterWidth, float heightPts = PdfPageSizes.LetterHeight)
        {
            if (widthPts <= 0) throw new ArgumentOutOfRangeException(nameof(widthPts));
            if (heightPts <= 0) throw new ArgumentOutOfRangeException(nameof(heightPts));

            var builder = new PdfPageBuilder(widthPts, heightPts);
            _pages.Add(new PageEntry { Builder = builder, Width = widthPts, Height = heightPts });
            return builder;
        }

        /// <summary>Writes a complete, valid PDF to <paramref name="output"/>.</summary>
        public void Save(Stream output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (_pages.Count == 0) throw new InvalidOperationException("Cannot save a document with no pages.");

            // Buffer all PDF objects, then write with accurate byte-offset xref.
            var objects = new List<(int num, byte[] data)>();
            int objNum = 1;

            // Object 1 = catalog, 2 = pages tree — allocated first, written last.
            int catalogNum = objNum++;     // 1
            int pagesTreeNum = objNum++;   // 2

            var pageObjNums = new List<int>();
            var contentObjNums = new List<int>();

            foreach (var entry in _pages)
            {
                // Content stream object
                byte[] contentBytes = entry.Builder.Build();
                int contentNum = objNum++;
                contentObjNums.Add(contentNum);

                // Font resource object (Helvetica, embedded as standard font dict)
                int fontNum = objNum++;

                // Page object
                int pageNum = objNum++;
                pageObjNums.Add(pageNum);

                // Serialise content stream
                var csHeader = Enc(
                    $"{contentNum} 0 obj\n" +
                    $"<< /Length {contentBytes.Length} >>\n" +
                    "stream\n");
                var csFooter = Enc("\nendstream\nendobj\n");

                objects.Add((contentNum, Concat(csHeader, contentBytes, csFooter)));

                // Font dict
                var fontData = Enc(
                    $"{fontNum} 0 obj\n" +
                    $"<< /Type /Font /Subtype /Type1 /BaseFont /{StandardFonts.Helvetica} " +
                    "/Encoding /WinAnsiEncoding >>\n" +
                    "endobj\n");
                objects.Add((fontNum, fontData));

                // Page dict
                var pageData = Enc(
                    $"{pageNum} 0 obj\n" +
                    "<< /Type /Page\n" +
                    $"   /Parent {pagesTreeNum} 0 R\n" +
                    $"   /MediaBox [0 0 {R(entry.Width)} {R(entry.Height)}]\n" +
                    $"   /Contents {contentNum} 0 R\n" +
                    $"   /Resources << /Font << /{PdfPageBuilder.FontResourceName} {fontNum} 0 R >> >>\n" +
                    ">>\n" +
                    "endobj\n");
                objects.Add((pageNum, pageData));
            }

            // Pages tree
            var kidsStr = new StringBuilder();
            foreach (int pn in pageObjNums)
                kidsStr.Append($"{pn} 0 R ");

            var pagesTreeData = Enc(
                $"{pagesTreeNum} 0 obj\n" +
                $"<< /Type /Pages /Kids [{kidsStr.ToString().TrimEnd()}] /Count {pageObjNums.Count} >>\n" +
                "endobj\n");
            objects.Add((pagesTreeNum, pagesTreeData));

            // Catalog
            var catalogData = Enc(
                $"{catalogNum} 0 obj\n" +
                $"<< /Type /Catalog /Pages {pagesTreeNum} 0 R >>\n" +
                "endobj\n");
            objects.Add((catalogNum, catalogData));

            // ---- Write to stream, track offsets ----
            using var outMs = new MemoryStream();
            byte[] header = Enc("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n");
            outMs.Write(header, 0, header.Length);

            var offsets = new Dictionary<int, long>(); // objNum -> offset

            foreach (var (num, data) in objects)
            {
                offsets[num] = outMs.Position;
                outMs.Write(data, 0, data.Length);
            }

            // ---- xref table ----
            long xrefOffset = outMs.Position;
            int totalObjs = objNum; // max obj number + 1

            // We need to write xref entries for 0..totalObjs-1
            // Build a sorted list of all object numbers
            var allNums = new SortedDictionary<int, long>();
            foreach (var kvp in offsets)
                allNums[kvp.Key] = kvp.Value;

            var xrefSb = new StringBuilder();
            xrefSb.Append("xref\n");
            xrefSb.Append($"0 {totalObjs}\n");
            // Object 0 — free
            xrefSb.Append(PdfXrefFormat.FreeListHead);
            for (int i = 1; i < totalObjs; i++)
            {
                if (allNums.TryGetValue(i, out long off))
                    xrefSb.Append(PdfXrefFormat.Entry(off));
                else
                    xrefSb.Append(PdfXrefFormat.FreeListHead);
            }

            byte[] xrefBytes = Enc(xrefSb.ToString());
            outMs.Write(xrefBytes, 0, xrefBytes.Length);

            // ---- Trailer ----
            var trailerStr =
                "trailer\n" +
                $"<< /Size {totalObjs} /Root {catalogNum} 0 R >>\n" +
                "startxref\n" +
                $"{xrefOffset}\n" +
                "%%EOF\n";
            byte[] trailerBytes = Enc(trailerStr);
            outMs.Write(trailerBytes, 0, trailerBytes.Length);

            outMs.Position = 0;
            outMs.CopyTo(output);
        }

        /// <summary>Saves the PDF to the file at <paramref name="path"/>.</summary>
        public void Save(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Save(fs);
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private static byte[] Enc(string s) => Core.Text.Latin1Text.Encode(s);

        private static byte[] Concat(byte[] a, byte[] b, byte[] c)
        {
            var result = new byte[a.Length + b.Length + c.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            Buffer.BlockCopy(c, 0, result, a.Length + b.Length, c.Length);
            return result;
        }

        // The page size as given: A4 is 595.28 x 841.89, and rounding it to whole points made it
        // narrower and shorter than asked.
        private static string R(float v) => PdfRealFormat.Format(v, "0.##");
    }
}

