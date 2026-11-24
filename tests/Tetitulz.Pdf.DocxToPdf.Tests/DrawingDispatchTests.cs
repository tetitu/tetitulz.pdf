// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The <c>a:graphicData/@uri</c> dispatch and the reserve-and-report fallback.
    ///
    /// <para>
    /// A SmartArt diagram or a chart carries neither an <c>a:blip</c> nor a <c>prstGeom</c>.
    /// Dropped silently, it would <b>consume no vertical space</b> — every later page paginating
    /// as though the drawing were absent. These tests pin what happens instead: the drawing's
    /// declared <c>wp:extent</c> box is reserved, and the loss is reported. Nothing is painted in
    /// its place.
    /// </para>
    ///
    /// <para>
    /// Height assertions compare <b>two conversions of the same document that differ only in the
    /// drawing's <c>cy</c></b>, so they measure the reserve alone and are independent of page
    /// margins, paragraph spacing and line height.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class DrawingDispatchTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" " +
            "xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        private const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        private const string ChartExUri = "http://schemas.microsoft.com/office/drawing/2014/chartex";
        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        // Inline drawing of the given kind. The payload deliberately contains no a:blip and no
        // a:prstGeom — that is exactly what makes a diagram / chart invisible to the structural
        // heuristics, and what used to make it vanish.
        private static string InlineGraphic(string uri, string payload, long cy)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""2743200"" cy=""{cy}""/>
<wp:docPr id=""1"" name=""G1""/><a:graphic><a:graphicData uri=""{uri}"">{payload}
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        private const string DiagramPayload =
            @"<dgm:relIds r:dm=""rId10"" r:lo=""rId11"" r:qs=""rId12"" r:cs=""rId13""/>";
        private const string ChartPayload = @"<c:chart r:id=""rId10""/>";

        // The drawing paragraph, then a text paragraph whose baseline reveals where the flow
        // cursor ended up.
        private static byte[] DocxWithDrawing(string drawingXml)
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
            return ms.ToArray();
        }

        private static string ToPdfText(byte[] docx)
            => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false }));

        private static List<double> TdYs(string pdf)
        {
            var ys = new List<double>();
            foreach (Match m in Regex.Matches(pdf, @"-?[0-9]+\.[0-9]+ (-?[0-9]+\.[0-9]+) Td"))
                ys.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return ys;
        }

        private static List<double> TdXs(string pdf)
        {
            var xs = new List<double>();
            foreach (Match m in Regex.Matches(pdf, @"(-?[0-9]+\.[0-9]+) -?[0-9]+\.[0-9]+ Td"))
                xs.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            return xs;
        }

        // The y of the "AFTER" line for a drawing of height `cy` EMU. Converting the *same*
        // document at two heights isolates the reserved space from everything else on the page.
        private static double AfterLineY(string uri, string payload, long cy)
        {
            string pdf = ToPdfText(DocxWithDrawing(InlineGraphic(uri, payload, cy)));
            var ys = TdYs(pdf);
            Assert.NotEmpty(ys);          // the fixture must reach the text pass at all
            return ys[ys.Count - 1];
        }

        // 914400 EMU = 1" = 72pt.  Two heights one inch apart ⇒ the baselines must differ by 72pt.
        private const long OneInch = 914400L;
        private const long TwoInches = 1828800L;

        [Fact]
        public void SmartArtDrawing_ReservesItsExtentHeight_SoLaterContentMovesDown()
        {
            double yShort = AfterLineY(DiagramUri, DiagramPayload, OneInch);
            double yTall = AfterLineY(DiagramUri, DiagramPayload, TwoInches);

            // One extra inch of diagram ⇒ the following text sits exactly one inch lower.
            Assert.Equal(72.0, yShort - yTall, 1);
        }

        [Fact]
        public void SmartArtDrawing_ReportsThatItWasNotRendered()
        {
            ToPdfText(DocxWithDrawing(InlineGraphic(DiagramUri, DiagramPayload, TwoInches)));

            Assert.Contains("SmartArt", DocxDiagnostics.LastError);
            Assert.Contains("144.0pt", DocxDiagnostics.LastError);
        }

        [Fact]
        public void ChartDrawing_ReservesItsExtentHeight_SoLaterContentMovesDown()
        {
            double yShort = AfterLineY(ChartUri, ChartPayload, OneInch);
            double yTall = AfterLineY(ChartUri, ChartPayload, TwoInches);

            Assert.Equal(72.0, yShort - yTall, 1);
        }

        [Fact]
        public void ChartDrawing_ReportsThatItWasNotRendered()
        {
            ToPdfText(DocxWithDrawing(InlineGraphic(ChartUri, ChartPayload, OneInch)));

            Assert.Contains("chart", DocxDiagnostics.LastError);
        }

        [Fact]
        public void ExtendedChartDrawing_ReservesItsExtentHeightAndReports()
        {
            double yShort = AfterLineY(ChartExUri, ChartPayload, OneInch);
            double yTall = AfterLineY(ChartExUri, ChartPayload, TwoInches);

            Assert.Equal(72.0, yShort - yTall, 1);
            Assert.Contains("extended chart", DocxDiagnostics.LastError);
        }

        [Fact]
        public void DrawingWithUnrecognisedUriAndNoGraphicContent_ReservesItsExtentAndReports()
        {
            const string bogus = "urn:example:not-a-drawingml-kind";
            double yShort = AfterLineY(bogus, "<a:none/>", OneInch);
            double yTall = AfterLineY(bogus, "<a:none/>", TwoInches);

            Assert.Equal(72.0, yShort - yTall, 1);
            Assert.Contains(bogus, DocxDiagnostics.LastError);
        }

        [Fact]
        public void DrawingWithUnrecognisedUriButAPresetGeometry_IsStillPaintedAsAShape()
        {
            // Producers that do not write a standard graphicData uri must keep rendering: the
            // structural heuristic stays as the dispatch's default arm, ahead of the fallback.
            string payload =
                @"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>" +
                @"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill></wps:spPr></wps:wsp>";
            string pdf = ToPdfText(DocxWithDrawing(
                InlineGraphic("urn:example:unknown", payload, OneInch)));

            Assert.Contains("1.000 0.000 0.000 rg", pdf);   // the shape is painted, not reserved
            Assert.Contains("re\nf\n", pdf);
        }

        // ------------------------------------------------------------------ anchored shapes

        private static string AnchoredShape(string fillHex)
            => $@"<w:drawing {Ns}><wp:anchor distT=""0"" distB=""0"" distL=""0"" distR=""0""
