// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Xunit;
using M = Tetitulz.Pdf.Builder.Model;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The DOCX exporter writes its parts in schema order: <c>w:bidiVisual</c> before <c>w:tblBorders</c>
/// (CT_TblPr), <c>w:numPr</c> before <c>w:bidi</c> (CT_PPrBase), and a <c>w:tblGrid</c> between the
/// table's properties and its rows (CT_Tbl), one column per grid column. The package validates
/// against the Office schema.
/// </summary>
public sealed class DocxSchemaOrderTests
{
    [Fact]
    public void ARightToLeftTableAndANumberedList_ValidateAgainstTheSchema()
    {
        var doc = new M.Document();
        var section = new M.Section { Direction = M.TextDirection.RightToLeft };
        var table = new M.Table();
        var row = new M.TableRow();
        var cell = new M.TableCell();
        var cellText = new M.Paragraph();
        cellText.Runs.Add(new M.TextRun { Text = "cell" });
        cell.Paragraphs.Add(cellText);
        row.Cells.Add(cell);
        var wide = new M.TableCell { GridSpan = 2 };
        wide.Paragraphs.Add(new M.Paragraph());
        var second = new M.TableRow();
        second.Cells.Add(wide);
        table.Rows.Add(row);
        table.Rows.Add(second);
        section.Children.Add(table);
        var list = new M.ListBlock { IsOrdered = true };
        list.Items.Add(new M.ListItem { Text = "item" });
        section.Children.Add(list);
        doc.Sections.Add(section);

        using var ms = new MemoryStream();
        doc.SaveAsDocx(ms);
        ms.Position = 0;
        using var word = WordprocessingDocument.Open(ms, false);

        var errors = new OpenXmlValidator().Validate(word).Select(e => e.Description + " at " + e.Path?.XPath).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));

        var written = word.MainDocumentPart!.Document.Body!.Elements<W.Table>().Single();
        Assert.IsType<W.BiDiVisual>(written.GetFirstChild<W.TableProperties>()!.FirstChild);
        var grid = Assert.IsType<W.TableGrid>(written.GetFirstChild<W.TableProperties>()!.NextSibling());
        Assert.Equal(2, grid.Elements<W.GridColumn>().Count());            // the spanning row's two columns
        var item = word.MainDocumentPart.Document.Body.Elements<W.Paragraph>()
            .Single(p => p.ParagraphProperties?.NumberingProperties != null);
        Assert.IsType<W.NumberingProperties>(item.ParagraphProperties!.FirstChild);
        Assert.NotNull(item.ParagraphProperties.GetFirstChild<W.BiDi>());
    }
}
