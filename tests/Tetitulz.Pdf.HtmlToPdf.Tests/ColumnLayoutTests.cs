// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// CSS multi-column (<c>column-count</c> / <c>columns</c>) in the managed engine.
///
/// <para><b>Measured glyph positions, for the fourth time in this mapper.</b> Before this a column
/// block fell through to ordinary flow and produced a valid PDF with everything in one column, so
/// every structural assertion passed. Only where the glyphs landed separates the two.</para>
///
/// <para><b>The approximation is wider here than for flex or grid, and the tests say which half
/// they are pinning.</b> Real multi-column flow breaks content anywhere; this splits on block
/// boundaries, balanced by the blocks' measured heights since batch 111 (<c>ColumnBalanceTests</c>
/// pins the balance; before that an estimate from character counts). So the cases below assert the
/// things that ARE exact — that a second column exists, that it is to the right, that reading order
/// is preserved, that the split lands on a block boundary — and never a precise balance point.</para>
/// </summary>
public class ColumnLayoutTests
{
    /// <summary>
    /// The claim: with <c>column-count: 2</c> the later blocks move into a second column to the
    /// right of the first, rather than continuing below it.
    /// </summary>
    [Fact]
    public void TwoColumns_PutLaterBlocksBesideTheEarlierOnes()
    {
        var words = Words(Columns(2, "AAA", "BBB", "CCC", "DDD"));

        var first = Word(words, "AAA");
        var last = Word(words, "DDD");

        Assert.True(last.X > first.X + 10,
            $"the last block is at x={last.X:F1}, not in a column right of the first at " +
            $"x={first.X:F1} — the content stayed in one column");
    }

    /// <summary>
    /// The control: the same markup without <c>column-count</c> stacks, so the case above is not
    /// passing on something the mapper did anyway.
    /// </summary>
    [Fact]
    public void WithoutColumnCount_TheSameContentStacks()
    {
        var words = Words("<div>" + string.Concat(
            new[] { "AAA", "BBB", "CCC", "DDD" }.Select(t => $"<p>{t}</p>")) + "</div>");

        var first = Word(words, "AAA");
        var last = Word(words, "DDD");

        Assert.True(Math.Abs(last.X - first.X) < 2.0, "the content was split into columns anyway");
        Assert.True(last.Y < first.Y, "the content did not stack");
    }

    /// <summary>
    /// Reading order survives the split: the blocks that stay in column one are the <b>earlier</b>
    /// ones. A distribution that reordered them would still look like two columns and would be
    /// unreadable.
    /// </summary>
    [Fact]
    public void ReadingOrderIsPreserved_EarlierBlocksStayInTheFirstColumn()
    {
        var words = Words(Columns(2, "AAA", "BBB", "CCC", "DDD"));

        double firstColumnX = Word(words, "AAA").X;
        double secondColumnX = Word(words, "DDD").X;

        // Everything in column one precedes everything in column two, in document order.
        var order = new[] { "AAA", "BBB", "CCC", "DDD" }.Select(t => Word(words, t)).ToList();
        int lastInFirst = order.FindLastIndex(w => Math.Abs(w.X - firstColumnX) < 2.0);
        int firstInSecond = order.FindIndex(w => Math.Abs(w.X - secondColumnX) < 2.0);

        Assert.True(lastInFirst >= 0 && firstInSecond >= 0, "the blocks did not land in two columns");
        Assert.True(lastInFirst < firstInSecond,
            "a later block was placed in the first column after an earlier one moved to the second");
    }

    /// <summary>
    /// <c>columns: 200px 3</c> — the shorthand names the count with whichever token is a bare
    /// integer, in either order. Three columns hold three of the four blocks on one line.
    /// </summary>
    [Fact]
    public void TheColumnsShorthand_NamesTheCount()
    {
        var words = Words(
            "<div style='columns:200px 3'>" +
            string.Concat(new[] { "AAA", "BBB", "CCC" }.Select(t => $"<p>{t}</p>")) +
            "</div>");

        var xs = new[] { "AAA", "BBB", "CCC" }.Select(t => Word(words, t).X).Distinct().ToList();
        Assert.Equal(3, xs.Count);
    }

    /// <summary>
    /// Fewer blocks than columns declines, rather than producing empty columns. Ordinary flow at
    /// least reads top to bottom; a two-column block with one block in it does not.
    /// </summary>
    [Fact]
    public void FewerBlocksThanColumns_FallsBackToOrdinaryFlow()
    {
        var reports = new List<string>();
        var words = Words(Columns(3, "AAA", "BBB"), reports);

        Assert.True(Math.Abs(Word(words, "AAA").X - Word(words, "BBB").X) < 2.0,
            "the content was split across columns it could not fill");
        Assert.Contains(reports, r => r.Contains("child block", StringComparison.Ordinal));
    }

