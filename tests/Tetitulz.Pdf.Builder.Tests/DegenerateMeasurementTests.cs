// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Wave 17 R15: what the exporters do with a measurement a caller set to NaN, an infinity or a
/// negative — the fluent API accepts any <c>double</c>, and so does the model. Every measurement is
/// checked by one walk over the document before any exporter reads it (R15.2,
/// <c>ModelMeasurements</c>), a page side not a positive finite number among them — it was left to
/// <c>PdfDocumentBuilder.AddPage</c> (R15.1), which only the PDF exporter reaches: a NaN or an infinity is
/// refused wherever it is, a size below 0 too, and the refusal names the element's path and the
/// property — where <c>FontSize(double.NaN)</c> used to export text drawn at size 0 and report
/// success. A measurement that may be negative (a margin, an indent, a spacing) is only required
/// finite, and the layout terminates on whatever finite value it is handed.
/// </summary>
public sealed class DegenerateMeasurementTests
{
    private static Document Doc(Action<SectionBuilder> section) =>
        new DocumentBuilder().AddSection(section).Build();

    private static byte[] Export(Document doc)
    {
        using var ms = new MemoryStream();
        new PdfExporter().Export(doc, ms);
        return ms.ToArray();
    }

    private static int Pages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return doc.Pages.Count;
    }

    /// <summary>The document's first paragraph placed, with one following paragraph whose run's size is NaN.</summary>
    private static Document WithFollower(Document doc)
    {
        var placed = (Paragraph)doc.Sections[0].Children[0];
        placed.Placement = new AbsolutePlacement { LeftPt = 10, TopPt = 10 };
        var follower = new Paragraph();
        follower.Runs.Add(new TextRun { Text = "n", FontSize = double.NaN });
        placed.Placement.Following.Add(follower);
        return doc;
    }

    /// <summary>A footer of a paragraph and a table, its one cell's run's size NaN.</summary>
    private static Document WithFooterTable(Document doc)
    {
        var footer = new Footer();
        footer.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "f" } } });
        var cell = new TableCell();
        cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "n", FontSize = double.NaN } } });
        var row = new TableRow();
        row.Cells.Add(cell);
        var table = new Table();
        table.Rows.Add(row);
        footer.Blocks.Add(table);
        doc.Sections[0].Footer = footer;
        return doc;
    }

    private static ArgumentOutOfRangeException Refusal(Document doc)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Export(doc));
        Assert.Equal(ModelMeasurements.ParamName, ex.ParamName);
        return ex;
    }

    /// <summary>
    /// A page side that is not a positive finite number is refused by every exporter, naming the
    /// section's side. AddPage refused it for the PDF exporter alone; DOCX wrote a NaN side as 0
    /// twips, and HTML and ODT never read it.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, 842, "PageWidthPt")]
    [InlineData(double.PositiveInfinity, 842, "PageWidthPt")]
    [InlineData(595, double.NaN, "PageHeightPt")]
    [InlineData(0, 0, "PageWidthPt")]
    public void PageSize_NotAPositiveFiniteNumber_IsRefusedByEveryExporter(double w, double h, string property)
    {
        var doc = Doc(s => s.PageSize(w, h).AddParagraph(p => p.AddText("Hello world")));
        foreach (Func<byte[]> export in new Func<byte[]>[] { () => Export(doc), doc.ToHtml, doc.ToDocx, doc.ToOdt })
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => export());
            Assert.Equal(ModelMeasurements.ParamName, ex.ParamName);
            Assert.Contains("Sections[0]." + property, ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The control for the refusal: a one-point page is a page.</summary>
    [Fact]
    public void PageSize_TinyButPositive_Exports()
    {
        byte[] pdf = Export(Doc(s => s.PageSize(1, 1).AddParagraph(p => p.AddText("Hello world, and a few more words to wrap."))));
        Assert.True(Pages(pdf) >= 1);
    }

    /// <summary>
    /// R15.2: a font size that is NaN, infinite or negative is refused before layout, naming the run.
    /// It used to export — text drawn at <c>PdfRealFormat</c>'s 0 for the non-finite values — and
    /// report success.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-12)]
    public void FontSize_NotANonNegativeFiniteNumber_IsRefusedAtExport_NamingTheRun(double size)
    {
        var ex = Refusal(Doc(s => s.AddParagraph(p => p.AddText("Hello world").FontSize(size))));
        Assert.Contains("Sections[0].Children[0].Runs[0].FontSize", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a font size of 0 is CSS's <c>font-size: 0</c> — text that takes no room — and exports.</summary>
    [Fact]
    public void FontSize_Zero_IsASizeAndExports()
    {
        byte[] pdf = Export(Doc(s => s.AddParagraph(p => p.AddText("Hello world").FontSize(0))));
        Assert.True(Pages(pdf) >= 1);
    }

    [Fact]
    public void Margins_NonFinite_IsRefusedAtExport_NamingTheSide()
    {
        var ex = Refusal(Doc(s => s.Margins(72, 72, double.NaN, 72).AddParagraph(p => p.AddText("Hello world"))));
        Assert.Contains("Sections[0].MarginBottom", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A margin may be negative, and a large one is the layout's business: both terminate and open.
    /// </summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(-1000)]
    public void Margins_LargeOrNegative_AreFinite_TerminateAndOpen(double m)
    {
        byte[] pdf = Export(Doc(s => s.Margins(m, m, m, m).AddParagraph(p => p.AddText("Hello world, and a few more words to wrap."))));
        Assert.True(Pages(pdf) >= 1);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5)]
    public void LineSpacing_NegativeOrNonFinite_IsRefusedAtExport(double spacing)
    {
        var ex = Refusal(Doc(s => s.AddParagraph(p => p.LineSpacing(spacing).AddText("one two three"))));
        Assert.Contains("Sections[0].Children[0].LineSpacingPt", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: 0 is the documented "automatic" line height.</summary>
    [Fact]
    public void LineSpacing_Zero_IsAutomaticAndExports()
    {
        byte[] pdf = Export(Doc(s => s.AddParagraph(p => p.LineSpacing(0).AddText(
            "one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty"))));
        Assert.True(Pages(pdf) >= 1);
    }

    /// <summary>
    /// The walk reaches the nested places — a table cell's block, a sublist's item, a footnote's
    /// run, a header's paragraph — and the path it names is the one the exporter reads.
    /// </summary>
    [Theory]
    [InlineData("cell",     "Sections[0].Children[0].Rows[0].Cells[0].Blocks[0].Runs[0].FontSize")]
    [InlineData("sublist",  "Sections[0].Children[0].Items[0].Sublist.Items[0].Runs[0].FontSize")]
    [InlineData("footnote", "Sections[0].Children[0].Runs[1].Runs[0].FontSize")]
    [InlineData("header",   "Sections[0].Header.Paragraphs[0].Runs[0].FontSize")]
    [InlineData("footerTable", "Sections[0].Footer.Blocks[1].Rows[0].Cells[0].Blocks[0].Runs[0].FontSize")]
    [InlineData("following", "Sections[0].Children[0].Placement.Following[0].Runs[0].FontSize")]
    public void NestedMeasurements_AreRefusedByTheirPath(string where, string expectedPath)
    {
        Document doc = where switch
        {
            "following" => WithFollower(Doc(s => s.AddParagraph(p => p.AddText("a")))),
            "footerTable" => WithFooterTable(Doc(s => s.AddParagraph(p => p.AddText("body")))),
            "cell" => Doc(s => s.AddTable(t => t.AddRow(r => r.AddCell(c => c.AddParagraph(p => p.AddText("x").FontSize(double.NaN)))))),
            "sublist" => Doc(s => s.AddList(l => l.AddItem(i => i.AddText("x").AddList(sub => sub.AddItem(j => j.AddText("y").FontSize(double.NaN)))))),
            "footnote" => Doc(s => s.AddParagraph(p => p.AddText("a").AddFootnote(
                runs => runs.Add(new TextRun { Text = "n", FontSize = double.NegativeInfinity })))),
            _ => Doc(s => s.Header(h => h.AddParagraph(p => p.AddText("h").FontSize(double.NaN))).AddParagraph(p => p.AddText("body"))),
        };

        var ex = Refusal(doc);
        Assert.Contains(expectedPath, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A size below 0 is refused wherever a size is: an image's width, a table's border, a shadow's
    /// blur, a box's padding, a cell's inner padding, a list's text indent.
    /// </summary>
    [Theory]
    [InlineData("image",   "Children[0].Runs[0].WidthPt")]
    [InlineData("border",  "Children[0].BorderWidth")]
    [InlineData("blur",    "Children[0].Shadow.BlurPt")]
    [InlineData("padding", "Children[0].PaddingTop")]
    [InlineData("cell",    "Children[0].Rows[0].Cells[0].InnerPaddingPt")]
    [InlineData("indent",  "Children[0].TextIndentPt")]
    public void NegativeSizes_AreRefused(string which, string expectedPath)
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        switch (which)
        {
            case "image":
                var p = new Paragraph();
                p.Runs.Add(new ImageRun { Data = new byte[] { 1, 2, 3 }, WidthPt = -1, HeightPt = 10 });
                section.Children.Add(p);
                break;
            case "border":
                section.Children.Add(new Table { BorderWidth = -0.5 });
                break;
            case "blur":
                section.Children.Add(new Paragraph { Shadow = new BoxShadow { BlurPt = -2 } });
                break;
            case "padding":
                section.Children.Add(new BoxStart { PaddingTop = -4 });
                section.Children.Add(new BoxEnd());
                break;
            case "cell":
                var t = new Table();
                var row = new TableRow();
                row.Cells.Add(new TableCell { InnerPaddingPt = -1 });
                t.Rows.Add(row);
                section.Children.Add(t);
                break;
            default:
                section.Children.Add(new ListBlock { TextIndentPt = -3 });
                break;
        }

        var ex = Refusal(doc);
        Assert.Contains(expectedPath, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control for the size rule: a measurement that may be negative — a paragraph's spacing
    /// and indent, a box's margin, a shadow's spread, the points term beside a width fraction —
    /// is not a size, and the document exports.
    /// </summary>
    [Fact]
    public void NegativeMarginsIndentsAndSpreads_AreNotSizes_AndExport()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        section.Children.Add(new BoxStart { MarginLeft = -10, MarginTop = -2 });
        var p = new Paragraph { SpacingBefore = -4, IndentLeft = -6, FirstLineIndent = -12, WidthFraction = 0.5, WidthPt = -10 };
        p.Shadow = new BoxShadow { SpreadPt = -3, OffsetXPt = -1 };
        p.Runs.Add(new TextRun { Text = "negative but finite", CharSpacing = -0.5, BaselineShift = -2 });
        section.Children.Add(p);
        section.Children.Add(new BoxEnd());

        Assert.True(Pages(Export(doc)) >= 1);
    }

    /// <summary>
    /// One document, four exporters, one answer: the HTML, DOCX and ODT exporters refuse what the
    /// PDF exporter refuses — a rule in one exporter alone is the same document going out as four
    /// different files.
    /// </summary>
    [Fact]
    public void EveryExporter_RefusesTheSameDocument()
    {
        var doc = Doc(s => s.AddParagraph(p => p.AddText("Hello world").FontSize(double.NaN)));
        Assert.Equal(ModelMeasurements.ParamName, Assert.Throws<ArgumentOutOfRangeException>(() => doc.ToPdf()).ParamName);
        Assert.Equal(ModelMeasurements.ParamName, Assert.Throws<ArgumentOutOfRangeException>(() => doc.ToHtml()).ParamName);
        Assert.Equal(ModelMeasurements.ParamName, Assert.Throws<ArgumentOutOfRangeException>(() => doc.ToDocx()).ParamName);
        Assert.Equal(ModelMeasurements.ParamName, Assert.Throws<ArgumentOutOfRangeException>(() => doc.ToOdt()).ParamName);

        // The control: the same document with a size exports in all four — the text in the HTML,
        // the document part in each package.
        var fine = Doc(s => s.AddParagraph(p => p.AddText("Hello world").FontSize(12)));
        Assert.True(Pages(fine.ToPdf()) >= 1);
        Assert.Contains("Hello world", System.Text.Encoding.UTF8.GetString(fine.ToHtml()), StringComparison.Ordinal);
        using (var docx = new System.IO.Compression.ZipArchive(new MemoryStream(fine.ToDocx())))
            Assert.NotNull(docx.GetEntry("word/document.xml"));
        using (var odt = new System.IO.Compression.ZipArchive(new MemoryStream(fine.ToOdt())))
            Assert.NotNull(odt.GetEntry("content.xml"));
    }

    /// <summary>
    /// The model is a public mutable graph: an item made its own descendant must not walk the
    /// validator for ever. The walk stops at the exporters' nesting cap, comes out of the cycle,
    /// and refuses the defect in the block after it — a walk without the cap never reaches it.
    /// </summary>
    [Fact]
    public void CyclicSublist_ValidationTerminates_AndRefusesTheDefectAfterTheCycle()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        var list = new ListBlock();
        var item = new ListItem { Text = "loop", TextFormat = new TextRun { FontSize = 12 } };
        list.Items.Add(item);
        item.Sublist = list;                       // the cycle: every level is this same, clean item
        section.Children.Add(list);

        var after = new Paragraph();
        after.Runs.Add(new TextRun { Text = "after the cycle", FontSize = double.NaN });
        section.Children.Add(after);

        var ex = Refusal(doc);
        Assert.Contains("Sections[0].Children[1].Runs[0].FontSize", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A list whose three items each hold the list: every exporter's walk caps the depth and remembers
    /// nothing, so it was 3²¹ items to each of them, and to this walk too — a hang before any export.
    /// The walk now checks each element once and refuses the document, which its exporters would walk
    /// past a million visits beyond once each. A 2 × 2 table whose cells hold the table is the same.
    /// </summary>
    [Fact]
    public void AdversarialListHoldingItselfThrice_IsRefusedNotWalked()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        var list = new ListBlock();
        for (int i = 0; i < 3; i++) list.Items.Add(new ListItem { Text = "x", Sublist = list });
        section.Children.Add(list);

        foreach (Func<byte[]> export in new Func<byte[]>[] { doc.ToPdf, doc.ToHtml, doc.ToDocx, doc.ToOdt })
        {
            var ex = Assert.Throws<ArgumentException>(() => export());
            Assert.Equal(ModelMeasurements.ParamName, ex.ParamName);
            Assert.Contains("beyond once each", ex.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A placed paragraph whose placement lists the paragraph itself twice: the walk branches at every
    /// level unless the paragraph is checked once, and is refused like the list above.
    /// </summary>
    [Fact]
    public void AdversarialParagraphFollowingItselfTwice_IsRefusedNotWalked()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        var p = new Paragraph { Runs = { new TextRun { Text = "x" } }, Placement = new AbsolutePlacement { LeftPt = 10, TopPt = 10 } };
        p.Placement.Following.Add(p);
        p.Placement.Following.Add(p);
        section.Children.Add(p);

        var ex = Assert.Throws<ArgumentException>(() => doc.ToHtml());
        Assert.Contains("beyond once each", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialTableHoldingItselfInEveryCell_IsRefusedNotWalked()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        var table = new Table();
        for (int r = 0; r < 2; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < 2; c++) { var cell = new TableCell(); cell.Blocks.Add(table); row.Cells.Add(cell); }
            table.Rows.Add(row);
        }
        section.Children.Add(table);

        var ex = Assert.Throws<ArgumentException>(() => doc.ToHtml());
        Assert.Contains("beyond once each", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: a list shared by two items once each — a few repeated visits — still exports,
    /// both occurrences written, as the single-item cycle above does.
    /// </summary>
    [Fact]
    public void AListSharedByTwoItems_StillExports()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        var shared = new ListBlock();
        shared.Items.Add(new ListItem { Text = "SHAREDX" });
        var list = new ListBlock();
        list.Items.Add(new ListItem { Text = "a", Sublist = shared });
        list.Items.Add(new ListItem { Text = "b", Sublist = shared });
        section.Children.Add(list);

        string html = System.Text.Encoding.UTF8.GetString(doc.ToHtml());
        Assert.Equal(2, html.Split("SHAREDX").Length - 1);
    }

    /// <summary>
    /// The walk reaches what the PDF exporter draws: tables nested twenty-one deep, the innermost
    /// cell's run NaN. The walk stopped at twenty levels while the table renderer draws to
    /// <c>PdfLimits.MaxTableNestingDepth</c> (24), so the run was drawn at size 0 and the export
    /// reported success.
    /// </summary>
    [Fact]
    public void ANaNInsideTablesNestedTwentyOneDeep_IsRefused()
    {
        var doc = new Document();
        var section = new Section();
        doc.Sections.Add(section);
        var inner = new Paragraph { Runs = { new TextRun { Text = "deep", FontSize = double.NaN } } };
        IDocElement block = inner;
        for (int level = 0; level < 21; level++)
        {
            var cell = new TableCell();
            cell.Blocks.Add(block);
            var row = new TableRow();
            row.Cells.Add(cell);
            var table = new Table();
            table.Rows.Add(row);
            block = table;
        }
        section.Children.Add(block);

        var ex = Refusal(doc);
        Assert.Contains(".Runs[0].FontSize", ex.Message, StringComparison.Ordinal);
    }
}
