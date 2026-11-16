// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// <c>float</c> / <c>clear</c> — a floated element and the content beside it become one
    /// side-by-side band, ended by <c>clear</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>What this is, precisely.</b> A run of <c>float:left</c> (or <c>right</c>) siblings
    /// plus the non-floated siblings that follow become a borderless one-row table: the floats in
    /// their own cells on the float side, the following content in a cell beside them. That gives
    /// the correct result for what business HTML floats — a logo beside an address block, an image
    /// beside a caption, a sidebar beside body copy.</para>
    /// <para><b>Exact when it can be, a band when it cannot.</b> Builder now has real floats —
    /// <c>Paragraph.Float</c> plus per-line exclusion rectangles in the exporter — so a float whose
    /// content is one paragraph and whose width is known is emitted as a genuine float: text flows
    /// down its side and <i>returns to the full measure</i> past its bottom, which is what CSS
    /// does and what a table cell never could.</para>
    ///
    /// <para><b>A float with no declared width shrinks to its content</b>, as CSS says: the mapped
    /// paragraph is measured by <see cref="PdfExporter.MeasureShrinkToFitWidth(M.Paragraph, double)"/> — the layout that
    /// will draw it, with the fonts it will draw it with — and that measure becomes its
    /// <c>Paragraph.FloatWidth</c>. The shrink-to-fit pass this mapper "did not run" is one call.</para>
    ///
    /// <para><b>The band survives for what the exact path cannot express.</b> A float whose element
    /// maps to a table, a list or several paragraphs has nothing to hang <c>Paragraph.Float</c> on,
    /// and a paragraph with nothing measurable has no width to reserve. Those still become a
    /// borderless one-row table, still keep the text in a fixed column for its whole length, and
    /// still report <see cref="BuilderDiagnosticKind.ContentApproximated"/> — which fires only when
    /// the approximation is real, rather than on every float.</para>
    /// <para><b><c>clear</c> ends a band</b> and is therefore honoured exactly: the cleared element
    /// and everything after it resume ordinary full-width flow, which is what <c>clear</c> means.</para>
    /// </remarks>
    internal sealed partial class HtmlToBuilderMapper
    {
        private enum FloatSide { None, Left, Right }

        /// <summary>
        /// If <paramref name="nodes"/> at <paramref name="i"/> starts a float band, maps the whole
        /// band and advances <paramref name="i"/> past it.
        /// </summary>
        private bool TryMapFloatBand(
            List<INode> nodes, ref int i, M.Section sec, List<M.IDocElement> sink,
            int listLevel, int depth, Style inherited, M.TextAlignment? align)
        {
            if (nodes[i] is not IElement first) return false;

            FloatSide side = ResolveFloatSide(first);
            if (side == FloatSide.None) return false;

            // ---- the floats themselves: consecutive floated siblings -------------------------
            // Either side: a logo floated left and a nav floated right share one line, which the
            // exact path below can say (a real float per side). Only it can — the band fallback
            // takes the run up to the first change of side, as it always has.
            var floats = new List<IElement> { first };
            var sides = new List<FloatSide> { side };
            int j = i + 1, sameSideCount = 0;
            for (; j < nodes.Count; j++)
            {
                if (nodes[j] is not IElement e) { if (IsIgnorableText(nodes[j])) continue; break; }
                if (IsHidden(e)) continue;
                FloatSide s = ResolveFloatSide(e);
                if (s == FloatSide.None) break;
                if (s != side)
                {
                    if (ClearsFloat(e)) break;   // below the run, not beside it
                    if (sameSideCount == 0) sameSideCount = floats.Count;
                }
                floats.Add(e);
                sides.Add(s);
            }

            // ---- the content that flows beside them, up to a clear -------------------------
            List<IElement> Beside(ref int at)
            {
                var found = new List<IElement>();
                for (; at < nodes.Count; at++)
                {
                    if (nodes[at] is not IElement e)
                    {
                        // Bare text beside a float is common (`<img float:left>Some text`). It
                        // cannot go in a cell on its own, so it ends the band rather than being
                        // dropped -- losing it would be silent content loss, which is worse than a
                        // plain layout.
                        if (IsIgnorableText(nodes[at])) continue;
                        break;
                    }
                    if (IsHidden(e)) continue;
                    if (ClearsFloat(e)) break;                       // `clear` ends the band, per CSS
                    if (ResolveFloatSide(e) != FloatSide.None) break; // a differently-floated run

                    // A clearfix — `::after { clear: both }` — ends the band AFTER the element: its
                    // content still sits beside the floats, and only what it generates at its end
                    // drops below them. An empty one (the grid idiom) has no content to place, so
                    // it is consumed and leaves nothing beside the floats.
                    if (AfterClears(e))
                    {
                        if (!IsEmptyElement(e)) found.Add(e);
                        at++;
                        break;
                    }
                    found.Add(e);
                }
                return found;
            }
            var beside = Beside(ref j);

            if (sameSideCount > 0)
            {
                if (TryMapExactFloat(floats, sides, beside, sec, sink, listLevel, depth, inherited, align))
                {
                    i = j - 1;   // the caller's loop increments
                    return true;
                }

                // Exact mapping of the whole mixed-side run failed. Map it in its same-side segments
                // HERE, in one pass. Reducing the run to its first same-side segment and returning — false,
                // when that segment is a lone float — would make the caller re-enter on the tail and
                // re-attempt the exact map, re-mapping and re-measuring every float over each shrinking
                // tail: O(n^2) on a long run of alternately-sided floats. `beside` followed the whole run,
                // so it attaches to the LAST segment; the segments go into the sink in document order.
                for (int seg = 0; seg < floats.Count; )
                {
                    FloatSide segSide = sides[seg];
                    int segEnd = seg + 1;
                    while (segEnd < floats.Count && sides[segEnd] == segSide) segEnd++;
                    var segFloats = floats.GetRange(seg, segEnd - seg);
                    var segBeside = segEnd == floats.Count ? beside : new List<IElement>();
                    MapSameSideSegment(segFloats, segSide, segBeside, sec, sink, listLevel, depth, inherited, align);
                    seg = segEnd;
                }
                i = j - 1;   // the caller's loop increments
                return true;
            }

            if (beside.Count == 0)
            {
                // A float with nothing beside it is just a block. Emitting a one-cell table would
                // change its width and gain nothing.
                if (floats.Count < 2) return false;

                // A RUN of them is the other case entirely, and the commonest float layout there
                // is: a float-based grid (Bootstrap 3's `.row > .col-*`) is nothing but floats with
                // nothing beside them. Falling back to block flow here stacked every column of such
                // a grid, silently.
                MapFloatRun(floats, side, sec, sink, listLevel, depth, inherited, align);
                i = j - 1;   // the caller's loop increments
                return true;
            }

            if (TryMapExactFloat(floats, sides, beside, sec, sink, listLevel, depth, inherited, align))
            {
                i = j - 1;   // the caller's loop increments
                return true;
            }

            EmitBandTable(floats, side, beside, sec, sink, listLevel, depth, inherited, align);
            i = j - 1;   // the caller's loop increments
            return true;
        }

        /// <summary>
        /// Maps one same-side segment of a float run exactly as the top-level band logic maps a
        /// single-side run: a lone float with nothing beside it is a plain block (its width kept on
        /// its side), a run of them with nothing beside is a <see cref="MapFloatRun"/>, and a segment
        /// with content beside it is the exact float if it can be, else the band table. Used only
        /// from the mixed-side fallback, so processing a whole run in one pass is linear.
        /// </summary>
        private void MapSameSideSegment(
            List<IElement> floats, FloatSide side, List<IElement> beside, M.Section sec,
            List<M.IDocElement> sink, int listLevel, int depth, Style inherited, M.TextAlignment? align)
        {
            if (beside.Count == 0)
            {
                if (floats.Count < 2)
                {
                    // A lone float with nothing beside it is a plain block — exactly what
                    // TryMapFloatBand returning false had the caller do, _loneFloat and all, so the
                    // float keeps its width on its side rather than becoming a one-cell table.
                    _loneFloat = floats[0];
                    MapBlock(floats[0], sec, sink, listLevel, depth + 1, inherited, align);
                }
                else MapFloatRun(floats, side, sec, sink, listLevel, depth, inherited, align);
                return;
            }

            var sides = new List<FloatSide>(floats.Count);
            for (int k = 0; k < floats.Count; k++) sides.Add(side);
            if (TryMapExactFloat(floats, sides, beside, sec, sink, listLevel, depth, inherited, align)) return;
            EmitBandTable(floats, side, beside, sec, sink, listLevel, depth, inherited, align);
        }

        /// <summary>
        /// The borderless one-row table fallback: the floats in their own cells on the float side,
        /// the content that flows beside them in a cell beside them, each float's track its declared
        /// or measured width. Reports <see cref="BuilderDiagnosticKind.ContentApproximated"/> because
        /// a fixed column does not re-flow to the full width below a short float, which CSS does.
        /// </summary>
        private void EmitBandTable(
            List<IElement> floats, FloatSide side, List<IElement> beside, M.Section sec,
            List<M.IDocElement> sink, int listLevel, int depth, Style inherited, M.TextAlignment? align)
        {
            // THE CONTAINER'S STYLE, not the first floated child's. DeriveStyle(inherited, first, …)
            // would leak a declaration on the first float into decisions that belong to the band:
            // the column order for the whole table, and the base style every OTHER float inherits
            // from. A first float carrying dir="ltr" would un-mirror an entire RTL band; one
            // carrying a colour would tint its siblings.
            //
            // TryMapFloatBand has no container element in scope at all -- it receives a node list,
            // an index and the container's already-derived style -- so `inherited` IS the
            // container style here, and `first` was simply the wrong variable in reach.
            Style st = inherited;
            double contentWidth = SectionContentWidth(sec);

            var table = NewBandTable();
            var row = new M.TableRow();
            var widths = new List<double>();

            M.TableCell BesideCell()
            {
                var c = new M.TableCell { VAlign = M.CellVerticalAlign.Top, InnerPaddingPt = 0 };
                foreach (IElement el in beside)
                    MapBlock(el, sec, c.Blocks, listLevel, depth + 1,
                             DeriveStyle(inherited, el, null, false), align);
                EnsureNotEmpty(c);
                return c;
            }

            if (FloatsLeadTheRow(side, BandIsRtl(st, sec)))
            {
                foreach (IElement f in floats)
                {
                    row.Cells.Add(FloatCell(f, sec, listLevel, depth, st, align));
                    widths.Add(FloatTrack(f, contentWidth));
                }
                row.Cells.Add(BesideCell()); widths.Add(0);
            }
            else
            {
                row.Cells.Add(BesideCell()); widths.Add(0);
                for (int k = floats.Count - 1; k >= 0; k--)
                {
                    row.Cells.Add(FloatCell(floats[k], sec, listLevel, depth, st, align));
                    widths.Add(FloatTrack(floats[k], contentWidth));
                }
            }

            table.Rows.Add(row);
            if (widths.Any(w => w > 0))
                foreach (double w in widths) table.ColumnWidths.Add(w);

            ApplyBandDirection(table, st, sec);

            ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                "float: content beside a float was laid out in a fixed column. CSS re-flows it to " +
                "the full width below the float; this does not, so a float shorter than the text " +
                "beside it leaves whitespace under it. Use HtmlRenderEngine.Browser where that " +
                "matters.");

            sink.Add(table);
        }

        // Every float ever handed to the exact mapper in one conversion. The exact map does the real
        // work (map + measure per float), so this is the cost a long float run spends there: O(n)
        // for an alternating run of n floats, where re-attempting the exact map over each shrinking
        // tail would spend O(n^2).
        [ThreadStatic] private static int _floatsExactlyMapped;

        /// <summary>The number of floats handed to the exact mapper in the last conversion.</summary>
        internal static int FloatsExactlyMappedCount => _floatsExactlyMapped;

        /// <summary>
        /// Emits the band as real floats — a <c>Paragraph.Float</c> for an element that maps to one
        /// paragraph, a <c>BoxStart.Float</c> round anything else — and answers
        /// <see langword="false"/> when a float's width cannot be known.
        /// </summary>
        /// <remarks>
        /// <para><b>One condition, about what can be measured.</b> An element that maps to a table, a
        /// list or several blocks floats as a floated box (<c>BoxStart.Float</c>), as a paragraph does
        /// through <c>Paragraph.Float</c>; the band is left for a float whose width nothing says — no declared width and a shape the
        /// measurer cannot size (tables nested past the layout's cap, which the draw drops).</para>
        ///
        /// <para><b>The width is the declared one, else the measured one.</b> CSS shrink-to-fit is
        /// min(max-content, available): the paragraph's widest unwrapped line, capped at the band.
        /// <see cref="PdfExporter.MeasureShrinkToFitWidth(M.Paragraph, double)"/> answers that with the layout that will
        /// draw the float, so the reserved width holds exactly the lines it measured; only a
        /// paragraph with nothing to measure — no text, no image — falls back to the band. A box of
        /// blocks is measured as a flex item is (<see cref="MeasureBlocks"/>): its widest block.</para>
        ///
        /// <para><b>Mapped into a scratch list first, then inspected.</b> The shape a floated
        /// element takes is only known after mapping it, so the decision cannot be made from the
        /// DOM — and mapping straight into <paramref name="sink"/> would leave half a band behind
        /// when the answer turns out to be no. Measuring needs the mapped paragraph too, which is
        /// why the width is settled after the mapping rather than before it.</para>
        ///
        /// <para><b><c>clear</c> becomes <c>Paragraph.ClearFloats</c> on the first block after the
        /// band.</b> The band form gets it for free, because a table structurally ends the
        /// side-by-side region; a real float does not end until its own bottom, so the statement
        /// has to be carried explicitly or a float taller than its neighbour would keep narrowing
        /// the rest of the section.</para>
        /// </remarks>
        private bool TryMapExactFloat(
            List<IElement> floats, List<FloatSide> sides, List<IElement> beside, M.Section sec,
            List<M.IDocElement> sink, int listLevel, int depth, Style inherited, M.TextAlignment? align)
        {
            _floatsExactlyMapped += floats.Count;
            double contentWidth = SectionContentWidth(sec);
            var emitted = new List<M.IDocElement>(floats.Count);

            // The scratch mapping records floated inline elements against paragraphs that go with it
            // when the answer is no; left behind, each would be counted as laid out in its line and
            // reported, though the band maps the element again and lifts it there.
            int recorded = _inlineFloats.Count;

            // It also allocates form-field names and radio-button states through MapBlock below
            // (FieldNameFor / RadioSpec). Those are side effects on the mapper's shared allocation
            // state, so on Abandon they must be rolled back too — otherwise a control inside an
            // abandoned float keeps its claimed name and the band's re-map of the same control gets a
            // different, suffixed one (name="f" would come out "f_2"). Snapshot the allocation state here
            // and restore it on Abandon; a successful map keeps it.
            var savedFieldNames = new HashSet<string>(_fieldNames, StringComparer.Ordinal);
            var savedNextField = new Dictionary<string, int>(_nextFieldSuffix, StringComparer.Ordinal);
            var savedNextRadioValue = new Dictionary<(string, string), int>(_nextRadioValueSuffix);
            var savedRadioGroups = new Dictionary<string, string>(_radioGroups, StringComparer.Ordinal);
            var savedRadioValues = _radioValues.ToDictionary(
                kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            var savedCheckedRadio = new Dictionary<string, M.FormFieldSpec>(_checkedRadio, StringComparer.Ordinal);
            // A new checked radio un-checks an earlier one in its group (RadioSpec), mutating a spec
            // a committed band already placed; record each so it can be set back.
            var savedCheckedStates = _checkedRadio.Values.Select(s => (Spec: s, s.IsChecked)).ToList();

            bool Abandon()
            {
                _inlineFloats.RemoveRange(recorded, _inlineFloats.Count - recorded);
                RestoreSet(_fieldNames, savedFieldNames);
                RestoreMap(_nextFieldSuffix, savedNextField);
                RestoreMap(_nextRadioValueSuffix, savedNextRadioValue);
                RestoreMap(_radioGroups, savedRadioGroups);
                RestoreMap(_radioValues, savedRadioValues);
                RestoreMap(_checkedRadio, savedCheckedRadio);
                foreach (var (spec, wasChecked) in savedCheckedStates) spec.IsChecked = wasChecked;
                return false;
            }

            for (int k = 0; k < floats.Count; k++)
            {
                IElement f = floats[k];
                var blocks = new List<M.IDocElement>();
                MapBlock(f, sec, blocks, listLevel, depth + 1, DeriveStyle(inherited, f, null, false), align);
                M.FloatSide side = sides[k] == FloatSide.Left ? M.FloatSide.Left : M.FloatSide.Right;
                double track = FloatTrack(f, contentWidth);

                if (blocks.Count == 0) return Abandon();
                if (blocks.Count != 1 || blocks[0] is not M.Paragraph paragraph)
                {
                    // Several blocks, a list, a table, a box of blocks: a floated box —
                    // BoxStart.Float on the element's own box when it maps to one (its stated width
                    // is the float's), else a bare box round its blocks — laid out whole at the
                    // float's edge, the blocks after it beside it. Its width is the declared one,
                    // else the blocks' shrink-to-fit width by the layout that draws them (a box's
                    // includes its margins, which the float width leaves outside); a shape nothing
                    // can measure — a table with a column nothing sizes — and the band says so.
                    M.BoxStart? own = blocks[0] is M.BoxStart first && M.BoxStart.IndexOfEnd(blocks, 0, blocks.Count) == blocks.Count - 1 ? first : null;
                    if (track <= 0)
                    {
                        double? measured = MeasureBlocks(blocks, 0, blocks.Count, contentWidth, minContent: false);
                        if (measured is not double m || m <= 0) return Abandon();
                        track = Math.Min(m, contentWidth) - (own == null ? 0 : own.MarginLeft + own.MarginRight);
                        if (track <= 0) return Abandon();
                    }
                    M.BoxStart box = own ?? new M.BoxStart();
                    box.Float = side;
                    box.FloatWidth = track;
                    if (own == null) emitted.Add(box);
                    emitted.AddRange(blocks);
                    if (own == null) emitted.Add(new M.BoxEnd());
                    continue;
                }

                // A floated inline element inside it — a badge in a pull-right — lifts into a block
                // before its line, which a floated paragraph has no room for; the band's cell has.
                if (_inlineFloats.Skip(recorded).Any(r => r.Host == paragraph)) return Abandon();

                // A declared width is reserved as stated. Without one, CSS shrink-to-fit: the
                // paragraph's own widest line, measured by the layout that will draw it and capped
                // at the band. Zero means nothing measurable, and the band says so.
                if (track <= 0) track = Measurer.MeasureShrinkToFitWidth(paragraph, contentWidth);

                // MeasureShrinkToFitWidth answers zero for a paragraph with nothing to draw, and
                // documents that as the caller's cue to fall back rather than reserve nothing.
                // NO HTML INPUT REACHES THIS LINE: every "nothing" — an empty element, whitespace,
                // a zero-width space, a zero-sized image — maps to no block at all and leaves
                // through the single-paragraph guard above, which is what
                // FloatShrinkToFitTests.AFloatWithNothingOfAnyShape_FallsBackAndCreatesNoFloat
                // pins. It stays because it honours the measurer's stated contract at the one place
                // that consumes it: a mapper change that began producing an empty paragraph here
                // would otherwise reserve a zero-width float and report nothing. Its mutation is
                // recorded as NOT EXERCISED in .claude/mutations-html-residuals.ps1, which is
                // neither a kill nor a survival.
                if (track <= 0) return Abandon();

                paragraph.Float = side;
                paragraph.FloatWidth = track;
                emitted.Add(paragraph);
            }

            sink.AddRange(emitted);

            foreach (IElement el in beside)
                MapBlock(el, sec, sink, listLevel, depth + 1,
                         DeriveStyle(inherited, el, null, false), align);

            return true;
        }

        /// <summary>Resets <paramref name="target"/> to the members <paramref name="saved"/> holds.</summary>
        private static void RestoreSet<T>(HashSet<T> target, HashSet<T> saved)
        {
            target.Clear();
            target.UnionWith(saved);
        }

        /// <summary>Resets <paramref name="target"/> to the entries <paramref name="saved"/> holds.</summary>
        private static void RestoreMap<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> saved)
            where TKey : notnull
        {
            target.Clear();
            foreach (var kv in saved) target[kv.Key] = kv.Value;
        }

        /// <summary>
        /// Lays a run of two or more floats with nothing beside them out as lines of side-by-side
        /// cells, one borderless table per line.
        /// </summary>
        /// <remarks>
        /// <para><b>A new line where the next float will not fit</b>, which is where CSS puts it:
        /// a float that does not fit beside the ones before it moves down. That is what makes
        /// twelve-column grids work — four <c>width:50%</c> floats are two lines of two, not one
        /// line of four squeezed to a quarter each. Only a declared width can overflow a line; an
        /// auto-width float never starts one.</para>
        ///
        /// <para><b>A line that does not fill the measure keeps its widths.</b> The table normalises
        /// an all-explicit column set to fill its width, so two <c>width:25%</c> floats would each
        /// become half. The remainder is given to an empty filler cell on the side away from the
        /// floats, so they keep their size and their edge.</para>
        ///
        /// <para><b>Exact for floats that declare a width, and for the ones that can be measured.</b>
        /// An auto-width float is as wide as its content — CSS shrink-to-fit, by
        /// the layout that draws it (<see cref="MeasureBlocks"/>, capped at the measure), so it can
        /// start a new line as a declared width can. One case is approximate and reported: a float
        /// nothing can measure (a table with a column nothing sizes) shares the line's leftover width
        /// with the others like it. Lines are also clean rows — a float never snags beside a taller
        /// one from the line above, which CSS can do and grids add a clearfix to avoid.</para>
        /// </remarks>
        private void MapFloatRun(
            List<IElement> floats, FloatSide side, M.Section sec, List<M.IDocElement> sink,
            int listLevel, int depth, Style inherited, M.TextAlignment? align)
        {
            double contentWidth = SectionContentWidth(sec);
            bool lead = FloatsLeadTheRow(side, BandIsRtl(inherited, sec));
            bool anyAuto = false;

            // Each float's cell is mapped once, measured when it declares no width, and placed.
            var line = new List<(M.TableCell Cell, double Track)>();
            double used = 0;

            void EmitLine()
            {
                if (line.Count == 0) return;

                var table = NewBandTable();
                var row = new M.TableRow();
                var widths = new List<double>();

                double rest = contentWidth - used;
                bool filler = line.TrueForAll(f => f.Track > 0) && rest > FloatLineFitTolerance;

                void AddFiller()
                {
                    row.Cells.Add(NewFillerCell());
                    widths.Add(rest);
                }

                if (filler && !lead) AddFiller();
                for (int k = 0; k < line.Count; k++)
                {
                    var (cell, track) = line[lead ? k : line.Count - 1 - k];
                    row.Cells.Add(cell);
                    widths.Add(track);
                }
                if (filler && lead) AddFiller();

                table.Rows.Add(row);
                if (widths.Exists(w => w > 0))
                    foreach (double w in widths) table.ColumnWidths.Add(w);
                ApplyBandDirection(table, inherited, sec);
                sink.Add(table);

                line.Clear();
                used = 0;
            }

            foreach (IElement f in floats)
            {
                double track = FloatTrack(f, contentWidth);
                M.TableCell cell = FloatCell(f, sec, listLevel, depth, inherited, align);
                if (track <= 0)
                {
                    // CSS shrink-to-fit: the float's content width by the layout that
                    // draws it, capped at the measure — a declared width in all but name. Nothing
                    // measurable, and it shares the line's rest, reported.
                    double? measured = MeasureBlocks(cell.Blocks, 0, cell.Blocks.Count, contentWidth, minContent: false);
                    if (measured is double m && m > 0) track = Math.Min(m, contentWidth);
                    else anyAuto = true;
                }
                if (track > 0 && line.Count > 0 && used + track > contentWidth + FloatLineFitTolerance) EmitLine();

                line.Add((cell, track));
                if (track > 0) used += track;
            }
            EmitLine();

            if (anyAuto)
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    "float: floats with no declared width and no measurable content (a table with a " +
                    "column nothing sizes) were laid out side by side sharing the line's remaining width " +
                    "equally. CSS sizes each to its content. Declare a width on each float, or use " +
                    "HtmlRenderEngine.Browser where that matters.");
        }

        /// <summary>
        /// How far past the measure a line of floats may reach and still count as fitting.
        /// </summary>
        /// <remarks>
        /// Grid widths are written as rounded percentages — Bootstrap's twelfth is
        /// <c>8.33333333%</c> — so a full line sums to a hair under or over 100%. Half a point
        /// absorbs that and is far below any width a real column has.
        /// </remarks>
        private const double FloatLineFitTolerance = 0.5;

        /// <summary>One floated element as a band cell, top-aligned, never empty.</summary>
        private M.TableCell FloatCell(IElement el, M.Section sec, int listLevel, int depth,
            Style container, M.TextAlignment? align)
        {
            var c = new M.TableCell { VAlign = M.CellVerticalAlign.Top, InnerPaddingPt = 0 };
            MapBlock(el, sec, c.Blocks, listLevel, depth + 1, DeriveStyle(container, el, null, false), align);
            EnsureNotEmpty(c);
            return c;
        }

        /// <summary>
        /// Whether a band's float cells come FIRST in its row — the cell order that, after
        /// <see cref="ApplyBandDirection"/> mirrors a right-to-left band, puts the floats on the
        /// physical side they name.
        /// </summary>
        /// <remarks>
        /// <c>float:left</c> and <c>float:right</c> are physical: a left float is on the left in a
        /// right-to-left paragraph too. A band's cells are in reading order and an RTL band is
        /// mirrored, so the floats lead the row exactly when their side is the reading start —
        /// left in LTR, right in RTL. When they trail it, they are also reversed, because the
        /// FIRST float of a run sits at the edge it names and the rest line up inward from it.
        /// </remarks>
        private static bool FloatsLeadTheRow(FloatSide side, bool rtl)
            => (side == FloatSide.Left) != rtl;

        /// <summary>
        /// True when the element's <c>::after</c> clears — the clearfix idiom, which ends a band
        /// after the element, where <c>clear</c> on the element itself ends it before.
        /// </summary>
        /// <remarks>
        /// <para>The element is an ordinary block, so it starts beside the floats; its generated
        /// <c>::after</c> is what drops below them, taking everything after it along.</para>
        /// <para>Both halves occur in one stylesheet. Bootstrap 3 puts a clearfix on every
        /// <c>.dl-horizontal dd</c>, whose text belongs beside the floated <c>dt</c> — ending the
        /// band before it stacked every definition list — and on an empty
        /// <c>&lt;div class="clearfix"&gt;</c> between grid columns, which has nothing to place
        /// and read as content beside the columns turned them into a band with an empty cell.</para>
        /// </remarks>
        private static bool AfterClears(IElement el)
        {
            string? after = el.GetAttribute(AfterStyleAttribute);
            if (string.IsNullOrWhiteSpace(after)) return false;
            string? clear = DeclarationValue(after!, "clear")?.Trim();
            return Eq(clear, "both") || Eq(clear, "left") || Eq(clear, "right");
        }

        /// <summary>
        /// An element with no child elements and no text — nothing of its own to lay out.
        /// </summary>
        private static bool IsEmptyElement(IElement el)
            => el.ChildElementCount == 0 && string.IsNullOrWhiteSpace(el.TextContent);

        /// <summary>The declared float side, from CSS or an <c>&lt;img align&gt;</c>.</summary>
        private FloatSide ResolveFloatSide(IElement el)
        {
            string? f = StyleValue(el, "float");
            if (f != null)
            {
                string v = f.Trim();
                if (Eq(v, "left")) return FloatSide.Left;
                if (Eq(v, "right")) return FloatSide.Right;
                return FloatSide.None;   // `none`, `inline-start/end` (unsupported), anything else
            }

            // The legacy attribute a browser still honours on images and tables.
            if (NameIs(el, HtmlTags.Img) || NameIs(el, HtmlTags.Table))
            {
                string? a = el.GetAttribute("align");
                if (Eq(a, "left")) return FloatSide.Left;
                if (Eq(a, "right")) return FloatSide.Right;
            }
            return FloatSide.None;
        }

        /// <summary><c>clear</c> that actually ends a band.</summary>
        private bool ClearsFloat(IElement el)
        {
            string? c = StyleValue(el, "clear");
            if (c == null) return false;
            string v = c.Trim();
            // `none` is the initial value and clears nothing -- treating it as a break would end
            // every band at the first sibling that merely mentions the property.
            return Eq(v, "both") || Eq(v, "left") || Eq(v, "right");
        }

        /// <summary>The column width for a floated element, or 0 for auto.</summary>
        private double FloatTrack(IElement el, double contentWidth)
        {
            string? w = StyleValue(el, "width") ?? el.GetAttribute("width");
            if (string.IsNullOrEmpty(w)) return 0;
            double v = ResolveWidth(w, contentWidth, 0);
            return v > 1.0 ? v : 0;
        }

        /// <summary>Whitespace-only text, which sits between block siblings and means nothing here.</summary>
        private static bool IsIgnorableText(INode node) =>
            node.NodeType == NodeType.Text && string.IsNullOrWhiteSpace(node.TextContent);
    }
}
