// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.DocxToPdf.Rendering.Charts;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// <see cref="ChartPartReader"/> read directly from an in-memory chart part, at the places the
/// overnight sweep found no test: literal data, multi-level categories, the schema's boolean
/// default, unparseable axis bounds, and a few edges.
/// </summary>
public class ChartPartReaderTests
{
    private const string CNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>Every <c>c:spPr</c> is red: enough to see which points got a colour.</summary>
    private sealed class RedFills : DrawingColorSource
    {
        public string SolidFillHexOf(OpenXmlElement spPr) => spPr == null ? null : "FF0000";
    }

    private static ChartModel Read(string chartInner, DrawingColorSource colors = null)
    {
        string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><c:chartSpace xmlns:c=\"{CNs}\" " +
                     $"xmlns:a=\"{ANs}\"><c:chart>{chartInner}</c:chart></c:chartSpace>";
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var part = main.AddNewPart<ChartPart>();
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(xml))) part.FeedData(s);

        Assert.True(ChartPartReader.TryRead(part, colors, out var model), "the chart was not read");
        return model;
    }

    private static ChartModel Bar(string barInner, string chartBefore = "")
        => Read(chartBefore + "<c:plotArea><c:barChart>" + barInner + "</c:barChart></c:plotArea>");

    /// <summary>
    /// Every series' caches draw on one chart-wide budget of points (four times
    /// <c>MaxChartDataPoints</c>): each array was capped and the series count was capped, and their
    /// product was not — a thousand series each declaring a hundred thousand points and supplying one
    /// allocated gigabytes. The first series is still read whole (the control).
    /// </summary>
    [Fact]
    public void AdversarialManySeriesDeclaringManyPoints_ShareOneBudget()
    {
        string series = string.Concat(Enumerable.Range(0, 1000).Select(i =>
            "<c:ser><c:val><c:numLit><c:ptCount val=\"100000\"/><c:pt idx=\"0\"><c:v>" + i + "</c:v></c:pt></c:numLit></c:val></c:ser>"));
        var model = Bar(series);

        long allocated = model.Series.Sum(s => (long)(s.Values?.Length ?? 0));
        Assert.True(allocated <= 4L * PdfLimits.MaxChartDataPoints, $"{allocated} points allocated");
        Assert.Equal(System.Math.Min(100000, PdfLimits.MaxChartDataPoints), model.Series[0].Values.Length);
        Assert.Equal(0, model.Series[0].Values[0]);
    }

    /// <summary>A chart without a plot area is not a chart: nothing is read.</summary>
    [Fact]
    public void AChartWithNoPlotAreaIsNotRead()
    {
        string xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><c:chartSpace xmlns:c=\"{CNs}\"><c:chart/></c:chartSpace>";
        using var ms = new MemoryStream();
        using var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var part = main.AddNewPart<ChartPart>();
        using (var s = new MemoryStream(Encoding.UTF8.GetBytes(xml))) part.FeedData(s);

        Assert.False(ChartPartReader.TryRead(part, null, out _));
    }

    /// <summary><c>c:barDir val="bar"</c> is a horizontal bar chart; without it, columns.</summary>
    [Fact]
    public void BarDirectionIsRead()
    {
        Assert.Equal(BarDirection.Bar, Bar("<c:barDir val=\"bar\"/>").BarDir);
        Assert.Equal(BarDirection.Column, Bar("").BarDir);
    }

    /// <summary>
    /// The upper end of <c>c:gapWidth</c>'s range (500) is kept as stated; a doughnut that states
    /// no <c>c:holeSize</c> gets the schema default, 50.
    /// </summary>
    [Fact]
    public void RangeEndsAndDefaultsAreTheSchemas()
    {
        Assert.Equal(500d, Bar("<c:gapWidth val=\"500\"/>").GapWidth);
        Assert.Equal(50d, Read("<c:plotArea><c:doughnutChart/></c:plotArea>").HoleSize);
    }

    /// <summary>
    /// Data given as literals (<c>c:numLit</c>, <c>c:strLit</c>) rather than as cached references
    /// is read the same way.
    /// </summary>
    [Fact]
    public void LiteralDataIsRead()
    {
        var m = Bar("<c:ser>" +
            "<c:cat><c:strLit><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>A</c:v></c:pt><c:pt idx=\"1\"><c:v>B</c:v></c:pt></c:strLit></c:cat>" +
            "<c:val><c:numLit><c:ptCount val=\"2\"/><c:pt idx=\"0\"><c:v>3</c:v></c:pt><c:pt idx=\"1\"><c:v>4</c:v></c:pt></c:numLit></c:val>" +
            "</c:ser>");

        Assert.Equal(new double?[] { 3, 4 }, m.Series.Single().Values);
        Assert.Equal(new[] { "A", "B" }, m.Categories);
    }

    /// <summary>
    /// Multi-level category labels (<c>c:multiLvlStrCache</c>) give the leaf level, the last
    /// <c>c:lvl</c>.
    /// </summary>
    [Fact]
    public void MultiLevelCategoriesGiveTheLeafLevel()
    {
        var m = Bar("<c:ser><c:cat><c:multiLvlStrRef><c:f>Sheet1!A1:B2</c:f><c:multiLvlStrCache>" +
                    "<c:ptCount val=\"2\"/>" +
                    "<c:lvl><c:pt idx=\"0\"><c:v>Q1</c:v></c:pt><c:pt idx=\"1\"><c:v>Q2</c:v></c:pt></c:lvl>" +
                    "<c:lvl><c:pt idx=\"0\"><c:v>2025</c:v></c:pt><c:pt idx=\"1\"><c:v>2026</c:v></c:pt></c:lvl>" +
                    "</c:multiLvlStrCache></c:multiLvlStrRef></c:cat>" +
                    "<c:val><c:numLit><c:pt idx=\"0\"><c:v>1</c:v></c:pt></c:numLit></c:val></c:ser>");

        Assert.Equal(new[] { "2025", "2026" }, m.Categories);
    }

    /// <summary>
    /// A boolean element with no <c>val</c> is true — <c>CT_Boolean</c>'s default — and
    /// <c>val="0"</c> is false.
    /// </summary>
    [Fact]
    public void ABooleanWithNoValueIsTrue()
    {
        Assert.True(Bar("", "<c:autoTitleDeleted/>").AutoTitleDeleted);
        Assert.False(Bar("", "<c:autoTitleDeleted val=\"0\"/>").AutoTitleDeleted);
    }

    /// <summary>
    /// A plot area holding two chart groups draws the first and says so of the second: its series are
    /// not drawn, and the value axis it names (<c>c:axId</c>) does not replace the first group's. The
    /// control is the first group's own axis, read as before.
    /// </summary>
    [Fact]
    public void ASecondChartGroup_IsReported_AndItsValueAxisIsNotTheFirstGroups()
    {
        Tetitulz.Pdf.DocxToPdf.Rendering.DocxDiagnostics.Clear();
        var m = Read("<c:plotArea>" +
            "<c:barChart><c:ser><c:val><c:numLit><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>4</c:v></c:pt></c:numLit></c:val></c:ser>" +
            "<c:axId val=\"1\"/><c:axId val=\"2\"/></c:barChart>" +
            "<c:lineChart><c:ser><c:val><c:numLit><c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>900</c:v></c:pt></c:numLit></c:val></c:ser>" +
            "<c:axId val=\"1\"/><c:axId val=\"3\"/></c:lineChart>" +
            "<c:catAx><c:axId val=\"1\"/></c:catAx>" +
            "<c:valAx><c:axId val=\"2\"/><c:scaling><c:max val=\"10\"/></c:scaling></c:valAx>" +
            "<c:valAx><c:axId val=\"3\"/><c:scaling><c:max val=\"1000\"/></c:scaling></c:valAx></c:plotArea>");

        Assert.Single(m.Series);
        Assert.Equal(4d, m.Series[0].Values[0]);
        Assert.Equal(10d, m.ValueAxis.Max);
        Assert.Contains("lineChart", DocxToPdfConverter.LastDiagnostic ?? "", System.StringComparison.Ordinal);
    }

    /// <summary>An axis bound that is not a number is no bound, not zero; a number beside it still reads.</summary>
    [Fact]
    public void AnUnparseableAxisBoundIsNone()
    {
        var m = Read("<c:plotArea><c:barChart/><c:valAx><c:axId val=\"1\"/>" +
                     "<c:scaling><c:min val=\"abc\"/><c:max val=\"9\"/></c:scaling></c:valAx></c:plotArea>");

        Assert.Null(m.ValueAxis.Min);
        Assert.Equal(9d, m.ValueAxis.Max);
    }

    /// <summary>
    /// A series name whose cache holds no points falls back to the literal <c>c:v</c> beside it.
    /// </summary>
    [Fact]
    public void ASeriesNameWithAnEmptyCacheFallsBackToTheLiteral()
    {
        var m = Bar("<c:ser><c:tx><c:strRef><c:f>Sheet1!A1</c:f><c:strCache><c:ptCount val=\"0\"/></c:strCache></c:strRef>" +
                    "<c:v>Revenue</c:v></c:tx></c:ser>");

        Assert.Equal("Revenue", m.Series.Single().Name);
    }

    /// <summary>
    /// A point colour at exactly the data-point cap is past the kept range and is dropped, not
    /// written one past the end of the array.
    /// </summary>
    [Fact]
    public void APointColourAtTheCapIsDropped()
    {
        int cap = PdfLimits.MaxChartDataPoints;
        var m = Read("<c:plotArea><c:barChart><c:ser>" +
                     $"<c:dPt><c:idx val=\"0\"/><c:spPr/></c:dPt><c:dPt><c:idx val=\"{cap}\"/><c:spPr/></c:dPt>" +
                     "</c:ser></c:barChart></c:plotArea>", new RedFills());

        var colours = m.Series.Single().PointColors;
        Assert.Equal(cap, colours.Length);
        Assert.Equal("FF0000", colours[0]);
    }

    /// <summary>An empty numeric cache is no data, not one blank point.</summary>
    [Fact]
    public void AnEmptyNumericCacheIsNoData()
    {
        var m = Bar("<c:ser><c:val><c:numRef><c:f>Sheet1!B1</c:f><c:numCache/></c:numRef></c:val></c:ser>");
        Assert.Empty(m.Series.Single().Values);
    }
}
