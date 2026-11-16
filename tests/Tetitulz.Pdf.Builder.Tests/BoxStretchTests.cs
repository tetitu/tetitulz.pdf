// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.StretchesToCell"/>: the outermost box of a cell reaches the cell's bottom,
/// as a stretched flex item does — beside a cell three lines tall, a one-line cell's box is three
/// lines tall. Without it (the control) the box is its own line's height.
/// </summary>
public class BoxStretchTests
{
    private static Paragraph Para(string text)
    {
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 10 };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    private static double FillHeight(bool stretch, double marginBottom = 0, bool nested = false)
    {
        var a = new TableCell { InnerPaddingPt = 0 };
        var box = new BoxStart { Shading = "#0000FF", StretchesToCell = stretch && !nested, MarginBottom = marginBottom };
        a.Blocks.Add(box);
        if (nested) a.Blocks.Add(new BoxStart { Shading = "#00FF00", StretchesToCell = stretch });
        a.Blocks.Add(Para("A"));
        if (nested) a.Blocks.Add(new BoxEnd());
        a.Blocks.Add(new BoxEnd());
        var b = new TableCell { InnerPaddingPt = 0 };
        b.Blocks.Add(Para("B1"));
        b.Blocks.Add(Para("B2"));
        b.Blocks.Add(Para("B3"));
        var row = new TableRow();
        row.Cells.Add(a);
        row.Cells.Add(b);
        var t = new Table { MinRowHeightPt = 0, BorderWidth = 0 };
        t.Rows.Add(row);
        var section = new Section();
        section.Children.Add(t);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        // The last fill is the innermost box's (a nested one is drawn inside the outer).
        var heights = Regex.Matches(c, @"[-\d.]+ [-\d.]+ [-\d.]+ ([-\d.]+) re\s+f").Cast<Match>()
            .Select(m => System.Math.Abs(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))).ToList();
        return nested ? heights.Last() : heights.First();
    }

    [Fact]
    public void AStretchedBoxReachesTheCellsBottom() => Assert.Equal(30, FillHeight(stretch: true), 1);

    [Fact]
    public void WithoutItTheBoxIsItsBlocksHeight() => Assert.Equal(10, FillHeight(stretch: false), 1);

    /// <summary>The box's own bottom margin stays inside the cell: 5pt of it leaves the fill 25pt.</summary>
    [Fact]
    public void ItsBottomMarginStaysInside() => Assert.Equal(25, FillHeight(stretch: true, marginBottom: 5), 1);

    /// <summary>A box inside another does not stretch, flag or no flag: only the cell's outermost box is the item.</summary>
    [Fact]
    public void ANestedBoxDoesNotStretch() => Assert.Equal(10, FillHeight(stretch: true, nested: true), 1);
}
