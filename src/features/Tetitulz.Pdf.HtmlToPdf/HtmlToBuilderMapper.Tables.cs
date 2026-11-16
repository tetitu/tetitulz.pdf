// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Text;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    internal sealed partial class HtmlToBuilderMapper
    {
        // ============================================================= tables

        private void MapTable(IElement tableEl, M.Section sec, List<M.IDocElement> sink, int depth,
            Style inherited, M.TextAlignment? inheritedAlign)
        {
            if (TooDeep(depth)) return;

            // A positioned table is the containing block of the positioned blocks in its cells (CSS
            // 2.1 §10.1): a bare box round it — the table's border box, its caption's included — which
            // the exporter measures their offsets from, the cells handing them to the box as they are
            // drawn — not the page.
            if (!IsPositioningAncestor(tableEl))
            {
                MapTableCore(tableEl, sec, sink, depth, inherited, inheritedAlign);
                return;
            }
            var box = new M.BoxStart();
            sink.Add(box);
            _positionedAncestors.Push((tableEl, box));
            try { MapTableCore(tableEl, sec, sink, depth, inherited, inheritedAlign); }
            finally { _positionedAncestors.Pop(); }
            sink.Add(new M.BoxEnd());
        }

        /// <summary>The table itself — its caption, rows and cells — into <paramref name="sink"/>.</summary>
        private void MapTableCore(IElement tableEl, M.Section sec, List<M.IDocElement> sink, int depth,
            Style inherited, M.TextAlignment? inheritedAlign)
        {
            // <caption> → a centred bold paragraph before the table.
            var caption = tableEl.Children.FirstOrDefault(c =>
                string.Equals(c.LocalName, HtmlTags.Caption, StringComparison.OrdinalIgnoreCase));
            if (caption != null)
            {
                var capStyle = DeriveStyle(inherited, caption, null, true);
                var cp = NewPara(caption, sec, M.TextAlignment.Center, capStyle);
                cp.Role = PdfStructureTypes.Caption;
                CollectInline(caption, cp, capStyle, depth + 1);
                if (cp.Runs.Count > 0) sink.Add(cp);
            }

            double contentWidth = SectionContentWidth(sec);

            // From the INHERITED direction, not the section's: a table inside
            // <div dir="rtl"> has to mirror its columns even when the document as a whole is LTR.
            var mt = new M.Table
            {
                RtlColumns = (inherited.Direction ?? sec.Direction) == M.TextDirection.RightToLeft,
                // CSS's default, `table-layout: auto`: columns sized by their content. Only
                // `fixed` asks for the widths to come from the first row and the table alone.
                AutoLayout = !Eq(StyleValue(tableEl, "table-layout")?.Trim(), "fixed"),
                // A browser's row is as tall as its content; the renderer's 16pt floor made a
                // compact table (small print, little padding) taller than the page drew it.
                MinRowHeightPt = 0,
            };
            // CSS `table { border: … }` styles the table's own box — a frame. The legacy
            // `<table border="1">` attribute genuinely means "rule every cell". Mapping both to the
            // per-cell default would make the CSS form paint a full grid.
            var tb = ParseBorder(StyleValue(tableEl, "border") ?? "");
            if (tb != null)
            {
                mt.BorderWidth = tb.Width;
                mt.BorderColor = tb.Color;
                mt.OuterBorderOnly = true;
            }
            else if (int.TryParse(tableEl.GetAttribute("border"), out int bw))
            {
                // HTML's rendering rules: `border="n"` rules every cell 1px — whatever n — in the
                // table's border-color, which the cells inherit, grey when none is set. The model's
                // black at n points ruled an e-mail's `border-color: #ddd` table in heavy black.
                mt.BorderWidth = bw > 0 ? PdfUnits.PixelsToPoints(1) : 0.0;
                mt.BorderColor = TryNormalizeColor(StyleValue(tableEl, "border-color")) ?? UaTableBorderColor;
            }
            // Neither: no default border. A browser draws none round a plain <table>'s cells; the
            // model's 0.5pt default ruled a grid round every one.
            else mt.BorderWidth = 0;

            // Its vertical margins: CSS's, and none by default — the UA stylesheet gives a table none.
            var (tableTop, _, tableBottom, _) = ResolveSides(tableEl, "margin", inherited.Size);
            mt.SpacingBefore = tableTop;
            mt.SpacingAfter = tableBottom;

            double tw = ResolveWidth(tableEl.GetAttribute("width") ?? StyleValue(tableEl, "width"), contentWidth, 0);
            if (tw > 0) mt.WidthPt = tw;
            // CSS's automatic table width: with none stated a table is as wide as its content, up
            // to its container — a button table, a key/value pair — not the page's width.
            else mt.ShrinkToContent = true;
            mt.Alignment = ResolveTableAlignment(tableEl);
            double tableWidthRef = tw > 0 ? tw : contentWidth;

            double cellPad = -1;
            if (int.TryParse(tableEl.GetAttribute("cellpadding"), out int cpv) && cpv >= 0)
                cellPad = PdfUnits.PixelsToPoints(cpv);

            var colWidths = ParseColGroups(tableEl, tableWidthRef);

            // Build the grid, materialising rowspan as Restart/Continue vertical merges so the
            // exporter lines columns up correctly (mirrors html2openxml's grid handling). Each cell's
            // first column is the markup's grid; LayGrid puts it on the columns the table keeps.
            var carry = new Dictionary<int, (int remaining, int span)>();
            // The rowspans still open, in column order: where a row goes on past its own cells.
            var open = new List<int>();
            var opened = new List<int>();
            var rowStarts = new List<List<int>>();
            int gridCols = 0;
            bool hasThead = TableRows(tableEl).Any(r => r.InHead);
            bool cellBudgetSpent = false;
            foreach (var (rowEl, inHead) in TableRows(tableEl))
            {
                if (!TakeNode() || cellBudgetSpent) break;
                if (IsHidden(rowEl)) continue; // display:none on a <tr>
                var cells = rowEl.Children
                    .Where(c => (NameIs(c, HtmlTags.Td) || NameIs(c, HtmlTags.Th)) && !IsHidden(c))
                    .ToList();

                // A <thead> row is a header row even when it holds <td> rather than <th>, which
                // is what makes RepeatHeaderRow reproduce it on each page.
                var mr = new M.TableRow { IsHeader = inHead };
                var starts = new List<int>();
                int col = 0, ci = 0, next = 0;
                bool closed = false;
                opened.Clear();
                while (true)
                {
                    _tableGridSteps++;
                    // A rowspan the row's cells reach past stays open for the row below.
                    while (next < open.Count && open[next] < col) next++;
                    if (next < open.Count && open[next] == col)
                    {
                        var cr = carry[col];
                        if (!TakeTableCell()) { cellBudgetSpent = true; break; }
                        var cont = new M.TableCell { VMerge = M.VerticalMerge.Continue, GridSpan = cr.span };
                        cont.Blocks.Add(new M.Paragraph { Runs = { new M.TextRun { Text = "" } } });
                        mr.Cells.Add(cont);
                        starts.Add(col);
                        if (cr.remaining > 1) carry[col] = (cr.remaining - 1, cr.span);
                        else { carry.Remove(col); closed = true; }
                        next++;
                        col += cr.span;
                        continue;
                    }
                    if (ci < cells.Count)
                    {
                        var cellEl = cells[ci++];
                        if (!TakeTableCell()) { cellBudgetSpent = true; break; }
                        bool isHeader = NameIs(cellEl, HtmlTags.Th);
                        int span = ParseSpan(cellEl.GetAttribute("colspan"), MaxColSpan);
                        int rs = ParseSpan(cellEl.GetAttribute("rowspan"), MaxRowSpan);
                        var mc = BuildCell(cellEl, sec, isHeader, span, tableWidthRef, cellPad, depth + 1, inherited);
                        mc.IsHeader = isHeader;
                        if (isHeader)
                        {
                            // A /TH with neither a scope nor an explicit headers association is
                            // reported by PDF/UA checkers. `scope` when the author gave one;
                            // otherwise infer from position — a header in the first row labels a
                            // column, one in the first column labels a row.
                            string? declared = cellEl.GetAttribute("scope");
                            mc.HeaderScope =
                                Eq(declared, "row") ? M.TableHeaderScope.Row
                                : Eq(declared, "col") ? M.TableHeaderScope.Column
                                : Eq(declared, "rowgroup") ? M.TableHeaderScope.Row
                                : Eq(declared, "colgroup") ? M.TableHeaderScope.Column
                                : mt.Rows.Count == 0 ? M.TableHeaderScope.Column
                                : col == 0 ? M.TableHeaderScope.Row
                                : M.TableHeaderScope.Column;
                        }
                        if (rs > 1)
                        {
                            mc.VMerge = M.VerticalMerge.Restart;
                            carry[col] = (rs - 1, span);
                            opened.Add(col);
                        }
                        mr.Cells.Add(mc);
                        starts.Add(col);
                        col += span;
                        continue;
                    }
                    // No cell left here: the row goes on at the next column a rowspan still covers,
                    // or ends. Walked a column at a time, scanning every rowspan at each, a row short
                    // of a far rowspan cost the distance times the rowspans.
                    if (next >= open.Count) break;
                    col = open[next];
                }
                if (closed || opened.Count > 0) open = MergeOpenRowspans(open, opened, carry);

                // A header row is a <thead> row, or, in a table written without one, a row of
                // nothing but <th>. A <th> beside a <td> labels its row — body content: as a header,
                // every labelled row above a page break would be re-drawn on the next page. Only the header rows a table STARTS with repeat (TableRenderer), so
                // an all-<th> section row further down is drawn once, where it stands.
                if (!mr.IsHeader && !hasThead && cells.Count > 0
                    && cells.All(c => NameIs(c, HtmlTags.Th)))
                    mr.IsHeader = true;

                if (mr.Cells.Count > 0)
                {
                    mt.Rows.Add(mr);
                    rowStarts.Add(starts);
                }
                if (col > gridCols) gridCols = col;
            }
            if (mt.Rows.Any(r => r.IsHeader)) mt.RepeatHeaderRow = true;

            var keptWidths = LayGrid(mt, rowStarts, colWidths, ref gridCols);
            bool collapse = Eq(StyleValue(tableEl, "border-collapse")?.Trim(), "collapse");
            if (!collapse) (mt.CellSpacingPt, mt.RowSpacingPt) = BorderSpacing(tableEl, inherited.Size);
            // With the cells spaced the frame is the table's own, drawn outside the spacing.
            if (mt.OuterBorderOnly && mt.CellSpacingPt <= 0 && mt.RowSpacingPt <= 0) FrameOuterEdges(mt);
            ReserveBorderRoom(mt, collapse);
            ApplyColumnWidths(mt, keptWidths, gridCols);
            sink.Add(mt);
        }

        /// <summary>
        /// The rowspans open after a row: those it met or passed that did not close, and those it opened,
        /// in column order — both lists are, so one merge keeps it.
        /// </summary>
        private static List<int> MergeOpenRowspans(List<int> open, List<int> opened,
            Dictionary<int, (int remaining, int span)> carry)
        {
            var merged = new List<int>(open.Count + opened.Count);
            int i = 0, j = 0;
            while (i < open.Count || j < opened.Count)
            {
                if (j >= opened.Count || (i < open.Count && open[i] < opened[j]))
                {
                    if (carry.ContainsKey(open[i])) merged.Add(open[i]);
                    i++;
                }
                else merged.Add(opened[j++]);
            }
            return merged;
        }

        /// <summary>
        /// Puts each row's cells on the columns the table keeps and fills what a row leaves empty — a gap
        /// before a rowspan it reaches past its own cells, and its end — with empty cells that state no
        /// border; returns the column widths for the kept columns and sets
        /// <paramref name="gridCols"/> to their count.
        /// </summary>
        /// <remarks>
        /// <para><b>Which columns stay.</b> Under automatic layout a column is as wide as the cells that
        /// start in it (CSS 2.1 §17.5.2.2), so one no cell starts in and no <c>&lt;col&gt;</c> gives a
        /// width has no width at all: <c>&lt;td colspan="100"&gt;</c> across a two-column table is two
        /// columns wide, as Chrome draws it. Kept, those columns would be drawn as ninety-eight empty
        /// boxes, and at <c>colspan="1000"</c> the real two would be slivers. Under <c>table-layout: fixed</c> a
        /// column keeps its share of the table whether a cell starts in it or not, so every one stays.</para>
        /// <para><b>The fill is charged</b> to <see cref="MaxTableCells"/>, as a mapped cell is, and stops
        /// when the budget is spent: a wide row over many narrow ones is the product of two counts the
        /// markup chooses — four kilobytes of it asked for a hundred million cells. And no table is laid
        /// out wider than <see cref="MaxTableColumns"/>: a cell that starts past it is dropped and the drop
        /// reported, one that reaches past it ends there.</para>
        /// <para><b>Why the fill.</b> A missing cell is not drawn, but a table's own border goes round all
        /// of it, and a regular grid is what a tagged table's structure wants — a ragged one is what a
        /// checker reports. A gap filled is also what keeps a rowspan's continuation under the cell it
        /// continues: it sat straight after the row's own cells, merging with the wrong cell above.</para>
        /// </remarks>
        private List<double> LayGrid(M.Table mt, List<List<int>> rowStarts, List<double> colWidths, ref int gridCols)
        {
            // The kept columns, ascending, under automatic layout; under fixed layout every column is.
            List<int>? kept = null;
            if (mt.AutoLayout)
            {
                var set = new HashSet<int>();
                foreach (var starts in rowStarts) foreach (int s in starts) set.Add(s);
                for (int i = 0; i < colWidths.Count && i < gridCols; i++) if (colWidths[i] > 0) set.Add(i);
                kept = set.ToList();
                kept.Sort();
            }
            // How many kept columns lie before the markup's column x.
            int Rank(int x)
            {
                if (kept == null) return x;
                int at = kept.BinarySearch(x);
                return at >= 0 ? at : ~at;
            }

            int columns = Math.Min(kept?.Count ?? gridCols, MaxTableColumns);
            bool clipped = false, fillSpent = false;
            for (int r = 0; r < mt.Rows.Count; r++)
            {
                var cells = mt.Rows[r].Cells;
                var starts = rowStarts[r];
                var laid = new List<M.TableCell>(cells.Count);
                int at = 0;
                for (int i = 0; i < cells.Count; i++)
                {
                    int first = Rank(starts[i]);
                    if (first >= columns) { clipped = true; break; }
                    // A cell past an unfilled gap is kept where it falls: its content is the budget's
                    // to keep, the alignment is what the spent budget gives up.
                    while (at < first && !fillSpent)
                    {
                        if (TakeTableCell()) { laid.Add(FillCell()); at++; }
                        else fillSpent = true;
                    }
                    var cell = cells[i];
                    int end = Rank(starts[i] + Math.Max(1, cell.GridSpan));
                    if (end > columns) { end = columns; clipped = true; }
                    cell.GridSpan = end - first;
                    laid.Add(cell);
                    at = end;
                }
                while (at < columns && !fillSpent)
                {
                    if (TakeTableCell()) { laid.Add(FillCell()); at++; }
                    else fillSpent = true;
                }
                cells.Clear();
                cells.AddRange(laid);
            }
            if (clipped)
                ReportApproximation("A table is laid out at most " + MaxTableColumns + " columns wide; the " +
                                    "cells past that column were not mapped and are absent from the output.");

            // The widths of the kept columns; both lists ascend, so the first column past the declared
            // widths ends them.
            var widths = new List<double>();
            for (int j = 0; j < columns; j++)
            {
                int source = kept == null ? j : kept[j];
                if (source >= colWidths.Count) break;
                widths.Add(colWidths[source]);
            }
            gridCols = columns;
            return widths;
        }

        /// <summary>
        /// An empty cell that states no border, filling a row with fewer cells than the grid so the
        /// table's frame runs along the whole table: a missing cell is not drawn, but a table's own border
        /// goes round all of it. A header wider than its body — a column a script moved into the header —
        /// left the frame open where the body's cell was missing.
        /// </summary>
        /// <remarks>
        /// In a table framed by its own border (<see cref="M.Table.OuterBorderOnly"/>) an edge the cell
        /// does not state falls back to the frame outside and to nothing inside; in a <c>border="1"</c>
        /// table its ruled edges lie where the table's frame and its neighbours' edges already are; in a
        /// borderless one it draws nothing.
        /// </remarks>
        private static M.TableCell FillCell()
        {
            var pad = new M.TableCell { InnerPaddingPt = 0 };
            pad.Blocks.Add(new M.Paragraph { SpacingAfter = 0, Runs = { new M.TextRun { Text = "" } } });
            return pad;
        }

        /// <summary>The browser's <c>border-spacing</c> when a table states none: 2px (HTML's UA stylesheet).</summary>
        internal const double DefaultBorderSpacingPx = 2;

        /// <summary>
        /// A separated table's <c>border-spacing</c> — one length for both directions, or the
        /// horizontal then the vertical — else the <c>cellspacing</c> attribute, else the browser's
        /// 2px. Only the separated model spaces cells (CSS 2.1 §17.6.1); the caller asks for no other.
        /// A negative length is invalid (§17.6.1), and an invalid declaration is ignored, not clamped.
        /// </summary>
        private (double Horizontal, double Vertical) BorderSpacing(IElement tableEl, double fontSize)
        {
            string? css = StyleValue(tableEl, "border-spacing")?.Trim();
            if (!string.IsNullOrEmpty(css))
            {
                var parts = css!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                double h = ParseLength(parts[0], 0, fontSize);
                double v = parts.Length > 1 ? ParseLength(parts[1], 0, fontSize) : h;
                if (h >= 0 && v >= 0) return (h, v);
            }
            double px = int.TryParse(tableEl.GetAttribute("cellspacing"), out int cs) && cs >= 0 ? cs : DefaultBorderSpacingPx;
            double pt = PdfUnits.PixelsToPoints(px);
            return (pt, pt);
        }

        /// <summary>
        /// Puts the table's own border on the outer edges of its outer cells wherever a cell states a
        /// thinner one, or none: the table's frame is drawn round it whatever its cells say, and in
        /// the collapsed model the wider of two borders meeting wins (CSS 2.1 §17.6.2.1). Bootstrap's
        /// <c>.table-bordered</c> is the case: its first header row sets <c>border-top: 0</c>, and the
        /// frame above it vanished.
        /// </summary>
        private static void FrameOuterEdges(M.Table mt)
        {
            if (mt.BorderWidth <= 0 || mt.Rows.Count == 0) return;
            M.Border frame = new M.Border(mt.BorderWidth, mt.BorderColor);
            M.Border? Framed(M.Border? edge) =>
                edge == null ? null : edge.None || edge.Width < frame.Width ? frame : edge;

            for (int r = 0; r < mt.Rows.Count; r++)
            {
                var cells = mt.Rows[r].Cells;
                for (int i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    if (r == 0) c.BorderTop = Framed(c.BorderTop);
                    if (r == mt.Rows.Count - 1) c.BorderBottom = Framed(c.BorderBottom);
                    // The logical first and last cells, on the physical side the columns run from.
                    if (i == 0)
                    {
                        if (mt.RtlColumns) c.BorderRight = Framed(c.BorderRight);
                        else c.BorderLeft = Framed(c.BorderLeft);
                    }
                    if (i == cells.Count - 1)
                    {
                        if (mt.RtlColumns) c.BorderLeft = Framed(c.BorderLeft);
                        else c.BorderRight = Framed(c.BorderRight);
                    }
                }
            }
        }

        /// <summary>
        /// Gives each cell the room its borders take, inside its padding, as CSS's table models do:
        /// with <c>border-collapse: separate</c> (HTML's default) a cell's box holds its whole
        /// border; with <c>collapse</c> the borders between two cells are one border, as wide as the
        /// wider of the two, and each cell holds half of it (CSS 2.1 §17.6.2).
        /// </summary>
        /// <remarks>
        /// <para>The renderer strokes a cell's border on its edge and sizes the row from its content
        /// and padding alone, so a Bootstrap row was a border short of Chrome's — a pixel a row, and
        /// ten rows down a line of text out of place.</para>
        /// <para>Row by row, not cell by cell: a collapsed boundary between two rows is the widest
        /// border any cell puts on it, which every cell of those rows takes half of. An edge a cell
        /// does not state is the table's default (<see cref="M.Table.BorderWidth"/>, unless the table
        /// draws only its outer frame); a suppressed one is none.</para>
        /// </remarks>
        private static void ReserveBorderRoom(M.Table mt, bool collapse)
        {
            double fallback = mt.OuterBorderOnly ? 0 : mt.BorderWidth;
            double Width(M.Border? b) => b == null ? fallback : b.None ? 0 : b.Width;

            int n = mt.Rows.Count;
            var tops = new double[n];
            var bottoms = new double[n];
            for (int r = 0; r < n; r++)
                foreach (var c in mt.Rows[r].Cells)
                {
                    tops[r] = Math.Max(tops[r], Width(c.BorderTop));
                    bottoms[r] = Math.Max(bottoms[r], Width(c.BorderBottom));
                }

            for (int r = 0; r < n; r++)
            {
                var cells = mt.Rows[r].Cells;
                for (int i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    if (!collapse)
                    {
                        c.MarginTop += Width(c.BorderTop);
                        c.MarginBottom += Width(c.BorderBottom);
                        c.MarginLeft += Width(c.BorderLeft);
                        c.MarginRight += Width(c.BorderRight);
                        continue;
                    }
                    double above = r == 0 ? tops[0] : Math.Max(bottoms[r - 1], tops[r]);
                    double below = r == n - 1 ? bottoms[r] : Math.Max(bottoms[r], tops[r + 1]);
                    double left = i == 0 ? Width(c.BorderLeft) : Math.Max(Width(cells[i - 1].BorderRight), Width(c.BorderLeft));
                    double right = i == cells.Count - 1 ? Width(c.BorderRight) : Math.Max(Width(c.BorderRight), Width(cells[i + 1].BorderLeft));
                    c.MarginTop += above / 2;
                    c.MarginBottom += below / 2;
                    c.MarginLeft += left / 2;
                    c.MarginRight += right / 2;
                }
            }
        }

        /// <summary>
        /// The background a cell with none of its own shows: its row's, else its row group's
        /// (<c>thead</c>/<c>tbody</c>/<c>tfoot</c>) — the table background layers of CSS 2.1
        /// §17.5.1, above the table's own.
        /// </summary>
        /// <remarks>
        /// Only the cell's own was read, so Bootstrap's <c>.table-striped</c> — whose stripes are on
        /// <c>tr:nth-of-type(odd)</c> — and a highlighted row (<c>tr.success</c>) drew no colour.
        /// </remarks>
        private static string? RowBackground(IElement cellEl)
        {
            // The HTML parser always gives a cell a row and a row a group.
            IElement? row = cellEl.ParentElement;
            if (row == null) return null;
            return ResolveBackgroundColor(row)
                ?? (row.ParentElement is { } group ? ResolveBackgroundColor(group) : null);
        }

        // Parses <colgroup>/<col> width hints into a per-column width list (0 = auto), as far as the
        // widest table is laid out: a hundred thousand <col span="1000"> built a hundred million widths.
        private List<double> ParseColGroups(IElement tableEl, double refWidth)
        {
            var widths = new List<double>();
            foreach (var col in TableCols(tableEl))
            {
                int span = ParseSpan(col.GetAttribute("span"), MaxColSpan);
                double w = ResolveWidth(col.GetAttribute("width") ?? StyleValue(col, "width"), refWidth, 0);
                for (int i = 0; i < span && widths.Count < MaxTableColumns; i++) widths.Add(w);
                if (widths.Count >= MaxTableColumns) break;
            }
            return widths;
        }

        // Sets Table.ColumnWidths from <colgroup>/<col> hints, or (failing that) the first
        // row's explicit cell widths. Leaves it empty (→ uniform columns) when neither exists.
        private static void ApplyColumnWidths(M.Table mt, List<double> colWidths, int gridCols)
        {
            if (gridCols <= 0) return;

            if (colWidths.Any(w => w > 0))
            {
                for (int i = 0; i < gridCols; i++)
                    mt.ColumnWidths.Add(i < colWidths.Count ? Math.Max(0, colWidths[i]) : 0);
                return;
            }

            if (mt.Rows.Count == 0) return;
            var derived = new double[gridCols];
            bool anyCell = false;
            int col = 0;
            foreach (var cell in mt.Rows[0].Cells)
            {
                int span = Math.Max(1, cell.GridSpan);
                if (cell.WidthPt is double cw && cw > 0)
                {
                    double per = cw / span;
                    for (int i = 0; i < span && col + i < gridCols; i++) { derived[col + i] = per; anyCell = true; }
                }
                col += span;
            }
            if (anyCell)
                for (int i = 0; i < gridCols; i++) mt.ColumnWidths.Add(derived[i]);
        }

        /// <summary>
        /// A cell's <c>vertical-align</c>: its own, else its row's, else its row group's — each by
        /// CSS or the <c>valign</c> attribute — and <c>middle</c> when none says, which is where the
        /// UA stylesheet puts it (<c>thead, tbody, tfoot, tr { vertical-align: middle }</c>, the cells
        /// inheriting). Read as top when nothing was declared, a rowspan cell's text sat at the top
        /// where a browser centres it. <c>baseline</c> puts the cell's first baseline on the row's,
        /// shared with the row's other baseline cells (CSS 2.1 §17.5.3).
        /// The rest (<c>sub</c>, <c>text-top</c>, a length) are top.
        /// </summary>
        private static M.CellVerticalAlign ResolveCellVerticalAlign(IElement cellEl)
        {
            for (IElement? el = cellEl; el != null && !NameIs(el, HtmlTags.Table); el = el.ParentElement)
            {
                string? va = StyleValue(el, "vertical-align")?.Trim();
                if (va != null && va.Equals("inherit", StringComparison.OrdinalIgnoreCase)) continue;
                va ??= el.GetAttribute("valign")?.Trim();
                if (va == null) continue;
                if (va.Equals("middle", StringComparison.OrdinalIgnoreCase) ||
                    va.Equals("center", StringComparison.OrdinalIgnoreCase)) return M.CellVerticalAlign.Center;
                if (va.Equals("bottom", StringComparison.OrdinalIgnoreCase)) return M.CellVerticalAlign.Bottom;
                if (va.Equals("baseline", StringComparison.OrdinalIgnoreCase)) return M.CellVerticalAlign.Baseline;
                return M.CellVerticalAlign.Top;
            }
            return M.CellVerticalAlign.Center;
        }

        /// <summary>
        /// Where a table narrower than its band sits (<c>M.Table.Alignment</c>): its own
        /// <c>align</c>; else auto side margins (both centre it, the left alone puts it right);
        /// else the nearest ancestor that aligns anything — a <c>&lt;center&gt;</c>, or an
        /// <c>align</c> attribute, which HTML's rendering rules map to <c>-webkit-center</c> and so
        /// centre block children too, or a CSS <c>text-align</c>, which centres only text (unless it
        /// is <c>-webkit-center</c>). Found on an HTML e-mail: a 600px table inside
        /// <c>&lt;center&gt;</c> and an <c>align="center"</c> button table sat at the left.
        /// </summary>
        private static M.TextAlignment? ResolveTableAlignment(IElement table)
        {
            M.TextAlignment? FromAlign(string? a)
                => Eq(a, "center") ? M.TextAlignment.Center : Eq(a, "right") ? M.TextAlignment.Right
                 : Eq(a, "left") ? M.TextAlignment.Left : null;

            if (FromAlign(table.GetAttribute("align")?.Trim()) is { } own) return own;

            bool leftAuto = MarginSideIsAuto(table, "margin-left", 3), rightAuto = MarginSideIsAuto(table, "margin-right", 1);
            if (leftAuto && rightAuto) return M.TextAlignment.Center;
            if (leftAuto) return M.TextAlignment.Right;

            for (IElement? p = table.ParentElement; p != null; p = p.ParentElement)
            {
                if (NameIs(p, HtmlTags.Center)) return M.TextAlignment.Center;
                string? css = StyleValue(p, "text-align")?.Trim();
                if (css != null) return Eq(css, "-webkit-center") ? M.TextAlignment.Center : null;
                if (p.GetAttribute("align")?.Trim() is string attr) return FromAlign(attr);
            }
            return null;
        }

        /// <summary>
        /// Whether one side's margin is <c>auto</c>: its longhand, else the <c>margin</c> shorthand's
        /// value for that side (<paramref name="shorthandIndex"/> of four: top, right, bottom, left).
        /// </summary>
        private static bool MarginSideIsAuto(IElement el, string longhand, int shorthandIndex)
        {
            if (StyleValue(el, longhand)?.Trim() is string v) return Eq(v, "auto");
            string[] parts = (StyleValue(el, "margin") ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            // 1 value: all; 2: vertical, horizontal; 3: top, horizontal, bottom; 4: each.
            string side = parts.Length switch { 1 => parts[0], 2 or 3 => parts[1], _ => parts[shorthandIndex] };
            return Eq(side, "auto");
        }

        private M.TableCell BuildCell(IElement cellEl, M.Section sec, bool isHeader, int span,
            double tableWidth, double cellPad, int depth, Style inherited)
        {
            var mc = new M.TableCell();
            if (span > 1) mc.GridSpan = span;

            string? cellBg = ResolveBackgroundColor(cellEl) ?? RowBackground(cellEl);
            if (cellBg != null) mc.BackgroundColor = cellBg;

            double cw = ResolveWidth(cellEl.GetAttribute("width") ?? StyleValue(cellEl, "width"), tableWidth, 0);
            if (cw > 0) mc.WidthPt = cw;

            mc.VAlign = ResolveCellVerticalAlign(cellEl);

            // Per-side, and tri-state. An earlier revision skipped this on the premise that "the
            // exporter draws a table's border from Table.BorderWidth alone and never reads a
            // TableCell's" — which is false: Builder/Export/TableRenderer.cs draws every edge as
            // pl.Cell.BorderTop ?? Fallback(…), and HtmlManagedCssCorrectnessTests
            // .ExplicitCellBorder_WinsOverTheOuterOnlyFallback has pinned that all along.
            //
            // A naive tri-state really would have been a no-op, but for a different reason: on a
            // CELL, null means "use the table default", so suppressing an edge with null hands it
            // straight back. Border.None is the model's word for suppression here — the opposite of
            // the paragraph convention, where null must stay null because the exporter gates on
            // BorderTop != null and a non-null no-op border would make border:none reserve a block
            // for pagination.
            ApplyCellBorders(cellEl, mc);

            // Padding, stated exactly (TableCell.InnerPaddingPt): CSS padding wins — a declared 0
            // included, which a test of the values could not tell from none; else the table-level
            // cellpadding attribute; else a browser's own `td, th { padding: 1px }`.
            mc.InnerPaddingPt = 0;
            var (pt, pr, pb, pl) = ResolveSides(cellEl, "padding", 12);
            if (DeclaresPadding(cellEl))
            {
                mc.MarginTop = pt; mc.MarginRight = pr; mc.MarginBottom = pb; mc.MarginLeft = pl;
            }
            else
            {
                double pad = cellPad >= 0 ? cellPad : PdfUnits.PixelsToPoints(DefaultCellPaddingPx);
                mc.MarginTop = mc.MarginRight = mc.MarginBottom = mc.MarginLeft = pad;
            }

            var cellStyle = DeriveStyle(inherited, cellEl, null, isHeader);
            M.TextAlignment? cellAlign = ResolveAlign(cellEl) ?? (isHeader ? M.TextAlignment.Center : (M.TextAlignment?)null);

            if (HasBlockChild(cellEl))
            {
                // Block content in a cell — a nested table, several paragraphs, a list — is walked
                // as blocks rather than collapsed into one run of text.
                //
                // Flattened by CollectInline, a nested <table> would lose its structure entirely and
                // become concatenated text; TableCell.Blocks and the renderer support nested
                // tables. The budget and depth are
                // threaded through because this opens a new recursion path: without them a
                // deeply-nested document would reach the walk with no guard at all.
                WalkBlocks(cellEl, sec, mc.Blocks, 0, depth, cellStyle, cellAlign);
                foreach (var block in mc.Blocks)
                    if (block is M.Paragraph bp && cellAlign.HasValue
                        && bp.Alignment == M.TextAlignment.Start)
                        bp.Alignment = cellAlign.Value;
            }

            if (mc.Blocks.Count == 0)
            {
                // The cell's own inline content: no element of its own, so no margin — the model's
                // default space after a paragraph added 8px under the text of every row — and the
                // line height the cell inherits, as the paragraphs NewPara makes have.
                var p = new M.Paragraph { SpacingAfter = 0, Hyphenate = Hyphenates(cellStyle) };
                if (cellStyle.LineHeight > 0) p.LineSpacingPt = cellStyle.LineHeight;
                else p.LineHeightFactor = NormalLineHeightFactor;
                if (cellAlign.HasValue) p.Alignment = cellAlign.Value;
                CollectInline(cellEl, p, cellStyle, depth + 1);
                if (p.Runs.Count == 0) p.Runs.Add(new M.TextRun { Text = "" });
                mc.Blocks.Add(p);
            }
            return mc;
        }

        /// <summary>
        /// Applies a cell's border declarations — the shorthands and the <c>border-color</c> /
        /// <c>-width</c> / <c>-style</c> longhands, in cascade order — distinguishing an edge that is
        /// <b>absent</b> from one that explicitly <b>suppresses</b> it.
        ///
        /// <para>Through <see cref="ResolveBorderEdges"/>, the one reading of an element's border,
        /// shared with the paragraph path. Reading the <c>border</c> shorthand <i>only</i>,
        /// <c>td{border:1px solid #ccc}</c> with <c>td.first{border-left:none}</c> — the commonest
        /// table idiom there is — would never see the second declaration at all.</para>
        ///
        /// <para><b>A suppressed cell edge is <c>Border.None</c>, never <c>null</c>.</b>
        /// <c>TableRenderer</c> draws each edge as <c>Cell.BorderX ?? Fallback(…)</c>, so on a cell
        /// <c>null</c> means "use the table's default" — assigning it for a suppressed edge hands
        /// the edge straight back and the declaration has no effect. This is the opposite of the
        /// paragraph convention, where <c>null</c> already draws nothing and must stay <c>null</c>
        /// (the exporter's <c>hasBox</c> test is <c>BorderTop != null || …</c>, so a non-null no-op
        /// border would make <c>border:none</c> reserve a block for pagination).</para>
        /// </summary>
        private void ApplyCellBorders(IElement cellEl, M.TableCell mc)
        {
            BorderEdge[] edges = ResolveBorderEdges(cellEl);
            M.Border CellEdge(int side) => edges[side].ToBorder() ?? new M.Border { None = true };

            if (edges[EdgeTop].Present) mc.BorderTop = CellEdge(EdgeTop);
            if (edges[EdgeRight].Present) mc.BorderRight = CellEdge(EdgeRight);
            if (edges[EdgeBottom].Present) mc.BorderBottom = CellEdge(EdgeBottom);
            if (edges[EdgeLeft].Present) mc.BorderLeft = CellEdge(EdgeLeft);
        }

        /// <summary>Whether <paramref name="cell"/>'s CSS sets any padding, by the shorthand or a longhand.</summary>
        private static bool DeclaresPadding(IElement cell) =>
            StyleValue(cell, "padding") != null || StyleValue(cell, "padding-top") != null
            || StyleValue(cell, "padding-right") != null || StyleValue(cell, "padding-bottom") != null
            || StyleValue(cell, "padding-left") != null;

        /// <summary>A table cell's padding when neither CSS nor <c>cellpadding</c> sets one — the HTML UA stylesheet's.</summary>
        private const double DefaultCellPaddingPx = 1;

        /// <summary>
        /// A <c>colspan</c>, <c>rowspan</c> or <c>span</c> attribute: 1 for anything that is not an
        /// integer above 1, and clamped to <paramref name="max"/> — the HTML Standard's own clamps,
        /// <see cref="MaxColSpan"/> (1000) and <see cref="MaxRowSpan"/> (65534). Read at face value, a
        /// <c>colspan="2147483647"</c> would build two billion columns and run out of memory on a table
        /// of ten cells.
        /// </summary>
        internal static int ParseSpan(string? s, int max)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v > 1 ? Math.Min(v, max) : 1;

        /// <summary>The HTML Standard's clamp on <c>colspan</c> and a column group's <c>span</c>.</summary>
        internal const int MaxColSpan = 1000;

        /// <summary>The HTML Standard's clamp on <c>rowspan</c>.</summary>
        internal const int MaxRowSpan = 65534;

        /// <summary>
        /// The most columns a table is laid out with — as many as one cell may span. A cell that starts
        /// past it is dropped and the drop reported; one that reaches past it ends there. Under automatic
        /// layout only the columns a cell starts in count, so the "span every column" idiom never meets
        /// it; under <c>table-layout: fixed</c> a row of <c>colspan="1000"</c> cells does at its second.
        /// </summary>
        internal const int MaxTableColumns = 1000;

        [ThreadStatic] private static int _tableGridSteps;

        /// <summary>
        /// The steps the grid's row walk took during the conversion this thread last ran: a cell, a
        /// rowspan's continuation, or a move to the next open rowspan.
        /// </summary>
        /// <remarks>
        /// <para>Evidence for the walk's bound, which no converted table shows: a row that walked to a
        /// far rowspan a column at a time drew the same table. Reset at the start of a conversion with
        /// the other liveness counters and left standing afterwards, so a caller — in practice a test —
        /// reads the figure for the conversion that just finished.</para>
        /// </remarks>
        internal static int TableGridSteps => _tableGridSteps;

        /// <summary>
        /// The cells one conversion may map, over every table; the rows past the budget are dropped and
        /// the drop reported once, as the node budget is.
        /// </summary>
        /// <remarks>
        /// Measured on the structure fuzzer's million-cell table (31 MB of markup): the
        /// parsed DOM is 0.9 GB whatever the budget; each mapped one-letter cell then costs about 1 KB
        /// of model and 3.5 KB through the export — a tagged cell is two structure objects and its
        /// marked content — so 250 000 cells exported as 520 000 objects, a 67 MB file and a 2.8 GB
        /// peak. At 100 000 the same file peaks at 1.2 GB, the DOM's share, and a hundred columns by
        /// a thousand rows is still whole. A larger table belongs to the browser engine.
        /// </remarks>
        internal static int MaxTableCells { get; set; } = DefaultMaxTableCells;

        /// <summary>Built-in value of <see cref="MaxTableCells"/>; a test lowers the property and restores it.</summary>
        internal const int DefaultMaxTableCells = 100_000;

        private int _tableCells;
        private bool _tableCellBudgetReported;

        /// <summary>Consumes one table cell from <see cref="MaxTableCells"/>; false, reporting once, when spent.</summary>
        private bool TakeTableCell()
        {
            if (++_tableCells <= MaxTableCells) return true;
            ReportTableCellBudgetSpent();
            return false;
        }

        /// <summary>
        /// Consumes <paramref name="want"/> cells at once, all or nothing: true and charged when the
        /// whole block fits the shared <see cref="MaxTableCells"/> budget, false and nothing charged
        /// (reporting once) when it does not. A CSS Grid charges a row's worth here before it reads
        /// it off, so many grids are bounded by the one document budget the HTML tables already are
        /// — a grid could otherwise read off 1024×64 cells per container unbilled.
        /// </summary>
        private bool TakeTableCells(int want)
        {
            if (want <= 0) return true;
            if (_tableCells + (long)want > MaxTableCells)
            {
                ReportTableCellBudgetSpent();
                return false;
            }
            _tableCells += want;
            return true;
        }

        private void ReportTableCellBudgetSpent()
        {
            if (_tableCellBudgetReported) return;
            _tableCellBudgetReported = true;
            ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                "The document's tables exceeded the " + MaxTableCells + "-cell mapping budget; the rows " +
                "after that point were not mapped and are absent from the output.");
        }
    }
}
