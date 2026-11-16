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
        // ============================================================= lists

        /// <summary>
        /// Maps a <c>&lt;ul&gt;</c> or <c>&lt;ol&gt;</c> onto one <see cref="M.ListBlock"/>, nesting
        /// a sublist inside the item that contains it.
        /// </summary>
        /// <remarks>
        /// <para><b>It does not flatten.</b> Every <c>&lt;li&gt;</c> as a sibling in the section's
        /// element list carrying an <c>int Level</c>, a nested list's items following as more
        /// siblings one level deeper, would leave the document model no record of where a sublist
        /// starts or stops. The model has a container to put them in, and an imported list that stays flat is a list every exporter has to
        /// re-derive from indentation, and the tagged-PDF one cannot re-derive at all.</para>
        ///
        /// <para><b>A marker is written only where the container cannot express the numbering.</b>
        /// HTML's is richer than the model's — <c>type="i"</c>, <c>type="A"</c>, a
        /// <c>list-style-type</c> bullet, and a per-item <c>value="N"</c> that resets the sequence
        /// mid-list — and <c>ListItem.Marker</c> exists for exactly that surplus. A plain
        /// decimal <c>&lt;ol&gt;</c> gets none, so <see cref="M.ListBlock.Start"/> and the item's
        /// position do the numbering.</para>
        ///
        /// <para><b>Resolving every marker was the first shape of this, and it made the container
        /// half-inert.</b> With a string on every item, <c>IsOrdered</c> and <c>Start</c> were never
        /// consulted for an imported list: deleting either changed no output, because
        /// <c>ModelRules.ListMarker</c> returns an explicit marker ahead of its own numbering. The
        /// structure was real and the numbering was still a set of literal strings — which is
        /// half of the defect the container was built to remove, and the half a tag tree does not
        /// show.</para>
        /// </remarks>
        private void MapList(IElement listEl, M.Section sec, List<M.IDocElement> sink,
            bool ordered, int level, int depth, Style inherited, M.TextAlignment? inheritedAlign)
        {
            var outOfFlow = new List<M.IDocElement>();
            // A positioned list is the containing block of the positioned blocks in its items (CSS 2.1
            // §10.1): a bare box round it, as every other positioned ancestor walked as blocks has one
            //, which they are measured from and drawn with.
            M.BoxStart? box = IsPositioningAncestor(listEl) ? new M.BoxStart() : null;
            if (box != null) _positionedAncestors.Push((listEl, box));
            var itemBoxes = new List<BoxedItem>();
            M.ListBlock? list;
            try { list = BuildList(listEl, sec, ordered, level, depth, inherited, inheritedAlign, outOfFlow, itemBoxes); }
            finally { if (box != null) _positionedAncestors.Pop(); }
            // The positioned blocks its items held, placed out of the flow: after the list, as a flex
            // container's go after its items — on the page the list ends on, or in the box round it.
            // A null list walked no item, so it has none.
            if (list == null) return;

            // Its vertical margins: CSS's, else the UA stylesheet's 1em. Only a list in the block
            // flow comes here; one nested in an item is that item's Sublist (BuildList) and keeps
            // none, as `ul ul`, `ol ol` and their mixes have none (HTML §15.3.8).
            var (top, _, bottom, _) = ResolveSides(listEl, "margin", inherited.Size);
            var (uaTop, uaBottom) = DefaultBlockMarginEm(listEl.LocalName);
            double listTop = DeclaresMargin(listEl, "margin-top") ? top : uaTop * inherited.Size;
            double listBottom = DeclaresMargin(listEl, "margin-bottom") ? bottom : uaBottom * inherited.Size;
            // Collapsed with the outer items' margins BuildList put there (ApplyItemMargins).
            list.SpacingBefore = list.SpacingBefore > 0 ? Math.Max(list.SpacingBefore, listTop) : listTop;
            list.SpacingAfter = list.SpacingAfter > 0 ? Math.Max(list.SpacingAfter, listBottom) : listBottom;
            if (box != null)
            {
                // The margins outside the box, so its padding box — what the offsets are measured
                // from — begins where the list's first item does. The list keeps its own, which
                // meet the box's through its open edges (CollapseAdjacentMargins) and add nothing.
                (box.MarginTop, box.MarginBottom) = (list.SpacingBefore, list.SpacingAfter);
                sink.Add(box);
            }
            AddListParts(list, itemBoxes, inherited.Direction == M.TextDirection.RightToLeft, sink);
            sink.AddRange(outOfFlow);
            if (box != null) sink.Add(new M.BoxEnd());
        }

        /// <summary>
        /// An item of a list in the block flow the list is cut round: its index; its box and the
        /// blocks placed in it when it is the containing block of positioned blocks, else null; and
        /// its sublists when one of them is, or holds an item that is, else null.
        /// </summary>
        private readonly struct BoxedItem
        {
            public BoxedItem(int index, M.BoxStart? box, List<M.IDocElement> blocks, List<CutSublist>? sublists)
            {
                Index = index; Box = box; Blocks = blocks; Sublists = sublists;
            }
            public int Index { get; }
            public M.BoxStart? Box { get; }
            public List<M.IDocElement> Blocks { get; }
            public List<CutSublist>? Sublists { get; }
        }

        /// <summary>
        /// A sublist taken out of its item into the flow after it: the list; its box and the blocks
        /// placed in it when it is the containing block of positioned blocks, else null; and its own
        /// items it is cut round.
        /// </summary>
        private readonly struct CutSublist
        {
            public CutSublist(M.ListBlock list, M.BoxStart? box, List<M.IDocElement> blocks, List<BoxedItem> items)
            {
                List = list; Box = box; Blocks = blocks; Items = items;
            }
            public M.ListBlock List { get; }
            public M.BoxStart? Box { get; }
            public List<M.IDocElement> Blocks { get; }
            public List<BoxedItem> Items { get; }
        }

        /// <summary>
        /// <paramref name="list"/> into <paramref name="sink"/> — whole, or cut round each item that is
        /// the containing block of positioned blocks (CSS 2.1 §10.1: a positioned <c>li</c>'s padding
        /// box), or whose sublist is or holds one, since the model has no box inside a list: the items
        /// before it, then a box holding a list of that item alone and its positioned blocks, then the
        /// rest, each part going on from the one before (<see cref="M.ListBlock.ContinuesPrevious"/>, its
        /// <c>Start</c> numbering on). The box is the item's: its start edge at the items' text, as the
        /// item's box begins after the list's padding, the marker hanging outside it as an outside marker
        /// does; the list's margins and the item's own gap after it outside it. A sublist cut out
        /// follows its item, one level deeper (<see cref="M.ListBlock.NestingDepth"/>), its text start
        /// moved on by the item's — or, when it is positioned, in a box of its own starting there. <paramref name="edge"/> is where the list's own edge is from the flow's.
        /// </summary>
        private static void AddListParts(M.ListBlock list, List<BoxedItem> boxed, bool rtl, List<M.IDocElement> sink,
            int nestingDepth = 0, bool continues = false, double edge = 0)
        {
            // Items [from, to): the list's margins only at its ends, the numbering going on.
            M.ListBlock Part(int from, int to)
            {
                var part = new M.ListBlock
                {
                    IsOrdered = list.IsOrdered, Start = list.Start + from,
                    TextIndentPt = edge == 0 ? list.TextIndentPt : edge + (list.TextIndentPt ?? 0),
                    Direction = list.Direction, ContinuesPrevious = continues || from > 0, NestingDepth = nestingDepth,
                    SpacingBefore = from == 0 ? list.SpacingBefore : 0,
                    SpacingAfter = to == list.Items.Count ? list.SpacingAfter : 0,
                };
                part.Items.AddRange(list.Items.GetRange(from, to - from));
                return part;
            }

            if (boxed.Count == 0) { sink.Add(edge == 0 && nestingDepth == 0 && !continues ? list : Part(0, list.Items.Count)); return; }

            int cursor = 0;
            foreach (BoxedItem item in boxed)
            {
                // The item's text start, where its sublists' edge is.
                double textStart = edge + (list.TextIndentPt ?? 0);
                if (item.Box is not { } box)
                {
                    // The list's own margin after it, when this is its last item, after its sublists.
                    M.ListBlock upTo = Part(cursor, item.Index + 1);
                    M.ListBlock last = item.Sublists![item.Sublists.Count - 1].List;
                    last.SpacingAfter = Math.Max(last.SpacingAfter, upTo.SpacingAfter);
                    upTo.SpacingAfter = 0;
                    sink.Add(upTo);
                    AddCutSublists(item.Sublists, rtl, sink, nestingDepth + 1, textStart);
                    cursor = item.Index + 1;
                    continue;
                }
                if (item.Index > cursor) sink.Add(Part(cursor, item.Index));

                M.ListBlock alone = Part(item.Index, item.Index + 1);
                if (rtl) box.MarginRight = alone.TextIndentPt ?? 0; else box.MarginLeft = alone.TextIndentPt ?? 0;
                alone.TextIndentPt = 0;
                (box.MarginTop, box.MarginBottom) = (alone.SpacingBefore, alone.SpacingAfter);
                (alone.SpacingBefore, alone.SpacingAfter) = (0, 0);
                // The item's gap after it is its margin, outside its box (a sublist's, or one taken out
                // of it, is the sublist's own: ApplyItemMargins).
                M.ListItem li = alone.Items[0];
                if (li.Sublist == null) { box.MarginBottom = Math.Max(box.MarginBottom, li.SpacingAfter); li.SpacingAfter = 0; }

                sink.Add(box);
                sink.Add(alone);
                if (item.Sublists != null) AddCutSublists(item.Sublists, rtl, sink, nestingDepth + 1, 0);
                sink.AddRange(item.Blocks);
                sink.Add(new M.BoxEnd());
                cursor = item.Index + 1;
            }
            if (cursor < list.Items.Count) sink.Add(Part(cursor, list.Items.Count));
        }

        /// <summary>
        /// An item's sublists taken out of it, into <paramref name="sink"/> after it at
        /// <paramref name="nestingDepth"/>, their edge at <paramref name="edge"/> (the item's text start):
        /// one list, as the item's sublists are (<see cref="BuildList"/>), each after the first going on
        /// from it; a positioned one in its box, its margins outside it.
        /// </summary>
        private static void AddCutSublists(List<CutSublist> sublists, bool rtl, List<M.IDocElement> sink, int nestingDepth, double edge)
        {
            for (int k = 0; k < sublists.Count; k++)
            {
                CutSublist sub = sublists[k];
                if (sub.Box is not { } box)
                {
                    AddListParts(sub.List, sub.Items, rtl, sink, nestingDepth, continues: k > 0, edge);
                    continue;
                }
                if (rtl) box.MarginRight = edge; else box.MarginLeft = edge;
                (box.MarginTop, box.MarginBottom) = (sub.List.SpacingBefore, sub.List.SpacingAfter);
                (sub.List.SpacingBefore, sub.List.SpacingAfter) = (0, 0);
                sink.Add(box);
                AddListParts(sub.List, sub.Items, rtl, sink, nestingDepth, continues: k > 0);
                sink.AddRange(sub.Blocks);
                sink.Add(new M.BoxEnd());
            }
        }

        /// <inheritdoc cref="MapList"/>
        /// <remarks>
        /// An item's absolutely positioned or fixed blocks go to <c>outOfFlow</c>, placed
        /// (<c>MapPositioned</c>) rather than collected into the item's text; the caller adds them
        /// after the list. An item that is itself positioned is their containing block: it
        /// gets a box, its positioned blocks (its sublists' too) go with it to <paramref name="itemBoxes"/>,
        /// and the caller cuts the list round it (<see cref="AddListParts"/>). Its sublists are built the
        /// same way, and one that is positioned, or holds an item the list is cut round, takes the item's
        /// sublists out of it into the flow after it. So every positioned ancestor of a
        /// block in a list keeps a box, and the block is measured from the nearest.
        /// </remarks>
        /// <returns>The list, or null when it is too deep or has no visible item.</returns>
        private M.ListBlock? BuildList(IElement listEl, M.Section sec,
            bool ordered, int level, int depth, Style inherited, M.TextAlignment? inheritedAlign,
            List<M.IDocElement> outOfFlow, List<BoxedItem> itemBoxes)
        {
            if (TooDeep(depth)) return null;

            int n = 1;
            if (ordered && int.TryParse(listEl.GetAttribute("start"), out int start)) n = start;
            // CSS's list-style-type outranks the presentational type="" attribute, as in a browser;
            // only the attribute was read, so an <ol style="list-style-type: lower-roman"> — or
            // hebrew — was numbered 1, 2, 3.
            string olType = ordered
                ? (OrderedType(ListStyleType(listEl)) ?? listEl.GetAttribute("type") ?? "1")
                : "";

            var list = new M.ListBlock { IsOrdered = ordered, Start = n };

            // Its items' text is its start-side padding in — the UA stylesheet's 40px unless the
            // page says otherwise (Bootstrap's .list-unstyled says 0) — with the marker before it.
            // The exporter's own was a marker's width, 14pt, where a browser's is 30.
            bool rtl = inherited.Direction == M.TextDirection.RightToLeft;
            var (_, padRight, _, padLeft) = ResolveSides(listEl, "padding", inherited.Size);
            list.TextIndentPt = DeclaresPaddingSide(listEl, rtl ? "padding-right" : "padding-left")
                ? (rtl ? padRight : padLeft)
                : PdfUnits.PixelsToPoints(DefaultListPaddingPx);

            var itemMargins = new List<(M.ListItem Item, M.ListBlock? Tail, double Top, double Bottom)>();
            foreach (var child in listEl.Children)
            {
                if (!TakeNode()) break;
                if (!string.Equals(child.LocalName, HtmlTags.Li, StringComparison.OrdinalIgnoreCase)) continue;
                if (IsHidden(child)) continue; // display:none on an <li>
                if (int.TryParse(child.GetAttribute("value"), out int v)) n = v; // <li value="N">

                // The bullet is the item's own list-style-type, else the list's (it inherits) —
                // per item, because an item can name a type its list does not, `none` included.
                string bullet = ordered ? "" : UlBullet(listEl, child, level);

                var liStyle = DeriveStyle(inherited, child, null, false);
                // A ListItem is not a Paragraph and has no Opacity of its own, so nothing will
                // claim the accumulated value the way NewPara does — clearing the block figure
                // makes the runs (and the marker, cloned from TextFormat) carry all of it.
                // Without this an `<li style="opacity:.5">` came out fully opaque: DeriveStyle had
                // just set BlockOpacity = Opacity, so RelativeOpacity returned 1 for every run. Inside
                // an opacity box the runs carry only what they add to the box's.
                liStyle.BlockOpacity = _groupOpacity;
                var li = new M.ListItem
                {
                    Level = level,
                    IsNumbered = ordered,
                    // An empty marker is the model's "none". An item gets one when its list style
                    // is `none` — on the item, else inherited from the list — or when it is not a
                    // list item at all: `display:block` removes the marker in CSS, and Bootstrap's
                    // .list-group-item is exactly that.
                    Marker = HasNoMarker(listEl, child) ? "" : ExplicitMarker(ordered, olType, bullet, n, list),
                    Direction = liStyle.Direction,
                    TextFormat = MakeRun("", liStyle), // marker takes the item's colour / size / family
                    // A browser's list item has no margin and the line height it inherits; the
                    // model's 2pt after each item and automatic line were the exporter's own.
                    SpacingAfter = 0,
                    LineSpacingPt = liStyle.LineHeight,
                    LineHeightFactor = liStyle.LineHeight > 0 ? 0 : NormalLineHeightFactor,
                };
                ApplyMarkerStyle(child, li, liStyle);
                var (liTop, _, liBottom, _) = ResolveSides(child, "margin", liStyle.Size);
                // A positioned item is the containing block of the positioned blocks inside it: a
                // box of its own, which they are measured from.
                M.BoxStart? itemBox = IsPositioningAncestor(child) ? new M.BoxStart() : null;
                List<M.IDocElement> itemOutOfFlow = itemBox != null ? new List<M.IDocElement>() : outOfFlow;
                int index = list.Items.Count;
                // The item's sublists, while one may yet have to leave it.
                var sublists = new List<CutSublist>();
                bool cutSublists = false;
                if (itemBox != null) _positionedAncestors.Push((child, itemBox));
                try
                {
                    // Collect the item's own inline content, but NOT nested <ul>/<ol> (attached to the
                    // item below — otherwise their text leaks into the parent marker).
                    var tmp = new M.Paragraph();
                    foreach (var n2 in child.ChildNodes)
                    {
                        if (n2 is IElement e &&
                            (string.Equals(e.LocalName, HtmlTags.Ul, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(e.LocalName, HtmlTags.Ol, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        // An absolutely positioned or fixed child, placed out of the item's text, as
                        // MapPositioned places one anywhere else, from its nearest positioned ancestor.
                        if (n2 is IElement positioned && IsPositioned(positioned) && !IsHidden(positioned)
                            && MapPositioned(positioned, sec, itemOutOfFlow, level, depth + 1, liStyle, inheritedAlign))
                            continue;
                        CollectInlineNode(n2, tmp, liStyle, depth + 1);
                    }
                    li.Runs.AddRange(tmp.Runs);
                    list.Items.Add(li);
                    n++;

                    // Nested lists hang off the item that contains them. HTML allows several inside one
                    // <li> and the model holds one, so a second is appended to the first rather than
                    // dropped: the alternative loses content, and the reading order is the same either
                    // way.
                    foreach (var sub in child.Children)
                    {
                        bool subOrdered =
                            string.Equals(sub.LocalName, HtmlTags.Ol, StringComparison.OrdinalIgnoreCase);
                        if (!subOrdered &&
                            !string.Equals(sub.LocalName, HtmlTags.Ul, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // A positioned sublist is the containing block of the positioned blocks in its
                        // items, as a positioned list in the flow is (MapList): a box of its own, and
                        // so out of its item into the flow (AddListParts).
                        M.BoxStart? subBox = IsPositioningAncestor(sub) ? new M.BoxStart() : null;
                        List<M.IDocElement> subOutOfFlow = subBox != null ? new List<M.IDocElement>() : itemOutOfFlow;
                        var subItems = new List<BoxedItem>();
                        M.ListBlock? nested;
                        if (subBox != null) _positionedAncestors.Push((sub, subBox));
                        try
                        {
                            nested = BuildList(sub, sec, subOrdered, level + 1, depth + 1, liStyle, inheritedAlign,
                                subOutOfFlow, subItems);
                        }
                        finally { if (subBox != null) _positionedAncestors.Pop(); }
                        if (nested == null) continue;

                        bool boxed = subBox != null && subOutOfFlow.Count > 0;
                        cutSublists |= boxed || subItems.Count > 0;
                        sublists.Add(new CutSublist(nested, boxed ? subBox : null, subOutOfFlow, subItems));
                    }
                }
                finally { if (itemBox != null) _positionedAncestors.Pop(); }

                // Sublists that stay in the item are one, as ever; one that leaves takes them all, so
                // they stay one list after it.
                if (!cutSublists)
                    foreach (CutSublist s in sublists)
                        if (li.Sublist == null) li.Sublist = s.List;
                        else li.Sublist.Items.AddRange(s.List.Items);
                bool boxedItem = itemBox != null && itemOutOfFlow.Count > 0;
                if (boxedItem || cutSublists)
                    itemBoxes.Add(new BoxedItem(index, boxedItem ? itemBox : null, boxedItem ? itemOutOfFlow : new List<M.IDocElement>(),
                        cutSublists ? sublists : null));
                // Its gap after it goes after what it draws last: its sublists, or the last one taken out.
                M.ListBlock? tail = li.Sublist ?? (cutSublists ? sublists[sublists.Count - 1].List : null);
                itemMargins.Add((li, tail,
                    DeclaresMargin(child, "margin-top") ? Math.Max(0, liTop) : 0,
                    DeclaresMargin(child, "margin-bottom") ? Math.Max(0, liBottom) : 0));
            }

            ApplyItemMargins(list, itemMargins);

            // A <ul> whose every child was hidden is not an empty list, it is no list.
            //
            // REDUNDANT TODAY FOR THE PDF PATH, and measured rather than assumed: deleting this
            // line changes no exported byte, because PdfStructureTree.Prune drops an element that
            // reaches no content, so the empty /L never survives to the file. It stays because the
            // hazard is real one exporter over -- HtmlExporter would write <ul></ul> and
            // OdtExporter an empty <text:list>, neither of which is pruned by anything -- and
            // because a mapper that answers "there is no list here" is a better thing to hand a
            // caller reading the model than one that answers "here is a list with nothing in it".
            return list.Items.Count > 0 ? list : null;
        }

        /// <summary>
        /// The items' own vertical margins — <c>li { margin-bottom: 4px }</c> — which were dropped:
        /// every item had none. Between two items the larger of the first's bottom and the second's
        /// top (CSS collapses adjoining margins), placed after the first item's nested list, which
        /// is where its bottom margin is — the model's <see cref="M.ListItem.SpacingAfter"/> comes
        /// before the sublist, so an item with one puts the gap on the sublist's own spacing after,
        /// which already holds (collapsed) its last item's. The first item's top and the last
        /// one's bottom collapse into the list's own margins. <c>Tail</c> is the sublist an item draws
        /// last — its own, or the last taken out of it — null for none.
        /// </summary>
        private static void ApplyItemMargins(M.ListBlock list, List<(M.ListItem Item, M.ListBlock? Tail, double Top, double Bottom)> items)
        {
            if (items.Count == 0) return;
            list.SpacingBefore = Math.Max(list.SpacingBefore, items[0].Top);
            list.SpacingAfter = Math.Max(list.SpacingAfter, items[items.Count - 1].Bottom);

            for (int k = 0; k + 1 < items.Count; k++)
            {
                double gap = Math.Max(items[k].Bottom, items[k + 1].Top);
                if (gap <= 0) continue;
                if (items[k].Tail is { } sub) sub.SpacingAfter = Math.Max(sub.SpacingAfter, gap);
                else items[k].Item.SpacingAfter = gap;
            }
        }

        /// <summary>
        /// The marker HTML asks for when the container cannot produce it, or null when it can.
        /// </summary>
        /// <param name="ordered">True for an &lt;ol&gt;, false for a &lt;ul&gt;.</param>
        /// <param name="olType">The &lt;ol&gt; type attribute: "1", "a", "A", "i" or "I".</param>
        /// <param name="bullet">The bullet the list's list-style-type asks for.</param>
        /// <param name="list">The list being built; its Start and item count give the position.</param>
        /// <param name="expectedOrdinal">
        /// The number the container would produce for this position — <c>Start</c> plus the
        /// index. A <c>&lt;li value="N"&gt;</c> that moves the sequence makes them differ, and that
        /// is precisely when the item needs a marker of its own.
        /// </param>
        /// <remarks>
        /// <b>A bullet always needs one.</b> The model cycles disc/circle/square by nesting depth
        /// the way a browser defaults to, but HTML can name a <c>list-style-type</c> —
        /// including <c>none</c>, which the model has no way to ask for at all.
        /// </remarks>
        private static string? ExplicitMarker(
            bool ordered, string olType, string bullet, int expectedOrdinal, M.ListBlock list)
        {
            if (!ordered) return bullet;

            // A non-decimal type is numbering the container does not implement.
            if (olType != "1") return OrderedMarker(expectedOrdinal, olType);

            // A value="N" that agrees with the position needs no marker; one that moves the
            // sequence does, because ListBlock carries a single Start and not a per-item ordinal.
            return expectedOrdinal == list.Start + list.Items.Count
                ? null
                : OrderedMarker(expectedOrdinal, olType);
        }

        /// <summary>
        /// True when an item draws no marker: its <c>list-style-type</c> — its own, else the list's,
        /// since the property inherits — is <c>none</c>, or it is not displayed as a list item.
        /// </summary>
        private static bool HasNoMarker(IElement listEl, IElement item)
            => !HasMarkerBox(item) || (ListStyleType(item) ?? ListStyleType(listEl)) == "none";

        /// <summary>
        /// Whether an item is a list item at all — <c>display</c> unset or naming <c>list-item</c> —
        /// and so has a marker box a <c>::marker</c> can fill; <c>display: block</c> (Bootstrap's
        /// <c>.list-group-item</c>) has none.
        /// </summary>
        private static bool HasMarkerBox(IElement item)
        {
            string? display = StyleValue(item, "display")?.Trim();
            return display == null || display.IndexOf("list-item", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The <c>list-style-type</c> an element declares — the longhand, else the one the
        /// <c>list-style</c> shorthand names — lower-cased, or <c>null</c> for neither.
        /// </summary>
        /// <remarks>
        /// The shorthand is read too: <c>list-style: none</c> is the way nearly every stylesheet
        /// (Bootstrap's <c>.list-unstyled</c>, <c>.breadcrumb</c>, <c>.nav</c>) turns markers off. In the shorthand <c>none</c> is the
        /// type when no other type keyword is present, which is what CSS says: <c>list-style:
        /// none</c> sets both the image and the type to none.
        /// </remarks>
        private static string? ListStyleType(IElement el)
        {
            string? longhand = StyleValue(el, "list-style-type")?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(longhand)) return longhand;

            string? shorthand = StyleValue(el, "list-style");
            if (string.IsNullOrWhiteSpace(shorthand)) return null;

            bool none = false;
            foreach (string token in SplitTopLevelTokens(shorthand!.Trim()))
            {
                string t = token.ToLowerInvariant();
                if (t == "none") none = true;
                else if (t != "inside" && t != "outside" && !t.StartsWith("url(", StringComparison.Ordinal)) return t;
            }
            return none ? "none" : null;
        }

        /// <summary>A list's start-side padding when the page sets none — the HTML UA stylesheet's (§15.3.8).</summary>
        private const double DefaultListPaddingPx = 40;

        /// <summary>Whether <paramref name="el"/>'s CSS sets the padding <paramref name="longhand"/>, itself or through the shorthand.</summary>
        private static bool DeclaresPaddingSide(IElement el, string longhand) =>
            StyleValue(el, longhand) != null || StyleValue(el, "padding") != null;

        /// <summary>
        /// Whether every visible item of <paramref name="listEl"/> is displayed as a block rather
        /// than a list item (<c>display: block</c>, <c>flex</c>, <c>grid</c>, <c>table</c>): such a
        /// list is laid out as a container of blocks, each item its own box with no marker, as a
        /// browser lays out Bootstrap's <c>.list-group</c>.
        /// </summary>
        private static bool IsBlockItemList(IElement listEl)
        {
            int items = 0;
            foreach (var child in listEl.Children)
            {
                if (!NameIs(child, HtmlTags.Li) || IsHidden(child)) continue;
                items++;
                string? display = StyleValue(child, "display")?.Trim().ToLowerInvariant();
                if (display is not ("block" or "flex" or "grid" or "table")) return false;
            }
            return items > 0;
        }

        /// <summary>
        /// True when every visible item of the list is laid out inline — <c>display: inline</c> /
        /// <c>inline-block</c>, or floated left in a row — so the list is a line of items, not a
        /// column: a breadcrumb, a pager, <c>.list-inline</c>, a row of tabs.
        /// </summary>
        private static bool IsInlineList(IElement listEl)
        {
            int items = 0;
            foreach (var child in listEl.Children)
            {
                if (!NameIs(child, HtmlTags.Li) || IsHidden(child)) continue;
                items++;
                string? display = StyleValue(child, "display")?.Trim();
                bool inline = display != null && display.StartsWith("inline", StringComparison.OrdinalIgnoreCase);
                if (!inline && !Eq(StyleValue(child, "float")?.Trim(), "left")) return false;
            }
            return items > 0;
        }

        /// <summary>
        /// Collects an inline list's items into one paragraph, as <see cref="CollectInline"/> does,
        /// with a space between two FLOATED items that the source does not separate.
        /// </summary>
        /// <remarks>
        /// Inline items are text in a line, so the source's whitespace between them is the whole
        /// story — none, and a browser draws them touching. Floated items are separate boxes: a row
        /// of tabs written <c>&lt;li&gt;A&lt;/li&gt;&lt;li&gt;B&lt;/li&gt;</c> is two tabs, not the
        /// word "AB".
        /// </remarks>
        private void CollectInlineList(IElement listEl, M.Paragraph para, Style st, int depth)
        {
            AddGeneratedContent(listEl, para, st, after: false);
            bool previousFloated = false;
            foreach (var node in listEl.ChildNodes)
            {
                if (node is IElement item && NameIs(item, HtmlTags.Li) && !IsHidden(item))
                {
                    bool floated = Eq(StyleValue(item, "float")?.Trim(), "left");
                    if (previousFloated && floated) para.Runs.Add(MakeRun(" ", st));
                    previousFloated = floated;
                }
                CollectInlineNode(node, para, st, depth);
            }
            AddGeneratedContent(listEl, para, st, after: true);
        }

        private static string UlBullet(IElement listEl, IElement item, int level)
        {
            string? type = ListStyleType(item)
                           ?? ListStyleType(listEl)
                           ?? listEl.GetAttribute("type")?.ToLowerInvariant();
            return type switch
            {
                "circle" => "◦ ",
                "square" => "▪ ",
                "disc" => "• ",
                "none" => "",
                _ => (level % 3) switch { 0 => "• ", 1 => "◦ ", _ => "▪ " },
            };
        }

        private static string OrderedMarker(int n, string type) => type switch
        {
            "a" => ToAlpha(n, false) + ". ",
            "A" => ToAlpha(n, true) + ". ",
            "i" => ToRoman(n, false) + ". ",
            "I" => ToRoman(n, true) + ". ",
            "hebrew" => Core.Util.OrdinalNumbering.ToCssHebrew(n) + ". ",
            _ => n.ToString(CultureInfo.InvariantCulture) + ". ",
        };

        /// <summary>
        /// A numbering <c>list-style-type</c> as the type the markers above know — the CSS keywords
        /// for the attribute's <c>1 a A i I</c>, and <c>hebrew</c> (CSS Counter Styles 3 §6.1) — or
        /// null for anything else, which leaves the attribute to decide.
        /// </summary>
        private static string? OrderedType(string? listStyleType) => listStyleType switch
        {
            "decimal" => "1",
            "lower-alpha" or "lower-latin" => "a",
            "upper-alpha" or "upper-latin" => "A",
            "lower-roman" => "i",
            "upper-roman" => "I",
            "hebrew" => "hebrew",
            _ => null,
        };

        // One implementation, in Core.Util.OrdinalNumbering.
        private static string ToAlpha(int n, bool upper) => Core.Util.OrdinalNumbering.ToLetters(n, upper);

        private static string ToRoman(int n, bool upper) => Core.Util.OrdinalNumbering.ToRoman(n, upper);
    }
}
