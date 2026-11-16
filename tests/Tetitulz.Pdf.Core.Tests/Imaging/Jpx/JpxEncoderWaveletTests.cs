// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxForwardWavelet"/> against the <b>inverse</b> procedures of T.800 F.3, written
/// here from equations F-5, F-6 (5-3) and F-7 (9-7) — a different procedure, so a misreading of
/// the forward equations cannot cancel out — and against values worked by hand from F-9 and F-10.
/// </summary>
public sealed class JpxEncoderWaveletTests
{
    private const int Extension = 4;

    /// <summary>F.3.7: X(i0 − k) = X(i0 + k), X(i1 − 1 + k) = X(i1 − 1 − k).</summary>
    private static T[] Extend<T>(T[] y, int n)
    {
        var e = new T[n + 2 * Extension];
        int period = 2 * (n - 1);
        for (int j = 0; j < e.Length; j++)
        {
            int k = ((j - Extension) % period + period) % period;
            if (k >= n) k = period - k;
            e[j] = y[k];
        }
        return e;
    }

    /// <summary>1D_SR with 1D_FILTR_5-3R: F-5 at the even positions, then F-6 at the odd ones.</summary>
    private static int[] Inverse53(int[] y, int i0, int i1)
    {
        int n = i1 - i0;
        if (n == 1) return new[] { (i0 & 1) == 0 ? y[0] : y[0] / 2 };
        int[] e = Extend(y, n);
        int first = i0 - Extension;
        for (int p = 2 * (int)Math.Floor(i0 / 2.0); p <= 2 * (int)Math.Floor(i1 / 2.0); p += 2)   // ⌊i0/2⌋ ≤ n < ⌊i1/2⌋ + 1
            e[p - first] -= (int)Math.Floor((e[p - 1 - first] + e[p + 1 - first] + 2) / 4.0);
        for (int p = 2 * (int)Math.Floor(i0 / 2.0) + 1; p < 2 * (int)Math.Floor(i1 / 2.0); p += 2)   // ⌊i0/2⌋ ≤ n < ⌊i1/2⌋
            e[p - first] += (int)Math.Floor((e[p - 1 - first] + e[p + 1 - first]) / 2.0);
        return e.Skip(Extension).Take(n).ToArray();
    }

    /// <summary>1D_SR with 1D_FILTR_9-7I: the six steps of F-7 in double precision.</summary>
    private static double[] Inverse97(float[] y, int i0, int i1)
    {
        int n = i1 - i0;
        if (n == 1) return new[] { (i0 & 1) == 0 ? y[0] : y[0] / 2.0 };
        double[] e = Extend(Array.ConvertAll(y, v => (double)v), n);
        int first = i0 - Extension;
        int lo = (int)Math.Floor(i0 / 2.0), hi = (int)Math.Floor(i1 / 2.0);
        const double k = 1.230174104914001, a = -1.586134342059924, b = -0.052980118572961, g = 0.882911075530934, d = 0.443506852043971;
        for (int m = lo - 1; m < hi + 2; m++) e[2 * m - first] *= k;                                            // STEP1
        for (int m = lo - 2; m < hi + 2; m++) e[2 * m + 1 - first] *= 1 / k;                                    // STEP2
        for (int m = lo - 1; m < hi + 2; m++) e[2 * m - first] -= d * (e[2 * m - 1 - first] + e[2 * m + 1 - first]);        // STEP3
        for (int m = lo - 1; m < hi + 1; m++) e[2 * m + 1 - first] -= g * (e[2 * m - first] + e[2 * m + 2 - first]);        // STEP4
        for (int m = lo; m < hi + 1; m++) e[2 * m - first] -= b * (e[2 * m - 1 - first] + e[2 * m + 1 - first]);            // STEP5
        for (int m = lo; m < hi; m++) e[2 * m + 1 - first] -= a * (e[2 * m - first] + e[2 * m + 2 - first]);                // STEP6
        return e.Skip(Extension).Take(n).ToArray();
    }

