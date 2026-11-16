// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.GsubJoiningFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Arabic letters Unicode gives no presentation forms — U+0750 onwards, the Kashmiri and Farsi yeh
/// variants — and the characters a join passes through. Their joining types come from Unicode's
/// data (<see cref="ArabicJoiningTypes"/>): such a letter now joins its neighbours, and a producer
/// draws its own position through the font's <c>GSUB</c> joining features.
/// </summary>
public class UnformedArabicJoiningTests
{
    private const char Beh = 'ب', Yeh = 'ي';
    private const int BehIsolated = 0xFE8F, BehInitial = 0xFE91, BehMedial = 0xFE92;
    private const int YehIsolated = 0xFEF1, YehFinal = 0xFEF2;

    // ------------------------------------------------------------------
    //  The shaper: its neighbours join to such a letter
    // ------------------------------------------------------------------

    /// <summary>
    /// A dual-joining letter without forms (U+0750, beh with three dots below) joins on both sides,
    /// so the beh before it is initial and the yeh after it final. It joined on neither side, and
    /// both were drawn isolated.
    /// </summary>
    [Fact]
    public void ADualJoiningLetterWithoutForms_JoinsItsNeighbours()
    {
        Assert.Equal(new[] { BehInitial, 0x0750 }, CodePoints(OpenTypeShaper.Shape("بݐ")));
        Assert.Equal(new[] { 0x0750, YehFinal }, CodePoints(OpenTypeShaper.Shape("ݐي")));
    }

    /// <summary>
    /// A right-joining one (U+0759, a dal) joins the letter before it and not the one after: the
    /// beh before it is initial, the beh after it isolated.
    /// </summary>
    [Fact]
    public void ARightJoiningLetterWithoutForms_JoinsOnlyTheLetterBefore()
    {
        Assert.Equal(new[] { BehInitial, 0x0759 }, CodePoints(OpenTypeShaper.Shape("بݙ")));
        Assert.Equal(new[] { 0x0759, BehIsolated }, CodePoints(OpenTypeShaper.Shape("ݙب")));
    }

    /// <summary>
    /// The yeh variants inside the Arabic block that the form table lacks (Kashmiri yeh U+0620, the
    /// Farsi yeh variants U+063B–U+063F) and an Extended-A letter (U+08A0) join too; the last also
    /// counts as Arabic for script detection now.
    /// </summary>
    [Theory]
    [InlineData(0x0620)]
    [InlineData(0x0677)]   // U WITH HAMZA ABOVE: right-joining, its isolated form the only one encoded
    [InlineData(0x063B)]
    [InlineData(0x063F)]
    [InlineData(0x08A0)]
    public void OtherLettersWithoutForms_Join(int letter)
    {
        Assert.Equal(Script.Arabic, OpenTypeShaper.ClassifyScript(letter));
        Assert.Equal(new[] { BehInitial, letter }, CodePoints(OpenTypeShaper.Shape("ب" + (char)letter)));
    }

    /// <summary>The zero width joiner causes a join; the non-joiner, the control, does not.</summary>
    [Fact]
    public void ZeroWidthJoiner_CausesAJoin()
    {
        Assert.Equal(BehInitial, CodePoints(OpenTypeShaper.Shape("ب‍"))[0]);
        Assert.Equal(BehIsolated, CodePoints(OpenTypeShaper.Shape("ب‌"))[0]);
    }

    /// <summary>
    /// Marks the hand range missed are transparent: the honorific U+0610 and the Extended-A mark
    /// U+08F0 between beh and yeh leave them joined.
    /// </summary>
    [Theory]
    [InlineData('ؐ')]
    [InlineData('ࣰ')]
    [InlineData('َ')]   // fatha, which the range had: the control
    public void AMark_IsTransparentToTheJoin(char mark)
        => Assert.Equal(new[] { BehInitial, mark, YehFinal },
                        CodePoints(OpenTypeShaper.Shape("ب" + mark + "ي")));

    /// <summary>
    /// Characters the hand range counted as marks and are not — the end-of-ayah sign, the rub el
    /// hizb and sajdah ornaments, the small waw and small yeh — break the join, as Unicode says.
    /// </summary>
    [Theory]
    [InlineData('۝')]
    [InlineData('۞')]
    [InlineData('ۥ')]
    [InlineData('ۦ')]
    [InlineData('۩')]
    public void ANonMarkInTheOldRange_BreaksTheJoin(char sign)
        => Assert.Equal(new[] { BehIsolated, sign, YehIsolated },
                        CodePoints(OpenTypeShaper.Shape("ب" + sign + "ي")));

