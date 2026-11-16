// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A table's own border goes round the whole table, as Chrome draws it: along a row with fewer
    /// cells than the header (a column a script moved), and over a cell edge a stylesheet zeroed
    /// (Bootstrap's first header row has <c>border-top: 0</c>).
    /// </summary>
    public class TableFrameTests
    {
        private const string Framed = "border:1px solid #000;border-collapse:collapse;width:100%";
        private const string Unframed = "border-collapse:collapse;width:100%";
        private const string Cell = "border:1px solid #000";

        private static (string Content, List<Tetitulz.Pdf.TestHelpers.PdfContentProbe.Segment> Segments) Render(string html)
        {
            string c = DecodeStreams(ToPdf(html));
            return (c, StrokedSegments(c));
        }

        /// <summary>
        /// Under a two-cell header, a body row of one cell: the frame's right edge runs down beside
        /// the body row too — at the table's right, level with the body text — where the missing
        /// cell was. Without a table border there is no frame, and nothing is drawn there (the
        /// control).
        /// </summary>
        [Fact]
        public void TheFrameRunsAlongARowShorterThanTheHeader()
        {
            string Table(string style) =>
                $"<table style='{style}'><thead><tr><th style='{Cell}'>H</th><th style='{Cell}'>K</th></tr></thead>" +
                $"<tbody><tr><td style='{Cell}'>A</td></tr></tbody></table>";

            static bool EdgeBesideTheBodyRow((string Content, List<Tetitulz.Pdf.TestHelpers.PdfContentProbe.Segment> Segments) r)
            {
                double right = r.Segments.Max(s => Math.Max(s.X0, s.X1));
                double bodyY = YOf(r.Content, "A");
                return r.Segments.Any(s => s.IsVertical && Math.Abs(s.X0 - right) < 0.5
                                           && Math.Min(s.Y0, s.Y1) < bodyY && Math.Max(s.Y0, s.Y1) > bodyY);
            }

            Assert.True(EdgeBesideTheBodyRow(Render(Table(Framed))), "the frame is open beside the body row");
            Assert.False(EdgeBesideTheBodyRow(Render(Table(Unframed))), "an unframed table drew an edge where no cell is");
        }

        /// <summary>
        /// A header cell whose stylesheet zeroes its top border still has the table's frame above
        /// it; in a table with no border of its own, the zeroed edge is gone (the control).
        /// </summary>
        [Theory]
        [InlineData("ltr")]
        [InlineData("rtl")]
        public void TheFrameCoversAZeroedOuterEdge(string dir)
        {
            string Table(string style) =>
                $"<html dir='{dir}'><body><table style='{style}'><tr>" +
                $"<th style='{Cell};border-top:0'>H</th><th style='{Cell};border-top:0'>K</th></tr></table></body></html>";

            static bool EdgeAbove((string Content, List<Tetitulz.Pdf.TestHelpers.PdfContentProbe.Segment> Segments) r, string text)
            {
                double x = XOf(r.Content, text), y = YOf(r.Content, text);
                return r.Segments.Any(s => s.IsHorizontal && s.Y0 > y
                                           && Math.Min(s.X0, s.X1) <= x && Math.Max(s.X0, s.X1) >= x);
            }

            var framed = Render(Table(Framed));
            var unframed = Render(Table(Unframed));
            Assert.True(EdgeAbove(framed, "H") && EdgeAbove(framed, "K"), "the frame is missing above a header cell");
            Assert.False(EdgeAbove(unframed, "H") || EdgeAbove(unframed, "K"), "a zeroed edge was drawn with no frame to draw it");
        }

        /// <summary>
        /// The frame's side edges are on the physical side the columns run from: in a right-to-left
        /// table the first cell's zeroed right edge is framed, and in a left-to-right one its left.
        /// </summary>
        [Theory]
        [InlineData("ltr", "border-left:0")]
        [InlineData("rtl", "border-right:0")]
        public void TheFrameCoversTheFirstCellsOuterSide(string dir, string zeroed)
        {
            string html = $"<html dir='{dir}'><body><table style='{Framed}'><tr>" +
                          $"<td style='{Cell};{zeroed}'>A</td><td style='{Cell}'>B</td></tr></table></body></html>";
            var r = Render(html);
            double outer = dir == "rtl" ? r.Segments.Max(s => Math.Max(s.X0, s.X1)) : r.Segments.Min(s => Math.Min(s.X0, s.X1));
            double y = YOf(r.Content, "A");
            Assert.Contains(r.Segments, s => s.IsVertical && Math.Abs(s.X0 - outer) < 0.5
                                             && Math.Min(s.Y0, s.Y1) < y && Math.Max(s.Y0, s.Y1) > y);
            Assert.True(dir == "rtl" ? XOf(r.Content, "A") > XOf(r.Content, "B") : XOf(r.Content, "A") < XOf(r.Content, "B"),
                "the first cell is not on the side its direction starts from");
        }
    }
}
