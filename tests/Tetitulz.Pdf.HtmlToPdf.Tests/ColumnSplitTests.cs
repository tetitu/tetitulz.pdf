// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A multi-column block breaks a paragraph between its lines since batch 129 (CSS Multi-column 1
/// §8.1 balancing, now below block granularity): a paragraph that straddles a column ends the column
/// with as many lines as fit — never fewer than its orphans, never leaving fewer than its widows —
/// and goes on at the top of the next, so one long paragraph alone fills two columns and two unequal
/// paragraphs balance to within a line. Before, the break fell on the block boundary, reported every
/// time; what cannot be broken (a box of blocks, a list, a paragraph too short for its orphans and
/// widows) stays whole, and only an uneven balance is reported.
/// </summary>
/// <remarks>Letter page, 28.8pt margins; the words' positions read back from the PDF. The HTML default for <c>orphans</c> and <c>widows</c> is 2.</remarks>
public class ColumnSplitTests
{
    private const double Margin = 28.8;

    private static string Words(int n, string stem = "w") => string.Join(" ", Enumerable.Range(1, n).Select(i => stem + i));

    /// <summary>One paragraph of sixty words alone in two columns is broken: words in both columns, the first column's words before the second's, no report.</summary>
    [Fact]
    public void OneLongParagraph_FillsTwoColumns()
    {
        var laid = Lay($"<div style='column-count:2'><p>{Words(60)}</p></div>");

        var first = laid.Column(0); var second = laid.Column(1);
        Assert.True(first.Count >= 3 && second.Count >= 3, $"the paragraph was not broken over two columns: {first.Count} and {second.Count} words");
        int lastOfFirst = first.Max(w => Index(w.Text)), firstOfSecond = second.Min(w => Index(w.Text));
        Assert.Equal(lastOfFirst + 1, firstOfSecond);
        Assert.DoesNotContain(laid.Reports, r => r.StartsWith("column-count", StringComparison.Ordinal));
    }

    /// <summary>Two paragraphs, sixty words and twenty: the first is broken so the columns balance — their last lines within a line of each other, four lines of the first in the first column, two and the second paragraph in the other — where whole blocks gave a 6:2 split.</summary>
    [Fact]
    public void UnequalParagraphs_BalanceByBreakingTheLongOne()
    {
        var laid = Lay($"<div style='column-count:2'><p>{Words(60, "a")}</p><p>{Words(20, "b")}</p></div>");

        double bottomOfFirst = laid.Column(0).Min(w => w.Y), bottomOfSecond = laid.Column(1).Min(w => w.Y);
        Assert.True(Math.Abs(bottomOfFirst - bottomOfSecond) < 16, $"the columns end {bottomOfFirst:F1} and {bottomOfSecond:F1}: not balanced: {laid.Dump()}");
        Assert.Contains(laid.Column(1), w => w.Text.StartsWith("a", StringComparison.Ordinal));
        Assert.True(laid.Column(1).Where(w => w.Text.StartsWith("b", StringComparison.Ordinal)).All(b => laid.Column(1).Where(w => w.Text.StartsWith("a", StringComparison.Ordinal)).All(a => a.Y > b.Y)), "the second paragraph was not after the first's tail");
    }

    /// <summary>
    /// A paragraph of three lines (thirty words at the column's width: fourteen, eleven and five)
    /// cannot be broken under two orphans and two widows — two and one leaves a widow, one and two an
    /// orphan: alone it is one block, fewer than two columns, and flows at the full width — its first
    /// line runs past the column break — reported; with <c>orphans: 1; widows: 1</c> it is broken,
    /// and no line of the first column reaches the break. (Twenty words were two lines, which no
    /// rule can break two and two, so a rule that dropped either count passed.)
    /// </summary>
    [Fact]
    public void OrphansAndWidows_KeepAShortParagraphWhole()
    {
        var kept = Lay($"<div style='column-count:2'><p>{Words(30)}</p></div>");
        double top = kept.Words.Max(w => w.Y);
        Assert.Contains(kept.Words, w => Math.Abs(w.Y - top) < 1 && w.X > 306);
        Assert.Contains(kept.Reports, r => r.Contains("was not applied", StringComparison.Ordinal));

        var loose = Lay($"<div style='column-count:2'><p style='orphans:1;widows:1'>{Words(30)}</p></div>");
        Assert.NotEmpty(loose.Column(1));
        Assert.All(loose.Column(0), w => Assert.True(w.X < 306 - 20, $"a first-column word at x={w.X:F1} reaches the break: {loose.Dump()}"));
        Assert.DoesNotContain(loose.Reports, r => r.StartsWith("column-count", StringComparison.Ordinal));
    }

