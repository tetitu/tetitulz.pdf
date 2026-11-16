// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Run and style properties both paths read alike: hidden text is left out; a toggle property set
    /// by both style types is off (ECMA-376 Part 1 §17.7.3); caps and small caps draw capitals; a
    /// non-breaking hyphen, a carriage return and an absolute tab are the run's content
    /// (<c>Rendering.RunContent</c>); a cell paragraph keeps its note references and its list marker;
    /// a paragraph style's numbering numbers its paragraphs; a level's <c>isLgl</c>, <c>decimalZero</c>
    /// and <c>none</c> formats; a table's justification, indent and <c>gridBefore</c>.
    /// </summary>
    public class RunStyleAndTableGapTests
    {
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void HiddenText_IsNotDrawn(bool legacy)
        {
            string text = TextOf(Convert(Docx((_, body) => body.Append(new Paragraph(
                new Run(new RunProperties(new Vanish()), new Text("SECRET")),
                new Run(new Text("VISIBLE"))))), legacy));

            Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
            Assert.Contains("VISIBLE", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// Italic in the paragraph style and italic again in the character style toggle off: the run
        /// in both is upright, the run in the paragraph style alone italic, so the page carries both faces.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AToggleSetByBothStyleTypes_IsOff(bool legacy)
        {
            string pdf = Convert(Docx((main, body) =>
            {
                var styles = main.AddNewPart<StyleDefinitionsPart>();
                styles.Styles = new Styles(
                    new Style(new StyleRunProperties(new Italic())) { Type = StyleValues.Paragraph, StyleId = "Quote" },
                    new Style(new StyleRunProperties(new Italic())) { Type = StyleValues.Character, StyleId = "Emphasis" });
                body.Append(new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Quote" }),
                    new Run(new RunProperties(new RunStyle { Val = "Emphasis" }), new Text("both ")),
                    new Run(new Text("para"))));
            }), legacy);

            Assert.Matches(@"/BaseFont\s*/Helvetica-Oblique", pdf);      // "para": the paragraph style's italic
            Assert.Matches(@"/BaseFont\s*/Helvetica(?![-\w])", pdf);     // "both": the two italics toggled off
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Caps_DrawsTheTextInCapitals(bool legacy)
        {
            string text = TextOf(Convert(Docx((_, body) => body.Append(new Paragraph(
                new Run(new RunProperties(new Caps()), new Text("hello"))))), legacy));

            Assert.Contains("HELLO", text, StringComparison.Ordinal);
            Assert.DoesNotContain("hello", text, StringComparison.Ordinal);
        }

        /// <summary>Small caps: the lowercase letters are drawn as capitals at 0.8 of the size, the capital at the size.</summary>
        [Fact]
        public void SmallCaps_DrawsLowercaseAsSmallerCapitals()
        {
            string pdf = Convert(Docx((_, body) => body.Append(new Paragraph(
                new Run(new RunProperties(new SmallCaps()), new Text("Hello"))))), legacy: true);

            Assert.Contains("HELLO", TextOf(pdf), StringComparison.Ordinal);
            Assert.Matches(@"/F\d+ 12\.00 Tf\n\(H\) Tj", pdf);
            Assert.Matches(@"/F\d+ 9\.60 Tf\n\(ELLO\) Tj", pdf);
        }

        [Fact]
        public void SmallCaps_ViaBuilder_DrawsCapitals()
        {
            string text = TextOf(Convert(Docx((_, body) => body.Append(new Paragraph(
                new Run(new RunProperties(new SmallCaps()), new Text("Hello"))))), legacy: false));

            Assert.Contains("HELLO", text, StringComparison.Ordinal);
        }

        /// <summary>A non-breaking hyphen is a hyphen and a carriage return a line break, on both paths and in a cell.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ANonBreakingHyphenAndACarriageReturn_AreTheRunsContent(bool legacy)
        {
            string text = TextOf(Convert(Docx((_, body) =>
            {
                body.Append(new Paragraph(new Run(new Text("555"), new NoBreakHyphen(), new Text("0100"))));
                body.Append(new Paragraph(new Run(new Text("one"), new CarriageReturn(), new Text("two"))));
                body.Append(new Table(new TableRow(new TableCell(new Paragraph(
                    new Run(new Text("777"), new NoBreakHyphen(), new Text("0200")))))));
            }), legacy));

            Assert.Contains("555-0100", text, StringComparison.Ordinal);
            Assert.Contains("777-0200", text, StringComparison.Ordinal);
            Assert.Contains("one", text, StringComparison.Ordinal);
            Assert.Contains("two", text, StringComparison.Ordinal);
            Assert.DoesNotContain("onetwo", text, StringComparison.Ordinal);
        }

        /// <summary>A footnote referenced from a cell is numbered in document order and laid out; the body's note after it is the second.</summary>
        [Fact]
        public void AFootnoteReferencedFromATableCell_IsNumberedAndLaidOut()
        {
            string text = TextOf(Convert(Docx((main, body) =>
            {
                main.AddNewPart<FootnotesPart>().Footnotes = new Footnotes(
                    new Footnote(new Paragraph(new Run(new Text("CELLNOTE")))) { Id = 1 },
                    new Footnote(new Paragraph(new Run(new Text("BODYNOTE")))) { Id = 2 });
                body.Append(new Table(new TableRow(new TableCell(new Paragraph(
                    new Run(new Text("Revenue")), new Run(new FootnoteReference { Id = 1 }))))));
                body.Append(new Paragraph(new Run(new Text("Later")), new Run(new FootnoteReference { Id = 2 })));
            }), legacy: true));

            Assert.Contains("1. CELLNOTE", text, StringComparison.Ordinal);
            Assert.Contains("2. BODYNOTE", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ACellParagraph_KeepsItsListMarker(bool legacy)
        {
            string text = TextOf(Convert(Docx((main, body) =>
            {
                Numbering(main, (0, NumberFormatValues.Decimal, "%1.", false));
                body.Append(new Table(new TableRow(new TableCell(Numbered("Step", 1, 0)))));
            }), legacy));

            Assert.Contains("1. Step", text, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void NumberingDefinedByAParagraphStyle_NumbersItsParagraphs(bool legacy)
        {
            string text = TextOf(Convert(Docx((main, body) =>
            {
                Numbering(main, (0, NumberFormatValues.Decimal, "%1.", false));
                var styles = main.AddNewPart<StyleDefinitionsPart>();
                styles.Styles = new Styles(new Style(new StyleParagraphProperties(new NumberingProperties(new NumberingId { Val = 1 })))
                { Type = StyleValues.Paragraph, StyleId = "ListNum" });
                body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "ListNum" }), new Run(new Text("alpha"))));
                body.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "ListNum" }), new Run(new Text("beta"))));
            }), legacy));

            Assert.Contains("1. alpha", text, StringComparison.Ordinal);
            Assert.Contains("2. beta", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// A legal level shows the levels before it in decimal (<c>Section 1.01</c>, not <c>Section I.1</c>), a
        /// decimalZero level pads below ten, and a level of format none draws no number and no bullet.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ListLevels_LegalDecimalZeroAndNone(bool legacy)
        {
            string text = TextOf(Convert(Docx((main, body) =>
            {
                Numbering(main,
                    (0, NumberFormatValues.UpperRoman, "Article %1.", false),
                    (1, NumberFormatValues.DecimalZero, "Section %1.%2", true));
                body.Append(Numbered("Top", 1, 0));
                body.Append(Numbered("Sub", 1, 1));
                body.Append(Numbered("Plain", 2, 0));
            }), legacy));

            Assert.Contains("Article I. Top", text, StringComparison.Ordinal);
            Assert.Contains("Section 1.01 Sub", text, StringComparison.Ordinal);
            Assert.Contains("Plain", text, StringComparison.Ordinal);
            Assert.DoesNotContain("•", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// A table narrower than the column is placed by its justification and its indent: centred,
        /// its first cell's text starts 126 pt in; indented 720 twips, 36 pt in; by default at the column's left.
        /// </summary>
        [Theory]
        [InlineData("center", 0, 126.0)]
        [InlineData(null, 720, 36.0)]
        [InlineData(null, 0, 0.0)]
        public void ATable_IsPlacedByItsJustificationAndIndent(string justification, int indentTwips, double expectedOffset)
        {
            string pdf = Convert(Docx((_, body) =>
            {
                var tblPr = new TableProperties(new TableWidth { Width = "4320", Type = TableWidthUnitValues.Dxa });
                if (justification != null) tblPr.Append(new TableJustification { Val = new EnumValue<TableRowAlignmentValues>(justification == "center" ? TableRowAlignmentValues.Center : TableRowAlignmentValues.Right) });
                if (indentTwips != 0) tblPr.Append(new TableIndentation { Width = indentTwips, Type = TableWidthUnitValues.Dxa });
                body.Append(new Table(tblPr, new TableRow(new TableCell(new Paragraph(new Run(new Text("CELLTEXT")))))));
            }), legacy: true);

            double x = TextX(pdf, "CELLTEXT");
            Assert.InRange(x, 72 + expectedOffset + 2, 72 + expectedOffset + 8);   // the cell's left padding after the offset
        }

        /// <summary>A row with <c>w:gridBefore</c> starts at that grid column: its one cell stands under the second column's cell.</summary>
        [Fact]
        public void GridBefore_StartsTheRowAtItsColumn()
        {
            string pdf = Convert(Docx((_, body) => body.Append(new Table(
                new TableGrid(new GridColumn { Width = "2000" }, new GridColumn { Width = "2000" }),
                new TableRow(
                    new TableCell(new Paragraph(new Run(new Text("AAA")))),
                    new TableCell(new Paragraph(new Run(new Text("BBB"))))),
                new TableRow(
                    new TableRowProperties(new GridBefore { Val = 1 }),
                    new TableCell(new Paragraph(new Run(new Text("CCC")))))))), legacy: true);

            double a = TextX(pdf, "AAA"), b = TextX(pdf, "BBB"), c = TextX(pdf, "CCC");
            Assert.True(b > a + 50, $"two columns: A at {a}, B at {b}");
            Assert.InRange(c, b - 0.5, b + 0.5);
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>One numbering instance (id 1) over one definition with the given levels; a second instance (id 2) over a level of format none.</summary>
        private static void Numbering(MainDocumentPart main, params (int ilvl, NumberFormatValues fmt, string text, bool legal)[] levels)
        {
            var definition = new AbstractNum { AbstractNumberId = 0 };
            foreach (var (ilvl, fmt, text, legal) in levels)
            {
                var level = new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = fmt }, new LevelText { Val = text }) { LevelIndex = ilvl };
                if (legal) level.Append(new IsLegalNumberingStyle());
                definition.Append(level);
            }
            var none = new AbstractNum(new Level(new StartNumberingValue { Val = 1 }, new NumberingFormat { Val = NumberFormatValues.None }, new LevelText { Val = "" }) { LevelIndex = 0 })
            { AbstractNumberId = 1 };
            main.AddNewPart<NumberingDefinitionsPart>().Numbering = new Numbering(definition, none,
                new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 },
                new NumberingInstance(new AbstractNumId { Val = 1 }) { NumberID = 2 });
        }

        private static Paragraph Numbered(string text, int numId, int ilvl) => new Paragraph(
            new ParagraphProperties(new NumberingProperties(new NumberingLevelReference { Val = ilvl }, new NumberingId { Val = numId })),
            new Run(new Text(text)));

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

        /// <summary>The x of the <c>Td</c> that positions the line showing <paramref name="text"/>.</summary>
        private static double TextX(string pdf, string text)
        {
            var m = Regex.Match(pdf, @"(-?[\d.]+) (-?[\d.]+) Td\n(?:[^\n]*\n){0,4}?\(" + Regex.Escape(text) + @"\) Tj");
            Assert.True(m.Success, $"'{text}' was not drawn");
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
