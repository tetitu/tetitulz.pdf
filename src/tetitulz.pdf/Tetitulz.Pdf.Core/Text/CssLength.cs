// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// A CSS absolute length (CSS Values and Units §6.2) in points or in CSS pixels:
    /// <c>pt</c>, <c>px</c>, <c>pc</c>, <c>in</c>, <c>cm</c>, <c>mm</c> and <c>q</c>. A relative unit
    /// (<c>em</c>, <c>rem</c>, <c>ex</c>, <c>%</c>) is not one, and is refused so the caller resolves
    /// it against its own context.
    /// </summary>
    /// <remarks>
    /// <para><b>One unit table</b>, in points or pixels, for HtmlToPdf's <c>ParseLength</c> and
    /// media-query <c>ParseMediaLength</c> and for <c>SvgToPdfConverter</c> — where cutting a unit's
    /// letters off and keeping the number would make <c>width="10mm"</c> 10 user units, about 3.5 mm
    /// on the page instead of 10.</para>
    /// <para><b>Each form keeps its caller's arithmetic, operation for operation.</b> The points form
    /// multiplies by 72 and then divides by the centimetres or millimetres per inch, and the pixels
    /// form multiplies by 96 and then divides, so every caller's length is bit-for-bit its own
    /// arithmetic's.</para>
    /// <para>The constants are <see cref="PdfUnits"/>'s: 96 CSS pixels to the inch, 72 points, 2.54
    /// centimetres, 25.4 millimetres, 12 points to the pica, and 40 quarter-millimetres to the
    /// centimetre.</para>
    /// </remarks>
    internal static class CssLength
    {
        /// <summary>Quarter-millimetres (the CSS <c>q</c> unit) per inch. Multiplying by 4 is exact.</summary>
        private const double QuarterMillimetresPerInch = PdfUnits.MillimetresPerInch * 4;

        /// <summary>
        /// CSS pixels per pica: exactly 16, as one constant, because the media-query parser this
        /// replaced computed a pica as <c>n * 16</c>, one multiplication and one rounding.
        /// </summary>
        /// <remarks>
        /// Written in the other arms' style, <c>value * 96 / 72 * 12</c>, it rounds three times, and
        /// for some values the result moves: 511.139 pc comes out 8178.223999999999 px where
        /// <c>n * 16</c> gives 8178.224. The constant's own expression, 96 ÷ (72 ÷ 12), has exact
        /// steps, so it is 16.
        /// </remarks>
        private const double CssPixelsPerPica = PdfUnits.CssPixelsPerInch / (PdfUnits.PointsPerInch / PdfUnits.PointsPerPica);

        /// <summary>
        /// <paramref name="value"/> in <paramref name="unit"/> as points; <c>false</c> for a unit
        /// that is not absolute. The unit is matched without regard to case; the empty unit is not
        /// one (a caller that reads a bare number as pixels says so itself).
        /// </summary>
        internal static bool TryToPoints(double value, string unit, out double points)
        {
            switch ((unit ?? string.Empty).ToLowerInvariant())
            {
                case "pt": points = value; return true;
                case "px": points = value * PdfUnits.PxToPoint; return true;
                case "pc": points = value * PdfUnits.PointsPerPica; return true;
                case "in": points = value * PdfUnits.PointsPerInch; return true;
                case "cm": points = value * PdfUnits.PointsPerInch / PdfUnits.CentimetresPerInch; return true;
                case "mm": points = value * PdfUnits.PointsPerInch / PdfUnits.MillimetresPerInch; return true;
                case "q":  points = value * PdfUnits.PointsPerInch / QuarterMillimetresPerInch; return true;
                default:   points = 0; return false;
            }
        }

        /// <summary>
        /// <paramref name="value"/> in <paramref name="unit"/> as CSS pixels; <c>false</c> for a unit
        /// that is not absolute. The unit is matched without regard to case.
        /// </summary>
        internal static bool TryToPixels(double value, string unit, out double pixels)
        {
            switch ((unit ?? string.Empty).ToLowerInvariant())
            {
                case "px": pixels = value; return true;
                case "pt": pixels = value * PdfUnits.CssPixelsPerInch / PdfUnits.PointsPerInch; return true;
                case "pc": pixels = value * CssPixelsPerPica; return true;
                case "in": pixels = value * PdfUnits.CssPixelsPerInch; return true;
                case "cm": pixels = value * PdfUnits.CssPixelsPerInch / PdfUnits.CentimetresPerInch; return true;
                case "mm": pixels = value * PdfUnits.CssPixelsPerInch / PdfUnits.MillimetresPerInch; return true;
                case "q":  pixels = value * PdfUnits.CssPixelsPerInch / QuarterMillimetresPerInch; return true;
                default:   pixels = 0; return false;
            }
        }
    }
}
