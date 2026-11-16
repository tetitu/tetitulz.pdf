// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Measures text in the PDF standard-14 fonts (Adobe Core AFM metrics, units of 1/1000 em),
    /// for line wrapping and centre/right alignment, so the layout matches the widths the PDF
    /// viewer actually advances by rather than a single average-character estimate.
    ///
    /// <para><b>The widths themselves live in <c>Core.Fonts.Standard14Metrics</c></b>, which is
    /// keyed by Unicode and covers the whole Latin-1 and typographic range. The ASCII tables below
    /// are only the fallback for a code point Core does not know — see
    /// <see cref="MeasureText"/> for what consulting them alone would cost.</para>
    /// </summary>
    internal static class StandardFontMetrics
    {
        private const int First = 32; // space
        private const int Last = 126; // ~
        private const double Em = PdfUnits.GlyphSpaceUnitsPerEm;

        // Helvetica (= Helvetica-Oblique)
        private static readonly int[] Helv =
        {
            278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,
            556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,
            1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,
            667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,
            333,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,
            556,556,333,500,278,556,500,722,500,500,500,334,260,334,584
        };

        // Helvetica-Bold (= Helvetica-BoldOblique)
        private static readonly int[] HelvB =
        {
            278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,
            556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
            975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,
            667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
            333,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,
            611,611,389,556,333,611,556,778,556,556,500,389,280,389,584
        };

        // Times-Roman
        private static readonly int[] Times =
        {
            250,333,408,500,500,833,778,180,333,333,500,564,250,333,250,278,
            500,500,500,500,500,500,500,500,500,500,278,278,564,564,564,444,
            921,722,667,667,722,611,556,722,722,333,389,722,611,889,722,722,
            556,722,667,556,611,722,722,944,722,722,611,333,278,333,469,500,
            333,444,500,444,500,444,333,500,500,278,278,500,278,778,500,500,
            500,500,333,389,278,500,500,722,500,500,444,480,200,480,541
        };

        // Times-Bold
        private static readonly int[] TimesB =
        {
            250,333,555,500,500,1000,833,278,333,333,500,570,250,333,250,278,
            500,500,500,500,500,500,500,500,500,500,333,333,570,570,570,500,
            930,722,667,722,722,667,611,778,778,389,500,778,667,944,722,778,
            611,778,722,556,667,722,722,1000,722,722,667,333,278,333,581,500,
            333,500,556,444,556,444,333,500,556,278,333,556,278,833,556,500,
            556,556,444,389,333,556,500,722,500,500,444,394,220,394,520
        };

        // Times-Italic
        private static readonly int[] TimesI =
        {
            250,333,420,500,500,833,778,214,333,333,500,675,250,333,250,278,
            500,500,500,500,500,500,500,500,500,500,333,333,675,675,675,500,
            920,611,611,667,722,611,611,722,722,333,444,667,556,833,667,722,
            611,722,611,500,556,722,611,833,611,556,556,389,278,389,422,500,
            333,500,500,444,500,444,278,500,500,278,278,444,278,722,500,500,
            500,500,389,389,278,500,444,667,444,444,389,400,275,400,541
        };

        // Times-BoldItalic
        private static readonly int[] TimesBI =
        {
            250,389,555,500,500,833,778,278,333,333,500,570,250,333,250,278,
            500,500,500,500,500,500,500,500,500,500,333,333,570,570,570,500,
            832,667,667,667,722,667,667,722,778,389,500,667,611,889,722,722,
            611,722,667,556,611,722,667,889,667,611,611,333,278,333,570,500,
            333,500,500,444,500,444,333,500,556,278,278,500,278,778,556,500,
            500,500,389,389,278,556,444,667,500,444,389,348,220,348,570
        };

        private static readonly Dictionary<string, int[]> Tables = Build();

        private static Dictionary<string, int[]> Build()
        {
            const int expected = Last - First + 1; // 95
            foreach (var t in new[] { Helv, HelvB, Times, TimesB, TimesI, TimesBI })
                if (t.Length != expected)
                    throw new InvalidOperationException($"Width table length {t.Length} != {expected}");

            return new Dictionary<string, int[]>(StringComparer.Ordinal)
            {
                [StandardFonts.Helvetica] = Helv,
                [StandardFonts.HelveticaOblique] = Helv,
                [StandardFonts.HelveticaBold] = HelvB,
                [StandardFonts.HelveticaBoldOblique] = HelvB,
                [StandardFonts.TimesRoman] = Times,
                [StandardFonts.TimesBold] = TimesB,
                [StandardFonts.TimesItalic] = TimesI,
                [StandardFonts.TimesBoldItalic] = TimesBI,
            };
        }

        /// <summary>Advance width of <paramref name="text"/> in points at the given size.</summary>
        /// <remarks>
        /// <para><b>Widths come from <see cref="Standard14Metrics"/>, which is keyed by Unicode and
        /// covers the whole Latin-1 and typographic range.</b> The ASCII-only table below is the
        /// fallback for a code point Core does not know, and nothing else.</para>
        ///
        /// <para><b>Why.</b> Consulting an ASCII table for codes 32–126 and charging a flat
        /// <c>500</c> for everything else is a single average-character estimate for every character
        /// outside ASCII. Measured in Helvetica: an em dash is 1000 and would be measured 500 (50%
        /// short), a right single quote is 222 and would be measured 500 (125% over), <c>©</c> is
        /// 737 (32% short), and every accented Latin-1 letter is 556 (10% short). This is the code that
        /// decides where lines wrap and where centred and right-aligned text starts, so those errors
        /// land directly in the layout of any document containing a smart quote or an accent —
        /// which is most real Word documents.</para>
        /// </remarks>
        internal static double MeasureText(string text, string pdfFontName, double fontSize)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            // Courier and its variants are monospaced at 600/1000.
            if (pdfFontName != null && pdfFontName.StartsWith(StandardFonts.Courier, StringComparison.Ordinal))
                return text.Length * 0.6 * fontSize;

            string font = Standard14Metrics.IsStandard14(pdfFontName)
                ? pdfFontName
                : StandardFonts.Helvetica;   // unchanged: an unknown face is measured as Helvetica

            if (!Tables.TryGetValue(font, out var fallback))
                fallback = Helv;

            double total = 0;
            foreach (char c in text)
            {
                double w = Standard14Metrics.GetWidthByUnicode(font, c);
                if (w > 0) { total += w; continue; }

                // Core returned nothing for this code point. Keep the historical behaviour rather
                // than charging 0, which would measure an unknown glyph as taking no space at all.
                total += c >= First && c <= Last ? fallback[c - First] : 500;
            }
            return total / Em * fontSize;
        }
    }
}