    /// <summary>
    /// What the mapping cannot express is reported, and only that (batch 129): four one-line
    /// paragraphs in two columns balance and report nothing, where every column block was reported
    /// as divided on block boundaries before; a tall box of blocks beside short paragraphs cannot be
    /// broken and leaves the columns uneven, which is reported, naming what could not be broken.
    /// </summary>
    /// <remarks>
    /// The <c>column-gap</c> and <c>column-rule</c> rows left this theory when both became
    /// applied — a report on either would now fire on input that lost nothing.
    /// <c>ColumnGapAndRuleTests</c> pins what they produce instead; the <c>column-span</c> row
    /// left in batch 89 (<see cref="ASpanningChild_BreaksTheColumnsAtTheFullWidth"/>), and the
    /// block-boundary row in batch 129 (<c>ColumnSplitTests</c>).
    /// </remarks>
    [Fact]
    public void OnlyAnUnevenBalance_IsReported()
    {
        var balanced = new List<string>();
        Words("<div style='column-count:2'>" + string.Concat(
            new[] { "AAA", "BBB", "CCC", "DDD" }.Select(t => $"<p>{t}</p>")) + "</div>", balanced);
        Assert.DoesNotContain(balanced, r => r.StartsWith("column-count", StringComparison.Ordinal));

        var uneven = new List<string>();
        Words("<div style='column-count:2'><div style='border:1px solid #000'>" + string.Concat(
            Enumerable.Range(1, 6).Select(i => $"<p>Boxed{i}</p>")) + "</div><p>AAA</p><p>BBB</p></div>", uneven);
        Assert.Contains(uneven, r => r.StartsWith("column-count: 2 is approximated", StringComparison.Ordinal) && r.Contains("cannot be broken", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>column-span: all</c> (CSS Multi-column 1 §6, batch 89) on a child splits the block: AAA and
    /// BBB are the two columns above the heading, the heading runs the full width below them — too
    /// long for a column, it stays on one line — and CCC and DDD are two columns again below it,
    /// where before batch 89 the heading sat inside a column and was reported. Both segments balance,
    /// so nothing is reported about the columns (every column block was, until batch 129), and nothing
    /// about <c>column-span</c>.
    /// </summary>
    [Fact]
    public void ASpanningChild_BreaksTheColumnsAtTheFullWidth()
    {
        var reports = new List<string>();
        // A heading wider than a column and narrower than the page: in a column it would wrap.
        const string Head = "HEAD alpha bravo charlie delta echo foxtrot golf TAIL";
        var words = Words("<div style='column-count:2'><p>AAA</p><p>BBB</p><h2 style='column-span:all'>" + Head + "</h2><p>CCC</p><p>DDD</p></div>", reports);
        var a = Word(words, "AAA"); var b = Word(words, "BBB"); var head = Word(words, "HEAD"); var tail = Word(words, "TAIL");
        var c = Word(words, "CCC"); var d = Word(words, "DDD");
        double margin = Word(Words("<p>MMM</p>"), "MMM").X;   // a full-width block's x
        double column = b.X - a.X;                             // one column's width

        Assert.True(b.X > a.X + 50 && Math.Abs(a.Y - b.Y) < 2, $"AAA ({a.X:F0},{a.Y:F0}) and BBB ({b.X:F0},{b.Y:F0}) are not two columns");
        Assert.True(Math.Abs(head.X - margin) < 1, $"HEAD at x={head.X:F1} does not start at the margin x={margin:F1}");
        Assert.True(Math.Abs(tail.Y - head.Y) < 1 && tail.X - head.X > column,
            $"the heading is not one full-width line: HEAD at ({head.X:F0},{head.Y:F0}), TAIL at ({tail.X:F0},{tail.Y:F0}), a column {column:F0} wide");
        Assert.True(head.Y < a.Y - 2 && head.Y > c.Y + 2, $"HEAD at y={head.Y:F1} is not between the columns above (y={a.Y:F1}) and below (y={c.Y:F1})");
        Assert.True(Math.Abs(c.X - a.X) < 2 && Math.Abs(d.X - b.X) < 2 && Math.Abs(c.Y - d.Y) < 2, $"CCC ({c.X:F0},{c.Y:F0}) and DDD ({d.X:F0},{d.Y:F0}) are not two columns under the heading");
        Assert.DoesNotContain(reports, r => r.StartsWith("column-count", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, r => r.Contains("column-span", StringComparison.Ordinal));
    }

    /// <summary>
    /// Only <c>all</c> spans: a child with <c>column-span: none</c>, or a hidden spanner, leaves the
    /// block whole — four blocks in two columns, CCC at the top of the second — as with no spanner.
    /// A segment shorter than the column count falls to flow and says so, as a short block always did.
    /// </summary>
    [Theory]
    [InlineData("<p style='column-span:none'>XXX</p>")]
    [InlineData("<h2 style='column-span:all;display:none'>HEAD</h2>")]
    public void OnlyAVisibleSpannerSplitsTheBlock(string child)
    {
        var words = Words("<div style='column-count:2'><p>AAA</p><p>BBB</p>" + child + "<p>CCC</p><p>DDD</p></div>");
        var plain = Words("<div style='column-count:2'><p>AAA</p><p>BBB</p><p>CCC</p><p>DDD</p></div>");

        Assert.Equal(Word(plain, "CCC").X, Word(words, "CCC").X, 1);
        Assert.Equal(Word(plain, "CCC").Y, Word(words, "CCC").Y, 1);
        Assert.Equal(Word(plain, "AAA").Y, Word(words, "AAA").Y, 1);

        var reports = new List<string>();
        var shortSegment = Words("<div style='column-count:2'><p>AAA</p><h2 style='column-span:all'>HEAD</h2><p>CCC</p><p>DDD</p></div>", reports);
        Assert.True(Word(shortSegment, "HEAD").Y < Word(shortSegment, "AAA").Y - 2, "the spanner did not follow the lone block");
        Assert.Contains(reports, r => r.Contains("only 1 child block", StringComparison.Ordinal));
    }

    /// <summary>
    /// A document with no columns hears nothing about them — the rule that keeps the reports above
    /// worth reading.
    /// </summary>
    [Fact]
    public void ADocumentWithoutColumns_ReportsNothingAboutThem()
    {
        var reports = new List<string>();
        Words("<div><p>AAA</p><p>BBB</p></div>", reports);

        Assert.DoesNotContain(reports, r => r.Contains("column", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adversarial: <c>column-count</c> is author-supplied and sizes an allocation. A million
    /// columns must be bounded — asserted on the guard's evidence (the conversion completes and
    /// the content is still on the page) rather than on a wall clock.
    /// </summary>
    [Fact]
    public void AnOversizedColumnCount_IsBounded()
    {
        var words = Words(
            "<div style='column-count:1000000'>" +
            string.Concat(Enumerable.Range(0, 20).Select(i => $"<p>W{i}</p>")) + "</div>");

        Assert.NotEmpty(words);
    }

    // ------------------------------------------------------------------

    /// <summary>A list is weighed by its items, not as an empty block.</summary>
    /// <remarks>
    /// <para><b>An imported list is one block carrying many items.</b> The balancer once estimated
    /// a block's share from the text it holds, and a container it did not recognise weighed the
    /// floor — so a column of nothing but lists weighed the same as a column of nothing at all and
    /// every list landed in the first column. That is invisible to a structural assertion: the
    /// output is a valid two-column div with one column empty. Since batch 111 the list is measured
    /// as its items' lines, which this still pins.</para>
    ///
    /// <para>The list is deliberately heavy — twenty items against three one-line paragraphs — so a
    /// balancer reading it puts the break after it, and one reading nothing puts the paragraphs in
    /// with it.</para>
    /// </remarks>
    [Fact]
    public void AListIsWeighedByItsItems_SoColumnsBalanceAroundIt()
    {
        // The fixture has to be one where the weight MOVES the boundary. Two blocks cannot be: the
        // "never leave a later column empty" rule puts one in each whatever they weigh, which is
        // why a two-list version of this test passed with the list weighed as nothing. A heavy
        // list followed by three short paragraphs does discriminate -- weighed, the list alone is
        // taller than the three paragraphs and takes column one by itself; weighed as nothing it
        // does not, and the first paragraphs join it.
        var words = Words(
            "<div style='column-count:2'>"
            + "<ul>" + string.Concat(Enumerable.Range(0, 20).Select(i => $"<li>item{i:D4}</li>"))
            + "</ul><p>AAA</p><p>BBB</p><p>CCC</p></div>");

        var listItem = Word(words, "item0000");
        var firstPara = Word(words, "AAA");

        Assert.True(firstPara.X > listItem.X + 10,
            $"the paragraph after the list is at x={firstPara.X:F1}, not in a column right of "
            + $"the list at x={listItem.X:F1}: the balancer weighed the list as an empty block");
    }

    private static string Columns(int count, params string[] blocks)
        => $"<div style='column-count:{count}'>" +
           string.Concat(blocks.Select(t => $"<p>{t}</p>")) + "</div>";

    private static PdfTextWord Word(List<PdfTextWord> words, string text)
    {
        var hit = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(hit != null, $"'{text}' was not laid out at all");
        return hit!;
    }

    private static List<PdfTextWord> Words(string html, List<string> reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null
                ? null
                : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });

        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
