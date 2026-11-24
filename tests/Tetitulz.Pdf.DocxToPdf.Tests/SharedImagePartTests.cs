// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// One image part named by many drawings is decoded once and written once, on both paths: the converter hands
    /// the writer the same bytes for each drawing (the legacy context's <c>DecodedImages</c>, the mapper's part read and
    /// the exporter's <c>DecodeOnce</c>), and the writer draws them from one object
    /// (<c>Core.Writing.ImageObjectShare</c>). Decoded and written per drawing, a 3 KB DOCX naming one 1000 × 1000 PNG
    /// two hundred times would take 20 s and write 2.6 MB.
    /// </summary>
    public class SharedImagePartTests
    {
        private static byte[] Docx(int drawings)
        {
            byte[] png = DocxImageBuilder.MakeTruecolorPng(40, 30, 40, 120, 200);
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var part = main.AddImagePart(ImagePartType.Png);
                using (var s = new MemoryStream(png)) part.FeedData(s);
                string rel = main.GetIdOfPart(part);
                var body = new Body();
                for (int i = 0; i < drawings; i++)
                {
                    body.Append(new Paragraph(new Run(DocxImageBuilder.BuildInlineDrawing(rel, 914400, 685800))));
                    body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                }
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        /// <summary>Six drawings of one part, a page each: six pages draw one image object.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OneImagePartDrawnOnSixPages_IsOneImageObject(bool legacy)
        {
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(Docx(6), new DocxToPdfOptions { UseLegacyRenderer = legacy });
            using var doc = PdfDocument.Open(new MemoryStream(pdf));

            var images = new HashSet<int>();
            int drawn = 0;
            foreach (var page in doc.Pages)
            {
                if (!(doc.ResolveObject(page.Dictionary[PdfName.Resources]) is PdfDictionary resources)
                    || !(doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary xobjects)) continue;
                foreach (var entry in xobjects)
                {
                    drawn++;
                    images.Add(((PdfReference)entry.Value).ReferenceObjectNumber);
                }
            }
            Assert.Equal(6, drawn);
            Assert.Single(images);
        }

        /// <summary>
        /// A part that cannot be decoded is decoded once too, and still reported for each picture that names it, as when
        /// each was decoded on its own: two drawings of a part of junk bytes, two reports.
        /// </summary>
        [Fact]
        public void AnUndecodablePartNamedTwice_IsReportedTwice_OnTheLegacyPath()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var part = main.AddImagePart(ImagePartType.Png);
                using (var s = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })) part.FeedData(s);
                string rel = main.GetIdOfPart(part);
                main.Document = new Document(new Body(
                    new Paragraph(new Run(DocxImageBuilder.BuildInlineDrawing(rel, 914400, 685800))),
                    new Paragraph(new Run(DocxImageBuilder.BuildInlineDrawing(rel, 914400, 685800)))));
                main.Document.Save();
            }

            int thread = System.Environment.CurrentManagedThreadId, reports = 0;
            void Count(string message)
            {
                if (System.Environment.CurrentManagedThreadId == thread && message.Contains("ImageRenderer.Decode")) reports++;
            }
            DocxToPdfConverter.DiagnosticReported += Count;
            try { DocxToPdfConverter.ConvertToBytes(ms.ToArray(), new DocxToPdfOptions { UseLegacyRenderer = true }); }
            finally { DocxToPdfConverter.DiagnosticReported -= Count; }

            Assert.Equal(2, reports);
        }
    }
}
