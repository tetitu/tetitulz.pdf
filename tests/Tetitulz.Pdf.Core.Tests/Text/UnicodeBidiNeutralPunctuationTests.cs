// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Bidi_Class of brackets and punctuation in <see cref="UnicodeBidi"/>: they are <b>ON</b> (Other
/// Neutral), not strong <c>L</c>.
///
/// <para><b>The defect these pin.</b> A <c>GetBidiClass</c> ending in <c>return BidiClass.L</c>
/// for any character it does not recognise classifies every bracket, quote, <c>?</c> and
/// <c>!</c> strong left-to-right. A strong type is not neutral, so N1/N2 — the rules whose whole
/// job is to resolve a neutral from its surroundings — never look at one. In an RTL paragraph that
/// anchors punctuation to LTR regardless of context.</para>
///
/// <para><b>These assert on mirroring and on position relative to RTL text</b>, which is where a
/// neutral and a strong-L bracket differ. Asserting that the brackets are present and that content
/// sits between them would pass under the strong-L classification too, because character presence
/// is not what the classification changes.</para>
/// </summary>
public class UnicodeBidiNeutralPunctuationTests
{
    private const string Shalom = "שלום";   // shalom, all-RTL

    /// <summary>
    /// A bracket pair whose preceding context is an RTL run resolves RTL, and L4 then mirrors it.
    /// With the brackets typed strong-L they would resolve to an even level instead and never be
    /// mirrored.
    ///
    /// <para><b>The expected string is <c>"(abc)"</c>, not <c>")abc("</c></b>:
    /// <see cref="BracketsAroundRtlText_AreMirroredAsAMatchedPair"/> immediately below asserts
    /// <c>"(םולש)"</c>, with the opening glyph leading, and both are RTL paragraphs whose bracket
    /// pair resolves RTL, so they cannot legitimately disagree about which glyph leads.
    ///
    /// <c>(שלום)</c> is uniformly level 1 while this fixture mixes levels 1 and 2. Applying L4 by
    /// walking the <i>already reordered</i> output while indexing <c>levels[i]</c> — which describes
    /// the character that was logically at <c>i</c>, not the one displayed there — agrees with one
    /// level and not with two: the brackets would be mirrored according to the levels of <c>'c'</c>
    /// and <c>'2'</c> (both even), so neither would be mirrored, and the pair would come out inside
    /// out.
    ///
    /// UAX #9 §3.4 Example 2 settles it: memory <c>'&gt;he said …&lt;'</c> displays as
    /// <c>&lt;he said …&gt;</c> — mirrored glyphs <i>and</i> reversed positions, which is what
    /// <c>(abc)</c> is here. L4 is applied by logical index in
    /// <see cref="BidiParagraph.MirroredCharAt"/>, where the misalignment is unavailable.</para>
    /// </summary>
    [Fact]
    public void BracketsInAnRtlParagraph_ResolveRtlAndAreMirrored()
    {
        string visual = UnicodeBidi.Reorder($"{Shalom} 12 (abc) {Shalom}");

        Assert.Contains("(abc)", visual, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The regression guard for the defect above: a bracket pair must mirror the same way whether
    /// or not the line also carries a higher embedding level. These two inputs differ only in
    /// containing an LTR run, which must not change how the brackets are treated.
    /// </summary>
    [Fact]
    public void BracketMirroring_IsTheSameWithAndWithoutASecondEmbeddingLevel()
    {
        string oneLevel = UnicodeBidi.Reorder($"{Shalom} (ג) {Shalom}");
        string twoLevels = UnicodeBidi.Reorder($"{Shalom} (abc) {Shalom}");

        Assert.Contains("(ג)", oneLevel, System.StringComparison.Ordinal);
        Assert.Contains("(abc)", twoLevels, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BracketsAroundRtlText_AreMirroredAsAMatchedPair()
    {
        Assert.Equal("(םולש)", UnicodeBidi.Reorder($"({Shalom})"));
    }

    /// <summary>
    /// The converse, so the pair of tests pins that mirroring follows the resolved direction rather
    /// than being unconditional: an LTR paragraph must leave its brackets alone.
    /// </summary>
    [Fact]
    public void BracketsAroundLatinText_AreNotMirrored()
    {
        Assert.Equal("(abc)", UnicodeBidi.Reorder("(abc)"));
    }

    [Fact]
    public void NestedBrackets_BothPairsSurvive()
    {
        string visual = UnicodeBidi.Reorder($"{Shalom} (a [12] b) {Shalom}");

        Assert.Equal(1, CountOf(visual, '('));
        Assert.Equal(1, CountOf(visual, ')'));
        Assert.Equal(1, CountOf(visual, '['));
        Assert.Equal(1, CountOf(visual, ']'));
    }

    /// <summary>A pure-LTR string takes the fast path and is returned untouched.</summary>
    [Fact]
    public void PureLatinText_IsUnchanged()
    {
        const string text = "See (RFC 3161) for details — really?";
        Assert.Equal(text, UnicodeBidi.Reorder(text));
    }

    /// <summary>
    /// Adversarial: a long unmatched bracket run must terminate and lose no characters. Nothing
    /// here pairs brackets, so this is about the reordering machinery surviving the input.
    /// </summary>
    [Fact]
    public void AdversarialDeeplyNestedBrackets_TerminateWithoutThrowing()
    {
        string text = Shalom + new string('(', 500) + "a" + new string(')', 500) + Shalom;

        Assert.Equal(text.Length, UnicodeBidi.Reorder(text).Length);
    }

    private const char OpenQuote = (char)0x201C;    // LEFT DOUBLE QUOTATION MARK
    private const char CloseQuote = (char)0x201D;   // RIGHT DOUBLE QUOTATION MARK
    private const char Apostrophe = (char)0x2019;   // RIGHT SINGLE QUOTATION MARK
    private const char Rlm = (char)0x200F;          // RIGHT-TO-LEFT MARK

    /// <summary>
    /// A curly apostrophe or closing quote is a neutral, so the paragraph's direction comes from the
    /// Hebrew after it. Classified strong left-to-right, as the default made them, it decided the
    /// paragraph itself and the line came back in Latin order.
    /// </summary>
    [Theory]
    [InlineData(Apostrophe)]
    [InlineData(CloseQuote)]
    public void ACurlyQuoteDoesNotDecideTheParagraphDirection(char quote)
    {
        Assert.Equal("םולש" + quote, UnicodeBidi.Reorder(quote + Shalom));
    }

    /// <summary>
    /// The curly quotes are not Bidi_Mirrored: a right-to-left line keeps each as it is. The
    /// guillemets are, so the control mirrors them in the same position — the difference is the
    /// characters', not the line's.
    /// </summary>
    [Fact]
    public void CurlyQuotesAreNotMirroredInARightToLeftLine()
    {
        Assert.Equal(string.Concat(CloseQuote, "א", OpenQuote, " םולש"),
                     UnicodeBidi.Reorder(string.Concat(Shalom, " ", OpenQuote, "א", CloseQuote)));

        Assert.Equal("\u00ABא\u00BB םולש", UnicodeBidi.Reorder(Shalom + " \u00ABא\u00BB"));
    }

    /// <summary>
    /// The right-to-left mark is a strong right-to-left character: a paragraph it begins is right
    /// to left, so its two Latin and numeric runs trade places. As a strong left-to-right
    /// character, as it was, it left the line as it was.
    /// </summary>
    [Fact]
    public void TheRightToLeftMarkBeginsARightToLeftParagraph()
    {
        Assert.Equal("abc 123" + Rlm, UnicodeBidi.Reorder(Rlm + "123 abc"));
    }

    private static int CountOf(string s, char c)
    {
        int n = 0;
        foreach (char ch in s) if (ch == c) n++;
        return n;
    }
}
