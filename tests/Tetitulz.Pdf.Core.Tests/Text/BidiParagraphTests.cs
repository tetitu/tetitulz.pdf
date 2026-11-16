// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="BidiParagraph"/> — the levels-and-permutation view of UAX #9 that a layout engine
/// needs, as opposed to <see cref="UnicodeBidi.Reorder(string)"/>'s "here is the finished string".
///
/// <para>These tests are deliberately font-free and host-independent: they assert on the
/// permutation and the levels directly, so they run identically everywhere and cannot be
/// weakened into "something came back".</para>
/// </summary>
public class BidiParagraphTests
{
    private const string Shalom = "שלום";  // shin lamed vav mem — all strong R

    private static int VisualIndexOf(int[] order, int logicalIndex) =>
        Array.IndexOf(order, logicalIndex);

    // ------------------------------------------------------------------ the headline defect

    /// <summary>
    /// The case that motivated this type. A multi-word Latin phrase inside a right-to-left
    /// paragraph is <b>one</b> left-to-right run: its words must stay in reading order, and only
    /// the run as a whole is placed right-to-left relative to the Hebrew.
    ///
    /// <para>Resolving direction per whitespace-delimited token — which is what a caller is forced
    /// into when the only API returns a finished string — makes each of "John" and "Smith" its own
    /// paragraph, and the surrounding right-to-left placement then emits them in reverse.</para>
    /// </summary>
    [Fact]
    public void RtlParagraph_MultiWordLatinRun_KeepsItsWordsInReadingOrder()
    {
        string text = $"{Shalom} John Smith";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);
        int[] order = para.VisualOrder(0, text.Length);

        int j = text.IndexOf("John", StringComparison.Ordinal);
        int s = text.IndexOf("Smith", StringComparison.Ordinal);

