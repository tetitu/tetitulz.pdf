// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// An empty clearing element — the clearfix <c>&lt;div style="clear:both"&gt;&lt;/div&gt;</c> —
    /// produces no block, so its <c>clear</c> had nothing to go on and the text after real floats
    /// went on beside them. It now waits for the next block: a sibling, bare text, or the first
    /// block after the container closes.
    /// </summary>
    public class EmptyClearTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        /// <summary>A float three lines tall with one line beside it.</summary>
        private const string TallFloat =
            "<div style='float:left;width:30%'>AAA<br>A2<br>A3</div><div>beside</div>";

        /// <summary>The control: with no clear, the next block goes on beside the float, above its last line.</summary>
        [Fact]
        public void WithoutAClearTheNextBlockIsBesideTheFloat()
        {
            string c = Content(TallFloat + "<div>NEXT</div>");
            Assert.True(YOf(c, "NEXT") > YOf(c, "A3"), $"NEXT {YOf(c, "NEXT")}, A3 {YOf(c, "A3")}");
        }

        [Fact]
        public void AnEmptyClearPutsTheNextBlockBelowTheFloat()
        {
            string c = Content(TallFloat + "<div style='clear:both'></div><div>NEXT</div>");
            Assert.True(YOf(c, "NEXT") < YOf(c, "A3"), $"NEXT {YOf(c, "NEXT")}, A3 {YOf(c, "A3")}");
        }

        [Fact]
        public void BareTextAfterAnEmptyClearIsBelowTheFloat()
        {
            string c = Content("<div>" + TallFloat + "<div style='clear:both'></div>NEXT</div>");
            Assert.True(YOf(c, "NEXT") < YOf(c, "A3"), $"NEXT {YOf(c, "NEXT")}, A3 {YOf(c, "A3")}");
        }

        /// <summary>
        /// Floats after the clear take it: they start below the floats before it, and the text after
        /// them (bare text, which ends their band) sits beside the new float, not below it — the
        /// clear was about the floats before it, and it is spent.
        /// </summary>
        [Fact]
        public void FloatsAfterTheClearAreNotClearedAgain()
        {
            string c = Content("<div>" + TallFloat + "<div style='clear:both'></div>" +
                               "<div style='float:left;width:30%'>BBB<br>B2<br>B3</div><div>beside2</div>NEXT</div>");
            Assert.True(YOf(c, "BBB") < YOf(c, "A3"), "the second float is below the first");
            Assert.True(YOf(c, "NEXT") > YOf(c, "B3"), $"NEXT {YOf(c, "NEXT")}, B3 {YOf(c, "B3")}");
        }

        /// <summary>The clearfix inside the floats' container clears for the block after the container.</summary>
        [Fact]
        public void AClearAtTheEndOfAContainerClearsTheBlockAfterIt()
        {
            string c = Content("<div>" + TallFloat + "<div style='clear:both'></div></div><div>NEXT</div>");
            Assert.True(YOf(c, "NEXT") < YOf(c, "A3"), $"NEXT {YOf(c, "NEXT")}, A3 {YOf(c, "A3")}");
        }
    }
}
