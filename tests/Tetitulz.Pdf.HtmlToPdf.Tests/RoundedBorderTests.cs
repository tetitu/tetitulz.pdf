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
    /// Bootstrap's <c>.alert</c>, <c>.panel</c> and <c>.form-control</c> have a 1px border and a
    /// 4px radius, and printed with square corners — the border's four straight strokes over the
    /// rounded fill. Found against Chrome.
    /// </summary>
    public class RoundedBorderTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        /// <summary>A rounded block with one border strokes a rounded outline — curves, no straight edges; a square one strokes lines (the control).</summary>
        [Fact]
        public void ARoundedBlocksBorderIsRounded()
        {
            string rounded = Content("<div style='border:1px solid #000;border-radius:4px;padding:8px'>A</div>");
            Assert.Equal(4, Regex.Matches(rounded, @"\bc\n").Count);
            Assert.Empty(StrokedSegments(rounded));

            string square = Content("<div style='border:1px solid #000;padding:8px'>A</div>");
            Assert.Equal(4, StrokedSegments(square).Count);
        }

        /// <summary>A rounded box of blocks — a panel — likewise.</summary>
        [Fact]
        public void ARoundedPanelsBorderIsRounded()
        {
            string c = Content("<div style='border:1px solid #000;border-radius:4px'><p>A</p><p>B</p></div>");
            Assert.Equal(4, Regex.Matches(c, @"\bc\n").Count);
            Assert.Empty(StrokedSegments(c));
        }
    }
}
