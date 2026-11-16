// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="HebrewSearchNormalizer"/> — folding the Hebrew spelling differences a reader does not
/// see, so a search can match across them.
/// </summary>
/// <remarks>
/// <para>Every case asserts the <b>folded string</b> and, where it matters, the <b>index map</b>.
/// Asserting only that folding changed something would pass for a fold that erased the wrong
/// characters, and asserting only the string would pass for one that silently moved every
/// highlight rectangle the callers derive from positions.</para>
///
/// <para>The file is deliberately half negative. A normaliser's failure mode is not "it did
/// nothing" — it is "it matched too much", and only a test that names a pair which must
/// <i>stay</i> distinct can see that.</para>
/// </remarks>
public class HebrewSearchNormalizerTests
{
    // שלום with niqqud (shin+holam, lamed+shuruq-ish, vav, mem-sofit), as a pointed document
    // extracts it. The unpointed spelling is what a person types.
    private const string PointedShalom = "שָׁלוֹם";
    private const string PlainShalom = "שלום";

    // ------------------------------------------------------------------
    //  Niqqud — the case that makes pointed documents searchable at all
    // ------------------------------------------------------------------

    [Fact]
    public void Niqqud_IsStripped_SoAnUnpointedQueryMatchesAPointedDocument()
    {
        Assert.Equal(PlainShalom, HebrewSearchNormalizer.Fold(PointedShalom));
    }

    /// <summary>
    /// The map points every surviving character back at the source character it came from — which
    /// is what lets a caller report the hit in the original text's coordinates.
    /// </summary>
    [Fact]
    public void TheIndexMapPointsBackAtTheOriginalCharacters()
    {
        string folded = HebrewSearchNormalizer.Fold(
            PointedShalom, HebrewFolding.Default, out int[] source);

        Assert.Equal(folded.Length, source.Length);

        // Each mapped position really holds that character in the source.
        for (int i = 0; i < folded.Length; i++)
            Assert.Equal(folded[i], PointedShalom[source[i]]);

        // …and the map is strictly increasing: a fold never reorders.
        for (int i = 1; i < source.Length; i++)
            Assert.True(source[i] > source[i - 1], "the index map went backwards at " + i);
    }

    /// <summary>
    /// <b>The end of a match cannot be derived from its length.</b> Four characters of folded text
    /// span seven of source here, so a caller computing <c>start + foldedLength</c> would under-run
    /// the highlight by three characters — the specific mistake the map exists to prevent.
    /// </summary>
    [Fact]
    public void TheMatchEndComesFromTheMapNotFromTheFoldedLength()
    {
        string folded = HebrewSearchNormalizer.Fold(
            PointedShalom, HebrewFolding.Default, out int[] source);

        int endFromMap = source[folded.Length - 1] + 1;

        Assert.Equal(PointedShalom.Length, endFromMap);
        Assert.NotEqual(folded.Length, endFromMap);
    }

    /// <summary>
    /// Only Hebrew marks go. A Latin or Arabic diacritic in the same document survives, because the
    /// filter is "non-spacing mark <i>inside the Hebrew block</i>" and not either half alone.
    /// </summary>
    [Fact]
    public void NonHebrewDiacriticsAreLeftAlone()
    {
        Assert.Equal("café", HebrewSearchNormalizer.Fold("café"));
        Assert.Equal("café", HebrewSearchNormalizer.Fold("café"));
    }

    [Fact]
    public void PlainLatinTextIsUnchanged()
    {
        const string s = "The quick brown fox, 1234.";
        Assert.Equal(s, HebrewSearchNormalizer.Fold(s));
    }

    // ------------------------------------------------------------------
    //  Punctuation
    // ------------------------------------------------------------------

