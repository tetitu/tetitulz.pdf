// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.DocxToPdf;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A chart series cache must not be allowed to size an allocation from numbers the <b>file</b>
/// chooses.
///
/// <para><b>The defect these guard.</b> <c>ReadNumericCache</c> sized its array from
/// <c>Math.Max(ptCount, maxIdx)</c> and <c>ReadPointColors</c> from one past the largest
/// <c>idx</c> attribute — both straight from the DOCX. A crafted
/// <c>&lt;c:ptCount val="2000000000"/&gt;</c> asks for ~32 GB of <c>double?</c> before a single
/// value is read; a single <c>&lt;c:dPt&gt;&lt;c:idx val="2000000000"/&gt;</c> asks for ~16 GB of
/// string references. Both from files containing no chart data at all.</para>
///
/// <para><b>Asserted on the guard's diagnostic, not on a stopwatch.</b> GAPS.md §8a records
/// wall-clock assertions in DoS tests as a cause of flaky whole-solution runs, and an earlier draft
/// of this file made that mistake: with the cap removed the malicious fixture took 25 s and still
/// came in under a 30 s budget, so the test passed against the broken code. The truncation
/// diagnostic fires exactly when the cap engages, which is the thing under test and is
/// deterministic.</para>
///
/// <para>All three routes into an unbounded allocation are covered: a declared <c>ptCount</c>, a
/// large value <c>idx</c>, and a large point-colour <c>idx</c> — the last being a separate method
/// with its own bound.</para>
/// </summary>
[Collection(DiagnosticsCollection.Name)]
public class ChartDataPointBudgetTests
{
    private const string CNs = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string WpNs = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private const string ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>
    /// A DOCX with a chart that is <b>actually drawn</b> — adding the part alone is not enough.
    /// </summary>
    /// <remarks>
    /// The first version of this fixture added a <c>ChartPart</c> and never referenced it from the
    /// body, so no drawing invoked it, <c>ChartPartReader</c> never ran, and every test here passed
    /// against the unfixed code in single-digit milliseconds. The chart must be reached through a
    /// <c>w:drawing</c> carrying the part's relationship id, as Word writes it. When one of these
    /// tests starts passing suspiciously fast, check that again first.
    /// </remarks>
    private static byte[] DocxWithChart(string seriesXml)
    {
        string chartXml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<c:chartSpace xmlns:c=\"{CNs}\" xmlns:a=\"{ANs}\" xmlns:r=\"{RNs}\">" +
            "<c:chart><c:plotArea><c:barChart>" + seriesXml +
            "</c:barChart></c:plotArea></c:chart></c:chartSpace>";

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body());

