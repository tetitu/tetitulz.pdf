// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A multi-column block is balanced by its blocks' measured heights at the column width (batch 111):
/// the columns are as short as the blocks allow, a box of blocks is one unit, a column is never left
/// empty while units remain, and a block too tall for a column stays whole. Before batch 111 the
/// balance was an estimate from character counts, so a tall block with little text was followed in
/// its column by blocks that belonged in the next.
/// </summary>
public class ColumnBalanceTests
{
    /// <summary>
    /// One tall paragraph of four letters (a 60px line) and four one-line paragraphs in two
    /// columns: by height the tall one fills the first column alone — the first short paragraph
    /// heads the second column at the tall one's top; by character count three of them joined it.
    /// </summary>
    [Fact]
    public void ATallBlockWithLittleText_FillsItsColumnAlone()
    {
        var words = Words("<div style='column-count:2'><p style='line-height:60px;margin:0'>Tall</p>"
            + string.Concat(new[] { "AAA", "BBB", "CCC", "DDD" }.Select(t => $"<p style='margin:0'>{t}</p>")) + "</div>");

        var tall = Word(words, "Tall");
        var first = Word(words, "AAA");
        Assert.True(first.X > tall.X + 50, $"AAA at x={first.X:F1} did not head the second column (Tall at x={tall.X:F1})");
        Assert.True(Math.Abs(first.Y - tall.Y) < 30, $"AAA at y={first.Y:F1} is not at the top of its column (Tall at y={tall.Y:F1})");
    }

    /// <summary>
    /// Heights are measured at the column's width: a paragraph that is one line across the page but
    /// two in a column counts two, so with four one-line blocks after it the columns balance as
    /// [long, BBB] and [CCC, DDD, EEE] — three lines each; measured across the page it would count
    /// one and CCC would join the first column.
    /// </summary>
    [Fact]
    public void HeightsAreMeasured_AtTheColumnWidth()
    {
        // About 60 characters: one line across the page's 6.5in, two in a half-width column.
        var words = Words("<div style='column-count:2'><p style='margin:0'>alpha bravo charlie delta echo foxtrot golf hotel india juliet</p>"
            + string.Concat(new[] { "BBB", "CCC", "DDD", "EEE" }.Select(t => $"<p style='margin:0'>{t}</p>")) + "</div>");

        var a = Word(words, "alpha");
        var b = Word(words, "BBB");
        var c = Word(words, "CCC");
        Assert.True(Math.Abs(b.X - a.X) < 2, $"BBB at x={b.X:F1} did not stay under the long paragraph (alpha at x={a.X:F1})");
        Assert.True(c.X > a.X + 50, $"CCC at x={c.X:F1} did not start the second column (alpha at x={a.X:F1})");
        Assert.True(Math.Abs(Word(words, "EEE").X - c.X) < 2, "EEE did not follow CCC in the second column");
    }

    /// <summary>A box of two paragraphs is one unit: both stay in one column, never one each side of the break.</summary>
    [Fact]
    public void ABoxOfBlocks_IsOneUnit()
    {
        var words = Words("<div style='column-count:2'><p style='margin:0'>AAA</p>"
            + "<div style='border:1px solid #000;padding:4px'><p style='margin:0'>BBB</p><p style='margin:0'>CCC</p></div>"
            + "<p style='margin:0'>DDD</p></div>");

        Assert.True(Math.Abs(Word(words, "BBB").X - Word(words, "CCC").X) < 2, "the box's two paragraphs were split across the columns");
        Assert.True(Word(words, "DDD").X > Word(words, "AAA").X + 50, "the content did not form two columns");
    }

    /// <summary>A box of blocks is one child block: alone it is fewer than two columns, so the block flows and says so.</summary>
    [Fact]
    public void ABoxOfBlocksAlone_IsOneChildBlock_AndFlows()
    {
        var reports = new List<string>();
        var words = Words("<div style='column-count:2'><div style='border:1px solid #000'><p>AAA</p><p>BBB</p></div></div>", reports);

        Assert.True(Math.Abs(Word(words, "AAA").X - Word(words, "BBB").X) < 2, "the box was split across columns");
        Assert.Contains(reports, r => r.Contains("only 1 child block", StringComparison.Ordinal));
    }

    /// <summary>Three columns with a tall block and two short ones: the short ones take a column each, the third is not left empty.</summary>
    [Fact]
    public void ALaterColumn_IsNeverLeftEmpty()
    {
        var words = Words("<div style='column-count:3'><p style='line-height:60px;margin:0'>Tall</p><p style='margin:0'>BBB</p><p style='margin:0'>CCC</p></div>");

        var xs = new[] { "Tall", "BBB", "CCC" }.Select(t => Math.Round(Word(words, t).X)).Distinct().ToList();
        Assert.Equal(3, xs.Count);
    }

    /// <summary>
    /// The report names what is still approximate — a block the balance cannot break, taller than
    /// an equal share — and nothing else: two one-line paragraphs balance and report nothing (they
    /// were reported as divided on block boundaries until batch 129), and a tall unbreakable box
    /// beside them is reported once, as the thing that could not be broken, never as an estimate.
    /// </summary>
    [Fact]
    public void TheReport_NamesTheUnbreakableBlock_NotAnEstimate()
    {
        var balanced = new List<string>();
        Words("<div style='column-count:2'><p>AAA</p><p>BBB</p></div>", balanced);
        Assert.DoesNotContain(balanced, r => r.StartsWith("column-count: 2 is approximated", StringComparison.Ordinal));

        var reports = new List<string>();
        Words("<div style='column-count:2'><div style='border:1px solid #000'><p>Boxed1</p><p>Boxed2</p><p>Boxed3</p><p>Boxed4</p><p>Boxed5</p><p>Boxed6</p></div><p>AAA</p><p>BBB</p></div>", reports);

        string report = Assert.Single(reports, r => r.StartsWith("column-count: 2 is approximated", StringComparison.Ordinal));
        Assert.Contains("cannot be broken", report, StringComparison.Ordinal);
        Assert.DoesNotContain("estimate", report, StringComparison.Ordinal);
    }

    private static PdfTextWord Word(List<PdfTextWord> words, string text)
    {
        var hit = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(hit != null, $"'{text}' was not laid out at all");
        return hit!;
    }

    private static List<PdfTextWord> Words(string html, List<string>? reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null ? null : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        return PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words).ToList();
    }
}
