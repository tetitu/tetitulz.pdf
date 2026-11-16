// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.GsubJoiningFont;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A font embedded in the DOCX that maps the Arabic letters but reaches their joining forms only
    /// through its <c>GSUB</c> (see <see cref="F"/>): the legacy renderer draws beh then yeh joined —
    /// the final yeh, then the initial beh, <c>&lt;00040003&gt; Tj</c> — where its coverage check
    /// passed the embedded font over for the forms it does not map.
    /// </summary>
    public class GsubJoiningFormsTests
    {
        private const string Family = "GsubJoiningEmbedded";
        private const string Key = "{3C4D5E6F-7081-92A3-B4C5-D6E7F8091A2B}";

        [Fact]
        public void TheEmbeddedFont_DrawsTheJoinedForms()
        {
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(Docx(F.Build()), new DocxToPdfOptions { UseLegacyRenderer = true });
            Assert.Contains("<00040003> Tj", Content(pdf));
        }

        /// <summary>The same font without its <c>GSUB</c> cannot draw the forms, and is not used for them.</summary>
        [Fact]
        public void WithoutItsGsub_TheEmbeddedFontIsNotUsed()
        {
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(Docx(F.Build(gsub: false)), new DocxToPdfOptions { UseLegacyRenderer = true });
            Assert.DoesNotContain("<00040003> Tj", Content(pdf));
        }

        private static string Content(byte[] pdf)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            return string.Concat(Enumerable.Range(0, doc.Pages.Count)
                .Select(i => Encoding.Latin1.GetString(doc.Pages[i].GetContentStreamData())));
        }

        private static byte[] Docx(byte[] font)
        {
            byte[] obfuscated = ObfuscatedFontReader.Deobfuscate(font, Key);   // XOR: its own inverse
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var rp = new RunProperties(
                    new RunFonts { Ascii = Family, HighAnsi = Family, ComplexScript = Family },
                    new FontSize { Val = "40" }, new FontSizeComplexScript { Val = "40" });
                main.Document = new Document(new Body(new Paragraph(new Run(rp, new Text("بي")))));
                main.Document.Save();

                var ftPart = main.AddNewPart<FontTablePart>();
                var fontPart = ftPart.AddNewPart<FontPart>("application/vnd.openxmlformats-officedocument.obfuscatedFont", "rIdEmb1");
                using (var fs = new MemoryStream(obfuscated)) fontPart.FeedData(fs);
                ftPart.Fonts = new Fonts(new Font(new EmbedRegularFont { Id = ftPart.GetIdOfPart(fontPart), FontKey = Key }) { Name = Family });
                ftPart.Fonts.Save();
            }
            return ms.ToArray();
        }
    }
}
