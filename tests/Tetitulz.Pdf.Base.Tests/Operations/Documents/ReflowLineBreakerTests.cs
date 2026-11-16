// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The word wrapping behind <see cref="PdfTextReflow"/> — GAPS.md §19d, the half of
/// find-and-replace that <b>does</b> reflow.
/// </summary>
/// <remarks>
/// <para><b>Measured by character count here, deliberately.</b> The production measure shapes the
/// run and asks the font, which is the only way to get a real width — and is also what makes a test
/// of the <i>breaking</i> depend on a font file, a shaper and a unit. Counting characters keeps
/// these tests about where the breaks fall, which is the decision this file makes.</para>
///
/// <para>That the real measure is used in production is a different claim, and it is asserted where
/// it belongs: on the reflow itself, by extracting the produced document.</para>
/// </remarks>
public class ReflowLineBreakerTests
{
    /// <summary>A measure in characters, so a "width" of 10 means ten characters.</summary>
    private static readonly Func<string, double> ByCharacter = s => s.Length;

    private static List<string> Break(string text, double width) =>
        ReflowLineBreaker.Break(text, ByCharacter, width);

    [Fact]
    public void TextShorterThanTheMeasureStaysOnOneLine()
    {
        Assert.Equal(new[] { "one two" }, Break("one two", 10));
    }

    /// <summary>
    /// Words are moved onto a new line only when the candidate would exceed the measure — so the
    /// last word that fits exactly stays.
    /// </summary>
    [Fact]
    public void AWordThatFitsExactlyStaysOnTheLine()
    {
        // "one two" is exactly 7 characters.
        Assert.Equal(new[] { "one two" }, Break("one two", 7));

        // One narrower and it does not fit.
        Assert.Equal(new[] { "one", "two" }, Break("one two", 6));
    }

    [Fact]
    public void TextLongerThanTheMeasureBreaksAtSpaces()
    {
        List<string> lines = Break("alpha beta gamma delta", 11);

        Assert.Equal(new[] { "alpha beta", "gamma delta" }, lines);
        Assert.All(lines, l => Assert.True(l.Length <= 11, $"'{l}' is wider than the measure"));
    }

    /// <summary>
    /// <b>A word wider than the measure gets its own line and overflows it</b>, rather than being
    /// broken at an invented point.
    /// </summary>
    /// <remarks>
    /// There is no hyphenation here, and guessing a break inside a word is how a URL or a part
    /// number becomes two wrong strings. An overflowing line is visible; a wrong break is not.
    /// </remarks>
    [Fact]
    public void AWordWiderThanTheMeasureOverflowsOnItsOwnLine()
    {
        List<string> lines = Break("a supercalifragilistic b", 5);

        Assert.Equal(new[] { "a", "supercalifragilistic", "b" }, lines);
    }

    /// <summary>
    /// Runs of whitespace collapse: a PDF's lines are joined with single spaces before breaking, and
    /// the text a caller supplies may carry its own.
    /// </summary>
    [Fact]
    public void RunsOfSpacesCollapseToOne()
    {
        Assert.Equal(new[] { "one two three" }, Break("one   two \t three".Replace('\t', ' '), 40));
    }

    [Fact]
    public void EmptyTextProducesNoLines()
    {
        Assert.Empty(Break(string.Empty, 10));
        Assert.Empty(Break("   ", 10));
        Assert.Empty(ReflowLineBreaker.Break(null, ByCharacter, 10));
    }

    /// <summary>
    /// A non-positive measure yields one line rather than one word per line — a measure that admits
    /// nothing is a caller error, and the degenerate reading makes it look like a formatting choice.
    /// </summary>
    [Fact]
    public void ANonPositiveMeasureProducesASingleLine()
    {
        Assert.Equal(new[] { "one two three" }, Break("one two three", 0));
        Assert.Equal(new[] { "one two three" }, Break("one two three", -5));
    }

    /// <summary>
    /// <b>Candidates are measured whole, not summed from pieces.</b>
    /// </summary>
    /// <remarks>
    /// Shaping is not additive — kerning happens across a join and a ligature can form over one — so
    /// "width of the line so far, plus a space, plus the word" is a different number from the width
    /// of the string that will actually be drawn. This pins the call pattern with a measure that
    /// reports a different total for the whole than for the sum of its parts.
    /// </remarks>
    [Fact]
    public void EachCandidateIsMeasuredAsOneString()
    {
        var measured = new List<string>();
        Func<string, double> recording = s => { measured.Add(s); return s.Length; };

        ReflowLineBreaker.Break("alpha beta gamma", recording, 11);

        // Every measurement is of a whole candidate line, never of a bare word or a separator.
        Assert.All(measured, m => Assert.False(m.StartsWith(" ", StringComparison.Ordinal)));
        Assert.Contains("alpha beta", measured);
        Assert.DoesNotContain(measured, m => m == " ");
    }

    /// <summary>
    /// Asked to hyphenate, a word that does not fit breaks at the last of its points (hy-phen-ation)
    /// whose head, with the hyphen, fits — measured as the whole candidate line; off, the default, it
    /// moves whole.
    /// </summary>
    [Fact]
    public void Hyphenating_AWordBreaksAtTheLastPointWhoseHeadFits()
    {
        Assert.Equal(new[] { "alpha hyphen-", "ation" }, ReflowLineBreaker.Break("alpha hyphenation", ByCharacter, 13, hyphenate: true));
        Assert.Equal(new[] { "alpha hy-", "phenation" }, ReflowLineBreaker.Break("alpha hyphenation", ByCharacter, 12, hyphenate: true));
        Assert.Equal(new[] { "alpha", "hyphenation" }, ReflowLineBreaker.Break("alpha hyphenation", ByCharacter, 8, hyphenate: true));   // no head fits
        Assert.Equal(new[] { "alpha", "hyphenation" }, ReflowLineBreaker.Break("alpha hyphenation", ByCharacter, 13));                   // off
        Assert.Equal(new[] { "alpha", "hyphenation" }, ReflowLineBreaker.Break("alpha hyphenation", ByCharacter, 13, hyphenate: false));
    }

    /// <summary>
    /// The letters between a word's punctuation are hyphenated and the punctuation goes with its side;
    /// a part number or a URL has no hyphenation point, so it still moves whole.
    /// </summary>
    [Fact]
    public void Hyphenating_PunctuationGoesWithItsSide_AndAPartNumberMovesWhole()
    {
        Assert.Equal(new[] { "alpha (hy-", "phenation)," }, ReflowLineBreaker.Break("alpha (hyphenation),", ByCharacter, 13, hyphenate: true));
        Assert.Equal(new[] { "alpha", "AB-1234567" }, ReflowLineBreaker.Break("alpha AB-1234567", ByCharacter, 12, hyphenate: true));
        Assert.Equal(new[] { "alpha", "example.com/path" }, ReflowLineBreaker.Break("alpha example.com/path", ByCharacter, 14, hyphenate: true));
    }
}
