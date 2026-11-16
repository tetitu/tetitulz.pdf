// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The coefficients a<sub>b</sub>(u<sub>b</sub>, v<sub>b</sub>) of one sub-band of a tile-component
    /// (T.800 §F.3.1, Table F.1): the decomposition level n<sub>b</sub> and the orientation that name
    /// the band, its bounds in the band's own coordinates (equation B-15 — the tbx0, tby0, tbx1, tby1
    /// of equation F-2) and the values in raster order over those bounds. The inverse transform
    /// returns the same type: sub-band 0LL is the tile-component's samples (§F.3.1), and rLL the
    /// samples at resolution level N<sub>L</sub> − r.
    /// <para>
    /// Values are <c>double</c> for both filters — one storage type, the lifting's own — so the
    /// reversible 5-3 path is bit-exact for every integer coefficient below 2^53 in magnitude: the
    /// 38-bit precisions SIZ can declare, with Table E.1's gains and the 1.5-per-level growth of the
    /// low-pass filter's worst case, stay far inside it. (As <c>float</c>, exactness would end near
    /// 2^24 — about 20 bits of precision — and nothing would report the loss.)
    /// </para>
    /// <para>
    /// A band may instead hold tier 1's values still <see cref="Quantized"/>, with the step and the
    /// filter that reconstruct them (E-6, or E-7/E-8): the transform then dequantises each value as
    /// it interleaves it (<c>JpxDequantizer.ReconstructStrided</c>, the one rule), and no
    /// <c>double</c> copy of the band is ever made.
    /// </para>
    /// </summary>
    internal sealed class JpxSubbandCoefficients
    {
        internal JpxSubbandCoefficients(int level, int orientation, JpxRect bounds, double[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            Require(level, orientation, bounds, values.Length, nameof(values));
            Level = level;
            Orientation = orientation;
            Bounds = bounds;
            Values = values;
        }

        /// <summary>A band of tier 1's values, dequantised on the way into the transform.</summary>
        internal JpxSubbandCoefficients(int level, int orientation, JpxRect bounds, int[] quantized, bool reversible, double stepSize)
        {
            if (quantized == null) throw new ArgumentNullException(nameof(quantized));
            Require(level, orientation, bounds, quantized.Length, nameof(quantized));
            Level = level;
            Orientation = orientation;
            Bounds = bounds;
            Quantized = quantized;
            Reversible = reversible;
            StepSize = stepSize;
        }

        /// <summary>A band of tier 1's values from a wide band (<see cref="JpxBandCoefficients.WideValues"/>), dequantised on the way in likewise.</summary>
        internal JpxSubbandCoefficients(int level, int orientation, JpxRect bounds, long[] quantized, bool reversible, double stepSize)
        {
            if (quantized == null) throw new ArgumentNullException(nameof(quantized));
            Require(level, orientation, bounds, quantized.Length, nameof(quantized));
            Level = level;
            Orientation = orientation;
            Bounds = bounds;
            QuantizedWide = quantized;
            Reversible = reversible;
            StepSize = stepSize;
        }

        private static void Require(int level, int orientation, JpxRect bounds, int length, string parameter)
        {
            long expected = bounds.Width <= 0 || bounds.Height <= 0 ? 0 : bounds.Width * bounds.Height;
            if (length != expected)
                throw new ArgumentException($"{Names[orientation & 3]} band at level {level} spans {bounds}, {expected} values; {length} were given", parameter);
        }

        internal static readonly string[] Names = { "LL", "HL", "LH", "HH" };

        /// <summary>n<sub>b</sub> (Table F.1): N<sub>L</sub> for the LL band, 1 to N<sub>L</sub> for the others.</summary>
        internal int Level { get; }

        /// <summary>0 LL, 1 HL, 2 LH, 3 HH — <see cref="JpxBand.Orientation"/>'s numbering.</summary>
        internal int Orientation { get; }

        /// <summary>In the band's coordinates (equation B-15).</summary>
        internal JpxRect Bounds { get; }

        /// <summary>Raster order over <see cref="Bounds"/>: <c>Values[(v − Y0) · Width + (u − X0)]</c>; null for a band still <see cref="Quantized"/>.</summary>
        internal double[] Values { get; }

        /// <summary>Tier 1's values in the same order, when the band was made from them; null otherwise (or when they are <see cref="QuantizedWide"/>).</summary>
        internal int[] Quantized { get; }

        /// <summary>Tier 1's values from a wide band; null otherwise.</summary>
        internal long[] QuantizedWide { get; }

        /// <summary>For a <see cref="Quantized"/> band: whether E-7/E-8 (true) or E-6 with <see cref="StepSize"/> reconstructs a value.</summary>
        internal bool Reversible { get; }

        /// <summary>Δ<sub>b</sub> for a <see cref="Quantized"/> irreversible band.</summary>
        internal double StepSize { get; }

        /// <summary>A band with no coefficients; the transform treats it as absent, that is all zero.</summary>
        internal bool IsEmpty => Bounds.Width <= 0 || Bounds.Height <= 0;

        /// <summary>The band's reconstructed values: <see cref="Values"/>, or the <see cref="Quantized"/> ones dequantised into a new array.</summary>
        internal double[] Reconstructed()
        {
            if (Values != null) return Values;
            if (QuantizedWide != null)
            {
                var wide = new double[QuantizedWide.Length];
                JpxDequantizer.Reconstruct(QuantizedWide, wide, Reversible, StepSize);
                return wide;
            }
            var values = new double[Quantized.Length];
            JpxDequantizer.Reconstruct(Quantized, values, Reversible, StepSize);
            return values;
        }
    }

    /// <summary>
    /// The inverse discrete wavelet transformation of T.800 Annex F. The IDWT procedure (§F.3.1)
    /// runs the 2D_SR procedure (§F.3.2) once per decomposition level from N<sub>L</sub> down: the
    /// level's four sub-bands are interleaved (2D_INTERLEAVE, §F.3.3, Figure F.8), then every row
    /// (HOR_SR, §F.3.4) and every column (VER_SR, §F.3.5) goes through the 1D_SR procedure (§F.3.6)
    /// — periodic symmetric extension (1D_EXTR, §F.3.7, equations F-3 and F-4, Tables F.2 and F.3)
    /// and lifting (1D_FILTR, §F.3.8: the reversible 5-3 filter of equations F-5 and F-6, or the
    /// irreversible 9-7 filter of equation F-7 with Table F.4). Every parity is that of the absolute
    /// coordinate, never of an array index: a signal starting at an odd u0 has its first sample on a
    /// high-pass position, and a signal of one sample at an odd coordinate is halved (§F.3.6).
    /// <para>
    /// Nothing here recurses, and every array is sized from a rectangle of the tile-component; the
    /// tile-component's own area is checked against <see cref="PdfLimits.MaxImagePixelArea"/> before
    /// the first allocation. Two arrays serve every level: the result, and one of a quarter of it
    /// for the levels in between, which alternate — a level reads the array the level above wrote
    /// and writes the one it read from two levels up, already consumed.
    /// </para>
    /// </summary>
    internal static class JpxWavelet
    {
        /// <summary>Table A.15: 0 to 32 decomposition levels.</summary>
        internal const int MaxLevels = 32;

        // Table F.4 — the lifting parameters of the 9-7 irreversible filter.
        private const double Alpha = -1.586134342059924;
        private const double Beta = -0.052980118572961;
        private const double Gamma = 0.882911075530934;
        private const double Delta = 0.443506852043971;
        private const double K = 1.230174104914001;

        /// <summary>
        /// The reversible 5-3 inverse (1D_FILTR5-3R, §F.3.8.1). <paramref name="bands"/> holds the
        /// N<sub>L</sub>LL band and the HL, LH and HH bands of levels 1 to N<sub>L</sub>; an absent or
        /// empty band is zero. With <paramref name="reduction"/> r the last r levels are not
        /// reconstructed and the result is the rLL band — the samples at resolution level
        /// N<sub>L</sub> − r, over that level's rectangle (equation B-14). With no level to
        /// reconstruct the LL band's own array is returned as it is (dequantised, if it was not).
        /// </summary>
        internal static JpxSubbandCoefficients Inverse53(JpxRect tileComponent, int levels, IReadOnlyList<JpxSubbandCoefficients> bands, int reduction = 0) =>
            Inverse(tileComponent, levels, bands, reduction, reversible: true);

        /// <summary>The irreversible 9-7 inverse (1D_FILTR9-7I, §F.3.8.2); otherwise as <see cref="Inverse53"/>.</summary>
        internal static JpxSubbandCoefficients Inverse97(JpxRect tileComponent, int levels, IReadOnlyList<JpxSubbandCoefficients> bands, int reduction = 0) =>
            Inverse(tileComponent, levels, bands, reduction, reversible: false);

        internal static JpxSubbandCoefficients Inverse(JpxRect tileComponent, int levels, IReadOnlyList<JpxSubbandCoefficients> bands, int reduction, bool reversible) =>
            Inverse(tileComponent, levels, bands, reduction, reversible, JpxParallel.Sequential);

        /// <summary>
        /// As <see cref="Inverse53"/> / <see cref="Inverse97"/>, with every level's rows (HOR_SR) and
        /// then its columns (VER_SR) shared out across <paramref name="parallel"/>'s workers in
        /// contiguous ranges — a level too small to repay a worker's start is lifted on the calling
        /// thread. A row's or column's arithmetic is the same whichever worker runs it, so the result
        /// is bit-identical to the sequential one.
        /// </summary>
        internal static JpxSubbandCoefficients Inverse(JpxRect tileComponent, int levels, IReadOnlyList<JpxSubbandCoefficients> bands, int reduction, bool reversible,
            JpxParallel parallel)
        {
            if (parallel == null) throw new ArgumentNullException(nameof(parallel));
            if (levels < 0 || levels > MaxLevels) throw new ArgumentOutOfRangeException(nameof(levels), levels, "T.800 Table A.15 allows 0 to 32 decomposition levels");
            if (reduction < 0 || reduction > levels) throw new ArgumentOutOfRangeException(nameof(reduction), reduction, "a reduction is 0 to the number of decomposition levels");
            if (bands == null) throw new ArgumentNullException(nameof(bands));

            long width = tileComponent.Width, height = tileComponent.Height;
            if (width > 0 && height > 0)
            {
                long limit = Math.Min(PdfLimits.MaxImagePixelArea, int.MaxValue);
                if (width > limit / height)
                    throw new ArgumentException($"the tile-component covers {width}x{height} samples; PdfLimits.MaxImagePixelArea is {PdfLimits.MaxImagePixelArea}", nameof(tileComponent));
            }

            var byBand = new JpxSubbandCoefficients[levels + 1, 4];
            foreach (JpxSubbandCoefficients band in bands)
            {
                if (band == null) throw new ArgumentException("a null band", nameof(bands));
                if (band.IsEmpty) continue;
                int level = band.Level, orientation = band.Orientation;
                if (orientation < 0 || orientation > 3 || (orientation == 0 ? level != levels : level < 1 || level > levels))
                    throw new ArgumentException($"no band {JpxSubbandCoefficients.Names[orientation & 3]} at level {level} in a {levels}-level decomposition (T.800 Table F.1)", nameof(bands));
                if (byBand[level, orientation] != null)
                    throw new ArgumentException($"band {JpxSubbandCoefficients.Names[orientation]} at level {level} was given twice", nameof(bands));
                JpxRect expected = JpxGeometry.Subband(tileComponent, level, orientation & 1, orientation >> 1);
                if (!SameRect(expected, band.Bounds))
                    throw new ArgumentException($"band {JpxSubbandCoefficients.Names[orientation]} at level {level} spans {band.Bounds}; equation B-15 gives {expected}", nameof(bands));
                byBand[level, orientation] = band;
            }

            JpxRect current = JpxGeometry.Subband(tileComponent, levels, 0, 0);
            JpxSubbandCoefficients ll = byBand[levels, 0];
            if (levels == reduction)
                return new JpxSubbandCoefficients(reduction, 0, current, ll != null ? ll.Reconstructed() : new double[Area(current)]);

            // The IDWT procedure (Figure F.3): lev runs from N_L down, each 2D_SR turning levLL and
            // the level's three high-pass bands into (lev − 1)LL; r levels are left when reducing.
            // The last level writes the result; the ones before alternate between it and a scratch
            // array of the largest intermediate rectangle, (reduction + 1)LL.
            JpxRect last = JpxGeometry.Subband(tileComponent, reduction, 0, 0);
            var result = new double[Area(last)];
            double[] scratch = levels - reduction >= 2 ? new double[Area(JpxGeometry.Subband(tileComponent, reduction + 1, 0, 0))] : null;
            double[] a = null;
            for (int lev = levels; lev > reduction; lev--)
            {
                JpxRect next = JpxGeometry.Subband(tileComponent, lev - 1, 0, 0);
                double[] target = (lev - reduction) % 2 == 1 ? result : scratch;
                Reconstruct(target, next, tileComponent, lev, a, current, ll, byBand[lev, 1], byBand[lev, 2], byBand[lev, 3], reversible, parallel);
                a = target;
                ll = null;
                current = next;
            }
            return new JpxSubbandCoefficients(reduction, 0, current, result);
        }

        private static bool SameRect(JpxRect a, JpxRect b) => a.X0 == b.X0 && a.Y0 == b.Y0 && a.X1 == b.X1 && a.Y1 == b.Y1;

        private static int Area(JpxRect r) => r.Width <= 0 || r.Height <= 0 ? 0 : (int)(r.Width * r.Height);

        /// <summary>
        /// The 2D_SR procedure (§F.3.2, Figure F.6): a(u, v) over the (lev − 1)LL rectangle
        /// <paramref name="rect"/> from the four sub-bands of level <paramref name="lev"/>, then HOR_SR
        /// and VER_SR. The LL band is either the array the level above wrote (<paramref name="llValues"/>
        /// over <paramref name="llRect"/>) or the given band <paramref name="llBand"/>.
        /// </summary>
        private static void Reconstruct(double[] a, JpxRect rect, JpxRect tileComponent, int lev, double[] llValues, JpxRect llRect, JpxSubbandCoefficients llBand,
            JpxSubbandCoefficients hl, JpxSubbandCoefficients lh, JpxSubbandCoefficients hh, bool reversible, JpxParallel parallel)
        {
            int w = (int)rect.Width, h = (int)rect.Height;
            if (w <= 0 || h <= 0) return;

            // 2D_INTERLEAVE (Figure F.8): levLL at (2u_b, 2v_b), levHL at (2u_b + 1, 2v_b),
            // levLH at (2u_b, 2v_b + 1), levHH at (2u_b + 1, 2v_b + 1), each over its own B-15 range.
            if (llValues != null) Interleave(a, rect, llValues, llRect, 0, 0);
            else Interleave(a, rect, llBand, tileComponent, lev, 0, 0);
            Interleave(a, rect, hl, tileComponent, lev, 1, 0);
            Interleave(a, rect, lh, tileComponent, lev, 0, 1);
            Interleave(a, rect, hh, tileComponent, lev, 1, 1);

            // A worker is a thread started for the loop: a level too small to repay one is lifted on the
            // calling thread.
            int ranges = (int)Math.Max(1, Math.Min(parallel.Degree, (long)w * h / SamplesPerRange));

            // HOR_SR (§F.3.4, Figure F.10): every row v, with i0 = u0 and i1 = u1 — the rows in
            // contiguous ranges, one per worker.
            int rowRanges = Math.Min(ranges, h);
            parallel.For(rowRanges, (range, _) =>
            {
                int from = (int)((long)h * range / rowRanges), to = (int)((long)h * (range + 1) / rowRanges);
                for (int v = from; v < to; v++) SynthesizeRow(a, v * w, w, rect.X0, reversible);
            });

            // VER_SR (§F.3.5, Figure F.12): every column u, with i0 = v0 and i1 = v1, lifted a row at a
            // time — the columns in contiguous ranges of eight-column groups, a cache line of doubles, so
            // workers meet only at a range's edges.
            int groups = (w + ColumnGroup - 1) / ColumnGroup;
            int columnRanges = Math.Min(ranges, groups);
            parallel.For(columnRanges, (range, _) =>
            {
                int from = (int)((long)groups * range / columnRanges) * ColumnGroup;
                int to = Math.Min(w, (int)((long)groups * (range + 1) / columnRanges) * ColumnGroup);
                SynthesizeColumns(a, w, h, rect.Y0, from, to, reversible);
            });
        }

        /// <summary>The samples a worker of HOR_SR or VER_SR is worth starting for: a few hundred microseconds of lifting.</summary>
        private const long SamplesPerRange = 1L << 17;

        /// <summary>Columns per cache line of <c>double</c>s: VER_SR's ranges are whole multiples of it.</summary>
        private const int ColumnGroup = 8;

        /// <summary>
        /// One sub-band into a(u, v) at (2u_b + xob, 2v_b + yob) — one quadrant of Figure F.8 — from
        /// the band's values, dequantising them if they are tier 1's; an absent band writes zeros
        /// over its quadrant, since the array may hold an earlier level.
        /// </summary>
        private static void Interleave(double[] a, JpxRect rect, JpxSubbandCoefficients band, JpxRect tileComponent, int lev, int xob, int yob)
        {
            if (band == null)
            {
                JpxRect absent = JpxGeometry.Subband(tileComponent, lev, xob, yob);
                if (absent.Width <= 0 || absent.Height <= 0) return;
                int w = (int)rect.Width, bw = (int)absent.Width, bh = (int)absent.Height;
                int u = (int)(2 * absent.X0 + xob - rect.X0);
                for (int vb = 0; vb < bh; vb++)
                {
                    int row = (int)(2 * (absent.Y0 + vb) + yob - rect.Y0) * w + u;
                    for (int ub = 0; ub < bw; ub++) a[row + 2 * ub] = 0;
                }
                return;
            }
            if (band.Values != null)
            {
                Interleave(a, rect, band.Values, band.Bounds, xob, yob);
                return;
            }
            JpxRect bandRect = band.Bounds;
            if (bandRect.Width <= 0 || bandRect.Height <= 0) return;
            int width = (int)rect.Width;
            int bandWidth = (int)bandRect.Width, bandHeight = (int)bandRect.Height;
            int u0 = (int)(2 * bandRect.X0 + xob - rect.X0);
            for (int vb = 0; vb < bandHeight; vb++)
            {
                int row = (int)(2 * (bandRect.Y0 + vb) + yob - rect.Y0) * width + u0;
                if (band.QuantizedWide != null)
                    JpxDequantizer.ReconstructStrided(band.QuantizedWide, vb * bandWidth, a, row, bandWidth, 2, band.Reversible, band.StepSize);
                else
                    JpxDequantizer.ReconstructStrided(band.Quantized, vb * bandWidth, a, row, bandWidth, 2, band.Reversible, band.StepSize);
            }
        }

        /// <summary>One sub-band's reconstructed values into a(u, v) at (2u_b + xob, 2v_b + yob).</summary>
        private static void Interleave(double[] a, JpxRect rect, double[] band, JpxRect bandRect, int xob, int yob)
        {
            if (bandRect.Width <= 0 || bandRect.Height <= 0) return;
            int w = (int)rect.Width;
            int bw = (int)bandRect.Width, bh = (int)bandRect.Height;
            int u = (int)(2 * bandRect.X0 + xob - rect.X0);
            for (int vb = 0; vb < bh; vb++)
            {
                int row = (int)(2 * (bandRect.Y0 + vb) + yob - rect.Y0) * w + u;
                int src = vb * bw;
                for (int ub = 0; ub < bw; ub++) a[row + 2 * ub] = band[src + ub];
            }
        }

        // ---- 1D_SR (§F.3.6) with the extension read, not built ----------------------------------------
        //
        // 1D_EXTR's extension (§F.3.7) mirrors the signal about its first and last samples, and every
        // lifting step of either filter is symmetric, so after each step a position outside the signal
        // holds what its mirror inside holds. Lifting the signal's own positions, a step therefore reads a
        // neighbour one past an end from the position one inside it — PSE_O (F-4) at distance one — and
        // gets the value 1D_FILTR computes over the extended signal: the same operands in the same order,
        // bit for bit. Tables F.2 and F.3's extents and §F.3.8's wider ranges are what make the extended
        // positions available to the printed procedure; reading the mirror needs neither.

        /// <summary>1D_SR along one row: <paramref name="n"/> samples from <paramref name="s"/>, the first at position <paramref name="i0"/>.</summary>
        private static void SynthesizeRow(double[] a, int s, int n, long i0, bool reversible)
        {
            if (n == 1)
            {
                // A signal of one sample is X(i0) = Y(i0) at an even i0 and Y(i0)/2 at an odd one.
                if ((i0 & 1) != 0) a[s] /= 2;
                return;
            }
            int even = (i0 & 1) == 0 ? 0 : 1, odd = 1 - even;
            if (reversible)
            {
                // F-5: X(2n) = Y(2n) − ⌊(Y(2n − 1) + Y(2n + 1) + 2) / 4⌋.
                for (int k = even; k < n; k += 2)
                    a[s + k] -= Math.Floor((a[s + (k == 0 ? 1 : k - 1)] + a[s + (k == n - 1 ? n - 2 : k + 1)] + 2) / 4);
                // F-6: X(2n + 1) = Y(2n + 1) + ⌊(X(2n) + X(2n + 2)) / 2⌋.
                for (int k = odd; k < n; k += 2)
                    a[s + k] += Math.Floor((a[s + (k == 0 ? 1 : k - 1)] + a[s + (k == n - 1 ? n - 2 : k + 1)]) / 2);
                return;
            }
            // F-7: STEP1 X(2n) = K · Y(2n); STEP2 X(2n + 1) = (1 / K) · Y(2n + 1); then four lifting steps.
            for (int k = even; k < n; k += 2) a[s + k] *= K;
            for (int k = odd; k < n; k += 2) a[s + k] /= K;
            LiftRow(a, s, n, even, Delta);   // STEP3
            LiftRow(a, s, n, odd, Gamma);    // STEP4
            LiftRow(a, s, n, even, Beta);    // STEP5
            LiftRow(a, s, n, odd, Alpha);    // STEP6
        }

        /// <summary>One lifting step along a row: X(p) −= weight · (X(p − 1) + X(p + 1)) at every position from <paramref name="first"/> in steps of two.</summary>
        private static void LiftRow(double[] a, int s, int n, int first, double weight)
        {
            for (int k = first; k < n; k += 2)
                a[s + k] -= weight * (a[s + (k == 0 ? 1 : k - 1)] + a[s + (k == n - 1 ? n - 2 : k + 1)]);
        }

        /// <summary>
        /// 1D_SR down columns <paramref name="from"/> to <paramref name="to"/> − 1 of the <paramref name="w"/>-wide,
        /// <paramref name="h"/>-high array, whose first row is at position <paramref name="v0"/> — each step a pass
        /// over the rows of one parity, each row updated from the rows either side.
        /// </summary>
        private static void SynthesizeColumns(double[] a, int w, int h, long v0, int from, int to, bool reversible)
        {
            if (h == 1)
            {
                if ((v0 & 1) != 0) for (int x = from; x < to; x++) a[x] /= 2;
                return;
            }
            int even = (v0 & 1) == 0 ? 0 : 1, odd = 1 - even;
            if (reversible)
            {
                for (int k = even; k < h; k += 2)
                {
                    int row = k * w, above = (k == 0 ? 1 : k - 1) * w, below = (k == h - 1 ? h - 2 : k + 1) * w;
                    for (int x = from; x < to; x++) a[row + x] -= Math.Floor((a[above + x] + a[below + x] + 2) / 4);   // F-5
                }
                for (int k = odd; k < h; k += 2)
                {
                    int row = k * w, above = (k == 0 ? 1 : k - 1) * w, below = (k == h - 1 ? h - 2 : k + 1) * w;
                    for (int x = from; x < to; x++) a[row + x] += Math.Floor((a[above + x] + a[below + x]) / 2);       // F-6
                }
                return;
            }
            for (int k = even; k < h; k += 2)
                for (int x = from, row = k * w; x < to; x++) a[row + x] *= K;   // STEP1
            for (int k = odd; k < h; k += 2)
                for (int x = from, row = k * w; x < to; x++) a[row + x] /= K;   // STEP2
            LiftRows(a, w, h, from, to, even, Delta);   // STEP3
            LiftRows(a, w, h, from, to, odd, Gamma);    // STEP4
            LiftRows(a, w, h, from, to, even, Beta);    // STEP5
            LiftRows(a, w, h, from, to, odd, Alpha);    // STEP6
        }

        /// <summary>One lifting step down columns <paramref name="from"/> to <paramref name="to"/> − 1: the rows from <paramref name="first"/> in steps of two.</summary>
        private static void LiftRows(double[] a, int w, int h, int from, int to, int first, double weight)
        {
            for (int k = first; k < h; k += 2)
            {
                int row = k * w, above = (k == 0 ? 1 : k - 1) * w, below = (k == h - 1 ? h - 2 : k + 1) * w;
                for (int x = from; x < to; x++) a[row + x] -= weight * (a[above + x] + a[below + x]);
            }
        }
    }
}
