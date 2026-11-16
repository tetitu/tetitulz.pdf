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
    /// Charts, end to end: a real <see cref="ChartPart"/> in the fixture, and assertions on the
    /// numbers the renderer emits.
    ///
    /// <para>
    /// Every geometric assertion is derived from <b>two emitted values</b> — a ratio, or a shared
    /// coordinate — never from an absolute position. That is what makes them survive an unrelated
    /// margin or font change while still failing on a real scaling bug.
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class ChartRenderingTests
    {
        private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
        private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string CNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";

        // A 6" × 4" inline chart.
        private static string ChartDrawing(string relId, long cx = 5486400, long cy = 3657600)
            => $@"<w:drawing xmlns:w=""{WNs}"" xmlns:wp=""{WpNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}"">
<wp:inline><wp:extent cx=""{cx}"" cy=""{cy}""/><wp:docPr id=""1"" name=""Chart 1""/>
<a:graphic><a:graphicData uri=""{ChartUri}"">
<c:chart xmlns:c=""{CNs}"" xmlns:r=""{RNs}"" r:id=""{relId}""/>
</a:graphicData></a:graphic></wp:inline></w:drawing>";

        /// <summary>One series: a name, cached category strings and cached numeric values.</summary>
        private static string Series(int idx, string name, string[] categories, double?[] values,
            string colorHex = null)
        {
            var cats = new StringBuilder();
            for (int i = 0; i < categories.Length; i++)
                cats.AppendFormat(CultureInfo.InvariantCulture,
                    "<c:pt idx=\"{0}\"><c:v>{1}</c:v></c:pt>", i, categories[i]);

            var pts = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
                if (values[i].HasValue)
                    pts.AppendFormat(CultureInfo.InvariantCulture,
                        "<c:pt idx=\"{0}\"><c:v>{1}</c:v></c:pt>", i,
                        values[i].Value.ToString("R", CultureInfo.InvariantCulture));

            string spPr = colorHex == null ? "" :
                $@"<c:spPr><a:solidFill><a:srgbClr val=""{colorHex}""/></a:solidFill></c:spPr>";

            return $@"<c:ser><c:idx val=""{idx}""/><c:order val=""{idx}""/>
<c:tx><c:strRef><c:f>Sheet1!$A${idx + 1}</c:f><c:strCache><c:ptCount val=""1""/>
<c:pt idx=""0""><c:v>{name}</c:v></c:pt></c:strCache></c:strRef></c:tx>{spPr}
<c:cat><c:strRef><c:f>Sheet1!$B$1</c:f><c:strCache><c:ptCount val=""{categories.Length}""/>
{cats}</c:strCache></c:strRef></c:cat>
<c:val><c:numRef><c:f>Sheet1!$B${idx + 1}</c:f><c:numCache><c:formatCode>General</c:formatCode>
<c:ptCount val=""{values.Length}""/>{pts}</c:numCache></c:numRef></c:val></c:ser>";
        }

        private static string ChartXml(string plotElement, string seriesXml, string axes = null,
            string plotExtras = "", string chartExtras = "")
            => $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<c:chartSpace xmlns:c=""{CNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}"">
<c:chart>{chartExtras}<c:plotArea><c:layout/>
<c:{plotElement}>{plotExtras}{seriesXml}</c:{plotElement}>
{axes ?? DefaultAxes}</c:plotArea></c:chart></c:chartSpace>";

        private const string DefaultAxes = @"
<c:catAx><c:axId val=""1""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""b""/><c:crossAx val=""2""/></c:catAx>
<c:valAx><c:axId val=""2""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""l""/><c:crossAx val=""1""/></c:valAx>";

        private static byte[] DocxWithChart(string chartXml, bool trailingParagraph = false,
            long cy = 3657600)
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

                main.Document.Body.Append(new Paragraph(new Run(
                    new Drawing(ChartDrawing(relId, cy: cy)))));
                if (trailingParagraph)
                    main.Document.Body.Append(new Paragraph(new Run(new Text("AFTER"))));
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static string ToPdf(byte[] docx)
            => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(docx,
                new DocxToPdfOptions { CompressStreams = false }));

        private static string Convert(string chartXml) => ToPdf(DocxWithChart(chartXml));

        /// <summary>
        /// Converts while collecting every diagnostic. A chart that cannot be drawn reports twice —
        /// why, and then that its box was reserved — and <c>LastError</c> keeps only the last.
        /// </summary>
        private static string ToPdf(byte[] docx, out List<string> diagnostics)
        {
            var seen = new List<string>();
            void Collect(string m) => seen.Add(m);

            DocxToPdfConverter.DiagnosticReported += Collect;
            try
            {
                string pdf = ToPdf(docx);
                diagnostics = seen;
                return pdf;
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }
        }

        /// <summary>Every filled rectangle in the content, as (x, y, w, h).</summary>
        private static List<double[]> Rects(string pdf)
            => Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\nf")
                .Select(m => Enumerable.Range(1, 4)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();

        /// <summary>The rectangles painted in a given fill colour.</summary>
        private static List<double[]> RectsOfColour(string pdf, string rgbOperator)
        {
            var result = new List<double[]>();
            foreach (Match m in Regex.Matches(pdf,
                Regex.Escape(rgbOperator) + @"\n(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\nf"))
            {
                result.Add(Enumerable.Range(1, 4)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray());
            }
            return result;
        }

        private static List<string> DrawnStrings(string pdf)
            => Regex.Matches(pdf, @"\(([^)]*)\) Tj").Select(m => m.Groups[1].Value).ToList();

        private static readonly string[] Cats = { "A", "B" };

        // ------------------------------------------------------------------ it renders

        [Fact]
        public void ColumnChart_TwoValues_BarHeightsAreInTheValueRatio()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 10, 20 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            var bars = RectsOfColour(pdf, "1.000 0.000 0.000 rg");
            Assert.Equal(2, bars.Count);

            var ordered = bars.OrderBy(b => b[0]).ToList();
            // Heights in a 1:2 ratio, and both standing on the same baseline.
            Assert.Equal(2.0, ordered[1][3] / ordered[0][3], 2);
            Assert.Equal(ordered[0][1], ordered[1][1], 2);
        }

        [Fact]
        public void ColumnChart_BarsAreSideBySideAndDoNotOverlap()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 10, 20 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            var bars = RectsOfColour(pdf, "1.000 0.000 0.000 rg").OrderBy(b => b[0]).ToList();
            Assert.Equal(2, bars.Count);
            Assert.True(bars[0][0] + bars[0][2] <= bars[1][0] + 0.01,
                "the two category bars must not overlap");
        }

        [Fact]
        public void BarChart_HorizontalDirection_GrowsAlongXInsteadOfY()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 10, 20 }, "FF0000"),
                plotExtras: @"<c:barDir val=""bar""/><c:grouping val=""clustered""/>"));

            var bars = RectsOfColour(pdf, "1.000 0.000 0.000 rg");
            Assert.Equal(2, bars.Count);

            // Word puts category 0 at the BOTTOM of a bar chart, so ascending y is A then B.
            var ordered = bars.OrderBy(b => b[1]).ToList();
            // Widths carry the values now, and both start from the same x baseline.
            Assert.Equal(2.0, ordered[1][2] / ordered[0][2], 2);
            Assert.Equal(ordered[0][0], ordered[1][0], 2);
        }

        [Fact]
        public void ColumnChart_TwoSeries_UsesADistinctColourPerSeries()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 10, 20 }, "FF0000")
                + Series(1, "S2", Cats, new double?[] { 5, 8 }, "00FF00"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            Assert.Equal(2, RectsOfColour(pdf, "1.000 0.000 0.000 rg").Count);
            Assert.Equal(2, RectsOfColour(pdf, "0.000 1.000 0.000 rg").Count);
        }

        // ------------------------------------------------------------------ the zero baseline

        [Fact]
        public void MixedSignValues_ShareOneBaselineAndGrowOppositeWays()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 50, -30 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            var bars = RectsOfColour(pdf, "1.000 0.000 0.000 rg").OrderBy(b => b[0]).ToList();
            Assert.Equal(2, bars.Count);

            double positiveBottom = bars[0][1];
            double negativeTop = bars[1][1] + bars[1][3];
            Assert.Equal(positiveBottom, negativeTop, 1);          // one shared baseline
            Assert.Equal(0.6, bars[1][3] / bars[0][3], 2);          // 30/50
        }

        [Fact]
        public void AllNegativeValues_HangFromABaselineAtTheTopOfTheData()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { -10, -20 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            var bars = RectsOfColour(pdf, "1.000 0.000 0.000 rg").OrderBy(b => b[0]).ToList();
            Assert.Equal(2, bars.Count);

            // Both hang from the same top edge, and the deeper value hangs further.
            Assert.Equal(bars[0][1] + bars[0][3], bars[1][1] + bars[1][3], 1);
            Assert.True(bars[1][3] > bars[0][3]);
        }

        [Fact]
        public void AllPositiveValues_StartAtZeroNotAtTheDataMinimum()
        {
            // 100/101/102 — if the axis started at 100 the bars would be in a 1:2:3-ish ratio
            // instead of nearly equal, which is the classic misleading chart.
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", new[] { "A", "B", "C" }, new double?[] { 100, 101, 102 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            var bars = RectsOfColour(pdf, "1.000 0.000 0.000 rg").OrderBy(b => b[0]).ToList();
            Assert.Equal(3, bars.Count);
            Assert.True(bars[2][3] / bars[0][3] < 1.1,
                "bars of 100/101/102 must be nearly equal, i.e. measured from zero");
        }

        // ------------------------------------------------------------------ stacking

        [Fact]
        public void StackedColumns_SegmentsAbutAndTheStackMatchesTheSum()
        {
            string stacked = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 10, 10 }, "FF0000")
                + Series(1, "S2", Cats, new double?[] { 30, 30 }, "00FF00"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""stacked""/><c:overlap val=""100""/>"));

            var lower = RectsOfColour(stacked, "1.000 0.000 0.000 rg").OrderBy(b => b[0]).First();
            var upper = RectsOfColour(stacked, "0.000 1.000 0.000 rg").OrderBy(b => b[0]).First();

            // The upper segment starts exactly where the lower one ends — not at the baseline,
            // which is the classic stacking bug.
            Assert.Equal(lower[1] + lower[3], upper[1], 1);
            Assert.Equal(3.0, upper[3] / lower[3], 2);   // 30 vs 10
        }

        // ------------------------------------------------------------------ line charts

        [Fact]
        public void LineChart_ThreePoints_EmitsOnePolylineThroughThem()
        {
            string pdf = Convert(ChartXml("lineChart",
                Series(0, "S1", new[] { "A", "B", "C" }, new double?[] { 10, 20, 30 }, "FF0000"),
                plotExtras: @"<c:grouping val=""standard""/>"));

            var seg = SeriesPolyline(pdf, "1.000 0.000 0.000 RG");
            Assert.Equal(3, seg.Count);
            Assert.True(seg[1].y > seg[0].y && seg[2].y > seg[1].y, "the line must ascend with the data");
        }

        [Fact]
        public void LineChart_MissingPoint_BreaksTheLineIntoTwoSubpaths()
        {
            // idx 2 absent — a sparse cache, which is how a blank cell is stored. Reading the
            // points in document order would slide 40 into slot 2 and never break the line.
            // Five categories with idx 2 absent, so BOTH sides of the gap have two points and can
            // be stroked — a run of one point is not a line and is dropped (and reported).
            string pdf = Convert(ChartXml("lineChart",
                Series(0, "S1", new[] { "A", "B", "C", "D", "E" },
                    new double?[] { 10, 20, null, 40, 50 }, "FF0000"),
                plotExtras: @"<c:grouping val=""standard""/>"));

            // A gap starts a new subpath, and each subpath is stroked separately — so the series
            // colour is set twice. Counting `m` inside one block would only ever see the first.
            Assert.Equal(2, Regex.Matches(pdf, Regex.Escape("1.000 0.000 0.000 RG")).Count);
        }

        [Fact]
        public void LineChart_WithNoGaps_IsASingleSubpath()
        {
            // The control for the test above.
            string pdf = Convert(ChartXml("lineChart",
                Series(0, "S1", new[] { "A", "B", "C", "D", "E" },
                    new double?[] { 10, 20, 30, 40, 50 }, "FF0000"),
                plotExtras: @"<c:grouping val=""standard""/>"));

            Assert.Single(Regex.Matches(pdf, Regex.Escape("1.000 0.000 0.000 RG")));
        }

        private static string ColourBlock(string pdf, string strokeOperator)
        {
            int start = pdf.IndexOf(strokeOperator, StringComparison.Ordinal);
            Assert.True(start >= 0, "the series colour was never set: " + strokeOperator);
            int end = pdf.IndexOf("\nS\n", start, StringComparison.Ordinal);
            Assert.True(end > start, "the series was never stroked");
            // Include the terminating newline, or the block's last point has no line ending and
            // the point regex silently drops it.
            return pdf.Substring(start, end - start + 1);
        }

        private static List<(double x, double y)> SeriesPolyline(string pdf, string strokeOperator)
        {
            string block = ColourBlock(pdf, strokeOperator);
            return Regex.Matches(block, @"(-?[\d.]+) (-?[\d.]+) [ml]\n")
                .Select(m => (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                              double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
                .ToList();
        }

        // ------------------------------------------------------------------ pie

        [Fact]
        public void PieChart_SlicesSweepInProportionToTheirValues()
        {
            string pdf = Convert(ChartXml("pieChart",
                Series(0, "S1", new[] { "A", "B" }, new double?[] { 75, 25 }),
                axes: ""));

            // Two filled paths, each a wedge: centre → arc → close.
            int wedges = Regex.Matches(pdf, @" m\n(?:[^m]*? c\n)+h\nf").Count;
            Assert.Equal(2, wedges);
        }

        [Fact]
        public void PieChart_UsesADistinctColourPerSlice()
        {
            string pdf = Convert(ChartXml("pieChart",
                Series(0, "S1", new[] { "A", "B", "C" }, new double?[] { 1, 1, 1 }),
                axes: ""));

            // A pie cycles the palette per POINT; per-series colouring would give one colour.
            var fills = Regex.Matches(pdf, @"([\d.]+ [\d.]+ [\d.]+) rg\n[\d.]")
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.True(fills.Count >= 3, "expected a distinct colour per slice, saw " + fills.Count);
        }

        // ------------------------------------------------------------------ axes and labels

        [Fact]
        public void ValueAxis_LabelsAreRoundNumbersCoveringTheData()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 47, 12 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            var drawn = DrawnStrings(pdf);
            Assert.Contains("0", drawn);
            Assert.Contains("50", drawn);
            Assert.DoesNotContain("47", drawn);   // the data max is not a tick
        }

        [Fact]
        public void CategoryLabels_AreDrawn()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", new[] { "Alpha", "Beta" }, new double?[] { 1, 2 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            var drawn = DrawnStrings(pdf);
            Assert.Contains("Alpha", drawn);
            Assert.Contains("Beta", drawn);
        }

        [Fact]
        public void ChartTitle_IsDrawn()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 1, 2 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/>",
                chartExtras: $@"<c:title><c:tx><c:rich xmlns:a=""{ANs}""><a:bodyPr/>
