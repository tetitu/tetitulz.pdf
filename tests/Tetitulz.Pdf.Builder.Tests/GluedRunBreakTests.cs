// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A line breaks only at a space (or a tab), never between two runs with nothing between them:
/// a bold order number and the full stop after it are one word. Found against Chrome on an HTML
/// e-mail — "… is <b>#10457</b>." left the full stop alone on the next line. The word now moves to
/// the next line whole; a word as wide as the line still breaks where it must.
/// </summary>
public class GluedRunBreakTests
{
    /// <summary><paramref name="words"/> filler words, then <c>BBB</c> and a second run <c>!!!!!!</c>, spaced or glued.</summary>
    private static string Render(int words, bool spaced)
    {
        var p = new Paragraph { SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = string.Join(" ", System.Linq.Enumerable.Repeat("word", words)) + " BBB" });
        p.Runs.Add(new TextRun { Text = (spaced ? " " : "") + "!!!!!!", IsBold = true });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    /// <summary>The filler length at which the spaced <c>!!!!!!</c> wraps and <c>BBB</c> stays on the first line.</summary>
    private static int EdgeOfTheLine()
    {
        for (int n = 1; n < 200; n++)
        {
            string c = Render(n, spaced: true);
            if (PdfContentProbe.YOf(c, "!!!!!!") < PdfContentProbe.YOf(c, "BBB") - 1) return n;
        }
        throw new System.InvalidOperationException("the line never filled");
    }

    [Fact]
    public void AGluedRunTakesItsWordToTheNextLine()
    {
        int n = EdgeOfTheLine();
        string spaced = Render(n, spaced: true), glued = Render(n, spaced: false);

        // The control: with a space the two part at it.
        Assert.True(PdfContentProbe.YOf(spaced, "!!!!!!") < PdfContentProbe.YOf(spaced, "BBB") - 1);
        // Glued, BBB goes down with them, onto one line, and starts it.
        Assert.Equal(PdfContentProbe.YOf(glued, "BBB"), PdfContentProbe.YOf(glued, "!!!!!!"), 1);
        Assert.True(PdfContentProbe.YOf(glued, "BBB") < PdfContentProbe.YOf(glued, "word") - 1);
        Assert.Equal(PdfContentProbe.XOf(glued, "word"), PdfContentProbe.XOf(glued, "BBB"), 1);
    }

