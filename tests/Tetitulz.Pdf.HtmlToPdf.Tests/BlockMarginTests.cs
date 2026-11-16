// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A block's vertical space is its CSS margins, else the browser's defaults for its tag, and
    /// margins that meet collapse to the larger — where every block had a flat 6pt after it and
    /// adjacent margins were added.
    /// </summary>
    /// <remarks>
    /// Read as the distance between two baselines. The texts are one line each at the default
    /// 12pt, so a gap is the line plus the space between the blocks.
    /// </remarks>
    public class BlockMarginTests
    {
        private static double Gap(string body, string first, string second)
        {
            string c = DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));
            double a = YOf(c, first), b = YOf(c, second);
            Assert.False(double.IsNaN(a) || double.IsNaN(b), "a text was not drawn");
            return a - b;
        }

        /// <summary>Two divs have no space between them: the line alone.</summary>
        private static double Line() => Gap("<div>A</div><div>B</div>", "A", "B");

        /// <summary>
        /// Two paragraphs are 1em apart — 12pt, their margins collapsed, not 24 — where two divs
        /// have none (the control, which also shows the flat 6pt is gone).
        /// </summary>
        [Fact]
        public void ParagraphsAreOneEmApartAndDivsNone()
        {
            Assert.Equal(Line() + 12, Gap("<p>A</p><p>B</p>", "A", "B"), 2);
            Assert.Equal(Line() + 12, Gap("<div>A</div><p>B</p>", "A", "B"), 2);
        }

        /// <summary>A margin the page sets replaces the browser's default: <c>margin: 0</c> leaves the line.</summary>
        [Fact]
        public void ADeclaredMarginReplacesTheDefault()
        {
            Assert.Equal(Line(), Gap("<p style='margin:0'>A</p><p style='margin:0'>B</p>", "A", "B"), 2);
            Assert.Equal(Line() + 15, Gap("<p style='margin-bottom:20px'>A</p><div>B</div>", "A", "B"), 2);
        }

        /// <summary>
        /// Adjacent margins collapse to the larger: 30pt after, 12pt before, is 30pt apart; and
        /// the other way round.
        /// </summary>
        [Fact]
        public void AdjacentMarginsCollapseToTheLarger()
        {
            Assert.Equal(Line() + 30, Gap("<p style='margin-bottom:40px'>A</p><p>B</p>", "A", "B"), 2);
            Assert.Equal(Line() + 30, Gap("<p>A</p><p style='margin-top:40px'>B</p>", "A", "B"), 2);
        }

        /// <summary>
        /// A heading's default margin is the UA stylesheet's, in its own ems — of its own size, the
        /// UA's em of the 12pt body: the space before it, less the same heading with <c>margin: 0</c>.
        /// </summary>
        [Theory]
        [InlineData("h1", 0.67 * 2.00 * 12)]
        [InlineData("h2", 0.83 * 1.50 * 12)]
        [InlineData("h3", 1.00 * 1.17 * 12)]
        [InlineData("h4", 1.33 * 1.00 * 12)]
        [InlineData("h5", 1.67 * 0.83 * 12)]
        [InlineData("h6", 2.33 * 0.67 * 12)]
        public void AHeadingHasTheBrowsersDefaultMargin(string tag, double marginPt)
        {
            double spaced = Gap($"<div>A</div><{tag}>B</{tag}>", "A", "B");
            double flush = Gap($"<div>A</div><{tag} style='margin:0'>B</{tag}>", "A", "B");
            Assert.Equal(marginPt, spaced - flush, 2);
        }

        /// <summary>
        /// A box's top margin collapses with the paragraph before it (30pt beats 12pt), and its
        /// border keeps its first paragraph's margin inside it: 30pt, the 0.75pt border, 12pt.
        /// </summary>
        [Fact]
        public void ABoxsMarginCollapsesOutsideItsBorderAndNotInside()
            => Assert.Equal(Line() + 30 + 0.75 + 12,
                   Gap("<p>A</p><div style='margin-top:40px;border-top:1px solid #000'><p>B</p></div>", "A", "B"), 2);

        /// <summary>
        /// A paragraph's padding does not collapse: under 7.5pt of it, its 12pt margin still meets
        /// the next one's 30pt — line, padding, 30.
        /// </summary>
        [Fact]
        public void PaddingDoesNotCollapseWithTheNextMargin()
            => Assert.Equal(Line() + 7.5 + 30,
                   Gap("<p style='padding-bottom:10px'>A</p><p style='margin-top:40px'>B</p>", "A", "B"), 2);

        /// <summary>Margins collapse inside a table cell too: two paragraphs in one are 12pt apart, not 24.</summary>
        [Fact]
        public void MarginsCollapseInsideATableCell()
        {
            double divs = Gap("<table><tr><td><div>A</div><div>B</div></td></tr></table>", "A", "B");
            Assert.Equal(divs + 12, Gap("<table><tr><td><p>A</p><p>B</p></td></tr></table>", "A", "B"), 2);
        }

        /// <summary>
        /// A float's margins do not collapse with its neighbours' (CSS 2.1 §8.3.1): a floated
        /// paragraph after one sits both 12pt margins below it.
        /// </summary>
        [Fact]
        public void AFloatsMarginDoesNotCollapse()
            => Assert.Equal(Line() + 12 + 12,
                   Gap("<p>A</p><p style='float:right;width:100px'>F</p><p>B</p>", "A", "F"), 2);

        /// <summary>
        /// A table's CSS margins are space round it, and collapse with its neighbours': 20px below
        /// a table meets the next paragraph's 12pt as 15pt. A table with none has no space after it
        /// (the renderer's old 4pt is not HTML's) — the control.
        /// </summary>
        [Fact]
        public void ATablesMarginsAreSpaceAndCollapse()
        {
            const string Row = "<tr><td style='padding:0'>A</td></tr>";
            double bare = Gap($"<table>{Row}</table><div>B</div>", "A", "B");
            Assert.Equal(bare + 15, Gap($"<table style='margin-bottom:20px'>{Row}</table><div>B</div>", "A", "B"), 2);
            Assert.Equal(bare + 15, Gap($"<table style='margin-bottom:20px'>{Row}</table><p>B</p>", "A", "B"), 2);
            Assert.Equal(bare + 12, Gap($"<table>{Row}</table><p>B</p>", "A", "B"), 2);
        }

        /// <summary>
        /// A table's top margin is space above it and meets the paragraph's before it: 40px (30pt)
        /// under a paragraph's 12pt is 18pt more than a table with none.
        /// </summary>
        [Fact]
        public void ATablesTopMarginMeetsTheParagraphAbove()
        {
            const string Row = "<tr><td style='padding:0'>B</td></tr>";
            double bare = Gap($"<p>A</p><table>{Row}</table>", "A", "B");
            Assert.Equal(bare + 18, Gap($"<p>A</p><table style='margin-top:40px'>{Row}</table>", "A", "B"), 2);
        }

        /// <summary>
        /// A heading's size is in ems of its parent's font, as the UA stylesheet's is: an h2 under a
        /// 20pt parent is 30pt, its lines 20/12 as far apart as under the 12pt body. It was a fixed
        /// 20pt wherever it stood.
        /// </summary>
        [Fact]
        public void AHeadingScalesWithItsParentsFont()
        {
            double body = Gap("<h2>A<br>B</h2>", "A", "B");
            double large = Gap("<div style='font-size:20pt'><h2>A<br>B</h2></div>", "A", "B");
            Assert.Equal(20.0 / 12, large / body, 3);
        }

        /// <summary>
        /// A negative margin collapses by CSS's rule — the largest positive plus the most negative:
        /// 12pt above meeting −15pt is −3pt, 30pt meeting −15pt is 15pt.
        /// </summary>
        [Fact]
        public void ANegativeMarginCollapsesWithAPositiveOne()
        {
            Assert.Equal(Line() - 3, Gap("<p>A</p><p style='margin-top:-20px'>B</p>", "A", "B"), 2);
            Assert.Equal(Line() + 15, Gap("<p style='margin-bottom:40px'>A</p><p style='margin-top:-20px'>B</p>", "A", "B"), 2);
        }

        /// <summary>
        /// A negative horizontal margin on a paragraph is not an indent: the text starts its padding
        /// in, as with no margin (negative indents are not supported).
        /// </summary>
        [Fact]
        public void ANegativeHorizontalMarginIsNotAnIndent()
        {
            string negative = DecodeStreams(ToPdf("<html><body><p style='margin-left:-10px;padding-left:20px'>A</p></body></html>"));
            string plain = DecodeStreams(ToPdf("<html><body><p style='padding-left:20px'>A</p></body></html>"));
            Assert.Equal(XOf(plain, "A"), XOf(negative, "A"), 2);
        }

        /// <summary>Two negative margins that meet are the more negative: −7.5pt and −15pt is −15pt, not their sum.</summary>
        [Fact]
        public void TwoNegativeMarginsAreTheMoreNegative()
            => Assert.Equal(Line() - 15,
                   Gap("<p style='margin-bottom:-10px'>A</p><p style='margin-top:-20px'>B</p>", "A", "B"), 2);

        /// <summary>
        /// A container whose only box property is a negative margin still takes it: 12pt above
        /// meeting its −15pt is −3pt.
        /// </summary>
        [Fact]
        public void AContainerWithOnlyANegativeMarginTakesIt()
            => Assert.Equal(Line() - 3,
                   Gap("<p>A</p><div style='margin-top:-20px'><p style='margin:0'>X</p></div>", "A", "X"), 2);

        /// <summary>
        /// Two bordered paragraphs' margins collapse between their borders: 3pt borders add 6pt to
        /// the gap — one under the first, one over the second — and the collapsed margin is the
        /// paragraphs' 12pt, not more.
        /// </summary>
        [Fact]
        public void BordersSitOutsideTheCollapsedMargin()
        {
            double plain = Gap("<p>A</p><p>B</p>", "A", "B");
            Assert.Equal(plain + 6, Gap("<p style='border:4px solid #000'>A</p><p style='border:4px solid #000'>B</p>", "A", "B"), 2);
        }
    }
}
