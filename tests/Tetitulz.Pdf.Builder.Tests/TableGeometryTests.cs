// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// How wide a table's columns come out and how tall its rows come out — the four column-width arms
/// and the two row-height rules, read off the rectangles the exporter actually emitted.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over <c>TableRenderer</c> found five
/// decisions no test reached. Three of them collapse a column to <b>zero width</b> — its content
/// squeezed into nothing, on a page that still exports cleanly — and two discard a row height the
/// caller explicitly asked for. The suite covered that tables render, that cells shade, that spans
/// clamp and that nesting is bounded; it did not cover the arithmetic that decides where anything
/// goes.</para>
///
/// <para><b>Each cell is shaded so it emits a rectangle</b>, which is the only way to read a
/// column's resolved width from the output: text is drawn at a position but carries no extent, so
/// a zero-width column still draws its first glyph and looks, to any text assertion, like a
/// working table.</para>
///
/// <para><b>The four width arms are different documents, not variations.</b> All-auto, all-explicit,
/// over-specified and mixed each take their own branch, and a test of one says nothing about the
/// others — which is exactly how three of the four came to be untested while the first was
/// covered.</para>
/// </remarks>
public class TableGeometryTests
{
    /// <summary>A filled rectangle: <c>x y w h re</c>.</summary>
    private readonly record struct Rect(double X, double Y, double W, double H);

