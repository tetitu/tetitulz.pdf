// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The legacy renderer finds a note by its id, and a hyperlink's target by its relationship id, through an
    /// index built once rather than a search per reference (references × notes was 2.1 s at 8 000 footnotes) —
    /// with the search's answers: the first note with an id, the first relationship with an id.
    /// </summary>
    public class NoteAndLinkIndexTests
    {
        private static string TextOf(byte[] docx)
        {
            using var doc = PdfDocument.Open(new MemoryStream(
                DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = true })));
            return PdfTextExtractor.ExtractText(doc);
        }

        /// <summary>A footnote and an endnote, each found by its id; and of two footnotes sharing id 2, the first is drawn.</summary>
        [Fact]
        public void NotesAreFoundByTheirIds_TheFirstOfADuplicatedIdWinning()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var fpart = main.AddNewPart<FootnotesPart>();
                fpart.Footnotes = new Footnotes(
                    new Footnote(new Paragraph(new Run(new Text("ALPHANOTE")))) { Id = 1 },
                    new Footnote(new Paragraph(new Run(new Text("FIRSTTWO")))) { Id = 2 },
                    new Footnote(new Paragraph(new Run(new Text("SECONDTWO")))) { Id = 2 });
                var epart = main.AddNewPart<EndnotesPart>();
                epart.Endnotes = new Endnotes(new Endnote(new Paragraph(new Run(new Text("ENDNOTEONE")))) { Id = 1 });
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("A")), new Run(new FootnoteReference { Id = 1 })),
                    new Paragraph(new Run(new Text("B")), new Run(new FootnoteReference { Id = 2 })),
                    new Paragraph(new Run(new Text("C")), new Run(new EndnoteReference { Id = 1 }))));
                main.Document.Save();
            }

            string text = TextOf(ms.ToArray());
            Assert.Contains("ALPHANOTE", text, StringComparison.Ordinal);
            Assert.Contains("FIRSTTWO", text, StringComparison.Ordinal);
            Assert.Contains("ENDNOTEONE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("SECONDTWO", text, StringComparison.Ordinal);
        }
    }
}
