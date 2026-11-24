// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Inline elements with a border or padding — Bootstrap's <c>.label</c>, <c>.badge</c>,
    /// <c>.btn</c>, <c>code</c> and a <c>.form-control</c> input — are drawn in a box.
    /// </summary>
    /// <remarks>
    /// With a run background and nothing else in the model, they would print as plain text — a
    /// label's padding and its print-stylesheet border dropped. Pure primaries make the colour
    /// operators unambiguous; <c>re S</c> is a stroked rectangle, which nothing else in these
    /// fixtures draws.
    /// </remarks>
    public class InlineBoxMappingTests
    {
        private static string Content(string html) => DecodeStreams(ToPdf(html));

        private static int StrokedRects(string content) => Regex.Matches(content, @"\bre\s*\n?S\b").Count;

        [Fact]
        public void ABorderedSpanIsDrawnInABox()
        {
            string boxed = Content("<p>Status <span style='border:1px solid #ff0000;padding:0 6px'>NEW</span> item</p>");
            string plain = Content("<p>Status <span>NEW</span> item</p>");

            Assert.Equal(1, StrokedRects(boxed));
            Assert.Contains("1 0 0 RG", boxed);
            Assert.Equal(0, StrokedRects(plain));
        }

        /// <summary>
        /// A border on some sides only is not drawn as a closed box: the model's one border per
        /// box cannot say "bottom only", and a rule under the text is not a frame around it. The
        /// padding still takes room.
        /// </summary>
        [Fact]
        public void APartlyBorderedSpanDrawsNoBox_ButKeepsItsPadding()
        {
            string partial = Content("<p>Status <span style='border-bottom:1px solid #ff0000;padding:0 12px'>NEW</span> ITEM</p>");
            string plain = Content("<p>Status <span>NEW</span> ITEM</p>");

            Assert.Equal(0, StrokedRects(partial));
            Assert.DoesNotContain("1 0 0 RG", partial);
            Assert.True(XOf(partial, "ITEM") - XOf(plain, "ITEM") > 15, "the span's padding took no room");
        }

        /// <summary>The span's padding takes room: the text after it moves right.</summary>
        [Fact]
        public void APaddedSpanPushesTheFollowingText()
        {
            string boxed = Content("<p>Status <span style='border:1px solid #ff0000;padding:0 12px'>NEW</span> ITEM</p>");
            string plain = Content("<p>Status <span>NEW</span> ITEM</p>");

            double moved = XOf(boxed, "ITEM") - XOf(plain, "ITEM");
            // 12px each side is 9pt, and 1px each side 0.75pt: 19.5pt in all.
            Assert.True(System.Math.Abs(moved - 19.5) < 0.5, $"the text after the span moved {moved:F2}pt, not 19.5pt");
        }

        /// <summary>
        /// A Bootstrap label: padding in em, a background, rounded corners. The box fills its padding
        /// with the background.
        /// </summary>
        [Fact]
        public void ABootstrapLabelIsAFilledBox()
        {
            const string Css = "<style>.label{display:inline;padding:.2em .6em .3em;font-size:75%;" +
                               "color:#fff;background-color:#0000ff;border-radius:.25em}</style>";
            string label = Content(Css + "<p>Widget <span class='label'>NEW</span> ITEM</p>");
            // The same 75% text with no box, so the padding is the only difference.
            string plain = Content("<p>Widget <span style='font-size:75%'>NEW</span> ITEM</p>");

            // Painted once, by the box: no run highlight is made from the background as well,
            // or the text's own tighter fill would be drawn over the box's.
            Assert.Single(Regex.Matches(label, @"\b0 0 1 rg\b"));
            Assert.True(XOf(label, "ITEM") - XOf(plain, "ITEM") > 5, "the label's padding took no room");
        }

        /// <summary>
        /// The control: a background alone stays a run highlight — it takes no room, as it never did.
        /// </summary>
        [Fact]
        public void ABackgroundAloneTakesNoRoom()
        {
            string shaded = Content("<p>Status <span style='background-color:#0000ff'>NEW</span> ITEM</p>");
            string plain = Content("<p>Status <span>NEW</span> ITEM</p>");

            Assert.Contains("0 0 1 rg", shaded);
            Assert.True(System.Math.Abs(XOf(shaded, "ITEM") - XOf(plain, "ITEM")) < 0.01,
                "a background-only span moved the following text");
        }

        /// <summary>
        /// An input the author gave a border (Bootstrap's <c>.form-control</c>) is drawn in that box
        /// instead of the plain shading that stands in for a browser's default control.
        /// </summary>
        [Fact]
        public void AStyledInputIsDrawnInItsOwnBox_AndAPlainOneKeepsTheShading()
        {
            string styled = Content("<p><input value='a@b.com' style='border:1px solid #ff0000;padding:6px 12px'></p>");
            string plain = Content("<p><input value='a@b.com'></p>");

            Assert.Equal(1, StrokedRects(styled));
            Assert.Contains("1 0 0 RG", styled);
            Assert.Equal(0, StrokedRects(plain));

            // The default shading (#EFEFEF, 0.937 per channel) is drawn for the plain control and
            // not the styled one.
            var shading = new Regex(@"0\.93\d*\s+0\.93\d*\s+0\.93\d*\s+rg");
            Assert.Matches(shading, plain);
            Assert.DoesNotMatch(shading, styled);
        }
    }
}
