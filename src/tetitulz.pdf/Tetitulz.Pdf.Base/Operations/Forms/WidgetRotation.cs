// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Table 192's <c>/MK /R</c> — the number of degrees, a multiple of 90, by which a widget
    /// annotation is rotated counterclockwise relative to the page — and what an appearance
    /// generator does with it.
    /// </summary>
    /// <remarks>
    /// <para><b>The turn belongs in the appearance, not in the renderer.</b> §12.5.5's algorithm
    /// maps the form's <c>/BBox</c> through its <c>/Matrix</c> and fits the result to <c>/Rect</c>;
    /// it has no rotation step of its own, and <c>/MK</c> is "additional information for
    /// constructing the annotation's appearance stream". So a generator that honours <c>/R</c> lays
    /// the content out in a box whose width and height are the rectangle's, swapped for 90 and
    /// 270, and writes a <c>/Matrix</c> that turns that box onto the rectangle — after which every
    /// conforming reader, this library's two renderers included, draws it turned. A renderer that
    /// turns an appearance a second time by <c>/R</c> draws it twice turned.</para>
    ///
    /// <para><b>One reading and one turn</b> for the form-field appearance generator, the
    /// visible-signature generator and both renderers' synthesised fallbacks, which draw straight
    /// into the rectangle and so apply the same coefficients to their own coordinates. A leaf: no
    /// static fields.</para>
    /// </remarks>
    internal static class WidgetRotation
    {
        /// <summary>
        /// The widget's <c>/MK /R</c>: 0, 90, 180 or 270. Absent, unresolvable, fractional or not a
        /// multiple of 90 reads as 0 — Table 192 makes the value "a multiple of 90", so anything
        /// else is a producer error, drawn upright rather than at an angle no other reader would
        /// agree on. A negative multiple or one past a full turn is normalised.
        /// </summary>
        /// <param name="widget">The widget annotation dictionary.</param>
        /// <param name="resolve">Follows an indirect reference (§7.3.10); null reads values as they are.</param>
        internal static int Read(PdfDictionary widget, Func<PdfObject, PdfObject> resolve)
        {
            if (widget == null) return 0;
            resolve = resolve ?? (o => o);

            var mk = resolve(widget[PdfName.MK]) as PdfDictionary;
            if (mk == null) return 0;
            if (!(resolve(mk[PdfName.R]) is PdfNumber r)) return 0;

            double value = r.DoubleValue;
            if (double.IsNaN(value) || double.IsInfinity(value) || value != Math.Floor(value)) return 0;
            if (Math.Abs(value) > int.MaxValue) return 0;

            int degrees = SaturatingCast.ToInt(value);
            if (degrees % 90 != 0) return 0;
            return ((degrees % 360) + 360) % 360;
        }

        /// <summary>
        /// The box an appearance is laid out in: the rectangle's width and height, swapped when the
        /// turn is 90 or 270, because the content's horizontal then runs along the page's vertical.
        /// </summary>
        internal static (double Width, double Height) LayoutBox(int rotation, double rectWidth, double rectHeight)
            => rotation == 90 || rotation == 270 ? (rectHeight, rectWidth) : (rectWidth, rectHeight);

        /// <summary>
        /// The six coefficients <c>[a b c d e f]</c> of the turn that maps a point of the layout
        /// box onto the rectangle, rectangle-relative: <c>(x, y) → (a·x + c·y + e, b·x + d·y + f)</c>.
        /// The identity for no turn. <see cref="Matrix"/> writes them and <see cref="ToRectangle"/>
        /// applies them, so a form and a directly drawn fallback turn the same way.
        /// </summary>
        internal static double[] Coefficients(int rotation, double rectWidth, double rectHeight)
        {
            switch (rotation)
            {
                case 90:  return new[] { 0d, 1, -1, 0, rectWidth, 0 };
                case 180: return new[] { -1d, 0, 0, -1, rectWidth, rectHeight };
                case 270: return new[] { 0d, -1, 1, 0, 0, rectHeight };
                default:  return new[] { 1d, 0, 0, 1, 0, 0 };
            }
        }

        /// <summary>
        /// The form <c>/Matrix</c> that turns a layout box of <see cref="LayoutBox"/>'s size onto
        /// the rectangle: <c>/BBox [0 0 boxWidth boxHeight]</c> transformed by it is exactly
        /// <c>[0 0 rectWidth rectHeight]</c>, so §12.5.5's fit is a pure translation and a reader
        /// that skips the fit still draws it in place. <c>null</c> for no turn — an identity matrix
        /// is noise in every file this library writes.
        /// </summary>
        internal static PdfArray Matrix(int rotation, double rectWidth, double rectHeight)
        {
            if (rotation == 0) return null;
            var array = new PdfArray();
            foreach (double v in Coefficients(rotation, rectWidth, rectHeight))
                array.Add(new PdfNumber(v));
            return array;
        }

        /// <summary>
        /// Where a point of the layout box lands in the rectangle, rectangle-relative — for a
        /// renderer that synthesises an appearance directly instead of writing a form.
        /// </summary>
        internal static (double X, double Y) ToRectangle(
            int rotation, double rectWidth, double rectHeight, double x, double y)
        {
            double[] m = Coefficients(rotation, rectWidth, rectHeight);
            return (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]);
        }
    }
}
