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
    /// DrawingML colour: <c>a:schemeClr</c>, the <c>lumMod</c>/<c>lumOff</c>/<c>tint</c>/<c>shade</c>
    /// transforms, alpha, and the <c>wps:style</c> → <c>a:fillRef</c> → theme format-scheme
    /// indirection.
    ///
    /// <para>
    /// The headline case is <see cref="ShapeWithOnlyAStyleReference_IsPaintedWithTheThemeAccentColour"/>:
    /// Word's default autoshape carries <b>no</b> <c>spPr/a:solidFill</c>, only a style reference,
    /// so a renderer that reads the shape's own properties alone paints nothing at all — which is
    /// what happened, silently, for every such shape.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ShapeColourTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"";

        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        private const string ThemeNs = "http://schemas.openxmlformats.org/drawingml/2006/main";

        private static string Inline(string payload)
            => $@"<w:drawing {Ns}><wp:inline><wp:extent cx=""914400"" cy=""457200""/>
<wp:docPr id=""1"" name=""S1""/><a:graphic><a:graphicData uri=""{ShapeUri}"">{payload}
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        private static string Shape(string spPrInner, string styleInner = null)
            => $@"<wps:wsp><wps:spPr><a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>{spPrInner}</wps:spPr>"
             + (styleInner == null ? "" : $"<wps:style>{styleInner}</wps:style>")
             + "</wps:wsp>";

        // A theme part with the given accent1, plus the fmtScheme entry a fillRef idx="1" resolves
        // against — a solid phClr, exactly as every stock Office theme writes it.
        private static string ThemeXml(string accent1Hex, string lineWidthEmu = "12700")
            => $@"<a:theme xmlns:a=""{ThemeNs}"" name=""t""><a:themeElements>
