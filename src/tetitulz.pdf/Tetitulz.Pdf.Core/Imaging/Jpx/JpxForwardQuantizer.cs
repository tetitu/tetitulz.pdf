// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Scalar quantisation on the encoder's side (T.800 E.2): the exponent and mantissa a QCD
    /// signals for a wanted step size, the step size they denote (E-3), and the quantised
    /// coefficient (E-9). The decoder's <see cref="JpxQuantizationSteps"/> reads the same fields back.
    /// </summary>
    internal static class JpxForwardQuantizer
    {
        /// <summary>The mantissa's width in a QCD (Table A.30): 11 bits — the decoder's constant, read here so the two sides cannot drift.</summary>
        internal const int MantissaBits = JpxDequantizer.MantissaBits;

        /// <summary>The largest exponent a QCD's five bits hold.</summary>
        internal const int MaxExponent = 31;

        /// <summary>
        /// The (ε<sub>b</sub>, μ<sub>b</sub>) pair whose E-3 step, for a band of nominal range
        /// <paramref name="rangeBits"/> (R<sub>b</sub>), is nearest to <paramref name="step"/>: the
        /// exponent from the step's power of two, the mantissa from what remains, each clamped to
        /// its field. The step actually used is <see cref="Step"/> of the pair, never the request.
        /// </summary>
        internal static void StepSize(double step, int rangeBits, out int exponent, out int mantissa)
        {
            if (!(step > 0) || double.IsInfinity(step)) throw new ArgumentOutOfRangeException(nameof(step), "a quantisation step is a positive finite number");
            int power = SaturatingCast.ToInt(Math.Floor(Math.Log(step, 2)));
            double fraction = step / Math.Pow(2, power) - 1;          // in [0, 1)
            mantissa = SaturatingCast.ToInt(Math.Round(fraction * (1 << MantissaBits)));
            if (mantissa == 1 << MantissaBits)
            {
                mantissa = 0;
                power++;
            }
            exponent = rangeBits - power;
            if (exponent > MaxExponent) { exponent = MaxExponent; mantissa = 0; }
            if (exponent < 0) { exponent = 0; mantissa = (1 << MantissaBits) - 1; }
        }

        /// <summary>E-3, Δ<sub>b</sub> = 2<sup>R<sub>b</sub> − ε<sub>b</sub></sup> (1 + μ<sub>b</sub> / 2<sup>11</sup>) — <see cref="JpxDequantizer.StepSize(int, int, int)"/>, the one statement of it, so the step the encoder quantises by is the step the decoder reconstructs by.</summary>
        internal static double Step(int exponent, int mantissa, int rangeBits) => JpxDequantizer.StepSize(exponent, mantissa, rangeBits);

        /// <summary>E-9: sign(a) · ⌊|a| / Δ<sub>b</sub>⌋.</summary>
        internal static long Quantize(double coefficient, double step)
        {
            long magnitude = SaturatingCast.ToLong(Math.Floor(Math.Abs(coefficient) / step));
            return coefficient < 0 ? -magnitude : magnitude;
        }

        /// <summary>E-9 for a <c>float</c> coefficient whose quotient fits an <c>int</c>.</summary>
        internal static int Quantize(float coefficient, double step) => checked((int)Quantize((double)coefficient, step));
    }
}
