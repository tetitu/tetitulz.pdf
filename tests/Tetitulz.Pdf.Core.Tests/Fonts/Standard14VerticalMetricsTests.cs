// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// AFM vertical metrics for the Standard-14 fonts.
    ///
    /// <para>
    /// Advance widths answer "how far does the pen move"; stacking a numerator over a denominator
    /// needs "how tall and how deep", which nothing in the repository could previously answer for a
    /// Standard-14 font. These tests pin the published Adobe figures, and — more usefully — pin that
    /// the fonts actually differ from one another, which is what catches a table copy-pasted between
    /// two of them.
    /// </para>
    /// </summary>
    public class Standard14VerticalMetricsTests
    {
        [Fact]
        public void TimesRoman_ReturnsItsPublishedMetrics()
        {
            Assert.True(Standard14Metrics.TryGetVerticalMetrics(StandardFonts.TimesRoman,
                out double asc, out double desc, out double cap, out double x));

            Assert.Equal(683, asc, 3);
            Assert.Equal(-217, desc, 3);
            Assert.Equal(662, cap, 3);
            Assert.Equal(450, x, 3);
        }

        [Fact]
        public void Helvetica_ReturnsItsPublishedMetrics()
        {
            Assert.True(Standard14Metrics.TryGetVerticalMetrics(StandardFonts.Helvetica,
                out double asc, out double desc, out double cap, out double x));

            Assert.Equal(718, asc, 3);
            Assert.Equal(-207, desc, 3);
            Assert.Equal(718, cap, 3);
            Assert.Equal(523, x, 3);
        }

        [Fact]
        public void Courier_ReturnsItsPublishedMetrics()
        {
            Assert.True(Standard14Metrics.TryGetVerticalMetrics(StandardFonts.Courier,
                out double asc, out double desc, out double cap, out double x));

            Assert.Equal(629, asc, 3);
            Assert.Equal(-157, desc, 3);
            Assert.Equal(562, cap, 3);
            Assert.Equal(426, x, 3);
        }

        [Fact]
        public void TheThreeFamilies_HaveDistinctMetrics()
        {
            // The assertion that catches a table copy-pasted from one font to another — which is
            // exactly how a widths table has gone wrong in this repository before.
            Standard14Metrics.TryGetVerticalMetrics(StandardFonts.TimesRoman, out double ta, out _, out _, out double tx);
            Standard14Metrics.TryGetVerticalMetrics(StandardFonts.Helvetica, out double ha, out _, out _, out double hx);
            Standard14Metrics.TryGetVerticalMetrics(StandardFonts.Courier, out double ca, out _, out _, out double cx);

            Assert.NotEqual(ta, ha, 3);
            Assert.NotEqual(ha, ca, 3);
            Assert.NotEqual(tx, hx, 3);
            Assert.NotEqual(hx, cx, 3);
        }

        [Fact]
        public void BoldVariants_DifferFromTheirRegularFace()
        {
            Standard14Metrics.TryGetVerticalMetrics(StandardFonts.TimesRoman, out _, out _, out double capRegular, out _);
            Standard14Metrics.TryGetVerticalMetrics(StandardFonts.TimesBold, out _, out _, out double capBold, out _);

            Assert.NotEqual(capRegular, capBold, 3);
        }

        [Fact]
        public void DescendersAreNegative_SoTheySitBelowTheBaseline()
        {
            // A positive descender would place every stacked box on the wrong side of the baseline.
            foreach (string font in new[]
            {
                StandardFonts.TimesRoman, StandardFonts.Helvetica, StandardFonts.Courier,
                StandardFonts.Symbol, StandardFonts.ZapfDingbats,
            })
            {
                Assert.True(Standard14Metrics.TryGetVerticalMetrics(font, out _, out double desc, out _, out _));
                Assert.True(desc < 0, font + " must report a descender below the baseline");
            }
        }

        [Fact]
        public void AscenderExceedsXHeight_ForEveryStandardFont()
        {
            foreach (string font in new[]
            {
                StandardFonts.TimesRoman, StandardFonts.TimesBold, StandardFonts.TimesItalic,
                StandardFonts.Helvetica, StandardFonts.HelveticaBold,
                StandardFonts.Courier, StandardFonts.CourierBold,
            })
            {
                Assert.True(Standard14Metrics.TryGetVerticalMetrics(font,
                    out double asc, out _, out double cap, out double x));
                Assert.True(asc > x, font + ": the ascender must exceed the x-height");
                Assert.True(cap > x, font + ": the cap height must exceed the x-height");
            }
        }

        [Fact]
        public void ANonStandardFont_ReportsThatItHasNoBuiltInMetrics()
        {
            Assert.False(Standard14Metrics.TryGetVerticalMetrics("NotAFont",
                out double asc, out double desc, out double cap, out double x));

            // And leaves the outputs at zero rather than at some other font's values.
            Assert.Equal(0, asc, 6);
            Assert.Equal(0, desc, 6);
            Assert.Equal(0, cap, 6);
            Assert.Equal(0, x, 6);
        }

        [Fact]
        public void ANullFontName_IsRejectedRatherThanThrowing()
        {
            Assert.False(Standard14Metrics.TryGetVerticalMetrics(null, out _, out _, out _, out _));
        }

        /// <summary>
        /// Every face's four values. The twelve Latin rows are the Adobe AFMs' <c>Ascender</c>,
        /// <c>Descender</c>, <c>CapHeight</c> and <c>XHeight</c>; the Symbol and ZapfDingbats rows
        /// are a characterisation of what this library returns (FontBBox top and bottom, and
        /// placeholder heights — those AFMs carry none of the four keys).
        /// </summary>
        [Theory]
        [InlineData(StandardFonts.Courier, 629, -157, 562, 426)]
        [InlineData(StandardFonts.CourierOblique, 629, -157, 562, 426)]
        [InlineData(StandardFonts.CourierBold, 629, -157, 562, 439)]
        [InlineData(StandardFonts.CourierBoldOblique, 629, -157, 562, 439)]
        [InlineData(StandardFonts.Helvetica, 718, -207, 718, 523)]
        [InlineData(StandardFonts.HelveticaOblique, 718, -207, 718, 523)]
        [InlineData(StandardFonts.HelveticaBold, 718, -207, 718, 532)]
        [InlineData(StandardFonts.HelveticaBoldOblique, 718, -207, 718, 532)]
        [InlineData(StandardFonts.TimesRoman, 683, -217, 662, 450)]
        [InlineData(StandardFonts.TimesBold, 683, -217, 676, 461)]
        [InlineData(StandardFonts.TimesItalic, 683, -217, 653, 441)]
        [InlineData(StandardFonts.TimesBoldItalic, 683, -217, 669, 462)]
        [InlineData(StandardFonts.Symbol, 1010, -293, 700, 520)]
        [InlineData(StandardFonts.ZapfDingbats, 820, -143, 700, 520)]
        public void EveryFace_ReturnsItsFourValues(string font, double ascender, double descender, double capHeight, double xHeight)
        {
            Assert.True(Standard14Metrics.TryGetVerticalMetrics(font, out double a, out double d, out double c, out double x));
            Assert.Equal((ascender, descender, capHeight, xHeight), (a, d, c, x));
        }

        /// <summary>
        /// Courier is monospaced, so a character outside its table still advances 600, in every
        /// Courier face; a proportional face has no such fallback and answers 0.
        /// </summary>
        [Theory]
        [InlineData(StandardFonts.Courier)]
        [InlineData(StandardFonts.CourierBoldOblique)]
        public void Courier_AdvancesItsFixedWidthForACharacterOutsideItsTable(string font)
        {
            Assert.True(Standard14Metrics.TryGetWidthTable(font, out _, out double missing));
            Assert.Equal(600, missing);
            Assert.Equal(600, Standard14Metrics.GetWidthByUnicode(font, 'א'));
            Assert.Equal(0, Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, 'א'));
        }
    }
}
