// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="InlineBox.OccupiesLine"/> — CSS's <c>inline-block</c>: the box's margin box is part of
/// its line, so a line holding a box with 20pt of padding and margins above and below its text is
/// taller by them, where an inline box's (the control) overlap the lines round it. Its horizontal
/// margins take room beside it. Measured with <see cref="Export.PdfExportOptions.HalfLeading"/>, as
/// the HTML converter draws: the text is centred in its line, so the extra room is shared above and
/// below it (without it the baseline sits at the line's foot and the room is all above).
/// </summary>
public class InlineBlockTests
{
    private static InlineBox Box(bool occupies) => new InlineBox
    {
        Background = "#CCCCCC",
        PaddingTop = 5, PaddingBottom = 5, MarginTop = 5, MarginBottom = 5,
        MarginLeft = 10, MarginRight = 20,
        OccupiesLine = occupies,
    };

    private static string Render(InlineBox? box)
    {
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 12 };
        p.Runs.Add(new TextRun { Text = "AAA", Box = box });
        p.Runs.Add(new TextRun { Text = "BBB" });
        p.Runs.Add(new LineBreak());
        p.Runs.Add(new TextRun { Text = "CCC" });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new Export.PdfExportOptions { HalfLeading = true }));
    }

    private static double Drop(string c) => PdfContentProbe.YOf(c, "AAA") - PdfContentProbe.YOf(c, "CCC");

    /// <summary>
    /// The line is as tall as the box's margin box — its glyph box and 20pt, past the 12pt line
    /// height — and centred, so the next baseline is half the difference further down.
    /// </summary>
    [Fact]
    public void ItsLineIsAsTallAsItsMarginBox()
    {
        double plain = Drop(Render(null));
        Assert.Equal(12, plain, 1);
        Assert.True(Drop(Render(Box(occupies: true))) > plain + 8, $"{Drop(Render(Box(true)))}");
    }

    /// <summary>The control: the same box as an inline one leaves the line its 12pt.</summary>
    [Fact]
    public void AnInlineBoxLeavesTheLineAlone() => Assert.Equal(12, Drop(Render(Box(occupies: false))), 1);

    /// <summary>
    /// Its margins count when the line is broken: a 440pt right margin leaves the next word no room
    /// on the 468pt line, and it wraps — where it would fit beside the box without them.
    /// </summary>
    [Fact]
    public void ItsMarginsCountTowardTheLinesWidth()
    {
        var p = new Paragraph { SpacingAfter = 0, LineSpacingPt = 12 };
        p.Runs.Add(new TextRun { Text = "AAA", Box = new InlineBox { MarginRight = 440, OccupiesLine = true } });
        p.Runs.Add(new TextRun { Text = " BBB" });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        Assert.True(PdfContentProbe.YOf(c, "BBB") < PdfContentProbe.YOf(c, "AAA") - 1, "BBB stayed on the box's line");
    }

    /// <summary>Its horizontal margins take room: the next word is 30pt further on than after a bare run.</summary>
    [Fact]
    public void ItsHorizontalMarginsTakeRoom()
    {
        string plain = Render(null), boxed = Render(Box(occupies: true));
        double plainGap = PdfContentProbe.XOf(plain, "BBB") - PdfContentProbe.XOf(plain, "AAA");
        double boxedGap = PdfContentProbe.XOf(boxed, "BBB") - PdfContentProbe.XOf(boxed, "AAA");
        Assert.Equal(plainGap + 20, boxedGap, 1);                                           // the right margin after the box
        Assert.Equal(PdfContentProbe.XOf(plain, "AAA") + 10, PdfContentProbe.XOf(boxed, "AAA"), 1);   // the left one before it
    }
}
