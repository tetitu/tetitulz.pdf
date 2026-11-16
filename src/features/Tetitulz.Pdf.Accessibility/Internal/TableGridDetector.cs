// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Decides which of a page's lines form a table, and which column each of their text-showing
    /// operators sits in.
    /// </summary>
    /// <remarks>
    /// <para><b>The signal is a column grid, not a line.</b> A list can be recognised one line at a
    /// time — a marker, a shared left edge — but nothing about a single line says "table". What says
    /// it is <i>several consecutive rows whose ink starts at the same set of X positions</i>: the
    /// left edges of the columns. That is a two-dimensional fact, so this runs over the whole page
    /// before any line is classified, exactly as <see cref="ListLineDetector"/> does for its
    /// one-dimensional one.</para>
    ///
    /// <para><b>One show operator, one cell.</b> The grid is built from the X of every text-showing
    /// operator rather than from a line's left edge, because a row of three cells is often a single
    /// line — one <c>BT…ET</c> with three <c>Td … Tj</c> pairs — and a line-level reading of it
    /// sees one left edge and no table at all. The converse shape, each cell in its own
    /// <c>BT…ET</c>, arrives here as three separate lines sharing a baseline, and is joined back
    /// into one row below. Both are common; neither is detectable without the other.</para>
    ///
    /// <para><b>A cell spanning two show operators is not detected, and the failure is a refusal.</b>
    /// Two shows inside one cell sit at two different X, and the second one's X depends on the width
    /// of the text before it — so it lands somewhere different on every row and the column set stops
    /// matching. The run breaks and no table is claimed. That is the safe direction: a table missed
    /// is a page tagged as paragraphs, which is conformant; a table invented is an <c>/L</c>-shaped
    /// mistake this repository's own UA12 check would report.</para>
    ///
    /// <para><b>Rows must be exactly as wide as each other.</b> A row that matches only some of the
    /// columns is rejected rather than padded, because
    /// <c>PdfDeepAccessibilityChecker.CheckTableStructure</c> reports a table whose rows span
    /// differing column counts as a UA12 <b>Error</b> — remediation manufacturing the violation it
    /// exists to remove. A table with an empty cell therefore does not become a table here; it stays
    /// a set of paragraphs.</para>
    ///
    /// <para><b>Reading order is content order.</b> Cells are numbered by the column their X falls
    /// in, but they are emitted in the order the content stream draws them. A producer that draws
    /// column 2 before column 1 gets a row whose cells read in that order — which is what the file
    /// says, and what every other pass in this feature also honours. Re-ordering is
    /// <c>PdfReadingOrderAnalyzer</c>'s job, not this one's.</para>
    /// </remarks>
    internal static class TableGridDetector
    {
        /// <summary>
        /// How many consecutive aligned rows make a table.
        /// </summary>
        /// <remarks>
        /// <b>Two: a header and one row of data is a table.</b> One row is a line with tab stops,
        /// which is not evidence of anything, and PDF/UA is satisfied by tagging it as a paragraph.
        /// </remarks>
        internal const int MinimumRows = 2;

        /// <summary>
        /// How many columns a table must have.
        /// </summary>
        /// <remarks>
        /// <b>Two, and the false positive that admits is known.</b> A table of contents — a caption
        /// at the left margin and a page number at the right, repeated down the page — is a
        /// two-column grid by every measure available here, and will be tagged
        /// <c>Table</c>/<c>TR</c>/<c>TD</c> rather than <c>TOC</c>/<c>TOCI</c>. The structure is
        /// valid and navigable either way, the semantics are poorer, and requiring three columns
        /// would reject the ordinary two-column table that is the commonest table there is. The
        /// inference is opt-in for this reason, and leaving <c>AutoTagOptions.InferTables</c> off is
        /// the only override — there is no public classifier hook, because the run types one would
        /// speak in are <c>internal</c>.
        /// </remarks>
        internal const int MinimumColumns = 2;

        /// <summary>
        /// How far two rows' column edges may differ, as a fraction of the type size.
        /// </summary>
        /// <remarks>
        /// Relative rather than absolute for the reason <see cref="ListLineDetector"/> gives: the
        /// tolerance that is right for 8 pt text is not the one for a 24 pt heading, and a producer
        /// placing every row with the same operand still lands them a rounding step apart after the
        /// text matrix. Tighter than the list detector's, because a column edge is being compared
        /// against a column edge rather than against a hanging indent.
        /// </remarks>
        private const float ColumnToleranceEms = 0.25f;

        /// <summary>Type size assumed when a line's own is not finite.</summary>
        private const float FallbackSize = 12f;

        /// <summary>
        /// Ceiling on the columns one table may have, so a pathological line cannot make the
        /// alignment comparison expensive.
        /// </summary>
        /// <remarks>
        /// The comparison is O(columns) per row and the column set comes from a single line, so the
        /// cost is already linear — this bounds the <i>memory</i> a synthetic page can make the
        /// detector allocate, and it is far above any real table. A line with more shows than this
        /// is simply not a table row.
        /// </remarks>
        private const int MaxColumns = 512;

        /// <summary>What the detector concluded about one page.</summary>
        internal sealed class Grid
        {
            internal Grid(int[] tableOfLine, int[] rowOfLine, int[][] columnOfShow, int[] columnsOfLine, int tableCount)
            {
                TableOfLine   = tableOfLine;
                RowOfLine     = rowOfLine;
                ColumnOfShow  = columnOfShow;
                ColumnsOfLine = columnsOfLine;
                TableCount    = tableCount;
            }

            /// <summary>Per line: the table it belongs to, or <c>-1</c>.</summary>
            internal int[] TableOfLine { get; }

            /// <summary>Per line: its zero-based row within that table, or <c>-1</c>.</summary>
            internal int[] RowOfLine { get; }

            /// <summary>
            /// Per line: the zero-based column of each of its shows, or <c>null</c> when the line is
            /// in no table.
            /// </summary>
            internal int[][] ColumnOfShow { get; }

            /// <summary>Per line: how many columns its table has, or <c>0</c>.</summary>
            internal int[] ColumnsOfLine { get; }

            /// <summary>How many tables were found.</summary>
            internal int TableCount { get; }

            /// <summary>True when nothing was found, so the caller can skip every later pass.</summary>
            internal bool IsEmpty => TableCount == 0;
        }

        /// <summary>An empty verdict for a page with nothing to look at.</summary>
        private static Grid Nothing(int count)
            => new Grid(Filled(count, -1), Filled(count, -1), new int[count][], new int[count], 0);

        private static int[] Filled(int count, int value)
        {
            var values = new int[count];
            for (int i = 0; i < count; i++) values[i] = value;
            return values;
        }

        /// <summary>
        /// Finds the tables on one page.
        /// </summary>
        /// <param name="ys">Each line's baseline, in page order.</param>
        /// <param name="sizes">Each line's effective type size.</param>
        /// <param name="showXs">
        /// Each line's shows' X positions, in content order. A non-finite entry disqualifies its
        /// line, for the reason <see cref="RunCandidate.X"/> gives: every comparison against NaN is
        /// false in both directions, so a heuristic written one way round and the same heuristic
        /// written the other take opposite branches on it.
        /// </param>
        /// <param name="sameLineTolerance">
        /// How close two baselines must be to be one visual row — the same value the tagger uses to
        /// decide whether two shows are one line, so a row assembled from several text objects is
        /// grouped exactly as one drawn inside a single object would be.
        /// </param>
        internal static Grid Detect(
            IReadOnlyList<float> ys,
            IReadOnlyList<float> sizes,
            IReadOnlyList<float[]> showXs,
            float sameLineTolerance)
        {
            int count = ys?.Count ?? 0;
            if (count < MinimumRows || sizes == null || showXs == null) return Nothing(count);

            var tableOfLine   = Filled(count, -1);
            var rowOfLine     = Filled(count, -1);
            var columnOfShow  = new int[count][];
            var columnsOfLine = new int[count];

            // ---- 1. visual rows -------------------------------------------------
            // A row is a maximal run of CONSECUTIVE lines sharing a baseline. Consecutive, because
            // a producer that returns to an earlier baseline after drawing something else has
            // declared a different reading order, and joining those into one row would claim a
            // structure the file does not.
            List<Row> rows = BuildRows(ys, sizes, showXs, sameLineTolerance, count);
            if (rows.Count < MinimumRows) return Nothing(count);

            // ---- 2. runs of rows with a common column set -----------------------
            int tableCount = 0;
            for (int start = 0; start < rows.Count;)
            {
                Row first = rows[start];
                if (!first.IsCandidate) { start++; continue; }

                int end = start + 1;
                while (end < rows.Count && rows[end].IsCandidate && Aligns(first, rows[end]))
                    end++;

                if (end - start < MinimumRows) { start++; continue; }

                int tableId = tableCount++;
                for (int r = start; r < end; r++)
                {
                    Row row = rows[r];
                    for (int k = 0; k < row.Lines.Count; k++)
                    {
                        int line = row.Lines[k];
                        tableOfLine[line]   = tableId;
                        rowOfLine[line]     = r - start;
                        columnsOfLine[line] = first.Columns.Length;
                        columnOfShow[line]  = ColumnsFor(first.Columns, showXs[line], row.Size);
                    }
                }

                start = end;
            }

            return tableCount == 0
                ? Nothing(count)
                : new Grid(tableOfLine, rowOfLine, columnOfShow, columnsOfLine, tableCount);
        }

        /// <summary>One visual row: the lines that share a baseline, and their shows' X positions.</summary>
        private sealed class Row
        {
            internal readonly List<int> Lines = new List<int>();

            /// <summary>Every show X on the row, ascending. Empty when the row cannot be a table row.</summary>
            internal float[] Columns = EmptyColumns;

            internal float Y;

            /// <summary>
            /// The row's largest type size, or <see cref="float.NaN"/> when no line on it reported a
            /// finite one. <b>Not defaulted to <see cref="FallbackSize"/>.</b> A row of 9 pt text
            /// whose size started at 12 would keep 12, because the accumulator only ever raises it —
            /// and every column tolerance on the page would then be a third too wide, which merges
            /// two real columns into one and takes the table apart. The fallback is applied where
            /// the value is read, not where it is collected.
            /// </summary>
            internal float Size = float.NaN;

            internal bool IsCandidate => Columns.Length >= MinimumColumns;
        }

        private static readonly float[] EmptyColumns = new float[0];

        private static List<Row> BuildRows(
            IReadOnlyList<float> ys,
            IReadOnlyList<float> sizes,
            IReadOnlyList<float[]> showXs,
            float sameLineTolerance,
            int count)
        {
            var rows = new List<Row>();
            var xs = new List<float>();
            Row current = null;

            for (int i = 0; i < count; i++)
            {
                float y = ys[i];

                // A non-finite baseline is on no row — the rule SameLine already applies for the
                // same reason, and letting one through would fold every following line into
                // whichever row was open.
                if (!IsFinite(y)) { Flush(rows, current, xs); current = null; continue; }

                if (current == null || Math.Abs(current.Y - y) > sameLineTolerance)
                {
                    Flush(rows, current, xs);
                    current = new Row { Y = y };
                    xs.Clear();
                }

                current.Lines.Add(i);

                float size = i < sizes.Count ? sizes[i] : float.NaN;
                if (IsFinite(size) && (!IsFinite(current.Size) || size > current.Size)) current.Size = size;

                float[] lineXs = i < showXs.Count ? showXs[i] : null;
                if (lineXs == null) { xs.Add(float.NaN); continue; }   // no shows recorded: disqualify
                for (int k = 0; k < lineXs.Length; k++) xs.Add(lineXs[k]);
            }

            Flush(rows, current, xs);
            return rows;
        }

        /// <summary>Finalises a row's column set: sorted, and empty when anything about it was unusable.</summary>
        private static void Flush(List<Row> rows, Row row, List<float> xs)
        {
            if (row == null) return;
            rows.Add(row);

            if (xs.Count < MinimumColumns || xs.Count > MaxColumns) { xs.Clear(); return; }

            var columns = new float[xs.Count];
            for (int i = 0; i < xs.Count; i++)
            {
                if (!IsFinite(xs[i])) { xs.Clear(); return; }
                columns[i] = xs[i];
            }
            xs.Clear();

            Array.Sort(columns);

            // Two shows at the same X are two cells the grid cannot tell apart, so the row is not
            // usable as one. Rejecting rather than merging: merging would change the column count
            // for this row alone and make the table ragged.
            float tolerance = ColumnToleranceEms * EffectiveSize(row.Size);
            for (int i = 1; i < columns.Length; i++)
                if (columns[i] - columns[i - 1] <= tolerance) return;

            row.Columns = columns;
        }

        /// <summary>Whether <paramref name="other"/> has the reference row's columns.</summary>
        private static bool Aligns(Row reference, Row other)
        {
            if (reference.Columns.Length != other.Columns.Length) return false;

            // The larger of the two type sizes, so a table mixing a 12 pt header with 9 pt data is
            // compared at the tolerance the header earns rather than at the body's tighter one.
            float tolerance = ColumnToleranceEms
                              * Math.Max(EffectiveSize(reference.Size), EffectiveSize(other.Size));

            for (int i = 0; i < reference.Columns.Length; i++)
                if (Math.Abs(reference.Columns[i] - other.Columns[i]) > tolerance) return false;

            return true;
        }

        /// <summary>Maps each of a line's shows onto the table's column it starts.</summary>
        /// <remarks>
        /// The row as a whole has already been proved to align, so every show here matches exactly
        /// one column; the nearest one is that column. A show that somehow matches none — which the
        /// alignment check makes unreachable — takes column 0 rather than -1, because a negative
        /// column would travel into the structure tree as a cell nobody can place.
        /// </remarks>
        private static int[] ColumnsFor(float[] columns, float[] lineXs, float size)
        {
            if (lineXs == null || lineXs.Length == 0) return new int[0];

            var result = new int[lineXs.Length];
            for (int i = 0; i < lineXs.Length; i++)
            {
                float x = lineXs[i];
                int best = 0;
                float bestDistance = float.MaxValue;

                for (int c = 0; c < columns.Length; c++)
                {
                    float distance = Math.Abs(columns[c] - x);
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = c;
                }

                result[i] = best;
            }

            return result;
        }

        /// <summary>A row's type size, or <see cref="FallbackSize"/> when it reported none usable.</summary>
        private static float EffectiveSize(float size)
            => IsFinite(size) && size > 0f ? size : FallbackSize;

        private static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
