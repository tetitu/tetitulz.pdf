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
    /// <c>::marker</c> styles a list item's marker — its colour, size and weight, and its
    /// <c>content</c> — apart from the item's text. It failed the selector match and was dropped.
    /// </summary>
    public class MarkerTests
    {
        private static string Content(string css) =>
            DecodeStreams(ToPdf("<html><head><style>" + css + "</style></head><body><ol><li>ITEM</li></ol></body></html>"));

        /// <summary>The fill colour and font size in force where <paramref name="text"/> is drawn.</summary>
        private static (string Colour, double Size) StyleOf(string content, string text)
        {
            int at = content.IndexOf("(" + text + ")", System.StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{text}' was not drawn");
            string before = content.Substring(0, at);
            var colour = Regex.Matches(before, @"([\d.]+ [\d.]+ [\d.]+) rg").Cast<Match>().LastOrDefault();
            var size = Regex.Matches(before, @"/\w+ ([\d.]+) Tf").Cast<Match>().Last();
            return (colour?.Groups[1].Value ?? "0 0 0", double.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>A marker's colour is its own; the item's text keeps the item's. With no <c>::marker</c> the two agree (the control).</summary>
        [Fact]
        public void AMarkersColourIsItsOwn()
        {
            string c = Content("li::marker{color:#ff0000}");
            Assert.Equal("1 0 0", StyleOf(c, "1.").Colour);
            Assert.NotEqual("1 0 0", StyleOf(c, "ITEM").Colour);

            string plain = Content("");
            Assert.Equal(StyleOf(plain, "ITEM").Colour, StyleOf(plain, "1.").Colour);
        }

        /// <summary>A marker's size is its own: 20px is 15pt, the item's text stays at its size.</summary>
        [Fact]
        public void AMarkersSizeIsItsOwn()
        {
            string c = Content("li::marker{font-size:20px}");
            Assert.Equal(15, StyleOf(c, "1.").Size, 1);
            Assert.NotEqual(15, StyleOf(c, "ITEM").Size, 1);
        }

        /// <summary>A string <c>content</c> is the marker; <c>none</c> is no marker.</summary>
        [Fact]
        public void AMarkersContentReplacesIt()
        {
            string star = Content("li::marker{content:'* '}");
            Assert.Contains("(*", star);
            Assert.DoesNotContain("(1.)", star);

            string none = Content("li::marker{content:none}");
            Assert.DoesNotContain("(1.)", none);
            Assert.Contains("(ITEM)", none);
        }

        /// <summary>
        /// A string <c>::marker</c> content is the marker even under <c>list-style: none</c>, which
        /// only says what <c>normal</c> content is; an item that is not a list item —
        /// <c>display: block</c>, Bootstrap's <c>.list-group-item</c> — has no marker box and gets none.
        /// </summary>
        [Fact]
        public void AMarkerStringNeedsAMarkerBoxOnly()
        {
            string c = DecodeStreams(ToPdf("<html><head><style>ol{list-style:none} li::marker{content:'* '}</style></head>" +
                "<body><ol><li>ITEM</li><li style='display:block'>BLOCK</li></ol></body></html>"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(c, @"\(\*"));
            Assert.Contains("(ITEM)", c);
            Assert.Contains("(BLOCK)", c);
        }
    }
}
