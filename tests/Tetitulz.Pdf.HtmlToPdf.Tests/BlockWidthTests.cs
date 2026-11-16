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
    /// CSS <c>width</c> on a block: its box is that wide, at its start — the right in a
    /// right-to-left page — under <c>box-sizing</c>'s rule; a float with nothing beside it keeps its
    /// width on its side. Found by a Bootstrap progress bar, drawn across the page where Chrome
    /// draws 60% of it.
    /// </summary>
    /// <remarks>
    /// Read from the box's top border: a 1px border is stroked along its middle, so the rule is the
    /// box's outer width less 0.75pt, from 0.375pt in at each end.
    /// </remarks>
    public class BlockWidthTests
    {
        private static (double Left, double Right) TopRule(string body, string dir = "ltr")
        {
            var c = DecodeStreams(ToPdf($"<html dir='{dir}'><body>{body}</body></html>"));
            var top = StrokedSegments(c).Where(s => s.IsHorizontal).OrderByDescending(s => s.Y0).First();
            return (Math.Min(top.X0, top.X1), Math.Max(top.X0, top.X1));
        }

        private const string Border = "border:1px solid #000";

        /// <summary>The page's content width: a full-width box's rule, plus the 0.75pt it is short of the box.</summary>
        private static double ContentWidth()
        {
            var full = TopRule($"<div style='{Border}'>A</div>");
            return full.Right - full.Left + 0.75;
        }

        /// <summary>
        /// A 50% box is half the width plus its border (content-box, CSS's default), at the left; a
        /// full-width one is the control.
        /// </summary>
        [Fact]
        public void APercentageWidthIsAShareOfTheWidth()
        {
            double w = ContentWidth();
            var full = TopRule($"<div style='{Border}'>A</div>");
            var half = TopRule($"<div style='width:50%;{Border}'>A</div>");

            Assert.Equal(full.Left, half.Left, 2);
            Assert.Equal(0.5 * w + 1.5 - 0.75, half.Right - half.Left, 2);
        }

        /// <summary>Under <c>box-sizing: border-box</c> — Bootstrap's — the width is the whole box's.</summary>
        [Fact]
        public void ABorderBoxWidthIsTheBoxs()
        {
            double w = ContentWidth();
            var half = TopRule($"<div style='width:50%;box-sizing:border-box;{Border}'>A</div>");
            Assert.Equal(0.5 * w - 0.75, half.Right - half.Left, 2);
        }

        /// <summary>A length is a length: 200px of text and 1px of border each side, 151.5pt.</summary>
        [Fact]
        public void ALengthWidthIsThatWide()
        {
            var box = TopRule($"<div style='width:200px;{Border}'>A</div>");
            Assert.Equal(150 + 1.5 - 0.75, box.Right - box.Left, 2);
        }

        /// <summary>In a right-to-left page the narrower box is at the right, where the page starts.</summary>
        [Fact]
        public void ARightToLeftBoxIsAtTheRight()
        {
            var full = TopRule($"<div style='{Border}'>A</div>", "rtl");
            var half = TopRule($"<div style='width:50%;{Border}'>A</div>", "rtl");
            Assert.Equal(full.Right, half.Right, 2);
            Assert.True(half.Left > full.Left + 100, "the narrower box is not at the right");
        }

        /// <summary>A box wider than its room keeps the room: 200% is the full width.</summary>
        [Fact]
        public void ATooWideBoxKeepsTheRoom()
        {
            var full = TopRule($"<div style='{Border}'>A</div>");
            var wide = TopRule($"<div style='width:200%;{Border}'>A</div>");
            Assert.Equal(full.Right - full.Left, wide.Right - wide.Left, 2);
        }

        /// <summary>
        /// A float with nothing beside it keeps its width on its side: floated right in a
        /// left-to-right page, at the right; floated left, at the left.
        /// </summary>
        [Theory]
        [InlineData("right", "ltr")]
        [InlineData("left", "ltr")]
        [InlineData("left", "rtl")]
        public void ALoneFloatKeepsItsWidthOnItsSide(string side, string dir)
        {
            double w = ContentWidth();
            var full = TopRule($"<div style='{Border}'>A</div>", dir);
            var box = TopRule($"<div style='float:{side};width:40%;{Border}'>A</div>", dir);

            Assert.Equal(0.4 * w + 1.5 - 0.75, box.Right - box.Left, 2);
            if (side == "right") Assert.Equal(full.Right, box.Right, 2);
            else Assert.Equal(full.Left, box.Left, 2);
        }

        /// <summary>
        /// A narrow box's lines are measured at its width, so a table row holds all of them: under
        /// a 20% box of many words, the next row starts below its last line, not across it.
        /// </summary>
        [Fact]
        public void ARowHoldsANarrowBoxsWrappedLines()
        {
            var c = DecodeStreams(ToPdf("<html><body><table><tr><td><div style='width:20%'>" +
                "one two three four five six seven eight nine ten eleven twelve Last</div></td></tr>" +
                "<tr><td>Below</td></tr></table></body></html>"));
            Assert.True(YOf(c, "Below") < YOf(c, "Last") - 5, $"the next row is at {YOf(c, "Below")}, the last line at {YOf(c, "Last")}");
        }

        // ------------------------------------------------------------------ a box of blocks

        private const string TwoParagraphs = "<p style='margin:0'>A</p><p style='margin:0'>B</p>";

        /// <summary>
        /// A box of blocks' top rule runs its whole outer width (it is stroked edge to edge, inside
        /// the band), so a full-width box's rule is the content width, and it is the control here.
        /// </summary>
        private static (double Left, double Right) FullBox(string dir = "ltr") =>
            TopRule($"<div style='{Border}'>{TwoParagraphs}</div>", dir);

        /// <summary>A box of several blocks takes its width too: 50% plus its border, at the start.</summary>
        [Fact]
        public void ABoxOfBlocksTakesItsWidth()
        {
            var full = FullBox();
            double w = full.Right - full.Left;
            var box = TopRule($"<div style='width:50%;{Border}'>{TwoParagraphs}</div>");

            Assert.Equal(full.Left, box.Left, 2);
            Assert.Equal(0.5 * w + 1.5, box.Right - box.Left, 2);
        }

        /// <summary>In a right-to-left page a narrower box of blocks is at the right.</summary>
        [Fact]
        public void ARightToLeftBoxOfBlocksIsAtTheRight()
        {
            var full = FullBox("rtl");
            var box = TopRule($"<div style='width:50%;{Border}'>{TwoParagraphs}</div>", "rtl");
            Assert.Equal(full.Right, box.Right, 2);
            Assert.True(box.Left > full.Left + 100, "the narrower box is not at the right");
        }

        /// <summary>A lone floated box of blocks keeps its width on its side: floated right, at the right.</summary>
        [Fact]
        public void ALoneFloatedBoxOfBlocksKeepsItsWidthOnItsSide()
        {
            var full = FullBox();
            double w = full.Right - full.Left;
            var box = TopRule($"<div style='float:right;width:40%;{Border}'>{TwoParagraphs}</div>");
            Assert.Equal(0.4 * w + 1.5, box.Right - box.Left, 2);
            Assert.Equal(full.Right, box.Right, 2);
        }

        /// <summary>
        /// A box of blocks in a table cell takes its width of the cell: 50% of a one-column,
        /// full-width table's unpadded cell is half the page.
        /// </summary>
        [Fact]
        public void ABoxInATableCellTakesItsWidthOfTheCell()
        {
            var full = FullBox();
            double w = full.Right - full.Left;
            var box = TopRule("<table style='width:100%;border-collapse:collapse'><tr><td style='padding:0'>" +
                              $"<div style='width:50%;{Border}'>{TwoParagraphs}</div></td></tr></table>");
            Assert.Equal(0.5 * w + 1.5, box.Right - box.Left, 1);
        }

        /// <summary>
        /// A width alone makes a box: a bordered paragraph inside a 50% div is half the page wide,
        /// where without the div's width it is the whole of it.
        /// </summary>
        [Fact]
        public void AWidthAloneMakesABox()
        {
            double w = ContentWidth();
            var inner = TopRule($"<div style='width:50%'><p style='margin:0;{Border}'>A</p><p style='margin:0'>B</p></div>");
            Assert.Equal(0.5 * w - 0.75, inner.Right - inner.Left, 2);
        }

        /// <summary>In a right-to-left page a narrower box in a table cell is at the cell's right.</summary>
        [Fact]
        public void ARightToLeftBoxInATableCellIsAtItsRight()
        {
            var full = FullBox("rtl");
            var box = TopRule("<table style='width:100%;border-collapse:collapse'><tr><td style='padding:0'>" +
                              $"<div style='width:50%;{Border}'>{TwoParagraphs}</div></td></tr></table>", "rtl");
            Assert.Equal(full.Right, box.Right, 1);
        }
    }
}

