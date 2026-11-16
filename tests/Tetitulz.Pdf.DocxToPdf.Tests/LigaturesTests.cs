// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.LigatureFont;
using W14 = DocumentFormat.OpenXml.Office2010.Word;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Word 2010's <c>w14:ligatures</c>: the font's <c>liga</c> ligatures are formed where it names
    /// any kind but <c>none</c> — the run's property, then its style, then the document defaults —
    /// and not otherwise, as Word leaves them off. Both renderers, the legacy one in a paragraph and
    /// a table cell. The DOCX embeds <see cref="F"/>, under which f, i, ā is <c>&lt;00040007&gt; Tj</c>
    /// with the fi ligature and <c>&lt;000100020007&gt; Tj</c> as the letters — the ā, outside
    /// WinAnsi, is what sends the legacy renderer's run to the embedded font at all; a run it can
    /// write in a standard face it draws there, where the viewer's font has no say in ligatures.
    /// </summary>
    public class LigaturesTests
    {
        private const string Family = "LigatureEmbedded";
        private const string Key = "{3C4D5E6F-7081-92A3-B4C5-D6E7F8091A2B}";
        private const string Sample = "fiā";
        private const string Ligature = "<00040007> Tj";
        private const string Plain = "<000100020007> Tj";

        public enum Where { Run, Style, Defaults }

        [Theory]
        [InlineData(false, Where.Run, "standard")]
        [InlineData(false, Where.Style, "standard")]
        [InlineData(false, Where.Defaults, "standard")]
        [InlineData(false, Where.Run, "standardContextual")]
        [InlineData(false, Where.Run, "all")]
        [InlineData(true, Where.Run, "standard")]
        [InlineData(true, Where.Style, "standard")]
        [InlineData(true, Where.Defaults, "standard")]
        public void LigaturesOn_FormsTheLigature(bool legacy, Where where, string val)
            => AssertLigature(Convert(Docx(val, where), legacy), true, legacy ? 2 : 1);

        /// <summary>No <c>w14:ligatures</c>, or one set to none: Word forms no ligature, and neither does the conversion.</summary>
        [Theory]
        [InlineData(false, null)]
        [InlineData(false, "none")]
        [InlineData(true, null)]
        [InlineData(true, "none")]
        public void LigaturesOff_DrawsTheLetters(bool legacy, string val)
            => AssertLigature(Convert(Docx(val, Where.Run), legacy), false, legacy ? 2 : 1);

        /// <summary>
        /// The legacy renderer lays the run out at the width it draws: right-aligned, the fi
        /// ligature (520) in place of f and i (550) makes the run 0.6 points narrower at 20 points,
        /// so it starts that much further right.
        /// </summary>
        [Fact]
        public void TheLegacyRenderer_LaysOutTheLigaturesWidth()
        {
            double on = FirstTextX(Convert(Docx("standard", Where.Run, right: true), legacy: true));
            double off = FirstTextX(Convert(Docx(null, Where.Run, right: true), legacy: true));
            Assert.Equal(0.6, on - off, 2);
        }

        /// <summary>
        /// The legacy renderer wraps at the ligated width too: a word is 20 points with the ligature
        /// and 20.6 without (the space has no glyph and takes no room), so 46 words fill two lines of
        /// 23 at 468 points when ligated and need three of 22 when not. The wrap measures each piece
        /// through its own site, which the whole-segment width cannot stand in for.
        /// </summary>
        [Fact]
        public void TheLegacyRenderer_WrapsAtTheLigatedWidth()
        {
            string words = string.Join(" ", Enumerable.Repeat(Sample, 46));
            int on = Count(Convert(Docx("standard", Where.Run, text: words, table: false), legacy: true), " Td\n");
            int off = Count(Convert(Docx(null, Where.Run, text: words, table: false), legacy: true), " Td\n");
            Assert.Equal(3, off);
            Assert.Equal(2, on);
        }

        /// <summary>Both renderers write the ligature back in <c>/ToUnicode</c> as its letters.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheLigature_IsWrittenBackAsItsLetters(bool legacy)
        {
            string streams = PdfContentProbe.DecodeStreams(Pdf(Docx("standard", Where.Run), legacy));
            Assert.Contains("<0004> <00660069>", streams);
            Assert.Contains("<0007> <0101>", streams);
            Assert.Contains(Ligature, streams);
        }

        private static double FirstTextX(string content)
        {
            var m = System.Text.RegularExpressions.Regex.Match(content, @"(\S+) \S+ Td");
            Assert.True(m.Success, "no text position was written");
            return double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void AssertLigature(string content, bool ligature, int atLeast)
        {
            int a = Count(content, Ligature), p = Count(content, Plain);
            Assert.True((ligature ? a : p) >= atLeast, $"ligature {a}, plain {p}");
            Assert.Equal(0, ligature ? p : a);
        }

        private static int Count(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, System.StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, System.StringComparison.Ordinal)) n++;
            return n;
        }

        /// <summary>
        /// The discretional and historical kinds of <c>w14:ligatures</c> form the font's <c>dlig</c> and
        /// <c>hlig</c> on the default (Builder) route; a value without the kind does not. The legacy
        /// renderer reads the standard set only.
        /// </summary>
        [Theory]
        [InlineData("dlig", "standardDiscretional", true)]
        [InlineData("dlig", "discretional", true)]
        [InlineData("dlig", "all", true)]
        [InlineData("dlig", "standardHistorical", false)]
        [InlineData("hlig", "historical", true)]
        [InlineData("hlig", "contextualHistoricalDiscretional", true)]
        [InlineData("hlig", "standardDiscretional", false)]
        public void TheDiscretionalAndHistoricalKinds_FormTheirFeatures(string feature, string val, bool ligature)
            => AssertLigature(Convert(Docx(val, Where.Run, table: false, feature: feature), legacy: false, feature), ligature, 1);

        private static byte[] Pdf(byte[] docx, bool legacy, string feature = "liga")
        {
            if (legacy)
                return DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = true });
            using var input = new MemoryStream(docx);
            using var wordDoc = WordprocessingDocument.Open(input, false);
            return DocxToBuilderMapper.Map(wordDoc, new DocxToPdfOptions()).ToPdf(new Builder.Export.PdfExportOptions
            {
                AllowSystemFontSubstitution = false,
                FontProvider = _ => F.Build(feature: feature),
            });
        }

        /// <summary>The page content of the conversion.</summary>
        private static string Convert(byte[] docx, bool legacy, string feature = "liga")
        {
            using var doc = PdfDocument.Open(new MemoryStream(Pdf(docx, legacy, feature)));
            return string.Concat(Enumerable.Range(0, doc.Pages.Count)
                .Select(i => Encoding.Latin1.GetString(doc.Pages[i].GetContentStreamData())));
        }

        // A w14:ligatures with the given w14:val; a null val means no element at all (the callers').
        private static W14.Ligatures Element(string val)
        {
            var e = new W14.Ligatures();
            e.SetAttribute(new DocumentFormat.OpenXml.OpenXmlAttribute("w14", "val", "http://schemas.microsoft.com/office/word/2010/wordml", val));
            return e;
        }

        private static Run LatinRun(string val, Where where, string text = Sample)
        {
            var rp = new RunProperties(
                new RunFonts { Ascii = Family, HighAnsi = Family, ComplexScript = Family },
                new FontSize { Val = "40" }, new FontSizeComplexScript { Val = "40" });
            if (where == Where.Style) rp.PrependChild(new RunStyle { Val = "Ligatures" });
            if (val != null && where == Where.Run) rp.Append(Element(val));
            return new Run(rp, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        }

        /// <summary>A body paragraph of <paramref name="text"/> and, unless <paramref name="table"/> is off, the same in a one-cell table.</summary>
        private static byte[] Docx(string val, Where where, bool right = false, string text = Sample, bool table = true,
                                   string feature = "liga")
        {
            byte[] obfuscated = ObfuscatedFontReader.Deobfuscate(F.Build(feature: feature), Key);   // XOR: its own inverse
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Paragraph(LatinRun(val, where, text));
                if (right) body.PrependChild(new ParagraphProperties(new Justification { Val = JustificationValues.Right }));
                var content = new Body(body);
                if (table) content.Append(new Table(new TableRow(new TableCell(new Paragraph(LatinRun(val, where, text))))));
                main.Document = new Document(content);
                main.Document.Save();

                if (val != null && where != Where.Run)
                {
                    var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
                    var styles = new Styles();
                    if (where == Where.Defaults)
                        styles.Append(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(Element(val)))));
                    else
                        styles.Append(new Style(new StyleName { Val = "Ligatures" }, new StyleRunProperties(Element(val)))
                        { Type = StyleValues.Character, StyleId = "Ligatures" });
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
