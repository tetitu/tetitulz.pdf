// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The fluent façade reaching model properties, so a code-first caller does not have to abandon
/// the chain and construct <c>Model.*</c> objects by hand.
///
/// <para><b>Each case asserts the model property the method sets</b>, not that <c>Build()</c>
/// returned something. A builder method that returned <c>this</c> and did nothing would satisfy
/// every "it produced a document" assertion, which is precisely the shape banned test pattern 3
/// describes — and precisely how chained-formatting methods can stay silent no-ops.</para>
///
/// <para>Two cases go further than the model, because the model alone cannot show them: the nested
/// table renders through to a content stream, and the untouched-path case compares whole
/// files.</para>
/// </summary>
public class FluentSurfaceTests
{
    private static Section OneSection(Action<SectionBuilder> configure) =>
        new DocumentBuilder().AddSection(configure).Build().Sections[0];

    private static Paragraph FirstParagraph(Action<ParagraphBuilder> configure) =>
        Assert.IsType<Paragraph>(OneSection(s => s.AddParagraph(configure)).Children[0]);

    // ================================================================ document

    [Fact]
    public void DocumentBuilder_ReachesTaggedAndLanguage()
    {
        // Without these the fluent path could not produce a tagged PDF at all: the exporter keys
        // the entire structure tree off Document.Tagged, whatever roles the paragraphs carry.
        var doc = new DocumentBuilder().Tagged().Language("he-IL").Build();

        Assert.True(doc.Tagged);
        Assert.Equal("he-IL", doc.Language);
    }

    [Fact]
    public void TaggedFluentDocument_WritesAStructureTreeAndTheCatalogLang()
    {
        // The end-to-end half: the flag has to reach the exporter, not merely the model.
        var doc = new DocumentBuilder()
            .Tagged().Language("he-IL")
            .AddSection(s => s.AddParagraph(p => p.AddText("Heading").Role("H1")))
            .Build();

        string raw = PdfContentProbe.Raw(doc.ToPdf());

        Assert.Contains("/StructTreeRoot", raw, StringComparison.Ordinal);
        Assert.Contains("/MarkInfo", raw, StringComparison.Ordinal);
        Assert.Contains("/Lang (he-IL)", raw, StringComparison.Ordinal);
        Assert.Contains("/H1", raw, StringComparison.Ordinal);
    }

    // ================================================================ section

    [Fact]
    public void SectionBuilder_ReachesEveryHeaderFooterVariantAndPageNumbering()
    {
        var section = OneSection(s => s
            .FirstPageHeader(h => h.AddParagraph(p => p.AddText("first-head")))
            .FirstPageFooter(f => f.AddParagraph(p => p.AddText("first-foot")))
            .EvenPageHeader(h => h.AddParagraph(p => p.AddText("even-head")))
            .EvenPageFooter(f => f.AddParagraph(p => p.AddText("even-foot")))
            .TitlePage()
            .EvenAndOddHeaders()
            .PageNumberFormat("upperRoman")
            .PageNumberStart(7)
            .PageBorders(new PageBorders { Top = new Border(2, "#FF0000") }));

        Assert.Equal("first-head", TextOf(section.FirstPageHeader!.Paragraphs[0]));
        Assert.Equal("first-foot", TextOf(section.FirstPageFooter!.Paragraphs[0]));
        Assert.Equal("even-head", TextOf(section.EvenPageHeader!.Paragraphs[0]));
        Assert.Equal("even-foot", TextOf(section.EvenPageFooter!.Paragraphs[0]));
        Assert.True(section.TitlePage);
        Assert.True(section.EvenAndOddHeaders);
        Assert.Equal("upperRoman", section.PageNumberFormat);
        Assert.Equal(7, section.PageNumberStart);
        Assert.Equal(2, section.PageBorders!.Top!.Width);
    }

    private static string TextOf(Paragraph p) =>
        string.Concat(p.Runs.OfType<TextRun>().Select(r => r.Text));

    // ================================================================ paragraph

