// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>display: grid</c> in the managed engine: a grid with a column template places its items in
/// rows across those columns.
/// </summary>
/// <remarks>
/// <para><b>Measured glyph positions, not structure.</b> Before this, a grid container fell through
/// to ordinary block flow and produced a perfectly valid PDF with every item stacked — so
/// "the element is there", "the text is there" and "the page is not blank" all passed. The only
/// thing that separates a grid from a stack is where the glyphs landed. This is the same lesson
/// <c>FlexLayoutTests</c> records, and grid is the third feature in this mapper to need it.</para>
/// <para><b>A grid without a usable template needs the opposite assertion</b>, and it is here for
/// the same reason as flex-column: one column of blocks IS ordinary flow, so that case was already
/// right and a change breaking it would otherwise be invisible.</para>
/// </remarks>
public class GridLayoutTests
{
    /// <summary>
    /// The claim: two items under a two-column template share a line, second to the right of first.
    /// </summary>
    [Fact]
    public void ATwoColumnGrid_PlacesItsItemsSideBySide()
    {
        var words = Words(
            "<div style='display:grid;grid-template-columns:1fr 1fr'>" +
            "<div>AAA</div><div>BBB</div></div>");

        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(Math.Abs(a.Y - b.Y) < 2.0,
            $"items are at y={a.Y:F1} and y={b.Y:F1}, so they were stacked rather than placed in " +
            "two columns");
        Assert.True(b.X > a.X, $"BBB at x={b.X:F1} is not right of AAA at x={a.X:F1}");
    }

    /// <summary>
    /// Items wrap to a new row once the tracks are full — the half that makes this a grid rather
    /// than a flex row. Four items across two columns give two rows, so the third item sits back at
    /// the first item's x and below it.
    /// </summary>
    [Fact]
    public void ItemsPastTheTrackCount_WrapToTheNextRow()
    {
        var words = Words(
            "<div style='display:grid;grid-template-columns:1fr 1fr'>" +
            "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>");

        var a = Word(words, "AAA");
        var c = Word(words, "CCC");
        var d = Word(words, "DDD");

        Assert.True(c.Y < a.Y - 2.0,
            $"the third item is at y={c.Y:F1}, not on a new row below the first at y={a.Y:F1}");
        Assert.True(Math.Abs(c.X - a.X) < 2.0,
            $"the third item is at x={c.X:F1}, not back in the first column at x={a.X:F1}");
        Assert.True(Math.Abs(c.Y - d.Y) < 2.0, "the third and fourth items are not on one row");
    }

    /// <summary>
    /// <c>repeat()</c> is expanded, so <c>repeat(3, 1fr)</c> really is three columns and the fourth
    /// item starts a second row. Without this, a mapper that ignored repeat and found no tracks
    /// would decline and stack everything — passing nothing above, but silently.
    /// </summary>
    [Fact]
    public void RepeatIsExpandedIntoItsTracks()
    {
        var words = Words(
            "<div style='display:grid;grid-template-columns:repeat(3, 1fr)'>" +
            "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>");

        var a = Word(words, "AAA");
        var c = Word(words, "CCC");
        var d = Word(words, "DDD");

        Assert.True(Math.Abs(a.Y - c.Y) < 2.0, "three tracks did not hold three items on one row");
        Assert.True(d.Y < a.Y - 2.0, "the fourth item did not start a second row");
    }

    /// <summary>
    /// A grid with no usable column template falls through to block flow. One column of blocks is
    /// ordinary flow, and wrapping it in a one-column table would add a tagging layer that says
    /// nothing about the document.
    /// </summary>
    [Fact]
    public void AGridWithNoTemplate_FallsThroughToBlockFlow()
    {
        var words = Words("<div style='display:grid'><div>AAA</div><div>BBB</div></div>");

        var a = Word(words, "AAA");
        var b = Word(words, "BBB");

        Assert.True(Math.Abs(a.Y - b.Y) > 2.0,
            "a grid with no column template placed its items side by side");
    }

