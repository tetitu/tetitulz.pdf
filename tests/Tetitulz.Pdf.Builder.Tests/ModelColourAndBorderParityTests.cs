// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// One model object must not mean two different things to two exporters.
/// </summary>
/// <remarks>
/// <para><b>Colour.</b> <c>Color = "ff0000"</c> — no leading <c>#</c>, the form
/// <c>ExportDetailTests</c> already pins as valid for the PDF path — drew red in the PDF and
/// exported as default <b>black</b> in ODT, because the ODT parser required the <c>#</c> and its
/// caller drops <c>fo:color</c> when the parse returns null. The grammars now agree.</para>
///
/// <para><b>Borders.</b> A <c>Border</c> with <c>Width = 0</c> and <c>None</c> left false is legal
/// — <c>Border.Width</c> is a public settable double. <c>TableRenderer.Edge</c> tested
/// <c>Width &lt;= 0</c> and emitted nothing; the paragraph and page-border sites had no width test,
/// so <c>Math.Max(0.2, 0)</c> produced a visible 0.2 pt hairline. The same object was invisible on
/// a table cell and visible on a paragraph. Zero width is now an <b>absent</b> border everywhere:
/// <c>Math.Max(0.2, …)</c> exists to keep a thin border visible, not to materialise one the model
/// does not ask for.</para>
/// </remarks>
public class ModelColourAndBorderParityTests
{
    /// <summary>
    /// The one reading of "does this border paint, and how wide" (<see cref="Border.Paints"/>,
    /// <see cref="Border.PaintedWidth"/>) every exporter and the HTML converter take, rather than
    /// spelling it out at each site: no border, one marked <c>None</c>, one of
    /// zero, negative or NaN width paint nothing; a positive width paints that width.
    /// </summary>
    [Theory]
    [InlineData(false, 2.0, true, 2.0)]
    [InlineData(true, 2.0, false, 0.0)]
    [InlineData(false, 0.0, false, 0.0)]
    [InlineData(false, -1.0, false, 0.0)]
    [InlineData(false, double.NaN, false, 0.0)]
    public void ABorderPaintsOnlyWhenPresentNotNoneAndOfPositiveWidth(bool none, double width, bool paints, double painted)
    {
        var border = new Border(width, "000000") { None = none };
        Assert.Equal(paints, Border.Paints(border));
        Assert.Equal(painted, Border.PaintedWidth(border));
        Assert.False(Border.Paints(null));
        Assert.Equal(0.0, Border.PaintedWidth(null));
    }

    /// <summary>
    /// A border thinner than 0.2pt is stroked at 0.2pt, so a hairline the model asks for stays
    /// visible: one edge alone, which is stroked on its own (<c>PdfExporter.StrokeEdge</c>).
    /// </summary>
    [Fact]
    public void AnEdgeThinnerThanAFifthOfAPoint_IsStrokedAtAFifth()
    {
        var doc = new DocumentBuilder().AddSection(s => s.AddParagraph(p => p.AddText("Ruled"))).Build();
        ((Paragraph)doc.Sections[0].Children[0]).BorderTop = new Border(0.05, "000000");

        string content = Tetitulz.Pdf.TestHelpers.PdfContentProbe.DecodeStreams(doc.ToPdf());

        Assert.Matches(@"(?<![\d.])0\.2 w\b", content);
        Assert.DoesNotContain("0.05 w", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The un-prefixed form reaches ODT's <c>fo:color</c> instead of being dropped.
    /// </summary>
    [Theory]
    [InlineData("ff0000")]
    [InlineData("#ff0000")]
    [InlineData("f00")]
    [InlineData("#ff0000ff")]   // CSS alpha — dropped, as the PDF path already does
    public void AnUnprefixedColourReachesTheOdtOutput(string colour)
    {
        string odt = ExportOdtText(colour);

        Assert.Contains("fo:color=\"#ff0000\"", odt, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The control.</b> A genuinely unparseable colour is still refused, so the case above
    /// cannot pass on a parser that started accepting everything — which would be worse than the
    /// defect, since an invalid <c>fo:color</c> makes LibreOffice drop the whole style.
    /// </summary>
    [Fact]
    public void AnUnparseableColourIsStillRefused()
    {
        Assert.DoesNotContain("fo:color", ExportOdtText("rebeccapurple"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero-width border draws nothing, on a paragraph as on a table cell.
    /// </summary>
    [Fact]
    public void AZeroWidthBorderDrawsNothing()
    {
        Assert.Equal(StrokeCount(BuildPdf(borderWidth: 0)), StrokeCount(BuildPdf(borderWidth: null)));
    }

    /// <summary>
    /// <b>The control.</b> A real border still draws — without this the case above would pass on an
    /// exporter that had started skipping every border.
    /// </summary>
    [Fact]
    public void ANonZeroBorderStillDraws()
    {
        Assert.True(StrokeCount(BuildPdf(borderWidth: 1)) > StrokeCount(BuildPdf(borderWidth: null)),
            "a 1 pt paragraph border must add stroke operators");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Counts stroke operators in the page's DECODED content stream. Scanning the file's raw bytes
    /// does not work — the exporter Flate-compresses the stream, so the operators are not there to
    /// find, and a naive scan reports 0 for every fixture including the control.
    /// </summary>
    private static int StrokeCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        byte[] content = doc.Pages[0].GetContentStreamData();
        Assert.NotNull(content);

        int n = 0;
        foreach (var op in Tetitulz.Pdf.Core.Text.ContentStreamParser.Parse(content))
            if (op.Operator == "S") n++;
        return n;
    }

    private static byte[] BuildPdf(double? borderWidth)
    {
        var para = new Paragraph();
        para.Runs.Add(new TextRun { Text = "Bordered paragraph" });
        if (borderWidth.HasValue)
            para.BorderTop = new Border { Width = borderWidth.Value, Color = "#000000" };

        var section = new Section();
        section.Children.Add(para);
        var doc = new Document();
        doc.Sections.Add(section);

        using var ms = new MemoryStream();
        doc.SaveAsPdf(ms);
        return ms.ToArray();
    }

    private static string ExportOdtText(string colour)
    {
        var para = new Paragraph();
        para.Runs.Add(new TextRun { Text = "coloured", Color = colour });

        var section = new Section();
        section.Children.Add(para);
        var doc = new Document();
        doc.Sections.Add(section);

        using var ms = new MemoryStream();
        doc.SaveAsOdt(ms);
        ms.Position = 0;

        using var zip = new System.IO.Compression.ZipArchive(
            ms, System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.GetEntry("content.xml");
        Assert.True(entry != null, "the ODT package has no content.xml");

        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
