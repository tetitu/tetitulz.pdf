// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Dequantisation (T.800 Annex E): the step size Δ<sub>b</sub> of equation E-3 for a sub-band, and
    /// the reconstruction of a transform coefficient from what tier 1 decoded — E-6 for an
    /// irreversible band, E-7 or E-8 for a reversible one.
    /// </summary>
    /// <remarks>
    /// <para><b>Tier 1's value convention</b> (<c>JpxBlockDecoder.Decode</c>): each value is twice the
    /// reconstructed magnitude — the half of E-6's r = ½ sits at the last decoded bit-plane — negated
    /// for a negative coefficient, 0 for one never found significant. An irreversible coefficient is
    /// therefore Δ<sub>b</sub> · value / 2, the half already in it; a reversible one is the value
    /// halved towards zero, which is E-7's exact integer when every bit-plane was decoded and E-8
    /// with r = ½ otherwise (the half bit sits at 2^−1 then, and dropping it is exactly E-7).</para>
    /// <para>The ROI scaling of §H.1 is tier 1's, which knows which coefficients sat above the
    /// shift; nothing here applies it again.</para>
    /// <para>Coefficients are <c>double</c> for both wavelets, the array type the inverse wavelet
    /// takes per band, so the 5-3 path's integers are exact at every precision tier 1 can hold.</para>
    /// <para><b>The gain of Table E.1 is load-bearing.</b> E-4's nominal range counts one bit per
    /// high-pass direction, and T.800's inverse 9-7 (F-7, STEP1 and STEP2) scales by K and 1/K with no
    /// factor of 2. Other implementations take the gain as 0 for the 9-7 and scale their high-pass
    /// synthesis by 2/K instead — the same numbers, distributed differently. The wavelet must follow
    /// F-7 as printed for these step sizes to be right.</para>
    /// </remarks>
    internal static class JpxDequantizer
    {
        /// <summary>The bits a mantissa μ<sub>b</sub> has (T.800 Table A.30) — the 2^11 of E-3.</summary>
        internal const int MantissaBits = 11;

        /// <summary>log<sub>2</sub>(gain<sub>b</sub>) of T.800 Table E.1 by orientation (0 LL, 1 HL, 2 LH, 3 HH): 0, 1, 1, 2.</summary>
        internal static int Log2Gain(int orientation)
        {
            switch (orientation)
            {
                case 0: return 0;
                case 1:
                case 2: return 1;
                case 3: return 2;
                default: throw new ArgumentOutOfRangeException(nameof(orientation), orientation, "a sub-band orientation is 0 to 3");
            }
        }

        /// <summary>R<sub>b</sub> = R<sub>I</sub> + log<sub>2</sub>(gain<sub>b</sub>) (E-4): the SIZ precision plus the band's gain.</summary>
        internal static int NominalRange(int precision, int orientation) => precision + Log2Gain(orientation);

        /// <summary>Δ<sub>b</sub> = 2^(R<sub>b</sub> − ε<sub>b</sub>) · (1 + μ<sub>b</sub> / 2^11) (E-3).</summary>
        internal static double StepSize(int exponent, int mantissa, int nominalRange) =>
            (double)(Math.Pow(2, nominalRange - exponent) * (1 + mantissa / (double)(1 << MantissaBits)));

        /// <summary>
        /// Δ<sub>b</sub> of a tile-component's band: 1 for the reversible wavelet (E.1.2.1, no
        /// quantisation), else E-3 from the QCD/QCC pair (E-5 for derived quantisation, through
        /// <see cref="JpxQuantizationSteps"/>) and the SIZ precision R<sub>I</sub>.
        /// </summary>
        internal static double StepSize(JpxQuantization q, bool reversible, int levels, int resolution, int orientation, int precision)
        {
            if (reversible) return 1;
            return StepSize(JpxQuantizationSteps.Exponent(q, levels, resolution, orientation),
                JpxQuantizationSteps.Mantissa(q, resolution, orientation), NominalRange(precision, orientation));
        }

        /// <summary>
        /// A tier-1 value as a reversible coefficient: halved towards zero — E-7 when fully decoded,
        /// E-8 with r = ½ otherwise. (An arithmetic shift of the signed value would floor a negative
        /// one too low.)
        /// </summary>
        internal static int ReversibleCoefficient(int value) => JpxBlockDecoder.Truncate(value);

        /// <summary>The same for a wide band's value.</summary>
        internal static long ReversibleCoefficient(long value) => JpxBlockDecoder.Truncate(value);

        /// <summary>
        /// Reconstructs every value of a block or band, in place of index: E-7/E-8 for a reversible
        /// band, E-6 with Δ<sub>b</sub> = <paramref name="stepSize"/> for an irreversible one.
        /// </summary>
        internal static void Reconstruct(int[] values, double[] coefficients, bool reversible, double stepSize)
        {
            if (coefficients.Length < values.Length)
                throw new ArgumentException("the coefficient array is shorter than the values", nameof(coefficients));
            ReconstructStrided(values, 0, coefficients, 0, values.Length, 1, reversible, stepSize);
        }

        /// <summary>
        /// The same reconstruction for <paramref name="count"/> values from <paramref name="values"/>
        /// at <paramref name="source"/> on, each into <paramref name="coefficients"/> at
        /// <paramref name="target"/> + i · <paramref name="targetStep"/> — the form the inverse
        /// wavelet's 2D_INTERLEAVE uses, where a band's row lands on every other column of the
        /// interleaved array, so that no dequantised copy of a band is ever made.
        /// </summary>
        internal static void ReconstructStrided(int[] values, int source, double[] coefficients, int target, int count, int targetStep,
            bool reversible, double stepSize)
        {
            if (reversible)
            {
                for (int i = 0; i < count; i++, target += targetStep) coefficients[target] = ReversibleCoefficient(values[source + i]);
            }
            else
            {
                double half = stepSize * 0.5;
                for (int i = 0; i < count; i++, target += targetStep) coefficients[target] = values[source + i] * half;
            }
        }

        /// <summary>The same reconstruction from a wide band's <c>long</c> values.</summary>
        internal static void Reconstruct(long[] values, double[] coefficients, bool reversible, double stepSize)
        {
            if (coefficients.Length < values.Length)
                throw new ArgumentException("the coefficient array is shorter than the values", nameof(coefficients));
            ReconstructStrided(values, 0, coefficients, 0, values.Length, 1, reversible, stepSize);
        }

        /// <summary>The strided form from a wide band's <c>long</c> values.</summary>
        internal static void ReconstructStrided(long[] values, int source, double[] coefficients, int target, int count, int targetStep,
            bool reversible, double stepSize)
        {
            if (reversible)
            {
                for (int i = 0; i < count; i++, target += targetStep) coefficients[target] = ReversibleCoefficient(values[source + i]);
            }
            else
            {
                double half = stepSize * 0.5;
                for (int i = 0; i < count; i++, target += targetStep) coefficients[target] = values[source + i] * half;
            }
        }
    }
}
