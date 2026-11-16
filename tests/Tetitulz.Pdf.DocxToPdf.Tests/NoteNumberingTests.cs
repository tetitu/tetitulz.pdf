// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Footnote and endnote numbering as <c>w:footnotePr</c> and <c>w:endnotePr</c> set it (ECMA-376
    /// Part 1 §17.11.11–12): the number format, the first number, and a restart on each page. Absent,
    /// the notes count from 1 in decimal.
    /// </summary>
    public class NoteNumberingTests
    {
        /// <summary>The endnotes' format in the settings is the marks' and the bodies' alike; the control, no settings, is decimal.</summary>
        [Fact]
        public void TheEndnoteFormat_FromTheSettings_NumbersTheNotes()
        {
            string roman = TextOf(Docx(settings: new Settings(new EndnoteDocumentWideProperties(
                new NumberingFormat { Val = NumberFormatValues.UpperRoman })), twoEndnotes: true));
            string decimalText = TextOf(Docx(settings: null, twoEndnotes: true));

            Assert.Contains("I. FIRSTNOTE", roman, StringComparison.Ordinal);
            Assert.Contains("II. SECONDNOTE", roman, StringComparison.Ordinal);
            Assert.Contains("1. FIRSTNOTE", decimalText, StringComparison.Ordinal);
            Assert.Contains("2. SECONDNOTE", decimalText, StringComparison.Ordinal);
        }

        /// <summary>The footnotes count from <c>w:numStart</c>.</summary>
        [Fact]
        public void TheFootnoteStart_FromTheSettings_IsTheFirstNumber()
        {
            string text = TextOf(Docx(settings: new Settings(new FootnoteDocumentWideProperties(
                new NumberingStart { Val = 5 })), twoEndnotes: false));

            Assert.Contains("5. FIRSTNOTE", text, StringComparison.Ordinal);
            Assert.Contains("6. SECONDNOTE", text, StringComparison.Ordinal);
        }

        /// <summary>Under <c>w:numRestart eachPage</c> the note on the second page is 1 again; the control is continuous.</summary>
        [Fact]
        public void TheFootnotes_RestartOnEachPage_WhenTheSettingsSay()
        {
            string restarted = TextOf(Docx(settings: new Settings(new FootnoteDocumentWideProperties(
                new NumberingRestart { Val = RestartNumberValues.EachPage })), twoEndnotes: false, pageBreakBetween: true));
            string continuous = TextOf(Docx(settings: null, twoEndnotes: false, pageBreakBetween: true));

            Assert.Contains("1. SECONDNOTE", restarted, StringComparison.Ordinal);
            Assert.Contains("2. SECONDNOTE", continuous, StringComparison.Ordinal);
            Assert.Contains("1. FIRSTNOTE", restarted, StringComparison.Ordinal);
        }

        /// <summary>Two notes, referenced from two paragraphs, with the settings given; endnotes or footnotes.</summary>
        private static byte[] Docx(Settings settings, bool twoEndnotes, bool pageBreakBetween = false)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                if (settings != null) main.AddNewPart<DocumentSettingsPart>().Settings = settings;
                OpenXmlElement firstRef, secondRef;
                if (twoEndnotes)
                {
                    main.AddNewPart<EndnotesPart>().Endnotes = new Endnotes(
                        new Endnote(new Paragraph(new Run(new Text("FIRSTNOTE")))) { Id = 1 },
                        new Endnote(new Paragraph(new Run(new Text("SECONDNOTE")))) { Id = 2 });
                    firstRef = new EndnoteReference { Id = 1 };
                    secondRef = new EndnoteReference { Id = 2 };
                }
                else
                {
                    main.AddNewPart<FootnotesPart>().Footnotes = new Footnotes(
                        new Footnote(new Paragraph(new Run(new Text("FIRSTNOTE")))) { Id = 1 },
                        new Footnote(new Paragraph(new Run(new Text("SECONDNOTE")))) { Id = 2 });
                    firstRef = new FootnoteReference { Id = 1 };
                    secondRef = new FootnoteReference { Id = 2 };
                }
                var body = new Body(new Paragraph(new Run(new Text("A")), new Run(firstRef)));
                if (pageBreakBetween) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                body.Append(new Paragraph(new Run(new Text("B")), new Run(secondRef)));
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static string TextOf(byte[] docx)
        {
            using var pdf = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(DocxToPdfConverter.ConvertToBytes(docx)));
            return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(pdf);
        }
    }
}
