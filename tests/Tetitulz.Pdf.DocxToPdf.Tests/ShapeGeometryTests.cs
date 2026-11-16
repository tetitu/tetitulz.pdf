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
    /// Preset geometry, <c>a:xfrm</c> placement and group walking.
    ///
    /// <para>
    /// What this replaced painted the <b>first</b> <c>prstGeom</c> found anywhere in the drawing,
    /// at the drawing's whole extent, ignoring every <c>a:xfrm</c> — so a group of shapes came out
    /// as one rectangle and nothing could be positioned, rotated or flipped. These tests read the
    /// emitted operands, so they fail on a geometry that is merely plausible.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ShapeGeometryTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:wpg=\"http://schemas.microsoft.com/office/word/2010/wordprocessingGroup\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        private const string GroupUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";

        // 2" × 1" drawing (1828800 × 914400 EMU) = 144 × 72 pt.
        private static string Inline(string uri, string payload)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""1828800"" cy=""914400""/>
<wp:docPr id=""1"" name=""G1""/><a:graphic><a:graphicData uri=""{uri}"">{payload}
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        private static string Shape(string prst, string xfrm = "", string spPrExtra = "",
            string fillHex = "FF0000")
            => $@"<wps:wsp><wps:spPr>{xfrm}<a:prstGeom prst=""{prst}""><a:avLst/></a:prstGeom>