<a:p><a:r><a:t>Quarterly</a:t></a:r></a:p></c:rich></c:tx></c:title>
<c:autoTitleDeleted val=""0""/>"));

            Assert.Contains("Quarterly", DrawnStrings(pdf));
        }

        [Fact]
        public void Gridlines_AreDrawnBehindTheBars()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 10, 20 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>",
                axes: DefaultAxes.Replace("<c:axPos val=\"l\"/>",
                    "<c:axPos val=\"l\"/><c:majorGridlines/>")));

            int gridline = pdf.IndexOf("0.851 0.851 0.851 RG", StringComparison.Ordinal);
            int firstBar = pdf.IndexOf("1.000 0.000 0.000 rg", StringComparison.Ordinal);

            Assert.True(gridline >= 0, "no gridline was drawn");
            Assert.True(gridline < firstBar,
                "gridlines must be painted before the series, or they cover the bars");
        }

        [Fact]
        public void LegendEntries_AreDrawnWithTheirSeriesNames()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "Revenue", Cats, new double?[] { 1, 2 }, "FF0000")
                + Series(1, "Cost", Cats, new double?[] { 1, 2 }, "00FF00"),
                plotExtras: @"<c:barDir val=""col""/>",
                chartExtras: @"<c:legend><c:legendPos val=""r""/></c:legend>"));

            var drawn = DrawnStrings(pdf);
            Assert.Contains("Revenue", drawn);
            Assert.Contains("Cost", drawn);
        }

        // ------------------------------------------------------------------ honest fallbacks

        [Fact]
        public void ChartWithNoCachedValues_ReservesItsBoxAndReports()
        {
            // A c:numRef with no c:numCache: the numbers live only in the embedded workbook.
            string noCache = $@"<?xml version=""1.0""?>
<c:chartSpace xmlns:c=""{CNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}""><c:chart><c:plotArea><c:layout/>
<c:barChart><c:barDir val=""col""/><c:ser><c:idx val=""0""/><c:order val=""0""/>
<c:val><c:numRef><c:f>Sheet1!$B$1</c:f></c:numRef></c:val></c:ser></c:barChart>
{DefaultAxes}</c:plotArea></c:chart></c:chartSpace>";

            string tall = ToPdf(DocxWithChart(noCache, trailingParagraph: true, cy: 1828800),
                out var reports);
            string shortChart = ToPdf(DocxWithChart(noCache, trailingParagraph: true, cy: 914400));

            // Same document, differing only in the chart's height.
            Assert.Equal(72.0, LastTdY(shortChart) - LastTdY(tall), 1);
            Assert.Contains(reports, m => m.Contains("numCache"));
            Assert.Contains(reports, m => m.Contains("space was reserved"));
        }

        [Fact]
        public void UnsupportedChartType_ReservesItsBoxAndReports()
        {
            string surface = $@"<?xml version=""1.0""?>
<c:chartSpace xmlns:c=""{CNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}""><c:chart><c:plotArea><c:layout/>
<c:surface3DChart><c:ser><c:idx val=""0""/></c:ser></c:surface3DChart>
{DefaultAxes}</c:plotArea></c:chart></c:chartSpace>";

            string tall = ToPdf(DocxWithChart(surface, trailingParagraph: true, cy: 1828800),
                out var reports);
            string shortChart = ToPdf(DocxWithChart(surface, trailingParagraph: true, cy: 914400));

            Assert.Equal(72.0, LastTdY(shortChart) - LastTdY(tall), 1);
            Assert.Contains(reports, m => m.Contains("surface3DChart"));
        }

        [Fact]
        public void ThreeDimensionalBarChart_IsDrawnFlatAndReported()
        {
            var reports = new List<string>();
            void Collect(string m) => reports.Add(m);

            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try
            {
                pdf = Convert(ChartXml("bar3DChart",
                    Series(0, "S1", Cats, new double?[] { 10, 20 }, "FF0000"),
                    plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            // The data is conveyed flat rather than lost to a blank box.
            var bars = RectsOfColour(pdf, "1.000 0.000 0.000 rg");
            Assert.Equal(2, bars.Count);
            Assert.Contains(reports, m => m.Contains("approximated"));
        }

        [Fact]
        public void ChartWithAllValuesEqual_StillDrawsFiniteGeometry()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 5, 5 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:grouping val=""clustered""/>"));

            // A zero-width range would map to Infinity, which some viewers reject outright.
            Assert.DoesNotContain("Infinity", pdf);
            Assert.DoesNotContain("NaN", pdf);
            Assert.Equal(2, RectsOfColour(pdf, "1.000 0.000 0.000 rg").Count);
        }

        // ------------------------------------------------------------------ area and scatter

        [Fact]
        public void AreaChart_FillsAClosedPolygonDownToTheBaseline()
        {
            string pdf = Convert(ChartXml("areaChart",
                Series(0, "S1", new[] { "A", "B", "C" }, new double?[] { 10, 20, 15 }, "FF0000"),
                plotExtras: @"<c:grouping val=""standard""/>"));

            // Filled, not stroked: an area is a region, and the region is bounded by zero.
            int start = pdf.IndexOf("1.000 0.000 0.000 rg", StringComparison.Ordinal);
            Assert.True(start >= 0, "the area series was never filled");

            // Scope to the polygon itself — a fixed-size window would pick up axis lines and
            // gridlines and count their operators as part of the shape.
            int end = pdf.IndexOf("h\nf\n", start, StringComparison.Ordinal);
            Assert.True(end > start, "the area path was never closed and filled");
            string block = pdf.Substring(start, end - start);

            var points = Regex.Matches(block, @"(-?[\d.]+) (-?[\d.]+) [ml]\n")
                .Select(m => (x: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                              y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
                .ToList();

            // Three data points plus the two baseline corners that close the polygon.
            Assert.Equal(5, points.Count);

            // The first and last vertices sit on the baseline, level with each other — that is
            // what makes the filled region "between the data and zero" rather than a stroked line.
            Assert.Equal(points[0].y, points[points.Count - 1].y, 1);
            Assert.True(points.Skip(1).Take(3).All(p => p.y > points[0].y),
                "every data vertex must sit above the baseline the polygon closes to");
        }

        [Fact]
        public void LineChart_IsStrokedNotFilled()
        {
            // The control for the area test: a line is a path, not a region.
            string pdf = Convert(ChartXml("lineChart",
                Series(0, "S1", new[] { "A", "B", "C" }, new double?[] { 10, 20, 15 }, "FF0000"),
                plotExtras: @"<c:grouping val=""standard""/>"));

            Assert.Contains("1.000 0.000 0.000 RG", pdf);          // stroke colour
            Assert.DoesNotContain("1.000 0.000 0.000 rg", pdf);    // never a fill
        }

        [Fact]
        public void ScatterChart_PositionsPointsByTheirOwnXValues()
        {
            // x = 1, 10 with y = 5, 5: the two markers must be far apart horizontally and level
            // vertically — which only holds if x came from c:xVal rather than the point index.
            string scatter = $@"<c:ser><c:idx val=""0""/><c:order val=""0""/>
<c:spPr><a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill></c:spPr>
<c:xVal><c:numRef><c:f>S!$A$1</c:f><c:numCache><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>1</c:v></c:pt><c:pt idx=""1""><c:v>10</c:v></c:pt></c:numCache></c:numRef></c:xVal>
<c:yVal><c:numRef><c:f>S!$B$1</c:f><c:numCache><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>5</c:v></c:pt><c:pt idx=""1""><c:v>5</c:v></c:pt></c:numCache></c:numRef></c:yVal></c:ser>";

            string axes = @"
<c:valAx><c:axId val=""1""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""b""/><c:crossAx val=""2""/></c:valAx>
<c:valAx><c:axId val=""2""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""l""/><c:crossAx val=""1""/></c:valAx>";

            string pdf = Convert(ChartXml("scatterChart", scatter, axes: axes));

            // Markers are round: each is a moveto plus four curves.
            var markers = Regex.Matches(pdf,
                    @"1\.000 0\.000 0\.000 rg\n(-?[\d.]+) (-?[\d.]+) m\n")
                .Select(m => (x: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                              y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
                .ToList();

            Assert.Equal(2, markers.Count);
            Assert.Equal(markers[0].y, markers[1].y, 1);
            Assert.True(Math.Abs(markers[0].x - markers[1].x) > 50,
                "x = 1 and x = 10 must land far apart, i.e. positioned by value not by index");
        }

        [Fact]
        public void ScatterChart_WithNoXCache_SpacesByIndexAndReports()
        {
            string noX = $@"<c:ser><c:idx val=""0""/><c:order val=""0""/>
<c:spPr><a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill></c:spPr>
<c:yVal><c:numRef><c:f>S!$B$1</c:f><c:numCache><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>5</c:v></c:pt><c:pt idx=""1""><c:v>8</c:v></c:pt></c:numCache></c:numRef></c:yVal></c:ser>";

            var reports = new List<string>();
            void Collect(string m) => reports.Add(m);
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try { pdf = Convert(ChartXml("scatterChart", noX)); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            // Visible rather than silently absent, and the substitution is stated.
            Assert.Contains("1.000 0.000 0.000 rg", pdf);
            Assert.Contains(reports, m => m.Contains("xVal"));
        }

        // ------------------------------------------------------------------ bubble

        /// <summary>
        /// A bubble series: x/y like a scatter, plus <c>c:bubbleSize</c>. Sizes 1 and 4 at the same
        /// y, far apart in x.
        /// </summary>
        private static string BubbleSeries(string sizes) => $@"<c:ser><c:idx val=""0""/><c:order val=""0""/>
<c:spPr><a:solidFill><a:srgbClr val=""FF0000""/></a:solidFill></c:spPr>
<c:xVal><c:numRef><c:f>S!$A$1</c:f><c:numCache><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>1</c:v></c:pt><c:pt idx=""1""><c:v>10</c:v></c:pt></c:numCache></c:numRef></c:xVal>
<c:yVal><c:numRef><c:f>S!$B$1</c:f><c:numCache><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>5</c:v></c:pt><c:pt idx=""1""><c:v>5</c:v></c:pt></c:numCache></c:numRef></c:yVal>
{sizes}</c:ser>";

        private const string BubbleSizes1And4 = @"<c:bubbleSize><c:numRef><c:f>S!$C$1</c:f>
<c:numCache><c:ptCount val=""2""/>
<c:pt idx=""0""><c:v>1</c:v></c:pt><c:pt idx=""1""><c:v>4</c:v></c:pt></c:numCache></c:numRef></c:bubbleSize>";

        private const string TwoValueAxes = @"
<c:valAx><c:axId val=""1""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""b""/><c:crossAx val=""2""/></c:valAx>
<c:valAx><c:axId val=""2""/><c:scaling><c:orientation val=""minMax""/></c:scaling>
<c:delete val=""0""/><c:axPos val=""l""/><c:crossAx val=""1""/></c:valAx>";

        /// <summary>
        /// Radii of the round markers, recovered from the emitted path. A marker starts at
        /// (X + half, Y) and its second curve ends at (X, Y + half), so the radius is the
        /// difference of the two x values.
        /// </summary>
        private static List<double> MarkerRadii(string pdf)
        {
            var radii = new List<double>();
            foreach (Match m in Regex.Matches(pdf,
                @"1\.000 0\.000 0\.000 rg\n(-?[\d.]+) (-?[\d.]+) m\n" +
                @"(?:-?[\d.]+ ){4}(-?[\d.]+) (-?[\d.]+) c\n"))
            {
                double startX = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double topX   = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                radii.Add(startX - topX);
            }
            return radii;
        }

        [Fact]
        public void BubbleChart_SizesMarkersByArea_NotByRadius()
        {
            // c:sizeRepresents defaults to "area" (ECMA-376 §21.2.2.180), so a size of 4 against a
            // size of 1 is FOUR times the area and therefore TWICE the radius — not four times it.
            // Scaling the radius linearly is the classic bubble exaggeration, and is what this
            // pins against.
            string pdf = Convert(ChartXml("bubbleChart",
                BubbleSeries(BubbleSizes1And4), axes: TwoValueAxes));

            var radii = MarkerRadii(pdf);
            Assert.Equal(2, radii.Count);

            double small = Math.Min(radii[0], radii[1]);
            double large = Math.Max(radii[0], radii[1]);
            Assert.True(small > 0, $"both bubbles must be drawn; got radii {radii[0]}, {radii[1]}");

            // sqrt(4/1) = 2. Generous tolerance: the small bubble may be lifted by the minimum
            // diameter floor, which can only pull the ratio DOWN, never up towards 4.
            Assert.InRange(large / small, 1.7, 2.6);
        }

        [Fact]
        public void BubbleChart_WithoutSizes_DrawsPointsAndReports()
        {
            // A bubble with no size is not a bubble. It must still be locatable — an invisible
            // point reads as missing data — and the substitution has to be stated.
            var reports = new List<string>();
            void Collect(string m) => reports.Add(m);
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try
            {
                pdf = Convert(ChartXml("bubbleChart", BubbleSeries(sizes: ""), axes: TwoValueAxes));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            Assert.Equal(2, MarkerRadii(pdf).Count);
            Assert.Contains(reports, m => m.Contains("bubbleSize"));
        }

        [Fact]
        public void ScatterChart_IgnoresBubbleSizes_AndKeepsAUniformMarker()
        {
            // The control that stops the bubble path from capturing scatter: the SAME sizes on a
            // scatterChart must leave both markers identical. Without it, sizing every MarkerItem
            // would satisfy the two assertions above while silently changing every scatter plot.
            string pdf = Convert(ChartXml("scatterChart",
                BubbleSeries(BubbleSizes1And4), axes: TwoValueAxes));

            var radii = MarkerRadii(pdf);
            Assert.Equal(2, radii.Count);
            Assert.Equal(radii[0], radii[1], 2);
        }

        // ------------------------------------------------------------------ data labels

        // 13 and 27 are deliberately NOT tick values: the axis runs 0–30 in steps of 5, so these
        // strings can only appear on the page as data labels. With 10 and 20 the positive test
        // would pass from the axis alone — which is what its control caught.
        private static readonly double?[] NonTickValues = { 13, 27 };

        [Fact]
        public void ShowValLabels_DrawEachBarsValue()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, NonTickValues, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/><c:dLbls><c:showVal val=""1""/></c:dLbls>"));

            var drawn = DrawnStrings(pdf);
            Assert.Contains("13", drawn);
            Assert.Contains("27", drawn);
        }

        [Fact]
        public void WithoutShowVal_NoDataLabelsAreDrawn()
        {
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, NonTickValues, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/>"));

            var drawn = DrawnStrings(pdf);
            Assert.DoesNotContain("13", drawn);
            Assert.DoesNotContain("27", drawn);
        }

        // ------------------------------------------------------------------ rotation and caps

        [Fact]
        public void RotatedCategoryLabels_UseATextMatrixWithTheCorrectSign()
        {
            // DrawingML rot is CLOCKWISE; PDF's text matrix is counter-clockwise. -2700000 is 45°
            // anticlockwise in DrawingML terms, so PDF must see +45°: a = d = cos45, b = +sin45.
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 1, 2 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/>",
                axes: DefaultAxes.Replace("<c:crossAx val=\"2\"/>",
                    $@"<c:txPr><a:bodyPr xmlns:a=""{ANs}"" rot=""-2700000""/></c:txPr><c:crossAx val=""2""/>")));

            var matrices = Regex.Matches(pdf,
                    @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm")
                .Select(m => Enumerable.Range(1, 4)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();

            double c = Math.Sqrt(2) / 2;
            Assert.Contains(matrices, m => Math.Abs(m[0] - c) < 0.01 && Math.Abs(m[1] - c) < 0.01);
        }

        [Fact]
        public void UnrotatedCategoryLabels_UseAPlainTextPosition()
        {
            // The control: without a stated rotation the labels must not acquire a text matrix,
            // so a renderer that always rotated could not pass both this and the test above.
            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", Cats, new double?[] { 1, 2 }, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/>"));

            // The only rotated text in an unrotated chart would be a value-axis title, and this
            // fixture has none.
            Assert.DoesNotMatch(@"[\d.]+ [\d.]+ [\d.]+ [\d.]+ [\d.]+ [\d.]+ Tm", pdf);
        }

        [Fact]
        public void ManyCategories_SkipLabelsRatherThanTruncateThem()
        {
            var cats = Enumerable.Range(0, 40)
                .Select(i => "LongCategoryName" + i).ToArray();
            var values = Enumerable.Range(0, 40).Select(i => (double?)(i + 1)).ToArray();

            string pdf = Convert(ChartXml("barChart",
                Series(0, "S1", cats, values, "FF0000"),
                plotExtras: @"<c:barDir val=""col""/>"));

            var drawn = DrawnStrings(pdf);
            var drawnCategories = drawn.Where(s => s.StartsWith("LongCategoryName", StringComparison.Ordinal)).ToList();

            Assert.True(drawnCategories.Count < 40, "40 long labels cannot all fit; some must be skipped");
            Assert.NotEmpty(drawnCategories);
            // Every label that IS drawn must be whole — never an ellipsis or a fragment.
            Assert.All(drawnCategories, s => Assert.Contains(s, cats));
        }

        [Fact]
        public void SeriesWithAbsurdlyManyPoints_IsDecimatedAndReported()
        {
            const int count = 60000;
            var cats = new string[count];
            var values = new double?[count];
            for (int i = 0; i < count; i++) { cats[i] = "c"; values[i] = i % 100; }

            var reports = new List<string>();
            void Collect(string m) => reports.Add(m);
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try
            {
                pdf = Convert(ChartXml("lineChart",
                    Series(0, "S1", cats, values, "FF0000"),
                    plotExtras: @"<c:grouping val=""standard""/>"));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            // The cap is on the emitted geometry, not on a stopwatch.
            int segments = Regex.Matches(pdf, @"[\d.]+ [\d.]+ l\n").Count;
            Assert.True(segments <= Tetitulz.Pdf.Core.Constants.PdfLimits.MaxChartPointsPerSeries,
                "the point cap must bound what a crafted cache can emit; saw " + segments);
            Assert.Contains(reports, m => m.Contains("cap"));
        }

        // ------------------------------------------------------------------ c:dispBlanksAs

        /// <summary>How many line segments the longest stroked polyline in the content has.</summary>
        private static int LongestPolylineSegments(string pdf)
            => Regex.Matches(pdf, @" m\n((?:-?[\d.]+ -?[\d.]+ l\n)+)S\n")
                .Select(m => Regex.Matches(m.Groups[1].Value, @" l\n").Count)
                .DefaultIfEmpty(0)
                .Max();

        [Fact]
        public void DispBlanksAsSpan_JoinsTheLineAcrossTheHole_InsteadOfBreakingIt()
        {
            // c:dispBlanksAs is a child of c:chart. Nothing ever read it, so DisplayBlanksAs stayed
            // at its "gap" default and two of the three branches in LayoutLines were unreachable:
            // "span" (join across the hole, what Excel draws) and "zero".
            string plot = @"<c:grouping val=""standard""/>";
            var cats = new[] { "A", "B", "C", "D" };
            var values = new double?[] { 10, null, 20, 30 };

            string span = Convert(ChartXml("lineChart", Series(0, "S1", cats, values, "FF0000"),
                plotExtras: plot, chartExtras: @"<c:dispBlanksAs val=""span""/>"));
            string gap = Convert(ChartXml("lineChart", Series(0, "S1", cats, values, "FF0000"),
                plotExtras: plot, chartExtras: @"<c:dispBlanksAs val=""gap""/>"));

            // Span: one unbroken subpath through all three plotted points — two segments.
            Assert.Equal(2, LongestPolylineSegments(span));

            // Gap: the hole splits it, so the longest surviving subpath is the two-point run
            // after it — one segment. (The single point before the hole is not a line at all.)
            Assert.Equal(1, LongestPolylineSegments(gap));
        }

        private static double LastTdY(string pdf)
        {
            var ys = Regex.Matches(pdf, @"-?[\d.]+ (-?[\d.]+) Td")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();
            Assert.NotEmpty(ys);
            return ys.Last();
        }
    }
}

