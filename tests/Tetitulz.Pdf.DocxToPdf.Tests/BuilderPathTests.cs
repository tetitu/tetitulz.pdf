// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using BuilderExport = Tetitulz.Pdf.Builder.Export;
using BuilderModel = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Exercises the centralized Builder render path (UseLegacyRenderer = false): the DOCX
    /// is mapped to the Tetitulz.Pdf.Builder document model and rendered by the shared PDF
    /// exporter. The default path (legacy renderer) is covered by the other test files.
    /// </summary>
    public class BuilderPathTests
    {
        private static readonly DocxToPdfOptions ViaBuilder =
            new DocxToPdfOptions { UseLegacyRenderer = false, CompressStreams = false };

        private static byte[] Convert(Action<Body> build, DocxToPdfOptions options)
            => Convert((main, body) => build(body), options);

        private static byte[] Convert(Action<MainDocumentPart, Body> build, DocxToPdfOptions options)
            => DocxToPdfConverter.ConvertToBytes(BuildDocx(build), options);

        // The DOCX on its own, so a test can drive the two halves of the Builder path
        // (OOXML -> Builder model, Builder model -> PDF) separately.
        private static byte[] BuildDocx(Action<MainDocumentPart, Body> build)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                build(main, body);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        // Inflates every FlateDecode content stream so ASCII operators can be asserted on.
        private static string InflateContent(byte[] pdf)
        {
            string raw = Encoding.Latin1.GetString(pdf);
            var sb = new System.Text.StringBuilder();
            int idx = 0;
            while (true)
            {
                int s = raw.IndexOf("stream", idx, StringComparison.Ordinal);
                if (s < 0) break;
                if (s >= 3 && raw.Substring(s - 3, 3) == "end") { idx = s + 6; continue; }
                int d = s + 6;
                if (d < raw.Length && raw[d] == '\r') d++;
                if (d < raw.Length && raw[d] == '\n') d++;
                int e = raw.IndexOf("endstream", d, StringComparison.Ordinal);
                if (e < 0) break;
                int len = e - d;
                while (len > 0 && (raw[d + len - 1] == '\n' || raw[d + len - 1] == '\r')) len--;
                byte[] data = Encoding.Latin1.GetBytes(raw.Substring(d, len));
                try
                {
                    using var inMs = new MemoryStream(data);
                    using var z = new System.IO.Compression.ZLibStream(inMs, System.IO.Compression.CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    z.CopyTo(outMs);
                    sb.Append(Encoding.Latin1.GetString(outMs.ToArray()));
                }
                catch { }
                idx = e + 9;
            }
            return sb.ToString();
        }

        private static int CountPages(byte[] pdf)
        {
            string raw = Encoding.Latin1.GetString(pdf);
            int n = 0, i = 0;
            while ((i = raw.IndexOf("/MediaBox", i, StringComparison.Ordinal)) >= 0) { n++; i += 9; }
            return n;
        }

        [Fact]
        public void BuilderPath_SimpleParagraph_ProducesValidPdf()
        {
            byte[] pdf = Convert(body => body.Append(
                new Paragraph(new Run(new Text("Hello from the Builder path")))), ViaBuilder);
            Assert.True(pdf.Length > 200);
            Assert.Equal((byte)'%', pdf[0]);
            Assert.Contains("%PDF-1.4", Encoding.Latin1.GetString(pdf, 0, 16));
            Assert.Equal("Helvetica", PdfContentProbe.BaseFontOf(pdf, "Hello"));   // the face it is set in
        }

        [Fact]
        public void BuilderPath_Table_Renders()
        {
            byte[] pdf = Convert(body =>
            {
                var table = new Table();
                var row = new TableRow();
                row.Append(new TableCell(new Paragraph(new Run(new Text("Item")))));
                row.Append(new TableCell(new Paragraph(new Run(new Text("Price")))));
                table.Append(row);
                body.Append(table);
            }, ViaBuilder);
            Assert.True(pdf.Length > 200);
            Assert.Equal((byte)'%', pdf[0]);
        }

        [Fact]
        public void BuilderPath_LongDocument_Paginates()
        {
            byte[] pdf = Convert(body =>
            {
                for (int i = 0; i < 150; i++)
                    body.Append(new Paragraph(new Run(new Text($"Line {i} of a long document."))));
            }, ViaBuilder);
            Assert.True(CountPages(pdf) >= 2, $"expected pagination, got {CountPages(pdf)} pages");
        }

        private const string Hebrew = "שלום עולם";   // "hello world"

        private static void HebrewParagraph(Body body) =>
            body.Append(new Paragraph(
                new ParagraphProperties(new BiDi()),
                new Run(new RunProperties(new RightToLeftText()), new Text(Hebrew))));

        /// <summary>
        /// The whole <c>ConvertToBytes</c> entry point, which reaches
        /// <c>Document.SaveAsPdf(Stream)</c> and therefore takes default export options — there is
        /// no seam through which a font can be supplied, so this one genuinely needs a host font
        /// and is <b>skipped</b>, visibly, where there is none.
        ///
        /// <para>Not <c>if (!HasHebrewFont()) return;</c>, which reports <i>Passed</i> while executing
        /// nothing (CLAUDE.md banned pattern 2) —
        /// <c>NoHardcodedPathsTests.NoTestSilentlyReturnsFromAGuardedEarlyReturn</c> flags such a
        /// predicate call however it is spelled.</para>
        ///
        /// <para>The companion below covers the same claim with an injected font, so a host with
        /// no Hebrew font still checks the mapping and the embedding.</para>
        /// </summary>
        [HebrewFontFact]
        public void BuilderPath_Hebrew_EmbedsCompositeFont()
        {
            byte[] pdf = Convert(HebrewParagraph, ViaBuilder);

            string raw = Encoding.Latin1.GetString(pdf);
            Assert.Contains("Type0", raw);
            Assert.Contains("Identity-H", raw);
            Assert.Contains("CIDFontType2", raw);
        }

        /// <summary>
        /// Host-independent counterpart: the DOCX is mapped to the Builder model and exported with
        /// a synthetic face injected through <see cref="BuilderExport.PdfExportOptions.FontProvider"/>
        /// and the host search switched off, so it asserts the same thing on a bare Linux container
        /// as on a Windows box.
        ///
        /// <para>It drives the two halves of the Builder path separately because
        /// <c>DocxToPdfConverter</c> calls <c>SaveAsPdf(Stream)</c> with no options — there is
        /// nowhere for a caller's font to enter the end-to-end call. Closing that would be a change
        /// to <c>src/</c>: <c>DocxToPdfOptions</c> would have to carry a font provider.</para>
        /// </summary>
        [Fact]
        public void BuilderPath_Hebrew_WithAnInjectedFont_EmbedsCompositeFont()
        {
            byte[] docx = BuildDocx((main, body) => HebrewParagraph(body));

            using var ms = new MemoryStream(docx);
            using var wordDoc = WordprocessingDocument.Open(ms, false);
            BuilderModel.Document mapped = DocxToBuilderMapper.Map(wordDoc, ViaBuilder);

            byte[] pdf = mapped.ToPdf(new BuilderExport.PdfExportOptions
            {
                AllowSystemFontSubstitution = false,
                FontProvider = _ => MinimalTrueType.Build(Hebrew),
            });

            string raw = Encoding.Latin1.GetString(pdf);
            Assert.Contains("Type0", raw);
            Assert.Contains("Identity-H", raw);
            Assert.Contains("CIDFontType2", raw);
            Assert.Contains("FontFile2", raw);   // the program really reached the file
        }

        [Fact]
        public void BuilderPath_MultiSection_NewPagePerSection()
        {
            byte[] pdf = Convert(body =>
            {
                body.Append(new Paragraph(new Run(new Text("Section one"))));
                // A paragraph carrying a sectPr ends the first section.
                body.Append(new Paragraph(new ParagraphProperties(new SectionProperties())));
                body.Append(new Paragraph(new Run(new Text("Section two"))));
                body.Append(new SectionProperties()); // body-level = final section
            }, ViaBuilder);
            Assert.True(CountPages(pdf) >= 2, $"expected a page per section, got {CountPages(pdf)}");
        }

        [Fact]
        public void BuilderPath_HeaderAndFooter_AreRendered()
        {
            byte[] pdf = Convert((main, body) =>
            {
                var headerPart = main.AddNewPart<HeaderPart>();
                headerPart.Header = new Header(new Paragraph(new Run(new Text("HDRTEXT"))));
                var footerPart = main.AddNewPart<FooterPart>();
                footerPart.Footer = new Footer(new Paragraph(new Run(new Text("FTRTEXT"))));

                body.Append(new Paragraph(new Run(new Text("Body content"))));
                body.Append(new SectionProperties(
                    new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(headerPart) },
                    new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) }));
            }, ViaBuilder);

            string content = InflateContent(pdf);
            Assert.Contains("HDRTEXT", content);
            Assert.Contains("FTRTEXT", content);
        }

        [Fact]
        public void BuilderPath_PageField_ShowsLivePageNumbers()
        {
            byte[] pdf = Convert((main, body) =>
            {
                var footerPart = main.AddNewPart<FooterPart>();
                footerPart.Footer = new Footer(new Paragraph(
                    new SimpleField(new Run(new Text("1"))) { Instruction = "PAGE" }));
                // Body text without digits, so the only numerals come from page fields.
                for (int i = 0; i < 150; i++)
                    body.Append(new Paragraph(new Run(new Text("alpha beta gamma delta epsilon"))));
                body.Append(new SectionProperties(
                    new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) }));
            }, ViaBuilder);

            string content = InflateContent(pdf);
            int pages = CountPages(pdf);
            Assert.True(pages >= 2);
            Assert.DoesNotContain("{{PAGE}}", content);
            Assert.Contains("(1)", content);            // page 1 footer
            Assert.Contains($"({pages})", content);     // last page footer
        }

        /// <summary>
        /// A section's <c>w:pgNumType</c> — its format and its start — reaches the Builder path: page 1
        /// of a section starting at 4 in upper Roman shows IV. Read for neither, every Builder-path page
        /// number would be decimal from 1; the control is the same document without the element, which
        /// shows 1.
        /// </summary>
        [Fact]
        public void BuilderPath_PageNumberType_SetsTheFormatAndTheStart()
        {
            byte[] Page(OpenXmlElement pageNumberType) => Convert((main, body) =>
            {
                var footerPart = main.AddNewPart<FooterPart>();
                footerPart.Footer = new Footer(new Paragraph(
                    new SimpleField(new Run(new Text("1"))) { Instruction = "PAGE" }));
                body.Append(new Paragraph(new Run(new Text("alpha"))));
                var sectPr = new SectionProperties(
                    new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) });
                if (pageNumberType != null) sectPr.Append(pageNumberType);
                body.Append(sectPr);
            }, ViaBuilder);

            string roman = InflateContent(Page(new PageNumberType { Format = NumberFormatValues.UpperRoman, Start = 4 }));
            Assert.Contains("(IV)", roman);
            Assert.DoesNotContain("(1)", roman);

            Assert.Contains("(1)", InflateContent(Page(null)));
        }

        [Fact]
        public void BuilderPath_NumPagesField_ShowsTotal()
        {
            byte[] pdf = Convert((main, body) =>
            {
                var footerPart = main.AddNewPart<FooterPart>();
                footerPart.Footer = new Footer(new Paragraph(
                    new SimpleField(new Run(new Text("99"))) { Instruction = "NUMPAGES" }));
                for (int i = 0; i < 150; i++)
                    body.Append(new Paragraph(new Run(new Text("alpha beta gamma delta epsilon"))));
                body.Append(new SectionProperties(
                    new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) }));
            }, ViaBuilder);

            string content = InflateContent(pdf);
            int pages = CountPages(pdf);
            Assert.DoesNotContain("{{NUMPAGES}}", content);
            Assert.Contains($"({pages})", content);     // the resolved total on every page
        }

        [Fact]
        public void BuilderPath_PageBorders_AreStroked()
        {
            byte[] pdf = Convert(body =>
            {
                body.Append(new Paragraph(new Run(new Text("Bordered"))));
                var borders = new PageBorders { OffsetFrom = PageBorderOffsetValues.Page };
                borders.Append(new TopBorder { Val = BorderValues.Single, Size = 24, Color = "FF0000", Space = 24 });
                borders.Append(new LeftBorder { Val = BorderValues.Single, Size = 24, Color = "FF0000", Space = 24 });
                body.Append(new SectionProperties(borders));
            }, ViaBuilder);
            Assert.Contains("1 0 0 RG", InflateContent(pdf)); // red border stroke colour
        }

        [Fact]
        public void BuilderPath_DefaultIsLegacy()
        {
            // With default options the legacy renderer is used; both paths must yield a PDF.
            byte[] legacy = Convert(body => body.Append(
                new Paragraph(new Run(new Text("x")))), new DocxToPdfOptions { CompressStreams = false });
            Assert.Equal((byte)'%', legacy[0]);
        }
    }
}
