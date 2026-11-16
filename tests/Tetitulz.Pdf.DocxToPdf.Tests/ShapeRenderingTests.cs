// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// DrawingML preset shapes (wps:wsp) — rectangle / ellipse / line with solid fill and
    /// outline — are now painted as vector graphics instead of being dropped. Shapes are
    /// located by element name, so the tests build them from raw DrawingML XML.
    /// </summary>
    public class ShapeRenderingTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        // An inline shape (914400×457200 EMU = 1"×0.5") with the given preset + spPr content.
        private static string InlineShape(string prst, string spPrInner)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""914400"" cy=""457200""/>
<wp:docPr id=""1"" name=""S1""/><a:graphic><a:graphicData
uri=""http://schemas.microsoft.com/office/word/2010/wordprocessingShape""><wps:wsp><wps:spPr>
<a:prstGeom prst=""{prst}""><a:avLst/></a:prstGeom>{spPrInner}</wps:spPr></wps:wsp>
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        private static byte[] Convert(string drawingXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Drawing(drawingXml)))));
                main.Document.Save();
            }
            return DocxToPdfConverter.ConvertToBytes(ms.ToArray(), new DocxToPdfOptions { CompressStreams = false });
        }

        [Fact]
        public void RectangleShape_WithSolidFill_IsPaintedAsFilledRect()
        {
            string xml = InlineShape("rect", @"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>");
            string content = Encoding.Latin1.GetString(Convert(xml));

            Assert.Contains("1.000 0.000 0.000 rg", content); // red fill colour
            Assert.Contains("re\nf\n", content);              // a filled rectangle (no stroke → 'f')
        }

        // Uses "rect", not "roundRect": the claim in this name is about the paint operator, and a
        // rounded rectangle is no longer emitted as a bare `re`. RoundRect has its own test in
        // ShapeGeometryTests, which pins the corner curves.
        [Fact]
        public void RectangleShape_WithFillAndOutline_IsFilledAndStroked()
        {
            string xml = InlineShape("rect",
                @"<a:solidFill><a:srgbClr val=""00FF00""/></a:solidFill>" +
                @"<a:ln w=""25400""><a:solidFill><a:srgbClr val=""000080""/></a:solidFill></a:ln>");
            string content = Encoding.Latin1.GetString(Convert(xml));

            Assert.Contains("0.000 1.000 0.000 rg", content);   // green fill
            Assert.Contains("0.000 0.000 0.502 RG", content);   // navy stroke (0x80/255≈0.502)
            Assert.Contains("re\nB\n", content);                // fill + stroke → 'B'
        }

        [Fact]
        public void EllipseShape_WithOutlineOnly_IsStrokedWithBeziers()
        {
            string xml = InlineShape("ellipse",
                @"<a:noFill/><a:ln w=""12700""><a:solidFill><a:srgbClr val=""0000FF""/></a:solidFill></a:ln>");
            string content = Encoding.Latin1.GetString(Convert(xml));

            Assert.Contains("0.000 0.000 1.000 RG", content); // blue stroke
            Assert.Contains(" c\n", content);                 // bezier arcs (ellipse, not a rect)
            Assert.Contains("S\n", content);                  // stroked (no fill)
        }

        [Fact]
        public void LineShape_IsStrokedAcrossItsBox()
        {
            string xml = InlineShape("line",
                @"<a:ln w=""19050""><a:solidFill><a:srgbClr val=""404040""/></a:solidFill></a:ln>");
            string content = Encoding.Latin1.GetString(Convert(xml));

            Assert.Contains("0.251 0.251 0.251 RG", content); // grey stroke (0x40/255≈0.251)
            Assert.Contains(" l\nS\n", content);              // a stroked line segment
        }
    }
}
