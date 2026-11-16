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
/// Implicit rows before the explicit grid (CSS Grid §8.3, batch 123): a negative <c>grid-row</c>
/// line counting back past the grid's start, or an end line before it, adds the rows it reaches at
/// the grid's start and moves every definite row line down; the new rows are sized by
/// <c>grid-auto-rows</c> from its last value backwards (§7.6), which also sizes the implicit rows
/// after the grid, in turn. Before batch 123 such a line was reported and the item placed
/// automatically, as a column line before the grid was before batch 122.
/// </summary>
/// <remarks>
/// A grid cell's content sits at its top, so the distance from one row's text to the next row's is
/// the first row's height: a 40px track is 30pt from its text to the text below it. A pixel is 0.75pt.
/// </remarks>
public class GridLeadingImplicitRowsTests
{
    private const string TwoTracks = "display:grid;grid-template-columns:100px 100px";

    /// <summary>
    /// Under two auto rows, line -4 is one before the first: AAA takes a new first row, and BBB and CCC,
    /// on lines 1 and 2, move down one each — AAA above BBB above CCC in the first track. Nothing is
    /// reported. Without the line AAA would be placed automatically, beside BBB on the first row.
    /// </summary>
    [Fact]
    public void ARowLineBeforeTheGrid_AddsARowThere_AndMovesTheRestDown()
    {
        var laid = Lay($"<div style='{TwoTracks};grid-template-rows:auto auto'><div style='grid-row:-4'>AAA</div><div style='grid-row:1'>BBB</div><div style='grid-row:2'>CCC</div></div>");
        var control = Lay($"<div style='{TwoTracks};grid-template-rows:auto auto'><div>AAA</div><div style='grid-row:1'>BBB</div><div style='grid-row:2'>CCC</div></div>");

        Assert.True(laid["AAA"].Y > laid["BBB"].Y + 2.0, $"AAA at y={laid["AAA"].Y:F1} is not above BBB at y={laid["BBB"].Y:F1}");
        Assert.True(laid["BBB"].Y > laid["CCC"].Y + 2.0, "BBB is not above CCC");
        Assert.Equal(laid["BBB"].X, laid["AAA"].X, 1);
        Assert.Empty(laid.Reports);

        Assert.True(Math.Abs(control["AAA"].Y - control["BBB"].Y) < 2.0, "the control's automatic AAA did not share BBB's row");
    }

