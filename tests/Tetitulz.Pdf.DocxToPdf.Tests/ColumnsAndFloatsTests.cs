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
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using Tetitulz.Pdf.DocxToPdf;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Multi-column flow (w:sectPr/w:cols) and floating / text-wrapped images (wp:anchor)
    /// in the legacy renderer. Assertions read the uncompressed content stream: text lines
    /// are emitted as "{x} {y} Td", so the x positions reveal which column / float-relative
    /// band each line landed in. (Default page = 612×792, margins 72 → content box 468 wide.)
    /// </summary>
    public class ColumnsAndFloatsTests
    {
        private static string ToPdfText(byte[] docx)
            => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false }));

        // The left x of every emitted text line.
        private static List<double> TdXs(string pdf)
        {
            var xs = new List<double>();
            foreach (Match m in Regex.Matches(pdf, @"(-?[0-9]+\.[0-9]+) -?[0-9]+\.[0-9]+ Td"))
                xs.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return xs;
        }

        // One /MediaBox is written per page dictionary (the pages tree has none).
        private static int PageCount(string pdf) => Regex.Matches(pdf, @"/MediaBox").Count;

        // ---------------------------------------------------------------- columns

        private static byte[] ColumnsDocx(int paragraphCount, int columns, bool columnBreakAfterFirst = false)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                for (int i = 0; i < paragraphCount; i++)
                {
                    body.Append(new Paragraph(new Run(
                        new Text("Paragraph " + i + " with several words to help fill the column nicely.")
                        { Space = SpaceProcessingModeValues.Preserve })));
                    if (columnBreakAfterFirst && i == 0)
                        body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Column })));
                }
                var sectPr = new SectionProperties();
                if (columns > 1)
                    sectPr.Append(new Columns { ColumnCount = (short)columns, Space = "720" });
                body.Append(sectPr);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        [Fact]
        public void TwoColumns_FlowIntoTwoXBands()
        {
            var xs = TdXs(ToPdfText(ColumnsDocx(120, 2)));
            Assert.Contains(xs, x => x < 100);   // left column (~72)
            Assert.Contains(xs, x => x > 300);   // right column (~324)
        }

        [Fact]
        public void OneColumn_StaysInTheLeftBand()
        {
            var xs = TdXs(ToPdfText(ColumnsDocx(120, 1)));
            Assert.Contains(xs, x => x < 100);
            Assert.DoesNotContain(xs, x => x > 300); // no second column
        }

        [Fact]
        public void TwoColumns_UseNoMorePagesThanOneColumn()
        {
            int two = PageCount(ToPdfText(ColumnsDocx(120, 2)));
            int one = PageCount(ToPdfText(ColumnsDocx(120, 1)));
            Assert.True(two >= 1 && one >= 1);
            Assert.True(two <= one, $"2-column pages ({two}) should be <= 1-column pages ({one})");
        }

        [Fact]
        public void ColumnBreak_JumpsToSecondColumnSamePage()
        {
            string pdf = ToPdfText(ColumnsDocx(3, 2, columnBreakAfterFirst: true));
            // The explicit w:br/@type=column forces the following text into the right column
            // (~324) even though the left column is nowhere near full, all on page 1.
            Assert.Contains(TdXs(pdf), x => x > 300);
            Assert.Equal(1, PageCount(pdf));
        }

        // ---------------------------------------------------------------- floats

        private static byte[] FloatDocx(byte[] png, int words = 200, Paragraph paragraph = null, bool floating = true)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var imagePart = main.AddImagePart("image/png");
                using (var s = new MemoryStream(png)) imagePart.FeedData(s);
                string relId = main.GetIdOfPart(imagePart);

                long emu = 100 * 12700; // 100pt square float at the column's top-left
                var sb = new StringBuilder();
                for (int i = 0; i < words; i++) sb.Append("word").Append(i).Append(' ');

                var body = new Body(
                    new Paragraph(new Run(floating ? BuildAnchorDrawing(relId, emu, emu) : (OpenXmlElement)new Text(""))),
                    paragraph ?? new Paragraph(new Run(new Text(sb.ToString().Trim()) { Space = SpaceProcessingModeValues.Preserve })),
                    new SectionProperties());
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static Drawing BuildAnchorDrawing(string relId, long cx, long cy)
        {
            return new Drawing(
                new DW.Anchor(
                    new DW.SimplePosition { X = 0L, Y = 0L },
                    new DW.HorizontalPosition(new DW.PositionOffset("0"))
                    { RelativeFrom = DW.HorizontalRelativePositionValues.Column },
                    new DW.VerticalPosition(new DW.PositionOffset("0"))
                    { RelativeFrom = DW.VerticalRelativePositionValues.Paragraph },
                    new DW.Extent { Cx = cx, Cy = cy },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.WrapSquare { WrapText = DW.WrapTextValues.BothSides },
                    new DW.DocProperties { Id = 1U, Name = "Float 1" },
                    new A.Graphic(new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = 0U, Name = "img.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(new A.Blip { Embed = relId }, new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = cx, Cy = cy }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
                {
                    DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U,
                    SimplePos = false, RelativeHeight = 0U, BehindDoc = false, Locked = false,
                    LayoutInCell = true, AllowOverlap = true
                });
        }

        [Fact]
        public void FloatingImage_WrapSquare_ShiftsTextRightThenBackToFullWidth()
        {
            byte[] png = DocxImageBuilder.MakeTruecolorPng(8, 8, 200, 50, 50);
            string pdf = ToPdfText(FloatDocx(png));

            Assert.Matches(@"/Im\d+ Do", pdf);              // the float is painted

            var xs = TdXs(pdf);
            Assert.Contains(xs, x => x > 150);              // lines beside the 100pt left float (~172)
            Assert.Contains(xs, x => x < 100);              // lines below it return to the margin (~72)
        }

        /// <summary>
        /// A paragraph indented past the column, beside a float, is laid out at once in the room its indent
        /// leaves: the line is short because of the indent, which no height clears, so descending until the
        /// float cleared broke pages until the blocked-line count ran out — some 220 of them. The control is
        /// the same paragraph with no float, which the batch path lays out on one page.
        /// </summary>
        [Fact]
        public void AParagraphIndentedPastTheColumn_BesideAFloat_StaysOnOnePage()
        {
            byte[] png = DocxImageBuilder.MakeTruecolorPng(8, 8, 200, 50, 50);
            static Paragraph Indented() => new Paragraph(
                new ParagraphProperties(new Indentation { Left = "9200" }),   // 460 pt of a 468 pt column
                new Run(new Text("x")));
            byte[] withFloat = FloatDocx(png, paragraph: Indented());
            byte[] without = FloatDocx(png, paragraph: Indented(), floating: false);

            Assert.Equal(1, PageCount(ToPdfText(without)));
            Assert.Equal(1, PageCount(ToPdfText(withFloat)));
            Assert.Contains("x", ExtractedLines(DocxToPdfConverter.ConvertToBytes(withFloat)));
        }

        /// <summary>
        /// Line breaks in a paragraph beside a float are kept. The float-aware loop wrapped the rest of the
        /// paragraph again for each line, from the pieces the last wrap made, and a line break is consumed by
        /// the wrap — so every break after the first line was lost and "two" and "three" ran together (found
        /// with Wave 22, DocxToPdf-3:3).
        /// </summary>
        [Fact]
        public void FloatingImage_LineBreaksInTheParagraphBesideIt_AreKept()
        {
            byte[] png = DocxImageBuilder.MakeTruecolorPng(8, 8, 200, 50, 50);
            byte[] docx = FloatDocx(png, paragraph: new Paragraph(new Run(
                new Text("one"), new Break(), new Text("two"), new Break(), new Text("three"))));

            string[] lines = ExtractedLines(DocxToPdfConverter.ConvertToBytes(docx));

            Assert.Contains("one", lines);
            Assert.Contains("two", lines);
            Assert.Contains("three", lines);
        }

        /// <summary>
        /// A long paragraph beside a float is wrapped once for each width the float gives it, not once a line:
        /// each line wrapped all the remaining text again, quadratic in the paragraph's lines (Wave 22,
        /// DocxToPdf-3:3). The conversion's allocation on this thread is compared with the same paragraph and
        /// no float.
        /// </summary>
        [Fact]
        public void FloatingImage_ALongParagraphBesideIt_IsWrappedOncePerWidth()
        {
            byte[] png = DocxImageBuilder.MakeTruecolorPng(8, 8, 200, 50, 50);
            const int Words = 8000;
            byte[] withFloat = FloatDocx(png, Words);
            byte[] without = FloatDocx(png, Words, floating: false);

            long Allocated(byte[] docx)
            {
                long before = System.GC.GetAllocatedBytesForCurrentThread();
                DocxToPdfConverter.ConvertToBytes(docx);
                return System.GC.GetAllocatedBytesForCurrentThread() - before;
            }

            long plain = Allocated(without);
            long floated = Allocated(withFloat);

            Assert.True(floated < plain * 2 + 8L * 1024 * 1024, $"beside a float {floated} bytes, without {plain}");
        }

        private static string[] ExtractedLines(byte[] pdf)
        {
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
            return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc)
                .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).ToArray();
        }

        [Fact]
        public void NoFloat_TextStartsAtTheLeftMargin()
        {
            // Sanity: without a float the same text never shifts right (batch path unchanged).
            byte[] docx = ColumnsDocx(5, 1);
            Assert.DoesNotContain(TdXs(ToPdfText(docx)), x => x > 150);
        }
    }
}
