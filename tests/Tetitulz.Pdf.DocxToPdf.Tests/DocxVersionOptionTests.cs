// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// <see cref="DocxToPdfOptions.Version"/> on both paths of the BuiltIn engine: the legacy renderer's
    /// own assembler and the Builder path. The header is written as asked, or raised to what the content
    /// needs and reported on <see cref="DocxToPdfConverter.DiagnosticReported"/>; unset, it is the lowest
    /// the content needs and never below 1.4.
    /// </summary>
    public class DocxVersionOptionTests
    {
        private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 8);

        private static byte[] Docx()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                body.Append(new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text("Title"))));
                body.Append(new Paragraph(new Run(new Text("Body paragraph text."))));
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static (byte[] pdf, List<string> seen) Convert(DocxToPdfOptions options)
        {
            var seen = new List<string>();
            Action<string> handler = seen.Add;
            DocxToPdfConverter.DiagnosticReported += handler;
            try { return (DocxToPdfConverter.ConvertToBytes(Docx(), options), seen); }
            finally { DocxToPdfConverter.DiagnosticReported -= handler; }
        }

        private static IEnumerable<string> VersionReports(List<string> seen)
            => seen.Where(m => m.Contains(DocxToPdfConverter.VersionOperation));

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AskedVersion_IsTheHeader_OnEitherPath(bool legacy)
        {
            var (pdf17, _) = Convert(new DocxToPdfOptions { UseLegacyRenderer = legacy, Version = PdfVersion.Pdf17 });
            Assert.Equal("%PDF-1.7", Header(pdf17));

            var (pdf20, _) = Convert(new DocxToPdfOptions { UseLegacyRenderer = legacy, Version = PdfVersion.Pdf20 });
            Assert.Equal("%PDF-2.0", Header(pdf20));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AVersionBelowCompression_IsRaisedTo12_AndReported_OnEitherPath(bool legacy)
        {
            // Compressed content streams are FlateDecode, PDF 1.2.
            var (pdf, seen) = Convert(new DocxToPdfOptions { UseLegacyRenderer = legacy, Version = PdfVersion.Pdf11 });

            Assert.Equal("%PDF-1.2", Header(pdf));
            string report = Assert.Single(VersionReports(seen));
            Assert.Contains("PDF 1.1 was asked for", report);
            Assert.Contains("FlateDecode", report);
        }

        [Fact]
        public void Legacy_TaggedOutput_RaisesTo14()
        {
            var (pdf, seen) = Convert(new DocxToPdfOptions { TaggedPdf = true, Version = PdfVersion.Pdf13 });

            Assert.Equal("%PDF-1.4", Header(pdf));
            Assert.Contains("PDF 1.3 was asked for", Assert.Single(VersionReports(seen)));
        }

        [Fact]
        public void Legacy_AVersionTheContentFits_StandsUnreported()
        {
            // Uncompressed, untagged and in a standard-14 font, the legacy output needs nothing past 1.0.
            var (pdf, seen) = Convert(new DocxToPdfOptions { CompressStreams = false, Version = PdfVersion.Pdf10 });

            Assert.Equal("%PDF-1.0", Header(pdf));
            Assert.Empty(VersionReports(seen));
        }

        [Fact]
        public void Legacy_Unset_Is14ForContentThatNeedsLess_AndReportsNothing()
        {
            var (pdf, seen) = Convert(new DocxToPdfOptions { CompressStreams = false });
            Assert.Equal("%PDF-1.4", Header(pdf));
            Assert.Empty(VersionReports(seen));
        }

        [Fact]
        public void Legacy_TheReassembledFile_OpensWithItsPagesOutlineAndStructure()
        {
            var (pdf, _) = Convert(new DocxToPdfOptions { TaggedPdf = true, Version = PdfVersion.Pdf20 });

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            Assert.Equal(PdfVersion.Pdf20, doc.Version);
            Assert.Single(doc.Pages);
            var catalog = doc.Catalog.Dictionary;
            Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(
                doc.ResolveObject(catalog[Tetitulz.Pdf.Core.Objects.PdfName.StructTreeRoot]));
            Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(
                doc.ResolveObject(catalog[Tetitulz.Pdf.Core.Objects.PdfName.Outlines]));
        }

        [Fact]
        public void AnUnpublishedVersion_IsRefused()
        {
            var options = new DocxToPdfOptions();
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => options.Version = new PdfVersion(2, 1));
            Assert.Equal("value", ex.ParamName);

            options.Version = PdfVersion.Pdf10;   // control
            Assert.Equal(PdfVersion.Pdf10, options.Version);
        }
    }
}