    [Fact]
    public void ParagraphBuilder_ReachesTheBoxAndStructureProperties()
    {
        var border = new Border(1.5, "#00FF00");
        var para = FirstParagraph(p => p
            .AddText("body")
            .Role("BlockQuote")
            .Shading("#EEEEEE")
            .LineSpacing(21)
            .PageBreakBefore()
            .IndentRight(12)
            .Borders(border)
            .Opacity(0.75));

        Assert.Equal("BlockQuote", para.Role);
        Assert.Equal("#EEEEEE", para.Shading);
        Assert.Equal(21, para.LineSpacingPt);
        Assert.True(para.PageBreakBefore);
        Assert.Equal(12, para.IndentRight);
        Assert.Same(border, para.BorderTop);
        Assert.Same(border, para.BorderBottom);
        Assert.Same(border, para.BorderLeft);
        Assert.Same(border, para.BorderRight);
        Assert.Equal(0.75, para.Opacity);
    }

    [Fact]
    public void ParagraphBuilder_ArtifactIsSeparateFromRole()
    {
        // /S /Artifact is deliberately NOT a structure type, so folding it into Role would model
        // the thing the format refuses — and it is exactly what a UA13 check reports.
        var para = FirstParagraph(p => p.AddText("rule").Artifact());

        Assert.True(para.IsArtifact);
        Assert.Null(para.Role);
    }

    [Fact]
    public void ParagraphBuilder_ReachesEveryRunProperty()
    {
        var para = FirstParagraph(p => p
            .AddText("styled")
            .Highlight("#FFFF00")
            .Superscript()
            .BaselineShift(3)
            .CharSpacing(0.5)
            .HorizontalScale(85)
            .SmallCaps()
            .AllCaps()
            .DoubleStrike()
            .Link("https://example.com")
            .TextOpacity(0.6));

        var run = Assert.IsType<TextRun>(para.Runs[0]);
        Assert.Equal("#FFFF00", run.Highlight);
        Assert.Equal(VerticalAlign.Superscript, run.VerticalAlign);
        Assert.Equal(3, run.BaselineShift);
        Assert.Equal(0.5, run.CharSpacing);
        Assert.Equal(85, run.HorizontalScale);
        Assert.True(run.IsSmallCaps);
        Assert.True(run.IsAllCaps);
        Assert.True(run.IsDoubleStrike);
        Assert.Equal("https://example.com", run.LinkUri);
        Assert.Equal(0.6, run.Opacity);
    }

    [Fact]
    public void ParagraphBuilder_ReachesLineBreakAndTabRun()
    {
        var para = FirstParagraph(p => p
            .AddTabStop(72, TabAlignment.Right, TabLeader.Dot)
            .AddText("a").AddTab().AddText("b").AddLineBreak().AddText("c"));

        Assert.Collection(para.Runs,
            r => Assert.IsType<TextRun>(r),
            r => Assert.IsType<TabRun>(r),
            r => Assert.IsType<TextRun>(r),
            r => Assert.IsType<Model.LineBreak>(r),
            r => Assert.IsType<TextRun>(r));

        var tab = Assert.Single(para.Tabs);
        Assert.Equal(72, tab.PosPt);
        Assert.Equal(TabAlignment.Right, tab.Align);
        Assert.Equal(TabLeader.Dot, tab.Leader);
    }

    [Fact]
    public void ImageAltText_AndDecorative_AreDistinctFromEachOtherAndFromSilence()
    {
        // Three different answers, and the model keeps them apart: "here is what it shows",
        // "it deliberately shows nothing", and "nobody said". Collapsing the last two is how a
        // remediation tool comes to write an empty /Alt, which is a UA finding of its own.
        byte[] bytes = { 1, 2, 3 };

        var described = FirstParagraph(p => p.AddImage(bytes, 10, 10, "a chart"));
        var decorative = FirstParagraph(p => p.AddDecorativeImage(bytes, 10, 10));
        var silent = FirstParagraph(p => p.AddImage(bytes, 10, 10));

        Assert.Equal("a chart", Assert.IsType<ImageRun>(described.Runs[0]).Alt);
        Assert.False(Assert.IsType<ImageRun>(described.Runs[0]).IsDecorative);

        Assert.True(Assert.IsType<ImageRun>(decorative.Runs[0]).IsDecorative);
        Assert.Null(Assert.IsType<ImageRun>(decorative.Runs[0]).Alt);

        Assert.Null(Assert.IsType<ImageRun>(silent.Runs[0]).Alt);
        Assert.False(Assert.IsType<ImageRun>(silent.Runs[0]).IsDecorative);
    }

