// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// CSS <c>orphans</c> and <c>widows</c> — the fewest lines of a paragraph a page may end and
    /// begin with — and their initial value, 2, which a browser applies to every paragraph.
    /// </summary>
    /// <remarks>
    /// Page placement is read from drawing order (see the Builder's <c>OrphansWidowsTests</c>): a
    /// line drawn after another and above it is on a later page. The push — a paragraph's top
    /// margin moving the one under test down the page — is found by bisection, not guessed.
    /// </remarks>
    public class OrphansWidowsMappingTests
    {
        private const string Unconstrained = "orphans:1;widows:1";

        private static string Html(double push, string style, int lines, string wrapper = "") =>
            "<p style='margin:0'>TOPX</p>"
            + $"<p style='margin:{push.ToString("0.###", CultureInfo.InvariantCulture)}px 0 0 0'>PUSHX</p>"
            + (wrapper.Length > 0 ? $"<div style='{wrapper}'>" : "")
            + $"<p style='{style}'>" + string.Join("<br>", Enumerable.Range(1, lines).Select(i => $"LINE{i}X")) + "</p>"
            + (wrapper.Length > 0 ? "</div>" : "");

        /// <summary>
        /// How many lines are on the push's page — 0 when the paragraph starts on the next, and -1
        /// when the push itself went over to the next page, which is too far to say anything.
        /// </summary>
        private static int LinesBeforeTheBreak(string html, int lines)
        {
            string c = DecodeStreams(ToPdf(html));
            double Y(string text)
            {
                double y = YOf(c, text);
                Assert.False(double.IsNaN(y), $"{text} was not drawn");
                return y;
            }

            // At or above the first line: the top of the next page, where the first line's y recurs.
            if (Y("PUSHX") >= Y("TOPX")) return -1;
            if (Y("LINE1X") > Y("PUSHX")) return 0;
            for (int i = 1; i < lines; i++)
                if (Y("LINE" + (i + 1) + "X") > Y("LINE" + i + "X")) return i;
            return lines;
        }

        /// <summary>
        /// The push that makes the unconstrained paragraph keep exactly <paramref name="kept"/>
        /// lines on the first page. A push too far to say (-1) counts as too much.
        /// </summary>
        private static double PushLeaving(int kept, int lines)
        {
            double lo = 0, hi = 1100;
            for (int i = 0; i < 40; i++)
            {
                double mid = (lo + hi) / 2;
                int k = LinesBeforeTheBreak(Html(mid, Unconstrained, lines), lines);
                if (k == kept) return mid;
                if (k > kept) lo = mid; else hi = mid;
            }
            Assert.Fail($"no push left {kept} of {lines} lines before the break; the test proves nothing");
            return -1;
        }

        /// <summary>A paragraph that declares nothing keeps two lines together at each side, as in a browser.</summary>
        [Fact]
        public void TheInitialValueIsTwo()
        {
            double one = PushLeaving(kept: 1, lines: 5);
            double four = PushLeaving(kept: 4, lines: 5);

            Assert.Equal(0, LinesBeforeTheBreak(Html(one, "", 5), 5));
            Assert.Equal(3, LinesBeforeTheBreak(Html(four, "", 5), 5));
        }

        [Fact]
        public void DeclaredCountsAreHonoured()
        {
            double three = PushLeaving(kept: 3, lines: 8);

            Assert.Equal(0, LinesBeforeTheBreak(Html(three, "orphans:4", 8), 8));
            Assert.Equal(3, LinesBeforeTheBreak(Html(three, "orphans:3", 8), 8));
            Assert.Equal(2, LinesBeforeTheBreak(Html(three, "widows:6", 8), 8));
        }

        /// <summary>Both inherit, as CSS defines them to: a wrapper's declaration reaches its paragraphs.</summary>
        [Fact]
        public void TheCountsInherit()
        {
            double three = PushLeaving(kept: 3, lines: 8);

            Assert.Equal(0, LinesBeforeTheBreak(Html(three, "", 8, wrapper: "orphans:4"), 8));
            Assert.Equal(2, LinesBeforeTheBreak(Html(three, "", 8, wrapper: "widows:6"), 8));
        }

        /// <summary>
        /// A value that is not a positive integer is ignored and the inherited one stands — here the
        /// initial 2, which moves a paragraph that would leave one line behind.
        /// </summary>
        [Theory]
        [InlineData("orphans:0")]
        [InlineData("orphans:-3")]
        [InlineData("orphans:1.5")]
        [InlineData("orphans:none")]
        public void AnInvalidCountIsIgnored(string style)
        {
            double one = PushLeaving(kept: 1, lines: 5);

            Assert.Equal(0, LinesBeforeTheBreak(Html(one, style, 5), 5));
            // Potency: a valid 1 in the same place does leave the line.
            Assert.Equal(1, LinesBeforeTheBreak(Html(one, "orphans:1", 5), 5));
        }
    }
}
