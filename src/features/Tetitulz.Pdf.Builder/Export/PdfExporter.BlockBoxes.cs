// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Builder.Export
{
    // Boxes around runs of blocks — BoxStart / BoxEnd: the band their
    // blocks lay out in, and the border and background drawn round them, sliced at a page break.
    // Painted behind, though drawn after. A box's height is known only once its blocks
    // are laid out, and its background must lie beneath them. Its position in the page's content is
    // noted where it opens, and its fill and border — drawn when it closes, or when a page break
    // cuts it — are moved back to that point (PdfContentStreamBuilder.MoveTailTo). Boxes close
    // innermost first, and an inner box's position is always the later, so every position still
    // open stays true.
    // The border band is inside the box, as CSS draws it: each side is stroked half its
    // width in from the edge, so a box with a 1pt border spans exactly its margin box less its
    // margins.
    public sealed partial class PdfExporter
    {
        /// <summary>A box open around the flow (or around a cell's blocks), and the fragment it is on.</summary>
        internal sealed class OpenBlockBox
        {
            public OpenBlockBox(BoxStart box, double bandLeft, double bandWidth, bool rtl)
            {
                Box = box;
                _rtl = rtl;
                Rebase(bandLeft, bandWidth);
            }

            private readonly bool _rtl;

            /// <summary>
            /// Lays the box out in the band at <paramref name="bandLeft"/>, <paramref name="bandWidth"/>
            /// wide: as it opens, and again when the flow carries it into a content area of another width
            /// (<see cref="Section.ContentAreas"/>) — a box of no width of its own then fills the new band.
            /// </summary>
            public void Rebase(double bandLeft, double bandWidth)
            {
                BoxStart box = Box;
                bool rtl = _rtl;
                Left = bandLeft + box.MarginLeft;
                Width = Math.Max(1, bandWidth - box.MarginLeft - box.MarginRight);
                // A stated width narrower than the room: the box keeps it, at the band's start (or
                // end), and the room it leaves stays empty. A floated box laid out as a block — the
                // flow found no measure beside it, or it was taller than a page — keeps its float
                // width on the physical side it names the same way; its stated width is
                // not read.
                bool floated = IsFloated(box);
                if (floated || box.WidthFraction is not null || box.WidthPt > 0)
                {
                    double wanted = floated ? box.FloatWidth : (box.WidthFraction ?? 0) * bandWidth + box.WidthPt;
                    double spare = Width - wanted;
                    if (spare > 0)
                    {
                        Width = wanted;
                        if (floated ? box.Float == FloatSide.Right : rtl != box.WidthAtEnd) Left += spare;
                    }
                }
                ContentLeft = Left + BoxLeftInset(box);
                ContentWidth = Math.Max(1, Width - BoxLeftInset(box) - BoxRightInset(box));
            }

            public BoxStart Box { get; }
            /// <summary>The border box's left edge and width.</summary>
            public double Left { get; private set; }
            public double Width { get; private set; }
            /// <summary>The band the box's blocks lay out in.</summary>
            public double ContentLeft { get; private set; }
            public double ContentWidth { get; private set; }
            /// <summary>Where this page's fragment of the box begins, and where in the page's content its paint goes.</summary>
            public double FragmentTop { get; set; }
            public int Mark { get; set; }
            /// <summary>Whether the fragment being laid out is the box's first — the one with its top border.</summary>
            public bool FirstFragment { get; set; } = true;
            /// <summary>
            /// Where in the page's content the fragment's transform goes (<see cref="BeginTransform"/>),
            /// and the annotation counts when the fragment began, for the rectangles it adds to follow it.
            /// </summary>
            public int TransformMark { get; set; }
            public AnnotationMarks Marks { get; set; }
            /// <summary>The annotation counts when the box's blocks began, for <see cref="AlignContent"/> to move the rectangles they add.</summary>
            public AnnotationMarks ContentMarks { get; set; }
            /// <summary>
            /// The content the fragment's transparency group stands in for (<see cref="BeginOpacityFragment"/>),
            /// null when the box is opaque; and the <c>Div</c> the group's <c>Do</c> is marked as.
            /// </summary>
            public PdfContentStreamBuilder? OuterPage { get; set; }
            public PdfStructureElement? GroupElement { get; set; }

            /// <summary>
            /// The positioned paragraphs measured from this box (<see cref="AbsolutePlacement.ContainingBox"/>),
            /// waiting for the fragment their anchor falls in; each is drawn once and removed.
            /// </summary>
            public readonly System.Collections.Generic.List<(Paragraph Para, AbsolutePlacement Place)> Positioned = new();

            /// <summary>The border-box height of this box's fragments already drawn — how far down the box the current fragment's top is.</summary>
            public double HeightBefore { get; set; }
        }

        /// <summary>The open box whose <see cref="BoxStart"/> is <paramref name="box"/>, or null — a placement's containing box, when it is open here.</summary>
        internal static OpenBlockBox? FindOpenBox(System.Collections.Generic.List<OpenBlockBox> open, BoxStart? box)
        {
            if (box == null) return null;
            for (int i = open.Count - 1; i >= 0; i--)
                if (ReferenceEquals(open[i].Box, box)) return open[i];
            return null;
        }

        /// <summary>
        /// Gives <paramref name="host"/>, as it opens, the positioned paragraphs measured from its box
        /// among <paramref name="blocks"/>[<paramref name="start"/> + 1 .. <paramref name="end"/>) — its
        /// blocks to its <see cref="BoxEnd"/>, nested boxes' included — so a paragraph listed after the
        /// box's blocks is known to the box's first fragment, whose page its anchor may fall on. One
        /// placed on every page is the section's, not the box's.
        /// </summary>
        internal static void AttachPositioned(OpenBlockBox host, System.Collections.Generic.IReadOnlyList<IDocElement> blocks, int start, int end)
        {
            for (int i = start + 1; i < end && i < blocks.Count; i++)
                if (blocks[i] is Paragraph { Placement: { EveryPage: false } place } para && ReferenceEquals(place.ContainingBox, host.Box))
                    host.Positioned.Add((para, place));
        }

        internal static double BoxTopInset(BoxStart b) => Border.PaintedWidth(b.BorderTop) + b.PaddingTop;
        internal static double BoxBottomInset(BoxStart b) => b.PaddingBottom + Border.PaintedWidth(b.BorderBottom);
        private static double BoxLeftInset(BoxStart b) => Border.PaintedWidth(b.BorderLeft) + b.PaddingLeft;
        private static double BoxRightInset(BoxStart b) => b.PaddingRight + Border.PaintedWidth(b.BorderRight);

        /// <summary>How much narrower a box's blocks lay out than the band it opens in (less, for a negative margin).</summary>
        internal static double BoxHorizontalInsets(BoxStart b) =>
            b.MarginLeft + b.MarginRight + BoxLeftInset(b) + BoxRightInset(b);

        /// <summary>A box the flow floats: <see cref="BoxStart.Float"/> with a width.</summary>
        private static bool IsFloated(BoxStart box) => box.Float != FloatSide.None && box.FloatWidth > 0;

        /// <summary>
        /// Opens a box in the paginated flow, on the page its top border and padding fit on — below
        /// every float still in effect when it clears (<see cref="BoxStart.ClearFloats"/>).
        /// </summary>
        private static void OpenBox(Ctx ctx, BoxStart box)
        {
            if (box.ClearFloats && ctx.HasFloats) ctx.Y = Math.Min(ctx.Y, ctx.LowestFloatBottom);
            var open = new OpenBlockBox(box, ctx.FlowLeft, ctx.FlowWidth, IsSectionRtl(ctx.Section));
            ctx.Y -= box.MarginTop;
            // Its top inset does not fit here; or it is kept together (BoxStart.KeepTogether) and its
            // minimum height fits the next region but not what is left of this one.
            bool keptWhole = box.KeepTogether && ctx.Y - box.MinHeightPt < ctx.BottomLimit - 0.01
                             && box.MinHeightPt <= ctx.NextRegionHeight + 0.01;
            if ((ctx.Y - BoxTopInset(box) < ctx.BottomLimit || keptWhole) && ctx.Y < ctx.TopStart - 0.01)
            {
                ctx.NewPageContinue();
                // Not open yet, so the break did not move it: laid out again in the band the flow has
                // there — another region's width, when it reached one (Section.ContentAreas).
                open.Rebase(ctx.FlowLeft, ctx.FlowWidth);
            }

            open.FragmentTop = ctx.Y;
            BeginOpacityFragment(ctx, open);
            BeginTransform(ctx, open);
            open.Mark = ctx.Page.ContentLength;
            open.ContentMarks = AnnotationMarks.Of(ctx);
            ctx.OpenBoxes.Add(open);
            ctx.Y -= BoxTopInset(box);
        }

        /// <summary>
        /// A box below full opacity draws each page's fragment — its fill, border and its blocks —
        /// into a transparency group: <see cref="Ctx.BeginOpacityGroup"/> here, and the
        /// page paints it once at the box's opacity when the fragment closes
        /// (<see cref="EndOpacityFragment"/>), inside one marked sequence for a <c>Div</c> made on the
        /// page's parent first — a form invoked inside a sequence belongs to it (§14.7.4.4), and no
        /// element is made inside a group. Nothing for an opaque box.
        /// </summary>
        private static void BeginOpacityFragment(Ctx ctx, OpenBlockBox open)
        {
            if (Alpha01(open.Box.Opacity) >= 1) return;
            open.GroupElement = ctx.NewElement(PdfStructureTypes.Div);
            open.OuterPage = ctx.BeginOpacityGroup(open.Box);
        }

        /// <summary>Closes the fragment's group (<see cref="BeginOpacityFragment"/>): the page paints it at the box's opacity.</summary>
        private static void EndOpacityFragment(Ctx ctx, OpenBlockBox open)
        {
            if (open.OuterPage == null) return;
            ctx.EndOpacityGroup(open.OuterPage, Alpha01(open.Box.Opacity), open.GroupElement);
            open.OuterPage = null;
        }

        /// <summary>
        /// Marks where the box's <see cref="BoxStart.Transform"/> will go round the fragment about to
        /// be drawn — the <c>q</c> and the matrix are written by <see cref="EndTransform"/>, once the
        /// fragment's height, and so its transform origin, is known, and moved back to this mark,
        /// before the fragment's own paint, so everything drawn since lies inside them. The
        /// annotation counts are taken here too. Nothing for a box without a transform.
        /// </summary>
        private static void BeginTransform(Ctx ctx, OpenBlockBox open)
        {
            if (open.Box.Transform is not { Length: 6 }) return;
            open.TransformMark = ctx.Page.ContentLength;
            open.Marks = AnnotationMarks.Of(ctx);
        }

        /// <summary>
        /// Writes the fragment's transform — the box's matrix about its transform origin within the
        /// fragment's border box, from <paramref name="top"/> to <paramref name="bottom"/> — at the
        /// mark <see cref="BeginTransform"/> took, closes it after the fragment's paint, and brings the
        /// link and form-field rectangles the fragment registered along. Nothing when the fragment
        /// drew nothing, or the box has no transform.
        /// </summary>
        private static void EndTransform(Ctx ctx, OpenBlockBox open, double top, double bottom)
        {
            if (open.Box.Transform is not { Length: 6 } m) return;
            if (ctx.Page.ContentLength == open.TransformMark) return;
            double[] about = TransformAbout(m, open.Box.TransformOrigin, open.Left, open.Width, top, Math.Max(0, top - bottom));
            int from = ctx.Page.ContentLength;
            ctx.Page.SaveState().Transform(about[0], about[1], about[2], about[3], about[4], about[5]);
            ctx.Page.MoveTailTo(from, open.TransformMark);
            ctx.Page.RestoreState();
            open.Marks.Remap(ctx, about);
        }

        /// <summary>
        /// Closes the innermost open box in the flow: its bottom padding and border, on the page
        /// they fit on, then its last fragment. An unmatched <see cref="BoxEnd"/> does nothing.
        /// </summary>
        private static void CloseBox(Ctx ctx)
        {
            if (ctx.OpenBoxes.Count == 0) return;
            var open = ctx.OpenBoxes[ctx.OpenBoxes.Count - 1];

            // Its minimum height, on the page it opened on and only where it fits there: a box that
            // has broken, or would, is as tall as its blocks.
            double grow = open.FirstFragment ? MinHeightShortfall(open.Box, open.FragmentTop, ctx.Y) : 0;
            // (A hundredth of a point of slack: a box as tall as the page area, opened at its top —
            // `min-height: 100vh` — ends exactly on the limit, give or take the arithmetic.)
            if (ctx.Y - BoxBottomInset(open.Box) - grow < ctx.BottomLimit - 0.01) grow = 0;

            // Still open while it asks for room, so a page break here slices it like any other.
            ctx.EnsureSpace(BoxBottomInset(open.Box));
            ctx.OpenBoxes.RemoveAt(ctx.OpenBoxes.Count - 1);

            ctx.Y -= BoxBottomInset(open.Box) + grow;
            AlignContent(ctx, open, grow);
            DrawBoxFragment(ctx, open, open.FragmentTop, ctx.Y, drawTop: open.FirstFragment, drawBottom: true);
            EndTransform(ctx, open, open.FragmentTop, ctx.Y);
            EndOpacityFragment(ctx, open);
            ctx.Y -= open.Box.MarginBottom;
        }

        /// <summary>
        /// How much further down than its blocks <paramref name="box"/>'s bottom edge goes for its
        /// <see cref="BoxStart.MinHeightPt"/>: from its top edge at <paramref name="top"/>, with its
        /// blocks ending at <paramref name="y"/>. 0 when the blocks already reach it.
        /// </summary>
        private static double MinHeightShortfall(BoxStart box, double top, double y)
            => box.MinHeightPt > 0 ? Math.Max(0, (y - BoxBottomInset(box)) - (top - box.MinHeightPt)) : 0;

        /// <summary>
        /// Moves the blocks of a box closing <paramref name="shortfall"/> taller than they are — its
        /// minimum height — down to where its <see cref="BoxStart.ContentAlign"/> puts them: a
        /// translation written now and moved back to where the blocks began, so it wraps them alone;
        /// the box's paint, drawn after and moved to the same mark, goes in front of it, unmoved. The
        /// rectangles the blocks registered move with them. Nothing at the top, with no room left, or
        /// when the blocks drew nothing.
        /// </summary>
        private static void AlignContent(Ctx ctx, OpenBlockBox open, double shortfall)
        {
            // Baseline is Top here: a box has no row of baselines to share.
            if (shortfall <= 0 || open.Box.ContentAlign is not (CellVerticalAlign.Center or CellVerticalAlign.Bottom)) return;
            if (ctx.Page.ContentLength == open.Mark) return;
            double dy = open.Box.ContentAlign == CellVerticalAlign.Center ? shortfall / 2 : shortfall;
            int from = ctx.Page.ContentLength;
            ctx.Page.SaveState().Transform(1, 0, 0, 1, 0, -dy);
            ctx.Page.MoveTailTo(from, open.Mark);
            ctx.Page.RestoreState();
            open.ContentMarks.Remap(ctx, new[] { 1.0, 0, 0, 1, 0, -dy });
        }

        /// <summary>Closes every box still open where its list ended.</summary>
        private static void CloseAllBoxes(Ctx ctx)
        {
            while (ctx.OpenBoxes.Count > 0) CloseBox(ctx);
        }

        /// <summary>
        /// A page is ending with boxes open: each draws its fragment down to the page's last line,
        /// with no bottom border — innermost first, the later positions first.
        /// </summary>
        internal static void SliceOpenBoxes(Ctx ctx)
        {
            for (int i = ctx.OpenBoxes.Count - 1; i >= 0; i--)
            {
                var open = ctx.OpenBoxes[i];
                DrawBoxFragment(ctx, open, open.FragmentTop, ctx.BottomLimit, drawTop: open.FirstFragment, drawBottom: false);
                // Each fragment turns about its own origin — the box's height across the pages is
                // not known here — which is not what CSS would draw for the box as a whole; once.
                if (open.Box.Transform is { Length: 6 } && !ctx.ReportedSlicedTransform)
                {
                    ctx.ReportedSlicedTransform = true;
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                        "a transformed box broke across pages: each page's fragment is transformed about " +
                        "its own transform origin (within the fragment), not the whole box's.");
                }
                EndTransform(ctx, open, open.FragmentTop, ctx.BottomLimit);
                EndOpacityFragment(ctx, open);
                open.FirstFragment = false;
            }
        }

        /// <summary>
        /// A new page has begun with boxes open: each continues from the top of the text block, with
        /// no top border and no top padding — the box is cut, not reopened.
        /// </summary>
        internal static void ResumeOpenBoxes(Ctx ctx)
        {
            foreach (var open in ctx.OpenBoxes)
            {
                open.FragmentTop = ctx.Y;
                BeginOpacityFragment(ctx, open);      // the last page's group was painted with its fragment
                BeginTransform(ctx, open);            // the last page's was closed with its fragment
                open.Mark = ctx.Page.ContentLength;   // in the new page's content, not the last one's
            }
        }

        /// <summary>
        /// Draws one fragment of a box — its fill, background image and border — between
        /// <paramref name="top"/> and <paramref name="bottom"/>, moves the paint behind the blocks
        /// drawn since the fragment began, and then draws the positioned paragraphs measured from the
        /// box whose anchor falls in this fragment (<see cref="DrawPositionedInBox"/>), over its blocks.
        /// </summary>
        private static void DrawBoxFragment(Ctx ctx, OpenBlockBox open, double top, double bottom,
            bool drawTop, bool drawBottom)
        {
            DrawBoxFragmentPaint(ctx, open, top, bottom, drawTop, drawBottom);
            DrawPositionedInBox(ctx, open, top, bottom, drawTop, final: drawBottom);
        }

        /// <summary>
        /// The positioned paragraphs measured from <paramref name="open"/>'s box
        /// (<see cref="AbsolutePlacement.ContainingBox"/>) whose anchor falls in the fragment
        /// between <paramref name="top"/> and <paramref name="bottom"/> — every one still waiting, when
        /// the fragment is the box's last (<paramref name="final"/>) — drawn in z order from the
        /// fragment's padding box: CSS 2.1 §10.3.7 and §10.6.4 for a block with a stated width or
        /// none; a block anchored at its right alone is as wide as its content (shrink-to-fit). The
        /// flow cursor is left alone, as <c>RenderPositioned</c> leaves it.
        /// </summary>
        /// <remarks>
        /// <para><b>Offsets are from the box's top padding edge</b>, across its fragments: the
        /// fragments drawn before this one (<see cref="OpenBlockBox.HeightBefore"/>) are how far down the
        /// box this fragment's top is, so an anchor further down than the first page's fragment is
        /// drawn with the fragment — the page — it falls in, as a reader sees it. A bottom-anchored
        /// block waits for the last fragment, whose bottom edge is the box's — and so does one whose
        /// top is a share of the box's height (<see cref="AbsolutePlacement.TopFraction"/>),
        /// which that edge decides; a share of the width is of the padding box's, known from the start.</para>
        /// <para>Drawn after the fragment's paint has gone behind the blocks, so the positioned block is
        /// over the box's blocks and inside the fragment's transform and opacity group.</para>
        /// </remarks>
        private static void DrawPositionedInBox(Ctx ctx, OpenBlockBox open, double top, double bottom, bool drawTop, bool final)
        {
            double h = Math.Max(0, top - bottom);
            if (open.Positioned.Count > 0)
            {
                var box = open.Box;
                double padLeft = open.Left + Border.PaintedWidth(box.BorderLeft);
                double padRight = open.Left + open.Width - Border.PaintedWidth(box.BorderRight);
                double padWidth = Math.Max(1, padRight - padLeft);
                double padBottom = bottom + (final ? Border.PaintedWidth(box.BorderBottom) : 0);
                // The box's top padding edge is this far above this fragment's top (below it, past the first fragment).
                double offsetOfTop = open.HeightBefore - Border.PaintedWidth(box.BorderTop);
                // The padding box's height — what a vertical share is of — once the last fragment's bottom is known.
                double padHeight = final ? Math.Max(0, open.HeightBefore + h - Border.PaintedWidth(box.BorderTop) - Border.PaintedWidth(box.BorderBottom)) : 0;

                foreach (var (para, place) in open.Positioned.OrderBy(p => p.Place.ZIndex).ToList())
                {
                    bool topAnchored = place.AnchorsTop || place.BottomPt == null;
                    bool here = final || (topAnchored && place.TopFraction == null && place.TopPt >= offsetOfTop && place.TopPt < offsetOfTop + h);
                    if (!here) continue;

                    double leftOffset = place.LeftPt + (place.LeftFraction ?? 0) * padWidth;
                    double? rightOffset = place.RightPt is double r0 ? r0 + (place.RightFraction ?? 0) * padWidth : (double?)null;
                    double statedWidth = place.WidthPt + (place.WidthFraction ?? 0) * padWidth;
                    double width;
                    if (statedWidth > 0) width = statedWidth;
                    else if (!place.AnchorsLeft && rightOffset is double rightAlone) width = ShrinkToFitGroupWidth(ctx, para, place, Math.Max(1, padWidth - rightAlone));
                    else if (rightOffset is double right) width = padWidth - leftOffset - right;
                    else width = padWidth - leftOffset;
                    width = Math.Max(1, width);
                    double x = place.AnchorsLeft || rightOffset == null ? padLeft + leftOffset : padRight - rightOffset.Value - width;

                    double topOffset = place.TopPt + (place.TopFraction ?? 0) * padHeight;
                    double y = topAnchored
                        ? top - (topOffset - offsetOfTop)
                        : padBottom + place.BottomPt!.Value + (place.BottomFraction ?? 0) * padHeight + MeasurePlacedGroup(ctx, para, place, width);

                    double savedY = ctx.Y;
                    RenderPlacedGroup(ctx, para, place, x, y, width);
                    ctx.Y = savedY;
                    open.Positioned.Remove((para, place));
                }
            }
            open.HeightBefore += h;
        }

        /// <summary>The fragment's own paint: fill, background image, border, inset shadows, clip.</summary>
        private static void DrawBoxFragmentPaint(Ctx ctx, OpenBlockBox open, double top, double bottom,
            bool drawTop, bool drawBottom)
        {
            var box = open.Box;
            double h = top - bottom;
            if (h <= 0) return;

            bool fill = !string.IsNullOrEmpty(box.Shading);
            bool image = box.BackgroundImage?.Data is { Length: > 0 };
            if (!fill && !image && box.Shadow == null && box.BorderTop == null && box.BorderRight == null
                && box.BorderBottom == null && box.BorderLeft == null && !box.ClipsContent)
                return;

            double x = open.Left, w = open.Width;
            // A fragment cut by a page break has square corners on the cut edge only; the edge the
            // box really has keeps its rounding.
            Corners radius = Corners.Of(box.CornerRadiusPt, box.CornerRadiiPt, box.CornerRadiiYPt, w, h, drawTop, drawBottom, box.CornerRadiusPercent,
                                        box.CornerRadiiPercent, box.CornerRadiiYPercent);

            // The padding box — the box less its borders, which lie inside it (none at an edge a page
            // break cuts): where an inset shadow is cast, and what the content is clipped to.
            double it = drawTop ? Border.PaintedWidth(box.BorderTop) : 0, ib = drawBottom ? Border.PaintedWidth(box.BorderBottom) : 0;
            double il = Border.PaintedWidth(box.BorderLeft), ir = Border.PaintedWidth(box.BorderRight);
            Corners inner = radius.Inset(il, it, ir, ib);

            int from = ctx.Page.ContentLength;
            using (ctx.MarkArtifact(PdfArtifactTypes.Layout))
            {
                // The shadow goes down first, behind the fill it sits under — as a paragraph's does,
                // through the one drawing (DrawShadow).
                foreach (var shadow in PaintOrder(box.Shadow, box.MoreShadows, inset: false))
                    DrawShadow(ctx, shadow, x, bottom, w, h, radius, 1);
                if (fill)
                {
                    var (r, g, b) = ParseColorD(box.Shading);
                    ctx.Page.SaveState().SetFillColorRgb((float)r, (float)g, (float)b);
                    AppendBoxPath(ctx, x, bottom, w, h, radius);
                    ctx.Page.Fill().RestoreState();
                }
                if (image) DrawBackgroundImage(ctx, box.BackgroundImage, x, bottom, w, h, 1);

                // Inset shadows over the background, inside the padding box.
                foreach (var shadow in PaintOrder(box.Shadow, box.MoreShadows, inset: true))
                    DrawInsetShadow(ctx, shadow, x + il, bottom + ib, w - il - ir, h - it - ib, inner, 1);

                double top2 = Border.PaintedWidth(box.BorderTop) / 2, bottom2 = Border.PaintedWidth(box.BorderBottom) / 2;
                double left2 = Border.PaintedWidth(box.BorderLeft) / 2, right2 = Border.PaintedWidth(box.BorderRight) / 2;
                // The border band lies inside the box, so its centre line is half a border in, and so
                // are its corners.
                if (!StrokeRoundedBorder(ctx, box.BorderTop, box.BorderRight, box.BorderBottom, box.BorderLeft,
                        x + left2, bottom + bottom2, x + w - right2, top - top2, radius.Grown(-left2), 1,
                        cutTop: !drawTop, cutBottom: !drawBottom))
                {
                    if (drawTop) StrokeEdge(ctx.Page, box.BorderTop, x, top - top2, x + w, top - top2, 1);
                    if (drawBottom) StrokeEdge(ctx.Page, box.BorderBottom, x, bottom + bottom2, x + w, bottom + bottom2, 1);
                    StrokeEdge(ctx.Page, box.BorderLeft, x + left2, bottom, x + left2, top, 1);
                    StrokeEdge(ctx.Page, box.BorderRight, x + w - right2, bottom, x + w - right2, top, 1);
                }
            }
            // overflow: the clip goes in after the box's own paint, outside its artifact sequence,
            // and moves with it to where the fragment began — so everything drawn inside the box
            // since then is clipped — and its state is restored here, after that content.
            if (box.ClipsContent)
            {
                ctx.Page.SaveState();
                AppendBoxPath(ctx, x + il, bottom + ib, w - il - ir, h - it - ib, inner);
                ctx.Page.ClipNonzero();
            }
            ctx.Page.MoveTailTo(from, open.Mark);
            if (box.ClipsContent) ctx.Page.RestoreState();
        }

        // ------------------------------------------------------------------ inside a table cell

        /// <summary>
        /// Lays a cell's blocks out from <paramref name="y"/> without paginating — a row is never
        /// split — honouring the boxes among them, and returns where the blocks end.
        /// </summary>
        /// <remarks>
        /// One loop for the draw and, through <paramref name="measureOnly"/>, the measure: a row is
        /// sized by measuring its cells and then filled by drawing them, and two loops over the same
        /// blocks are how the two come to disagree. Both callbacks get the block's band and y, since
        /// a float among the blocks is placed by them and measured beside them.
        /// </remarks>
        internal static double LayOutCellBlocks(Ctx ctx, System.Collections.Generic.IEnumerable<IDocElement> blocks,
            double x, double y, double width, bool measureOnly,
            Func<IDocElement, double, double, double, double> drawBlock,
            Func<IDocElement, double, double, double, double> measureBlock,
            double stretchTo = double.NaN)
        {
            var open = new System.Collections.Generic.List<OpenBlockBox>();
            double bandLeft = x, bandWidth = width;

            // Indexed: a floated box takes its blocks to its BoxEnd in one step.
            var list = blocks as System.Collections.Generic.IReadOnlyList<IDocElement>
                       ?? new System.Collections.Generic.List<IDocElement>(blocks);
            for (int i = 0; i < list.Count; i++)
            {
                IDocElement block = list[i];
                // A positioned paragraph takes no height of its own in the cell. One placed
                // against a box open here is the box's — attached as the box opened, drawn with the
                // box's fragment over its blocks; one placed against a box open round the table — a
                // positioned table's or multi-column container's own — is that box's,
                // attached as the cell is drawn, each time it is drawn; one placed against the page is
                // drawn after the section's flow with the section's own (Ctx.DeferredPositioned), once
                // — a header row repeats, its paragraphs with it — where it was laid out in the cell's flow.
                if (block is Paragraph positioned && positioned.Placement != null)
                {
                    if (!measureOnly)
                    {
                        // (A paragraph on every page has no containing box, so neither lookup finds one.)
                        AbsolutePlacement place = positioned.Placement;
                        bool ownBox = FindOpenBox(open, place.ContainingBox) != null;
                        OpenBlockBox? outer = ownBox ? null : FindOpenBox(ctx.OpenBoxes, place.ContainingBox);
                        if (outer != null) outer.Positioned.Add((positioned, place));
                        // Add returns false when the paragraph is already held back, so the
                        // one-per-paragraph rule costs an O(1) set probe, not a scan of the list.
                        else if (!ownBox && ctx.DeferredPositionedSeen.Add(positioned))
                            ctx.DeferredPositioned.Add((positioned, place));
                    }
                    continue;
                }
                if (block is BoxStart floated && IsFloated(floated))
                {
                    // A floated box among a cell's blocks sits at the band's edge and the blocks
                    // after it are laid out — and measured — beside it, as a floated paragraph is;
                    // one with no measure beside it opens as a block of its float width below.
                    int end = BoxStart.IndexOfEnd(list, i);
                    if (TryPlaceFloatedBox(ctx, list, i, end, bandLeft, ref y, bandWidth, measureOnly, paginate: false, drawBlock, measureBlock))
                    {
                        i = Math.Min(end, list.Count - 1);
                        continue;
                    }
                }
                switch (block)
                {
                    case BoxStart start:
                    {
                        if (start.ClearFloats && ctx.HasFloats) y = Math.Min(y, ctx.LowestFloatBottom);
                        var box = new OpenBlockBox(start, bandLeft, bandWidth, IsSectionRtl(ctx.Section));
                        AttachPositioned(box, list, i, BoxStart.IndexOfEnd(list, i));
                        y -= start.MarginTop;
                        box.FragmentTop = y;
                        if (!measureOnly)
                        {
                            BeginOpacityFragment(ctx, box);
                            BeginTransform(ctx, box);
                            box.Mark = ctx.Page.ContentLength;
                            box.ContentMarks = AnnotationMarks.Of(ctx);
                        }
                        open.Add(box);
                        y -= BoxTopInset(start);
                        bandLeft = box.ContentLeft;
                        bandWidth = box.ContentWidth;
                        break;
                    }
                    case BoxEnd:
                    {
                        if (open.Count == 0) break;
                        y = CloseCellBox(ctx, open, y, measureOnly, stretchTo);
                        bandLeft = open.Count > 0 ? open[open.Count - 1].ContentLeft : x;
                        bandWidth = open.Count > 0 ? open[open.Count - 1].ContentWidth : width;
                        break;
                    }
                    default:
                        y = measureOnly ? y - measureBlock(block, bandLeft, y, bandWidth) : drawBlock(block, bandLeft, y, bandWidth);
                        break;
                }
            }
            while (open.Count > 0) y = CloseCellBox(ctx, open, y, measureOnly, stretchTo);
            return y;
        }

        // ------------------------------------------------------------------ a floated box

        /// <summary>
        /// Lays the floated box at <paramref name="blocks"/>[<paramref name="start"/>..<paramref name="end"/>]
        /// (<see cref="BoxStart.Float"/>) out at the edge of the band <paramref name="width"/>
        /// wide from <paramref name="x"/>, asked to sit at <paramref name="y"/>: the whole box at once,
        /// as a cell lays its blocks out, through <paramref name="drawBlock"/> (or
        /// <paramref name="measureBlock"/> when <paramref name="measureOnly"/>), the floats outside it
        /// detached while its own blocks lay out; registers its margin box as an exclusion; and leaves
        /// <paramref name="y"/> where it was, so the block after it starts beside it — or at the next
        /// page's top, when the box was moved there (<paramref name="paginate"/>). Answers
        /// <see langword="false"/> when the box is laid out in the flow instead: no usable measure
        /// beside it (<see cref="FloatBox(Ctx, FloatSide, double, double, double, double, out double, out double, out double)"/>),
        /// or taller than a page, which is reported.
        /// </summary>
        /// <remarks>
        /// <para><b>Measured before it is drawn</b>, since a box is drawn whole: a paragraph float
        /// paginates as it draws, but a box that does not fit the rest of its page moves to the next
        /// page first, and one taller than a page cannot float at all — CSS would break it across pages
        /// beside the text, which the one-page layout of a cell has no way to say.</para>
        /// <para><b>The exclusion is the margin box</b> (CSS 2.1 §9.5.1): <see cref="BoxStart.FloatWidth"/>
        /// is the border box, the margins outside it, so <see cref="OpenBlockBox"/> finds its float
        /// width as its band less its margins, the stated width unread.</para>
        /// </remarks>
        private static bool TryPlaceFloatedBox(Ctx ctx, System.Collections.Generic.IReadOnlyList<IDocElement> blocks,
            int start, int end, double x, ref double y, double width, bool measureOnly, bool paginate,
            Func<IDocElement, double, double, double, double> drawBlock,
            Func<IDocElement, double, double, double, double> measureBlock)
        {
            var box = (BoxStart)blocks[start];
            int last = Math.Min(end, blocks.Count - 1);
            var slice = new System.Collections.Generic.List<IDocElement>(last - start + 1);
            for (int i = start; i <= last; i++) slice.Add(blocks[i]);

            // A float can clear too (CSS 2.1 §9.5.2): it starts below the floats before it.
            if (box.ClearFloats && ctx.HasFloats) y = Math.Min(y, ctx.LowestFloatBottom);

            double marginBox = Math.Max(1, box.FloatWidth + box.MarginLeft + box.MarginRight);
            if (!FloatBox(ctx, box.Float, marginBox, x, y, width, out double left, out double floatWidth, out double top))
                return false;

            // The measure pass exists only to decide pagination: its height is read nowhere outside
            // the `if (paginate)` block below. Computing it unconditionally laid out the box's whole
            // subtree an extra time at every level, and because a nested float in a cell is placed
            // with paginate:false, the two passes compounded to 2^depth layouts over nested floated
            // boxes. Measure only when the height is actually used.
            if (paginate)
            {
                double height = -LayOutFloatedBox(ctx, slice, 0, 0, floatWidth, measureOnly: true, drawBlock, measureBlock);
                if (top - height < ctx.BottomLimit - 0.01)
                {
                    if (height > ctx.TopStart - ctx.BottomLimit)
                    {
                        if (!measureOnly && !ctx.MeasureOnly)
                            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                                "float: a floated box taller than a page was laid out in the flow, a block of its float " +
                                "width on its side; CSS breaks a float across pages with the text beside it, which the " +
                                "one-page layout of a box cannot.");
                        return false;
                    }
                    if (top < ctx.TopStart - 0.01)
                    {
                        double shift = ctx.RegionShiftX;
                        ctx.NewPageContinue();
                        x += ctx.RegionShiftX - shift;   // a content area further right (Section.ContentAreas)
                        y = ctx.Y;
                        if (!FloatBox(ctx, box.Float, marginBox, x, y, width, out left, out floatWidth, out top)) return false;
                    }
                }
            }

            double bottom = LayOutFloatedBox(ctx, slice, left, top, floatWidth, measureOnly, drawBlock, measureBlock);
            ctx.AddFloat(new Ctx.FloatExclusion(left, left + floatWidth, top, bottom));
            return true;
        }

        /// <summary>
        /// The box's blocks laid out — or measured — from <paramref name="top"/> in a band
        /// <paramref name="width"/> wide at <paramref name="left"/>, the page's floats detached meanwhile
        /// (its own blocks are not beside them) and restored after, its own floats gone with the
        /// layout; where the blocks end, a float of its own reaching lower included.
        /// </summary>
        private static double LayOutFloatedBox(Ctx ctx, System.Collections.Generic.List<IDocElement> slice,
            double left, double top, double width, bool measureOnly,
            Func<IDocElement, double, double, double, double> drawBlock,
            Func<IDocElement, double, double, double, double> measureBlock)
        {
            FloatedBoxLayoutPasses++;
            var outer = ctx.DetachFloats();
            try
            {
                double end = LayOutCellBlocks(ctx, slice, left, top, width, measureOnly, drawBlock, measureBlock);
                if (ctx.HasFloats) end = Math.Min(end, ctx.LowestFloatBottom);
                return end;
            }
            finally { ctx.RestoreFloats(outer); }
        }

        /// <summary>
        /// Closes a cell's innermost box. <c>stretchTo</c> is where the cell's content ends, for an
        /// outermost box that <see cref="BoxStart.StretchesToCell"/>; <c>NaN</c> when measuring,
        /// which is before the row's height is known.
        /// </summary>
        private static double CloseCellBox(Ctx ctx, System.Collections.Generic.List<OpenBlockBox> open, double y, bool measureOnly,
            double stretchTo = double.NaN)
        {
            var box = open[open.Count - 1];
            open.RemoveAt(open.Count - 1);
            // Its minimum height too: a cell measures and draws through this one loop, so the row
            // is sized for it.
            double shortfall = MinHeightShortfall(box.Box, box.FragmentTop, y);
            y -= BoxBottomInset(box.Box) + shortfall;
            // A stretched item's box reaches the cell's bottom, its own bottom margin still inside.
            if (box.Box.StretchesToCell && open.Count == 0 && !double.IsNaN(stretchTo))
                y = Math.Min(y, stretchTo + box.Box.MarginBottom);
            if (!measureOnly)
            {
                AlignContent(ctx, box, shortfall);
                DrawBoxFragment(ctx, box, box.FragmentTop, y, drawTop: true, drawBottom: true);
                EndTransform(ctx, box, box.FragmentTop, y);
                EndOpacityFragment(ctx, box);
            }
            return y - box.Box.MarginBottom;
        }
    }
}
