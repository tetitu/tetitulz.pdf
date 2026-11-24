// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    // CSS multi-column (column-count / the columns shorthand) — mapped onto the
    // document model's table, the fourth feature to take that route after flex,
    // float and grid.
    //
    // The decline this replaces argued from where the code lived. GAPS said
    // "multi-column flow exists only in the legacy DocxToPdf renderer", which is true and is not
    // an answer to whether the Builder model can express it. A two-column block is a borderless
    // one-row table with two cells and the content divided between them — and a table cell already
    // holds arbitrary blocks, paginates, RTL-mirrors and tags.
    //
    // What it is, and what is left. The content is balanced by
    // each block's measured height at the column's width (PdfExporter.MeasureHeight,
    // the layout that will draw it): the columns are as short as the blocks allow (CSS
    // Multi-column 1 §8.1's balancing — the least height everything fits under in the count asked),
    // reading order kept; and a paragraph of plain text that straddles a column is
    // broken between its lines (PdfExporter.SplitAtHeight, its orphans and widows
    // kept), as CSS breaks it, not at the block boundary. A box of blocks is one unit, and so is a list, a table, a paragraph kept together
    // or holding an image, a footnote, a form field or an inline box; where one of those is taller
    // than an equal share the columns are uneven, and that is reported through
    // BuilderDiagnosticKind.ContentApproximated — not when the element declares
    // columns that this declines to build. Mid-line breaks there are none, in CSS either.
    //
    // column-gap and column-rule are applied, not reported. The gap
    // is the distance between two columns' text, which a table expresses as inner padding on the
    // sides of adjacent cells; the rule is a border on the edge between them. "A table has no
    // inter-cell spacing" is true of spacing, and not an answer to whether the cells have padding
    // and borders, which they carry. What is reported is a gap narrower than the two cells' own padding, and a rule
    // style other than solid.
    //
    // column-span: all is applied: a spanning child splits the
    // block into the columns before it, the spanner at the full width, and the columns after it —
    // which is what the split-on-block-boundaries model does naturally, since the spanner is a
    // block boundary the author drew.
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>
        /// Most columns this will build. <c>column-count</c> is author-supplied and sizes an
        /// allocation; past a handful the columns are narrower than a word anyway.
        /// </summary>
        private const int MaxColumnCount = 16;

        /// <summary>True when <paramref name="el"/> declares a usable multi-column block.</summary>
        private bool IsColumnContainer(IElement el) => ReadColumnCount(el) >= 2;

        /// <summary>
        /// Lays a multi-column block out. Returns <c>false</c> when the caller should fall through
        /// to ordinary block handling — which is the right answer for one column, since that IS
        /// ordinary flow.
        /// </summary>
        private bool MapColumnContainer(
            IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel, int depth,
            Style inherited, M.TextAlignment? align)
        {
            int count = ReadColumnCount(el);
            if (count < 2) return false;

            Style st = DeriveStyle(inherited, el, null, false);

            // A positioned multi-column container is the containing block of the positioned blocks
            // in its columns (CSS 2.1 §10.1): a bare box round its segments, which the columns' cells
            // hand their positioned paragraphs to as they are drawn — not the page.
            M.BoxStart? box = IsPositioningAncestor(el) ? new M.BoxStart() : null;
            var segments = new List<M.IDocElement>();
            if (box != null) _positionedAncestors.Push((el, box));
            bool emitted;
            try { emitted = MapColumnSegments(el, sec, segments, listLevel, depth, st, align, count); }
            finally { if (box != null) _positionedAncestors.Pop(); }
            if (!emitted) return false;
            if (box != null) sink.Add(box);
            sink.AddRange(segments);
            if (box != null) sink.Add(new M.BoxEnd());
            return true;
        }

        /// <summary>The container's segments — its column tables and the spanners between them — into <paramref name="sink"/>; false when nothing was laid out.</summary>
        private bool MapColumnSegments(IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel, int depth,
            Style st, M.TextAlignment? align, int count)
        {
            // column-span: all (CSS Multi-column 1 §6) on a child splits the block there: the
            // children before it are one column block, the spanner an ordinary block at the full
            // width, the children after it another column block — the segments walked one at a
            // time.
            var nodes = el.ChildNodes.ToList();
            bool reported = false, emitted = false;
            int start = 0;
            for (int i = 0; i <= nodes.Count; i++)
            {
                bool spans = i < nodes.Count && nodes[i] is IElement spanner && !IsHidden(spanner) && SpansAllColumns(spanner);
                if (i < nodes.Count && !spans) continue;

                // Built into a staging list first: the blocks have to exist before they can be
                // distributed, and the walk is the one place that knows how to turn children into
                // them. Mapping straight into cells would need the split decided in advance, which is
                // exactly what cannot be known without the content.
                var blocks = new List<M.IDocElement>();
                WalkNodes(el, nodes, start, i, sec, blocks, listLevel, depth, st, align);
                emitted |= EmitColumnBlock(el, st, sec, sink, blocks, count, ref reported);
                if (spans)
                {
                    MapBlock((IElement)nodes[i], sec, sink, listLevel, depth + 1, st, align);
                    emitted = true;
                }
                start = i + 1;
            }
            return emitted;
        }

        /// <summary>A child that spans every column of its multi-column container: <c>column-span: all</c> (§6).</summary>
        private bool SpansAllColumns(IElement child)
            => StyleValue(child, "column-span") is string span && Eq(span.Trim(), "all");

        /// <summary>
        /// Lays <paramref name="blocks"/> — one segment of a multi-column block's content — out as
        /// its <paramref name="count"/>-cell table, or as ordinary flow, reported, when there are
        /// fewer blocks than columns. False when there was nothing to lay out.
        /// </summary>
        /// <param name="el">The multi-column container, whose gap and rule the table takes.</param>
        /// <param name="st">The container's style.</param>
        /// <param name="sec">The section the block is in.</param>
        /// <param name="sink">Where the table, or the flowed blocks, go.</param>
        /// <param name="blocks">The segment's blocks, in document order.</param>
        /// <param name="count">The column count.</param>
        /// <param name="reported">Whether the block's approximation has been reported already: once per container, not per segment.</param>
        private bool EmitColumnBlock(IElement el, Style st, M.Section sec, List<M.IDocElement> sink,
            List<M.IDocElement> blocks, int count, ref bool reported)
        {
            if (blocks.Count == 0) return false;

            // The units the content divides into: a block, or a box of blocks as one — a box split
            // across two columns would draw as two boxes. A paragraph of plain text is a unit the
            // balance may break into two.
            List<List<M.IDocElement>> units = Units(blocks);

            // The gap is the distance between two columns' text (half of it each side), so the
            // content widths divide what is left — the width each unit is measured at.
            double bandWidth = SectionContentWidth(sec);
            double gap = ReadColumnGap(el, st.Size);
            double half = Math.Max(0, gap / 2);
            double columnWidth = Math.Max(1, (bandWidth - (count - 1) * 2 * half) / count);

            // Too little to fill every column — fewer units than columns and no paragraph that can be
            // broken into them, or too few lines under the orphans and widows: a column would be
            // empty, and the result is worse than ordinary flow, which at least reads top to bottom.
            // Declining is not a failure here. The balance's own answer covers fewer units than
            // columns too.
            List<List<Slice>> buckets = Balance(units, count, columnWidth, out bool uneven);
            if (buckets.Exists(b => b.Count == 0))
            {
                ReportFewerBlocks(count, units.Count);
                foreach (var block in blocks) sink.Add(block);
                return true;
            }

            // The one approximation left: a block that cannot be broken is taller than an
            // equal share, so the columns are uneven where CSS would break it. Once per container.
            if (uneven && !reported) ReportColumnApproximations(count);
            reported |= uneven;

            var table = NewBandTable();
            ApplyBandDirection(table, st, sec);

            // The paragraphs the balance broke, cut once each at every line it broke them at — from
            // one layout, so the parts' lines are the ones the balance counted.
            Dictionary<(M.Paragraph Paragraph, int FromLine), M.Paragraph> parts = CutParagraphs(buckets, columnWidth);

            var row = new M.TableRow();
            foreach (var bucket in buckets)
            {
                var cell = new M.TableCell { InnerPaddingPt = 0 };   // no padding a column does not have
                foreach (Slice slice in bucket)
                    foreach (var block in slice.Blocks(parts)) cell.Blocks.Add(block);
                // A column that ended up empty still occupies its track; dropping the cell would
                // widen its neighbours and shift the rest.
                EnsureNotEmpty(cell);
                row.Cells.Add(cell);
            }

            ApplyColumnGapAndRule(el, table, row, half, columnWidth);

            table.Rows.Add(row);
            sink.Add(table);
            return true;
        }

        /// <summary>
        /// <paramref name="blocks"/> as the units a column holds whole: each block its own, except
        /// a <see cref="M.BoxStart"/> through its matching <see cref="M.BoxEnd"/> (nested boxes
        /// inside), which is one. An unmatched close joins the unit before it.
        /// </summary>
        private static List<List<M.IDocElement>> Units(List<M.IDocElement> blocks)
        {
            var units = new List<List<M.IDocElement>>();
            for (int i = 0; i < blocks.Count; i++)
            {
                var unit = new List<M.IDocElement> { blocks[i] };
                if (blocks[i] is M.BoxStart)
                {
                    int end = M.BoxStart.IndexOfEnd(blocks, i, blocks.Count);
                    for (int j = i + 1; j <= end && j < blocks.Count; j++) unit.Add(blocks[j]);
                    i = Math.Min(end, blocks.Count - 1);
                }
                else if (blocks[i] is M.BoxEnd && units.Count > 0)
                {
                    units[units.Count - 1].Add(blocks[i]);
                    continue;
                }
                units.Add(unit);
            }
            return units;
        }

        /// <summary>
        /// <c>column-gap</c> and <c>column-rule</c>, applied: the gap as inner padding on the sides
        /// of adjacent cells, the rule as a border on the edge between them.
        /// </summary>
        /// <remarks>
        /// <para><b>The gap is the distance between two columns' text.</b> The cells add no padding
        /// of their own, so each inner side gets half the gap as its margin, and the gap is exactly
        /// the gap asked. (It was less the renderer's 3pt a side, and a gap under 6pt was drawn at
        /// 6pt and reported.)</para>
        ///
        /// <para><b>Equal column CONTENT, not equal cells.</b> An outer column has one inner side
        /// and a middle column two, so equal cells would give the middle columns narrower text.
        /// Each cell's explicit width carries its own margins, and the renderer normalises the
        /// list to the band it actually has.</para>
        ///
        /// <para><b>A mirrored band mirrors the sides.</b> The renderer draws <c>MarginLeft</c> and
        /// <c>BorderLeft</c> at a cell's VISUAL left edge, and an RTL band draws logical cell 0 at
        /// the visual right — so there the side that faces the next column is the left one.</para>
        ///
        /// <para>No declaration means CSS's <c>normal</c>: 1em of the container's font.</para>
        /// </remarks>
        /// <param name="el">The multi-column container, whose gap and rule the table takes.</param>
        /// <param name="table">The column table.</param>
        /// <param name="row">Its one row, a cell per column.</param>
        /// <param name="half">Half the gap — each inner side's margin.</param>
        /// <param name="content">One column's content width, the band less the gaps divided by the count.</param>
        private void ApplyColumnGapAndRule(IElement el, M.Table table, M.TableRow row, double half, double content)
        {
            int count = row.Cells.Count;

            M.Border? rule = ReadColumnRule(el);
            bool rtl = table.RtlColumns;

            for (int i = 0; i < count; i++)
            {
                M.TableCell cell = row.Cells[i];
                double toNext = i < count - 1 ? half : 0;
                double toPrev = i > 0 ? half : 0;

                if (rtl) { cell.MarginLeft = toNext; cell.MarginRight = toPrev; }
                else { cell.MarginRight = toNext; cell.MarginLeft = toPrev; }

                if (rule != null && i < count - 1)
                {
                    if (rtl) cell.BorderLeft = rule;
                    else cell.BorderRight = rule;
                }

                table.ColumnWidths.Add(content + toNext + toPrev);
            }
        }

        /// <summary>
        /// <c>column-gap</c>, else the column half of <c>gap</c>, else CSS's <c>normal</c> — 1em.
        /// </summary>
        private double ReadColumnGap(IElement el, double em)
        {
            string? value = StyleValue(el, "column-gap");
            if (value == null)
            {
                // `gap` is <row-gap> <column-gap>?, and one value serves both.
                string? shorthand = StyleValue(el, "gap");
                if (shorthand != null)
                {
                    string[] parts = shorthand.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) value = parts[1];
                    else if (parts.Length == 1) value = parts[0];
                }
            }

            if (value == null || Eq(value.Trim(), "normal")) return em;

            double gap = ParseLength(value, double.NaN, em);
            return double.IsNaN(gap) || gap < 0 ? em : gap;
        }

        /// <summary>
        /// The rule between columns, or null when there is none — CSS's initial
        /// <c>column-rule-style</c> is <c>none</c>, so a width or colour alone draws nothing.
        /// </summary>
        /// <remarks>
        /// The longhands are appended after the shorthand and go through the one border parser,
        /// whose later-wins rule is CSS's cascade order for them. A style other than
        /// <c>solid</c> is drawn solid and reported: the renderer strokes one kind of line.
        /// </remarks>
        private M.Border? ReadColumnRule(IElement el)
        {
            string?[] declared =
            {
                StyleValue(el, "column-rule"),
                StyleValue(el, "column-rule-width"),
                StyleValue(el, "column-rule-style"),
                StyleValue(el, "column-rule-color"),
            };
            if (declared.All(d => d == null)) return null;

            string composed = string.Join(" ", declared.Where(d => d != null));
            string? style = composed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant())
                .LastOrDefault(IsBorderStyleWord);

            if (style == null || style == "none" || style == "hidden") return null;

            if (style != "solid")
                ReportApproximation($"column-rule-style: {style} is drawn solid; the renderer strokes one " +
                                    "kind of line between columns.");

            return ParseBorder(composed);
        }

        private static string Fmt(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>
        /// Divides <paramref name="units"/>, in document order, into <paramref name="count"/>
        /// columns as short as they can be: each unit's height measured at
        /// <paramref name="columnWidth"/> by the layout that will draw it
        /// (<see cref="PdfExporter.MeasureHeight"/>), then the least column height every unit fits
        /// under in <paramref name="count"/> consecutive runs (CSS Multi-column 1 §8.1's balancing),
        /// found by bisection and filled forward, a paragraph of plain text broken at the room its
        /// column has left (<see cref="PdfExporter.SplitAtHeight"/>; before it the units
        /// were whole blocks); a run never closes while the columns after it would have fewer units
        /// left than they need to be non-empty. <paramref name="uneven"/>: a block that cannot be
        /// broken is taller than an equal share of the content, so the columns cannot balance.
        /// </summary>
        /// <remarks>
        /// The forward fill keeps reading order, the property that matters most when a unit is
        /// taller than its column (a box of blocks, a list, a table — or a paragraph too short to
        /// break under its orphans and widows — stays whole, and the columns after it balance round it).
        /// </remarks>
        private List<List<Slice>> Balance(List<List<M.IDocElement>> units, int count, double columnWidth, out bool uneven)
        {
            // Each unit measured once: a paragraph the fill may break by its lines
            // (PdfExporter.MeasureLineHeights), so the breaks are arithmetic from here; anything else
            // by its height. A unit measuring nothing (an empty paragraph with no margins) counts
            // nothing: where it lands is invisible, and the fill's "never leave a later column empty"
            // rule places it.
            var slices = new List<Slice>(units.Count);
            foreach (var unit in units)
            {
                IReadOnlyList<double>? lines = CanSplitUnit(unit) ? Measurer.MeasureLineHeights((M.Paragraph)unit[0], columnWidth) : null;
                slices.Add(lines is { Count: > 0 }
                    ? new Slice(unit, (M.Paragraph)unit[0], lines, 0, lines.Count)
                    : new Slice(unit, Measurer.MeasureHeight(unit, columnWidth)));
            }

            double total = slices.Sum(s => s.Height);
            // The least limit that fits: bisection from nothing to everything, to a hundredth — a
            // column may now be as short as the breaks allow, and the fill says when a limit is too
            // short for a block it cannot break. The `mid == low || mid == high` stop is what makes it
            // terminate: once the heights pass 2^46 (a block with a 10^15-px margin, which ParseLength
            // admits), adjacent doubles are more than a hundredth apart and `high - low` can never cross
            // it — mid rounds to an end and the loop would spin forever.
            double low = 0, high = total;
            while (high - low > 0.01)
            {
                double mid = low + (high - low) / 2;
                if (mid <= low || mid >= high) break;
                if (Fill(slices, count, mid, out _)) high = mid;
                else low = mid;
            }
            Fill(slices, count, high, out var buckets);
            uneven = high > total / count + ColumnBalanceSlackPt;
            return buckets;
        }

        /// <summary>How far past an equal share a balanced column may run before the balance counts as uneven: half a line.</summary>
        private const double ColumnBalanceSlackPt = 7;

        /// <summary>
        /// A unit as the balance holds it: a block or box of blocks whole, with its measured height —
        /// or a paragraph the fill may break, as a range of its lines with their heights, its spacing
        /// before on the first line and after on the last. Breaking is arithmetic on the range; the
        /// paragraphs themselves are cut once the balance is settled (<see cref="CutParagraphs"/>).
        /// </summary>
        private sealed class Slice
        {
            public Slice(List<M.IDocElement> unit, double measured) { Unit = unit; Measured = measured; }

            public Slice(List<M.IDocElement> unit, M.Paragraph paragraph, IReadOnlyList<double> lines, int from, int to)
            {
                Unit = unit; Paragraph = paragraph; Lines = lines; From = from; To = to;
            }

            public List<M.IDocElement> Unit { get; }
            public M.Paragraph? Paragraph { get; }
            public IReadOnlyList<double> Lines { get; } = Array.Empty<double>();
            /// <summary>The lines of <see cref="Paragraph"/> this slice holds, <see cref="To"/> exclusive.</summary>
            public int From { get; }
            public int To { get; }
            private double Measured { get; }

            public bool Whole => Paragraph == null || (From == 0 && To == Lines.Count);

            /// <summary>The slice's height as the cell will lay it out: its lines, and the spacing the paragraph's ends carry.</summary>
            public double Height
            {
                get
                {
                    if (Paragraph == null) return Measured;
                    double h = From == 0 ? Math.Max(0, Paragraph.SpacingBefore) : 0;
                    for (int i = From; i < To; i++) h += Lines[i];
                    return h + Trailing;
                }
            }

            /// <summary>The spacing after a paragraph's last line — the part of the height a column may end without; 0 for any other slice.</summary>
            public double Trailing => Paragraph != null && To == Lines.Count ? Math.Max(0, Paragraph.SpacingAfter) : 0;

            /// <summary>
            /// The slice broken so that its head, its spacing before included, is no taller than
            /// <paramref name="room"/>: the lines that fit, where <see cref="PdfExporter.LinesBeforeBreak"/>
            /// allows a break — the head its orphans (and its widows too for a tail, which begins after
            /// a break), the rest its widows; <c>false</c> for a slice that is not a breakable
            /// paragraph's, one whose lines all fit (nothing to break), or no break under those bounds.
            /// </summary>
            public bool TryBreak(double room, out Slice head, out Slice tail)
            {
                head = tail = this;
                if (Paragraph == null || !(room > 0)) return false;
                double used = From == 0 ? Math.Max(0, Paragraph.SpacingBefore) : 0;
                int fit = 0;
                while (From + fit < To && used + Lines[From + fit] <= room + 1e-6) { used += Lines[From + fit]; fit++; }
                if (From + fit >= To) return false;   // every line fits: nothing to break
                int keep = PdfExporter.LinesBeforeBreak(Paragraph, To - From, fit, afterBreak: From > 0);
                if (keep < 0) return false;
                head = new Slice(Unit, Paragraph, Lines, From, From + keep);
                tail = new Slice(Unit, Paragraph, Lines, From + keep, To);
                return true;
            }

            /// <summary>
            /// How many columns the slice could be divided over at most: one, or for a breakable
            /// paragraph the fragments its lines allow (<see cref="PdfExporter.MinFragmentLines"/>) —
            /// the first at least the orphans, and the widows too for a tail, which begins after a
            /// break; the last at least the widows; each between at least both. A slice is a
            /// paragraph's whole or its tail (a head is placed as it is cut), so its last fragment is
            /// the paragraph's. What the fill counts to know whether the slices left can fill the
            /// columns left.
            /// </summary>
            public int Pieces
            {
                get
                {
                    if (Paragraph == null) return 1;
                    int first = PdfExporter.MinFragmentLines(Paragraph, afterBreak: From > 0, beforeBreak: true);
                    int last = PdfExporter.MinFragmentLines(Paragraph, afterBreak: true, beforeBreak: false);
                    int between = PdfExporter.MinFragmentLines(Paragraph, afterBreak: true, beforeBreak: true);
                    int lines = To - From;
                    return lines < first + last ? 1 : 2 + (lines - first - last) / between;
                }
            }

            /// <summary>
            /// The blocks the slice puts in its cell: the unit whole, or its part of the paragraph the
            /// balance cut, found by the line it begins at — every part is there, since a paragraph
            /// slice exists only for a paragraph the cut allows (<see cref="CanSplitUnit"/>).
            /// </summary>
            public IEnumerable<M.IDocElement> Blocks(Dictionary<(M.Paragraph Paragraph, int FromLine), M.Paragraph> parts)
                => Whole || Paragraph == null ? Unit : new M.IDocElement[] { parts[(Paragraph, From)] };
        }

        /// <summary>
        /// Cuts each paragraph the balance broke at every line it broke it at — the slices' first
        /// lines, gathered over the buckets — once, through <see cref="PdfExporter.SplitAtLines"/>, so
        /// the parts come from one layout and their lines are the ones the balance counted. Each part
        /// is keyed by its paragraph and the line it begins at, the slice's own key.
        /// </summary>
        private Dictionary<(M.Paragraph Paragraph, int FromLine), M.Paragraph> CutParagraphs(List<List<Slice>> buckets, double columnWidth)
        {
            var cutsOf = new Dictionary<M.Paragraph, SortedSet<int>>();
            foreach (var bucket in buckets)
                foreach (Slice slice in bucket)
                    if (slice.Paragraph != null && !slice.Whole && slice.From > 0)
                    {
                        if (!cutsOf.TryGetValue(slice.Paragraph, out var cuts)) cutsOf[slice.Paragraph] = cuts = new SortedSet<int>();
                        cuts.Add(slice.From);
                    }

            var parts = new Dictionary<(M.Paragraph, int), M.Paragraph>();
            foreach (var pair in cutsOf)
            {
                List<int> cuts = pair.Value.ToList();
                // Never null: a paragraph slice exists only for a paragraph CanSplit allows, measured
                // at a width that laid its lines out.
                IReadOnlyList<M.Paragraph> cut = Measurer.SplitAtLines(pair.Key, columnWidth, cuts)!;
                parts[(pair.Key, 0)] = cut[0];
                for (int i = 0; i < cuts.Count; i++) parts[(pair.Key, cuts[i])] = cut[i + 1];
            }
            return parts;
        }

        /// <summary>
        /// Fills <paramref name="count"/> columns forward with <paramref name="slices"/>, a column
        /// closing when the next slice would take it past <paramref name="limit"/> — a paragraph that
        /// can be broken is broken at the room left first, its head ending the column and its tail
        /// going on as the next slice — or when the slices left can supply no more than a piece for
        /// each column after it (<see cref="Slice.Pieces"/>: a block one, a breakable paragraph as
        /// many as its lines allow), a slice that fits whole broken for the pieces the slices after it
        /// cannot supply; <c>false</c> when the slices do not fit under the limit in that many columns
        /// — a column holding a block taller than the limit that it could not break. (A column left
        /// empty is the caller's to decline: it is empty at every limit, since a paragraph that could
        /// be broken for it is.) The spacing after a paragraph that ends a column is not held against
        /// the limit — it overflows nothing visible, as a margin at the end of a CSS column fragment
        /// is truncated.
        /// </summary>
        private static bool Fill(List<Slice> slices, int count, double limit, out List<List<Slice>> buckets)
        {
            buckets = new List<List<Slice>>(count);
            for (int i = 0; i < count; i++) buckets.Add(new List<Slice>());

            // The slices as the fill breaks them; the caller's are left as they are, since the
            // bisection fills again at the next limit.
            var work = new List<Slice>(slices);

            // How many columns the slices after the current one could fill between them (their
            // Slice.Pieces), kept as a running suffix sum rather than re-summed per slice: summing the
            // tail at each of N slices is N² per fill and the fill runs once a bisection step, so a
            // column of two hundred thousand short paragraphs took hours. A break inserts one slice,
            // adding its pieces; advancing past a slice drops its own.
            long piecesAfter = 0;
            foreach (Slice s in work) piecesAfter += s.Pieces;

            int column = 0;
            double running = 0;
            bool fits = true;
            for (int i = 0; i < work.Count; i++)
            {
                piecesAfter -= work[i].Pieces;   // now the pieces of work[i+1 ..]
                Slice slice = work[i];
                bool forced = false;
                int columnsLeft;
                while (true)
                {
                    columnsLeft = count - column - 1;

                    if (running + slice.Height - slice.Trailing <= limit + 1e-9)
                    {
                        // It fits here whole. But the columns after this one need a piece each, and
                        // what the slices after this one cannot supply, this one is broken for: its
                        // head ends this column with an equal share of the slice with the pieces
                        // needed, and its tail faces the same question in the next column. One long
                        // paragraph that fits is as many columns as asked, where it was one block.
                        if (piecesAfter < columnsLeft)
                        {
                            double share = (slice.Height - slice.Trailing) / (columnsLeft - piecesAfter + 1);
                            if (slice.TryBreak(Math.Min(limit - running, share), out Slice first, out Slice rest))
                            {
                                work[i] = slice = first;
                                work.Insert(i + 1, rest);
                                piecesAfter += rest.Pieces;
                                forced = true;
                            }
                        }
                        break;
                    }

                    // Break a paragraph at the room this column has left: its head fills
                    // the column, its tail is the next slice — broken again in the next column if it
                    // is taller than that too. The column fills forward to the limit, as CSS fills
                    // its columns; the columns after it are counted when the tail arrives there.
                    if (slice.TryBreak(limit - running, out Slice head, out Slice tail))
                    {
                        work[i] = slice = head;
                        work.Insert(i + 1, tail);
                        piecesAfter += tail.Pieces;
                        break;
                    }
                    // Nothing to break: the next column, where a fresh limit may break it after all —
                    // and the columns after that one are counted again.
                    if (buckets[column].Count > 0 && columnsLeft > 0) { column++; running = 0; continue; }
                    fits = false;   // alone in its column and still too tall, or no column left
                    break;
                }
                buckets[column].Add(slice);
                running += slice.Height;

                // A head broken off for the columns after it ends its column; and so does any column
                // once the slices left can supply no more than a piece for each column after it.
                if (columnsLeft > 0 && (forced || piecesAfter <= columnsLeft)) { column++; running = 0; }
            }
            return fits;
        }

        /// <summary>A unit the fill may break: one paragraph <see cref="PdfExporter.CanSplit"/> allows.</summary>
        private static bool CanSplitUnit(List<M.IDocElement> unit)
            => unit.Count == 1 && unit[0] is M.Paragraph p && PdfExporter.CanSplit(p);

        /// <summary>
        /// <c>column-count</c>, or the integer in the <c>columns</c> shorthand; 0 when neither
        /// names one.
        /// </summary>
        /// <remarks>
        /// The shorthand is <c>&lt;column-width&gt; || &lt;column-count&gt;</c> in either order, so
        /// the count is whichever token is a bare integer — <c>columns: 200px 3</c> and
        /// <c>columns: 3</c> both mean three. <c>column-width</c> alone asks the engine to choose a
        /// count from the available width, which needs a measured container; that yields 0 and is
        /// reported by the caller rather than guessed at.
        /// </remarks>
        private int ReadColumnCount(IElement el)
        {
            string? explicitCount = StyleValue(el, "column-count");
            if (explicitCount != null &&
                int.TryParse(explicitCount.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                             out int n) && n >= 1)
                return Math.Min(n, MaxColumnCount);

            string? shorthand = StyleValue(el, "columns");
            if (shorthand == null) return 0;

            foreach (string token in shorthand.Split(new[] { ' ', '\t' },
                                                     StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)
                    && c >= 1)
                    return Math.Min(c, MaxColumnCount);

            return 0;
        }

        /// <summary>
        /// Reports the one approximation left in a column block: a block the balance
        /// cannot break — a box of blocks, a list, a table, a paragraph too short for its orphans and
        /// widows or kept together, one with an image, a footnote or a form field — is taller than an
        /// equal share of the content, so the columns are uneven where CSS would break it. Fired only
        /// when a column block is built and uneven, once per container, so a document that uses no
        /// columns, or balances, hears nothing. (Every column block was reported before, since every
        /// break fell on a block boundary.)
        /// </summary>
        private void ReportColumnApproximations(int count)
        {
            ReportApproximation($"column-count: {count} is approximated: a block that cannot be broken mid-way " +
                          "(a box of blocks, a list, a table, a paragraph kept together or too short for its " +
                          "orphans and widows, one with an image or a footnote) is taller than an equal share " +
                          "of the content, so the columns are uneven; CSS breaks it. Paragraphs of plain text " +
                          "are broken between lines.");
        }

        /// <summary>Declines a column block with too little to fill its columns, saying so.</summary>
        private void ReportFewerBlocks(int count, int units)
        {
            ReportApproximation($"column-count: {count} was not applied: the block has only {units} child " +
                          "block(s) to divide and not enough lines to break them into that many columns under " +
                          "their orphans and widows.");
        }

    }
}