            var chartPart = main.AddNewPart<ChartPart>();
            using (var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(chartXml)))
                chartPart.FeedData(s);
            string relId = main.GetIdOfPart(chartPart);

            main.Document.Body.Append(new Paragraph(new Run(new Drawing(
                $@"<w:drawing xmlns:w=""{WNs}"" xmlns:wp=""{WpNs}"" xmlns:a=""{ANs}"" xmlns:r=""{RNs}"">
                   <wp:inline><wp:extent cx=""5486400"" cy=""3657600""/>
                   <wp:docPr id=""1"" name=""Chart 1""/>
                   <a:graphic><a:graphicData uri=""{CNs}"">
                   <c:chart xmlns:c=""{CNs}"" xmlns:r=""{RNs}"" r:id=""{relId}""/>
                   </a:graphicData></a:graphic></wp:inline></w:drawing>"))));

            main.Document.Save();
        }
        return ms.ToArray();
    }

    /// <summary>Converts, collecting every diagnostic raised during the conversion.</summary>
    private static List<string> ConvertCollectingDiagnostics(byte[] docx)
    {
        var seen = new List<string>();
        void Sink(string m) { lock (seen) seen.Add(m); }

        DocxToPdfConverter.DiagnosticReported += Sink;
        try
        {
            using var input = new MemoryStream(docx);
            using var output = new MemoryStream();
            DocxToPdfConverter.Convert(input, output);
        }
        finally { DocxToPdfConverter.DiagnosticReported -= Sink; }

        return seen;
    }

    /// <summary>The truncation report, whatever the exact wording.</summary>
    private static bool IsTruncationReport(string m) =>
        m.Contains("only the first") && m.Contains("data points");

    [Fact]
    public void AnAbsurdDeclaredPointCount_IsCappedAndReported()
    {
        var seen = ConvertCollectingDiagnostics(DocxWithChart(
            "<c:ser><c:val><c:numRef><c:numCache>" +
            "<c:ptCount val=\"2000000000\"/>" +
            "<c:pt idx=\"0\"><c:v>1</c:v></c:pt>" +
            "</c:numCache></c:numRef></c:val></c:ser>"));

        Assert.Contains(seen, IsTruncationReport);
    }

    /// <summary>
    /// The other route into the same allocation: one point with an enormous <c>idx</c>, so
    /// <c>maxIdx</c> is huge even though <c>ptCount</c> is absent.
    /// </summary>
    /// <remarks>
    /// The reported figure is <c>idx + 1</c>, not <c>idx</c> — an earlier version of this test
    /// asserted the literal <c>"2000000000"</c> and failed against correct code for that reason.
    /// Matching on the shape of the message rather than on an arithmetic coincidence.
    /// </remarks>
    [Fact]
    public void AnAbsurdPointIndex_IsCappedAndReported()
    {
        var seen = ConvertCollectingDiagnostics(DocxWithChart(
            "<c:ser><c:val><c:numRef><c:numCache>" +
            "<c:pt idx=\"2000000000\"><c:v>1</c:v></c:pt>" +
            "</c:numCache></c:numRef></c:val></c:ser>"));

        Assert.Contains(seen, IsTruncationReport);
    }

    /// <summary>
    /// The control. An ordinary chart must convert with <b>no</b> truncation diagnostic — without
    /// it, a guard that reported unconditionally would satisfy every test above and the reports
    /// would distinguish nothing.
    /// </summary>
    [Fact]
    public void AnOrdinaryChart_IsNotTruncatedAndReportsNothing()
    {
        var seen = ConvertCollectingDiagnostics(DocxWithChart(
            "<c:ser><c:val><c:numRef><c:numCache>" +
            "<c:ptCount val=\"3\"/>" +
            "<c:pt idx=\"0\"><c:v>1</c:v></c:pt>" +
            "<c:pt idx=\"1\"><c:v>2</c:v></c:pt>" +
            "<c:pt idx=\"2\"><c:v>3</c:v></c:pt>" +
            "</c:numCache></c:numRef></c:val></c:ser>"));

        Assert.DoesNotContain(seen, IsTruncationReport);
        Assert.DoesNotContain(seen, IsColourTruncationReport);
    }

    /// <summary>The point-colour truncation report, whatever the exact wording.</summary>
    private static bool IsColourTruncationReport(string m) =>
        m.Contains("point-colour") && m.Contains("only the first");

    /// <summary>
    /// The point-colour array is sized from an <c>idx</c> on <c>c:dPt</c> — a separate code path
    /// from the value cache, and separately unbounded before the fix.
    /// </summary>
    /// <remarks>
    /// An earlier pass concluded this path was untestable because <c>ReadPointColors</c> returns
    /// early without a <c>DrawingColorSource</c>. That was wrong: <c>ChartRenderer</c> implements
    /// the interface and passes itself, so the source is never null in a real conversion. The test
    /// had actually failed for the same reason as its numeric sibling — it asserted the literal
    /// <c>2000000000</c> while the report says <c>2000000001</c>. Diagnosing a red test as
    /// "unreachable code" is a comfortable answer and was the wrong one.
    ///
    /// <para>The fixture needs an explicit <c>a:srgbClr</c>: a colour that resolves to null leaves
    /// <c>byIndex</c> empty, <c>max</c> at zero, and the method returns before reaching the cap —
    /// so a theme-dependent colour really would make this vacuous.</para>
    /// </remarks>
    [Fact]
    public void AnAbsurdDataPointColourIndex_IsCappedAndReported()
    {
        var seen = ConvertCollectingDiagnostics(DocxWithChart(
            "<c:ser>" +
            "<c:dPt><c:idx val=\"2000000000\"/>" +
            "<c:spPr><a:solidFill><a:srgbClr val=\"FF0000\"/></a:solidFill></c:spPr></c:dPt>" +
            "<c:val><c:numRef><c:numCache>" +
            "<c:ptCount val=\"1\"/><c:pt idx=\"0\"><c:v>1</c:v></c:pt>" +
            "</c:numCache></c:numRef></c:val></c:ser>"));

        Assert.Contains(seen, IsColourTruncationReport);
    }


    /// <summary>The cap is a real bound, not an accidentally-huge one.</summary>
    [Fact]
    public void TheCapIsBoundedWellBelowAnAllocationThatWouldHurt()
    {
        Assert.True(PdfLimits.MaxChartDataPoints > 0);
        Assert.True(PdfLimits.MaxChartDataPoints <= 1_000_000,
            $"MaxChartDataPoints is {PdfLimits.MaxChartDataPoints}; at 16 bytes per double? that is " +
            "no longer a bound worth having");
    }

    /// <summary>The label truncation report, whatever the exact wording.</summary>
    private static bool IsLabelTruncationReport(string m) =>
        m.Contains("chart labels") && m.Contains("only the first");

    /// <summary>
    /// The category label cache is sized from a <c>c:pt/@idx</c> too, and was the one cache left without
    /// the clamp its numeric and point-colour siblings carry: a single <c>&lt;c:pt idx="2000000000"/&gt;</c>
    /// filled a two-billion-entry list of strings.
    /// </summary>
    [Fact]
    public void AnAbsurdCategoryLabelIndex_IsCappedAndReported()
    {
        var seen = ConvertCollectingDiagnostics(DocxWithChart(
            "<c:ser><c:cat><c:strRef><c:strCache>" +
            "<c:pt idx=\"2000000000\"><c:v>x</c:v></c:pt>" +
            "</c:strCache></c:strRef></c:cat>" +
            "<c:val><c:numRef><c:numCache><c:ptCount val=\"1\"/>" +
            "<c:pt idx=\"0\"><c:v>1</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>"));

        Assert.Contains(seen, IsLabelTruncationReport);
    }

    /// <summary>The series-count truncation report, whatever the exact wording.</summary>
    private static bool IsSeriesTruncationReport(string m) =>
        m.Contains("series") && m.Contains("were not read");

    /// <summary>
    /// The number of series multiplies the per-series point cap, so it is bounded too: each series past
    /// <see cref="PdfLimits.MaxChartSeries"/> is dropped and the drop reported. Twenty thousand empty
    /// series each declaring a hundred-thousand-point cache allocated tens of gigabytes before any
    /// decimation ran. The fixture holds one past the cap, so the limit stays at its default and no
    /// parallel test sees it move.
    /// </summary>
    [Fact]
    public void MoreSeriesThanTheCap_AreDroppedAndReported()
    {
        var series = new System.Text.StringBuilder();
        for (int i = 0; i <= PdfLimits.MaxChartSeries; i++)
            series.Append("<c:ser><c:val><c:numRef><c:numCache><c:ptCount val=\"1\"/>" +
                          "<c:pt idx=\"0\"><c:v>1</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>");

        var seen = ConvertCollectingDiagnostics(DocxWithChart(series.ToString()));

        Assert.Contains(seen, IsSeriesTruncationReport);
    }

    /// <summary>The control: a chart within the series cap reports no series truncation.</summary>
    [Fact]
    public void FewerSeriesThanTheCap_ReportNoSeriesTruncation()
    {
        var seen = ConvertCollectingDiagnostics(DocxWithChart(
            "<c:ser><c:val><c:numRef><c:numCache><c:ptCount val=\"1\"/>" +
            "<c:pt idx=\"0\"><c:v>1</c:v></c:pt></c:numCache></c:numRef></c:val></c:ser>"));

        Assert.DoesNotContain(seen, IsSeriesTruncationReport);
    }
}
