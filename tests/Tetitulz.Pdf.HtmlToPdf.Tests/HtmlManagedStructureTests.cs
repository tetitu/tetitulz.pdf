// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Structural fidelity in the managed engine: block content inside a <c>&lt;td&gt;</c> or
    /// <c>&lt;li&gt;</c>, and images measured the same way they are drawn.
    /// </summary>
    public class HtmlManagedStructureTests
    {
        private static double XOfWord(string content, string word)
        {
            double x = XOf(content, word);
            Assert.False(double.IsNaN(x), $"'{word}' was never drawn");
            return x;
        }

        private static double YOfWord(string content, string word)
        {
            double y = YOf(content, word);
            Assert.False(double.IsNaN(y), $"'{word}' was never drawn");
            return y;
        }

        // ------------------------------------------------------------------ nested tables

        /// <summary>
        /// A table inside a cell keeps its structure. It used to be flattened into one run of
        /// concatenated text by the inline collector, even though the model and the renderer have
        /// supported nested tables all along.
        /// </summary>
        [Fact]
        public void NestedTable_KeepsItsOwnRowsAndCells()
        {
            string dec = DecodeStreams(ToPdf(
                "<table><tr><td>" +
                "<table><tr><td>INNERA</td><td>INNERB</td></tr>" +
                "<tr><td>INNERC</td><td>INNERD</td></tr></table>" +
                "</td></tr></table>"));

            // All four inner cells drawn, once each…
            foreach (string cell in new[] { "INNERA", "INNERB", "INNERC", "INNERD" })
                Assert.Single(Regex.Matches(dec, cell));

            // …laid out as a grid: A left of B, and C below A.
            Assert.True(XOfWord(dec, "INNERA") < XOfWord(dec, "INNERB"),
                "the nested table's columns must be side by side, not concatenated");
            Assert.True(YOfWord(dec, "INNERC") < YOfWord(dec, "INNERA"),
                "the nested table's second row must sit below the first");
        }

        [Fact]
        public void NestedTable_IsDrawnInsideItsHostCell()
        {
            string dec = DecodeStreams(ToPdf(
                "<table><tr><td>OUTERA</td>" +
                "<td><table><tr><td>NESTED</td></tr></table></td></tr></table>"));

            // The nested content belongs to the SECOND column, so it must sit to the right of the
            // first cell's text rather than being appended to it.
            Assert.True(XOfWord(dec, "NESTED") > XOfWord(dec, "OUTERA"));
        }

        [Fact]
        public void CellWithSeveralParagraphs_KeepsThemSeparate()
        {
            string dec = DecodeStreams(ToPdf(
                "<table><tr><td><p>FIRSTPARA</p><p>SECONDPARA</p></td></tr></table>"));

            // Separate paragraphs are drawn on separate lines; flattening put them on one.
            Assert.True(YOfWord(dec, "SECONDPARA") < YOfWord(dec, "FIRSTPARA"),
                "the second paragraph must be on its own line below the first");
        }

        [Fact]
        public void CellWithAHeading_KeepsTheHeadingsSize()
        {
            string dec = DecodeStreams(ToPdf("<table><tr><td><h1>BIGTITLE</h1></td></tr></table>"));

            // h1 is 24 pt; a flattened cell would draw it at the cell's 12 pt body size.
            Assert.Matches(@"/F\d+\s+24\s+Tf", dec);
        }

        [Fact]
        public void CellWithNoBlockChildren_IsUnchanged()
        {
            // The control: plain inline content must still take the simple single-paragraph path.
            string dec = DecodeStreams(ToPdf("<table><tr><td>Plain <b>text</b> here</td></tr></table>"));

            Assert.Contains("Plain", dec);
            Assert.Contains("text", dec);
            Assert.Contains("here", dec);
        }

        [Fact]
        public void Adversarial_DeeplyNestedTablesInCells_DoesNotHang()
        {
            // Walking block content inside a cell opens a new recursion path; without the depth
            // and node budget threaded through it, hostile markup would recurse unbounded.
            var sb = new StringBuilder();
            for (int i = 0; i < 400; i++) sb.Append("<table><tr><td>");
            sb.Append("DEEP");
            for (int i = 0; i < 400; i++) sb.Append("</td></tr></table>");

            var ex = Record.Exception(() => ToPdf(sb.ToString()));
            Assert.Null(ex);
        }

        // ------------------------------------------------------------------ image measurement

        /// <summary>
        /// A row must be tall enough for the image it contains. Layout skips images when wrapping
        /// lines — they are not line content — so their height has to be added when measuring, by
        /// the same rule that draws them. It was not, so the row was sized from a measurement that
        /// excluded its images and then filled with them at full height.
        /// </summary>
        [Fact]
        public void ImageInACell_MakesTheRowTallEnoughForIt()
        {
            string png = "data:image/png;base64," + Convert.ToBase64String(MakeRgbPng(4, 4));

            string withImage = DecodeStreams(ToPdf(
                $"<table><tr><td><img src=\"{png}\" width=\"80\" height=\"80\"></td></tr></table>" +
                "<p>BELOW</p>"));
            string withoutImage = DecodeStreams(ToPdf(
                "<table><tr><td></td></tr></table><p>BELOW</p>"));

            double withY = YOfWord(withImage, "BELOW");
            double withoutY = YOfWord(withoutImage, "BELOW");

            Assert.True(withY < withoutY - 60,
                $"an 80 pt image must push the table's bottom down (with={withY}, without={withoutY})");
        }

        [Fact]
        public void ImageInAParagraph_ReservesTheSameHeightItDraws()
        {
            // Two conversions differing ONLY in the declared height: the following baseline must
            // move by exactly that difference. This measures the reserve alone, independent of
            // margins and line heights.
            //
            // The expected value is 37.5 pt, not 50: an HTML width/height attribute is in CSS
            // PIXELS, and a CSS pixel is 0.75 pt. So a 50 px difference is 37.5 pt on the page.
            string png = "data:image/png;base64," + Convert.ToBase64String(MakeRgbPng(4, 4));

            double Baseline(int heightPx) => YOfWord(DecodeStreams(ToPdf(
                $"<p>ABOVE</p><p><img src=\"{png}\" width=\"40\" height=\"{heightPx}\"></p><p>BELOW</p>")), "BELOW");

            const double CssPxToPt = 0.75;
            Assert.Equal((90 - 40) * CssPxToPt, Baseline(40) - Baseline(90), 1);
        }
    }
}