    /// <summary>
    /// The row template indexes past the rows added before the grid: under <c>40px 100px</c>, with
    /// AAA on line -4, BBB on line 1 is in the 40px row and CCC on line 2 in the 100px row — BBB is
    /// 30pt above CCC and CCC 75pt above DDD on line 3 (an implicit row after the grid). Indexed from
    /// the first row, BBB's row would be the 100px one.
    /// </summary>
    [Fact]
    public void TheRowTemplate_IndexesPastTheRowsBefore()
    {
        var laid = Lay($"<div style='{TwoTracks};grid-template-rows:40px 100px'><div style='grid-row:-4'>AAA</div>" +
                       "<div style='grid-row:1'>BBB</div><div style='grid-row:2'>CCC</div><div style='grid-row:3'>DDD</div></div>");

        Assert.Equal(30.0, laid["BBB"].Y - laid["CCC"].Y, 1);
        Assert.Equal(75.0, laid["CCC"].Y - laid["DDD"].Y, 1);
        Assert.DoesNotContain(laid.Reports, r => r.StartsWith("grid-row", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>grid-auto-rows: 80px 40px</c> sizes the two rows line -4 adds before a one-row template from
    /// its end backwards — the row next to the grid 40px, the first 80px — and the rows after the grid
    /// from its start: AAA (line -4) is 60pt above CCC (line -3), CCC 30pt above BBB (line 1, the 40px
    /// template row), BBB 30pt above DDD (line 2, the first implicit row after, 80px) and DDD 60pt
    /// above EEE (line 3). With one value, <c>40px</c>, every implicit row is 30pt.
    /// </summary>
    [Fact]
    public void TheImplicitRows_AreSizedByAutoRows_BackwardsBeforeTheGridAndForwardsAfterIt()
    {
        const string Items = "<div style='grid-row:-4'>AAA</div><div style='grid-row:-3'>CCC</div><div style='grid-row:1'>BBB</div>" +
                             "<div style='grid-row:2'>DDD</div><div style='grid-row:3'>EEE</div></div>";
        var laid = Lay($"<div style='{TwoTracks};grid-template-rows:40px;grid-auto-rows:80px 40px'>{Items}");
        var one = Lay($"<div style='{TwoTracks};grid-template-rows:40px;grid-auto-rows:40px'>{Items}");

        Assert.Equal(60.0, laid["AAA"].Y - laid["CCC"].Y, 1);
        Assert.Equal(30.0, laid["CCC"].Y - laid["BBB"].Y, 1);
        Assert.Equal(30.0, laid["BBB"].Y - laid["DDD"].Y, 1);
        Assert.Equal(60.0, laid["DDD"].Y - laid["EEE"].Y, 1);

        Assert.Equal(30.0, one["AAA"].Y - one["CCC"].Y, 1);
        Assert.Equal(30.0, one["DDD"].Y - one["EEE"].Y, 1);
        Assert.DoesNotContain(laid.Reports, r => r.StartsWith("grid-row", StringComparison.Ordinal));
    }

    /// <summary>
    /// A span across the start — <c>-4 / 2</c> under two auto rows — covers the implicit row and the first
    /// explicit one in the first track, so BBB on line 1 takes the second track beside it; under <c>-4</c>
    /// alone BBB is in the first track, below AAA.
    /// </summary>
    [Fact]
    public void ASpanAcrossTheStart_CoversTheImplicitAndTheFirstExplicitRow()
    {
        var laid = Lay($"<div style='{TwoTracks};grid-template-rows:auto auto'><div style='grid-row:-4 / 2'>AAA</div><div style='grid-row:1'>BBB</div></div>");
        var single = Lay($"<div style='{TwoTracks};grid-template-rows:auto auto'><div style='grid-row:-4'>AAA</div><div style='grid-row:1'>BBB</div></div>");

        Assert.True(laid["BBB"].X > laid["AAA"].X + 20.0, "BBB did not move to the second track beside AAA's span");
        Assert.Equal(single["AAA"].X, single["BBB"].X, 1);
        Assert.True(single["AAA"].Y > single["BBB"].Y + 2.0, "under -4 alone BBB is not below AAA");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// Column flow runs down the rows before the grid too: with one explicit row and AAA on line -3, the
    /// cursor has two rows per column, so BBB goes below AAA in the first track and CCC starts the
    /// second track beside AAA; over the explicit row alone BBB would be beside AAA.
    /// </summary>
    [Fact]
    public void ColumnFlow_RunsDownTheRowsBeforeTheGridToo()
    {
        var laid = Lay($"<div style='{TwoTracks};grid-template-rows:auto;grid-auto-flow:column'><div style='grid-row:-3'>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>");

        Assert.Equal(laid["AAA"].X, laid["BBB"].X, 1);
        Assert.True(laid["AAA"].Y > laid["BBB"].Y + 2.0, "BBB is not below AAA in the first track");
        Assert.True(Math.Abs(laid["AAA"].Y - laid["CCC"].Y) < 2.0 && laid["CCC"].X > laid["AAA"].X + 20.0, "CCC does not start the second track beside AAA");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A span from a line before the grid is clamped to the 1024 rows this lays out: <c>-1000 / span 2000</c>
    /// with no row template puts AAA 999 rows before the grid and spans it to the bound, 25 rows, so BBB
    /// on line 1 — row 1000 — is below it in the first track, the empty rows between them no height, on
    /// one page. Clamped only to the 1024 rows from the first, the span would still cover row 1000 and
    /// push BBB to the second track.
    /// </summary>
    [Fact]
    public void ASpanFromBeforeTheGrid_IsClampedToTheRowBound()
    {
        var laid = Lay($"<div style='{TwoTracks}'><div style='grid-row:-1000 / span 2000'>AAA</div><div style='grid-row:1'>BBB</div></div>");

        Assert.True(laid["AAA"].Y > laid["BBB"].Y + 2.0, "BBB is not below AAA");
        Assert.Equal(laid["AAA"].X, laid["BBB"].X, 1);
        Assert.InRange(laid.Pages, 1, 2);
        Assert.Empty(laid.Reports);
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
        public readonly List<string> Reports = new();
        public int Pages;
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
        var pages = PdfOperations.ExtractStructuredText(input).ToList();
        laid.Pages = pages.Count;
        foreach (var word in pages.SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);
        return laid;
    }
}
