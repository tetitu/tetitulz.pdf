// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>display: inline-block</c>: the element's margin box is part of its line. Found against
    /// Chrome on a CV — a wrapped row of tags (<c>padding: 2px 8px; margin: 0 4px 4px 0</c>) sat 13px
    /// apart where Chrome spaces them 20px, since an inline box's vertical padding and margins
    /// overlap the lines round it. Its horizontal margins take room too.
    /// </summary>
    public class InlineBlockTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        private static string Tags(string display)
            => $"<div><span style='display:{display};padding:4px;margin:0 0 8px 0;background:#ccc'>AAA</span><br>" +
               $"<span style='display:{display};padding:4px;margin:0 0 8px 0;background:#ccc'>BBB</span></div>";

        [Fact]
        public void AnInlineBlocksLineHoldsItsMarginBox()
        {
            string inline = Content(Tags("inline")), block = Content(Tags("inline-block"));
            double inlineDrop = YOf(inline, "AAA") - YOf(inline, "BBB");
            double blockDrop = YOf(block, "AAA") - YOf(block, "BBB");
            Assert.True(blockDrop > inlineDrop + 5, $"inline {inlineDrop}, inline-block {blockDrop}");
        }

        /// <summary>A right margin of 20px (15pt) puts the next word 15pt further on than the same box inline.</summary>
        [Fact]
        public void AnInlineBlocksHorizontalMarginTakesRoom()
        {
            string Box(string display) => Content($"<p><span style='display:{display};margin-right:20px;background:#ccc'>AAA</span>BBB</p>");
            double inlineGap = XOf(Box("inline"), "BBB") - XOf(Box("inline"), "AAA");
            double blockGap = XOf(Box("inline-block"), "BBB") - XOf(Box("inline-block"), "AAA");
            Assert.Equal(inlineGap + 15, blockGap, 1);
        }

        /// <summary>An inline-block with margins and nothing to paint still takes its room: a box of its own.</summary>
        [Fact]
        public void AnUndecoratedInlineBlockStillTakesItsMargins()
        {
            string plain = Content("<p><span>AAA</span>BBB</p>");
            string spaced = Content("<p><span style='display:inline-block;margin-left:20px'>AAA</span>BBB</p>");
            Assert.Equal(XOf(plain, "AAA") + 15, XOf(spaced, "AAA"), 1);
        }
    }
}
