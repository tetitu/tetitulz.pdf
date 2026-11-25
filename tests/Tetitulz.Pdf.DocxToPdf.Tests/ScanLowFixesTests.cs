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
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The LOW-band findings of the 2026-08 assembly scan that were confirmed against the current
    /// code: a divide-by-zero from a file-supplied percentage, an axis suppression key that was
    /// parsed and never read, a diagnostic that fired on conformant input, an inline picture laid
    /// out by different rules from every other inline drawing, a cleared tab stop that survived,
    /// and a percentage table width that was thrown away.
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ScanLowFixesTests
    {
        private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string CNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        private const string WpsNs = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

        private static readonly DocxToPdfOptions NoCompress =
            new DocxToPdfOptions { CompressStreams = false };

        private static string Convert(Action<WordprocessingDocument, Body> build)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                build(doc, body);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(ms.ToArray(), NoCompress));
        }

        // ================================================================== charts

        private static string ChartDocx(string chartXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body());

                var chartPart = main.AddNewPart<ChartPart>();
                using (var s = new MemoryStream(Encoding.UTF8.GetBytes(chartXml)))
                    chartPart.FeedData(s);
                string relId = main.GetIdOfPart(chartPart);

                main.Document.Body.Append(new Paragraph(new Run(new Drawing(
                    $@"<w:drawing xmlns:w=""{WNs}"" xmlns:wp=""{WpNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}"">
<wp:inline><wp:extent cx=""5486400"" cy=""3657600""/><wp:docPr id=""1"" name=""Chart 1""/>
<a:graphic><a:graphicData uri=""{ChartUri}"">
<c:chart xmlns:c=""{CNs}"" xmlns:r=""{RNs}"" r:id=""{relId}""/>
</a:graphicData></a:graphic></wp:inline></w:drawing>"))));
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(ms.ToArray(), NoCompress));
        }

        private static string BarChartXml(string plotExtras, string axes)
            => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<c:chartSpace xmlns:c=""{CNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}""><c:chart><c:plotArea><c:layout/>
<c:barChart><c:barDir val=""col""/><c:grouping val=""clustered""/>{plotExtras}
<c:ser><c:idx val=""0""/><c:order val=""0""/>
<c:spPr><a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill></c:spPr>
<c:cat><c:strRef><c:f>a</c:f><c:strCache><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>A</c:v></c:pt><c:pt idx=""1""><c:v>B</c:v></c:pt></c:strCache></c:strRef></c:cat>
<c:val><c:numRef><c:f>b</c:f><c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>10</c:v></c:pt><c:pt idx=""1""><c:v>20</c:v></c:pt></c:numCache></c:numRef></c:val>
</c:ser></c:barChart>{axes}</c:plotArea></c:chart></c:chartSpace>";

        private const string PlainAxes = @"
<c:catAx><c:axId val=""1""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""b""/><c:crossAx val=""2""/></c:catAx>
<c:valAx><c:axId val=""2""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""l""/><c:crossAx val=""1""/></c:valAx>";

        private const string DeletedValueAxis = @"
<c:catAx><c:axId val=""1""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""b""/><c:crossAx val=""2""/></c:catAx>
<c:valAx><c:axId val=""2""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""1""/><c:axPos val=""l""/><c:crossAx val=""1""/>
<c:title><c:tx><c:rich><a:p><a:r><a:t>VALUEAXISTITLE</a:t></a:r></a:p></c:rich></c:tx></c:title>
</c:valAx>";

        private static int RedBarCount(string pdf)
            => Regex.Matches(pdf, @"1\.000 0\.000 0\.000 rg\n-?[\d.]+ -?[\d.]+ -?[\d.]+ -?[\d.]+ re\nf").Count;

        // ------------------------------------------------------------------ ChartLayout:251

        [Fact]
        public void NegativeGapWidth_IsClampedAndReported_InsteadOfMakingEveryBarVanish()
        {
            // gapWidth = -100 with one series makes `lanes + GapWidth / 100.0` exactly zero, so
            // barW is +Infinity, laneStart is -Infinity, and ChartPainter's finite guard skips
            // every rect — a chart drawn as axes and gridlines around an empty plot, reported
            // nowhere. The clamp's own evidence is the bars, plus the message naming the value.
            string pdf = ChartDocx(BarChartXml(@"<c:gapWidth val=""-100""/>", PlainAxes));

            Assert.Equal(2, RedBarCount(pdf));
            Assert.Contains("c:gapWidth", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        [Fact]
        public void OrdinaryGapWidth_IsNotReported()
        {
            // The complement, because a diagnostic that fires on conformant input is one callers
            // learn to ignore: an in-range value must pass through silently.
            string pdf = ChartDocx(BarChartXml(@"<c:gapWidth val=""150""/>", PlainAxes));

            Assert.Equal(2, RedBarCount(pdf));
            Assert.DoesNotContain("c:gapWidth", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        // ------------------------------------------------------------------ ChartPartReader:350

        [Fact]
        public void DeletedValueAxis_DrawsNoTickLabelsAndNoAxisTitle()
        {
            string deleted = ChartDocx(BarChartXml("", DeletedValueAxis));
            string kept = ChartDocx(BarChartXml("", PlainAxes));

            // The data is still plotted — c:delete removes the AXIS, not the series.
            Assert.Equal(2, RedBarCount(deleted));

            // Its title is gone…
            Assert.DoesNotContain("VALUEAXISTITLE", deleted);

            // …and so are its tick labels. AxisSpec.Deleted was filled in by the reader and read by
            // nothing, so the converted chart carried axis furniture Word shows nowhere. Counting
            // drawn strings rather than naming one keeps this independent of the tick values the
            // scale happens to choose.
            Assert.True(DrawnStrings(deleted).Count < DrawnStrings(kept).Count,
                "a deleted value axis drew as many strings as a kept one");
        }

        private static List<string> DrawnStrings(string pdf)
            => Regex.Matches(pdf, @"\(([^)]*)\) Tj").Select(m => m.Groups[1].Value).ToList();

        // ------------------------------------------------------------------ ShapeTreeRenderer:56

        private static string ShapeTreeDrawing(int shapeCount)
        {
            var shapes = new StringBuilder();
            for (int i = 0; i < shapeCount; i++)
                shapes.Append(
                    @"<wps:wsp><wps:spPr><a:xfrm><a:off x=""0"" y=""0""/><a:ext cx=""9144"" cy=""9144""/></a:xfrm>"
                    + @"<a:prstGeom prst=""rect""><a:avLst/></a:prstGeom>"
                    + @"<a:solidFill><a:srgbClr val=""00FF00""/></a:solidFill></wps:spPr></wps:wsp>");

            return $@"<w:drawing xmlns:w=""{WNs}"" xmlns:wp=""{WpNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}""
xmlns:wps=""{WpsNs}""><wp:inline><wp:extent cx=""914400"" cy=""914400""/>
<wp:docPr id=""1"" name=""G1""/><a:graphic><a:graphicData uri=""{ShapeUri}"">{shapes}
</a:graphicData></a:graphic></wp:inline></w:drawing>";
        }

        [Fact]
        public void ShapeCountAtExactlyTheCap_PaintsEverythingAndReportsNothing()
        {
            int cap = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDrawingShapes;

            // The walk stops BEFORE painting the (cap+1)-th shape, so `painted == cap` is the case
            // where every shape was drawn — yet the report was conditioned on exactly that and
            // claimed "the remainder were skipped" for a conformant drawing.
            Convert((doc, body) => body.Append(new Paragraph(new Run(new Drawing(ShapeTreeDrawing(cap))))));
            Assert.DoesNotContain("the remainder were skipped", DocxToPdfConverter.LastDiagnostic ?? "");

            // One more, and the truncation is real — so the guard cannot be satisfied by deleting
            // the report.
            Convert((doc, body) => body.Append(new Paragraph(new Run(new Drawing(ShapeTreeDrawing(cap + 1))))));
            Assert.Contains("the remainder were skipped", DocxToPdfConverter.LastDiagnostic ?? "");
        }

        // ------------------------------------------------------------------ ImageRenderer:70

        /// <summary>The y of the bottom-left corner of every painted image, in emit order.</summary>
        private static List<double> ImageBottomsY(string pdf)
            => Regex.Matches(pdf, @"[\d.]+ 0 0 [\d.]+ [\d.]+ (-?[\d.]+) cm\n/Im\d+ Do")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

        [Fact]
        public void InlinePicture_ClearsThePreviousLinesDescenders_LikeEveryOtherInlineDrawing()
        {
            byte[] png = DocxImageBuilder.MakeTruecolorPng(4, 4, 10, 20, 30);

            string WithLeadingText(bool leadingParagraph)
            {
                using var ms = new MemoryStream();
                using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                {
                    var main = doc.AddMainDocumentPart();
                    var imagePart = main.AddImagePart("image/png");
                    using (var s = new MemoryStream(png)) imagePart.FeedData(s);

                    var body = new Body();
                    if (leadingParagraph)
                        body.Append(new Paragraph(new Run(new Text("paging"))));
                    body.Append(new Paragraph(new Run(
                        DocxImageBuilder.BuildInlineDrawing(main.GetIdOfPart(imagePart), 914400, 914400))));
                    main.Document = new Document(body);
                    main.Document.Save();
                }
                return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(ms.ToArray(), NoCompress));
            }

            double afterText = ImageBottomsY(WithLeadingText(true)).Single();
            double alone = ImageBottomsY(WithLeadingText(false)).Single();

            // DocxRenderContext.LastLineDescent is consumed in exactly one place —
            // DrawingPlacement.PlaceInline — and RenderInlineImage did its own placement, so a
            // picture's top edge landed exactly on the previous line's baseline and painted over
            // the descenders of its p's and g's. Routing it through PlaceInline drops the picture
            // by that descent; nothing else about the two documents differs below the first line.
            double lineHeight = 12 * DocxLayoutMetrics.SingleLineFactor;
            double descent = 12 * (DocxLayoutMetrics.SingleLineFactor - 1.0);
            Assert.Equal(alone - lineHeight - descent, afterText, 1);
        }

        // ------------------------------------------------------------------ StyleResolver:566

        /// <summary>The single tab advance in the content, in points at the given font size.</summary>
        private static double TabAdvancePt(string pdf, double fontSize)
        {
            var m = Regex.Match(pdf, @"\[(-?[\d.]+)\] TJ");
            Assert.True(m.Success, "no TJ tab advance in the output");
            return -double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0 * fontSize;
        }

        [Fact]
        public void ClearedTabStop_RemovesTheInheritedStopItNames()
        {
            string Build(bool clearIt) => Convert((doc, body) =>
            {
                var stylesPart = doc.MainDocumentPart.AddNewPart<StyleDefinitionsPart>();
                stylesPart.Styles = new Styles(new Style(
                    new StyleName { Val = "Tabbed" },
                    new StyleParagraphProperties(new Tabs(
                        new TabStop { Val = TabStopValues.Left, Position = 720 })))
                {
                    Type = StyleValues.Paragraph,
                    StyleId = "Tabbed",
                });
                stylesPart.Styles.Save();

                var tabs = new Tabs();
                if (clearIt) tabs.Append(new TabStop { Val = TabStopValues.Clear, Position = 720 });
                tabs.Append(new TabStop { Val = TabStopValues.Left, Position = 4320 });

                body.Append(new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Tabbed" }, tabs),
                    new Run(new Text("A") { Space = SpaceProcessingModeValues.Preserve },
                            new TabChar(),
                            new Text("B") { Space = SpaceProcessingModeValues.Preserve })));
            });

            // Uncleared: the style's 0.5" stop is the first one past the cursor, so the tab is
            // short. This is the control, and it also proves the style's stop really is inherited.
            double kept = TabAdvancePt(Build(false), 12);

            // Cleared: ECMA-376 §17.3.1.37 says a `clear` entry REMOVES the inherited stop at that
            // position. Skipping the entry left the stop in the list, and being the lower one it
            // was the first ResolveTabStop found — so the text stopped at half an inch instead of
            // running out to the 3" stop the author asked for.
            double cleared = TabAdvancePt(Build(true), 12);

            Assert.True(cleared > kept + 100,
                $"cleared tab advanced {cleared:F1}pt, uncleared {kept:F1}pt — the cleared stop survived");
        }

        // ------------------------------------------------------------------ TableRenderer:571

        /// <summary>The x of every stroked vertical rule, which bound the table's drawn width.</summary>
        private static double TableSpan(string pdf)
        {
            var xs = Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) m\n(-?[\d.]+) (-?[\d.]+) l\nS")
                .Where(m => m.Groups[1].Value == m.Groups[3].Value)   // vertical rules only
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();
            Assert.True(xs.Count >= 2, "no table rules in the output");
            return xs.Max() - xs.Min();
        }

        [Fact]
        public void PercentTableWidth_IsHonoured_NotLaidOutAtTheFullColumnWidth()
        {
            string Build(TableWidthUnitValues type, string width) => Convert((doc, body) =>
                body.Append(new Table(
                    new TableProperties(
                        new TableWidth { Type = type, Width = width },
                        new TableBorders(
                            new TopBorder { Val = BorderValues.Single, Size = 4 },
                            new BottomBorder { Val = BorderValues.Single, Size = 4 },
                            new LeftBorder { Val = BorderValues.Single, Size = 4 },
                            new RightBorder { Val = BorderValues.Single, Size = 4 },
                            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                            new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })),
                    new TableGrid(new GridColumn { Width = "4680" }, new GridColumn { Width = "4680" }),
                    new TableRow(
                        new TableCell(new Paragraph(new Run(new Text("L")))),
                        new TableCell(new Paragraph(new Run(new Text("R"))))))));

            double full = TableSpan(Build(TableWidthUnitValues.Auto, "0"));
            double half = TableSpan(Build(TableWidthUnitValues.Pct, "2500"));

            // 2500 fiftieths of a percent is 50% — the normal form an HTML-to-DOCX exporter and
            // Word's "AutoFit to window" both emit. Only `dxa` was honoured, so this table was
            // laid out across the whole column and every cell came out twice as wide as authored.
            Assert.Equal(full / 2.0, half, 1);
        }
    }
}
