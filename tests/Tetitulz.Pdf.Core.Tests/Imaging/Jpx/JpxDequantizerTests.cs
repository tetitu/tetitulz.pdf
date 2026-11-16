// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxDequantizer"/> against T.800 Annex E: equation E-3 with hand-worked values, Table
/// E.1's gains through E-4, E-5 through <see cref="JpxQuantizationSteps"/>, and the reconstruction
/// of E-6, E-7 and E-8 from tier 1's twice-the-magnitude values.
/// </summary>
public sealed class JpxDequantizerTests
{
    /// <summary>
    /// E-3 by hand: ε 8, μ 0, R<sub>b</sub> 8 gives 2^0 · 1 = 1; μ 1024 gives 1 + 1024/2048 = 1.5;
    /// ε 10 gives 2^(8−10) = 0.25 — the exponent's sign — and ε 6 gives 4.
    /// </summary>
    [Fact]
    public void StepSize_FollowsEquationE3()
    {
        Assert.Equal(1.0, JpxDequantizer.StepSize(exponent: 8, mantissa: 0, nominalRange: 8));
        Assert.Equal(1.5, JpxDequantizer.StepSize(8, 1024, 8));
        Assert.Equal(0.25, JpxDequantizer.StepSize(10, 0, 8));
        Assert.Equal(4.0, JpxDequantizer.StepSize(6, 0, 8));
        // The 11-bit mantissa's largest value is just short of a doubling (Table A.30).
        Assert.Equal(1 + 2047 / 2048.0, JpxDequantizer.StepSize(8, 2047, 8));
    }

    /// <summary>
    /// Table E.1 through E-4: an 8-bit component's LL band has R<sub>b</sub> 8, HL and LH 9, HH 10 —
    /// so at ε 8 the steps are 1, 2, 2 and 4.
    /// </summary>
    [Fact]
    public void NominalRange_AddsTableE1sGain()
    {
        Assert.Equal(8, JpxDequantizer.NominalRange(8, 0));
        Assert.Equal(9, JpxDequantizer.NominalRange(8, 1));
        Assert.Equal(9, JpxDequantizer.NominalRange(8, 2));
        Assert.Equal(10, JpxDequantizer.NominalRange(8, 3));
        Assert.Equal(2.0, JpxDequantizer.StepSize(8, 0, JpxDequantizer.NominalRange(8, 1)));
        Assert.Equal(4.0, JpxDequantizer.StepSize(8, 0, JpxDequantizer.NominalRange(8, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxDequantizer.Log2Gain(4));
    }

    /// <summary>
    /// The band-level step: expounded pairs by band index, derived pairs by E-5, and 1 for a
    /// reversible band whatever the QCD says (E.1.2.1).
    /// </summary>
    [Fact]
    public void BandStepSize_ReadsTheQuantizationAndIsOneWhenReversible()
    {
        // Expounded, one level, 8-bit component: LL (ε 9, μ 0), HL (ε 9, μ 1024), LH (ε 10, μ 0), HH (ε 8, μ 0).
        var expounded = new JpxQuantization(JpxQuantizationStyle.ScalarExpounded, 2, new[] { 9, 9, 10, 8 }, new[] { 0, 1024, 0, 0 });
        Assert.Equal(0.5, JpxDequantizer.StepSize(expounded, reversible: false, levels: 1, resolution: 0, orientation: 0, precision: 8));   // 2^(8−9)
        Assert.Equal(1.5, JpxDequantizer.StepSize(expounded, false, 1, 1, 1, 8));   // 2^(9−9) · 1.5
        Assert.Equal(0.5, JpxDequantizer.StepSize(expounded, false, 1, 1, 2, 8));   // 2^(9−10)
        Assert.Equal(4.0, JpxDequantizer.StepSize(expounded, false, 1, 1, 3, 8));     // 2^(10−8)
        Assert.Equal(1.0, JpxDequantizer.StepSize(expounded, reversible: true, 1, 1, 3, 8));

        // Derived (E-5): ε_b = ε_0 − N_L + n_b. With ε_0 10, μ_0 512 and N_L 2, a level-1 band (n_b 1)
        // has ε 9: HL's step is 2^(9−9) · 1.25; the LL band (n_b = N_L) keeps ε_0: 2^(8−10) · 1.25.
        var derived = new JpxQuantization(JpxQuantizationStyle.ScalarDerived, 2, new[] { 10 }, new[] { 512 });
        Assert.Equal(1.25, JpxDequantizer.StepSize(derived, false, levels: 2, resolution: 2, orientation: 1, precision: 8));
        Assert.Equal(0.3125, JpxDequantizer.StepSize(derived, false, 2, 0, 0, 8));
    }

    /// <summary>
    /// Tier 1's value 11 is twice 5.5: a reversible band gives 5 (E-7, the half dropped), an
    /// irreversible one 5.5 · Δ<sub>b</sub> (E-6); −11 gives −5 and −5.5 · Δ<sub>b</sub> — halved towards
    /// zero, where an arithmetic shift would give −6. The step is ignored on a reversible band.
    /// </summary>
    [Fact]
    public void Reconstruct_HalvesTowardsZeroOrScalesByTheStep()
    {
        int[] values = { 11, -11, 0, 3, -3, 2 };
        var reversible = new double[6];
        JpxDequantizer.Reconstruct(values, reversible, reversible: true, stepSize: 0.25);
        Assert.Equal(new double[] { 5, -5, 0, 1, -1, 1 }, reversible);

        var irreversible = new double[6];
        JpxDequantizer.Reconstruct(values, irreversible, reversible: false, stepSize: 0.25);
        Assert.Equal(new[] { 1.375, -1.375, 0, 0.375, -0.375, 0.25 }, irreversible);

        Assert.Equal(-5, JpxDequantizer.ReversibleCoefficient(-11));
        Assert.Equal(5, JpxDequantizer.ReversibleCoefficient(11));

        // A tier 1 value at the top of the 30 planes tier 1 holds is exact in double storage: float
        // would have rounded 2^29 + 3 (twice 2^28 + 1, plus the half) to a multiple of 32 before halving.
        var wide = new double[1];
        JpxDequantizer.Reconstruct(new[] { (1 << 29) + 3 }, wide, reversible: true, stepSize: 1);
        Assert.Equal(new[] { (1 << 28) + 1.0 }, wide);
    }
}
