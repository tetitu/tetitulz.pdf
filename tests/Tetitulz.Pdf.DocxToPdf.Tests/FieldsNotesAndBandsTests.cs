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
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A complex field draws its live value on the legacy path and in a cell (<c>Rendering.FieldWalk</c>);
    /// an <c>mc:AlternateContent</c> is opened to its choice on open; the Builder path maps footnotes and
    /// endnotes and says what it cannot draw; a band taller than the page is cut a page below it and said;
    /// a font resolution remembers the file it answered with, not a program of its own.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class FieldsNotesAndBandsTests
    {
        private const string WpsNamespace = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        /// <summary>
        /// A footer's complex PAGE field (fldChar begin, instrText, separate, the cached "7", end) shows each
        /// page's own number on the legacy path, where it showed the cached number on every page.
        /// </summary>
        [Fact]
        public void AComplexPageField_DrawsTheLivePageNumber_OnTheLegacyPath()
        {
            byte[] docx = Docx((main, body) =>
            {
                var fp = main.AddNewPart<FooterPart>();
                fp.Footer = new Footer(new Paragraph(
                    new Run(new Text("Pg ") { Space = SpaceProcessingModeValues.Preserve }),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.Begin }),
                    new Run(new FieldCode(" PAGE ") { Space = SpaceProcessingModeValues.Preserve }),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.Separate }),
                    new Run(new Text("7")),
                    new Run(new FieldChar { FieldCharType = FieldCharValues.End })));
                for (int i = 0; i < 150; i++) body.Append(new Paragraph(new Run(new Text("Body line " + i))));
                body.Append(new SectionProperties(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(fp) }));
            });

            string pdf = Convert(docx, legacy: true);

            Assert.True(PageCount(pdf) >= 2, "the body should spill onto a second page");
            Assert.Contains("(1) Tj", pdf, StringComparison.Ordinal);
            Assert.Contains("(2) Tj", pdf, StringComparison.Ordinal);
            Assert.DoesNotContain("(7) Tj", pdf, StringComparison.Ordinal);
        }

        /// <summary>A simple PAGE field in a table cell is evaluated, not opened to its cached result.</summary>
        [Fact]
        public void ASimplePageFieldInACell_IsEvaluated()
        {
            byte[] docx = Docx((_, body) => body.Append(new Table(new TableRow(new TableCell(new Paragraph(
                new SimpleField(new Run(new Text("x"))) { Instruction = "PAGE" }))))));

            string pdf = Convert(docx, legacy: true);

            Assert.Contains("(1) Tj", pdf, StringComparison.Ordinal);
            Assert.DoesNotContain("(x) Tj", pdf, StringComparison.Ordinal);
        }

        /// <summary>
        /// A text box Word writes inside <c>mc:AlternateContent</c> is drawn: the wrapper is replaced by
        /// its choice when the package is opened (<c>Rendering.MarkupCompatibility</c>). On the Builder path,
        /// which maps pictures alone, the drawing is reported. A wrapper with no choice and a VML fallback
        /// draws nothing and is reported too.
        /// </summary>
        [Fact]
        public void AnAlternateContentChoice_IsOpenedAndDrawn()
        {
            byte[] docx = Docx((_, body) =>
            {
                var alternate = new AlternateContent(
                    new AlternateContentChoice(new Drawing(TextBoxDrawing("BOXTEXT"))) { Requires = "wps" },
                    new AlternateContentFallback());
                alternate.AddNamespaceDeclaration("wps", WpsNamespace);
                body.Append(new Paragraph(new Run(alternate)));
            });
            byte[] vmlOnly = Docx((_, body) => body.Append(new Paragraph(new Run(new AlternateContent(
                new AlternateContentFallback(new Picture()))))));

            Convert(vmlOnly, legacy: true);
            Assert.Contains("VML", DocxToPdfConverter.LastDiagnostic ?? "", StringComparison.Ordinal);

            string legacy = Convert(docx, legacy: true);
            Assert.Contains("(BOXTEXT) Tj", legacy, StringComparison.Ordinal);

            Convert(docx, legacy: false);
            Assert.Contains("not a picture", DocxToPdfConverter.LastDiagnostic ?? "", StringComparison.Ordinal);
        }

        /// <summary>The Builder path maps a footnote and an endnote: the mark is numbered and the note's text drawn.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ANoteOnTheBuilderPath_IsNumberedAndDrawn(bool endnote)
        {
            byte[] docx = Docx((main, body) =>
            {
                OpenXmlElement reference;
                if (endnote)
                {
                    main.AddNewPart<EndnotesPart>().Endnotes = new Endnotes(new Endnote(new Paragraph(new Run(new Text("NOTETEXT")))) { Id = 1 });
                    reference = new EndnoteReference { Id = 1 };
                }
                else
                {
                    main.AddNewPart<FootnotesPart>().Footnotes = new Footnotes(new Footnote(new Paragraph(new Run(new Text("NOTETEXT")))) { Id = 1 });
                    reference = new FootnoteReference { Id = 1 };
                }
                body.Append(new Paragraph(new Run(new Text("Body")), new Run(reference)));
            });

            string text = TextOf(Convert(docx, legacy: false));

            Assert.Contains("Body", text, StringComparison.Ordinal);
            Assert.Contains("NOTETEXT", text, StringComparison.Ordinal);
            Assert.Contains("1", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// A header of thousands of lines is laid out on every page only to a page's height below the page,
        /// and the cut is said; the control, a three-line header, draws its three lines on each page.
        /// </summary>
        [Fact]
        public void ABandTallerThanThePage_IsCutAPageBelowIt_AndSaid()
        {
            byte[] Build(int breaks) => Docx((main, body) =>
            {
                var run = new Run(new Text("H"));
                for (int i = 0; i < breaks; i++) { run.Append(new Break()); run.Append(new Text("H")); }
                var hp = main.AddNewPart<HeaderPart>();
                hp.Header = new Header(new Paragraph(run));
                body.Append(new Paragraph(new Run(new Text("One"))));
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
                body.Append(new Paragraph(new Run(new Text("Two"))));
                body.Append(new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(hp) }));
            });

            string tall = Convert(Build(3000), legacy: true);
            int drawn = Regex.Matches(tall, Regex.Escape("(H) Tj")).Count;
            Assert.Equal(2, PageCount(tall));
            Assert.InRange(drawn, 100, 2 * 400);                       // two pages of lines, not 6002
            Assert.Contains("taller than the page", DocxToPdfConverter.LastDiagnostic ?? "", StringComparison.Ordinal);

            string control = Convert(Build(2), legacy: true);
            Assert.Equal(6, Regex.Matches(control, Regex.Escape("(H) Tj")).Count);
        }

        /// <summary>
        /// A resolution remembers the file it answered with: with the file cache emptied, the same request
        /// reads the file again and hands back a new program, so a resolution pins no program of its own;
        /// the control, with the file cache kept, hands back the cached one.
        /// </summary>
        [Fact]
        public void AFontResolution_RemembersTheFile_NotAProgramOfItsOwn()
        {
            string path = Path.Combine(Path.GetTempPath(), "tetitu-resolve-" + Guid.NewGuid().ToString("N") + ".ttf");
            File.WriteAllBytes(path, MinimalTrueType.Build("אב"));
            try
            {
                using (FontResolver.UseHostFontFilesForTest(new[] { path }))
                {
                    var first = FontResolver.Resolve("Zzz No Such Family", false, false, new[] { 0x05D0 });
                    Assert.NotNull(first);

                    FontResolver.ClearFileCacheForTest();
                    var reread = FontResolver.Resolve("Zzz No Such Family", false, false, new[] { 0x05D0 });
                    Assert.NotNull(reread);
                    Assert.NotSame(first, reread);

                    var cached = FontResolver.Resolve("Zzz No Such Family", false, false, new[] { 0x05D0 });
                    Assert.Same(reread, cached);
                }
            }
            finally { File.Delete(path); }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>A 3" × 1" inline text box holding <paramref name="text"/>, as Word writes one.</summary>
        private static string TextBoxDrawing(string text)
            => "<w:drawing xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
               "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
               "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
               $"xmlns:wps=\"{WpsNamespace}\"><wp:inline><wp:extent cx=\"2743200\" cy=\"914400\"/>" +
               $"<wp:docPr id=\"1\" name=\"S1\"/><a:graphic><a:graphicData uri=\"{WpsNamespace}\">" +
               "<wps:wsp><wps:spPr><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></wps:spPr>" +
               $"<wps:txbx><w:txbxContent><w:p><w:r><w:rPr><w:sz w:val=\"24\"/></w:rPr><w:t>{text}</w:t></w:r></w:p></w:txbxContent></wps:txbx>" +
               "</wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing>";

        private static byte[] Docx(Action<MainDocumentPart, Body> build)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                build(main, body);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static string Convert(byte[] docx, bool legacy)
            => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = legacy }));

        private static string TextOf(string pdf)
        {
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(pdf)));
            return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);
        }

        private static int PageCount(string pdf) => Regex.Matches(pdf, "/MediaBox").Count;
    }
}
