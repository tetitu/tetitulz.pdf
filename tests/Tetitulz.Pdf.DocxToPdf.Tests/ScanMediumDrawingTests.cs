// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Drawing defects from the 2026-08 assembly scan (MEDIUM band): the shape/SmartArt arms that
    /// returned silently, and the preset classifier that made a filled shape unfillable.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ScanMediumDrawingTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        private static string Inline(string payload, long cx, long cy)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""{cx}"" cy=""{cy}""/>
<wp:docPr id=""1"" name=""S1""/><a:graphic><a:graphicData uri=""{ShapeUri}"">{payload}
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        private static string Shape(string prst, string fillHex)
            => $@"<wps:wsp><wps:spPr><a:prstGeom prst=""{prst}""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""{fillHex}""/></a:solidFill></wps:spPr></wps:wsp>";

        private static string Convert(string drawingXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Drawing(drawingXml))),
                    new Paragraph(new Run(new Text("AFTER")))));
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
                new DocxToPdfOptions { CompressStreams = false }));
        }

        // ================================================================== DrawingRenderer:246

        [Fact]
        public void ShapeWithNoUsableExtent_IsReported_NotDroppedSilently()
        {
            // A wps:wsp whose wp:extent states cx="0": TryReadExtent fails, and RenderShapeTree
            // bailed with a bare `return` — the shape, and any text box content it carried, left
            // the PDF with LastDiagnostic still null. The same drawing under an UNRECOGNISED
            // graphicData uri was reported, so whether the loss was visible depended on the uri
            // rather than on what was lost.
            string pdf = Convert(Inline(Shape("rect", "FF0000"), cx: 0, cy: 914400));

            Assert.Contains("(AFTER)", pdf);   // the rest of the document still converts
            Assert.Contains("no usable wp:extent", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        [Fact]
        public void ShapeWithNoGraphicData_IsReported_NotDroppedSilently()
        {
            // The second bare return in the same method: a readable extent, but no a:graphicData.
            //
            // Reaching it takes care. Render dispatches on a:graphicData/@uri, so an absent
            // graphicData also means an absent uri — the ShapeUri arm cannot get here. The route in
            // is the `default` arm's STRUCTURAL test: an a:prstGeom anywhere in the container sends
            // the drawing to RenderShapeTree even though no graphicData exists for it to find. A
            // first version of this test used a bare <a:graphic/>, which never left the default
            // arm's own Reserve — and the mutation survived, which is how the gap was found.
            string drawing =
                $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""914400"" cy=""914400""/>
<wp:docPr id=""1"" name=""S1""/><a:graphic><wps:wsp><wps:spPr>
<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""00FF00""/></a:solidFill>
</wps:spPr></wps:wsp></a:graphic></wp:inline></w:drawing>";

            string pdf = Convert(drawing);
            Assert.Contains("(AFTER)", pdf);
            Assert.Contains("shape or group is not rendered", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        // ================================================================== PresetGeometry:192

        /// <summary>True when the content fills a path in the given RGB operator's colour.</summary>
        private static bool FillsWith(string pdf, string rgbOperator)
            => Regex.IsMatch(pdf, Regex.Escape(rgbOperator) + @"[\s\S]{0,4000}?\bf\b");

        [Fact]
        public void FlowChartConnector_IsFilled_NotClassifiedStrokeOnlyByItsName()
        {
            // flowChartConnector is registered in this very file's preset table as an Ellipse — an
            // AREA shape — yet the substring test "contains Connector" classified it as a line, so
            // hasFill was forced false and Word's solid coloured circle came out a hollow outline.
            string connector = Convert(Inline(Shape("flowChartConnector", "FF0000"), 914400, 914400));
            Assert.True(FillsWith(connector, "1.000 0.000 0.000 rg"),
                "flowChartConnector was not filled");

            // The control: a preset that really IS a line must still never be filled, so the fix
            // cannot be satisfied by dropping the stroke-only concept altogether.
            string line = Convert(Inline(Shape("straightConnector1", "FF0000"), 914400, 914400));
            Assert.False(FillsWith(line, "1.000 0.000 0.000 rg"),
                "straightConnector1 was filled, but it is a line");
        }
    }
}
