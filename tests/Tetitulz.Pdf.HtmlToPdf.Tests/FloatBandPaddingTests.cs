// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A float band's cells add no padding of their own: a Bootstrap column's content starts its
    /// CSS padding in from the column's edge, no further. The band's cells kept the renderer's 3pt a
    /// side, so every grid column's content — a form control, a panel — was 3pt narrower each side
    /// than Chrome's (found against Chrome on the second Bootstrap page).
    /// </summary>
    public class FloatBandPaddingTests
    {
        /// <summary>
        /// A row cancelling its container's 15px padding, a 25% and a 75% column with 15px padding
        /// each: the bordered box in the second column is the row's 75% less 15px each side — its
        /// right edge on the container's content edge, where the reference box above it ends, and
        /// its left edge 25% of the row plus 15px in from the row's.
        /// </summary>
        [Fact]
        public void AColumnsContentStartsItsCssPaddingIn()
        {
            string html = "<html><body><div style='padding:0 15px'>" +
                "<div style='border:1px solid #f00'>REF</div>" +
                "<div style='margin:0 -15px'>" +
                "<div style='float:left;width:25%;padding:0 15px;box-sizing:border-box'>L</div>" +
                "<div style='float:left;width:75%;padding:0 15px;box-sizing:border-box'><div style='border:1px solid #00f'>X</div></div>" +
                "</div></div></body></html>";
            var xs = StrokedSegments(DecodeStreams(ToPdf(html))).Where(s => s.IsVertical).Select(s => s.X0).ToList();
            double refLeft = xs[0], refRight = xs[1];              // the reference box's two sides
            var inner = xs.Skip(2).ToList();                        // the column's box
            double half = 0.375;                                    // a 1px border is stroked half a border in
            double rowLeft = refLeft - half - 11.25, rowWidth = (refRight - refLeft + 2 * half) + 22.5;

            Assert.Equal(rowLeft + 0.25 * rowWidth + 11.25 + half, inner.Min(), 1);
            Assert.Equal(refRight, inner.Max(), 1);
        }

        /// <summary>
        /// The text beside a float the band holds — a float of two paragraphs, which no single
        /// paragraph can carry — starts at the float's edge: 100px (75pt) past the float's own
        /// left edge, no further.
        /// </summary>
        [Fact]
        public void TextBesideABandedFloatStartsAtItsEdge()
        {
            string c = DecodeStreams(ToPdf("<html><body><div style='float:left;width:100px'><p style='margin:0'>F1</p><p style='margin:0'>F2</p></div>" +
                                           "<p style='margin:0'>BESIDE</p></body></html>"));
            Assert.Equal(XOf(c, "F1") + 75, XOf(c, "BESIDE"), 1);
        }
    }
}
