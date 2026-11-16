// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The three JPX-baseline colour spaces a PDF may carry beside sRGB, greyscale, sYCC, CIELab
    /// and CMYK (T.801 Table M.25; ISO 32000-2 §7.4.9), each to 8-bit sRGB: <b>e-sRGB</b> and
    /// <b>e-sYCC</b> (PIMA 7667: sRGB's nonlinear values over an extended range, offset and
    /// scaled into the code values; Annex B's luma-chroma form of the same) and <b>ROMM-RGB</b>
    /// (ISO 22028-2: a 1.8-gamma encoding of linear ROMM primaries under D50, to XYZ, to D65 by
    /// a Bradford adaptation, to linear sRGB by IEC 61966-2-1's matrix, then its curve). The
    /// results are clipped to sRGB's range: every one of these spaces is wider than sRGB, and a
    /// PDF viewer paints sRGB. One implementation, applied where sYCC is, so none of the three
    /// is painted as sRGB or sYCC untransformed.
    /// </summary>
    internal static class Jp2ExtendedColour
    {
        /// <summary>
        /// e-sRGB (PIMA 7667 §4.4.3, equation 3): the code value of an n-bit channel is
        /// R'·255·2^(n−9) + 2^(n−2) + 2^(n−3), R' the sRGB nonlinear value — so 10-bit 384 is 0,
        /// 639 is 0.5 and 894 is 1; 12-bit 1536 is 0 and 3576 is 1; 16-bit 24576 is 0. Values
        /// below 0 and above 1 are the extended range, clipped here.
        /// </summary>
        internal static double ESRgbNonlinear(double code, int depth) =>
            (code - (Math.Pow(2, depth - 2) + Math.Pow(2, depth - 3))) / (255.0 * Math.Pow(2, depth - 9));

        /// <summary>An e-sRGB code value as an 8-bit sRGB sample.</summary>
        internal static byte ESRgbToSRgb8(double code, int depth) => Clip8(ESRgbNonlinear(code, depth));

        /// <summary>
        /// e-sYCC (PIMA 7667 Annex B, equations B7–B9): Y' = Y_code / (2^m − 1); Cb' and Cr'
        /// centred at 2^(m−1) over the same range; then R' = Y' + 2.804 Cr', G' = Y' − 0.6882 Cb'
        /// − 1.4282 Cr', B' = Y' + 3.544 Cb' + 0.0003 Cr' — sRGB nonlinear values over the
        /// extended range, clipped. A neutral (Y' 0.5, centred chroma) is mid grey.
        /// </summary>
        internal static void ESYccToSRgb8(double yCode, double cbCode, double crCode, int yDepth, int cbDepth, int crDepth,
            out byte r, out byte g, out byte b)
        {
            double y = yCode / (Math.Pow(2, yDepth) - 1);
            double cb = (cbCode - Math.Pow(2, cbDepth - 1)) / (Math.Pow(2, cbDepth) - 1);
            double cr = (crCode - Math.Pow(2, crDepth - 1)) / (Math.Pow(2, crDepth) - 1);
            r = Clip8(y + 2.804 * cr);
            g = Clip8(y - 0.6882 * cb - 1.4282 * cr);
            b = Clip8(y + 3.544 * cb + 0.0003 * cr);
        }

        /// <summary>
        /// ROMM-RGB (ISO 22028-2): V = code / (2^n − 1); linear X = V / 16 below 1/32 (the
        /// inverse of the 16·X toe below E_t = 1/512), else V^1.8; XYZ under D50 by the ROMM
        /// matrix; D50 to D65 by Bradford; linear sRGB by IEC 61966-2-1's matrix; the sRGB curve;
        /// clipped. ROMM white is sRGB white within one level, ROMM mid grey (V 0.5, linear 0.287)
        /// is sRGB 146, and ROMM red is outside sRGB's gamut and clips to (255, 0, 0).
        /// </summary>
        internal static void RommRgbToSRgb8(double rCode, double gCode, double bCode, int depth, out byte r, out byte g, out byte b)
        {
            double max = Math.Pow(2, depth) - 1;
            double lr = RommLinear(rCode / max), lg = RommLinear(gCode / max), lb = RommLinear(bCode / max);
            // ISO 22028-2 Table 4: ROMM-RGB to XYZ (D50).
            double x = 0.7977 * lr + 0.1352 * lg + 0.0313 * lb;
            double y = 0.2880 * lr + 0.7119 * lg + 0.0001 * lb;
            double z = 0.0000 * lr + 0.0000 * lg + 0.8249 * lb;
            // Bradford chromatic adaptation, D50 to D65.
            double x65 = 0.9555766 * x - 0.0230393 * y + 0.0631636 * z;
            double y65 = -0.0282895 * x + 1.0099416 * y + 0.0210077 * z;
            double z65 = 0.0122982 * x - 0.0204830 * y + 1.3299098 * z;
            // IEC 61966-2-1: XYZ (D65) to linear sRGB.
            double sr = 3.2406 * x65 - 1.5372 * y65 - 0.4986 * z65;
            double sg = -0.9689 * x65 + 1.8758 * y65 + 0.0415 * z65;
            double sb = 0.0557 * x65 - 0.2040 * y65 + 1.0570 * z65;
            r = Clip8(SRgbCurve(sr));
            g = Clip8(SRgbCurve(sg));
            b = Clip8(SRgbCurve(sb));
        }

        private static double RommLinear(double v) => v < 1.0 / 32 ? v / 16 : Math.Pow(v, 1.8);

        /// <summary>IEC 61966-2-1's transfer curve on a linear value clipped to 0–1.</summary>
        private static double SRgbCurve(double c) => SrgbTransfer.Encode(c);

        /// <summary>A nonlinear sRGB value clipped to 0–1 and rounded to its 8-bit sample.</summary>
        internal static byte Clip8(double nonlinear)
        {
            if (!(nonlinear > 0)) return 0;
            if (nonlinear >= 1) return 255;
            return SaturatingCast.ToByte(Math.Round(nonlinear * 255, MidpointRounding.AwayFromZero));
        }
    }
}