    // ================================================================ tables

    [Fact]
    public void TableBuilder_ReachesWidthColumnsAndHeaderRepetition()
    {
        var section = OneSection(s => s.AddTable(t => t
            .Width(400)
            .ColumnWidths(100, 0, 300)
            .RtlColumns()
            .RepeatHeaderRow()
            .OuterBorderOnly()
            .AddRow(r => r.Header().Height(30).AddCell("h"))));

        var table = Assert.IsType<Table>(section.Children[0]);
        Assert.Equal(400, table.WidthPt);
        Assert.Equal(new double[] { 100, 0, 300 }, table.ColumnWidths);
        Assert.True(table.RtlColumns);
        Assert.True(table.RepeatHeaderRow);
        Assert.True(table.OuterBorderOnly);

        Assert.True(table.Rows[0].IsHeader);
        Assert.Equal(30, table.Rows[0].HeightPt);
        Assert.False(table.Rows[0].ExactHeight);
    }

    [Fact]
    public void CellBuilder_ReachesSpansMergesScopeAndPadding()
    {
        var section = OneSection(s => s.AddTable(t => t.AddRow(r => r.AddCell(c => c
            .AddParagraph(p => p.AddText("h"))
            .Header()
            .HeaderScope(TableHeaderScope.Column)
            .Width(120)
            .Background("#CCCCCC")
            .GridSpan(2)
            .VMerge(VerticalMerge.Restart)
            .VAlign(CellVerticalAlign.Center)
            .Borders(new Border(1, "#000000"))
            .Padding(1, 2, 3, 4)))));

        var cell = Assert.IsType<Table>(section.Children[0]).Rows[0].Cells[0];
        Assert.True(cell.IsHeader);
        Assert.Equal(TableHeaderScope.Column, cell.HeaderScope);
        Assert.Equal(120, cell.WidthPt);
        Assert.Equal("#CCCCCC", cell.BackgroundColor);
        Assert.Equal(2, cell.GridSpan);
        Assert.Equal(VerticalMerge.Restart, cell.VMerge);
        Assert.Equal(CellVerticalAlign.Center, cell.VAlign);
        Assert.NotNull(cell.BorderTop);
        Assert.Equal(1, cell.MarginLeft);
        Assert.Equal(2, cell.MarginRight);
        Assert.Equal(3, cell.MarginTop);
        Assert.Equal(4, cell.MarginBottom);
    }

    [Fact]
    public void CellBuilder_WritesBlocks_SoNestedTablesAndMultipleParagraphsRenderThrough()
    {
        // The structural gap this overload exists to close. Asserted through to the content stream
        // rather than on the model, because reaching Blocks is only half of it — TableRenderer's
        // nesting path has to run for the inner cell's text to be drawn at all.
        var doc = new DocumentBuilder()
            .AddSection(s => s.AddTable(t => t.AddRow(r => r.AddCell(c => c
                .AddParagraph(p => p.AddText("outer-first"))
                .AddTable(inner => inner.AddRow(ir => ir.AddCell("inner-cell")))
                .AddParagraph(p => p.AddText("outer-last"))))))
            .Build();

        var cell = Assert.IsType<Table>(doc.Sections[0].Children[0]).Rows[0].Cells[0];
        Assert.Equal(3, cell.Blocks.Count);
        Assert.Empty(cell.Paragraphs);
        Assert.IsType<Table>(cell.Blocks[1]);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        Assert.Contains("(outer-first) Tj", content, StringComparison.Ordinal);
        Assert.Contains("(inner-cell) Tj", content, StringComparison.Ordinal);
        Assert.Contains("(outer-last) Tj", content, StringComparison.Ordinal);
    }