<a:solidFill><a:srgbClr val=""{fillHex}""/></a:solidFill>{spPrExtra}</wps:spPr></wps:wsp>";

        // a:off/a:ext in EMU.
        private static string Xfrm(long x, long y, long cx, long cy, string extra = "")
            => $@"<a:xfrm {extra}><a:off x=""{x}"" y=""{y}""/><a:ext cx=""{cx}"" cy=""{cy}""/></a:xfrm>";

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

        // Every "a b c d e f cm" in the content, as six numbers.
        private static List<double[]> Cms(string pdf)
            => Regex.Matches(pdf,
                    @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm")
                .Select(m => Enumerable.Range(1, 6)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();

        // The shape matrices, i.e. every cm except the drawing frame's own y-flip.
        private static List<double[]> ShapeCms(string pdf)
            => Cms(pdf).Where(v => !(v[0] == 1 && v[1] == 0 && v[2] == 0 && v[3] == -1)).ToList();

        private static int Count(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ------------------------------------------------------------------ the y-flip frame

        [Fact]
        public void EveryDrawing_OpensASingleYFlippedFrame_SoGeometryIsAuthoredTopDown()
        {
            string pdf = Convert(Inline(ShapeUri, Shape("rect")));

            // "1 0 0 -1 <left> <top> cm" — the one place PDF's upward y axis is dealt with.
            Assert.Matches(@"1 0 0 -1 [\d.]+ [\d.]+ cm", pdf);
        }

        // ------------------------------------------------------------------ preset geometry

        [Fact]
        public void RoundRect_EmitsCornerCurves_NotAPlainRectangle()
        {
            string pdf = Convert(Inline(ShapeUri, Shape("roundRect")));

            Assert.Contains(" c\n", pdf);                 // rounded corners are bezier arcs
            Assert.DoesNotContain("0.00 0.00 144.00 72.00 re", pdf);   // and not a bare box
        }

        [Fact]
        public void RoundRect_AdjustValue_ChangesTheCornerRadius()
        {
            // adj is a fraction of min(w,h) = 72pt: 0 ⇒ square corners, 50000 ⇒ fully round.
            string square = Convert(Inline(ShapeUri, Shape("roundRect",
                spPrExtra: "").Replace("<a:avLst/>", @"<a:avLst><a:gd name=""adj"" fmla=""val 0""/></a:avLst>")));
            string round = Convert(Inline(ShapeUri, Shape("roundRect")
                .Replace("<a:avLst/>", @"<a:avLst><a:gd name=""adj"" fmla=""val 50000""/></a:avLst>")));

            // A zero radius degenerates to the plain rectangle; a large one must not.
            Assert.Contains("re\n", square);
            Assert.DoesNotContain("re\n", round);
        }

        [Fact]
        public void Chevron_EmitsItsSixVertices()
        {
            string pdf = Convert(Inline(ShapeUri, Shape("chevron")));

            // One moveto + five linetos + close — a chevron, not the fallback rectangle.
            Assert.Equal(1, Count(pdf, " m\n"));
            Assert.Equal(5, Count(pdf, " l\n"));
            Assert.Contains("h\n", pdf);
            Assert.DoesNotContain(" re\n", pdf);
        }

        [Fact]
        public void Diamond_ApexIsAtTheTopCentreOfTheShapeBox()
        {
            string pdf = Convert(Inline(ShapeUri, Shape("diamond")));

            // Top-down frame: the first point is (w/2, 0) = (72, 0) for a 144×72 box.
            Assert.Contains("72.00 0.00 m", pdf);
        }

        [Fact]
        public void UnknownPreset_FallsBackToItsBoundingRectangleAndReports()
        {
            string pdf = Convert(Inline(ShapeUri, Shape("funnel")));

            Assert.Contains(" re\n", pdf);
            Assert.Contains("funnel", DocxDiagnostics.LastError);
        }

        [Fact]
        public void KnownPreset_DoesNotReportAnApproximation()
        {
            Convert(Inline(ShapeUri, Shape("hexagon")));

            // A preset that really is implemented must not claim it was approximated.
            Assert.True(DocxDiagnostics.LastError == null
                        || !DocxDiagnostics.LastError.Contains("hexagon"),
                "hexagon is implemented but was reported as approximated: " + DocxDiagnostics.LastError);
        }

        // ------------------------------------------------------------------ flow placement

        [Fact]
        public void InlineDrawing_StartsBelowThePrecedingLinesDescenders()
        {
            // The flow cursor rests ON a line's baseline, so a drawing placed there paints over
            // that line's "p" and "g". Found by rendering a page and looking at it — no geometry
            // assertion could see it, because every one of them is about relative position within
            // the drawing rather than overlap with the line above.
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text("pgqyj"))),
                    new Paragraph(new Run(new Drawing(Inline(ShapeUri, Shape("rect")))))));
                main.Document.Save();
            }

            string pdf = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));

            double baseline = Regex.Matches(pdf, @"-?[\d.]+ (-?[\d.]+) Td")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .First();

            // The drawing frame's y-flip carries the box's TOP edge.
            var frame = Regex.Match(pdf, @"1 0 0 -1 [\d.]+ ([\d.]+) cm");
            Assert.True(frame.Success, "the drawing was never placed");
            double top = double.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture);

            // 12pt text descends about 2.4pt below its baseline; the drawing must clear that.
            Assert.True(top < baseline - 1.5,
                $"the drawing's top ({top}) must sit below the caption's baseline ({baseline})");
        }

        // ------------------------------------------------------------------ a:xfrm

        [Fact]
        public void ShapeWithXfrmOffset_IsPaintedAtThatOffset_NotAtTheDrawingOrigin()
        {
            // Offset 457200 EMU = 36pt right, 228600 = 18pt down; box 914400×457200 = 72×36pt.
            string pdf = Convert(Inline(ShapeUri,
                Shape("rect", Xfrm(457200, 228600, 914400, 457200))));

            var cms = ShapeCms(pdf);
            Assert.Single(cms);
            Assert.Equal(36.0, cms[0][4], 1);   // e — x offset
            Assert.Equal(18.0, cms[0][5], 1);   // f — y offset, downward in this frame
        }

        [Fact]
        public void RotatedShape_EmitsARotationMatrix()
        {
            // 45° clockwise = 2700000 in 60000ths of a degree.
            string pdf = Convert(Inline(ShapeUri,
                Shape("rect", Xfrm(0, 0, 914400, 914400, @"rot=""2700000"""))));

            var cms = ShapeCms(pdf);
            Assert.Single(cms);
            double c = Math.Sqrt(2) / 2;
            Assert.Equal(c, cms[0][0], 3);      // a =  cos θ
            Assert.Equal(c, cms[0][1], 3);      // b =  sin θ
            Assert.Equal(-c, cms[0][2], 3);     // c = -sin θ
            Assert.Equal(c, cms[0][3], 3);      // d =  cos θ
        }

        [Fact]
        public void HorizontallyFlippedShape_EmitsANegativeHorizontalScale()
        {
            string pdf = Convert(Inline(ShapeUri,
                Shape("rect", Xfrm(0, 0, 914400, 457200, @"flipH=""1"""))));

            var cms = ShapeCms(pdf);
            Assert.Single(cms);
            Assert.Equal(-1.0, cms[0][0], 3);
            Assert.Equal(1.0, cms[0][3], 3);
        }

        [Fact]
        public void UnrotatedShape_EmitsTheIdentityOrientation()
        {
            // The control for the two above: without rot/flip the orientation must be untouched,
            // so a mutation that always rotates or always flips cannot hide.
            string pdf = Convert(Inline(ShapeUri, Shape("rect", Xfrm(0, 0, 914400, 457200))));

            var cms = ShapeCms(pdf);
            Assert.Single(cms);
            Assert.Equal(1.0, cms[0][0], 3);
            Assert.Equal(0.0, cms[0][1], 3);
            Assert.Equal(0.0, cms[0][2], 3);
            Assert.Equal(1.0, cms[0][3], 3);
        }

        // ------------------------------------------------------------------ groups

        private static string Group(params string[] shapes)
            => $@"<wpg:wgp><wpg:grpSpPr><a:xfrm><a:off x=""0"" y=""0""/><a:ext cx=""1828800"" cy=""914400""/>
