// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Regression tests for the DocxToPdf full-review fixes: image rendering
    /// (PNG/JPEG), table grid-span / borders, list numbering, colour-operator
    /// robustness, and hanging indents.
    /// </summary>
    public class DocxToPdfFixesTests
    {
        private static string ConvertToText(byte[] docx)
        {
            // Uncompressed streams so the content stream is inspectable as Latin-1 text.
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false });
            return Encoding.Latin1.GetString(pdf);
        }

        // ---------------- Images ----------------

        [Fact]
        public void PngImage_IsDecodedAndEmbedded()
        {
            byte[] png = DocxImageBuilder.MakeTruecolorPng(4, 3, 10, 20, 30);
            byte[] docx = DocxImageBuilder.CreateDocxWithImage(png, "image/png");

            string pdf = ConvertToText(docx);

            // Previously ImageRenderer was never invoked AND PNG was dropped; now the
            // PNG is decoded to RGB and embedded as a FlateDecode image XObject.
            Assert.Contains("/Subtype /Image", pdf);
            Assert.Contains("/Width 4", pdf);
            Assert.Contains("/Height 3", pdf);
            Assert.Contains("/FlateDecode", pdf);
            Assert.Matches(@"/Im\d+ Do", pdf);
        }

        [Fact]
        public void PngDecompressionBomb_IsRejectedAndConversionSucceeds()
        {
            // Tiny declared dimensions but a ~2 MB-inflating IDAT: the bounded
            // inflate must reject it (no OOM) and the conversion must still produce
            // a valid PDF with no embedded image.
            byte[] docx = DocxImageBuilder.CreateDocxWithImage(DocxImageBuilder.MakeBombPng(), "image/png");

            string pdf = ConvertToText(docx);

            Assert.Contains("%PDF", pdf);
            Assert.Contains("%%EOF", pdf);
            Assert.Contains("/MediaBox", pdf);
            Assert.DoesNotContain("/Subtype /Image", pdf); // bomb skipped, nothing embedded
        }

        [Fact]
        public void JpegImage_IsEmbeddedAsDctDecodeWithParsedDimensions()
        {
            byte[] jpg = DocxImageBuilder.MakeJpegHeader(20, 10);
            byte[] docx = DocxImageBuilder.CreateDocxWithImage(jpg, "image/jpeg");

            string pdf = ConvertToText(docx);

            Assert.Contains("/DCTDecode", pdf);
            Assert.Contains("/Width 20", pdf);   // from SOF0, not the display points
            Assert.Contains("/Height 10", pdf);
        }

        /// <summary>
        /// A JPEG is embedded under the colour space its frame denotes (Core.Imaging.JpegColorSpace):
        /// one component is DeviceGray, Adobe's four DeviceCMYK with the inverting /Decode, and four
        /// without the Adobe marker — whose polarity nothing in the codestream says — are refused and
        /// reported. Three components, the test above, are DeviceRGB.
        /// </summary>
        [Fact]
        public void JpegImage_IsEmbeddedUnderItsFramesColourSpace()
        {
            string grey = ConvertToText(DocxImageBuilder.CreateDocxWithImage(
                DocxImageBuilder.MakeJpegHeader(8, 8, components: 1), "image/jpeg"));
            Assert.Contains("/ColorSpace /DeviceGray", grey);
            Assert.Matches(@"/Im\d+ Do", grey);

            string cmyk = ConvertToText(DocxImageBuilder.CreateDocxWithImage(
                DocxImageBuilder.MakeJpegHeader(8, 8, components: 4, adobe: true), "image/jpeg"));
            Assert.Contains("/ColorSpace /DeviceCMYK", cmyk);
            Assert.Matches(@"/Decode \[\s*1 0 1 0 1 0 1 0\s*\]", cmyk);

            string unknown = ConvertToText(DocxImageBuilder.CreateDocxWithImage(
                DocxImageBuilder.MakeJpegHeader(8, 8, components: 4), "image/jpeg"));
            Assert.DoesNotContain("/Subtype /Image", unknown);
            Assert.Contains("APP14", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        // ---------------- Tables ----------------

        [Fact]
        public void Table_WithNilBorders_DrawsNoCellBoxes()
        {
            string pdf = ConvertToText(BuildTableDocx(nilBorders: true));
            Assert.DoesNotContain("re S", pdf);   // no default box
            Assert.DoesNotContain("l\nS", pdf);   // no per-side strokes
        }

        [Fact]
        public void Table_WithSingleBorders_DrawsStrokes()
        {
            string pdf = ConvertToText(BuildTableDocx(nilBorders: false));
            Assert.Contains("l\nS", pdf);
        }

        [Fact]
        public void Table_GridSpan_CellSpansFullWidth()
        {
            string pdf = ConvertToText(BuildGridSpanTableDocx());
            // The single gridSpan=2 cell must reach the table's full width: a border
            // line from the table left (x=72) to the table right (x=540). Without
            // gridSpan handling the spanned cell would stop at the first column.
            Assert.Matches(@"72\.00 (\d+\.\d\d) m\n540\.00 \1 l", pdf);
        }

        [Fact]
        public void NestedTable_IsLaidOutAsRealSubTable()
        {
            string pdf = ConvertToText(BuildNestedTableDocx());
            Assert.Contains("Nested row 1", pdf);
            Assert.Contains("Nested row 2", pdf);
            // The nested table's own left border sits inside the parent cell's padding
            // (x = ContentLeft 72 + pad 4 = 76) — proof it's drawn as a real sub-table,
            // not flattened to plain text in the parent cell.
            Assert.Matches(@"76\.00 \d+\.\d\d m\n76\.00", pdf);
        }

        [Fact]
        public void NestedTable_TallerThanPage_SplitsAcrossPages()
        {
            // A cell hosting a 120-row nested table is far taller than a page; the
            // nested rows must split across pages (no lost content).
            string pdf = ConvertToText(BuildTallNestedTableDocx(120));

            int pageCount = Regex.Matches(pdf, "/MediaBox").Count;
            Assert.True(pageCount >= 2, $"a tall nested table must split across pages, got {pageCount}");
            Assert.Contains("Nested 0 ", pdf);
            Assert.Contains("Nested 119", pdf);
        }

        [Fact]
        public void Table_DistinctOuterAndInsideBorders_UseCorrectWidthsByPosition()
        {
            // Outer borders sz=24 (3pt), inside borders sz=4 (0.5pt). With
            // position-aware resolution the perimeter edges are 3pt and the
            // interior (shared) edges are 0.5pt — both widths must appear.
            string pdf = ConvertToText(BuildOuterInnerBorderDocx());
            Assert.Contains("3.00 w", pdf);  // perimeter
            Assert.Contains("0.50 w", pdf);  // interior (would be absent if outer were folded onto every edge)
        }

        [Fact]
        public void Table_GridSpanOvershoot_DoesNotDropCells()
        {
            // A row with a gridSpan=2 cell followed by another cell exceeds a
            // 2-column grid; the trailing cell must still render (not be dropped).
            string pdf = ConvertToText(BuildGridSpanOvershootDocx());
            Assert.Contains("Spans", pdf);
            Assert.Contains("Overshoot", pdf);
        }

        [Fact]
        public void Table_RowTallerThanPage_SplitsAcrossPages()
        {
            // One row whose single cell holds ~120 lines — far taller than a page body.
            string pdf = ConvertToText(BuildTallRowDocx(120));

            // Each page dictionary carries its own /MediaBox; the pages tree does not.
            int pageCount = Regex.Matches(pdf, "/MediaBox").Count;
            Assert.True(pageCount >= 2, $"a row taller than the page must split across pages, got {pageCount}");

            // No content is lost — early and late lines both appear.
            Assert.Contains("Line 1 ", pdf);
            Assert.Contains("Line 119", pdf);
        }

        // ---------------- Lists ----------------

        [Fact]
        public void List_HonorsStartValue()
        {
            byte[] docx = BuildListDocx(
                new[] { (0, NumberFormatValues.Decimal, 5, "%1.") },
                new[] { (0, "First") });

            string pdf = ConvertToText(docx);
            Assert.Contains("(5. )", pdf);          // started at 5, not 1
            Assert.DoesNotContain("(1. )", pdf);
        }

        [Fact]
        public void List_MultiLevel_SubstitutesAllLevels()
        {
            byte[] docx = BuildListDocx(
                new[]
                {
                    (0, NumberFormatValues.Decimal, 1, "%1."),
                    (1, NumberFormatValues.Decimal, 1, "%1.%2."),
                },
                new[] { (0, "Parent"), (1, "Child") });

            string pdf = ConvertToText(docx);
            Assert.Contains("1.1.", pdf);           // not a literal "%1.%2."
            Assert.DoesNotContain("%2", pdf);
        }

        [Fact]
        public void List_LowerLetter_WrapsBeyond26()
        {
            var items = Enumerable.Range(0, 27).Select(_ => (0, "x")).ToArray();
            byte[] docx = BuildListDocx(
                new[] { (0, NumberFormatValues.LowerLetter, 1, "%1.") },
                items);

            string pdf = ConvertToText(docx);
            Assert.Contains("aa.", pdf);            // 27th item is "aa", not "a"
        }

        // ---------------- Colour / indent helpers ----------------

        [Theory]
        [InlineData("auto")]
        [InlineData("GGGGGG")]
        [InlineData("12")]
        [InlineData("")]
        public void FormatColorOperator_InvalidInput_ReturnsNullWithoutThrowing(string input)
        {
            Assert.Null(RunRenderer.FormatColorOperator(input));
        }

        [Fact]
        public void FormatColorOperator_ValidHex_ProducesRgb()
        {
            Assert.Equal("1.000 0.000 0.000 rg\n", RunRenderer.FormatColorOperator("FF0000"));
        }

        [Fact]
        public void HangingIndent_ProducesNegativeFirstLineOffset()
        {
            byte[] docx = BuildHangingIndentDocx(hangingTwips: 720); // 36 pt

            using var ms = new MemoryStream(docx);
            using var doc = WordprocessingDocument.Open(ms, false);
            var resolver = new StyleResolver(doc);
            var para = doc.MainDocumentPart!.Document.Body!.Elements<Paragraph>().First();

            var (_, _, firstLine) = resolver.ResolveIndentation(para);
            Assert.True(firstLine < 0, $"hanging indent should outdent the first line, got {firstLine}");
            Assert.Equal(-36.0, firstLine, 3);
        }

        // ---------------- Glyph metrics ----------------

        [Fact]
        public void GlyphMetrics_ProportionalFont_DistinguishesNarrowAndWide()
        {
            double iiii = StandardFontMetrics.MeasureText("iiii", "Helvetica", 12);
            double mmmm = StandardFontMetrics.MeasureText("mmmm", "Helvetica", 12);
            Assert.True(iiii < mmmm,
                $"narrow 'i' must measure less than wide 'm' (got {iiii} vs {mmmm}) — proves real " +
                "per-glyph widths replaced the flat average estimate");
        }

        [Fact]
        public void GlyphMetrics_Courier_IsMonospaced()
        {
            Assert.Equal(
                StandardFontMetrics.MeasureText("i", "Courier", 12),
                StandardFontMetrics.MeasureText("m", "Courier", 12), 6);
        }

        [Fact]
        public void GlyphMetrics_KnownString_MatchesAfmWidths()
        {
            // Helvetica "Hello" = H722 + e556 + l222 + l222 + o556 = 2278 /1000 * 12pt.
            double w = StandardFontMetrics.MeasureText("Hello", "Helvetica", 12);
            Assert.Equal(2278.0 / 1000.0 * 12.0, w, 4);
        }

        // ==================================================================
        //  DOCX builders
        // ==================================================================

        private static byte[] BuildTableDocx(bool nilBorders)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                BorderType B<T>() where T : BorderType, new() =>
                    nilBorders
                        ? new T { Val = BorderValues.Nil }
                        : new T { Val = BorderValues.Single, Size = 4U, Color = "000000" };

                var borders = new TableBorders(
                    B<TopBorder>(), B<LeftBorder>(), B<BottomBorder>(), B<RightBorder>(),
                    B<InsideHorizontalBorder>(), B<InsideVerticalBorder>());

                var table = new Table(new TableProperties(borders));
                for (int r = 0; r < 2; r++)
                {
                    var row = new TableRow();
                    for (int c = 0; c < 2; c++)
                        row.Append(new TableCell(new Paragraph(new Run(new Text($"R{r}C{c}")))));
                    table.Append(row);
                }

                mainPart.Document = new Document(new Body(table));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] BuildGridSpanTableDocx()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                BorderType B<T>() where T : BorderType, new() =>
                    new T { Val = BorderValues.Single, Size = 4U, Color = "000000" };
                var borders = new TableBorders(
                    B<TopBorder>(), B<LeftBorder>(), B<BottomBorder>(), B<RightBorder>(),
                    B<InsideHorizontalBorder>(), B<InsideVerticalBorder>());

                var table = new Table(
                    new TableProperties(borders),
                    new TableGrid(new GridColumn { Width = "4680" }, new GridColumn { Width = "4680" }));

                // Row 1: one cell spanning both columns.
                var spanned = new TableCell(
                    new TableCellProperties(new GridSpan { Val = 2 }),
                    new Paragraph(new Run(new Text("Spanned"))));
                table.Append(new TableRow(spanned));

                // Row 2: two normal cells.
                table.Append(new TableRow(
                    new TableCell(new Paragraph(new Run(new Text("A")))),
                    new TableCell(new Paragraph(new Run(new Text("B"))))));

                mainPart.Document = new Document(new Body(table));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static TableBorders SingleBorders()
        {
            BorderType Bd<T>() where T : BorderType, new() =>
                new T { Val = BorderValues.Single, Size = 4U, Color = "000000" };
            return new TableBorders(
                Bd<TopBorder>(), Bd<LeftBorder>(), Bd<BottomBorder>(), Bd<RightBorder>(),
                Bd<InsideHorizontalBorder>(), Bd<InsideVerticalBorder>());
        }

        private static byte[] BuildTallNestedTableDocx(int nestedRows)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                var nested = new Table(new TableProperties(SingleBorders()),
                    new TableGrid(new GridColumn { Width = "9000" }));
                for (int i = 0; i < nestedRows; i++)
                    nested.Append(new TableRow(new TableCell(new Paragraph(new Run(new Text($"Nested {i} row"))))));

                var outerCell = new TableCell(nested, new Paragraph());
                var outer = new Table(new TableProperties(SingleBorders()),
                    new TableGrid(new GridColumn { Width = "9360" }), new TableRow(outerCell));

                mainPart.Document = new Document(new Body(outer));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] BuildOuterInnerBorderDocx()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                var borders = new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 24U, Color = "000000" },
                    new LeftBorder { Val = BorderValues.Single, Size = 24U, Color = "000000" },
                    new BottomBorder { Val = BorderValues.Single, Size = 24U, Color = "000000" },
                    new RightBorder { Val = BorderValues.Single, Size = 24U, Color = "000000" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "000000" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "000000" });
                var table = new Table(new TableProperties(borders),
                    new TableGrid(new GridColumn { Width = "4680" }, new GridColumn { Width = "4680" }));
                for (int r = 0; r < 2; r++)
                    table.Append(new TableRow(
                        new TableCell(new Paragraph(new Run(new Text($"R{r}C0")))),
                        new TableCell(new Paragraph(new Run(new Text($"R{r}C1"))))));

                mainPart.Document = new Document(new Body(table));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] BuildGridSpanOvershootDocx()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                var table = new Table(new TableProperties(SingleBorders()),
                    new TableGrid(new GridColumn { Width = "4680" }, new GridColumn { Width = "4680" }));
                table.Append(new TableRow(
                    new TableCell(new TableCellProperties(new GridSpan { Val = 2 }),
                        new Paragraph(new Run(new Text("Spans")))),
                    new TableCell(new Paragraph(new Run(new Text("Overshoot"))))));

                mainPart.Document = new Document(new Body(table));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] BuildTallRowDocx(int lineCount)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                BorderType Bd<T>() where T : BorderType, new() =>
                    new T { Val = BorderValues.Single, Size = 4U, Color = "000000" };
                var borders = new TableBorders(
                    Bd<TopBorder>(), Bd<LeftBorder>(), Bd<BottomBorder>(), Bd<RightBorder>(),
                    Bd<InsideHorizontalBorder>(), Bd<InsideVerticalBorder>());

                var cell = new TableCell(new TableCellProperties());
                for (int i = 0; i < lineCount; i++)
                    cell.Append(new Paragraph(new Run(new Text($"Line {i} of a very tall single-row cell"))));

                var table = new Table(new TableProperties(borders), new TableRow(cell));
                mainPart.Document = new Document(new Body(table));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] BuildNestedTableDocx()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                BorderType Bd<T>() where T : BorderType, new() =>
                    new T { Val = BorderValues.Single, Size = 4U, Color = "000000" };
                TableBorders Borders() => new TableBorders(
                    Bd<TopBorder>(), Bd<LeftBorder>(), Bd<BottomBorder>(), Bd<RightBorder>(),
                    Bd<InsideHorizontalBorder>(), Bd<InsideVerticalBorder>());

                var nested = new Table(new TableProperties(Borders()),
                    new TableGrid(new GridColumn { Width = "9000" }));
                nested.Append(new TableRow(new TableCell(new Paragraph(new Run(new Text("Nested row 1"))))));
                nested.Append(new TableRow(new TableCell(new Paragraph(new Run(new Text("Nested row 2"))))));

                // A cell hosting a nested table still requires a trailing paragraph.
                var outerCell = new TableCell(nested, new Paragraph());
                var outer = new Table(new TableProperties(Borders()),
                    new TableGrid(new GridColumn { Width = "9360" }));
                outer.Append(new TableRow(outerCell));

                mainPart.Document = new Document(new Body(outer));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] BuildListDocx(
            (int ilvl, NumberFormatValues fmt, int start, string levelText)[] levels,
            (int ilvl, string text)[] items)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();

                var numberingPart = mainPart.AddNewPart<NumberingDefinitionsPart>();
                var abstractNum = new AbstractNum { AbstractNumberId = 0 };
                foreach (var (ilvl, fmt, start, levelText) in levels)
                {
                    abstractNum.Append(new Level(
                        new StartNumberingValue { Val = start },
                        new NumberingFormat { Val = fmt },
                        new LevelText { Val = levelText })
                    { LevelIndex = ilvl });
                }
                var instance = new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 };
                numberingPart.Numbering = new Numbering(abstractNum, instance);
                numberingPart.Numbering.Save();

                var body = new Body();
                foreach (var (ilvl, text) in items)
                {
                    body.Append(new Paragraph(
                        new ParagraphProperties(new NumberingProperties(
                            new NumberingLevelReference { Val = ilvl },
                            new NumberingId { Val = 1 })),
                        new Run(new Text(text))));
                }

                mainPart.Document = new Document(body);
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] BuildHangingIndentDocx(int hangingTwips)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new Document(new Body(
                    new Paragraph(
                        new ParagraphProperties(new Indentation
                        {
                            Left = "720",
                            Hanging = hangingTwips.ToString()
                        }),
                        new Run(new Text("Hanging")))));
                mainPart.Document.Save();
            }
            return ms.ToArray();
        }
    }
}
