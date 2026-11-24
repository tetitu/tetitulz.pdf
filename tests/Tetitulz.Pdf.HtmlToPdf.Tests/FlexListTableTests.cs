// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A flex item holding a list or a table is measured: its hypothetical main size is
/// the list's width at its items' text starts, or the table's stated width or columns, by the
/// layout that draws it — so a wrapping line breaks after it, a <c>flex: 1</c> list starts a line
/// at its longest word, and nothing is reported. A nested table is measured too; what is
/// reported is a table nested past the layout's cap.
/// </summary>
public class FlexListTableTests
{
    private const string Half = "<div style='width:50%'>";

    /// <summary>A half-width item, a short list and another half-width item: the list fits beside the first, the third wraps; no report.</summary>
    [Fact]
    public void AListItem_IsMeasured_AndTheLineBreaksAfterIt()
    {
        var laid = Lay($"<div style='display:flex;flex-wrap:wrap'>{Half}AAA</div><ul style='margin:0'><li>BBB</li></ul>{Half}CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "the list did not stay on the first line");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, "the third item did not wrap after the measured list");
        Assert.DoesNotContain(laid.Reports, r => r.StartsWith("flex-wrap", StringComparison.Ordinal));
    }

    /// <summary>
    /// The list's text indent is part of its width: a 300px row holds a half item, a two-letter list
    /// with no padding and a 30% item; the same list padded 60px pushes the 30% item to the next line.
    /// </summary>
    [Fact]
    public void AListsIndent_Counts()
    {
        // The 10px gaps keep the list's text and the 30% item's apart, so extraction reads two words.
        string Row(int padPx) => $"<div style='display:flex;flex-wrap:wrap;width:300px;column-gap:10px'>{Half}AAA</div><ul style='margin:0;padding-left:{padPx}px'><li>BB</li></ul><div style='width:30%'>CCC</div></div>";
        var bare = Lay(Row(0));
        var padded = Lay(Row(60));

        Assert.True(Math.Abs(bare["AAA"].Y - bare["CCC"].Y) < 2.0, "the 30% item did not fit beside an unpadded list");
        Assert.True(padded["CCC"].Y < padded["AAA"].Y - 2.0, "the 30% item did not wrap after a 60px-padded list");
        Assert.True(padded["BB"].X - bare["BB"].X > 40, $"the padded list's text did not move in: {bare["BB"].X:F1} bare, {padded["BB"].X:F1} padded (AAA at {bare["AAA"].X:F1} and {padded["AAA"].X:F1})");
    }

    /// <summary>
    /// A <c>flex: 1</c> list's minimum is its longest word at its text start: a long word starts a
    /// line, a short one stays beside a 60% item — and so does a two-word item whose longest word
    /// fits though its whole line would not (the minimum is the longest word, not the line).
    /// </summary>
    [Fact]
    public void AFlexOneList_StartsALineAtItsLongestWord()
    {
        string Row(string word) => $"<div style='display:flex;flex-wrap:wrap;width:300px'><div style='width:60%'>AAA</div><ul style='flex:1 1 0;margin:0;padding-left:20px'><li>{word}</li></ul></div>";
        var wide = Lay(Row("BBBBBBBBBBBBBBBBBBBBBBBB"));
        var narrow = Lay(Row("BB"));
        var twoWords = Lay(Row("BBBBBBBB CC"));

        Assert.True(wide["BBBBBBBBBBBBBBBBBBBBBBBB"].Y < wide["AAA"].Y - 2.0, "the long word did not start a line");
        Assert.True(Math.Abs(narrow["AAA"].Y - narrow["BB"].Y) < 2.0, "the short word did not stay on the line");
        Assert.True(Math.Abs(twoWords["AAA"].Y - twoWords["BBBBBBBB"].Y) < 2.0, "the two-word item did not stay on the line at its longest word");
        Assert.True(twoWords["CC"].Y < twoWords["BBBBBBBB"].Y - 2.0, "the two-word item's second word did not wrap inside it");
    }

    /// <summary>
    /// A table's stated width is its size: 200px beside a half item does not fit a 300px row, 100px
    /// does. The table is inside an unsized item, so its own width — not the item's — is what is read.
    /// </summary>
    [Fact]
    public void ATablesStatedWidth_IsItsSize()
    {
        string Row(int px) => $"<div style='display:flex;flex-wrap:wrap;width:300px'>{Half}AAA</div><div><table style='width:{px}px'><tr><td>BBB</td></tr></table></div></div>";
        var wide = Lay(Row(200));
        var narrow = Lay(Row(100));

        Assert.True(wide["BBB"].Y < wide["AAA"].Y - 5.0, "the 200px table did not wrap");
        // The cell's padding lowers the table's text by about 2pt on the shared line.
        Assert.True(Math.Abs(narrow["AAA"].Y - narrow["BBB"].Y) < 5.0, $"the 100px table did not stay on the line: AAA at ({narrow["AAA"].X:F1}, {narrow["AAA"].Y:F1}), BBB at ({narrow["BBB"].X:F1}, {narrow["BBB"].Y:F1})");
    }

    /// <summary>
    /// A list inside a table cell keeps its <c>padding-left</c>: its text sits 60px in from a
    /// zero-padded list's, its marker drawn before it — not one paragraph per item at the exporter's
    /// own indent, which would also make a flex item measured from the list disagree with the list it
    /// draws.
    /// </summary>
    [Fact]
    public void AListInATableCell_KeepsItsPadding()
    {
        string Cell(int padPx) => $"<table><tr><td><ul style='margin:0;padding-left:{padPx}px'><li>BB</li></ul></td></tr></table>";
        var bare = Lay(Cell(0));
        var padded = Lay(Cell(60));

        Assert.Equal(45, padded["BB"].X - bare["BB"].X, 1);
        Assert.Contains(padded.Words.Keys, w => w.Contains('•'));
    }

    /// <summary>
    /// A table whose only column holds a nested table is as wide as the nested table (CSS 2.1
    /// §17.5.2.2: a cell's content, a table included, sizes its column): the item fits beside the
    /// first half, the third wraps, nothing is reported. Before 2026-10-19 the column was unknown —
    /// one line, reported.
    /// </summary>
    [Fact]
    public void AnItemWithANestedTable_IsMeasured_AndTheLineBreaksAfterIt()
    {
        var laid = Lay($"<div style='display:flex;flex-wrap:wrap'>{Half}AAA</div><section><table><tr><td><table><tr><td>BBB</td></tr></table></td></tr></table></section>{Half}CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 8.0, "the nested table did not stay on the first line");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 8.0, "the third item did not wrap after the measured table");
        Assert.DoesNotContain(laid.Reports, r => r.StartsWith("flex-wrap", StringComparison.Ordinal));
    }

    /// <summary>
    /// A table nested past the cap the layout draws to has no width this can know: the line stays
    /// unbroken and the report names the item — the fallback's control.
    /// </summary>
    [Fact]
    public void AnItemPastTheNestingCap_LeavesTheLineUnbroken_AndIsReported()
    {
        var laid = Lay($"<div style='display:flex;flex-wrap:wrap'>{Half}AAA</div><section>{ManagedTestSupport.TablesPastTheNestingCap("BBB")}</section>{Half}CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["CCC"].Y) < 2.0, "the line was broken without a size to break on");
        string report = Assert.Single(laid.Reports, r => r.StartsWith("flex-wrap", StringComparison.Ordinal));
        Assert.Contains("<section>", report, StringComparison.Ordinal);
        Assert.Contains("one line", report, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new();
        public readonly List<string> Reports = new();

        public (double X, double Y) this[string word]
        {
            get
            {
                Assert.True(Words.ContainsKey(word), $"'{word}' was not laid out at all");
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
        byte[] pdf = output.ToArray();

        using var input = new MemoryStream(pdf, writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);
        return laid;
    }
}
