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
/// Implicit column tracks (CSS Grid §7.5, batch 112): a <c>grid-column</c> line past the template, a
/// span wider than the grid, and column flow past the explicit columns each add tracks to the grid —
/// sized by <c>grid-auto-columns</c> (§7.6), auto when none is stated — up to 64 columns, past which
/// the item is placed automatically or the items left take rows below, reported. Before batch 112 a
/// line past the template was reported and the item placed automatically, and column flow stopped at
/// the explicit columns.
/// </summary>
public class GridImplicitTracksTests
{
    private const string TwoTracks = "display:grid;grid-template-columns:100px 100px";

    /// <summary>
    /// A start line past the two-track template is a third, implicit column: the item sits there on
    /// the first row, and the two automatic items after it take the next row's first two tracks (the
    /// sparse cursor, §8.5, does not go back), left of it; nothing is reported. Before batch 112 the
    /// line was reported and the item placed first, at the first track.
    /// </summary>
    [Fact]
    public void AStartLinePastTheTemplate_IsAnImplicitColumn()
    {
        var laid = Lay($"<div style='{TwoTracks}'><div style='grid-column:3'>AAA</div><div>BBB</div><div>CCC</div></div>");

        Assert.True(laid["BBB"].Y < laid["AAA"].Y - 2.0 && Math.Abs(laid["CCC"].Y - laid["BBB"].Y) < 2.0, "BBB and CCC did not take the row after AAA's");
        Assert.True(laid["AAA"].X > laid["CCC"].X + 10 && laid["CCC"].X > laid["BBB"].X + 10,
            $"AAA ({laid["AAA"].X:F1}) is not in a third column right of CCC ({laid["CCC"].X:F1}) right of BBB ({laid["BBB"].X:F1})");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A span past the last track adds the tracks it reaches: <c>2 / span 3</c> on two tracks makes
    /// four columns, so the second track starts a quarter of the way across — half as far as in the
    /// two-column control, where BBB is <c>grid-column: 2</c> alone. A span with no start line adds
    /// tracks from the first too: <c>span 3</c> makes three columns, the second track a third across.
    /// </summary>
    [Fact]
    public void ASpanPastTheLastTrack_AddsTheTracksItReaches()
    {
        string Grid(string bbb) => $"<div style='{TwoTracks}'><div>AAA</div><div style='grid-column:{bbb}'>BBB</div><div>CCC</div></div>";
        var spanned = Lay(Grid("2 / span 3"));
        var control = Lay(Grid("2"));
        var auto = Lay($"<div style='{TwoTracks}'><div>AAA</div><div style='grid-column:span 3'>BBB</div><div style='grid-column:2'>CCC</div></div>");

        double track = control["BBB"].X - control["AAA"].X;   // one of two columns
        Assert.True(Math.Abs(spanned["AAA"].Y - spanned["BBB"].Y) < 2.0 && spanned["BBB"].X > spanned["AAA"].X, "BBB did not start at the second track on AAA's row");
        Assert.Equal(0.5, (spanned["BBB"].X - spanned["AAA"].X) / track, 1);
        Assert.True(spanned["CCC"].Y < spanned["AAA"].Y - 2.0 && Math.Abs(spanned["CCC"].X - spanned["AAA"].X) < 1.0, "CCC did not start the second row under AAA");
        // BBB (span 3) does not fit beside AAA: the second row, first three tracks; CCC's second track is a third across.
        Assert.True(auto["BBB"].Y < auto["AAA"].Y - 2.0 && Math.Abs(auto["BBB"].X - auto["AAA"].X) < 1.0, "the spanning BBB did not take the second row from the first track");
        Assert.Equal(2.0 / 3, (auto["CCC"].X - auto["AAA"].X) / track, 1);
        Assert.Empty(spanned.Reports);
        Assert.Empty(auto.Reports);
    }

    /// <summary>
    /// <c>grid-auto-columns</c> sizes the implicit tracks, in proportion to the explicit ones (the
    /// table shares the band out over the list): a 60px third column is 0.6 of a 100px track, a
    /// 120px one 1.2; two sizes cycle; with none stated the implicit track is the explicit average.
    /// </summary>
    [Fact]
    public void GridAutoColumns_SizesTheImplicitTracks()
    {
        string Grid(string auto) => $"<div style='{TwoTracks};{auto}'><div>AAA</div><div>BBB</div><div style='grid-column:3'>CCC</div><div style='grid-column:4'>DDD</div><div style='grid-column:5'>EEE</div></div>";
        var sized = Lay(Grid("grid-auto-columns:60px"));
        var wide = Lay(Grid("grid-auto-columns:120px"));
        var cycled = Lay(Grid("grid-auto-columns:60px 120px"));
        var plain = Lay(Grid(""));

        double Ratio(Laid l, string from, string to, string unit1, string unit2) => (l[to].X - l[from].X) / (l[unit2].X - l[unit1].X);
        Assert.Equal(0.6, Ratio(sized, "CCC", "DDD", "AAA", "BBB"), 2);
        Assert.Equal(1.2, Ratio(wide, "CCC", "DDD", "AAA", "BBB"), 2);
        Assert.Equal(0.6, Ratio(cycled, "CCC", "DDD", "AAA", "BBB"), 2);
        Assert.Equal(1.2, Ratio(cycled, "DDD", "EEE", "AAA", "BBB"), 2);
        Assert.Equal(1.0, Ratio(plain, "CCC", "DDD", "AAA", "BBB"), 2);
    }

    /// <summary>Column flow past the explicit columns goes on into implicit ones: six items in a 2×2 grid put the fifth and sixth in a third column, unreported.</summary>
    [Fact]
    public void ColumnFlow_GoesOnIntoImplicitColumns()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:100px 100px;grid-template-rows:auto auto;grid-auto-flow:column'>" +
                       "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div><div>EEE</div><div>FFF</div></div>");

