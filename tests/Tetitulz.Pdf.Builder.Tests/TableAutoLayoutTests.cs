// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Table.AutoLayout"/>: auto columns sized by their content, as CSS's automatic table
/// layout does — found by rendering a Bootstrap form's label/value table beside Chrome, where the
/// managed engine split it 50/50 and Chrome gave the labels their width and the values the rest.
/// </summary>
public class TableAutoLayoutTests
{
    // ------------------------------------------------------------------ the rule

    /// <summary>Everything fits unwrapped: the width is shared in proportion to max-content.</summary>
    [Fact]
    public void WhenEverythingFitsTheWidthFollowsMaxContent()
    {
        double[] w = { 50, 50 };
        TableRenderer.ShareByContent(w, new[] { true, true }, new double[] { 10, 10 }, new double[] { 20, 60 });
        Assert.Equal(new[] { 25.0, 75.0 }, w, new Tolerance());
    }

    /// <summary>
    /// Between min- and max-content: each column its minimum, and the rest in proportion to how
    /// much more its max-content wants (10 + 50 × 70/120, 20 + 70 × 70/120).
    /// </summary>
    [Fact]
    public void BetweenMinAndMaxEachGetsItsMinimumAndAShareOfTheRest()
    {
        double[] w = { 50, 50 };
        TableRenderer.ShareByContent(w, new[] { true, true }, new double[] { 10, 20 }, new double[] { 60, 90 });
        Assert.Equal(new[] { 10 + 50 * 70 / 120.0, 20 + 70 * 70 / 120.0 }, w, new Tolerance());
    }

    /// <summary>When even the minimums do not fit, the width follows them — the overflow is spread, not put in one column.</summary>
    [Fact]
    public void WhenTheMinimumsDoNotFitTheWidthFollowsThem()
    {
        double[] w = { 50, 50 };
        TableRenderer.ShareByContent(w, new[] { true, true }, new double[] { 80, 40 }, new double[] { 90, 50 });
        Assert.Equal(new[] { 100 * 80 / 120.0, 100 * 40 / 120.0 }, w, new Tolerance());
    }

    /// <summary>An explicit column keeps its width; the auto columns share only what they were given.</summary>
    [Fact]
    public void AnExplicitColumnKeepsItsWidth()
    {
        double[] w = { 40, 30, 30 };
        TableRenderer.ShareByContent(w, new[] { false, true, true }, new double[] { 5, 5, 5 }, new double[] { 100, 10, 50 });
        Assert.Equal(new[] { 40.0, 10.0, 50.0 }, w, new Tolerance());
    }

    /// <summary>
    /// An auto column with nothing measurable (only empty cells, a nested table, spans) leaves the
    /// division as it was — content sizing would give it nothing; a measurable one beside it is the
    /// control, and the table with both measurable does change.
    /// </summary>
    [Fact]
    public void AnUnmeasurableColumnLeavesTheDivisionAlone()
    {
        double[] unmeasured = { 50, 50 };
        TableRenderer.ShareByContent(unmeasured, new[] { true, true }, new double[] { 0, 10 }, new double[] { 0, 60 });
        Assert.Equal(new[] { 50.0, 50.0 }, unmeasured, new Tolerance());

        double[] measured = { 50, 50 };
        TableRenderer.ShareByContent(measured, new[] { true, true }, new double[] { 5, 10 }, new double[] { 20, 60 });
        Assert.NotEqual(50.0, measured[0], 2);
    }

    // ------------------------------------------------------------------ a table

    private const double Size = 12;
    private const string Label = "ab";
    // Several words, so its max-content (unwrapped) and min-content (longest word) differ. Each
    // word is drawn as its own run: the column is read from where the first one starts.
    private const string Value = "second column value";
    private const string ValueStart = "second";

    private static double FirstColumnWidth(bool autoLayout, params double[] widths)
    {
        var table = new Table { AutoLayout = autoLayout };
        foreach (double w in widths) table.ColumnWidths.Add(w);
        var row = new TableRow();
        foreach (string text in new[] { Label, Value })
        {
            var cell = new TableCell();
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = text, FontSize = Size });
            cell.Blocks.Add(p);
            row.Cells.Add(cell);
        }
        table.Rows.Add(row);
        var section = new Section();
        section.Children.Add(table);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        double a = PdfContentProbe.XOf(content, Label), b = PdfContentProbe.XOf(content, ValueStart);
        Assert.False(double.IsNaN(a) || double.IsNaN(b), "a cell's text was not found");
        return b - a;
    }

    /// <summary>
    /// With auto layout the label column is its max-content's share of the table — the two texts'
    /// advance widths plus each cell's padding — where the equal split (the control, and still the
    /// default) gives it half.
    /// </summary>
    [Fact]
    public void AnAutoLayoutTableSizesItsColumnsByTheirContent()
    {
        double half = FirstColumnWidth(autoLayout: false);
        double tableWidth = 2 * half;
        double pad = 2 * TableCell.DefaultPaddingPt;
        double label = PdfExporter.AdvanceWidth(Label, false, false, Size) + pad;
        double value = PdfExporter.AdvanceWidth(Value, false, false, Size) + pad;
        Assert.True(label + value < tableWidth, "the fixture must fit unwrapped");

        Assert.Equal(label * tableWidth / (label + value), FirstColumnWidth(autoLayout: true), 0);
        Assert.True(FirstColumnWidth(autoLayout: true) < half / 2);
    }

    /// <summary>An explicit column keeps its width under auto layout; only the auto one is sized by content.</summary>
    [Fact]
    public void AnExplicitColumnKeepsItsWidthInAnAutoLayoutTable()
        => Assert.Equal(200, FirstColumnWidth(autoLayout: true, 200, 0), 1);

    /// <summary>
    /// A block image does not set its column's minimum: it is drawn at most as wide as its band,
    /// so a picture wider than the column's share is shrunk into it rather than widening it — the
    /// column keeps half, as the text-only control's does.
    /// </summary>
    [Fact]
    public void AWideImageDoesNotWidenItsColumn()
    {
        static string Render(bool withImage)
        {
            var table = new Table();
            var row = new TableRow();
            var first = new TableCell();
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = Label, FontSize = Size });
            if (withImage) p.Runs.Add(new ImageRun { Data = ExifJpeg.Build(8, 8, 1), WidthPt = 400, HeightPt = 20 });
            first.Blocks.Add(p);
            row.Cells.Add(first);
            var second = new TableCell();
            var q = new Paragraph();
            q.Runs.Add(new TextRun { Text = ValueStart, FontSize = Size });
            second.Blocks.Add(q);
            row.Cells.Add(second);
            table.Rows.Add(row);
            var section = new Section();
            section.Children.Add(table);
            var doc = new Document();
            doc.Sections.Add(section);
            return PdfContentProbe.DecodeStreams(doc.ToPdf());
        }
        static double Span(string c) => PdfContentProbe.XOf(c, ValueStart) - PdfContentProbe.XOf(c, Label);

        Assert.Equal(Span(Render(false)), Span(Render(true)), 1);
    }

    /// <summary><see cref="Table.AutoLayout"/> is off unless a producer asks: DOCX and Builder tables keep the equal split.</summary>
    [Fact]
    public void AutoLayoutIsOffByDefault() => Assert.False(new Table().AutoLayout);

    private sealed class Tolerance : System.Collections.Generic.IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => System.Math.Abs(x - y) < 0.01;
        public int GetHashCode(double obj) => 0;
    }
}
