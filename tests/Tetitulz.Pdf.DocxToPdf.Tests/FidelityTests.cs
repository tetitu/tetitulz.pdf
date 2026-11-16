// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Tests for the fidelity pass: justified text, content controls, tracked
    /// changes, and run-level character formatting.
    /// </summary>
    public class FidelityTests
    {
        private static string Convert(System.Action<Body> build)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                build(body);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(
                DocxToPdfConverter.ConvertToBytes(ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));
        }

        [Fact]
        public void JustifiedText_EmitsWordSpacing()
        {
            string longText = string.Concat(Enumerable.Repeat("word ", 60));
            string pdf = Convert(body => body.Append(new Paragraph(
                new ParagraphProperties(new Justification { Val = JustificationValues.Both }),
                new Run(new Text(longText) { Space = SpaceProcessingModeValues.Preserve }))));
            Assert.Contains(" Tw\n", pdf); // inter-word stretching applied to wrapped lines
        }

        [Fact]
        public void ContentControl_TextIsRendered()
        {
            string pdf = Convert(body => body.Append(new Paragraph(
                new SdtRun(new SdtProperties(), new SdtContentRun(new Run(new Text("SdtInlineText")))))));
            Assert.Contains("(SdtInlineText) Tj", pdf);

            string pdf2 = Convert(body => body.Append(
                new SdtBlock(new SdtProperties(), new SdtContentBlock(
                    new Paragraph(new Run(new Text("SdtBlockText")))))));
            Assert.Contains("(SdtBlockText) Tj", pdf2);
        }

        [Fact]
        public void TrackedChanges_InsertionRendered_DeletionSkipped()
        {
            string pdf = Convert(body => body.Append(new Paragraph(
                new InsertedRun(new Run(new Text("InsertedText"))) { Author = "x", Id = "1" },
                new DeletedRun(new Run(new DeletedText("DeletedText"))) { Author = "x", Id = "2" })));
            Assert.Contains("InsertedText", pdf);
            Assert.DoesNotContain("DeletedText", pdf);
        }

        [Fact]
        public void PageBorders_AreStroked()
        {
            string pdf = Convert(body =>
            {
                body.Append(new Paragraph(new Run(new Text("Bordered"))));
                var sect = new SectionProperties();
                var borders = new PageBorders { OffsetFrom = PageBorderOffsetValues.Page };
                borders.Append(new TopBorder { Val = BorderValues.Single, Size = 24, Color = "FF0000", Space = 24 });
                borders.Append(new BottomBorder { Val = BorderValues.Single, Size = 24, Color = "FF0000", Space = 24 });
                borders.Append(new LeftBorder { Val = BorderValues.Single, Size = 24, Color = "FF0000", Space = 24 });
                borders.Append(new RightBorder { Val = BorderValues.Single, Size = 24, Color = "FF0000", Space = 24 });
                sect.Append(borders);
                body.Append(sect);
            });
            Assert.Contains("1 0 0 RG", pdf);  // red stroke colour
            Assert.Contains(" l S\n", pdf);     // a stroked border edge
        }

        [Fact]
        public void CharacterFormatting_SpacingAndScale_AreEmitted()
        {
            string pdf = Convert(body => body.Append(new Paragraph(
                new Run(new RunProperties(
                    new Spacing { Val = 40 },           // +2pt char spacing → Tc
                    new CharacterScale { Val = 150 }),  // 150% horizontal scale → Tz
                    new Text("Spaced")))));
            Assert.Contains(" Tc\n", pdf);
            Assert.Contains("150 Tz\n", pdf);
        }
    }
}
