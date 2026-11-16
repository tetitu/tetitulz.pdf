// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>
    /// Model-driven table renderer for <see cref="PdfExporter"/>: equal- or explicit-width
    /// columns (<see cref="Table.ColumnWidths"/>) with gridSpan, vertical merge (vMerge
    /// restart/continue), nested tables, per-side cell borders, cell background shading,
    /// vertical alignment, per-cell margins, RTL column mirroring, content-derived (or
    /// explicit) row heights, row pagination, and header-row repetition across page breaks.
    /// </summary>
    internal static class TableRenderer
    {
        // Tables nest (a Table inside a cell). Render↔DrawRow↔Render and Measure↔RowHeight↔Measure
        // recurse once per nesting level with no bound, so a pathologically deep Builder model (or
        // a deeply-nested DOCX mapped via the non-legacy path) would StackOverflow — uncatchable,
        // crashing the host. This thread-local counter caps render-time nesting; both entry points
        // (Render and Measure) share it because they form the recursion cycle together. The cap
        // itself is PdfLimits.MaxTableNestingDepth, shared with the other table layout engine.
        [ThreadStatic] private static int _depth;

        // A cell with its resolved column span and start column within the grid.
        private sealed class Placed
        {
            public TableCell Cell = null!;
            public int StartCol;
            public int Span;
        }

        /// <summary>
        /// Upper bound on a table's resolved column count.
        /// </summary>
        /// <remarks>
        /// <para><c>GridSpan</c> is copied straight out of the input file by the DOCX mapper, so
        /// the column count is a file-controlled integer that was summed with no cap and then used
        /// to size <c>new double[cols]</c>: <c>&lt;w:gridSpan w:val="200000000"/&gt;</c> asked for a
        /// 1.6 GB array, and a value near <see cref="int.MaxValue"/> overflowed the sum into a
        /// negative index instead. Either way one small file took the conversion down.</para>
        /// <para><b>It belongs in <c>PdfLimits</c> beside <c>MaxTableNestingDepth</c></b>, which the
        /// other table engine shares. It is here for now because that constant class is outside this
        /// change's scope; move it down when the two engines are next unified.</para>
        /// </remarks>
        internal const int MaxTableColumns = 4096;

        public static double Render(PdfExporter.Ctx ctx, Table table, double x, double y,
            double width, bool paginate = true)
        {
            // refuse pathological nesting (StackOverflow guard) — and SAY so: a dropped subtree and
            // an empty table were indistinguishable in the output and in the diagnostic sink.
            if (_depth >= PdfLimits.MaxTableNestingDepth) { ReportDepthLimit(ctx); return y; }
            _depth++;
            try { return RenderCore(ctx, table, x, y, width, paginate); }
            finally { _depth--; }
        }

        private static double RenderCore(PdfExporter.Ctx ctx, Table table, double x, double y,
            double width, bool paginate)
        {
            if (table.Rows.Count == 0) return y;

            int cols = ColumnCount(ctx, table);
            if (cols == 0) return y;

            double tableWidth = TableWidth(ctx, table, cols, width);
            double[] colW = ResolveColumnWidths(ctx, table, cols, ColumnSpace(table, cols, tableWidth));
            // Through PdfExporter's single resolver, NOT an open-coded `== RightToLeft`: adding
            // TextDirection.Auto to three independent comparisons is exactly how one renderer
            // changes while another silently disagrees.
            bool rtl = table.RtlColumns || PdfExporter.IsSectionRtl(ctx.Section);

            // A narrower table's place in its band (Table.Alignment): every edge below is drawn
            // from x, so moving it once moves the whole table.
            double bandX = x;
            x = Aligned(ctx, table, bandX, width, tableWidth);

            y -= table.SpacingBefore;
            if (paginate) ctx.Y = y;

            var rows = new List<List<Placed>>(table.Rows.Count);
            var heights = new double[table.Rows.Count];

            // Pass A — placements + row heights, and the structure skeleton.
            //
            // Built here rather than in DrawRow because a repeated header row calls DrawRow again
            // on the SAME logical row: building there would emit a duplicate /TR per page.
            var cellElements = new Dictionary<Placed, PdfStructureElement>();
            var tableEl = ctx.Tagged && ctx.ArtifactType == null
                ? ctx.NewElement(PdfStructureTypes.Table) : null;

            for (int i = 0; i < table.Rows.Count; i++)
            {
                var placed = PlaceRow(table.Rows[i], cols);
                rows.Add(placed);
                heights[i] = RowHeight(ctx, table, table.Rows[i], placed, colW);

                if (tableEl == null) continue;
                var rowEl = tableEl.AddChild(PdfStructureTypes.TR);
                foreach (var pl in placed)
                {
                    // A vMerge continuation is not a cell of its own — the merged cell above
                    // spans into it. Emitting one would make every row after the first look wider
                    // than the table is, which a regularity check reports as an error.
                    if (pl.Cell.VMerge == VerticalMerge.Continue) continue;
                    var cellEl = rowEl.AddChild(pl.Cell.IsHeader
                        ? PdfStructureTypes.TH : PdfStructureTypes.TD);
                    // Kept even when empty: a missing cell makes the table ragged. It is given an
                    // empty marked sequence below so it still reaches content.
                    cellEl.ExemptFromPruning = true;
                    cellEl.Attributes = BuildCellAttributes(pl);
                    cellElements[pl] = cellEl;
                }
            }

            // Pass B — draw with pagination + header repeat.
            var headerRows = RepeatedHeaderRows(ctx, table, heights);
            double rowGap = table.RowSpacingPt;
            double frameTop = y;
            y -= rowGap;

            for (int i = 0; i < table.Rows.Count; i++)
            {
                double rh = heights[i];
                if (paginate && y - rh < ctx.BottomLimit && y < ctx.TopStart - 0.01)
                {
                    DrawSpacedFrame(ctx, table, x, tableWidth, frameTop, y);
                    // The rows go on in the content area the break reaches, at its left edge, in the
                    // band it gives — of another width, the table's own width read again in it: a table
                    // of no width of its own takes the new one, its columns resolved again and the rows
                    // still to come and the repeated header measured at them.
                    double leftBefore = ctx.FlowLeft, widthBefore = ctx.FlowWidth;
                    ctx.NewPageContinue();
                    bandX += ctx.FlowLeft - leftBefore;
                    if (Math.Abs(ctx.FlowWidth - widthBefore) > 0.01)
                    {
                        width = Math.Max(1, width + ctx.FlowWidth - widthBefore);
                        double resized = TableWidth(ctx, table, cols, width);
                        if (Math.Abs(resized - tableWidth) > 0.01)
                        {
                            tableWidth = resized;
                            colW = ResolveColumnWidths(ctx, table, cols, ColumnSpace(table, cols, tableWidth));
                            for (int r = 0; r < table.Rows.Count; r++)
                                if (r >= i || headerRows.Contains(r)) heights[r] = RowHeight(ctx, table, table.Rows[r], rows[r], colW);
                            rh = heights[i];
                        }
                    }
                    x = Aligned(ctx, table, bandX, width, tableWidth);
                    frameTop = ctx.Y;
                    y = ctx.Y - rowGap;
                    foreach (int hr in headerRows)
                    {
                        if (hr >= i) break;
                        DrawRow(ctx, table, rows, heights, hr, x, y, colW, tableWidth, rtl, cellElements, repeatedHeader: true, clampToPage: paginate);
                        y -= heights[hr] + rowGap;
                    }
                }
                DrawRow(ctx, table, rows, heights, i, x, y, colW, tableWidth, rtl, cellElements, clampToPage: paginate);
                y -= rh + rowGap;
                if (paginate) ctx.Y = y;
            }
            DrawSpacedFrame(ctx, table, x, tableWidth, frameTop, y);

            y -= table.SpacingAfter;
            if (paginate) ctx.Y = y;
            return y;
        }

        /// <summary>
        /// Where a table <paramref name="tableWidth"/> wide starts in a band at <paramref name="bandX"/>,
        /// <paramref name="width"/> wide (<see cref="Table.Alignment"/>): at the band's left, its centre,
        /// or its right end.
        /// </summary>
        private static double Aligned(PdfExporter.Ctx ctx, Table table, double bandX, double width, double tableWidth)
        {
            double spare = width - tableWidth;
            if (spare <= 0) return bandX;
            if (table.Alignment == TextAlignment.Center) return bandX + spare / 2;
            if (table.Alignment == TextAlignment.Right
                || (table.Alignment == TextAlignment.End && !PdfExporter.IsSectionRtl(ctx.Section)))
                return bandX + spare;
            return bandX;
        }

        /// <summary>
        /// The rows repeated at the top of each continuation page: the header rows the table STARTS
        /// with, and none when those would take more than half a page.
        /// </summary>
        /// <remarks>
        /// <para><b>Leading rows only.</b> Repeating every row marked a header, wherever it stands,
        /// a producer that marks row labels as headers — a row holding a <c>&lt;th&gt;</c>, say —
        /// would get every row placed so far re-drawn on each new page, a sixty-row table running to
        /// thirty-one pages of the same rows. A header is what a table begins with; one further down is a label.</para>
        /// <para><b>Half a page at most.</b> A continuation page must have room for the rows it
        /// continues; a header block taller than that would leave each page one row of progress, or
        /// none, and is not repeated at all.</para>
        /// </remarks>
        private static List<int> RepeatedHeaderRows(PdfExporter.Ctx ctx, Table table, double[] heights)
        {
            var rows = new List<int>();
            if (!table.RepeatHeaderRow) return rows;

            double total = 0;
            for (int i = 0; i < table.Rows.Count && table.Rows[i].IsHeader; i++)
            {
                rows.Add(i);
                total += heights[i];
            }

            if (total > (ctx.TopStart - ctx.BottomLimit) / 2) rows.Clear();
            return rows;
        }

        /// <summary>
        /// The height of the start of the table — its leading header rows and its first body row —
        /// which is what must fit for the table to begin on a page, and what a block kept with the
        /// table has to share the page with.
        /// </summary>
        internal static double MeasureLead(PdfExporter.Ctx ctx, Table table, double width)
        {
            if (table.Rows.Count == 0) return 0;
            int cols = ColumnCount(ctx, table);
            if (cols == 0) return 0;
            double tableWidth = TableWidth(ctx, table, cols, width);
            double[] colW = ResolveColumnWidths(ctx, table, cols, ColumnSpace(table, cols, tableWidth));

            double h = table.SpacingBefore + table.RowSpacingPt;
            for (int i = 0; i < table.Rows.Count; i++)
            {
                h += RowHeight(ctx, table, table.Rows[i], PlaceRow(table.Rows[i], cols), colW) + table.RowSpacingPt;
                if (!table.Rows[i].IsHeader) break;
            }
            return h;
        }

        /// <summary>
        /// The table's width in a band <paramref name="width"/> wide: its stated
        /// <see cref="Table.WidthPt"/>, capped at the band; else, for a
        /// <see cref="Table.ShrinkToContent"/> table, its columns' max-content widths and the
        /// spacing between them (CSS's automatic table width — a table with no width is as wide as
        /// its content, up to its container), the band when a column cannot be measured (a nested
        /// table with such a column, past the nesting cap); else the band. One answer for the
        /// draw and both measures, which size rows by it.
        /// </summary>
        private static double TableWidth(PdfExporter.Ctx ctx, Table table, int cols, double width)
        {
            if (table.WidthPt is double w && w > 0) return Math.Min(w, width);
            if (!table.ShrinkToContent) return width;
            return NaturalWidth(ctx, table, cols, minContent: false) is double natural
                ? Math.Min(width, natural + PdfExporter.ShrinkToFitSlackPt)
                : width;
        }

        /// <summary>
        /// The width <paramref name="table"/>'s content takes: its cell spacing and, per column,
        /// its content (the longest words when <paramref name="minContent"/>, else the unwrapped lines)
        /// or its stated width (<see cref="Table.ColumnWidths"/>, in points — HTML's
        /// <c>&lt;th width="60"&gt;</c>) where that is wider; <c>null</c> when a column has neither and
        /// holds content whose width cannot be known (<see cref="ColumnContentWidths(PdfExporter.Ctx, Table, int, bool, out bool[])"/>).
        /// A column of empty cells (a flex line's filler after its items) is 0 wide, not unknown.
        /// </summary>
        private static double? NaturalWidth(PdfExporter.Ctx ctx, Table table, int cols, bool minContent)
        {
            double sum = table.CellSpacingPt * (cols + 1);
            double[] content = ColumnContentWidths(ctx, table, cols, minContent, out bool[] unmeasured);
            for (int i = 0; i < cols; i++)
            {
                double stated = i < table.ColumnWidths.Count ? table.ColumnWidths[i] : 0;
                double col = Math.Max(content[i], stated);
                if (col <= 0 && unmeasured[i]) return null;
                sum += col;
            }
            return sum;
        }

        /// <summary>
        /// The width <paramref name="table"/> shrinks to inside a band <paramref name="width"/> wide:
        /// its stated width, else its <see cref="NaturalWidth"/> — of its longest words when
        /// <paramref name="minContent"/>, else of its unwrapped lines — with the shrink-to-fit slack;
        /// capped at the band. 0 for no rows; <c>null</c> when a column's width cannot be known, or
        /// the table is past <see cref="PdfLimits.MaxTableNestingDepth"/>. What
        /// <see cref="PdfExporter.MeasureShrinkToFitWidth(Table, double)"/> answers, and a nested
        /// table's width in its column.
        /// </summary>
        internal static double? MeasureWidth(PdfExporter.Ctx ctx, Table table, double width, bool minContent)
        {
            // The nesting cap the draw keeps, counted as it counts: a table the draw would drop
            // (and report) has no width to measure — and a nested one is measured through here, so
            // a chain of them stops where the draw stops.
            if (_depth >= PdfLimits.MaxTableNestingDepth) return null;
            _depth++;
            try { return MeasureWidthCore(ctx, table, width, minContent); }
            finally { _depth--; }
        }

        private static double? MeasureWidthCore(PdfExporter.Ctx ctx, Table table, double width, bool minContent)
        {
            if (table.Rows.Count == 0) return 0;
            int cols = ColumnCount(ctx, table);
            if (cols == 0) return 0;
            if (table.WidthPt is double w && w > 0) return Math.Min(w, width);
            return NaturalWidth(ctx, table, cols, minContent) is double natural
                ? Math.Min(width, natural + PdfExporter.ShrinkToFitSlackPt)
                : null;
        }

        // Total height the table would occupy (for nested-table measurement).
        public static double Measure(PdfExporter.Ctx ctx, Table table, double width)
        {
            // The measuring half of the same recursion cycle, and the more damaging one: it returns
            // 0, so the surrounding cell is sized as if the dropped content did not exist.
            if (_depth >= PdfLimits.MaxTableNestingDepth) { ReportDepthLimit(ctx); return 0; }
            _depth++;
            try { return MeasureCore(ctx, table, width); }
            finally { _depth--; }
        }

        private static double MeasureCore(PdfExporter.Ctx ctx, Table table, double width)
        {
            if (table.Rows.Count == 0) return 0;
            int cols = ColumnCount(ctx, table);
            if (cols == 0) return 0;
            double tableWidth = TableWidth(ctx, table, cols, width);
            double[] colW = ResolveColumnWidths(ctx, table, cols, ColumnSpace(table, cols, tableWidth));
            double h = table.SpacingBefore + table.SpacingAfter + table.RowSpacingPt;
            for (int i = 0; i < table.Rows.Count; i++)
                h += RowHeight(ctx, table, table.Rows[i], PlaceRow(table.Rows[i], cols), colW) + table.RowSpacingPt;
            return h;
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// The per-column widths: <see cref="DistributeColumnWidths"/>; for an
        /// <see cref="Table.AutoLayout"/> table the auto columns' share sized by their content
        /// (<see cref="ShareByContent"/>); then widened where a column would be narrower than its
        /// content's longest unbreakable word.
        /// </summary>
        private static double[] ResolveColumnWidths(PdfExporter.Ctx ctx, Table table, int cols, double tableWidth)
        {
            double[] colW = DistributeColumnWidths(table, cols, tableWidth);
            double[] min = ColumnContentWidths(ctx, table, cols, minContent: true);
            if (table.AutoLayout)
                ShareByContent(colW, AutoColumns(table, cols), min,
                               ColumnContentWidths(ctx, table, cols, minContent: false));
            WidenToMinimums(colW, min, tableWidth);
            return colW;
        }

        /// <summary>Which columns have no explicit width in <see cref="Table.ColumnWidths"/>.</summary>
        private static bool[] AutoColumns(Table table, int cols)
        {
            var auto = new bool[cols];
            for (int i = 0; i < cols; i++)
                auto[i] = !(table.ColumnWidths != null && i < table.ColumnWidths.Count && table.ColumnWidths[i] > 0);
            return auto;
        }

        /// <summary>
        /// Re-divides the width the auto columns were given among them by their content, as CSS's
        /// automatic table layout does: in proportion to max-content when every column fits
        /// unwrapped, else each its min-content plus a share of the rest in proportion to how much
        /// more its max-content wants. Explicit columns keep theirs.
        /// </summary>
        /// <remarks>
        /// <b>An auto column with no measurable content leaves the division as it was</b> — the
        /// equal split. Its max-content is 0 when it holds only empty cells, cells spanning into it,
        /// or a nested table whose own width cannot be known, none of which this measures; content
        /// sizing would give it nothing, and a column of nothing loses whatever those cells hold.
        /// </remarks>
        internal static void ShareByContent(double[] colW, bool[] auto, double[] min, double[] max)
        {
            double avail = 0, sumMin = 0, sumMax = 0;
            for (int i = 0; i < colW.Length; i++)
            {
                if (!auto[i]) continue;
                if (max[i] <= 0) return;
                avail += colW[i];
                sumMin += min[i];
                sumMax += Math.Max(max[i], min[i]);
            }
            if (avail <= 0 || sumMax <= 0) return;

            for (int i = 0; i < colW.Length; i++)
            {
                if (!auto[i]) continue;
                double hi = Math.Max(max[i], min[i]);
                if (sumMax <= avail) colW[i] = hi * avail / sumMax;
                else if (sumMin >= avail) colW[i] = sumMin > 0 ? min[i] * avail / sumMin : avail * hi / sumMax;
                else colW[i] = min[i] + (hi - min[i]) * (avail - sumMin) / (sumMax - sumMin);
            }
        }

        /// <summary>
        /// Each column's widest single-column cell — its min-content width when
        /// <paramref name="minContent"/> (<see cref="PdfExporter.MinContentWidth"/>), else its
        /// max-content width (<see cref="PdfExporter.MaxContentWidth"/>) — plus that cell's padding
        /// and margins: its paragraphs, its lists, its nested tables (CSS 2.1 §17.5.2.2: a cell's
        /// content, whatever it is, sets its column's minimum and maximum).
        /// </summary>
        /// <remarks>
        /// <para>A spanning cell and a merge continuation add nothing: they are the cases CSS
        /// distributes over several columns, and a floor from any one of them would be a guess about
        /// which column should take it.</para>
        /// <para>A cell with no text adds nothing either, padding included: a word processor's
        /// narrow spacer column is empty, and flooring it at its padding would widen it.</para>
        /// </remarks>
        private static double[] ColumnContentWidths(PdfExporter.Ctx ctx, Table table, int cols, bool minContent)
            => ColumnContentWidths(ctx, table, cols, minContent, out _);

        /// <summary>
        /// As <see cref="ColumnContentWidths(PdfExporter.Ctx, Table, int, bool)"/>, also naming the
        /// columns some cell of which holds a block whose width cannot be known — a nested table with
        /// such a column, or one past <see cref="PdfLimits.MaxTableNestingDepth"/>, a table of contents,
        /// a loose list item — so a 0 there is unknown rather than empty. Measured once per context
        /// (<see cref="PdfExporter.Ctx.ColumnContent"/>); the arrays handed out are copies.
        /// </summary>
        private static double[] ColumnContentWidths(PdfExporter.Ctx ctx, Table table, int cols,
            bool minContent, out bool[] unmeasured)
        {
            var key = (table, minContent, cols);
            if (!ctx.ColumnContent.TryGetValue(key, out var known))
            {
                double[] widths = MeasureColumnContent(ctx, table, cols, minContent, out bool[] unknown);
                ctx.ColumnContent[key] = known = (widths, unknown);
            }
            unmeasured = (bool[])known.Unmeasured.Clone();
            return (double[])known.Widths.Clone();
        }

        /// <summary>
        /// How many times this thread has measured a table's column content afresh — the work
        /// <see cref="PdfExporter.Ctx.ColumnContent"/> saves, for a test to see the cache is live
        /// (its answer and the uncached one agree).
        /// </summary>
        [ThreadStatic] internal static long ColumnContentMeasures;

        private static double[] MeasureColumnContent(PdfExporter.Ctx ctx, Table table, int cols,
            bool minContent, out bool[] unmeasured)
        {
            ColumnContentMeasures++;
            Func<PdfExporter.Ctx, Paragraph, double> measure = minContent ? PdfExporter.MinContentWidth : PdfExporter.MaxContentWidth;
            var min = new double[cols];
            unmeasured = new bool[cols];
            foreach (var row in table.Rows)
            {
                foreach (var pl in PlaceRow(row, cols))
                {
                    if (pl.Span != 1 || pl.Cell.VMerge == VerticalMerge.Continue) continue;

                    // A paragraph inside boxes (BoxStart) needs their margins, borders and padding
                    // beside its words.
                    double content = 0, inset = 0;
                    var insets = new Stack<double>();
                    foreach (var block in pl.Cell.Content())
                    {
                        switch (block)
                        {
                            case BoxStart box:
                                double boxInset = PdfExporter.BoxHorizontalInsets(box);
                                insets.Push(boxInset);
                                inset += boxInset;
                                break;
                            case BoxEnd when insets.Count > 0:
                                inset -= insets.Pop();
                                break;
                            case Paragraph p:
                                double words = measure(ctx, p);
                                if (words > 0) content = Math.Max(content, words + inset);
                                break;
                            case BoxEnd:
                                break;
                            // A nested table or a list at its own min- or max-content width, measured
                            // by the layout that draws it; a nested table nothing says the width of
                            // leaves the column unknown, as does one past the nesting cap.
                            case Table nested:
                                double? inner = MeasureWidth(ctx, nested, double.MaxValue, minContent);
                                if (inner == null) unmeasured[pl.StartCol] = true;
                                else if (inner.Value > 0) content = Math.Max(content, inner.Value + inset);
                                break;
                            case ListBlock list:
                                double items = PdfExporter.ListContentWidth(ctx, list, minContent);
                                if (items > 0) content = Math.Max(content, items + inset);
                                break;
                            default:
                                unmeasured[pl.StartCol] = true;
                                break;
                        }
                    }
                    if (content <= 0) continue;

                    double w = content + pl.Cell.MarginLeft + pl.Cell.MarginRight + 2 * pl.Cell.InnerPaddingPt;
                    if (w > min[pl.StartCol]) min[pl.StartCol] = w;
                }
            }
            return min;
        }

        /// <summary>
        /// Widens every column narrower than its minimum, taking the width from the columns that
        /// have more than theirs — the floor CSS table layout keeps, so a word is never wider than
        /// its cell while the table can hold every column's minimum.
        /// </summary>
        /// <remarks>
        /// <para><b>Only a column that would overflow changes anything</b>; a table whose words all
        /// fit keeps the widths it was given. The donors give in proportion to their spare width,
        /// so none is taken below its own minimum.</para>
        /// <para><b>When the minimums cannot all fit</b>, each column gets a share of the table in
        /// proportion to its minimum: CSS would make the table wider than its container, which a
        /// page cannot, and this spreads the overflow instead of leaving it all in one column.</para>
        /// <para>An HTML <c>&lt;col style="width:100%"&gt;</c> beside an auto column is the case:
        /// the over-specified set would leave the auto column a flat tenth of the table, and a form's
        /// label column its words standing out of the cell.</para>
        /// </remarks>
        internal static void WidenToMinimums(double[] colW, double[] min, double tableWidth)
        {
            double deficit = 0, spare = 0, sumMin = 0;
            for (int i = 0; i < colW.Length; i++)
            {
                sumMin += min[i];
                if (colW[i] < min[i]) deficit += min[i] - colW[i];
                else spare += colW[i] - min[i];
            }
            if (deficit <= 0) return;

            if (sumMin >= tableWidth)
            {
                for (int i = 0; i < colW.Length; i++) colW[i] = tableWidth * min[i] / sumMin;
                return;
            }

            // The widths given sum to tableWidth, so the spare exceeds the deficit by exactly
            // tableWidth − sumMin: below 1 here, and no donor is taken under its own minimum.
            double take = deficit / spare;
            for (int i = 0; i < colW.Length; i++)
                colW[i] = colW[i] < min[i] ? min[i] : colW[i] - (colW[i] - min[i]) * take;
        }

        // Distributes the table's width over its columns. Empty Table.ColumnWidths → uniform (each
        // column tableWidth/cols, identical to the original behaviour). Otherwise explicit (&gt;0)
        // entries are honoured and auto (&lt;=0 / missing) columns split the leftover width;
        // an over-specified set is scaled to fit (reserving a small share for auto columns).
        private static double[] DistributeColumnWidths(Table table, int cols, double tableWidth)
        {
            var colW = new double[cols];
            double specified = 0; int autoCount = 0;
            for (int i = 0; i < cols; i++)
            {
                double cw = table.ColumnWidths != null && i < table.ColumnWidths.Count ? table.ColumnWidths[i] : 0;
                if (cw > 0) { colW[i] = cw; specified += cw; } else autoCount++;
            }

            if (autoCount == cols)            // all auto → uniform (original math)
            {
                double u = tableWidth / cols;
                for (int i = 0; i < cols; i++) colW[i] = u;
                return colW;
            }
            if (autoCount == 0)               // all explicit → normalise to fill tableWidth
            {
                double f = specified > 0 ? tableWidth / specified : 1;
                for (int i = 0; i < cols; i++) colW[i] *= f;
                return colW;
            }
            if (specified >= tableWidth)      // over-specified → reserve 10% for auto columns
            {
                double autoShare = tableWidth * 0.10 / autoCount;
                double f = (tableWidth - autoShare * autoCount) / specified;
                for (int i = 0; i < cols; i++) colW[i] = colW[i] > 0 ? colW[i] * f : autoShare;
                return colW;
            }
            double share = (tableWidth - specified) / autoCount;   // auto columns split leftover
            for (int i = 0; i < cols; i++) if (colW[i] <= 0) colW[i] = share;
            return colW;
        }

        /// <summary>
        /// Whether the table's own border is a frame at its edge, apart from its cells: when the
        /// cells are spaced (CSS's separated model), the frame lies outside the spacing, not on the
        /// outer cells' edges.
        /// </summary>
        private static bool HasSpacedFrame(Table table) =>
            table.BorderWidth > 0 && (table.CellSpacingPt > 0 || table.RowSpacingPt > 0);

        /// <summary>The table's frame round the part of it on this page, from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
        private static void DrawSpacedFrame(PdfExporter.Ctx ctx, Table table, double x, double width, double top, double bottom)
        {
            if (!HasSpacedFrame(table) || top - bottom <= 0) return;
            var frame = new Border(table.BorderWidth, table.BorderColor);
            using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
            {
                PdfExporter.StrokeEdge(ctx.Page, frame, x, top, x + width, top);
                PdfExporter.StrokeEdge(ctx.Page, frame, x, bottom, x + width, bottom);
                PdfExporter.StrokeEdge(ctx.Page, frame, x, bottom, x, top);
                PdfExporter.StrokeEdge(ctx.Page, frame, x + width, bottom, x + width, top);
            }
        }

        /// <summary>
        /// The columns' share of the table's width: the width less the cell spacing on each side of
        /// every column (<see cref="Table.CellSpacingPt"/>) — all of it when there is none.
        /// </summary>
        private static double ColumnSpace(Table table, int cols, double tableWidth) =>
            table.CellSpacingPt > 0 ? Math.Max(1, tableWidth - (cols + 1) * table.CellSpacingPt) : tableWidth;

        /// <summary>A cell spanning <paramref name="span"/> columns from <paramref name="start"/>: their widths and the spacing between them.</summary>
        private static double SpanWidth(double[] colW, int start, int span, double spacing = 0)
        {
            double s = 0;
            int n = 0;
            for (int i = start; i < start + span && i < colW.Length; i++) { s += colW[i]; n++; }
            return s + Math.Max(0, n - 1) * spacing;
        }

        /// <summary>Where column <paramref name="idx"/> starts, from the table's left edge: the columns before it and the spacing before each, and before it.</summary>
        private static double EdgeX(double[] colW, int idx, double spacing = 0)
        {
            double s = spacing;
            for (int i = 0; i < idx && i < colW.Length; i++) s += colW[i] + spacing;
            return s;
        }

        /// <summary>
        /// The table's column count, clamped to <see cref="MaxTableColumns"/>.
        /// </summary>
        /// <remarks>
        /// Accumulated in <see cref="long"/> because the per-cell span is file-controlled: summing
        /// in <c>int</c> let a handful of cells near <see cref="int.MaxValue"/> wrap the total
        /// negative, and the unclamped total then sized <c>new double[cols]</c> and three
        /// <c>O(cols)</c> loops.
        /// </remarks>
        private static int ColumnCount(PdfExporter.Ctx ctx, Table table)
        {
            long cols = 0;
            foreach (var row in table.Rows)
            {
                long sum = 0;
                foreach (var c in row.Cells)
                {
                    sum += Math.Min(Math.Max(1, c.GridSpan), MaxTableColumns);
                    if (sum >= MaxTableColumns) { sum = MaxTableColumns; break; }
                }
                if (sum > cols) cols = sum;
            }
            if (cols > MaxTableColumns)
            {
                cols = MaxTableColumns;
            }
            if (cols == MaxTableColumns) ReportColumnClamp(ctx);
            return (int)cols;
        }

        private static List<Placed> PlaceRow(TableRow row, int cols)
        {
            var list = new List<Placed>(row.Cells.Count);
            int col = 0;
            foreach (var cell in row.Cells)
            {
                if (col >= cols) break;                 // past the clamped grid: nothing to place
                int span = Math.Min(Math.Max(1, cell.GridSpan), cols - col);
                list.Add(new Placed { Cell = cell, StartCol = col, Span = span });
                col += span;
            }
            return list;
        }

        /// <summary>
        /// Reports a tolerated table limit once per export.
        /// </summary>
        /// <remarks>
        /// CLAUDE.md's Diagnostics Pattern names exactly this shape: a failure the code tolerates
        /// must report, or "the table was empty" and "the table was dropped" are the same answer to
        /// a batch converter. The one-shot flags live on the export context so a model that is wide
        /// as well as deep cannot flood the sink.
        /// </remarks>
        private static void ReportDepthLimit(PdfExporter.Ctx ctx)
        {
            if (ctx == null || ctx.ReportedTableDepthLimit) return;
            ctx.ReportedTableDepthLimit = true;
            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                $"table nesting past {PdfLimits.MaxTableNestingDepth} levels was dropped, and the " +
                "cell containing it was sized as if that content did not exist.");
        }

        private static void ReportColumnClamp(PdfExporter.Ctx ctx)
        {
            if (ctx == null || ctx.ReportedTableColumnClamp) return;
            ctx.ReportedTableColumnClamp = true;
            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                $"a table asked for more than {MaxTableColumns} columns (a cell's GridSpan comes " +
                "straight from the source file); the grid was clamped and cells past the limit " +
                "were dropped.");
        }

        private static void ReportMergedCellClamp(PdfExporter.Ctx ctx)
        {
            if (ctx == null || ctx.ReportedMergedCellClamp) return;
            ctx.ReportedMergedCellClamp = true;
            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                "a vertically merged cell spans a page break; its border and background were " +
                "clamped to the bottom of the page instead of being continued on the next one.");
        }

        private static double RowHeight(PdfExporter.Ctx ctx, Table table, TableRow row, List<Placed> placed, double[] colW)
        {
            if (row.ExactHeight && row.HeightPt is double eh && eh > 0) return eh;
            if (ctx.RowHeights.TryGetValue((row, colW), out double known)) return known;
            double height = MeasureRowHeight(ctx, table, row, placed, colW);
            ctx.RowHeights[(row, (double[])colW.Clone())] = height;
            return height;
        }

        /// <summary>
        /// How many times this thread has measured a row's height afresh — the work
        /// <see cref="PdfExporter.Ctx.RowHeights"/> saves, for a test to see the cache is live.
        /// </summary>
        [ThreadStatic] internal static long RowHeightMeasures;

        private static double MeasureRowHeight(PdfExporter.Ctx ctx, Table table, TableRow row, List<Placed> placed, double[] colW)
        {
            RowHeightMeasures++;
            double[]? shifts = BaselineShifts(ctx, table, placed, colW, out _);
            double content = 0;
            for (int i = 0; i < placed.Count; i++)
            {
                Placed pl = placed[i];
                if (pl.Cell.VMerge == VerticalMerge.Continue) continue;
                double h = MeasureCell(ctx, pl.Cell, InnerWidth(table, pl, colW))
                           + pl.Cell.MarginTop + pl.Cell.MarginBottom + 2 * pl.Cell.InnerPaddingPt
                           + (shifts?[i] ?? 0);
                if (h > content) content = h;
            }
            double min = row.HeightPt is double mh && mh > 0 ? mh : table.MinRowHeightPt;
            return Math.Max(content, min);
        }

        /// <summary>
        /// A row and the column widths it is laid out at, as a key: the row by identity — through
        /// <see cref="Tetitulz.Pdf.Core.Objects.PdfReferenceEquality"/>, the one identity comparer — the
        /// widths by value.
        /// </summary>
        internal sealed class RowAtWidths : IEqualityComparer<(TableRow Row, double[] Widths)>
        {
            internal static readonly RowAtWidths Instance = new RowAtWidths();

            private static readonly Tetitulz.Pdf.Core.Objects.PdfReferenceEquality Identity = Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance;

            public bool Equals((TableRow Row, double[] Widths) a, (TableRow Row, double[] Widths) b)
            {
                if (!Identity.Equals(a.Row, b.Row) || a.Widths.Length != b.Widths.Length) return false;
                for (int i = 0; i < a.Widths.Length; i++)
                    if (!a.Widths[i].Equals(b.Widths[i])) return false;
                return true;
            }

            public int GetHashCode((TableRow Row, double[] Widths) key)
            {
                int hash = Identity.GetHashCode(key.Row);
                foreach (double w in key.Widths) hash = unchecked(hash * 31 + w.GetHashCode());
                return hash;
            }
        }

        /// <summary>The width a placed cell's content is laid out in: its span less its margins and padding, at least 1.</summary>
        private static double InnerWidth(Table table, Placed pl, double[] colW)
            => Math.Max(1, SpanWidth(colW, pl.StartCol, pl.Span, table.CellSpacingPt) - pl.Cell.MarginLeft - pl.Cell.MarginRight - 2 * pl.Cell.InnerPaddingPt);

        /// <summary>
        /// How far each of a row's <see cref="CellVerticalAlign.Baseline"/> cells is lowered so their
        /// first baselines share one line (CSS 2.1 §17.5.3): the row's baseline is the greatest
        /// distance from a baseline cell's top to its first baseline — under its top margin and padding;
        /// a cell with no line has its baseline at its content's bottom — and each is lowered by what
        /// it lacks of it. <c>null</c> (and no baseline) when the row has no baseline cell; 0 for every
        /// other cell. Per placed cell, in <paramref name="placed"/>'s order.
        /// </summary>
        private static double[]? BaselineShifts(PdfExporter.Ctx ctx, Table table, List<Placed> placed, double[] colW, out double? rowBaseline)
        {
            rowBaseline = null;
            if (!placed.Exists(pl => pl.Cell.VAlign == CellVerticalAlign.Baseline && pl.Cell.VMerge != VerticalMerge.Continue)) return null;
            var below = new double[placed.Count];
            double row = 0;
            for (int i = 0; i < placed.Count; i++)
            {
                Placed pl = placed[i];
                if (pl.Cell.VAlign != CellVerticalAlign.Baseline || pl.Cell.VMerge == VerticalMerge.Continue) { below[i] = double.NaN; continue; }
                double width = InnerWidth(table, pl, colW);
                double line = PdfExporter.FirstBaseline(ctx, pl.Cell.Content(), width) ?? MeasureCell(ctx, pl.Cell, width);
                below[i] = pl.Cell.MarginTop + pl.Cell.InnerPaddingPt + line;
                row = Math.Max(row, below[i]);
            }
            var shifts = new double[placed.Count];
            for (int i = 0; i < placed.Count; i++)
                if (!double.IsNaN(below[i])) shifts[i] = row - below[i];
            rowBaseline = row;
            return shifts;
        }

        /// <summary>
        /// The distance from <paramref name="table"/>'s top, laid out in a band <paramref name="width"/>
        /// wide, to its first row's baseline (CSS Tables 3 §3.10.2): the baseline its
        /// <see cref="CellVerticalAlign.Baseline"/> cells share, else the bottom content edge of its
        /// lowest cell — the cell's bottom less its bottom margin and padding, a merged cell's bottom in
        /// the last row it spans. With no rows, the table's top. <c>null</c> past
        /// <see cref="PdfLimits.MaxTableNestingDepth"/>, counted as the draw counts it.
        /// </summary>
        internal static double? FirstBaseline(PdfExporter.Ctx ctx, Table table, double width)
        {
            if (_depth >= PdfLimits.MaxTableNestingDepth) return null;
            _depth++;
            try
            {
                double top = table.SpacingBefore;
                if (table.Rows.Count == 0) return top;
                int cols = ColumnCount(ctx, table);
                if (cols == 0) return top;
                double[] colW = ResolveColumnWidths(ctx, table, cols, ColumnSpace(table, cols, TableWidth(ctx, table, cols, width)));
                top += table.RowSpacingPt;
                List<Placed> first = PlaceRow(table.Rows[0], cols);
                BaselineShifts(ctx, table, first, colW, out double? shared);
                if (shared is double baseline) return top + baseline;

                double height = RowHeight(ctx, table, table.Rows[0], first, colW);
                double lowest = 0;
                double[]? below = null;   // the rows under the first, measured only for a merged cell
                foreach (Placed pl in first)
                {
                    double cellHeight = height;
                    if (pl.Cell.VMerge == VerticalMerge.Restart)
                    {
                        below ??= RowsBelowFirst(ctx, table, cols, colW);
                        for (int r = 1; r < table.Rows.Count; r++)
                        {
                            Placed? next = FindAtCol(PlaceRow(table.Rows[r], cols), pl.StartCol);
                            if (next == null || next.Cell.VMerge != VerticalMerge.Continue) break;
                            cellHeight += table.RowSpacingPt + below[r];
                        }
                    }
                    lowest = Math.Max(lowest, cellHeight - pl.Cell.MarginBottom - pl.Cell.InnerPaddingPt);
                }
                return top + lowest;
            }
            finally { _depth--; }
        }

        /// <summary>Each row's height but the first's (index 0 left 0).</summary>
        private static double[] RowsBelowFirst(PdfExporter.Ctx ctx, Table table, int cols, double[] colW)
        {
            var heights = new double[table.Rows.Count];
            for (int r = 1; r < table.Rows.Count; r++)
                heights[r] = RowHeight(ctx, table, table.Rows[r], PlaceRow(table.Rows[r], cols), colW);
            return heights;
        }

        /// <summary>
        /// A cell's content height: its blocks measured by the walk that draws them, boxes
        /// (<see cref="BoxStart"/>) included, and floats among them registered at the measure's own
        /// coordinates — the page's put aside meanwhile — so the blocks after a float are measured
        /// beside it and the cell reaches the float's bottom.
        /// </summary>
        /// <remarks>
        /// A float in a cell was measured as an ordinary block and drawn as one: the cell's layout
        /// had no float, so the HTML mapper left a floated inline element in its line, reported.
        /// </remarks>
        private static double MeasureCell(PdfExporter.Ctx ctx, TableCell cell, double innerWidth)
            => MeasureBlocks(ctx, cell.Content(), innerWidth);

        /// <summary>
        /// The height <paramref name="blocks"/> take in a cell <paramref name="innerWidth"/> wide —
        /// the measure half of the cell's one layout loop, for a cell's content and for
        /// <see cref="PdfExporter.MeasureHeight"/> alike.
        /// </summary>
        internal static double MeasureBlocks(PdfExporter.Ctx ctx, IEnumerable<IDocElement> blocks, double innerWidth)
        {
            var pageFloats = ctx.DetachFloats();
            try
            {
                double end = PdfExporter.LayOutCellBlocks(ctx, blocks, 0, 0, innerWidth, measureOnly: true,
                    drawBlock: (_, _, y, _) => y,
                    measureBlock: (block, left, y, width) => MeasureCellBlock(ctx, block, left, y, width));
                if (ctx.HasFloats) end = Math.Min(end, ctx.LowestFloatBottom);
                return -end;
            }
            finally { ctx.RestoreFloats(pageFloats); }
        }

        /// <summary>
        /// One of a cell's blocks measured, and one drawn, without paginating — the two halves of
        /// <see cref="PdfExporter.LayOutCellBlocks"/>'s loop, which a floated box in the section's
        /// flow lays its blocks out through too.
        /// </summary>
        internal static double MeasureCellBlock(PdfExporter.Ctx ctx, IDocElement block, double left, double y, double width)
        {
            switch (block)
            {
                // A float takes no height of its own: it is registered where it will be drawn, and
                // the blocks after it are measured beside it, as PlaceFloat draws them.
                case Paragraph p when p.Float != FloatSide.None && p.FloatWidth > 0:
                    return y - PdfExporter.MeasureFloat(ctx, p, left, y, width);
                case Paragraph p: return PdfExporter.MeasureParagraphAt(ctx, p, left, y, width);
                case Table t: return Measure(ctx, t, width);
                // Measured through the same conversion the draw path uses, so the row is sized by
                // the paragraph it will actually be filled with.
                case ListItem li: return PdfExporter.MeasureParagraph(ctx, PdfExporter.ListItemParagraph(ctx, li), width);
                // Each item's body, at the list's own geometry (a label takes no height of its own);
                // the paragraphs DrawCellBlock fills the row with.
                case ListBlock lb:
                    double h = lb.SpacingBefore + lb.SpacingAfter;
                    foreach (var (_, body) in PdfExporter.ListParagraphs(ctx, lb))
                        h += PdfExporter.MeasureParagraph(ctx, body, width);
                    return h;
                default: return 0;
            }
        }

        /// <summary>Draws one of a cell's blocks without paginating, and returns where it ends — <see cref="MeasureCellBlock"/>'s other half.</summary>
        internal static double DrawCellBlock(PdfExporter.Ctx ctx, IDocElement block, double x, double y, double width)
            => DrawCellBlock(ctx, block, x, y, width, out _);

        /// <summary>Draws one of a cell's blocks without paginating, and returns where it ends.</summary>
        private static double DrawCellBlock(PdfExporter.Ctx ctx, IDocElement block, double x, double y,
            double width, out bool drew)
        {
            drew = true;
            switch (block)
            {
                case Paragraph p when p.Float != FloatSide.None && p.FloatWidth > 0:
                    return PdfExporter.PlaceFloat(ctx, p, x, y, width, paginate: false);
                case Paragraph p:
                    return PdfExporter.RenderParagraphAt(ctx, p, x, y, width, paginate: false);
                case Table t:
                    return Render(ctx, t, x, y, width, paginate: false);
                case ListItem li:
                    return PdfExporter.RenderParagraphAt(ctx, PdfExporter.ListItemParagraph(ctx, li), x, y, width,
                        paginate: false);
                case ListBlock lb:
                    // Flat rather than tagged /L inside a cell: the cell already owns a structure
                    // element. The paragraphs are the page's own (PdfExporter.ListParagraphs) — a
                    // label at its indent, discarding its advance, and a body at the item's text
                    // start — so the list keeps its stated text indent here too, and the measure
                    // pass above agrees with this one paragraph for paragraph, or the row is sized
                    // from one shape and filled with another.
                    y -= lb.SpacingBefore;
                    foreach (var (label, body) in PdfExporter.ListParagraphs(ctx, lb))
                    {
                        if (label != null) PdfExporter.RenderParagraphAt(ctx, label, x, y, width, paginate: false);
                        y = PdfExporter.RenderParagraphAt(ctx, body, x, y, width, paginate: false);
                    }
                    return y - lb.SpacingAfter;
                default:
                    drew = false;
                    return y;
            }
        }

        private static void DrawRow(PdfExporter.Ctx ctx, Table table, List<List<Placed>> rows,
            double[] heights, int rowIndex, double tableX, double rowTop, double[] colW,
            double tableWidth, bool rtl,
            Dictionary<Placed, PdfStructureElement>? cellElements = null, bool repeatedHeader = false,
            bool clampToPage = false)
        {
            double[]? shifts = BaselineShifts(ctx, table, rows[rowIndex], colW, out _);
            for (int index = 0; index < rows[rowIndex].Count; index++)
            {
                Placed pl = rows[rowIndex][index];
                if (pl.Cell.VMerge == VerticalMerge.Continue) continue; // drawn by its restart cell

                double spacing = table.CellSpacingPt;
                double cellW = SpanWidth(colW, pl.StartCol, pl.Span, spacing);
                // Mirrored in a right-to-left table: the cell's right edge, measured from the left,
                // becomes its left edge measured from the right.
                double visLeft = rtl
                    ? tableX + (tableWidth - (EdgeX(colW, pl.StartCol, spacing) + cellW))
                    : tableX + EdgeX(colW, pl.StartCol, spacing);

                double mergedH = heights[rowIndex];
                int lastRow = rowIndex;
                if (pl.Cell.VMerge == VerticalMerge.Restart)
                {
                    for (int r = rowIndex + 1; r < rows.Count; r++)
                    {
                        var below = FindAtCol(rows[r], pl.StartCol);
                        if (below != null && below.Cell.VMerge == VerticalMerge.Continue)
                        { mergedH += table.RowSpacingPt + heights[r]; lastRow = r; }
                        else break;
                    }
                }

                // A merged cell's height is summed from the continuation rows BELOW it, and pass B
                // decides the page break independently — so a merge spanning one drew its border
                // and background over the footer and off the sheet, while the rows it was supposed
                // to continue into rendered at the top of the next page. Clamping keeps the ink on
                // the page; continuing it on the next one is the full fix, and the difference is
                // what ContentApproximated exists to say.
                if (clampToPage && rowTop - mergedH < ctx.BottomLimit - 0.01)
                {
                    double fits = Math.Max(heights[rowIndex], rowTop - ctx.BottomLimit);
                    if (fits < mergedH)
                    {
                        mergedH = fits;
                        ReportMergedCellClamp(ctx);
                    }
                }

                double cellBottom = rowTop - mergedH;

                // A repeated header row is a visual convenience: the logical row already appears
                // once in the tree, so its second drawing is an artifact. Attaching the extra
                // MCIDs to the same /TR would make one row claim two disjoint page regions.
                PdfStructureElement? cellEl = null;
                if (!repeatedHeader) cellElements?.TryGetValue(pl, out cellEl);
                using var repeatScope = repeatedHeader
                    ? ctx.EnterArtifact(PdfArtifactTypes.Layout)
                    : default;

                // Background shading — decoration.
                using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                if (!string.IsNullOrEmpty(pl.Cell.BackgroundColor))
                {
                    var (br, bg, bb) = PdfExporter.ParseColorD(pl.Cell.BackgroundColor);
                    double under = SeamOverlap(rows[rowIndex], pl, colW, spacing);
                    double fillLeft = rtl ? visLeft - under : visLeft;
                    ctx.Page.SaveState().SetFillColorRgb((float)br, (float)bg, (float)bb);
                    ctx.Page.Rectangle(fillLeft, cellBottom, cellW + under, mergedH).Fill().RestoreState();
                }

                // Borders (per-side, falling back to the table default). A cell's own per-side
                // border always wins; the fallback is where Table.OuterBorderOnly applies — with
                // it set, the table's border is its outer FRAME (CSS `table { border: … }`)
                // rather than the default border of every cell (legacy `<table border="1">`).
                Border def = table.BorderWidth > 0
                    ? new Border(table.BorderWidth, table.BorderColor)
                    : new Border { None = true };
                Border interior = table.OuterBorderOnly ? new Border { None = true } : def;

                int cols = colW.Length;
                bool atLogicalLeft = pl.StartCol == 0;
                bool atLogicalRight = pl.StartCol + pl.Span >= cols;
                // RTL mirrors the grid, so the VISUAL left edge is the logical right one.
                // A spaced table's frame is its own (DrawSpacedFrame), not its outer cells' edges.
                bool outerIsFrame = !HasSpacedFrame(table);
                Border Fallback(bool onOuterEdge) => onOuterEdge && outerIsFrame ? def : interior;

                using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
                {
                    PdfExporter.StrokeEdge(ctx.Page, pl.Cell.BorderTop ?? Fallback(rowIndex == 0),
                        visLeft, rowTop, visLeft + cellW, rowTop);
                    PdfExporter.StrokeEdge(ctx.Page, pl.Cell.BorderBottom ?? Fallback(lastRow == rows.Count - 1),
                        visLeft, cellBottom, visLeft + cellW, cellBottom);
                    PdfExporter.StrokeEdge(ctx.Page, pl.Cell.BorderLeft ?? Fallback(rtl ? atLogicalRight : atLogicalLeft),
                        visLeft, cellBottom, visLeft, rowTop);
                    PdfExporter.StrokeEdge(ctx.Page, pl.Cell.BorderRight ?? Fallback(rtl ? atLogicalLeft : atLogicalRight),
                        visLeft + cellW, cellBottom, visLeft + cellW, rowTop);
                }

                // Content with vertical alignment.
                double innerW = Math.Max(1, cellW - pl.Cell.MarginLeft - pl.Cell.MarginRight - 2 * pl.Cell.InnerPaddingPt);
                double innerX = visLeft + pl.Cell.MarginLeft + pl.Cell.InnerPaddingPt;
                double contentH = MeasureCell(ctx, pl.Cell, innerW);
                double slack = Math.Max(0, mergedH - pl.Cell.MarginTop - pl.Cell.MarginBottom - 2 * pl.Cell.InnerPaddingPt - contentH);
                double vOff = pl.Cell.VAlign switch
                {
                    CellVerticalAlign.Center => slack / 2,
                    CellVerticalAlign.Bottom => slack,
                    CellVerticalAlign.Baseline => shifts?[index] ?? 0,   // onto the row's shared baseline
                    _ => 0,
                };
                double cy = rowTop - pl.Cell.MarginTop - pl.Cell.InnerPaddingPt - vOff;
                using (ctx.EnterParent(cellEl))
                {
                    bool drewAnything = false;
                    PdfExporter.LayOutCellBlocks(ctx, pl.Cell.Content(), innerX, cy, innerW, measureOnly: false,
                        drawBlock: (block, bx, by, bw) =>
                        {
                            double next = DrawCellBlock(ctx, block, bx, by, bw, out bool drew);
                            drewAnything |= drew;
                            return next;
                        },
                        measureBlock: (_, _, _, _) => 0,
                        // The content's bottom: a stretched box (BoxStart.StretchesToCell) reaches it.
                        stretchTo: cellBottom + pl.Cell.MarginBottom + pl.Cell.InnerPaddingPt);
                    // An empty cell must still reach content, or it is a hollow element — but it
                    // must also still EXIST, or the table goes ragged. An empty marked sequence
                    // satisfies both; a marked sequence enclosing no ink is legal.
                    if (!drewAnything && cellEl != null)
                        using (ctx.Mark(cellEl)) { }
                }
            }
        }

        /// <summary>
        /// The cell's <c>/A</c> attribute dictionary: its header scope and any column span. A
        /// <c>/TH</c> with neither a scope nor a headers association is reported by PDF/UA
        /// checkers, which is why the scope is inferred rather than left unset.
        /// </summary>
        private static PdfDictionary? BuildCellAttributes(Placed pl)
        {
            string? scope = pl.Cell.HeaderScope switch
            {
                TableHeaderScope.Row => "Row",
                TableHeaderScope.Column => "Column",
                TableHeaderScope.Both => "Both",
                _ => null,
            };
            if (scope == null && pl.Span <= 1) return null;

            var dict = new PdfDictionary();
            dict.Set(PdfName.O, new PdfName(PdfStructureTypes.Table));
            if (scope != null) dict.Set(PdfName.Scope, new PdfName(scope));
            if (pl.Span > 1) dict.Set(PdfName.ColSpan, new PdfNumber(pl.Span));
            return dict;
        }

        /// <summary>How far a cell's background runs on under its neighbour's, at most (1pt: a device pixel at 72dpi).</summary>
        private const double SeamOverlapPt = 1;

        /// <summary>
        /// How far <paramref name="pl"/>'s background reaches past its trailing edge, under the next
        /// cell of the row — 0 unless that cell, drawn after it, abuts it and paints a background of
        /// its own. Two fills meeting mid-pixel are each anti-aliased against the white page, and
        /// every viewer shows a light seam between them where a browser shows one band (an
        /// invoice's header row, found against Chrome); the later fill covers the overlap.
        /// </summary>
        /// <remarks>
        /// A merged cell, either side, reaches nothing: the rows below it are drawn later, and the
        /// overlap would lie under whichever cells those are. A spaced table's cells do not abut.
        /// </remarks>
        private static double SeamOverlap(List<Placed> row, Placed pl, double[] colW, double spacing)
        {
            if (spacing > 0 || pl.Cell.VMerge != VerticalMerge.None) return 0;
            var next = FindAtCol(row, pl.StartCol + pl.Span);
            // PlaceRow lists a row in column order: the next column's cell is drawn after this one.
            if (next == null || next.Cell.VMerge != VerticalMerge.None || string.IsNullOrEmpty(next.Cell.BackgroundColor))
                return 0;
            return Math.Min(SeamOverlapPt, SpanWidth(colW, next.StartCol, next.Span, spacing));
        }

        private static Placed? FindAtCol(List<Placed> row, int startCol)
        {
            foreach (var pl in row) if (pl.StartCol == startCol) return pl;
            return null;
        }
    }
}
