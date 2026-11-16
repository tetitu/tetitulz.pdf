// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// An inline element's background image, and the box of an element with no content — a
    /// checklist's tick drawn as the background of an empty, padded <c>&lt;i&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Both used to draw nothing: an inline element had no background image, and an empty one no
    /// run to carry a box. Image draws are read back as <c>w 0 0 h x y cm /Im Do</c>.
    /// </remarks>
    public class InlineBackgroundTests
    {
        private static readonly string Tick = Convert.ToBase64String(MakeRgbPng(8, 8));

        private static string Icon(string position = "center center") =>
            $"<i style=\"padding-left:24px;background-image:url(data:image/png;base64,{Tick});" +
            $"background-repeat:no-repeat;background-position:{position}\"></i>";

        private static List<(double W, double H, double X, double Y)> ImageDraws(string content) =>
            Regex.Matches(content, @"([-\d.]+)\s+0\s+0\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+cm\s*/\w+\s+Do\b")
                .Cast<Match>()
                .Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value)))
                .ToList();

        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        [Fact]
        public void AnEmptyPaddedElementDrawsItsBackgroundImage_AndTakesItsRoom()
        {
            string icon = DecodeStreams(ToPdf("<p>Before " + Icon() + " After</p>"));
            // The same two spaces, around an element that draws nothing.
            string plain = DecodeStreams(ToPdf("<p>Before <b></b> After</p>"));

            Assert.Single(ImageDraws(icon));
            Assert.Empty(ImageDraws(plain));
            // 24px of padding is 18pt of room.
            Assert.Equal(18, XOf(icon, "After") - XOf(plain, "After"), 1);
        }

        /// <summary>
        /// <c>background-position</c> places the tile in the box: centred, it sits half the box's
        /// spare width to the right of where <c>left</c> puts it.
        /// </summary>
        [Fact]
        public void TheBackgroundPositionPlacesTheTile()
        {
            double X(string position) => ImageDraws(DecodeStreams(ToPdf("<p>" + Icon(position) + "</p>"))).Single().X;

            double left = X("left top");
            // An 8px tile is 6pt; the box is 18pt wide, so it has 12pt to spare.
            Assert.Equal(left + 6, X("center center"), 1);
            Assert.Equal(left + 12, X("right top"), 1);
            Assert.Equal(left + 12, X("100% 0%"), 1);
        }

        /// <summary>
        /// The other ways CSS writes a position: the vertical keyword first, one value (a keyword for
        /// its own axis, a percentage for the horizontal one), and the <c>background</c> shorthand.
        /// </summary>
        [Theory]
        [InlineData("top right", 12)]
        [InlineData("right", 12)]
        [InlineData("50%", 6)]
        [InlineData("center", 6)]
        [InlineData("top", 6)]
        public void EveryWayOfWritingAPositionIsRead(string position, double fromLeft)
        {
            double left = ImageDraws(DecodeStreams(ToPdf("<p>" + Icon("left top") + "</p>"))).Single().X;
            double x = ImageDraws(DecodeStreams(ToPdf("<p>" + Icon(position) + "</p>"))).Single().X;

            Assert.Equal(left + fromLeft, x, 1);
        }

        [Fact]
        public void TheShorthandsPositionIsRead()
        {
            string shorthand = $"<p><i style=\"padding-left:24px;background:url(data:image/png;base64,{Tick}) no-repeat right top\"></i></p>";
            double left = ImageDraws(DecodeStreams(ToPdf("<p>" + Icon("left top") + "</p>"))).Single().X;

            Assert.Equal(left + 12, ImageDraws(DecodeStreams(ToPdf(shorthand))).Single().X, 1);
        }

        /// <summary>A background image with no padding still makes a box: it is drawn behind the text.</summary>
        [Fact]
        public void ABackgroundImageBehindTextIsDrawn()
        {
            string html = $"<p><span style=\"background-image:url(data:image/png;base64,{Tick})\">TEXT</span></p>";

            Assert.NotEmpty(ImageDraws(DecodeStreams(ToPdf(html))));
            Assert.Empty(ImageDraws(DecodeStreams(ToPdf("<p><span>TEXT</span></p>"))));
        }

        /// <summary>
        /// <c>PrintBackground = false</c> drops an inline box's image and fill, as it drops every
        /// other background; its padding stays, because padding is not a background.
        /// </summary>
        [Fact]
        public void PrintBackgroundOffDropsAnInlineBoxsBackgrounds()
        {
            string html = "<p>Before " + Icon() + " <span style='padding:0 6px;background-color:#0000ff'>BLUE</span> After</p>";
            var off = Managed();
            off.PrintBackground = false;

            string with = DecodeStreams(ToPdf(html));
            string without = DecodeStreams(ToPdf(html, off));

            Assert.Single(ImageDraws(with));
            Assert.Contains("0 0 1 rg", with);
            Assert.Empty(ImageDraws(without));
            Assert.DoesNotContain("0 0 1 rg", without);
            Assert.Equal(XOf(with, "After"), XOf(without, "After"), 2);
        }
    }

    /// <summary>
    /// A table cell with no background of its own shows its row's, then its row group's (CSS 2.1
    /// §17.5.1) — which is where Bootstrap's <c>.table-striped</c> puts its stripes.
    /// </summary>
    public class TableRowBackgroundTests
    {
        private static int Fills(string content, string rg) =>
            Regex.Matches(content, Regex.Escape(rg)).Count;

        [Fact]
        public void ACellShowsItsRowsBackground()
        {
            Assert.Contains("0 0 1 rg", DecodeStreams(ToPdf(
                "<table><tr style='background-color:#0000ff'><td>X</td></tr></table>")));
            Assert.DoesNotContain("0 0 1 rg", DecodeStreams(ToPdf("<table><tr><td>X</td></tr></table>")));
        }

        [Fact]
        public void ACellsOwnBackgroundWinsOverItsRows()
        {
            string dec = DecodeStreams(ToPdf(
                "<table><tr style='background-color:#0000ff'><td style='background-color:#ff0000'>X</td></tr></table>"));

            Assert.Contains("1 0 0 rg", dec);
            Assert.DoesNotContain("0 0 1 rg", dec);
        }

        [Fact]
        public void ARowWithNoBackgroundShowsItsRowGroups()
        {
            Assert.Contains("0 1 0 rg", DecodeStreams(ToPdf(
                "<table><tbody style='background-color:#00ff00'><tr><td>X</td></tr></tbody></table>")));
        }

        /// <summary>Bootstrap's stripes: every odd row of the body, one fill per cell.</summary>
        [Fact]
        public void BootstrapStripesColourTheOddRows()
        {
            string dec = DecodeStreams(ToPdf(
                "<style>.s > tbody > tr:nth-of-type(odd) { background-color:#0000ff }</style>" +
                "<table class='s'><tbody><tr><td>ONE</td></tr><tr><td>TWO</td></tr><tr><td>THREE</td></tr></tbody></table>"));

            Assert.Equal(2, Fills(dec, "0 0 1 rg"));
        }
    }
}
