// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Structural feature tests: multi-section page sizes, clickable link annotations,
    /// headers/footers, and live PAGE / NUMPAGES fields.
    /// </summary>
    public class StructuralTests
    {
        private static string Convert(byte[] docx) =>
            Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                docx, new DocxToPdfOptions { CompressStreams = false }));

        [Fact]
        public void MultiSection_DifferentPageSizes_ProduceDifferentMediaBoxes()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                // Section 1 (Letter, 612pt wide) ended by a paragraph-level sectPr.
                body.Append(new Paragraph(
                    new ParagraphProperties(new SectionProperties(
                        new PageSize { Width = 12240U, Height = 15840U })),
                    new Run(new Text("Section one"))));
                // Section 2 (narrow, 420pt wide) — the body-level sectPr.
                body.Append(new Paragraph(new Run(new Text("Section two"))));
                body.Append(new SectionProperties(new PageSize { Width = 8400U, Height = 11900U }));
                main.Document = new Document(body);
                main.Document.Save();
            }
            string pdf = Convert(ms.ToArray());
            Assert.Equal(2, Regex.Matches(pdf, "/MediaBox").Count);
            Assert.Contains("612", pdf); // section 1 width
            Assert.Contains("420", pdf); // section 2 width
        }

        [Fact]
        public void Hyperlink_ProducesClickableLinkAnnotation()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var rel = main.AddHyperlinkRelationship(new Uri("https://example.com/"), true);
                var body = new Body(new Paragraph(
                    new Hyperlink(new Run(new Text("Visit"))) { Id = rel.Id }));
                main.Document = new Document(body);
                main.Document.Save();
            }
            string pdf = Convert(ms.ToArray());
            Assert.Contains("/Subtype /Link", pdf);
            Assert.Contains("/S /URI", pdf);
            Assert.Contains("https://example.com/", pdf);
        }

        /// <summary>
        /// A hyperlink relationship is the file's to choose: a <c>javascript:</c> target, or one a leading space
        /// hides from a raw scheme test, gets no <c>/URI</c> action — its text stays — and is reported once; the
        /// https link beside them, the control, is written (LinkTargets).
        /// </summary>
        [Fact]
        public void Hyperlink_ToAnUnsafeScheme_WritesNoActionAndIsReportedOnce()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var bad = main.AddHyperlinkRelationship(new Uri("javascript:alert(1)"), true);
                // System.Uri trims the space itself, so the raw target is written into the part below.
                var spaced = main.AddHyperlinkRelationship(new Uri("https://placeholder.invalid/"), true);
                var good = main.AddHyperlinkRelationship(new Uri("https://example.com/ok"), true);
                var body = new Body(
                    new Paragraph(new Hyperlink(new Run(new Text("Bad"))) { Id = bad.Id }),
                    new Paragraph(new Hyperlink(new Run(new Text("Spaced"))) { Id = spaced.Id }),
                    new Paragraph(new Hyperlink(new Run(new Text("Good"))) { Id = good.Id }));
                main.Document = new Document(body);
                main.Document.Save();
            }

            using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = zip.GetEntry("word/_rels/document.xml.rels")!;
                string rels;
                using (var r = new StreamReader(entry.Open())) rels = r.ReadToEnd();
                Assert.Contains("https://placeholder.invalid/", rels, StringComparison.Ordinal);
                rels = rels.Replace("https://placeholder.invalid/", " java&#9;script:alert(2)");
                entry.Delete();
                using var w = new StreamWriter(zip.CreateEntry("word/_rels/document.xml.rels").Open());
                w.Write(rels);
            }

            var seen = new System.Collections.Generic.List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try { pdf = Convert(ms.ToArray()); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            Assert.DoesNotContain("javascript", pdf, StringComparison.Ordinal);
            Assert.Single(Regex.Matches(pdf, "/S /URI"));
            Assert.Contains("(https://example.com/ok)", pdf, StringComparison.Ordinal);
            Assert.Single(seen, m => m.Contains("was not written", StringComparison.Ordinal));
            string text = PdfTextOf(pdf);
            Assert.Contains("Bad", text, StringComparison.Ordinal);
            Assert.Contains("Spaced", text, StringComparison.Ordinal);
        }

        private static string PdfTextOf(string pdf)
        {
            using var ps = new MemoryStream(Encoding.Latin1.GetBytes(pdf));
            using var pdoc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ps);
            return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(pdoc);
        }

        [Fact]
        public void Footer_WithPageField_ShowsPageNumberOnEachPage()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();

                var footerPart = main.AddNewPart<FooterPart>();
                footerPart.Footer = new Footer(new Paragraph(
                    new Run(new Text("Pg ") { Space = SpaceProcessingModeValues.Preserve }),
                    new SimpleField(new Run(new Text("x"))) { Instruction = "PAGE" }));
                string fid = main.GetIdOfPart(footerPart);

                var body = new Body();
                for (int i = 0; i < 90; i++) // force ≥2 pages
                    body.Append(new Paragraph(new Run(new Text($"Body line {i}"))));
                body.Append(new SectionProperties(
                    new FooterReference { Type = HeaderFooterValues.Default, Id = fid }));
                main.Document = new Document(body);
                main.Document.Save();
            }
            string pdf = Convert(ms.ToArray());
            Assert.True(Regex.Matches(pdf, "/MediaBox").Count >= 2, "expected multiple pages");
            Assert.Contains("(Pg ) Tj", pdf);
            Assert.Contains("(1) Tj", pdf); // footer page number on page 1
            Assert.Contains("(2) Tj", pdf); // and page 2
        }

        [Fact]
        public void NumPagesField_IsResolvedToFinalCount()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body(new Paragraph(
                    new SimpleField(new Run(new Text("y"))) { Instruction = "NUMPAGES" }));
                main.Document = new Document(body);
                main.Document.Save();
            }
            string pdf = Convert(ms.ToArray());
            Assert.Contains("(1) Tj", pdf);            // single page → "1"
            Assert.DoesNotContain("{{NUMPAGES}}", pdf); // placeholder fully substituted
        }
    }
}
