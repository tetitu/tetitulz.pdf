// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Arabic-script joining beyond the Arabic alphabet's 28 letters, and the joining rules read from one
/// table. The shaper's forms stopped at U+064A, and its rule for which letters join leftwards was a
/// hand list defaulting to "everything up to U+064A": Persian's peh, tcheh, gaf, keheh and farsi yeh
/// had no forms and joined on neither side, so a Persian word was a row of separate letters; hamza,
/// which joins on neither side, counted as joining; and the tatweel broke a join it exists to lengthen.
/// </summary>
public class ExtendedArabicJoiningTests
{
    /// <summary>Peh, farsi yeh, keheh — پیک — initial, medial, final.</summary>
    [Fact]
    public void APersianWord_Joins()
        => Assert.Equal(Codes(0xFB58, 0xFBFF, 0xFB8F), Shape("پیک"));

    /// <summary>
    /// Gaf before jeh joins (initial, final); jeh joins only on its right, so a gaf after it stands
    /// alone — the right-joining rule for a letter past U+064A.
    /// </summary>
    [Fact]
    public void ARightJoiningPersianLetter_DoesNotJoinLeftwards()
    {
        Assert.Equal(Codes(0xFB94, 0xFB8B), Shape("گژ"));
        Assert.Equal(Codes(0xFB8A, 0xFB92), Shape("ژگ"));
    }

    /// <summary>Hamza joins on neither side: the behs either side of it stand alone.</summary>
    [Fact]
    public void Hamza_JoinsOnNeitherSide()
        => Assert.Equal(Codes(0xFE8F, 0xFE80, 0xFE8F), Shape("بءب"));

    /// <summary>The tatweel lengthens a join: the behs either side of it are initial and final.</summary>
    [Fact]
    public void Tatweel_KeepsTheJoin()
        => Assert.Equal(Codes(0xFE91, 0x0640, 0xFE90), Shape("بـب"));

    /// <summary>
    /// Every presentation form the shaper knows maps back to the letter Unicode decomposes it to —
    /// checked against .NET's own Unicode data (NFKD), not this repository's table; and the Persian
    /// letters' forms are among them.
    /// </summary>
    [Fact]
    public void EveryFormMapsBackToItsUnicodeLetter()
    {
        int known = 0;
        for (int form = 0xFB50; form <= 0xFEFF; form++)
        {
            if (form >= 0xFE00 && form < 0xFE70) continue;
            if (!OpenTypeShaper.TryGetBasePresentationForm(form, out int letter)) continue;
            known++;
            // NFKD of both sides: the decomposition is full, so a letter that itself decomposes (heh
            // with yeh above is heh and hamza) is compared as Unicode decomposes it.
            string nfkd = ((char)form).ToString().Normalize(NormalizationForm.FormKD);
            string letterNfkd = ((char)letter).ToString().Normalize(NormalizationForm.FormKD);
            Assert.True(nfkd == letterNfkd, $"U+{form:X4} maps to U+{letter:X4}, Unicode says {string.Join(" ", nfkd.Select(c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)))}");
        }
        Assert.True(known > 200, $"only {known} forms known");
        foreach (int persian in new[] { 0xFB58, 0xFB7C, 0xFB8B, 0xFB90, 0xFB94, 0xFBFE })
            Assert.True(OpenTypeShaper.TryGetBasePresentationForm(persian, out _), $"U+{persian:X4} is not known");
    }

    private static int[] Shape(string text) => (OpenTypeShaper.Shape(text) ?? text).Select(c => (int)c).ToArray();

    private static int[] Codes(params int[] codes) => codes;
}
