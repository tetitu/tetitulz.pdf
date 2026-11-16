// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.Hyphenate"/>: a word that does not fit breaks at the last of its hyphenation
/// points (hy-phen-ation) whose head, with a drawn hyphen, still fits, and the rest opens the next
/// line; off, the word moves whole. The filler pushes the word to the line's end, and a growing right
/// indent takes the room there down a point at a time.
/// </summary>
public class HyphenationTests
{
    private const string Shalom = "שלום";

    private static List<PdfContentProbe.DrawnText> Render(int words, bool hyphenate, string word = "hyphenation", bool glued = false,
                                                          double indentRight = 0, InlineBox? box = null)
        => PdfContentProbe.DrawnTexts(Content(words, hyphenate, word, glued, indentRight, box));

    private static string Content(int words, bool hyphenate, string word = "hyphenation", bool glued = false,
                                  double indentRight = 0, InlineBox? box = null, string filler = "word",
                                  TextDirection? direction = null, PdfExportOptions? options = null)
    {
        var p = new Paragraph { SpacingAfter = 0, Hyphenate = hyphenate, IndentRight = indentRight };
        if (direction.HasValue) p.Direction = direction.Value;
        p.Runs.Add(new TextRun { Text = string.Join(" ", Enumerable.Repeat(filler, words)) + (glued ? " BB" : " ") });
        p.Runs.Add(new TextRun { Text = word, Box = box });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(options == null ? doc.ToPdf() : doc.ToPdf(options));
    }

    /// <summary>The filler counts at which the whole word, unhyphenated, wraps — the word is the next line's first draw.</summary>
    private static IEnumerable<int> CountsWhereTheWordWraps()
    {
        var found = new List<int>();
        for (int n = 1; n < 200 && found.Count < 4; n++)
        {
            var d = Render(n, hyphenate: false);
            var w = d.Single(t => t.Text == "hyphenation");
            if (w.Y < d[0].Y - 1) found.Add(n);
        }
        Assert.NotEmpty(found);
        return found;
    }

    /// <summary>The first filler count, from one short of the wrap, and the indent at which the word hyphenates.</summary>
    private static (int Words, double Indent) WhereItHyphenates()
    {
        int n = CountsWhereTheWordWraps().First() - 1;
        for (double indent = 0; indent <= 80; indent += 1)
            if (Render(n, hyphenate: true, indentRight: indent).Any(t => t.Text.EndsWith("-"))) return (n, indent);
        throw new System.InvalidOperationException("never hyphenated");
    }

    [Fact]
    public void AWordThatDoesNotFitBreaksAtTheLastPointThatFits_TheHeadWithAHyphen()
    {
        var heads = new HashSet<string>();
        // One count short of the wrap the word fits whole; a growing right indent then takes the room
        // at the line's end down through every head, a point at a time.
        int n = CountsWhereTheWordWraps().First() - 1;
        for (double indent = 0; indent <= 80; indent += 1)
        {
            var d = Render(n, hyphenate: true, indentRight: indent);
            var head = d.FirstOrDefault(t => t.Text.EndsWith("-"));
            if (head.Text == null) continue;
            heads.Add(head.Text);
            string rest = "hyphenation".Substring(head.Text.Length - 1);
            var tail = d.Single(t => t.Text == rest);
            Assert.True(tail.Y < head.Y - 1, $"{rest} is on the line after {head.Text}");
            Assert.Equal(d[0].X, tail.X, 1);                                   // and opens it
            Assert.DoesNotContain(d, t => t.Text == "hyphenation");
        }
        // The last point that fits, not the first: with room, hyphen-; with less, hy-.
        Assert.Contains("hyphen-", heads);
        Assert.Contains("hy-", heads);
        Assert.Empty(heads.Except(new[] { "hy-", "hyphen-" }));
    }

    /// <summary>
    /// The head and its hyphen end inside the line — the space before the word counts against the room —
    /// measured by the standard font's own widths, at every indent that hyphenates.
    /// </summary>
    [Fact]
    public void TheHeadAndItsHyphenEndInsideTheLine()
    {
        double size = new TextRun().FontSize;
        double right = new Section().PageWidthPt - new Section().MarginRight;
        int n = CountsWhereTheWordWraps().First() - 1;
        int seen = 0;
        for (double indent = 0; indent <= 80; indent += 0.5)
        {
            var d = Render(n, hyphenate: true, indentRight: indent);
            var head = d.FirstOrDefault(t => t.Text.EndsWith("-"));
            if (head.Text == null) continue;
            seen++;
            double width = head.Text.Sum(c => Standard14Metrics.GetWidthByUnicode(StandardFontName, c)) * size / 1000;
            Assert.True(head.X + width <= right - indent + 0.01,
                $"{head.Text} ends at {head.X + width:F2}, past the line's end at {right - indent:F2}");
        }
        Assert.True(seen > 20, $"{seen} indents hyphenated");
    }

    private const string StandardFontName = "Helvetica";

    [Fact]
    public void OffByDefault_TheWordMovesWhole()
    {
        Assert.False(new Paragraph().Hyphenate);
        foreach (int n in CountsWhereTheWordWraps())
        {
            var d = Render(n, hyphenate: false);
            Assert.DoesNotContain(d, t => t.Text.EndsWith("-"));
            Assert.Single(d, t => t.Text == "hyphenation");
        }
    }