    /// <summary>
    /// Both spellings fold to the ASCII form, so either one typed as a query finds either one in a
    /// document. Producers are inconsistent about which they emit and a reader cannot tell.
    /// </summary>
    [Theory]
    [InlineData("צה״ל", "צה\"ל")]      // gershayim -> "
    [InlineData("צה\"ל", "צה\"ל")]           // already ASCII, unchanged
    [InlineData("ד׳ר", "ד'ר")]          // geresh -> '
    [InlineData("בן־גוריון", "בן-גוריון")]  // maqaf -> -
    public void HebrewPunctuationFoldsToItsAsciiLookalike(string input, string expected)
    {
        Assert.Equal(expected, HebrewSearchNormalizer.Fold(input));
    }

    // ------------------------------------------------------------------
    //  Presentation forms
    // ------------------------------------------------------------------

    /// <summary>
    /// U+FB2A is shin-with-a-dot as one code point. Decomposing it lets the niqqud pass remove the
    /// dot, so the precomposed and the spelled-out forms of the same word meet.
    /// </summary>
    [Fact]
    public void APrecomposedPresentationForm_FoldsToItsBareLetter()
    {
        Assert.Equal("ש", HebrewSearchNormalizer.Fold("שׁ"));
    }

    /// <summary>Its map entry still names the single source character it came from.</summary>
    [Fact]
    public void ADecomposedPresentationForm_MapsBackToItsOneSourceCharacter()
    {
        string folded = HebrewSearchNormalizer.Fold(
            "אשׁב", HebrewFolding.Default, out int[] source);

        Assert.Equal("אשב", folded);
        Assert.Equal(new[] { 0, 1, 2 }, source);
    }

