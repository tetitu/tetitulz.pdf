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
using F = Tetitulz.Pdf.TestHelpers.RcltFont;
using W14 = DocumentFormat.OpenXml.Office2010.Word;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Word 2010's <c>w14:cntxtAlts</c> ("use contextual alternates"): the font's <c>calt</c> is drawn
    /// where it is on — the run's property, then its style, then the document defaults — and not
    /// otherwise, as Word leaves it off. Both renderers, the legacy one in a paragraph and a table
    /// cell. The DOCX embeds <see cref="F"/>'s <c>calt</c> font, under which beh then yeh is
    /// <c>&lt;00020003&gt; Tj</c> with the alternate and <c>&lt;00020001&gt; Tj</c> without.
    /// </summary>
    public class ContextualAlternatesTests
    {
        private const string Family = "RcltEmbedded";
        private const string Key = "{2B3C4D5E-6F70-8192-A3B4-C5D6E7F8091A}";
        private const string Alternate = "<00020003> Tj";
        private const string Plain = "<00020001> Tj";

        public enum Where { Run, Style, Defaults }

        [Theory]
        [InlineData(false, Where.Run)]
        [InlineData(false, Where.Style)]
        [InlineData(false, Where.Defaults)]
        [InlineData(true, Where.Run)]
        [InlineData(true, Where.Style)]
        [InlineData(true, Where.Defaults)]
        public void ContextualAlternatesOn_DrawsTheAlternate(bool legacy, Where where)
            => AssertAlternate(Convert(Docx("1", where), legacy), true, legacy ? 2 : 1);

        /// <summary>No <c>w14:cntxtAlts</c>, or one set to false or 0: Word draws no alternate, and neither does the conversion.</summary>
        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "false")]
        [InlineData(false, "0")]
        [InlineData(true, null)]
        [InlineData(true, "false")]
        public void ContextualAlternatesOff_DrawsTheLetter(bool legacy, string val)
            => AssertAlternate(Convert(Docx(val, Where.Run), legacy), false, legacy ? 2 : 1);

        /// <summary>
        /// The legacy renderer lays the run out at the width it draws: right-aligned, the alternate
        /// (300) in place of the beh (400) makes it 2 points narrower at 20 points, so it starts 2
        /// points further right — 525 against 523, the margin at 540.
        /// </summary>
        [Fact]
        public void TheLegacyRenderer_LaysOutTheAlternatesWidth()
        {
            double on = FirstTextX(Convert(Docx("1", Where.Run, right: true), legacy: true));
            double off = FirstTextX(Convert(Docx(null, Where.Run, right: true), legacy: true));
            Assert.Equal(525.0, on, 2);
            Assert.Equal(523.0, off, 2);
        }

        private static double FirstTextX(string content)
        {
            var m = System.Text.RegularExpressions.Regex.Match(content, @"(\S+) \S+ Td");
            Assert.True(m.Success, "no text position was written");
            return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void AssertAlternate(string content, bool alternate, int atLeast)
        {
            int a = Count(content, Alternate), p = Count(content, Plain);
            Assert.True((alternate ? a : p) >= atLeast, $"alternate {a}, plain {p}");
            Assert.Equal(0, alternate ? p : a);
        }

        private static int Count(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, System.StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, System.StringComparison.Ordinal)) n++;
            return n;
        }

        private static string Convert(byte[] docx, bool legacy)
        {
            byte[] pdf;
            if (legacy)
                pdf = DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = true });
            else
            {
                using var input = new MemoryStream(docx);
                using var wordDoc = WordprocessingDocument.Open(input, false);
                pdf = DocxToBuilderMapper.Map(wordDoc, new DocxToPdfOptions()).ToPdf(new Builder.Export.PdfExportOptions
                {
                    AllowSystemFontSubstitution = false,
                    FontProvider = _ => F.Build(feature: "calt"),
                });
            }
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            return string.Concat(Enumerable.Range(0, doc.Pages.Count)
                .Select(i => Encoding.Latin1.GetString(doc.Pages[i].GetContentStreamData())));
        }

        // A w14:cntxtAlts with the given w14:val; a null val means no element at all (the callers').
        private static W14.ContextualAlternatives Element(string val)
        {
            var e = new W14.ContextualAlternatives();
            e.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("w14", "val", "http://schemas.microsoft.com/office/word/2010/wordml", val));
            return e;
        }

        private static Run ArabicRun(string val, Where where)
        {
            var rp = new RunProperties(
                new RunFonts { Ascii = Family, HighAnsi = Family, ComplexScript = Family },
                new FontSize { Val = "40" }, new FontSizeComplexScript { Val = "40" });
            if (where == Where.Style) rp.PrependChild(new RunStyle { Val = "Alternates" });
            if (val != null && where == Where.Run) rp.Append(Element(val));
            return new Run(rp, new Text("بي"));
        }

        private static byte[] Docx(string val, Where where, bool right = false)
        {
            byte[] obfuscated = ObfuscatedFontReader.Deobfuscate(F.Build(feature: "calt"), Key);   // XOR: its own inverse
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var table = new Table(new TableRow(new TableCell(new Paragraph(ArabicRun(val, where)))));
                var body = new Paragraph(ArabicRun(val, where));
                if (right) body.PrependChild(new ParagraphProperties(new Justification { Val = JustificationValues.Right }));
                main.Document = new Document(new Body(body, table));
                main.Document.Save();

                if (val != null && where != Where.Run)
                {
                    var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
                    var styles = new Styles();
                    if (where == Where.Defaults)
                        styles.Append(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(Element(val)))));
                    else
                        styles.Append(new Style(new StyleName { Val = "Alternates" }, new StyleRunProperties(Element(val)))
                        { Type = StyleValues.Character, StyleId = "Alternates" });
                    stylesPart.Styles = styles;
                    stylesPart.Styles.Save();
                }

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
