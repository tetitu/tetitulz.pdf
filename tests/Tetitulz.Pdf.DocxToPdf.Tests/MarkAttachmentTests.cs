// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.MarkAttachmentFont;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The legacy renderer writes a run's mark attachment (GAPS.md §19c) at its three sites — a
    /// paragraph, a table cell and an equation — through <c>UnicodeText.ShowText</c>. The DOCX embeds
    /// <see cref="F"/>, under which a bet with qamats is the qamats raised under a text rise and moved
    /// by <c>[-150 &lt;0002&gt; 450] TJ</c>, then the bet; each site wrote <c>&lt;00020001&gt; Tj</c>.
    /// </summary>
    public class MarkAttachmentTests
    {
        private const string Family = "MarkAttachmentEmbedded";
        private const string Key = "{0F3C2B1A-4D5E-6F70-8192-A3B4C5D6E7F8}";
        private static readonly string BetQamats = $"{F.Bet}{F.Qamats}";
        private static readonly Regex Placed = new Regex(@"-[\d.]+ Ts\n\[-150 <0002> 450\] TJ\n0 Ts\n");

        [Fact]
        public void AParagraphAndATableCell_PlaceTheMark()
        {
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(Docx(), new DocxToPdfOptions { UseLegacyRenderer = true });

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            string content = string.Concat(Enumerable.Range(0, doc.Pages.Count)
                .Select(i => Encoding.Latin1.GetString(doc.Pages[i].GetContentStreamData())));
            Assert.Equal(2, Placed.Matches(content).Count);
        }

        [Fact]
        public void AnEquation_PlacesTheMark()
        {
            using var ms = new MemoryStream(Docx());
            using var wd = WordprocessingDocument.Open(ms, false);
            var context = new DocxRenderContext(wd, new DocxToPdfOptions());
            context.NewPage();   // the box records its font on the page it is drawn on

            var box = Assert.IsType<MathGlyphBox>(MathMeasure.Glyphs(BetQamats, MathStyle.Root(Family, 10, false, null), context));
            var ops = new StringBuilder();
            box.Draw(ops, 0, 0, new MathDrawContext(context));

            Assert.Contains("-0.5 Ts\n[-150 <0002> 450] TJ\n0 Ts\n<0001> Tj", ops.ToString());
        }

        private static Run HebrewRun() => new Run(
            new RunProperties(new RunFonts { Ascii = Family, HighAnsi = Family, ComplexScript = Family }),
            new Text(BetQamats));

        private static byte[] Docx()
        {
            byte[] obfuscated = ObfuscatedFontReader.Deobfuscate(F.Build(), Key);   // XOR: its own inverse
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var table = new Table(new TableRow(new TableCell(new Paragraph(HebrewRun()))));
                main.Document = new Document(new Body(new Paragraph(HebrewRun()), table));
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
