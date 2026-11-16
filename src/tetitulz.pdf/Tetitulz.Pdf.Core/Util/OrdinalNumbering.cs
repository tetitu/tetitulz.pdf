// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// The two ordinal formats a numbered list uses: Roman numerals and bijective base-26 letters.
    ///
    /// <para>Six copies existed — <c>DocxToPdf</c>'s <c>DocxRenderContext</c> and
    /// <c>ListRenderer</c>, and <c>HtmlToPdf</c>'s list mapper — under four names
    /// (<c>ToRoman</c>, <c>ToLetter</c>, <c>ToLetters</c>, <c>ToAlpha</c>) and two Roman algorithms
    /// that agree on every value in range.</para>
    /// </summary>
    /// <remarks>
    /// <b>The PDF page-label formats are NOT these, and must not be routed here.</b>
    /// <c>PdfOperations.FacadeCompleteness</c> implements ISO 32000-1 Table 159, whose alphabetic
    /// labels are <i>repeated</i> letters — a…z, then <c>aa</c>, <c>bb</c>, <c>cc</c> — not
    /// bijective base-26. They agree at n = 27 (both <c>aa</c>), which is exactly why the
    /// difference is easy to miss; they diverge at n = 28, where Table 159 says <c>bb</c> and a list
    /// says <c>ab</c>. Its Roman keeps writing <c>m</c> past 3 999, as viewers do, where this one
    /// turns decimal; both of its formats are bounded instead by <c>PdfLimits.MaxPageLabelLength</c>,
    /// because the repeated-letter length grows with the number itself. Folding it in here would
    /// silently change every alphabetic page label from 28 onward. If a shared page-label formatter is ever
    /// wanted it belongs beside these under its own name, never as the same function.
    ///
    /// <para><b>No static fields</b> — the tables are locals, so the type has no initializer and
    /// cannot draw a caller into an initialization cycle.</para>
    /// </remarks>
    public static class OrdinalNumbering
    {
        /// <summary>Above this, Roman numerals need overbars, so the caller gets decimal instead.</summary>
        private const int MaxRoman = 3999;

        /// <summary>
        /// <paramref name="n"/> as a Roman numeral, or as an invariant decimal string when it is
        /// outside 1…<see cref="MaxRoman"/>.
        /// </summary>
        /// <remarks>
        /// The out-of-range fallback is decimal rather than an empty string or an exception because
        /// a list item must still be numbered; all six copies agreed on that, and the decimal string
        /// has no letters, so <paramref name="upper"/> leaves it alone either way.
        /// </remarks>
        public static string ToRoman(int n, bool upper)
        {
            if (n <= 0 || n > MaxRoman) return n.ToString(CultureInfo.InvariantCulture);

            int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] symbols = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };

            var sb = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
                while (n >= values[i]) { sb.Append(symbols[i]); n -= values[i]; }

            string s = sb.ToString();
            return upper ? s : s.ToLowerInvariant();
        }

        /// <summary>
        /// <paramref name="n"/> as bijective base-26 letters — 1 → <c>a</c>, 26 → <c>z</c>,
        /// 27 → <c>aa</c>, 28 → <c>ab</c> — or as an invariant decimal string for n ≤ 0.
        /// </summary>
        /// <remarks>
        /// <b>Bijective, not the repeated-letter rule.</b> See the remarks on the type: the PDF
        /// page-label alphabet is a different function that happens to agree at 27.
        /// </remarks>
        public static string ToLetters(int n, bool upper)
        {
            if (n <= 0) return n.ToString(CultureInfo.InvariantCulture);

            char baseChar = upper ? 'A' : 'a';
            var sb = new StringBuilder();
            while (n > 0)
            {
                n--;
                sb.Insert(0, (char)(baseChar + n % 26));
                n /= 26;
            }
            return sb.ToString();
        }

        /// <summary>Above this, <see cref="ToHebrewAlphabet"/> gives decimal: its length grows with the value.</summary>
        public const int MaxHebrewAlphabet = 1100;

        /// <summary>
        /// <paramref name="n"/> in OOXML's <c>hebrew1</c>, "Hebrew Letters" (ECMA-376 Part 1, 5th ed.,
        /// §17.18.59): the thousands digit as its letter (א–ט, no geresh), the hundreds as ק ר ש ת and
        /// then the final forms ך ם ן ף ץ for 500–900, a remainder of 15 or 16 as טו or טז, else the tens
        /// as י–צ and the ones as א–ט — or an invariant decimal string outside 1–9999, which the
        /// clause's single thousands digit does not reach. In logical order, first letter first.
        /// </summary>
        /// <remarks>
        /// The clause's step 6 allows a group spelling a word to be rearranged; nothing is rearranged
        /// here beyond its own 15 and 16, as Word's is not either for the values a list reaches.
        /// </remarks>
        public static string ToHebrewNumerals(int n)
        {
            if (n <= 0 || n > 9999) return n.ToString(CultureInfo.InvariantCulture);

            const string ones = "\u05D0\u05D1\u05D2\u05D3\u05D4\u05D5\u05D6\u05D7\u05D8";
            const string tens = "\u05D9\u05DB\u05DC\u05DE\u05E0\u05E1\u05E2\u05E4\u05E6";
            const string hundreds = "\u05E7\u05E8\u05E9\u05EA\u05DA\u05DD\u05DF\u05E3\u05E5";

            var sb = new StringBuilder();
            if (n >= 1000) sb.Append(ones[n / 1000 - 1]);
            if (n / 100 % 10 > 0) sb.Append(hundreds[n / 100 % 10 - 1]);

            int rest = n % 100;
            if (rest == 15) sb.Append("\u05D8\u05D5");
            else if (rest == 16) sb.Append("\u05D8\u05D6");
            else
            {
                if (rest / 10 > 0) sb.Append(tens[rest / 10 - 1]);
                if (rest % 10 > 0) sb.Append(ones[rest % 10 - 1]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="n"/> in OOXML's <c>hebrew2</c>, "Hebrew Alphabet" (ECMA-376 Part 1, 5th ed.,
        /// §17.18.59): 1–22 as the 22 letters א…ת; past 22, the letter of what is left after taking 22
        /// away as often as it goes, with a ת for each time — on its right as printed, so first in
        /// reading order: 23 is תא, 45 is תתא. An invariant decimal string for n ≤ 0 or past
        /// <see cref="MaxHebrewAlphabet"/>, since a file chooses the value and the ת repeat with it.
        /// </summary>
        /// <remarks>
        /// <b>The order was read from the clause's printed example</b>, not its wording: "to the right
        /// of the first symbol" in right-to-left text could be either. The example shows 23 with א on
        /// the left and ת on the right — read right to left, ת then א — and the same page's
        /// <c>hebrew1</c> example shows 17 as י on the right, ז on the left, which is the gematria יז.
        /// </remarks>
        public static string ToHebrewAlphabet(int n)
        {
            if (n <= 0 || n > MaxHebrewAlphabet) return n.ToString(CultureInfo.InvariantCulture);

            const string letters =
                "\u05D0\u05D1\u05D2\u05D3\u05D4\u05D5\u05D6\u05D7\u05D8\u05D9\u05DB" +
                "\u05DC\u05DE\u05E0\u05E1\u05E2\u05E4\u05E6\u05E7\u05E8\u05E9\u05EA";
            int times = (n - 1) / 22;
            return new string('\u05EA', times) + letters[n - 22 * times - 1];
        }

        /// <summary>
        /// <paramref name="n"/> in CSS's predefined <c>hebrew</c> counter style (CSS Counter Styles
        /// Level 3 §6.1): additive over its own table — the thousands as a letter and a geresh (׳),
        /// the hundreds as ת repeated then ק ר ש (500 is תק, unlike Word's ך), 15 and 16 as טו and טז —
        /// over the range 1–10 999, outside which the style falls back to decimal.
        /// </summary>
        public static string ToCssHebrew(int n)
        {
            if (n < 1 || n > 10999) return n.ToString(CultureInfo.InvariantCulture);

            int[] weights =
            {
                10000, 9000, 8000, 7000, 6000, 5000, 4000, 3000, 2000, 1000,
                400, 300, 200, 100, 90, 80, 70, 60, 50, 40, 30, 20,
                19, 18, 17, 16, 15, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1,
            };
            string[] symbols =
            {
                "\u05D9\u05F3", "\u05D8\u05F3", "\u05D7\u05F3", "\u05D6\u05F3", "\u05D5\u05F3",
                "\u05D4\u05F3", "\u05D3\u05F3", "\u05D2\u05F3", "\u05D1\u05F3", "\u05D0\u05F3",
                "\u05EA", "\u05E9", "\u05E8", "\u05E7", "\u05E6", "\u05E4", "\u05E2", "\u05E1", "\u05E0",
                "\u05DE", "\u05DC", "\u05DB",
                "\u05D9\u05D8", "\u05D9\u05D7", "\u05D9\u05D6", "\u05D8\u05D6", "\u05D8\u05D5",
                "\u05D9", "\u05D8", "\u05D7", "\u05D6", "\u05D5", "\u05D4", "\u05D3", "\u05D2", "\u05D1", "\u05D0",
            };

            // §3.1.6's additive algorithm: each weight as often as it fits, largest first.
            var sb = new StringBuilder();
            for (int i = 0; i < weights.Length && n > 0; i++)
                for (; n >= weights[i]; n -= weights[i])
                    sb.Append(symbols[i]);
            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="n"/> in one of OOXML's page-number formats — <c>upperRoman</c>,
        /// <c>lowerRoman</c>, <c>upperLetter</c>, <c>lowerLetter</c>, <c>hebrew1</c>, <c>hebrew2</c> and
        /// <c>decimal</c> — as <c>w:pgNumType/@w:fmt</c> spells them. Any other value, including
        /// <see langword="null"/>, is decimal.
        /// </summary>
        /// <remarks>
        /// <para><b>The vocabulary is the duplicated part, not the arithmetic.</b> The Builder's PDF
        /// exporter and <c>DocxToPdf</c>'s legacy renderer both keyed these five strings onto
        /// <see cref="ToRoman"/> and <see cref="ToLetters"/>, in switches whose agreement was
        /// asserted by a comment. It lives here, beside the two primitives it is written in terms
        /// of, because both features reach <c>Core</c> and neither may reference the other.</para>
        ///
        /// <para><b>These are NOT the PDF page-label styles</b> — see the remarks on this type.
        /// Table 159 spells its styles <c>/R</c>, <c>/r</c>, <c>/A</c>, <c>/a</c>, <c>/D</c> and its
        /// alphabet is repeated letters, so the match here is <b>exact and case-sensitive</b>: a
        /// Table 159 style falls to decimal rather than being honoured as the wrong alphabet.</para>
        /// </remarks>
        public static string FormatOoxmlNumber(int n, string format)
        {
            switch (format)
            {
                case "upperRoman": return ToRoman(n, upper: true);
                case "lowerRoman": return ToRoman(n, upper: false);
                case "upperLetter": return ToLetters(n, upper: true);
                case "lowerLetter": return ToLetters(n, upper: false);
                case "hebrew1": return ToHebrewNumerals(n);
                case "hebrew2": return ToHebrewAlphabet(n);
                default: return n.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
