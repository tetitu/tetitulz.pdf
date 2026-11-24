// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.KerningFont;
using J = Tetitulz.Pdf.TestHelpers.GsubJoiningFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Which OpenType script a run's features are looked up under: Unicode's Script property for the
/// run's first scripted character (<see cref="UnicodeScripts"/>, generated from the Unicode
/// Character Database), with a shaper's fallbacks. A composer answering Hebrew, Arabic or Latin
/// only would give a Cyrillic, Greek, Thai or Devanagari run Latin's lookups — or, in a font that
/// registers its features per script, none.
/// </summary>
public class ScriptTagTests
{
    /// <summary>
    /// The table: each script's ISO 15924 code in lower case; the tags that predate the codes
    /// (Hiragana and Katakana share <c>kana</c>; Lao, Yi, N'Ko and Vai are space-padded); the Indic
    /// second-generation tags; and no tag for a Common or Inherited character.
    /// </summary>
    [Theory]
    [InlineData('A', "latn")]
    [InlineData(0x0416, "cyrl")]      // Ж
    [InlineData(0x03A9, "grek")]      // Ω
    [InlineData(0x05D0, "hebr")]
    [InlineData(0x0628, "arab")]
    [InlineData(0x0915, "dev2")]      // क
    [InlineData(0x0995, "bng2")]
    [InlineData(0x0E01, "thai")]
    [InlineData(0x0E81, "lao ")]
    [InlineData(0xA000, "yi  ")]
    [InlineData(0x07C0, "nko ")]
    [InlineData(0xA500, "vai ")]
    [InlineData(0x3042, "kana")]      // あ
    [InlineData(0x30A2, "kana")]      // ア
    [InlineData(0xAC00, "hang")]
    [InlineData(0x4E00, "hani")]
    [InlineData(0x1000, "mym2")]
    public void TheTagOfACodePoint_IsItsScripts(int codePoint, string expected)
        => Assert.Equal(expected, UnicodeScripts.TagOf(codePoint));

    [Theory]
    [InlineData('0')]           // Common
    [InlineData(' ')]           // Common
    [InlineData(0x0301)]        // Inherited: the combining acute
    [InlineData(0x0378)]        // unassigned
    [InlineData(0x1F600)]       // an emoji: Common
    public void ACharacterOfNoScript_HasNoTag(int codePoint)
        => Assert.Null(UnicodeScripts.TagOf(codePoint));

    /// <summary>A range boundary from the data: Cyrillic ends at U+0484, the two combining marks after it are Inherited, and U+0487 is Cyrillic again.</summary>
    [Fact]
    public void TheTable_FollowsTheDataAtARangeBoundary()
    {
        Assert.Equal("cyrl", UnicodeScripts.TagOf(0x0484));
        Assert.Null(UnicodeScripts.TagOf(0x0485));
        Assert.Null(UnicodeScripts.TagOf(0x0486));
        Assert.Equal("cyrl", UnicodeScripts.TagOf(0x0487));
        Assert.Equal("18.0.0", UnicodeScripts.UnicodeVersion);
    }

    // A run no longer has one tag: the composer cuts it into segments of one script and looks each up
    // under its own (MixedScriptRunTests). The per-code-point answer above is what the cut is made from.

    /// <summary>The fallback chain: the tag, an Indic script's first-generation tag, DFLT, dflt, latn — each once.</summary>
    [Fact]
    public void TheFallbacks_AreAShapers()
    {
        Assert.Equal(new[] { "cyrl", "DFLT", "dflt", "latn" }, UnicodeScripts.Fallbacks("cyrl"));
        Assert.Equal(new[] { "dev2", "deva", "DFLT", "dflt", "latn" }, UnicodeScripts.Fallbacks("dev2"));
        Assert.Equal(new[] { "latn", "DFLT", "dflt" }, UnicodeScripts.Fallbacks("latn"));
        Assert.Equal(new[] { "DFLT", "dflt", "latn" }, UnicodeScripts.Fallbacks(null));
    }

    /// <summary>
    /// <b>The claim.</b> A font whose kerning is registered under <c>cyrl</c> alone kerns a Cyrillic run
    /// (А then В, the Latin A's and V's glyphs: −80) and not a Latin one — the Latin run finds no
    /// <c>latn</c>, <c>DFLT</c> or <c>dflt</c>. Before, every non-Hebrew, non-Arabic run was looked up
    /// as Latin, so the Cyrillic run was unkerned too.
    /// </summary>
    [Fact]
    public void AFontRegisteringAScriptAlone_KernsThatScriptsRunAndNoOther()
    {
        var font = TrueTypeFontProgram.Parse(F.Build(latinScriptTag: "cyrl"));
        var kerned = new TextRunOptions { Kerning = true };

        ShapedGlyphRun cyrillic = TextRunComposer.Compose($"{F.CyrillicA}{F.CyrillicVe}", font, kerned);
        Assert.Equal(new[] { F.AGlyph, F.VGlyph }, cyrillic.GlyphIds);
        Assert.Equal(1120d, cyrillic.AdvanceGlyphSpaceUnits);

        ShapedGlyphRun latin = TextRunComposer.Compose("AV", font, kerned);
        Assert.Equal(new[] { F.AGlyph, F.VGlyph }, latin.GlyphIds);
        Assert.Equal(1200d, latin.AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// The joining features take the chain too: a font that registers <c>init</c> and <c>fina</c>
    /// under its default script alone joins beh and yeh as one registering them under <c>arab</c>
    /// does — the initial beh and the final yeh, not the two isolated letters a font with no
    /// <c>GSUB</c> draws.
    /// </summary>
    [Fact]
    public void AFontRegisteringItsJoiningFeaturesUnderTheDefaultScript_JoinsThroughTheFallback()
    {
        ushort[] underDefault = TextRunComposer.Compose("بي", TrueTypeFontProgram.Parse(J.Build(scriptTag: "DFLT")), null).GlyphIds;
        ushort[] underArabic = TextRunComposer.Compose("بي", TrueTypeFontProgram.Parse(J.Build()), null).GlyphIds;
        ushort[] unjoined = TextRunComposer.Compose("بي", TrueTypeFontProgram.Parse(J.Build(gsub: false)), null).GlyphIds;

        Assert.Equal(new[] { J.FinalYehGlyph, J.InitialBehGlyph }, underArabic);
        Assert.Equal(underArabic, underDefault);
        Assert.NotEqual(underArabic, unjoined);
    }

    /// <summary>
    /// The control, and the fallback: the ordinary font registers Latin only, and a Cyrillic run
    /// takes Latin's lookups through the chain's last step, as a shaper has it — so a face that
    /// covers Cyrillic without listing the script still kerns it.
    /// </summary>
    [Fact]
    public void AFontRegisteringLatinAlone_KernsACyrillicRunThroughTheFallback()
    {
        var font = TrueTypeFontProgram.Parse(F.Build());
        var kerned = new TextRunOptions { Kerning = true };

        Assert.Equal(1120d, TextRunComposer.Compose($"{F.CyrillicA}{F.CyrillicVe}", font, kerned).AdvanceGlyphSpaceUnits);
        Assert.Equal(1120d, TextRunComposer.Compose("AV", font, kerned).AdvanceGlyphSpaceUnits);
    }
}
