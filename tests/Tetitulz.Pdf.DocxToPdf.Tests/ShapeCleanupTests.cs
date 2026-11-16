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
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The cleanup stage: custom geometry (<c>a:custGeom</c>), dash patterns, drawings inside table
    /// cells, and the two legacy <c>Last*</c> slots forwarding into the feature's diagnostics sink.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ShapeCleanupTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        // A 1" × 1" inline shape (914400 EMU square = 72 × 72 pt).
        private static string Inline(string payload)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""914400"" cy=""914400""/>
<wp:docPr id=""1"" name=""S1""/><a:graphic><a:graphicData uri=""{ShapeUri}"">{payload}
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        private static string Shape(string spPrInner)
            => $@"<wps:wsp><wps:spPr>{spPrInner}</wps:spPr></wps:wsp>";

        private static string Convert(byte[] docx)
            => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false }));

        private static string ConvertDrawing(string drawingXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Drawing(drawingXml)))));
                main.Document.Save();
            }
            return Convert(ms.ToArray());
        }

        /// <summary>
        /// Converts while collecting every diagnostic. One drawing commonly reports more than once
        /// — here, why a subpath was skipped and then what was drawn instead — and
        /// <c>LastError</c> keeps only the most recent.
        /// </summary>
        private static string ConvertDrawing(string drawingXml, out List<string> diagnostics)
        {
            var seen = new List<string>();
            void Collect(string m) => seen.Add(m);

            DocxToPdfConverter.DiagnosticReported += Collect;
            try
            {
                string pdf = ConvertDrawing(drawingXml);
                diagnostics = seen;
                return pdf;
            }
            finally
            {
                DocxToPdfConverter.DiagnosticReported -= Collect;
            }
        }

        private static int Count(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ------------------------------------------------------------------ custGeom

        private const string TriangleCustGeom = @"<a:custGeom><a:pathLst>
<a:path w=""100"" h=""100"">
<a:moveTo><a:pt x=""0"" y=""0""/></a:moveTo>
<a:lnTo><a:pt x=""100"" y=""0""/></a:lnTo>
<a:lnTo><a:pt x=""50"" y=""100""/></a:lnTo>
<a:close/></a:path></a:pathLst></a:custGeom>";

        [Fact]
        public void CustomGeometry_IsDrawnFromItsOwnCommands()
        {
            string pdf = ConvertDrawing(Inline(Shape(
                TriangleCustGeom + @"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>")));

            Assert.Equal(1, Count(pdf, " m\n"));
            Assert.Equal(2, Count(pdf, " l\n"));
            Assert.Contains("h\n", pdf);
            Assert.DoesNotContain(" re\n", pdf);   // not the bounding-box fallback
        }

        [Fact]
        public void CustomGeometry_ScalesItsOwnPathSpaceOntoTheShapeBox()
        {
            string pdf = ConvertDrawing(Inline(Shape(
                TriangleCustGeom + @"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>")));

            // Path space is 100×100, the shape is 72×72pt, so x=100 maps to 72.00.
            Assert.Contains("72.00 0.00 l", pdf);
            Assert.Contains("36.00 72.00 l", pdf);
        }

        [Fact]
        public void CustomGeometry_WithAnArc_SkipsThatSubpathAndReports()
        {
            string pdf = ConvertDrawing(Inline(Shape(
                @"<a:custGeom><a:pathLst><a:path w=""100"" h=""100"">
<a:moveTo><a:pt x=""0"" y=""0""/></a:moveTo>
<a:arcTo wR=""50"" hR=""50"" stAng=""0"" swAng=""5400000""/>
<a:close/></a:path></a:pathLst></a:custGeom>"
                + @"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>")), out var reports);

            // Drawing half a contour would close it across the gap and invent a shape. Two facts
            // are reported — why the subpath went, and what was drawn instead — so the assertion
            // is against the whole set rather than the last message.
            Assert.Contains(reports, m => m.Contains("arcTo"));
            Assert.Contains(" re\n", pdf);   // the honest bounding-box fallback
        }

        [Fact]
        public void PresetGeometry_StillWinsWhenThereIsNoCustomGeometry()
        {
            // The control: custGeom handling must not capture ordinary preset shapes.
            string pdf = ConvertDrawing(Inline(Shape(
                @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>")));

            Assert.Contains(" re\n", pdf);
        }

        // ------------------------------------------------------------------ dashes

        [Fact]
        public void DashedOutline_EmitsADashArrayScaledByTheLineWidth()
        {
            // prstDash "dash" is 4 on / 3 off in multiples of the width; w=25400 EMU = 2pt.
            string pdf = ConvertDrawing(Inline(Shape(
                @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:ln w=""25400""><a:solidFill><a:srgbClr val=""000000""/></a:solidFill>
<a:prstDash val=""dash""/></a:ln>")));

            Assert.Contains("[8.00 6.00] 0 d", pdf);
        }

        [Fact]
        public void DottedOutline_UsesADifferentPatternFromDashed()
        {
            string dotted = ConvertDrawing(Inline(Shape(
                @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:ln w=""12700""><a:solidFill><a:srgbClr val=""000000""/></a:solidFill>
<a:prstDash val=""dot""/></a:ln>")));

            Assert.Contains("[1.00 3.00] 0 d", dotted);
        }

        [Fact]
        public void SolidOutline_EmitsNoDashArray()
        {
            // The control: without a prstDash the stroke must stay solid.
            string pdf = ConvertDrawing(Inline(Shape(
                @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:ln w=""25400""><a:solidFill><a:srgbClr val=""000000""/></a:solidFill></a:ln>")));

            Assert.DoesNotContain("] 0 d", pdf);
        }

        [Fact]
        public void UnknownDashPreset_DrawsSolidAndReports()
        {
            string pdf = ConvertDrawing(Inline(Shape(
                @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:ln w=""25400""><a:solidFill><a:srgbClr val=""000000""/></a:solidFill>
<a:prstDash val=""zigzag""/></a:ln>")));

            Assert.DoesNotContain("] 0 d", pdf);
            Assert.Contains("zigzag", DocxDiagnostics.LastError);
        }

        // ------------------------------------------------------------------ drawings in cells

        private static byte[] TableWithDrawingInCell(string drawingXml, bool includeDrawing = true)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var cellParagraph = includeDrawing
                    ? new Paragraph(new Run(new Drawing(drawingXml)))
                    : new Paragraph(new Run(new Text("x")));

                var table = new Table(
                    new TableProperties(new TableBorders(
                        new TopBorder { Val = BorderValues.Single, Size = 4 },
                        new BottomBorder { Val = BorderValues.Single, Size = 4 },
                        new LeftBorder { Val = BorderValues.Single, Size = 4 },
                        new RightBorder { Val = BorderValues.Single, Size = 4 })),
                    new TableRow(new TableCell(cellParagraph)));

                main.Document = new Document(new Body(table, new Paragraph(new Run(new Text("AFTER")))));
                main.Document.Save();
            }
            return ms.ToArray();
        }

        [Fact]
        public void ShapeInsideATableCell_IsPainted()
        {
            string pdf = Convert(TableWithDrawingInCell(Inline(Shape(
                @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>"))));

            Assert.Contains("1.000 0.000 0.000 rg", pdf);
        }

        [Fact]
        public void DrawingInsideATableCell_GivesTheCellItsHeight()
        {
            string withDrawing = Convert(TableWithDrawingInCell(Inline(Shape(
                @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>"))));
            string withoutDrawing = Convert(TableWithDrawingInCell(null, includeDrawing: false));

            // The trailing paragraph must sit lower when the cell holds a 72pt-tall drawing;
            // a cell that ignored the drawing would leave the row at one text line.
            double after = TrailingTextY(withDrawing);
            double baseline = TrailingTextY(withoutDrawing);
            Assert.True(baseline - after > 50,
                $"expected the drawing to grow the row: {baseline} → {after}");
        }

        private static double TrailingTextY(string pdf)
        {
            var ys = Regex.Matches(pdf, @"-?[\d.]+ (-?[\d.]+) Td")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();
            Assert.NotEmpty(ys);
            return ys.Last();
        }

        // ------------------------------------------------------------------ the legacy slots

        [Fact]
        public void TableRendererLastError_AlsoReachesTheDiagnosticsSink()
        {
            var seen = new List<string>();
            void Collect(string m) => seen.Add(m);

            DocxToPdfConverter.DiagnosticReported += Collect;
            try
            {
                // Nest tables past the depth guard so TableRenderer drops content and records it.
                Table nested = new Table(new TableRow(new TableCell(new Paragraph(new Run(new Text("deep"))))));
                for (int i = 0; i < 30; i++)
                    nested = new Table(new TableRow(new TableCell(nested, new Paragraph())));

                using var ms = new MemoryStream();
                using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                {
                    var main = doc.AddMainDocumentPart();
                    main.Document = new Document(new Body(nested, new Paragraph()));
                    main.Document.Save();
                }
                Convert(ms.ToArray());
            }
            finally
            {
                DocxToPdfConverter.DiagnosticReported -= Collect;
            }

            Assert.NotNull(TableRenderer.LastError);          // the legacy slot still works
            Assert.Contains(seen, m => m.Contains("TableRenderer"));   // and it reaches the sink
        }
    }
}

