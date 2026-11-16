// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>border-color</c>, <c>border-width</c>, <c>border-style</c> and their per-side forms, and
    /// the <c>transparent</c> colour — applied in cascade order against the shorthands.
    /// </summary>
    /// <remarks>
    /// <para><b>The shape every Bootstrap 3 component has:</b> a base rule
    /// <c>border:1px solid transparent</c> and a variant rule that sets only
    /// <c>border-color</c>. The engine read neither half: <c>transparent</c> was not a colour it
    /// knew, so the shorthand fell through to <b>black</b>, and no longhand was read at all, so the
    /// variant's colour never arrived. Every alert, panel and button came out boxed in black.</para>
    ///
    /// <para>Pure primaries are used so the stroke-colour operator names the edge unambiguously:
    /// <c>1 0 0 RG</c> is red and nothing else, and none of these fixtures draws a red or blue
    /// stroke any other way.</para>
    /// </remarks>
    public class BorderLonghandTests
    {
        private const string Red = "1 0 0 RG";
        private const string Blue = "0 0 1 RG";

        private static string Content(string html) => DecodeStreams(ToPdf(html));

        private static int StrokeCount(string content) => Regex.Matches(content, @"\bS\b").Count;

        // ------------------------------------------------------------------ transparent

        /// <summary>
        /// <c>transparent</c> paints nothing — and a later <c>border-color</c> gives the same edge a
        /// real colour.
        /// </summary>
        [Fact]
        public void ATransparentShorthandPaintsNothing_AndALaterBorderColorPaintsIt()
        {
            const string Css = "<style>.box{border:1px solid transparent} .box.red{border-color:#ff0000}</style>";

            string plain = Content(Css + "<div class='box'>A</div>");
            string coloured = Content(Css + "<div class='box red'>A</div>");

            Assert.Equal(0, StrokeCount(plain));
            Assert.Contains(Red, coloured);
            Assert.True(StrokeCount(coloured) > 0, "the coloured box drew no stroke at all");
        }

        /// <summary>
        /// Order decides: a shorthand AFTER <c>border-color</c> resets the colour, as CSS says.
        /// </summary>
        /// <remarks>
        /// The pair is what makes this a test of order rather than of "longhands win". The same two
        /// declarations are ranked both ways round, and each must end in its own colour.
        /// </remarks>
        [Fact]
        public void TheLaterOfShorthandAndLonghandWins()
        {
            string longhandLast = Content(
                "<style>.box{border:1px solid #0000ff} .box.x{border-color:#ff0000}</style>" +
                "<div class='box x'>A</div>");
            string shorthandLast = Content(
                "<style>.box{border-color:#ff0000} .box.x{border:1px solid #0000ff}</style>" +
                "<div class='box x'>A</div>");

            Assert.Contains(Red, longhandLast);
            Assert.DoesNotContain(Blue, longhandLast);

            Assert.Contains(Blue, shorthandLast);
            Assert.DoesNotContain(Red, shorthandLast);
        }

        /// <summary>
        /// An inline-only document is never folded by the cascade, so <c>!important</c> is still on
        /// the declaration and must be honoured where it stands.
        /// </summary>
        [Fact]
        public void AnImportantLonghandBeatsALaterInlineShorthand()
        {
            string important = Content(
                "<div style='border-color:#ff0000 !important;border:1px solid #0000ff'>A</div>");
            string normal = Content(
                "<div style='border-color:#ff0000;border:1px solid #0000ff'>A</div>");

            Assert.Contains(Red, important);
            Assert.DoesNotContain(Blue, important);

            // The control: without !important the later shorthand wins.
            Assert.Contains(Blue, normal);
            Assert.DoesNotContain(Red, normal);
        }

        // ------------------------------------------------------------------ per-side and box values

        /// <summary>A per-side colour longhand recolours that edge and no other.</summary>
        [Fact]
        public void APerSideColourRecoloursOnlyThatEdge()
        {
            string dec = Content(
                "<div style='border:1px solid #0000ff;border-bottom-color:#ff0000'>A</div>");

            Assert.Contains(Red, dec);
            Assert.Contains(Blue, dec);
        }

        /// <summary>
        /// <c>border-color</c> with two values is top/bottom then left/right, the box order every
        /// 1–4 value property shares.
        /// </summary>
        [Fact]
        public void ATwoValueBorderColorAssignsBothPairs()
        {
            string two = Content("<div style='border:1px solid;border-color:#ff0000 #0000ff'>A</div>");
            string one = Content("<div style='border:1px solid;border-color:#ff0000'>A</div>");

            Assert.Contains(Red, two);
            Assert.Contains(Blue, two);

            // The control: one value colours all four, so no blue.
            Assert.Contains(Red, one);
            Assert.DoesNotContain(Blue, one);
        }

        /// <summary>
        /// A CSS-wide keyword as a side's whole border resets that side to no border.
        /// bootstrap-rtl's <c>border-left: initial</c> on a table's first and last cells read as a
        /// colour keyword that restores the default, and drew a black border.
        /// </summary>
        [Theory]
        [InlineData("initial")]
        [InlineData("unset")]
        [InlineData("inherit")]
        [InlineData("revert")]
        public void ABorderSideResetByAKeywordIsNotDrawn(string keyword)
        {
            string reset = Content($"<div style='border:1px solid #ff0000;border-left:{keyword}'>A</div>");
            string kept = Content("<div style='border:1px solid #ff0000'>A</div>");

            // Three red edges and no black one, where all four were drawn.
            Assert.Equal(3, StrokedSegments(reset).Count);
            Assert.Equal(4, StrokedSegments(kept).Count);
            Assert.DoesNotContain("0 0 0 RG", reset);
            Assert.Contains(Red, reset);
        }

        /// <summary><c>border-style:none</c> after a shorthand removes the border.</summary>
        [Fact]
        public void ALaterBorderStyleNoneRemovesTheBorder()
        {
            string removed = Content("<div style='border:1px solid #ff0000;border-style:none'>A</div>");
            string kept = Content("<div style='border:1px solid #ff0000'>A</div>");

            Assert.Equal(0, StrokeCount(removed));
            Assert.Contains(Red, kept);
        }

        /// <summary>
        /// An explicit <c>none</c> style inside the shorthand suppresses the border, which it did not:
        /// the style word was read and discarded, and the width and colour drew anyway.
        /// </summary>
        [Fact]
        public void ANoneStyleInsideTheShorthandDrawsNothing()
        {
            string none = Content("<div style='border:1px none #ff0000'>A</div>");
            string solid = Content("<div style='border:1px solid #ff0000'>A</div>");

            Assert.DoesNotContain(Red, none);
            Assert.Contains(Red, solid);
        }

        // ------------------------------------------------------------------ table cells

        /// <summary>
        /// The cell path reads the same longhands — Bootstrap's <c>.table-bordered</c> cells and its
        /// contextual rows set their colours this way.
        /// </summary>
        [Fact]
        public void ACellBorderColorAfterATransparentShorthandIsDrawn()
        {
            string dec = Content(
                "<style>td{border:1px solid transparent} td.x{border-color:#ff0000}</style>" +
                "<table><tr><td class='x'>A</td></tr></table>");

            Assert.Contains(Red, dec);
        }

        /// <summary>
        /// A transparent cell edge is a suppressed edge: it must not fall back to the table's grid —
        /// the legacy <c>border="1"</c> one, which rules every cell (a plain table has none).
        /// </summary>
        [Fact]
        public void ATransparentCellBorderSuppressesTheTableGrid()
        {
            string grid = Content("<table border='1' cellspacing='0'><tr><td>A</td></tr></table>");
            string transparent = Content(
                "<table border='1' cellspacing='0'><tr><td style='border:1px solid transparent'>A</td></tr></table>");

            Assert.True(StrokeCount(grid) > 0, "the control table drew no grid, so this proves nothing");
            Assert.Equal(0, StrokeCount(transparent));
        }

        /// <summary>
        /// A colour with no style is not a border: CSS's initial style is <c>none</c>. On a cell that
        /// matters, because an edge the cell does not declare belongs to the table's grid — reading
        /// a colour-only declaration as a suppression would erase that grid.
        /// </summary>
        [Fact]
        public void ACellColourWithNoStyleLeavesTheTableGridAlone()
        {
            string grid = Content("<table border='1'><tr><td>A</td></tr></table>");
            string colourOnly = Content(
                "<table border='1'><tr><td style='border-color:#ff0000'>A</td></tr></table>");

            Assert.Equal(StrokeCount(grid), StrokeCount(colourOnly));
            Assert.DoesNotContain(Red, colourOnly);
        }
    }
}
