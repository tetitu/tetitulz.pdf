// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Tests for RTL/bidi/Unicode support: Hebrew/Arabic text is embedded as an
    /// Identity-H composite font and is selectable (round-trips via /ToUnicode), with
    /// the font embedded once and shared by all pages.
    /// </summary>
    public class RtlBidiTests
    {
        private const string Hebrew = "שלום עולם";
        // "house" — all three letters join, so shaping substitutes every one of them.
        private const string Arabic = "بيت";

        private static byte[] Convert(byte[] docx) =>
            DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { CompressStreams = false });

        [Fact]
        public void Hebrew_IsEmbeddedAsCompositeFont_NotDroppedToSpaces()
        {
            string pdf = Encoding.Latin1.GetString(Convert(BuildHebrewDocx(1)));

            // Composite (Type0/Identity-H) font with an embedded program + ToUnicode,
            // and the text shown as a hex glyph string — not a WinAnsi "(...)" run.
            Assert.Contains("/Type0", pdf);
            Assert.Contains("/Identity-H", pdf);
            Assert.Contains("/CIDFontType2", pdf);
            Assert.Contains("/FontFile2", pdf);
            Assert.Contains("/ToUnicode", pdf);
            Assert.Matches(@"<[0-9A-F]{8,}> Tj", pdf); // ≥2 glyphs shown as hex
        }

        [Fact]
        public void Hebrew_RoundTripsThroughToUnicode()
        {
            byte[] pdfBytes = Convert(BuildHebrewDocx(1));
            using var ms = new MemoryStream(pdfBytes);
            using var doc = PdfDocument.Open(ms);
            string extracted = PdfTextExtractor.ExtractText(doc);

            // Every distinct Hebrew letter survives extraction (ToUnicode maps the
            // glyph ids back to the Hebrew code points — proof the text is real, not blanks).
            foreach (char c in Hebrew.Where(c => c >= '֐' && c <= '׿').Distinct())
                Assert.Contains(c.ToString(), extracted);
        }

        [Fact]
        public void EmbeddedFont_IsSubsetted_SmallOutput()
        {
            // A short Hebrew document must embed only a glyph subset (a few KB), not
            // the full ~500KB font — and the descendant font references a CIDToGIDMap
            // stream (remapped), not /Identity.
            byte[] pdfBytes = Convert(BuildHebrewDocx(1));
            string pdf = Encoding.Latin1.GetString(pdfBytes);
            Assert.True(pdfBytes.Length < 80_000, $"subsetted Hebrew PDF should be small, was {pdfBytes.Length}");
            Assert.DoesNotContain("/CIDToGIDMap /Identity", pdf);
            Assert.Contains("/CIDToGIDMap", pdf);
        }

        [Fact]
        public void EmbeddedFont_IsSharedAcrossPages_NotDuplicated()
        {
            // Many Hebrew paragraphs spanning multiple pages.
            string pdf = Encoding.Latin1.GetString(Convert(BuildHebrewDocx(120)));

            int pages = Regex.Matches(pdf, "/MediaBox").Count;
            int fontFiles = Regex.Matches(pdf, "/FontFile2").Count;
            Assert.True(pages >= 2, $"expected multiple pages, got {pages}");
            Assert.Equal(1, fontFiles); // embedded once, referenced by every page
        }

        [Fact]
        public void RtlParagraph_IsRightAligned()
        {
            // A short bidi Hebrew line right-aligns: its glyph run starts well right of
            // the left margin (x ≈ ContentLeft 72 + remaining width).
            string pdf = Encoding.Latin1.GetString(Convert(BuildHebrewDocx(1)));
            var m = Regex.Match(pdf, @"BT\n(\d+(?:\.\d+)?) [\d.]+ Td\n.*?<[0-9A-F]+> Tj", RegexOptions.Singleline);
            Assert.True(m.Success, "expected a glyph line");
            double x = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(x > 200, $"RTL line should be right-aligned (x={x})");
        }

        [Fact]
        public void Formatting_UnderlineHighlightSuperscript_AreDrawn()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                body.Append(new Paragraph(new Run(
                    new RunProperties(new Underline { Val = UnderlineValues.Single }), new Text("Under"))));
                body.Append(new Paragraph(new Run(
                    new RunProperties(new Highlight { Val = HighlightColorValues.Yellow }), new Text("Mark"))));
                body.Append(new Paragraph(new Run(
                    new RunProperties(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript }),
                    new Text("Sup"))));
                main.Document = new Document(body);
                main.Document.Save();
            }
            string pdf = Encoding.Latin1.GetString(Convert(ms.ToArray()));

            Assert.Contains(" m\n", pdf);              // underline rule (path)
            Assert.Contains("1.000 1.000 0.000 rg", pdf); // yellow highlight fill
            Assert.Contains(" Ts\n", pdf);             // superscript text rise
        }

        [Fact]
        public void Hebrew_InTableCell_IsEmbedded_NotDropped()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var table = new Table(
                    new TableGrid(new GridColumn { Width = "9000" }),
                    new TableRow(new TableCell(new Paragraph(
                        new Run(new RunProperties(new RightToLeftText()), new Text(Hebrew))))));
                main.Document = new Document(new Body(table));
                main.Document.Save();
            }
            string pdf = Encoding.Latin1.GetString(Convert(ms.ToArray()));
            Assert.Contains("/Type0", pdf);
            Assert.Matches(@"<[0-9A-F]{8,}> Tj", pdf); // cell text drawn as embedded glyphs
        }

        [Fact]
        public void HyperlinkAndFieldText_AreRendered_NotDropped()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                // A hyperlink and a simple field (cached page number) — both wrap runs
                // the renderer must not skip.
                body.Append(new Paragraph(
                    new Hyperlink(new Run(new Text("ClickHere"))) { Id = "rId1" }));
                body.Append(new Paragraph(
                    new SimpleField(new Run(new Text("Page7"))) { Instruction = "PAGE" }));
                main.Document = new Document(body);
                main.Document.Save();
            }
            string pdf = Encoding.Latin1.GetString(Convert(ms.ToArray()));
            Assert.Contains("(ClickHere) Tj", pdf);
            Assert.Contains("(1) Tj", pdf);     // PAGE field computed live (page 1), not the cached "Page7"
            Assert.DoesNotContain("(Page7) Tj", pdf);
        }

        /// <summary>
        /// Arabic is <b>shaped</b> into presentation forms before being drawn, so its
        /// <c>/ToUnicode</c> has to map back to the base letters. Hebrew cannot catch this: it is
        /// reordered but never substituted, so <see cref="Hebrew_RoundTripsThroughToUnicode"/>
        /// passes whether or not the inverse mapping exists.
        /// </summary>
        /// <remarks>
        /// Without the inverse, extraction returns U+FE70-block presentation forms: a search for
        /// the word that is visibly on the page fails, and copy-paste produces code points no
        /// keyboard emits. The document still <i>looks</i> right, which is what makes this the kind
        /// of defect that ships.
        /// </remarks>
        [Fact]
        public void Arabic_RoundTripsToBaseLetters_NotPresentationForms()
        {
            using var ms = new MemoryStream(Convert(BuildArabicDocx()));
            using var doc = PdfDocument.Open(ms);
            string extracted = PdfTextExtractor.ExtractText(doc);

            foreach (char c in Arabic.Distinct())
                Assert.Contains(c.ToString(), extracted);

            Assert.DoesNotContain(extracted, c => c >= 'ﹰ' && c <= '﻿');
        }

        /// <summary>
        /// Line breaking must measure the glyphs that are actually <b>drawn</b>:
        /// <c>UnicodeText.MeasureWidth</c> and <c>UnicodeText.Shape</c> must agree.
        /// </summary>
        /// <remarks>
        /// <c>ParagraphRenderer</c> accumulates a line's width from <c>Shape(...).WidthPts</c>
        /// (shaped) and tests the next word with <c>MeasureWidth</c>; were that raw, the comparison
        /// would mix two units. Joined Arabic forms are narrower than isolated ones, so the wrap
        /// would fire early.
        /// <para><b>Asserted directly rather than through the wrap.</b> Comparing <c>Tj</c> counts
        /// between a paragraph and a doubled one bounds a ratio loose enough to absorb the
        /// error.</para>
        /// <para><b>How strong this test is depends on the host's fonts.</b> Arabic positional forms
        /// often carry the same advance as their base letter — they do in Arial, where shaped and
        /// unshaped <c>بيت</c> both measure 18.000 pt at 12 pt — so with such a font a raw
        /// measurement passes too. It goes red on any font that gives the forms distinct advances,
        /// which is common enough to be worth keeping. What a passing run proves is that measuring
        /// and drawing agree, which is the invariant the renderer depends on either way.</para>
        /// </remarks>
        [Fact]
        public void MeasuredWidth_MatchesTheWidthOfTheGlyphsThatAreDrawn()
        {
            // Font-independent: shaping must actually substitute something, or the equality below
            // is vacuous for a reason that has nothing to do with the font.
            Assert.NotEqual(Arabic, OpenTypeShaper.Shape(Arabic));

            // The renderer's own resolver, asked for the same code points the drawn run needs —
            // the shaped forms, not the base letters, since those are the glyphs that get measured.
            var font = FontResolver.Resolve("Arial", bold: false, italic: false,
                OpenTypeShaper.Shape(Arabic).Select(c => (int)c));
            Assert.True(font != null, "no installed font covers shaped Arabic on this host");

            double measured = UnicodeText.MeasureWidth(Arabic, font, 12);
            double drawn = UnicodeText.Shape(Arabic, font, 12).WidthPts;

            Assert.Equal(drawn, measured, 3);
        }

        private static byte[] BuildArabicDocx() => BuildDocx(Arabic, 1);

        private static byte[] BuildHebrewDocx(int paragraphs) => BuildDocx(Hebrew, paragraphs);

        private static byte[] BuildDocx(string text, int paragraphs)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                for (int i = 0; i < paragraphs; i++)
                {
                    var pPr = new ParagraphProperties(new BiDi());
                    var rPr = new RunProperties(new RightToLeftText(), new FontSize { Val = "32" });
                    body.Append(new Paragraph(pPr,
                        new Run(rPr, new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
                }
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }
    }
}
