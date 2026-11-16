// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Arithmetic on a PDF affine matrix <c>[a b c d e f]</c> (§8.3.4) held as six doubles — the
    /// form a <c>cm</c> operand takes and <c>Paragraph.Transform</c> carries: composition, applying
    /// one to a point, and conjugating one about a point so that it turns, scales or skews about
    /// that point rather than the origin (CSS <c>transform-origin</c>). One implementation for the
    /// producers that compose a transform (HtmlToPdf's mapper) and the exporter that applies it to
    /// painting and to annotation rectangles, which the content stream's matrix does not reach.
    /// </summary>
    /// <remarks>
    /// PDF composes row vectors: a point <c>p</c> under <c>M</c> then <c>N</c> is <c>p × M × N</c>,
    /// so <see cref="Multiply(double[], double[])"/><c>(M, N)</c> is "M first, then N" — the matrix
    /// written by an inner <c>cm</c> is the left factor of the CTM it produces with the outer one.
    /// </remarks>
    internal static class TransformMatrix
    {
        /// <summary>The identity.</summary>
        public static double[] Identity => new[] { 1.0, 0, 0, 1, 0, 0 };

        /// <summary>A translation by (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public static double[] Translation(double x, double y) => new[] { 1.0, 0, 0, 1, x, y };

        /// <summary><paramref name="m"/> applied first, then <paramref name="n"/> (<c>m × n</c> on row vectors).</summary>
        public static double[] Multiply(double[] m, double[] n) => new[]
        {
            m[0] * n[0] + m[1] * n[2],
            m[0] * n[1] + m[1] * n[3],
            m[2] * n[0] + m[3] * n[2],
            m[2] * n[1] + m[3] * n[3],
            m[4] * n[0] + m[5] * n[2] + n[4],
            m[4] * n[1] + m[5] * n[3] + n[5],
        };

        /// <summary>The point (<paramref name="x"/>, <paramref name="y"/>) under <paramref name="m"/>.</summary>
        public static (double X, double Y) Apply(double[] m, double x, double y)
            => (m[0] * x + m[2] * y + m[4], m[1] * x + m[3] * y + m[5]);

        /// <summary>
        /// <paramref name="m"/> taken about the point (<paramref name="ox"/>, <paramref name="oy"/>)
        /// instead of the origin: the point moved to the origin, <paramref name="m"/> applied, the
        /// point moved back — so a rotation turns about it and a scale grows from it, and the point
        /// itself stays where it is. A pure translation is unchanged by any origin.
        /// </summary>
        public static double[] About(double[] m, double ox, double oy)
            => Multiply(Multiply(Translation(-ox, -oy), m), Translation(ox, oy));

        /// <summary>
        /// The axis-aligned rectangle holding the four corners of the rectangle
        /// (<paramref name="x0"/>, <paramref name="y0"/>)–(<paramref name="x1"/>, <paramref name="y1"/>)
        /// under <paramref name="m"/>: the rectangle itself when <paramref name="m"/> only translates
        /// or scales, and the bounding box of the turned quadrilateral when it rotates or skews.
        /// </summary>
        public static (double X0, double Y0, double X1, double Y1) BoundsOf(double[] m, double x0, double y0, double x1, double y1)
        {
            var a = Apply(m, x0, y0);
            var b = Apply(m, x1, y0);
            var c = Apply(m, x1, y1);
            var d = Apply(m, x0, y1);
            return (Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)), Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
                    Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)), Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)));
        }
    }
}
