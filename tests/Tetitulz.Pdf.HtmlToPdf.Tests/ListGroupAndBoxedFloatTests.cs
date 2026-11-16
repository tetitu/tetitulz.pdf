// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Bootstrap's <c>.list-group</c> as Chrome draws it: a list whose items are blocks is a stack of
    /// boxes, and a badge floated in a boxed item is inside its padding and painted over its
    /// background — where the badge had vanished under the item's white fill.
    /// </summary>
    public class ListGroupAndBoxedFloatTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        private const string Item = "display:block;padding:10px 15px;border:1px solid #000";

        /// <summary>
        /// A list whose items are displayed as blocks draws each item's box — eight rules for two
        /// bordered items — and puts the text past its border and padding; a plain list draws none
        /// (the control).
        /// </summary>
        [Fact]
        public void BlockItemsAreBoxes()
        {
            string boxed = Content($"<ul style='padding:0;margin:0'><li style='{Item}'>A</li><li style='{Item}'>B</li></ul>");
            string plain = Content("<ul style='padding:0;margin:0'><li>A</li><li>B</li></ul>");

            Assert.Equal(8, StrokedSegments(boxed).Count);
            Assert.Empty(StrokedSegments(plain));
            double margin = XOf(Content("<p>M</p>"), "M");
            Assert.Equal(margin + 0.75 + 11.25, XOf(boxed, "A"), 1);
        }

        /// <summary>
        /// A float in a boxed paragraph is inside the box's border and padding — not at the page
        /// margin — and is painted after the box's background, so the background cannot cover it.
        /// </summary>
        [Fact]
        public void AFloatInABoxedParagraphIsInsideItAndOverItsBackground()
        {
            string c = Content("<div style='padding:10px 15px;background-color:#ffffff;border:1px solid #ddd'>" +
                               "A <span style='float:left'>N</span></div>");
            double margin = XOf(Content("<p>M</p>"), "M");

            Assert.Equal(margin + 0.75 + 11.25, XOf(c, "N"), 1);
            int fill = c.IndexOf("1 1 1 rg", StringComparison.Ordinal);
            int badge = c.IndexOf("(N) Tj", StringComparison.Ordinal);
            Assert.True(fill >= 0 && badge > fill, $"the background is painted at {fill}, the float at {badge}");
        }

        /// <summary>
        /// A boxed paragraph that lifts a float keeps its negative margin on the box it moves to:
        /// items overlapping by 1px (-0.75pt) are 0.75pt closer than items with none.
        /// </summary>
        [Fact]
        public void AMovedBoxKeepsItsNegativeMargin()
        {
            static string Items(string margin) =>
                $"<div style='padding:10px;border:1px solid #000;margin-bottom:{margin}'>A <span style='float:left'>N</span></div>" +
                "<div style='padding:10px;border:1px solid #000'>B</div>";
            static double Gap(string c) => YOf(c, "A") - YOf(c, "B");

            Assert.Equal(Gap(Content(Items("0"))) - 0.75, Gap(Content(Items("-1px"))), 2);
        }

        /// <summary>
        /// A float lifted out of a thickly bordered, padded paragraph leaves the paragraph's own text
        /// where it is without it: the box moved round both keeps the border and padding the text
        /// sat inside.
        /// </summary>
        [Fact]
        public void LiftingAFloatDoesNotMoveItsHostsText()
        {
            const string Box = "border:4px solid #000;padding:10px";
            string with = Content($"<div style='{Box}'>A <span style='float:right'>N</span></div>");
            string without = Content($"<div style='{Box}'>A</div>");

            Assert.Equal(XOf(without, "A"), XOf(with, "A"), 2);
            Assert.Equal(YOf(without, "A"), YOf(with, "A"), 2);
        }
    }
}
