// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>line-height</c> inherits, and a table cell's padding is what its CSS says — the two
    /// reasons a Bootstrap table row was 50px in the managed engine and 37px in Chrome.
    /// </summary>
    /// <remarks>
    /// Read as the distance between two baselines: two lines of one paragraph (<c>A&lt;br&gt;B</c>)
    /// are one line height apart; the first lines of two table rows, one row pitch.
    /// </remarks>
    public class LineHeightAndCellPaddingTests
    {
        private static double Gap(string html, string first = "A", string second = "B")
        {
            string content = DecodeStreams(ToPdf(html));
            double a = YOf(content, first), b = YOf(content, second);
            Assert.False(double.IsNaN(a), first + " was not drawn");
            Assert.False(double.IsNaN(b), second + " was not drawn");
            return a - b;
        }

        private static string Page(string css, string body) =>
            $"<html><head><style>{css}</style></head><body>{body}</body></html>";

        // ------------------------------------------------------------------ line-height inherits

        /// <summary>The distance between two lines of 10pt text with no <c>line-height</c> anywhere — <c>normal</c>.</summary>
        private static double NormalGap() => Gap(Page("body{font-size:10pt}", "<p>A<br>B</p>"));

        /// <summary>
        /// A <c>line-height</c> declared on <c>body</c> reaches a paragraph that declares none — it
        /// was read off the paragraph's own element only, so the line stayed <c>normal</c> (the
        /// control, which must differ for the first assertion to mean anything).
        /// </summary>
        [Fact]
        public void ALineHeightOnTheBodyReachesAParagraph()
        {
            Assert.Equal(20, Gap(Page("body{font-size:10pt;line-height:2}", "<p>A<br>B</p>")), 2);
            Assert.NotEqual(20, NormalGap(), 2);
        }

        /// <summary>
        /// A unitless line height inherits as the number: a descendant with a larger font multiplies
        /// its own size — 2 × 15pt, not the body's 2 × 10pt.
        /// </summary>
        [Fact]
        public void AUnitlessLineHeightScalesWithTheDescendantsFont()
            => Assert.Equal(30, Gap(Page("body{font-size:10pt;line-height:2} p{font-size:15pt}", "<p>A<br>B</p>")), 2);

        /// <summary>A length inherits as the length: 18pt, whatever the descendant's font size.</summary>
        [Fact]
        public void ALengthLineHeightInheritsAsItsLength()
            => Assert.Equal(18, Gap(Page("body{font-size:10pt;line-height:18pt} p{font-size:15pt}", "<p>A<br>B</p>")), 2);

        /// <summary>A length on a descendant replaces a unitless height it inherited: 18pt, not 2 × its font.</summary>
        [Fact]
        public void ALengthOnADescendantReplacesAnInheritedFactor()
            => Assert.Equal(18, Gap(Page("body{font-size:10pt;line-height:2} p{line-height:18pt}", "<p>A<br>B</p>")), 2);

        /// <summary>
        /// A cell's text starts its CSS padding in from the cell's edge and no further: a 20px left
        /// padding puts it 15pt right of a paragraph's text just above the table (whose own left
        /// edge is the paragraph's).
        /// </summary>
        [Fact]
        public void ACellsTextStartsItsPaddingIn()
        {
            string content = DecodeStreams(ToPdf(Page("table{border-collapse:collapse}",
                "<p>P</p><table><tr><td style='padding:0 0 0 20px'>A</td></tr></table>")));
            Assert.Equal(15, XOf(content, "A") - XOf(content, "P"), 2);
        }

        /// <summary><c>normal</c> on a descendant ends an inherited line height: the line is as if none were declared.</summary>
        [Fact]
        public void NormalOnADescendantEndsTheInheritedLineHeight()
            => Assert.Equal(NormalGap(), Gap(Page("body{font-size:10pt;line-height:2} p{line-height:normal}", "<p>A<br>B</p>")), 2);

        /// <summary>A table cell's text inherits the line height too.</summary>
        [Fact]
        public void ATableCellInheritsTheLineHeight()
            => Assert.Equal(20, Gap(Page("body{font-size:10pt;line-height:2}", "<table><tr><td>A<br>B</td></tr></table>")), 2);

        // ------------------------------------------------------------------ cell padding is exact

        // Unspaced, so a row's pitch is the row alone (BorderSpacingTests has the spacing).
        private const string Rows = "<table style='border-spacing:0'><tr><td>A</td></tr><tr><td>B</td></tr></table>";

        /// <summary>
        /// Two rows are one line and the cell's CSS padding apart — 15pt and 2 × 8px — with nothing
        /// added: not the renderer's own 3pt a side, not a paragraph's space after. Chrome's 37px
        /// Bootstrap row is this plus its 1px border.
        /// </summary>
        [Fact]
        public void ARowIsItsLineAndItsCssPadding()
            => Assert.Equal(15 + 12, Gap(Page("body{font-size:10.5pt;line-height:15pt} td{padding:8px}", Rows)), 2);

        /// <summary>A cell with no padding declared gets a browser's 1px a side.</summary>
        [Fact]
        public void ACellWithoutPaddingGetsOnePixel()
            => Assert.Equal(15 + 1.5, Gap(Page("body{font-size:10.5pt;line-height:15pt}", Rows)), 2);

        /// <summary>The table's <c>cellpadding</c> attribute pads a cell that declares none.</summary>
        [Fact]
        public void TheCellpaddingAttributePadsTheCells()
            => Assert.Equal(15 + 6, Gap(Page("body{font-size:10.5pt;line-height:15pt}",
                   "<table cellpadding='4' cellspacing='0'><tr><td>A</td></tr><tr><td>B</td></tr></table>")), 2);

        /// <summary>
        /// A compact table's row is as tall as its content — a 10pt line with no padding is 10pt,
        /// where the renderer's 16pt floor used to hold it.
        /// </summary>
        [Fact]
        public void ACompactRowIsOnlyAsTallAsItsContent()
            => Assert.Equal(10, Gap(Page("body{font-size:8pt;line-height:10pt} td{padding:0}", Rows)), 2);

        // ------------------------------------------------------------------ borders take room

        /// <summary>
        /// A collapsed border between two rows is one border, and the row pitch holds it: line,
        /// padding and 0.75pt — Chrome's 37px Bootstrap row, where it was a pixel short.
        /// </summary>
        [Fact]
        public void ACollapsedRowBoundaryAddsOneBorder()
            => Assert.Equal(15 + 12 + 0.75, Gap(Page(
                   "body{font-size:10.5pt;line-height:15pt} table{border-collapse:collapse} td{padding:8px;border:1px solid #000}", Rows)), 2);

        /// <summary>Separated borders (HTML's default) are each cell's own: a row boundary holds two.</summary>
        [Fact]
        public void SeparatedRowBordersAreEachCellsOwn()
            => Assert.Equal(15 + 12 + 1.5, Gap(Page(
                   "body{font-size:10.5pt;line-height:15pt} td{padding:8px;border:1px solid #000}", Rows)), 2);

        /// <summary>
        /// A cell's text starts half a collapsed border in from the cell's edge, past its padding:
        /// with a 4px border it is 1.5pt further right than with none (fixed layout, so the column
        /// itself stays put).
        /// </summary>
        [Fact]
        public void TextStartsHalfACollapsedBorderIn()
        {
            static double X(string td) => XOf(DecodeStreams(ToPdf(Page(
                "table{border-collapse:collapse;table-layout:fixed} td{padding:0" + td + "}",
                "<table><tr><td>A</td><td>B</td></tr></table>"))), "A");
            Assert.Equal(X("") + 1.5, X(";border:4px solid #000"), 2);
        }

        /// <summary>
        /// A plain table draws no border round its cells, as a browser draws none; the legacy
        /// <c>border="1"</c> attribute does rule them (the control) — and, its cells spaced by the
        /// browser's 2px, frames the table apart from them.
        /// </summary>
        [Fact]
        public void APlainTableDrawsNoGrid()
        {
            string plain = DecodeStreams(ToPdf("<table><tr><td>A</td></tr></table>"));
            string ruled = DecodeStreams(ToPdf("<table border='1'><tr><td>A</td></tr></table>"));
            Assert.Empty(StrokedSegments(plain));
            Assert.Equal(8, StrokedSegments(ruled).Count);   // the table's frame and the one cell's four edges
        }
    }
}
