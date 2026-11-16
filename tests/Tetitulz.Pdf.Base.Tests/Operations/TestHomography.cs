// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The barcode perspective tests' own projective transform, solved from four point pairs here — never the
/// locator's <c>PerspectiveTransform</c>, which the tests exist to check.
/// </summary>
internal static class TestHomography
{
    /// <summary>
    /// The 3×3 homography (row-major, h22 = 1) taking each of <paramref name="from"/> to the matching
    /// <paramref name="to"/>.
    /// </summary>
    internal static double[] Solve((double X, double Y)[] from, (double X, double Y)[] to)
    {
        var m = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            var (u, v) = from[i];
            var (x, y) = to[i];
            double[] row1 = { u, v, 1, 0, 0, 0, -u * x, -v * x, x };
            double[] row2 = { 0, 0, 0, u, v, 1, -u * y, -v * y, y };
            for (int k = 0; k < 9; k++) { m[2 * i, k] = row1[k]; m[(2 * i) + 1, k] = row2[k]; }
        }
        // Gauss–Jordan with partial pivoting on the 8×8 system.
        for (int col = 0; col < 8; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 8; row++) if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            for (int k = 0; k < 9; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            double p = m[col, col];
            for (int k = 0; k < 9; k++) m[col, k] /= p;
            for (int row = 0; row < 8; row++)
            {
                if (row == col) continue;
                double f = m[row, col];
                for (int k = 0; k < 9; k++) m[row, k] -= f * m[col, k];
            }
        }
        var h = new double[9];
        for (int i = 0; i < 8; i++) h[i] = m[i, 8];
        h[8] = 1;
        return h;
    }

    internal static double[] Invert(double[] h)
    {
        double a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], k = h[7], l = h[8];
        double det = (a * ((e * l) - (f * k))) - (b * ((d * l) - (f * g))) + (c * ((d * k) - (e * g)));
        return new[]
        {
            ((e * l) - (f * k)) / det, ((c * k) - (b * l)) / det, ((b * f) - (c * e)) / det,
            ((f * g) - (d * l)) / det, ((a * l) - (c * g)) / det, ((c * d) - (a * f)) / det,
            ((d * k) - (e * g)) / det, ((b * g) - (a * k)) / det, ((a * e) - (b * d)) / det,
        };
    }

    /// <summary>The point <paramref name="h"/> takes (<paramref name="x"/>, <paramref name="y"/>) to.</summary>
    internal static (double X, double Y) Apply(double[] h, double x, double y)
    {
        double d = (h[6] * x) + (h[7] * y) + h[8];
        return (((h[0] * x) + (h[1] * y) + h[2]) / d, ((h[3] * x) + (h[4] * y) + h[5]) / d);
    }
}
