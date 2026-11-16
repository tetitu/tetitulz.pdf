// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Arabic positional forms — the medial and final ones, which the shaper could not produce at all.
///
/// <para>Two independent defects made every Arabic word render as a row of disconnected letters.
/// The substitution is in place, so by the time letter <i>i</i> was examined letter <i>i−1</i> had
/// already been replaced by a presentation form that <c>IsArabicLetter</c> does not recognise —
/// making <c>canJoinRight</c> false for every letter in every word. And the neighbour test looked
/// at the immediate neighbour, so a harakat between two letters (the ordinary vocalised spelling)
/// broke the join even once the first defect was fixed. Unicode joining treats a non-spacing mark
/// as transparent.</para>
///
/// <para>Every expectation below is derived from the joining rules and the Presentation Forms-B
/// block, not captured from the implementation — the previous output was self-consistent and
/// wrong.</para>
/// </summary>
public class ArabicJoiningTests
{
    // بيت — beh + yeh + teh. Unvocalised, so only the in-place-substitution defect is in play.
    [Fact]
    public void Shape_ThreeLetterWord_GivesInitialMedialAndFinalForms()
    {
        string shaped = OpenTypeShaper.Shape("بيت");

        Assert.Equal(3, shaped.Length);
        Assert.Equal(0xFE91, shaped[0]);   // BEH initial
        Assert.Equal(0xFEF4, shaped[1]);   // YEH medial
        Assert.Equal(0xFE96, shaped[2]);   // TEH final
    }

    // بَيْت — the same word vocalised: beh + fatha + yeh + sukun + teh.
    // The marks must not break the joining, and must survive in the output.
    [Fact]
    public void Shape_VocalisedWord_JoinsAcrossHarakat()
    {
        string shaped = OpenTypeShaper.Shape("بَيْت");

        Assert.Equal(5, shaped.Length);
        Assert.Equal(0xFE91, shaped[0]);   // BEH initial — joins across the fatha to the yeh
        Assert.Equal('َ', shaped[1]); // fatha, passed through
        Assert.Equal(0xFEF4, shaped[2]);   // YEH medial — both neighbours are letters
        Assert.Equal('ْ', shaped[3]); // sukun, passed through
        Assert.Equal(0xFE96, shaped[4]);   // TEH final
    }

    // رؤية — reh + waw-with-hamza + yeh + teh marbuta.
    // U+0624 is right-joining, so the yeh after it must be INITIAL, not medial.
    [Fact]
    public void Shape_AfterARightJoiningWawHamza_TheNextLetterIsInitialNotMedial()
    {
        string shaped = OpenTypeShaper.Shape("رؤية");

        Assert.Equal(0xFEF3, shaped[2]);   // YEH initial — no connecting bar back to the waw-hamza
        Assert.Equal(0xFE94, shaped[3]);   // TEH MARBUTA final
    }

    // U+0629 TEH MARBUTA is right-joining too: it only ever ends a word.
    [Fact]
    public void Shape_TehMarbutaIsRightJoining_SoAFollowingLetterDoesNotJoinBackToIt()
    {
        // teh marbuta followed by beh: the beh must be ISOLATED (nothing joins to it on either
        // side), not final.
        string shaped = OpenTypeShaper.Shape("ةب");

        Assert.Equal(0xFE8F, shaped[1]);   // BEH isolated
    }

    [Fact]
    public void Shape_LetterFollowedByASpace_TakesItsFinalForm()
    {
        // A space is not transparent — it must still break the join.
        string shaped = OpenTypeShaper.Shape("بي ت");

        Assert.Equal(0xFE91, shaped[0]);   // BEH initial
        Assert.Equal(0xFEF2, shaped[1]);   // YEH final — the space ends the word
        Assert.Equal(' ', shaped[2]);
        Assert.Equal(0xFE95, shaped[3]);   // TEH isolated — it stands alone after the space
    }

    // Nine Hebrew letters beside a three-letter Arabic word: the Arabic joins as it would alone, since
    // the pass touches Arabic letters only and runs whenever the text holds one — a shaper that shaped
    // the script with the most letters left this word as three isolated glyphs.
    [Fact]
    public void Shape_ArabicWordBesideMoreHebrew_StillJoins()
    {
        string shaped = OpenTypeShaper.Shape("אבגדהוזחט بيت");

        Assert.Equal("אבגדהוזחט ", shaped.Substring(0, 10));
        Assert.Equal(0xFE91, shaped[10]);   // BEH initial
        Assert.Equal(0xFEF4, shaped[11]);   // YEH medial
        Assert.Equal(0xFE96, shaped[12]);   // TEH final
    }
}
