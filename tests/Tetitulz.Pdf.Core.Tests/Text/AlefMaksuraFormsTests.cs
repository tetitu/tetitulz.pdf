// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using J = Tetitulz.Pdf.TestHelpers.GsubJoiningFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Alef maksura (U+0649) is dual-joining in Unicode (<c>ArabicShaping.txt</c>: D), with the Uighur,
/// Kazakh and Kyrgyz initial and medial forms U+FBE8 and U+FBE9 in Presentation Forms-A. The form
/// table kept it right-joining — its isolated form standing for the initial, its final for the
/// medial — so a Uighur word with it in the middle lost the join on its left. Arabic, which ends a
/// word with the letter, is unchanged: the final and isolated forms are the ones it reaches.
/// </summary>
public class AlefMaksuraFormsTests
{
    private const char AlefMaksura = 'ى';
    private const char Beh = 'ب';

    [Fact]
    public void UnicodeHasItDualJoining()
        => Assert.Equal(ArabicJoiningTypes.DualJoining, ArabicJoiningTypes.Of(AlefMaksura));

    /// <summary>Between two behs it is medial (U+FBE9), and the beh after it is final: the join reaches across it.</summary>
    [Fact]
    public void BetweenTwoLetters_ItIsMedial_AndTheNextLetterJoinsToIt()
    {
        string shaped = OpenTypeShaper.Shape($"{Beh}{AlefMaksura}{Beh}");

        Assert.Equal(new[] { 0xFE91, 0xFBE9, 0xFE90 }, ToCodePoints(shaped));   // beh initial, alef maksura medial, beh final
    }

    /// <summary>At the start of a word before a letter it is initial (U+FBE8); the letter after it joins.</summary>
    [Fact]
    public void BeforeALetter_ItIsInitial()
    {
        string shaped = OpenTypeShaper.Shape($"{AlefMaksura}{Beh}");

        Assert.Equal(new[] { 0xFBE8, 0xFE90 }, ToCodePoints(shaped));
    }

    /// <summary>The Arabic cases are as they were: final after a letter (U+FEF0), isolated alone (U+FEEF).</summary>
    [Fact]
    public void AtTheEndOfAWord_ItIsFinal_AndAloneIsolated()
    {
        Assert.Equal(new[] { 0xFE91, 0xFEF0 }, ToCodePoints(OpenTypeShaper.Shape($"{Beh}{AlefMaksura}")));
        Assert.Equal(new[] { 0xFEEF }, ToCodePoints(OpenTypeShaper.Shape($"{AlefMaksura}")));
    }

    /// <summary>The two new forms map back to the letter for <c>/ToUnicode</c>, and name their joining features.</summary>
    [Theory]
    [InlineData(0xFBE8, "init")]
    [InlineData(0xFBE9, "medi")]
    [InlineData(0xFEEF, "isol")]
    [InlineData(0xFEF0, "fina")]
    public void EachForm_MapsBackToTheLetter_AndNamesItsFeature(int form, string expectedFeature)
    {
        Assert.True(OpenTypeShaper.TryGetBasePresentationForm(form, out int letter));
        Assert.Equal(AlefMaksura, letter);
        Assert.True(OpenTypeShaper.TryGetJoiningFeature(form, out int baseCodePoint, out string feature));
        Assert.Equal(AlefMaksura, baseCodePoint);
        Assert.Equal(expectedFeature, feature);
    }

    /// <summary>
    /// A font mapping the forms draws them: the composed run of beh, alef maksura, beh is the three
    /// forms' glyphs in visual order, each mapped back to its letter.
    /// </summary>
    [Fact]
    public void AFontWithTheForms_DrawsTheMedialOne()
    {
        const string covers = "ﺑﯩﺐ";
        var font = TrueTypeFontProgram.Parse(MinimalTrueType.Build(covers));
        ShapedGlyphRun run = TextRunComposer.Compose($"{Beh}{AlefMaksura}{Beh}", font, null);

        Assert.True(run.IsComplete, TextRunComposer.LastCoverageError);
        Assert.Equal(3, run.GlyphIds.Length);
        Assert.Equal(new[] { (int)Beh, (int)AlefMaksura, (int)Beh }, new[] { run.Glyphs[2].CodePoint, run.Glyphs[1].CodePoint, run.Glyphs[0].CodePoint });
        font.TryGetGlyph(0xFBE9, out ushort medial);
        Assert.Equal(medial, run.GlyphIds[1]);
    }

    /// <summary>
    /// A font that maps the letter and reaches its forms through <c>GSUB</c> draws the initial one
    /// through its <c>init</c> feature, as it does for any letter: alef maksura mapped to the joining
    /// font's beh glyph takes the initial beh's shape before a final beh.
    /// </summary>
    [Fact]
    public void AFontReachingTheFormsThroughGsub_DrawsTheInitialOne()
    {
        var cmap = new Dictionary<int, int> { [AlefMaksura] = J.BehGlyph, [Beh] = J.BehGlyph };
        var font = TrueTypeFontProgram.Parse(J.Build(cmap: cmap));
        ShapedGlyphRun run = TextRunComposer.Compose($"{AlefMaksura}{Beh}", font, null);

        Assert.True(run.IsComplete, TextRunComposer.LastCoverageError);
        Assert.Equal(new[] { J.FinalBehGlyph, J.InitialBehGlyph }, run.GlyphIds);
    }

    private static int[] ToCodePoints(string s)
    {
        var cps = new int[s.Length];
        for (int i = 0; i < s.Length; i++) cps[i] = s[i];
        return cps;
    }
}
