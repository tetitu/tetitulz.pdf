// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Blend-mode names for the ExtGState <c>/BM</c> entry
    /// (PDF 32000-1:2008, §11.3.5 "Blend Mode").
    ///
    /// <para>The Skia blend-mode table, the SVG <c>mix-blend-mode</c> table and the producers that
    /// set a mode all share this list.</para>
    /// </summary>
    public static class PdfBlendModes
    {
        // ---- Separable blend modes (§11.3.5 "Blend Mode") ----
        /// <summary><c>/Normal</c> — the default; the backdrop is ignored.</summary>
        public const string Normal = "Normal";
        /// <summary><c>/Compatible</c> — a PDF 1.3 alias for <see cref="Normal"/>.</summary>
        public const string Compatible = "Compatible";
        /// <summary><c>/Multiply</c></summary>
        public const string Multiply = "Multiply";
        /// <summary><c>/Screen</c></summary>
        public const string Screen = "Screen";
        /// <summary><c>/Overlay</c></summary>
        public const string Overlay = "Overlay";
        /// <summary><c>/Darken</c></summary>
        public const string Darken = "Darken";
        /// <summary><c>/Lighten</c></summary>
        public const string Lighten = "Lighten";
        /// <summary><c>/ColorDodge</c></summary>
        public const string ColorDodge = "ColorDodge";
        /// <summary><c>/ColorBurn</c></summary>
        public const string ColorBurn = "ColorBurn";
        /// <summary><c>/HardLight</c></summary>
        public const string HardLight = "HardLight";
        /// <summary><c>/SoftLight</c></summary>
        public const string SoftLight = "SoftLight";
        /// <summary><c>/Difference</c></summary>
        public const string Difference = "Difference";
        /// <summary><c>/Exclusion</c></summary>
        public const string Exclusion = "Exclusion";

        // ---- Non-separable blend modes (§11.3.5 "Blend Mode") ----
        /// <summary><c>/Hue</c></summary>
        public const string Hue = "Hue";
        /// <summary><c>/Saturation</c></summary>
        public const string Saturation = "Saturation";
        /// <summary><c>/Color</c></summary>
        public const string Color = "Color";
        /// <summary><c>/Luminosity</c></summary>
        public const string Luminosity = "Luminosity";

        /// <summary>
        /// Whether <paramref name="name"/> is one of the standard blend modes (ISO 32000-1 Tables 136
        /// and 137, ISO 32000-2 Tables 134 and 135) or the <c>/Compatible</c> alias — what a renderer
        /// recognises in a <c>/BM</c> array, and what PDF/A-2 to -4 permit.
        /// </summary>
        public static bool IsStandard(string name)
            => name switch
            {
                Normal or Compatible or Multiply or Screen or Overlay or Darken or Lighten or
                ColorDodge or ColorBurn or HardLight or SoftLight or Difference or Exclusion or
                Hue or Saturation or Color or Luminosity => true,
                _ => false,
            };
    }

    /// <summary>
    /// Base-14 standard font names (PDF 32000-1:2008, §9.6.2 "Type 1 Fonts") plus the two abbreviations that
    /// real-world files use for the symbolic faces.
    ///
    /// <para>These names appeared as bare literals 26 times across nine files, with four separate
    /// metric tables keyed off them.</para>
    /// </summary>
    public static class StandardFonts
    {
        /// <summary><c>Helvetica</c></summary>
        public const string Helvetica = "Helvetica";
        /// <summary><c>Helvetica-Bold</c></summary>
        public const string HelveticaBold = "Helvetica-Bold";
        /// <summary><c>Helvetica-Oblique</c></summary>
        public const string HelveticaOblique = "Helvetica-Oblique";
        /// <summary><c>Helvetica-BoldOblique</c></summary>
        public const string HelveticaBoldOblique = "Helvetica-BoldOblique";

        /// <summary><c>Times-Roman</c></summary>
        public const string TimesRoman = "Times-Roman";
        /// <summary><c>Times-Bold</c></summary>
        public const string TimesBold = "Times-Bold";
        /// <summary><c>Times-Italic</c></summary>
        public const string TimesItalic = "Times-Italic";
        /// <summary><c>Times-BoldItalic</c></summary>
        public const string TimesBoldItalic = "Times-BoldItalic";

        /// <summary><c>Courier</c></summary>
        public const string Courier = "Courier";
        /// <summary><c>Courier-Bold</c></summary>
        public const string CourierBold = "Courier-Bold";
        /// <summary><c>Courier-Oblique</c></summary>
        public const string CourierOblique = "Courier-Oblique";
        /// <summary><c>Courier-BoldOblique</c></summary>
        public const string CourierBoldOblique = "Courier-BoldOblique";

        /// <summary><c>Symbol</c></summary>
        public const string Symbol = "Symbol";
        /// <summary><c>ZapfDingbats</c></summary>
        public const string ZapfDingbats = "ZapfDingbats";
        /// <summary><c>ZaDb</c> — the abbreviation for ZapfDingbats used in form field <c>/DA</c> strings.</summary>
        public const string ZapfDingbatsAbbrev = "ZaDb";
        /// <summary><c>Helv</c> — the abbreviation for Helvetica used in form field <c>/DA</c> strings.</summary>
        public const string HelveticaAbbrev = "Helv";
    }
}
