// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>border-spacing</c> in the separated border model (HTML's default): a browser sets a
    /// table's cells 2px apart unless told otherwise, and the managed engine set them edge to edge —
    /// a row a pixel and a half short of Chrome's, ten rows down a line out of place.
    /// </summary>
    public class BorderSpacingTests
    {
        private const string Rows = "<tr><td>A</td></tr><tr><td>B</td></tr>";

        /// <summary>The distance between two rows' text, in a table styled by <paramref name="css"/> and opened by <paramref name="tableTag"/>.</summary>
        private static double Pitch(string css, string tableTag = "<table>")
        {
            string c = DecodeStreams(ToPdf("<html><head><style>body{font-size:10.5pt;line-height:15pt} " + css +
                "</style></head><body>" + tableTag + Rows + "</table></body></html>"));
            return YOf(c, "A") - YOf(c, "B");
        }

        /// <summary>A table stating nothing spaces its rows 2px (1.5pt) apart; stated 0, not at all.</summary>
        [Fact]
        public void TheBrowsersTwoPixelsByDefault()
            => Assert.Equal(Pitch("table{border-spacing:0}") + 1.5, Pitch(""), 2);

        /// <summary>One length spaces both ways: 10px moves the next row 7.5pt.</summary>
        [Fact]
        public void OneLengthSpacesTheRows()
            => Assert.Equal(Pitch("table{border-spacing:0}") + 7.5, Pitch("table{border-spacing:10px}"), 2);

        /// <summary>Two lengths are the horizontal then the vertical: <c>10px 0</c> leaves the rows touching, <c>0 10px</c> spaces them.</summary>
        [Fact]
        public void TwoLengthsAreHorizontalThenVertical()
        {
            double flush = Pitch("table{border-spacing:0}");
            Assert.Equal(flush, Pitch("table{border-spacing:10px 0}"), 2);
            Assert.Equal(flush + 7.5, Pitch("table{border-spacing:0 10px}"), 2);
        }

        /// <summary>The horizontal length sets a cell's text in from the table's edge: 10px is 7.5pt further right than 0.</summary>
        [Fact]
        public void TheHorizontalLengthSetsTheCellsIn()
        {
            static double X(string spacing)
            {
                string c = DecodeStreams(ToPdf("<p>P</p><table style='border-spacing:" + spacing + "'><tr><td>A</td></tr></table>"));
                return XOf(c, "A") - XOf(c, "P");
            }
            Assert.Equal(X("0") + 7.5, X("10px 0"), 2);
        }

        /// <summary>A negative length is invalid, and the declaration is ignored: the browser's 2px, not 0 and not less.</summary>
        [Fact]
        public void ANegativeLengthIsIgnored()
            => Assert.Equal(Pitch(""), Pitch("table{border-spacing:-10px}"), 2);

        /// <summary>
        /// A cell stating a border thinner than the table's keeps it when the cells are spaced — the
        /// table's frame is apart from them — and gives its outer edges to the frame when they are not
        /// (the control): four red edges a cell, two cells.
        /// </summary>
        [Fact]
        public void ASpacedCellKeepsItsOwnBorder()
        {
            static int RedEdges(string spacing) => System.Text.RegularExpressions.Regex.Matches(DecodeStreams(ToPdf(
                "<html><head><style>table{border:2px solid #000;border-spacing:" + spacing + "} td{border:1px solid #f00}</style></head>" +
                "<body><table><tr><td>A</td><td>B</td></tr></table></body></html>")), @"\b1 0 0 RG\b").Count;
            Assert.Equal(8, RedEdges("10px"));
            Assert.True(RedEdges("0") < 8, "unspaced, the frame did not take the outer edges, so this proves nothing");
        }

        /// <summary>The collapsed model has no spacing: a stated <c>border-spacing</c> is ignored there.</summary>
        [Fact]
        public void TheCollapsedModelHasNone()
            => Assert.Equal(Pitch("table{border-spacing:0}"), Pitch("table{border-collapse:collapse;border-spacing:10px}"), 2);

        /// <summary>The <c>cellspacing</c> attribute spaces a table that states no CSS: 8px is 6pt.</summary>
        [Fact]
        public void TheCellspacingAttributeSpacesTheCells()
            => Assert.Equal(Pitch("table{border-spacing:0}") + 6, Pitch("", "<table cellspacing='8'>"), 2);

        /// <summary>
        /// A table framed by its CSS border, its cells spaced, is framed at its own edge: two vertical
        /// rules, not the outer cells' edges; with the cells unspaced the frame is on them (the same two
        /// rules — the control that the frame is not lost).
        /// </summary>
        [Fact]
        public void ASpacedTablesFrameIsItsOwn()
        {
            static System.Collections.Generic.List<double> Rules(string spacing) =>
                StrokedSegments(DecodeStreams(ToPdf("<table style='width:100%;border:1px solid #000;border-spacing:" + spacing +
                    "'><tr><td>A</td><td>B</td></tr></table>")))
                .Where(s => s.IsVertical).Select(s => s.X0).Distinct().OrderBy(x => x).ToList();
            var spaced = Rules("10px");
            Assert.Equal(2, spaced.Count);
            Assert.Equal(spaced, Rules("0"));
        }
    }
}