    /// <summary>The HTML export says the same to a browser: <c>hyphens:auto</c> on a hyphenating paragraph alone.</summary>
    [Fact]
    public void TheHtmlExportWritesHyphensAuto_OnAHyphenatingParagraphAlone()
    {
        static string Html(bool hyphenate)
        {
            var p = new Paragraph { Hyphenate = hyphenate };
            p.Runs.Add(new TextRun { Text = "hyphenation" });
            var section = new Section();
            section.Children.Add(p);
            var doc = new Document { Language = "en" };
            doc.Sections.Add(section);
            return System.Text.Encoding.UTF8.GetString(doc.ToHtml());
        }
        Assert.Contains("<p style=\"hyphens:auto;\">hyphenation", Html(true), System.StringComparison.Ordinal);
        string off = Html(false);
        Assert.Contains("hyphenation", off, System.StringComparison.Ordinal);
        Assert.DoesNotContain("hyphens", off, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A word of anything but ASCII letters is left whole, and so is a word glued to the run before it
    /// (BBhyphenation is one word, which moves whole) — each beside the hyphenated control at the same
    /// place; a full stop after the word is punctuation, set aside, and goes with the rest.
    /// </summary>
    [Fact]
    public void AWordWithADigit_OrGluedToTheRunBefore_IsLeftWhole_AFullStopGoesWithTheRest()
    {
        var (n, indent) = WhereItHyphenates();

        var digit = Render(n, hyphenate: true, word: "hyphenation2", indentRight: indent);
        Assert.DoesNotContain(digit, t => t.Text.EndsWith("-"));
        Assert.Contains(digit, t => t.Text == "hyphenation2");

        for (int k = n - 2; k <= n + 2; k++)
            Assert.DoesNotContain(Render(k, hyphenate: true, glued: true, indentRight: indent), t => t.Text.EndsWith("-"));

        var stop = Render(n, hyphenate: true, word: "hyphenation.", indentRight: indent);
        var head = stop.Single(t => t.Text.EndsWith("-"));
        Assert.Contains(stop, t => t.Text == "hyphenation.".Substring(head.Text.Length - 1));
    }

    /// <summary>
    /// A word drawn otherwise than it is written — a stray pop-directional-formatting control after it,
    /// which changes no level (so the word is one token) and which the drawing strips — and a word in an
    /// inline box each move whole, beside the hyphenated control.
    /// </summary>
    [Fact]
    public void AWordWithABidiControl_OrInABox_MovesWhole()
    {
        var (n, indent) = WhereItHyphenates();

        var marked = Render(n, hyphenate: true, word: "hyphenation\u202C", indentRight: indent);
        Assert.DoesNotContain(marked, t => t.Text.EndsWith("-"));
        Assert.Contains(marked, t => t.Text == "hyphenation");

        var boxed = Render(n, hyphenate: true, indentRight: indent, box: new InlineBox());
        Assert.DoesNotContain(boxed, t => t.Text.EndsWith("-"));
        Assert.Contains(boxed, t => t.Text == "hyphenation");
    }

    /// <summary>
    /// In a right-to-left paragraph the line's logical end is its left edge: the head of an English word
    /// hyphenated after Hebrew is the leftmost thing drawn on its line, and the rest opens the next.
    /// </summary>
    [Fact]
    public void InARightToLeftParagraph_TheHeadEndsItsLineAtTheLeft()
    {
        var options = new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build(Shalom + "hyphenation-. "),
        };
        string Rtl(int words, bool hyphenate, double indent) =>
            Content(words, hyphenate, filler: Shalom, direction: TextDirection.RightToLeft, options: options, indentRight: indent);

        int wraps = -1;
        for (int k = 1; k < 200 && wraps < 0; k++)
        {
            string c = Rtl(k, hyphenate: false, indent: 0);
            if (PdfContentProbe.YOf(c, "hyphenation") < PdfContentProbe.DrawnGlyphRuns(c).Max(r => r.Y) - 1) wraps = k;
        }
        Assert.True(wraps > 1, "the word never wrapped");

        int checkedLines = 0;
        for (double indent = 0; indent <= 80; indent += 1)
        {
            string c = Rtl(wraps - 1, hyphenate: true, indent);
            var head = PdfContentProbe.DrawnTexts(c).FirstOrDefault(t => t.Text.EndsWith("-"));
            if (head.Text == null) continue;
            var hebrewOnItsLine = PdfContentProbe.DrawnGlyphRuns(c).Where(r => System.Math.Abs(r.Y - head.Y) < 0.5).ToList();
            Assert.NotEmpty(hebrewOnItsLine);
            Assert.True(hebrewOnItsLine.All(r => r.X > head.X),
                $"{head.Text} at x={head.X:F1} is not left of the Hebrew on its line ({string.Join(", ", hebrewOnItsLine.Select(r => r.X.ToString("F1")))})");
            var rest = PdfContentProbe.DrawnTexts(c).Single(t => t.Text == "hyphenation".Substring(head.Text.Length - 1));
            Assert.True(rest.Y < head.Y - 1);
            checkedLines++;
        }
        Assert.True(checkedLines > 0, "no indent hyphenated");
    }
}