    /// <summary>
    /// <paramref name="n"/> numbered filler words at <paramref name="fillerSize"/>, then
    /// <c>BBB!!!!!!</c> at 40pt: as two glued runs, or as one token (the control, which never needed
    /// the rule).
    /// </summary>
    private static string RenderSized(int n, bool glued, TextAlignment align, double fillerSize = 20, double lineHeightFactor = 0)
    {
        var p = new Paragraph { SpacingAfter = 0, Alignment = align, LineHeightFactor = lineHeightFactor };
        var filler = string.Join(" ", System.Linq.Enumerable.Range(1, n).Select(i => "w" + i.ToString("D2")));
        p.Runs.Add(new TextRun { Text = filler + " ", FontSize = fillerSize });
        if (glued)
        {
            p.Runs.Add(new TextRun { Text = "BBB", FontSize = 40 });
            p.Runs.Add(new TextRun { Text = "!!!!!!", FontSize = 40 });
        }
        else p.Runs.Add(new TextRun { Text = "BBB!!!!!!", FontSize = 40 });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private const string Token = "BBB!!!!!!";

    /// <summary>
    /// The line the word leaves is laid out from what stays on it: as tall as its own words, not the
    /// 40pt word that left, and justified over its own spaces, not the one before the word. So it
    /// matches, word for word, the line a single unglued token leaves behind. At 8pt with a line-height
    /// factor the line's height is its largest run's, which no default floor hides.
    /// </summary>
    [Theory]
    [InlineData(20, 0)]
    [InlineData(8, 1.15)]
    public void TheLineAWordLeavesIsMeasuredWithoutIt(double fillerSize, double factor)
    {
        string Render(int k, bool g, TextAlignment a) => RenderSized(k, g, a, fillerSize, factor);
        int n = 1;
        while (n < 100)
        {
            string c = Render(n, false, TextAlignment.Justify);
            if (PdfContentProbe.YOf(c, Token) < PdfContentProbe.YOf(c, "w01") - 1) break;
            n++;
        }
        Assert.True(n < 100, "the line never filled");
        string last = "w" + n.ToString("D2");

        string control = Render(n, false, TextAlignment.Justify);
        string glued = Render(n, true, TextAlignment.Justify);
        // Potency: the control's first line is justified — its last word further right than unjustified.
        Assert.True(PdfContentProbe.XOf(control, last)
                    > PdfContentProbe.XOf(Render(n, false, TextAlignment.Left), last) + 1);

        Assert.True(PdfContentProbe.YOf(glued, "BBB") < PdfContentProbe.YOf(glued, "w01") - 1, "the word did not move");
        Assert.Equal(PdfContentProbe.YOf(control, "w01"), PdfContentProbe.YOf(glued, "w01"), 1);
        Assert.Equal(PdfContentProbe.XOf(control, last), PdfContentProbe.XOf(glued, last), 1);
        Assert.Equal(PdfContentProbe.YOf(control, Token), PdfContentProbe.YOf(glued, "BBB"), 1);
    }

    /// <summary>
    /// The moved word belongs to the new line's bidi range: in a paragraph with a Hebrew word, whose
    /// lines are reordered by their ranges, it still starts its line rather than being put at the end.
    /// </summary>
    [Fact]
    public void AMovedWordStartsItsLineWhenTheLineIsReordered()
    {
        static string Render(int k, bool spaced)
        {
            var p = new Paragraph { SpacingAfter = 0, Direction = TextDirection.LeftToRight };
            p.Runs.Add(new TextRun { Text = string.Join(" ", System.Linq.Enumerable.Repeat("word", k)) + " BBB" });
            p.Runs.Add(new TextRun { Text = (spaced ? " " : "") + "!!!!!!" });
            p.Runs.Add(new TextRun { Text = " שלום" });
            var section = new Section();
            section.Children.Add(p);
            var doc = new Document();
            doc.Sections.Add(section);
            return PdfContentProbe.DecodeStreams(doc.ToPdf());
        }
        int n = 1;
        while (n < 200 && !(PdfContentProbe.YOf(Render(n, true), "!!!!!!") < PdfContentProbe.YOf(Render(n, true), "BBB") - 1)) n++;
        Assert.True(n < 200, "the line never filled");
        string c = Render(n, false);
        Assert.True(PdfContentProbe.YOf(c, "BBB") < PdfContentProbe.YOf(c, "word") - 1, "the word did not move");
        Assert.Equal(PdfContentProbe.XOf(c, "word"), PdfContentProbe.XOf(c, "BBB"), 1);
        Assert.True(PdfContentProbe.XOf(c, "!!!!!!") > PdfContentProbe.XOf(c, "BBB"));
    }

    /// <summary>
    /// An inline-block that moves with its word takes its height to the new line: a 30pt top margin
    /// sets the line lower by about that much than the same glued word without the box (the control).
    /// </summary>
    [Fact]
    public void AMovedInlineBlockKeepsItsHeight()
    {
        static string Render(int k, bool boxed, bool spaced = false)
        {
            var p = new Paragraph { SpacingAfter = 0 };
            p.Runs.Add(new TextRun { Text = string.Join(" ", System.Linq.Enumerable.Repeat("word", k)) + " " });
            p.Runs.Add(new TextRun { Text = "BBB", Box = boxed ? new InlineBox { OccupiesLine = true, MarginTop = 30 } : null });
            p.Runs.Add(new TextRun { Text = (spaced ? " " : "") + "!!!!!!" });
            var section = new Section();
            section.Children.Add(p);
            var doc = new Document();
            doc.Sections.Add(section);
            return PdfContentProbe.DecodeStreams(doc.ToPdf());
        }
        int n = 1;
        while (n < 200 && !(PdfContentProbe.YOf(Render(n, false, true), "!!!!!!") < PdfContentProbe.YOf(Render(n, false, true), "BBB") - 1)) n++;
        Assert.True(n < 200, "the line never filled");
        string boxed = Render(n, true), plain = Render(n, false);
        Assert.True(PdfContentProbe.YOf(boxed, "BBB") < PdfContentProbe.YOf(boxed, "word") - 1, "the word did not move");
        // The first lines agree, so the difference is the second line's own.
        Assert.Equal(PdfContentProbe.YOf(plain, "word"), PdfContentProbe.YOf(boxed, "word"), 1);
        Assert.True(PdfContentProbe.YOf(plain, "BBB") - PdfContentProbe.YOf(boxed, "BBB") > 20);
    }

    /// <summary>A tab's leader ends a word as the tab does: the dots stay on their line, against the stop.</summary>
    [Fact]
    public void ATabLeaderStaysOnItsLine()
    {
        var p = new Paragraph { SpacingAfter = 0 };
        p.Tabs.Add(new TabStop { PosPt = 200, Leader = TabLeader.Dot });
        p.Runs.Add(new TextRun { Text = "Name" });
        p.Runs.Add(new TabRun());
        p.Runs.Add(new TextRun { Text = "BBB" });
        p.Runs.Add(new TextRun { Text = new string('!', 100) });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());

        var dots = PdfContentProbe.DrawnTexts(c).First(d => d.Text.StartsWith("...", System.StringComparison.Ordinal));
        Assert.Equal(PdfContentProbe.YOf(c, "Name"), dots.Y, 1);
        Assert.True(PdfContentProbe.YOf(c, "BBB") < PdfContentProbe.YOf(c, "Name") - 1, "the glued word did not move");
        Assert.Equal(PdfContentProbe.XOf(c, "Name"), PdfContentProbe.XOf(c, "BBB"), 1);
    }

    /// <summary>A word as wide as the line has nowhere else to break: its runs still part, rather than overflow for ever.</summary>
    [Fact]
    public void AWordAsWideAsTheLineStillBreaks()
    {
        var p = new Paragraph { SpacingAfter = 0 };
        for (int i = 0; i < 40; i++) p.Runs.Add(new TextRun { Text = "Q" + i.ToString("D2") + "xxxxxxxx" });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf());
        Assert.True(PdfContentProbe.YOf(c, "Q39xxxxxxxx") < PdfContentProbe.YOf(c, "Q00xxxxxxxx") - 1, "one unbroken line");
    }
}
