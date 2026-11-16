// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExporter.SplitAtHeight"/> (batch 129): a paragraph cut at the line boundary that
/// fits a height — the head's spacing before and whole lines, never fewer than its orphans; the tail
/// never fewer than its widows — into two copies whose runs keep their formatting, the run the cut
/// falls in made two. Nothing to split answers null: the whole paragraph fits, too few lines fit or
/// are left, or the paragraph is one <see cref="PdfExporter.CanSplit"/> refuses.
/// </summary>
public sealed class ParagraphSplitTests
{
    private const double Width = 200;
    private static readonly PdfExporter Measurer = new PdfExporter(PdfExportOptions.Default);

    /// <summary>Forty words in a 200pt band: about eight lines.</summary>
    private static Paragraph Long(int spacingBefore = 0)
    {
        var p = new Paragraph { SpacingBefore = spacingBefore, SpacingAfter = 6 };
        p.Runs.Add(new TextRun { Text = string.Join(" ", Enumerable.Range(1, 40).Select(i => "word" + i)) });
        return p;
    }

    private static double Height(Paragraph p) => Measurer.MeasureHeight(new IDocElement[] { p }, Width);

    /// <summary>
    /// The head is as tall as the lines that fit — three of eight — with the spacing before and no
    /// spacing after; the tail is the rest with no spacing before; together they hold every word once,
    /// in order, and the original is untouched.
    /// </summary>
    [Fact]
    public void SplitsAtTheLineThatFits()
    {
        Paragraph p = Long(spacingBefore: 10);
        double whole = Height(p);
        double line = (whole - 10 - 6) / Lines(p);
        var split = Measurer.SplitAtHeight(p, Width, 10 + 3.5 * line);

        Assert.NotNull(split);
        var (head, tail) = split!.Value;
        Assert.Equal(10 + 3 * line, Height(head), 1);
        Assert.Equal(0, head.SpacingAfter);
        Assert.Equal(0, tail.SpacingBefore);
        Assert.Equal(whole - 10 - 3 * line, Height(tail), 1);

        string original = Text(p), joined = Text(head) + " " + Text(tail);
        Assert.Equal(original, joined);
        Assert.Single(p.Runs);
        Assert.Equal(40, Text(p).Split(' ').Length);
    }

    /// <summary>The run the cut falls in is cut in two with its formatting: a bold run then a plain one, cut inside the bold, gives a bold head and a tail whose first run is bold and second plain.</summary>
    [Fact]
    public void TheCutRun_KeepsItsFormattingOnBothSides()
    {
        var p = new Paragraph { SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = string.Join(" ", Enumerable.Range(1, 30).Select(i => "bold" + i)), IsBold = true });
        p.Runs.Add(new TextRun { Text = " " + string.Join(" ", Enumerable.Range(1, 10).Select(i => "plain" + i)) });
        double line = Height(p) / Lines(p);
        var (head, tail) = Measurer.SplitAtHeight(p, Width, 2.5 * line)!.Value;

