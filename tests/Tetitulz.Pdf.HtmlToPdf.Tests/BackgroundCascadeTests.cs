// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>background</c> against <c>background-color</c> / <c>background-image</c>: the later in
    /// cascade order decides, and <c>!important</c> beats a normal declaration.
    /// </summary>
    /// <remarks>
    /// The longhand used to win whenever both were present. Bootstrap 3's print stylesheet is a
    /// shorthand over longhands — <c>* { background: transparent !important }</c> — so a browser
    /// prints its alerts, panels and progress bars without fills, and this engine painted them.
    /// Pure primaries make the fill operator name the colour: <c>1 0 0 rg</c> is red and nothing
    /// else in these fixtures.
    /// </remarks>
    public class BackgroundCascadeTests
    {
        private const string RedFill = "1 0 0 rg";
        private const string BlueFill = "0 0 1 rg";

        private static string Content(string html) => DecodeStreams(ToPdf(html));

        [Fact]
        public void APrintRuleBackgroundTransparentImportant_RemovesAnEarlierBackgroundColor()
        {
            string plain = Content("<style>.alert{background-color:#ff0000}</style><div class='alert'>A</div>");
            string print = Content(
                "<style>.alert{background-color:#ff0000} @media print{*{background:transparent !important}}</style>" +
                "<div class='alert'>A</div>");

            Assert.Contains(RedFill, plain);
            Assert.DoesNotContain(RedFill, print);
        }

        [Fact]
        public void TheLaterOfBackgroundAndBackgroundColorWins()
        {
            string longhandLast = Content(
                "<style>.a{background:#0000ff} .a.b{background-color:#ff0000}</style><div class='a b'>A</div>");
            string shorthandLast = Content(
                "<style>.a{background-color:#ff0000} .a.b{background:#0000ff}</style><div class='a b'>A</div>");

            Assert.Contains(RedFill, longhandLast);
            Assert.DoesNotContain(BlueFill, longhandLast);
            Assert.Contains(BlueFill, shorthandLast);
            Assert.DoesNotContain(RedFill, shorthandLast);
        }

        /// <summary>
        /// An inline-only document is never folded by the cascade, so <c>!important</c> is still on
        /// the declaration: an important longhand beats a later normal shorthand.
        /// </summary>
        [Fact]
        public void AnImportantBackgroundColorBeatsALaterInlineShorthand()
        {
            string important = Content("<div style='background-color:#ff0000 !important;background:#0000ff'>A</div>");
            string normal = Content("<div style='background-color:#ff0000;background:#0000ff'>A</div>");

            Assert.Contains(RedFill, important);
            Assert.DoesNotContain(BlueFill, important);
            // The control: without !important the later shorthand wins.
            Assert.Contains(BlueFill, normal);
            Assert.DoesNotContain(RedFill, normal);
        }

        /// <summary>A later <c>background</c> with no image removes an earlier <c>background-image</c>.</summary>
        [Fact]
        public void ALaterBackgroundShorthandRemovesAnEarlierImage()
        {
            string png = System.Convert.ToBase64String(MakeRgbPng(4, 4));
            string css = $".i{{background-image:url(data:image/png;base64,{png});height:20px}}";

            string withImage = Content($"<style>{css}</style><div class='i'>A</div>");
            string removed = Content($"<style>{css} .i.n{{background:none}}</style><div class='i n'>A</div>");

            int Draws(string c) => Regex.Matches(c, @"/\w+\s+Do\b").Count;
            Assert.True(Draws(withImage) > 0, "the control drew no background image");
            Assert.Equal(0, Draws(removed));
        }
    }
}
