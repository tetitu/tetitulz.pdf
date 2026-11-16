// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A profile's black point, as black-point compensation needs it — Adobe's method
    /// (source and destination detection,
    /// each answer an L* with a* = b* = 0).
    /// </summary>
    /// <remarks>
    /// <para><b>Checked against an independent colour engine, not taken on trust.</b> It is the
    /// oracle on both sides: rendering through RSWOP as an output profile (<c>IccOutputTransformTests</c>)
    /// and rendering an <c>/ICCBased</c> RSWOP colour to sRGB (<c>IccSourceBlackPointTests</c>), each with
    /// compensation on and off.</para>
    ///
    /// <para><b>The source rule.</b> For relative colorimetric from a CMYK printer profile, the
    /// perceptual round trip of L* 0 — Lab through <c>B2A0</c> and back through <c>A2B1</c>
    /// (<c>BlackPointUsingPerceptualBlack</c>; the round trip goes out at the
    /// intent and comes back colorimetrically, so the perceptual black is measured as it prints); otherwise every colorant at its darkest through the
    /// intent's <c>A2B</c> (<c>BlackPointAsDarkerColorant</c>). Either is clipped to L* 50.</para>
    ///
    /// <para><b>The destination rule</b> adds, for relative colorimetric, the refinement in
    /// <see cref="TryDetectDestination"/>.</para>
    /// </remarks>
    internal static class IccBlackPoint
    {
        /// <summary>
        /// A source profile's black point L* for <paramref name="intent"/>, or 0 when there is none to
        /// compensate (absolute colorimetric, a table that cannot be evaluated raw).
        /// </summary>
        /// <param name="profile">The profile's bytes — read only for the perceptual round trip's <c>B2A0</c>.</param>
        /// <param name="deviceClass">The header's device class.</param>
        /// <param name="dataSpace">The header's data colour space.</param>
        /// <param name="aToB">The profile's <c>A2B</c> table for an intent.</param>
        /// <param name="intent">The intent the colour is converted at.</param>
        internal static double Source(byte[] profile, string deviceClass, string dataSpace,
            Func<IccRenderingIntent, IccLut> aToB, IccRenderingIntent intent)
        {
            if (intent == IccRenderingIntent.AbsoluteColorimetric) return 0;
            double l;
            if (intent == IccRenderingIntent.MediaRelativeColorimetric && deviceClass == "prtr" && dataSpace == "CMYK")
            {
                IccLut b2a0 = IccProfile.ParseB2A(profile, out int channels, out _, IccRenderingIntent.Perceptual);
                if (!TryRoundTripL(b2a0, channels, aToB(IccRenderingIntent.MediaRelativeColorimetric), 0, out l)) return 0;
            }
            else if (!TryDarkestColorantL(aToB(intent), dataSpace, out l)) return 0;
            return Math.Max(0, Math.Min(l, 50));
        }

        /// <summary>
        /// An output profile's black point L* for <paramref name="intent"/>, as Adobe's
        /// destination detection finds it.
        /// </summary>
        /// <remarks>
        /// The estimate is <see cref="Source"/>'s. Only relative colorimetric refines it: round-trip a
        /// neutral L* ramp, 0 to 100 in 256 steps, through the relative tables; make the result
        /// monotonic from the top (steps 254 down to 1); where the midrange is nearly
        /// straight — every step either within the lowest fifth or within 4 L* of where it started —
        /// the estimate stands; otherwise the black point is the root of a least-squares quadratic
        /// through the steps whose normalised result lies in [0.1, 0.5), clamped to [0, 50].
        /// </remarks>
        internal static bool TryDetectDestination(byte[] profile, IccRenderingIntent intent, out double l)
        {
            l = 0;
            if (!IccProfile.TryReadHeader(profile, out IccProfile.IccHeader header, out _)) return false;
            IccProfile parsed = IccProfile.Parse(profile);
            if (parsed.Model != IccModel.LutAToB) return false;

            double initial = Source(profile, header.DeviceClass, header.DataColorSpace, parsed.LutFor, intent);
            if (intent != IccRenderingIntent.MediaRelativeColorimetric) { l = initial; return true; }

            IccLut bToA = IccProfile.ParseB2A(profile, out int channels, out _, intent);
            IccLut aToB = parsed.LutFor(IccRenderingIntent.MediaRelativeColorimetric);
            var inRamp = new double[256];
            var outRamp = new double[256];
            for (int i = 0; i < 256; i++)
            {
                inRamp[i] = i * 100.0 / 255.0;
                if (!TryRoundTripL(bToA, channels, aToB, inRamp[i], out outRamp[i])) return false;
            }
            for (int i = 254; i > 0; i--) outRamp[i] = Math.Min(outRamp[i], outRamp[i + 1]);
            if (!(outRamp[0] < outRamp[255])) return false;

            double minL = outRamp[0], maxL = outRamp[255];
            bool straight = true;
            for (int i = 0; i < 256; i++)
                if (!(inRamp[i] <= minL + 0.2 * (maxL - minL) || Math.Abs(inRamp[i] - outRamp[i]) < 4.0))
                    straight = false;
            if (straight) { l = initial; return true; }

            var xs = new double[256];
            var ys = new double[256];
            int n = 0;
            for (int i = 0; i < 256; i++)
            {
                double y = (outRamp[i] - minL) / (maxL - minL);
                if (y >= 0.1 && y < 0.5) { xs[n] = inRamp[i]; ys[n] = y; n++; }
            }
            if (n < 3) return false;

            l = Math.Max(0, RootOfQuadraticFit(n, xs, ys));
            return true;
        }

        /// <summary>L* in → device through <paramref name="bToA"/> (the intent's) → back to L* through <paramref name="aToB"/> (always the relative colorimetric table), neutral (a = b = 0).</summary>
        private static bool TryRoundTripL(IccLut bToA, int channels, IccLut aToB, double lIn, out double lOut)
        {
            lOut = 0;
            if (bToA == null || aToB == null || !aToB.IsLabPcs) return false;
            var pcs = new float[3];
            bToA.EncodeLab(lIn, 0, 0, pcs);
            var device = new double[channels];
            if (!bToA.TryEvaluateRaw(pcs, device)) return false;
            return TryDeviceToL(aToB, device, out lOut);
        }

        /// <summary>Every colorant at its darkest through <paramref name="aToB"/>, as L*.</summary>
        private static bool TryDarkestColorantL(IccLut aToB, string dataSpace, out double l)
        {
            l = 0;
            if (aToB == null || !aToB.IsLabPcs) return false;
            var device = new double[aToB.InputChannels];
            // CMYK and grey: full ink is the darkest; RGB: zero light is.
            double full = dataSpace == "RGB " ? 0.0 : 1.0;
            for (int i = 0; i < device.Length; i++) device[i] = full;
            return TryDeviceToL(aToB, device, out l);
        }

        private static bool TryDeviceToL(IccLut aToB, double[] device, out double l)
        {
            l = 0;
            var input = new float[device.Length];
            for (int i = 0; i < device.Length; i++) input[i] = (float)device[i];
            var pcs = new double[Math.Max(3, aToB.OutputChannels)];
            if (!aToB.TryEvaluateRaw(input, pcs)) return false;
            aToB.DecodeLab(pcs[0], pcs[1], pcs[2], out l, out _, out _);
            return true;
        }

        /// <summary>
        /// The vertex root of the least-squares quadratic <c>y = a·x² + b·x + c</c> through the points,
        /// clamped to [0, 50]: <c>(−b + √d) / 2a</c>, or for a line <c>min(0, max(50,
        /// −c/b))</c> — 0; 0 when there is no real root or fewer than four points.
        /// </summary>
        internal static double RootOfQuadraticFit(int n, double[] x, double[] y)
        {
            if (n < 4) return 0;
            double sx = 0, sx2 = 0, sx3 = 0, sx4 = 0, sy = 0, syx = 0, syx2 = 0;
            for (int i = 0; i < n; i++)
            {
                double xn = x[i], yn = y[i], x2 = xn * xn;
                sx += xn; sx2 += x2; sx3 += x2 * xn; sx4 += x2 * x2;
                sy += yn; syx += yn * xn; syx2 += yn * x2;
            }

            // Normal equations [n sx sx2; sx sx2 sx3; sx2 sx3 sx4]·[c b a] = [sy syx syx2].
            if (!Solve3(new[,] { { n, sx, sx2 }, { sx, sx2, sx3 }, { sx2, sx3, sx4 } },
                        new[] { sy, syx, syx2 }, out double[] r)) return 0;
            double c = r[0], b = r[1], a = r[2];

            if (Math.Abs(a) < 1.0E-10) return Math.Min(0, Math.Max(50, -c / b));
            double d = b * b - 4.0 * a * c;
            if (d <= 0) return 0;
            return Math.Max(0, Math.Min(50, (-b + Math.Sqrt(d)) / (2.0 * a)));
        }

        private static bool Solve3(double[,] m, double[] v, out double[] r)
        {
            r = null;
            double det = Det(m[0, 0], m[0, 1], m[0, 2], m[1, 0], m[1, 1], m[1, 2], m[2, 0], m[2, 1], m[2, 2]);
            if (Math.Abs(det) < 1e-300) return false;
            r = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double[,] t = (double[,])m.Clone();
                for (int i = 0; i < 3; i++) t[i, k] = v[i];
                r[k] = Det(t[0, 0], t[0, 1], t[0, 2], t[1, 0], t[1, 1], t[1, 2], t[2, 0], t[2, 1], t[2, 2]) / det;
            }
            return true;
        }

        private static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i)
            => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
    }
}