simplePos=""0"" relativeHeight=""1"" behindDoc=""0"" locked=""0"" layoutInCell=""1"" allowOverlap=""1"">
<wp:simplePos x=""0"" y=""0""/>
<wp:positionH relativeFrom=""column""><wp:posOffset>0</wp:posOffset></wp:positionH>
<wp:positionV relativeFrom=""paragraph""><wp:posOffset>0</wp:posOffset></wp:positionV>
<wp:extent cx=""1270000"" cy=""1270000""/><wp:wrapSquare wrapText=""bothSides""/>
<wp:docPr id=""2"" name=""S2""/><a:graphic><a:graphicData uri=""{ShapeUri}""><wps:wsp><wps:spPr>
<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""{fillHex}""/></a:solidFill></wps:spPr></wps:wsp>
</a:graphicData></a:graphic></wp:anchor></w:drawing>";

        [Fact]
        public void AnchoredShape_RegistersAnExclusionRect_SoTextWrapsBesideItThenReturns()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                // The anchor rides on the first paragraph; the rest of the text flows past it.
                body.Append(new Paragraph(
                    new Run(new Drawing(AnchoredShape("FF0000"))),
                    new Run(new Text("Text beside the floating shape. ")
                    { Space = SpaceProcessingModeValues.Preserve })));
                for (int i = 0; i < 30; i++)
                    body.Append(new Paragraph(new Run(
                        new Text("More body text that must flow around and then below the shape. ")
                        { Space = SpaceProcessingModeValues.Preserve })));
                main.Document = new Document(body);
                main.Document.Save();
            }

            string pdf = ToPdfText(ms.ToArray());
            var xs = TdXs(pdf);

            Assert.Contains("1.000 0.000 0.000 rg", pdf);   // the shape itself is painted
            // The shape occupies x 72..172 (100pt at the column left), so lines level with it
            // start to its right, and lines below it return to the margin.
            Assert.Contains(xs, x => x > 150);
            Assert.Contains(xs, x => x < 100);
        }

        // ------------------------------------------------------------------ the sink itself

        [Fact]
        public void Diagnostic_IsClearedAtTheStartOfEachConversion()
        {
            ToPdfText(DocxWithDrawing(InlineGraphic(ChartUri, ChartPayload, OneInch)));
            Assert.NotNull(DocxDiagnostics.LastError);

            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("plain")))));
                main.Document.Save();
            }
            ToPdfText(ms.ToArray());

            // A clean conversion must not inherit the previous document's message.
            Assert.Null(DocxDiagnostics.LastError);
        }

        [Fact]
        public void Diagnostic_LastError_IsThreadStatic()
        {
            DocxDiagnostics.Clear();
            DocxDiagnostics.Report("DrawingDispatchTests", "set on the main thread");
            Assert.NotNull(DocxDiagnostics.LastError);

            string observedOnOtherThread = "not-run";
            var t = new Thread(() => observedOnOtherThread = DocxDiagnostics.LastError);
            t.Start();
            t.Join();

            Assert.Null(observedOnOtherThread);
            Assert.NotNull(DocxDiagnostics.LastError);   // and this thread still sees its own
            DocxDiagnostics.Clear();
        }

        [Fact]
        public void Diagnostic_OnEvent_ReceivesEveryReport_AndAThrowingHandlerIsContained()
        {
            var seen = new List<string>();
            void Bad(string _) => throw new InvalidDataException("handler blew up");
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Good(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }

            DocxDiagnostics.OnEvent += Bad;
            DocxDiagnostics.OnEvent += Good;
            try
            {
                // A throwing subscriber must not escape into the conversion.
                ToPdfText(DocxWithDrawing(InlineGraphic(ChartUri, ChartPayload, OneInch)));
            }
            finally
            {
                DocxDiagnostics.OnEvent -= Bad;
                DocxDiagnostics.OnEvent -= Good;
            }

            // Good receives it even though Bad threw first.
            //
            // `handler(message)` invokes the whole multicast list in one call, so the first
            // subscriber to throw would abort the walk; a try/catch around that call contains the
            // exception without fixing the delivery. Core.Util.DiagnosticFanOut owns the rule for
            // every sink.
            Assert.NotEmpty(seen);
            Assert.Contains(seen, m => m.Contains("chart"));
            Assert.Contains("chart", DocxDiagnostics.LastError);
        }
    }
}