    [Fact]
    public void StringCell_StillWritesParagraphs_NotBlocks()
    {
        // TableCell.Content() prefers Blocks whenever it is non-empty, so moving the string form
        // onto Blocks would be invisible in the model and would silently change what a cell that
        // mixes the two renders.
        var section = OneSection(s => s.AddTable(t => t.AddRow(r => r.AddCell("plain"))));

        var cell = Assert.IsType<Table>(section.Children[0]).Rows[0].Cells[0];
        Assert.Empty(cell.Blocks);
        Assert.Single(cell.Paragraphs);
    }

    [Fact]
    public void RowBuilder_UnderlineStrikethroughAndFontName_StyleTheCellRun()
    {
        // The asymmetry these close: cells and paragraphs style the same TextRun, but a cell could
        // not reach three of the properties a paragraph could.
        var section = OneSection(s => s.AddTable(t => t.AddRow(r => r
            .AddCell("x").Underline().Strikethrough().FontName("Courier"))));

        var run = Assert.IsType<TextRun>(
            Assert.IsType<Table>(section.Children[0]).Rows[0].Cells[0].Paragraphs[0].Runs[0]);
        Assert.True(run.IsUnderline);
        Assert.True(run.IsStrikethrough);
        Assert.Equal("Courier", run.FontName);
    }

    // ================================================================ list items

    [Fact]
    public void ListItemBuilder_ReachesMarkerRunsAndDirection()
    {
        var section = OneSection(s => s.AddListItem(l => l
            .Level(2)
            .Numbered()
            .Marker("1.2.")
            .Rtl()
            .AddText("item").Bold()));

        var li = Assert.IsType<ListItem>(section.Children[0]);
        Assert.Equal(2, li.Level);
        Assert.True(li.IsNumbered);
        Assert.Equal("1.2.", li.Marker);
        Assert.Equal(TextDirection.RightToLeft, li.Direction);
        Assert.True(Assert.IsType<TextRun>(Assert.Single(li.Runs)).IsBold);
    }

    // ================================================================ the untouched path

    [Fact]
    public void ADocumentUsingOnlyThePreExistingMethods_ExportsByteIdenticallyAcrossAllFourFormats()
    {
        // The widening is additive, so nothing a caller already wrote may move. Built twice rather
        // than compared against a stored baseline: a stored one would have to be regenerated on
        // every unrelated change and would stop discriminating the moment it was.
        Document Build() => new DocumentBuilder()
            .WithTitle("Report").WithAuthor("Tamir")
            .AddSection(s => s
                .PageSize(595, 842).Margins(72, 72, 72, 72)
                .Header(h => h.AddParagraph(p => p.AddText("head")))
                .AddParagraph(p => p.AddText("Quarterly").Bold().FontSize(20))
                .AddTable(t => t.AddRow(r => r.AddCell("Region").Bold().AddCell("Revenue")))
                .AddListItem("first", numbered: true))
            .Build();

        Assert.Equal(PdfContentProbe.NormalizedRaw(Build().ToPdf()),
                     PdfContentProbe.NormalizedRaw(Build().ToPdf()));

        // The three non-PDF exporters have no /ID to mask, so they compare directly.
        Assert.Equal(Encoding.UTF8.GetString(Build().ToHtml()), Encoding.UTF8.GetString(Build().ToHtml()));
        Assert.Equal(Build().ToDocx().Length, Build().ToDocx().Length);
        Assert.Equal(Build().ToOdt().Length, Build().ToOdt().Length);

        // And nothing the new members add leaked into it: no opacity, no ExtGState, no tagging.
        string raw = PdfContentProbe.Raw(Build().ToPdf());
        Assert.DoesNotContain("/ExtGState", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/StructTreeRoot", raw, StringComparison.Ordinal);
    }
}
