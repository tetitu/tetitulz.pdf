// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>column-gap</c> and <c>column-rule</c> on a multi-column block: applied to the table the
/// block maps to, rather than reported.
/// </summary>
/// <remarks>
/// <para><b>The decline argued from spacing and the answer was padding.</b> "A table has no
/// inter-cell spacing in this model" is true and was not the question: a cell has per-side
/// margins the renderer adds to its padding, and per-side borders. A gap is the distance between
/// two columns' text — margins on the facing sides; a rule is a line on the edge between them —
/// a border on that side.</para>
///
/// <para><b>Asserted on the model and on glyph positions.</b> The model says what the mapper
/// decided — which side, how much, which cells — and a glyph position says the decision reached
/// the page; a table with the right margins that the renderer ignored would pass the first alone.</para>
/// </remarks>
public class ColumnGapAndRuleTests
{
    /// <summary>The gap lands as margins on the facing sides, half each, and the cells add no padding of their own.</summary>
    /// <remarks>
    /// Two columns 60 pt apart: each facing side gets 30pt, and the outer sides get nothing — a
    /// margin on an outer side would indent the block from the band, which CSS does not. The cells
    /// took the renderer's 3pt a side and the margins that much less; they take none now.
    /// </remarks>
    [Fact]
    public void AColumnGap_BecomesMarginsOnTheFacingSidesOnly()
    {
        TableRow row = Row("<div style='column-count:2;column-gap:60pt'>" + Blocks(4) + "</div>");

        Assert.Equal(60 / 2.0, row.Cells[0].MarginRight, 3);
        Assert.Equal(60 / 2.0, row.Cells[1].MarginLeft, 3);
        Assert.Equal(0, row.Cells[0].MarginLeft);
        Assert.Equal(0, row.Cells[1].MarginRight);
        Assert.All(row.Cells, c => Assert.Equal(0, c.InnerPaddingPt));
    }

    /// <summary>A middle column faces two neighbours and gets a margin on each side.</summary>
    [Fact]
    public void AMiddleColumn_GetsAMarginOnBothSides()
    {
        TableRow row = Row("<div style='column-count:3;column-gap:40pt'>" + Blocks(6) + "</div>");

        Assert.Equal(40 / 2.0, row.Cells[1].MarginLeft, 3);
        Assert.Equal(40 / 2.0, row.Cells[1].MarginRight, 3);
    }

    /// <summary>The decision reaches the page: a wider gap moves the second column's text right.</summary>
    /// <remarks>
    /// By half the difference. Each cell's explicit width carries its own margins, so the
    /// boundary between the two columns stays where an equal split puts it and only the inner
    /// margin moves the text — which is what "the columns' content stays equal" means in glyphs.
    /// </remarks>
    [Fact]
    public void AWiderGap_MovesTheSecondColumnsTextByHalfTheDifference()
    {
        double narrow = Word(Words("<div style='column-count:2;column-gap:12pt'>" + Blocks(4) + "</div>"), "DDD").X;
        double wide = Word(Words("<div style='column-count:2;column-gap:60pt'>" + Blocks(4) + "</div>"), "DDD").X;

        Assert.InRange(wide - narrow, (60 - 12) / 2.0 - 2.0, (60 - 12) / 2.0 + 2.0);
    }

    /// <summary>No declaration is CSS's <c>normal</c>: one em of the container's font.</summary>
    /// <remarks>
    /// Asserted at two font sizes, so a mapper that hard-coded 12 pt would fail the second row.
    /// </remarks>
    [Theory]
    [InlineData("", 12.0)]
    [InlineData("font-size:24pt;", 24.0)]
    public void TheDefaultGap_IsOneEmOfTheContainersFont(string fontSize, double em)
    {
        TableRow row = Row($"<div style='{fontSize}column-count:2'>" + Blocks(4) + "</div>");

        Assert.Equal(em / 2, row.Cells[0].MarginRight, 3);
    }

    /// <summary>The column half of the <c>gap</c> shorthand is read when <c>column-gap</c> is absent.</summary>
    [Fact]
    public void TheGapShorthand_SuppliesTheColumnGap()
    {
        TableRow row = Row("<div style='column-count:2;gap:5pt 40pt'>" + Blocks(4) + "</div>");

        Assert.Equal(40 / 2.0, row.Cells[0].MarginRight, 3);
    }

    /// <summary>
    /// A narrow gap is drawn as narrow as it is: 2pt is 1pt a facing side, and nothing is reported.
    /// It was floored at the cells' two 3pt paddings and reported, when the cells had them.
    /// </summary>
    [Fact]
    public void ANarrowGap_IsDrawnExactly()
    {
        var reports = new List<string>();
        TableRow row = Row("<div style='column-count:2;column-gap:2pt'>" + Blocks(4) + "</div>", reports);

        Assert.Equal(1, row.Cells[0].MarginRight, 3);
        Assert.DoesNotContain(reports, r => r.Contains("column-gap", StringComparison.Ordinal));
    }

