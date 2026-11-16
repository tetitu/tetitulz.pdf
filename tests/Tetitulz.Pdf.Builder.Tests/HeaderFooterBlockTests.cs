// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A header's or footer's blocks (<see cref="Header.Blocks"/>, <see cref="Footer.Blocks"/>): a table beside its
/// paragraphs — a letterhead's usual layout — drawn on every page as a cell's content is, untagged, and read by
/// every walk of the model (the font coverage, the validation, the HTML export).
/// </summary>
public class HeaderFooterBlockTests
{
    private static Table Row(string left, string right)
    {
        var row = new TableRow();
        foreach (string text in new[] { left, right })
        {
            var cell = new TableCell();
            cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = text, FontSize = 10 } } });
            row.Cells.Add(cell);
        }
        var table = new Table();
        table.Rows.Add(row);
        return table;
    }

    private static Paragraph Text(string text) => new Paragraph { Runs = { new TextRun { Text = text, FontSize = 10 } } };

    private static Document Letterhead(int bodyParagraphs, bool tagged = false, bool bodyTable = false)
    {
        var section = new Section();
        section.Header = new Header();
        section.Header.Blocks.Add(Text("ABOVEX"));
        section.Header.Blocks.Add(Row("LEFTX", "RIGHTX"));
        section.Footer = new Footer();
        section.Footer.Blocks.Add(Row("FOOTLX", "FOOTRX"));
        for (int i = 1; i <= bodyParagraphs; i++) section.Children.Add(Text("BODY" + i + "X"));
        if (bodyTable) section.Children.Add(Row("CELLAX", "CELLBX"));
        var doc = new Document { Tagged = tagged };
        doc.Sections.Add(section);
        return doc;
    }

    [Fact]
    public void AHeadersTable_IsDrawnOnEveryPage_InItsColumns_BetweenItsParagraphAndTheBody()
    {
        byte[] pdf = Letterhead(80).ToPdf();
        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 2, "the body runs onto a second page");
        var drawn = PdfContentProbe.DrawnTextsOnPage(pdf);
        foreach (string mark in new[] { "ABOVEX", "LEFTX", "RIGHTX", "FOOTLX", "FOOTRX" })
            Assert.Equal(pages, drawn.Count(t => t.Text.Contains(mark, StringComparison.Ordinal)));

        var above = drawn.First(t => t.Text.Contains("ABOVEX", StringComparison.Ordinal));
        var left = drawn.First(t => t.Text.Contains("LEFTX", StringComparison.Ordinal));
        var right = drawn.First(t => t.Text.Contains("RIGHTX", StringComparison.Ordinal));
        var body = drawn.First(t => t.Text.Contains("BODY1X", StringComparison.Ordinal));
        Assert.True(left.Y < above.Y, $"the table ({left.Y}) after the header's paragraph ({above.Y})");
        Assert.True(left.Y > body.Y, $"the table ({left.Y}) above the body ({body.Y})");
        Assert.Equal(left.Y, right.Y, 1);                                  // one row
        Assert.True(right.X > left.X + 100, $"the second column ({right.X}) beside the first ({left.X})");

        var foot = drawn.First(t => t.Text.Contains("FOOTLX", StringComparison.Ordinal));
        double lowestBody = drawn.Where(t => t.Text.StartsWith("BODY", StringComparison.Ordinal)).Take(10).Min(t => t.Y);
        Assert.True(foot.Y < lowestBody, $"the footer's table ({foot.Y}) below the body ({lowestBody})");
    }

    [Fact]
    public void AHeadersTable_IsAnArtifact_WhereABodyTableIsTagged()
    {
        string Everything(byte[] pdf) => PdfContentProbe.Raw(pdf) + PdfContentProbe.DecodeStreams(pdf);
        int Tables(byte[] pdf) => Regex.Matches(Everything(pdf), @"/S\s*/Table\b").Count;

        byte[] withBodyTable = Letterhead(3, tagged: true, bodyTable: true).ToPdf();
        Assert.Equal(1, Tables(withBodyTable));                            // the body's table: the tagging is potent
        Assert.Equal(0, Tables(Letterhead(3, tagged: true).ToPdf()));      // the header's and footer's: none
        Assert.Contains("LEFTX", PdfContentProbe.DecodeStreams(withBodyTable), StringComparison.Ordinal);
    }

    [Fact]
    public void TextOnlyInAHeadersTable_IsCoveredByAnEmbeddedFont()
    {
        const string Hebrew = "שלום";
        var doc = Letterhead(1);
        var cell = new TableCell();
        cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = Hebrew, FontSize = 10 } } });
        ((Table)doc.Sections[0].Header!.Blocks[1]).Rows[0].Cells.Add(cell);

        byte[] pdf = doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build(Hebrew),
        });
        string raw = PdfContentProbe.Raw(pdf);
        Assert.Contains("/Type0", raw);
        Assert.Contains("/FontFile2", raw);
    }

    [Fact]
    public void TheHtmlExport_WritesTheBandsTables()
    {
        string html = Encoding.UTF8.GetString(Letterhead(1).ToHtml());
        int header = html.IndexOf("<header>", StringComparison.Ordinal), headerEnd = html.IndexOf("</header>", StringComparison.Ordinal);
        int footer = html.IndexOf("<footer>", StringComparison.Ordinal), footerEnd = html.IndexOf("</footer>", StringComparison.Ordinal);
        string inHeader = html.Substring(header, headerEnd - header), inFooter = html.Substring(footer, footerEnd - footer);
        Assert.Contains("<table", inHeader, StringComparison.Ordinal);
        Assert.Contains("RIGHTX", inHeader, StringComparison.Ordinal);
        Assert.True(inHeader.IndexOf("ABOVEX", StringComparison.Ordinal) < inHeader.IndexOf("LEFTX", StringComparison.Ordinal));
        Assert.Contains("<table", inFooter, StringComparison.Ordinal);
        Assert.Contains("FOOTRX", inFooter, StringComparison.Ordinal);
    }

    [Fact]
    public void ANoteInAHeadersTable_IsNumberedFirst_AheadOfTheBodys()
    {
        var doc = Letterhead(1);
        var inHeader = new Footnote();
        inHeader.Runs.Add(new TextRun { Text = "header note" });
        ((Table)doc.Sections[0].Header!.Blocks[1]).Rows[0].Cells[0].Blocks.Add(new Paragraph { Runs = { inHeader } });
        var inBody = new Footnote();
        inBody.Runs.Add(new TextRun { Text = "body note" });
        ((Paragraph)doc.Sections[0].Children[0]).Runs.Add(inBody);

        var numbering = FootnoteNumbering.For(doc);
        Assert.Equal("1", numbering.MarkOf(inHeader));
        Assert.Equal("2", numbering.MarkOf(inBody));
    }

    [Fact]
    public void ABandsBlocks_SupersedeItsParagraphs_AsACellsDo()
    {
        var doc = Letterhead(1);
        doc.Sections[0].Header!.Paragraphs.Add(Text("IGNOREDX"));
        string drawn = PdfContentProbe.DecodeStreams(doc.ToPdf());
        Assert.DoesNotContain("IGNOREDX", drawn, StringComparison.Ordinal);
        Assert.Contains("LEFTX", drawn, StringComparison.Ordinal);

        doc.Sections[0].Header!.Blocks.Clear();                            // no blocks: the paragraphs are the content
        Assert.Contains("IGNOREDX", PdfContentProbe.DecodeStreams(doc.ToPdf()), StringComparison.Ordinal);
    }
}
