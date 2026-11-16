// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// <see cref="DocxToPdfOptions.MaxPages"/> (2026-10-20, the owner's decision: a parameter, unlimited by default),
    /// on both paths: a document past the limit ends at the last whole page within it — its header drawn on every
    /// page written — and <see cref="DocxToPdfConverter.DiagnosticReported"/> says so.
    /// </summary>
    public class PageLimitTests
    {
        /// <summary>Two hundred paragraphs under a header: several pages on either path.</summary>
        private static byte[] LongDocument()
        {
            var body = new StringBuilder();
            for (int i = 0; i < 200; i++)
                body.Append($"<w:p><w:r><w:t xml:space=\"preserve\">Paragraph {i}: {string.Concat(Enumerable.Repeat("words that fill a line ", 8))}</w:t></w:r></w:p>");
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var header = main.AddNewPart<HeaderPart>();
                using (var h = new MemoryStream(Encoding.UTF8.GetBytes(
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr xmlns:w=\"{HostileDocxInputTests.WordNamespace}\"><w:p><w:r><w:t>Running head</w:t></w:r></w:p></w:hdr>")))
                    header.FeedData(h);
                string headerId = main.GetIdOfPart(header);
                using var d = new MemoryStream(Encoding.UTF8.GetBytes(
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\" "
                    + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>"
                    + body + $"<w:sectPr><w:headerReference w:type=\"default\" r:id=\"{headerId}\"/></w:sectPr></w:body></w:document>"));
                main.FeedData(d);
            }
            return ms.ToArray();
        }

        private static DocxToPdfOptions Options(bool legacy, int maxPages = 0)
            => new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = legacy, MaxPages = maxPages };

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ADocumentPastTheLimit_EndsAtItWholeAndSaysSo(bool legacy)
        {
            byte[] docx = LongDocument();
            using (var all = PdfDocument.Open(new MemoryStream(DocxToPdfConverter.ConvertToBytes(docx, Options(legacy)))))
                Assert.True(all.Pages.Count > 3, $"the control runs past the limit: {all.Pages.Count} pages");   // potency

            var seen = new List<string>();
            void Collect(string m) => seen.Add(m);
            DocxToPdfConverter.DiagnosticReported += Collect;
            byte[] pdf;
            try { pdf = DocxToPdfConverter.ConvertToBytes(docx, Options(legacy, maxPages: 2)); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            using var limited = PdfDocument.Open(new MemoryStream(pdf));
            Assert.Equal(2, limited.Pages.Count);
            for (int i = 0; i < 2; i++)
                Assert.Contains("Running head", Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(limited.Pages[i]), StringComparison.Ordinal);
            Assert.Single(seen, m => m.Contains("2-page limit", StringComparison.Ordinal));
        }

        [Fact]
        public void ANegativeLimit_IsRefused_AndZeroIsNoLimit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new DocxToPdfOptions { MaxPages = -1 });
            Assert.Equal(0, new DocxToPdfOptions().MaxPages);
        }
    }
}
