// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>repeat(auto-fill, …)</c> and <c>repeat(auto-fit, …)</c> (CSS Grid §7.2.3.2, batch 103): the
/// repetitions are as many as fit the container's width with the column gap between them, each
/// track counted as its max when that is definite, else its min — so the common
/// <c>repeat(auto-fill, minmax(150px, 1fr))</c> is as many 150px columns as fit, grown to fill.
/// <c>auto-fit</c> collapses the repetitions no item reaches, so two items in such a grid share the
/// width. Before batch 103 both were skipped quietly: the template lost its tracks and the grid fell
/// to block flow with nothing reported.
/// </summary>
public class GridAutoRepeatTests
{
    private const string Ref = "<div style='border-top:1px solid #000'>REF</div>";
    private const double Px = 0.75;   // CSS px in points

    /// <summary>As many 150px columns as fit the width: the items up to that count share the first row, the next is under the first.</summary>
    [Fact]
    public void AutoFill_CountsTheColumnsThatFit()
    {
        var laid = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fill, minmax(150px, 1fr))'>" +
                       string.Concat(Enumerable.Range(0, 8).Select(i => $"<div>W{i}W</div>")) + "</div>");

        double width = Assert.Single(laid.Rules);
        int columns = (int)Math.Floor(width / (150 * Px));
        Assert.InRange(columns, 2, 7);
        for (int i = 1; i < columns; i++)
            Assert.True(Math.Abs(laid[$"W{i}W"].Y - laid["W0W"].Y) < 2.0 && laid[$"W{i}W"].X > laid[$"W{i - 1}W"].X, $"item {i} is not in column {i + 1} of the first row");
        Assert.True(laid[$"W{columns}W"].Y < laid["W0W"].Y - 2.0 && Math.Abs(laid[$"W{columns}W"].X - laid["W0W"].X) < 1.0, $"item {columns} did not wrap under the first: {columns} columns fit");
        Assert.Empty(laid.Reports);
    }

    /// <summary>The track's min is counted when its max is not definite, and its max when it is: <c>minmax(100px, 250px)</c> counts 250px.</summary>
    [Fact]
    public void AutoFill_CountsADefiniteMax()
    {
        var laid = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fill, minmax(100px, 250px))'>" +
                       string.Concat(Enumerable.Range(0, 8).Select(i => $"<div>W{i}W</div>")) + "</div>");

        double width = Assert.Single(laid.Rules);
        int columns = (int)Math.Floor(width / (250 * Px));
        Assert.InRange(columns, 2, 3);
        Assert.True(laid[$"W{columns}W"].Y < laid["W0W"].Y - 2.0, $"item {columns} did not wrap: the max (250px) was not what was counted");
        Assert.True(Math.Abs(laid[$"W{columns - 1}W"].Y - laid["W0W"].Y) < 2.0, $"item {columns - 1} is not on the first row");
    }

    /// <summary>The column gap is counted between the repetitions: 120px tracks with a 40px gap are one fewer than without it.</summary>
    [Fact]
    public void AutoFill_CountsTheGap()
    {
        string items = string.Concat(Enumerable.Range(0, 8).Select(i => $"<div>W{i}W</div>"));
        var tight = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fill, 120px)'>" + items + "</div>");
        var gapped = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fill, 120px);column-gap:40px'>" + items + "</div>");

        double width = Assert.Single(tight.Rules);
        int tightColumns = (int)Math.Floor(width / (120 * Px));
        int gappedColumns = (int)Math.Floor((width + 40 * Px) / (160 * Px));
        Assert.True(gappedColumns < tightColumns, "the fixture's width does not tell the two counts apart");
        Assert.True(Math.Abs(tight[$"W{tightColumns - 1}W"].Y - tight["W0W"].Y) < 2.0 && tight[$"W{tightColumns}W"].Y < tight["W0W"].Y - 2.0, $"without a gap {tightColumns} columns fit");
        Assert.True(Math.Abs(gapped[$"W{gappedColumns - 1}W"].Y - gapped["W0W"].Y) < 2.0 && gapped[$"W{gappedColumns}W"].Y < gapped["W0W"].Y - 2.0, $"with the gap {gappedColumns} columns fit");
    }

    /// <summary>
    /// A pattern of two tracks has two gaps per repetition: <c>repeat(auto-fill, 100px 100px)</c> with a
    /// 40px gap fits n repetitions where n·150pt + (2n − 1)·30pt is within the width — one fewer than
    /// counting one gap per repetition would say.
    /// </summary>
    [Fact]
    public void AutoFill_CountsAGapForEveryTrackOfThePattern()
    {
        var laid = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fill, 100px 100px);column-gap:40px'>" +
                       string.Concat(Enumerable.Range(0, 8).Select(i => $"<div>W{i}W</div>")) + "</div>");

        double width = Assert.Single(laid.Rules);
        int repetitions = (int)Math.Floor((width + 30) / (150 + 60));
        int oneGapEach = (int)Math.Floor((width + 30) / (150 + 30));
        Assert.True(oneGapEach > repetitions, "the fixture's width does not tell the two counts apart");
        int columns = 2 * repetitions;
        Assert.True(Math.Abs(laid[$"W{columns - 1}W"].Y - laid["W0W"].Y) < 2.0, $"item {columns - 1} is not on the first row of {columns} columns");
        Assert.True(laid[$"W{columns}W"].Y < laid["W0W"].Y - 2.0, $"item {columns} did not wrap: more than {repetitions} repetitions were counted");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// <c>auto-fit</c> collapses the empty repetitions: two items in a grid that could hold many
    /// 100px columns share the width, the second starting near the middle; under <c>auto-fill</c>
    /// the empty columns stay and the second item starts in the second of many.
    /// </summary>
    [Fact]
    public void AutoFit_CollapsesTheEmptyRepetitions()
    {
        var fit = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fit, minmax(100px, 1fr))'><div>AAA</div><div>BBB</div></div>");
        var fill = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fill, minmax(100px, 1fr))'><div>AAA</div><div>BBB</div></div>");

        double width = Assert.Single(fit.Rules);
        Assert.True(Math.Abs(fit["AAA"].Y - fit["BBB"].Y) < 2.0, "the two auto-fit items do not share the row");
        Assert.InRange(fit["BBB"].X - fit["AAA"].X, 0.45 * width, 0.55 * width);
        Assert.True(fill["BBB"].X - fill["AAA"].X < 0.3 * width, $"under auto-fill BBB starts {fill["BBB"].X - fill["AAA"].X:F0}pt in of {width:F0}pt: the empty columns collapsed");
        Assert.Empty(fit.Reports);
    }

    /// <summary>
    /// Line names inside the pattern repeat with it: <c>c 3</c> is the third repetition, two columns
    /// in from the container's left edge (REF's), and the automatic next item takes the fourth column
    /// beside it.
    /// </summary>
    [Fact]
    public void NamesInsideAnAutoFill_RepeatWithIt()
    {
        var laid = Lay(Ref + "<div style='display:grid;grid-template-columns:repeat(auto-fill, [c] minmax(100px, 1fr))'><div style='grid-column:c 3'>AAA</div><div>BBB</div></div>");

        double width = Assert.Single(laid.Rules);
        int columns = (int)Math.Floor(width / (100 * Px));
        Assert.InRange(columns, 4, 8);
        double column = width / columns;
        Assert.InRange(laid["AAA"].X - laid["REF"].X, 2 * column - 3, 2 * column + 3);
        Assert.True(Math.Abs(laid["BBB"].Y - laid["AAA"].Y) < 2.0, "BBB did not take the column beside AAA");
        Assert.InRange(laid["BBB"].X - laid["AAA"].X, column - 3, column + 3);
        Assert.Empty(laid.Reports);
    }

    /// <summary>A pattern with no definite size (<c>1fr</c>) cannot be counted: repeated once, reported, and the grid still laid out.</summary>
    [Fact]
    public void AnIndefinitePattern_IsRepeatedOnce_Reported()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:repeat(auto-fill, 1fr) 1fr'><div>AAA</div><div>BBB</div><div>CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "AAA and BBB do not share the first row");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, "CCC did not wrap: the pattern was repeated more than once");
        Assert.Contains(laid.Reports, r => r.Contains("repeat(auto-fill, 1fr) needs a definite track size", StringComparison.Ordinal));
    }

    /// <summary>In <c>grid-template-rows</c> the repeat needs a height to count against: the template is not applied, reported, rows by content.</summary>
    [Fact]
    public void AutoFillRows_AreReportedAndNotApplied()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:repeat(auto-fill, 100px)'><div>AAA</div><div>BBB</div><div>CCC</div></div>");

        Assert.InRange(laid["AAA"].Y - laid["CCC"].Y, 5, 40);
        Assert.Contains(laid.Reports, r => r.StartsWith("grid-template-rows: repeat(auto-fill, 100px) is not applied", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
        public readonly List<string> Reports = new();
        public readonly List<double> Rules = new();
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
        foreach (var rule in ManagedTestSupport.StrokedSegments(ManagedTestSupport.DecodeStreams(pdf)).Where(s => s.IsHorizontal))
            laid.Rules.Add(Math.Abs(rule.X1 - rule.X0));
        return laid;
    }
}
