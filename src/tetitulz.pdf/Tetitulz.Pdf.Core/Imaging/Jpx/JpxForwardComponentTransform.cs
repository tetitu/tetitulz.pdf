// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The encoder's side of T.800 Annex G: the DC level shift of an unsigned component (G-1), the
    /// forward reversible component transformation (G.2.1, equations G-3 to G-5) and the forward
    /// irreversible one (G.3.1, equations G-9 to G-11), each on the first three components in place.
    /// </summary>
    internal static class JpxForwardComponentTransform
    {
        /// <summary>G-1: an unsigned component's samples less 2<sup>precision − 1</sup>; a signed component is left alone.</summary>
        internal static void DcShift(long[] samples, int precision, bool isSigned)
        {
            if (isSigned) return;
            long shift = 1L << (precision - 1);
            for (int i = 0; i < samples.Length; i++) samples[i] -= shift;
        }

        /// <summary>The shift on integer samples: the <c>long</c> one over a copy, for callers that hold integers.</summary>
        internal static void DcShift(int[] samples, int precision, bool isSigned)
        {
            long[] wide = Widen(samples);
            DcShift(wide, precision, isSigned);
            Narrow(wide, samples);
        }

        /// <summary>
        /// The forward RCT: Y0 = ⌊(I0 + 2·I1 + I2) / 4⌋, Y1 = I2 − I1, Y2 = I0 − I1 — exact integers,
        /// the chrominance a bit wider than the input (G.2.1).
        /// </summary>
        internal static void Rct(long[] c0, long[] c1, long[] c2)
        {
            Check(c0, c1, c2);
            for (int i = 0; i < c0.Length; i++)
            {
                long r = c0[i], g = c1[i], b = c2[i];
                c0[i] = (r + 2 * g + b) >> 2;
                c1[i] = b - g;
                c2[i] = r - g;
            }
        }

        /// <summary>The forward RCT on integer arrays, likewise.</summary>
        internal static void Rct(int[] c0, int[] c1, int[] c2)
        {
            long[] w0 = Widen(c0), w1 = Widen(c1), w2 = Widen(c2);
            Rct(w0, w1, w2);
            Narrow(w0, c0);
            Narrow(w1, c1);
            Narrow(w2, c2);
        }

        internal static long[] Widen(int[] values)
        {
            var wide = new long[values.Length];
            for (int i = 0; i < values.Length; i++) wide[i] = values[i];
            return wide;
        }

        private static void Narrow(long[] wide, int[] values)
        {
            for (int i = 0; i < values.Length; i++) values[i] = checked((int)wide[i]);
        }

        /// <summary>The forward ICT: the YCbCr rotation of G-9 to G-11.</summary>
        internal static void Ict(double[] c0, double[] c1, double[] c2)
        {
            Check(c0, c1, c2);
            for (int i = 0; i < c0.Length; i++)
            {
                double r = c0[i], g = c1[i], b = c2[i];
                c0[i] = 0.299 * r + 0.587 * g + 0.114 * b;
                c1[i] = -0.16875 * r - 0.331260 * g + 0.5 * b;
                c2[i] = 0.5 * r - 0.41869 * g - 0.08131 * b;
            }
        }

        /// <summary>The forward ICT on <c>float</c> arrays: the <c>double</c> one over copies, for callers that hold floats.</summary>
        internal static void Ict(float[] c0, float[] c1, float[] c2)
        {
            double[] d0 = ToDoubles(c0), d1 = ToDoubles(c1), d2 = ToDoubles(c2);
            Ict(d0, d1, d2);
            for (int i = 0; i < c0.Length; i++)
            {
                c0[i] = (float)d0[i];
                c1[i] = (float)d1[i];
                c2[i] = (float)d2[i];
            }
        }

        internal static double[] ToDoubles(float[] values)
        {
            var d = new double[values.Length];
            for (int i = 0; i < values.Length; i++) d[i] = values[i];
            return d;
        }

        private static void Check<T>(T[] c0, T[] c1, T[] c2)
        {
            if (c0 == null || c1 == null || c2 == null) throw new ArgumentNullException(nameof(c0));
            if (c1.Length != c0.Length || c2.Length != c0.Length)
                throw new ArgumentException("the three components must have the same number of samples", nameof(c1));
        }
    }
}
