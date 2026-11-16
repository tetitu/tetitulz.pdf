// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A block element whose content is blocks draws its own margins, border, padding and
    /// background round them — a Bootstrap <c>.panel</c>, <c>.panel-body</c>, <c>.well</c>, and the
    /// <c>.container</c> / <c>.row</c> pair whose padding and negative margin line a grid up.
    /// </summary>
    /// <remarks>
    /// All of it used to be dropped: only a paragraph could carry a border or padding, so such an
    /// element's blocks printed with no frame and at the page's margin.
    /// </remarks>
    public class BlockBoxMappingTests
    {
        private static string Content(string html) => DecodeStreams(ToPdf(html));

        private static double Margin() => XOf(Content("<p>Margin</p>"), "Margin");

        [Fact]
        public void ABorderedContainerFramesItsParagraphs()
        {
            string framed = Content(
                "<div style='border:1px solid #ff0000;padding:12px'><p>One</p><p>Two</p></div><p>After</p>");
            var segments = StrokedSegments(framed);

            Assert.Equal(4, segments.Count);
            // The border's 0.75pt and the 9pt padding in from the margin.
            Assert.Equal(Margin() + 9.75, XOf(framed, "One"), 1);
            double bottom = segments.Where(s => s.IsHorizontal).Min(s => s.Y0);
            Assert.True(YOf(framed, "Two") > bottom && YOf(framed, "After") < bottom,
                "the paragraphs are not inside the frame, or the next one is not below it");
        }

        /// <summary>A border width written with a leading point (<c>.5px</c>) is a width: 0.375pt and the 9pt padding in from the margin.</summary>
        [Fact]
        public void ABorderWidthWithALeadingPoint_IsReadAsAWidth()
        {
            string framed = Content(
                "<div style='border:.5px solid #ff0000;padding:12px'><p>One</p><p>Two</p></div><p>After</p>");

            Assert.Equal(4, StrokedSegments(framed).Count);
            Assert.Equal(Margin() + 9.375, XOf(framed, "One"), 2);
        }

        /// <summary>Padding alone insets the blocks and draws nothing.</summary>
        [Fact]
        public void PaddingAloneInsetsTheBlocks()
        {
            string padded = Content("<div style='padding-left:24px'><p>In</p><p>Also</p></div>");

            Assert.Equal(Margin() + 18, XOf(padded, "In"), 1);
            Assert.Empty(StrokedSegments(padded));
        }

        /// <summary>
        /// A negative margin cancels the padding around it, as Bootstrap's <c>.row</c> cancels
        /// <c>.container</c>'s: the blocks inside both sit at the margin.
        /// </summary>
        [Fact]
        public void ANegativeMarginCancelsItsParentsPadding()
        {
            string row = Content("<div style='padding:0 20px'><div style='margin:0 -20px'><p>Row</p><p>More</p></div></div>");
            string longhand = Content("<div style='padding:0 20px'><div style='margin-left:-20px'><p>Row</p><p>More</p></div></div>");
            string container = Content("<div style='padding:0 20px'><p>Inset</p><p>More</p></div>");

            Assert.Equal(Margin() + 15, XOf(container, "Inset"), 1);
            Assert.Equal(Margin(), XOf(row, "Row"), 1);
            Assert.Equal(Margin(), XOf(longhand, "Row"), 1);
        }

        /// <summary>The right side too: right-aligned text ends at the margin it would end at alone.</summary>
        [Fact]
        public void ANegativeRightMarginCancelsItsParentsPadding()
        {
            string row = Content("<div style='padding:0 20px'><div style='margin:0 -20px'>" +
                                 "<p style='text-align:right'>End</p><p>More</p></div></div>");
            string alone = Content("<p style='text-align:right'>End</p>");

            Assert.Equal(XOf(alone, "End"), XOf(row, "End"), 1);
        }

        /// <summary>A border on one side draws that side only.</summary>
        [Fact]
        public void ABorderOnOneSideDrawsThatSide()
        {
            var segments = StrokedSegments(Content("<div style='border-top:1px solid #ff0000'><p>A</p><p>B</p></div>"));

            Assert.Single(segments);
            Assert.True(segments[0].IsHorizontal);
        }

        /// <summary>A container's rounded corners are its box's.</summary>
        [Fact]
        public void AContainersCornersAreRounded()
        {
            static int Curves(string content) => System.Text.RegularExpressions.Regex.Matches(content, @"\bc\n").Count;

            Assert.True(Curves(Content("<div style='background-color:#0000ff;border-radius:6px'><p>A</p><p>B</p></div>")) >= 4);
            Assert.Equal(0, Curves(Content("<div style='background-color:#0000ff'><p>A</p><p>B</p></div>")));
        }

        /// <summary>
        /// With nothing between them, a container's top margin and its first paragraph's collapse
        /// into the larger (CSS 2.1 §8.3.1) instead of adding up.
        /// </summary>
        [Fact]
        public void AContainersMarginCollapsesWithItsFirstParagraphs()
        {
            string nested = Content("<div style='margin-top:40px'><p style='margin-top:10px'>X</p><p>Y</p></div>");
            string single = Content("<p style='margin-top:40px'>X</p><p>Y</p>");
            // The control: a border between them keeps both margins.
            string bordered = Content("<div style='margin-top:40px;border-top:1px solid #ff0000'><p style='margin-top:10px'>X</p><p>Y</p></div>");

            Assert.Equal(YOf(single, "X"), YOf(nested, "X"), 1);
            Assert.True(YOf(bordered, "X") < YOf(single, "X") - 7, "the border did not keep the two margins apart");
        }

        /// <summary>
        /// The same at the bottom: the space after a container is the larger of its margin and its
        /// last paragraph's spacing — a margin smaller than the spacing disappears into it.
        /// </summary>
        /// <remarks>
        /// The paragraphs' own margins are a browser's default, 1em (<see cref="ParagraphMarginPt"/>):
        /// a container margin under it disappears, one over it replaces it.
        /// </remarks>
        [Fact]
        public void AContainersMarginCollapsesWithItsLastParagraphs()
        {
            static double Gap(string c) => YOf(c, "Y") - YOf(c, "Z");
            double plain = Gap(Content("<p>X</p><p>Y</p><p>Z</p>"));

            // 3pt, under the paragraph's margin; 30pt, over it.
            Assert.Equal(plain, Gap(Content("<div style='margin-bottom:4px'><p>X</p><p>Y</p></div><p>Z</p>")), 1);
            Assert.Equal(plain + 30 - ParagraphMarginPt, Gap(Content("<div style='margin-bottom:40px'><p>X</p><p>Y</p></div><p>Z</p>")), 1);
        }

        /// <summary>A paragraph's margin when its CSS sets none: the browser's 1em, at the default 12pt.</summary>
        private const double ParagraphMarginPt = 12;

        /// <summary>A container's background is a background: <c>PrintBackground = false</c> drops it.</summary>
        [Fact]
        public void AContainersBackgroundIsDroppedWithTheOthers()
        {
            const string html = "<div style='background-color:#0000ff'><p>A</p><p>B</p></div>";
            var off = Managed();
            off.PrintBackground = false;

            Assert.Contains("0 0 1 rg", Content(html));
            Assert.DoesNotContain("0 0 1 rg", DecodeStreams(ToPdf(html, off)));
        }

        /// <summary>A Bootstrap panel in a grid column: the frame is drawn inside the column's cell.</summary>
        [Fact]
        public void APanelInAGridColumnIsFramed()
        {
            const string css = "<style>.col{float:left;width:50%;padding:0 15px}" +
                               ".panel{border:1px solid #ff0000;padding:10px}</style>";
            string grid = Content(css + "<div><div class='col'><div class='panel'><p>Left</p><p>Panel</p></div></div>" +
                                  "<div class='col'><p>Right</p></div></div>");

            Assert.Contains("1 0 0 RG", grid);
            Assert.Equal(4, StrokedSegments(grid).Count(s => s.IsVertical || s.IsHorizontal));
        }

        /// <summary>
        /// A negative vertical margin pulls the flow up, collapsing by CSS's rule for negatives
        /// (CSS 2.1 §8.3.1: the largest positive plus the most negative): the paragraph's 12pt
        /// meets the box's −15pt as −3pt, not 12 — so the box's first line is 15pt higher than with
        /// no margin, and the paragraph after it, meeting −15pt the same way, 30pt higher.
        /// </summary>
        /// <remarks>It read as 0 until the negative collapse was implemented.</remarks>
        [Fact]
        public void ANegativeVerticalMarginPullsTheFlowUp()
        {
            const string frame = "border:1px solid #ff0000";
            string negative = Content($"<p>Above</p><div style='margin-top:-20px;margin-bottom:-20px;{frame}'><p>X</p><p>Y</p></div><p>Below</p>");
            string zero = Content($"<p>Above</p><div style='{frame}'><p>X</p><p>Y</p></div><p>Below</p>");

            Assert.Equal(YOf(zero, "X") + 15, YOf(negative, "X"), 1);
            Assert.Equal(YOf(zero, "Below") + 30, YOf(negative, "Below"), 1);
        }

        /// <summary>A container's background image is painted in its box.</summary>
        [Fact]
        public void AContainersBackgroundImageIsPainted()
        {
            string png = System.Convert.ToBase64String(MakeRgbPng(8, 8));
            string pictured = Content($"<div style=\"background-image:url(data:image/png;base64,{png});padding:4px\"><p>A</p><p>B</p></div>");
            string plain = Content("<div style='padding:4px'><p>A</p><p>B</p></div>");

            Assert.Contains(" Do", pictured);
            Assert.DoesNotContain(" Do", plain);
        }

        /// <summary>
        /// A container whose children map to nothing takes its margins, collapsed through it with
        /// the paragraph after (CSS 2.1 §8.3.1: an empty box's top and bottom margins adjoin): the
        /// space is its 15pt, the largest — not 15 + 15 — and nothing after it is lost. The
        /// paragraph carrying a 20px margin itself is the same space.
        /// </summary>
        [Fact]
        public void AnEmptyContainersMarginsCollapseThroughIt()
        {
            string empty = Content("<p>Top</p><div style='margin:20px 0'><div></div></div><p>After</p>");
            string spaced = Content("<p>Top</p><p style='margin-top:20px'>After</p>");
            string summed = Content("<p>Top</p><p style='margin-top:40px'>After</p>");

            Assert.Equal(YOf(spaced, "After"), YOf(empty, "After"), 1);
            Assert.NotEqual(YOf(summed, "After"), YOf(empty, "After"), 1);
        }

        /// <summary>
        /// A paragraph margin larger than its container's wins, and the container adds nothing —
        /// never a negative remainder that would move the flow back up.
        /// </summary>
        [Fact]
        public void AParagraphsLargerMarginWins()
        {
            string nested = Content("<p>Above</p><div style='margin-top:10px'><p style='margin-top:40px'>X</p><p>Y</p></div>");
            string single = Content("<p>Above</p><p style='margin-top:40px'>X</p><p>Y</p>");

            Assert.Equal(YOf(single, "X"), YOf(nested, "X"), 1);
        }

        /// <summary>
        /// Padding or a border between a container's margin and its paragraph's keeps both, at
        /// either end (CSS 2.1 §8.3.1: only adjoining margins collapse).
        /// </summary>
        [Fact]
        public void PaddingOrABorderKeepsBothMargins()
        {
            static double Gap(string c) => YOf(c, "Y") - YOf(c, "Z");
            string collapsed = Content("<div style='margin-top:40px'><p style='margin-top:10px'>X</p><p>Y</p></div>");
            string padded = Content("<div style='margin-top:40px;padding-top:4px'><p style='margin-top:10px'>X</p><p>Y</p></div>");
            double plain = Gap(Content("<p>X</p><p>Y</p><p>Z</p>"));

            // The top: 3pt of padding, and the paragraph's 7.5pt margin kept beside the container's.
            Assert.Equal(YOf(collapsed, "X") - 3 - 7.5, YOf(padded, "X"), 1);
            // The bottom: the container's 30pt kept beside its last paragraph's margin, past 3pt of
            // padding or a 0.75pt border — and collapsing with the next paragraph's, outside.
            Assert.Equal(plain + 30 + 3, Gap(Content("<div style='margin-bottom:40px;padding-bottom:4px'><p>X</p><p>Y</p></div><p>Z</p>")), 1);
            Assert.Equal(plain + 30 + 0.75, Gap(Content("<div style='margin-bottom:40px;border-bottom:1px solid #ff0000'><p>X</p><p>Y</p></div><p>Z</p>")), 1);
            // The control: with nothing between, the two collapse.
            Assert.Equal(plain + 30 - ParagraphMarginPt, Gap(Content("<div style='margin-bottom:40px'><p>X</p><p>Y</p></div><p>Z</p>")), 1);
        }
    }
}
