// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A bordered block's padding is inside its border: the border sits at the margin edge and the
    /// text a padding in from it.
    /// </summary>
    /// <remarks>
    /// <para>The box used to be drawn a fixed 2pt from the text, so the padding — already in the
    /// text's indent — landed outside it: a <c>padding: 8px</c> notes box had its text against the
    /// border and its last line on the bottom edge, and the border stood 8px in from its column.</para>
    /// <para>The geometry is CSS's exactly since the Bootstrap comparison: the border's band lies
    /// between the margin and the padding (a 1px border is stroked 0.375pt in from the margin edge,
    /// along its middle), the text starts past border and padding, and no padding is no gap — the
    /// 2pt floor a Word paragraph border keeps is not an HTML box's.</para>
    /// </remarks>
    public class BlockPaddingTests
    {
        private static string Content(string html) => DecodeStreams(ToPdf(html));

        /// <summary>A 1px border, 0.75pt: stroked along its middle, half of it in from the edge.</summary>
        private const double HalfBorder = 0.375;

        private static (double Left, double Right, double Bottom, double Top) Edges(string content)
        {
            var segments = StrokedSegments(content);
            var vertical = segments.Where(s => s.IsVertical).Select(s => s.X0).ToList();
            var horizontal = segments.Where(s => s.IsHorizontal).Select(s => s.Y0).ToList();
            Assert.Equal(2, vertical.Count);
            Assert.Equal(2, horizontal.Count);
            return (vertical.Min(), vertical.Max(), horizontal.Min(), horizontal.Max());
        }

        [Fact]
        public void TheBorderSitsAtTheMarginEdge_AndThePaddingIsInsideIt()
        {
            double margin = XOf(Content("<p>Margin</p>"), "Margin");
            string boxed = Content("<div style='border:1px solid #ff0000;padding:12px'>Inside</div>");

            var box = Edges(boxed);
            Assert.Equal(margin, box.Left - HalfBorder, 2);
            // The border's 0.75pt and 12px — 9pt — of padding.
            Assert.Equal(0.75 + 9, XOf(boxed, "Inside") - (box.Left - HalfBorder), 2);
        }

        /// <summary>
        /// Padding moves the text and not the border: on every side the padded box is exactly where
        /// the unpadded one is — at the margin edge — and the text is 9pt further in.
        /// </summary>
        [Fact]
        public void PaddingMovesTheTextAndNotTheBorder()
        {
            string unpaddedContent = Content("<div style='border:1px solid #ff0000'>Inside</div>");
            string paddedContent = Content("<div style='border:1px solid #ff0000;padding:12px'>Inside</div>");
            var unpadded = Edges(unpaddedContent);
            var padded = Edges(paddedContent);

            Assert.Equal(unpadded.Left, padded.Left, 2);
            Assert.Equal(unpadded.Right, padded.Right, 2);
            Assert.Equal(unpadded.Top, padded.Top, 2);
            Assert.Equal(XOf(unpaddedContent, "Inside") + 9, XOf(paddedContent, "Inside"), 2);
        }

        /// <summary>The bottom padding is below the last line, inside the border.</summary>
        [Fact]
        public void TheBottomPaddingIsInsideTheBorder()
        {
            string padded = Content("<div style='border:1px solid #ff0000;padding:12px'>Inside</div>");
            string flush = Content("<div style='border:1px solid #ff0000;padding:12px 12px 0'>Inside</div>");

            double paddedGap = YOf(padded, "Inside") - Edges(padded).Bottom;
            double flushGap = YOf(flush, "Inside") - Edges(flush).Bottom;
            // 12px of padding, 9pt: the flush box has none, not a floor.
            Assert.Equal(9, paddedGap - flushGap, 2);
        }

        /// <summary>A legend is a block: it has its own line, and its border-bottom is a rule under it.</summary>
        [Fact]
        public void ALegendIsABlock()
        {
            string dec = Content("<fieldset><legend style='border-bottom:1px solid #ff0000'>Details</legend>Inner</fieldset>");

            Assert.True(YOf(dec, "Inner") < YOf(dec, "Details") - 5, "the text after the legend shares its line");

            // One horizontal rule and no sides: a paragraph border, not a box around the caption.
            var segments = StrokedSegments(dec);
            Assert.Single(segments, s => s.IsHorizontal);
            Assert.DoesNotContain(segments, s => s.IsVertical);
        }
    }
}