        Assert.All(head.Runs.Cast<TextRun>(), r => Assert.True(r.IsBold));
        Assert.True(((TextRun)tail.Runs[0]).IsBold, "the tail's first run lost the cut run's formatting");
        Assert.False(((TextRun)tail.Runs[tail.Runs.Count - 1]).IsBold);
        Assert.Contains("plain10", Text(tail), StringComparison.Ordinal);
        Assert.DoesNotContain("plain", Text(head), StringComparison.Ordinal);
    }

    /// <summary>Orphans and widows bound the cut: with both at two, a height for one line answers null, a height leaving one line for the tail cuts a line earlier, and a height the whole fits in answers null.</summary>
    [Fact]
    public void OrphansAndWidows_BoundTheCut()
    {
        Paragraph p = Long();
        p.Orphans = 2; p.Widows = 2;
        int lines = Lines(p);
        double line = (Height(p) - 6) / lines;

        Assert.Null(Measurer.SplitAtHeight(p, Width, 1.5 * line));
        var nearTheEnd = Measurer.SplitAtHeight(p, Width, (lines - 1) * line + 0.5 * line)!.Value;
        Assert.Equal(2, Lines(nearTheEnd.Tail));
        Assert.Null(Measurer.SplitAtHeight(p, Width, Height(p) + 10));

        Paragraph loose = Long();   // orphans and widows 1: one line may end the head or begin the tail
        Assert.Equal(1, Lines(Measurer.SplitAtHeight(loose, Width, 1.5 * line)!.Value.Head));
    }

    /// <summary>What cannot be split answers null: a kept-together, floated or placed paragraph, a boxed one, and one holding an image, a form field, a page break, a tab or an inline box.</summary>
    [Fact]
    public void WhatCannotBeSplit_AnswersNull()
    {
        double height = Height(Long()) / 2;
        Assert.NotNull(Measurer.SplitAtHeight(Long(), Width, height));   // the control: this one splits

        Paragraph kept = Long(); kept.KeepTogether = true;
        Paragraph floated = Long(); floated.Float = FloatSide.Left; floated.FloatWidth = 100;
        Paragraph placed = Long(); placed.Placement = new AbsolutePlacement { LeftPt = 10, TopPt = 10 };
        Paragraph boxed = Long(); boxed.Shading = "#EEEEEE";
        Paragraph withImage = Long(); withImage.Runs.Add(new ImageRun { Data = new byte[] { 1 }, WidthPt = 10, HeightPt = 10 });
        Paragraph withField = Long(); withField.Runs.Add(new TextRun { Text = "x", FormField = new FormFieldSpec { Name = "f" } });
        Paragraph withBreak = Long(); withBreak.Runs.Add(new PageBreak());
        Paragraph withTab = Long(); withTab.Runs.Add(new TabRun());
        Paragraph withBox = Long(); withBox.Runs.Add(new TextRun { Text = "tag", Box = new InlineBox { PaddingLeft = 2 } });
        foreach (Paragraph p in new[] { kept, floated, placed, boxed, withImage, withField, withBreak, withTab, withBox })
        {
            Assert.False(PdfExporter.CanSplit(p));
            Assert.Null(Measurer.SplitAtHeight(p, Width, height));
        }
    }

    /// <summary><paramref name="count"/> short lines, each ended by a line break but the last.</summary>
    private static Paragraph BrokenLines(int count)
    {
        var p = new Paragraph { SpacingAfter = 0 };
        for (int i = 1; i <= count; i++)
        {
            if (i > 1) p.Runs.Add(new LineBreak());
            p.Runs.Add(new TextRun { Text = "L" + i });
        }
        return p;
    }

    /// <summary>
    /// A paragraph of line breaks splits: cut where three of ten lines fit, the head holds the first
    /// three lines and their breaks — the third's too, which opens no empty fourth line, as a
    /// paragraph's last break never does — and the tail the other seven, from L4.
    /// </summary>
    [Fact]
    public void LineBreaks_AreKeptInTheirPart()
    {
        Paragraph p = BrokenLines(10);
        Assert.True(PdfExporter.CanSplit(p));
        double line = Height(p) / 10;
        var (head, tail) = Measurer.SplitAtHeight(p, Width, 3.5 * line)!.Value;

        Assert.Equal("L1\nL2\nL3\n", BrokenText(head));
        Assert.Equal(3 * line, Height(head), 1);
        Assert.Equal("L4\nL5\nL6\nL7\nL8\nL9\nL10", BrokenText(tail));
        Assert.Equal(7 * line, Height(tail), 1);
        Assert.Equal(19, p.Runs.Count);   // the original is untouched
    }

    /// <summary>
    /// An empty line — two breaks in a row — is a line of its own: cut after it, the head keeps both
    /// breaks, the second closing the empty line, and is two lines tall.
    /// </summary>
    [Fact]
    public void AnEmptyLine_StaysALineOfTheHead()
    {
        var p = new Paragraph { SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = "first" });
        p.Runs.Add(new LineBreak());
        p.Runs.Add(new LineBreak());
        p.Runs.Add(new TextRun { Text = "third " + Words(30, "w") });
        var parts = Measurer.SplitAtLines(p, Width, new[] { 2 })!;

        Assert.Equal("first\n\n", BrokenText(parts[0]));
        Assert.StartsWith("third", BrokenText(parts[1]), StringComparison.Ordinal);
        Assert.Equal(Height(p), Height(parts[0]) + Height(parts[1]), 1);
    }

    /// <summary>
    /// The trim of a cut's spaces stops at a line break: the spaces before the break that ends the
    /// head are its line's text, kept, not the cut's.
    /// </summary>
    [Fact]
    public void TheSpacesBeforeABreak_AreKept()
    {
        var p = new Paragraph { SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = "alpha" });
        p.Runs.Add(new LineBreak());
        p.Runs.Add(new TextRun { Text = "beta  " });
        p.Runs.Add(new LineBreak());
        p.Runs.Add(new TextRun { Text = Words(30, "w") });
        var parts = Measurer.SplitAtLines(p, Width, new[] { 2 })!;

        Assert.Equal("alpha\nbeta  \n", BrokenText(parts[0]));
    }

    /// <summary>A paragraph of nothing but line breaks has no text to split, and is refused.</summary>
    [Fact]
    public void OnlyLineBreaks_CannotBeSplit()
    {
        var p = new Paragraph();
        p.Runs.Add(new LineBreak());
        p.Runs.Add(new LineBreak());
        Assert.False(PdfExporter.CanSplit(p));
        Assert.True(PdfExporter.CanSplit(BrokenLines(2)));   // the control
    }

    private static string BrokenText(Paragraph p) =>
        string.Concat(p.Runs.Select(r => r is LineBreak ? "\n" : ((TextRun)r).Text));

    /// <summary>The parts are copies of the paragraph's formatting — alignment, indents, line height, orphans — and the tail drops the first-line indent, which belongs to the paragraph's first line.</summary>
    [Fact]
    public void TheParts_CopyTheFormatting_TheTailWithoutTheFirstLineIndent()
    {
        Paragraph p = Long();
        p.Alignment = TextAlignment.Justify; p.IndentLeft = 12; p.FirstLineIndent = 20; p.LineHeightFactor = 1.4; p.Widows = 2; p.Role = "P";
        var (head, tail) = Measurer.SplitAtHeight(p, Width, Height(p) / 2)!.Value;

        foreach (Paragraph part in new[] { head, tail })
        {
            Assert.Equal(TextAlignment.Justify, part.Alignment);
            Assert.Equal(12, part.IndentLeft);
            Assert.Equal(1.4, part.LineHeightFactor);
            Assert.Equal(2, part.Widows);
            Assert.Equal("P", part.Role);
        }
        Assert.Equal(20, head.FirstLineIndent);
        Assert.Equal(0, tail.FirstLineIndent);
    }

    /// <summary>
    /// <see cref="PdfExporter.MeasureLineHeights"/> counts the lines and their heights: as many as the
    /// paragraph's lines, summing with the spacing to its height, each line its own — a last line of
    /// larger text is taller than the first — and nothing for no width.
    /// </summary>
    [Fact]
    public void MeasureLineHeights_IsEachLinesOwnHeight()
    {
        Paragraph p = Long(spacingBefore: 10);
        var heights = Measurer.MeasureLineHeights(p, Width);
        Assert.Equal(Lines(p), heights.Count);
        Assert.Equal(Height(p), 10 + heights.Sum() + 6, 3);
        Assert.All(heights, h => Assert.Equal(heights[0], h, 6));

        Paragraph tallEnd = Long();
        tallEnd.Runs.Add(new TextRun { Text = " " + Words(12, "big"), FontSize = 24 });
        var mixed = Measurer.MeasureLineHeights(tallEnd, Width);
        Assert.True(mixed[mixed.Count - 1] > mixed[0] * 1.5, $"the last line, of 24pt text, is {mixed[mixed.Count - 1]:F1} to the first's {mixed[0]:F1}");
        Assert.Equal(Height(tallEnd), mixed.Sum() + 6, 3);

        Assert.Empty(Measurer.MeasureLineHeights(p, 0));
    }

    /// <summary>
    /// <see cref="PdfExporter.SplitAtLines"/> cuts before each line asked — two cuts, three parts of
    /// two, three and the remaining lines, the last cut before the last line — from one layout: the
    /// parts' own line counts are the cuts' distances, their texts the whole in order; the first
    /// keeps the spacing before and the first-line indent, the last the spacing after and the
    /// keep-with-next, the one between none of them.
    /// </summary>
    [Fact]
    public void SplitAtLines_CutsBeforeEachLineAsked()
    {
        Paragraph p = Long(spacingBefore: 10);
        p.FirstLineIndent = 20; p.KeepWithNext = true;
        int lines = Lines(p);
        var parts = Measurer.SplitAtLines(p, Width, new[] { 2, lines - 1 })!;

        Assert.Equal(3, parts.Count);
        Assert.Equal(2, Lines(parts[0]));
        Assert.Equal(lines - 3, Lines(parts[1]));
        Assert.Equal(1, Lines(parts[2]));
        Assert.Equal(Text(p), string.Join(" ", parts.Select(Text)));

        Assert.Equal(10, parts[0].SpacingBefore); Assert.Equal(0, parts[0].SpacingAfter); Assert.Equal(20, parts[0].FirstLineIndent); Assert.False(parts[0].KeepWithNext);
        Assert.Equal(0, parts[1].SpacingBefore); Assert.Equal(0, parts[1].SpacingAfter); Assert.Equal(0, parts[1].FirstLineIndent); Assert.False(parts[1].KeepWithNext);
        Assert.Equal(0, parts[2].SpacingBefore); Assert.Equal(6, parts[2].SpacingAfter); Assert.Equal(0, parts[2].FirstLineIndent); Assert.True(parts[2].KeepWithNext);
        Assert.Single(p.Runs);   // the original is untouched
    }

    /// <summary>A cut outside the lines, or out of order, is refused as an argument; what <see cref="PdfExporter.CanSplit"/> refuses answers null; and a cut before the last line is accepted (the control).</summary>
    [Fact]
    public void SplitAtLines_RefusesACutOutsideTheLines()
    {
        Paragraph p = Long();
        int lines = Lines(p);
        Assert.NotNull(Measurer.SplitAtLines(p, Width, new[] { lines - 1 }));

        Assert.Throws<ArgumentOutOfRangeException>(() => Measurer.SplitAtLines(p, Width, new[] { 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Measurer.SplitAtLines(p, Width, new[] { lines }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Measurer.SplitAtLines(p, Width, new[] { 3, 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Measurer.SplitAtLines(p, Width, new[] { 2, 2 }));

        Paragraph kept = Long(); kept.KeepTogether = true;
        Assert.Null(Measurer.SplitAtLines(kept, Width, new[] { 2 }));
    }

    // ------------------------------------------------------------------

    private static string Words(int n, string stem) => string.Join(" ", Enumerable.Range(1, n).Select(i => stem + i));

    private static string Text(Paragraph p) => string.Concat(p.Runs.Cast<TextRun>().Select(r => r.Text));

    /// <summary>The paragraph's line count at the band's width, from its height: lines are a line height each above the spacing.</summary>
    private static int Lines(Paragraph p)
    {
        var one = new Paragraph { SpacingBefore = p.SpacingBefore, SpacingAfter = p.SpacingAfter, LineHeightFactor = p.LineHeightFactor };
        one.Runs.Add(new TextRun { Text = "x" });
        double lineHeight = Height(one) - p.SpacingBefore - p.SpacingAfter;
        return (int)Math.Round((Height(p) - p.SpacingBefore - p.SpacingAfter) / lineHeight);
    }
}
