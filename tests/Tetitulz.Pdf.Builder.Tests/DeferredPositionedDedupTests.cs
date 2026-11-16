// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A page-placed positioned paragraph inside a repeating header row is deferred to the section and
/// drawn once, however many pages the header repeats on (Builder-1:8).
/// </summary>
/// <remarks>
/// <para>The cell is laid out again on every page the header repeats on, so <c>LayOutCellBlocks</c>
/// meets the same positioned paragraph once per page and must add it to the section's held-back list
/// exactly once. That one-per-paragraph rule used to be a linear <c>Exists</c> scan of the list — an
/// O(n²) over a long multi-page table; it is now an O(1) reference-identity set probe.</para>
/// <para>This asserts the rule's result — the paragraph is drawn once — against a control that the
/// header really does repeat (its ordinary text appears once per page), so "drawn once" is a
/// statement about the dedup over several repeats, not about a one-page document. An inverted or
/// dropped guard would draw it once per page, or never.</para>
/// </remarks>
public class DeferredPositionedDedupTests
{
    private const int BodyRows = 90;

    private static Document Doc()
    {
        var t = new Table { RepeatHeaderRow = true };
        var head = new TableRow { IsHeader = true };
        var hc = new TableCell { IsHeader = true };
        hc.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "HEADX" } } });
        // Page-placed (no ContainingBox): deferred to the section and drawn once.
        hc.Blocks.Add(new Paragraph
        {
            Placement = new AbsolutePlacement { LeftPt = 50, TopPt = 700 },
            Runs = { new TextRun { Text = "PINNEDX" } },
        });
        head.Cells.Add(hc);
        t.Rows.Add(head);
        for (int i = 1; i <= BodyRows; i++)
        {
            var row = new TableRow();
            var cell = new TableCell();
            cell.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "ROW" + i + "X" } } });
            row.Cells.Add(cell);
            t.Rows.Add(row);
        }

        var section = new Section();
        section.Children.Add(t);
        var doc = new Document();
        doc.Sections.Add(section);
        return doc;
    }

    [Fact]
    public void APagePlacedParagraphInARepeatedHeader_IsDrawnOnce()
    {
        byte[] pdf = Doc().ToPdf();
        int pages = PdfContentProbe.CountPages(pdf);
        Assert.True(pages >= 3, "the table must run over several pages for the repeat to be meaningful");

        string content = PdfContentProbe.DecodeStreams(pdf);
        int pinned = Regex.Matches(content, Regex.Escape("(PINNEDX)")).Count;
        int header = Regex.Matches(content, Regex.Escape("(HEADX)")).Count;

        // The header's ordinary text is redrawn on every page: the control that the repeat happened.
        Assert.Equal(pages, header);
        // The deferred, page-placed paragraph is drawn exactly once.
        Assert.Equal(1, pinned);
    }
}
