// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A flex row's items stretch to the row's height — <c>align-items: stretch</c>, the initial
    /// value. Found against Chrome on a CV: the sidebar's background ended with its own text where
    /// the browser runs it to the bottom of the row. The short item's fill is as tall as the tall
    /// item's three lines.
    /// </summary>
    public class FlexStretchTests
    {
        private static double FirstFillHeight(string container, string shortItem = "")
        {
            string c = DecodeStreams(ToPdf("<html><body><div style='display:flex;" + container + "'>" +
                "<div style='background:#00f;" + shortItem + "'>A</div><div>B1<br>B2<br>B3</div></div></body></html>"));
            var m = Regex.Match(c, @"[-\d.]+ [-\d.]+ [-\d.]+ ([-\d.]+) re\s+f");
            Assert.True(m.Success, "no fill");
            return System.Math.Abs(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        [Fact]
        public void AnItemStretchesByDefault()
            => Assert.True(FirstFillHeight("") > 2.5 * FirstFillHeight("align-items:flex-start"));

        [Fact]
        public void AlignItemsStretchStretches()
            => Assert.Equal(FirstFillHeight(""), FirstFillHeight("align-items:stretch"), 1);

        /// <summary>The control: an item aligned to the start is its own line's height, stretched it is three lines'.</summary>
        [Fact]
        public void AnItemAlignedToTheStartDoesNotStretch()
        {
            double start = FirstFillHeight("align-items:flex-start");
            Assert.True(start < 20, $"{start}");
            Assert.Equal(start, FirstFillHeight("", "align-self:flex-start"), 1);
        }

        /// <summary>
        /// Only a box that holds the whole item stretches: an undecorated item whose first child has
        /// a background, with text after it, keeps that child to its own line.
        /// </summary>
        [Fact]
        public void AChildBoxThatIsNotTheItemDoesNotStretch()
        {
            string c = DecodeStreams(ToPdf("<html><body><div style='display:flex'>" +
                "<div><div style='background:#00f'><p style='margin:0'>A</p></div><p style='margin:0'>after</p></div><div>B1<br>B2<br>B3<br>B4</div></div></body></html>"));
            var m = Regex.Match(c, @"[-\d.]+ [-\d.]+ [-\d.]+ ([-\d.]+) re\s+f");
            Assert.True(m.Success, "no fill");
            Assert.True(System.Math.Abs(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) < 20);
        }

        /// <summary>
        /// A container's least height is its line's: in a <c>min-height: 100vh</c> row the stretched
        /// item runs the page down — a CV's sidebar — and the page is still one page, the
        /// container's own padding (inside its <c>border-box</c> height) coming off the row's height
        /// rather than adding to it.
        /// </summary>
        [Fact]
        public void AContainersMinimumHeightIsItsRows()
        {
            byte[] pdf = ToPdf("<html><body style='margin:0'><div style='display:flex;min-height:100vh;padding:20px 0;box-sizing:border-box'>" +
                               "<div style='background:#00f;width:30%'>A</div><div>B</div></div></body></html>");
            Assert.Equal(1, CountPages(pdf));
            var m = Regex.Match(DecodeStreams(pdf), @"[-\d.]+ [-\d.]+ [-\d.]+ ([-\d.]+) re\s+f");
            Assert.True(m.Success, "no fill");
            Assert.True(System.Math.Abs(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) > 600);
        }

        /// <summary><c>align-self: auto</c> is the container's: under <c>flex-start</c> the item does not stretch.</summary>
        [Fact]
        public void AlignSelfAutoAsksTheContainer()
        {
            Assert.Equal(FirstFillHeight("align-items:flex-start"), FirstFillHeight("align-items:flex-start", "align-self:auto"), 1);
            Assert.Equal(FirstFillHeight(""), FirstFillHeight("", "align-self:auto"), 1);   // and stretches under the default
        }
    }
}
