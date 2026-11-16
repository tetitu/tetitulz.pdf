// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A <c>&lt;canvas&gt;</c> is laid out as the blank box it is before a script draws on it, and an
    /// <c>&lt;img&gt;</c> draws its padding and border — compared with Chrome on a Bootstrap 3 RTL
    /// form, where the canvas collapsed to a sliver and let the page below it move up.
    /// </summary>
    public class CanvasAndImageBoxTests
    {
        private static string Content(string body, string dir = "ltr") =>
            DecodeStreams(ToPdf($"<html dir='{dir}'><body><p>Before</p>{body}<p>After</p></body></html>"));

        private static double After(string body) => YOf(Content(body), "After");

        /// <summary>CSS pixels to points at 96 dpi.</summary>
        private const double Px = 0.75;

        private static readonly string Png = Convert.ToBase64String(MakeRgbPng(8, 8));

        private static (double X, double Y) ImageAt(string content)
        {
            var m = Regex.Match(content,
                @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+cm\s*/\w+\s+Do\b");
            Assert.True(m.Success, "no image was drawn");
            return (D(m.Groups[5].Value), D(m.Groups[6].Value));
        }

        private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ canvas

        /// <summary>
        /// A canvas takes its <c>height</c> attribute's room: 100 CSS pixels more of it moves the
        /// next paragraph down by 75pt. It took the height of an empty line whatever it said.
        /// </summary>
        [Fact]
        public void ACanvasTakesTheRoomItsHeightAttributeGives()
        {
            double shorter = After("<canvas width='300' height='100'></canvas>");
            double taller = After("<canvas width='300' height='200'></canvas>");

            Assert.Equal(100 * Px, shorter - taller, 1);
        }

        /// <summary>A canvas with no size attributes is 300 × 150, HTML's default — the same as one that says so.</summary>
        [Fact]
        public void ACanvasWithoutAttributesIsThreeHundredByOneFifty()
        {
            double implicitSize = After("<canvas></canvas>");
            double explicitSize = After("<canvas width='300' height='150'></canvas>");
            double other = After("<canvas width='300' height='40'></canvas>");

            Assert.Equal(explicitSize, implicitSize, 2);
            Assert.NotEqual(other, implicitSize, 2);
        }

        /// <summary>CSS <c>height</c> sizes the box over the attribute, as it scales an image.</summary>
        [Fact]
        public void ACanvasCssHeightOverridesItsAttribute()
        {
            double styled = After("<canvas width='300' height='200' style='height:50px'></canvas>");
            double attributed = After("<canvas width='300' height='50'></canvas>");

            Assert.Equal(attributed, styled, 2);
        }

        /// <summary>
        /// The canvas's border is drawn round its whole box — a rectangle the size of the bitmap plus
        /// the border — and no image is embedded for it.
        /// </summary>
        [Fact]
        public void ACanvasDrawsItsBorderRoundItsWholeBox()
        {
            string content = Content("<canvas width='300' height='100' style='border:1px solid #000'></canvas>");

            var rects = Regex.Matches(content, @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\b")
                             .Cast<Match>().Select(m => (W: D(m.Groups[3].Value), H: D(m.Groups[4].Value))).ToList();
            // Stroked along the middle of the border band: the outer size less one border.
            Assert.Contains(rects, r => Math.Abs(r.W - (300 + 1) * Px) < 0.05 && Math.Abs(r.H - (100 + 1) * Px) < 0.05);
            Assert.DoesNotContain(" Do", content, StringComparison.Ordinal);
        }

        /// <summary>
        /// A zero-sized canvas is not given the fallback size an unsized image gets: it takes less
        /// room than a 10px one, which takes more than no canvas at all (the potency control).
        /// </summary>
        [Fact]
        public void AZeroSizedCanvasIsNotGivenTheFallbackSize()
        {
            double zero = After("<canvas width='0' height='0'></canvas>");
            double none = After("");
            double some = After("<canvas width='10' height='10'></canvas>");

            Assert.True(zero > some, $"a 0px canvas left After at {zero}, a 10px one at {some}");
            Assert.True(some < none - 5 * Px, $"a 10px canvas left After at {some}, none at {none}");
        }

        // ------------------------------------------------------------------ img frame and placement

        /// <summary>
        /// An image's padding and border frame it: the picture moves in by both on the left — 4px
        /// padding and a 1px border — and stays its own size (Bootstrap's <c>.img-thumbnail</c>).
        /// </summary>
        [Fact]
        public void AnImagesPaddingAndBorderFrameIt()
        {
            string img = $"<img src='data:image/png;base64,{Png}' width='40' height='40'";
            var plain = ImageAt(Content(img + ">"));
            var framed = ImageAt(Content(img + " style='padding:4px;border:1px solid red'>"));

            Assert.Equal(plain.X + 5 * Px, framed.X, 2);
            Assert.Equal(plain.Y - 5 * Px, framed.Y, 2);
        }

        /// <summary>
        /// In a right-to-left page an image sits at the right, where the browser puts it; in a
        /// left-to-right one at the left (the control).
        /// </summary>
        [Fact]
        public void AnImageInARightToLeftPageSitsAtTheRight()
        {
            string img = $"<img src='data:image/png;base64,{Png}' width='40' height='40'>";
            double ltr = ImageAt(Content(img, "ltr")).X;
            double rtl = ImageAt(Content(img, "rtl")).X;

            Assert.True(rtl > ltr + 300, $"rtl image at {rtl}, ltr at {ltr}");
        }
    }
}
