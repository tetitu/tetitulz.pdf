// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.GsubJoiningFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A font that maps the Arabic letters but not their presentation forms, reaching each joining form
/// through its own <c>GSUB</c> (<c>init</c>, <c>medi</c>, <c>fina</c>, <c>isol</c>). The composer
/// shapes to presentation forms; it draws such a font's letter and applies the feature the form
/// stands for, rather than finding no glyph and dropping the text. See <see cref="F"/>.
/// </summary>
public class GsubJoiningFormsTests
{
    /// <summary>
    /// Beh then yeh: the initial beh and the final yeh, through <c>init</c> and <c>fina</c>, drawn
    /// right to left and mapped back to the letters typed. The width is the forms' (400 and 450).
    /// </summary>
    [Fact]
    public void JoiningForms_AreReachedThroughTheFontsOwnFeatures()
    {
        ShapedGlyphRun run = Compose(F.Build(), "بي");

        Assert.True(run.IsComplete);
        Assert.Equal(new[] { F.FinalYehGlyph, F.InitialBehGlyph }, run.GlyphIds);
        Assert.Equal(new[] { 0x064A, 0x0628 }, run.Glyphs.Select(g => g.CodePoint));
        Assert.Equal(850d, run.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// A feature applies only where its form is wanted: two behs are the initial and the final one.
    /// <c>init</c> run over every glyph would make both initial; <c>fina</c>, both final.
    /// </summary>
    [Fact]
    public void EachFeature_StartsOnlyAtItsOwnPosition()
    {
        ShapedGlyphRun run = Compose(F.Build(), "بب");

        Assert.Equal(new[] { F.FinalBehGlyph, F.InitialBehGlyph }, run.GlyphIds);
    }

    /// <summary>
    /// The isolated form is the letter's own glyph: a lone beh draws as the letter, with no <c>isol</c>
    /// feature in the font, and with no <c>GSUB</c> at all.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnIsolatedLetter_IsTheLettersOwnGlyph(bool gsub)
    {
        ShapedGlyphRun run = Compose(F.Build(gsub), "ب");

        Assert.True(run.IsComplete);
        Assert.Equal(new[] { F.BehGlyph }, run.GlyphIds);
    }

    /// <summary>
    /// Without a <c>GSUB</c> the joined forms cannot be reached: both are missing, reported as such —
    /// the refusal the font with the features no longer meets.
    /// </summary>
    [Fact]
    public void WithoutTheFeatures_TheJoinedFormsAreMissing()
    {
        ShapedGlyphRun run = Compose(F.Build(gsub: false), "بي");

        Assert.False(run.IsComplete);
        Assert.Equal(2, run.MissingCodePointCount);
        Assert.Empty(run.GlyphIds);
    }

    /// <summary>
    /// <see cref="TrueTypeFontProgram.CoversShaped"/> answers the producer's question: a form the
    /// font reaches is covered, which <see cref="TrueTypeFontProgram.Covers"/> denies; without the
    /// features only the isolated form is; and a letter the font lacks is not covered by either.
    /// </summary>
    [Fact]
    public void CoversShaped_AcceptsAFormTheFontReaches()
    {
        TrueTypeFontProgram font = TrueTypeFontProgram.Parse(F.Build());
        TrueTypeFontProgram plain = TrueTypeFontProgram.Parse(F.Build(gsub: false));

        Assert.False(font.Covers(0xFE91));
        Assert.True(font.CoversShaped(0xFE91));          // initial beh, through init
        Assert.True(font.CoversShaped(0xFEF2));          // final yeh, through fina
        Assert.False(font.CoversShaped(0xFE92));         // medial beh: no medi
        Assert.False(plain.CoversShaped(0xFE91));
        Assert.True(plain.CoversShaped(0xFE8F));         // isolated beh: the letter itself
        Assert.False(font.CoversShaped(0xFE95));         // initial teh: no teh at all
    }

    /// <summary>
    /// The cheap cmap probe cannot see a <c>GSUB</c>, so a form over a letter the font maps is left
    /// to the full read (<c>null</c>) rather than rejected; a code point with neither is still a
    /// rejection, and the letters alone a yes.
    /// </summary>
    [Fact]
    public void TheCoverageProbe_DefersAFormItCannotJudge()
    {
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-gsub-joining-" + System.Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, F.Build());
        try
        {
            Assert.Null(FontCoverageProbe.Covers(path, new[] { 0x0628, 0xFE91 }));
            Assert.False(FontCoverageProbe.Covers(path, new[] { 0xFE91, 0x05D0 }));
            Assert.True(FontCoverageProbe.Covers(path, new[] { 0x0628, 0x064A }));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The resolver producers ask accepts the caller's font for the shaped run, where it named the
    /// forms as missing and failed; the same font without its <c>GSUB</c> still fails, naming them.
    /// </summary>
    [Fact]
    public void TheResolver_AcceptsTheCallersFontForTheShapedRun()
    {
        var request = new FontResolutionRequest
        {
            RequiredCodePoints = TextRunComposer.CodePointsToCover("بي"),
            AllowSystemFontSubstitution = false,
        };
        FontResolution resolved = SystemFontResolver.Resolve(request, _ => F.Build());
        Assert.NotNull(resolved.Program);

        FontResolution refused = SystemFontResolver.Resolve(request, _ => F.Build(gsub: false));
        Assert.Null(refused.Program);
        Assert.Contains("U+FE91", SystemFontResolver.LastResolutionError);
    }

    private static ShapedGlyphRun Compose(byte[] font, string text)
        => TextRunComposer.Compose(text, TrueTypeFontProgram.Parse(font), null);
}