    /// <summary>A box of blocks taller than the other column's content is not broken: the columns are uneven and that is reported; the short paragraphs after it fill the second column.</summary>
    [Fact]
    public void AnUnbreakableBlock_StaysWhole_Reported()
    {
        var laid = Lay("<div style='column-count:2'><div style='border:1px solid #000'>" + string.Concat(Enumerable.Range(1, 6).Select(i => $"<p>Boxed{i}</p>")) + "</div><p>AAA</p><p>BBB</p></div>");

        Assert.True(laid.Column(0).All(w => w.Text.StartsWith("Boxed", StringComparison.Ordinal)), "the box was broken across the columns");
        Assert.Contains(laid.Column(1), w => w.Text == "AAA");
        Assert.Contains(laid.Reports, r => r.Contains("cannot be broken", StringComparison.Ordinal));
    }

    /// <summary>An uneven balance is reported once per container, not per segment: two segments round a spanner, each with an unbreakable tall box, say so once.</summary>
    [Fact]
    public void AnUnevenReport_IsOncePerContainer()
    {
        string tallBox = "<div style='border:1px solid #000'>" + string.Concat(Enumerable.Range(1, 6).Select(i => $"<p>Boxed{i}</p>")) + "</div>";
        var laid = Lay($"<div style='column-count:2'>{tallBox}<p>AAA</p><h2 style='column-span:all'>HEAD</h2>{tallBox}<p>BBB</p></div>");

        Assert.Equal(1, laid.Reports.Count(r => r.StartsWith("column-count: 2 is approximated", StringComparison.Ordinal)));
        Assert.Contains(laid.Words, w => w.Text == "HEAD");
    }

    /// <summary>
    /// A long paragraph after a short one is broken for the balance — the short one does not end
    /// the first column just because one block is left for one column — and its top margin counts
    /// against the room its head has: a short paragraph and then a hundred words with
    /// <c>margin-top: 30pt</c> balance to within a line, the first column holding the short
    /// paragraph and the long one's head. The margin is two lines, so a fill that counted lines
    /// alone would end the first column two lines lower.
    /// </summary>
    [Fact]
    public void TheHeadsSpacingBefore_CountsAgainstItsRoom()
    {
        var laid = Lay($"<div style='column-count:2'><p>{Words(4, "s")}</p><p style='margin-top:30pt'>{Words(100, "a")}</p></div>");

        double bottomOfFirst = laid.Column(0).Min(w => w.Y), bottomOfSecond = laid.Column(1).Min(w => w.Y);
        Assert.True(Math.Abs(bottomOfFirst - bottomOfSecond) < 16, $"the columns end {bottomOfFirst:F1} and {bottomOfSecond:F1}: not balanced: {laid.Dump()}");
        Assert.True(laid.Column(0).Count(w => w.Text.StartsWith("a", StringComparison.Ordinal)) >= 5, $"the long paragraph was not broken into the first column: {laid.Dump()}");
    }

    /// <summary>
    /// Short blocks before a long paragraph fill the first column with it (CSS flows the columns in
    /// order and balances them): two one-line paragraphs and then ninety words in three columns put
    /// both short ones and the paragraph's head in the first column, and the three columns end within
    /// a line of each other. A fill that closed a column as soon as one block was left per column
    /// put each short paragraph alone in a column.
    /// </summary>
    [Fact]
    public void ShortBlocksBeforeALongParagraph_ShareItsColumns()
    {
        var laid = Lay($"<div style='column-count:3'><p style='margin:0'>SA</p><p style='margin:0'>SB</p><p style='margin:0'>{Words(90, "a")}</p></div>");

        var columns = Enumerable.Range(0, 3).Select(c => laid.Column(c, 3)).ToList();
        Assert.Contains(columns[0], w => w.Text == "SA");
        Assert.Contains(columns[0], w => w.Text == "SB");
        Assert.True(columns[0].Count(w => w.Text.StartsWith("a", StringComparison.Ordinal)) >= 10, $"the long paragraph's head did not join the short blocks: {laid.Dump()}");
        double lowest = columns.Max(c => c.Min(w => w.Y)), highest = columns.Min(c => c.Min(w => w.Y));
        Assert.True(lowest - highest < 16, $"the columns end {string.Join(", ", columns.Select(c => c.Min(w => w.Y).ToString("F1")))}: not balanced: {laid.Dump()}");
    }

