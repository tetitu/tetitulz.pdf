// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <see cref="HtmlToPdfOptions.MediaType"/>: which medium's rules apply — <c>@media</c> blocks and
    /// a sheet's own <c>media</c> attribute alike.
    /// </summary>
    /// <remarks>
    /// Each case is asserted both ways: under print one text is drawn and the other is not, and
    /// under screen the reverse — so neither "contains" nor "does not contain" can pass on a render
    /// that lost the text for some other reason.
    /// </remarks>
    public class MediaTypeTests
    {
        private static string Content(string html, HtmlMediaType? medium = null)
        {
            var options = Managed();
            if (medium is { } m) options.MediaType = m;
            return DecodeStreams(ToPdf(html, options));
        }

        private const string MediaBlocks =
            "<style>@media print { .screenonly { display:none } } @media screen { .printonly { display:none } }</style>" +
            "<p class='printonly'>PRINTED</p><p class='screenonly'>SCREENED</p>";

        [Fact]
        public void PrintIsTheDefault()
        {
            string dec = Content(MediaBlocks);

            Assert.Contains("PRINTED", dec);
            Assert.DoesNotContain("SCREENED", dec);
        }

        [Fact]
        public void ScreenAppliesTheScreenRulesAndNotThePrintOnes()
        {
            string dec = Content(MediaBlocks, HtmlMediaType.Screen);

            Assert.Contains("SCREENED", dec);
            Assert.DoesNotContain("PRINTED", dec);
        }

        /// <summary>
        /// A sheet's own <c>media</c> applies it only for that medium. It was not read at all, so a
        /// screen-only sheet styled the print.
        /// </summary>
        [Fact]
        public void ASheetsMediaAttributeDecidesWhetherItApplies()
        {
            const string html =
                "<style media='screen'>.a { display:none }</style>" +
                "<style media='print'>.b { display:none }</style>" +
                "<p class='a'>ALPHA</p><p class='b'>BRAVO</p>";

            string print = Content(html);
            Assert.Contains("ALPHA", print);
            Assert.DoesNotContain("BRAVO", print);

            string screen = Content(html, HtmlMediaType.Screen);
            Assert.Contains("BRAVO", screen);
            Assert.DoesNotContain("ALPHA", screen);
        }
    }
}
