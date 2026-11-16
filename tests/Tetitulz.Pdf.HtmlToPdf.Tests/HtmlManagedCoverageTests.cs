// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Coverage tests for the html2openxml-level HTML/CSS features added to the managed
    /// engine: monospace (pre/code), hr, blockquote, dl/dt/dd, table rowspan/caption/borders,
    /// ordered-list type/start, text-transform, letter-spacing, mark/highlight, named colors.
    /// Assertions inspect the PDF: font dictionaries are uncompressed (so a /Courier BaseFont
    /// proves monospace), and content-stream operators / literal show-text are read back by
    /// inflating the FlateDecode streams.
    /// </summary>
    public class HtmlManagedCoverageTests
    {
        private static HtmlToPdfOptions Managed() => new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed };

        private static byte[] Pdf(string html) => HtmlToPdfConverter.ConvertHtmlToBytes(html, Managed());

        private static int CountPages(byte[] pdf)
        {
            string raw = Encoding.Latin1.GetString(pdf);
            int n = 0, i = 0;
            while ((i = raw.IndexOf("/MediaBox", i, StringComparison.Ordinal)) >= 0) { n++; i += 9; }
            return n;
        }

        // Inflates every FlateDecode stream and concatenates the decoded bytes as Latin1, so
        // content-stream operators and literal (text) Tj show-strings can be asserted on.
        // The shared inflater (ManagedTestSupport), kerned standard-14 words joined back into their Tj.
        private static string DecodeStreams(byte[] pdf) => ManagedTestSupport.DecodeStreams(pdf);

        [Fact]
        public void Pre_RendersInCourierMonospace()
        {
            // The face the text is SET in: every page carries all twelve standard faces, so
            // "Courier" somewhere in the file passed with monospace deleted.
            byte[] pdf = Pdf("<pre>int main()</pre>");
            Assert.Equal("Courier", Tetitulz.Pdf.TestHelpers.PdfContentProbe.BaseFontOf(pdf, "int"));
        }

        [Fact]
        public void InlineCode_RendersInCourier()
        {
            byte[] pdf = Pdf("<p>Call <code>printf</code> now</p>");
            Assert.Equal("Courier", Tetitulz.Pdf.TestHelpers.PdfContentProbe.BaseFontOf(pdf, "printf"));
            Assert.Equal("Helvetica", Tetitulz.Pdf.TestHelpers.PdfContentProbe.BaseFontOf(pdf, "Call"));   // the control
        }

        [Fact]
        public void Pre_PreservesLineBreaks_MultipleLines()
        {
            // Two source lines in a <pre> should both render (line break preserved).
            string dec = DecodeStreams(Pdf("<pre>line1\nline2</pre>"));
            Assert.Contains("line1", dec);
            Assert.Contains("line2", dec);
        }

        [Fact]
        public void TextTransformUppercase_Uppercases()
        {
            string dec = DecodeStreams(Pdf("<p style=\"text-transform:uppercase\">hello</p>"));
            Assert.Contains("HELLO", dec);
        }

        [Fact]
        public void LetterSpacing_EmitsCharSpacingOperator()
        {
            string dec = DecodeStreams(Pdf("<p style=\"letter-spacing:2px\">spaced</p>"));
            Assert.Contains("Tc", dec); // PDF char-spacing operator
        }

        [Fact]
        public void Hr_ProducesSingleValidPage_WithSurroundingText()
        {
            byte[] pdf = Pdf("<p>Above</p><hr><p>Below</p>");
            Assert.Equal((byte)'%', pdf[0]);
            Assert.Equal(1, CountPages(pdf));
            string dec = DecodeStreams(pdf);
            Assert.Contains("Above", dec);
            Assert.Contains("Below", dec);
        }

        [Fact]
        public void Blockquote_TextRendered()
        {
            string dec = DecodeStreams(Pdf("<blockquote>Quoted</blockquote>"));
            Assert.Contains("Quoted", dec);
        }

        [Fact]
        public void DefinitionList_TermsAndDefinitionsRendered()
        {
            string dec = DecodeStreams(Pdf("<dl><dt>Termo</dt><dd>Defn</dd></dl>"));
            Assert.Contains("Termo", dec);
            Assert.Contains("Defn", dec);
        }

        [Fact]
        public void Table_Rowspan_AllCellsRendered()
        {
            // The first cell spans two rows; every cell's text must still render.
            byte[] pdf = Pdf(
                "<table border=\"1\">" +
                "<tr><td rowspan=\"2\">Spanned</td><td>R1</td></tr>" +
                "<tr><td>R2</td></tr></table>");
            Assert.Equal((byte)'%', pdf[0]);
            string dec = DecodeStreams(pdf);
            Assert.Contains("Spanned", dec);
            Assert.Contains("R1", dec);
            Assert.Contains("R2", dec);
        }

        [Fact]
        public void Table_Caption_Rendered()
        {
            string dec = DecodeStreams(Pdf("<table><caption>MyCaption</caption><tr><td>x</td></tr></table>"));
            Assert.Contains("MyCaption", dec);
        }

        [Fact]
        public void OrderedList_TypeAlpha_UsesLetters()
        {
            string dec = DecodeStreams(Pdf("<ol type=\"a\"><li>One</li><li>Two</li></ol>"));
            Assert.Contains("a.", dec);
            Assert.Contains("b.", dec);
        }

        [Fact]
        public void OrderedList_StartAttribute_Respected()
        {
            string dec = DecodeStreams(Pdf("<ol start=\"5\"><li>Five</li></ol>"));
            Assert.Contains("5.", dec);
        }

        [Fact]
        public void NamedColor_ResolvesToFill()
        {
            // rebeccapurple = #663399 → fill components 0.4 0.2 0.6.
            string dec = DecodeStreams(Pdf("<p style=\"color:rebeccapurple\">Purple</p>"));
            Assert.Contains("Purple", dec);
            Assert.Contains("0.4 0.2 0.6", dec);
        }

        [Fact]
        public void Mark_TextRendered()
        {
            string dec = DecodeStreams(Pdf("<p>See <mark>Highlighted</mark> text</p>"));
            Assert.Contains("Highlighted", dec);
        }

        [Fact]
        public void NestedList_ParentItemDoesNotDuplicateChildText()
        {
            string dec = DecodeStreams(Pdf("<ul><li>parentitem<ul><li>kiditem</li></ul></li></ul>"));
            Assert.Contains("parentitem", dec);
            Assert.Contains("kiditem", dec);
            // 'kiditem' must render once (the nested item only), not also leak into the parent.
            int i = 0, c = 0;
            while ((i = dec.IndexOf("kiditem", i, StringComparison.Ordinal)) >= 0) { c++; i += 7; }
            Assert.Equal(1, c);
        }
    }
}
