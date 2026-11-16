// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// HSL and RGB, the model CSS Color 4 §7 defines and DrawingML's <c>hslClr</c> and luminance
    /// modulations use: each channel 0 to 1, the hue in turns (a fraction of the colour wheel). One
    /// implementation, for <see cref="CssColor"/> and the DOCX converter's drawing colours alike.
    /// </summary>
    public static class HslColor
    {
        /// <summary>
        /// The RGB of <paramref name="hueTurns"/>, <paramref name="saturation"/> and
        /// <paramref name="lightness"/>; the hue wraps, so −0.25 and 0.75 are the same.
        /// </summary>
        /// <param name="hueTurns">The hue as a fraction of a turn.</param>
        /// <param name="saturation">0 to 1.</param>
        /// <param name="lightness">0 to 1.</param>
        public static (double R, double G, double B) ToRgb(double hueTurns, double saturation, double lightness)
        {
            if (saturation <= 0) return (lightness, lightness, lightness);
            double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
            double p = 2 * lightness - q;
            return (Channel(p, q, hueTurns + 1.0 / 3), Channel(p, q, hueTurns), Channel(p, q, hueTurns - 1.0 / 3));
        }

        /// <summary>The HSL of an RGB colour, the hue in turns (0 for a grey).</summary>
        /// <param name="r">0 to 1.</param>
        /// <param name="g">0 to 1.</param>
        /// <param name="b">0 to 1.</param>
        public static (double HueTurns, double Saturation, double Lightness) FromRgb(double r, double g, double b)
        {
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double l = (max + min) / 2.0;
            if (Math.Abs(max - min) < 1e-9) return (0, 0, l);

            double d = max - min;
            double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            double h;
            if (max == r) h = ((g - b) / d + (g < b ? 6.0 : 0.0)) / 6.0;
            else if (max == g) h = ((b - r) / d + 2.0) / 6.0;
            else h = ((r - g) / d + 4.0) / 6.0;
            return (h, s, l);
        }

        private static double Channel(double p, double q, double t)
        {
            t = t - Math.Floor(t);
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}
