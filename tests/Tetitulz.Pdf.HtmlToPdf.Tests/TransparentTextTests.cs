// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.RegularExpressions;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A fully transparent <c>color</c> draws the text invisibly, as a browser does: the run is shown
    /// in text render mode 3 and takes its room; a descendant's own colour, and a link's default
    /// colour, end it.
    /// </summary>
    /// <remarks>
    /// Dropped as an absent one, a transparent colour would leave the text the inherited colour, and
    /// seen; composited to white, it would show on a coloured background.
    /// </remarks>
    public class TransparentTextTests
    {
        private static string Content(string html) => DecodeStreams(ToPdf(html));

        private static int IndexOf(string content, string token)
        {
            int at = content.IndexOf(token, StringComparison.Ordinal);
            Assert.True(at >= 0, token + " is not in the content");
            return at;
        }

        /// <summary>The last <c>Tr</c> written before <paramref name="token"/>, or null for none.</summary>
        private static string ModeBefore(string content, string token)
        {
            var modes = Regex.Matches(content.Substring(0, IndexOf(content, token)), @"\b(\d) Tr\b");
            return modes.Count == 0 ? null : modes[modes.Count - 1].Groups[1].Value;
        }

        [Theory]
        [InlineData("color: transparent")]
        [InlineData("color: rgba(0, 0, 0, 0)")]
        [InlineData("color: #0000")]
        public void ATransparentColourDrawsTheTextInvisibly(string style)
        {
            string content = Content($"<p>Hi <span style='{style}'>gone</span> there</p>");
            string control = Content("<p>Hi <span style='color: red'>gone</span> there</p>");

            Assert.Equal("3", ModeBefore(content, "(gone) Tj"));
            Assert.Equal("0", ModeBefore(content, "(there) Tj"));
            Assert.Equal(XOf(control, "there"), XOf(content, "there"), 3);
            Assert.DoesNotContain(" Tr", control, StringComparison.Ordinal);
        }

        /// <summary>A descendant's own colour ends the transparency; one without stays invisible.</summary>
        [Fact]
        public void ADescendantsOwnColourEndsIt()
        {
            string content = Content("<p><span style='color: transparent'>gone <b style='color: red'>seen</b> <i>also</i></span></p>");

            Assert.Equal("3", ModeBefore(content, "(gone) Tj"));
            Assert.Equal("0", ModeBefore(content, "(seen) Tj"));
            Assert.Equal("3", ModeBefore(content, "(also) Tj"));
        }

        /// <summary>A link takes the browser's default link colour, which ends the transparency.</summary>
        [Fact]
        public void ALinksDefaultColourEndsIt()
        {
            string content = Content("<p><span style='color: transparent'>gone <a href='https://example.org/'>link</a></span></p>");

            Assert.Equal("3", ModeBefore(content, "(gone) Tj"));
            Assert.Equal("0", ModeBefore(content, "(link) Tj"));
        }

        /// <summary>The underline takes the text's colour, so a transparent run's is not drawn.</summary>
        [Fact]
        public void ATransparentRunsUnderlineIsNotDrawn()
        {
            int Rules(string content) => Regex.Matches(content, @"\bre\s+f\b").Count;

            int plain = Rules(Content("<p>Hi <span>gone</span></p>"));
            Assert.Equal(plain + 1, Rules(Content("<p>Hi <span style='text-decoration: underline'>gone</span></p>")));
            Assert.Equal(plain, Rules(Content("<p>Hi <span style='color: transparent; text-decoration: underline'>gone</span></p>")));
        }
    }
}
