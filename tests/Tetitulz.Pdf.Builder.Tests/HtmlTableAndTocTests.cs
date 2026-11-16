// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Three defects in the HTML export's table and TOC output (Builder-1:7).
///
/// <list type="number">
/// <item>A cell's paragraphs were emitted by <c>RenderInline</c> back to back — no <c>&lt;p&gt;</c>,
/// no separation, no direction or alignment — so two paragraphs in a cell became one run of text
/// aligned the default way.</item>
/// <item>A vertically merged cell (<c>VerticalMerge.Restart</c>) dropped its <c>Continue</c> rows
/// but wrote no <c>rowspan</c>, leaving every merged row one cell short and the table misaligned.</item>
/// <item>The contents list indented each level with physical <c>margin-left</c>, which puts the
/// indentation on the wrong side of an RTL document; a logical <c>margin-inline-start</c> follows
/// the direction.</item>
/// </list>
/// Asserted on the parsed markup, not on substrings of a flat string, so run-together text or a
/// missing span cannot pass (2026-10-23).
/// </summary>
public class HtmlTableAndTocTests
{
    private static string Html(params IDocElement[] blocks)
    {
        var doc = new Document();
        var section = new Section();
        foreach (IDocElement b in blocks) section.Children.Add(b);
        doc.Sections.Add(section);
        return Encoding.UTF8.GetString(doc.ToHtml());
    }

    /// <summary>The first <c>&lt;table&gt;…&lt;/table&gt;</c> in the export, parsed as XML.</summary>
    private static XElement TableElement(string html)
    {
        int start = html.IndexOf("<table", StringComparison.Ordinal);
        Assert.True(start >= 0, "no <table> in the export");
        int end = html.IndexOf("</table>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "unterminated <table>");
        return XElement.Parse(html.Substring(start, end - start + "</table>".Length));
    }

    private static Paragraph Para(string text, TextAlignment align = TextAlignment.Left,
                                  TextDirection? dir = null)
    {
        var p = new Paragraph { Alignment = align, Direction = dir };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static IEnumerable<XElement> CellsOf(XElement row)
        => row.Elements().Where(e => e.Name.LocalName is "td" or "th");

    /// <summary>(1) Two paragraphs in a cell are two <c>&lt;p&gt;</c> elements that keep their own
    /// alignment and direction, not one run of text.</summary>
    [Fact]
    public void ACellsParagraphs_AreSeparatePElements_KeepingAlignmentAndDirection()
    {
        var cell = new TableCell();
        cell.Blocks.Add(Para("centered", TextAlignment.Center));
        cell.Blocks.Add(Para("hebrew", dir: TextDirection.RightToLeft));
        var row = new TableRow();
        row.Cells.Add(cell);
        var table = new Table();
        table.Rows.Add(row);

        XElement t = TableElement(Html(table));
        XElement td = CellsOf(t.Elements("tr").Single()).Single();

        var ps = td.Elements("p").ToList();
        Assert.Equal(2, ps.Count);
        Assert.Equal("centered", ps[0].Value);
        Assert.Contains("text-align:center", (string?)ps[0].Attribute("style") ?? "", StringComparison.Ordinal);
        Assert.Equal("rtl", (string?)ps[1].Attribute("dir"));
    }

    /// <summary>(2) A Restart cell carries <c>rowspan</c> equal to the rows it absorbs, and the
    /// Continue row is one cell shorter. The rowspan attribute is the potent half — it was absent
    /// before, and its absence is exactly what misaligned the table.</summary>
    [Fact]
    public void AVerticallyMergedCell_GetsRowspan_AndTheContinueRowIsShorter()
    {
        var r0 = new TableRow();
        var merged = new TableCell { VMerge = VerticalMerge.Restart };
        merged.Blocks.Add(Para("spans two"));
        var b = new TableCell();
        b.Blocks.Add(Para("B"));
        r0.Cells.Add(merged);
        r0.Cells.Add(b);

        var r1 = new TableRow();
        r1.Cells.Add(new TableCell { VMerge = VerticalMerge.Continue });
        var c = new TableCell();
        c.Blocks.Add(Para("C"));
        r1.Cells.Add(c);

        var table = new Table();
        table.Rows.Add(r0);
        table.Rows.Add(r1);

        XElement t = TableElement(Html(table));
        var rows = t.Elements("tr").ToList();

        XElement mergedCell = CellsOf(rows[0]).First();
        Assert.Equal("2", (string?)mergedCell.Attribute("rowspan"));

        // The Continue cell was folded away: the second row carries only its own cell.
        var secondRowCells = CellsOf(rows[1]).ToList();
        Assert.Single(secondRowCells);
        Assert.Contains("C", secondRowCells[0].Value, StringComparison.Ordinal);
    }

    /// <summary>(3) The contents list indents with the logical <c>margin-inline-start</c>, never the
    /// physical <c>margin-left</c> that lands on the wrong side in RTL.</summary>
    [Fact]
    public void TheTocIndent_UsesLogicalMarginInlineStart()
    {
        var heading = new Paragraph { Role = Tetitulz.Pdf.Core.Constants.PdfStructureTypes.H1 };
        heading.Runs.Add(new TextRun { Text = "Chapter" });
        var toc = new TocBlock { Title = "Contents", MaxLevel = 3 };

        string html = Html(heading, toc);
        int navStart = html.IndexOf("<nav", StringComparison.Ordinal);
        Assert.True(navStart >= 0, "no TOC <nav> in the export");
        int navEnd = html.IndexOf("</nav>", navStart, StringComparison.Ordinal);
        string nav = html.Substring(navStart, navEnd - navStart);

        Assert.Contains("margin-inline-start:", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("margin-left:", nav, StringComparison.Ordinal);
        Assert.Contains("<a href=\"#", nav, StringComparison.Ordinal); // a real, linked entry, not an empty list
    }
}