        // "John" must appear entirely to the left of "Smith" in the display.
        Assert.True(VisualIndexOf(order, j) < VisualIndexOf(order, s),
            "the Latin run must not be word-reversed inside an RTL paragraph");
        // …and each word must itself read forwards.
        Assert.True(VisualIndexOf(order, j) < VisualIndexOf(order, j + 3), "'John' must read forwards");
        Assert.True(VisualIndexOf(order, s) < VisualIndexOf(order, s + 4), "'Smith' must read forwards");
        // The whole Latin run sits left of the Hebrew, which starts the paragraph.
        Assert.True(VisualIndexOf(order, s + 4) < VisualIndexOf(order, 0),
            "the RTL paragraph's first word belongs at the right");
    }

    [Fact]
    public void RtlParagraph_LatinRunAndItsInternalSpace_ShareTheHigherLevel()
    {
        string text = $"{Shalom} John Smith";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);

        Assert.Equal(1, para.ParagraphLevel);
        Assert.True(para.IsRightToLeft);
        Assert.Equal(1, para.LevelAt(0));                                   // Hebrew
        int j = text.IndexOf("John", StringComparison.Ordinal);
        Assert.Equal(2, para.LevelAt(j));                                   // Latin
        Assert.Equal(2, para.LevelAt(j + 4));                               // the space BETWEEN two L runs
        Assert.Equal(1, para.LevelAt(j - 1));                               // the space before the Latin run
    }

    // ------------------------------------------------------------------ L1

    /// <summary>
    /// UAX #9 L1 clause 4: a run of whitespace at the end of a <b>line</b> resets to the paragraph
    /// level.
    ///
    /// <para>The fixture is chosen so the clause is actually reachable, which took a mutation run
    /// to establish. At the end of a <i>paragraph</i> this rule is unobservable in this engine:
    /// <c>ResolveNeutralTypes</c> already uses the paragraph direction as <c>eos</c>, so a trailing
    /// neutral run has the paragraph level before L1 ever looks at it — an "abc " fixture passes
    /// with <c>ApplyL1</c> commented out entirely.</para>
    ///
    /// <para>Mid-paragraph it is a different matter, and this is exactly why L1 has to be a
    /// per-line operation rather than part of paragraph resolution. In <c>"abc def"</c> under an
    /// RTL paragraph every character — including the space, which sits between two LTR runs —
    /// resolves to level 2. Break the line after that space and it is now trailing whitespace, so
    /// it must reset to level 1 and move to the line's left edge. Nothing about the paragraph
    /// changed; only where the line ended.</para>
    /// </summary>
    [Fact]
    public void L1_WhitespaceAtALineEnd_ResetsEvenThoughTheParagraphGaveItTheHigherLevel()
    {
        const string text = "abc def";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);

        // Precondition: the space is level 2 in the paragraph, NOT the paragraph level.
        Assert.Equal(2, para.LevelAt(3));

        int[] line = para.VisualOrder(0, 4);          // the line "abc "

        Assert.Equal(0, VisualIndexOf(line, 3));      // the space moved to the left edge
        Assert.True(VisualIndexOf(line, 0) < VisualIndexOf(line, 2),
            "'abc' must still read forwards after the reset");
    }

    /// <summary>
    /// L1 clause 3: a run of whitespace <i>preceding</i> a segment separator resets too.
    /// </summary>
    [Fact]
    public void L1_WhitespaceBeforeASegmentSeparator_AlsoResets()
    {
        const string text = "abc \tdef";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);
        int[] order = para.VisualOrder(0, text.Length);

        // Both the space (3) and the tab (4) sit at the paragraph level, so the two LTR runs
        // swap around them rather than the space clinging to "abc".
        Assert.True(VisualIndexOf(order, 5) < VisualIndexOf(order, 0),
            "'def' should precede 'abc' visually once the separator run is reset");
    }

    [Fact]
    public void L1_TabResetsToTheParagraphLevel()
    {
        string text = "abc\tdef";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);
        int[] order = para.VisualOrder(0, text.Length);

        // The tab (a segment separator) is reset to level 1, so the two LTR runs swap around it.
        Assert.True(VisualIndexOf(order, 4) < VisualIndexOf(order, 0),
            "'def' should precede 'abc' visually in an RTL paragraph split by a segment separator");
    }

    /// <summary>
    /// L1 is a <b>per-line</b> reset, so resolving once and asking for two different line ranges
    /// must not let the first call's reset leak into the second.
    /// </summary>
    [Fact]
    public void L1_IsPerLine_AndDoesNotMutateTheParagraph()
    {
        string text = "abc def";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);

        int levelBefore = para.LevelAt(3);          // the space
        para.VisualOrder(0, 4);                     // a line ENDING at that space → L1 resets it
        int levelAfter = para.LevelAt(3);

        Assert.Equal(levelBefore, levelAfter);
    }

    // ------------------------------------------------------------------ surrogate pairs

    /// <summary>
    /// An astral character is one character. Reversing UTF-16 code units blindly emits its low
    /// surrogate first, producing an unpaired-surrogate string that no font can render — silent
    /// corruption of every emoji or historic script inside a right-to-left run.
    ///
    /// <para><b>The fixture needs the pair at an ODD level, which took a mutation run to
    /// establish.</b> An emoji between two Hebrew words cannot detect the defect: an emoji is not
    /// right-to-left, so it sits at level 2 inside an RTL paragraph, L2 reverses it twice (once
    /// for level 2, once for level 1), and the two reversals cancel. Only a character that is
    /// <i>itself</i> right-to-left lands on an odd level, where exactly one reversal applies —
    /// which is why this uses Adlam, a supplementary-plane RTL script.</para>
    /// </summary>
    [Fact]
    public void SurrogatePair_StaysIntactInsideAReversedRun()
    {
        // U+1E900 ADLAM CAPITAL LETTER ALIF — a real right-to-left script outside the BMP.
        // Exactly ONE character: two of them would legitimately swap places in the display (they
        // are right-to-left), so a substring assertion could not tell that apart from the
        // corruption under test.
        const string Adlam = "\U0001E900";
        string text = Shalom + " " + Adlam;

        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);
        int first = text.IndexOf(Adlam, StringComparison.Ordinal);

        // Precondition: the pair really is at an odd level, so exactly one reversal applies to it.
        Assert.Equal(1, para.LevelAt(first) & 1);

        int[] order = para.VisualOrder(0, text.Length);
        string rebuilt = new string(order.Select(para.MirroredCharAt).ToArray());

        Assert.Equal(text.Length, rebuilt.Length);
        Assert.Contains(Adlam, rebuilt, StringComparison.Ordinal);
        for (int i = 0; i < rebuilt.Length; i++)
            if (char.IsHighSurrogate(rebuilt[i]))
                Assert.True(i + 1 < rebuilt.Length && char.IsLowSurrogate(rebuilt[i + 1]),
                    $"a high surrogate at {i} must be followed by its low surrogate");
    }

    /// <summary>
    /// The classification fix the test above depends on: a supplementary-plane right-to-left
    /// script must resolve right-to-left. Both surrogates used to fall through to strong <c>L</c>
    /// independently, so an all-Adlam paragraph auto-detected as left-to-right.
    /// </summary>
    [Fact]
    public void AstralRtlScript_ResolvesRightToLeft()
    {
        const string Adlam = "\U0001E900\U0001E921";

        var para = UnicodeBidi.ResolveParagraph(Adlam, BaseDirection.Auto);

        Assert.True(para.IsRightToLeft, "Adlam is a right-to-left script");
        Assert.Equal(1, para.LevelAt(0) & 1);
    }

    [Fact]
    public void AstralLtrScript_StillResolvesLeftToRight()
    {
        // The control: an astral character that is NOT right-to-left must be unaffected.
        const string Emoji = "\U0001F600";

        var para = UnicodeBidi.ResolveParagraph(Emoji + "abc", BaseDirection.Auto);

        Assert.False(para.IsRightToLeft);
    }

    // ------------------------------------------------------------------ L4 by logical index

    [Fact]
    public void L4_MirrorsByLogicalIndex_EvenWhenTheLineCarriesTwoLevels()
    {
        string text = $"{Shalom} (abc) {Shalom}";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);

        int open = text.IndexOf('(');
        int close = text.IndexOf(')');

        // Both brackets resolved RTL (odd level), so both mirror — regardless of the level of
        // whatever happens to be displayed at their visual positions.
        Assert.Equal(1, para.LevelAt(open) & 1);
        Assert.Equal(1, para.LevelAt(close) & 1);
        Assert.Equal(')', para.MirroredCharAt(open));
        Assert.Equal('(', para.MirroredCharAt(close));
    }

    [Fact]
    public void L4_LeavesLtrBracketsAlone()
    {
        const string text = "(abc)";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.LeftToRight);

        Assert.Equal('(', para.MirroredCharAt(0));
        Assert.Equal(')', para.MirroredCharAt(4));
    }

    // ------------------------------------------------------------------ agreement with Reorder

    /// <summary>
    /// <see cref="UnicodeBidi.Reorder(string, BaseDirection)"/> is implemented on top of
    /// <see cref="UnicodeBidi.ResolveParagraph"/>, so composing the permutation with the mirrored
    /// characters must reproduce it exactly. This is what keeps the two from drifting into
    /// separate implementations of the same algorithm.
    /// </summary>
    [Theory]
    [InlineData("שלום עולם", BaseDirection.Auto)]
    [InlineData("שלום John Smith", BaseDirection.RightToLeft)]
    [InlineData("Hello שלום world", BaseDirection.LeftToRight)]
    [InlineData("1,234 ₪", BaseDirection.RightToLeft)]
    [InlineData("(שלום)", BaseDirection.Auto)]
    [InlineData("abc", BaseDirection.RightToLeft)]
    public void VisualOrderComposedWithMirroring_MatchesReorder(string text, BaseDirection dir)
    {
        var para = UnicodeBidi.ResolveParagraph(text, dir);
        int[] order = para.VisualOrder(0, text.Length);
        string composed = new string(order.Select(para.MirroredCharAt).ToArray());

        Assert.Equal(UnicodeBidi.Reorder(text, dir), composed);
    }

    // ------------------------------------------------------------------ contracts

    [Fact]
    public void VisualOrder_IsAPermutationOfTheRequestedRange()
    {
        string text = $"{Shalom} abc {Shalom} 123";
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);

        int[] order = para.VisualOrder(2, 8);

        Assert.Equal(8, order.Length);
        Assert.Equal(Enumerable.Range(2, 8).ToArray(), order.OrderBy(i => i).ToArray());
    }

    [Fact]
    public void ForcedDirection_OverridesFirstStrongDetection()
    {
        // "Tel Aviv 12" in an RTL field: first-strong would call this LTR, which is wrong when the
        // direction is a property of the form rather than of the value.
        var auto = UnicodeBidi.ResolveParagraph("Tel Aviv 12", BaseDirection.Auto);
        var forced = UnicodeBidi.ResolveParagraph("Tel Aviv 12", BaseDirection.RightToLeft);

        Assert.False(auto.IsRightToLeft);
        Assert.True(forced.IsRightToLeft);
    }

    [Fact]
    public void EmptyText_ResolvesToAnEmptyParagraph()
    {
        var para = UnicodeBidi.ResolveParagraph("", BaseDirection.Auto);
        Assert.Equal(0, para.Length);
        Assert.Empty(para.VisualOrder(0, 0));
    }

    [Fact]
    public void OutOfRangeLine_IsRefused()
    {
        var para = UnicodeBidi.ResolveParagraph("abc", BaseDirection.Auto);
        Assert.Throws<ArgumentOutOfRangeException>(() => para.VisualOrder(1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => para.LevelAt(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => para.MirroredCharAt(-1));
    }

    /// <summary>
    /// L1 where it changes the order: in a right-to-left paragraph, the space before a tab and a
    /// newline inside the line both sit between Latin letters, so the paragraph resolves them to
    /// the Latin level; L1 sends them back to the paragraph level, splitting the Latin run in two.
    /// </summary>
    [Theory]
    [InlineData("ab \tcd", new[] { 4, 5, 3, 2, 0, 1 })]   // whitespace before a segment separator
    [InlineData("ab\ncd", new[] { 3, 4, 2, 0, 1 })]       // a paragraph separator inside the line
    public void L1_SeparatorsAndTheWhitespaceBeforeThem_SplitALatinRun(string text, int[] expected)
    {
        var para = UnicodeBidi.ResolveParagraph(text, BaseDirection.RightToLeft);
        Assert.Equal(expected, para.VisualOrder(0, text.Length));
    }

    /// <summary>
    /// A start so large that <c>start + length</c> overflows is still refused as an argument error,
    /// not let through to fail as an index out of range. The whole-range call is the control.
    /// </summary>
    [Fact]
    public void VisualOrder_ARangeWhoseEndOverflows_IsRefusedAsAnArgument()
    {
        var para = UnicodeBidi.ResolveParagraph("abc", BaseDirection.LeftToRight);
        Assert.Equal(new[] { 0, 1, 2 }, para.VisualOrder(0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => para.VisualOrder(int.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => para.VisualOrder(1, int.MaxValue));
    }
}