    /// <summary>A rule is a border on the edge between the columns — on every cell but the last.</summary>
    [Fact]
    public void AColumnRule_IsABorderOnTheEdgeBetweenColumns()
    {
        TableRow row = Row("<div style='column-count:3;column-rule:1px solid red'>" + Blocks(6) + "</div>");

        Assert.NotNull(row.Cells[0].BorderRight);
        Assert.NotNull(row.Cells[1].BorderRight);
        Assert.Null(row.Cells[2].BorderRight);
        Assert.Null(row.Cells[0].BorderLeft);

        Assert.Equal(0.75, row.Cells[0].BorderRight!.Width, 3);           // 1px
        Assert.Equal("#FF0000", row.Cells[0].BorderRight!.Color);
    }

    /// <summary>In a right-to-left band the edge facing the next column is the left one.</summary>
    /// <remarks>
    /// The renderer draws <c>BorderLeft</c> at a cell's VISUAL left and mirrors an RTL band's
    /// columns, so logical cell 0 sits at the visual right and its rule belongs on its left. A
    /// mapper that always chose the right edge would put the rule on the band's outer edge.
    /// </remarks>
    [Fact]
    public void AColumnRuleInARightToLeftBand_SitsOnTheLeftEdgeOfEachCell()
    {
        TableRow row = Row("<div dir='rtl' style='column-count:2;column-rule:1px solid red;column-gap:30pt'>"
                           + Blocks(4) + "</div>");

        Assert.NotNull(row.Cells[0].BorderLeft);
        Assert.Null(row.Cells[0].BorderRight);
        Assert.Null(row.Cells[1].BorderLeft);

        // And the gap's margins swap sides with it.
        Assert.Equal(30 / 2.0, row.Cells[0].MarginLeft, 3);
        Assert.Equal(0, row.Cells[0].MarginRight);
    }

    /// <summary>The rule's longhands compose: a style alone draws a rule in the default width.</summary>
    [Fact]
    public void TheRuleLonghands_Compose()
    {
        TableRow row = Row("<div style='column-count:2;column-rule-style:solid;column-rule-color:blue'>"
                           + Blocks(4) + "</div>");

        Assert.NotNull(row.Cells[0].BorderRight);
        Assert.Equal("#0000FF", row.Cells[0].BorderRight!.Color);
    }

    /// <summary>A width or colour without a style draws nothing: CSS's initial rule style is <c>none</c>.</summary>
    [Theory]
    [InlineData("column-rule-width:2px")]
    [InlineData("column-rule-color:red")]
    [InlineData("column-rule:2px none red")]
    public void ARuleWithoutAStyle_DrawsNothing(string declaration)
    {
        TableRow row = Row($"<div style='column-count:2;{declaration}'>" + Blocks(4) + "</div>");

        Assert.Null(row.Cells[0].BorderRight);
    }

    /// <summary>A non-solid rule is drawn solid, and says so.</summary>
    [Fact]
    public void ANonSolidRule_IsDrawnSolidAndReported()
    {
        var reports = new List<string>();
        TableRow row = Row("<div style='column-count:2;column-rule:2px dashed blue'>" + Blocks(4) + "</div>", reports);

        Assert.NotNull(row.Cells[0].BorderRight);
        Assert.Contains(reports, r => r.Contains("dashed", StringComparison.Ordinal));
    }

    /// <summary>Neither property is reported any more, because neither is dropped.</summary>
    /// <remarks>
    /// The block-boundaries approximation still is — it is real — so the assertion is on the two
    /// property names, not on silence.
    /// </remarks>
    [Fact]
    public void AnAppliedGapAndRule_AreNotReported()
    {
        var reports = new List<string>();
        Row("<div style='column-count:2;column-gap:20pt;column-rule:1px solid black'>" + Blocks(4) + "</div>", reports);

        Assert.DoesNotContain(reports, r => r.Contains("column-gap", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, r => r.Contains("column-rule", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------

    private static string Blocks(int count)
        => string.Concat(Enumerable.Range(0, count).Select(i => $"<p>{(char)('A' + i)}{(char)('A' + i)}{(char)('A' + i)}</p>"));

    /// <summary>The one row of the table a column block maps to.</summary>
    private static TableRow Row(string html, List<string>? reports = null)
    {
        Document model = HtmlToBuilderMapper.MapToModel(html, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null ? null : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });

        Table table = model.Sections[0].Children.OfType<Table>().First();
        return Assert.Single(table.Rows);
    }

    private static PdfTextWord Word(List<PdfTextWord> words, string text)
    {
        var hit = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(hit != null, $"'{text}' was not laid out at all");
        return hit!;
    }

    private static List<PdfTextWord> Words(string html)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

        using var input = new MemoryStream(output.ToArray(), writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
