// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// WTPDF 1.0's recommendation for a table that runs over several pages: one <c>/Table</c>
/// structure, and the header row a producer repeats at the top of each later page drawn as an
/// artifact, never as a second <c>/TR</c> (GAPS.md §22e).
/// </summary>
/// <remarks>
/// <para><b>The fixture is a real multi-page table</b> — ninety rows under a header, which the page
/// breaker splits over at least three pages — not a hand-written stream, so what is asserted is what
/// <c>TableRenderer</c> writes.</para>
/// <para><b>Two controls make the negative half falsifiable.</b> Without <see cref="Table.RepeatHeaderRow"/>
/// the header is drawn once and the tree holds the same rows, so "no extra <c>/TR</c>" is a statement
/// about the repeat, not about an empty tree; and the repeated drawings are counted, so "every repeat
/// is an artifact" is a statement about at least two of them.</para>
/// </remarks>
public class RepeatedHeaderRowTaggingTests
{
    private const int BodyRows = 90;

    private static Document Doc(bool repeatHeader)
    {
        var t = new Table { RepeatHeaderRow = repeatHeader };
        var head = new TableRow { IsHeader = true };
        var hc = new TableCell { IsHeader = true };
        hc.Blocks.Add(new Paragraph { Runs = { new TextRun { Text = "HEADX" } } });
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
        var doc = new Document { Tagged = true };
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>
    /// For every drawing of <paramref name="text"/>, the innermost marked-content sequence open at
    /// that point: the first token of its <c>BDC</c> line (<c>/Artifact</c>, <c>/TH</c>, <c>/TD</c>,
    /// …), or <c>(none)</c> when the text is drawn outside any sequence.
    /// </summary>
    private static List<string> EnclosingSequences(string content, string text)
    {
        var stack = new Stack<string>();
        var found = new List<string>();
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.EndsWith(" BDC", System.StringComparison.Ordinal))
            {
                stack.Push(line.Split(' ')[0]);
                continue;
            }
            if (line == "EMC")
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }
            if (line.Contains("(" + text + ")", System.StringComparison.Ordinal))
                found.Add(stack.Count > 0 ? stack.Peek() : "(none)");
        }
        return found;
    }

    private static int Count(string raw, string pattern) => Regex.Matches(raw, pattern).Count;

    [Fact]
    public void TheHeaderRowIsOneRowInTheTreeAndItsRepeatsAreArtifacts()
    {
        byte[] control = Doc(repeatHeader: false).ToPdf();
        byte[] repeated = Doc(repeatHeader: true).ToPdf();

        int pages = PdfContentProbe.CountPages(repeated);
        Assert.True(pages >= 3, "the table must run over at least three pages for a repeat to mean anything");

        string controlContent = PdfContentProbe.DecodeStreams(control);
        string repeatedContent = PdfContentProbe.DecodeStreams(repeated);

        // Drawn once without the repeat; once per page with it. The text's own sequence is its
        // paragraph's /P (the /TH is that paragraph's parent element, not a sequence of its own).
        List<string> controlSeq = EnclosingSequences(controlContent, "HEADX");
        List<string> repeatSeq = EnclosingSequences(repeatedContent, "HEADX");
        Assert.Single(controlSeq);
        Assert.Equal("/P", controlSeq[0]);
        Assert.Equal(pages, repeatSeq.Count);

        // The logical row is drawn once, inside its paragraph's sequence; every repeat is a layout artifact.
        Assert.Equal("/P", repeatSeq[0]);
        Assert.All(repeatSeq.Skip(1), s => Assert.Equal("/Artifact", s));
        Assert.Contains("/Artifact <</Type /Layout>> BDC", repeatedContent, System.StringComparison.Ordinal);

        // One /Table, one header /TR and /TH, the same ninety body rows — the repeats add no structure.
        string controlRaw = PdfContentProbe.Raw(control);
        string repeatedRaw = PdfContentProbe.Raw(repeated);
        Assert.Equal(1, Count(controlRaw, @"/S\s*/Table\b"));
        Assert.Equal(1, Count(repeatedRaw, @"/S\s*/Table\b"));
        Assert.Equal(BodyRows + 1, Count(controlRaw, @"/S\s*/TR\b"));
        Assert.Equal(BodyRows + 1, Count(repeatedRaw, @"/S\s*/TR\b"));
        Assert.Equal(1, Count(controlRaw, @"/S\s*/TH\b"));
        Assert.Equal(1, Count(repeatedRaw, @"/S\s*/TH\b"));
    }
}
