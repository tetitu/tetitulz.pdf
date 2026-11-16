// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A Word list numbered in Hebrew — <c>hebrew1</c>, letters by gematria, and <c>hebrew2</c>, the
    /// alphabet (ECMA-376 §17.18.59) — is numbered in Hebrew on both conversion paths. Until
    /// 2026-10-21 both fell to decimal. Read back as the page's text, which is what a reader sees.
    /// </summary>
    public class HebrewNumberingTests
    {
        private const string Item = "item";

        /// <summary>Seventeen items: the run passes 10 (י), 11 (יא), and 15 and 16, which gematria writes טו and טז.</summary>
        [HebrewCapableDocxFontFact]
        public void Hebrew1_NumbersTheListByGematria_OnBothPaths()
        {
            foreach (bool legacy in new[] { true, false })
            {
                string text = TextOf(ListDocx(NumberFormatValues.Hebrew1, 17), legacy);

                Assert.Contains("\u05D0", text, System.StringComparison.Ordinal);                // 1
                Assert.Contains("\u05D9\u05D0", text, System.StringComparison.Ordinal);          // 11
                Assert.Contains("\u05D8\u05D5", text, System.StringComparison.Ordinal);          // 15, not \u05D9\u05D4
                Assert.Contains("\u05D8\u05D6", text, System.StringComparison.Ordinal);          // 16, not \u05D9\u05D5
                Assert.Contains("\u05D9\u05D6", text, System.StringComparison.Ordinal);          // 17
                Assert.DoesNotContain("\u05D9\u05D4", text, System.StringComparison.Ordinal);
                Assert.DoesNotContain("17", text, System.StringComparison.Ordinal);              // nothing fell back to decimal
            }
        }

        /// <summary>Twenty-three items: 22 is ת, the last letter, and 23 is ת then א.</summary>
        [HebrewCapableDocxFontFact]
        public void Hebrew2_NumbersTheListByTheAlphabet_OnBothPaths()
        {
            foreach (bool legacy in new[] { true, false })
            {
                string text = TextOf(ListDocx(NumberFormatValues.Hebrew2, 23), legacy);

                Assert.Contains("\u05EA\u05D0", text, System.StringComparison.Ordinal);          // 23
                Assert.DoesNotContain("23", text, System.StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A section's page numbers in <c>hebrew1</c>, starting at 15: the footer's PAGE field shows טו
        /// (not יה, not 15) on both paths — the format map and, on the Builder path, the
        /// <c>w:pgNumType</c> read that carries it.
        /// </summary>
        [HebrewCapableDocxFontFact]
        public void Hebrew1_PageNumbersAreGematria_OnBothPaths()
        {
            foreach (bool legacy in new[] { true, false })
            {
                string text = TextOf(PageNumberedDocx(NumberFormatValues.Hebrew1, 15), legacy);

                Assert.Contains("טו", text, System.StringComparison.Ordinal);
                Assert.DoesNotContain("15", text, System.StringComparison.Ordinal);
                Assert.Contains("body", text, System.StringComparison.Ordinal);   // potency: the page was read
            }
        }

        /// <summary>The same in <c>hebrew2</c>, starting at 23: תא.</summary>
        [HebrewCapableDocxFontFact]
        public void Hebrew2_PageNumbersAreTheAlphabet_OnBothPaths()
        {
            foreach (bool legacy in new[] { true, false })
            {
                string text = TextOf(PageNumberedDocx(NumberFormatValues.Hebrew2, 23), legacy);

                Assert.Contains("תא", text, System.StringComparison.Ordinal);
                Assert.DoesNotContain("23", text, System.StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A PAGE field in the body counts from the section's <c>w:pgNumType/@w:start</c> too. The
        /// legacy path read no start until 2026-10-21, so this page said 1.
        /// </summary>
        [Fact]
        public void PageStart_NumbersABodyPageField_OnBothPaths()
        {
            foreach (bool legacy in new[] { true, false })
            {
                string text = TextOf(PageNumberedDocx(NumberFormatValues.Decimal, 15, footer: false), legacy);

                Assert.Contains("body 15", text, System.StringComparison.Ordinal);
            }
        }

        private static byte[] PageNumberedDocx(NumberFormatValues format, int start, bool footer = true)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                MainDocumentPart main = doc.AddMainDocumentPart();
                var footerPart = main.AddNewPart<FooterPart>();
                footerPart.Footer = new Footer(new Paragraph(
                    new ParagraphProperties(new BiDi()),
                    new SimpleField(new Run(new Text("1"))) { Instruction = "PAGE" }));
                footerPart.Footer.Save();

                var body = new Body(footer
                    ? new Paragraph(new Run(new Text("body")))
                    : new Paragraph(new Run(new Text("body ") { Space = SpaceProcessingModeValues.Preserve }),
                                    new SimpleField(new Run(new Text("1"))) { Instruction = "PAGE" }));
                var sectPr = new SectionProperties();
                if (footer)
                    sectPr.Append(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) });
                sectPr.Append(new PageNumberType { Format = format, Start = start });
                if (footer) sectPr.Append(new BiDi());
                body.Append(sectPr);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static string TextOf(byte[] docx, bool legacy)
        {
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = legacy });
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            return PdfTextExtractor.ExtractText(doc);
        }

        /// <summary>A right-to-left document whose one list has <paramref name="count"/> items, numbered in <paramref name="format"/>.</summary>
        private static byte[] ListDocx(NumberFormatValues format, int count)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                MainDocumentPart main = doc.AddMainDocumentPart();
                var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                numbering.Numbering = new Numbering(
                    new AbstractNum(new Level(
                        new StartNumberingValue { Val = 1 },
                        new NumberingFormat { Val = format },
                        new LevelText { Val = "%1." }) { LevelIndex = 0 }) { AbstractNumberId = 0 },
                    new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
                numbering.Numbering.Save();

                var body = new Body();
                for (int i = 0; i < count; i++)
                    body.Append(new Paragraph(
                        new ParagraphProperties(
                            new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }),
                            new BiDi()),
                        new Run(new RunProperties(new RightToLeftText()), new Text(Item))));
                body.Append(new SectionProperties(new BiDi()));
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }
    }
}
