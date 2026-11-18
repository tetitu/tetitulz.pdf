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
    /// SmartArt rendering, from the drawing Word persists rather than from the diagram's data model.
    ///
    /// <para>
    /// The body of a DOCX holds only a <c>dgm:relIds</c> pointing at the data / layout / style /
    /// colour parts; running that layout would mean implementing SmartArt itself. Word also writes
    /// a <see cref="DiagramPersistLayoutPart"/> (<c>word/diagrams/drawing1.xml</c>) containing the
    /// laid-out <c>dsp:spTree</c> — one shape per node with an <b>absolute</b> <c>a:xfrm</c> —
    /// precisely so a consumer that cannot lay out a diagram can still draw one.
    /// </para>
    ///
    /// <para>
    /// These fixtures therefore build a real persisted drawing part, and the assertions read the
    /// emitted shape matrices, so a renderer that stacked every node at the origin would fail.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class SmartArtRenderingTests
    {
        private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string DgmNs = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        private const string DspNs = "http://schemas.microsoft.com/office/drawing/2008/diagram";
        private const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

        // The body reference: a 4" × 2" inline diagram naming its four parts.
        private static string DiagramDrawing(string dmId, long cx = 3657600, long cy = 1828800)
            => $@"<w:drawing xmlns:w=""{WNs}"" xmlns:wp=""{WpNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}"" xmlns:dgm=""{DgmNs}"">
<wp:inline><wp:extent cx=""{cx}"" cy=""{cy}""/><wp:docPr id=""1"" name=""Diagram 1""/>
<a:graphic><a:graphicData uri=""{DiagramUri}"">
<dgm:relIds r:dm=""{dmId}"" r:lo=""rIdLo"" r:qs=""rIdQs"" r:cs=""rIdCs""/>
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        // One persisted node: an absolutely-placed rounded rectangle with a fill and a label.
        private static string PersistedNode(long x, long y, long cx, long cy, string fillHex, string label)
            => $@"<dsp:sp modelId=""{{{Guid.NewGuid()}}}"">
<dsp:spPr><a:xfrm><a:off x=""{x}"" y=""{y}""/><a:ext cx=""{cx}"" cy=""{cy}""/></a:xfrm>
<a:prstGeom prst=""roundRect""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""{fillHex}""/></a:solidFill></dsp:spPr>
<dsp:txBody><a:bodyPr/><a:lstStyle/>
<a:p><a:r><a:rPr lang=""en-US"" sz=""1200""/><a:t>{label}</a:t></a:r></a:p></dsp:txBody></dsp:sp>";

        private static string PersistedDrawing(string nodes, long groupCx = 3657600, long groupCy = 1828800)
            => $@"<dsp:drawing xmlns:dsp=""{DspNs}"" xmlns:a=""{ANs}""><dsp:spTree>
<dsp:nvGrpSpPr><dsp:cNvPr id=""0"" name=""""/><dsp:cNvGrpSpPr/></dsp:nvGrpSpPr>
<dsp:grpSpPr><a:xfrm><a:off x=""0"" y=""0""/><a:ext cx=""{groupCx}"" cy=""{groupCy}""/>
<a:chOff x=""0"" y=""0""/><a:chExt cx=""{groupCx}"" cy=""{groupCy}""/></a:xfrm></dsp:grpSpPr>
{nodes}</dsp:spTree></dsp:drawing>";

        private const string EmptyDataModel =
            @"<dgm:dataModel xmlns:dgm=""http://schemas.openxmlformats.org/drawingml/2006/diagram"">
<dgm:ptLst/><dgm:cxnLst/><dgm:bg/><dgm:whole/></dgm:dataModel>";

        /// <summary>
        /// Builds a DOCX whose body holds a SmartArt reference. When
        /// <paramref name="persistedDrawingXml"/> is null the persisted part is omitted — the case
        /// a non-Word producer leaves behind.
        /// </summary>
        private static byte[] SmartArtDocx(string persistedDrawingXml, long cx = 3657600, long cy = 1828800,
            long groupCx = 3657600, long groupCy = 1828800, bool trailingParagraph = false)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body());

                var dataPart = main.AddNewPart<DiagramDataPart>();
                string dmId = main.GetIdOfPart(dataPart);

                string dataXml = EmptyDataModel;
                if (persistedDrawingXml != null)
                {
                    // The persisted drawing hangs off the part that *contains the diagram
                    // reference* — the main document here — and the data model's
                    // dsp:dataModelExt names it by that part's relationship id. (The SDK enforces
                    // this: adding it to the data part is rejected outright.)
                    var drawingPart = main.AddNewPart<DiagramPersistLayoutPart>();
                    using (var ds = new MemoryStream(Encoding.UTF8.GetBytes(persistedDrawingXml)))
                        drawingPart.FeedData(ds);

                    string drawingRel = main.GetIdOfPart(drawingPart);
                    dataXml = dataXml.Replace("<dgm:whole/>",
                        $@"<dgm:whole/><dgm:extLst><a:ext xmlns:a=""{ANs}"" uri=""{{FF2B5EF4-FFF2-40B4-BE49-F238E27FC236}}"">
<dsp:dataModelExt xmlns:dsp=""{DspNs}"" relId=""{drawingRel}"" minVer=""12.0""/></a:ext></dgm:extLst>");
                }

                using (var s = new MemoryStream(Encoding.UTF8.GetBytes(dataXml)))
                    dataPart.FeedData(s);

                main.Document.Body.Append(new Paragraph(new Run(new Drawing(DiagramDrawing(dmId, cx, cy)))));
                if (trailingParagraph)
                    main.Document.Body.Append(new Paragraph(new Run(new Text("AFTER"))));
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static string ToPdfText(byte[] docx)
            => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false }));

        /// <summary>
        /// Converts while collecting <b>every</b> diagnostic, not just the last one. One drawing
        /// routinely reports more than once — here, "no persisted drawing" and then "the box was
        /// reserved" — and <c>LastError</c> keeps only the most recent, which is exactly why the
        /// feature has an event sink rather than a single slot.
        /// </summary>
        private static string ToPdfText(byte[] docx, out List<string> diagnostics)
        {
            var seen = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }

            DocxToPdfConverter.DiagnosticReported += Collect;
            try
            {
                string pdf = ToPdfText(docx);
                diagnostics = seen;
                return pdf;
            }
            finally
            {
                DocxToPdfConverter.DiagnosticReported -= Collect;
            }
        }

        private static List<double[]> ShapeCms(string pdf)
            => Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm")
                .Select(m => Enumerable.Range(1, 6)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .Where(v => !(v[0] == 1 && v[1] == 0 && v[2] == 0 && v[3] == -1))
                .ToList();

        private static List<double> TdYs(string pdf)
            => Regex.Matches(pdf, @"-?[\d.]+ (-?[\d.]+) Td")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

        // 914400 EMU = 1" = 72pt.
        private const long Inch = 914400;

        // ------------------------------------------------------------------ it renders

        [Fact]
        public void SmartArt_WithAPersistedDrawing_PaintsOneShapePerNode()
        {
            string nodes = PersistedNode(0, 0, Inch, Inch / 2, "FF0000", "N1")
                         + PersistedNode(Inch, 0, Inch, Inch / 2, "00FF00", "N2")
                         + PersistedNode(2 * Inch, 0, Inch, Inch / 2, "0000FF", "N3");
            string pdf = ToPdfText(SmartArtDocx(PersistedDrawing(nodes)));

            Assert.Contains("1.000 0.000 0.000 rg", pdf);
            Assert.Contains("0.000 1.000 0.000 rg", pdf);
            Assert.Contains("0.000 0.000 1.000 rg", pdf);
            Assert.Equal(3, ShapeCms(pdf).Count);
        }

        [Fact]
        public void SmartArt_DrawsEachNodesLabel()
        {
            string nodes = PersistedNode(0, 0, Inch, Inch / 2, "FF0000", "Alpha")
                         + PersistedNode(Inch, 0, Inch, Inch / 2, "00FF00", "Beta");
            string pdf = ToPdfText(SmartArtDocx(PersistedDrawing(nodes)));

            Assert.Contains("(Alpha) Tj", pdf);
            Assert.Contains("(Beta) Tj", pdf);
        }

        [Fact]
        public void SmartArt_NodesArePaintedAtTheirAbsoluteOffsets_NotStackedAtTheOrigin()
        {
            string nodes = PersistedNode(0, 0, Inch, Inch / 2, "FF0000", "A")
                         + PersistedNode(Inch, 0, Inch, Inch / 2, "00FF00", "B")
                         + PersistedNode(2 * Inch, 0, Inch, Inch / 2, "0000FF", "C");
            string pdf = ToPdfText(SmartArtDocx(PersistedDrawing(nodes)));

            var xs = ShapeCms(pdf).Select(v => v[4]).OrderBy(v => v).ToList();
            Assert.Equal(3, xs.Count);
            Assert.Equal(0.0, xs[0], 1);
            Assert.Equal(72.0, xs[1], 1);
            Assert.Equal(144.0, xs[2], 1);
        }

        [Fact]
        public void SmartArt_NodesAtDifferentRows_ArePaintedAtTheirVerticalOffsets()
        {
            string nodes = PersistedNode(0, 0, Inch, Inch / 2, "FF0000", "Top")
                         + PersistedNode(0, Inch, Inch, Inch / 2, "00FF00", "Bottom");
            string pdf = ToPdfText(SmartArtDocx(PersistedDrawing(nodes)));

            var ys = ShapeCms(pdf).Select(v => v[5]).OrderBy(v => v).ToList();
            Assert.Equal(2, ys.Count);
            Assert.Equal(0.0, ys[0], 1);
            Assert.Equal(72.0, ys[1], 1);   // y grows downward inside the drawing frame
        }

        [Fact]
        public void SmartArt_ScalesThePersistedDrawingOntoTheInlineExtent()
        {
            // The persisted drawing is 4" wide; the body reserves 2", so everything halves.
            string nodes = PersistedNode(2 * Inch, 0, Inch, Inch / 2, "FF0000", "R");
            string pdf = ToPdfText(SmartArtDocx(
                PersistedDrawing(nodes, groupCx: 4 * Inch, groupCy: 2 * Inch),
                cx: 2 * Inch, cy: Inch));

            var cm = Assert.Single(ShapeCms(pdf));
            Assert.Equal(0.5, cm[0], 3);     // the node is drawn at half scale
            Assert.Equal(72.0, cm[4], 1);    // 2" offset × 0.5 = 1" = 72pt
        }

        // ------------------------------------------------------------------ honest fallback

        [Fact]
        public void SmartArt_WithoutAPersistedDrawing_ReservesItsBoxAndReports()
        {
            string tall = ToPdfText(SmartArtDocx(null, cy: 2 * Inch, trailingParagraph: true), out var reports);
            string shortDiagram = ToPdfText(SmartArtDocx(null, cy: Inch, trailingParagraph: true));

            // Same document, differing only in the diagram's height: the following text must move
            // by exactly that difference.
            Assert.Equal(72.0, TdYs(shortDiagram).Last() - TdYs(tall).Last(), 1);

            // Two separate facts get reported — why it could not be drawn, and what was done about
            // it — so the assertion is against the whole set, not the last message.
            Assert.Contains(reports, m => m.Contains("persisted drawing part"));
            Assert.Contains(reports, m => m.Contains("space was reserved"));
        }

        [Fact]
        public void SmartArt_WithoutAPersistedDrawing_PaintsNothing()
        {
            string pdf = ToPdfText(SmartArtDocx(null));

            // No placeholder, no guessed layout — a box we cannot draw is left blank, not invented.
            Assert.Empty(ShapeCms(pdf));
        }

        [Fact]
        public void SmartArt_ShapeCountBeyondTheCap_IsBoundedAndReported()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 6000; i++)
                sb.Append(PersistedNode(0, 0, Inch, Inch / 2, "FF0000", "n"));
            string pdf = ToPdfText(SmartArtDocx(PersistedDrawing(sb.ToString())));

            Assert.True(ShapeCms(pdf).Count <= Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDrawingShapes,
                "the shape cap must bound what a crafted diagram can emit");
            Assert.Contains("shapes", DocxDiagnostics.LastError);
        }
    }
}

