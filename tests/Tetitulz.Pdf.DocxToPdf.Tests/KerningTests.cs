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
using F = Tetitulz.Pdf.TestHelpers.KerningFont;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Word's <c>w:kern</c>: a run is kerned when its size is at least the value, in half-points; no
    /// value, or 0, does not kern. The run's own property, then its style, then the document
    /// defaults — and both renderers, the legacy one in a paragraph and a table cell. The DOCX embeds
    /// <see cref="F"/>, under which bet then vav at 20 points kerned is vav, narrowed by 50
    /// thousandths, then bet: <c>[&lt;0009&gt; 50 &lt;0008&gt;] TJ</c>.
    /// </summary>
    public class KerningTests
    {
        private const string Family = "KerningEmbedded";
        private const string Key = "{1A2B3C4D-5E6F-7081-92A3-B4C5D6E7F809}";
        private const string Kerned = "[<0009> 50 <0008>] TJ";
        private const string Plain = "<00090008> Tj";
        private static readonly string BetVav = $"{F.Bet}{F.Vav}";

        public enum Where { Run, Style, Defaults }

        /// <summary>At 20 points (40 half-points), kerning from 12 points kerns, from 24 does not, and 0 never does.</summary>
        [Theory]
        [InlineData(false, 24u, true)]
        [InlineData(false, 40u, true)]
        [InlineData(false, 48u, false)]
        [InlineData(false, 0u, false)]
        [InlineData(true, 24u, true)]
        [InlineData(true, 48u, false)]
        [InlineData(true, 0u, false)]
        public void ARunIsKerned_FromItsThreshold(bool legacy, uint threshold, bool kerned)
            => AssertKerned(Convert(Docx(threshold, Where.Run), legacy), kerned, legacy ? 2 : 1);

        [Theory]
        [InlineData(false, Where.Style)]
        [InlineData(false, Where.Defaults)]
        [InlineData(true, Where.Style)]
        [InlineData(true, Where.Defaults)]
        public void TheThreshold_IsInheritedFromTheStyleAndTheDefaults(bool legacy, Where where)
            => AssertKerned(Convert(Docx(24, where), legacy), true, legacy ? 2 : 1);

        /// <summary>No <c>w:kern</c> anywhere: Word does not kern, and neither does the conversion.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NoThreshold_DoesNotKern(bool legacy)
            => AssertKerned(Convert(Docx(null, Where.Run), legacy), false, legacy ? 2 : 1);

        // The legacy renderer draws the paragraph and the table cell; the Builder path the paragraph
        // (its table cell draws the run too, but its count is the Builder's concern, not w:kern's).
        private static void AssertKerned(string content, bool kerned, int atLeast)
        {
            int k = Count(content, Kerned), p = Count(content, Plain);
            Assert.True((kerned ? k : p) >= atLeast, $"kerned {k}, plain {p}");
            Assert.Equal(0, kerned ? p : k);
        }

        private static int Count(string s, string what)
        {
            int n = 0;
            for (int i = s.IndexOf(what, System.StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + 1, System.StringComparison.Ordinal)) n++;
            return n;
        }

        // The Builder path is driven in its two halves, the export given the fixture through
        // FontProvider: DocxToPdfConverter exports with no options, and the Builder does not read a
        // DOCX's embedded fonts (BuilderPathTests.BuilderPath_Hebrew_WithAnInjectedFont_EmbedsCompositeFont).
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
                    FontProvider = _ => F.Build(),
                });
            }
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            return string.Concat(Enumerable.Range(0, doc.Pages.Count)
                .Select(i => Encoding.Latin1.GetString(doc.Pages[i].GetContentStreamData())));
        }

        private static Run HebrewRun(uint? threshold, Where where)
        {
            var rp = new RunProperties(
                new RunFonts { Ascii = Family, HighAnsi = Family, ComplexScript = Family },
                new FontSize { Val = "40" }, new FontSizeComplexScript { Val = "40" });
            if (where == Where.Style) rp.PrependChild(new RunStyle { Val = "Kerned" });
            if (threshold != null && where == Where.Run) rp.Append(new Kern { Val = threshold.Value });
            return new Run(rp, new Text(BetVav));
        }

        private static byte[] Docx(uint? threshold, Where where)
        {
            byte[] obfuscated = ObfuscatedFontReader.Deobfuscate(F.Build(), Key);   // XOR: its own inverse
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var table = new Table(new TableRow(new TableCell(new Paragraph(HebrewRun(threshold, where)))));
                main.Document = new Document(new Body(new Paragraph(HebrewRun(threshold, where)), table));
                main.Document.Save();

                if (threshold != null && where != Where.Run)
                {
                    var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
                    var styles = new Styles();
                    if (where == Where.Defaults)
                        styles.Append(new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(new Kern { Val = threshold.Value }))));
                    else
                        styles.Append(new Style(new StyleName { Val = "Kerned" },
                            new StyleRunProperties(new Kern { Val = threshold.Value }))
                        { Type = StyleValues.Character, StyleId = "Kerned" });
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
