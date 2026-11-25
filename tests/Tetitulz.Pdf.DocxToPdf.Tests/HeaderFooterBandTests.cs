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
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Which header and footer a page shows, and where a footer stands: a reference is inherited by
    /// type across sections and a type stands only for itself (<c>HeaderFooterReferences</c>, both
    /// paths); a reference naming no part is reported, not fatal; a footer of several lines rises
    /// from the footer distance; a drawing anchored to the page's or the margin's bottom inside a
    /// footer is on the page; the pages the endnotes add carry the last section's footer.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class HeaderFooterBandTests
    {
        private const string HeaderText = "HDRTEXT";

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ADanglingHeaderReference_IsReported_AndTheDocumentStillConverts(bool legacy)
        {
            byte[] docx = Docx((main, body) =>
            {
                body.Append(new Paragraph(new Run(new Text("BODY"))));
                body.Append(new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = "rIdMissing" }));
            });

            string text = TextOf(Convert(docx, legacy));

            Assert.Contains("BODY", text, StringComparison.Ordinal);
            Assert.Contains("rIdMissing", DocxToPdfConverter.LastDiagnostic ?? "", StringComparison.Ordinal);
        }

        /// <summary>
        /// ECMA-376 Part 1 §17.10.5: a section with no header reference takes the previous section's,
        /// which is how Word writes "same as previous". Two one-paragraph sections, the second
        /// naming no header: both pages show it.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHeaderReference_IsInheritedByASectionNamingNone(bool legacy)
        {
            byte[] docx = Docx((main, body) =>
            {
                string id = Header(main, HeaderText);
                body.Append(new Paragraph(
                    new ParagraphProperties(new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = id })),
                    new Run(new Text("S1"))));
                body.Append(new Paragraph(new Run(new Text("S2"))));
                body.Append(new SectionProperties());
            });

            string pdf = Convert(docx, legacy);

            Assert.Equal(2, PageCount(pdf));
            Assert.Equal(2, Occurrences(TextOf(pdf), HeaderText));
        }

        /// <summary>
        /// A <c>first</c> reference is the first page's alone: a titlePg section referencing only a
        /// first header shows it on its cover and nothing on the pages after, where the default band
        /// — none — applies. The first reference does not stand in for the default when the section
        /// names no default.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AFirstPageReference_IsNotTheDefaultPagesHeader(bool legacy)
        {
            byte[] docx = Docx((main, body) =>
            {
                string id = Header(main, HeaderText);
                for (int i = 0; i < 90; i++) body.Append(new Paragraph(new Run(new Text("Body line " + i))));
                body.Append(new SectionProperties(new HeaderReference { Type = HeaderFooterValues.First, Id = id }, new TitlePage()));
            });

            string pdf = Convert(docx, legacy);

            Assert.True(PageCount(pdf) >= 2, "the body should spill onto a second page");
            Assert.Equal(1, Occurrences(TextOf(pdf), HeaderText));
        }

        /// <summary>
        /// On the Builder path too, a titlePg section with only a default header shows none on its
        /// cover: the mapper hands the exporter an empty first-page band, since a null one falls back
        /// to the default band there. The control without titlePg shows it on every page.
        /// </summary>
        [Fact]
        public void TitlePage_WithNoFirstPageHeaderReference_LeavesTheCoverClean_ViaBuilder()
        {
            byte[] Build(bool titlePage) => Docx((main, body) =>
            {
                string id = Header(main, HeaderText);
                for (int i = 0; i < 90; i++) body.Append(new Paragraph(new Run(new Text("Body line " + i))));
                var sect = new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = id });
                if (titlePage) sect.Append(new TitlePage());
                body.Append(sect);
            });

            string withTitlePage = Convert(Build(true), legacy: false);
            string without = Convert(Build(false), legacy: false);

            int pages = PageCount(without);
            Assert.True(pages >= 2, "the body should spill onto a second page");
            Assert.Equal(pages, Occurrences(TextOf(without), HeaderText));
            Assert.Equal(pages - 1, Occurrences(TextOf(withTitlePage), HeaderText));
        }

        /// <summary>
        /// A footer grows upward from the footer distance (half an inch): five lines end with the last
        /// baseline at the distance and the first four above it, every one on the page. Laid out
        /// downward from a fixed baseline, the third and later lines fell below the page.
        /// </summary>
        [Fact]
        public void AFooterOfSeveralLines_RisesFromTheFooterDistance()
        {
            byte[] docx = Docx((main, body) =>
            {
                var fp = main.AddNewPart<FooterPart>();
                fp.Footer = new Footer(Enumerable.Range(1, 5).Select(i => (OpenXmlElement)new Paragraph(new Run(new Text("F" + i)))));
                body.Append(new Paragraph(new Run(new Text("BODY"))));
                body.Append(new SectionProperties(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(fp) }));
            });

            string pdf = Convert(docx, legacy: true);
            var lines = FooterLines(pdf);

            Assert.Equal(new[] { "F1", "F2", "F3", "F4", "F5" }, lines.Select(l => l.Text));
            Assert.All(lines, l => Assert.True(l.Y >= 0, $"{l.Text} at y {l.Y} is below the page"));
            Assert.InRange(lines[4].Y, 30, 42);                                  // the last baseline at the half-inch distance
            for (int i = 1; i < lines.Count; i++) Assert.True(lines[i - 1].Y > lines[i].Y, "the lines read downward");
        }

        /// <summary>
        /// A drawing anchored to the page's bottom inside a footer stands at the page's edge, and one
        /// anchored to the margin's bottom at the section's margin: the band is drawn with the
        /// context's margin set to a sentinel a million points down, which placed both there.
        /// </summary>
        [Theory]
        [InlineData(true, 0.0)]
        [InlineData(false, 72.0)]
        public void ABottomAnchoredDrawingInAFooter_StandsAtTheBottomItNames(bool relativeToPage, double expectedBottom)
        {
            var relativeTo = relativeToPage ? DW.VerticalRelativePositionValues.Page : DW.VerticalRelativePositionValues.Margin;
            byte[] png = DocxImageBuilder.MakeTruecolorPng(4, 4, 200, 50, 50);
            byte[] docx = Docx((main, body) =>
            {
                var fp = main.AddNewPart<FooterPart>();
                var image = fp.AddImagePart("image/png");
                using (var s = new MemoryStream(png)) image.FeedData(s);
                fp.Footer = new Footer(new Paragraph(new Run(BottomAnchoredDrawing(fp.GetIdOfPart(image), relativeTo, 100 * 12700, 50 * 12700))));
                body.Append(new Paragraph(new Run(new Text("BODY"))));
                body.Append(new SectionProperties(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(fp) }));
            });

            string pdf = Convert(docx, legacy: true);

            var placed = Regex.Match(pdf, @"(-?[\d.]+) 0 0 (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm\n/Im\d+ Do");
            Assert.True(placed.Success, "the footer's picture was not painted");
            Assert.InRange(double.Parse(placed.Groups[4].Value, CultureInfo.InvariantCulture), expectedBottom - 1, expectedBottom + 1);
        }

        /// <summary>
        /// The pages the endnotes add at the document's end are the last section's: each carries its
        /// footer. They were laid out after the section's pages had been decorated.
        /// </summary>
        [Fact]
        public void ThePagesTheEndnotesAdd_CarryTheLastSectionsFooter()
        {
            byte[] docx = Docx((main, body) =>
            {
                var fp = main.AddNewPart<FooterPart>();
                fp.Footer = new Footer(new Paragraph(new Run(new Text("FTR"))));
                var ep = main.AddNewPart<EndnotesPart>();
                ep.Endnotes = new Endnotes(new Endnote(new Paragraph(new Run(new Text(
                    string.Join(" ", Enumerable.Range(0, 4000).Select(i => "word" + i)))))) { Id = 1 });
                body.Append(new Paragraph(new Run(new Text("A")), new Run(new EndnoteReference { Id = 1 })));
                body.Append(new SectionProperties(new FooterReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(fp) }));
            });

            string pdf = Convert(docx, legacy: true);

            int pages = PageCount(pdf);
            Assert.True(pages >= 2, "the endnote should spill onto a second page");
            Assert.Equal(pages, Regex.Matches(pdf, Regex.Escape("(FTR) Tj")).Count);
        }

        // ---------------------------------------------------------------- helpers

        private static string Header(MainDocumentPart main, string text)
        {
            var hp = main.AddNewPart<HeaderPart>();
            hp.Header = new Header(new Paragraph(new Run(new Text(text))));
            return main.GetIdOfPart(hp);
        }

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

        private static int Occurrences(string text, string word) => Regex.Matches(text, Regex.Escape(word)).Count;

        /// <summary>
        /// The footer's lines with the baseline each is shown at: a text line's <c>Td</c>, plus the
        /// translation of the <c>q … cm … Q</c> block the band was risen in, when there is one.
        /// </summary>
        private static List<(string Text, double Y)> FooterLines(string pdf)
        {
            var lines = new List<(string, double)>();
            double shift = 0;
            foreach (Match m in Regex.Matches(pdf, @"1 0 0 1 (-?[\d.]+) (-?[\d.]+) cm\n|\nQ\n|BT\n(-?[\d.]+) (-?[\d.]+) Td\n(?:[^\n]*\n){0,3}?\((F\d)\) Tj"))
            {
                if (m.Value.StartsWith("1 0 0 1", StringComparison.Ordinal))
                    shift = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                else if (m.Value == "\nQ\n")
                    shift = 0;
                else
                    lines.Add((m.Groups[5].Value, double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) + shift));
            }
            return lines;
        }

        private static Drawing BottomAnchoredDrawing(string relId, DW.VerticalRelativePositionValues relativeTo, long cx, long cy)
        {
            return new Drawing(
                new DW.Anchor(
                    new DW.SimplePosition { X = 0L, Y = 0L },
                    new DW.HorizontalPosition(new DW.PositionOffset("0")) { RelativeFrom = DW.HorizontalRelativePositionValues.Column },
                    new DW.VerticalPosition(new DW.VerticalAlignment("bottom")) { RelativeFrom = relativeTo },
                    new DW.Extent { Cx = cx, Cy = cy },
                    new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                    new DW.WrapNone(),
                    new DW.DocProperties { Id = 1U, Name = "Anchored 1" },
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
    }
}
