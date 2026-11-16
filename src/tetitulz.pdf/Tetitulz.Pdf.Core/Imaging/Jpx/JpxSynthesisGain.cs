// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// How much of a coefficient's error reaches the samples: the energy (squared L2 norm) of the
    /// inverse wavelet's response to a unit coefficient in each sub-band, and of the inverse
    /// component transformation's response to a unit in each of its three inputs. A rate
    /// allocator weighs a code-block's distortion by them, so that a bit spent in a band whose
    /// synthesis basis is large counts for what it does to the picture.
    /// </summary>
    /// <remarks>
    /// <para><b>Measured through the decoder's own transforms, never tabled.</b> A unit impulse in
    /// a band of a one-row image goes through <see cref="JpxWavelet.Inverse(JpxRect, int, IReadOnlyList{JpxSubbandCoefficients}, int, bool)"/>
    /// — a column of one sample is the identity there (F.3.7), so the result is the row filter's
    /// response alone — and its energy is the one-dimensional gain of that level's low-pass or
    /// high-pass basis; a two-dimensional band's gain is the product of its row and column
    /// gains. Likewise a unit through <see cref="JpxComponentTransforms.InverseIct"/>, and a large
    /// unit through <see cref="JpxComponentTransforms.InverseRct(int[], int[], int[])"/> (whose floors vanish against
    /// it), gives each input's gain. Whatever convention the transforms keep — F-7's K on the even
    /// samples, say — the weights follow it.</para>
    /// <para>The response of a level-l basis spans about 2<sup>l</sup> samples, so the image is
    /// sized to the level; past <see cref="MeasuredLevels"/> the one-dimensional energy is
    /// extrapolated by the doubling per level it settles into (a basis twice as wide, no taller),
    /// which is all a weight needs.</para>
    /// <para>These are T.800's units: a high-pass synthesis basis here has half the norm that
    /// other encoders' tables give it, because they scale its high-pass coefficients by two
    /// on the way in and back on the way out. The weights must match the coefficients this
    /// encoder quantises and this decoder reconstructs, which are T.800's.</para>
    /// </remarks>
    internal static class JpxSynthesisGain
    {
        /// <summary>Levels measured exactly; above it each direction's gain doubles per level (the basis widens by two, its height unchanged), so a band's quadruples.</summary>
        internal const int MeasuredLevels = 12;

        private static readonly object Sync = new object();
        private static readonly Dictionary<int, double[][]> Cache = new Dictionary<int, double[][]>();   // key: levels, +64 for reversible
        private static double[] _ictGain, _rctGain;

        /// <summary>
        /// The two-dimensional energy gain of every band of an N<sub>L</sub>-level decomposition,
        /// indexed like the forward wavelet's output: <c>[0][0]</c> the LL band, <c>[r][b]</c>
        /// for r ≥ 1 the HL, LH and HH bands (b = 0, 1, 2) of decomposition level N<sub>L</sub> − r + 1.
        /// </summary>
        internal static double[][] Bands(int levels, bool reversible)
        {
            if (levels < 0 || levels > JpxWavelet.MaxLevels) throw new ArgumentOutOfRangeException(nameof(levels));
            int key = levels + (reversible ? 64 : 0);
            lock (Sync)
            {
                if (Cache.TryGetValue(key, out double[][] cached)) return cached;
                var gains = new double[levels + 1][];
                double[] low = OneDimensional(levels, reversible, high: false);
                double[] high = OneDimensional(levels, reversible, high: true);
                gains[0] = new[] { levels == 0 ? 1.0 : low[levels] * low[levels] };
                for (int r = 1; r <= levels; r++)
                {
                    int nb = levels - r + 1;
                    gains[r] = new[] { high[nb] * low[nb], low[nb] * high[nb], high[nb] * high[nb] };
                }
                Cache[key] = gains;
                return gains;
            }
        }

        /// <summary>The gain of a unit error in input <paramref name="component"/> (0 to 2) of the inverse ICT or RCT; 1 for any other component.</summary>
        internal static double ComponentTransform(int component, bool reversible)
        {
            if (component < 0 || component > 2) return 1;
            lock (Sync)
            {
                if (reversible)
                {
                    if (_rctGain == null)
                    {
                        // A unit large enough that G-6's floor is a rounding of the linear response.
                        const int unit = 1 << 20;
                        _rctGain = new double[3];
                        for (int c = 0; c < 3; c++)
                        {
                            int[] y0 = { c == 0 ? unit : 0 }, y1 = { c == 1 ? unit : 0 }, y2 = { c == 2 ? unit : 0 };
                            JpxComponentTransforms.InverseRct(y0, y1, y2);
                            _rctGain[c] = ((double)y0[0] * y0[0] + (double)y1[0] * y1[0] + (double)y2[0] * y2[0]) / ((double)unit * unit);
                        }
                    }
                    return _rctGain[component];
                }
                if (_ictGain == null)
                {
                    _ictGain = new double[3];
                    for (int c = 0; c < 3; c++)
                    {
                        double[] y0 = { c == 0 ? 1 : 0 }, y1 = { c == 1 ? 1 : 0 }, y2 = { c == 2 ? 1 : 0 };
                        JpxComponentTransforms.InverseIct(y0, y1, y2);
                        _ictGain[c] = y0[0] * y0[0] + y1[0] * y1[0] + y2[0] * y2[0];
                    }
                }
                return _ictGain[component];
            }
        }

        /// <summary>Index nb (1 to levels): the energy of the row filter's response to a unit in the level-nb low-pass (or high-pass) band of a one-row image.</summary>
        private static double[] OneDimensional(int levels, bool reversible, bool high)
        {
            var gains = new double[levels + 1];
            int measured = Math.Min(levels, MeasuredLevels);
            for (int nb = 1; nb <= measured; nb++)
            {
                int width = 1 << (nb + 5);
                var tile = new JpxRect(0, 0, width, 1);
                JpxRect bounds = JpxGeometry.Subband(tile, nb, high ? 1 : 0, 0);
                // A unit large enough that the reversible filter's floors are a rounding of the linear response.
                const double unit = 1 << 20;
                var values = new double[bounds.Width * bounds.Height];
                values[values.Length / 2] = unit;
                var band = new JpxSubbandCoefficients(nb, high ? 1 : 0, bounds, values);
                JpxSubbandCoefficients samples = JpxWavelet.Inverse(tile, nb, new[] { band }, 0, reversible);
                double energy = 0;
                foreach (double v in samples.Values) energy += v * v;
                gains[nb] = energy / (unit * unit);
            }
            for (int nb = measured + 1; nb <= levels; nb++) gains[nb] = 2 * gains[nb - 1];
            return gains;
        }
    }
}
