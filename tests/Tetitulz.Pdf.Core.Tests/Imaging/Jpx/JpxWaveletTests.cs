// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxWavelet"/> against T.800 Annex F. The 1D cases are worked by hand from equations
/// F-3 to F-7 with every number printed here; the forward transform of §F.4 (2D_SD, 1D_SD with
/// Tables F.8 and F.9, equations F-9 to F-11) is written out below as the round-trip oracle — it is
/// a different procedure from the inverse, and the hand-worked cases pin it too, so the two cannot
/// share a misreading. Every 1D case goes through the 2D entry point on a one-row tile-component:
/// its columns are single samples at v0 = 0, which §F.3.6 leaves untouched.
/// </summary>
public sealed class JpxWaveletTests
{
    private static JpxSubbandCoefficients Band(int level, int orientation, JpxRect tc, params double[] values) =>
        new(level, orientation, JpxGeometry.Subband(tc, level, orientation & 1, orientation >> 1), values);

    /// <summary>
    /// Equation F-5 then F-6 on Y = (8, 2, 0, 17) at i0 = 0: i_left = 1 and i_right = 2 (Tables F.2,
    /// F.3), so Y_ext(−1) = Y(1) = 2, Y_ext(4) = Y(2) = 0, Y_ext(5) = Y(1) = 2. F-5 for n = 0, 1, 2:
    /// X(0) = 8 − ⌊(2 + 2 + 2)/4⌋ = 7, X(2) = 0 − ⌊(2 + 17 + 2)/4⌋ = −5, X(4) = 0 − ⌊(17 + 2 + 2)/4⌋ = −5.
    /// F-6 for n = 0, 1: X(1) = 2 + ⌊(7 − 5)/2⌋ = 3, X(3) = 17 + ⌊(−5 − 5)/2⌋ = 12. The forward of
    /// (7, 3, −5, 12) by F-9 and F-10 is (8, 2, 0, 17) — worked in the oracle's own test below.
    /// </summary>
    [Fact]
    public void Inverse53_FourSamples_FollowsEquationsF5AndF6()
    {
        var tc = new JpxRect(0, 0, 4, 1);
        // 1LL holds Y(0), Y(2); 1HL holds Y(1), Y(3); 1LH and 1HH are empty (equation B-15).
        var bands = new[] { Band(1, 0, tc, 8, 0), Band(1, 1, tc, 2, 17) };

        JpxSubbandCoefficients x = JpxWavelet.Inverse53(tc, 1, bands);

        Assert.Equal(new double[] { 7, 3, -5, 12 }, x.Values);
        Assert.Equal((0, 0, tc), (x.Level, x.Orientation, x.Bounds));
    }

