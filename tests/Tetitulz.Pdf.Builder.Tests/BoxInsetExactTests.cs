// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.BoxInsetIsExact"/>: a paragraph's box is drawn at least
/// <see cref="Paragraph.DefaultBoxInsetPt"/> from its text — Word's paragraph-border spacing — unless
/// the insets are stated exactly, as CSS states them, where 0 is 0.
/// </summary>
public class BoxInsetExactTests
{
    /// <summary>How far the left border is drawn from the text's left edge.</summary>
    private static double LeftGap(bool exact)
    {
        var p = new Paragraph { BorderLeft = new Border(1, "#000000"), BoxInsetIsExact = exact };
        p.Runs.Add(new TextRun { Text = "A" });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        double border = PdfContentProbe.StrokedSegments(c).Single(s => s.IsVertical).X0;
        return PdfContentProbe.XOf(c, "A") - border;
    }

    /// <summary>By default the border keeps its 2pt floor from the text.</summary>
    [Fact]
    public void TheDefaultKeepsTheFloor()
    {
        Assert.False(new Paragraph().BoxInsetIsExact);
        Assert.Equal(Paragraph.DefaultBoxInsetPt, LeftGap(exact: false), 2);
    }

    /// <summary>An exact inset of 0 draws the border on the text's edge.</summary>
    [Fact]
    public void AnExactZeroInsetIsZero() => Assert.Equal(0, LeftGap(exact: true), 2);

    /// <summary>
    /// Shrink-to-fit measures an exact box without the floor: the same bordered paragraph is
    /// 2 × <see cref="Paragraph.DefaultBoxInsetPt"/> narrower when its insets are exact.
    /// </summary>
    [Fact]
    public void AnExactBoxShrinksWithoutTheFloor()
    {
        static Paragraph Para(bool exact)
        {
            var p = new Paragraph { BorderLeft = new Border(1, "#000000"), BoxInsetIsExact = exact };
            p.Runs.Add(new TextRun { Text = "Word" });
            return p;
        }
        var exporter = new Tetitulz.Pdf.Builder.Export.PdfExporter();
        Assert.Equal(exporter.MeasureShrinkToFitWidth(Para(false), 400) - 2 * Paragraph.DefaultBoxInsetPt,
                     exporter.MeasureShrinkToFitWidth(Para(true), 400), 3);
    }
}
