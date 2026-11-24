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
        // ============================================================= block styling

        // Applies block-level CSS (background colour → shading, background-image → the paragraph's
        // background image, margin/padding → indent + spacing, line-height, text-indent,
        // border[-side] → paragraph borders) to a block paragraph.
        //
        // Instance rather than static since the background-image arm reads bytes, and every local
        // read in this engine goes through the one ResolveLocalPath trust boundary. All six call
        // sites were already instance methods.
        private void ApplyBlockStyle(IElement el, M.Paragraph p, double fontSize)
        {
            // border-radius / box-shadow / transform, and the overflow report. Here rather than at
            // the block callers for the same reason the borders below are: this is the one place
            // that turns an element's block-level CSS into a paragraph, so a property added at a
            // caller instead would apply to whichever blocks that caller happens to build.
            ApplyBoxDecoration(el, p);

            string? blockBg = ResolveBackgroundColor(el);
            if (blockBg != null) p.Shading = blockBg;

            var bgImage = ResolveBackgroundImage(el);
            if (bgImage != null) p.BackgroundImage = bgImage;

            // Vertical margins may be negative (they pull the block up, and collapse by CSS's
            // rule for negatives); horizontal ones stay clamped, as indents.
            var (mt, mr, mb, ml) = ResolveSides(el, "margin", fontSize, allowNegative: true);   // a paragraph's
            mr = NonNeg(mr);
            ml = NonNeg(ml);
            // An edge the page does not set takes the browser's own (DefaultBlockMarginEm): a <p>
            // is 1em from its neighbours, a <div> nothing.
            var (uaTop, uaBottom) = DefaultBlockMarginEm(el.LocalName);
            if (!DeclaresMargin(el, "margin-top")) mt = uaTop * fontSize;
            if (!DeclaresMargin(el, "margin-bottom")) mb = uaBottom * fontSize;
            var (uaLeft, uaRight) = DefaultSideMarginsPt(el.LocalName, p.Direction);
            if (!DeclaresMargin(el, "margin-left")) ml = uaLeft;   // a paragraph's
            if (!DeclaresMargin(el, "margin-right")) mr = uaRight;
            var (ptp, prp, pbp, plp) = ResolveSides(el, "padding", fontSize);

            // The border takes room too, between the margin and the padding: the text starts past
            // all three, as CSS's content box does. It took none, so a bordered block's text sat a
            // border's width short of the browser's.
            BorderEdge[] edges = ResolveBorderEdges(el);
            double btp = DrawnWidth(edges[EdgeTop]), brp = DrawnWidth(edges[EdgeRight]);
            double bbp = DrawnWidth(edges[EdgeBottom]), blp = DrawnWidth(edges[EdgeLeft]);

            if (ml + blp + plp > 0) p.IndentLeft += ml + blp + plp;
            if (mr + brp + prp > 0) p.IndentRight += mr + brp + prp;
            if (mt + btp + ptp != 0) p.SpacingBefore += mt + btp + ptp;
            if (mb + bbp + pbp != 0) p.SpacingAfter += mb + bbp + pbp;

            // The padding is INSIDE the box. Indents and spacing put the text past margin, border
            // and padding alike, and a box drawn a fixed inset from the text — inside the padding,
            // which would then read as margin — would put a bordered `padding: 8px` block's text
            // against the border and its border 8px in from where the page put it. The inset is the
            // padding and half the border, since the exporter strokes a border along the middle of
            // its band: the band then lies exactly between margin and padding (ParagraphFrame).
            p.BoxInsetTop = ptp + btp / 2;
            p.BoxInsetRight = prp + brp / 2;
            p.BoxInsetBottom = pbp + bbp / 2;
            p.BoxInsetLeft = plp + blp / 2;
            // CSS's box: no padding is no gap, not the 2pt Word's paragraph borders keep.
            p.BoxInsetIsExact = true;

            // Its width, as a share of what it is laid out in: the exporter knows that width, the
            // mapper does not (a panel's padding, a table cell). Not a float's: it has its own.
            if (TakesItsOwnWidth(el))
            {
                ApplyBlockWidth(el, p, fontSize, plp + prp + blp + brp);
                p.WidthAtEnd = WidthAtEnd(el, p.Direction);
            }

            string? ti = StyleValue(el, "text-indent");
            if (ti != null) { double t = ParseLength(ti, 0, fontSize); if (t > 0) p.FirstLineIndent = t; }

            // An edge that is PRESENT wins even when it paints nothing — `border-top:none` after
            // `border:1px solid` must remove the top — which is what Present is for. On a
            // paragraph null already draws nothing, so a suppressed edge is simply null here.
            if (edges[EdgeTop].Present) p.BorderTop = edges[EdgeTop].ToBorder();
            if (edges[EdgeRight].Present) p.BorderRight = edges[EdgeRight].ToBorder();
            if (edges[EdgeBottom].Present) p.BorderBottom = edges[EdgeBottom].ToBorder();
            if (edges[EdgeLeft].Present) p.BorderLeft = edges[EdgeLeft].ToBorder();
        }

        /// <summary>
        /// CSS <c>width</c> on a block, as <see cref="M.Paragraph.WidthFraction"/> (a percentage)
        /// or <see cref="M.Paragraph.WidthPt"/> (a length) of its box: under <c>box-sizing:
        /// content-box</c>, CSS's default, the width is the text's and <paramref name="frame"/> —
        /// padding and border — is added; under <c>border-box</c> (Bootstrap's, on everything) it
        /// is the box's already. <c>auto</c> and anything unreadable leave the full width.
        /// </summary>
        private void ApplyBlockWidth(IElement el, M.Paragraph p, double fontSize, double frame)
        {
            if (ResolveCssWidth(el, fontSize, frame) is not { } width) return;
            p.WidthFraction = width.Fraction;
            p.WidthPt = width.Pt;
        }

        /// <summary>
        /// CSS <c>width</c> as a box width — a fraction of the containing width plus points — for a
        /// paragraph (<see cref="ApplyBlockWidth"/>) and a box of blocks
        /// (<see cref="ResolveBlockBox"/>) alike: under <c>box-sizing: content-box</c>, CSS's
        /// default, <paramref name="frame"/> (padding and border) is added; under
        /// <c>border-box</c> (Bootstrap's) it is the box's already. <c>null</c> for <c>auto</c>, none,
        /// or anything unreadable.
        /// </summary>
        private (double? Fraction, double Pt)? ResolveCssWidth(IElement el, double fontSize, double frame)
        {
            string? w = StyleValue(el, "width")?.Trim();
            if (string.IsNullOrEmpty(w) || Eq(w, "auto")) return null;
            bool borderBox = Eq(StyleValue(el, "box-sizing")?.Trim(), "border-box");
            double extra = borderBox ? 0 : frame;

            if (w!.EndsWith("%", StringComparison.Ordinal))
            {
                if (double.TryParse(w.Substring(0, w.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) && pct >= 0)
                    return (pct / 100, extra);
                return null;
            }
            double pt = ParseLength(w, 0, fontSize);
            return pt > 0 ? (null, pt + extra) : ((double?, double)?)null;
        }

        /// <summary>
        /// The least border-box height <paramref name="el"/>'s CSS asks for, in points: the larger
        /// of <c>height</c> and <c>min-height</c>, each an absolute length, the content box's
        /// plus <paramref name="frame"/> (its vertical padding and borders) unless
        /// <c>box-sizing: border-box</c>; a viewport unit is of the page area (<c>ParseLength</c>).
        /// 0 for none — <c>auto</c>, and a percentage, of a height the flow does not know, which
        /// <c>ParseLength</c> declines. A stated <c>height</c> is taken
        /// as a minimum: content taller than it would overflow the box in CSS, and here grows it.
        /// </summary>
        private double ResolveCssHeight(IElement el, double fontSize, double frame)
        {
            bool borderBox = Eq(StyleValue(el, "box-sizing")?.Trim(), "border-box");
            double least = 0;
            foreach (string name in new[] { "height", "min-height" })
            {
                double pt = ParseLength(StyleValue(el, name), 0, fontSize);
                if (pt > 0) least = Math.Max(least, pt + (borderBox ? 0 : frame));
            }
            return least;
        }

        /// <summary>Whether <paramref name="el"/> asks for a height its box must have (<see cref="ResolveCssHeight"/>).</summary>
        private bool DeclaresBlockHeight(IElement el, double fontSize) => ResolveCssHeight(el, fontSize, 0) > 0;

        /// <summary>
        /// The float <c>WalkNodes</c> is mapping as a block because it has nothing beside it, or
        /// <c>null</c>: such a float keeps its CSS width on its side, where every other float's width
        /// is the float layouts' (a band's track, an exact float's <c>FloatWidth</c>) and applying it
        /// again inside them would shrink the float twice.
        /// </summary>
        private IElement? _loneFloat;

        /// <summary>
        /// A <c>clear</c> an empty element stated, waiting for the next block to carry it as
        /// <c>Paragraph.ClearFloats</c> (<c>WalkNodes</c>).
        /// </summary>
        private bool _clearPending;

        /// <summary>
        /// The flex item being mapped that stretches to its row (<c>FlexItemStretches</c>): a
        /// container of text alone takes the box path for it, since a paragraph has no box to
        /// stretch. <c>null</c> outside one.
        /// </summary>
        private IElement? _stretchItem;

        /// <summary>
        /// The accumulated CSS opacity of the innermost box of blocks being mapped that carries one
        /// (<see cref="M.BoxStart.Opacity"/>) — 1 outside any. A block inside it carries
        /// only what it adds on top (<see cref="RelativeToGroup"/>), since the box's group paints the
        /// rest, as a run carries only what it adds to its paragraph's.
        /// </summary>
        private double _groupOpacity = 1.0;

        /// <summary><paramref name="opacity"/>, an accumulated value, relative to the enclosing opacity box's: what a block inside it draws at.</summary>
        private double RelativeToGroup(double opacity)
        {
            if (_groupOpacity <= 0) return 1.0;    // inside a box at 0 nothing shows; keep the arithmetic finite
            double rel = opacity / _groupOpacity;
            return rel < 0 ? 0 : rel > 1 ? 1 : rel;
        }

        /// <summary>Whether <paramref name="el"/>'s CSS width is its box's: a block not floated, or the lone float.</summary>
        private bool TakesItsOwnWidth(IElement el) =>
            (ResolveFloatSide(el) == FloatSide.None || ReferenceEquals(el, _loneFloat)) && !IsFlexRowItem(el);

        /// <summary>
        /// Whether <paramref name="el"/> is an item of a flex row, whose <c>width</c> is its flex
        /// basis — the track <c>ResolveFlexTrack</c> gives it — and so not a width it takes again
        /// inside that track: a signature line at 40% drew at 40% of 40%.
        /// </summary>
        private bool IsFlexRowItem(IElement el)
            => el.ParentElement is { } parent && IsFlexContainer(parent)
               && !FlexFlow(parent).Direction.StartsWith("column", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a narrower box goes at its band's end: a lone float floated to the side opposite
        /// the start — right in a left-to-right page, left in a right-to-left one.
        /// </summary>
        private bool WidthAtEnd(IElement el, M.TextDirection? direction)
        {
            FloatSide side = ResolveFloatSide(el);
            if (side == FloatSide.None) return false;
            bool rtl = direction == M.TextDirection.RightToLeft;
            return (side == FloatSide.Right) != rtl;
        }

        /// <summary>The width a border edge paints, or 0 when it paints nothing.</summary>
        private static double DrawnWidth(BorderEdge edge) => M.Border.PaintedWidth(edge.ToBorder());

        /// <summary>
        /// What of a paragraph's spacing on each side is its box rather than its margin: the inset
        /// (padding and half the border) and the other half of the border. The margin collapse and
        /// a box moved off the paragraph (<c>BoxOf</c>) both take the margin as what is left.
        /// </summary>
        private static (double Top, double Right, double Bottom, double Left) ParagraphFrame(M.Paragraph p)
            => (p.BoxInsetTop + M.Border.PaintedWidth(p.BorderTop) / 2, p.BoxInsetRight + M.Border.PaintedWidth(p.BorderRight) / 2,
                p.BoxInsetBottom + M.Border.PaintedWidth(p.BorderBottom) / 2, p.BoxInsetLeft + M.Border.PaintedWidth(p.BorderLeft) / 2);

        /// <summary>Whether <paramref name="el"/>'s CSS sets <paramref name="longhand"/>, itself or through the <c>margin</c> shorthand.</summary>
        private static bool DeclaresMargin(IElement el, string longhand) =>
            StyleValue(el, longhand) != null || StyleValue(el, "margin") != null;

        /// <summary>
        /// The vertical margins, in em of the element's own font size, the HTML UA stylesheet gives a
        /// block that its page's CSS leaves unset (HTML §15.3.3, §15.3.6, §15.3.7): 1em round a
        /// paragraph, a quotation, preformatted text and a list; 0.67em to 2.33em round the six
        /// headings; nothing round a <c>div</c> or any other element.
        /// </summary>
        /// <remarks>
        /// These replace a flat 6pt after every block, which was a paragraph gap on a page of
        /// <c>div</c>s, where a browser has none, and half of one on a page of <c>p</c>s.
        /// </remarks>
        private static (double Top, double Bottom) DefaultBlockMarginEm(string tag) =>
            tag.ToLowerInvariant() switch
            {
                HtmlTags.P or HtmlTags.Blockquote or HtmlTags.Pre or HtmlTags.Dl
                    or HtmlTags.Ul or HtmlTags.Ol => (1, 1),
                HtmlTags.H1 => (0.67, 0.67),
                HtmlTags.H2 => (0.83, 0.83),
                HtmlTags.H3 => (1, 1),
                HtmlTags.H4 => (1.33, 1.33),
                HtmlTags.H5 => (1.67, 1.67),
                HtmlTags.H6 => (2.33, 2.33),
                _ => (0, 0),
            };

        /// <summary>
        /// The horizontal margins, in points, the HTML UA stylesheet gives a block its page leaves
        /// unset, as (left, right) for <paramref name="direction"/>: 40px each side of a
        /// <c>&lt;blockquote&gt;</c>, and 40px on the start side of a <c>&lt;dd&gt;</c> — its
        /// <c>margin-inline-start</c>, the right in a right-to-left block (HTML §15.3.3, §15.3.7);
        /// none for any other.
        /// </summary>
        /// <remarks>
        /// From the UA stylesheet and the page, not a fixed 24pt left indent whatever the page says,
        /// which would indent Bootstrap's <c>dd { margin-left: 0 }</c> anyway, and a right-to-left one
        /// on the wrong side.
        /// </remarks>
        private static (double Left, double Right) DefaultSideMarginsPt(string tag, M.TextDirection? direction)
        {
            double side = PdfUnits.PixelsToPoints(DefaultSideMarginPx);
            if (string.Equals(tag, HtmlTags.Blockquote, StringComparison.OrdinalIgnoreCase)) return (side, side);
            if (string.Equals(tag, HtmlTags.Dd, StringComparison.OrdinalIgnoreCase))
                return direction == M.TextDirection.RightToLeft ? (0, side) : (side, 0);
            return (0, 0);
        }

        /// <summary>The UA stylesheet's 40px — a blockquote's sides, a definition's start.</summary>
        internal const double DefaultSideMarginPx = 40;

        /// <summary>
        /// Collapses vertical margins through every block list of the document — a section's and
        /// each table cell's — as CSS 2.1 §8.3.1 does: margins that meet become the largest of them,
        /// not their sum. Paragraphs' margins, and the margins of the boxes round them
        /// (<see cref="M.BoxStart"/>), take part alike.
        /// </summary>
        /// <remarks>
        /// <para><b>One pass, carrying the margin already placed above the current position</b>
        /// (<c>above</c>). Each margin that meets it keeps only what it exceeds it by, so the space
        /// is their maximum. A box's top edge meets the margin above it, and — when no border or
        /// padding stands between — its first child's too; its bottom edge likewise with its last
        /// child's, and then with what follows. A border or padding stops the collapse at that
        /// edge: the box's own margin still meets its neighbour outside.</para>
        /// <para><b>Only the margin collapses.</b> A paragraph's <c>SpacingBefore</c> and
        /// <c>SpacingAfter</c> also hold its padding (<c>BoxInsetTop</c>, <c>BoxInsetBottom</c>),
        /// which never does. A table's margins meet like a paragraph's (its cells are lists of their
        /// own), and so do a list's; a loose list item ends the chain; a floated or a positioned
        /// paragraph — out of flow — takes no part.</para>
        /// <para><b>Negative margins collapse by CSS's own rule:</b> the margins that meet are the
        /// largest positive one plus the most negative one. The chain carries both, and each margin
        /// adds only what it changes of that sum.</para>
        /// <para>It replaces a collapse of a box with its first and last paragraph alone, made
        /// before the paragraphs around the box were known — so a <c>&lt;p&gt;</c> before a panel,
        /// or after it, added its whole margin to the panel's.</para>
        /// </remarks>
        private static void CollapseAdjacentMargins(List<M.IDocElement> blocks)
        {
            // The margins already met above the current position, as CSS combines them: the
            // largest positive and the most negative. Null where nothing is left to collapse with.
            (double Pos, double Neg)? above = null;
            var open = new Stack<M.BoxStart>();

            static (double Pos, double Neg) Start(double margin) => (Math.Max(0, margin), Math.Min(0, margin));

            // The part of `margin` the flow still has to add once it meets `above`, which it joins.
            static double Meet(double margin, ref (double Pos, double Neg)? above)
            {
                if (above is not { } met) { above = Start(margin); return margin; }
                var joined = (Pos: Math.Max(met.Pos, Math.Max(0, margin)), Neg: Math.Min(met.Neg, Math.Min(0, margin)));
                above = joined;
                return (joined.Pos + joined.Neg) - (met.Pos + met.Neg);
            }

            foreach (var block in blocks)
            {
                switch (block)
                {
                    case M.Paragraph { Float: not M.FloatSide.None }:
                        break;

                    // Out of flow: a positioned paragraph between two blocks neither
                    // meets their margins nor parts them — they collapse through it, as CSS 2.1
                    // §8.3.1 has it; it stood between them and doubled the space.
                    case M.Paragraph { Placement: not null }:
                        break;

                    case M.Paragraph p:
                        var frame = ParagraphFrame(p);
                        double top = Meet(p.SpacingBefore - frame.Top, ref above);
                        p.SpacingBefore = frame.Top + top;
                        above = Start(p.SpacingAfter - frame.Bottom);
                        break;

                    case M.BoxStart box:
                        open.Push(box);
                        box.MarginTop = Meet(box.MarginTop, ref above);
                        bool openTop = box.BorderTop == null && box.PaddingTop <= 0;
                        if (!openTop) above = null;
                        break;

                    case M.BoxEnd when open.Count > 0:
                        var closing = open.Pop();
                        bool openBottom = closing.BorderBottom == null && closing.PaddingBottom <= 0;
                        if (openBottom) closing.MarginBottom = Meet(closing.MarginBottom, ref above);
                        else above = Start(closing.MarginBottom);
                        break;

                    case M.ListBlock list:
                        list.SpacingBefore = Meet(list.SpacingBefore, ref above);
                        above = Start(list.SpacingAfter);
                        break;

                    case M.Table table:
                        table.SpacingBefore = Meet(table.SpacingBefore, ref above);
                        foreach (var row in table.Rows)
                            foreach (var cell in row.Cells)
                                CollapseAdjacentMargins(cell.Blocks);
                        above = Start(table.SpacingAfter);
                        break;

                    default:
                        above = null;
                        break;
                }
            }
        }

        /// <summary>
        /// Records <paramref name="el"/>'s own <c>line-height</c> on <paramref name="st"/> — which
        /// every descendant's style is cloned from, so that is how it inherits (see
        /// <see cref="Style.LineHeightFactor"/>). An element that declares none leaves the
        /// inherited value alone. Called once the element's font size is known, since a length in
        /// <c>em</c> or a percentage is computed against it here, where it was declared.
        /// </summary>
        private void ApplyLineHeight(IElement el, Style st)
        {
            string? lh = StyleValue(el, "line-height");
            if (lh == null) return;
            string v = lh.Trim();
            if (Eq(v, "normal")) { st.LineHeightFactor = 0; st.LineHeightPt = 0; return; }
            if (IsCssNumber(v, signed: false)
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double factor))
            {
                st.LineHeightFactor = factor;
                st.LineHeightPt = 0;
                return;
            }
            double pt = ParseLineHeight(v, st.Size);
            if (pt > 0) { st.LineHeightFactor = 0; st.LineHeightPt = pt; }
        }

        private double ParseLineHeight(string v, double fontSize)
        {
            v = v.Trim();
            if (Eq(v, "normal")) return 0;
            if (v.EndsWith("%", StringComparison.Ordinal))
                return double.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)
                    ? pct / 100 * fontSize : 0;
            if (IsCssNumber(v, signed: true)) // unitless multiplier
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double mul)
                    ? mul * fontSize : 0;
            return ParseLength(v, 0, fontSize);
        }

        // Resolves a box property (margin/padding) to (top,right,bottom,left) in points,
        // combining the shorthand with any per-side longhand overrides. Negative values read as 0
        // unless the caller can place a negative margin (a block box, BoxStart).
        private (double t, double r, double b, double l) ResolveSides(IElement el, string prop, double em,
            bool allowNegative = false)
        {
            double Keep(double value) => allowNegative ? value : NonNeg(value);
            double t = 0, r = 0, b = 0, l = 0;
            string? sh = StyleValue(el, prop);
            if (sh != null) (t, r, b, l) = ParseBoxShorthand(sh, em, allowNegative);
            string? v;
            if ((v = StyleValue(el, prop + "-top")) != null) t = Keep(ParseLength(v, 0, em));
            if ((v = StyleValue(el, prop + "-right")) != null) r = Keep(ParseLength(v, 0, em));
            if ((v = StyleValue(el, prop + "-bottom")) != null) b = Keep(ParseLength(v, 0, em));
            if ((v = StyleValue(el, prop + "-left")) != null) l = Keep(ParseLength(v, 0, em));
            return (t, r, b, l);
        }

        private (double, double, double, double) ParseBoxShorthand(string v, double em, bool allowNegative = false)
        {
            var parts = v.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return (0, 0, 0, 0);
            double P(int i) => allowNegative ? ParseLength(parts[i], 0, em) : NonNeg(ParseLength(parts[i], 0, em));
            return parts.Length switch
            {
                1 => (P(0), P(0), P(0), P(0)),
                2 => (P(0), P(1), P(0), P(1)),
                3 => (P(0), P(1), P(2), P(1)),
                _ => (P(0), P(1), P(2), P(3)),
            };
        }

        private static double NonNeg(double v) => v > 0 ? v : 0;

        /// <summary>
        /// The box a block element draws round its block children — its margins, border, padding,
        /// background and corner radius, as a <see cref="M.BoxStart"/> — or <c>null</c> when it has
        /// none of them.
        /// </summary>
        /// <remarks>
        /// <para>An element whose content is blocks keeps its box: if only a paragraph could carry a
        /// border or padding, a Bootstrap <c>.panel</c> would print its contents with no frame and a
        /// <c>.panel-body</c> none of its padding, and <c>.row</c>'s negative margins — which
        /// cancel <c>.container</c>'s padding so a grid's gutters line up with the text around it —
        /// would be dropped with it.</para>
        /// <para><b>A horizontal margin may be negative</b>, which widens the band; a vertical one is
        /// read as 0, since pulling a block up over the one before it is not something the flow can
        /// draw. <c>auto</c> reads as 0.</para>
        /// </remarks>
        private M.BoxStart? ResolveBlockBox(IElement el, double fontSize, M.TextDirection? direction = null, double opacity = 1.0)
        {
            var (mt, mr, mb, ml) = ResolveSides(el, "margin", fontSize, allowNegative: true);
            // The UA stylesheet's margins for an edge the page leaves unset, as a paragraph gets
            // them (ApplyBlockStyle): a <blockquote> of paragraphs is 1em from its neighbours and
            // 40px in from each side.
            var (uaTop, uaBottom) = DefaultBlockMarginEm(el.LocalName);
            if (!DeclaresMargin(el, "margin-top")) mt = uaTop * fontSize;   // the box's
            if (!DeclaresMargin(el, "margin-bottom")) mb = uaBottom * fontSize;
            var (uaLeft, uaRight) = DefaultSideMarginsPt(el.LocalName, direction);
            if (!DeclaresMargin(el, "margin-left")) ml = uaLeft;   // the box's
            if (!DeclaresMargin(el, "margin-right")) mr = uaRight;
            var (pt, pr, pb, pl) = ResolveSides(el, "padding", fontSize);
            BorderEdge[] edges = ResolveBorderEdges(el);

            var box = new M.BoxStart
            {
                MarginTop = mt, MarginBottom = mb, MarginLeft = ml, MarginRight = mr,
                PaddingTop = pt, PaddingRight = pr, PaddingBottom = pb, PaddingLeft = pl,
                BorderTop = edges[EdgeTop].ToBorder(), BorderRight = edges[EdgeRight].ToBorder(),
                BorderBottom = edges[EdgeBottom].ToBorder(), BorderLeft = edges[EdgeLeft].ToBorder(),
                Shading = ResolveBackgroundColor(el) ?? "",
                BackgroundImage = ResolveBackgroundImage(el),
                // Its opacity relative to the enclosing box's: the exporter paints each fragment as
                // one layer at it, never its fill opaque and its blocks each at the accumulated alpha.
                Opacity = opacity < 0 ? 0 : opacity > 1 ? 1 : opacity,
            };

            if (ResolveCornerPercent(el) is double pct) box.CornerRadiusPercent = pct;
            else if (ResolveCornerRadii(el, fontSize) is { } radii)
            {
                (box.CornerRadiusPt, box.CornerRadiiPt) = radii.Uniform is double u ? (u, (double[]?)null) : (0, radii.Corners);
                box.CornerRadiiYPt = radii.CornersY;
                box.CornerRadiiPercent = radii.Percents; box.CornerRadiiYPercent = radii.PercentsY;
            }

            // Its width, as a share of the band it opens in (TakesItsOwnWidth).
            if (TakesItsOwnWidth(el)
                && ResolveCssWidth(el, fontSize, pl + pr + M.Border.PaintedWidth(box.BorderLeft) + M.Border.PaintedWidth(box.BorderRight)) is { } width)
            {
                box.WidthFraction = width.Fraction;
                box.WidthPt = width.Pt;
                box.WidthAtEnd = WidthAtEnd(el, direction);
            }

            // Its shadows, as a paragraph's (ResolveShadows): a box of blocks read none, so a
            // .panel's and a .well's were never drawn.
            var shadows = ResolveShadows(el);
            if (shadows.Count > 0)
            {
                box.Shadow = shadows[0];
                box.MoreShadows.AddRange(shadows.Skip(1));
            }

            box.ClipsContent = ClipsOverflow(el);
            box.MinHeightPt = ResolveCssHeight(el, fontSize,
                pt + pb + M.Border.PaintedWidth(box.BorderTop) + M.Border.PaintedWidth(box.BorderBottom));

            bool any = box.MinHeightPt > 0 || box.MarginTop != 0 || box.MarginBottom != 0 || ml != 0 || mr != 0
                       || box.WidthFraction != null || box.WidthPt > 0
                       || pt + pr + pb + pl > 0
                       || box.BorderTop != null || box.BorderRight != null || box.BorderBottom != null || box.BorderLeft != null
                       || box.Shading.Length > 0 || box.BackgroundImage != null || box.Shadow != null
                       || box.ClipsContent || box.Opacity < 1;
            return any ? box : null;
        }

        // A colour value that must paint nothing and let the inherited value stand. These are
        // keywords rather than colours, so TryNormalizeColor already declines every one of them;
        // the check is kept as an explicit, cheap short-circuit at the call sites that have a
        // "leave it alone" branch of their own. `transparent` is not here: it is a colour
        // (CssColor reads it, alpha 0), and TryNormalizeColor declines every fully transparent one.
        private static bool IsTransparent(string v)
        {
            v = v.Trim();
            return v.Equals("inherit", StringComparison.OrdinalIgnoreCase)
                || v.Equals("initial", StringComparison.OrdinalIgnoreCase)
                || v.Equals("unset", StringComparison.OrdinalIgnoreCase)
                || v.Equals("revert", StringComparison.OrdinalIgnoreCase)
                || v.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)
                || v.Equals("none", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The background colour an element sets — from the <c>background-color</c> longhand, the
        /// <c>background</c> shorthand, or the legacy <c>bgcolor</c> attribute, in that order — or
        /// <c>null</c> when it sets none. Shared by the block (paragraph shading) and table-cell
        /// paths so the two cannot disagree about what counts as a background.
        /// </summary>
        private static string? ResolveBackgroundColor(IElement el)
        {
            var (property, value) = DecidingDeclaration(el, "background-color", "background");
            if (property == "background-color")
                return IsTransparent(value!) ? null : TryNormalizeColor(value!);
            if (property == "background")
                return IsTransparent(value!) ? null : TryExtractBackgroundColor(value);

            string? legacy = el.GetAttribute("bgcolor");
            return legacy != null ? TryNormalizeColor(legacy) : null;
        }

        /// <summary>
        /// Of a longhand and the shorthand that also sets it, the declaration that decides the value
        /// — the later in cascade order, an <c>!important</c> one over any normal one — as
        /// (property, value), or (null, null) when the element declares neither.
        /// </summary>
        /// <remarks>
        /// <para>The later declaration wins: a shorthand written after the longhand resets it. Bootstrap's print
        /// stylesheet is exactly that — <c>* { background: transparent !important }</c> over
        /// <c>.alert-success { background-color: #dff0d8 }</c> — and a browser prints no fill.</para>
        /// <para>The resolved <c>style</c> attribute is in cascade rank order (see
        /// <see cref="ResolveBorderEdges"/>), so position in it is the answer; an inline-only
        /// document is never folded, and its <c>!important</c> is honoured here.</para>
        /// </remarks>
        private static (string? Property, string? Value) DecidingDeclaration(IElement el, string longhand, string shorthand)
        {
            var map = DeclarationMap(el);
            bool hasLong = map.TryGetValue(longhand, out string? longValue);
            bool hasShort = map.TryGetValue(shorthand, out string? shortValue);
            if (!hasLong && !hasShort) return (null, null);
            if (hasLong != hasShort) return hasLong ? (longhand, longValue) : (shorthand, shortValue);

            string? property = null, value = null;
            bool important = false;
            foreach (string decl in SplitDeclarations(el.GetAttribute("style")))
            {
                if (!TrySplitDeclaration(decl, out string p, out string v, out bool imp)) continue;
                p = p.ToLowerInvariant();
                if (p != longhand && p != shorthand) continue;
                if (important && !imp) continue;     // a normal declaration never beats an important one
                important |= imp;
                property = p;
                value = v;
            }
            return (property, value);
        }

        /// <summary>
        /// The colour a <c>background</c> shorthand sets, or <c>null</c> when it sets none.
        ///
        /// <para>The shorthand carries an image, a position, a size and repeat flags alongside the
        /// colour, so the whole value rarely parses as one. Tokenising and taking the first
        /// component that <i>is</i> a colour keeps <c>background: #eee url(bg.png) no-repeat</c>
        /// painting <c>#eee</c> — while <c>background: url(bg.png)</c> correctly yields nothing,
        /// not a black box.</para>
        /// </summary>
        private static string? TryExtractBackgroundColor(string? shorthand)
        {
            if (string.IsNullOrEmpty(shorthand)) return null;
            string? direct = TryNormalizeColor(shorthand);
            if (direct != null) return direct;
            foreach (string tok in SplitTopLevelTokens(shorthand!))
            {
                string? c = TryNormalizeColor(tok);
                if (c != null) return c;
            }
            return null;
        }

        // ============================================================= background-image

        /// <summary>
        /// The background image an element sets — from the <c>background-image</c> longhand or the
        /// <c>background</c> shorthand's <c>url()</c> component — or <c>null</c> when it sets none
        /// or the bytes cannot be used.
        ///
        /// <para><b>The url goes through <see cref="ResolveLocalPath"/>, like every other local
        /// read in this engine.</b> A background url is a fresh SSRF / path-traversal surface, and
        /// the <c>@font-face</c> loader's doc comment already states the rule this follows: a
        /// second path guard here would be a second thing to get wrong. Nothing outside
        /// <see cref="HtmlToPdfOptions.BasePath"/> is read, and remote <c>http(s)</c> goes through
        /// <see cref="LoadRemote"/> — the one remote path — or is not fetched at all.</para>
        ///
        /// <para>Decodability is decided here, where the url is still known, for the same reason
        /// <c>MapImage</c> decides it here: the exporter sees only bytes, so its diagnostic can
        /// name a byte count and nothing else.</para>
        /// </summary>
        private M.BackgroundImage? ResolveBackgroundImage(IElement el)
        {
            string? url = BackgroundImageUrl(el);
            if (url == null) return null;

            bool isDataUri = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            bool isRemote = !isDataUri && IsRemoteUrl(url);
            byte[]? data = isDataUri ? DecodeDataUri(url)
                         : isRemote ? LoadRemote(url, "background image", BuilderDiagnosticKind.ImageDecodeFailed)
                         : LoadLocalImage(url);      // → ResolveLocalPath: out-of-base returns null
            if (data == null || data.Length == 0)
            {
                // The second of the three silent-drop sites. `url(https://…)` and a path outside
                // BasePath both landed here and returned null, so a document lost its backgrounds
                // with nothing on OnDiagnostic — while an undecodable one four lines below reported.
                // A remote url has already said why, in LoadRemote.
                if (!isDataUri && !isRemote) ReportResourceDeclined("background image", url);
                return null;
            }

            if (!BuilderImageDecoder.CanDecode(data, out string? why))
            {
                ReportDiagnostic(BuilderDiagnosticKind.ImageDecodeFailed,
                    $"background-image url({Excerpt(url)}) was not painted: {why}.");
                return null;
            }

            var (px, py) = ParseBackgroundPosition(el);
            return new M.BackgroundImage
            {
                Data = data,
                Repeat = ParseBackgroundRepeat(el),
                Size = ParseBackgroundSize(el),
                PositionX = px,
                PositionY = py,
            };
        }

        /// <summary>
        /// <c>background-position</c> as the fractions <see cref="M.BackgroundImage.PositionX"/> and
        /// <c>PositionY</c> take: the keywords and percentages, from the longhand or the shorthand,
        /// whichever the cascade put later.
        /// </summary>
        /// <remarks>
        /// One value sets the axis its keyword names (a percentage is horizontal) and centres the
        /// other; two are horizontal then vertical, unless a keyword says otherwise
        /// (<c>top left</c>). A length offset is not a fraction of anything the mapper knows — it
        /// depends on the tile's size against the box's — so it, and the four-value form, leave
        /// the top-left default.
        /// </remarks>
        private static (double X, double Y) ParseBackgroundPosition(IElement el)
        {
            var (property, value) = DecidingDeclaration(el, "background-position", "background");
            if (property == null || value == null) return (0, 0);

            if (property == "background")
            {
                // The shorthand's position tokens: drop the image, and the size after a slash.
                var withoutUrls = new System.Text.StringBuilder(value.Length);
                int from = 0;
                while (NextUrl(value, from, out int start, out int end, out _))
                {
                    withoutUrls.Append(value, from, start - from).Append(' ');
                    from = end;
                }
                value = withoutUrls.Append(value, from, value.Length - from).ToString();
                int slash = value.IndexOf('/');
                if (slash >= 0) value = value.Substring(0, slash);
            }

            var tokens = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant())
                .Where(t => t is "left" or "right" or "top" or "bottom" or "center" || t.EndsWith("%", StringComparison.Ordinal))
                .ToList();

            static double? Fraction(string t) => t switch
            {
                "left" or "top" => 0,
                "center" => 0.5,
                "right" or "bottom" => 1,
                _ => double.TryParse(t.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double p)
                    ? p / 100 : null,
            };

            if (tokens.Count == 1)
            {
                double f = Fraction(tokens[0]) ?? 0;
                return tokens[0] is "top" or "bottom" ? (0.5, f) : tokens[0] == "center" ? (0.5, 0.5) : (f, 0.5);
            }
            if (tokens.Count == 2)
            {
                // `top left` / `bottom right`: the vertical keyword came first.
                bool swapped = tokens[0] is "top" or "bottom" || tokens[1] is "left" or "right";
                string h = swapped ? tokens[1] : tokens[0], v = swapped ? tokens[0] : tokens[1];
                return (Fraction(h) ?? 0, Fraction(v) ?? 0);
            }
            return (0, 0);
        }

        /// <summary>
        /// The <c>url(...)</c> a background declaration names, or <c>null</c>.
        ///
        /// <para><c>background-image: none</c> and the other "leave it alone" keywords yield
        /// <c>null</c> through <see cref="IsTransparent"/> — the same set
        /// <see cref="ResolveBackgroundColor"/> consults, so the two halves of one CSS property
        /// cannot disagree about what "unset" means. Gradients are not supported and produce
        /// nothing rather than a wrong solid fill.</para>
        /// </summary>
        private static string? BackgroundImageUrl(IElement el)
        {
            // The later of the two decides, as for the colour: a `background` shorthand written after
            // a `background-image` resets the image.
            var (property, value) = DecidingDeclaration(el, "background-image", "background");
            if (property == null || IsTransparent(value!)) return null;
            return UnquoteFamily(MatchUrl(value!) ?? "") is { Length: > 0 } u ? u : null;
        }

        /// <summary>The argument of the first <c>url(...)</c> in a CSS value, or null.</summary>
        private static string? MatchUrl(string value)
            => NextUrl(value, 0, out _, out _, out string argument) ? argument : null;

        /// <summary>
        /// The first <c>url(</c> at or after <paramref name="from"/> (any case) with a <c>)</c> after it: where it
        /// starts, the index past its <c>)</c>, and its argument trimmed. <see langword="false"/> when there is none —
        /// and an opener with no <c>)</c> after it means no later one has one either. Two passes of
        /// <see cref="string.IndexOf(string, int, StringComparison)"/>: unanchored patterns such as
        /// <c>url\([^)]*\)</c> and <c>url\(\s*([^)]*)\s*\)</c> scan from every unclosed opener to the end of the
        /// value, which a style attribute of nothing but <c>url(</c> makes quadratic.
        /// </summary>
        private static bool NextUrl(string value, int from, out int start, out int end, out string argument)
        {
            end = 0;
            argument = "";
            start = value.IndexOf("url(", from, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return false;
            int close = value.IndexOf(')', start + 4);
            if (close < 0) return false;
            argument = value.Substring(start + 4, close - start - 4).Trim();
            end = close + 1;
            return true;
        }

        /// <summary>
        /// <c>background-repeat</c>, read from the longhand and then from the shorthand's keyword
        /// tokens — <c>background: url(x) no-repeat</c> is how it is almost always written.
        /// </summary>
        private static M.BackgroundRepeat ParseBackgroundRepeat(IElement el)
        {
            string value = StyleValue(el, "background-repeat") ?? StyleValue(el, "background") ?? "";
            string v = value.ToLowerInvariant();
            if (v.Contains("no-repeat")) return M.BackgroundRepeat.NoRepeat;
            if (v.Contains("repeat-x")) return M.BackgroundRepeat.RepeatX;
            if (v.Contains("repeat-y")) return M.BackgroundRepeat.RepeatY;
            return M.BackgroundRepeat.Repeat;      // the CSS initial value
        }

        /// <summary>
        /// <c>background-size</c>, restricted to <c>cover</c> / <c>contain</c>. An explicit length
        /// pair is deliberately not honoured: it would need the tile's intrinsic size to resolve
        /// <c>auto</c> and percentages against the box, which the mapper does not have — and a
        /// half-implemented size is worse than the intrinsic one, because it looks handled.
        /// </summary>
        private static M.BackgroundSize ParseBackgroundSize(IElement el)
        {
            string v = (StyleValue(el, "background-size") ?? "").Trim().ToLowerInvariant();
            if (v == "cover") return M.BackgroundSize.Cover;
            if (v == "contain") return M.BackgroundSize.Contain;
            return M.BackgroundSize.Auto;
        }

        // ============================================================= printBackground

        /// <summary>
        /// Removes every background fill from the built model — paragraph shading, run highlight
        /// and table-cell background — for <see cref="HtmlToPdfOptions.PrintBackground"/> =
        /// <c>false</c>.
        ///
        /// <para><b>Why a post-build scrub and not a gate at the source.</b>
        /// <see cref="ResolveBackgroundColor"/> is the one place block shading and cell background
        /// are decided, but it — like <c>ApplyBlockStyle</c>, <c>DeriveStyle</c>,
        /// <c>ApplyInlineStyle</c> and <c>MakeRun</c> above it — is <c>static</c>, while
        /// <c>_options</c> is instance state. Threading the flag down that chain would pull four
        /// partials and about a dozen call sites into instance scope to carry one boolean.
        /// Scrubbing the finished model is one method, and it cannot miss a <i>source</i>: it
        /// enumerates the model's background <i>fields</i>, so a new way to set one is caught here
        /// rather than in whichever partial happened to set it.</para>
        ///
        /// <para><b>Borders are not backgrounds and must survive.</b> A paragraph or cell border,
        /// <c>&lt;hr&gt;</c>'s rule and the <c>&lt;blockquote&gt;</c> bar are all
        /// <c>Model.Border</c>s, drawn as strokes rather than filled rectangles; Chromium keeps
        /// them under <c>printBackground: false</c> and so does this.</para>
        ///
        /// <para>The UA-stylesheet backgrounds go too — <c>&lt;pre&gt;</c>'s grey box,
        /// <c>&lt;mark&gt;</c>'s yellow, a form control's field shading. Chromium drops all three,
        /// and keeping them would make "backgrounds off" a half-truth.</para>
        /// </summary>
        private static void StripBackgrounds(M.Document doc)
        {
            foreach (var section in doc.Sections)
            {
                StripBackgrounds(section.Children, 0);

                // The running blocks are the blind spot this has to cover: the exporter draws
                // them through a separate paginate:false path, so a body-only scrub would leave
                // a shaded running head painting and no body-only test would ever see it
                // (ParagraphBoxTests holds the header/footer case).
                StripBackgrounds(section.Header?.Paragraphs);
                StripBackgrounds(section.FirstPageHeader?.Paragraphs);
                StripBackgrounds(section.EvenPageHeader?.Paragraphs);
                StripBackgrounds(section.Footer?.Paragraphs);
                StripBackgrounds(section.FirstPageFooter?.Paragraphs);
                StripBackgrounds(section.EvenPageFooter?.Paragraphs);
            }
        }

        private static void StripBackgrounds(List<M.Paragraph>? paragraphs)
        {
            if (paragraphs == null) return;
            foreach (var p in paragraphs) StripParagraphBackground(p);
        }

        // Depth-bounded for the same reason WalkBlocks is: a table cell may hold another table,
        // so this recurses as deeply as the markup nested. MaxDepth is the cap the DOM walk built
        // the model under, so the guard cannot fire on anything this mapper produced — it is here
        // so the scrub can never itself be what overflows the stack.
        // Deliberately NOT routed through TooDeep: this one runs over the model the mapper already
        // built, under a cap the DOM walk has already enforced, so it cannot fire on anything real —
        // and a diagnostic that cannot fire is worse than none.
        private static void StripBackgrounds(IEnumerable<M.IDocElement> blocks, int depth)
        {
            if (depth > MaxDepth) return;
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case M.Paragraph p:
                        StripParagraphBackground(p);
                        break;

                    // A box's fill and image are backgrounds; its border and padding are not.
                    case M.BoxStart box:
                        box.Shading = "";
                        box.BackgroundImage = null;
                        break;

                    case M.ListItem li:
                        StripListItemBackground(li, depth);
                        break;

                    // An imported list is a container, so its items are not siblings in this
                    // sequence. Without this arm a `<li style="background:yellow">` would keep its
                    // highlight in a print-background:false render — the scrub walking past the
                    // whole list, because the items are one level down.
                    case M.ListBlock lb:
                        foreach (var item in lb.Items) StripListItemBackground(item, depth + 1);
                        break;

                    case M.Table t:
                        foreach (var row in t.Rows)
                            foreach (var cell in row.Cells)
                            {
                                cell.BackgroundColor = "";
                                // Content() — not Paragraphs — because it returns whichever list
                                // the renderer will actually draw from, so the scrub and the
                                // paint cannot disagree about which one a cell is using.
                                StripBackgrounds(cell.Content(), depth + 1);
                            }
                        break;
                }
            }
        }

        /// <summary>One item's own highlight and its sublist's.</summary>
        /// <remarks>
        /// <c>TextFormat</c> is the marker's run formatting, so a highlighted <c>&lt;li&gt;</c>
        /// highlights its own bullet as well as its text.
        /// </remarks>
        private static void StripListItemBackground(M.ListItem li, int depth)
        {
            if (depth > MaxDepth) return;

            if (li.TextFormat != null) li.TextFormat.Highlight = "";
            StripRunBackgrounds(li.Runs);

            if (li.Sublist == null) return;
            foreach (var nested in li.Sublist.Items) StripListItemBackground(nested, depth + 1);
        }

        private static void StripParagraphBackground(M.Paragraph p)
        {
            p.Shading = "";
            // A background IMAGE is a background: Chromium drops it under printBackground:false
            // exactly as it drops the colour. This is the case the scrub's own doc comment
            // predicted — "a new way to set one is caught here rather than in whichever partial
            // happened to set it" — so it is one line, in one place, and not six.
            p.BackgroundImage = null;
            StripRunBackgrounds(p.Runs);
        }

        private static void StripRunBackgrounds(List<M.IInlineElement> runs)
        {
            foreach (var run in runs)
            {
                if (run is not M.TextRun tr) continue;
                tr.Highlight = "";
                // An inline box's fill and image are backgrounds too; its border and padding are not.
                if (tr.Box != null)
                {
                    tr.Box.Background = "";
                    tr.Box.BackgroundImage = null;
                }
            }
        }

        /// <summary>
        /// A border shorthand value (<c>1px solid #ccc</c>) as the border it draws, or <c>null</c>
        /// when it draws none — absent, <c>none</c>/<c>hidden</c>/<c>0</c>, a zero width, or a
        /// <c>transparent</c> colour.
        /// </summary>
        private M.Border? ParseBorder(string value) => ParseBorderShorthand(value).ToBorder();

        // Indices into ResolveBorderEdges' result, in CSS's own box order (top right bottom left),
        // which is also the order the 1–4 value longhands assign in.
        private const int EdgeTop = 0, EdgeRight = 1, EdgeBottom = 2, EdgeLeft = 3;

        /// <summary>
        /// One edge of a box's border, built up the way CSS builds it: a shorthand sets its width,
        /// style and colour together, and each longhand sets one of them.
        /// </summary>
        /// <remarks>
        /// <para><b><see cref="Present"/> is not "draws".</b> It records that a declaration set this
        /// edge's line style — the shorthand, or a <c>border-style</c> longhand — so the caller can
        /// tell an edge that was <i>suppressed</i> (<c>border-top:none</c>, which must remove the
        /// edge) from one nothing mentioned (which must leave it to the table's default). Sharing one
        /// <c>null</c>, a per-side reset of a shorthand would be a no-op:
        /// <c>td{border:1px solid #ccc} td.first{border-left:none}</c> would paint all four sides.</para>
        ///
        /// <para><b>A colour or width alone does not make an edge present</b>, because CSS's initial
        /// line style is <c>none</c>: <c>border-color:red</c> with no style draws nothing. Treating
        /// it as present would make a table cell's colour-only declaration erase the table's own
        /// grid line on that side.</para>
        /// </remarks>
        private struct BorderEdge
        {
            public bool Present;
            public bool Drawn;
            public double? Width;
            public string? Color;
            public bool Transparent;

            /// <summary>The border this edge paints, or <c>null</c> for none.</summary>
            public M.Border? ToBorder()
            {
                if (!Present || !Drawn || Transparent) return null;
                double w = Width ?? DefaultBorderWidth;
                return w > 0 ? new M.Border(w, Color ?? BlackColor) : null;
            }
        }

        /// <summary>
        /// The width a border takes when no width is declared. CSS says <c>medium</c>; this engine has
        /// always drawn one point, and a stylesheet that omits the width expects a thin line.
        /// </summary>
        private const double DefaultBorderWidth = 1;

        /// <summary>
        /// Parses a border shorthand into an edge. <c>Present</c> is false only for an empty value.
        /// </summary>
        /// <remarks>
        /// <para>A shorthand with no style word still draws. CSS would draw nothing (the omitted
        /// style is <c>none</c>), but <c>border:1px #ccc</c> draws here, and documents
        /// written against this engine rely on it; an explicit <c>none</c> or <c>hidden</c> does
        /// suppress the edge.</para>
        /// <para><c>transparent</c> is a colour that paints nothing, and it is kept as a flag rather
        /// than mapped to no border, because a later <c>border-color</c> may give the same edge a
        /// real colour — the ubiquitous <c>border:1px solid transparent</c> base rule with a
        /// per-variant <c>border-color</c> (every Bootstrap alert, panel and button), which would
        /// otherwise fail to parse as a colour and fall through to black.</para>
        /// </remarks>
        private BorderEdge ParseBorderShorthand(string? value)
        {
            var edge = new BorderEdge();
            string v = value?.Trim() ?? "";
            if (v.Length == 0) return edge;

            edge.Present = true;
            if (v.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("hidden", StringComparison.OrdinalIgnoreCase) || v == "0" || ResetsBorder(v)) return edge;

            edge.Drawn = true;
            foreach (string tok in SplitTopLevelTokens(v))
            {
                string t = tok.ToLowerInvariant();
                if (TryParseBorderWidth(t, out double width)) edge.Width = width;
                else if (IsBorderStyleWord(t)) edge.Drawn = t != "none" && t != "hidden";
                else ApplyBorderColor(ref edge, tok);
                // A token that is neither a width, a style word nor a colour is simply not
                // understood — it must not silently become a black border, nor erase one.
            }
            return edge;
        }

        /// <summary>
        /// A CSS-wide keyword as a border's whole value: the initial border, whose style is
        /// <c>none</c> — no border.
        /// </summary>
        /// <remarks>
        /// It read as a colour keyword that restores the default, so bootstrap-rtl's
        /// <c>border-left: initial</c> on a table's first and last cells drew a black border. The
        /// border properties do not inherit, so <c>unset</c> and <c>revert</c> are the initial value
        /// too; <c>inherit</c> would take the parent's border, which for a cell (its row has none) is
        /// none as well.
        /// </remarks>
        private static bool ResetsBorder(string value) =>
            value.ToLowerInvariant() is "initial" or "unset" or "revert" or "revert-layer" or "inherit";

        /// <summary>A border width keyword or length, in points.</summary>
        private bool TryParseBorderWidth(string lowerCaseToken, out double width)
        {
            switch (lowerCaseToken)
            {
                case "thin": width = 0.75; return true;
                case "medium": width = 1.5; return true;
                case "thick": width = 3; return true;
            }
            width = 0;
            int at = lowerCaseToken.StartsWith("-", StringComparison.Ordinal) ? 1 : 0;
            if (at < lowerCaseToken.Length && lowerCaseToken[at] == '.') at++;
            if (at >= lowerCaseToken.Length || !char.IsDigit(lowerCaseToken[at])) return false;
            width = ParseLength(lowerCaseToken, DefaultBorderWidth);
            return true;
        }

        /// <summary>
        /// Sets an edge's colour from one token: <c>transparent</c> paints nothing, the keywords
        /// that defer (<c>currentcolor</c>, <c>inherit</c>, …) restore the default, and anything
        /// that is not a colour leaves the edge as it was.
        /// </summary>
        private static void ApplyBorderColor(ref BorderEdge edge, string token)
        {
            // Any fully transparent colour, not just the keyword: rgba(0, 0, 0, 0) composited to
            // white and drew a white edge.
            if (Tetitulz.Pdf.Core.Text.CssColor.IsFullyTransparent(token))
            {
                edge.Transparent = true;
                edge.Color = null;
                return;
            }
            if (IsTransparent(token))
            {
                edge.Transparent = false;
                edge.Color = null;
                return;
            }
            if (TryNormalizeColor(token) is { } color)
            {
                edge.Transparent = false;
                edge.Color = color;
            }
        }

        /// <summary>
        /// The border declarations <see cref="ResolveBorderEdges"/> reads — and the gate that keeps
        /// it off every element that declares none of them, which is nearly all of them.
        /// </summary>
        private static readonly HashSet<string> BorderEdgeProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "border", "border-top", "border-right", "border-bottom", "border-left",
            "border-color", "border-width", "border-style",
            "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
            "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
            "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
        };

        /// <summary>
        /// The four edges an element's border declarations produce, in CSS box order, applying the
        /// declarations in the order the cascade ranked them.
        /// </summary>
        /// <remarks>
        /// <para><b>Order is the whole point.</b> A shorthand resets the longhands it covers, so
        /// <c>border:1px solid transparent</c> followed by <c>border-color:#d6e9c6</c> is a green
        /// border and the reverse is a transparent one. <see cref="StyleValue"/> answers one
        /// property at a time and cannot say which came last, so this reads the resolved
        /// <c>style</c> attribute itself: the cascade fold writes it in ascending rank (origin,
        /// specificity, source order), which makes position in it the CSS answer. An inline-only
        /// document is never folded, so its <c>!important</c> declarations are applied last here,
        /// which is where the fold would have put them.</para>
        /// <para>The <c>border-color</c> family, the shorthand and the per-side shorthands all go
        /// through this order; separate <see cref="StyleValue"/> calls in a fixed order would let no
        /// longhand reach an edge.</para>
        /// </remarks>
        private BorderEdge[] ResolveBorderEdges(IElement el)
        {
            var edges = new BorderEdge[4];

            bool any = false;
            foreach (string key in DeclarationMap(el).Keys)
                if (BorderEdgeProperties.Contains(key)) { any = true; break; }
            if (!any) return edges;

            var normal = new List<(string Prop, string Value)>();
            var important = new List<(string Prop, string Value)>();
            foreach (string decl in SplitDeclarations(el.GetAttribute("style")))
                if (TrySplitDeclaration(decl, out string prop, out string value, out bool imp)
                    && BorderEdgeProperties.Contains(prop))
                    (imp ? important : normal).Add((prop.ToLowerInvariant(), value));

            foreach (var (prop, value) in normal) ApplyBorderDeclaration(edges, prop, value);
            foreach (var (prop, value) in important) ApplyBorderDeclaration(edges, prop, value);
            return edges;
        }

        /// <summary>Applies one border declaration to the edges it names.</summary>
        private void ApplyBorderDeclaration(BorderEdge[] edges, string prop, string value)
        {
            switch (prop)
            {
                case "border":
                    BorderEdge all = ParseBorderShorthand(value);
                    if (all.Present) edges[EdgeTop] = edges[EdgeRight] = edges[EdgeBottom] = edges[EdgeLeft] = all;
                    return;
                case "border-top":    SetEdge(edges, EdgeTop, value); return;
                case "border-right":  SetEdge(edges, EdgeRight, value); return;
                case "border-bottom": SetEdge(edges, EdgeBottom, value); return;
                case "border-left":   SetEdge(edges, EdgeLeft, value); return;
            }

            // A longhand: border[-side]-(color|width|style). The side-less form takes one to four
            // values in box order; a per-side one takes exactly one.
            int dash = prop.LastIndexOf('-');
            string part = prop.Substring(dash + 1);
            string sidePart = prop.Substring(0, dash);
            int side = sidePart switch
            {
                "border-top" => EdgeTop,
                "border-right" => EdgeRight,
                "border-bottom" => EdgeBottom,
                "border-left" => EdgeLeft,
                _ => -1,
            };

            var tokens = new List<string>(SplitTopLevelTokens(value.Trim()));
            if (tokens.Count == 0) return;

            if (side >= 0)
            {
                if (tokens.Count == 1) ApplyBorderPart(ref edges[side], part, tokens[0]);
                return;
            }
            if (tokens.Count > 4) return;
            for (int e = 0; e < 4; e++)
                ApplyBorderPart(ref edges[e], part, tokens[BoxValueIndex(tokens.Count, e)]);
        }

        private void SetEdge(BorderEdge[] edges, int side, string value)
        {
            BorderEdge edge = ParseBorderShorthand(value);
            if (edge.Present) edges[side] = edge;
        }

        /// <summary>Applies one <c>color</c>, <c>width</c> or <c>style</c> value to one edge.</summary>
        private void ApplyBorderPart(ref BorderEdge edge, string part, string token)
        {
            string t = token.Trim().ToLowerInvariant();
            switch (part)
            {
                case "color":
                    ApplyBorderColor(ref edge, token);
                    return;
                case "width":
                    if (TryParseBorderWidth(t, out double width)) edge.Width = width;
                    return;
                case "style":
                    if (!IsBorderStyleWord(t)) return;
                    edge.Present = true;
                    edge.Drawn = t != "none" && t != "hidden";
                    return;
            }
        }

        /// <summary>
        /// Which of a 1–4 value box list (<c>margin</c>, <c>border-color</c>, …) applies to edge
        /// <paramref name="edge"/> in top-right-bottom-left order.
        /// </summary>
        private static int BoxValueIndex(int count, int edge) => count switch
        {
            1 => 0,
            2 => edge % 2,
            3 => edge == EdgeLeft ? 1 : edge,
            _ => edge,
        };

        /// <summary>
        /// The <c>&lt;line-style&gt;</c> keywords a border (or column rule) shorthand may carry.
        /// One list, so the column-rule reader and the border parser cannot disagree about which
        /// token is the style and which a colour that failed to parse.
        /// </summary>
        private static bool IsBorderStyleWord(string lowerCaseToken)
            => lowerCaseToken is "solid" or "dashed" or "dotted" or "double" or "groove"
               or "ridge" or "inset" or "outset" or "none" or "hidden";
    }
}
