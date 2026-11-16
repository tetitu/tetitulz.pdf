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
    /// <c>a:gradFill</c> painted as a real PDF shading.
    ///
    /// <para>
    /// Gradients are the default for several stock SmartArt styles and every "Intense Effect" shape
    /// style. Substituting a solid colour would be a <i>confidently wrong</i> result rather than an
    /// obviously missing one, so these assert on the emitted shading dictionary — a solid-colour
    /// stand-in would fail every one of them.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ShapeGradientTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        private static string Inline(string payload)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""1828800"" cy=""914400""/>
<wp:docPr id=""1"" name=""S1""/><a:graphic><a:graphicData uri=""{ShapeUri}"">{payload}
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        private static string GradientShape(string gsLst, string geometry, string prst = "rect")
            => $@"<wps:wsp><wps:spPr><a:prstGeom prst=""{prst}""><a:avLst/></a:prstGeom>
<a:gradFill><a:gsLst>{gsLst}</a:gsLst>{geometry}</a:gradFill></wps:spPr></wps:wsp>";

        private static string Stop(int pos, string hex)
            => $@"<a:gs pos=""{pos}""><a:srgbClr val=""{hex}""/></a:gs>";

        private const string TwoStops = null;

        private static string Convert(string drawingXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Drawing(drawingXml)))));
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));
        }

        private static string LinearRedToBlue(string angle = @"<a:lin ang=""0""/>")
            => Inline(GradientShape(Stop(0, "FF0000") + Stop(100000, "0000FF"), angle));

        // ------------------------------------------------------------------ it paints

        [Fact]
        public void GradientFill_EmitsAShadingClippedToTheShape()
        {
            string pdf = Convert(LinearRedToBlue());

            Assert.Matches(@"/Sh\d+ sh", pdf);   // the shading is painted
            Assert.Contains("W n", pdf);         // clipped to the geometry, since `sh` has no path
        }

        [Fact]
        public void LinearGradient_IsWrittenAsAnAxialShading()
        {
            string pdf = Convert(LinearRedToBlue());

            Assert.Contains("/ShadingType 2", pdf);
        }

        [Fact]
        public void PathGradient_IsWrittenAsARadialShading()
        {
            string pdf = Convert(Inline(GradientShape(
                Stop(0, "FFFFFF") + Stop(100000, "000000"),
                @"<a:path path=""circle""><a:fillToRect l=""50000"" t=""50000"" r=""50000"" b=""50000""/></a:path>")));

            Assert.Contains("/ShadingType 3", pdf);
        }

        [Fact]
        public void GradientStops_BecomeTheColoursOfTheRamp()
        {
            string pdf = Convert(LinearRedToBlue());

            // C0 red, C1 blue — an implementation that lost the stops could not produce both.
            Assert.Matches(@"/C0\s*\[\s*1\s+0\s+0\s*\]", pdf);
            Assert.Matches(@"/C1\s*\[\s*0\s+0\s+1\s*\]", pdf);
        }

        [Fact]
        public void ThreeStops_ProduceAStitchingFunctionWithAnInteriorBoundary()
        {
            string pdf = Convert(Inline(GradientShape(
                Stop(0, "FF0000") + Stop(25000, "00FF00") + Stop(100000, "0000FF"),
                @"<a:lin ang=""0""/>")));

            Assert.Contains("/FunctionType 3", pdf);
            Assert.Matches(@"/Bounds\s*\[\s*0\.25\s*\]", pdf);   // at the stop's own position
        }

        [Fact]
        public void GradientAngle_ChangesTheShadingAxis()
        {
            // 0° runs left→right; 5400000 (90°) runs top→bottom, so the axes must differ.
            string horizontal = Convert(LinearRedToBlue(@"<a:lin ang=""0""/>"));
            string vertical = Convert(LinearRedToBlue(@"<a:lin ang=""5400000""/>"));

            Assert.NotEqual(Coords(horizontal), Coords(vertical));

            // Horizontal: the axis spans x and holds y constant.
            var h = Coords(horizontal);
            Assert.Equal(h[1], h[3], 2);
            Assert.NotEqual(h[0], h[2], 2);

            // Vertical: the other way round.
            var v = Coords(vertical);
            Assert.Equal(v[0], v[2], 2);
            Assert.NotEqual(v[1], v[3], 2);
        }

        private static double[] Coords(string pdf)
        {
            var m = Regex.Match(pdf, @"/Coords\s*\[([^\]]+)\]");
            Assert.True(m.Success, "no /Coords was written");
            return m.Groups[1].Value
                .Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture))
                .ToArray();
        }

        [Fact]
        public void GradientOnANonRectangularShape_IsClippedToThatGeometry()
        {
            string pdf = Convert(Inline(GradientShape(
                Stop(0, "FF0000") + Stop(100000, "0000FF"), @"<a:lin ang=""0""/>", prst: "ellipse")));

            // The clip path must be the ellipse's beziers, not the bounding rectangle.
            int shIndex = pdf.IndexOf(" sh", StringComparison.Ordinal);
            Assert.True(shIndex > 0, "no shading was painted");
            string beforeFill = pdf.Substring(0, shIndex);
            Assert.Contains(" c\n", beforeFill);
            Assert.DoesNotContain(" re\n", beforeFill);
        }

        [Fact]
        public void GradientAndOutline_BothPaint()
        {
            string pdf = Convert(Inline(
                $@"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:gradFill><a:gsLst>{Stop(0, "FF0000")}{Stop(100000, "0000FF")}</a:gsLst><a:lin ang=""0""/></a:gradFill>
<a:ln w=""25400""><a:solidFill><a:srgbClr val=""000000""/></a:solidFill></a:ln></wps:spPr></wps:wsp>"));

            Assert.Matches(@"/Sh\d+ sh", pdf);
            Assert.Contains("0.000 0.000 0.000 RG", pdf);
            // The gradient's clip must not leak onto the stroke: they are separate q…Q blocks.
            Assert.Contains("S\n", pdf);
        }

        [Fact]
        public void SolidFill_EmitsNoShading()
        {
            // The control: a plain fill must not acquire a shading, or the assertions above would
            // pass for any shape at all.
            string pdf = Convert(Inline(
                @"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill></wps:spPr></wps:wsp>"));

            Assert.DoesNotMatch(@"/Sh\d+ sh", pdf);
            Assert.Contains("1.000 0.000 0.000 rg", pdf);
        }

        [Fact]
        public void GradientFill_IsNoLongerReportedAsUnsupported()
        {
            Convert(LinearRedToBlue());

            Assert.True(DocxDiagnostics.LastError == null
                        || !DocxDiagnostics.LastError.Contains("gradient fill"),
                "a painted gradient must not claim it was dropped: " + DocxDiagnostics.LastError);
        }

        [Fact]
        public void GradientWithAbsurdlyManyStops_IsCappedAndReported()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 500; i++)
                sb.Append(Stop(i * 200, i % 2 == 0 ? "FF0000" : "0000FF"));
            string pdf = Convert(Inline(GradientShape(sb.ToString(), @"<a:lin ang=""0""/>")));

            Assert.Matches(@"/Sh\d+ sh", pdf);
            Assert.Contains("stops", DocxDiagnostics.LastError);
        }
    }
}

