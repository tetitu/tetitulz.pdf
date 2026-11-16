// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Text.Hyphenation;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="EnglishHyphenation"/>: Liang's algorithm over hyph-utf8's American English patterns. The
/// expected hyphenations were computed by pyphen 0.18.1 — an independent implementation of the same
/// algorithm (libhyphen's) — loaded with the same patterns and the same margins (2 and 3), not by this
/// code; the exceptions are the file's own spellings.
/// </summary>
public class EnglishHyphenationTests
{
    [Theory]
    [InlineData("hyphenation", "hy-phen-ation")]
    [InlineData("computer", "com-puter")]
    [InlineData("algorithm", "al-go-rithm")]
    [InlineData("typesetting", "type-set-ting")]
    [InlineData("independent", "in-de-pen-dent")]
    [InlineData("implementation", "im-ple-men-ta-tion")]
    [InlineData("characteristic", "char-ac-ter-is-tic")]
    [InlineData("international", "in-ter-na-tional")]
    [InlineData("representation", "rep-re-sen-ta-tion")]
    [InlineData("environment", "en-vi-ron-ment")]
    [InlineData("beautiful", "beau-ti-ful")]
    [InlineData("probability", "prob-a-bil-ity")]
    [InlineData("mathematics", "math-e-mat-ics")]
    [InlineData("university", "uni-ver-sity")]
    [InlineData("paragraph", "para-graph")]
    [InlineData("accessibility", "ac-ces-si-bil-ity")]
    [InlineData("demonstration", "demon-stra-tion")]
    [InlineData("development", "de-vel-op-ment")]
    public void AWordBreaksWhereLiangsAlgorithmOverThePatternsSays(string word, string expected)
    {
        Assert.Equal(expected, EnglishHyphenation.Hyphenated(word));
    }

    /// <summary>Case is not a pattern's business: a capitalised word breaks where its lower case does, and keeps its case.</summary>
    [Fact]
    public void CaseIsKept_AndDoesNotChangeTheBreaks()
    {
        Assert.Equal("Hy-phen-ation", EnglishHyphenation.Hyphenated("Hyphenation"));
        Assert.Equal(EnglishHyphenation.BreakPoints("hyphenation"), EnglishHyphenation.BreakPoints("HYPHENATION"));
    }

    /// <summary>The file's exceptions replace the patterns' answer — as-so-ciate; present and project with no break; ta-ble, where the patterns would give none (pyphen, fed the patterns alone, leaves it whole).</summary>
    [Theory]
    [InlineData("associate", "as-so-ciate")]
    [InlineData("declination", "dec-li-na-tion")]
    [InlineData("obligatory", "oblig-a-tory")]
    [InlineData("present", "present")]
    [InlineData("project", "project")]
    [InlineData("table", "ta-ble")]        // the patterns alone give no break (an even 4 before the b)
    public void TheFilesExceptionsWin(string word, string expected)
    {
        Assert.Equal(expected, EnglishHyphenation.Hyphenated(word));
    }

    /// <summary>
    /// The margins hold for every break — at least 2 letters before, 3 after — and a word of anything
    /// but ASCII letters, too short to break, or past the length cap is left whole.
    /// </summary>
    [Fact]
    public void TheMarginsHold_AndOtherWordsAreLeftWhole()
    {
        foreach (string w in new[] { "hyphenation", "characteristic", "accessibility", "mathematics" })
        {
            var points = EnglishHyphenation.BreakPoints(w);
            Assert.NotEmpty(points);
            Assert.All(points, p => Assert.InRange(p, EnglishHyphenation.LeftMin, w.Length - EnglishHyphenation.RightMin));
        }

        Assert.Empty(EnglishHyphenation.BreakPoints("abcd"));                 // shorter than the margins together
        Assert.Empty(EnglishHyphenation.BreakPoints("co-operate"));
        Assert.Empty(EnglishHyphenation.BreakPoints("hyphenation2"));
        Assert.Empty(EnglishHyphenation.BreakPoints("caféterias"));
        Assert.Empty(EnglishHyphenation.BreakPoints(string.Concat(Enumerable.Repeat("hyphenation", 6))));   // 66 letters
        Assert.NotEmpty(EnglishHyphenation.BreakPoints(string.Concat(Enumerable.Repeat("hyphenation", 5))));  // 55
    }

    /// <summary>
    /// A token as text lays it out: the letters between its punctuation are asked, the positions
    /// counted in the token; punctuation inside the letters leaves it whole.
    /// </summary>
    [Theory]
    [InlineData("hyphenation", new[] { 2, 6 })]
    [InlineData("hyphenation,", new[] { 2, 6 })]
    [InlineData("(hyphenation).", new[] { 3, 7 })]
    [InlineData("\"Hyphenation\"", new[] { 3, 7 })]
    [InlineData("hyphenation2", new int[0])]
    [InlineData("caf\u00E9terias,", new int[0])]
    [InlineData("co-operate", new int[0])]
    [InlineData("don't", new int[0])]
    [InlineData("AB-1234567", new int[0])]
    [InlineData("...", new int[0])]
    [InlineData("", new int[0])]
    public void ATokensPunctuationIsSetAside(string token, int[] expected)
    {
        Assert.Equal(expected, EnglishHyphenation.BreakPointsWithin(token));
    }
}
