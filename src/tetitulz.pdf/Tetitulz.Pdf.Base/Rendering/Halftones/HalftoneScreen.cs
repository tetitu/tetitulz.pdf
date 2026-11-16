// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering.Halftones
{
    /// <summary>
    /// One halftone screen in device space (ISO 32000-2 §10.6): a lattice of cells tiling the device's pixels and,
    /// for each pixel, a threshold — every type of halftone in one form.
    /// </summary>
    /// <remarks>
    /// <para><b>The one rule.</b> A pixel takes ink where <c>(255 − tint) × M &lt; T × 255</c>: the component's
    /// additive level, <c>1 − tint</c>, below the pixel's threshold <c>T / M</c>. For a threshold array (§10.6.4,
    /// Types 6, 10 and 16) <c>T</c> is the array's value, a 0 read as 1, and <c>M</c> 255 or 65 535. For a spot
    /// function (§10.6.3, Type 1) a cell of <c>n</c> pixels whitens <c>floor(g × n)</c> of them, lowest spot value
    /// first, so the pixel ranked <c>k</c> is inked exactly when <c>floor(g × n) ≤ k</c> — the same rule with
    /// <c>M = n</c> and <c>T = k + 1</c>.</para>
    /// <para><b>One tiling.</b> A threshold array is a <c>W × H</c> rectangle with, for Types 10 and 16, a second
    /// <c>W₂ × H₂</c> below it starting in the same column (§10.6.5.4, NOTE 2: the first square's last row is
    /// immediately followed by the second's first); the two repeat under the lattice spanned by <c>(W, −H₂)</c>
    /// and <c>(W₂, H)</c>, device y downward — the next pair to the right raised by the second's height, the pair
    /// below moved right by its width, so the area to the right of the upper rectangle falls in the lower one's
    /// notch (Figure 67) — Type 10's <c>(X, −Y)</c> and <c>(Y, X)</c>, and for one rectangle <c>(W, 0)</c> and
    /// <c>(0, H)</c>. A spot-function screen's cell is the square of side <c>resolution ÷ frequency</c> at its
    /// angle, its side vector rounded to whole pixels as ordinary halftoning does (§10.6.5.2, NOTE 1), and its
    /// pixels are the centres inside that square. Either way the pattern repeats with a period derived from the
    /// lattice, and the screen holds one period's thresholds as a tile when that is small enough; past that, each
    /// pixel is placed in its cell by the same integer arithmetic.</para>
    /// <para>All arithmetic that places a pixel is in integers: a pixel on a cell's edge is never put in two cells
    /// or none by a rounding.</para>
    /// </remarks>
    internal sealed class HalftoneScreen
    {
        /// <summary>The largest period tile kept, in pixels; past it each pixel is placed on its own.</summary>
        internal const int MaxTilePixels = 1 << 22;

        private readonly int _x1, _y1, _x2, _y2;   // the lattice: t1 = (x1, y1), t2 = (x2, y2)
        private readonly long _det;                // |t1 × t2|, the pixels in one cell
        private readonly Func<int, int, int> _threshold;   // a pixel's threshold, 1 … Denominator
        private readonly byte[] _minInk;           // over one period: the least tint that inks the pixel
        private readonly int _periodX, _periodY;

        private HalftoneScreen(int x1, int y1, int x2, int y2, int denominator, Func<int, int, int> threshold)
        {
            _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
            _det = Math.Abs((long)x1 * y2 - (long)x2 * y1);
            if (_det == 0) throw new ArgumentException("a halftone cell has no area");
            Denominator = denominator;
            _threshold = threshold;

            long px = Period(_det, y1, y2), py = Period(_det, x1, x2);
            if (px * py <= MaxTilePixels)
            {
                _periodX = (int)px;
                _periodY = (int)py;
                _minInk = new byte[_periodX * _periodY];
                for (int y = 0; y < _periodY; y++)
                    for (int x = 0; x < _periodX; x++)
                        _minInk[y * _periodX + x] = MinInk(threshold(x, y), denominator);
            }
        }

        /// <summary>The <c>M</c> of the rule: 255 or 65 535 for a threshold array, the cell's pixel count for a spot function.</summary>
        internal int Denominator { get; }

        /// <summary>The pixels in one cell — the levels the screen renders, less one.</summary>
        internal long CellPixels => _det;

        /// <summary>The period tile's size, or 0 when the screen places each pixel on its own.</summary>
        internal int PeriodX => _minInk == null ? 0 : _periodX;

        /// <summary>See <see cref="PeriodX"/>.</summary>
        internal int PeriodY => _minInk == null ? 0 : _periodY;

        /// <summary>The cell's side vector — a spot-function screen's frequency is resolution ÷ its length.</summary>
        internal (int X, int Y) CellVector => (_x1, _y1);

        /// <summary>The least tint, 1 … 255, that inks device pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
        internal int MinInkAt(int x, int y)
        {
            if (_minInk == null) return MinInk(_threshold(x, y), Denominator);
            return _minInk[Mod(y, _periodY) * _periodX + Mod(x, _periodX)];
        }

        /// <summary>
        /// The least tint, 1 … 255, that inks device pixel (<paramref name="x"/>, <paramref name="y"/>), worked out
        /// from the threshold without the tile — the reference the tile is checked against.
        /// </summary>
        internal int MinInkUntiled(int x, int y) => MinInk(_threshold(x, y), Denominator);

        /// <summary>
        /// A plane of ink amounts (0 none … 255 full, row-major) halftoned to packed 1-bit rows, most significant
        /// bit first, 1 where ink is laid. <paramref name="transfer"/>, when given, maps each tint first (the
        /// halftone's transfer function, §10.6.5.2); <paramref name="originX"/> and <paramref name="originY"/> shift
        /// the screen's phase (<c>/HTO</c>).
        /// </summary>
        internal byte[] Apply(byte[] tints, int width, int height, byte[] transfer = null, int originX = 0, int originY = 0)
        {
            int stride = (width + 7) / 8;
            var bits = new byte[checked(stride * height)];
            for (int y = 0; y < height; y++)
            {
                int row = y * width, outRow = y * stride;
                int sy = y + originY;
                for (int x = 0; x < width; x++)
                {
                    int tint = tints[row + x];
                    if (tint == 0) continue;
                    if (transfer != null) tint = transfer[tint];
                    if (tint >= MinInkAt(x + originX, sy))
                        bits[outRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
            return bits;
        }

        /// <summary>The top of the tint scale a plane holds: a tint is 0 … this, an integer, never a colour channel quantised.</summary>
        private const int FullTint = 255;

        /// <summary>
        /// The least tint inking a pixel of threshold <paramref name="threshold"/>, 1 … <paramref name="m"/>, under
        /// denominator <paramref name="m"/>: (255 − tint) × M &lt; T × 255, so 256 − ⌈T × 255 / M⌉, 1 … 255 — an
        /// integer ceiling, exact.
        /// </summary>
        private static byte MinInk(int threshold, int m) => (byte)(FullTint + 1 - ((long)threshold * FullTint + m - 1) / m);

        /// <summary>
        /// The smallest <c>n &gt; 0</c> with <c>(n, 0)</c> in the lattice — or, called with the x components, <c>(0, n)</c>:
        /// <c>n × v / det</c> is whole for both components <c>v</c>.
        /// </summary>
        private static long Period(long det, int a, int b)
            => Lcm(det / Gcd(det, Math.Abs((long)a)), det / Gcd(det, Math.Abs((long)b)));

        private static long Gcd(long a, long b)
        {
            while (b != 0) { long t = a % b; a = b; b = t; }
            return a == 0 ? 1 : a;
        }

        private static long Lcm(long a, long b) => a / Gcd(a, b) * b;

        private static int Mod(int v, int m) { int r = v % m; return r < 0 ? r + m : r; }

        private static long FloorDiv(long a, long b) { long q = a / b; return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q; }

        // ------------------------------------------------------------------ threshold arrays

        /// <summary>
        /// A threshold array (§10.6.4): a <paramref name="width"/> × <paramref name="height"/> rectangle and, for Types
        /// 10 and 16, a second <paramref name="width2"/> × <paramref name="height2"/> below it; <paramref name="values"/>
        /// the first rectangle's thresholds row by row, then the second's, each 1 … <paramref name="maximum"/> (a 0 is
        /// read as 1).
        /// </summary>
        internal static HalftoneScreen FromThresholdArray(int width, int height, int width2, int height2, int[] values, int maximum)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("a threshold array needs a positive width and height");
            if (width2 < 0 || height2 < 0 || (width2 == 0) != (height2 == 0)) throw new ArgumentException("a second rectangle needs both sides");
            long expected = (long)width * height + (long)width2 * height2;
            if (values == null || values.Length != expected) throw new ArgumentException("the threshold array holds the wrong number of values");

            int x1 = width, y1 = -height2, x2 = width2, y2 = height;
            long det = (long)x1 * y2 - (long)x2 * y1;

            int Threshold(int x, int y)
            {
                // The real lattice coordinates of the pixel, then the few lattice translates near them: one puts the
                // pixel inside the two rectangles.
                long aNum = (long)x * y2 - (long)y * x2, bNum = (long)x1 * y - (long)y1 * x;
                long a0 = FloorDiv(aNum, det), b0 = FloorDiv(bNum, det);
                for (long i = -2; i <= 2; i++)
                    for (long j = -2; j <= 2; j++)
                    {
                        long qx = x - (a0 + i) * x1 - (b0 + j) * x2;
                        long qy = y - (a0 + i) * y1 - (b0 + j) * y2;
                        if (qx >= 0 && qx < width && qy >= 0 && qy < height) return Value(values[qy * width + qx]);
                        if (width2 > 0 && qx >= 0 && qx < width2 && qy >= height && qy < height + height2)
                            return Value(values[(long)width * height + (qy - height) * width2 + qx]);
                    }
                throw new InvalidOperationException("a pixel lies in no cell of the threshold array");
            }

            int Value(int v) => v <= 0 ? 1 : v;

            return new HalftoneScreen(x1, y1, x2, y2, maximum, Threshold);
        }

        // ------------------------------------------------------------------ spot functions

        /// <summary>
        /// A Type 1 screen (§10.6.5.2): cells of side <paramref name="dpi"/> ÷ <paramref name="frequency"/> pixels at
        /// <paramref name="angle"/> degrees counterclockwise in device space, each pixel whitened in the order
        /// <paramref name="spot"/> gives it at the pixel's centre in cell coordinates. A cell larger than
        /// <paramref name="maxCellPixels"/> is refused (null).
        /// </summary>
        internal static HalftoneScreen FromSpotFunction(double frequency, double angle, double dpi,
            Func<double, double, double> spot, long maxCellPixels)
        {
            if (!(frequency > 0) || double.IsInfinity(frequency) || !(dpi > 0) || double.IsNaN(angle) || double.IsInfinity(angle))
                return null;
            double side = dpi / frequency;
            double theta = angle % 90;
            if (theta < 0) theta += 90;
            double radians = theta * Math.PI / 180;
            int x1 = SaturatingCast.ToInt(Math.Round(side * Math.Cos(radians))), y1 = SaturatingCast.ToInt(Math.Round(side * Math.Sin(radians)));
            if (x1 == 0 && y1 == 0) x1 = 1;
            int x2 = -y1, y2 = x1;
            long det = (long)x1 * x1 + (long)y1 * y1;
            if (det > maxCellPixels) return null;

            // The cell's pixels: the centres (x + ½, y + ½) inside the square spanned by t1 and t2, each with its
            // spot value, ranked. Doubled coordinates keep every test in integers.
            var cell = new List<(int X, int Y, double Value)>((int)det);
            int minX = Math.Min(0, Math.Min(x1, Math.Min(x2, x1 + x2))), maxX = Math.Max(0, Math.Max(x1, Math.Max(x2, x1 + x2)));
            int minY = Math.Min(0, Math.Min(y1, Math.Min(y2, y1 + y2))), maxY = Math.Max(0, Math.Max(y1, Math.Max(y2, y1 + y2)));
            for (int y = minY; y < maxY; y++)
                for (int x = minX; x < maxX; x++)
                {
                    long aNum = (2L * x + 1) * y2 - (2L * y + 1) * x2, bNum = x1 * (2L * y + 1) - y1 * (2L * x + 1);
                    if (aNum < 0 || aNum >= 2 * det || bNum < 0 || bNum >= 2 * det) continue;
                    double s = (double)aNum / (2 * det), t = (double)bNum / (2 * det);
                    cell.Add((x, y, spot(2 * s - 1, 2 * t - 1)));
                }
            if (cell.Count != det) throw new InvalidOperationException("a halftone cell holds the wrong number of pixels");

            // Lowest value whitened first; equal values in scan order, a choice the specification leaves open.
            var order = new int[cell.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (i, j) =>
            {
                int c = cell[i].Value.CompareTo(cell[j].Value);
                if (c != 0) return c;
                c = cell[i].Y.CompareTo(cell[j].Y);
                return c != 0 ? c : cell[i].X.CompareTo(cell[j].X);
            });
            var rank = new Dictionary<long, int>(cell.Count);
            for (int k = 0; k < order.Length; k++)
                rank[Key(cell[order[k]].X, cell[order[k]].Y)] = k + 1;

            int Threshold(int x, int y)
            {
                long aNum = (2L * x + 1) * y2 - (2L * y + 1) * x2, bNum = x1 * (2L * y + 1) - y1 * (2L * x + 1);
                long a = FloorDiv(aNum, 2 * det), b = FloorDiv(bNum, 2 * det);
                long cx = x - a * x1 - b * x2, cy = y - a * y1 - b * y2;
                return rank[Key((int)cx, (int)cy)];
            }

            return new HalftoneScreen(x1, y1, x2, y2, (int)det, Threshold);
        }

        private static long Key(int x, int y) => ((long)x << 32) ^ (uint)y;
    }
}
