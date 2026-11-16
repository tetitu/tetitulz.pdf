// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>line-height: normal</c> is 1.15 of the font size, as a browser prints Arial and Times,
    /// with no floor — found against Chrome on an invoice: every line of 13px text advanced 14pt
    /// (the exporter's own floor for its 1.16 default) where Chrome's advances 15px, 11.2pt, so
    /// every table row and box on the page was taller.
    /// </summary>
    public class NormalLineHeightTests
    {
        private static double Gap(string body, string first = "AAA", string second = "BBB")
        {
            string c = DecodeStreams(ToPdf("<html><head><style>body{font-size:13px}</style></head><body>" + body + "</body></html>"));
            return YOf(c, first) - YOf(c, second);
        }

        private const double Normal = 13 * 0.75 * 1.15;   // 11.21pt

        [Fact]
        public void AParagraphsNormalLineIsItsSizeTimesOnePointOneFive()
            => Assert.Equal(Normal, Gap("<p style='margin:0'>AAA<br>BBB</p>"), 2);

        [Fact]
        public void AListItemsNormalLineIsTheSame()
            => Assert.Equal(Normal, Gap("<ul style='margin:0'><li>AAA</li><li>BBB</li></ul>"), 2);

        [Fact]
        public void ATableCellsNormalLineIsTheSame()
            => Assert.Equal(Normal, Gap("<table><tr><td style='padding:0'>AAA<br>BBB</td></tr></table>"), 2);

        /// <summary>A declared line height still wins — 20px is 15pt (the control).</summary>
        [Fact]
        public void ADeclaredLineHeightStillWins()
            => Assert.Equal(15, Gap("<p style='margin:0;line-height:20px'>AAA<br>BBB</p>"), 2);

        /// <summary>A list's marker sits on its text's baseline: the number is drawn in a line of the item's height, not the floored default.</summary>
        [Fact]
        public void AMarkerSharesItsTextsBaseline()
        {
            string c = DecodeStreams(ToPdf("<html><head><style>body{font-size:13px}</style></head><body><ol><li>AAA</li></ol></body></html>"));
            Assert.Equal(YOf(c, "AAA"), YOf(c, "1."), 2);
        }

        /// <summary>A list in a table cell has the same normal line.</summary>
        [Fact]
        public void AListInACellHasTheSameLine()
            => Assert.Equal(Normal, Gap("<table><tr><td style='padding:0'><ul style='margin:0'><li>AAA</li><li>BBB</li></ul></td></tr></table>"), 2);
    }
}
