// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Each paragraph's lines take its own <c>w:spacing w:line</c> on the legacy path, the spacing remembered for the
    /// paragraph being laid out (<c>StyleResolver.LineSpacingOf</c>, which keeps the line height from searching a
    /// paragraph of many runs once a line) and never carried to the next.
    /// </summary>
    public class LineSpacingPerParagraphTests
    {
        private static Paragraph TwoLines(string name, int line)
            => new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Line = line.ToString(CultureInfo.InvariantCulture), LineRule = LineSpacingRuleValues.Auto }),
                new Run(new Text(name + "1"), new Break(), new Text(name + "2")));

        private static double Gap(string content, string name)
            => LineY(content, name + "1") - LineY(content, name + "2");

        private static double LineY(string content, string literal)
        {
            int at = content.IndexOf("(" + literal + ") Tj", StringComparison.Ordinal);
            Assert.True(at >= 0, $"({literal}) Tj not drawn");
            Match line = null;
            foreach (Match m in Regex.Matches(content.Substring(0, at), @"BT\s+(-?[\d.]+)\s+(-?[\d.]+)\s+Td")) line = m;
            Assert.NotNull(line);
            return double.Parse(line.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        private static string Render(params Paragraph[] paragraphs)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(paragraphs));
                main.Document.Save();
            }
            return PdfContentProbe.DecodeStreams(DocxToPdfConverter.ConvertToBytes(ms.ToArray(), new DocxToPdfOptions { UseLegacyRenderer = true }));
        }

        /// <summary>Single (240) then double (480) spacing, and the other way round: each paragraph's gap is its own, the double twice the single.</summary>
        [Fact]
        public void EachParagraph_KeepsItsOwnLineSpacing_InEitherOrder()
        {
            string singleFirst = Render(TwoLines("SA", 240), TwoLines("DB", 480));
            string doubleFirst = Render(TwoLines("DA", 480), TwoLines("SB", 240));

            double single = Gap(singleFirst, "SA");
            Assert.True(single > 5, $"single gap {single}");
            Assert.Equal(2 * single, Gap(singleFirst, "DB"), 1);
            Assert.Equal(2 * single, Gap(doubleFirst, "DA"), 1);
            Assert.Equal(single, Gap(doubleFirst, "SB"), 1);
        }
    }
}
