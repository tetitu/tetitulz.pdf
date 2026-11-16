// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="OpenTypeShaper.Shape"/> over Arabic text that also contains a supplementary-plane
/// (astral) character.
/// </summary>
/// <remarks>
/// <para>
/// <c>ShapeArabic</c> copies UTF-16 code UNITS into an <c>int[]</c> and then reassembled the string
/// with <c>char.ConvertFromUtf32</c> per element. That method throws
/// <see cref="System.ArgumentOutOfRangeException"/> for anything in U+D800–U+DFFF, so each half of
/// a surrogate pair threw — and since <c>ClassifyScript</c> puts surrogates past its last range
/// test they count as Latin, leaving the Arabic letters dominant and <c>ShapeArabic</c> selected.
/// </para>
/// <para>
/// Nothing on the path catches it: not <c>TextRunComposer.Compose</c>, not the AcroForm appearance
/// generator, not the template engine. Filling an Arabic form field whose value contains an emoji
/// aborted the whole fill.
/// </para>
/// </remarks>
public class OpenTypeShaperAstralTests
{
    /// <summary>U+1F600 GRINNING FACE — two UTF-16 code units, D83D DE00.</summary>
    private const string Grinning = "\U0001F600";

    /// <summary>مرحبا — five Arabic letters, all dual-joining except the trailing alef.</summary>
    private const string Marhaba = "مرحبا";

    [Fact]
    public void Shape_ArabicWithAstralCharacter_KeepsTheAstralCharacterIntact()
    {
        string shaped = OpenTypeShaper.Shape(Marhaba + " " + Grinning);

        // The emoji survives as a well-formed pair, not as two lone surrogates and not dropped.
        Assert.EndsWith(Grinning, shaped, System.StringComparison.Ordinal);
        Assert.Equal(1, CountCodePoint(shaped, 0x1F600));
    }

    /// <summary>
    /// The astral character must not cost the Arabic its shaping. Presentation forms live in
    /// U+FE70–U+FEFF / U+FB50–U+FDFF, so their presence is what proves ShapeArabic actually ran
    /// rather than the string being passed through by a defensive early return.
    /// </summary>
    [Fact]
    public void Shape_ArabicWithAstralCharacter_StillAppliesPositionalForms()
    {
        string shaped = OpenTypeShaper.Shape(Marhaba + " " + Grinning);

        Assert.DoesNotContain('م', shaped);            // the raw MEEM is gone…
        Assert.Contains(shaped, c => c >= 0xFE70 && c <= 0xFEFF); // …replaced by a presentation form

        // And the shaping matches what the same Arabic produces on its own — the emoji changes
        // nothing about the letters before it.
        string arabicAlone = OpenTypeShaper.Shape(Marhaba);
        Assert.StartsWith(arabicAlone, shaped, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A lone (unpaired) surrogate is malformed input rather than an astral character; it must be
    /// carried through rather than throwing, since it reaches here from arbitrary field values.
    /// </summary>
    [Fact]
    public void Shape_ArabicWithUnpairedSurrogate_IsCarriedThroughUnchanged()
    {
        string shaped = OpenTypeShaper.Shape(Marhaba + "\uD83D");

        Assert.EndsWith("\uD83D", shaped, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The astral character must be preserved wherever it sits, including between two Arabic runs
    /// where it also breaks the joining context.
    /// </summary>
    [Fact]
    public void Shape_AstralCharacterBetweenArabicRuns_IsPreserved()
    {
        string shaped = OpenTypeShaper.Shape(Marhaba + Grinning + Marhaba);

        Assert.Equal(1, CountCodePoint(shaped, 0x1F600));
        Assert.Contains(shaped, c => c >= 0xFE70 && c <= 0xFEFF);
    }

    private static int CountCodePoint(string s, int codePoint)
    {
        int count = 0;
        for (int i = 0; i < s.Length; i++)
        {
            int cp = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])
                ? char.ConvertToUtf32(s[i], s[i + 1])
                : s[i];
            if (cp == codePoint) count++;
            if (cp > 0xFFFF) i++;
        }
        return count;
    }
}
