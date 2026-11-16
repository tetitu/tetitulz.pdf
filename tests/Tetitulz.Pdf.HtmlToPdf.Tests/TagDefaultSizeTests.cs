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
    /// A tag's own default size — <c>&lt;small&gt;</c>'s, a heading's, <c>&lt;pre&gt;</c>'s — applies
    /// only where the CSS sets none; a declared percentage is of the parent's size, as CSS computes
    /// it. Bootstrap's <c>h3 small { font-size: 65% }</c> came out at 52% of the heading: 0.8 × 0.65.
    /// </summary>
    public class TagDefaultSizeTests
    {
        /// <summary>The size <paramref name="text"/> is drawn at: the last <c>Tf</c> before its <c>Tj</c>.</summary>
        private static double SizeOf(string body, string text)
        {
            string c = DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));
            int at = c.IndexOf("(" + text + ") Tj", StringComparison.Ordinal);
            Assert.True(at >= 0, text + " was not drawn");
            var tf = Regex.Matches(c.Substring(0, at), @"/\w+\s+([\d.]+)\s+Tf").Cast<Match>().Last();
            return double.Parse(tf.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A 65% <c>&lt;small&gt;</c> in a 20pt paragraph is 13pt; without the declaration it is the
        /// tag's own 0.8, 16pt (the control).
        /// </summary>
        [Fact]
        public void ADeclaredSizeOnSmallIsOfTheParent()
        {
            Assert.Equal(13, SizeOf("<p style='font-size:20pt'>A <small style='font-size:65%'>B</small></p>", "B"), 2);
            Assert.Equal(16, SizeOf("<p style='font-size:20pt'>A <small>B</small></p>", "B"), 2);
        }

        /// <summary>
        /// A heading's declared percentage is of its parent: <c>h2 { font-size: 200% }</c> under
        /// 10pt is 20pt, not 200% of the heading's own 15; without it, the UA's 1.5em, 15pt (the
        /// control).
        /// </summary>
        [Fact]
        public void ADeclaredSizeOnAHeadingIsOfTheParent()
        {
            Assert.Equal(20, SizeOf("<div style='font-size:10pt'><h2 style='font-size:200%'>H</h2></div>", "H"), 2);
            Assert.Equal(15, SizeOf("<div style='font-size:10pt'><h2>H</h2></div>", "H"), 2);
        }

        /// <summary>
        /// The same for <c>&lt;big&gt;</c>, and through the <c>font</c> shorthand: 100% is the
        /// parent's 20pt, not the tag's 1.2 of it; <c>font: 65% Arial</c> on a small is 13pt.
        /// </summary>
        [Fact]
        public void BigAndTheFontShorthandAreReadToo()
        {
            Assert.Equal(20, SizeOf("<p style='font-size:20pt'>A <big style='font-size:100%'>B</big></p>", "B"), 2);
            Assert.Equal(24, SizeOf("<p style='font-size:20pt'>A <big>B</big></p>", "B"), 2);
            Assert.Equal(13, SizeOf("<p style='font-size:20pt'>A <small style='font:65% Arial'>B</small></p>", "B"), 2);
        }
    }
}