    // ------------------------------------------------------------------
    //  The letter's own position, read from its neighbours' forms
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ݐ", 0, "isol")]
    [InlineData("ݐﻲ", 0, "init")]            // a final yeh after it
    [InlineData("ﺑݐ", 1, "fina")]            // an initial beh before it
    [InlineData("ﺒݐﻲ", 1, "medi")]
    [InlineData("ݐﺒﻲ", 0, "init")]      // a medial beh after it
    [InlineData("ﺏݐ", 1, "isol")]            // an isolated beh does not reach it
    [InlineData("ݐﻱ", 0, "isol")]            // nor an isolated yeh
    [InlineData("ݐݐ", 0, "init")]            // two unformed letters join each other
    [InlineData("ݐݐ", 1, "fina")]
    [InlineData("ـݐ", 1, "fina")]            // the tatweel
    [InlineData("‍ݐ", 1, "fina")]            // the zero width joiner
    [InlineData("ﺑَݐ", 2, "fina")]      // through a mark
    [InlineData("ݐﻼ", 0, "init")]            // a final lam-alef joins the letter before it
    [InlineData("ݐﻻ", 0, "isol")]            // an isolated one does not
    [InlineData("ﻼݐ", 1, "isol")]            // and no lam-alef joins the letter after it
    [InlineData("ݙﻲ", 0, "isol")]            // a right-joining letter never joins forwards
    [InlineData("ﺑݙ", 1, "fina")]
    public void TheFeature_IsReadFromTheNeighboursForms(string shaped, int index, string expected)
    {
        Assert.True(OpenTypeShaper.TryGetUnformedJoiningFeature(shaped, index, out string feature));
        Assert.Equal(expected, feature);
    }

    /// <summary>Only a joining letter with no forms is answered: not a form, a letter with forms,
    /// a non-joining character or a mark.</summary>
    [Theory]
    [InlineData("ب")]
    [InlineData("ﺑ")]
    [InlineData("؀")]
    [InlineData("َ")]
    [InlineData("a")]
    public void OtherCharacters_HaveNoUnformedFeature(string shaped)
        => Assert.False(OpenTypeShaper.TryGetUnformedJoiningFeature(shaped, 0, out _));

    /// <summary>The shaper's own output agrees with the feature: for every pair it shapes, the
    /// unformed letter's position is the one its neighbour's form implies.</summary>
    [Theory]
    [InlineData("بݐي", 1, "medi")]
    [InlineData("بݐ", 1, "fina")]
    [InlineData("ݐي", 0, "init")]
    [InlineData("ءݐ", 1, "isol")]            // hamza joins nothing
    public void TheFeature_AgreesWithShape(string text, int index, string expected)
    {
        Assert.True(OpenTypeShaper.TryGetUnformedJoiningFeature(OpenTypeShaper.Shape(text), index, out string feature));
        Assert.Equal(expected, feature);
    }

    // ------------------------------------------------------------------
    //  The composer: the font's own features draw it joined
    // ------------------------------------------------------------------

    /// <summary>
    /// U+0750 mapped to the font's beh glyph takes <c>init</c> before a yeh, and the yeh its final
    /// form; the code point recorded is the letter typed. Before, U+0750 was the letter's own glyph
    /// and the yeh isolated.
    /// </summary>
    [Fact]
    public void AnUnformedLetter_IsDrawnInitialThroughTheFont()
    {
        ShapedGlyphRun run = Compose(new Dictionary<int, int> { [0x0750] = F.BehGlyph, [0x064A] = F.YehGlyph }, "ݐي");

        Assert.True(run.IsComplete);
        Assert.Equal(new[] { F.FinalYehGlyph, F.InitialBehGlyph }, run.GlyphIds);
        Assert.Equal(new[] { 0x064A, 0x0750 }, run.Glyphs.Select(g => g.CodePoint));
    }

    [Fact]
    public void AnUnformedLetter_IsDrawnFinalThroughTheFont()
    {
        ShapedGlyphRun run = Compose(new Dictionary<int, int> { [0x0628] = F.BehGlyph, [0x0750] = F.BehGlyph }, "بݐ");

        Assert.Equal(new[] { F.FinalBehGlyph, F.InitialBehGlyph }, run.GlyphIds);
        Assert.Equal(new[] { 0x0750, 0x0628 }, run.Glyphs.Select(g => g.CodePoint));
    }

    /// <summary>A right-joining letter without forms, mapped to the yeh glyph, takes <c>fina</c>.</summary>
    [Fact]
    public void ARightJoiningUnformedLetter_IsDrawnFinal()
    {
        ShapedGlyphRun run = Compose(new Dictionary<int, int> { [0x0628] = F.BehGlyph, [0x0759] = F.YehGlyph }, "بݙ");

        Assert.Equal(new[] { F.FinalYehGlyph, F.InitialBehGlyph }, run.GlyphIds);
    }

    /// <summary>
    /// Medial: the font has no <c>medi</c>, so the letter stays its own glyph, while <c>init</c> and
    /// <c>fina</c>, whose lookups would both change it, leave it alone — each feature starts only at
    /// its own position.
    /// </summary>
    [Fact]
    public void AMedialUnformedLetter_TakesNeitherNeighboursFeature()
    {
        ShapedGlyphRun run = Compose(new Dictionary<int, int> { [0x0628] = F.BehGlyph, [0x0750] = F.BehGlyph, [0x064A] = F.YehGlyph },
                                     "بݐي");

        Assert.Equal(new[] { F.FinalYehGlyph, F.BehGlyph, F.InitialBehGlyph }, run.GlyphIds);
    }

    /// <summary>Alone, the letter is its own glyph although the font's <c>init</c> would change it.</summary>
    [Fact]
    public void AnIsolatedUnformedLetter_IsItsOwnGlyph()
    {
        ShapedGlyphRun run = Compose(new Dictionary<int, int> { [0x0750] = F.BehGlyph }, "ݐ");

        Assert.Equal(new[] { F.BehGlyph }, run.GlyphIds);
    }

    private static ShapedGlyphRun Compose(IReadOnlyDictionary<int, int> cmap, string text)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(F.Build(cmap: cmap)), null);

    private static int[] CodePoints(string s) => s.Select(c => (int)c).ToArray();
}
