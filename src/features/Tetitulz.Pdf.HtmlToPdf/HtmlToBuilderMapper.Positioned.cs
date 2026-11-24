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
    // position: absolute / fixed with top/left/right/
    // bottom and z-index.
    //
    // The decline said this needs "a containing-block chain and a paint order independent
    // of flow". The second half was the real requirement and the exporter already had the
    // mechanism: RenderParagraphAt(…, paginate: false) draws at a given point without
    // moving or breaking, which is what headers, footers and table cells have always used. What
    // was missing was a way for the model to say where, and a moment to draw it —
    // M.AbsolutePlacement and a held-back pass after the flow.
    //
    // The containing block is the nearest positioned ancestor, not the page. A
    // positioned element that is a box of blocks (MapContainer gives one
    // with nothing to draw a bare box) is the containing block of the positioned blocks inside it
    // (M.AbsolutePlacement.ContainingBox): the exporter measures their offsets from
    // its padding box and draws them with its fragment, so a badge at top: 5px; right: 10px
    // sits in its card's corner. For a stamp, a watermark or a corner label with no positioned
    // ancestor the page IS the containing block, exactly, and nothing is reported. Every positioned ancestor this mapper walks as blocks keeps a box — a flex or grid container
    // (its positioned children taken out of the items, as CSS takes them), a table, a multi-column
    // container, a form or fieldset — and a percentage offset or width inside one is a share of the
    // ancestor's padding box, which the exporter resolves (M.AbsolutePlacement.LeftFraction
    // and its kin). A positioned block inside a list item is placed too, after the list
    // (BuildList) — measured from the list when the list is positioned, a bare box round
    // it; from a positioned item, its box cut out of the list; and from a positioned sublist or an
    // item of one, the sublist taken out of its item. Against the page, right and
    // bottom are the block's far edges, the exporter measuring its content width and height.
    //
    // fixed repeats on every page (M.AbsolutePlacement.EveryPage):
    // print media lays a fixed block out on each page, which is what a running stamp or watermark
    // written as position: fixed asks for — not once, like absolute.
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>True when <paramref name="el"/> is taken out of flow by <c>position</c>.</summary>
        private bool IsPositioned(IElement el)
        {
            string? position = StyleValue(el, "position")?.Trim();
            return position != null && (Eq(position, "absolute") || Eq(position, "fixed"));
        }

        /// <summary>
        /// True when <paramref name="el"/> is a positioned element — CSS 2.1 §9.3.1, any <c>position</c>
        /// but <c>static</c> — and so the containing block of the absolutely positioned blocks inside
        /// it (§10.1).
        /// </summary>
        private static bool IsPositioningAncestor(IElement el)
        {
            string? position = StyleValue(el, "position")?.Trim();
            return position != null && !Eq(position, "static");
        }

        /// <summary>
        /// The positioned elements being walked, innermost last, each with the box of blocks it maps
        /// to — the containing block a positioned block inside it is measured from.
        /// </summary>
        private readonly Stack<(IElement Element, M.BoxStart Box)> _positionedAncestors = new Stack<(IElement, M.BoxStart)>();

        /// <summary>
        /// The box of <paramref name="el"/>'s nearest positioned ancestor — the one being walked, whose
        /// box is the top of <see cref="_positionedAncestors"/>, since every positioned ancestor walked
        /// as blocks pushes one; <c>null</c> when it has none, or when that ancestor is the
        /// document's body or root — the page is its containing block, as the initial containing
        /// block is the viewport's.
        /// </summary>
        private M.BoxStart? ContainingBoxOf(IElement el)
        {
            for (IElement? a = el.ParentElement; a != null; a = a.ParentElement)
            {
                if (!IsPositioningAncestor(a)) continue;
                if (_positionedAncestors.Count > 0 && ReferenceEquals(_positionedAncestors.Peek().Element, a))
                    return _positionedAncestors.Peek().Box;
                return null;
            }
            return null;
        }

        /// <summary>
        /// An offset or width of a positioned block: in points, from <see cref="ResolveOffset"/> — or,
        /// a percentage against a containing box (<paramref name="againstBox"/>), 0 with
        /// <paramref name="share"/> the fraction of the box's padding box the exporter adds, never of the page's
        /// size. <c>null</c> when absent or unresolvable.
        /// </summary>
        private double? ResolvePlacementOffset(IElement el, string property, double basis, bool againstBox, out double? share)
        {
            share = null;
            string? raw = StyleValue(el, property);
            if (againstBox && raw != null && raw.Trim().EndsWith("%", StringComparison.Ordinal))
            {
                double fraction = ResolveWidth(raw, 1.0, double.NaN);
                if (!double.IsNaN(fraction)) { share = fraction; return 0; }
            }
            return ResolveOffset(el, property, basis);
        }

        /// <summary>
        /// Maps an out-of-flow element. Returns <c>false</c> when the caller should fall through to
        /// ordinary block handling — which is what happens when the element declares no usable
        /// offset, since a positioned block with no <c>top</c>/<c>left</c> stays where flow put it
        /// and this engine has no "where flow put it" to give it.
        /// </summary>
        private bool MapPositioned(
            IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel, int depth,
            Style inherited, M.TextAlignment? align)
        {
            string position = StyleValue(el, "position")!.Trim();

            // The containing block: the nearest positioned ancestor's box, else the page —
            // exact either way. A fixed block's is the viewport, the page, always.
            bool fixedBlock = Eq(position, "fixed");
            M.BoxStart? containing = fixedBlock ? null : ContainingBoxOf(el);

            // Against a box a percentage is a share of its padding box, which the exporter alone knows
            // the size of; against the page it is the page's.
            bool againstBox = containing != null;
            double? left = ResolvePlacementOffset(el, "left", sec.PageWidthPt, againstBox, out double? leftShare);
            double? right = ResolvePlacementOffset(el, "right", sec.PageWidthPt, againstBox, out double? rightShare);
            double? top = ResolvePlacementOffset(el, "top", sec.PageHeightPt, againstBox, out double? topShare);
            double? bottom = ResolvePlacementOffset(el, "bottom", sec.PageHeightPt, againstBox, out double? bottomShare);

            if (left == null && right == null && top == null && bottom == null)
            {
                ReportApproximation($"position:{position} with no top/left/right/bottom was laid out " +
                                 "in the flow: this engine has no static position to offset from.");
                return false;
            }

            // The element's own blocks first, so there is something to place. Mapped into a staging
            // list because a positioned element becomes ONE placed paragraph plus, possibly, more
            // blocks that cannot be placed — and those are reported rather than dropped.
            var blocks = new List<M.IDocElement>();
            Style st = DeriveStyle(inherited, el, null, false);
            // The element is the containing block of the positioned blocks inside it (CSS 2.1 §10.1): a
            // bare box, pushed while its children are walked, which they are measured from; when one of
            // them is, the element's blocks are that box's, and they are placed together with it.
            var ownBox = new M.BoxStart();
            _positionedAncestors.Push((el, ownBox));
            try { WalkBlocks(el, sec, blocks, listLevel, depth, st, align); }
            finally { _positionedAncestors.Pop(); }
            // A block placed against the page — a fixed one, whose containing block is the viewport
            // whatever its ancestors (CSS 2.1 §10.1) — is not one of the element's: placed on its own,
            // beside it, so the element's other blocks stay placed together rather than going to the
            // flow.
            static bool AgainstThePage(M.IDocElement b) => b is M.Paragraph { Placement: { ContainingBox: null } };
            List<M.IDocElement> pageAnchored = blocks.FindAll(AgainstThePage);
            blocks.RemoveAll(AgainstThePage);
            sink.AddRange(pageAnchored);
            if (blocks.Count == 0) return pageAnchored.Count > 0;
            if (blocks.Exists(b => b is M.Paragraph { Placement.ContainingBox: var cb } && ReferenceEquals(cb, ownBox)))
            {
                blocks.Insert(0, ownBox);
                blocks.Add(new M.BoxEnd());
            }

            double width = ResolvePlacementOffset(el, "width", sec.PageWidthPt, againstBox, out double? widthShare) ?? 0;

            // The first block carries the placement, the blocks after it ride along in
            // AbsolutePlacement.Following and FollowingBlocks; a placed paragraph among them sends the
            // rest to the flow, reported below. An element opening with another block — a table, a
            // list, a box — is placed by an anchor of no height, every block after it.
            var placed = blocks[0] as M.Paragraph;
            int firstFollowing = 1;
            if (placed == null)
            {
                if (AllFollow(blocks, 0))
                {
                    placed = new M.Paragraph { SpacingBefore = 0, SpacingAfter = 0 };
                    firstFollowing = 0;
                }
                else
                {
                    ReportApproximation($"position:{position} on a {blocks[0].GetType().Name} was laid " +
                                     "out in the flow: a positioned block among its blocks cannot be placed with them.");
                    foreach (var block in blocks) sink.Add(block);
                    return true;
                }
            }

            placed.Placement = containing != null
                // Against the ancestor's padding box: each offset as stated, the exporter resolving
                // the ones the box's size decides and the shares of it.
                ? new M.AbsolutePlacement
                {
                    ContainingBox = containing,
                    LeftPt = left ?? 0, LeftFraction = leftShare, AnchorsLeft = left != null || right == null,
                    RightPt = right, RightFraction = rightShare,
                    TopPt = top ?? 0, TopFraction = topShare, AnchorsTop = top != null || bottom == null,
                    BottomPt = bottom, BottomFraction = bottomShare,
                    WidthPt = width, WidthFraction = widthShare,
                    ZIndex = ReadZIndex(el),
                }
                // Against the page's edges, the exporter measuring what the block's height or content
                // width decides — a bottom anchor, a right anchor with no width. With no offset on an
                // axis the block starts at the page's margin on it: there is no static position here.
                : new M.AbsolutePlacement
                {
                    LeftPt = left ?? (right == null ? sec.MarginLeft : 0), AnchorsLeft = left != null || right == null,
                    RightPt = right,
                    TopPt = top ?? (bottom == null ? sec.MarginTop : 0), AnchorsTop = top != null || bottom == null,
                    BottomPt = bottom,
                    WidthPt = width,
                    ZIndex = ReadZIndex(el),
                    // Print media lays a fixed block out on every page (CSS 2.1 §9.6.1), not once,
                    // like absolute.
                    EveryPage = fixedBlock,
                };
            sink.Add(placed);

            if (blocks.Count > firstFollowing)
            {
                // The blocks after the first are drawn under it, each where the one above ended, so
                // the exporter measures and the mapper need not: paragraphs alone in
                // AbsolutePlacement.Following, as before, and with anything else among them — a table,
                // a list, a box, a float — all of them in order in FollowingBlocks, laid out as a
                // cell's blocks are. A positioned block among them sends the rest to the flow, reported.
                bool allParagraphs = true;
                for (int i = firstFollowing; i < blocks.Count && allParagraphs; i++)
                    allParagraphs = IsFollowingParagraph(blocks[i]);
                if (allParagraphs)
                {
                    for (int i = firstFollowing; i < blocks.Count; i++) placed.Placement.Following.Add((M.Paragraph)blocks[i]);
                }
                else if (AllFollow(blocks, firstFollowing))
                {
                    for (int i = firstFollowing; i < blocks.Count; i++) placed.Placement.FollowingBlocks.Add(blocks[i]);
                }
                else
                {
                    ReportApproximation($"position:{position} placed only the first of {blocks.Count} " +
                                     "child blocks; the rest were left in the flow, because a positioned block " +
                                     "among them cannot be placed with them.");
                    for (int i = 1; i < blocks.Count; i++) sink.Add(blocks[i]);
                }
            }

            return true;
        }

        /// <summary>A paragraph that can be drawn under a placed one: itself in the flow, neither placed nor floated.</summary>
        private static bool IsFollowingParagraph(M.IDocElement block)
            => block is M.Paragraph p && p.Placement == null && p.Float == M.FloatSide.None;

        /// <summary>
        /// Whether every block from <paramref name="from"/> on can follow a placed paragraph in
        /// <see cref="M.AbsolutePlacement.FollowingBlocks"/>: what a table cell holds — a paragraph in
        /// the flow (a float among them), a table, a list or list item, a box's start or end — which
        /// the exporter lays out as it lays out a cell; and a paragraph placed against a box among them
        /// (a positioned element in a positioned one), which the cell's layout draws with that box. Any
        /// other placed paragraph is not.
        /// </summary>
        private static bool AllFollow(List<M.IDocElement> blocks, int from)
        {
            var among = blocks.GetRange(from, blocks.Count - from);
            for (int i = from; i < blocks.Count; i++)
                if (!M.AbsolutePlacement.CanFollow(blocks[i], among))
                    return false;
            return true;
        }

        /// <summary>
        /// A length property resolved to points, or <c>null</c> when absent or not a length this
        /// engine can resolve. A percentage is taken against <paramref name="basis"/>.
        /// </summary>
        private double? ResolveOffset(IElement el, string property, double basis)
        {
            string? raw = StyleValue(el, property);
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string v = raw!.Trim();
            // `auto` is CSS's "work it out from the flow", which is exactly what cannot be done
            // here — treated as absent so the caller's fallbacks apply and report.
            if (Eq(v, "auto")) return null;

            double resolved = ResolveWidth(v, basis, double.NaN);
            return double.IsNaN(resolved) ? (double?)null : resolved;
        }

        private int ReadZIndex(IElement el)
        {
            string? raw = StyleValue(el, "z-index");
            if (raw != null && int.TryParse(raw.Trim(), out int z)) return z;
            return 0;
        }

        /// <summary>
        /// Walks <paramref name="el"/>'s blocks into <paramref name="sink"/> as <see cref="WalkBlocks"/>
        /// does — inside a bare box when the element is positioned, the containing block of the
        /// positioned blocks among them. A form or fieldset is walked without the CSS box a
        /// container gets, so this is what gives a positioned one an edge to measure from.
        /// </summary>
        private void WalkBlocksAsPositionedAncestor(IElement el, M.Section sec, List<M.IDocElement> sink,
            int listLevel, int depth, Style st, M.TextAlignment? align)
        {
            if (!IsPositioningAncestor(el)) { WalkBlocks(el, sec, sink, listLevel, depth, st, align); return; }
            var box = new M.BoxStart();
            sink.Add(box);
            _positionedAncestors.Push((el, box));
            try { WalkBlocks(el, sec, sink, listLevel, depth, st, align); }
            finally { _positionedAncestors.Pop(); }
            sink.Add(new M.BoxEnd());
        }

        /// <summary>
        /// The positioned children of a flex or grid container, mapped after its items into
        /// <paramref name="sink"/> — inside the container's box, so a box that is their containing block
        /// finds them among its blocks. An absolutely positioned child takes no part in the
        /// flex or grid layout (CSS Flexbox §4.1, CSS Grid §9); it was an item. One with no offset to
        /// place by is a block in the flow, as it is anywhere.
        /// </summary>
        private void MapPositionedChildren(List<IElement> positioned, M.Section sec, List<M.IDocElement> sink,
            int listLevel, int depth, Style st, M.TextAlignment? align)
        {
            foreach (IElement child in positioned)
            {
                if (!TakeNode()) break;
                MapBlock(child, sec, sink, listLevel, depth + 1, st, align);
            }
        }
    }
}
