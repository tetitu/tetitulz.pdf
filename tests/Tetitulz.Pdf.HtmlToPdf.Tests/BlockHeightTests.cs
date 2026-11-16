// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A block's <c>height</c> and <c>min-height</c>, as the least height of its box. Found against
    /// Chrome on a CV: the skill bars — an empty <c>div</c> 6px tall with a background, holding a
    /// 90%-wide fill — were not drawn at all, because an element with no text made no paragraph and
    /// a block's height was never read.
    /// </summary>
    public class BlockHeightTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        private static List<(double W, double H)> Fills(string content) =>
            Regex.Matches(content, @"[-\d.]+ [-\d.]+ ([-\d.]+) ([-\d.]+) re\s+f").Cast<Match>()
                 .Select(m => (System.Math.Abs(D(m, 1)), System.Math.Abs(D(m, 2)))).ToList();

        private static double D(Match m, int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

        /// <summary>An empty 20px block with a background is a 15pt fill.</summary>
        [Fact]
        public void AnEmptyBlockWithAHeightIsDrawn()
            => Assert.Equal(15, Assert.Single(Fills(Content("<div style='height:20px;background:#00f'></div>"))).H, 1);

        /// <summary>A spacer — a height and nothing else — still makes room: the next block is 30pt (40px) lower.</summary>
        [Fact]
        public void AnEmptySpacerMakesRoom()
        {
            string plain = Content("<p style='margin:0'>A</p><div></div><p style='margin:0'>B</p>");
            string spaced = Content("<p style='margin:0'>A</p><div style='height:40px'></div><p style='margin:0'>B</p>");
            Assert.Equal(YOf(plain, "B") - 30, YOf(spaced, "B"), 1);
        }

        /// <summary>The control: with no height an empty block draws nothing.</summary>
        [Fact]
        public void AnEmptyBlockWithoutAHeightDrawsNothing()
            => Assert.Empty(Fills(Content("<div style='background:#00f'></div>")));

        /// <summary>The content box's height plus the padding: 20px and 5px each side is 30px, 22.5pt; <c>border-box</c> keeps 20px.</summary>
        [Fact]
        public void PaddingIsAddedUnlessBorderBox()
        {
            Assert.Equal(22.5, Assert.Single(Fills(Content("<div style='height:20px;padding:5px;background:#00f'></div>"))).H, 1);
            Assert.Equal(15, Assert.Single(Fills(Content("<div style='height:20px;padding:5px;box-sizing:border-box;background:#00f'></div>"))).H, 1);
        }

        /// <summary>A <c>min-height</c> is one too, and the larger of the two wins.</summary>
        [Fact]
        public void MinHeightCounts()
            => Assert.Equal(30, Assert.Single(Fills(Content("<div style='height:20px;min-height:40px;background:#00f'></div>"))).H, 1);

        /// <summary>Content taller than the height grows the box: three lines in a 2px box are more than 2px.</summary>
        [Fact]
        public void TallerContentGrowsTheBox()
            => Assert.True(Assert.Single(Fills(Content("<div style='height:2px;background:#00f'>A<br>B<br>C</div>"))).H > 20);

        /// <summary>A percentage is of a height the flow does not know: no box, nothing drawn.</summary>
        [Fact]
        public void APercentageHeightIsIgnored()
            => Assert.Empty(Fills(Content("<div style='height:50%;background:#00f'></div>")));

        /// <summary>
        /// A viewport unit is of the page area, as print has it: <c>50vh</c> is half its height and
        /// <c>100vh</c> all of it (Letter less the default margins), so a CV's <c>min-height: 100vh</c>
        /// sidebar runs the page down.
        /// </summary>
        [Fact]
        public void AViewportHeightIsThePageAreas()
        {
            double full = Assert.Single(Fills(Content("<div style='min-height:100vh;background:#00f'></div>"))).H;
            double half = Assert.Single(Fills(Content("<div style='height:50vh;background:#00f'></div>"))).H;
            Assert.InRange(full, 600, 792);
            Assert.Equal(full / 2, half, 1);
        }

        /// <summary>
        /// The width units too: on a portrait page <c>50vw</c> and <c>50vmin</c> are half the page
        /// area's width — a top rule half a full-width one's — and <c>50vmax</c> half its height, more.
        /// </summary>
        [Theory]
        [InlineData("50vw", false)]
        [InlineData("50vmin", false)]
        [InlineData("50vmax", true)]
        public void ViewportWidthsAreThePageAreas(string width, bool ofTheHeight)
        {
            string c = Content("<div style='border-top:1px solid #000'>REF</div>" +
                               $"<div style='width:{width};border-top:1px solid #000'>X</div>");
            var rules = StrokedSegments(c).Where(s => s.IsHorizontal).Select(s => System.Math.Abs(s.X1 - s.X0)).OrderByDescending(w => w).ToList();
            if (ofTheHeight) Assert.True(rules.Count(r => r > 0.55 * rules.Max()) == 2, "half the height is wider than half the width");
            else Assert.Equal(rules[0] / 2, rules[1], 1);
        }

        /// <summary>The CV's skill bar: a 6px track and, inside it, a 90%-wide fill of the same height.</summary>
        [Fact]
        public void ASkillBarIsATrackAndAFill()
        {
            var fills = Fills(Content("<div style='height:6px;background:#ccc'><div style='height:6px;width:90%;background:#0a0'></div></div>"));
            Assert.Equal(2, fills.Count);
            Assert.All(fills, f => Assert.Equal(4.5, f.H, 1));
            Assert.Equal(0.9 * fills[0].W, fills[1].W, 0);
        }
    }
}
