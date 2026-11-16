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
/// Implicit column tracks before the explicit grid (CSS Grid §8.3, batch 122): a negative
/// <c>grid-column</c> line counting back past the grid's start, or an end line before it, adds the
/// tracks it reaches at the grid's start and moves every line over; the new tracks are sized by
/// <c>grid-auto-columns</c> from its last value backwards (§7.6). Before batch 122 such a line was
/// reported and the item placed automatically, as a negative row line was until batch 123.
/// </summary>
public class GridLeadingImplicitTracksTests
{
    private const string TwoTracks = "display:grid;grid-template-columns:100px 100px";

    /// <summary>
    /// On two 100px tracks line -4 is one before the first: the item takes a new first track, as wide
    /// as the explicit average, and the automatic items follow it — BBB one track over, CCC two —
    /// where without it BBB starts the row. The table shares the band over the three equal tracks,
    /// so each is two thirds of the two-track control's. Nothing is reported.
    /// </summary>
    [Fact]
    public void ALineBeforeTheGrid_AddsATrackThere_AndMovesTheRestOver()
    {
        var laid = Lay($"<div style='{TwoTracks}'><div style='grid-column:-4'>AAA</div><div>BBB</div><div>CCC</div></div>");
        var control = Lay($"<div style='{TwoTracks}'><div>BBB</div><div>CCC</div></div>");

        double track = control["CCC"].X - control["BBB"].X;   // one of two
        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0 && Math.Abs(laid["AAA"].Y - laid["CCC"].Y) < 2.0, "the three items did not share the row");
        Assert.Equal(control["BBB"].X, laid["AAA"].X, 1);
        Assert.Equal(2.0 / 3, (laid["BBB"].X - laid["AAA"].X) / track, 2);
        Assert.Equal(2.0 / 3, (laid["CCC"].X - laid["BBB"].X) / track, 2);
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// The leading tracks are sized from <c>grid-auto-columns</c>' end backwards: with <c>50px 100px</c>
    /// and two tracks before the grid (line -5), the one next to the grid is 100px and the first
    /// 50px — so BBB, automatic in the second, is half as far after AAA as CCC is after BBB (the band
    /// shared over 50:100:100:100). With one value, <c>50px</c>, both leading tracks are equal.
    /// </summary>
    [Fact]
    public void TheLeadingTracks_AreSizedFromTheAutoColumnsEndBackwards()
    {
        var laid = Lay($"<div style='{TwoTracks};grid-auto-columns:50px 100px'><div style='grid-column:-5'>AAA</div><div>BBB</div><div>CCC</div></div>");
        var one = Lay($"<div style='{TwoTracks};grid-auto-columns:50px'><div style='grid-column:-5'>AAA</div><div>BBB</div><div>CCC</div></div>");

        Assert.Equal(0.5, (laid["BBB"].X - laid["AAA"].X) / (laid["CCC"].X - laid["BBB"].X), 2);
        Assert.Equal(1.0, (one["BBB"].X - one["AAA"].X) / (one["CCC"].X - one["BBB"].X), 2);
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// Every definite column line moves over by the tracks added: with AAA at -4 and BBB at line 1 on
    /// two tracks, AAA takes the new first track and BBB the one after it, on one row. A shift one short
    /// would leave AAA on line 0 — automatic — and put BBB first, with AAA after it or on the next row.
    /// </summary>
    [Fact]
    public void TheExplicitLines_MoveOverByTheTracksAdded()
    {
        var laid = Lay($"<div style='{TwoTracks}'><div style='grid-column:-4'>AAA</div><div style='grid-column:1'>BBB</div></div>");
        var control = Lay($"<div style='{TwoTracks}'><div style='grid-column:1'>BBB</div><div>CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "BBB did not share AAA's row");
        Assert.Equal(control["BBB"].X, laid["AAA"].X, 1);
        Assert.Equal(2.0 / 3, (laid["BBB"].X - laid["AAA"].X) / (control["CCC"].X - control["BBB"].X), 2);
        Assert.Empty(laid.Reports);
    }

    /// <summary>A span across the start — <c>-4 / 2</c> — covers the implicit track and the first explicit one: BBB lands two tracks over, where <c>-4</c> alone puts it one over.</summary>
    [Fact]
    public void ASpanAcrossTheStart_CoversTheImplicitAndTheFirstExplicitTrack()
    {
        var laid = Lay($"<div style='{TwoTracks}'><div style='grid-column:-4 / 2'>AAA</div><div>BBB</div></div>");
        var single = Lay($"<div style='{TwoTracks}'><div style='grid-column:-4'>AAA</div><div>BBB</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "BBB did not share AAA's row");
        Assert.Equal(2.0, (laid["BBB"].X - laid["AAA"].X) / (single["BBB"].X - single["AAA"].X), 2);
        Assert.Empty(laid.Reports);
    }

    // The row axis: GridLeadingImplicitRowsTests (batch 123; before it a row line before the grid was reported here).

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
