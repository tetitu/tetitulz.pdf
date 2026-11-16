// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Text sits in the middle of its line box, the leading split half above and half below it
    /// (CSS 2.1 §10.8.1).
    /// </summary>
    /// <remarks>
    /// The Builder's default puts the baseline on the line's bottom edge, all the leading above the
    /// text; the converter asks for CSS's placement instead (<c>PdfExportOptions.HalfLeading</c>).
    /// A bordered block shows it: its border stands the box's 2pt inset outside the line box, so
    /// the space from the text to each border is the half-leading plus that inset.
    /// </remarks>
    public class LineBoxTests
    {
        [Fact]
        public void TextSitsInTheMiddleOfATallLine()
        {
            string dec = DecodeStreams(ToPdf(
                "<div style='border:1px solid #ff0000;line-height:40px;font-size:16px'>Inside</div>"));

            var segments = StrokedSegments(dec);
            var horizontal = segments.Where(s => s.IsHorizontal).Select(s => s.Y0).ToList();
            Assert.Equal(2, horizontal.Count);
            double top = horizontal.Max(), bottom = horizontal.Min();

            double baseline = YOf(dec, "Inside");
            const double size = 12;   // 16px
            // The glyph box the exporter centres: Helvetica's line metrics (Liberation Sans's, Arial's),
            // 0.905 of the size above the baseline and 0.212 below.
            double above = top - (baseline + 0.905 * size);
            double below = (baseline - 0.212 * size) - bottom;

            Assert.Equal(above, below, 1);
            // Potency: the line really is tall — 30pt of line for 12pt text.
            Assert.True(top - bottom > 30, $"the box is {top - bottom:F1}pt tall");
        }
    }
}
