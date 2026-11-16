// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging
{
    /// <summary>
    /// The sRGB transfer function, IEC 61966-2-1: linear light to the encoded value and back, each
    /// clipped to 0–1 (NaN as 0). The linear segment's threshold is 0.0031308 one way and
    /// 12.92 × that the other — which is where a hand-written 0.04045 comes from; no 8-bit value lies
    /// between the two.
    /// </summary>
    /// <remarks>
    /// One implementation, for the raster colour model, the colour-space converter, the SVG filters,
    /// the JPEG 2000 extended-colour reader and the PostScript plugin's CIE conversion — copies differ
    /// on clipping and on the decode threshold.
    /// </remarks>
    public static class SrgbTransfer
    {
        /// <summary>A linear-light value, clipped to 0–1, as its sRGB encoding.</summary>
        /// <param name="linear">Linear light; outside 0–1 it is clipped, NaN read as 0.</param>
        public static double Encode(double linear)
        {
            if (double.IsNaN(linear) || linear <= 0) return 0;
            if (linear >= 1) return 1;
            return linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        }

        /// <summary>An sRGB-encoded value, clipped to 0–1, as linear light.</summary>
        /// <param name="encoded">The encoded value; outside 0–1 it is clipped, NaN read as 0.</param>
        public static double Decode(double encoded)
        {
            if (double.IsNaN(encoded) || encoded <= 0) return 0;
            if (encoded >= 1) return 1;
            return encoded <= 0.0031308 * 12.92 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
        }
    }
}
