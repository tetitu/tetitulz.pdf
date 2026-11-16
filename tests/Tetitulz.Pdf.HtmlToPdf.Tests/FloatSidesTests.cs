// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A float left and a float right — a logo and a nav, a <c>.pull-left</c> and a
    /// <c>.pull-right</c> — share one line, each at its own edge. A run of floats ended at the first
    /// change of side, and a lone float with nothing beside it is mapped as a block, so the two
    /// were stacked. They are now one run of real floats, each with its own side; only a float the
    /// model cannot float (a table, several blocks) falls back to the old split.
    /// </summary>
    public class FloatSidesTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        private const string LeftAndRight =
            "<div style='float:left;width:30%'>AAA</div><div style='float:right;width:30%'>BBB</div>" +
            "<div style='clear:both'></div><div>NEXT</div>";

        /// <summary>One baseline for both, the right one in the right half; the next block below them.</summary>
        [Fact]
        public void ALeftAndARightFloatShareALine()
        {
            string c = Content(LeftAndRight);
            Assert.Equal(YOf(c, "AAA"), YOf(c, "BBB"), 1);
            Assert.True(XOf(c, "BBB") > XOf(c, "AAA") + 200, $"AAA at {XOf(c, "AAA")}, BBB at {XOf(c, "BBB")}");
            Assert.True(YOf(c, "NEXT") < YOf(c, "AAA") - 1, "the cleared block is below the floats");
        }

        /// <summary>
        /// The right float's box ends at the right margin: a 30% float starts 70% of the way across,
        /// where a left float of 70% (the reference) ends.
        /// </summary>
        [Fact]
        public void TheRightFloatIsAtTheRightEdge()
        {
            string c = Content(LeftAndRight);
            string reference = Content("<div style='float:left;width:70%'>X</div><div style='float:left;width:30%'>BBB</div><div style='clear:both'></div>");
            Assert.Equal(XOf(reference, "BBB"), XOf(c, "BBB"), 1);
        }

        /// <summary>Without widths each is its own text's width: the right one still ends at the right edge, on the left one's line.</summary>
        [Fact]
        public void AutoWidthFloatsShareALineToo()
        {
            string c = Content("<div style='float:left'>Logo</div><div style='float:right'>Nav</div><div style='clear:both'></div>");
            Assert.Equal(YOf(c, "Logo"), YOf(c, "Nav"), 1);
            Assert.True(XOf(c, "Nav") > 400, $"Nav at {XOf(c, "Nav")}");
        }

        /// <summary>A right float that clears is below the left one, not beside it.</summary>
        [Fact]
        public void AnOppositeFloatThatClearsIsBelow()
        {
            string c = Content("<div style='float:left;width:30%'>AAA</div><div style='float:right;width:30%;clear:both'>BBB</div><div style='clear:both'></div>");
            Assert.True(YOf(c, "BBB") < YOf(c, "AAA") - 1, $"AAA {YOf(c, "AAA")}, BBB {YOf(c, "BBB")}");
        }

        /// <summary>A table whose second cell holds only tables nested past the layout's cap: a float whose width nothing says, which the model cannot float.</summary>
        private static readonly string Unmeasurable = "<table style='float:right'><tr><td>BBB</td><td>" + ManagedTestSupport.TablesPastTheNestingCap("") + "</td></tr></table>";

        /// <summary>
        /// A right float the model cannot float — a table with no width, a column of it past the nesting cap —
        /// falls back to the split: the left float a block and the table below it, the control that
        /// the fallback is kept. (A table with a width is a floated box since batch 125:
        /// <c>ATableFloatedRight_SharesTheLeftFloatsLine</c>.)
        /// </summary>
        [Fact]
        public void AFloatTheModelCannotFloatFallsBackToTheSplit()
        {
            string c = Content("<div style='float:left;width:30%'>AAA</div>" + Unmeasurable + "<div style='clear:both'></div>");
            // Half a line at least: a cell's padding alone moves a table's text a point or two.
            Assert.True(YOf(c, "BBB") < YOf(c, "AAA") - 6, $"stacked, as before: AAA {YOf(c, "AAA")}, BBB {YOf(c, "BBB")}");
        }

        /// <summary>
        /// A table floated right beside a left float is a floated box since batch 125: the two share
        /// a line, the table at the right edge. Before, the run fell back to the split and the table
        /// was drawn below.
        /// </summary>
        [Fact]
        public void ATableFloatedRight_SharesTheLeftFloatsLine()
        {
            string c = Content("<div style='float:left;width:30%'>AAA</div>" +
                               "<table style='float:right;width:30%'><tr><td>BBB</td></tr></table><div style='clear:both'></div>");
            Assert.True(Math.Abs(YOf(c, "BBB") - YOf(c, "AAA")) < 6, $"not one line: AAA {YOf(c, "AAA")}, BBB {YOf(c, "BBB")}");
            Assert.True(XOf(c, "BBB") > XOf(c, "AAA") + 200, $"BBB at x={XOf(c, "BBB"):F1} is not at the right edge, AAA at x={XOf(c, "AAA"):F1}");
        }

        /// <summary>
        /// The fallback keeps the whole first run and then the rest: two left floats become a line
        /// of their own, and the table floated right after them — one the model cannot float — is
        /// still drawn, below.
        /// </summary>
        [Fact]
        public void TheFallbackKeepsWhatFollowsTheFirstRun()
        {
            string c = Content("<div style='float:left;width:30%'>AAA</div><div style='float:left;width:30%'>CCC</div>" +
                               Unmeasurable + "after");
            Assert.Equal(YOf(c, "AAA"), YOf(c, "CCC"), 1);
            Assert.True(YOf(c, "BBB") < YOf(c, "AAA") - 6, $"AAA {YOf(c, "AAA")}, BBB {YOf(c, "BBB")}");
        }
    }
}