    /// <summary>
    /// A paragraph after a block that fills a column is broken for the columns left, and evenly: a
    /// tall box and then sixty words in three columns put the box in the first column and the
    /// paragraph over the other two, in order and ending within a line of each other — not whole in
    /// the second with the third empty and the block declined, and not six lines against three,
    /// which a head that did not end its column gave (the tail went on filling the column).
    /// </summary>
    [Fact]
    public void AParagraphAfterATallBlock_IsBrokenForTheColumnsLeft()
    {
        string tallBox = "<div style='border:1px solid #000'>" + string.Concat(Enumerable.Range(1, 12).Select(i => $"<p>Boxed{i}</p>")) + "</div>";
        var laid = Lay($"<div style='column-count:3'>{tallBox}<p style='margin:0'>{Words(60, "a")}</p></div>");

        var columns = Enumerable.Range(0, 3).Select(c => laid.Column(c, 3)).ToList();
        Assert.True(columns[0].Count > 0 && columns[0].All(w => w.Text.StartsWith("Boxed", StringComparison.Ordinal)), $"the box did not fill the first column alone: {laid.Dump()}");
        Assert.True(columns[1].Count >= 7 && columns[2].Count >= 7, $"the paragraph was not broken over the second and third columns: {laid.Dump()}");
        Assert.True(columns[2].Min(w => Index(w.Text)) > columns[1].Max(w => Index(w.Text)), $"the columns are not in reading order: {laid.Dump()}");
        double second = columns[1].Min(w => w.Y), third = columns[2].Min(w => w.Y);
        Assert.True(Math.Abs(second - third) < 16, $"the paragraph's two columns end {second:F1} and {third:F1}: not shared evenly: {laid.Dump()}");
        Assert.DoesNotContain(laid.Reports, r => r.Contains("was not applied", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fragment between two breaks keeps both the orphans and the widows (CSS 2.1 §13.3.3 — it ends
    /// at a break and begins after one): seven lines with <c>orphans: 1; widows: 3</c> in three
    /// columns flow, reported — the four lines that fit the first column leave three, which fit the
    /// second whole, so the third would be empty — where a middle fragment that kept its orphans
    /// alone would have made columns of three, one and three lines; with <c>widows: 1</c> the same
    /// lines make three columns.
    /// </summary>
    [Fact]
    public void AMiddleFragment_KeepsTheWidowsToo()
    {
        string lines = Words(50, "a");   // ten words on the first line, then seven a line, at a third of the band: seven lines
        var kept = Lay($"<div style='column-count:3'><p style='orphans:1;widows:3;margin:0'>{lines}</p></div>");
        Assert.Contains(kept.Reports, r => r.Contains("was not applied", StringComparison.Ordinal));
        double top = kept.Words.Max(w => w.Y);   // flowing at the full width, the first twenty words share the top line
        Assert.All(kept.Words.Where(w => Index(w.Text) <= 20), w => Assert.True(Math.Abs(w.Y - top) < 1, $"{w.Text} is below the top line: the block did not flow at the full width: {kept.Dump()}"));

        var loose = Lay($"<div style='column-count:3'><p style='orphans:1;widows:1;margin:0'>{lines}</p></div>");
        var third = loose.Column(2, 3);
        Assert.NotEmpty(third);
        Assert.True(loose.Column(0, 3).All(w => Index(w.Text) < third.Min(t => Index(t.Text))), $"the columns are not in reading order: {loose.Dump()}");
        Assert.DoesNotContain(loose.Reports, r => r.Contains("was not applied", StringComparison.Ordinal));
    }

    /// <summary>Three columns from one paragraph: broken twice, each column's words in order and every word once.</summary>
    [Fact]
    public void ThreeColumns_BreakOneParagraphTwice()
    {
        var laid = Lay($"<div style='column-count:3'><p>{Words(90)}</p></div>");

        var columns = Enumerable.Range(0, 3).Select(c => laid.Column(c, 3)).ToList();
        Assert.All(columns, c => Assert.NotEmpty(c));
        Assert.Equal(90, columns.Sum(c => c.Count));
        for (int c = 1; c < 3; c++)
            Assert.True(columns[c].Min(w => Index(w.Text)) > columns[c - 1].Max(w => Index(w.Text)), $"the columns are not in reading order: {laid.Dump()}");
    }

    // ------------------------------------------------------------------

    private static int Index(string word) => int.Parse(word.Substring(1));

    private sealed class Laid
    {
        public readonly List<(string Text, double X, double Y)> Words = new();
        public readonly List<string> Reports = new();

        /// <summary>The words of column <paramref name="n"/> of <paramref name="count"/>: the band is 554.4pt from x = 28.8, each column an equal share of it, so a two-column break is at x = 306 and a three-column one at 213.6 and 398.4.</summary>
        public List<(string Text, double X, double Y)> Column(int n, int count = 2)
        {
            double share = (612 - 2 * Margin) / count;
            double from = Margin + n * share - 2, to = Margin + (n + 1) * share - 2;
            return Words.Where(w => w.X >= from && w.X < to).ToList();
        }

        public string Dump() => string.Join("; ", Words.OrderByDescending(w => w.Y).ThenBy(w => w.X).Select(w => $"{w.Text}@({w.X:F0},{w.Y:F0})"));
    }

    private static Laid Lay(string html)
    {
        var laid = new Laid();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            laid.Words.Add((word.Text, word.X, word.Y));
        return laid;
    }
}
