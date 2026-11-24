// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Util;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    // display: grid — mapped onto the document model's table, the same way
    // display: flex is, and for the same reason: a table is the only construct in the model
    // that places blocks side by side, and the exporter already places, paginates, RTL-mirrors and
    // tags one.
    //
    // This row was declined, and the decline's reasoning is what changed. GAPS said
    // grid "genuinely does still need item placement" — true of the whole of CSS grid, and not an
    // answer to what its common form needs. A grid with a column template whose items sit
    // in source order is a table: take the track count from grid-template-columns and deal
    // the items row-major. That was the third time this table recorded a decline whose reasoning
    // argued from what the model lacks rather than from where the feature could go.
    //
    // Column spanning is applied, and so, now, is explicit placement: an item
    // naming its column line, its row line, or both (grid-column: 2 / 4,
    // grid-row: 2, grid-area: 2 / 1 / 3 / 3) is placed there, and the items that
    // name nothing fill the cells it leaves — CSS's own placement algorithm (§8.5) run over an
    // occupancy grid, in its sparse row flow. A row span becomes a vertical merge, which the table
    // has carried for DOCX w:vMerge all along. "Honouring one item's position means
    // re-flowing every item after it" was the reason placement stayed reported; it does, and that
    // re-flow is a dozen lines.
    //
    // grid-template-areas and gap are applied: an item's
    // grid-area: name, and the name-start / name-end lines every area implies,
    // resolve against the template; a gap is the facing cells' margins over their padding, and a
    // row gap the top margin of every cell below the first row.
    // What it deliberately does not do, each reported through
    // HtmlToPdfOptions.OnDiagnostic rather than silently ignored: other named lines,
    // a negative line on an axis whose explicit grid is not known (a row template this cannot
    // read), a row track that is a function other than minmax(), fit-content() and
    // min()/max()/clamp() of lengths (a percentage inside one is of a
    // height the flow does not know), and grid-auto-flow: column under such a template. A line before the explicit grid adds
    // implicit tracks there, columns and rows, as a line beyond the template does, the rows sized
    // by grid-auto-rows.
    // grid-template-rows' fixed tracks are row heights, each a least height — a row grows
    // to its content where CSS lets the content overflow — and its tracks are explicit rows. dense packing is applied (§8.5: the cursor back at
    // the start for every item placed automatically). A negative index within the explicit grid —
    // grid-column: 1 / -1, the full-width span — counts back from its end edge (§8.3).
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>Tracks narrower than this are treated as auto rather than fixed.</summary>
        private const double MinGridTrackPt = 1.0;

        /// <summary>
        /// A grid may not declare more columns than this. The count comes from a
        /// <c>repeat()</c> whose multiplier is author-supplied, so it sizes an allocation:
        /// <c>repeat(1000000, 1fr)</c> is three words of CSS.
        /// </summary>
        private const int MaxGridColumns = 64;

        /// <summary>
        /// The furthest row line an explicit <c>grid-row</c> may name. A row line is author-supplied
        /// and sizes the occupancy grid and the table, so <c>grid-row: 1000000</c> is bounded here:
        /// the item is placed automatically instead, and says so.
        /// </summary>
        private const int MaxGridRows = 1024;

        /// <summary>True when <paramref name="el"/> establishes a grid container.</summary>
        private bool IsGridContainer(IElement el) => DisplayIs(el, "grid");

        /// <summary>
        /// Lays a grid container out. Returns <c>false</c> when the caller should fall through to
        /// ordinary block handling — which is the right answer whenever the grid has no usable
        /// column template, since one column of blocks IS ordinary flow and a one-column table
        /// would only add a tagging layer that says nothing.
        /// </summary>
        private bool MapGridContainer(
            IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel, int depth,
            Style inherited, M.TextAlignment? align)
        {
            // An absolutely positioned child takes no part in the grid layout (CSS Grid §9): it is
            // placed against the container — its containing block when the container is positioned —
            // after the tracks, inside the container's box — not as an item in a cell.
            List<IElement> children = el.Children.Where(c => !IsHidden(c)).ToList();
            List<IElement> items = children.Where(c => !IsPositioned(c)).ToList();
            List<IElement> positioned = children.Where(IsPositioned).ToList();
            if (items.Count == 0) return false;

            Style st = DeriveStyle(inherited, el, null, false);

            // The container's own box — its margins, border, padding and background —
            // round the tracks, as a flex container's: a bordered grid card drew no border and ran its
            // tracks over its padding. A positioned container with nothing to draw keeps a bare box,
            // the containing block of its positioned children.
            double room = SectionContentWidth(sec);
            M.BoxStart? box = ResolveBlockBox(el, st.Size, st.Direction, RelativeToGroup(st.Opacity));
            bool anchorsPositioned = IsPositioningAncestor(el);
            if (box == null && anchorsPositioned) box = new M.BoxStart();
            double contentWidth = box == null ? room : Math.Max(1, room - BlockBoxInset(box, room));

            // repeat(auto-fill | auto-fit, …) (§7.2.3.2) is counted against the container's
            // width and the column gap, and auto-fit's empty tracks collapse against the item count.
            double columnGap = Math.Max(0, ReadGridGap(el, "column-gap", "grid-column-gap", index: 1, st.Size));
            List<double> tracks = ParseTrackList(StyleValue(el, "grid-template-columns"), contentWidth, out GridLineNames colNames,
                                                 (pattern, fit) => AutoRepeatCount(pattern, contentWidth, columnGap, items.Count, fit));
            if (tracks.Count < 2) return false;

            // A grid container draws no rules of its own; borders on the items stay theirs.
            var table = NewBandTable();
            ApplyBandDirection(table, st, sec);

            // grid-template-areas (§7.3): the named areas an item's grid-area, or a `name-start` /
            // `name-end` line, resolves against.
            var areas = ParseTemplateAreas(StyleValue(el, "grid-template-areas"), tracks.Count);

            // grid-template-rows (§7.2): each row track's least height, or null where content sizes it.
            // The explicit grid's rows (§7.1), which a negative row line counts back from, are the
            // more of its tracks and grid-template-areas' rows; a template this cannot read leaves
            // the count unknown.
            string? templateRows = StyleValue(el, "grid-template-rows");
            List<double?>? rowTracks = ParseRowTracks(templateRows, st.Size, out GridLineNames rowNames);
            bool rowsUnread = rowTracks == null && templateRows != null && !Eq(templateRows.Trim(), "none");
            int? explicitRows = rowsUnread ? null : Math.Max(areas?.Rows ?? 0, rowTracks?.Count ?? 0);

            // grid-auto-flow: column (§8.5): the cursor runs down each column of the
            // explicit grid's rows before the next — one implicit row when the template has none
            // (§7.5) — and past the explicit columns into implicit ones. It needs the
            // row count, so a row template this cannot read leaves the items in row flow, reported.
            bool columnFlow = IsColumnFlow(el);
            bool columnFlowApplied = columnFlow && !rowsUnread;
            if (columnFlow && !columnFlowApplied)
                ReportApproximation($"grid-auto-flow: {StyleValue(el, "grid-auto-flow")!.Trim()} needs the explicit grid's rows " +
                            "to place by column; the row template was not read, so items were placed in row flow" +
                            (IsDenseFlow(el) ? ", densely." : "."));

            // grid-auto-columns (§7.6): the sizes of the implicit column tracks a placement past the
            // template, or column flow past its columns, adds — cycled; auto when none is stated.
            List<double> autoColumns = ParseTrackList(StyleValue(el, "grid-auto-columns"), contentWidth);

            // grid-auto-rows (§7.6): the least heights of the implicit rows — the ones a
            // row line before the grid adds at its start, sized from the list's end backwards, and the
            // ones placement adds after the explicit rows, sized from its start; content-sized where
            // it is auto or none is stated. A row template this cannot read leaves every row explicit.
            List<double?> autoRows = rowsUnread ? new List<double?>()
                : ParseRowTracks(StyleValue(el, "grid-auto-rows"), st.Size, out _, "grid-auto-rows") ?? new List<double?>();

            GridPlacement[] placed = PlaceItems(items, tracks, autoColumns, explicitRows, areas, IsDenseFlow(el), out int rowsBefore, columnFlowApplied, colNames, rowNames);
            if (placed.Length == 0) return false;   // the node budget ran out before the first item

            // A row's least height from its track (§7.1, §7.6): an implicit row before the grid takes
            // grid-auto-rows from the end backwards, an explicit row its template track (the template
            // indexes past the rows before the grid), an implicit row after the grid grid-auto-rows in turn.
            double? RowTrackHeight(int r)
            {
                if (r < rowsBefore)
                    return autoRows.Count > 0 ? autoRows[autoRows.Count - 1 - (rowsBefore - 1 - r) % autoRows.Count] : null;
                int e = r - rowsBefore;
                if (e < (explicitRows ?? 0))
                    return rowTracks != null && e < rowTracks.Count ? rowTracks[e] : null;
                return autoRows.Count > 0 ? autoRows[(e - (explicitRows ?? 0)) % autoRows.Count] : null;
            }

            // gap (§10.1): the distance between two tracks' content, carried as the facing cells'
            // margins over the padding every cell already has, and a row gap as the top margin of
            // every cell below the first row.
            var (half, rowExtra) = GridGaps(el, st);
            bool rtl = table.RtlColumns;
            void Space(M.TableCell cell, int row, int col, int span)
            {
                double toPrev = col > 0 ? half : 0, toNext = col + span < tracks.Count ? half : 0;
                // The renderer draws MarginLeft at a cell's visual left edge, which in a mirrored band
                // faces the previous logical column.
                if (rtl) { cell.MarginLeft = toNext; cell.MarginRight = toPrev; }
                else { cell.MarginLeft = toPrev; cell.MarginRight = toNext; }
                if (row > 0) cell.MarginTop = rowExtra;
            }

            // Which item owns each cell of the occupancy grid, so the table can be read off it row
            // by row: an item's own cell at its origin, a vertical-merge continuation under it,
            // and an empty cell where nothing landed.
            int rowCount = placed.Max(p => p.Row + p.RowSpan);
            var owner = new int[rowCount, tracks.Count];
            for (int r = 0; r < rowCount; r++)
                for (int c = 0; c < tracks.Count; c++)
                    owner[r, c] = -1;
            for (int i = 0; i < placed.Length; i++)
                for (int r = placed[i].Row; r < placed[i].Row + placed[i].RowSpan; r++)
                    for (int c = placed[i].Col; c < placed[i].Col + placed[i].ColSpan; c++)
                        owner[r, c] = i;

            // Mapped in source order, whatever cell each landed in, so diagnostics fire in the order
            // the author wrote the items. The items carry only the opacity they add to the container's
            // box's, and a positioned container is their positioned siblings' containing
            // block while they are mapped.
            var cells = new M.TableCell[placed.Length];
            var after = new List<M.IDocElement>();   // the positioned children, after the tracks
            double outerGroup = _groupOpacity;
            if (box != null && box.Opacity < 1) _groupOpacity = st.Opacity;
            if (anchorsPositioned) _positionedAncestors.Push((el, box!));
            try
            {
                for (int i = 0; i < placed.Length; i++)
                {
                    var cell = new M.TableCell { InnerPaddingPt = 0 };   // the item's own padding, as CSS has it
                    MapBlock(items[i], sec, cell.Blocks, listLevel, depth + 1, DeriveStyle(st, items[i], null, false), align);
                    // An item that produced nothing still occupies its cell: dropping it would shift
                    // every following item one column left, which is the failure the flex mapper
                    // records the same guard for.
                    EnsureNotEmpty(cell);
                    cells[i] = cell;
                }
                MapPositionedChildren(positioned, sec, after, listLevel, depth, st, align);
            }
            finally
            {
                _groupOpacity = outerGroup;
                if (anchorsPositioned) _positionedAncestors.Pop();
            }

            for (int r = 0; r < rowCount; r++)
            {
                // Charge this row's cells to the shared table-cell budget before reading it off, so a
                // page of grid containers cannot each materialise 1024×64 cells unbilled. A row's own
                // cells never exceed its track count; stop (reporting once) when the budget is spent.
                if (!TakeTableCells(tracks.Count)) break;
                var row = new M.TableRow();
                for (int c = 0; c < tracks.Count;)
                {
                    int i = owner[r, c];
                    if (i < 0)
                    {
                        // An empty track keeps its width: a row with fewer cells than its siblings
                        // is a shape the exporter sizes against the wrong widths.
                        var empty = NewFillerCell();
                        Space(empty, r, c, 1);
                        row.Cells.Add(empty);
                        c++;
                        continue;
                    }

                    GridPlacement p = placed[i];
                    if (p.Row == r)
                    {
                        M.TableCell cell = cells[i];
                        if (p.ColSpan > 1) cell.GridSpan = p.ColSpan;
                        if (p.RowSpan > 1) cell.VMerge = M.VerticalMerge.Restart;
                        Space(cell, r, c, p.ColSpan);
                        row.Cells.Add(cell);
                    }
                    else
                    {
                        // The rows a row span reaches into: the same shape the HTML table mapper
                        // writes for rowspan, which the renderer draws as one merged cell.
                        var continued = new M.TableCell
                        {
                            VMerge = M.VerticalMerge.Continue,
                            GridSpan = p.ColSpan,
                            Blocks = { new M.Paragraph { Runs = { new M.TextRun { Text = "" } } } },
                        };
                        Space(continued, r, c, p.ColSpan);
                        row.Cells.Add(continued);
                    }
                    c += p.ColSpan;
                }
                // A fixed row track is the row's least height; a row below the first holds its gap
                // as its cells' top margin, so the height holds it too.
                if (RowTrackHeight(r) is double height)
                    row.HeightPt = height + (r > 0 ? rowExtra : 0);
                table.Rows.Add(row);
            }

            // Only when the author sized something. With no explicit widths the model reads the
            // tracks as equal, which is exactly what `repeat(n, 1fr)` means.
            if (tracks.Any(t => t > MinGridTrackPt))
                foreach (double t in tracks) table.ColumnWidths.Add(t);

            // A clear left pending by an empty clearing element is the box's.
            if (box != null) { TakePendingClear(box); sink.Add(box); }
            sink.Add(table);
            sink.AddRange(after);
            if (box != null) sink.Add(new M.BoxEnd());
            return true;
        }

        // ------------------------------------------------------------------ placement

        /// <summary>Where an item landed: zero-based row and column, and the tracks it covers.</summary>
        private readonly struct GridPlacement
        {
            public GridPlacement(int row, int col, int rowSpan, int colSpan)
            {
                Row = row; Col = col; RowSpan = rowSpan; ColSpan = colSpan;
            }

            public int Row { get; }
            public int Col { get; }
            public int RowSpan { get; }
            public int ColSpan { get; }
        }

        /// <summary>
        /// What an item asked for: one-based start lines (0 = automatic) and spans, per axis.
        /// </summary>
        private readonly struct GridRequest
        {
            public GridRequest(int col, int colSpan, int row, int rowSpan, bool colDefinite = false, bool rowDefinite = false)
            {
                Col = col; ColSpan = colSpan; Row = row; RowSpan = rowSpan;
                ColDefinite = colDefinite || col != 0;
                RowDefinite = rowDefinite || row != 0;
            }

            /// <summary>The one-based start line, 0 for automatic — or, before <see cref="PlaceItems"/> shifts the grid, a line at or before 0 that counts back past the explicit grid's start.</summary>
            public int Col { get; }
            public int ColSpan { get; }
            /// <summary>The one-based start row line, 0 for automatic — or, before the shift, a line at or before 0, as <see cref="Col"/> may be.</summary>
            public int Row { get; }
            public int RowSpan { get; }

            /// <summary>Whether <see cref="Col"/> is a line the item asked for; false for an automatic column.</summary>
            public bool ColDefinite { get; }

            /// <summary>Whether <see cref="Row"/> is a line the item asked for; false for an automatic row.</summary>
            public bool RowDefinite { get; }

            /// <summary>The request with every definite line moved over by the implicit tracks added before the grid on its axis.</summary>
            public GridRequest ShiftedBy(int columns, int rows)
                => new GridRequest(ColDefinite ? Col + columns : 0, ColSpan, RowDefinite ? Row + rows : 0, RowSpan, ColDefinite, RowDefinite);
        }

        /// <summary>
        /// CSS Grid §8.5: definite positions first, then the items definite on the flow's own axis
        /// (a row in row flow, a column in column flow), then everything else through the sparse
        /// cursor — along the rows, or down the explicit grid's columns when
        /// <paramref name="columnFlow"/> is set.
        /// </summary>
        /// <remarks>
        /// <para><b>An item cannot overlap another here.</b> CSS lets two definitely placed items
        /// share cells and paints one over the other; a table cannot, so the second is moved down
        /// to the first row where it fits and the move is reported.</para>
        /// <para><b>Rows are made as they are needed</b> and bounded by <see cref="MaxGridRows"/>
        /// through the parser, so the occupancy grid is never larger than the items plus the
        /// furthest line an author named.</para>
        /// <para><b>Implicit column tracks (§7.5)</b> are added to <paramref name="tracks"/>
        /// — sized from <paramref name="autoColumns"/>, cycled, auto when it is empty — where a
        /// definite column line or a span reaches past the explicit grid, and where column flow
        /// fills the explicit columns and goes on; bounded by <see cref="MaxGridColumns"/>, past
        /// which the items left over take row flow below the explicit rows, reported once.</para>
        /// <para><b>Implicit tracks before the grid (§8.3)</b>: a line counting back
        /// past the explicit grid's start on either axis grows that axis at its start by the furthest
        /// such line and moves every definite line over; <paramref name="rowsBefore"/> is how many rows
        /// were added, so the caller can size them (<c>grid-auto-rows</c>) and index the row template
        /// past them.</para>
        /// </remarks>
        private GridPlacement[] PlaceItems(List<IElement> items, List<double> tracks, List<double> autoColumns,
            int? explicitRows, GridAreas? areas, bool dense, out int rowsBefore,
            bool columnFlow = false, GridLineNames? colNames = null, GridLineNames? rowNames = null)
        {
            int explicitTracks = tracks.Count;
            int explicitRowCount = explicitRows ?? 0;
            rowsBefore = 0;
            var reported = new HashSet<string>(StringComparer.Ordinal);
            var requests = new List<GridRequest>(items.Count);
            foreach (IElement item in items)
            {
                if (!TakeNode()) break;
                requests.Add(ParsePlacement(item, explicitTracks, explicitRows, reported, areas, colNames, rowNames));
            }
            foreach (string message in reported) ReportApproximation(message);

            int n = requests.Count;
            var result = new GridPlacement[n];
            var settled = new bool[n];
            var occupied = new List<bool[]>();

            // An auto implicit track is as wide as the explicit tracks' average: the table reads a
            // zero width as "take what the stated columns leave", which beside fr shares (raw
            // hundredths of the width) is nearly the whole band. CSS sizes it by its content and
            // stretches it into free space; the average keeps it in step with its neighbours, which
            // the table shares the band out over as it does the explicit tracks.
            double meanExplicit = explicitTracks > 0 ? tracks.Sum() / explicitTracks : 0;

            // Implicit tracks before the explicit grid (§8.3): a negative line counting back
            // past the grid's start, or an end line before it, names a line at or before 0. The grid
            // grows at its start by the furthest such line, every definite line moves over by that
            // many, and the new tracks are sized by grid-auto-columns from its last value backwards
            // (§7.6: "the last implicit grid track before the explicit grid receives the last
            // specified size, and so on backwards"), the explicit average where that is auto or none.
            // The row axis the same way: the rows added before the grid are the caller's to
            // size, from grid-auto-rows' end backwards, and the row template indexes past them.
            int before = 0;
            foreach (GridRequest q in requests)
            {
                if (q.ColDefinite && q.Col < 1) before = Math.Max(before, 1 - q.Col);
                if (q.RowDefinite && q.Row < 1) rowsBefore = Math.Max(rowsBefore, 1 - q.Row);
            }
            if (before > 0)
            {
                for (int k = 1; k <= before; k++)
                {
                    int fromEnd = (k - 1) % Math.Max(1, autoColumns.Count);
                    double size = autoColumns.Count > 0 ? autoColumns[autoColumns.Count - 1 - fromEnd] : 0;
                    if (size <= 0) size = meanExplicit;
                    tracks.Insert(0, size);
                }
            }
            if (before > 0 || rowsBefore > 0)
                for (int i = 0; i < requests.Count; i++) requests[i] = requests[i].ShiftedBy(before, rowsBefore);
            int rowsBeforeHere = rowsBefore;   // a local function cannot read an out parameter

            // The implicit tracks the placements ask for up front: a definite line's end, or a span
            // wider than the grid (which an automatic placement takes from the first track).
            int needed = 0;
            foreach (GridRequest q in requests)
                needed = Math.Max(needed, q.Col > 0 ? q.Col - 1 + q.ColSpan : q.ColSpan);
            if (needed > tracks.Count) Grow(needed);

            // Adds implicit column tracks up to `to` columns, each sized by grid-auto-columns in
            // turn (§7.6), the explicit average where that is auto or none is stated. Its callers
            // keep `to` within MaxGridColumns: ParsePlacement bounds a line and its span, Reach the
            // cursor.
            void Grow(int to)
            {
                while (tracks.Count < to)
                {
                    int implicitIndex = tracks.Count - explicitTracks - before;
                    double size = autoColumns.Count > 0 ? autoColumns[implicitIndex % autoColumns.Count] : 0;
                    if (size <= 0) size = meanExplicit;
                    tracks.Add(size);
                }
                for (int r = 0; r < occupied.Count; r++)
                {
                    bool[] cells = occupied[r];
                    if (cells.Length >= tracks.Count) continue;
                    Array.Resize(ref cells, tracks.Count);
                    occupied[r] = cells;
                }
            }

            bool Free(int row, int col, int rowSpan, int colSpan)
            {
                if (col < 0 || col + colSpan > tracks.Count) return false;
                for (int r = row; r < row + rowSpan; r++)
                {
                    if (r >= occupied.Count) return true;   // rows past the end are empty
                    for (int c = col; c < col + colSpan; c++)
                        if (occupied[r][c]) return false;
                }
                return true;
            }

            void Place(int i, int row, int col, int rowSpan, int colSpan)
            {
                while (occupied.Count < row + rowSpan) occupied.Add(new bool[tracks.Count]);
                for (int r = row; r < row + rowSpan; r++)
                    for (int c = col; c < col + colSpan; c++)
                        occupied[r][c] = true;
                result[i] = new GridPlacement(row, col, rowSpan, colSpan);
                settled[i] = true;
            }

            // 1. Both lines definite.
            for (int i = 0; i < n; i++)
            {
                GridRequest q = requests[i];
                if (q.Row == 0 || q.Col == 0) continue;

                int col = q.Col - 1;
                int colSpan = Math.Min(q.ColSpan, tracks.Count - col);
                int row = q.Row - 1;
                if (!Free(row, col, q.RowSpan, colSpan))
                {
                    ReportApproximation("two grid items were placed on the same cells; a table cannot " +
                                        "overlap them, so the later one was moved down to the first row " +
                                        "where it fits.");
                    while (!Free(row, col, q.RowSpan, colSpan)) row++;
                }
                Place(i, row, col, q.RowSpan, colSpan);
            }

            if (columnFlow)
            {
                PlaceByColumn();
                return result;
            }

            // 2. Definite row, automatic column: the first free run of cells in that row, past any
            //    item this step already put there (which "first free" gives for free).
            for (int i = 0; i < n; i++)
            {
                GridRequest q = requests[i];
                if (q.Row == 0 || q.Col != 0) continue;

                int colSpan = Math.Min(q.ColSpan, tracks.Count);
                int row = q.Row - 1;
                while (true)
                {
                    int col = FirstFreeColumn(row, q.RowSpan, colSpan);
                    if (col >= 0) { Place(i, row, col, q.RowSpan, colSpan); break; }
                    row++;   // CSS would add implicit column tracks; the next row is what a table has
                }
            }

            // 3. The rest, through the cursor, in order. `dense` puts the cursor back at the grid's
            //    start for every item, so a later item fills a hole an earlier one left (§8.5).
            int cursorRow = 0, cursorCol = 0;
            for (int i = 0; i < n; i++)
            {
                if (settled[i]) continue;
                GridRequest q = requests[i];
                if (dense) { cursorRow = 0; cursorCol = 0; }

                if (q.Col != 0)
                {
                    // Definite column: the cursor jumps to it, moving down a row first when that
                    // column is behind it, then down until the item fits.
                    int col = q.Col - 1;
                    int colSpan = Math.Min(q.ColSpan, tracks.Count - col);
                    if (col < cursorCol) cursorRow++;
                    cursorCol = col;
                    while (!Free(cursorRow, col, q.RowSpan, colSpan)) cursorRow++;
                    Place(i, cursorRow, col, q.RowSpan, colSpan);
                    cursorCol = col + colSpan;
                }
                else
                {
                    int colSpan = Math.Min(q.ColSpan, tracks.Count);
                    while (true)
                    {
                        if (cursorCol + colSpan > tracks.Count) { cursorRow++; cursorCol = 0; }
                        if (Free(cursorRow, cursorCol, q.RowSpan, colSpan)) break;
                        cursorCol++;
                    }
                    Place(i, cursorRow, cursorCol, q.RowSpan, colSpan);
                    cursorCol += colSpan;
                }
            }

            return result;

            int FirstFreeColumn(int row, int rowSpan, int colSpan)
            {
                for (int col = 0; col + colSpan <= tracks.Count; col++)
                    if (Free(row, col, rowSpan, colSpan)) return col;
                return -1;
            }

            // §8.5 with the axes exchanged: items definite on a column take the first free rows of
            // that column (rows grow as needed, as CSS grows implicit rows there); the rest go to
            // the cursor, which runs down the rows of each column — the implicit rows before the
            // grid and the explicit ones; one implicit row when there are none — before the next,
            // and past the explicit columns into implicit ones (§7.5). An item definite on a row
            // moves the cursor to that row, to the next column when the row is behind it. Only past
            // MaxGridColumns can the cursor not go on: the items left take row flow below the rows,
            // reported once.
            void PlaceByColumn()
            {
                int rows = Math.Max(1, rowsBeforeHere + explicitRowCount);

                for (int i = 0; i < n; i++)
                {
                    GridRequest q = requests[i];
                    if (q.Col == 0 || q.Row != 0) continue;

                    int col = q.Col - 1;
                    int colSpan = Math.Min(q.ColSpan, tracks.Count - col);
                    int firstFreeRow = 0;
                    while (!Free(firstFreeRow, col, q.RowSpan, colSpan)) firstFreeRow++;
                    Place(i, firstFreeRow, col, q.RowSpan, colSpan);
                }

                int cRow = 0, cCol = 0;
                bool overflowed = false;
                for (int i = 0; i < n; i++)
                {
                    if (settled[i]) continue;
                    GridRequest q = requests[i];
                    if (dense) { cRow = 0; cCol = 0; }
                    int colSpan = Math.Min(q.ColSpan, MaxGridColumns);

                    if (q.Row != 0)
                    {
                        // Definite row: the cursor jumps to it, to the next column when that row is
                        // behind it, then along the columns until the item fits.
                        int row = q.Row - 1;
                        if (row < cRow) cCol++;
                        cRow = row;
                        while (cCol + colSpan <= MaxGridColumns && Reach(cCol + colSpan) && !Free(cRow, cCol, q.RowSpan, colSpan)) cCol++;
                        if (cCol + colSpan > MaxGridColumns) { PlaceBeyond(i, q); continue; }
                        Place(i, cRow, cCol, q.RowSpan, colSpan);
                        cRow += q.RowSpan;
                        continue;
                    }

                    while (true)
                    {
                        if (cRow + q.RowSpan > rows) { cCol++; cRow = 0; }
                        if (cCol + colSpan > MaxGridColumns || !Reach(cCol + colSpan)) break;
                        if (Free(cRow, cCol, q.RowSpan, colSpan)) break;
                        cRow++;
                    }
                    if (cCol + colSpan > MaxGridColumns) { PlaceBeyond(i, q); continue; }
                    Place(i, cRow, cCol, q.RowSpan, colSpan);
                    cRow += q.RowSpan;
                }

                // The grid grown to hold `columns` columns — implicit tracks past the explicit ones —
                // or false when that would pass MaxGridColumns.
                bool Reach(int columns)
                {
                    if (columns > MaxGridColumns) return false;
                    if (columns > tracks.Count) Grow(columns);
                    return true;
                }

                // Past the most columns this holds: the first free run of cells in row flow, from
                // the row below the grid's rows.
                void PlaceBeyond(int i, GridRequest q)
                {
                    if (!overflowed)
                    {
                        overflowed = true;
                        ReportApproximation($"grid-auto-flow: column filled {MaxGridColumns} columns, the most this engine lays out; " +
                                            "the remaining items were placed in rows below the grid.");
                    }
                    int colSpan = Math.Min(q.ColSpan, tracks.Count);
                    int row = rows;
                    while (true)
                    {
                        int col = FirstFreeColumn(row, q.RowSpan, colSpan);
                        if (col >= 0) { Place(i, row, col, q.RowSpan, colSpan); return; }
                        row++;
                    }
                }
            }
        }

        /// <summary>
        /// An item's <c>grid-area</c>, <c>grid-column[-start/-end]</c> and <c>grid-row[-start/-end]</c>,
        /// resolved to one-based lines and spans. Longhands win over shorthands, as the last
        /// declaration would in CSS — the declaration map keeps one value per property, so that is
        /// the order this can know.
        /// </summary>
        private GridRequest ParsePlacement(IElement item, int explicitTracks, int? explicitRows, HashSet<string> reported, GridAreas? areas,
            GridLineNames? colNames = null, GridLineNames? rowNames = null)
        {
            string? colStart = null, colEnd = null, rowStart = null, rowEnd = null;

            string? area = StyleValue(item, "grid-area");
            if (area != null)
            {
                string[] parts = area.Split('/').Select(p => p.Trim()).ToArray();
                if (parts.Length >= 1) rowStart = parts[0];
                if (parts.Length >= 2) colStart = parts[1];
                if (parts.Length >= 3) rowEnd = parts[2];
                if (parts.Length >= 4) colEnd = parts[3];

                // One token is a template area's name (§8.3): its four lines are the area's, from
                // grid-template-areas.
                // A name the template does not define still is.
                if (parts.Length == 1 && !Eq(parts[0], "auto") && !IsGridLineToken(parts[0]))
                {
                    if (areas != null && areas.TryGetValue(parts[0], out GridArea named))
                    {
                        rowStart = Line(named.RowStart); colStart = Line(named.ColStart);
                        rowEnd = Line(named.RowEnd); colEnd = Line(named.ColEnd);
                    }
                    else
                    {
                        reported.Add($"grid-area: '{parts[0]}' names no area of grid-template-areas, so the item " +
                                     "was placed automatically.");
                        rowStart = null;
                    }
                }
            }

            SplitLinePair(StyleValue(item, "grid-column"), ref colStart, ref colEnd);
            SplitLinePair(StyleValue(item, "grid-row"), ref rowStart, ref rowEnd);
            colStart = StyleValue(item, "grid-column-start") ?? colStart;
            colEnd = StyleValue(item, "grid-column-end") ?? colEnd;
            rowStart = StyleValue(item, "grid-row-start") ?? rowStart;
            rowEnd = StyleValue(item, "grid-row-end") ?? rowEnd;

            // The implicit named lines every area defines (§7.3.2): `name-start` and `name-end` on
            // each axis. Any other name is still one this mapping has not got.
            colStart = NamedLine(colStart, areas, colNames, column: true);
            colEnd = NamedLine(colEnd, areas, colNames, column: true);
            rowStart = NamedLine(rowStart, areas, rowNames, column: false);
            rowEnd = NamedLine(rowEnd, areas, rowNames, column: false);

            (int col, int colSpan, bool colDefinite) = ResolveAxis(colStart, colEnd, "grid-column", explicitTracks, reported);
            (int row, int rowSpan, bool rowDefinite) = ResolveAxis(rowStart, rowEnd, "grid-row", explicitRows, reported);

            // A line past the template is an implicit track (§7.5), which the placement adds —
            // up to MaxGridColumns, the allocation's bound, as a row line is bounded; so is a line
            // before the grid (§8.3), whose implicit tracks come before it, on either axis.
            if (col > MaxGridColumns || (colDefinite && col < 1 - MaxGridColumns))
            {
                reported.Add($"grid-column: a start line past {MaxGridColumns} is not applied; the item was placed " +
                             "automatically.");
                col = 0;
                colDefinite = false;
            }
            if (row > MaxGridRows || (rowDefinite && row < 1 - MaxGridRows))
            {
                reported.Add($"grid-row: a start line past {MaxGridRows} is not applied; the item was placed " +
                             "automatically.");
                row = 0;
                rowDefinite = false;
            }

            // The span too stays within the columns this lays out: from its start line, or from the
            // first track for an automatic placement (a line before the grid counts its tracks before).
            colSpan = Math.Min(colSpan, MaxGridColumns - (col > 0 ? col - 1 : 0));
            if (colDefinite && col < 1) colSpan = Math.Min(colSpan, MaxGridColumns - (1 - col));
            colSpan = Math.Max(1, colSpan);
            rowSpan = Math.Min(rowSpan, MaxGridRows - (row > 0 ? row - 1 : 0));
            if (rowDefinite && row < 1) rowSpan = Math.Min(rowSpan, MaxGridRows - (1 - row));
            rowSpan = Math.Max(1, rowSpan);
            return new GridRequest(col, colSpan, row, rowSpan, colDefinite, rowDefinite);
        }

        /// <summary>A one-based line number as the token <see cref="ParseLine"/> reads.</summary>
        private static string Line(int number) => number.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// <paramref name="token"/> as a line number when it is a line the templates name (§7.2.1,
        /// <c>name</c> or <c>name N</c>) or an area's implicit <c>name-start</c> or
        /// <c>name-end</c> line (§7.3.2) on this axis — a template's explicit name winning, as §7.3.2
        /// gives an area's implicit names only where no explicit line has them; otherwise as it came.
        /// </summary>
        private static string? NamedLine(string? token, GridAreas? areas, GridLineNames? names, bool column)
        {
            if (token == null) return token;
            string t = token.Trim();
            if (t.Length == 0 || char.IsDigit(t[0]) || t[0] == '-' || Eq(t, "auto") || t.StartsWith("span", StringComparison.OrdinalIgnoreCase)) return token;
            if (names != null && names.Resolve(t) is int named) return Line(named);
            if (areas == null) return token;
            if (t.EndsWith("-start", StringComparison.Ordinal) && areas.TryGetValue(t.Substring(0, t.Length - 6), out GridArea start))
                return Line(column ? start.ColStart : start.RowStart);
            if (t.EndsWith("-end", StringComparison.Ordinal) && areas.TryGetValue(t.Substring(0, t.Length - 4), out GridArea end))
                return Line(column ? end.ColEnd : end.RowEnd);
            return token;
        }

        /// <summary>A named area's one-based lines: its first row and column, and the lines it ends before.</summary>
        private readonly struct GridArea
        {
            public GridArea(int rowStart, int colStart, int rowEnd, int colEnd)
            {
                RowStart = rowStart; ColStart = colStart; RowEnd = rowEnd; ColEnd = colEnd;
            }

            public int RowStart { get; }
            public int ColStart { get; }
            public int RowEnd { get; }
            public int ColEnd { get; }
        }

        private sealed class GridAreas : Dictionary<string, GridArea>
        {
            public GridAreas(int rows) : base(StringComparer.Ordinal) { Rows = rows; }

            /// <summary>The template's row count — the explicit grid's rows, which a negative row line counts back from.</summary>
            public int Rows { get; }
        }

        /// <summary>
        /// <c>grid-template-areas</c> (§7.3): each string a row of names separated by white space, a
        /// run of dots a cell no area covers; every name's cells must form one rectangle. The areas
        /// as one-based lines — or <c>null</c>, reported, when a row has other than the template's
        /// track count (CSS would make implicit columns, which a table has not), a name is not
        /// rectangular, the rows outnumber <see cref="MaxGridRows"/>, or the value holds no strings.
        /// </summary>
        private GridAreas? ParseTemplateAreas(string? value, int tracks)
        {
            if (value == null || Eq(value.Trim(), "none")) return null;

            var rows = new List<string[]>();
            for (int i = 0; i < value.Length; i++)
            {
                char quote = value[i];
                if (quote != '"' && quote != '\'') continue;
                int close = value.IndexOf(quote, i + 1);
                if (close < 0) break;
                rows.Add(value.Substring(i + 1, close - i - 1).Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));
                i = close;
                if (rows.Count > MaxGridRows) break;
            }

            string? invalid = null;
            if (rows.Count == 0) invalid = "it holds no strings";
            else if (rows.Count > MaxGridRows) invalid = $"it has more than {MaxGridRows} rows";
            else if (rows.Exists(r => r.Length != tracks)) invalid = $"a row of it has other than the template's {tracks} columns, and a table has no implicit ones";

            var areas = new GridAreas(rows.Count);
            if (invalid == null)
            {
                for (int r = 0; r < rows.Count; r++)
                    for (int c = 0; c < tracks; c++)
                    {
                        string name = rows[r][c];
                        if (name.Trim('.').Length == 0) continue;   // a null cell token: one or more dots
                        // Row-major, so a name's first cell is its top-left corner and only its end
                        // lines grow; a cell that would move the start is one the rectangle check refuses.
                        if (!areas.TryGetValue(name, out GridArea a))
                            areas[name] = new GridArea(r + 1, c + 1, r + 2, c + 2);
                        else
                            areas[name] = new GridArea(a.RowStart, a.ColStart, Math.Max(a.RowEnd, r + 2), Math.Max(a.ColEnd, c + 2));
                    }

                // Rectangular: every cell of the name's bounding box carries the name (§7.3: "the
                // declaration is invalid" otherwise).
                foreach (var pair in areas)
                {
                    GridArea a = pair.Value;
                    for (int r = a.RowStart; r < a.RowEnd && invalid == null; r++)
                        for (int c = a.ColStart; c < a.ColEnd; c++)
                            if (rows[r - 1][c - 1] != pair.Key) { invalid = $"the area '{pair.Key}' is not a rectangle"; break; }
                }
            }

            if (invalid != null)
            {
                ReportApproximation($"grid-template-areas is not applied — {invalid}; the items were placed by their own " +
                                    "grid-column / grid-row, else in row flow.");
                return null;
            }
            return areas;
        }

        /// <summary>
        /// The grid's <c>gap</c> as the table carries it: half the column gap on each side that faces
        /// another track, and the row gap on the top of every cell below the first row. The cells add
        /// no padding of their own, so the gap is exactly the gap asked, not less the renderer's 3pt
        /// a side.
        /// </summary>
        private (double Half, double RowExtra) GridGaps(IElement el, Style st)
        {
            double column = ReadGridGap(el, "column-gap", "grid-column-gap", index: 1, st.Size);
            double row = ReadGridGap(el, "row-gap", "grid-row-gap", index: 0, st.Size);
            return (Math.Max(0, column / 2), Math.Max(0, row));
        }

        /// <summary>
        /// One axis of a grid's gap (§10.1), in points: the longhand, else its legacy <c>grid-</c>
        /// alias, else its half of <c>gap</c> (<c>&lt;row-gap&gt; &lt;column-gap&gt;?</c>, one value for
        /// both) or of <c>grid-gap</c>; <c>normal</c>, nothing, or a bad length is 0 — a grid's
        /// initial gap, unlike a multi-column container's.
        /// </summary>
        private double ReadGridGap(IElement el, string longhand, string legacy, int index, double em)
        {
            string? value = StyleValue(el, longhand) ?? StyleValue(el, legacy);
            if (value == null)
            {
                string? shorthand = StyleValue(el, "gap") ?? StyleValue(el, "grid-gap");
                if (shorthand != null)
                {
                    string[] parts = shorthand.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) value = parts[index];
                    else if (parts.Length == 1) value = parts[0];
                }
            }
            if (value == null || Eq(value.Trim(), "normal")) return 0;
            double gap = ParseLength(value, double.NaN, em);
            return double.IsNaN(gap) || gap < 0 ? 0 : gap;
        }

        /// <summary><c>a / b</c> into its halves; a lone value is the start line.</summary>
        private static void SplitLinePair(string? shorthand, ref string? start, ref string? end)
        {
            if (shorthand == null) return;
            int slash = shorthand.IndexOf('/');
            if (slash < 0) { start = shorthand.Trim(); end = null; return; }
            start = shorthand.Substring(0, slash).Trim();
            end = shorthand.Substring(slash + 1).Trim();
        }

        private enum LineKind { Auto, Number, FromEnd, Span, Unresolvable }

        /// <summary>
        /// One <c>&lt;grid-line&gt;</c>: automatic, a positive line number, a negative one (its
        /// magnitude, counted back from the explicit grid's end edge), a span, or something this
        /// cannot resolve.
        /// </summary>
        private static (LineKind Kind, int Value) ParseLine(string? token)
        {
            if (token == null) return (LineKind.Auto, 0);
            string t = token.Trim();
            if (t.Length == 0 || Eq(t, "auto")) return (LineKind.Auto, 0);

            if (t.StartsWith("span", StringComparison.OrdinalIgnoreCase))
            {
                string rest = t.Substring(4).Trim();
                if (rest.Length == 0) return (LineKind.Span, 1);   // `span` alone is `span 1`
                return int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0
                    ? (LineKind.Span, n)
                    : (LineKind.Unresolvable, 0);                   // `span header-end`: a named line
            }

            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
                return line > 0 ? (LineKind.Number, line)
                     : line < 0 ? (LineKind.FromEnd, -line)                 // §8.3: counts in reverse from the end edge
                     : (LineKind.Unresolvable, 0);                          // 0 is invalid

            return (LineKind.Unresolvable, 0);                      // a named line
        }

        /// <summary>
        /// A negative line as the line it names, counting back from the explicit grid's end edge
        /// (§8.3: -1 is the line after the last explicit track) — at or before 0 for a line before the
        /// explicit grid, where the placement adds implicit tracks; left <see cref="LineKind.FromEnd"/> when the explicit grid is not known.
        /// </summary>
        private static (LineKind Kind, int Value) FromStart((LineKind Kind, int Value) line, int? explicitTracks)
        {
            if (line.Kind != LineKind.FromEnd || explicitTracks == null) return line;
            return (LineKind.Number, explicitTracks.Value + 2 - line.Value);
        }

        /// <summary>True when <paramref name="token"/> reads as a line rather than an area name.</summary>
        private static bool IsGridLineToken(string token) => ParseLine(token).Kind != LineKind.Unresolvable;

        /// <summary>
        /// One axis of a placement — CSS Grid §8.3: a backwards pair is swapped, a span rides on
        /// either half, an end line alone places the item so it ends there.
        /// </summary>
        /// <remarks>A negative line counts back from <paramref name="explicitTracks"/>, the explicit
        /// grid's track count on this axis — <c>null</c> when it is not known, which is reported. A line
        /// at or before 0 — before the explicit grid — is returned as such, definite, for the placement
        /// to add implicit tracks before the grid, columns and rows alike.</remarks>
        /// <returns>The one-based start line (0 = automatic, unless <c>Definite</c>) and the span, never below one.</returns>
        private static (int Start, int Span, bool Definite) ResolveAxis(
            string? startToken, string? endToken, string property, int? explicitTracks, HashSet<string> reported)
        {
            var start = FromStart(ParseLine(startToken), explicitTracks);
            var end = FromStart(ParseLine(endToken), explicitTracks);

            if (start.Kind == LineKind.FromEnd || end.Kind == LineKind.FromEnd)
            {
                reported.Add($"{property}: a negative line is not applied; it counts back from the end of an explicit " +
                             "grid this axis does not define here, so the item was placed automatically.");
                int keptSpan = start.Kind == LineKind.Span ? start.Value : end.Kind == LineKind.Span ? end.Value : 1;
                return (0, keptSpan, false);
            }
            if (start.Kind == LineKind.Unresolvable || end.Kind == LineKind.Unresolvable)
            {
                reported.Add($"{property}: a named line is not applied; this mapping has no named lines but an " +
                             "area's own, so the item was placed automatically.");
                int keptSpan = start.Kind == LineKind.Span ? start.Value : end.Kind == LineKind.Span ? end.Value : 1;
                return (0, keptSpan, false);
            }

            switch (start.Kind, end.Kind)
            {
                case (LineKind.Number, LineKind.Number):
                {
                    int a = start.Value, b = end.Value;
                    if (b < a) (a, b) = (b, a);          // "swap the two lines" — §8.3
                    return (a, Math.Max(1, b - a), true);      // equal lines are an error CSS reads as span 1
                }
                case (LineKind.Number, LineKind.Span): return (start.Value, end.Value, true);
                case (LineKind.Number, LineKind.Auto): return (start.Value, 1, true);
                case (LineKind.Span, LineKind.Number): return (end.Value - start.Value, start.Value, true);   // ends at the line, spanning back — before the grid when that is at or before 0
                case (LineKind.Span, LineKind.Span): return (0, end.Value, false);   // a start span beside an end span is ignored — §8.3
                case (LineKind.Span, LineKind.Auto): return (0, start.Value, false);
                case (LineKind.Auto, LineKind.Number): return (end.Value - 1, 1, true);
                case (LineKind.Auto, LineKind.Span): return (0, end.Value, false);
                default: return (0, 1, false);
            }
        }

        // ------------------------------------------------------------------ tracks

        /// <summary>
        /// A <c>grid-template-columns</c> track list, resolved to point widths. A track this
        /// mapping cannot size — <c>auto</c>, <c>min-content</c>, <c>minmax()</c> — comes back as
        /// <c>0</c>, which the caller reads as "let the table divide it".
        /// </summary>
        /// <remarks>
        /// <c>fr</c> is resolved as a share of the container rather than of the free space, which
        /// is exact when every track is an <c>fr</c> (the overwhelmingly common case,
        /// <c>repeat(3, 1fr)</c>) and an approximation when fixed and flexible tracks are mixed.
        /// The mixed case is not reported: the table sizes columns from the widths it is given, so
        /// the fixed tracks still come out at their stated size and only the flexible ones absorb
        /// the difference — which is what <c>fr</c> asks for.
        /// </remarks>
        private List<double> ParseTrackList(string? value, double contentWidth)
            => ParseTrackList(value, contentWidth, out _);

        /// <summary>
        /// <see cref="ParseTrackList(string?, double)"/>, also collecting the template's line names
        /// (§7.2.1): a <c>[name …]</c> group names the line before the track that follows
        /// it — or the line after the last track — and a name may name several lines, in order.
        /// A function track (<see cref="FunctionTrackWidth"/>) is sized from its arguments.
        /// </summary>
        private List<double> ParseTrackList(string? value, double contentWidth, out GridLineNames names,
                                            Func<string, bool, int>? autoRepeat = null)
        {
            var tracks = new List<double>();
            names = new GridLineNames();
            if (string.IsNullOrWhiteSpace(value)) return tracks;

            foreach (string token in ExpandRepeats(value!, autoRepeat))
            {
                if (token.Length > 0 && token[0] == '[')
                {
                    names.Add(token, tracks.Count + 1);
                    continue;
                }

                if (tracks.Count >= MaxGridColumns)
                {
                    // Truncation is reported rather than applied quietly. The cap protects the
                    // allocation, but a grid cut from its declared width to 64 columns lays out
                    // wrongly — 64 tracks across a page are a few points each, narrower than a
                    // word — so the caller has to be able to tell that from a grid that fitted.
                    ReportApproximation($"grid-template-columns declares more than {MaxGridColumns} tracks; " +
                               "the list was truncated, so items past that column were placed on " +
                               "later rows.");
                    break;
                }

                if (token.IndexOf('(') >= 0)
                {
                    tracks.Add(FunctionTrackWidth(token, contentWidth));
                    continue;
                }

                tracks.Add(TrackWidth(token, contentWidth));
            }

            return tracks;
        }

        /// <summary>One track token's width: an <c>fr</c> as a raw share the table normalises, else a length or percentage, 0 for auto and keywords.</summary>
        private double TrackWidth(string token, double contentWidth)
        {
            if (token.EndsWith("fr", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(token.Substring(0, token.Length - 2), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out double fr))
            {
                // A raw share; the caller hands the whole list to the table, which normalises.
                // "NaNfr" parses to NaN (Math.Max(0, NaN) is NaN); a non-finite share would reach the
                // table's ColumnWidths and the model. Treat it as auto (0).
                if (double.IsNaN(fr) || double.IsInfinity(fr)) return 0;
                return Math.Max(0, fr) * contentWidth / 100.0;
            }

            double w = ResolveWidth(token, contentWidth, 0);
            return w > MinGridTrackPt ? w : 0;
        }

        /// <summary>
        /// A function track's width (§7.2.3): <c>minmax(min, max)</c> is its <c>max</c> when that is a
        /// length, percentage or <c>fr</c> (the track grows to it where room allows, which a table
        /// sharing the width out does), else its <c>min</c>; <c>fit-content(len)</c> is its argument;
        /// <c>min()</c>, <c>max()</c> and <c>clamp()</c> over lengths and percentages of the width are
        /// their value (CSS Values 4 §10.3); any other function — or an <c>fr</c>, an
        /// <c>auto</c> or a nested function inside one of those — is auto (0), reported.
        /// </summary>
        private double FunctionTrackWidth(string token, double contentWidth)
        {
            var (name, args) = SplitFunction(token);
            if (Eq(name, "minmax") && args.Length == 2)
            {
                double max = TrackWidth(args[1], contentWidth);
                return max > 0 ? max : TrackWidth(args[0], contentWidth);
            }
            if (Eq(name, "fit-content") && args.Length == 1)
                return TrackWidth(args[0], contentWidth);
            if (MathTrack(name, args, a => DefiniteLength(a, contentWidth)) is double math)
                return math;

            ReportApproximation($"grid-template-columns: {token} is not a function this engine sizes; the track is auto.");
            return 0;
        }

        /// <summary>
        /// <c>min()</c>, <c>max()</c> and <c>clamp(min, value, max)</c> over the track sizes
        /// <paramref name="value"/> resolves (CSS Values 4 §10.3: <c>clamp()</c> is
        /// <c>max(min, min(value, max))</c>) — <c>null</c> for another function, no arguments, or an
        /// argument that does not resolve (an <c>fr</c>, <c>auto</c>, a percentage of a height the flow
        /// does not know, a nested function), which leaves the track to the caller's report.
        /// </summary>
        private double? MathTrack(string name, string[] args, Func<string, double?> value)
        {
            if (Eq(name, "clamp") && args.Length == 3)
            {
                double? lo = value(args[0]), v = value(args[1]), hi = value(args[2]);
                if (lo == null || v == null || hi == null) return null;
                return Math.Max(lo.Value, Math.Min(v.Value, hi.Value));
            }
            bool min = Eq(name, "min"), max = Eq(name, "max");
            if ((!min && !max) || args.Length == 0) return null;
            double result = min ? double.MaxValue : double.MinValue;
            foreach (string a in args)
            {
                if (value(a) is not double d) return null;
                result = min ? Math.Min(result, d) : Math.Max(result, d);
            }
            return result;
        }

        /// <summary>
        /// How many times <c>repeat(auto-fill | auto-fit, <paramref name="pattern"/>)</c> repeats
        /// (§7.2.3.2): the most repetitions whose tracks and the gaps between them fit the container's
        /// width, at least one — each track counted as its max when that is definite, else its min
        /// (so <c>minmax(200px, 1fr)</c> counts 200px). <c>auto-fit</c> collapses the repetitions no
        /// item reaches, so with fewer items than tracks the items share the width. A pattern with a
        /// track this cannot size (<c>1fr</c>, <c>auto</c>) is repeated once and reported.
        /// </summary>
        private int AutoRepeatCount(string pattern, double contentWidth, double gap, int itemCount, bool fit)
        {
            double size = 0;
            int perRepetition = 0;
            bool indefinite = false;
            foreach (string token in ExpandRepeats(pattern))
            {
                if (token.Length > 0 && token[0] == '[') continue;
                double? definite = DefiniteTrackSize(token, contentWidth);
                if (definite is double d) size += d; else indefinite = true;
                perRepetition++;
            }

            string keyword = fit ? "auto-fit" : "auto-fill";
            if (perRepetition == 0 || indefinite)
            {
                ReportApproximation($"grid-template-columns: repeat({keyword}, {pattern}) needs a definite track size to count " +
                                    "its repetitions; the pattern was repeated once.");
                return 1;
            }

            // n repetitions are n·k tracks with n·k − 1 gaps: n·size + (n·k − 1)·gap ≤ width.
            int count = Math.Max(1, SaturatingCast.ToInt(Math.Floor((contentWidth + gap) / (size + perRepetition * gap))));
            if (fit)
                count = Math.Min(count, Math.Max(1, (itemCount + perRepetition - 1) / perRepetition));
            return count;
        }

        /// <summary>
        /// A track token's size when it is definite — a length or percentage, <c>minmax()</c>'s max
        /// when that is definite else its min, <c>fit-content()</c>'s argument — or null for
        /// <c>fr</c>, <c>auto</c>, a keyword or a function this engine does not size.
        /// </summary>
        private double? DefiniteTrackSize(string token, double contentWidth)
        {
            if (token.IndexOf('(') >= 0)
            {
                var (name, args) = SplitFunction(token);
                if (Eq(name, "minmax") && args.Length == 2)
                    return DefiniteLength(args[1], contentWidth) ?? DefiniteLength(args[0], contentWidth);
                if (Eq(name, "fit-content") && args.Length == 1)
                    return DefiniteLength(args[0], contentWidth);
                // min(), max(), clamp() of lengths and percentages are definite.
                return MathTrack(name, args, a => DefiniteLength(a, contentWidth));
            }
            return DefiniteLength(token, contentWidth);
        }

        /// <summary>A length or percentage token in points, or null for <c>fr</c>, <c>auto</c> and keywords.</summary>
        private double? DefiniteLength(string token, double contentWidth)
        {
            if (token.EndsWith("fr", StringComparison.OrdinalIgnoreCase)) return null;
            double w = TrackWidth(token, contentWidth);
            return w > 0 ? w : (double?)null;
        }

        /// <summary>A function token as its name and its comma-separated arguments, trimmed; an unbalanced token has no arguments.</summary>
        private static (string Name, string[] Args) SplitFunction(string token)
        {
            int open = token.IndexOf('(');
            if (open < 0 || !token.EndsWith(")", StringComparison.Ordinal)) return (token, Array.Empty<string>());
            string name = token.Substring(0, open).Trim();
            string inner = token.Substring(open + 1, token.Length - open - 2);
            return (name, inner.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToArray());
        }

        /// <summary>
        /// A grid axis's line names (§7.2.1): each name to the one-based lines it names, in template
        /// order, so <c>name</c> is the first and <c>name 2</c> the second.
        /// </summary>
        private sealed class GridLineNames : Dictionary<string, List<int>>
        {
            public GridLineNames() : base(StringComparer.Ordinal) { }

            /// <summary>Records every name in a <c>[a b]</c> group as naming <paramref name="line"/>.</summary>
            public void Add(string group, int line)
            {
                foreach (string name in group.Trim('[', ']').Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!TryGetValue(name, out List<int>? lines)) this[name] = lines = new List<int>();
                    lines.Add(line);
                }
            }

            /// <summary>The line <paramref name="token"/> names — <c>name</c> or <c>name N</c>, the Nth so named — or null.</summary>
            public int? Resolve(string token)
            {
                string[] parts = token.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                int nth = 1;
                if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0) nth = n;
                else if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n > 0) { nth = n; parts = new[] { parts[1], parts[0] }; }
                else if (parts.Length != 1) return null;
                return TryGetValue(parts[0], out List<int>? lines) && lines.Count >= nth ? lines[nth - 1] : (int?)null;
            }
        }

        /// <summary>
        /// <c>grid-template-rows</c> (§7.2) as each row track's least height in points — a fixed
        /// length — or <c>null</c> for one its content sizes (<c>auto</c>, <c>fr</c>, a percentage of a
        /// height the flow does not know, a keyword); <c>null</c> for the whole when there is no
        /// template (<c>none</c>), and when a track is a function other than <c>minmax()</c> or
        /// <c>fit-content()</c>, which is reported and the template not applied. A fixed track whose
        /// content is taller grows here, where CSS lets the content overflow; that is reported once.
        /// </summary>
        private List<double?>? ParseRowTracks(string? value, double em)
            => ParseRowTracks(value, em, out _);

        /// <summary>
        /// <see cref="ParseRowTracks(string?, double)"/>, also collecting the template's line names
        /// (§7.2.1). A <c>minmax(min, max)</c> row is its <c>min</c> when that is a length —
        /// the least height a table row can hold — and <c>fit-content()</c> is sized by content; any
        /// other function is reported and leaves the template unread. <paramref name="property"/>
        /// names the list in its reports: the template, or <c>grid-auto-rows</c>, whose
        /// tracks size the implicit rows the same way.
        /// </summary>
        private List<double?>? ParseRowTracks(string? value, double em, out GridLineNames names, string property = "grid-template-rows")
        {
            names = new GridLineNames();
            if (value == null || Eq(value.Trim(), "none")) return null;
            if (value.IndexOf("auto-fill", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("auto-fit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Counting the repetitions needs a definite height to fit them into (§7.2.3.2), which
                // the flow does not have, so the repeat is reported, never skipped quietly.
                ReportApproximation($"{property}: {value.Trim()} is not applied; an auto-fill or auto-fit repeat " +
                                    "needs a definite height to count against, which the flow does not have, so row " +
                                    "heights come from their content.");
                return null;
            }
            var rows = new List<double?>();
            foreach (string token in ExpandRepeats(value))
            {
                if (token.Length > 0 && token[0] == '[')
                {
                    names.Add(token, rows.Count + 1);
                    continue;
                }
                if (rows.Count >= MaxGridRows) break;   // the bound placement already keeps rows to
                if (token.IndexOf('(') >= 0)
                {
                    var (name, args) = SplitFunction(token);
                    if (Eq(name, "minmax") && args.Length == 2)
                    {
                        double min = ParseLength(args[0], double.NaN, em);
                        rows.Add(min > 0 ? min : null);
                        continue;
                    }
                    if (Eq(name, "fit-content") && args.Length == 1)
                    {
                        rows.Add(null);
                        continue;
                    }
                    // min(), max(), clamp() over lengths: the row's least height. A
                    // percentage is of a height the flow does not know — the NaN basis — and leaves
                    // the template unread.
                    if (MathTrack(name, args, a => ParseLength(a, double.NaN, em) is double l && l > 0 ? l : (double?)null) is double math)
                    {
                        rows.Add(math);
                        continue;
                    }
                    ReportApproximation($"{property}: {value.Trim()} is not applied; {token} is not a " +
                                        "function this engine reads, so row heights come from their content.");
                    return null;
                }
                double height = ParseLength(token, double.NaN, em);
                rows.Add(height > 0 ? height : null);
            }
            if (rows.Exists(h => h != null))
                ReportApproximation($"{property}: a fixed row track is a least height here; a row whose " +
                                    "content is taller grows to hold it, where CSS lets the content overflow.");
            return rows;
        }

        /// <summary>
        /// Splits a track list into tokens, expanding <c>repeat(n, …)</c>. A <c>[name …]</c> line-name
        /// group is one token, brackets kept, and a function (<c>minmax(100px, 1fr)</c>) is one token to
        /// its closing parenthesis — split at their spaces, a template of two named tracks would read as
        /// five. A nested repeat (not valid CSS) is expanded by the recursion,
        /// its count clamped like any other so it cannot run away.
        /// </summary>
        private static IEnumerable<string> ExpandRepeats(string value, Func<string, bool, int>? autoRepeat = null)
        {
            int i = 0;
            while (i < value.Length)
            {
                while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
                if (i >= value.Length) yield break;

                if (string.Compare(value, i, "repeat(", 0, 7, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int close = ClosingParen(value, i + 6);
                    if (close < 0) yield break;

                    string inner = value.Substring(i + 7, close - i - 7);
                    i = close + 1;

                    int comma = inner.IndexOf(',');
                    if (comma < 0) continue;

                    string countText = inner.Substring(0, comma).Trim();
                    string pattern = inner.Substring(comma + 1).Trim();

                    // `auto-fill` / `auto-fit` are counted by the caller (§7.2.3.2), which
                    // knows the container's width and gap; a caller without them (the row template)
                    // skips the repeat, having said so.
                    if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                      out int count) || count <= 0)
                    {
                        bool fill = Eq(countText, "auto-fill"), fit = Eq(countText, "auto-fit");
                        if (autoRepeat == null || !(fill || fit)) continue;
                        count = autoRepeat(pattern, fit);
                        if (count <= 0) continue;
                    }

                    // Clamp the count to one past the largest consumer cap. This is a lazy iterator, so a
                    // consumer that stops at its own cap (MaxGridColumns or MaxGridRows) pulls no more —
                    // but a pattern of only [name] groups never reaches a track cap, so repeat(2^31, [a])
                    // was pulled 2^31 times, repeat(1, repeat(2^31, 1px))'s inner .ToList() built a 2^31
                    // list, and AutoRepeatCount walked the pattern 2^31 times. MaxGridRows + 1 is one past
                    // the largest consumer, so a consumer still SEES it stopped early — the reason the
                    // first cut, clamping to the cap itself, could not fire its truncation report.
                    if (count > MaxGridRows + 1) count = MaxGridRows + 1;

                    var patternTokens = ExpandRepeats(pattern).ToList();
                    for (int r = 0; r < count; r++)
                        foreach (string t in patternTokens)
                            yield return t;
                    continue;
                }

                int end = i;
                if (value[i] == '[')
                {
                    end = value.IndexOf(']', i);
                    end = end < 0 ? value.Length : end + 1;
                }
                else
                {
                    while (end < value.Length && !char.IsWhiteSpace(value[end]) && value[end] != '(') end++;
                    if (end < value.Length && value[end] == '(')
                    {
                        int close = ClosingParen(value, end);
                        end = close < 0 ? value.Length : close + 1;
                    }
                }
                yield return value.Substring(i, end - i);
                i = end;
            }
        }

        // ------------------------------------------------------------------ diagnostics

        /// <summary>True when the container's <c>grid-auto-flow</c> asks for column flow (§8.5).</summary>
        private bool IsColumnFlow(IElement el)
            => StyleValue(el, "grid-auto-flow") is { } flow && FlowKeywords(flow).Contains("column");

        /// <summary><c>grid-auto-flow</c>'s keywords, lower-cased: <c>[ row | column ] || dense</c>.</summary>
        private static string[] FlowKeywords(string flow)
            => flow.Trim().ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>True when the container's <c>grid-auto-flow</c> asks for <c>dense</c> packing.</summary>
        private bool IsDenseFlow(IElement el)
            => StyleValue(el, "grid-auto-flow") is { } flow && FlowKeywords(flow).Contains("dense");
    }
}
