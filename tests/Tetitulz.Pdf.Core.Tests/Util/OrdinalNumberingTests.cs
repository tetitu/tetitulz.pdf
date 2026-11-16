// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util
{
    /// <summary>
    /// The single Roman-numeral and bijective-letter formatter, which replaced six copies across
    /// <c>DocxToPdf</c> and <c>HtmlToPdf</c> under four names.
    /// </summary>
    /// <remarks>
    /// <b>Two Roman algorithms were merged into one, so the merge is a claim.</b> Two copies were
    /// greedy subtractive and one was a four-array positional lookup. They agree across the whole
    /// range, which was checked before merging rather than assumed — the boundary forms below are
    /// that check, kept.
    /// </remarks>
    public class OrdinalNumberingTests
    {
        /// <summary>The values where the subtractive forms live, which is where the two algorithms could have differed.</summary>
        [Theory]
        [InlineData(1, "I")]
        [InlineData(4, "IV")]
        [InlineData(9, "IX")]
        [InlineData(40, "XL")]
        [InlineData(90, "XC")]
        [InlineData(400, "CD")]
        [InlineData(900, "CM")]
        [InlineData(1990, "MCMXC")]
        [InlineData(3999, "MMMCMXCIX")]
        public void RomanNumeralsUseTheSubtractiveForms(int n, string expected)
        {
            Assert.Equal(expected, OrdinalNumbering.ToRoman(n, upper: true));
            Assert.Equal(expected.ToLowerInvariant(), OrdinalNumbering.ToRoman(n, upper: false));
        }

        /// <summary>
        /// Outside 1…3999 the answer is decimal, not an empty string and not an exception: a list
        /// item still has to be numbered. All six copies agreed on this.
        /// </summary>
        [Theory]
        [InlineData(0, "0")]
        [InlineData(-3, "-3")]
        [InlineData(4000, "4000")]
        [InlineData(int.MaxValue, "2147483647")]
        public void OutOfRangeFallsBackToDecimal(int n, string expected)
        {
            Assert.Equal(expected, OrdinalNumbering.ToRoman(n, upper: true));
            // The decimal string has no letters, so the case flag must leave it alone.
            Assert.Equal(expected, OrdinalNumbering.ToRoman(n, upper: false));
        }

        [Theory]
        [InlineData(1, "a")]
        [InlineData(26, "z")]
        [InlineData(27, "aa")]
        [InlineData(52, "az")]
        [InlineData(53, "ba")]
        [InlineData(703, "aaa")]
        public void LettersAreBijectiveBase26(int n, string expected)
        {
            Assert.Equal(expected, OrdinalNumbering.ToLetters(n, upper: false));
            Assert.Equal(expected.ToUpperInvariant(), OrdinalNumbering.ToLetters(n, upper: true));
        }

        /// <summary>
        /// n = 28 is the value that separates this rule from the PDF page-label rule, and the whole
        /// reason <c>PdfOperations</c>' page-label formatter is deliberately NOT routed here.
        /// </summary>
        /// <remarks>
        /// ISO 32000-1 Table 159 alphabetic page labels are <i>repeated</i> letters — a…z, then
        /// <c>aa</c>, <c>bb</c>, <c>cc</c> — so Table 159 says <c>bb</c> where a list says
        /// <c>ab</c>. They agree at 27 (both <c>aa</c>), which is exactly why folding the two
        /// together looks safe and is not. This case is the tripwire on that.
        /// </remarks>
        [Fact]
        public void At28TheListRuleIsAbWhichIsNotThePageLabelRule()
        {
            Assert.Equal("ab", OrdinalNumbering.ToLetters(28, upper: false));
            Assert.Equal("aa", OrdinalNumbering.ToLetters(27, upper: false));
        }

        [Theory]
        [InlineData(0, "0")]
        [InlineData(-1, "-1")]
        public void NonPositiveLettersFallBackToDecimal(int n, string expected)
        {
            Assert.Equal(expected, OrdinalNumbering.ToLetters(n, upper: false));
        }

        /// <summary>
        /// ECMA-376 §17.18.59 <c>hebrew1</c>: gematria, the final forms ך ם ן ף ץ for 500–900, 15 and 16
        /// written טו and טז (never יה, יו), the thousands digit a letter with no mark; outside 1…9999 decimal.
        /// </summary>
        [Theory]
        [InlineData(1, "\u05D0")]
        [InlineData(10, "\u05D9")]
        [InlineData(11, "\u05D9\u05D0")]
        [InlineData(15, "\u05D8\u05D5")]
        [InlineData(16, "\u05D8\u05D6")]
        [InlineData(17, "\u05D9\u05D6")]
        [InlineData(115, "\u05E7\u05D8\u05D5")]
        [InlineData(500, "\u05DA")]
        [InlineData(900, "\u05E5")]
        [InlineData(5786, "\u05D4\u05DF\u05E4\u05D5")]
        [InlineData(0, "0")]
        [InlineData(10000, "10000")]
        public void Hebrew1IsGematriaWithTheFinalFormsForTheHighHundreds(int n, string expected)
        {
            Assert.Equal(expected, OrdinalNumbering.ToHebrewNumerals(n));
            Assert.Equal(expected, OrdinalNumbering.FormatOoxmlNumber(n, "hebrew1"));
        }

        /// <summary>
        /// ECMA-376 §17.18.59 <c>hebrew2</c>: the 22 letters, then ת repeated before the letter —
        /// 23 is תא (the standard's own example, page 1441 of the 5th edition). Beyond the cap, decimal.
        /// </summary>
        [Theory]
        [InlineData(1, "\u05D0")]
        [InlineData(22, "\u05EA")]
        [InlineData(23, "\u05EA\u05D0")]
        [InlineData(44, "\u05EA\u05EA")]
        [InlineData(45, "\u05EA\u05EA\u05D0")]
        [InlineData(0, "0")]
        public void Hebrew2IsTheAlphabetWithTavRepeated(int n, string expected)
        {
            Assert.Equal(expected, OrdinalNumbering.ToHebrewAlphabet(n));
            Assert.Equal(expected, OrdinalNumbering.FormatOoxmlNumber(n, "hebrew2"));
        }

        /// <summary>The repeat width a file can ask for is capped: the last value in range is 50 letters, the next is decimal.</summary>
        [Fact]
        public void Hebrew2PastItsCapIsDecimal()
        {
            int max = OrdinalNumbering.MaxHebrewAlphabet;
            string last = OrdinalNumbering.ToHebrewAlphabet(max);
            Assert.Equal((max - 1) / 22 + 1, last.Length);
            Assert.All(last, c => Assert.Equal('\u05EA', c));
            Assert.Equal((max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                OrdinalNumbering.ToHebrewAlphabet(max + 1));
        }

        /// <summary>
        /// CSS Counter Styles 3 §6.1 <c>hebrew</c>: additive, so 500 is תק where Word's hebrew1 writes ך;
        /// thousands carry a geresh; the range is 1…10999, decimal outside it.
        /// </summary>
        [Theory]
        [InlineData(1, "\u05D0")]
        [InlineData(15, "\u05D8\u05D5")]
        [InlineData(16, "\u05D8\u05D6")]
        [InlineData(500, "\u05EA\u05E7")]
        [InlineData(900, "\u05EA\u05EA\u05E7")]
        [InlineData(1000, "\u05D0\u05F3")]
        [InlineData(5786, "\u05D4\u05F3\u05EA\u05E9\u05E4\u05D5")]
        [InlineData(10999, "\u05D9\u05F3\u05EA\u05EA\u05E7\u05E6\u05D8")]
        [InlineData(0, "0")]
        [InlineData(11000, "11000")]
        public void CssHebrewIsAdditiveWithAGereshOnTheThousands(int n, string expected)
        {
            Assert.Equal(expected, OrdinalNumbering.ToCssHebrew(n));
        }
    }
}