<a:clrScheme name=""c"">
<a:dk1><a:srgbClr val=""111111""/></a:dk1><a:lt1><a:srgbClr val=""FFFFFF""/></a:lt1>
<a:dk2><a:srgbClr val=""222222""/></a:dk2><a:lt2><a:srgbClr val=""EEEEEE""/></a:lt2>
<a:accent1><a:srgbClr val=""{accent1Hex}""/></a:accent1>
<a:accent2><a:srgbClr val=""00FF00""/></a:accent2><a:accent3><a:srgbClr val=""0000FF""/></a:accent3>
<a:accent4><a:srgbClr val=""FFFF00""/></a:accent4><a:accent5><a:srgbClr val=""00FFFF""/></a:accent5>
<a:accent6><a:srgbClr val=""FF00FF""/></a:accent6>
<a:hlink><a:srgbClr val=""0000EE""/></a:hlink><a:folHlink><a:srgbClr val=""551A8B""/></a:folHlink>
</a:clrScheme>
<a:fontScheme name=""f""><a:majorFont><a:latin typeface=""Calibri""/></a:majorFont>
<a:minorFont><a:latin typeface=""Calibri""/></a:minorFont></a:fontScheme>
<a:fmtScheme name=""m"">
<a:fillStyleLst><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:fillStyleLst>
<a:lnStyleLst><a:ln w=""{lineWidthEmu}""><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:ln></a:lnStyleLst>
<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>
<a:bgFillStyleLst><a:solidFill><a:schemeClr val=""phClr""/></a:solidFill></a:bgFillStyleLst>
</a:fmtScheme></a:themeElements></a:theme>";

        private static string Convert(string drawingXml, string themeXml = null)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Drawing(drawingXml)))));
                if (themeXml != null)
                {
                    var themePart = main.AddNewPart<ThemePart>();
                    using var s = new MemoryStream(Encoding.UTF8.GetBytes(themeXml));
                    themePart.FeedData(s);
                }
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));
        }

        // Every fill colour the content sets, as (r,g,b).
        private static List<double[]> Fills(string pdf)
            => Regex.Matches(pdf, @"([\d.]+) ([\d.]+) ([\d.]+) rg")
                .Select(m => Enumerable.Range(1, 3)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();

        private static double[] OnlyFill(string pdf)
        {
            var fills = Fills(pdf);
            Assert.Single(fills);
            return fills[0];
        }

        // ------------------------------------------------------------------ the headline defect

        [Fact]
        public void ShapeWithOnlyAStyleReference_IsPaintedWithTheThemeAccentColour()
        {
            // No spPr fill at all — exactly what Word writes for a default autoshape.
            string pdf = Convert(
                Inline(Shape("", @"<a:fillRef idx=""1""><a:schemeClr val=""accent1""/></a:fillRef>")),
                ThemeXml("FF00FF"));

            var rgb = OnlyFill(pdf);
            Assert.Equal(1.0, rgb[0], 2);
            Assert.Equal(0.0, rgb[1], 2);
            Assert.Equal(1.0, rgb[2], 2);
        }

        [Fact]
        public void DirectFill_WinsOverTheStyleReference()
        {
            string pdf = Convert(
                Inline(Shape(@"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>",
                             @"<a:fillRef idx=""1""><a:schemeClr val=""accent1""/></a:fillRef>")),
                ThemeXml("00FF00"));

            var rgb = OnlyFill(pdf);
            Assert.Equal(1.0, rgb[0], 2);   // the shape's own red, not the theme's green
            Assert.Equal(0.0, rgb[1], 2);
        }

        [Fact]
        public void LineReference_SuppliesBothTheOutlineColourAndItsWidth()
        {
            // lnStyleLst[1] is <a:ln w="38100"> = 3pt.
            string pdf = Convert(
                Inline(Shape("", @"<a:lnRef idx=""1""><a:schemeClr val=""accent2""/></a:lnRef>")),
                ThemeXml("FF00FF", lineWidthEmu: "38100"));

            Assert.Contains("0.000 1.000 0.000 RG", pdf);   // accent2 = 00FF00
            Assert.Contains("3.00 w", pdf);
        }

        // ------------------------------------------------------------------ scheme tokens

        [Fact]
        public void SchemeColour_bg1_ResolvesToLight1_NotDark1()
        {
            // The trap: DrawingML's bg1 is lt1. The WordprocessingML enum maps differently, so
            // reusing that mapping would paint this shape black.
            string pdf = Convert(
                Inline(Shape(@"<a:solidFill><a:schemeClr val=""bg1""/></a:solidFill>")),
                ThemeXml("FF00FF"));

            var rgb = OnlyFill(pdf);
            Assert.Equal(1.0, rgb[0], 2);
            Assert.Equal(1.0, rgb[1], 2);
            Assert.Equal(1.0, rgb[2], 2);
        }

        [Fact]
        public void SchemeColour_tx1_ResolvesToDark1()
        {
            string pdf = Convert(
                Inline(Shape(@"<a:solidFill><a:schemeClr val=""tx1""/></a:solidFill>")),
                ThemeXml("FF00FF"));

            var rgb = OnlyFill(pdf);
            Assert.Equal(0x11 / 255.0, rgb[0], 2);   // the theme's dk1 = 111111
        }

        [Fact]
        public void NoThemePart_FallsBackToTheOfficeDefaultsAndReports()
        {
            string pdf = Convert(Inline(Shape(@"<a:solidFill><a:schemeClr val=""accent1""/></a:solidFill>")));

            var rgb = OnlyFill(pdf);
            Assert.Equal(0x44 / 255.0, rgb[0], 2);   // Office accent1 = 4472C4
            Assert.Equal(0x72 / 255.0, rgb[1], 2);
            Assert.Equal(0xC4 / 255.0, rgb[2], 2);
            Assert.Contains("no theme part", DocxDiagnostics.LastError);
        }

        // ------------------------------------------------------------------ transforms

        [Fact]
        public void LumModAndLumOff_LightenTheSchemeColour()
        {
            // "Accent1, Lighter 40%" — the commonest colour in a SmartArt diagram.
            string pdf = Convert(
                Inline(Shape(@"<a:solidFill><a:schemeClr val=""accent1"">
<a:lumMod val=""60000""/><a:lumOff val=""40000""/></a:schemeClr></a:solidFill>")),
                ThemeXml("4472C4"));

            var lightened = OnlyFill(pdf);
            var plain = OnlyFill(Convert(
                Inline(Shape(@"<a:solidFill><a:schemeClr val=""accent1""/></a:solidFill>")),
                ThemeXml("4472C4")));

            // Every channel must move towards white, and the hue must survive.
            for (int i = 0; i < 3; i++)
                Assert.True(lightened[i] > plain[i],
                    $"channel {i}: expected the lightened value {lightened[i]} to exceed {plain[i]}");
            Assert.True(lightened[2] > lightened[0], "the blue accent must stay blue-dominant");
        }

        [Fact]
        public void Shade_DarkensProportionally()
        {
            string pdf = Convert(Inline(Shape(
                @"<a:solidFill><a:srgbClr val=""FF0000""><a:shade val=""50000""/></a:srgbClr></a:solidFill>")));

            var rgb = OnlyFill(pdf);
            Assert.Equal(0.5, rgb[0], 2);
            Assert.Equal(0.0, rgb[1], 2);
        }

        [Fact]
        public void Tint_MovesTowardsWhite()
        {
            string pdf = Convert(Inline(Shape(
                @"<a:solidFill><a:srgbClr val=""FF0000""><a:tint val=""50000""/></a:srgbClr></a:solidFill>")));

            var rgb = OnlyFill(pdf);
            Assert.Equal(1.0, rgb[0], 2);
            Assert.Equal(0.5, rgb[1], 2);   // green and blue rise towards white
            Assert.Equal(0.5, rgb[2], 2);
        }

        [Fact]
        public void UntransformedColour_IsEmittedUnchanged()
        {
            // The control for the three above: without transforms the colour must be untouched,
            // so an implementation that always lightens or always darkens cannot hide.
            string pdf = Convert(Inline(Shape(
                @"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>")));

            var rgb = OnlyFill(pdf);
            Assert.Equal(1.0, rgb[0], 3);
            Assert.Equal(0.0, rgb[1], 3);
            Assert.Equal(0.0, rgb[2], 3);
        }

        [Fact]
        public void PercentSpelledWithASign_IsReadTheSameAsTheIntegerForm()
        {
            string integerForm = Convert(Inline(Shape(
                @"<a:solidFill><a:srgbClr val=""FF0000""><a:shade val=""50000""/></a:srgbClr></a:solidFill>")));
            string percentForm = Convert(Inline(Shape(
                @"<a:solidFill><a:srgbClr val=""FF0000""><a:shade val=""50%""/></a:srgbClr></a:solidFill>")));

            Assert.Equal(OnlyFill(integerForm)[0], OnlyFill(percentForm)[0], 3);
        }

        // ------------------------------------------------------------------ alpha

        [Fact]
        public void TranslucentFill_EmitsAnExtGStateWithTheMatchingConstantAlpha()
        {
            string pdf = Convert(Inline(Shape(
                @"<a:solidFill><a:srgbClr val=""FF0000""><a:alpha val=""50000""/></a:srgbClr></a:solidFill>")));

            Assert.Matches(@"/GS\d+ gs", pdf);
            Assert.Contains("/ca 0.5", pdf);
        }

        [Fact]
        public void OpaqueFill_EmitsNoExtGState()
        {
            string pdf = Convert(Inline(Shape(@"<a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill>")));

            Assert.DoesNotMatch(@"/GS\d+ gs", pdf);
        }

        [Fact]
        public void TwoShapesAtTheSameOpacity_ShareOneExtGState()
        {
            string two = Shape(@"<a:solidFill><a:srgbClr val=""FF0000""><a:alpha val=""50000""/></a:srgbClr></a:solidFill>")
                       + Shape(@"<a:solidFill><a:srgbClr val=""00FF00""><a:alpha val=""50000""/></a:srgbClr></a:solidFill>");
            string pdf = Convert(Inline(
                $@"<wpg:wgp xmlns:wpg=""http://schemas.microsoft.com/office/word/2010/wordprocessingGroup"">
<wpg:grpSpPr/>{two}</wpg:wgp>"));

            var names = Regex.Matches(pdf, @"/GS(\d+) gs")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.Single(names);
        }

        // ------------------------------------------------------------------ honest omissions

        // Gradients are painted as real PDF shadings — see ShapeGradientTests.

        [Fact]
        public void PictureFill_IsNotPaintedAndIsReported()
        {
            string pdf = Convert(Inline(Shape(
                @"<a:blipFill><a:blip r:embed=""rId9"" xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""/></a:blipFill>")));

            Assert.Empty(Fills(pdf));
            Assert.Contains("picture fill", DocxDiagnostics.LastError);
        }

        [Fact]
        public void NoFill_StaysUnpainted()
        {
            string pdf = Convert(Inline(Shape(@"<a:noFill/>",
                @"<a:fillRef idx=""1""><a:schemeClr val=""accent1""/></a:fillRef>")));

            // An explicit a:noFill must beat the style reference, or every "no fill" shape in a
            // themed document would acquire an accent colour.
            Assert.Empty(Fills(pdf));
        }
    }
}

