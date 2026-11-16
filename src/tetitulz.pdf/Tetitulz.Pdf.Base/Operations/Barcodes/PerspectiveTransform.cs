// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// A plane projective transform (a homography): the one mapping four points to four others, as a
    /// 3×3 matrix on homogeneous coordinates — what a barcode detector samples a foreshortened symbol
    /// through.
    /// </summary>
    /// <remarks>
    /// Built as Heckbert's <i>Fundamentals of Texture Mapping and Image Warping</i> (1989, §2.2.3)
    /// builds it, and as barcode detectors do: the unit square to each
    /// quadrilateral in closed form, one of the two inverted, the two composed. The matrix acts on a
    /// column vector, (u, v, w) = M · (x, y, 1), the point being (u / w, v / w); an inverse is the
    /// adjugate, since a homography's scale does not matter.
    /// </remarks>
    internal sealed class PerspectiveTransform
    {
        private readonly double[] _m;   // row-major 3×3

        private PerspectiveTransform(double[] m) => _m = m;

        /// <summary>
        /// The transform taking (x0, y0) … (x3, y3) to (u0, v0) … (u3, v3), the corners of each
        /// quadrilateral in the same order around it.
        /// </summary>
        internal static PerspectiveTransform QuadrilateralToQuadrilateral(
            double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3,
            double u0, double v0, double u1, double v1, double u2, double v2, double u3, double v3)
        {
            double[] toSquare = Adjugate(SquareToQuadrilateral(x0, y0, x1, y1, x2, y2, x3, y3));
            double[] fromSquare = SquareToQuadrilateral(u0, v0, u1, v1, u2, v2, u3, v3);
            return new PerspectiveTransform(Multiply(fromSquare, toSquare));
        }

        /// <summary>Maps (<paramref name="x"/>, <paramref name="y"/>); NaN when it maps to infinity.</summary>
        internal void Apply(double x, double y, out double u, out double v)
        {
            double w = (_m[6] * x) + (_m[7] * y) + _m[8];
            if (w == 0) { u = v = double.NaN; return; }
            u = ((_m[0] * x) + (_m[1] * y) + _m[2]) / w;
            v = ((_m[3] * x) + (_m[4] * y) + _m[5]) / w;
        }

        /// <summary>
        /// The unit square's corners (0, 0), (1, 0), (1, 1), (0, 1) to the four points, in closed form;
        /// a parallelogram is the affine case.
        /// </summary>
        private static double[] SquareToQuadrilateral(
            double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            double sx = x0 - x1 + x2 - x3, sy = y0 - y1 + y2 - y3;
            double g = 0, h = 0;
            if (sx != 0 || sy != 0)
            {
                double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
                double denominator = (dx1 * dy2) - (dx2 * dy1);
                g = ((sx * dy2) - (dx2 * sy)) / denominator;
                h = ((dx1 * sy) - (sx * dy1)) / denominator;
            }
            return new[]
            {
                x1 - x0 + (g * x1), x3 - x0 + (h * x3), x0,
                y1 - y0 + (g * y1), y3 - y0 + (h * y3), y0,
                g,                  h,                  1,
            };
        }

        private static double[] Adjugate(double[] m) => new[]
        {
            (m[4] * m[8]) - (m[5] * m[7]), (m[2] * m[7]) - (m[1] * m[8]), (m[1] * m[5]) - (m[2] * m[4]),
            (m[5] * m[6]) - (m[3] * m[8]), (m[0] * m[8]) - (m[2] * m[6]), (m[2] * m[3]) - (m[0] * m[5]),
            (m[3] * m[7]) - (m[4] * m[6]), (m[1] * m[6]) - (m[0] * m[7]), (m[0] * m[4]) - (m[1] * m[3]),
        };

        private static double[] Multiply(double[] a, double[] b)
        {
            var r = new double[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r[(i * 3) + j] = (a[i * 3] * b[j]) + (a[(i * 3) + 1] * b[3 + j]) + (a[(i * 3) + 2] * b[6 + j]);
            return r;
        }
    }
}
