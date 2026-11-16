// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.DocxToPdf.Rendering.Charts;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The bounded Excel format-code subset used for chart labels.
    ///
    /// <para>
    /// The behaviour worth pinning is the <b>tick-aware General</b> default: formatting an axis
    /// label with a plain <c>ToString()</c> gives <c>0.30000000000000004</c> on an ordinary 0.1
    /// step, and a fixed rounding gives <c>0 0 0 0</c> on a 0–0.001 axis. Deriving the decimal
    /// count from the step avoids both, and nothing else in the subset matters as much.
    /// </para>
    /// </summary>
    public class ExcelNumberFormatTests
    {
        [Fact]
        public void General_UsesAsManyDecimalsAsTheStepNeeds()
        {
            // A 0.1 step needs one decimal — and must not leak binary-floating-point noise.
            Assert.Equal("0.3", ExcelNumberFormat.General(0.1 + 0.2, 0.1));
        }

        [Fact]
        public void General_KeepsSmallStepsDistinguishable()
        {
            // A 0–0.001 axis rounded to whole numbers would label every tick "0".
            Assert.Equal("0.0005", ExcelNumberFormat.General(0.0005, 0.0005));
        }

        [Fact]
        public void General_UsesNoDecimalsForWholeNumberSteps()
        {
            Assert.Equal("50", ExcelNumberFormat.General(50, 10));
        }

        [Fact]
        public void General_DoesNotEmitNegativeZero()
        {
            Assert.Equal("0", ExcelNumberFormat.General(-0.0001, 1));
        }

        [Theory]
        [InlineData(0.25, "0%", "25%")]
        [InlineData(0.25, "0.0%", "25.0%")]
        [InlineData(0.5, "0%", "50%")]
        public void PercentCodes_ScaleByAHundredAndAppendTheSign(double value, string code, string expected)
        {
            Assert.Equal(expected, ExcelNumberFormat.Format(value, code, 0.1));
        }

        [Theory]
        [InlineData(1234.5, "0.00", "1234.50")]
        [InlineData(1234.5, "0", "1235")]
        [InlineData(0.5, "0.000", "0.500")]
        public void PlaceholderCodes_ControlTheDecimalCount(double value, string code, string expected)
        {
            Assert.Equal(expected, ExcelNumberFormat.Format(value, code, 1));
        }

        /// <summary>
        /// A code's decimal places are the file's — a placeholder each — and stop at Excel's thirty:
        /// "0." and a million zeros wrote a label of two megabytes for every axis label. Thirty are
        /// formatted as written.
        /// </summary>
        [Theory]
        [InlineData(30, 30)]
        [InlineData(31, 30)]
        [InlineData(1_000_000, 30)]
        public void AdversarialDecimalPlaces_StopAtThirty(int zeros, int places)
        {
            string label = ExcelNumberFormat.Format(0.5, "0." + new string('0', zeros), 1);
            Assert.Equal("0." + "5" + new string('0', places - 1), label);
        }

        [Fact]
        public void ThousandsSeparator_IsHonoured()
        {
            Assert.Equal("1,234,568", ExcelNumberFormat.Format(1234567.8, "#,##0", 1));
        }

        [Fact]
        public void ColourAndConditionTags_AreStrippedRatherThanRejected()
        {
            // [Red] carries no meaning in a chart label; failing on it would lose the number too.
            Assert.Equal("42.0", ExcelNumberFormat.Format(42, "[Red]0.0", 1));
            Assert.Equal("42.0", ExcelNumberFormat.Format(42, "[$-409]0.0", 1));
        }

        [Fact]
        public void CurrencyLiterals_SurviveAroundTheNumber()
        {
            Assert.Equal("$42.00", ExcelNumberFormat.Format(42, "\"$\"0.00", 1));
        }

        [Fact]
        public void NegativeSection_IsUsedForNegativeValues()
        {
            // Section 2 carries its own sign, so the value is formatted unsigned inside it.
            Assert.Equal("(5.0)", ExcelNumberFormat.Format(-5, "0.0;(0.0)", 1));
        }

        [Fact]
        public void UnparseableCode_FallsBackToGeneralRatherThanThrowing()
        {
            Assert.Equal("42", ExcelNumberFormat.Format(42, "\"unterminated", 1));
            Assert.Equal("42", ExcelNumberFormat.Format(42, "??!!??", 1));
        }

        [Fact]
        public void NonFiniteValues_FormatAsEmptyRatherThanNaN()
        {
            // "NaN" or "∞" printed on an axis is worse than no label.
            Assert.Equal("", ExcelNumberFormat.Format(double.NaN, "0.0", 1));
            Assert.Equal("", ExcelNumberFormat.Format(double.PositiveInfinity, "0.0", 1));
        }

        [Fact]
        public void DateCodes_FormatTheSerialAsADate()
        {
            // OLE automation date 45000 = 2023-03-15.
            Assert.Equal("2023", ExcelNumberFormat.Format(45000, "yyyy", 1));
        }

        /// <summary>
        /// Excel's <c>m</c> is the month, and the minute only beside an hour or a second (ECMA-376
        /// Part 1 §18.8.31); .NET tells the two by case, so a code passed through as written printed
        /// minute 00 for every month. Serial 44927 is 2023-01-01, 45000 is 2023-03-15; .5 is noon and
        /// .75 six in the evening. A quoted or escaped literal and a separator are written as the code
        /// has them, and a one-letter code is read as the token, not as one of .NET's standard formats.
        /// </summary>
        [Theory]
        [InlineData(44927, "mmm-yy", "Jan-23")]
        [InlineData(44927, "m/d/yyyy", "1/1/2023")]
        [InlineData(45000, "mmmm d, yyyy", "March 15, 2023")]
        [InlineData(45000, "dd.mm.yyyy", "15.03.2023")]
        [InlineData(45000, "yyyy-mm-dd", "2023-03-15")]
        [InlineData(44927.5, "h:mm", "12:00")]
        [InlineData(44927.75, "hh:mm:ss", "18:00:00")]
        [InlineData(44927.75, "h:mm AM/PM", "6:00 PM")]
        [InlineData(44927.75, "mm:ss", "00:00")]
        [InlineData(45000, "d", "15")]
        [InlineData(45000, "d \"of\" mmmm", "15 of March")]
        [InlineData(45000, "d\\.m\\.yyyy", "15.3.2023")]
        public void DateTokens_AreExcelsMonthAndMinute(double serial, string code, string expected)
        {
            Assert.Equal(expected, ExcelNumberFormat.Format(serial, code, 1));
        }

        [Fact]
        public void ANumericCodeIsNotMistakenForADate()
        {
            // The control: "0.00" contains no date token and must stay numeric.
            Assert.Equal("45000.00", ExcelNumberFormat.Format(45000, "0.00", 1));
        }

        /// <summary>
        /// Codes the overnight sweep found untested: scaling commas, a grouping comma anywhere
        /// among the placeholders, the <c>?</c> placeholder, and zero in a two-section code.
        /// </summary>
        [Theory]
        [InlineData(1500000, "0.0,,", "1.5")]          // each trailing comma divides by 1000
        [InlineData(2500, "0,", "3")]                   // ... rounded half away from zero
        [InlineData(1234567, "##,##0", "1,234,567")]    // a grouping comma at an even index
        [InlineData(1234, "0,0", "1,234")]              // a grouping comma before the last digit
        [InlineData(1234, "#,???", "1,234")]            // '?' after the comma: grouping, not scaling
        [InlineData(3.14159, "0.??", "3.14")]           // '?' counts as a decimal place
        [InlineData(0, "0;(0)", "0")]                   // zero takes the first section, not the negative one
        [InlineData(-5, "0;(0)", "(5)")]                // the control: a negative takes the second
        public void LessCommonCodes(double value, string code, string expected)
        {
            Assert.Equal(expected, ExcelNumberFormat.Format(value, code, 1));
        }

        /// <summary>
        /// The trailing scaling commas are removed in one pass, and they are the ones the search it
        /// replaced removed one at a time — kept here as the reference. Random codes of the characters
        /// that matter, at a fixed seed, agree on the code left and the commas counted.
        /// </summary>
        [Fact]
        public void TrailingScalingCommas_AreTheOnesTheRepeatedSearchRemoved()
        {
            char[] tokens = { ',', '0', '#', '?', '.', 'x', '%' };
            var random = new System.Random(20261023);
            for (int n = 0; n < 4000; n++)
            {
                var sb = new System.Text.StringBuilder();
                int length = random.Next(0, 12);
                for (int i = 0; i < length; i++) sb.Append(tokens[random.Next(tokens.Length)]);
                string code = sb.ToString();

                string expected = RepeatedSearch(code, out int expectedCount);
                string actual = ExcelNumberFormat.WithoutTrailingScalingCommas(code, out int actualCount);

                Assert.True(expected == actual && expectedCount == actualCount,
                    "differs on '" + code + "': '" + actual + "' x" + actualCount + ", expected '" + expected + "' x" + expectedCount);
            }
        }

        /// <summary>
        /// A code of a hundred thousand trailing commas is read in one pass. The search removed one comma
        /// and copied the code again each time: some ten gigabytes for this code (Wave 22, DocxToPdf-1:1).
        /// </summary>
        [Fact]
        public void AHundredThousandScalingCommas_AreReadInLinearWork()
        {
            string code = "0" + new string(',', 100_000);

            long before = System.GC.GetAllocatedBytesForCurrentThread();
            string label = ExcelNumberFormat.Format(1e6, code, 1);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal("0", label);
            Assert.True(allocated < 4L * 1024 * 1024, "allocated " + allocated + " bytes");
        }

        /// <summary>The removal this replaced: the last comma before any placeholder from the end, until none.</summary>
        private static string RepeatedSearch(string body, out int count)
        {
            count = 0;
            while (true)
            {
                int idx = -1;
                for (int i = body.Length - 1; i >= 0; i--)
                {
                    char c = body[i];
                    if (c == ',') { idx = i; break; }
                    if (c == '0' || c == '#' || c == '?') break;
                }
                if (idx < 0) return body;
                body = body.Remove(idx, 1);
                count++;
            }
        }
    }
}