<a:chOff x=""0"" y=""0""/><a:chExt cx=""1828800"" cy=""914400""/></a:xfrm></wpg:grpSpPr>
{string.Join("\n", shapes)}</wpg:wgp>";

        [Fact]
        public void GroupOfTwoShapes_PaintsBothGeometries_NotOneRectangle()
        {
            string pdf = Convert(Inline(GroupUri, Group(
                Shape("rect", Xfrm(0, 0, 914400, 914400), fillHex: "FF0000"),
                Shape("ellipse", Xfrm(914400, 0, 914400, 914400), fillHex: "00FF00"))));

            Assert.Contains("1.000 0.000 0.000 rg", pdf);   // the rectangle
            Assert.Contains("0.000 1.000 0.000 rg", pdf);   // the ellipse
            Assert.Contains(" re\n", pdf);
            Assert.Contains(" c\n", pdf);
            Assert.Equal(2, Count(pdf, "f\nQ\n"));          // exactly two painted shapes
        }

        [Fact]
        public void GroupChildren_ArePaintedAtTheirOwnOffsets()
        {
            string pdf = Convert(Inline(GroupUri, Group(
                Shape("rect", Xfrm(0, 0, 914400, 914400)),
                Shape("rect", Xfrm(914400, 0, 914400, 914400)))));

            var xs = ShapeCms(pdf).Select(v => v[4]).OrderBy(v => v).ToList();
            Assert.Equal(2, xs.Count);
            Assert.Equal(0.0, xs[0], 1);
            Assert.Equal(72.0, xs[1], 1);   // 914400 EMU = 72pt to the right of the first
        }

        [Fact]
        public void GroupWithAChildExtentScale_MapsChildrenOntoTheGroupBox()
        {
            // chExt is twice ext, so the group's contents are drawn at half size.
            string payload = $@"<wpg:wgp><wpg:grpSpPr><a:xfrm><a:off x=""0"" y=""0""/>
<a:ext cx=""914400"" cy=""914400""/><a:chOff x=""0"" y=""0""/><a:chExt cx=""1828800"" cy=""1828800""/>
</a:xfrm></wpg:grpSpPr>{Shape("rect", Xfrm(914400, 0, 914400, 914400))}</wpg:wgp>";
            string pdf = Convert(Inline(GroupUri, payload));

            var cms = ShapeCms(pdf);
            Assert.Single(cms);
            Assert.Equal(0.5, cms[0][0], 3);    // the child scale
            Assert.Equal(36.0, cms[0][4], 1);   // 72pt offset, halved
        }

        [Fact]
        public void GroupNestedBeyondTheDepthCap_IsBoundedAndReported()
        {
            string payload = Shape("rect");
            for (int i = 0; i < 40; i++)
                payload = $@"<a:grpSp><a:grpSpPr><a:xfrm><a:off x=""0"" y=""0""/>
<a:ext cx=""1828800"" cy=""914400""/><a:chOff x=""0"" y=""0""/><a:chExt cx=""1828800"" cy=""914400""/>
</a:xfrm></a:grpSpPr>{payload}</a:grpSp>";

            string pdf = Convert(Inline(GroupUri, $@"<wpg:wgp><wpg:grpSpPr/>{payload}</wpg:wgp>"));

            Assert.Contains("nesting", DocxDiagnostics.LastError);
            Assert.Equal(0, Count(pdf, "f\nQ\n"));   // nothing beyond the cap was painted
        }
    }
}

