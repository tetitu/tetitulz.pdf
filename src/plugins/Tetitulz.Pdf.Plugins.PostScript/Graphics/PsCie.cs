// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// The colorimetry a CIE-based colour needs here: CIE 1976 L*a*b* to and from XYZ against a white
    /// point (CIE 15:2004 §8.2.1), and XYZ to sRGB (IEC 61966-2-1) for what <c>currentrgbcolor</c> and
    /// its kin read back. What is painted is the colour in its own space, written as PDF writes it; this
    /// answers only the program's questions about it, and the device colour a space past its cap is
    /// painted in.
    /// </summary>
    internal static class PsCie
    {
        /// <summary>The D50 white (ICC.1 PCS), where a space names none usable.</summary>
        public static readonly double[] D50 = { 0.9642, 1.0, 0.8249 };

        private const double Delta = 6.0 / 29.0;

        /// <summary><c>L*a*b*</c> as XYZ relative to <paramref name="white"/>.</summary>
        public static (double X, double Y, double Z) LabToXyz(double l, double a, double b, double[] white)
        {
            double fy = (l + 16) / 116, fx = fy + a / 500, fz = fy - b / 200;
            return (white[0] * Inverse(fx), white[1] * Inverse(fy), white[2] * Inverse(fz));
        }

        /// <summary>XYZ as <c>L*a*b*</c> relative to <paramref name="white"/>.</summary>
        public static (double L, double A, double B) XyzToLab(double x, double y, double z, double[] white)
        {
            double fx = Forward(x / white[0]), fy = Forward(y / white[1]), fz = Forward(z / white[2]);
            return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
        }

        /// <summary>
        /// XYZ relative to <paramref name="white"/> as sRGB components in [0, 1]: the white scaled onto
        /// D65 channel by channel (a von Kries adaptation in XYZ, enough for a read-back), then
        /// IEC 61966-2-1's matrix and transfer.
        /// </summary>
        public static (double R, double G, double B) XyzToRgb(double x, double y, double z, double[] white)
        {
            x *= 0.95047 / white[0]; y *= 1.0 / white[1]; z *= 1.08883 / white[2];
            double r = 3.2406 * x - 1.5372 * y - 0.4986 * z;
            double g = -0.9689 * x + 1.8758 * y + 0.0415 * z;
            double bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
            return (Encode(r), Encode(g), Encode(bl));
        }

        private static double Forward(double t) => t > Delta * Delta * Delta ? Math.Pow(t, 1.0 / 3) : t / (3 * Delta * Delta) + 4.0 / 29;

        private static double Inverse(double t) => t > Delta ? t * t * t : 3 * Delta * Delta * (t - 4.0 / 29);

        private static double Encode(double linear) => Core.Imaging.SrgbTransfer.Encode(linear);
    }
}
