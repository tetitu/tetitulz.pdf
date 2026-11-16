// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Unit conversions used across the PDF, DOCX and HTML paths.
    ///
    /// <para>Named rather than written as literals at the call site — <c>/ 20.0</c> (twips),
    /// <c>/ 1000.0</c> (glyph space), <c>/ 72f</c> (points per inch) — so one conversion is not
    /// spelled two different ways.</para>
    /// </summary>
    public static class PdfUnits
    {
        /// <summary>
        /// PostScript points per inch. A PDF user-space unit is 1/72 inch by default, so this is
        /// also the default user-space units per inch.
        /// </summary>
        public const float PointsPerInch = 72f;

        /// <summary>
        /// Glyph-space units per em for Type 1, TrueType and CFF fonts: widths in a PDF font
        /// dictionary are expressed in 1/1000 of the text-space unit.
        /// </summary>
        public const double GlyphSpaceUnitsPerEm = 1000.0;

        /// <summary>
        /// Reciprocal of <see cref="GlyphSpaceUnitsPerEm"/>, for hot paths that would otherwise
        /// divide per glyph.
        ///
        /// <para><b>Not a drop-in replacement for dividing.</b> This is a <c>float</c>, and
        /// <c>x * 0.001f</c> is not bit-identical to <c>x / 1000.0</c> in <c>double</c>. Use it
        /// only where a float scale factor was already what the call site wanted (a glyph-outline
        /// or text matrix); keep <see cref="GlyphSpaceUnitsPerEm"/> where widths are accumulated
        /// in double.</para>
        /// </summary>
        public const float GlyphSpaceScale = 0.001f;

        /// <summary>Twentieths of a point — the OOXML <c>w:sz</c>/<c>w:ind</c> measurement unit.</summary>
        public const double TwipsPerPoint = 20.0;

        /// <summary>Half-points — the OOXML font-size unit (<c>w:sz val="24"</c> is 12&#160;pt).</summary>
        public const double HalfPointsPerPoint = 2.0;

        /// <summary>Eighths of a point — the OOXML table/paragraph border-width unit.</summary>
        public const double BorderEighthsPerPoint = 8.0;

        /// <summary>English Metric Units per inch, as defined by OOXML DrawingML.</summary>
        public const double EmuPerInch = 914400.0;

        /// <summary>English Metric Units per point (<c>914400 / 72</c>).</summary>
        public const double EmuPerPoint = EmuPerInch / PointsPerInch;

        /// <summary>
        /// The CSS reference resolution. A CSS <c>px</c> is 1/96 inch, which is what makes
        /// <see cref="PxToPoint"/> exactly 0.75.
        /// </summary>
        public const double CssPixelsPerInch = 96.0;

        /// <summary>Points per CSS pixel at <see cref="CssPixelsPerInch"/> (i.e. 0.75).</summary>
        public const double PxToPoint = PointsPerInch / CssPixelsPerInch;

        /// <summary>Millimetres per inch, for CSS <c>mm</c> lengths.</summary>
        public const double MillimetresPerInch = 25.4;

        /// <summary>Centimetres per inch, for CSS <c>cm</c> lengths.</summary>
        public const double CentimetresPerInch = 2.54;

        /// <summary>Points per CSS <c>pc</c> (pica).</summary>
        public const double PointsPerPica = 12.0;

        /// <summary>Converts twentieths of a point to points.</summary>
        public static double TwipsToPoints(double twips) => twips / TwipsPerPoint;

        /// <summary>Converts points to twentieths of a point.</summary>
        public static double PointsToTwips(double points) => points * TwipsPerPoint;

        /// <summary>Converts an OOXML half-point font size to points.</summary>
        public static double HalfPointsToPoints(double halfPoints) => halfPoints / HalfPointsPerPoint;

        /// <summary>Converts English Metric Units to points.</summary>
        public static double EmuToPoints(double emu) => emu / EmuPerPoint;

        /// <summary>Converts points to English Metric Units.</summary>
        public static double PointsToEmu(double points) => points * EmuPerPoint;

        /// <summary>Converts CSS pixels to points at the 96&#160;dpi CSS reference resolution.</summary>
        public static double PixelsToPoints(double px) => px * PxToPoint;

        /// <summary>Converts inches to points.</summary>
        public static double InchesToPoints(double inches) => inches * PointsPerInch;

        /// <summary>Converts millimetres to points.</summary>
        public static double MillimetresToPoints(double mm) => mm / MillimetresPerInch * PointsPerInch;

        /// <summary>Converts centimetres to points.</summary>
        public static double CentimetresToPoints(double cm) => cm / CentimetresPerInch * PointsPerInch;

        /// <summary>Scale factor mapping PDF user space to a raster target at the given dpi.</summary>
        public static float DpiScale(float dpi) => dpi / PointsPerInch;
    }
}
