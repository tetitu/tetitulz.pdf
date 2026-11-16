// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// Where a shape's outline is written: a thin sink over the operator
    /// <see cref="StringBuilder"/> the drawing is being built into.
    ///
    /// <para>
    /// Deliberately not a path <i>object model</i>. A preset builder writes its outline straight
    /// into the content stream, so there is no second representation of the geometry to keep in
    /// sync with the first and nothing is allocated per shape. `ImageRenderer.EmitShape` already
    /// established that convention for this renderer; this only gives it a name.
    /// </para>
    ///
    /// <para>
    /// <b>Coordinates are the shape's own, top-down, spanning (0,0)–(w,h) in points</b> — the frame
    /// ECMA-376 authors <c>a:pathLst</c> in, and the frame <see cref="DrawingTransform"/> has
    /// already established with a single y-flip at the drawing level. A builder therefore never
    /// thinks about PDF's upward y axis.
    /// </para>
    /// </summary>
    internal sealed class GeometryPath
    {
        private readonly StringBuilder _ops;

        internal GeometryPath(StringBuilder ops)
        {
            _ops = ops ?? throw new ArgumentNullException(nameof(ops));
        }

        internal void MoveTo(double x, double y)
            => _ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} m\n", x, y);

        internal void LineTo(double x, double y)
            => _ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} l\n", x, y);

        internal void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
            => _ops.AppendFormat(CultureInfo.InvariantCulture,
                "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n", x1, y1, x2, y2, x3, y3);

        internal void Close() => _ops.Append("h\n");

        /// <summary>
        /// A whole rectangle as a single <c>re</c>. Kept distinct from four <see cref="LineTo"/>
        /// calls because <c>re</c> is what the renderer has always emitted for a plain rectangle,
        /// and the existing shape tests assert on it.
        /// </summary>
        internal void Rectangle(double x, double y, double w, double h)
            => _ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2} {3:F2} re\n", x, y, w, h);

        /// <summary>
        /// The bezier constant for a quarter arc: <c>4/3·tan(90°/4)</c>. A circle drawn from four
        /// cubic segments with control points this far along the tangents is within 0.02% of the
        /// true circle — the standard approximation, and the value this renderer has always used.
        /// </summary>
        internal const double QuarterArcK = 0.5522847498;

        /// <summary>
        /// A full ellipse inscribed in <c>(x, y, w, h)</c>, as four cubic arcs, closed.
        /// </summary>
        internal void Ellipse(double x, double y, double w, double h)
        {
            double cx = x + w / 2, cy = y + h / 2, rx = w / 2, ry = h / 2;
            double kx = rx * QuarterArcK, ky = ry * QuarterArcK;

            MoveTo(cx + rx, cy);
            CurveTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry);
            CurveTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy);
            CurveTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry);
            CurveTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy);
            Close();
        }

        /// <summary>
        /// A rounded corner joining a horizontal run to a vertical one, as one cubic arc from
        /// <c>(x0,y0)</c> to <c>(x1,y1)</c> whose control points pull towards the corner
        /// <c>(cxr, cyr)</c>. Used by <c>roundRect</c> and its relatives.
        /// </summary>
        internal void CornerTo(double x0, double y0, double cxr, double cyr, double x1, double y1)
            => CurveTo(x0 + (cxr - x0) * QuarterArcK, y0 + (cyr - y0) * QuarterArcK,
                       x1 + (cxr - x1) * QuarterArcK, y1 + (cyr - y1) * QuarterArcK,
                       x1, y1);
    }
}
