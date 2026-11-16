// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The forward discrete wavelet transformation of T.800 F.4: the 2D_SD procedure at each
    /// decomposition level — every column through 1D_SD, then every row, then 2D_DEINTERLEAVE into
    /// the four sub-bands (Figure F.28) — on the 5-3 reversible filter (F-9, F-10, in integers) or
    /// the 9-7 irreversible one (F-11, Table F.4). Signals are extended by the periodic symmetric
    /// extension of F.4.7, a signal of one sample is kept or doubled by the parity of its index
    /// (F.4.6), and every sub-band's extent is <see cref="JpxGeometry.Subband"/>'s — the one
    /// geometry the tile layout uses too.
    /// </summary>
    /// <remarks>
    /// <para><b>The extension is read, not built.</b> F.4.7's extension is a mirror about a signal's
    /// first and last samples, and every lifting step of either filter is symmetric, so after each step
    /// a position outside the signal holds what its mirror inside holds: lifting the signal's own
    /// positions, a step reads a neighbour one past an end from the position one inside it (F-4's
    /// PSE<sub>O</sub> at distance one), and the values are those of lifting the extended signal —
    /// the same operands in the same order, bit for bit, the 9-7's doubles included. Nothing is copied
    /// out of the plane for it.</para>
    /// <para><b>The columns are lifted a row at a time.</b> VER_SD's 1D_SD runs down every column; a
    /// lifting step updates the rows of one parity from the rows either side, so each step is a pass
    /// over whole rows — memory in order — rather than a column gathered sample by sample.</para>
    /// </remarks>
    internal static class JpxForwardWavelet
    {
        /// <summary>Table F.4's lifting parameters and scaling for the 9-7 filter.</summary>
        internal const double Alpha = -1.586134342059924;
        internal const double Beta = -0.052980118572961;
        internal const double Gamma = 0.882911075530934;
        internal const double Delta = 0.443506852043971;
        internal const double K = 1.230174104914001;

        /// <summary>
        /// The 5-3 reversible FDWT of a tile-component whose samples fill <paramref name="bounds"/> in
        /// raster order, over <paramref name="levels"/> decomposition levels. The result is indexed by
        /// resolution level, then band: <c>[0]</c> holds the N<sub>L</sub>LL band alone; <c>[r]</c> for
        /// r ≥ 1 holds HL, LH and HH of decomposition level N<sub>L</sub> − r + 1 — the order
        /// <see cref="JpxResolution.Bands"/> takes — each in raster order over its own extent.
        /// </summary>
        internal static long[][][] Reversible(long[] samples, JpxRect bounds, int levels) =>
            Decompose(samples, bounds, levels, Columns53, Rows53);

        /// <summary>The reversible FDWT of integer samples: the <c>long</c> one, its bands narrowed back — for a caller that holds integers.</summary>
        internal static int[][][] Reversible(int[] samples, JpxRect bounds, int levels)
        {
            long[][][] wide = Reversible(JpxForwardComponentTransform.Widen(samples), bounds, levels);
            var result = new int[wide.Length][][];
            for (int r = 0; r < wide.Length; r++)
            {
                result[r] = new int[wide[r].Length][];
                for (int b = 0; b < wide[r].Length; b++)
                {
                    result[r][b] = new int[wide[r][b].Length];
                    for (int i = 0; i < wide[r][b].Length; i++) result[r][b][i] = checked((int)wide[r][b][i]);
                }
            }
            return result;
        }

        /// <summary>The 9-7 irreversible FDWT, laid out as <see cref="Reversible(long[], JpxRect, int)"/>'s result.</summary>
        internal static double[][][] Irreversible(double[] samples, JpxRect bounds, int levels) =>
            Decompose(samples, bounds, levels, Columns97, Rows97);

        /// <summary>The irreversible FDWT of <c>float</c> samples: the <c>double</c> one, its bands narrowed back.</summary>
        internal static float[][][] Irreversible(float[] samples, JpxRect bounds, int levels)
        {
            double[][][] wide = Irreversible(JpxForwardComponentTransform.ToDoubles(samples), bounds, levels);
            var result = new float[wide.Length][][];
            for (int r = 0; r < wide.Length; r++)
            {
                result[r] = new float[wide[r].Length][];
                for (int b = 0; b < wide[r].Length; b++)
                {
                    result[r][b] = new float[wide[r][b].Length];
                    for (int i = 0; i < wide[r][b].Length; i++) result[r][b][i] = (float)wide[r][b][i];
                }
            }
            return result;
        }

        /// <summary>1D_SD with the 5-3 filter (F.4.8.1) on the signal x(i0) … x(i1 − 1), in place.</summary>
        internal static void Filter53(long[] x, int i0, int i1)
        {
            if (i1 - i0 >= 1) Rows53(x, i1 - i0, 1, i0);
        }

        /// <summary>The 5-3 filter on an integer line: the <c>long</c> one over a copy.</summary>
        internal static void Filter53(int[] x, int i0, int i1)
        {
            long[] wide = JpxForwardComponentTransform.Widen(x);
            Filter53(wide, i0, i1);
            for (int i = 0; i < x.Length; i++) x[i] = checked((int)wide[i]);
        }

        /// <summary>1D_SD with the 9-7 filter (F.4.8.2) on the signal x(i0) … x(i1 − 1), in place.</summary>
        internal static void Filter97(double[] x, int i0, int i1)
        {
            if (i1 - i0 >= 1) Rows97(x, i1 - i0, 1, i0);
        }

        /// <summary>The 9-7 filter on a <c>float</c> line: the <c>double</c> one over a copy.</summary>
        internal static void Filter97(float[] x, int i0, int i1)
        {
            double[] wide = JpxForwardComponentTransform.ToDoubles(x);
            Filter97(wide, i0, i1);
            for (int i = 0; i < x.Length; i++) x[i] = (float)wide[i];
        }

        // ---- The 5-3 filter (F.4.8.1): F-9 at the odd positions, then F-10 at the even ones -------------

        /// <summary>HOR_SD with the 5-3 filter: each of the <paramref name="h"/> rows of <paramref name="w"/> samples, whose first is at position <paramref name="u0"/>.</summary>
        private static void Rows53(long[] a, int w, int h, int u0)
        {
            if (w == 1)
            {
                // F.4.6: kept at an even position, doubled at an odd one.
                if ((u0 & 1) == 1) for (int y = 0; y < h; y++) a[y] *= 2;
                return;
            }
            int odd = (u0 & 1) == 1 ? 0 : 1, even = 1 - odd;
            for (int y = 0, s = 0; y < h; y++, s += w)
            {
                // F-9: Y(2n + 1) = X(2n + 1) − ⌊(X(2n) + X(2n + 2)) / 2⌋.
                for (int k = odd; k < w; k += 2)
                    a[s + k] -= (a[s + (k == 0 ? 1 : k - 1)] + a[s + (k == w - 1 ? w - 2 : k + 1)]) >> 1;
                // F-10: Y(2n) = X(2n) + ⌊(Y(2n − 1) + Y(2n + 1) + 2) / 4⌋.
                for (int k = even; k < w; k += 2)
                    a[s + k] += (a[s + (k == 0 ? 1 : k - 1)] + a[s + (k == w - 1 ? w - 2 : k + 1)] + 2) >> 2;
            }
        }

        /// <summary>VER_SD with the 5-3 filter, row by row: the <paramref name="h"/> rows of <paramref name="w"/> samples are the column signal's positions from <paramref name="v0"/>.</summary>
        private static void Columns53(long[] a, int w, int h, int v0)
        {
            if (h == 1)
            {
                if ((v0 & 1) == 1) for (int x = 0; x < w; x++) a[x] *= 2;
                return;
            }
            int odd = (v0 & 1) == 1 ? 0 : 1, even = 1 - odd;
            for (int k = odd; k < h; k += 2)
            {
                int row = k * w, above = (k == 0 ? 1 : k - 1) * w, below = (k == h - 1 ? h - 2 : k + 1) * w;
                for (int x = 0; x < w; x++) a[row + x] -= (a[above + x] + a[below + x]) >> 1;
            }
            for (int k = even; k < h; k += 2)
            {
                int row = k * w, above = (k == 0 ? 1 : k - 1) * w, below = (k == h - 1 ? h - 2 : k + 1) * w;
                for (int x = 0; x < w; x++) a[row + x] += (a[above + x] + a[below + x] + 2) >> 2;
            }
        }

        // ---- The 9-7 filter (F.4.8.2): four lifting steps — odd, even, odd, even — then the scalings ----------

        /// <summary>HOR_SD with the 9-7 filter on each of the <paramref name="h"/> rows.</summary>
        private static void Rows97(double[] a, int w, int h, int u0)
        {
            if (w == 1)
            {
                if ((u0 & 1) == 1) for (int y = 0; y < h; y++) a[y] *= 2;
                return;
            }
            int odd = (u0 & 1) == 1 ? 0 : 1, even = 1 - odd;
            for (int y = 0, s = 0; y < h; y++, s += w)
            {
                LiftRow(a, s, w, odd, Alpha);   // STEP1
                LiftRow(a, s, w, even, Beta);   // STEP2
                LiftRow(a, s, w, odd, Gamma);   // STEP3
                LiftRow(a, s, w, even, Delta);  // STEP4
                for (int k = odd; k < w; k += 2) a[s + k] *= K;        // STEP5
                for (int k = even; k < w; k += 2) a[s + k] *= 1 / K;   // STEP6
            }
        }

        /// <summary>One lifting step along a row: Y(p) += factor · (Y(p − 1) + Y(p + 1)) at every position from <paramref name="first"/> in steps of two.</summary>
        private static void LiftRow(double[] a, int s, int w, int first, double factor)
        {
            for (int k = first; k < w; k += 2)
                a[s + k] += factor * (a[s + (k == 0 ? 1 : k - 1)] + a[s + (k == w - 1 ? w - 2 : k + 1)]);
        }

        /// <summary>VER_SD with the 9-7 filter, row by row.</summary>
        private static void Columns97(double[] a, int w, int h, int v0)
        {
            if (h == 1)
            {
                if ((v0 & 1) == 1) for (int x = 0; x < w; x++) a[x] *= 2;
                return;
            }
            int odd = (v0 & 1) == 1 ? 0 : 1, even = 1 - odd;
            LiftRows(a, w, h, odd, Alpha);   // STEP1
            LiftRows(a, w, h, even, Beta);   // STEP2
            LiftRows(a, w, h, odd, Gamma);   // STEP3
            LiftRows(a, w, h, even, Delta);  // STEP4
            for (int k = odd; k < h; k += 2)
                for (int x = 0, row = k * w; x < w; x++) a[row + x] *= K;        // STEP5
            for (int k = even; k < h; k += 2)
                for (int x = 0, row = k * w; x < w; x++) a[row + x] *= 1 / K;    // STEP6
        }

        /// <summary>One lifting step down the columns: the rows from <paramref name="first"/> in steps of two, each from the rows either side.</summary>
        private static void LiftRows(double[] a, int w, int h, int first, double factor)
        {
            for (int k = first; k < h; k += 2)
            {
                int row = k * w, above = (k == 0 ? 1 : k - 1) * w, below = (k == h - 1 ? h - 2 : k + 1) * w;
                for (int x = 0; x < w; x++) a[row + x] += factor * (a[above + x] + a[below + x]);
            }
        }

        /// <summary>One level's filtering of a w-by-h array whose first sample is at position <c>start</c>.</summary>
        private delegate void Pass<T>(T[] a, int w, int h, int start);

        private static T[][][] Decompose<T>(T[] samples, JpxRect bounds, int levels, Pass<T> columns, Pass<T> rows)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            if (bounds.Width < 0 || bounds.Height < 0 || samples.Length != bounds.Width * bounds.Height)
                throw new ArgumentException("the samples must fill the tile-component", nameof(samples));
            if (levels < 0) throw new ArgumentOutOfRangeException(nameof(levels));

            var result = new T[levels + 1][][];
            T[] current = (T[])samples.Clone();   // the caller's samples are left as they were
            JpxRect cur = bounds;
            for (int lev = 1; lev <= levels; lev++)
            {
                int w = (int)cur.Width, h = (int)cur.Height;
                int u0 = (int)cur.X0, v0 = (int)cur.Y0;

                // VER_SD, then HOR_SD (F.4.2). A tile-component can be empty at a deep level (B-15):
                // there is nothing to filter then.
                if (w > 0 && h > 0)
                {
                    columns(current, w, h, v0);
                    rows(current, w, h, u0);
                }

                // 2D_DEINTERLEAVE (Figure F.28) into the four sub-bands of this level, whose extents
                // are the geometry's; a coefficient at (u, v) lands at (⌊u/2⌋, ⌊v/2⌋) of the band its
                // parities name.
                JpxRect ll = JpxGeometry.Subband(bounds, lev, 0, 0);
                JpxRect hl = JpxGeometry.Subband(bounds, lev, 1, 0);
                JpxRect lh = JpxGeometry.Subband(bounds, lev, 0, 1);
                JpxRect hh = JpxGeometry.Subband(bounds, lev, 1, 1);
                var llData = new T[ll.Width * ll.Height];
                var hlData = new T[hl.Width * hl.Height];
                var lhData = new T[lh.Width * lh.Height];
                var hhData = new T[hh.Width * hh.Height];
                for (int y = 0; y < h; y++)
                {
                    int v = v0 + y;
                    bool oddV = (v & 1) == 1;
                    // The row's even positions go to LL (or LH), its odd ones to HL (or HH).
                    JpxRect lowBand = oddV ? lh : ll, highBand = oddV ? hh : hl;
                    T[] low = oddV ? lhData : llData, high = oddV ? hhData : hlData;
                    int lowRow = (int)((v >> 1) - lowBand.Y0) * (int)lowBand.Width - (int)lowBand.X0;
                    int highRow = (int)((v >> 1) - highBand.Y0) * (int)highBand.Width - (int)highBand.X0;
                    for (int x = 0, at = y * w; x < w; x++, at++)
                    {
                        int u = u0 + x;
                        if ((u & 1) == 0) low[lowRow + (u >> 1)] = current[at];
                        else high[highRow + (u >> 1)] = current[at];
                    }
                }
                result[levels - lev + 1] = new[] { hlData, lhData, hhData };
                current = llData;
                cur = ll;
            }
            result[0] = new[] { current };
            return result;
        }
    }
}
