// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// What a QCD or QCC says about one sub-band (T.800 Annex E): its exponent ε<sub>b</sub> and
    /// mantissa μ<sub>b</sub> — signalled per sub-band, or derived from the LL band's pair by
    /// equation E-5 — and from them the magnitude bit-plane count M<sub>b</sub> of equation E-2,
    /// which tier 1 needs before it can read a single pass.
    /// </summary>
    internal static class JpxQuantizationSteps
    {
        /// <summary>
        /// The index of a sub-band in a QCD's list (Table A.30 order): the LL band, then HL, LH, HH
        /// of each resolution level from 1 up.
        /// </summary>
        internal static int BandIndex(int resolution, int orientation) =>
            resolution == 0 ? 0 : 3 * (resolution - 1) + orientation;

        /// <summary>ε<sub>b</sub> for the band at <paramref name="resolution"/> (0 = LL) with <paramref name="orientation"/> (1 HL, 2 LH, 3 HH).</summary>
        internal static int Exponent(JpxQuantization q, int levels, int resolution, int orientation)
        {
            if (q.Style == JpxQuantizationStyle.ScalarDerived)
            {
                // E-5: ε_b = ε_0 − N_L + n_b, with n_b the band's decomposition level (N_L for LL).
                int nb = resolution == 0 ? levels : levels - resolution + 1;
                return q.Exponents[0] - levels + nb;
            }
            int index = BandIndex(resolution, orientation);
            // A COC can raise the level count past what the QCD listed; the last entry then serves.
            if (index >= q.Exponents.Length) index = q.Exponents.Length - 1;
            return q.Exponents[index];
        }

        /// <summary>μ<sub>b</sub>: the signalled mantissa, or the LL band's under derived quantisation; 0 without quantisation.</summary>
        internal static int Mantissa(JpxQuantization q, int resolution, int orientation)
        {
            if (q.Style == JpxQuantizationStyle.ScalarDerived) return q.Mantissas[0];
            int index = BandIndex(resolution, orientation);
            if (index >= q.Mantissas.Length) index = q.Mantissas.Length - 1;
            return q.Mantissas[index];
        }

        /// <summary>M<sub>b</sub> = G + ε<sub>b</sub> − 1 (equation E-2): the magnitude bit-planes a coefficient of the band has.</summary>
        internal static int MagnitudeBitPlanes(JpxQuantization q, int levels, int resolution, int orientation) =>
            q.GuardBits + Exponent(q, levels, resolution, orientation) - 1;
    }
}
