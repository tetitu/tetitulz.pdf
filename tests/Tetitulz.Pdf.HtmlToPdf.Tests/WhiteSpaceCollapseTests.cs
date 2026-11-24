// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A collapsible space at the start of a line is removed (CSS Text 3 §4.1.2).
    /// </summary>
    /// <remarks>
    /// Dropping only a whitespace-only node opening a paragraph is not enough: indented source would
    /// draw a space before its first word, and the newline after a closing <c>&lt;br&gt;</c> would
    /// become a space run the layout puts on a line of its own — a form whose every label cell reads
    /// <c>Label&lt;br&gt;</c> would have every row two lines tall.
    /// </remarks>
    public class WhiteSpaceCollapseTests
    {
        private static string Content(string html) => DecodeStreams(ToPdf(html));

        /// <summary>The form's shape: an indented label, a closing br, the indent before the end tag.</summary>
        [Fact]
        public void ACellEndingInABr_IsOneLineTall()
        {
            string br = Content("<table><tr><td>\n        Label<br>\n    </td></tr></table><p>NEXT</p>");
            string plain = Content("<table><tr><td>Label</td></tr></table><p>NEXT</p>");
            // The control: a br with text after it does open a second line.
            string two = Content("<table><tr><td>Label<br>more</td></tr></table><p>NEXT</p>");

            double y = YOf(plain, "NEXT");
            Assert.False(double.IsNaN(y), "the text after the table was not found");
            Assert.Equal(y, YOf(br, "NEXT"), 2);
            Assert.True(YOf(two, "NEXT") < y - 5, "a br followed by text did not add a line");
        }

        [Fact]
        public void IndentedSourceDrawsNoSpaceBeforeTheFirstWord()
        {
            string indented = Content("<p>\n        Hello world</p>");
            string plain = Content("<p>Hello world</p>");

            double x = XOf(plain, "Hello");
            Assert.False(double.IsNaN(x), "the word was not found");
            Assert.Equal(x, XOf(indented, "Hello"), 2);
        }

        [Fact]
        public void TheLineAfterABrStartsAtTheMargin()
        {
            string spaced = Content("<p>one<br>\n        two</p>");
            string plain = Content("<p>one<br>two</p>");

            double margin = XOf(plain, "one");
            Assert.False(double.IsNaN(margin), "the first line was not found");
            Assert.Equal(margin, XOf(plain, "two"), 2);
            Assert.Equal(margin, XOf(spaced, "two"), 2);
        }

        /// <summary>The control: a space between two words on one line is kept.</summary>
        [Fact]
        public void ASpaceWithinTheLineIsKept()
        {
            string spaced = Content("<p>one <b>two</b></p>");
            string glued = Content("<p>one<b>two</b></p>");

            Assert.True(XOf(spaced, "two") > XOf(glued, "two") + 1, "the space between the words was dropped");
        }
    }
}
