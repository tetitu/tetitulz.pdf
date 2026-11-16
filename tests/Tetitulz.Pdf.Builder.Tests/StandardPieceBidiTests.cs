// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A piece the standard fonts draw — a Latin word with its punctuation — is displayed in its
/// characters' bidi order, as a piece drawn through an embedded font always was: in a
/// right-to-left paragraph, <c>well.</c> at the end is displayed <c>.well</c> (UAX #9 N2), which
/// Chrome prints and this printed <c>well.</c>. Since a word is cut where its characters' levels
/// differ, the period is a piece of its own, placed at the paragraph's end — so these tests read
/// where it is drawn, not one drawn string.
/// </summary>
public class StandardPieceBidiTests
{
    /// <summary>Each draw's text and its x, in drawing order.</summary>
    private static List<(string Text, double X)> Draws(string text, TextDirection direction = TextDirection.RightToLeft)
    {
        var p = new Paragraph { Direction = direction };
        p.Runs.Add(new TextRun { Text = text });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf())).Select(t => (t.Text, t.X)).ToList();
    }

    /// <summary>
    /// Where <paramref name="punctuation"/> and <paramref name="word"/> are drawn, after checking the
    /// punctuation is never drawn glued to the word's right.
    /// </summary>
    private static (double Mark, double Word) Positions(string text, string word, string punctuation)
    {
        var draws = Draws(text);
        Assert.DoesNotContain(draws, d => d.Text == word + punctuation);
        return (draws.Single(d => d.Text == punctuation).X, draws.Single(d => d.Text == word).X);
    }

    private static List<string> Drawn(string text, TextDirection direction = TextDirection.RightToLeft)
    {
        var p = new Paragraph { Direction = direction };
        p.Runs.Add(new TextRun { Text = text });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf())).Select(t => t.Text).ToList();
    }

    /// <summary>The period at the end of a right-to-left paragraph is displayed to the left of the Latin word before it.</summary>
    [Fact]
    public void ATrailingPeriodGoesToTheParagraphsEnd()
    {
        var (period, word) = Positions("תוכן בתוך well.", "well", ".");
        Assert.True(period < word, $"the period at {period} is not left of the word at {word}");
    }

    /// <summary>A line that is one word is arranged too — the case a one-piece line skipped.</summary>
    [Fact]
    public void AOneWordLineIsArranged()
    {
        var (period, word) = Positions("well.", "well", ".");
        Assert.True(period < word, $"the period at {period} is not left of the word at {word}");
    }

    /// <summary>
    /// From the paragraph's levels, not the token's: <c>Mr.</c> before another Latin word keeps
    /// its period, which lies between two left-to-right letters (N1).
    /// </summary>
    [Fact]
    public void APeriodBetweenLatinWordsStays()
    {
        var drawn = Drawn("שלום Mr. Smith שלום");
        Assert.Contains("Mr.", drawn);
        Assert.DoesNotContain(".Mr", drawn);
    }

    /// <summary>A bracket at an odd level is mirrored (L4): a lone <c>(</c> between Hebrew words is displayed <c>)</c>.</summary>
    [Fact]
    public void ALoneBracketAtAnOddLevelIsMirrored()
    {
        var drawn = Drawn("שלום ( עולם");
        Assert.Contains(")", drawn);
        Assert.DoesNotContain("(", drawn);
    }

    /// <summary>A number's closing period goes to the paragraph's end too: <c>1234.</c> is displayed <c>.1234</c>.</summary>
    [Fact]
    public void ANumbersPeriodGoesToTheEnd()
    {
        var (period, number) = Positions("סכום 1234.", "1234", ".");
        Assert.True(period < number, $"the period at {period} is not left of the number at {number}");
    }

    /// <summary>
    /// A word whose punctuation resolves to the other level no longer drags the word with it: in a
    /// right-to-left paragraph, <c>Frank Ruehl:</c> keeps its Latin run in order — <c>Frank</c> left of
    /// <c>Ruehl</c> — with the colon at the run's left, on the paragraph's side (UAX #9 N1, N2). The
    /// piece <c>Ruehl:</c> was placed where its colon was displayed, so it printed "RuehlFrank".
    /// </summary>
    [Fact]
    public void AWordsTrailingPunctuationDoesNotDragItOutOfItsRun()
    {
        var draws = Draws("Frank Ruehl:");
        double frank = draws.Single(d => d.Text == "Frank").X;
        double ruehl = draws.Single(d => d.Text == "Ruehl").X;
        double colon = draws.Single(d => d.Text == ":").X;
        Assert.True(frank < ruehl, $"Frank at {frank}, Ruehl at {ruehl}");
        Assert.True(colon < frank, $"the colon at {colon} is not left of the run at {frank}");
    }

    /// <summary>The control: in a left-to-right paragraph the word keeps its period where it was written.</summary>
    [Fact]
    public void ALeftToRightParagraphIsUnchanged()
    {
        var drawn = Drawn("inside the well.", TextDirection.LeftToRight);
        Assert.Contains("well.", drawn);
        Assert.DoesNotContain(".well", drawn);
    }
}
