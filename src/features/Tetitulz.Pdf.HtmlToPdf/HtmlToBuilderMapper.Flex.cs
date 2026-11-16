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
    /// <summary>
    /// <c>display: flex</c> — mapped onto the document model's table, which is the only construct
    /// in it that places blocks side by side.
    /// </summary>
    /// <remarks>
    /// <para><b>This is an approximation and is named one.</b> A flex <i>row</i> becomes a
    /// borderless one-row table with one cell per flex item — one such table per flex line when
    /// the container wraps — and a flex <i>column</i> is ordinary vertical block flow, which the
    /// mapper already produced. That covers what business HTML actually uses flex for — a logo
    /// beside an address, a label beside a value, a toolbar of blocks, a grid of cards that wraps —
    /// and it composes with everything else, because a cell already holds arbitrary blocks
    /// including nested tables and nested flex.</para>
    /// <para><b>What it deliberately does not do:</b> shrinking below content width (a
    /// <c>flex-shrink</c> share is floored by the table at the longest word). Baseline alignment lines
    /// up the items' first paragraphs' baselines (<c>AlignBaselines</c>), and <c>align-content</c>
    /// shares a wrapping container's free height among its lines when the container has a height
    /// (<c>AlignFlexLines</c>; without one CSS has no free space to share either).
    /// Wrapping (<c>flex-wrap</c>) breaks the items into lines on their hypothetical main
    /// sizes (§9.3) — a stated basis or width, else a measured paragraph's width — and an item
    /// whose size is neither is the one case still laid out on one line. (<c>order</c> and
    /// <c>justify-content</c> over fixed-width items are applied.) Each is reported
    /// through <see cref="HtmlToPdfOptions.OnDiagnostic"/> as
    /// <see cref="BuilderDiagnosticKind.ContentApproximated"/> rather than silently ignored, so a
    /// caller can tell "laid out as asked" from "laid out approximately".</para>
    /// <para><b>Why a table rather than a new layout primitive:</b> the exporter already places,
    /// paginates, RTL-mirrors and <i>tags</i> tables. A new primitive would need all of that again,
    /// and tagged output is not optional here — an untagged side-by-side block is exactly the
    /// structure a PDF/UA checker flags.</para>
    /// </remarks>
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>Flex items narrower than this are treated as auto rather than fixed.</summary>
        private const double MinFlexBasisPt = 1.0;

        /// <summary><c>flex-wrap</c> (CSS Flexbox §5.2).</summary>
        private enum FlexWrap { NoWrap, Wrap, WrapReverse }

        /// <summary>One flex item mapped to its cell, with the sizes the line algorithm reads.</summary>
        private sealed class FlexItem
        {
            public M.TableCell Cell = null!;
            /// <summary>The track (<see cref="ResolveFlexTrack"/>): a stated width, a grow share, or 0 for auto.</summary>
            public double Track;
            /// <summary>True when the track is a width the item will not grow past.</summary>
            public bool FixedBasis;
            /// <summary>The stated basis, or an auto paragraph's measured width; 0 for none.</summary>
            public double Basis;
            public double Grow;
            /// <summary>The <c>flex-shrink</c> factor (§7.1.4), 1 unless stated; weighted by the basis when a line overflows.</summary>
            public double Shrink = 1;
            /// <summary>
            /// The hypothetical main size (§9.3 step 3) a line is broken on: the stated basis or
            /// width (0 for a zero basis), else a measured paragraph's width; <c>null</c> when the
            /// item has neither, so no line can be broken on it.
            /// </summary>
            public double? Hypothetical;
            public IElement Element = null!;
        }

        /// <summary>
        /// True when <paramref name="el"/> establishes a flex container.
        /// </summary>
        private bool IsFlexContainer(IElement el) => DisplayIs(el, "flex");

        /// <summary>
        /// Lays a flex container out. Returns <c>false</c> when the caller should fall through to
        /// ordinary block handling.
        /// </summary>
        private bool MapFlexContainer(
            IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel, int depth,
            Style inherited, M.TextAlignment? align)
        {
            Style st = DeriveStyle(inherited, el, null, false);

            var (dir, wrap) = FlexFlow(el);

            // An absolutely positioned child takes no part in the flex layout (CSS Flexbox §4.1): it
            // is placed against the container — its containing block when the container is positioned
            // — after the items, inside the container's box — not as a flex item.
            List<IElement> children = el.Children.Where(c => !IsHidden(c)).ToList();
            List<IElement> items = children.Where(c => !IsPositioned(c)).ToList();
            List<IElement> positioned = children.Where(IsPositioned).ToList();
            if (items.Count == 0) return false;

            // The container's own box — its margins, border, padding and background — round the
            // items, as any other container's: a flex <header>'s bottom rule was never drawn, since
            // only the items' table reached the page. A positioned container with nothing to draw
            // keeps a bare box, the containing block of its positioned children.
            M.BoxStart? box = ResolveBlockBox(el, st.Size, st.Direction, RelativeToGroup(st.Opacity));
            bool anchorsPositioned = IsPositioningAncestor(el);
            if (box == null && anchorsPositioned) box = new M.BoxStart();
            if (box == null)
            {
                if (!MapFlexItems(el, sec, sink, listLevel, depth, st, align, items, dir, wrap, 0)) return false;
                MapPositionedChildren(positioned, sec, sink, listLevel, depth, st, align);
                return true;
            }
            // A clear left pending by an empty clearing element is the box's, before the
            // items' walk hands it to a paragraph in a cell, where it does nothing.
            TakePendingClear(box);

            var inner = new List<M.IDocElement>();
            double room = SectionContentWidth(sec);
            double inset = BlockBoxInset(box, room);
            // Its least height is its line's too, less its own padding and borders: a
            // `min-height: 100vh` container's stretched sidebar runs the page down.
            double rowMin = box.MinHeightPt > 0
                ? box.MinHeightPt - box.PaddingTop - box.PaddingBottom - M.Border.PaintedWidth(box.BorderTop) - M.Border.PaintedWidth(box.BorderBottom)
                : 0;
            // The items carry only the opacity they add to the container's box's.
            double outerGroup = _groupOpacity;
            if (box.Opacity < 1) _groupOpacity = st.Opacity;
            if (anchorsPositioned) _positionedAncestors.Push((el, box));
            bool mapped;
            try
            {
                mapped = MapFlexItems(el, sec, inner, listLevel, depth, st, align, items, dir, wrap, inset, rowMin);
                if (mapped) MapPositionedChildren(positioned, sec, inner, listLevel, depth, st, align);
            }
            finally
            {
                _groupOpacity = outerGroup;
                if (anchorsPositioned) _positionedAncestors.Pop();
            }
            if (!mapped) return false;
            sink.Add(box);
            sink.AddRange(inner);
            sink.Add(new M.BoxEnd());
            return true;
        }

        /// <summary>
        /// How much narrower than <paramref name="room"/> the blocks inside <paramref name="box"/> lay
        /// out: its margins, padding and borders — and, for a stated width narrower than the room, the
        /// room that width leaves: the box's border box is that width (<c>OpenBlockBox</c> keeps it and
        /// leaves the rest empty), so a 50% item in a 400px container is 200px and the line wraps at
        /// 400px. A flex container's line and a grid container's tracks are the room less this
        /// (the flex container had it alone).
        /// </summary>
        private static double BlockBoxInset(M.BoxStart box, double room)
        {
            double inset = box.MarginLeft + box.MarginRight + box.PaddingLeft + box.PaddingRight
                           + M.Border.PaintedWidth(box.BorderLeft) + M.Border.PaintedWidth(box.BorderRight);
            if (box.WidthFraction != null || box.WidthPt > 0)
            {
                double wanted = (box.WidthFraction ?? 0) * room + box.WidthPt;
                double borderBox = room - box.MarginLeft - box.MarginRight;
                if (wanted > 0 && wanted < borderBox) inset += borderBox - wanted;
            }
            return inset;
        }

        /// <summary>
        /// The container's <c>flex-direction</c> and <c>flex-wrap</c>: the longhands, else the
        /// <c>flex-flow</c> shorthand's two keywords in either order (§5.3); <c>row</c> and
        /// <c>nowrap</c> when nothing says.
        /// </summary>
        private (string Direction, FlexWrap Wrap) FlexFlow(IElement el)
        {
            string? dir = StyleValue(el, "flex-direction");
            string? wrap = StyleValue(el, "flex-wrap");
            if ((dir == null || wrap == null) && StyleValue(el, "flex-flow") is string flow)
            {
                foreach (string part in flow.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Eq(part, "row") || Eq(part, "row-reverse") || Eq(part, "column") || Eq(part, "column-reverse")) dir ??= part;
                    else if (Eq(part, "nowrap") || Eq(part, "wrap") || Eq(part, "wrap-reverse")) wrap ??= part;
                }
            }
            FlexWrap w = wrap == null ? FlexWrap.NoWrap
                : Eq(wrap.Trim(), "wrap") ? FlexWrap.Wrap
                : Eq(wrap.Trim(), "wrap-reverse") ? FlexWrap.WrapReverse
                : FlexWrap.NoWrap;
            return ((dir ?? "row").Trim(), w);
        }

        /// <summary>
        /// A flex container's items into <paramref name="sink"/>: a column as block flow, a row as
        /// one-row band tables — one per flex line — over the width the container's box leaves
        /// (<paramref name="inset"/> less than the section's).
        /// </summary>
        private bool MapFlexItems(IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel, int depth,
            Style st, M.TextAlignment? align, List<IElement> items, string dir, FlexWrap wrap, double inset, double rowMin = 0)
        {
            bool reverse = dir.EndsWith("-reverse", StringComparison.OrdinalIgnoreCase);
            bool isColumn = dir.StartsWith("column", StringComparison.OrdinalIgnoreCase);

            // `order` (CSS Flexbox §5.4.1): the items are laid out by ascending order, ties in
            // document order — OrderBy is stable — before any reversal, which is applied to the
            // ordered sequence.
            bool ordered = items.Exists(item => FlexOrder(item) != 0);
            if (ordered) items = items.OrderBy(FlexOrder).ToList();

            // A flex COLUMN is vertical block flow, which is what the mapper already does. Only the
            // order can differ, so `column` costs nothing and `column-reverse` — or an `order` —
            // costs a walk over the items in their order. A column wraps only at a definite height
            // (§9.3), which the flow has not got, so `flex-wrap` on one is the state we are in.
            if (isColumn)
            {
                if (!reverse && !ordered)
                {
                    WalkBlocks(el, sec, sink, listLevel, depth, st, align);
                    return true;
                }

                foreach (IElement item in reverse ? Enumerable.Reverse(items) : items)
                {
                    if (!TakeNode()) break;
                    MapBlock(item, sec, sink, listLevel, depth + 1, st, align);
                }
                return true;
            }

            // ---- row / row-reverse ------------------------------------------------------
            // (A row-reverse is reversed per line, below: the lines are collected in the items'
            // order and each is laid out from its right edge — §9.3 breaks before §9.5 places.)

            // Derived from the section exactly as MapTable does, rather than cached on the
            // mapper: a section can change page size mid-document, and a stale field would
            // size flex tracks against the previous section.
            double contentWidth = Math.Max(1, SectionContentWidth(sec) - inset);
            bool wrapping = wrap != FlexWrap.NoWrap;

            var flexItems = new List<FlexItem>(items.Count);
            foreach (IElement item in items)
            {
                if (!TakeNode()) break;

                var cell = new M.TableCell
                {
                    VAlign = ResolveFlexCrossAlign(el, item),
                    InnerPaddingPt = 0,   // the item's own padding, as CSS has it
                };

                Style itemStyle = DeriveStyle(st, item, null, false);
                bool stretches = !isColumn && FlexItemStretches(el, item);
                // A stretched item needs a box to stretch — a text-only one would be a paragraph,
                // whose background ends with its text (MapContainer).
                IElement? outerStretch = _stretchItem;
                if (stretches) _stretchItem = item;
                MapBlock(item, sec, cell.Blocks, listLevel, depth + 1, itemStyle, align);
                _stretchItem = outerStretch;

                // An item that produced nothing still occupies its track: dropping the cell would
                // shift every following item into the wrong column.
                EnsureNotEmpty(cell);
                if (stretches) StretchItemBox(cell);

                double w = ResolveFlexTrack(item, contentWidth, out bool fixedBasis);
                double basis = FlexBasisPt(item, contentWidth), grow = ResolveFlexGrow(item);
                // An auto item's content width (§9.2): measured, as a float's is — its paragraphs,
                // boxes, lists and tables; unknown for a shape the layout cannot measure.
                double? measured = MeasureFlexItem(cell, contentWidth);
                // An auto item's basis is that width: for a non-growing item (an auto item of any
                // other shape keeps 0, and the table widens it to its longest word), and in a
                // wrapping container for a growing one too — a `flex: auto` card on a wrapped line
                // grows from its content, as CSS has it (§9.2), not from nothing.
                if (basis <= 0 && measured is double m && (grow <= 0 || wrapping)) basis = m;
                var fi = new FlexItem
                {
                    Cell = cell, Element = item, Track = w, FixedBasis = fixedBasis, Basis = basis, Grow = grow,
                    Shrink = ResolveFlexShrink(item),
                    Hypothetical = FlexHypotheticalMainSize(item, cell, contentWidth, measured),
                };
                // A line broken on its items' sizes must place them at those sizes, or the break
                // means nothing: on a wrapping container a measured, non-growing auto item is a
                // fixed track of its content width, where a nowrap row still lets the table share
                // the width out.
                if (wrapping && fi.Track <= 0 && grow <= 0 && measured is double mw && mw > MinFlexBasisPt)
                {
                    fi.Track = mw;
                    fi.FixedBasis = true;
                }
                flexItems.Add(fi);
            }

            if (flexItems.Count == 0) return false;

            double gap = ReadGridGap(el, "column-gap", "grid-column-gap", index: 1, st.Size);

            // `flex-wrap` (§9.3 step 5): the items collected into lines, each line holding as many
            // consecutive items as fit its hypothetical main sizes and the gaps between them, at
            // least one; `wrap-reverse` stacks the lines from the bottom (§5.2). An item whose
            // size cannot be known — no stated width or basis, and a block the layout cannot
            // measure (only a table nested past the layout's cap, which the draw drops) — leaves
            // nothing to break on: one line, reported.
            var lines = new List<List<FlexItem>> { flexItems };
            if (wrapping)
            {
                FlexItem? unmeasured = flexItems.Find(f => f.Hypothetical == null);
                if (unmeasured != null)
                    ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                        "flex-wrap: " + (wrap == FlexWrap.Wrap ? "wrap" : "wrap-reverse") + " is approximated; an item (<" +
                        unmeasured.Element.LocalName + ">) has no measurable width — no stated width or basis, and " +
                        "content this engine cannot measure (tables nested past the layout's cap, or a block of another " +
                        "kind) — so the items were laid out on one line.");
                else
                {
                    lines = BreakFlexLines(flexItems, contentWidth, gap);
                    if (wrap == FlexWrap.WrapReverse) lines.Reverse();
                }
            }

            // `row-gap` between the lines (§8 of Box Alignment, as the grid's): the top margin of
            // every cell of a line below the first.
            double rowGap = lines.Count > 1 ? ReadGridGap(el, "row-gap", "grid-row-gap", index: 0, st.Size) : 0;
            string? justify = StyleValue(el, "justify-content")?.Trim();
            bool justifyReported = false, shrinkReported = false;

            var lineTables = new List<M.Table>(lines.Count);
            for (int li = 0; li < lines.Count; li++)
            {
                // A flex container draws no rules of its own here — its box does, round the table.
                // Borders the author put ON the items are still theirs and are applied inside each
                // cell. Through NewBandTable, like every other band — this open-coded the same two
                // properties, which is half a consolidation.
                var table = NewBandTable();

                // `direction: rtl` on the container mirrors the main axis, which for a table is the
                // column order -- the same knob a bidi table uses, so the two cannot disagree.
                ApplyBandDirection(table, st, sec);

                var row = new M.TableRow();
                // A least height: the row still grows to its items. Several lines would share it
                // as CSS's `align-content: stretch` shares the free cross space, which needs each
                // line's height; the box keeps its own least height either way.
                if (rowMin > 0 && lines.Count == 1 && wrap == FlexWrap.NoWrap) row.HeightPt = rowMin;

                List<FlexItem> line = lines[li];
                if (reverse) line = Enumerable.Reverse(line).ToList();
                var widths = new List<double>(line.Count);
                var bases = new List<double>(line.Count);
                var grows = new List<double>(line.Count);
                var shrinks = new List<double>(line.Count);
                bool anyExplicit = false, everyBasisFixed = true;
                foreach (FlexItem fi in line)
                {
                    row.Cells.Add(fi.Cell);
                    if (fi.Track > MinFlexBasisPt) anyExplicit = true;
                    if (!fi.FixedBasis) everyBasisFixed = false;
                    widths.Add(fi.Track);
                    bases.Add(fi.Basis);
                    grows.Add(fi.Grow);
                    shrinks.Add(fi.Shrink);
                }

                // Auto items of measured width beside nothing that grows: when every
                // item's basis is known — stated, or an auto item's measured content width — and the
                // bases and the gaps fit the line, the items keep those widths (§9.7: nothing grows,
                // nothing shrinks) and the free space is `justify-content`'s to place, at the end for
                // flex-start. The table stretched such items across the width before; a wrapping
                // container's lines already kept them. A line the bases overflow keeps the
                // table's share-out, which the shrink step below refines.
                double fitGaps = row.Cells.Count > 1 ? gap * (row.Cells.Count - 1) : 0;
                if (!everyBasisFixed && grows.TrueForAll(g => g <= 0) && bases.TrueForAll(b => b > MinFlexBasisPt)
                    && bases.Sum() + fitGaps <= contentWidth + FloatLineFitTolerance)
                {
                    for (int i = 0; i < widths.Count; i++) widths[i] = bases[i];
                    anyExplicit = true;
                    everyBasisFixed = true;
                }

                // Stated bases beside growing items (§9.7): the growers share, by their factors, what
                // the bases and gaps leave — in points, per line. A grow factor is a ratio, and it was
                // carried as a share of the width to be normalised with the bases, a length: a 32%
                // sidebar beside a `flex: 1` column took three quarters of the row. A basis is stated,
                // or an auto paragraph's measured width; an auto item of any other shape keeps 0, and
                // the table widens it to its longest word. Only when the bases fit: when they overflow
                // nothing grows, and the table's proportional shrink is CSS's.
                double lineGaps = row.Cells.Count > 1 ? gap * (row.Cells.Count - 1) : 0;
                if (grows.Exists(g => g > 0) && bases.Exists(b => b > 0))
                {
                    double left = contentWidth - bases.Sum() - lineGaps;
                    if (left > 0)
                    {
                        double sumGrow = grows.Sum();
                        for (int i = 0; i < widths.Count; i++)
                            widths[i] = bases[i] + (grows[i] > 0 ? left * grows[i] / sumGrow : 0);
                    }
                }

                // `flex-shrink` (§9.7): when the bases and the gaps overflow the line, the
                // negative free space is shared by each item's shrink factor weighted by its basis —
                // `flex-shrink: 0` keeps its basis, a factor of 2 gives up twice a factor of 1's share
                // — and an item with no stated basis keeps its track (the table widens it to its
                // longest word). With every factor 1 this is the table's own proportional shrink. Every factor 0 cannot be honoured — CSS
                // lets the items overflow the container — so the bases stand and the table shrinks them
                // in proportion, reported once per container.
                if (bases.Exists(b => b > 0))
                {
                    double overflow = contentWidth - bases.Sum() - lineGaps;
                    if (overflow < 0)
                    {
                        double weight = 0;
                        for (int i = 0; i < bases.Count; i++) weight += bases[i] > 0 ? shrinks[i] * bases[i] : 0;
                        if (weight > 0)
                        {
                            for (int i = 0; i < widths.Count; i++)
                                if (bases[i] > 0) widths[i] = Math.Max(0, bases[i] + overflow * shrinks[i] * bases[i] / weight);
                        }
                        else if (!shrinkReported)
                        {
                            shrinkReported = true;
                            ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                                "flex-shrink: 0 on every item of a line whose bases overflow it is approximated; CSS lets " +
                                "the items overflow the container, and here the table shrank them in proportion to their bases.");
                        }
                    }
                }

                // `gap` (CSS Box Alignment §8): half on each facing side of two items, mirrored in a
                // right-to-left row, as a multi-column block's is. The margins are points, so the tracks
                // must be too: proportional ones — no stated width (equal), or `flex-grow` shares, which
                // the table would scale to the width — become their share of the width the gaps leave,
                // and only then take their margins; fixed ones keep their widths, the gaps coming out of
                // the free space.
                if (gap > 0 && row.Cells.Count > 1)
                {
                    int n = row.Cells.Count;
                    if (!anyExplicit)
                    {
                        for (int i = 0; i < n; i++) widths[i] = 1;
                        anyExplicit = true;
                        everyBasisFixed = false;
                    }
                    if (!everyBasisFixed)
                    {
                        double sum = widths.Sum(), roomLeft = Math.Max(1, contentWidth - gap * (n - 1));
                        for (int i = 0; i < n; i++) widths[i] = sum > 0 ? widths[i] * roomLeft / sum : roomLeft / n;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        double toNext = i < n - 1 ? gap / 2 : 0, toPrev = i > 0 ? gap / 2 : 0;
                        var cell = row.Cells[i];
                        if (table.RtlColumns) { cell.MarginLeft = toNext; cell.MarginRight = toPrev; }
                        else { cell.MarginRight = toNext; cell.MarginLeft = toPrev; }
                        widths[i] += toNext + toPrev;
                    }
                }

                // `justify-content` (§8.2): where the line's free space goes when every item has a fixed
                // width and none grows — the one case with free space to place. The table normalises an
                // all-explicit column set to fill its width, so the space is given to empty filler cells
                // of the widths the value asks for, as a float grid keeps a short line's widths; a
                // reversed row's main-start is its right edge, so the gaps are mirrored.
                double free = contentWidth - widths.Sum();
                double[]? gaps = everyBasisFixed && free > FloatLineFitTolerance ? JustifyGaps(justify, row.Cells.Count, free, reverse) : null;
                // Flex-start, left to right, puts all the free space after the items: that filler is an
                // auto column (no stated width), which takes whatever the table has left — the page's
                // free space here, and nothing when the table is fitted into a measured cell (a nav list
                // in a flex header, a float) — where a filler sized against the page's width squeezed
                // the items in the cell. The other values place their space on each side of the items,
                // which only a stated width does.
                bool autoFiller = gaps != null && (justify == null || IsFlexStart(justify)) && !reverse;
                if (gaps != null)
                {
                    var cells = row.Cells.ToList();
                    row.Cells.Clear();
                    var spaced = new List<double>(gaps.Length + widths.Count);
                    for (int i = 0; i <= cells.Count; i++)
                    {
                        if (gaps[i] > FloatLineFitTolerance)
                        {
                            row.Cells.Add(NewFillerCell());
                            spaced.Add(autoFiller ? 0 : gaps[i]);
                        }
                        if (i < cells.Count) { row.Cells.Add(cells[i]); spaced.Add(widths[i]); }
                    }
                    widths = spaced;
                }
                else if (justify != null && !IsFlexStart(justify) && !justifyReported)
                {
                    justifyReported = true;   // once per container, not per line
                    ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                        "justify-content: " + justify + " is approximated; the items were distributed across the " +
                        "full width — " + (everyBasisFixed ? "the value is not one this engine places" : "an item grows, has no measurable width, or the items overflow the line, so there is no free space to place") + ".");
                }

                if (li > 0 && rowGap > 0)
                    foreach (M.TableCell cell in row.Cells) cell.MarginTop = rowGap;
                AlignBaselines(el, line, contentWidth);

                table.Rows.Add(row);

                // Left empty when no item asked for a width: the model reads "no explicit widths" as
                // equal tracks, which is what `flex: 1` on everything means anyway.
                if (anyExplicit)
                    foreach (double w in widths) table.ColumnWidths.Add(w);

                sink.Add(table);
                lineTables.Add(table);
            }
            // `align-content` (§8.4): a multi-line container with a definite height shares its free
            // cross space among its lines; a single-line (nowrap) container's one line is stretched above.
            if (wrap != FlexWrap.NoWrap && rowMin > 0) AlignFlexLines(el, lineTables, rowMin, rowGap, contentWidth, wrap == FlexWrap.WrapReverse);
            return true;
        }

        /// <summary>
        /// The items collected into flex lines (§9.3 step 5): each line takes consecutive items
        /// while their hypothetical main sizes, with a <paramref name="gap"/> between each pair,
        /// fit <paramref name="contentWidth"/> (to the line-fit tolerance); an item that does not
        /// fit starts the next line, and a line holds at least one item however wide.
        /// </summary>
        private static List<List<FlexItem>> BreakFlexLines(List<FlexItem> items, double contentWidth, double gap)
        {
            var lines = new List<List<FlexItem>>();
            var line = new List<FlexItem>();
            double used = 0;
            foreach (FlexItem item in items)
            {
                double size = item.Hypothetical ?? 0;
                if (line.Count > 0 && used + gap + size > contentWidth + FloatLineFitTolerance)
                {
                    lines.Add(line);
                    line = new List<FlexItem>();
                    used = 0;
                }
                used += (line.Count > 0 ? gap : 0) + size;
                line.Add(item);
            }
            lines.Add(line);
            return lines;
        }

        /// <summary>
        /// The item's hypothetical main size (§9.3 step 3), the size a line is broken on: its
        /// stated basis — <c>flex-basis</c>, the <c>flex</c> shorthand's, else <c>width</c> — resolved
        /// against <paramref name="contentWidth"/> and clamped up to the item's minimum main size
        /// (<see cref="FlexMinMainSize"/>: a stated <c>min-width</c>, else its min-content width, the
        /// longest word — so a zero basis, <c>flex: 1</c>, is the longest word, not 0, since batch
        /// 107), else <paramref name="measured"/>, the content width of its blocks, else <c>null</c>:
        /// nothing to break on.
        /// </summary>
        private double? FlexHypotheticalMainSize(IElement item, M.TableCell cell, double contentWidth, double? measured)
        {
            string? basis = StatedFlexBasis(item);
            if (basis != null) return Math.Max(Math.Max(0, ResolveWidth(basis, contentWidth, 0)), FlexMinMainSize(item, cell, contentWidth));
            return measured;
        }

        /// <summary>
        /// The item's minimum main size (CSS Flexbox §4.5): a stated <c>min-width</c> in points, else
        /// — <c>auto</c>, the initial value — its min-content width, the widest unbreakable piece of its
        /// blocks, measured by the layout that will draw it; 0 for a shape this cannot measure.
        /// </summary>
        private double FlexMinMainSize(IElement item, M.TableCell cell, double contentWidth)
        {
            string? stated = StyleValue(item, "min-width");
            if (stated != null && !Eq(stated.Trim(), "auto"))
                return Math.Max(0, ResolveWidth(stated, contentWidth, 0));
            return MeasureBlocks(cell.Blocks, 0, cell.Blocks.Count, contentWidth, minContent: true) ?? 0;
        }

        /// <summary>
        /// The basis the item states, as written: <c>flex-basis</c>, else the <c>flex</c> shorthand's
        /// — which is <c>0</c> when it gives a grow factor and no basis (§7.1.1: <c>flex: 1</c> is
        /// <c>1 1 0</c>) — and only for an <c>auto</c> basis the item's <c>width</c> (§7.2.3);
        /// <c>null</c> for an auto basis with no width. The one reading, for the track and the
        /// hypothetical size alike.
        /// </summary>
        private string? StatedFlexBasis(IElement item)
        {
            string? basis = StyleValue(item, "flex-basis") ?? FlexShorthand(item).Basis;
            if (basis == null || IsAutoBasis(basis)) basis = StyleValue(item, "width");
            return string.IsNullOrEmpty(basis) || IsAutoBasis(basis!) ? null : basis;
        }

        /// <summary>
        /// An auto item's content width, measured by the layout that will draw it
        /// (<see cref="PdfExporter.MeasureShrinkToFitWidth(M.Paragraph, double)"/>): the widest of its blocks
        /// (<see cref="MeasureBlocks"/>) — paragraphs, boxes of them with their horizontal margins,
        /// padding and borders, or their stated width, lists and tables — else <c>null</c>, a shape
        /// this cannot measure (tables nested past the layout's cap).
        /// </summary>
        private double? MeasureFlexItem(M.TableCell cell, double contentWidth)
            => MeasureBlocks(cell.Blocks, 0, cell.Blocks.Count, contentWidth, minContent: false);

        /// <summary>
        /// The widest of the blocks <paramref name="from"/> (inclusive) to <paramref name="to"/>
        /// (exclusive) in a band <paramref name="available"/> wide: a paragraph's shrink-to-fit width,
        /// or its min-content width when <paramref name="minContent"/>; a box's stated width plus its
        /// margins, or its blocks' width plus its margins, padding and borders; a list's or a table's
        /// width by the layout that draws it (<see cref="PdfExporter.MeasureShrinkToFitWidth(M.ListBlock, double)"/>,
        /// <see cref="PdfExporter.MeasureShrinkToFitWidth(M.Table, double)"/>); 0 for no blocks;
        /// <c>null</c> when a block is of a kind this cannot measure, or a table's width cannot be known.
        /// </summary>
        private double? MeasureBlocks(List<M.IDocElement> blocks, int from, int to, double available, bool minContent)
        {
            double widest = 0;
            for (int i = from; i < to; i++)
            {
                switch (blocks[i])
                {
                    case M.Paragraph para:
                        widest = Math.Max(widest, minContent
                            ? Measurer.MeasureMinContentWidth(para, available)
                            : Measurer.MeasureShrinkToFitWidth(para, available));
                        break;
                    case M.BoxStart box:
                    {
                        int end = M.BoxStart.IndexOfEnd(blocks, i, to);
                        double margins = box.MarginLeft + box.MarginRight;
                        double edges = margins + box.PaddingLeft + box.PaddingRight + M.Border.PaintedWidth(box.BorderLeft) + M.Border.PaintedWidth(box.BorderRight);
                        double width;
                        if (box.WidthFraction != null || box.WidthPt > 0)
                            width = (box.WidthFraction ?? 0) * available + box.WidthPt + margins;
                        else
                        {
                            double? inner = MeasureBlocks(blocks, i + 1, end, Math.Max(1, available - edges), minContent);
                            if (inner == null) return null;
                            width = inner.Value + edges;
                        }
                        widest = Math.Max(widest, width);
                        i = end;   // the BoxEnd, stepped over by the loop
                        break;
                    }
                    case M.BoxEnd:
                        break;     // an unmatched close: nothing to measure
                    // A list at its items' text starts, a table at its columns — measured by the
                    // layout that draws them.
                    case M.ListBlock list:
                        widest = Math.Max(widest, minContent
                            ? Measurer.MeasureMinContentWidth(list, available)
                            : Measurer.MeasureShrinkToFitWidth(list, available));
                        break;
                    case M.Table table:
                    {
                        double? width = minContent
                            ? Measurer.MeasureMinContentWidth(table, available)
                            : Measurer.MeasureShrinkToFitWidth(table, available);
                        if (width == null) return null;   // tables nested past the layout's cap
                        widest = Math.Max(widest, width.Value);
                        break;
                    }
                    default:
                        return null;
                }
            }
            return widest;
        }

        /// <summary>The item's <c>order</c> (§5.4.1): an integer, else 0, its initial value.</summary>
        private int FlexOrder(IElement item)
            => StyleValue(item, "order") is string v
               && int.TryParse(v.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int order) ? order : 0;

        /// <summary>The <c>justify-content</c> values that put every gap at the line's end — the layout a table row gives anyway.</summary>
        private static bool IsFlexStart(string justify)
            => Eq(justify, "flex-start") || Eq(justify, "start") || Eq(justify, "normal") || Eq(justify, "stretch");

        /// <summary>
        /// The <paramref name="count"/> + 1 gaps — before the first item, between each pair, after the
        /// last — that <paramref name="justify"/> makes of <paramref name="free"/> space (§8.2):
        /// <c>flex-start</c> puts it all after, <c>flex-end</c> all before, <c>center</c> half at each
        /// end, <c>space-between</c> equal shares between (all after, for one item), <c>space-around</c>
        /// a share round each item (a half at each end), <c>space-evenly</c> equal shares everywhere.
        /// A <paramref name="reversed"/> row's main-start is its right edge, so its gaps are
        /// mirrored. <c>null</c> for a value this engine does not place.
        /// </summary>
        private static double[]? JustifyGaps(string? justify, int count, double free, bool reversed)
        {
            var gaps = new double[count + 1];
            string v = justify ?? "flex-start";
            if (IsFlexStart(v)) gaps[count] = free;
            else if (Eq(v, "flex-end") || Eq(v, "end")) gaps[0] = free;
            else if (Eq(v, "center")) { gaps[0] = free / 2; gaps[count] = free / 2; }
            else if (Eq(v, "space-between"))
            {
                if (count == 1) gaps[count] = free;
                else for (int i = 1; i < count; i++) gaps[i] = free / (count - 1);
            }
            else if (Eq(v, "space-around"))
            {
                double share = free / count;
                gaps[0] = gaps[count] = share / 2;
                for (int i = 1; i < count; i++) gaps[i] = share;
            }
            else if (Eq(v, "space-evenly"))
                for (int i = 0; i <= count; i++) gaps[i] = free / (count + 1);
            else return null;
            if (reversed) Array.Reverse(gaps);
            return gaps;
        }

        /// <summary>
        /// The width track for one flex item, in points, or <c>0</c> for auto.
        /// </summary>
        /// <remarks>
        /// <c>width</c> and <c>flex-basis</c> are absolute and win. A bare <c>flex-grow</c> is a
        /// <i>ratio</i>, not a length, so it is carried as a proportional share of the content
        /// width — which is what the model's <c>ColumnWidths</c> distributes.
        /// </remarks>
        /// <param name="item">The flex item.</param>
        /// <param name="contentWidth">The container's width, for a percentage basis.</param>
        /// <param name="fixedBasis">
        /// True when the track is a fixed width the item will not grow past — a <c>width</c> or
        /// <c>flex-basis</c> with no <c>flex-grow</c> — so the line's free space is free indeed.
        /// </param>
        private double ResolveFlexTrack(IElement item, double contentWidth, out bool fixedBasis)
        {
            double grow = ResolveFlexGrow(item);
            fixedBasis = false;

            double w = FlexBasisPt(item, contentWidth);
            if (w > 0)
            {
                fixedBasis = grow <= 0;
                return w;
            }

            if (grow > 0 && contentWidth > 0)
            {
                // Proportional: the caller normalises by summing the tracks, so a raw share is
                // enough and avoids needing every sibling's value here.
                return grow * contentWidth / 100.0;
            }

            return 0;
        }

        /// <summary>
        /// The item's stated basis (<see cref="StatedFlexBasis"/>) in points, or 0 for none (auto,
        /// or too small to be one).
        /// </summary>
        private double FlexBasisPt(IElement item, double contentWidth)
        {
            string? basis = StatedFlexBasis(item);
            if (basis == null) return 0;
            double w = ResolveWidth(basis, contentWidth, 0);
            return w > MinFlexBasisPt ? w : 0;
        }

        /// <summary><c>auto</c>, or <c>content</c> — a basis this mapping takes as auto.</summary>
        private static bool IsAutoBasis(string basis) => Eq(basis.Trim(), "auto") || Eq(basis.Trim(), "content");

        /// <summary>
        /// The largest <c>flex-grow</c> or <c>flex-shrink</c> factor used. A factor scales the free
        /// space into a track width, so an enormous one — <c>flex-grow:1e306</c>, finite but vast —
        /// overflows the track to infinity, which the Builder model refuses at export with an exception
        /// naming an argument the caller never passed. A real factor is a small number; above this it is
        /// clamped, and factors this large share the space equally, which is what they would anyway.
        /// </summary>
        private const double MaxFlexFactor = 1_000_000;

        /// <summary>The <c>flex-grow</c> factor, read from <c>flex-grow</c> or the <c>flex</c> shorthand.</summary>
        private double ResolveFlexGrow(IElement item)
        {
            string? g = StyleValue(item, "flex-grow");
            // An invalid value is dropped (CSS Flexbox §7.1.4: a number, a negative one invalid); a huge
            // one is clamped so the track it scales stays finite.
            if (g != null &&
                double.TryParse(g.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double gv)
                && !double.IsNaN(gv) && !double.IsInfinity(gv) && gv >= 0)
                return Math.Min(gv, MaxFlexFactor);
            return Math.Min(FlexShorthand(item).Grow, MaxFlexFactor);
        }

        /// <summary>The <c>flex-shrink</c> factor (§7.1.4), from <c>flex-shrink</c> or the <c>flex</c> shorthand; 1 unless stated, never negative.</summary>
        private double ResolveFlexShrink(IElement item)
        {
            string? s = StyleValue(item, "flex-shrink");
            if (s != null &&
                double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double sv)
                && !double.IsNaN(sv) && !double.IsInfinity(sv))
                return Math.Min(Math.Max(0, sv), MaxFlexFactor);
            return Math.Min(FlexShorthand(item).Shrink, MaxFlexFactor);
        }

        /// <summary>
        /// The <c>flex</c> shorthand (CSS Flexbox §7.1.1) as the grow and shrink factors and the basis
        /// it sets: <c>none</c> is <c>0 0 auto</c>, <c>auto</c> <c>1 1 auto</c>, <c>initial</c>
        /// <c>0 1 auto</c>; otherwise up to two numbers — grow, then shrink — and a basis in either
        /// order, a missing grow being 1, a missing shrink 1, and a missing basis 0 when a number was
        /// given. No shorthand, or one that does not parse, sets nothing: grow 0, shrink 1 and no basis.
        /// </summary>
        private (double Grow, double Shrink, string? Basis) FlexShorthand(IElement item)
        {
            string? shorthand = StyleValue(item, "flex");
            if (shorthand == null) return (0, 1, null);
            string[] parts = shorthand.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && Eq(parts[0], "none")) return (0, 0, "auto");
            if (parts.Length == 1 && Eq(parts[0], "auto")) return (1, 1, "auto");
            if (parts.Length == 1 && Eq(parts[0], "initial")) return (0, 1, "auto");

            double? grow = null, shrink = null;
            string? basis = null;
            int numbers = 0;
            foreach (string part in parts)
            {
                // Two numbers at most are the factors — grow, then shrink; a
                // third token that is a number is a unitless zero basis, as in `flex: 1 1 0`. A
                // non-finite or negative factor is not a valid number here (it would make a track
                // infinite), so it falls through to the basis slot.
                if (numbers < 2 && double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)
                    && !double.IsNaN(n) && !double.IsInfinity(n) && n >= 0)
                {
                    if (numbers == 0) grow = n; else shrink = n;
                    numbers++;
                }
                else basis ??= part;
            }
            if (parts.Length > 3) return (0, 1, null);
            return (grow ?? 1, shrink ?? 1, basis ?? (grow != null ? "0" : null));
        }

        /// <summary>
        /// <c>align-items</c> / <c>align-self</c> on the cross axis, which for a flex row is
        /// vertical — so it maps exactly onto a table cell's vertical alignment.
        /// </summary>
        private M.CellVerticalAlign ResolveFlexCrossAlign(IElement container, IElement item)
        {
            string? a = StyleValue(item, "align-self") ?? StyleValue(container, "align-items");
            if (a == null) return M.CellVerticalAlign.Top;

            string v = a.Trim();
            if (Eq(v, "center")) return M.CellVerticalAlign.Center;
            if (Eq(v, "flex-end") || Eq(v, "end")) return M.CellVerticalAlign.Bottom;
            // `stretch` (the initial value) sits at the top: a table cell already stretches to the row
            // height. `baseline` sits at the top too, moved down by its baseline's offset from the
            // line's lowest (AlignBaselines).
            return M.CellVerticalAlign.Top;
        }

        /// <summary>
        /// <c>align-content</c> (Flexbox §8.4) for the line tables of a wrapping container whose least height
        /// is <paramref name="height"/>: the cross space the lines and row gaps leave is given as the value
        /// says — <c>stretch</c>/<c>normal</c> to every line's height, <c>flex-start</c> to nothing,
        /// <c>flex-end</c>, <c>center</c> and the <c>space-*</c> values above the lines (a cell's top margin,
        /// as the row gap is). <c>wrap-reverse</c> swaps the start and the end, its lines being stacked from
        /// the bottom. A value this does not place is reported, the lines left at the start.
        /// </summary>
        private void AlignFlexLines(IElement el, List<M.Table> lines, double height, double rowGap, double width, bool reversed)
        {
            if (lines.Count == 0) return;
            var heights = lines.ConvertAll(t => Measurer.MeasureHeight(new List<M.IDocElement> { t }, width));
            double free = height - heights.Sum();
            if (free <= FloatLineFitTolerance) return;

            string v = StyleValue(el, "align-content")?.Trim() ?? "normal";
            int n = lines.Count;
            if (Eq(v, "normal") || Eq(v, "stretch"))
            {
                for (int i = 0; i < n; i++) lines[i].Rows[0].HeightPt = heights[i] + free / n;
                return;
            }
            bool start = Eq(v, "flex-start") || Eq(v, "start"), end = Eq(v, "flex-end") || Eq(v, "end");
            if (reversed && (start || end)) (start, end) = (end, start);
            var above = new double[n];
            if (start) return;
            if (end) above[0] = free;
            else if (Eq(v, "center")) above[0] = free / 2;
            else if (Eq(v, "space-between")) { for (int i = 1; i < n; i++) above[i] = free / (n - 1); }
            else if (Eq(v, "space-around")) { above[0] = free / (2 * n); for (int i = 1; i < n; i++) above[i] = free / n; }
            else if (Eq(v, "space-evenly")) { for (int i = 0; i < n; i++) above[i] = free / (n + 1); }
            else
            {
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "align-content: " + v + " is approximated; the lines are packed at the start.");
                return;
            }
            for (int i = 0; i < n; i++)
                if (above[i] > 0)
                    foreach (M.TableCell cell in lines[i].Rows[0].Cells) cell.MarginTop += above[i];
        }

        /// <summary>
        /// <c>align-items</c>/<c>align-self: baseline</c> (§8.3) across one flex line: each such item's first
        /// baseline — its first paragraph's, directly or inside its box (its padding and border above it) —
        /// measured, and the item moved down by its distance from the line's lowest, as a cell's top
        /// margin. An item with no line of its own takes the baseline its border box synthesizes (its bottom
        /// edge, §8.5); one whose content starts with a table or a list stays at the top, reported.
        /// </summary>
        private void AlignBaselines(IElement container, List<FlexItem> line, double contentWidth)
        {
            var offsets = new List<(M.TableCell Cell, double Offset)>();
            foreach (FlexItem fi in line)
            {
                if (!FlexItemOnBaseline(container, fi.Element)) continue;
                double width = fi.FixedBasis && fi.Track > MinFlexBasisPt ? fi.Track : fi.Hypothetical ?? contentWidth / line.Count;
                double? baseline = FirstBaselineOf(fi.Cell, width);
                if (baseline == null)
                {
                    ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                        "align-items: baseline is approximated for <" + fi.Element.LocalName + ">: its content opens with a floated box, after which the first baseline is not measured, so it is aligned at the top.");
                    continue;
                }
                offsets.Add((fi.Cell, fi.Cell.MarginTop + baseline.Value));
            }
            if (offsets.Count < 2) return;
            double lowest = offsets.Max(o => o.Offset);
            foreach (var (cell, offset) in offsets) cell.MarginTop += lowest - offset;
        }

        /// <summary>Whether the item's cross alignment is <c>baseline</c> (or <c>first baseline</c>): its <c>align-self</c>, else the container's <c>align-items</c>.</summary>
        private bool FlexItemOnBaseline(IElement container, IElement item)
        {
            string? self = StyleValue(item, "align-self")?.Trim();
            string? a = self == null || Eq(self, "auto") ? StyleValue(container, "align-items")?.Trim() : self;
            return a != null && (Eq(a, "baseline") || Eq(a, "first baseline"));
        }

        /// <summary>
        /// The distance from a cell's top to its first baseline, by the layout that draws it
        /// (<see cref="PdfExporter.MeasureFirstBaseline(IReadOnlyList{M.IDocElement}, double)"/>): its first
        /// paragraph's, list's or table's, under the top margin, border and padding of every box it opens
        /// inside. An item with no line to give one — empty, or boxes holding nothing — has one synthesized
        /// from its border box (Flexbox §8.5; CSS Box Alignment §9.1): the box's bottom edge. Null for
        /// content whose first baseline is not measured: content opening with a floated box.
        /// </summary>
        private double? FirstBaselineOf(M.TableCell cell, double width)
        {
            int i = 0;
            while (i < cell.Blocks.Count && cell.Blocks[i] is M.BoxStart open && !(open.Float != M.FloatSide.None && open.FloatWidth > 0)) i++;
            if (i < cell.Blocks.Count && cell.Blocks[i] is not M.BoxEnd)
                return Measurer.MeasureFirstBaseline(cell.Blocks, width);

            // Nothing with a baseline: the border box's bottom — the item's height less its outer box's bottom margin.
            double height = Measurer.MeasureHeight(cell.Blocks, width);
            return cell.Blocks.Count > 0 && cell.Blocks[0] is M.BoxStart outer ? height - outer.MarginBottom : height;
        }

        /// <summary>
        /// Whether a row's item stretches to the row's height: <c>align-self</c>, else the
        /// container's <c>align-items</c>, is <c>stretch</c> or <c>normal</c> — the initial value, so
        /// also when neither says.
        /// </summary>
        private bool FlexItemStretches(IElement container, IElement item)
        {
            string? self = StyleValue(item, "align-self")?.Trim();
            string? a = self == null || Eq(self, "auto") ? StyleValue(container, "align-items")?.Trim() : self;
            return a == null || Eq(a, "stretch") || Eq(a, "normal");
        }

        /// <summary>
        /// Marks a stretched item's box to reach its cell's bottom (<c>BoxStart.StretchesToCell</c>)
        /// when the box holds the whole cell — its <c>BoxStart</c> first and its matching
        /// <c>BoxEnd</c> last — so a sidebar's background runs to the bottom of the row, as the
        /// cell does.
        /// </summary>
        private static void StretchItemBox(M.TableCell cell)
        {
            if (cell.Blocks.Count < 2 || cell.Blocks[0] is not M.BoxStart box) return;
            int depth = 0;
            for (int i = 0; i < cell.Blocks.Count; i++)
            {
                if (cell.Blocks[i] is M.BoxStart) depth++;
                else if (cell.Blocks[i] is M.BoxEnd && --depth == 0)
                {
                    if (i == cell.Blocks.Count - 1) box.StretchesToCell = true;
                    return;
                }
            }
        }
    }
}
