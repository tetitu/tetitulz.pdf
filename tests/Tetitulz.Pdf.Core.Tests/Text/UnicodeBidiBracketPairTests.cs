// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// UAX #9 <b>N0</b> — paired brackets take a direction from what they ENCLOSE, so both members of a
/// pair move together.
/// </summary>
/// <remarks>
/// Without N0, N1/N2 resolve each bracket independently from its own neighbours, which is how a pair
/// ends up split across a direction boundary — the classic "backwards bracket" in bidi text.
/// <para>Hebrew is used as the RTL alphabet throughout: א = U+05D0, ב = U+05D1, ג = U+05D2.</para>
/// </remarks>
public sealed class UnicodeBidiBracketPairTests
{
    private const string Alef = "א";
    private const string Bet = "ב";
    private const string Gimel = "ג";

    /// <summary>
    /// N0 b: the pair encloses a strong type matching the embedding direction, so both brackets take
    /// it. <b>A control, not a discriminating test</b> — N1/N2 reach the same answer here from the
    /// neighbours alone, which is exactly why it is worth pinning: N0 must not disturb the case that
    /// was already right. It stays green when N0 is disabled, and that is the point.
    /// </summary>
    [Fact]
    public void RtlParagraph_BracketsAroundRtlText_AreUnchangedByN0()
    {
        string logical = $"{Alef}({Bet}){Gimel}";
        string visual = UnicodeBidi.Reorder(logical);

        // Reversed, the '(' at logical index 1 becomes the ')' glyph at the mirrored position.
        Assert.Equal($"{Gimel}({Bet}){Alef}", visual);
    }

    /// <summary>
    /// N0 c.1: the pair encloses only the OPPOSITE direction, and the context before the opening
    /// bracket is that same opposite direction — so the brackets take the opposite direction too,
    /// keeping them attached to the Latin run rather than to the surrounding Hebrew.
    /// </summary>
    [Fact]
    public void RtlParagraph_BracketsAroundLatinText_FollowTheLatinRun()
    {
        string logical = $"{Alef} ab(cd) {Bet}";
        string visual = UnicodeBidi.Reorder(logical);

        // The Latin span including its brackets stays in logical order inside the reversed line;
        // the parentheses must NOT be mirrored, because they resolved to L.
        Assert.Contains("ab(cd)", visual);
    }

    /// <summary>
    /// N0 d: nothing strong inside, so N0 declines and N1/N2 decide. The point is that N0 does not
    /// force a direction it has no evidence for.
    /// </summary>
    [Fact]
    public void EmptyBrackets_AreLeftToTheNeutralRules()
    {
        string logical = $"{Alef}() {Bet}";
        string visual = UnicodeBidi.Reorder(logical);

        // Both brackets surrounded by RTL resolve RTL and mirror together — the pair is never split.
        Assert.Contains("()", visual);
    }

    /// <summary>
    /// A pair must be matched even when other brackets are opened and never closed between them —
    /// BD16 pops the matched entry <b>and everything above it</b>.
    /// </summary>
    [Fact]
    public void UnclosedInnerBracket_DoesNotPreventTheOuterPairMatching()
    {
        string logical = $"{Alef} a([b) {Bet}";
        string visual = UnicodeBidi.Reorder(logical);

        // The '(' … ')' pair still resolves together around its Latin content.
        Assert.Contains("a([b)", visual);
    }

    /// <summary>
    /// <c>&lt;</c> and <c>&gt;</c> are Bidi_Mirrored but their Bidi_Paired_Bracket_Type is
    /// <b>None</b>, so N0 must not treat them as a pair.
    /// </summary>
    /// <remarks>
    /// This is the trap the implementation calls out: the mirror table and the bracket table answer
    /// different questions. Reusing the mirror table for N0 would make <c>a &lt; b &gt; c</c> a
    /// bracket pair and drag both operators to one direction.
    /// </remarks>
    [Fact]
    public void AngleBrackets_AreMirroredButNotAPair()
    {
        // The closing bracket must sit where N1/N2 alone would resolve it the OTHER way: Latin on
        // its left, whitespace-then-RTL on its right. With `a<b>c` both brackets are flanked by
        // Latin, so the neutral rules already answer L and the test cannot see N0 at all.
        string withAngles = UnicodeBidi.Reorder($"{Alef} ab<cd> {Bet}");
        string withParens = UnicodeBidi.Reorder($"{Alef} ab(cd) {Bet}");

        // The parenthesised form is a real pair: N0 keeps both brackets with the Latin run.
        Assert.Contains("ab(cd)", withParens);
        // The angle form is not a pair, so its closing bracket still resolves from its neighbours
        // and is pulled away. Feeding the mirror table to N0 would make the two agree.
        Assert.DoesNotContain("ab<cd>", withAngles);
    }

    /// <summary>
    /// BD16 matches under canonical equivalence: U+2329 ⟨ is canonically U+3008 〈, so it closes
    /// with either U+232A ⟩ or U+3009 〉.
    /// </summary>
    [Fact]
    public void CanonicallyEquivalentBrackets_ArePaired()
    {
        // Opening U+2329, closing U+3009: different code points, canonically the same pair.
        // Shaped so the CLOSING bracket's neighbours would resolve it the other way without N0
        // (Latin on its left, whitespace-then-RTL on its right). With the brackets flanked by Latin
        // on both sides the neutral rules already answer L and the test cannot see N0 at all.
        const string open = "\u2329", close = "\u3009";
        string visual = UnicodeBidi.Reorder($"{Alef} ab{open}cd{close} {Bet}");

        Assert.Contains($"ab{open}cd{close}", visual);
    }

    /// <summary>
    /// ...and a bracket that is not the canonical partner does not pair, so the closing bracket
    /// falls back to its neighbours. Without this control the test above would also pass on an
    /// implementation that paired any opening bracket with any closing one.
    /// </summary>
    [Fact]
    public void NonMatchingBrackets_AreNotPaired()
    {
        const string open = "\u2329";
        string visual = UnicodeBidi.Reorder($"{Alef} ab{open}cd] {Bet}");

        Assert.DoesNotContain($"ab{open}cd]", visual);
    }

    /// <summary>An LTR paragraph with no RTL at all must be returned untouched.</summary>
    [Fact]
    public void LtrOnlyText_IsUnchanged()
    {
        const string s = "a(b)c [d] {e}";
        Assert.Equal(s, UnicodeBidi.Reorder(s));
    }

    /// <summary>
    /// Deeply nested brackets terminate and do not mis-pair: BD16 caps its stack at 63 and abandons
    /// pairing entirely on overflow rather than dropping one entry, which would mis-pair the rest.
    /// </summary>
    [Fact]
    public void DeeplyNestedBrackets_TerminateWithoutMispairing()
    {
        string logical = Alef + new string('(', 200) + "a" + new string(')', 200) + Bet;

        string visual = UnicodeBidi.Reorder(logical);

        // Every character survives; the run is reordered, never truncated.
        Assert.Equal(logical.Length, visual.Length);
    }
}