        Assert.True(Math.Abs(laid["EEE"].Y - laid["AAA"].Y) < 2.0, "EEE is not on the first row");
        Assert.True(laid["EEE"].X > laid["CCC"].X + 10, $"EEE ({laid["EEE"].X:F1}) is not in a column right of CCC's ({laid["CCC"].X:F1})");
        Assert.True(Math.Abs(laid["FFF"].Y - laid["BBB"].Y) < 2.0 && Math.Abs(laid["FFF"].X - laid["EEE"].X) < 1.0, "FFF is not under EEE");
        Assert.Empty(laid.Reports);
    }

    /// <summary>With no row template, column flow has one implicit row: every item its own column, the third and fourth implicit, unreported.</summary>
    [Theory]
    [InlineData("display:grid;grid-template-columns:100px 100px;grid-auto-flow:column")]
    [InlineData("display:grid;grid-template-columns:100px 100px;grid-template-rows:auto;grid-auto-flow:column")]
    public void ColumnFlowWithOneRowOrNone_MakesOneRow(string style)
    {
        var laid = Lay($"<div style='{style}'><div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>");
        var rowFlow = Lay($"<div style='{TwoTracks}'><div>AAA</div><div>BBB</div></div>");

        var ys = new[] { "AAA", "BBB", "CCC", "DDD" }.Select(t => laid[t].Y).ToList();
        Assert.True(ys.Max() - ys.Min() < 2.0, "the items did not share one row");
        Assert.Equal(4, new[] { "AAA", "BBB", "CCC", "DDD" }.Select(t => Math.Round(laid[t].X)).Distinct().Count());
        // The first item in the first track — the cursor starts there, not one column in.
        Assert.Equal(rowFlow["AAA"].X, laid["AAA"].X, 1);
        Assert.Empty(laid.Reports);
    }

    /// <summary>The bound: a start line past 64 is reported and the item placed automatically; column flow past 64 columns puts the rest in rows below, reported once.</summary>
    [Fact]
    public void PastSixtyFourColumns_TheItemIsPlacedAutomatically_AndColumnFlowFallsBelow_Reported()
    {
        var far = Lay($"<div style='{TwoTracks}'><div style='grid-column:1000'>AAA</div><div>BBB</div></div>");
        Assert.True(Math.Abs(far["AAA"].Y - far["BBB"].Y) < 2.0 && far["BBB"].X > far["AAA"].X, "AAA was not placed automatically at the first track");
        Assert.Contains(far.Reports, r => r.Contains("past 64", StringComparison.Ordinal));

        // 128 items fill 64 columns of two rows; the 129th (Q) and 130th (Z) have nowhere to go.
        // One small letter each, so a 64th of the band holds a word.
        string items = string.Concat(Enumerable.Range(0, 128).Select(_ => "<div>x</div>")) + "<div>Q</div><div>Z</div>";
        var flow = Lay($"<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:auto auto;grid-auto-flow:column;font-size:8px'>{items}</div>");
        Assert.True(flow["Q"].Y < flow["x"].Y - 2.0, "the 129th item did not fall below the two rows");
        Assert.True(Math.Abs(flow["Q"].X - flow["x"].X) < 1.0, "the 129th item did not start a row at the first column");
        Assert.True(Math.Abs(flow["Z"].Y - flow["Q"].Y) < 2.0 && flow["Z"].X > flow["Q"].X, "the 130th item did not follow in that row");
        Assert.Single(flow.Reports, r => r.Contains("64 columns", StringComparison.Ordinal));
    }

    /// <summary>
    /// A span from a line inside the bound reaching past it is clamped to the 64 columns: with
    /// <c>60 / span 10</c> the grid has 64 columns, so column 60 starts 59/64 of the way across —
    /// 59/32 of a two-column control's track; left to grow to 69 columns it would start 59/69 of the way.
    /// </summary>
    [Fact]
    public void ASpanPastTheColumnBound_IsClampedToIt()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;font-size:8px'><div style='grid-column:1'>BBB</div><div style='grid-column:60 / span 10'>AAA</div></div>");
        var control = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;font-size:8px'><div>BBB</div><div>CCC</div></div>");

        double track = control["CCC"].X - control["BBB"].X;
        Assert.Equal(59.0 / 32, (laid["AAA"].X - laid["BBB"].X) / track, 2);
        Assert.Empty(laid.Reports);
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
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
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);
        return laid;
    }
}
