// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="OpenTypeShaper.Shape"/> must survive a supplementary-plane character inside
/// Arabic-dominant text.
///
/// <para><c>ShapeArabic</c> copies UTF-16 <b>code units</b> into an <c>int[]</c> and then
/// reassembled the string with <c>char.ConvertFromUtf32</c>, which throws
/// <see cref="System.ArgumentOutOfRangeException"/> for anything in U+D800–U+DFFF. An emoji
/// therefore threw out of <c>Shape</c>, and nothing on the way in catches it — not
/// <c>PdfOperations.ShapeText</c>, not <c>TextRunComposer.Compose</c>, not the AcroForm
/// appearance generator, not the template engine — so filling an Arabic form field whose value
/// contained one aborted the whole operation.</para>
/// </summary>
public class OpenTypeShaperSurrogateTests
{
    private const string Emoji = "\U0001F600";           // U+1F600, i.e. D83D DE00
    private const string Marhaba = "مرحبا";   // مرحبا

    [Fact]
    public void Shape_ArabicContainingAnAstralCharacter_KeepsTheSurrogatePairIntact()
    {
        string shaped = OpenTypeShaper.Shape(Marhaba + " " + Emoji);

        // The astral character must come back as a well-formed pair, not as replacement text
        // and not dropped.
        Assert.EndsWith(Emoji, shaped);
        Assert.Equal(0xD83D, shaped[shaped.Length - 2]);
        Assert.Equal(0xDE00, shaped[shaped.Length - 1]);
    }

    [Fact]
    public void Shape_ArabicContainingAnAstralCharacter_StillAppliesPositionalForms()
    {
        // The surrogates must not disturb the shaping of the Arabic that precedes them. Asserting
        // the substitution — not merely that nothing threw — is the point: a shaper that gave up
        // and returned its input unchanged would also "not throw".
        //
        // MIM opens the word, so it takes the INITIAL form U+FEE3. The trailing ALEF is asserted
        // only to be *some* presentation form, deliberately: this shaper substitutes in place, so
        // by the time a letter is examined its predecessor has already been replaced by a
        // presentation form that IsArabicLetter does not recognise — medial and final forms are
        // therefore never produced. That is a pre-existing limitation of the joining analysis and
        // is not what these tests are about; pinning U+FE8E here would assert a behaviour the
        // shaper does not have.
        string shaped = OpenTypeShaper.Shape(Marhaba + " " + Emoji);

        Assert.Equal(0xFEE3, shaped[0]);   // MIM, initial
        Assert.Equal(' ', shaped[5]);

        // Every Arabic letter must have moved into the presentation-forms block.
        for (int i = 0; i < 5; i++)
            Assert.InRange(shaped[i], (char)0xFE70, (char)0xFEFF);
    }

    [Fact]
    public void Shape_ArabicWithAnUnpairedSurrogate_IsStillShaped()
    {
        // A lone high surrogate is what a string sliced mid-pair produces. It is not valid
        // UTF-16, but it is a `string` the caller can hand us, and ConvertFromUtf32 rejects it
        // just as hard as a valid pair's halves.
        string shaped = OpenTypeShaper.Shape(Marhaba + "\uD83D");

        Assert.Equal('\uD83D', shaped[shaped.Length - 1]);
        Assert.Equal(0xFEE3, shaped[0]);
    }

    [Fact]
    public void Shape_ArabicWithNoAstralCharacter_IsUnaffectedByTheSurrogateHandling()
    {
        // Guards against a "fix" that routes every code point through the surrogate branch:
        // one code unit in, one out, and every one of them substituted.
        string shaped = OpenTypeShaper.Shape(Marhaba);

        Assert.Equal(5, shaped.Length);
        Assert.Equal(0xFEE3, shaped[0]);   // MIM, initial
        for (int i = 0; i < shaped.Length; i++)
            Assert.InRange(shaped[i], (char)0xFE70, (char)0xFEFF);
    }
}