    /// <summary>The 2D_SR of F.3.2 for one level: 2D_INTERLEAVE (Figure F.8), HOR_SR on every row, VER_SR on every column.</summary>
    private static int[] Inverse2D(int[] ll, int[] hl, int[] lh, int[] hh, JpxRect bounds)
    {
        int u0 = (int)bounds.X0, v0 = (int)bounds.Y0, w = (int)bounds.Width, h = (int)bounds.Height;
        JpxRect llR = JpxGeometry.Subband(bounds, 1, 0, 0), hlR = JpxGeometry.Subband(bounds, 1, 1, 0);
        JpxRect lhR = JpxGeometry.Subband(bounds, 1, 0, 1), hhR = JpxGeometry.Subband(bounds, 1, 1, 1);
        var a = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int u = u0 + x, v = v0 + y;
                bool oddU = (u & 1) == 1, oddV = (v & 1) == 1;
                JpxRect band = oddU ? (oddV ? hhR : hlR) : (oddV ? lhR : llR);
                int[] data = oddU ? (oddV ? hh : hl) : (oddV ? lh : ll);
                a[y * w + x] = data[(int)((v >> 1) - band.Y0) * (int)band.Width + (int)((u >> 1) - band.X0)];
            }
        for (int y = 0; y < h; y++)
        {
            int[] row = Inverse53(a.Skip(y * w).Take(w).ToArray(), u0, u0 + w);
            Array.Copy(row, 0, a, y * w, w);
        }
        for (int x = 0; x < w; x++)
        {
            var column = new int[h];
            for (int y = 0; y < h; y++) column[y] = a[y * w + x];
            int[] back = Inverse53(column, v0, v0 + h);
            for (int y = 0; y < h; y++) a[y * w + x] = back[y];
        }
        return a;
    }

    /// <summary>
    /// F-9 and F-10 by hand. X = 4, 7, 1, 9 from i0 = 0: Xext(−1) = 7, Xext(4) = 1; the odd outputs
    /// 7 − ⌊(4 + 1)/2⌋ = 5 and 9 − ⌊(1 + 1)/2⌋ = 8, with Y(−1) = 7 − ⌊(1 + 4)/2⌋ = 5 off the left end;
    /// the even ones 4 + ⌊(5 + 5 + 2)/4⌋ = 7 and 1 + ⌊(5 + 8 + 2)/4⌋ = 4. From i0 = 1 the same
    /// three samples 7, 1, 9 extend as Xext(0) = 1: Y(1) = 7 − ⌊(1 + 1)/2⌋ = 6, Y(3) = 8, Y(2) = 1 + ⌊(6 + 8 + 2)/4⌋ = 5.
    /// A ramp has no high-pass at all.
    /// </summary>
    [Fact]
    public void Reversible1D_MatchesTheHandWorkedEquations()
    {
        int[] x = { 4, 7, 1, 9 };
        JpxForwardWavelet.Filter53(x, 0, 4);
        Assert.Equal(new[] { 7, 5, 4, 8 }, x);

        int[] odd = { 7, 1, 9 };
        JpxForwardWavelet.Filter53(odd, 1, 4);
        Assert.Equal(new[] { 6, 5, 8 }, odd);

        int[] ramp = { 1, 2, 3, 4, 5 };
        JpxForwardWavelet.Filter53(ramp, 0, 5);
        Assert.Equal(new[] { 1, 0, 3, 0, 5 }, ramp);

        // F.4.6: a signal of one sample is kept at an even index and doubled at an odd one.
        int[] even = { 9 }, oddOne = { 9 };
        JpxForwardWavelet.Filter53(even, 4, 5);
        JpxForwardWavelet.Filter53(oddOne, 5, 6);
        Assert.Equal((9, 18), (even[0], oddOne[0]));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 9)]
    [InlineData(1, 3)]
    [InlineData(3, 12)]
    [InlineData(6, 7)]
    [InlineData(7, 8)]
    [InlineData(5, 40)]
    public void Reversible1D_IsUndoneExactlyByTheInverseOfF5AndF6(int i0, int i1)
    {
        var random = new Random(i0 * 100 + i1);
        int[] x = new int[i1 - i0];
        for (int i = 0; i < x.Length; i++) x[i] = random.Next(-300, 300);
        int[] y = (int[])x.Clone();
        JpxForwardWavelet.Filter53(y, i0, i1);
        Assert.Equal(x, Inverse53(y, i0, i1));
        if (x.Length > 1) Assert.NotEqual(x, y);   // potency: the transform did something the inverse had to undo
    }

    /// <summary>Two levels on a tile-component away from the origin, so odd starts reach the deinterleave (Figure F.28) and the band extents of B-15.</summary>
    [Fact]
    public void Reversible2D_TwoLevels_IsUndoneExactly_AndBandsSpanTheGeometry()
    {
        var bounds = new JpxRect(3, 5, 16, 14);
        var random = new Random(7);
        int[] samples = new int[bounds.Width * bounds.Height];
        for (int i = 0; i < samples.Length; i++) samples[i] = random.Next(-128, 128);

        int[][][] bands = JpxForwardWavelet.Reversible((int[])samples.Clone(), bounds, 2);
        Assert.Equal(3, bands.Length);
        Assert.Single(bands[0]);
        Assert.Equal(3, bands[1].Length);
        JpxRect ll2 = JpxGeometry.Subband(bounds, 2, 0, 0), hh1 = JpxGeometry.Subband(bounds, 1, 1, 1);
        Assert.Equal(ll2.Width * ll2.Height, bands[0][0].Length);
        Assert.Equal(hh1.Width * hh1.Height, bands[2][2].Length);

        JpxRect ll1 = JpxGeometry.Subband(bounds, 1, 0, 0);
        int[] level1 = Inverse2D(bands[0][0], bands[1][0], bands[1][1], bands[1][2], ll1);
        int[] back = Inverse2D(level1, bands[2][0], bands[2][1], bands[2][2], bounds);
        Assert.Equal(samples, back);
    }

    /// <summary>
    /// The 9-7 filter's normalisation (Table F.4's K in STEP5 and STEP6): a constant signal comes
    /// out as the constant in the low-pass and zero in the high-pass everywhere; a ramp likewise
    /// keeps its even samples away from the ends — the low-pass has unit DC gain and the high-pass
    /// vanishing moments, while the symmetric extension bends a ramp within four samples of an end.
    /// </summary>
    [Fact]
    public void Irreversible1D_KeepsAConstantAndARamp_InTheLowPass()
    {
        float[] constant = { 10, 10, 10, 10, 10, 10, 10, 10 };
        JpxForwardWavelet.Filter97(constant, 0, 8);
        for (int i = 0; i < 8; i++) Assert.Equal((i & 1) == 0 ? 10 : 0, constant[i], 3);

        var ramp = new float[17];
        for (int i = 0; i < 17; i++) ramp[i] = 3 * i;
        JpxForwardWavelet.Filter97(ramp, 0, 17);
        for (int i = 4; i <= 12; i++) Assert.Equal((i & 1) == 0 ? 3 * i : 0, ramp[i], 2);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(0, 16)]
    [InlineData(1, 6)]
    [InlineData(3, 12)]
    [InlineData(5, 40)]
    public void Irreversible1D_IsUndoneByTheInverseOfF7(int i0, int i1)
    {
        var random = new Random(i0 * 7 + i1);
        float[] x = new float[i1 - i0];
        for (int i = 0; i < x.Length; i++) x[i] = random.Next(-128, 128);
        float[] y = (float[])x.Clone();
        JpxForwardWavelet.Filter97(y, i0, i1);
        double[] back = Inverse97(y, i0, i1);
        for (int i = 0; i < x.Length; i++) Assert.Equal(x[i], back[i], 2);
        double moved = 0;
        for (int i = 0; i < x.Length; i++) moved += Math.Abs(x[i] - y[i]);
        Assert.True(moved > 1, "the transform changed nothing for the inverse to undo");
    }

    // ---- F.4 as written: the extended signal built, every lifting step over the range F.4.8 gives it ----

    /// <summary>1D_SD with 1D_FILTR_5-3R over the extended signal (F.4.7, F-9, F-10), the procedure as printed.</summary>
    private static void Forward53AsPrinted(long[] x, int i0, int i1)
    {
        int n = i1 - i0;
        if (n == 1) { if ((i0 & 1) == 1) x[0] *= 2; return; }
        long[] e = Extend(x.Take(n).ToArray(), n);
        int first = i0 - Extension;
        for (int p = i0 - 3; p < i1 + 3; p++) if ((p & 1) == 1) e[p - first] -= (e[p - 1 - first] + e[p + 1 - first]) >> 1;   // F-9
        for (int p = i0; p < i1; p++) if ((p & 1) == 0) e[p - first] += (e[p - 1 - first] + e[p + 1 - first] + 2) >> 2;        // F-10
        Array.Copy(e, Extension, x, 0, n);
    }

    /// <summary>1D_SD with 1D_FILTR_9-7I over the extended signal (F-11), each step over the range that feeds the next.</summary>
    private static void Forward97AsPrinted(double[] x, int i0, int i1)
    {
        int n = i1 - i0;
        if (n == 1) { if ((i0 & 1) == 1) x[0] *= 2; return; }
        double[] e = Extend(x.Take(n).ToArray(), n);
        int first = i0 - Extension;
        void Step(int from, int to, int parity, double factor)
        {
            for (int p = from; p < to; p++) if ((p & 1) == parity) e[p - first] += factor * (e[p - 1 - first] + e[p + 1 - first]);
        }
        Step(i0 - 3, i1 + 3, 1, JpxForwardWavelet.Alpha);
        Step(i0 - 2, i1 + 2, 0, JpxForwardWavelet.Beta);
        Step(i0 - 1, i1 + 1, 1, JpxForwardWavelet.Gamma);
        Step(i0, i1, 0, JpxForwardWavelet.Delta);
        for (int p = i0; p < i1; p++) if ((p & 1) == 1) e[p - first] *= JpxForwardWavelet.K;
        for (int p = i0; p < i1; p++) if ((p & 1) == 0) e[p - first] *= 1 / JpxForwardWavelet.K;
        Array.Copy(e, Extension, x, 0, n);
    }

    /// <summary>2D_SD as printed: each column gathered and filtered, then each row, then 2D_DEINTERLEAVE sample by sample.</summary>
    private static T[][][] DecomposeAsPrinted<T>(T[] samples, JpxRect bounds, int levels, Action<T[], int, int> filter)
    {
        var result = new T[levels + 1][][];
        T[] current = (T[])samples.Clone();
        JpxRect cur = bounds;
        for (int lev = 1; lev <= levels; lev++)
        {
            int w = (int)cur.Width, h = (int)cur.Height, u0 = (int)cur.X0, v0 = (int)cur.Y0;
            if (w > 0 && h > 0)
            {
                var column = new T[h];
                for (int x = 0; x < w; x++)
                {
                    for (int y = 0; y < h; y++) column[y] = current[y * w + x];
                    filter(column, v0, v0 + h);
                    for (int y = 0; y < h; y++) current[y * w + x] = column[y];
                }
                var row = new T[w];
                for (int y = 0; y < h; y++)
                {
                    Array.Copy(current, y * w, row, 0, w);
                    filter(row, u0, u0 + w);
                    Array.Copy(row, 0, current, y * w, w);
                }
            }
            var bands = new JpxRect[4];
            var data = new T[4][];
            for (int o = 0; o < 4; o++)
            {
                bands[o] = JpxGeometry.Subband(bounds, lev, o & 1, o >> 1);
                data[o] = new T[bands[o].Width * bands[o].Height];
            }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int u = u0 + x, v = v0 + y, o = (u & 1) | ((v & 1) << 1);
                    data[o][(int)((v >> 1) - bands[o].Y0) * (int)bands[o].Width + (int)((u >> 1) - bands[o].X0)] = current[y * w + x];
                }
            result[levels - lev + 1] = new[] { data[1], data[2], data[3] };
            current = data[0];
            cur = bands[0];
        }
        result[0] = new[] { current };
        return result;
    }

    public static IEnumerable<object[]> Shapes()
    {
        // Every parity of start, lengths of one, two and three (where the mirror wraps), and odd and even sizes.
        foreach ((int x0, int y0, int w, int h) in new[] { (0, 0, 1, 1), (1, 0, 2, 1), (0, 1, 1, 2), (3, 5, 2, 2), (0, 0, 3, 3), (1, 1, 3, 2),
                     (2, 7, 5, 9), (7, 2, 16, 14), (1, 3, 33, 17), (0, 0, 40, 24), (5, 6, 64, 3) })
            foreach (int levels in new[] { 1, 3 })
                yield return new object[] { x0, y0, w, h, levels };
    }

    /// <summary>
    /// The forward transform reads F.4.7's extension from the mirror rather than building it, and lifts the columns a
    /// row at a time: it is F.4 as printed, bit for bit — the 5-3's integers and the 9-7's doubles alike — on every
    /// parity of start and every short signal, where the mirror wraps.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheTransform_IsF4AsPrinted_BitForBit(int x0, int y0, int w, int h, int levels)
    {
        var bounds = new JpxRect(x0, y0, x0 + w, y0 + h);
        var random = new Random(x0 * 1000 + y0 * 100 + w * 10 + h + levels);
        long[] integers = new long[w * h];
        for (int i = 0; i < integers.Length; i++) integers[i] = random.Next(-512, 512);
        double[] reals = integers.Select(v => v + random.NextDouble()).ToArray();

        long[][][] expected53 = DecomposeAsPrinted(integers, bounds, levels, Forward53AsPrinted);
        long[][][] actual53 = JpxForwardWavelet.Reversible(integers, bounds, levels);
        double[][][] expected97 = DecomposeAsPrinted(reals, bounds, levels, Forward97AsPrinted);
        double[][][] actual97 = JpxForwardWavelet.Irreversible(reals, bounds, levels);
        for (int r = 0; r <= levels; r++)
            for (int b = 0; b < expected53[r].Length; b++)
            {
                Assert.Equal(expected53[r][b], actual53[r][b]);
                Assert.Equal(expected97[r][b].Select(BitConverter.DoubleToInt64Bits), actual97[r][b].Select(BitConverter.DoubleToInt64Bits));
            }
    }

    [Fact]
    public void Decompose_RefusesSamplesThatDoNotFillTheBounds()
    {
        Assert.Throws<ArgumentException>(() => JpxForwardWavelet.Reversible(new int[5], new JpxRect(0, 0, 2, 3), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxForwardWavelet.Reversible(new int[6], new JpxRect(0, 0, 2, 3), -1));
        Assert.Single(JpxForwardWavelet.Reversible(new int[6], new JpxRect(0, 0, 2, 3), 0)[0]);   // the control: no level is the samples themselves
    }
}
