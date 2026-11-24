// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A flex item's minimum main size (CSS Flexbox §4.5): with <c>min-width: auto</c> an item is never
/// narrower than its min-content width — its longest word — so a line breaks before a <c>flex: 1</c>
/// item whose longest word does not fit, as CSS breaks, rather than a zero basis counting 0 and
/// every <c>flex: 1</c> item joining the line. A stated <c>min-width</c> is the floor instead. And
/// an item of several blocks is measured as its widest block, so it has a size to break on. The
/// widths are words of one letter repeated, in a 200px (150pt) line.
/// </summary>
public class FlexMinContentTests
{
    private const string Wrap = "display:flex;flex-wrap:wrap;width:200px";

    /// <summary>Two <c>flex: 1</c> items whose longest words are each most of the line break into two lines; with <c>min-width: 0</c> they share one.</summary>
    [Fact]
    public void AZeroBasisItem_IsAtLeastItsLongestWord()
    {
        var laid = Lay($"<div style='{Wrap}'><div style='flex:1'>WWWWWWWWWWWW</div><div style='flex:1'>MMMMMMMMMMMM</div></div>");
        var floorless = Lay($"<div style='{Wrap}'><div style='flex:1;min-width:0'>WWWWWWWWWWWW</div><div style='flex:1;min-width:0'>MMMMMMMMMMMM</div></div>");

        Assert.True(laid["MMMMMMMMMMMM"].Y < laid["WWWWWWWWWWWW"].Y - 2.0, "the second flex: 1 item did not start a line though its longest word does not fit beside the first's");
        // On one line the two over-wide words overlap in their 75pt cells, and the extractor reads
        // them as one interleaved word: the control is that nothing is on a second line.
        AssertOneLine(floorless, "min-width: 0 did not let the two zero-basis items share the line");
        Assert.Empty(laid.Reports);
    }

    /// <summary>A stated basis narrower than the longest word is clamped up to it: two <c>flex-basis: 20px</c> items with wide words break into two lines.</summary>
    [Fact]
    public void AStatedBasis_IsClampedUpToTheLongestWord()
    {
        var laid = Lay($"<div style='{Wrap}'><div style='flex:0 0 20px'>WWWWWWWWWWWW</div><div style='flex:0 0 20px'>MMMMMMMMMMMM</div></div>");
        var floorless = Lay($"<div style='{Wrap}'><div style='flex:0 0 20px;min-width:0'>WWWWWWWWWWWW</div><div style='flex:0 0 20px;min-width:0'>MMMMMMMMMMMM</div></div>");

        Assert.True(laid["MMMMMMMMMMMM"].Y < laid["WWWWWWWWWWWW"].Y - 2.0, "the 20px basis was not clamped up to the longest word");
        AssertOneLine(floorless, "min-width: 0 did not let the two 20px items share the line");
    }

    /// <summary>The floor is the longest word, not the whole text: two items whose longest words fit the line together (30px apart, so the extractor reads them as two words), though their whole texts would not, share it.</summary>
    [Fact]
    public void TheFloor_IsTheLongestWord_NotTheWholeText()
    {
        var laid = Lay($"<div style='{Wrap};column-gap:30px'><div style='flex:0 0 20px'>WWWWW aaaaaa</div><div style='flex:0 0 20px'>MMMMM bbbbbb</div></div>");

        Assert.True(System.Math.Abs(laid["MMMMM"].Y - laid["WWWWW"].Y) < 2.0, "the items broke on their whole texts, not their longest words");
        Assert.True(laid["MMMMM"].X > laid["WWWWW"].X + 50, "the items are not side by side");
    }

    /// <summary>An item of several paragraphs is measured as its widest: two such items with wide words break into two lines, and nothing is reported.</summary>
    [Fact]
    public void AnItemOfSeveralBlocks_IsMeasuredAsItsWidest()
    {
        var laid = Lay($"<div style='{Wrap}'><div><p>a</p><p>WWWWWWWWWWWW</p></div><div><p>MMMMMMMMMMMM</p><p>b</p></div></div>");

        Assert.True(laid["MMMMMMMMMMMM"].Y < laid["WWWWWWWWWWWW"].Y - 2.0, "the second item of two paragraphs did not start a line");
        Assert.Empty(laid.Reports);
    }

    /// <summary>A box among the blocks counts its padding: a five-letter word in 40px of padding beside another five-letter word overflows the line, where the two words alone fit it (30px apart, so the extractor reads them as two words).</summary>
    [Fact]
    public void ABoxAmongTheBlocks_CountsItsEdges()
    {
        var padded = Lay($"<div style='{Wrap};column-gap:30px'><div><div style='padding:0 40px'><p>WWWWW</p></div></div><div><p>MMMMM</p></div></div>");
        var bare = Lay($"<div style='{Wrap};column-gap:30px'><div><div><p>WWWWW</p></div></div><div><p>MMMMM</p></div></div>");

        Assert.True(padded["MMMMM"].Y < padded["WWWWW"].Y - 2.0, "the padded box's edges were not counted");
        Assert.True(System.Math.Abs(bare["MMMMM"].Y - bare["WWWWW"].Y) < 2.0, "the two bare words do not share the line");
        Assert.Empty(padded.Reports);
    }

    /// <summary>A box with a stated width among the blocks counts that width: two 120px boxes of one letter each break into two lines on a 200px line.</summary>
    [Fact]
    public void ABoxWithAStatedWidth_CountsIt()
    {
        var laid = Lay($"<div style='{Wrap}'><div><div style='width:120px'><p>x</p></div></div><div><div style='width:120px'><p>y</p></div></div>");

        Assert.True(laid["y"].Y < laid["x"].Y - 2.0, "the boxes' stated widths were not counted: two 120px boxes shared a 200px line");
        Assert.Empty(laid.Reports);
    }

    // ------------------------------------------------------------------

    /// <summary>Every word the layout produced shares the first one's line — the one-line control, where over-wide words overlap and extract as one.</summary>
    private static void AssertOneLine(Laid laid, string because)
    {
        Assert.NotEmpty(laid.Words);
        double top = laid.Words.Values.First().Y;
        Assert.True(laid.Words.Values.All(v => System.Math.Abs(v.Y - top) < 2.0), because + $"; the words laid out: {string.Join(" | ", laid.Words.Select(w => $"{w.Key}@({w.Value.X:F0},{w.Value.Y:F0})"))}");
    }

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
        public readonly List<string> Reports = new();
        public (double X, double Y) this[string word]
        {
            get
            {
                Assert.True(Words.ContainsKey(word), $"'{word}' was not laid out at all; the words laid out: {string.Join(" | ", Words.Select(w => $"{w.Key}@({w.Value.X:F0},{w.Value.Y:F0})"))}; reports: {string.Join(" / ", Reports)}");
                return Words[word];
            }
        }
    }

    private static Laid Lay(string html)
    {
        var laid = new Laid();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);
        return laid;
    }
}
