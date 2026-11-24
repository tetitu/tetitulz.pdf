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
        // ============================================================= block walk

        private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        {
            HtmlTags.P, HtmlTags.Div, HtmlTags.Section, HtmlTags.Article, HtmlTags.Header,
            HtmlTags.Footer, HtmlTags.Main,
            HtmlTags.H1, HtmlTags.H2, HtmlTags.H3, HtmlTags.H4, HtmlTags.H5, HtmlTags.H6,
            HtmlTags.Ul, HtmlTags.Ol, HtmlTags.Table, HtmlTags.Blockquote, HtmlTags.Pre,
            HtmlTags.Hr, HtmlTags.Figure, HtmlTags.FigCaption, HtmlTags.Nav, HtmlTags.Aside,
            HtmlTags.Address, HtmlTags.Dl, HtmlTags.Dt, HtmlTags.Dd,
            HtmlTags.Form, HtmlTags.FieldSet,
            // display:block in the HTML rendering rules. Collected inline, a <legend> ran into the
            // first line of what followed it, and its border-bottom (Bootstrap's) became an inline
            // box around the caption.
            HtmlTags.Legend,
            // Obsolete but everywhere in HTML e-mail: a block that centres (ResolveAlign). Walked as
            // inline, a <center> round a layout table flattened the whole message into one line.
            HtmlTags.Center,
        };

        /// <summary>
        /// Tags with no inline meaning of their own — no link, no emphasis, no replaced content —
        /// which CSS may therefore make blocks (<see cref="IsBlockLevel"/>, <see cref="WalkNodes"/>)
        /// without the block path losing anything: it maps an element's children, never the
        /// element, so an <c>&lt;a&gt;</c> would lose its link and an <c>&lt;img&gt;</c> itself.
        /// </summary>
        private static readonly HashSet<string> BlockifiableTags = new(StringComparer.OrdinalIgnoreCase)
        {
            HtmlTags.Span, HtmlTags.Label,
            // A list item not displayed as one — Bootstrap's .list-group-item is a block.
            HtmlTags.Li,
        };

        /// <summary>
        /// Whether <paramref name="el"/> is laid out as a block: a block tag, or a tag CSS may
        /// blockify (<see cref="BlockifiableTags"/>) whose <c>display</c> is block-level. Bootstrap
        /// makes a <c>&lt;label&gt;</c> or a <c>&lt;span&gt;</c> a block this way; read by tag alone,
        /// it ran into the line of whatever followed.
        /// </summary>
        private static bool IsBlockLevel(IElement el) =>
            BlockTags.Contains(el.LocalName)
            || (BlockifiableTags.Contains(el.LocalName) && IsBlockDisplay(StyleValue(el, "display")));

        /// <summary>
        /// Whether <paramref name="nodes"/>[<paramref name="i"/>] is a float CSS makes a block (CSS
        /// 2.1 §9.7) that stands among blocks: a <see cref="BlockifiableTags"/> element floated, with
        /// no inline content on either side of it — only blocks, other floats, whitespace or the
        /// parent's start or end. A floated <c>&lt;label class="col-xs-3"&gt;</c> before a
        /// <c>&lt;div class="col-xs-9"&gt;</c> is one of a form row's two columns.
        /// </summary>
        /// <remarks>
        /// <b>Next to inline content it is not</b>: a float in running text belongs to that text's
        /// line, where the inline-float lift places it (a <c>.badge</c> after a word). Taken as a
        /// block there, it fell below the line it floated in.
        /// </remarks>
        private bool IsFloatAmongBlocks(List<INode> nodes, int i)
        {
            if (nodes[i] is not IElement el || !BlockifiableTags.Contains(el.LocalName)
                || ResolveFloatSide(el) == FloatSide.None)
                return false;

            bool IsInlineContent(INode n) =>
                n is IElement e
                    ? !IsHidden(e) && !IsBlockLevel(e) && !IsBlockControl(e) && ResolveFloatSide(e) == FloatSide.None
                    : n.NodeType == NodeType.Text && !IsIgnorableText(n);

            for (int k = i - 1; k >= 0; k--)
            {
                if (IsInlineContent(nodes[k])) return false;
                if (nodes[k] is IElement e && ResolveFloatSide(e) == FloatSide.None && !IsHidden(e)) break;
            }
            for (int k = i + 1; k < nodes.Count; k++)
            {
                if (IsInlineContent(nodes[k])) return false;
                if (nodes[k] is IElement e && ResolveFloatSide(e) == FloatSide.None && !IsHidden(e)) break;
            }
            return true;
        }

        /// <summary>The <c>display</c> values that make a box block-level.</summary>
        private static bool IsBlockDisplay(string? display) =>
            display?.Trim().ToLowerInvariant() is "block" or "list-item" or "table" or "flex" or "grid";

        private void WalkBlocks(INode parent, M.Section sec, List<M.IDocElement> sink, int listLevel,
            int depth, Style inherited, M.TextAlignment? inheritedAlign)
        {
            var nodes = parent.ChildNodes.ToList();
            WalkNodes(parent, nodes, 0, nodes.Count, sec, sink, listLevel, depth, inherited, inheritedAlign);
        }

        /// <summary>
        /// <see cref="WalkBlocks"/> over <paramref name="nodes"/>[<paramref name="from"/>..<paramref name="to"/>),
        /// a run of <paramref name="parent"/>'s children: a multi-column block walks the children
        /// between two spanning ones as one segment.
        /// </summary>
        private void WalkNodes(INode parent, List<INode> nodes, int from, int to, M.Section sec, List<M.IDocElement> sink,
            int listLevel, int depth, Style inherited, M.TextAlignment? inheritedAlign)
        {
            if (TooDeep(depth)) return;
            M.Paragraph? pending = null;
            void Flush()
            {
                if (pending != null && pending.Runs.Count > 0)
                {
                    if (_clearPending) { pending.ClearFloats = true; _clearPending = false; }
                    sink.Add(pending);
                }
                pending = null;
            }

            // Indexed rather than foreach: a float band spans SIBLINGS -- the floated element plus
            // the content that flows beside it -- so the walk has to be able to consume a run.
            for (int i = from; i < to && i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!TakeNode()) break;
                if (node is IElement el
                    && (IsBlockLevel(el) || IsBlockControl(el) || IsFloatAmongBlocks(nodes, i)))
                {
                    Flush();
                    int bandStart = sink.Count;
                    // A clear left pending is the band's to place, not a block inside a float's:
                    // held back while the floats and the content beside them are mapped, so the
                    // first paragraph of a floated box of blocks does not take it.
                    bool clearBeforeBand = _clearPending;
                    _clearPending = false;
                    if (TryMapFloatBand(nodes, ref i, sec, sink, listLevel, depth, inherited, inheritedAlign))
                    {
                        // Floats after a clear start below the floats before it: the first real
                        // float carries it — a paragraph or a floated box. A band's
                        // table cannot, and it is dropped there.
                        if (clearBeforeBand && sink.Count > bandStart && sink[bandStart] is M.Paragraph firstFloat)
                            firstFloat.ClearFloats = true;
                        else if (clearBeforeBand && sink.Count > bandStart && sink[bandStart] is M.BoxStart firstBox)
                            firstBox.ClearFloats = true;
                        continue;
                    }
                    _clearPending = clearBeforeBand;

                    // `clear` has to be carried onto the block itself now that a float can be real.
                    // The band form got it structurally -- a table ends the side-by-side region, so
                    // whatever followed was simply the next block -- but a real float does not end
                    // until its own bottom, and a float taller than the text beside it would go on
                    // narrowing the rest of the section. Marked on the FIRST block the element
                    // produces, because that is the one whose placement the statement is about.
                    int before = sink.Count;
                    // A float TryMapFloatBand left as a block — nothing beside it — still has its
                    // width, on its side (a Bootstrap progress bar): marked for the paragraph and
                    // box paths, which otherwise leave a float's width to the float layouts. Never
                    // cleared: it is compared by reference, and an element's own width is resolved
                    // before its children are walked, so a stale one matches nothing.
                    if (ResolveFloatSide(el) != FloatSide.None) _loneFloat = el;
                    bool clearBefore = _clearPending;
                    MapBlock(el, sec, sink, listLevel, depth + 1, inherited, inheritedAlign);

                    // A clear an earlier element left pending lands on this one's first block —
                    // unless a descendant already took it: a box of blocks takes it as it is made
                    // (TakePendingClear), so its frame starts below the floats with its
                    // content; a paragraph takes it here; another block drops it, as it always was.
                    if (clearBefore && _clearPending && sink.Count > before)
                    {
                        if (sink[before] is M.Paragraph next) next.ClearFloats = true;
                        _clearPending = false;
                    }

                    if (ClearsFloat(el))
                    {
                        if (sink.Count > before && sink[before] is M.Paragraph cleared) cleared.ClearFloats = true;
                        else if (sink.Count > before && sink[before] is M.BoxStart clearedBox) clearedBox.ClearFloats = true;
                        // An empty clearing element — the clearfix `<div style="clear:both">` —
                        // produces no block to carry it: the next block does, here or after the
                        // container closes, or the text after the floats would go on beside them.
                        else if (sink.Count == before) _clearPending = true;
                    }
                }
                else
                {
                    pending ??= NewPara(parent as IElement, sec, inheritedAlign, inherited);
                    CollectInlineNode(node, pending, inherited, depth + 1);
                }
            }
            Flush();
        }

        private void MapBlock(IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel,
            int depth, Style inherited, M.TextAlignment? inheritedAlign)
        {
            if (TooDeep(depth) || IsHidden(el)) return;
            int firstBlock = sink.Count;   // this element's blocks, for break-inside / break-after
            var align = ResolveAlign(el) ?? inheritedAlign;

            // Before anything is mapped: say so if this element asks for a layout this engine cannot
            // express. Deliberately here rather than in the `default:` arm, because `display: grid`
            // and `position: absolute` are just as meaningful on a <table> or a <ul> as on a <div>,
            // and an unreported one is a wrong-looking PDF that claims to be fine. An absolutely
            // positioned or fixed element is reported after the switch, only when it was not placed.
            ReportEngineLayoutLimits(el);
            bool mappedAsPositioned = false;

            // CSS forced page breaks (page-break-before / break-before). Emitted as an empty
            // break-marker paragraph so it works regardless of the element type (div/table/list).
            if (HasForcedPageBreak(el, "page-break-before") || HasForcedPageBreak(el, "break-before"))
                sink.Add(new M.Paragraph { PageBreakBefore = true, SpacingAfter = 0 });

            string tag = el.LocalName.ToLowerInvariant();
            switch (tag)
            {
                case HtmlTags.H1:
                case HtmlTags.H2:
                case HtmlTags.H3:
                case HtmlTags.H4:
                case HtmlTags.H5:
                case HtmlTags.H6:
                    sink.Add(Heading(el, tag, sec, HeadingSizesEm[tag] * inherited.Size, depth, inherited, align));
                    break;

                case HtmlTags.Hr:
                    sink.Add(new M.Paragraph
                    {
                        SpacingBefore = 4,
                        SpacingAfter = 4,
                        BorderBottom = new M.Border(HrRuleWidthPt, HrRuleColor),
                        Runs = { new M.TextRun { Text = "", FontSize = 1 } },
                        // A rule is decoration. Tagged as a /P it would be a paragraph containing
                        // nothing — hollow tagging, which is exactly what a PDF/UA checker flags.
                        IsArtifact = true,
                    });
                    break;

                case HtmlTags.Ul:
                case HtmlTags.Ol:
                    {
                        var st = DeriveStyle(inherited, el, null, false);
                        // A list displayed as a flex or grid container lays its items out as the
                        // container does (a horizontal nav: `ul { display: flex }`), its items the
                        // items, not a column of bullets whatever its display says.
                        if (IsFlexContainer(el) && MapFlexContainer(el, sec, sink, listLevel, depth, inherited, align)) break;
                        if (IsGridContainer(el) && MapGridContainer(el, sec, sink, listLevel, depth, inherited, align)) break;
                        if (IsInlineList(el))
                        {
                            // Items laid out in a line are a paragraph of their content, not a
                            // column of bullets. Their own ::before / ::after come along — a
                            // breadcrumb's `li + li:before { content: "/ " }` separator is that.
                            var p = NewPara(el, sec, align, st);
                            ApplyBlockStyle(el, p, st.Size);
                            CollectInlineList(el, p, st, depth + 1);
                            if (p.Runs.Count > 0) sink.Add(p);
                            break;
                        }
                        // Items its CSS makes blocks are blocks, not list items: a .list-group is a
                        // stack of bordered boxes, and a list of bullets drew them as plain lines.
                        if (IsBlockItemList(el))
                        {
                            MapContainer(el, sec, sink, listLevel, depth, st, align, role: null);
                            break;
                        }
                        MapList(el, sec, sink, tag == HtmlTags.Ol, listLevel, depth, st, align);
                        break;
                    }
                case HtmlTags.Table: MapTable(el, sec, sink, depth, DeriveStyle(inherited, el, null, false), align); break;

                case HtmlTags.Pre:
                    {
                        var st = DeriveStyle(inherited, el, 10, false);
                        var p = NewPara(el, sec, align, st);
                        p.Role = PdfStructureTypes.Code;
                        // The UA stylesheet's `pre { font-family: monospace; white-space: pre }`:
                        // the family only where the page names none for it — it was forced over a
                        // declared one — and no background, which browsers do not give it (a grey
                        // one was painted whatever the page said; its CSS background is applied below).
                        if (StyleValue(el, "font-family") == null && StyleValue(el, "font") == null)
                        {
                            // The UA rule is on the element, so it replaces an inherited family:
                            // a named one (a Georgia body) had won over monospace by its name.
                            st.Mono = true; st.Serif = false; st.FontFamily = null;
                        }
                        st.Pre = true;
                        ApplyBlockStyle(el, p, st.Size);
                        CollectInline(el, p, st, depth + 1);
                        if (p.Runs.Count > 0) sink.Add(p);
                        break;
                    }

                // A container like any other — its CSS box round its blocks, or a paragraph — with
                // the UA stylesheet's margins (DefaultBlockMarginEm, DefaultInlineMarginPx) — no house
                // style: a grey bar on the LEFT and fixed indents whatever the page says would never
                // draw Bootstrap-RTL's bar on the right and would draw one where it has none.
                case HtmlTags.Blockquote:
                    MapContainer(el, sec, sink, listLevel, depth, DeriveStyle(inherited, el, null, false), align,
                        PdfStructureTypes.BlockQuote);
                    break;

                // A container with its UA margins (1em above and below) and its CSS box — it walked
                // its children with neither, so Bootstrap's 20px under a <dl> was lost.
                case HtmlTags.Dl:
                    MapContainer(el, sec, sink, listLevel, depth, DeriveStyle(inherited, el, null, false), align, null);
                    break;
                // A <fieldset> groups controls and a <legend> names the group; both are ordinary
                // blocks here, with the legend emphasised the way a browser's default stylesheet
                // does.
                // Walked without a CSS box of their own; a positioned one keeps a bare box, the containing
                // block of the positioned blocks inside it.
                case HtmlTags.Form: WalkBlocksAsPositionedAncestor(el, sec, sink, listLevel, depth, DeriveStyle(inherited, el, null, false), align); break;
                case HtmlTags.FieldSet:
                    {
                        var fs = DeriveStyle(inherited, el, null, false);
                        WalkBlocksAsPositionedAncestor(el, sec, sink, listLevel, depth, fs, align);
                        break;
                    }
                case HtmlTags.Dt:
                    {
                        var st = DeriveStyle(inherited, el, null, true);
                        var p = NewPara(el, sec, align, st);
                        ApplyBlockStyle(el, p, st.Size);
                        CollectInline(el, p, st, depth + 1);
                        if (p.Runs.Count > 0) sink.Add(p);
                        break;
                    }
                case HtmlTags.Dd:
                    {
                        var st = DeriveStyle(inherited, el, null, false);
                        var p = NewPara(el, sec, align, st);
                        ApplyBlockStyle(el, p, st.Size);   // its start-side 40px (DefaultSideMarginsPt)
                        CollectInline(el, p, st, depth + 1);
                        if (p.Runs.Count > 0) sink.Add(p);
                        break;
                    }

                default:
                    {
                        if (IsBlockControl(el)) { MapBlockControl(el, sec, sink, inherited, align); break; }

                        // display:flex places children side by side; ordinary block flow stacks
                        // them. Checked before the generic case so a flex container that also has
                        // block children is not silently laid out as a column.
                        if (IsFlexContainer(el) &&
                            MapFlexContainer(el, sec, sink, listLevel, depth, inherited, align))
                            break;

                        // display:grid, on the same terms and for the same reason: a table is the
                        // only construct in the model that places blocks side by side. Declining
                        // falls through to block flow below, which is the right answer for a grid
                        // with no usable column template — one column of blocks IS ordinary flow.
                        if (IsGridContainer(el) &&
                            MapGridContainer(el, sec, sink, listLevel, depth, inherited, align))
                            break;

                        // CSS multi-column, on the same terms again. Checked after grid because an
                        // element can carry both and grid is the more specific statement about how
                        // its children are placed.
                        if (IsColumnContainer(el) &&
                            MapColumnContainer(el, sec, sink, listLevel, depth, inherited, align))
                            break;

                        // position:absolute / fixed. Checked LAST of the layout mappings, because
                        // taking an element out of flow is a statement about where the ELEMENT
                        // goes, while flex/grid/columns are statements about where its CHILDREN go
                        // — and a positioned flex container is still a flex container inside.
                        if (IsPositioned(el) &&
                            MapPositioned(el, sec, sink, listLevel, depth, inherited, align))
                        {
                            mappedAsPositioned = true;
                            break;
                        }

                        MapContainer(el, sec, sink, listLevel, depth, DeriveStyle(inherited, el, null, false), align, role: null);
                        break;
                    }
            }

            // MapPositioned reports its own fallbacks; an element it never saw stayed in the flow.
            if (!mappedAsPositioned) ReportUnplacedOutOfFlow(el);

            if (HasForcedPageBreak(el, "page-break-after") || HasForcedPageBreak(el, "break-after"))
                sink.Add(new M.Paragraph { PageBreakBefore = true, SpacingAfter = 0 });

            ApplyBreakAvoidance(el, sink, firstBlock);
        }

        /// <summary>
        /// <c>break-inside: avoid</c> / <c>page-break-inside: avoid</c> and <c>break-after: avoid</c>
        /// / <c>page-break-after: avoid</c>, as keep flags on the blocks <paramref name="el"/>
        /// produced, <paramref name="firstBlock"/> onwards.
        /// </summary>
        /// <remarks>
        /// <para>An element is often several blocks here — a <c>&lt;div&gt;</c> of paragraphs is its
        /// paragraphs, since the model has no container — so "keep it together" is each of them kept
        /// together and chained to the next with <c>KeepWithNext</c>, which the exporter measures as
        /// one group. "Keep it with the next" is the last one chained to whatever follows.</para>
        /// <para>Rows need nothing: a table row is never split across pages, which is what
        /// <c>tr { page-break-inside: avoid }</c> — in Bootstrap's print stylesheet — asks for.</para>
        /// </remarks>
        private static void ApplyBreakAvoidance(IElement el, List<M.IDocElement> sink, int firstBlock)
        {
            bool inside = AvoidsBreak(el, "page-break-inside") || AvoidsBreak(el, "break-inside");
            bool after = AvoidsBreak(el, "page-break-after") || AvoidsBreak(el, "break-after");
            if (!inside && !after) return;

            int end = sink.Count;
            // An element that is one box keeps as a box: together, and with what follows it — the exporter
            // measuring the box whole. Its blocks are not chained: a chain runs through a box's end into the
            // block after it, which avoid-inside does not ask for; and avoid-after is kept at the box's end,
            // where the last block is.
            if (end - firstBlock >= 2 && sink[firstBlock] is M.BoxStart own && M.BoxStart.IndexOfEnd(sink, firstBlock, end) == end - 1)
            {
                if (inside) own.KeepTogether = true;
                if (after) own.KeepWithNext = true;
                return;
            }
            for (int b = firstBlock; b < end; b++)
            {
                bool lastBlock = b == end - 1;
                bool keepWithNext = (inside && !lastBlock) || (after && lastBlock);
                switch (sink[b])
                {
                    case M.Paragraph p:
                        if (inside) p.KeepTogether = true;
                        if (keepWithNext) p.KeepWithNext = true;
                        break;
                    case M.Table t:
                        if (inside) t.KeepTogether = true;
                        if (keepWithNext) t.KeepWithNext = true;
                        break;
                    // The element's last block closes a box: that box keeps with what follows.
                    case M.BoxEnd when after && lastBlock && M.BoxEnd.IndexOfStart(sink, b) is int opened && opened >= firstBlock:
                        ((M.BoxStart)sink[opened]).KeepWithNext = true;
                        break;
                }
            }
        }

        /// <summary>True when a CSS break property asks to avoid a page break (<c>avoid</c> / <c>avoid-page</c>).</summary>
        private static bool AvoidsBreak(IElement el, string prop)
        {
            string? v = StyleValue(el, prop);
            if (v == null) return false;
            string t = v.Trim().ToLowerInvariant();
            return t is "avoid" or "avoid-page";
        }

        // True when a CSS break property forces a page break (always / page / left / right / recto / verso).
        private static bool HasForcedPageBreak(IElement el, string prop)
        {
            string? v = StyleValue(el, prop);
            if (v == null) return false;
            string t = v.Trim().ToLowerInvariant();
            return t is "always" or "page" or "left" or "right" or "recto" or "verso";
        }

        /// <summary>
        /// A block element in ordinary flow: its own margins, border, padding and background round
        /// its blocks (<see cref="M.BoxStart"/>), or — holding only inline content — one paragraph
        /// with them, tagged <paramref name="role"/> when given.
        /// </summary>
        private void MapContainer(IElement el, M.Section sec, List<M.IDocElement> sink, int listLevel,
            int depth, Style st, M.TextAlignment? align, string? role)
        {
            // A stated height needs a box to hold it — a paragraph has none — and an empty element
            // with one (a progress bar's track, a spacer) is still drawn: it made no paragraph and
            // was dropped.
            if (HasBlockChild(el) || DeclaresBlockHeight(el, st.Size) || ReferenceEquals(el, _stretchItem))
            {
                // The box's opacity is what the element adds to the enclosing box's; its blocks
                // then carry only what they add to the box's.
                M.BoxStart? box = ResolveBlockBox(el, st.Size, st.Direction, RelativeToGroup(st.Opacity));
                // A positioned element is the containing block of the positioned blocks inside it
                //: a box with nothing to draw still gives them an edge to measure from.
                bool anchorsPositioned = IsPositioningAncestor(el);
                if (box == null && anchorsPositioned) box = new M.BoxStart();
                if (box is { ClipsContent: true }) ReportOverflowIfDeclared(el);
                if (box != null) { TakePendingClear(box); sink.Add(box); }
                double outerGroup = _groupOpacity;
                if (box != null && box.Opacity < 1) _groupOpacity = st.Opacity;
                if (anchorsPositioned) _positionedAncestors.Push((el, box!));
                try { WalkBlocks(el, sec, sink, listLevel, depth, st, align); }
                finally
                {
                    _groupOpacity = outerGroup;
                    if (anchorsPositioned) _positionedAncestors.Pop();
                }
                if (box != null) sink.Add(new M.BoxEnd());
                return;
            }

            var p = NewPara(el, sec, align, st);
            if (role != null) p.Role = role;
            ApplyBlockStyle(el, p, st.Size);
            CollectInline(el, p, st, depth + 1);
            if (p.Runs.Count > 0) sink.Add(p);
        }

        /// <summary>
        /// Hands a <c>clear</c> an empty clearing element left pending to the box of blocks being
        /// made (<see cref="M.BoxStart.ClearFloats"/>) — before its blocks are walked, so
        /// the box's frame starts below the floats rather than its first paragraph alone, with the
        /// frame beside them.
        /// </summary>
        private void TakePendingClear(M.BoxStart box)
        {
            if (!_clearPending) return;
            box.ClearFloats = true;
            _clearPending = false;
        }

        private bool HasBlockChild(IElement el)
        {
            foreach (var n in el.ChildNodes)
                if (n is IElement c && (IsBlockLevel(c) || IsBlockControl(c))) return true;
            return false;
        }

        /// <summary>
        /// Builds a heading paragraph. <paramref name="tag"/> is the caller's already-lower-cased
        /// tag name, so no second <c>LocalName.ToLowerInvariant()</c> allocates a copy of a string the
        /// caller holds.
        /// </summary>
        private M.Paragraph Heading(IElement el, string tag, M.Section sec, double size, int depth,
            Style inherited, M.TextAlignment? align)
        {
            var st = DeriveStyle(inherited, el, size, true);
            var p = NewPara(el, sec, align, st);
            // The heading LEVEL is the whole point: a tree of undifferentiated /P elements passes
            // structural checks while telling a screen reader nothing about the document's shape.
            p.Role = tag switch
            {
                HtmlTags.H1 => PdfStructureTypes.H1,
                HtmlTags.H2 => PdfStructureTypes.H2,
                HtmlTags.H3 => PdfStructureTypes.H3,
                HtmlTags.H4 => PdfStructureTypes.H4,
                HtmlTags.H5 => PdfStructureTypes.H5,
                _ => PdfStructureTypes.H6,
            };
            ApplyBlockStyle(el, p, st.Size);
            CollectInline(el, p, st, depth + 1);
            if (p.Runs.Count == 0) p.Runs.Add(new M.TextRun { Text = "", FontSize = st.Size });
            return p;
        }
    }
}