    // ------------------------------------------------------------------
    //  Final forms — off by default, and this is why
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>The negative control, and the reason the flag is opt-in.</b> The final form <i>is</i> the
    /// word boundary. Folded, ארץ becomes a substring of ארצות — a match the document does not
    /// contain, manufactured by the normalisation.
    /// </summary>
    [Fact]
    public void FinalFormFolding_MakesAWordASubstringOfAnUnrelatedOne()
    {
        string query = HebrewSearchNormalizer.Fold("ארץ", HebrewFolding.FinalForms);
        string text = HebrewSearchNormalizer.Fold("ארצות", HebrewFolding.FinalForms);

        Assert.Contains(query, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// …and with the default folding it does not, which is the whole point of leaving the flag off.
    /// Without this pair, "the default is safe" is an assertion in a doc comment and nothing else.
    /// </summary>
    [Fact]
    public void TheDefaultFolding_DoesNotMakeThatMatch()
    {
        string query = HebrewSearchNormalizer.Fold("ארץ");
        string text = HebrewSearchNormalizer.Fold("ארצות");

        Assert.DoesNotContain(query, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ך", "כ")]
    [InlineData("ם", "מ")]
    [InlineData("ן", "נ")]
    [InlineData("ף", "פ")]
    [InlineData("ץ", "צ")]
    public void EachFinalForm_FoldsToItsMedialTwinWhenAskedFor(string final, string medial)
    {
        Assert.Equal(medial, HebrewSearchNormalizer.Fold(final, HebrewFolding.FinalForms));
        Assert.Equal(final, HebrewSearchNormalizer.Fold(final));   // and not by default
    }

    // ------------------------------------------------------------------
    //  Flags are independent
    // ------------------------------------------------------------------

    [Fact]
    public void NoneFoldsNothing()
    {
        Assert.Equal(PointedShalom, HebrewSearchNormalizer.Fold(PointedShalom, HebrewFolding.None));
    }

    /// <summary>
    /// Asking for punctuation alone leaves the niqqud in place. Without this, one flag doing the
    /// work of another would be invisible.
    /// </summary>
    [Fact]
    public void PunctuationAlone_LeavesNiqqudAlone()
    {
        Assert.Equal(PointedShalom,
                     HebrewSearchNormalizer.Fold(PointedShalom, HebrewFolding.Punctuation));
    }

    [Fact]
    public void NiqqudAlone_LeavesPunctuationAlone()
    {
        Assert.Equal("צה״ל",
                     HebrewSearchNormalizer.Fold("צה״ל", HebrewFolding.Niqqud));
    }

    // ------------------------------------------------------------------
    //  WouldChange — the cheap pre-check
    // ------------------------------------------------------------------

    [Fact]
    public void WouldChange_IsTrueExactlyWhenFoldingChangesSomething()
    {
        foreach (string s in new[] { PointedShalom, "צה״ל", "שׁ" })
        {
            Assert.True(HebrewSearchNormalizer.WouldChange(s), s);
            Assert.NotEqual(s, HebrewSearchNormalizer.Fold(s));
        }

        foreach (string s in new[] { "plain latin", PlainShalom, "צה\"ל", "" })
        {
            Assert.False(HebrewSearchNormalizer.WouldChange(s), s);
            Assert.Equal(s, HebrewSearchNormalizer.Fold(s));
        }
    }

    /// <summary>
    /// "Exactly", over the whole presentation-forms block and every flag combination: the pre-check
    /// says yes precisely where the fold changes the character. It said yes for the wide letters,
    /// the ligature, U+FB1E and the unassigned code points, which the fold left whole.
    /// </summary>
    [Fact]
    public void WouldChange_AgreesWithFold_OnEveryPresentationFormUnderEveryFlagSet()
    {
        for (int flags = 0; flags < 16; flags++)
            for (char c = 'יִ'; c <= 'ﭏ'; c++)
            {
                string s = c.ToString();
                var folding = (HebrewFolding)flags;
                Assert.True(HebrewSearchNormalizer.WouldChange(s, folding) == (HebrewSearchNormalizer.Fold(s, folding) != s),
                    $"U+{(int)c:X4} under {folding}");
            }
    }

    /// <summary>
    /// The flag promises the block's base letters: a wide letter folds to its letter, and the
    /// alef-lamed ligature to alef and lamed, both mapped back to the ligature.
    /// </summary>
    [Fact]
    public void PresentationForms_FoldsTheWideLettersAndTheLigatureToTheirBaseLetters()
    {
        Assert.Equal("א", HebrewSearchNormalizer.Fold("ﬡ"));            // wide alef
        Assert.Equal("ע", HebrewSearchNormalizer.Fold("ﬠ"));            // alternative ayin
        Assert.Equal("אל", HebrewSearchNormalizer.Fold("xﭏ", HebrewFolding.Default, out int[] map).Substring(1));
        Assert.Equal(new[] { 0, 1, 1 }, map);
        Assert.Equal("ﬞ", HebrewSearchNormalizer.Fold("ﬞ"));            // no decomposition: kept
    }

    [Fact]
    public void WouldChange_TracksTheFlagsItIsGiven()
    {
        Assert.False(HebrewSearchNormalizer.WouldChange("ארץ"));
        Assert.True(HebrewSearchNormalizer.WouldChange("ארץ", HebrewFolding.FinalForms));
    }

    // ------------------------------------------------------------------
    //  Degenerate input
    // ------------------------------------------------------------------

    [Fact]
    public void NullIsNull_AndEmptyIsEmpty()
    {
        Assert.Null(HebrewSearchNormalizer.Fold(null));
        Assert.Null(HebrewSearchNormalizer.Fold(null, HebrewFolding.Default, out int[] noMap));
        Assert.Null(noMap);

        Assert.Equal(string.Empty, HebrewSearchNormalizer.Fold(string.Empty));
    }

    /// <summary>
    /// A string of nothing but marks folds to nothing, and the map is empty rather than stale — a
    /// caller indexing it must get no positions, not the identity.
    /// </summary>
    [Fact]
    public void AStringOfOnlyMarks_FoldsToEmptyWithAnEmptyMap()
    {
        string folded = HebrewSearchNormalizer.Fold(
            "ְִּ", HebrewFolding.Default, out int[] source);

        Assert.Equal(string.Empty, folded);
        Assert.Empty(source);
    }

    /// <summary>The map is the identity when nothing is folded, not null.</summary>
    [Fact]
    public void FoldingNothing_StillReportsAUsableIdentityMap()
    {
        HebrewSearchNormalizer.Fold("abc", HebrewFolding.None, out int[] source);

        Assert.Equal(new[] { 0, 1, 2 }, source);
    }
}
