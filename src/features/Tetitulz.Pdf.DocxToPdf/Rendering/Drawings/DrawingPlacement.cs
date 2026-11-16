// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using Tetitulz.Pdf.Core.Constants;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// Where a <c>&lt;w:drawing&gt;</c> goes on the page, for every kind of drawing.
    ///
    /// <para>
    /// Placement is a property of the drawing wrapper (<c>wp:inline</c> / <c>wp:anchor</c>), not
    /// of what is inside it: a picture, a vector shape, a SmartArt diagram and a chart are all
    /// positioned by the same rules. Keeping one implementation here is what stops those from
    /// drifting — an anchored <i>shape</i> overlapping body text while an anchored <i>image</i>
    /// does not, because only one path registers an exclusion rectangle.
    /// </para>
    ///
    /// <para>
    /// The anchor maths is moved verbatim from <c>ImageRenderer</c>; behaviour for images is
    /// unchanged.
    /// </para>
    /// </summary>
    internal static class DrawingPlacement
    {
        /// <summary>
        /// A drawing's box in PDF user space: <see cref="X"/> is the left edge and
        /// <see cref="TopY"/> the <b>top</b> edge (PDF y grows upward, so the bottom is
        /// <c>TopY - Height</c>).
        /// </summary>
        internal readonly struct Box
        {
            internal Box(double x, double topY, double width, double height)
            {
                X = x; TopY = topY; Width = width; Height = height;
            }

            internal double X { get; }
            internal double TopY { get; }
            internal double Width { get; }
            internal double Height { get; }
            internal double BottomY => TopY - Height;
        }

        /// <summary>
        /// Reads <c>wp:extent</c> (EMU) as points. Located by local name so it works for both the
        /// typed <c>wp:inline</c>/<c>wp:anchor</c> wrappers and raw-XML subtrees the SDK models
        /// poorly. Returns <c>false</c> when the extent is missing or non-positive.
        /// </summary>
        internal static bool TryReadExtent(OpenXmlElement container, out double widthPts, out double heightPts)
        {
            widthPts = heightPts = 0;
            var extent = container?.Descendants().FirstOrDefault(e => e.LocalName == "extent");
            if (extent == null) return false;

            widthPts = ParseLong(Attr(extent, "cx")) / PdfUnits.EmuPerPoint;
            heightPts = ParseLong(Attr(extent, "cy")) / PdfUnits.EmuPerPoint;
            return widthPts > 0 && heightPts > 0;
        }

        /// <summary>
        /// Places an inline drawing at the flow cursor, shrinking it to the column width and
        /// breaking the flow first when it does not fit. Does <b>not</b> advance the cursor —
        /// the caller does that after painting, so a drawing that fails to paint can still
        /// reserve its height.
        /// </summary>
        internal static Box PlaceInline(double widthPts, double heightPts, DocxRenderContext context)
        {
            if (widthPts > context.ContentWidth && widthPts > 0)
            {
                double s = context.ContentWidth / widthPts;
                widthPts *= s;
                heightPts *= s;
            }

            // Clear the previous line's descenders first. The flow cursor rests on that line's
            // baseline, so placing a drawing's top edge there paints over its "p" and "g" — which
            // is invisible in any geometry assertion and obvious the moment anyone looks at a page.
            if (context.LastLineDescent > 0)
            {
                context.AdvanceCursor(context.LastLineDescent);
                context.LastLineDescent = 0;
            }

            if (context.NeedsNewPage(heightPts)) context.BreakFlow();

            return new Box(context.ContentLeft, context.CursorY, widthPts, heightPts);
        }

        /// <summary>
        /// Places an anchored (floating) drawing at the absolute position given by
        /// <c>wp:positionH</c> / <c>wp:positionV</c>, clamped to the page.
        /// </summary>
        internal static Box PlaceAnchored(DW.Anchor anchor, double widthPts, double heightPts,
            DocxRenderContext context)
        {
            double x = ComputeAnchorX(anchor, widthPts, context);
            double topY = ComputeAnchorTopY(anchor, heightPts, context);

            // Keep the drawing on the page: its bottom no lower than the page's edge, its top no
            // higher than the page's.
            if (x < 0) x = 0;
            if (x + widthPts > context.PageWidth) x = Math.Max(0, context.PageWidth - widthPts);
            if (topY < heightPts) topY = heightPts;
            if (topY > context.PageHeight) topY = context.PageHeight;

            return new Box(x, topY, widthPts, heightPts);
        }

        /// <summary>
        /// Registers the exclusion rectangle body text wraps around, for an anchored drawing that
        /// has been placed at <paramref name="box"/>.
        ///
        /// <para>
        /// Painted-only floats (in front of / behind text, or <c>wrapNone</c>) exert no exclusion.
        /// <c>wrapTopAndBottom</c> reserves the whole column band so no text sits beside the
        /// drawing; <c>wrapSquare</c> / <c>wrapTight</c> / <c>wrapThrough</c> reserve only the
        /// drawing's own side (tight and through approximate to the bounding box — there is no
        /// polygon contour).
        /// </para>
        ///
        /// <para>
        /// A float never advances the flow cursor; surrounding text flows around it.
        /// </para>
        /// </summary>
        internal static void RegisterFloat(DW.Anchor anchor, Box box, DocxRenderContext context)
        {
            bool wrapNone = anchor.GetFirstChild<DW.WrapNone>() != null;
            bool behind = anchor.BehindDoc?.Value ?? false;
            if (behind || wrapNone) return;

            bool wrapTopBottom = anchor.GetFirstChild<DW.WrapTopBottom>() != null;

            double distL = (anchor.DistanceFromLeft?.Value ?? 0) / PdfUnits.EmuPerPoint;
            double distR = (anchor.DistanceFromRight?.Value ?? 0) / PdfUnits.EmuPerPoint;
            double distT = (anchor.DistanceFromTop?.Value ?? 0) / PdfUnits.EmuPerPoint;
            double distB = (anchor.DistanceFromBottom?.Value ?? 0) / PdfUnits.EmuPerPoint;

            if (wrapTopBottom)
            {
                context.AddFloat(new DocxRenderContext.FloatRect(
                    context.ContentLeft - 1, context.ContentLeft + context.ContentWidth + 1,
                    box.TopY + distT, box.BottomY - distB, false));
            }
            else
            {
                context.AddFloat(new DocxRenderContext.FloatRect(
                    box.X - distL, box.X + box.Width + distR,
                    box.TopY + distT, box.BottomY - distB, false));
            }
        }

        // Computes the float's left edge (PDF user space) from wp:positionH.
        internal static double ComputeAnchorX(DW.Anchor anchor, double w, DocxRenderContext context)
        {
            var hp = anchor.GetFirstChild<DW.HorizontalPosition>();
            double baseLeft = context.MarginLeft, baseWidth = context.FullContentWidth;
            var rel = hp?.RelativeFrom?.Value;
            if (rel == DW.HorizontalRelativePositionValues.Page) { baseLeft = 0; baseWidth = context.PageWidth; }
            else if (rel == DW.HorizontalRelativePositionValues.Column) { baseLeft = context.ContentLeft; baseWidth = context.ContentWidth; }

            var off = hp?.GetFirstChild<DW.PositionOffset>()?.Text;
            if (!string.IsNullOrEmpty(off) && long.TryParse(off, NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu))
                return baseLeft + emu / PdfUnits.EmuPerPoint;

            switch (hp?.GetFirstChild<DW.HorizontalAlignment>()?.Text)
            {
                case "center": return baseLeft + (baseWidth - w) / 2.0;
                case "right":
                case "outside": return baseLeft + baseWidth - w;
                default: return baseLeft; // left / inside / unspecified
            }
        }

        // Computes the PDF y of the float's TOP edge from wp:positionV.
        internal static double ComputeAnchorTopY(DW.Anchor anchor, double h, DocxRenderContext context)
        {
            var vp = anchor.GetFirstChild<DW.VerticalPosition>();
            var rel = vp?.RelativeFrom?.Value;
            var off = vp?.GetFirstChild<DW.PositionOffset>()?.Text;
            string align = vp?.GetFirstChild<DW.VerticalAlignment>()?.Text;
            long emu = 0;
            bool hasOff = !string.IsNullOrEmpty(off)
                && long.TryParse(off, NumberStyles.Integer, CultureInfo.InvariantCulture, out emu);
            double offPt = hasOff ? emu / PdfUnits.EmuPerPoint : 0;

            // Paragraph / line / unspecified → relative to the current text cursor.
            if (rel == null
                || rel == DW.VerticalRelativePositionValues.Paragraph
                || rel == DW.VerticalRelativePositionValues.Line)
                return context.CursorY - offPt;

            // The page's bottom edge, or the section's bottom margin — the section's, not the
            // context's, which a header or footer being drawn sets to a sentinel far below the page.
            if (rel == DW.VerticalRelativePositionValues.Page)
            {
                if (hasOff) return context.PageHeight - offPt;
                if (align == "center") return context.PageHeight / 2.0 + h / 2.0;
                if (align == "bottom") return h;
                return context.PageHeight; // top / default
            }

            // margin / topMargin / etc.
            double topBase = context.PageHeight - context.MarginTop;
            if (hasOff) return topBase - offPt;
            if (align == "center") return context.PageHeight / 2.0 + h / 2.0;
            if (align == "bottom") return context.SectionMarginBottom + h;
            return topBase; // top / default
        }

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);

        private static long ParseLong(string s)
            => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : 0;
    }
}