    /// <summary>
    /// Three samples at i0 = 0, i1 = 3 (both extensions 1, Y_ext(−1) = Y_ext(3) = Y(1)). The +2 of
    /// F-5: Y = (10, 1, 6) gives X(0) = 10 − ⌊(1 + 1 + 2)/4⌋ = 9 (10 without it), X(2) = 6 − 1 = 5,
    /// X(1) = 1 + ⌊(9 + 5)/2⌋ = 8. The floor of a negative: Y = (10, −2, 6) gives
    /// X(0) = 10 − ⌊(−2 − 2 + 2)/4⌋ = 10 − ⌊−0.5⌋ = 11 (10 if truncated), X(2) = 7, X(1) = −2 + ⌊18/2⌋ = 7.
    /// </summary>
    [Fact]
    public void Inverse53_ThreeSamples_RoundsTowardMinusInfinityWithThePlusTwoTerm()
    {
        var tc = new JpxRect(0, 0, 3, 1);

        JpxSubbandCoefficients plusTwo = JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 0, tc, 10, 6), Band(1, 1, tc, 1) });
        JpxSubbandCoefficients negative = JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 0, tc, 10, 6), Band(1, 1, tc, -2) });

        Assert.Equal(new double[] { 9, 8, 5 }, plusTwo.Values);
        Assert.Equal(new double[] { 11, 7, 7 }, negative.Values);
    }

    /// <summary>
    /// F-5 and F-6 at a magnitude float storage cannot hold: Y = (2^26 + 1, 2, 0, 2^26 + 17) at i0 = 0,
    /// the same shape as the four-sample case. X(0) = 67108865 − ⌊6/4⌋ = 67108864,
    /// X(2) = 0 − ⌊(2 + 67108881 + 2)/4⌋ = −16777221, X(4) = 0 − ⌊(67108881 + 2 + 2)/4⌋ = −16777221,
    /// X(1) = 2 + ⌊(67108864 − 16777221)/2⌋ = 25165823, X(3) = 67108881 + ⌊(−16777221 − 16777221)/2⌋
    /// = 50331660. Float storage holds every second integer above 2^24 and every eighth near 2^26:
    /// X(1) and X(2) are odd, so a float-stored result could not be these numbers (review of 15a–15f,
    /// decision 6).
    /// </summary>
    [Fact]
    public void Inverse53_AtTwoToTheTwentySix_IsExact()
    {
        var tc = new JpxRect(0, 0, 4, 1);
        var bands = new[] { Band(1, 0, tc, 67108865, 0), Band(1, 1, tc, 2, 67108881) };

        JpxSubbandCoefficients x = JpxWavelet.Inverse53(tc, 1, bands);

        Assert.Equal(new double[] { 67108864, 25165823, -16777221, 50331660 }, x.Values);
    }

    /// <summary>
    /// The oracle against the same hand-worked numbers, from the other side. F-9 on X = (7, 3, −5, 12)
    /// at i0 = 0 (X_ext(−2, −1) = −5, 3; X_ext(4, 5) = −5, 3): Y(−1) = 3 − ⌊(−5 + 7)/2⌋ = 2,
    /// Y(1) = 3 − ⌊(7 − 5)/2⌋ = 2, Y(3) = 12 − ⌊(−5 − 5)/2⌋ = 17; F-10: Y(0) = 7 + ⌊(2 + 2 + 2)/4⌋ = 8,
    /// Y(2) = −5 + ⌊(2 + 17 + 2)/4⌋ = 0. And at i0 = 1 on X = (21, 16, 25) (X_ext(0) = 16,
    /// X_ext(4) = 16): Y(1) = 21 − 16 = 5, Y(3) = 25 − 16 = 9, Y(2) = 16 + ⌊(5 + 9 + 2)/4⌋ = 20.
    /// </summary>
    [Fact]
    public void ForwardOracle_MatchesTheHandWorkedCases()
    {
        Assert.Equal(new double[] { 8, 2, 0, 17 }, Fdwt.Analyze(new double[] { 7, 3, -5, 12 }, 0, reversible: true));
        Assert.Equal(new double[] { 10, 1, 6 }, Fdwt.Analyze(new double[] { 9, 8, 5 }, 0, reversible: true));
        Assert.Equal(new double[] { 10, -2, 6 }, Fdwt.Analyze(new double[] { 11, 7, 7 }, 0, reversible: true));
        Assert.Equal(new double[] { 5, 20, 9 }, Fdwt.Analyze(new double[] { 21, 16, 25 }, 1, reversible: true));
    }

    /// <summary>
    /// Three samples at i0 = 1, i1 = 4: an odd origin puts Y(1) and Y(3) in the HL band and Y(2) alone
    /// in LL (B-15: 1LL = ⌈1/2⌉..⌈4/2⌉ = [1, 2), 1HL = ⌊1/2⌋..⌊4/2⌋ = [0, 2)). Extensions 2 and 2
    /// (Tables F.2, F.3, period 4): Y_ext(0) = Y(2), Y_ext(−1) = Y(3), Y_ext(4) = Y(2), Y_ext(5) = Y(1).
    /// With Y = (5, 20, 9): X(0) = X(2) = X(4) = 20 − ⌊(9 + 5 + 2)/4⌋ = 16, X(1) = 5 + 16 = 21,
    /// X(3) = 9 + 16 = 25. Parity taken from the array index instead would answer (−5, 17, −1).
    /// </summary>
    [Fact]
    public void Inverse53_OddOrigin_TakesParityFromTheCoordinate()
    {
        var tc = new JpxRect(1, 0, 4, 1);
        var bands = new[] { Band(1, 0, tc, 20), Band(1, 1, tc, 5, 9) };

        JpxSubbandCoefficients x = JpxWavelet.Inverse53(tc, 1, bands);

        Assert.Equal(new double[] { 21, 16, 25 }, x.Values);
        Assert.Equal(tc, x.Bounds);
    }

    /// <summary>
    /// §F.3.6 on a signal of one sample. A tile-component one column wide at u0 = 3: every row is the
    /// single sample Y(3), an odd coordinate, so it is halved — and it comes from the HL and HH bands,
    /// the only ones B-15 leaves non-empty. The column (v0 = 0) then goes through F-5 and F-6 on
    /// (8, 4, 12, 2): X(0) = 8 − ⌊(4 + 4 + 2)/4⌋ = 6, X(2) = 12 − ⌊(4 + 2 + 2)/4⌋ = 10,
    /// X(4) = 12 − ⌊(2 + 4 + 2)/4⌋ = 10, X(1) = 4 + 8 = 12, X(3) = 2 + 10 = 12. The control at
    /// u0 = 2 (even, from LL and LH) is not halved: (16, 8, 24, 4) gives X(0) = 16 − 4 = 12,
    /// X(2) = 24 − 3 = 21, X(4) = 24 − 3 = 21, X(1) = 8 + 16 = 24, X(3) = 4 + 21 = 25.
    /// </summary>
    [Fact]
    public void Inverse53_SingleSample_IsHalvedAtAnOddCoordinateOnly()
    {
        var odd = new JpxRect(3, 0, 4, 4);
        JpxSubbandCoefficients halved = JpxWavelet.Inverse53(odd, 1, new[] { Band(1, 1, odd, 16, 24), Band(1, 3, odd, 8, 4) });

        var even = new JpxRect(2, 0, 3, 4);
        JpxSubbandCoefficients kept = JpxWavelet.Inverse53(even, 1, new[] { Band(1, 0, even, 16, 24), Band(1, 2, even, 8, 4) });

        Assert.Equal(new double[] { 6, 12, 10, 12 }, halved.Values);
        Assert.Equal(new double[] { 12, 24, 21, 25 }, kept.Values);
    }

    /// <summary>
    /// Figure F.8: an HL coefficient sits on an odd column and an even row, an LH coefficient the
    /// other way round. One HL coefficient of 4 on a 2x2 tile-component: a(1, 0) = 4; HOR_SR on
    /// row 0 = (0, 4) gives (−2, 2) (X(0) = 0 − ⌊(4 + 4 + 2)/4⌋, X(1) = 4 + ⌊(−2 − 2)/2⌋), row 1 stays
    /// 0; VER_SR on the columns (−2, 0) and (2, 0) gives (−2, −2) and (2, 2). The picture varies
    /// along the row and not down the column — an LH coefficient gives the transpose.
    /// </summary>
    [Fact]
    public void Interleave_PutsHLOnOddColumnsAndLHOnOddRows()
    {
        var tc = new JpxRect(0, 0, 2, 2);

        JpxSubbandCoefficients fromHl = JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 1, tc, 4) });
        JpxSubbandCoefficients fromLh = JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 2, tc, 4) });

        Assert.Equal(new double[] { -2, 2, -2, 2 }, fromHl.Values);
        Assert.Equal(new double[] { -2, -2, 2, 2 }, fromLh.Values);
    }

    /// <summary>
    /// Two levels of the §F.4 forward on a 17x12 tile-component at (3, 5) — odd in both directions,
    /// and even by odd at the next level ((2, 3)-(10, 9)) — invert exactly.
    /// </summary>
    [Fact]
    public void Inverse53_RoundTripsTheForwardExactlyAtAnOddOrigin()
    {
        var tc = new JpxRect(3, 5, 20, 17);
        double[] samples = Samples(tc, seed: 7);
        List<JpxSubbandCoefficients> bands = Fdwt.Decompose(tc, 2, samples, reversible: true, out _);

        JpxSubbandCoefficients x = JpxWavelet.Inverse53(tc, 2, bands);

        Assert.Equal(tc, x.Bounds);
        Assert.Equal(samples.ToArray(), x.Values);
    }

    /// <summary>
    /// The 9-7 filter has unity DC gain: a constant image's N_L LL band is that constant and its
    /// other bands are zero (STEP1 to STEP6 of F-7 on a constant c: K·c, then 0 − γ·2K·c = −2.172c
    /// on the odd samples, then K·c − β·(−4.345c) = c, then −2.172c − α·2c = c). With K on the odd
    /// samples instead of the even ones the answer would be 0.66c.
    /// </summary>
    [Fact]
    public void Inverse97_KeepsAConstantImageConstant()
    {
        var tc = new JpxRect(3, 5, 20, 17);
        JpxRect ll = JpxGeometry.Subband(tc, 2, 0, 0);
        double[] constant = Enumerable.Repeat(100.0, (int)(ll.Width * ll.Height)).ToArray();

        JpxSubbandCoefficients x = JpxWavelet.Inverse97(tc, 2, new[] { new JpxSubbandCoefficients(2, 0, ll, constant) });

        Assert.Equal(17 * 12, x.Values.Length);
        Assert.All(x.Values, v => Assert.InRange((double)v, 100 - 1e-4, 100 + 1e-4));
    }

    /// <summary>Two levels of the §F.4 irreversible forward (F-11, Table F.4) invert within 1e-3.</summary>
    [Fact]
    public void Inverse97_RoundTripsTheForwardWithinTolerance()
    {
        var tc = new JpxRect(3, 5, 20, 17);
        double[] samples = Samples(tc, seed: 11);
        List<JpxSubbandCoefficients> bands = Fdwt.Decompose(tc, 2, samples, reversible: false, out _);

        JpxSubbandCoefficients x = JpxWavelet.Inverse97(tc, 2, bands);

        Assert.Equal(tc, x.Bounds);
        for (int i = 0; i < samples.Length; i++)
            Assert.InRange((double)x.Values[i], samples[i] - 1e-3, samples[i] + 1e-3);
    }

    /// <summary>
    /// A reduction of r leaves the last r levels alone: the result is the rLL band over resolution
    /// level N_L − r (equation B-14) — the forward's own intermediate LL, exactly.
    /// </summary>
    [Fact]
    public void Reduction_ReturnsTheLowerResolutionLevel()
    {
        var tc = new JpxRect(3, 5, 20, 17);
        double[] samples = Samples(tc, seed: 3);
        List<JpxSubbandCoefficients> bands = Fdwt.Decompose(tc, 2, samples, reversible: true, out double[][] llByLevel);

        JpxSubbandCoefficients one = JpxWavelet.Inverse53(tc, 2, bands, reduction: 1);
        JpxSubbandCoefficients two = JpxWavelet.Inverse53(tc, 2, bands, reduction: 2);

        Assert.Equal((1, 0, "(2, 3)-(10, 9)"), (one.Level, one.Orientation, one.Bounds.ToString()));
        Assert.Equal(llByLevel[1].ToArray(), one.Values);
        Assert.Equal((2, 0, "(1, 2)-(5, 5)"), (two.Level, two.Orientation, two.Bounds.ToString()));
        Assert.Equal(llByLevel[2].ToArray(), two.Values);
        Assert.NotEqual(one.Values.Length, two.Values.Length);
    }

    [Fact]
    public void NoLevels_ReturnsTheLLBandAsItIs()
    {
        var tc = new JpxRect(3, 5, 6, 7);
        var ll = new JpxSubbandCoefficients(0, 0, tc, new double[] { 1, 2, 3, 4, 5, 6 });

        JpxSubbandCoefficients x = JpxWavelet.Inverse53(tc, 0, new[] { ll });

        Assert.Same(ll.Values, x.Values);
        Assert.Equal((0, tc), (x.Level, x.Bounds));
    }

    /// <summary>
    /// A band left out of the list, and one given with an empty rectangle, are zero: a lone LL sample
    /// of 5 on a 2x1 tile-component reconstructs to (5, 5) (X(0) = 5 − ⌊(0 + 0 + 2)/4⌋, X(1) = 0 + 5)
    /// whether the HL band is absent or all zero — and 1LH here is empty by B-15 (rows ⌈−1/2⌉ to ⌈0/2⌉).
    /// </summary>
    [Fact]
    public void MissingAndEmptyBands_AreZero()
    {
        var tc = new JpxRect(0, 0, 2, 1);
        JpxRect lh = JpxGeometry.Subband(tc, 1, 0, 1);
        Assert.Equal("(0, 0)-(1, 0)", lh.ToString());

        JpxSubbandCoefficients absent = JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 0, tc, 5), new JpxSubbandCoefficients(1, 2, lh, new double[0]) });
        JpxSubbandCoefficients zero = JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 0, tc, 5), Band(1, 1, tc, 0) });

        Assert.Equal(new double[] { 5, 5 }, absent.Values);
        Assert.Equal(absent.Values, zero.Values);
    }

    [Fact]
    public void BandWithBoundsOtherThanEquationB15_IsRefused()
    {
        var tc = new JpxRect(0, 0, 4, 1);
        var shifted = new JpxSubbandCoefficients(1, 1, new JpxRect(1, 0, 3, 1), new double[] { 2, 17 });

        var ex = Assert.Throws<ArgumentException>(() => JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 0, tc, 8, 0), shifted }));

        Assert.Contains("B-15", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(0, 0)-(2, 1)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesNotMatchingTheBounds_AreRefusedByTheBand()
    {
        var ex = Assert.Throws<ArgumentException>(() => new JpxSubbandCoefficients(1, 0, new JpxRect(0, 0, 2, 1), new double[3]));
        Assert.Contains("2 values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReductionBeyondTheLevels_IsRefused()
    {
        var tc = new JpxRect(0, 0, 4, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxWavelet.Inverse53(tc, 1, new[] { Band(1, 0, tc, 8, 0) }, reduction: 2));
    }

    /// <summary>
    /// A hostile tile-component is refused by the area limit before any array is sized from it:
    /// 20 000 x 20 000 is four times the default <c>PdfLimits.MaxImagePixelArea</c>.
    /// </summary>
    [Fact]
    public void OversizedTileComponent_IsRefusedBeforeAllocation()
    {
        var huge = new JpxRect(0, 0, 20_000, 20_000);
        var ex = Assert.Throws<ArgumentException>(() => JpxWavelet.Inverse53(huge, 0, Array.Empty<JpxSubbandCoefficients>()));
        Assert.Contains("MaxImagePixelArea", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A band given as tier 1's values with its step reconstructs to exactly what the same band
    /// given already dequantised does — the dequantisation on the way into the interleave is the
    /// one rule (<c>JpxDequantizer.ReconstructStrided</c>), for the reversible half-bit form and
    /// for an irreversible step alike; and the result is not the input, so the transform ran.
    /// </summary>
    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.75)]
    public void QuantisedBands_ReconstructAsTheirDequantisedValuesDo(bool reversible, double step)
    {
        var tc = new JpxRect(3, 1, 27, 18);
        var quantised = new List<JpxSubbandCoefficients>();
        var dequantised = new List<JpxSubbandCoefficients>();
        uint state = 77;
        foreach ((int level, int orientation) in new[] { (2, 0), (2, 1), (2, 2), (2, 3), (1, 1), (1, 2), (1, 3) })
        {
            JpxRect b = JpxGeometry.Subband(tc, level, orientation & 1, orientation >> 1);
            var values = new int[b.Width * b.Height];
            for (int i = 0; i < values.Length; i++)
            {
                state = unchecked(state * 1103515245u + 12345u);
                int magnitude = (int)((state >> 16) & 0x3F);           // 0..63, twice the magnitude with the half bit below
                values[i] = magnitude == 0 ? 0 : (state & 1) != 0 ? -(2 * magnitude + 1) : 2 * magnitude + 1;
            }
            quantised.Add(new JpxSubbandCoefficients(level, orientation, b, values, reversible, step));
            var reconstructed = new double[values.Length];
            JpxDequantizer.Reconstruct(values, reconstructed, reversible, step);
            dequantised.Add(new JpxSubbandCoefficients(level, orientation, b, reconstructed));
        }

        JpxSubbandCoefficients viaQuantised = JpxWavelet.Inverse(tc, 2, quantised, 0, reversible);
        JpxSubbandCoefficients viaDequantised = JpxWavelet.Inverse(tc, 2, dequantised, 0, reversible);
        Assert.Equal(viaDequantised.Values, viaQuantised.Values);
        Assert.Equal(tc, viaQuantised.Bounds);
        Assert.Contains(viaQuantised.Values, v => v != 0);
        Assert.NotEqual(quantised[0].Reconstructed().Take(8).ToArray(), viaQuantised.Values.Take(8).ToArray());
    }

    /// <summary>
    /// The transform's two arrays alternate between levels, so the array a level writes may still
    /// hold a level two above it. Three levels: level 3 writes the result array, level 2 the
    /// scratch, level 1 the result again — and level 1's bands, when absent, must be written as
    /// zeros over what level 3 left, not left out. Absent level-1 bands give exactly what explicit
    /// zero bands give; the control shows level 3's output is not zero where it would leak.
    /// </summary>
    [Fact]
    public void AbsentBands_AreZeroEvenWhereAnEarlierLevelWroteTheArray()
    {
        var tc = new JpxRect(0, 0, 16, 8);
        List<JpxSubbandCoefficients> all = Fdwt.Decompose(tc, 3, Samples(tc, 5), reversible: true, out double[][] llByLevel);
        Assert.Contains(llByLevel[2], v => v != 0);   // what level 3 leaves in the result array

        List<JpxSubbandCoefficients> withoutLevelOne = all.Where(b => b.Level != 1).ToList();
        List<JpxSubbandCoefficients> withZeroLevelOne = new(withoutLevelOne);
        for (int orientation = 1; orientation <= 3; orientation++)
        {
            JpxRect b = JpxGeometry.Subband(tc, 1, orientation & 1, orientation >> 1);
            withZeroLevelOne.Add(new JpxSubbandCoefficients(1, orientation, b, new double[b.Width * b.Height]));
        }

        Assert.Equal(JpxWavelet.Inverse53(tc, 3, withZeroLevelOne).Values, JpxWavelet.Inverse53(tc, 3, withoutLevelOne).Values);
        Assert.NotEqual(JpxWavelet.Inverse53(tc, 3, all).Values, JpxWavelet.Inverse53(tc, 3, withoutLevelOne).Values);
    }

    public static IEnumerable<object[]> Shapes()
    {
        // Every parity of origin, signals of one, two and three samples (where the mirror wraps), odd and
        // even sizes; and one large enough that both passes are shared between workers.
        foreach ((int x0, int y0, int w, int h) in new[] { (0, 0, 1, 1), (1, 0, 2, 1), (0, 1, 1, 2), (3, 5, 2, 2), (0, 0, 3, 3), (1, 1, 3, 2),
                     (2, 7, 5, 9), (7, 2, 16, 14), (1, 3, 33, 17), (5, 6, 64, 3), (3, 1, 701, 523) })
            foreach (bool reversible in new[] { true, false })
                yield return new object[] { x0, y0, w, h, reversible };
    }

    /// <summary>
    /// The inverse reads §F.3.7's extension from the mirror rather than building it, lifts the columns a row at a
    /// time and keeps small levels off the workers: it is Annex F as printed — Tables F.2 and F.3's extents,
    /// §F.3.8's ranges — bit for bit, on every parity of origin and every short signal, on one worker and on
    /// four (whose passes split the large shape).
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheInverse_IsAnnexFAsPrinted_BitForBit(int x0, int y0, int w, int h, bool reversible)
    {
        var tc = new JpxRect(x0, y0, x0 + w, y0 + h);
        int levels = Math.Min(3, (int)Math.Floor(Math.Log2(Math.Max(1, Math.Min(w, h)))) + 1);
        double[] samples = Samples(tc, x0 + 7 * y0 + w + h);
        if (!reversible) for (int i = 0; i < samples.Length; i++) samples[i] += 0.25 + i % 7 * 0.125;
        List<JpxSubbandCoefficients> bands = Fdwt.Decompose(tc, levels, samples, reversible, out _);

        double[] expected = Idwt.Reconstruct(tc, levels, bands, reversible);
        foreach (int degree in new[] { 1, 4 })
        {
            double[] actual = JpxWavelet.Inverse(tc, levels, bands, 0, reversible, new JpxParallel(degree)).Values;
            Assert.Equal(expected.Select(BitConverter.DoubleToInt64Bits), actual.Select(BitConverter.DoubleToInt64Bits));
        }
        if (reversible) Assert.Equal(samples, expected);   // the oracle itself inverts the forward exactly
    }

    /// <summary>Deterministic samples in [−128, 127] over the rectangle, from a small LCG.</summary>
    private static double[] Samples(JpxRect rect, int seed)
    {
        var samples = new double[rect.Width * rect.Height];
        uint state = (uint)seed;
        for (int i = 0; i < samples.Length; i++)
        {
            state = unchecked(state * 1103515245u + 12345u);
            samples[i] = (int)((state >> 16) & 0xFF) - 128;
        }
        return samples;
    }

    /// <summary>
    /// The forward transform of T.800 §F.4, written from its own figures: the FDWT procedure
    /// (Figure F.19) runs 2D_SD (§F.4.2) per level — VER_SD on every column, HOR_SD on every row,
    /// then 2D_DEINTERLEAVE (Figure F.28) — and 1D_SD (§F.4.6) extends by Tables F.8 and F.9
    /// (here by 4 on each side, which §F.3.7 allows) before 1D_FILTD (F-9 and F-10, or F-11).
    /// </summary>
    internal static class Fdwt
    {
        private const double Alpha = -1.586134342059924;
        private const double Beta = -0.052980118572961;
        private const double Gamma = 0.882911075530934;
        private const double Delta = 0.443506852043971;
        private const double K = 1.230174104914001;
        private const int Ext = 4;

        internal static List<JpxSubbandCoefficients> Decompose(JpxRect tc, int levels, double[] samples, bool reversible, out double[][] llByLevel)
        {
            var bands = new List<JpxSubbandCoefficients>();
            llByLevel = new double[levels + 1][];
            llByLevel[0] = samples;
            double[] a = samples;
            JpxRect rect = tc;
            for (int lev = 1; lev <= levels; lev++)
            {
                int w = (int)rect.Width, h = (int)rect.Height;
                a = (double[])a.Clone();
                var column = new double[h];
                for (int u = 0; u < w; u++)
                {
                    for (int v = 0; v < h; v++) column[v] = a[v * w + u];
                    double[] y = Analyze(column, rect.Y0, reversible);
                    for (int v = 0; v < h; v++) a[v * w + u] = y[v];
                }
                var row = new double[w];
                for (int v = 0; v < h; v++)
                {
                    Array.Copy(a, v * w, row, 0, w);
                    double[] y = Analyze(row, rect.X0, reversible);
                    Array.Copy(y, 0, a, v * w, w);
                }

                double[] ll = null!;
                for (int orientation = 0; orientation < 4; orientation++)
                {
                    int xob = orientation & 1, yob = orientation >> 1;
                    JpxRect b = JpxGeometry.Subband(tc, lev, xob, yob);
                    int bw = (int)Math.Max(0, b.Width), bh = (int)Math.Max(0, b.Height);
                    var values = new double[bw * bh];
                    for (int vb = 0; vb < bh; vb++)
                        for (int ub = 0; ub < bw; ub++)
                            values[vb * bw + ub] = a[(int)(2 * (b.Y0 + vb) + yob - rect.Y0) * w + (int)(2 * (b.X0 + ub) + xob - rect.X0)];
                    if (orientation == 0) ll = values;
                    else bands.Add(new JpxSubbandCoefficients(lev, orientation, b, values.ToArray()));
                }
                rect = JpxGeometry.Subband(tc, lev, 0, 0);
                a = ll;
                llByLevel[lev] = ll;
            }
            bands.Add(new JpxSubbandCoefficients(levels, 0, rect, a.ToArray()));
            return bands;
        }

        /// <summary>1D_SD (§F.4.6): X(i0 .. i1) to Y over the same indices.</summary>
        internal static double[] Analyze(double[] x, long i0, bool reversible)
        {
            int count = x.Length;
            if (count == 1) return new[] { (i0 & 1) != 0 ? 2 * x[0] : x[0] };

            var line = new double[count + 2 * Ext];
            Array.Copy(x, 0, line, Ext, count);
            int period = 2 * (count - 1);
            for (int k = 1; k <= Ext; k++)
            {
                line[Ext - k] = line[Ext + Reflect(-k, period)];
                line[Ext + count + k - 1] = line[Ext + Reflect(count + k - 1, period)];
            }

            long i1 = i0 + count;
            long c0 = JpxGeometry.CeilDiv(i0, 2), c1 = JpxGeometry.CeilDiv(i1, 2);
            int At(long i) => Ext + (int)(i - i0);
            if (reversible)
            {
                // F-9 for ⌈i0/2⌉ − 1 ≤ n < ⌈i1/2⌉, then F-10 for ⌈i0/2⌉ ≤ n < ⌈i1/2⌉.
                for (long n = c0 - 1; n < c1; n++)
                    line[At(2 * n + 1)] -= Math.Floor((line[At(2 * n)] + line[At(2 * n + 2)]) / 2);
                for (long n = c0; n < c1; n++)
                    line[At(2 * n)] += Math.Floor((line[At(2 * n - 1)] + line[At(2 * n + 1)] + 2) / 4);
            }
            else
            {
                // F-11, steps 1 to 6 over the ranges §F.4.8.2 gives them.
                for (long n = c0 - 2; n < c1 + 1; n++) line[At(2 * n + 1)] += Alpha * (line[At(2 * n)] + line[At(2 * n + 2)]);
                for (long n = c0 - 1; n < c1 + 1; n++) line[At(2 * n)] += Beta * (line[At(2 * n - 1)] + line[At(2 * n + 1)]);
                for (long n = c0 - 1; n < c1; n++) line[At(2 * n + 1)] += Gamma * (line[At(2 * n)] + line[At(2 * n + 2)]);
                for (long n = c0; n < c1; n++) line[At(2 * n)] += Delta * (line[At(2 * n - 1)] + line[At(2 * n + 1)]);
                for (long n = c0 - 1; n < c1; n++) line[At(2 * n + 1)] *= K;
                for (long n = c0; n < c1; n++) line[At(2 * n)] /= K;
            }

            var y = new double[count];
            Array.Copy(line, Ext, y, 0, count);
            return y;
        }

        private static int Reflect(int k, int period)
        {
            int m = k % period;
            if (m < 0) m += period;
            return Math.Min(m, period - m);
        }
    }

    /// <summary>
    /// The inverse as Annex F prints it: per level 2D_INTERLEAVE (Figure F.8), HOR_SR on every row and VER_SR on
    /// every column, each 1D_SR extending by Tables F.2 and F.3 (§F.3.7) and lifting over §F.3.8's ranges — what
    /// the product, which reads the extension from the mirror, must equal bit for bit.
    /// </summary>
    internal static class Idwt
    {
        private const double Alpha = -1.586134342059924;
        private const double Beta = -0.052980118572961;
        private const double Gamma = 0.882911075530934;
        private const double Delta = 0.443506852043971;
        private const double K = 1.230174104914001;
        private const int Ext = 4;

        internal static double[] Reconstruct(JpxRect tc, int levels, List<JpxSubbandCoefficients> bands, bool reversible)
        {
            double[] a = bands.Single(b => b.Orientation == 0).Values;
            for (int lev = levels; lev >= 1; lev--)
            {
                JpxRect rect = JpxGeometry.Subband(tc, lev - 1, 0, 0);
                int w = (int)Math.Max(0, rect.Width), h = (int)Math.Max(0, rect.Height);
                var next = new double[w * h];
                for (int o = 0; o < 4; o++)
                {
                    int xob = o & 1, yob = o >> 1;
                    JpxRect b = JpxGeometry.Subband(tc, lev, xob, yob);
                    double[] values = o == 0 ? a : bands.Single(x => x.Level == lev && x.Orientation == o).Values;
                    int bw = (int)Math.Max(0, b.Width), bh = (int)Math.Max(0, b.Height);
                    for (int vb = 0; vb < bh; vb++)
                        for (int ub = 0; ub < bw; ub++)
                            next[(int)(2 * (b.Y0 + vb) + yob - rect.Y0) * w + (int)(2 * (b.X0 + ub) + xob - rect.X0)] = values[vb * bw + ub];
                }
                if (w > 0 && h > 0)
                {
                    var row = new double[w];
                    for (int v = 0; v < h; v++)
                    {
                        Array.Copy(next, v * w, row, 0, w);
                        Array.Copy(Synthesize(row, rect.X0, reversible), 0, next, v * w, w);
                    }
                    var column = new double[h];
                    for (int u = 0; u < w; u++)
                    {
                        for (int v = 0; v < h; v++) column[v] = next[v * w + u];
                        double[] x = Synthesize(column, rect.Y0, reversible);
                        for (int v = 0; v < h; v++) next[v * w + u] = x[v];
                    }
                }
                a = next;
            }
            return a;
        }

        /// <summary>1D_SR (§F.3.6): 1D_EXTR by Tables F.2 and F.3, then 1D_FILTR over §F.3.8's ranges.</summary>
        private static double[] Synthesize(double[] y, long i0, bool reversible)
        {
            int count = y.Length;
            if (count == 1) return new[] { (i0 & 1) != 0 ? y[0] / 2 : y[0] };
            var line = new double[count + 2 * Ext];
            Array.Copy(y, 0, line, Ext, count);
            long i1 = i0 + count;
            bool i0Odd = (i0 & 1) != 0, i1Odd = (i1 & 1) != 0;
            int left = reversible ? (i0Odd ? 2 : 1) : (i0Odd ? 4 : 3);
            int right = reversible ? (i1Odd ? 1 : 2) : (i1Odd ? 3 : 4);
            int period = 2 * (count - 1);
            for (int k = 1; k <= left; k++) line[Ext - k] = line[Ext + Reflect(-k, period)];
            for (int k = 0; k < right; k++) line[Ext + count + k] = line[Ext + Reflect(count + k, period)];

            long n0 = JpxGeometry.FloorDiv(i0, 2), n1 = JpxGeometry.FloorDiv(i1, 2);
            int At(long i) => Ext + (int)(i - i0);
            if (reversible)
            {
                for (long n = n0; n < n1 + 1; n++) line[At(2 * n)] -= Math.Floor((line[At(2 * n - 1)] + line[At(2 * n + 1)] + 2) / 4);   // F-5
                for (long n = n0; n < n1; n++) line[At(2 * n + 1)] += Math.Floor((line[At(2 * n)] + line[At(2 * n + 2)]) / 2);           // F-6
            }
            else
            {
                for (long n = n0 - 1; n < n1 + 2; n++) line[At(2 * n)] *= K;                                                           // STEP1
                for (long n = n0 - 2; n < n1 + 2; n++) line[At(2 * n + 1)] /= K;                                                       // STEP2
                for (long n = n0 - 1; n < n1 + 2; n++) line[At(2 * n)] -= Delta * (line[At(2 * n - 1)] + line[At(2 * n + 1)]);         // STEP3
                for (long n = n0 - 1; n < n1 + 1; n++) line[At(2 * n + 1)] -= Gamma * (line[At(2 * n)] + line[At(2 * n + 2)]);         // STEP4
                for (long n = n0; n < n1 + 1; n++) line[At(2 * n)] -= Beta * (line[At(2 * n - 1)] + line[At(2 * n + 1)]);              // STEP5
                for (long n = n0; n < n1; n++) line[At(2 * n + 1)] -= Alpha * (line[At(2 * n)] + line[At(2 * n + 2)]);                 // STEP6
            }
            var x = new double[count];
            Array.Copy(line, Ext, x, 0, count);
            return x;
        }

        private static int Reflect(int k, int period)
        {
            int m = k % period;
            if (m < 0) m += period;
            return Math.Min(m, period - m);
        }
    }
}
