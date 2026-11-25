// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// What an ODT table cell holds. A cell must hold at least one paragraph, so one with no
/// blocks gets an empty one; a cell that already holds a paragraph gets no extra one. Both
/// directions are pinned.
/// </summary>
public class OdtTableCellContentTests
{
    private static readonly XNamespace TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    [Fact]
    public void AnEmptyCell_HoldsOneEmptyParagraph()
    {
        var cell = CellsOf(OneCellTable(blocks => { }));

        var paragraphs = cell.Elements(TextNs + "p").ToList();
        Assert.Single(paragraphs);
        Assert.Equal(string.Empty, paragraphs[0].Value);
    }

    [Fact]
    public void ACellThatHoldsAParagraph_GetsNoExtraEmptyParagraph()
    {
        var cell = CellsOf(OneCellTable(blocks => blocks.Add(Para("Hi"))));

        var paragraphs = cell.Elements(TextNs + "p").ToList();
        Assert.Single(paragraphs);
        Assert.Contains("Hi", paragraphs[0].Value, System.StringComparison.Ordinal);
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static Table OneCellTable(System.Action<System.Collections.Generic.List<IDocElement>> fill)
    {
        var table = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        fill(cell.Blocks);
        row.Cells.Add(cell);
        table.Rows.Add(row);
        return table;
    }

    /// <summary>The single table cell of a document holding just <paramref name="table"/>.</summary>
    private static XElement CellsOf(Table table)
    {
        var doc = new Document();
        var section = new Section();
        section.Children.Add(table);
        doc.Sections.Add(section);

        using var ms = new MemoryStream();
        doc.SaveAsOdt(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var s = zip.GetEntry("content.xml")!.Open();
        var content = XDocument.Load(s);

        return content.Descendants(TableNs + "table-cell").Single();
    }
}
