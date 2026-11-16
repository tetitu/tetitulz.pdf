// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Drawing;

namespace Tetitulz.Pdf.StampSign
{
    /// <summary>
    /// Page geometry in the terms the user sees: "display" coordinates are points from the page's
    /// top-left corner as it is shown (after its <c>/Rotate</c>), and they are turned into the page's
    /// own PDF coordinates (points, origin bottom-left, the crop box's) for writing.
    /// </summary>
    internal static class Placement
    {
        public const double PointsPerMm = 72.0 / 25.4;

        /// <summary>The page's shown width and height in points.</summary>
        public static SizeF DisplaySize(RectangleF box, int rotation)
            => rotation == 90 || rotation == 270 ? new SizeF(box.Height, box.Width) : new SizeF(box.Width, box.Height);

        /// <summary>A shown point (from the shown top-left) in the page's PDF coordinates.</summary>
        public static PointF DisplayToPdf(RectangleF box, int rotation, double u, double v)
        {
            // RectangleF's Y is the smaller y and its Bottom the larger: in PDF terms the box's bottom and top.
            (double x, double y) = ShownPage.ToPage(box.X, box.Y, box.Right, box.Bottom, rotation, u, v);
            return new PointF((float)x, (float)y);
        }

        /// <summary>A shown rectangle as the PDF rectangle covering it.</summary>
        public static RectangleF DisplayRectToPdf(RectangleF box, int rotation, double u, double v, double w, double h)
        {
            PointF a = DisplayToPdf(box, rotation, u, v);
            PointF b = DisplayToPdf(box, rotation, u + w, v + h);
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        /// <summary>The signature's fixed rectangle on a page, in PDF coordinates, from the settings.</summary>
        public static RectangleF SignatureRect(AppSettings s, RectangleF box, int rotation)
        {
            SizeF shown = DisplaySize(box, rotation);
            double w = s.SignatureWidthMm * PointsPerMm, h = s.SignatureHeightMm * PointsPerMm;
            double dx = s.OffsetXMm * PointsPerMm, dy = s.OffsetYMm * PointsPerMm;
            double u = s.Anchor == SignatureAnchor.BottomLeft || s.Anchor == SignatureAnchor.TopLeft ? dx : shown.Width - dx - w;
            double v = s.Anchor == SignatureAnchor.TopLeft || s.Anchor == SignatureAnchor.TopRight ? dy : shown.Height - dy - h;
            return DisplayRectToPdf(box, rotation, u, v, w, h);
        }

        /// <summary>
        /// The <c>cm</c> matrix that draws an image's unit square over <paramref name="rect"/> upright as
        /// the page is shown: the image's bottom-left, bottom-right and top-left corners where the
        /// viewer shows them.
        /// </summary>
        public static double[] UprightImageMatrix(RectangleF rect, int rotation)
            // RectangleF's Top is the smaller y: in PDF terms the rectangle's bottom edge.
            => UprightImage.Matrix(rect.Left, rect.Top, rect.Right, rect.Bottom, rotation);
    }
}
