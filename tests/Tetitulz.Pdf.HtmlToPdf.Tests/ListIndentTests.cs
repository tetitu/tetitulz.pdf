// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A list's items' text is its start-side padding in — the UA stylesheet's 40px (30pt) unless
    /// the page says otherwise — with the marker before it, and a nested list is that far in from
    /// its item's text. The exporter's own indent was a marker's width, 14pt.
    /// </summary>
    public class ListIndentTests
    {
        private static string Content(string body, string dir = "ltr") =>
            DecodeStreams(ToPdf($"<html dir='{dir}'><body>{body}</body></html>"));

        private static double Margin() => XOf(Content("<p>M</p>"), "M");

        /// <summary>An item's text is 30pt in, and its marker before it.</summary>
        [Fact]
        public void AnItemsTextIsFortyPixelsIn()
        {
            string c = Content("<ol><li>A</li></ol>");
            Assert.Equal(Margin() + 30, XOf(c, "A"), 1);
            Assert.True(XOf(c, "1.") < XOf(c, "A"), "the marker is not before the text");
        }

        /// <summary>A nested list's text is another 30pt in from its item's.</summary>
        [Fact]
        public void ANestedListIsFortyPixelsInFromItsItem()
        {
            string c = Content("<ul><li>A<ul><li>B</li></ul></li></ul>");
            Assert.Equal(XOf(c, "A") + 30, XOf(c, "B"), 1);
        }

        /// <summary>The page's padding replaces the default: 0 puts the text at the edge, 20px 15pt in.</summary>
        [Fact]
        public void TheListsPaddingReplacesTheDefault()
        {
            Assert.Equal(Margin(), XOf(Content("<ul style='padding:0'><li>A</li></ul>"), "A"), 1);
            Assert.Equal(Margin() + 15, XOf(Content("<ul style='padding-left:20px'><li>A</li></ul>"), "A"), 1);
        }

        /// <summary>In a right-to-left page the indent is from the right: the text ends 30pt short of it.</summary>
        [Fact]
        public void ARightToLeftListIsIndentedFromTheRight()
        {
            double paragraph = XOf(Content("<p>A</p>", "rtl"), "A");
            double item = XOf(Content("<ul><li>A</li></ul>", "rtl"), "A");
            Assert.Equal(paragraph - 30, item, 1);
            // The left padding is the end side here: zeroing it leaves the right's default.
            double leftZeroed = XOf(Content("<ul style='padding-left:0'><li>A</li></ul>", "rtl"), "A");
            Assert.Equal(paragraph - 30, leftZeroed, 1);
        }
    }
}
