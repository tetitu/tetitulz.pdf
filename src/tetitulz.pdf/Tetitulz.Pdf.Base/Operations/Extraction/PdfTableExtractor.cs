// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    // ------------------------------------------------------------------
    //  Data model
    // ------------------------------------------------------------------

    /// <summary>A single cell within a detected table.</summary>
    public sealed class PdfTableCell
    {
        /// <summary>Cell text (may be empty for merged / blank cells).</summary>
        public string Text { get; }

        /// <summary>Bounding box in PDF page coordinates (origin bottom-left, Y-up, pts).</summary>
        public float X { get; }
        public float Y { get; }
        public float Width  { get; }
        public float Height { get; }

        /// <summary>Zero-based column index within the parent row.</summary>
        public int Column { get; }

        /// <summary>Number of columns this cell spans horizontally. 1 means a regular cell;
        /// values &gt; 1 indicate a horizontally-merged cell detected by the extractor.</summary>
        public int ColSpan { get; }

        public PdfTableCell(string text, float x, float y, float width, float height, int column, int colSpan = 1)
        {
            Text    = text ?? string.Empty;
            X       = x;
            Y       = y;
            Width   = width;
            Height  = height;
            Column  = column;
            ColSpan = colSpan < 1 ? 1 : colSpan;
        }

        public override string ToString() =>
            ColSpan > 1
                ? $"[Col {Column}+{ColSpan - 1}] \"{Text}\" @({X:F1},{Y:F1})"
                : $"[Col {Column}] \"{Text}\" @({X:F1},{Y:F1})";
    }

    /// <summary>A row of cells detected inside a table.</summary>
    public sealed class PdfTableRow
    {
        public IReadOnlyList<PdfTableCell> Cells { get; }

        /// <summary>Baseline Y of the row (PDF coordinates).</summary>
        public float Y { get; }

        public PdfTableRow(IReadOnlyList<PdfTableCell> cells, float y)
        {
            Cells = cells ?? Array.Empty<PdfTableCell>();
            Y     = y;
        }

        /// <summary>Returns the text in the given column, or empty if the column is absent.</summary>
        public string GetCell(int column)
        {
            foreach (var c in Cells)
                if (c.Column == column) return c.Text;
            return string.Empty;
        }
    }

    /// <summary>A table detected on a single PDF page.</summary>
    public sealed class PdfTable
    {
        /// <summary>Zero-based index of the page this table was found on.</summary>
        public int PageIndex { get; }

        /// <summary>Rows, ordered top-to-bottom (highest Y first in PDF coordinates).</summary>
        public IReadOnlyList<PdfTableRow> Rows { get; }

        /// <summary>Number of distinct columns detected.</summary>
        public int ColumnCount { get; }

        /// <summary>
        /// Detection confidence in [0, 1].
        /// <para>For the <see cref="TableStrategy.Lines"/> strategy this is the fraction
        /// of cell borders confirmed by actual vector rulings (1.0 means every cell has
        /// all four borders).</para>
        /// <para>For the <see cref="TableStrategy.Text"/> strategy this is a heuristic based
        /// on column-alignment consistency — the fraction of filled cells in the
        /// (rows × effective-columns) grid.  Returns 0 for single-column or otherwise
        /// ambiguous tables.</para>
        /// </summary>
        public float Confidence { get; }

        public PdfTable(int pageIndex, IReadOnlyList<PdfTableRow> rows, int columnCount, float confidence = 0f)
        {
            PageIndex   = pageIndex;
            Rows        = rows ?? Array.Empty<PdfTableRow>();
            ColumnCount = columnCount;
            Confidence  = Math.Max(0f, Math.Min(1f, confidence));
        }

        /// <summary>
        /// Returns a rectangular string grid (rows × columns), padding empty cells with null.
        /// </summary>
        public string[][] ToGrid()
        {
            var grid = new string[Rows.Count][];
            for (int r = 0; r < Rows.Count; r++)
            {
                grid[r] = new string[ColumnCount];
                foreach (var cell in Rows[r].Cells)
                    if (cell.Column < ColumnCount)
                        grid[r][cell.Column] = cell.Text;
            }
            return grid;
        }
    }

    // ------------------------------------------------------------------
    //  Strategy + options
    // ------------------------------------------------------------------

    /// <summary>Algorithm used to detect table cell boundaries.</summary>
    public enum TableStrategy
    {
        /// <summary>
        /// Heuristic based on word positions: clusters words by Y into rows then
        /// finds recurring X gaps as column separators.  No vector lines required.
        /// This is the original algorithm and the default fallback.
        /// </summary>
        Text,

        /// <summary>
        /// Uses horizontal and vertical vector rulings extracted from the page's
        /// graphics stream as cell borders.  Accurate for bordered tables exported
        /// from spreadsheets or word processors.
        /// </summary>
        Lines,

        /// <summary>
        /// Like <see cref="Lines"/> but only emits cells that have all four borders
        /// confirmed by actual rulings.  Provides the highest precision at the cost
        /// of dropping partially-bordered cells.
        /// </summary>
        LinesStrict,

        /// <summary>
        /// Tries <see cref="Lines"/> first; if no tables are found on a page falls
        /// back to <see cref="Text"/>.  Best general-purpose choice.
        /// </summary>
        Auto,
    }

    public sealed class TableExtractionOptions
    {
        // ── Text strategy ────────────────────────────────────────────────

        /// <summary>
        /// Maximum vertical gap (pts) between two words for them to be considered
        /// part of the same row.  Default: 3 pt.
        /// </summary>
        public float RowTolerance { get; set; } = 3f;

        /// <summary>
        /// Minimum horizontal gap (pts) between two words to be treated as a
        /// column separator.  Default: 8 pt (roughly one space in 10 pt text).
        /// A cell's phrase — words a word space apart, read as one cell whatever
        /// separator other rows put through it — also ends at such a gap.
        /// </summary>
        public float ColumnGap { get; set; } = 8f;

        /// <summary>
        /// Minimum number of rows a candidate column boundary must appear in
        /// before it is accepted as a real column separator.
        /// Default: 2.
        /// </summary>
        public int MinColumnBoundaryRows { get; set; } = 2;

        // ── Shared ───────────────────────────────────────────────────────

        /// <summary>
        /// Minimum number of rows required for a cluster of lines to be
        /// considered a table.  Default: 2.
        /// </summary>
        public int MinTableRows { get; set; } = 2;

        /// <summary>
        /// Detection algorithm.  Default: <see cref="TableStrategy.Auto"/>.
        /// </summary>
        public TableStrategy Strategy { get; set; } = TableStrategy.Auto;

        // ── Lines strategy ───────────────────────────────────────────────

        /// <summary>
        /// Maximum distance in pts at which a text word centroid or ruling endpoint
        /// is snapped to a grid line.  Default: 3 pt.
        /// </summary>
        public float SnapTolerance { get; set; } = 3f;

        /// <summary>
        /// Restrict table search to this page sub-region (PDF coordinates, Y-up).
        /// Null means search the whole page.
        /// </summary>
        public (float X, float Y, float Width, float Height)? SearchArea { get; set; }

        // ── RTL / bidi (invoice support) ─────────────────────────────────

        /// <summary>
        /// When true, each cell's text is rebuilt in logical reading order via the full Unicode bidi
        /// algorithm instead of a left-to-right word join.  Fixes Hebrew/Arabic invoice cells (an RTL
        /// description reads right-to-left while an embedded amount or part-code stays intact).
        /// Numeric cells (no strong RTL characters) are returned unchanged.  Default <c>false</c>.
        /// </summary>
        public bool ApplyBidi { get; set; } = false;

        /// <summary>
        /// When true, detected columns are numbered right-to-left so column 0 is the rightmost zone —
        /// matching the reading order of an RTL (Hebrew/Arabic) table header.  Default <c>false</c>
        /// (column 0 is the leftmost zone).
        /// </summary>
        public bool RightToLeftColumns { get; set; } = false;
    }

    // ------------------------------------------------------------------
    //  Extractor
    // ------------------------------------------------------------------

    /// <summary>
    /// Detects tables in PDF pages.
    ///
    /// <para>Two complementary strategies are available:</para>
    /// <list type="bullet">
    ///   <item><b>Text</b> — clusters word positions into rows/columns (no vector
    ///   lines required; works for any text-layout table).</item>
    ///   <item><b>Lines</b> — parses the graphics stream for horizontal and vertical
    ///   rulings and uses them as cell grid lines (accurate for bordered tables).</item>
    /// </list>
    /// <para>Use <see cref="TableStrategy.Auto"/> (the default) to try Lines first and
    /// fall back to Text when no rulings are found.</para>
    /// </summary>
    public static class PdfTableExtractor
    {
        [ThreadStatic] private static string _lastError;

        /// <summary>
        /// Why the last extraction on this thread left a page's ruled grid unread as a table, or
        /// <c>null</c>. Reset at the start of every extraction. <c>[ThreadStatic]</c>: read it on the
        /// thread that made the call.
        /// </summary>
        /// <remarks>
        /// A grid of more than <see cref="MaxGridCells"/> cells is not built: a few hundred kilobytes
        /// of strokes make millions of cells, each a table cell with its text. The page is then read
        /// by the text strategy where the options allow (<see cref="TableStrategy.Auto"/>).
        /// </remarks>
        public static string LastError => _lastError;

        /// <summary>The most cells a ruled grid may have and still be read as a table.</summary>
        internal const int MaxGridCells = 250_000;

        // ------------------------------------------------------------------
        //  Public API
        // ------------------------------------------------------------------

        public static IReadOnlyList<PdfTable> Extract(
            string inputPath,
            TableExtractionOptions options = null)
        {
            _lastError = null;
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Extract(fs, options);
        }

        public static IReadOnlyList<PdfTable> Extract(
            Stream input,
            TableExtractionOptions options = null)
        {
            _lastError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            options = options ?? new TableExtractionOptions();

            // Ensure seekable for potential two-pass (text + graphics).
            bool ownsSeekable = false;
            Stream seekable = input;
            if (!input.CanSeek)
            {
                var ms = new MemoryStream();
                input.CopyTo(ms);
                ms.Position = 0;
                seekable = ms;
                ownsSeekable = true;
            }

            try
            {
                seekable.Position = 0;
                var pages = PdfStructuredTextExtractor.Extract(seekable);

                IReadOnlyList<PdfPageGraphics> graphics = null;
                if (options.Strategy != TableStrategy.Text)
                {
                    seekable.Position = 0;
                    graphics = PdfGraphicsExtractor.Extract(seekable);
                }

                var tables = new List<PdfTable>();
                for (int pi = 0; pi < pages.Count; pi++)
                    tables.AddRange(ExtractFromPage(pi, pages[pi], graphics?[pi], options));

                return tables;
            }
            finally
            {
                if (ownsSeekable) seekable.Dispose();
            }
        }

        /// <summary>
        /// Restricts table search to <paramref name="area"/> on the given page.
        /// Equivalent to setting <see cref="TableExtractionOptions.SearchArea"/> and calling
        /// <see cref="Extract(Stream, TableExtractionOptions)"/> filtered to one page.
        /// </summary>
        public static IReadOnlyList<PdfTable> FindTables(
            Stream input,
            int pageIndex,
            (float X, float Y, float Width, float Height) area,
            TableExtractionOptions options = null)
        {
            _lastError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            var opts = options != null ? Copy(options) : new TableExtractionOptions();
            opts.SearchArea = area;

            return Extract(input, opts)
                .Where(t => t.PageIndex == pageIndex)
                .ToList();
        }

        /// <summary>
        /// Tables from the text layouts and graphics the caller already extracted, for a caller that needs the
        /// layouts itself: the stream door extracts both and keeps neither, so a caller reading the lines too
        /// extracted the document's text twice. The answer is the stream door's for the same document.
        /// </summary>
        /// <remarks>
        /// A layout is the caller's: a null page or line, null words, are passed over as the walks pass them over
        /// (<see cref="TextLayouts"/>), and every page is read under the stream door's ceilings
        /// (<see cref="MaxGridCells"/>, reported on <see cref="LastError"/>).
        /// </remarks>
        /// <param name="pages">One layout a page, in page order.</param>
        /// <param name="graphics">
        /// One a page, or <see langword="null"/>: a page with none — the list shorter than <paramref name="pages"/>
        /// — is read by the text strategy, as <see cref="TableStrategy.Auto"/> does; a longer list is refused.
        /// </param>
        /// <param name="options">Detection options, or <see langword="null"/> for the defaults.</param>
        public static IReadOnlyList<PdfTable> Extract(
            IReadOnlyList<PdfPageTextLayout> pages,
            IReadOnlyList<PdfPageGraphics> graphics,
            TableExtractionOptions options = null)
        {
            if (pages == null) throw new ArgumentNullException(nameof(pages));
            if (graphics != null && graphics.Count > pages.Count)
                throw new ArgumentException(
                    $"{graphics.Count} pages of graphics for {pages.Count} pages of text: one a page, in the same order.",
                    nameof(graphics));
            return ExtractFromLayouts(pages, graphics, options);
        }

        /// <summary>
        /// Tables from layouts the caller already has — the invoice extractor's door, which reads
        /// the text once for its fields and its tables alike, and hands over a recognised page's
        /// layout in place of the page's own. <paramref name="graphics"/> may be <c>null</c>, or
        /// shorter than <paramref name="pages"/>: a page without graphics is read by the text
        /// strategy whatever <see cref="TableExtractionOptions.Strategy"/> says, as
        /// <see cref="TableStrategy.Auto"/> does.
        /// </summary>
        internal static List<PdfTable> ExtractFromLayouts(
            IReadOnlyList<PdfPageTextLayout> pages,
            IReadOnlyList<PdfPageGraphics> graphics,
            TableExtractionOptions options)
        {
            _lastError = null;
            options ??= new TableExtractionOptions();
            var tables = new List<PdfTable>();
            for (int pi = 0; pi < pages.Count; pi++)
            {
                // A layout is public and constructible: a page without lines has no table, and the
                // pages after it keep their own indexes.
                if (pages[pi]?.Lines == null) continue;
                PdfPageGraphics gfx = graphics != null && pi < graphics.Count ? graphics[pi] : null;
                var opts = options;
                if (gfx == null && options.Strategy != TableStrategy.Text)
                {
                    opts = Copy(options);
                    opts.Strategy = TableStrategy.Text;
                }
                tables.AddRange(ExtractFromPage(pi, pages[pi], gfx, opts));
            }
            return tables;
        }

        /// <summary>A copy of every option, for a caller that changes one of them.</summary>
        private static TableExtractionOptions Copy(TableExtractionOptions o)
            => new TableExtractionOptions
            {
                RowTolerance          = o.RowTolerance,
                ColumnGap             = o.ColumnGap,
                MinColumnBoundaryRows = o.MinColumnBoundaryRows,
                MinTableRows          = o.MinTableRows,
                Strategy              = o.Strategy,
                SnapTolerance         = o.SnapTolerance,
                SearchArea            = o.SearchArea,
                ApplyBidi             = o.ApplyBidi,
                RightToLeftColumns    = o.RightToLeftColumns,
            };

        // ------------------------------------------------------------------
        //  Strategy dispatch
        // ------------------------------------------------------------------

        private static IReadOnlyList<PdfTable> ExtractFromPage(
            int pageIndex,
            PdfPageTextLayout page,
            PdfPageGraphics gfx,
            TableExtractionOptions opts)
        {
            switch (opts.Strategy)
            {
                case TableStrategy.Lines:
                    return ExtractLinesStrategy(pageIndex, page, gfx, opts, strict: false);

                case TableStrategy.LinesStrict:
                    return ExtractLinesStrategy(pageIndex, page, gfx, opts, strict: true);

                case TableStrategy.Auto:
                    if (gfx != null)
                    {
                        // A grid of one column is rules between rows and nothing between columns: the
                        // text says where its columns are.
                        var lineTables = ExtractLinesStrategy(pageIndex, page, gfx, opts, strict: false);
                        if (lineTables.Count > 0 && lineTables.All(t => t.ColumnCount > 1)) return lineTables;
                    }
                    return ExtractTextStrategy(pageIndex, page, opts);

                default: // Text
                    return ExtractTextStrategy(pageIndex, page, opts);
            }
        }

        // ------------------------------------------------------------------
        //  Lines strategy
        // ------------------------------------------------------------------

        private static IReadOnlyList<PdfTable> ExtractLinesStrategy(
            int pageIndex,
            PdfPageTextLayout page,
            PdfPageGraphics gfx,
            TableExtractionOptions opts,
            bool strict)
        {
            if (gfx == null) return Array.Empty<PdfTable>();

            var hRulings = gfx.HorizontalRulings;
            var vRulings = gfx.VerticalRulings;

            if (hRulings.Count < 2 || vRulings.Count < 2)
                return Array.Empty<PdfTable>();

            // Apply optional search area filter.
            if (opts.SearchArea.HasValue)
            {
                var (ax, ay, aw, ah) = opts.SearchArea.Value;
                hRulings = hRulings.Where(r => r.Y >= ay && r.Y <= ay + ah
                                            && r.X2 > ax && r.X1 < ax + aw).ToList();
                vRulings = vRulings.Where(r => r.X >= ax && r.X <= ax + aw
                                            && r.Y2 > ay && r.Y1 < ay + ah).ToList();
                if (hRulings.Count < 2 || vRulings.Count < 2) return Array.Empty<PdfTable>();
            }

            // Cluster Y positions from H rulings → horizontal grid lines.
            var yVals = hRulings.Select(r => r.Y).ToList();
            yVals.Sort();
            var distinctY = ClusterFloats(yVals, opts.SnapTolerance);
            distinctY.Sort((a, b) => b.CompareTo(a)); // descending: top first

            // Cluster X positions from V rulings → vertical grid lines.
            var xVals = vRulings.Select(r => r.X).ToList();
            xVals.Sort();
            var distinctX = ClusterFloats(xVals, opts.SnapTolerance);
            // already ascending

            int nRowBands = distinctY.Count - 1;
            int nColBands = distinctX.Count - 1;
            if (nRowBands < 1 || nColBands < 1) return Array.Empty<PdfTable>();
            if ((long)nRowBands * nColBands > MaxGridCells)
            {
                _lastError ??= $"page {pageIndex + 1}'s rulings make a grid of {nRowBands} × {nColBands} " +
                               $"cells, past the {MaxGridCells}-cell ceiling, so they were not read as a table";
                return Array.Empty<PdfTable>();
            }

            // Each cell's four borders are looked up in the rulings near its grid line — a binary
            // search — where each was a scan of every ruling on the page, for every cell.
            float lineSnap = opts.SnapTolerance;
            var rowLines = new LineRulings[distinctY.Count];
            var byY = hRulings.OrderBy(r => r.Y).ToList();
            for (int i = 0; i < distinctY.Count; i++)
                rowLines[i] = new LineRulings(Near(byY, r => r.Y, distinctY[i], lineSnap).Select(r => (r.X1, r.X2)));
            var columnLines = new LineRulings[distinctX.Count];
            var byX = vRulings.OrderBy(r => r.X).ToList();
            for (int i = 0; i < distinctX.Count; i++)
                columnLines[i] = new LineRulings(Near(byX, r => r.X, distinctX[i], lineSnap).Select(r => (r.Y1, r.Y2)));

            // Collect all words from the page (optionally area-filtered).
            var allWords = TextLayouts.LinesOf(page).SelectMany(TextLayouts.WordsOf).ToList();

            float snap = opts.SnapTolerance;

            // Each word in one cell: the band its centre falls in, across among the grid's interior
            // vertical lines and down among its horizontal ones — a centre on a line in the band
            // after it, one within SnapTolerance outside the grid in the band at that edge, one
            // further out in none. Testing each cell's snap-widened box gave a word near an interior
            // line to the cells on both sides of it.
            var interiorX = distinctX.GetRange(1, nColBands - 1);
            var interiorY = distinctY.GetRange(1, nRowBands - 1);
            interiorY.Reverse();   // ascending
            var wordsOfCell = new Dictionary<(int Row, int Col), List<PdfTextWord>>();
            foreach (var w in allWords)
            {
                float cx = w.X + w.Width  * 0.5f;
                float cy = w.Y + w.Height * 0.5f;
                if (cx < distinctX[0] - snap || cx > distinctX[nColBands] + snap
                 || cy < distinctY[nRowBands] - snap || cy > distinctY[0] + snap)
                    continue;
                var key = (nRowBands - 1 - AssignColumn(cy, interiorY), AssignColumn(cx, interiorX));
                if (!wordsOfCell.TryGetValue(key, out var list)) wordsOfCell[key] = list = new List<PdfTextWord>();
                list.Add(w);
            }
            bool rightToLeft = ReadsRightToLeft(wordsOfCell.Values.SelectMany(ws => ws));

            int totalBorderSlots  = 0;
            int presentBorderSlots = 0;
            var tableRows = new List<PdfTableRow>(nRowBands);

            for (int r = 0; r < nRowBands; r++)
            {
                float cellTop    = distinctY[r];
                float cellBottom = distinctY[r + 1];
                var cells = new List<PdfTableCell>(nColBands);

                for (int c = 0; c < nColBands; c++)
                {
                    float cellLeft  = distinctX[c];
                    float cellRight = distinctX[c + 1];

                    int borders = (rowLines[r].Spans(cellLeft, cellRight, snap) ? 1 : 0)
                                + (rowLines[r + 1].Spans(cellLeft, cellRight, snap) ? 1 : 0)
                                + (columnLines[c].Spans(cellBottom, cellTop, snap) ? 1 : 0)
                                + (columnLines[c + 1].Spans(cellBottom, cellTop, snap) ? 1 : 0);

                    // In strict mode, only kept cells contribute to the confidence
                    // ratio — skipped cells must not pollute the denominator.
                    if (strict && borders < 4) continue;

                    totalBorderSlots  += 4;
                    presentBorderSlots += borders;

                    string text = wordsOfCell.TryGetValue((r, c), out var cellWords)
                        ? CellText(cellWords, opts, rightToLeft)
                        : string.Empty;

                    cells.Add(new PdfTableCell(
                        text,
                        cellLeft, cellBottom,
                        cellRight - cellLeft, cellTop - cellBottom,
                        c));
                }

                if (cells.Count > 0)
                    tableRows.Add(new PdfTableRow(cells, cellTop));
            }

            if (tableRows.Count < opts.MinTableRows) return Array.Empty<PdfTable>();

            float confidence = totalBorderSlots > 0
                ? (float)presentBorderSlots / totalBorderSlots
                : 0f;

            var linesTable = new PdfTable(pageIndex, tableRows, nColBands, confidence);
            if (opts.RightToLeftColumns) linesTable = ReverseColumns(linesTable);
            return new[] { linesTable };
        }

        // ------------------------------------------------------------------
        //  Text strategy (original algorithm)
        // ------------------------------------------------------------------

        private static IReadOnlyList<PdfTable> ExtractTextStrategy(
            int pageIndex,
            PdfPageTextLayout page,
            TableExtractionOptions opts)
        {
            var allWords = TextLayouts.LinesOf(page).SelectMany(TextLayouts.WordsOf).ToList();

            // Apply SearchArea filter (Phase 4.4 — consistent with Lines strategy)
            if (opts.SearchArea.HasValue)
            {
                var sa = opts.SearchArea.Value;
                float saRight = sa.X + sa.Width, saTop = sa.Y + sa.Height;
                allWords = allWords.Where(w =>
                    w.X + w.Width > sa.X && w.X < saRight &&
                    w.Y + w.Height > sa.Y && w.Y < saTop).ToList();
            }

            if (allWords.Count == 0)
                return Array.Empty<PdfTable>();

            allWords.Sort((a, b) => b.Y.CompareTo(a.Y));
            var rowGroups = ClusterByY(allWords, opts.RowTolerance);

            // Redundant with the same test on table.Rows.Count below — BuildTextTable adds exactly
            // one row per group, unconditionally — so this cannot change an outcome. Kept as an
            // early-out that skips building a table for a page of prose, which is most pages.
            if (rowGroups.Count < opts.MinTableRows)
                return Array.Empty<PdfTable>();

            // No separator found means ONE column, and the cells belong in it.
            //
            // A boundary at x = 0 would make colCount 2 and send every cell to column 1 — a table
            // reporting a column it never filled. That matters: the DOCX converter's
            // IsWorthLayingOut is structural ("two rows that each carry two or more non-empty
            // cells"), and the remark
            // there records a page of prose becoming a table and its paragraphs vanishing.
            var colBoundaries = DetectColumnBoundaries(rowGroups, opts);

            // Prose at the grid's edges: a line of running text above or below the grid shares no
            // column alignment with it — its words cross the separators and none of its gaps sits on
            // one — and must not be taken in as a row split at a column
            // boundary ("A closing sentence" | "of prose."), so it never reached Word or Excel as a
            // paragraph. Such a row ends the table, and so does one far outside the grid's vertical
            // extent. Edges only: a row between two of the grid's rows is the table's, whatever
            // its shape.
            if (colBoundaries.Count > 0) rowGroups = TrimProseRows(rowGroups, colBoundaries, opts);

            var table = BuildTextTable(pageIndex, rowGroups, colBoundaries, opts);

            if (table.Rows.Count < opts.MinTableRows)
                return Array.Empty<PdfTable>();

            if (opts.RightToLeftColumns) table = ReverseColumns(table);

            return new[] { table };
        }

        // ------------------------------------------------------------------
        //  Border detection helpers (Lines strategy)
        // ------------------------------------------------------------------

        /// <summary>
        /// The rulings within the snap tolerance of one grid line, each as the span it covers
        /// along the line, sorted by where it starts with the furthest end reached so far: whether
        /// one covers a cell's side is a binary search.
        /// </summary>
        private sealed class LineRulings
        {
            private readonly float[] _starts, _furthestEnd;

            internal LineRulings(IEnumerable<(float Start, float End)> spans)
            {
                var sorted = spans.OrderBy(s => s.Start).ToList();
                _starts = new float[sorted.Count];
                _furthestEnd = new float[sorted.Count];
                float furthest = float.MinValue;
                for (int i = 0; i < sorted.Count; i++)
                {
                    _starts[i] = sorted[i].Start;
                    furthest = Math.Max(furthest, sorted[i].End);
                    _furthestEnd[i] = furthest;
                }
            }

            /// <summary>
            /// Whether a ruling starts by <paramref name="from"/> and ends by <paramref name="to"/>,
            /// each within <paramref name="snap"/> — the test each scan of every ruling made.
            /// </summary>
            internal bool Spans(float from, float to, float snap)
            {
                int lo = 0, hi = _starts.Length - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_starts[mid] <= from + snap) { found = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                return found >= 0 && _furthestEnd[found] >= to - snap;
            }
        }

        // The rulings of `sorted` (ascending by `key`) within `snap` of `line`.
        private static IEnumerable<T> Near<T>(List<T> sorted, Func<T, float> key, float line, float snap)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (key(sorted[mid]) < line - snap) lo = mid + 1;
                else hi = mid;
            }
            for (int i = lo; i < sorted.Count && key(sorted[i]) <= line + snap; i++)
                if (Math.Abs(key(sorted[i]) - line) <= snap) yield return sorted[i];
        }

        // ------------------------------------------------------------------
        //  Text strategy internals
        // ------------------------------------------------------------------

        private static List<List<PdfTextWord>> ClusterByY(List<PdfTextWord> words, float tolerance)
        {
            var groups = new List<List<PdfTextWord>>();
            if (words.Count == 0) return groups;

            var current = new List<PdfTextWord> { words[0] };
            float refY  = words[0].Y;

            for (int i = 1; i < words.Count; i++)
            {
                if (Math.Abs(words[i].Y - refY) <= tolerance)
                {
                    current.Add(words[i]);
                }
                else
                {
                    groups.Add(current);
                    current = new List<PdfTextWord> { words[i] };
                    refY    = words[i].Y;
                }
            }
            groups.Add(current);
            return groups;
        }

        private static List<float> DetectColumnBoundaries(
            List<List<PdfTextWord>> rows, TableExtractionOptions opts)
        {
            var gapMidpoints = new List<float>();
            var gaps = new List<ColumnGapSeen>();

            foreach (var row in rows)
            {
                var sorted = row.OrderBy(w => w.X).ToList();
                for (int i = 0; i + 1 < sorted.Count; i++)
                {
                    float rightEdge = sorted[i].X + sorted[i].Width;
                    float leftEdge  = sorted[i + 1].X;
                    if (SeparatesColumns(rightEdge, leftEdge, opts))
                    {
                        gapMidpoints.Add((rightEdge + leftEdge) / 2f);
                        gaps.Add(new ColumnGapSeen(rightEdge, leftEdge, sorted[0].Y,
                                                   Math.Max(sorted[i].Height, sorted[i + 1].Height)));
                    }
                }
            }

            if (gapMidpoints.Count == 0) return new List<float>();

            gapMidpoints.Sort();
            var separators = ClusterFloats(gapMidpoints, opts.ColumnGap);

            var validSeps = new List<float>();
            foreach (float sep in separators)
            {
                int count = 0;
                foreach (var row in rows)
                {
                    var sorted = row.OrderBy(w => w.X).ToList();
                    for (int i = 0; i + 1 < sorted.Count; i++)
                    {
                        if (GapMidpointNear(sorted[i], sorted[i + 1], sep, opts.ColumnGap))
                        { count++; break; }
                    }
                }
                if (count >= opts.MinColumnBoundaryRows)
                    validSeps.Add(sep);
            }

            return PlaceInFreeSpace(validSeps, gaps, rows, opts);
        }

        /// <summary>A gap wide enough to part two columns: where it is, and its row's baseline and height.</summary>
        private readonly struct ColumnGapSeen
        {
            internal readonly float Left, Right, RowY, Height;
            internal ColumnGapSeen(float left, float right, float rowY, float height)
            {
                Left = left;
                Right = right;
                RowY = rowY;
                Height = height;
            }
        }

        /// <summary>
        /// Each separator a header's words cross, moved into space its gaps leave free in every row
        /// of its table: the part every gap that voted for it shares, less every phrase of those rows
        /// reaching into it. Of two such stretches, the one leaving the most rows a word on either
        /// side of it — before the next separator and after the one placed before it — then the
        /// one passing its header the way the last header was passed, then the nearer.
        /// </summary>
        /// <remarks>
        /// <para>A header set right over numbers set left — an invoice's <c>סכום</c> at its column's
        /// right edge, its amounts at the left — lies across the midpoint of the numbers' gaps, and
        /// the header went to the column beside it, the two headers one cell. A stretch inside a
        /// phrase (<c>מספר רכב</c>'s word space) is none. A table sets its headers alike, so a tie
        /// goes the way the header before it went.</para>
        /// <para>Only a phrase with letters is moved past, and only to a place leaving its row a word
        /// on each side: two figures a word space apart are refused as they are, and a heading over
        /// two columns with nothing of its row beyond it spans them.
        /// The rows are the table's — the voting gaps' rows, and a row within two text heights of
        /// them (its header) — of more than one phrase: a field line above the table or a section's
        /// title across it has no boundary between cells for a separator to keep to.</para>
        /// </remarks>
        private static List<float> PlaceInFreeSpace(List<float> seps, List<ColumnGapSeen> gaps,
                                                    List<List<PdfTextWord>> rows, TableExtractionOptions opts)
        {
            if (seps.Count == 0) return seps;
            var phrases = new List<(float A, float B, bool Letters, float RowY, int Row)>();
            for (int r = 0; r < rows.Count; r++)
            {
                var sorted = rows[r].OrderBy(w => w.X).ToList();
                var own = new List<(float A, float B, bool Letters, float RowY, int Row)>();
                for (int i = 0; i < sorted.Count; )
                {
                    int j = i;
                    while (j + 1 < sorted.Count && ContinuesPhrase(sorted[j], sorted[j + 1], opts)) j++;
                    bool letters = false;
                    for (int w = i; w <= j; w++) letters |= sorted[w].Text.Any(char.IsLetter);
                    own.Add((sorted[i].X, sorted[j].X + sorted[j].Width, letters, sorted[0].Y, r));
                    i = j + 1;
                }
                if (own.Count > 1) phrases.AddRange(own);
            }

            var placed = new List<float>(seps.Count);
            int side = 0; // the way the last header was passed: a table aligns its headers alike
            for (int k = 0; k < seps.Count; k++)
            {
                float s = seps[k];
                placed.Add(s);

                float lo = float.MinValue, hi = float.MaxValue;
                float top = float.MinValue, bottom = float.MaxValue, height = 0f;
                foreach (var g in gaps)
                {
                    if (Math.Abs((g.Left + g.Right) / 2f - s) > opts.ColumnGap) continue;
                    lo = Math.Max(lo, g.Left);
                    hi = Math.Min(hi, g.Right);
                    top = Math.Max(top, g.RowY);
                    bottom = Math.Min(bottom, g.RowY);
                    height = Math.Max(height, g.Height);
                }
                if (!(lo < hi)) continue;

                float reach = 2f * height;
                var own = phrases.Where(p => p.RowY <= top + reach && p.RowY >= bottom - reach).ToList();
                var crossing = own.Where(p => p.A < s && s < p.B).ToList();
                if (crossing.Count == 0 || crossing.Any(p => !p.Letters)) continue;

                var free = new List<(float A, float B)>();
                float at = lo;
                foreach (var (a, b, _, _, _) in own.Where(p => p.A < hi && p.B > lo).OrderBy(p => p.A))
                {
                    if (a > at) free.Add((at, a));
                    at = Math.Max(at, b);
                }
                if (hi > at) free.Add((at, hi));
                if (free.Count == 0) continue;

                float before = k > 0 ? placed[k - 1] : float.MinValue;
                float after = k + 1 < seps.Count ? seps[k + 1] : float.MaxValue;
                var tableRows = rows.Where(r => r.Count > 0 && LeftmostY(r) <= top + reach
                                                && LeftmostY(r) >= bottom - reach).ToList();
                int bestScore = -1;
                bool bestKeepsSide = false;
                float bestDistance = float.MaxValue;
                bool Parts(List<PdfTextWord> r, float c)
                    => r.Any(w => Centre(w) > before && Centre(w) < c) && r.Any(w => Centre(w) > c && Centre(w) < after);
                foreach (var (a, b) in free)
                {
                    float c = (a + b) / 2f;
                    // A crossing phrase's row keeps a word on each side: a heading over two columns
                    // with nothing of its row beyond it spans them, and stays across.
                    if (crossing.Any(p => !rows[p.Row].Any(w => Centre(w) < c) || !rows[p.Row].Any(w => Centre(w) > c))) continue;
                    int score = tableRows.Count(r => Parts(r, c));
                    bool keepsSide = Math.Sign(c - s) == side;
                    float distance = Math.Abs(c - s);
                    if (score > bestScore
                        || (score == bestScore && keepsSide && !bestKeepsSide)
                        || (score == bestScore && keepsSide == bestKeepsSide && distance < bestDistance))
                    {
                        placed[k] = c;
                        bestScore = score;
                        bestKeepsSide = keepsSide;
                        bestDistance = distance;
                    }
                }
                if (bestScore >= 0) side = Math.Sign(placed[k] - s);
            }
            return placed;
        }

        private static float Centre(PdfTextWord w) => w.X + w.Width / 2f;

        // A row's baseline as its gaps and phrases record it: its leftmost word's.
        private static float LeftmostY(List<PdfTextWord> row)
        {
            var leftmost = row[0];
            foreach (var w in row) if (w.X < leftmost.X) leftmost = w;
            return leftmost.Y;
        }

        /// <summary>
        /// Whether a gap from <paramref name="leftEnd"/> to <paramref name="rightStart"/> can
        /// separate two columns: <see cref="TableExtractionOptions.ColumnGap"/> or wider. The one
        /// reading of that option, for a separator's candidates, a row's prose test and where a
        /// phrase of a cell ends.
        /// </summary>
        private static bool SeparatesColumns(float leftEnd, float rightStart, TableExtractionOptions opts)
            => rightStart - leftEnd >= opts.ColumnGap;

        /// <summary>
        /// Whether the gap between two neighbouring words sits on a separator: its midpoint within
        /// <paramref name="tolerance"/> of <paramref name="separator"/>. The one reading, for
        /// validating a separator over the rows and for asking whether a row aligns with the grid.
        /// </summary>
        /// <remarks>
        /// A separator's validation counts a row's gap of any width here, a word space included
        /// (GAPS §4a records the question); only its candidates must clear the column gap.
        /// </remarks>
        private static bool GapMidpointNear(PdfTextWord left, PdfTextWord right, float separator, float tolerance)
        {
            float mid = (left.X + left.Width + right.X) / 2f;
            return Math.Abs(mid - separator) <= tolerance;
        }

        /// <summary>
        /// An edge row further from its neighbour than this many times the grid's median row pitch
        /// is outside the grid's vertical extent.
        /// </summary>
        private const float OutlierPitchFactor = 3f;

        /// <summary>
        /// The rows with the prose at either edge removed: a leading or trailing row whose words
        /// cross a separator while none of its gaps sits on one (running text, not cells), then a
        /// leading or trailing row further from its neighbour than <see cref="OutlierPitchFactor"/>
        /// times the median pitch of the rows that remain. Never below one row; the caller's
        /// <c>MinTableRows</c> decides whether what is left is a table.
        /// </summary>
        private static List<List<PdfTextWord>> TrimProseRows(
            List<List<PdfTextWord>> rows, List<float> separators, TableExtractionOptions opts)
        {
            int first = 0, last = rows.Count - 1;
            while (first < last && IsProseRow(rows[first], separators, opts)) first++;
            while (last > first && IsProseRow(rows[last], separators, opts)) last--;

            float pitch = MedianPitch(rows, first, last);
            if (pitch > 0)
            {
                while (first < last && RowY(rows[first]) - RowY(rows[first + 1]) > OutlierPitchFactor * pitch) first++;
                while (last > first && RowY(rows[last - 1]) - RowY(rows[last]) > OutlierPitchFactor * pitch) last--;
            }

            if (first == 0 && last == rows.Count - 1) return rows;
            return rows.GetRange(first, last - first + 1);
        }

        /// <summary>
        /// Running text rather than cells: a word crosses a separator and no gap of at least
        /// <c>ColumnGap</c> sits on one. A single cell that crosses nothing (a subtotal in the last
        /// column) and a spanning cell beside an aligned gap (a header over two columns) are rows.
        /// </summary>
        private static bool IsProseRow(List<PdfTextWord> row, List<float> separators, TableExtractionOptions opts)
        {
            var sorted = row.OrderBy(w => w.X).ToList();
            bool crosses = false, aligned = false;
            foreach (var w in sorted)
                if (AssignColumn(w.X, separators) != AssignColumn(w.X + w.Width, separators)) crosses = true;
            for (int i = 0; i + 1 < sorted.Count && !aligned; i++)
            {
                if (!SeparatesColumns(sorted[i].X + sorted[i].Width, sorted[i + 1].X, opts)) continue;
                foreach (float sep in separators)
                    if (GapMidpointNear(sorted[i], sorted[i + 1], sep, opts.ColumnGap)) { aligned = true; break; }
            }
            return crosses && !aligned;
        }

        /// <summary>The median of the pitches between consecutive rows <paramref name="first"/>..<paramref name="last"/>, the lower middle for an even count; 0 for fewer than two rows.</summary>
        private static float MedianPitch(List<List<PdfTextWord>> rows, int first, int last)
        {
            if (last - first < 1) return 0;
            var pitches = new List<float>(last - first);
            for (int i = first; i < last; i++) pitches.Add(RowY(rows[i]) - RowY(rows[i + 1]));
            pitches.Sort();
            return pitches[(pitches.Count - 1) / 2];
        }

        /// <summary>A row's y: its highest word's, as the table row it becomes reports.</summary>
        private static float RowY(List<PdfTextWord> row) => row.Max(w => w.Y);

        private static PdfTable BuildTextTable(
            int pageIndex,
            List<List<PdfTextWord>> rowGroups,
            List<float> colSeparators,
            TableExtractionOptions opts)
        {
            int colCount  = colSeparators.Count + 1;
            var tableRows = new List<PdfTableRow>(rowGroups.Count);
            bool rightToLeft = ReadsRightToLeft(rowGroups.SelectMany(r => r));

            foreach (var rowWords in rowGroups)
                tableRows.Add(new PdfTableRow(RowCells(rowWords, colSeparators, opts, rightToLeft), RowY(rowWords)));

            float confidence = ComputeTextStrategyConfidence(tableRows, colCount);
            return new PdfTable(pageIndex, tableRows, colCount, confidence);
        }

        /// <summary>
        /// The widest gap, in word heights, between two words of one phrase of a cell: a word space
        /// — about a quarter of an em in most fonts — with room for its stretch, and narrower than
        /// the space between two cells.
        /// </summary>
        internal const float PhraseGapEm = 0.4f;

        // Whether `next` goes on the phrase `last` ends: a word space away, and no column's gap.
        private static bool ContinuesPhrase(PdfTextWord last, PdfTextWord next, TableExtractionOptions opts)
        {
            float end = last.X + last.Width;
            return !SeparatesColumns(end, next.X, opts)
                   && next.X - end <= PhraseGapEm * Math.Max(last.Height, next.Height);
        }

        /// <summary>
        /// A row's cells: its words in phrases — runs a word space apart — each over the columns it
        /// reaches from its left edge to its right; a column a one-column phrase lies in is that
        /// phrase's, and a longer phrase gives it up at its ends, keeping the column its centre is in
        /// when that leaves it none; phrases that still share a column are one cell.
        /// </summary>
        /// <remarks>
        /// A row never covers a column twice, and a phrase is never cut by a separator other rows put
        /// through it — <c>שירותי אינטגרציה</c> under a field block whose gaps line up inside it, or a
        /// word space a separator falls in. Placing each word on its own, a word crossing a separator
        /// made a cell spanning two columns beside a cell in one of them, and a reader asking for that
        /// column got whichever came first: one word of the description.
        /// </remarks>
        private static List<PdfTableCell> RowCells(List<PdfTextWord> rowWords, List<float> separators,
                                                   TableExtractionOptions opts, bool rightToLeft)
        {
            var phrases = new List<List<PdfTextWord>>();
            foreach (var w in rowWords.OrderBy(w => w.X))
            {
                var current = phrases.Count > 0 ? phrases[phrases.Count - 1] : null;
                if (current != null && ContinuesPhrase(current[current.Count - 1], w, opts)) current.Add(w);
                else phrases.Add(new List<PdfTextWord> { w });
            }

            var spans = phrases.Select(p =>
            {
                float left = p.Min(w => w.X), right = p.Max(w => w.X + w.Width);
                return (Words: p, First: AssignColumn(left, separators), Last: AssignColumn(right, separators),
                        Centre: (left + right) / 2f);
            }).ToList();
            var held = new HashSet<int>(spans.Where(s => s.First == s.Last).Select(s => s.First));

            var placed = new List<(List<PdfTextWord> Words, int First, int Last)>(spans.Count);
            foreach (var s in spans)
            {
                int first = s.First, last = s.Last;
                if (first < last)
                {
                    if (held.Contains(first)) first++;
                    if (held.Contains(last)) last--;
                    if (first > last) first = last = AssignColumn(s.Centre, separators);
                }
                placed.Add((s.Words, first, last));
            }
            placed.Sort((a, b) => a.First.CompareTo(b.First));

            var cells = new List<PdfTableCell>();
            List<PdfTextWord> words = null;
            int from = 0, to = 0;
            foreach (var (pw, first, last) in placed)
            {
                if (words != null && first > to)
                {
                    cells.Add(Cell(words, from, to, opts, rightToLeft));
                    words = null;
                }
                if (words == null) { words = new List<PdfTextWord>(); from = first; to = last; }
                words.AddRange(pw);
                if (last > to) to = last;
            }
            if (words != null) cells.Add(Cell(words, from, to, opts, rightToLeft));
            return cells;
        }

        private static PdfTableCell Cell(List<PdfTextWord> words, int first, int last,
                                         TableExtractionOptions opts, bool rightToLeft)
        {
            var ws = words.OrderBy(w => w.X).ToList();
            float x = ws.Min(w => w.X), y = ws.Min(w => w.Y);
            float right = ws.Max(w => w.X + w.Width), top = ws.Max(w => w.Y + w.Height);
            return new PdfTableCell(CellText(ws, opts, rightToLeft), x, y, right - x, top - y, first, last - first + 1);
        }

        /// <summary>
        /// Whether a table's words hold more right-to-left letters than left-to-right ones — the
        /// direction its cells read in. A cell alone cannot say: "שירותי DevOps" has as many of each,
        /// and a Hebrew table's own cells, however Latin, read right to left. Digits have no say.
        /// </summary>
        private static bool ReadsRightToLeft(IEnumerable<PdfTextWord> words)
        {
            int rtl = 0, ltr = 0;
            foreach (var w in words)
                foreach (char c in w.Text ?? string.Empty)
                {
                    if (TextDirection.IsRtlChar(c)) rtl++;
                    else if (char.IsLetter(c)) ltr++;
                }
            return rtl > ltr;
        }

        // Heuristic confidence for the Text strategy:
        // Uses the count of ACTUALLY OCCUPIED columns (not separator-derived colCount)
        // so phantom separator columns do not inflate the denominator.
        private static float ComputeTextStrategyConfidence(List<PdfTableRow> rows, int colCount)
        {
            if (colCount < 2 || rows.Count == 0) return 0f;

            // Determine which column indices are actually used.
            //
            // This whitespace check is untestable through PdfTableExtractor's public surface, and
            // that is a fact about the caller, not the check: a cell only
            // exists here at all because BuildTextTable's bucket for its column held at least one
            // PdfTextWord (empty buckets are skipped before a cell is ever built), and a word's own
            // Text can never be blank unless AssembleLines ran with WordAssemblyOptions
            // .KeepBlankChars true — a space glyph is otherwise filtered out before word grouping,
            // never appended to a word's text. PdfTableExtractor calls PdfStructuredTextExtractor
            // .Extract with no options, so it always gets the default (false). The guard stays,
            // because "no caller reaches this yet" is not "no caller ever could" — a future caller
            // plumbing KeepBlankChars through would revive it silently if it were deleted instead.
            var usedCols = new System.Collections.Generic.HashSet<int>();
            foreach (var row in rows)
                foreach (var cell in row.Cells)
                    if (!string.IsNullOrWhiteSpace(cell.Text))
                        usedCols.Add(cell.Column);

            int effectiveCols = usedCols.Count;
            if (effectiveCols < 2) return 0f;

            // Fraction of filled slots out of (rows × effective columns). No row covers a column
            // twice (RowCells), so a row fills at most the columns used and this stays within 1.
            int totalSlots  = rows.Count * effectiveCols;
            int filledSlots = 0;
            foreach (var row in rows)
                foreach (var cell in row.Cells)
                    if (!string.IsNullOrWhiteSpace(cell.Text) && usedCols.Contains(cell.Column))
                        filledSlots++;

            return (float)filledSlots / totalSlots;
        }

        private static int AssignColumn(float x, List<float> separators)
        {
            for (int i = 0; i < separators.Count; i++)
                if (x < separators[i]) return i;
            return separators.Count;
        }

        // ------------------------------------------------------------------
        //  RTL / bidi helpers (invoice support)
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds a cell's text from its words.  With <see cref="TableExtractionOptions.ApplyBidi"/>
        /// the words are reassembled in logical reading order via the shared
        /// <see cref="TextDirection.ToReadingOrder(IReadOnlyList{PdfTextWord}, bool)"/>, in the
        /// table's direction (RTL descriptions read right-to-left, embedded numbers/codes stay
        /// intact, one space where words meet); otherwise the legacy left-to-right word join is used.
        /// </summary>
        private static string CellText(IEnumerable<PdfTextWord> words, TableExtractionOptions opts, bool rightToLeft)
        {
            // A cell's words line by line, top to bottom, each line in its own order: ordered by x
            // alone, the words of a two-line cell interleave ("Wireless with USB keyboard receiver").
            var lines = new List<string>();
            foreach (List<PdfTextWord> line in CellLines(words))
            {
                string text = opts.ApplyBidi
                    ? TextDirection.ToReadingOrder(line, rightToLeft: rightToLeft)
                    : string.Join(" ", line.OrderBy(w => w.X).Select(w => w.Text));
                if (text.Length > 0) lines.Add(text);
            }
            return string.Join(" ", lines);
        }

        /// <summary>
        /// A cell's words grouped into lines by their vertical centres — a word joins the line whose
        /// centre lies within half the smaller height of it — ordered top to bottom.
        /// </summary>
        private static List<List<PdfTextWord>> CellLines(IEnumerable<PdfTextWord> words)
        {
            var lines = new List<(float Centre, float Height, List<PdfTextWord> Words)>();
            foreach (var w in words.Where(w => w != null).OrderByDescending(w => w.Y + w.Height * 0.5f))
            {
                float cy = w.Y + w.Height * 0.5f;
                int at = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    float band = Math.Min(lines[i].Height, w.Height) * 0.5f;
                    if (Math.Abs(lines[i].Centre - cy) <= Math.Max(band, 0.5f)) { at = i; break; }
                }
                if (at < 0) lines.Add((cy, w.Height, new List<PdfTextWord> { w }));
                else lines[at].Words.Add(w);
            }
            return lines.Select(l => l.Words).ToList();
        }

        /// <summary>
        /// Renumbers a table's columns right-to-left so column 0 becomes the rightmost zone, matching
        /// RTL header reading order.  Bounding boxes and span widths are unchanged; only the column
        /// index of each cell is mirrored (a cell spanning [c, c+span-1] becomes [n-c-span, n-c-1]).
        /// </summary>
        private static PdfTable ReverseColumns(PdfTable t)
        {
            int n = t.ColumnCount;
            var newRows = new List<PdfTableRow>(t.Rows.Count);
            foreach (var row in t.Rows)
            {
                var newCells = new List<PdfTableCell>(row.Cells.Count);
                foreach (var c in row.Cells)
                {
                    int newStart = n - c.Column - c.ColSpan;
                    if (newStart < 0) newStart = 0;
                    newCells.Add(new PdfTableCell(c.Text, c.X, c.Y, c.Width, c.Height, newStart, c.ColSpan));
                }
                newCells.Sort((a, b) => a.Column.CompareTo(b.Column));
                newRows.Add(new PdfTableRow(newCells, row.Y));
            }
            return new PdfTable(t.PageIndex, newRows, n, t.Confidence);
        }

        // ------------------------------------------------------------------
        //  Shared cluster helper
        // ------------------------------------------------------------------

        /// <summary>Groups nearby float values (must be pre-sorted) and returns their averages.</summary>
        private static List<float> ClusterFloats(List<float> sorted, float tolerance)
        {
            var clusters = new List<List<float>>();
            var cur      = new List<float> { sorted[0] };

            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] - sorted[i - 1] <= tolerance)
                    cur.Add(sorted[i]);
                else
                {
                    clusters.Add(cur);
                    cur = new List<float> { sorted[i] };
                }
            }
            clusters.Add(cur);

            return clusters.Select(c => c.Sum() / c.Count).ToList();
        }
    }
}