    /// <summary>
    /// The cell fills, left to right. Operands accumulate until an operator ends them; anything
    /// smaller than a point in either direction is a border stroke rather than a cell.
    /// </summary>
    private static List<Rect> CellRects(string content)
    {
        var rects = new List<Rect>();
        var pending = new List<double>();
        foreach (string token in content.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                pending.Add(v);
                continue;
            }
            if (token == "re" && pending.Count >= 4)
            {
                double[] a = pending.Skip(pending.Count - 4).ToArray();
                if (a[2] > 1 && a[3] > 1) rects.Add(new Rect(a[0], a[1], a[2], a[3]));
            }
            pending.Clear();
        }
        // A fill runs on under the next cell's (TableCellSeamTests): a column ends where the next
        // one starts.
        rects = rects.OrderBy(r => r.X).ToList();
        for (int i = 0; i + 1 < rects.Count; i++)
            if (rects[i].X + rects[i].W > rects[i + 1].X)
                rects[i] = rects[i] with { W = rects[i + 1].X - rects[i].X };
        return rects;
    }

    /// <summary>Long enough to wrap to several lines across the full band, so the row's own
    /// content measures well above any height these fixtures ask for.</summary>
    private const string Tall =
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. " +
        "How vexingly quick daft zebras jump. Sphinx of black quartz, judge my vow. " +
        "The five boxing wizards jump quickly. Jackdaws love my big sphinx of quartz.";

    /// <summary>One table, one row, <paramref name="cells"/> shaded cells.</summary>
    private static List<Rect> Render(
        int cells, double[]? columnWidths = null, double? rowHeight = null, bool exactHeight = false,
        string text = "c")
    {
        var doc = new Document();
        var section = new Section();
        var table = new Table { BorderWidth = 0 };
        if (columnWidths != null) foreach (double w in columnWidths) table.ColumnWidths.Add(w);

        var row = new TableRow { HeightPt = rowHeight, ExactHeight = exactHeight };
        for (int i = 0; i < cells; i++)
        {
            var cell = new TableCell { BackgroundColor = "#DDDDDD" };
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = text });
            cell.Paragraphs.Add(p);
            row.Cells.Add(cell);
        }
        table.Rows.Add(row);
        section.Children.Add(table);
        doc.Sections.Add(section);

        return CellRects(PdfContentProbe.DecodeStreams(doc.ToPdf()));
    }

    // ── the four column-width arms ────────────────────────────────────────────────────

    /// <summary>
    /// All columns auto: the width is shared evenly and the row spans the band. The one arm the
    /// suite already reached, kept here so the file describes the whole ladder.
    /// </summary>
    [Fact]
    public void AllAutoColumns_ShareTheBandEvenly()
    {
        var r = Render(cells: 3);

        Assert.Equal(3, r.Count);
        Assert.Equal(r[0].W, r[1].W, precision: 3);
        Assert.Equal(r[1].W, r[2].W, precision: 3);
        Assert.True(r.Sum(x => x.W) > 400, $"three auto columns must fill the band; got {r.Sum(x => x.W)}.");
    }

    /// <summary>
    /// All columns explicit: the widths are <b>normalised to fill the table</b> rather than taken
    /// literally, so a caller's 100 + 100 becomes the band rather than 200 points of table floating
    /// in a 468-point column.
    /// </summary>
    [Fact]
    public void AllExplicitColumns_AreNormalisedToFillTheTable()
    {
        var r = Render(cells: 2, columnWidths: new[] { 100.0, 100.0 });

        Assert.Equal(2, r.Count);
        Assert.Equal(r[0].W, r[1].W, precision: 3);
        Assert.True(r.Sum(x => x.W) > 400,
            $"explicit widths must be scaled to fill the table; the row came out {r.Sum(x => x.W)} " +
            "points wide, i.e. the caller's raw numbers were used unscaled.");
    }

    /// <summary>
    /// Explicit widths that already exceed the table still leave room for the auto columns — they
    /// are reserved a share rather than being crushed to nothing.
    /// </summary>
    [Fact]
    public void OverSpecifiedColumns_StillReserveWidthForTheAutoOnes()
    {
        var r = Render(cells: 3, columnWidths: new[] { 400.0, 400.0, 0.0 });

        Assert.Equal(3, r.Count);   // a zero-width column emits no readable rectangle at all
        Assert.True(r.All(x => x.W > 1),
            "every column must keep a positive width when the explicit ones over-specify; " +
            $"got {string.Join(", ", r.Select(x => x.W.ToString("0.##", CultureInfo.InvariantCulture)))}.");
    }

    /// <summary>
    /// A mix: the explicit column keeps its (scaled) width and the auto columns split what is left,
    /// evenly.
    /// </summary>
    [Fact]
    public void AutoColumns_SplitWhatTheExplicitOnesLeave()
    {
        var r = Render(cells: 3, columnWidths: new[] { 100.0, 0.0, 0.0 });

        Assert.Equal(3, r.Count);
        Assert.True(r.All(x => x.W > 1),
            "the auto columns must take a share of the leftover, not zero; " +
            $"got {string.Join(", ", r.Select(x => x.W.ToString("0.##", CultureInfo.InvariantCulture)))}.");

        // The two auto columns split the leftover evenly — the arithmetic, not just "non-zero".
        var auto = r.Skip(1).ToList();
        Assert.Equal(auto[0].W, auto[1].W, precision: 3);
    }

    // ── the two row-height rules ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="TableRow.ExactHeight"/> is exact: the row is that tall whatever its content
    /// measures, which is what makes a fixed-layout form reproducible.
    /// </summary>
    /// <remarks>
    /// <b>The content has to be TALLER than the requested height, or the test cannot fail.</b> A
    /// short row asked for 60 points comes out 60 tall under either rule — exact height and
    /// minimum height agree — so a fixture built that way is accidentally equivalent, and the
    /// first version of this test was. Exactness is only observable where the two rules disagree,
    /// which is when the content would otherwise win.
    /// </remarks>
    [Fact]
    public void AnExactRowHeight_IsUsedVerbatim_EvenWhenTheContentIsTaller()
    {
        var natural = Render(cells: 1, text: Tall);
        var exact = Render(cells: 1, rowHeight: 25, exactHeight: true, text: Tall);

        Assert.Single(exact);
        Assert.True(natural[0].H > 40,
            $"the fixture's own content height ({natural[0].H}) must exceed the 25 points asked " +
            "for, or exact and minimum give the same answer and this cannot discriminate.");
        Assert.Equal(25, exact[0].H, precision: 3);
    }

    /// <summary>
    /// The contrast that gives the test above its meaning: <i>without</i>
    /// <see cref="TableRow.ExactHeight"/>, the same too-tall content grows the row past the
    /// requested height instead of being held to it.
    /// </summary>
    [Fact]
    public void WithoutExact_TallContentGrowsTheRowPastTheRequestedHeight()
    {
        var floored = Render(cells: 1, rowHeight: 25, exactHeight: false, text: Tall);

        Assert.Single(floored);
        Assert.True(floored[0].H > 40,
            $"a non-exact height is a floor, so tall content must grow the row; got {floored[0].H}.");
    }

    /// <summary>
    /// Without <see cref="TableRow.ExactHeight"/> the height is a <b>minimum</b>: content may grow
    /// the row, but a short row is still padded out to it.
    /// </summary>
    [Fact]
    public void ARowHeightWithoutExact_IsAMinimum()
    {
        var natural = Render(cells: 1);
        var floored = Render(cells: 1, rowHeight: 60, exactHeight: false);

        Assert.Single(floored);
        Assert.True(natural[0].H < 50,
            $"the fixture's natural row height ({natural[0].H}) must be well under 60 for this to " +
            "discriminate.");
        Assert.True(floored[0].H >= 60 - 0.01,
            $"a row asking for 60 points must not come out {floored[0].H} tall.");
    }
}