    /// <summary>
    /// Fixed track widths are honoured, so a wide first column really does push the second item
    /// further right than an even split would. Asserted against the equal-track case rather than
    /// against an absolute x, which would be a page-geometry assertion in disguise.
    /// </summary>
    [Fact]
    public void FixedTrackWidths_MoveTheSecondColumn()
    {
        double even = Word(Words(
            "<div style='display:grid;grid-template-columns:1fr 1fr'>" +
            "<div>AAA</div><div>BBB</div></div>"), "BBB").X;

        double lopsided = Word(Words(
            "<div style='display:grid;grid-template-columns:3fr 1fr'>" +
            "<div>AAA</div><div>BBB</div></div>"), "BBB").X;

        Assert.True(lopsided > even + 10,
            $"a 3fr first track put the second column at x={lopsided:F1}, barely past the even " +
            $"split's x={even:F1}");
    }

    /// <summary>
    /// What the mapping cannot express is <b>reported</b>, not silently dropped — and the result
    /// still looks like a laid-out grid, which is exactly when a caller needs telling.
    /// </summary>
    /// <remarks>
    /// <b>Five rows have left this theory as their features became applied</b>, each time because a
    /// report would otherwise fire on input that lost nothing. <c>grid-column: span 2</c> went when
    /// spanning was wired to <c>TableCell.GridSpan</c>; <c>grid-column: 2 / 4</c> went when
    /// placement was (<c>GridPlacementTests</c>); <c>gap</c> and <c>grid-template-areas</c> went in
    /// batch 86 (<see cref="AColumnGap_MovesTheSecondTracksContentByTheGap"/> and the placement tests);
    /// <c>grid-auto-flow: column</c> with no row template went in batch 112, when column flow gained
    /// implicit columns and one implicit row (<c>GridImplicitTracksTests</c>).
    /// </remarks>
    [Theory]
    [InlineData("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:100px 100px'>" +
                "<div>AAA</div><div>BBB</div></div>", "grid-template-rows")]
    public void UnsupportedGridFeatures_AreReported(string html, string expected)
    {
        var reports = new List<string>();
        Convert(html, d => { lock (reports) reports.Add(d.Detail ?? ""); });

        lock (reports)
            Assert.Contains(reports, r => r.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>column-gap</c> (§10.1, batch 86) is the distance between two tracks' content. The cells
    /// add no padding of their own, so the second track's text moves right by the gap's half — 40px
    /// is 30pt, so by 15pt — and the first track's does not move; nothing is reported. <c>gap</c>'s one value and the legacy <c>grid-gap</c> read the same.
    /// Before batch 86 the property was reported and the tracks left adjacent.
    /// </summary>
    [Theory]
    [InlineData("column-gap:40px")]
    [InlineData("gap:40px")]
    [InlineData("grid-gap:40px")]
    [InlineData("gap:10px 40px")]
    public void AColumnGap_MovesTheSecondTracksContentByTheGap(string declaration)
    {
        var plain = Words(Two(""));
        var reports = new List<string>();
        var gapped = Words(Two(declaration), reports);

        Assert.Equal(Word(plain, "BBB").X + 15, Word(gapped, "BBB").X, 1);
        Assert.Equal(Word(plain, "AAA").X, Word(gapped, "AAA").X, 1);
        Assert.Equal(Word(gapped, "AAA").Y, Word(gapped, "BBB").Y, 1);
        Assert.Empty(reports);
    }

    /// <summary>
    /// <c>row-gap</c> is the distance between two rows' content: the second row's text moves down
    /// by the gap — 40px is 30pt — and the first row's does not move. <c>gap</c>'s first value is
    /// the row gap.
    /// </summary>
    [Theory]
    [InlineData("row-gap:40px")]
    [InlineData("gap:40px 0")]
    public void ARowGap_MovesTheSecondRowsContentDownByTheGap(string declaration)
    {
        var plain = Words(Four(""));
        var gapped = Words(Four(declaration));

        Assert.Equal(Word(plain, "CCC").Y - 30, Word(gapped, "CCC").Y, 1);
        Assert.Equal(Word(plain, "AAA").Y, Word(gapped, "AAA").Y, 1);
        Assert.Equal(Word(plain, "BBB").X, Word(gapped, "BBB").X, 1);   // a row gap moves nothing sideways
    }

    /// <summary>
    /// A narrow gap is drawn as narrow as it is — 8px (6pt) moves the second track 3pt and the
    /// second row 6pt, and nothing is reported. It was floored at the cells' two 3pt paddings and
    /// reported, when they had them. No gap, or <c>normal</c>, reports nothing and moves nothing.
    /// </summary>
    [Theory]
    [InlineData("column-gap:8px", 3, 0)]
    [InlineData("row-gap:8px", 0, 6)]
    public void ANarrowGap_IsDrawnExactly(string declaration, double right, double down)
    {
        var plain = Words(Four(""));
        var reports = new List<string>();
        var gapped = Words(Four(declaration), reports);
        var normal = new List<string>();
        var normalWords = Words(Four("gap:normal"), normal);

        Assert.Equal(Word(plain, "BBB").X + right, Word(gapped, "BBB").X, 1);
        Assert.Equal(Word(plain, "CCC").Y - down, Word(gapped, "CCC").Y, 1);
        Assert.Empty(reports);
        Assert.Empty(normal);
        Assert.Equal(Word(plain, "BBB").X, Word(normalWords, "BBB").X, 1);   // a grid's normal is 0, not an em
        Assert.Equal(Word(plain, "CCC").Y, Word(normalWords, "CCC").Y, 1);
    }

    private static string Two(string declaration)
        => "<div style='display:grid;grid-template-columns:1fr 1fr;" + declaration + "'><div>AAA</div><div>BBB</div></div>";

    private static string Four(string declaration)
        => "<div style='display:grid;grid-template-columns:1fr 1fr;" + declaration + "'><div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>";

    /// <summary>
    /// The counterpart, and the one that keeps the reports worth reading: a grid using nothing but
    /// what this mapping supports reports nothing. A channel that fires on conforming input is one
    /// a caller learns to ignore.
    /// </summary>
    [Fact]
    public void APlainGrid_ReportsNothing()
    {
        var reports = new List<string>();
        Convert("<div style='display:grid;grid-template-columns:1fr 1fr'>" +
                "<div>AAA</div><div>BBB</div></div>",
                d => { lock (reports) reports.Add(d.Detail ?? ""); });

        lock (reports)
            Assert.DoesNotContain(reports, r => r.Contains("grid", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Adversarial: <c>repeat()</c> takes an author-supplied multiplier, so it sizes an
    /// allocation — <c>repeat(1000000, 1fr)</c> is three words of CSS.
    ///
    /// <para>Asserted on <b>the guard's own evidence</b> — a reported truncation — rather than on a
    /// wall clock, and rather than on the layout. The first version of this test did assert the
    /// layout, expecting both items on one row, and that was wrong twice over: 64 tracks across a
    /// page are a few points each, so the text does not fit and is not laid out at all. Which is
    /// exactly why the truncation has to be reported: the cap keeps the conversion safe, and the
    /// result is not the grid the author wrote.</para>
    /// </summary>
    [Fact]
    public void AnOversizedRepeat_IsTruncatedAndSaysSo()
    {
        var reports = new List<string>();
        byte[] pdf = Convert(
            "<div style='display:grid;grid-template-columns:repeat(1000000, 1fr)'>" +
            "<div>AAA</div><div>BBB</div></div>",
            d => { lock (reports) reports.Add(d.Detail ?? ""); });

        Assert.NotEmpty(pdf);
        lock (reports)
            Assert.Contains(reports, r => r.Contains("truncated", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------

    private static PdfTextWord Word(List<PdfTextWord> words, string text)
    {
        var hit = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(hit != null, $"'{text}' was not laid out at all");
        return hit!;
    }

    private static byte[] Convert(string html, Action<BuilderDiagnostic> onDiagnostic = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = onDiagnostic,
        });
        return output.ToArray();
    }

    private static List<PdfTextWord> Words(string html, List<string> reports = null)
    {
        byte[] pdf = Convert(html, reports == null ? null : d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) lock (reports) reports.Add(d.Detail ?? ""); });
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }
}
