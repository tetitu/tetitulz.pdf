// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.MarkAttachmentFont;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// A producer draws a Hebrew letter and its point as the presentation form where the font has the
/// form's glyph — bet with dagesh U+FB31, shin with its dot U+FB2A — as the Hebrew fonts on a Windows
/// host design them, and <c>/ToUnicode</c> writes the form back as the letter and point typed.
/// </summary>
public class HebrewPresentationFormsTests
{
    private const char Bet = 'ב', Shin = 'ש', Vav = 'ו';
    private const char Dagesh = 'ּ', ShinDot = 'ׁ', Qamats = 'ָ', Holam = 'ֹ';

    [Theory]
    [InlineData("בּ", "בּ")]                 // bet + dagesh
    [InlineData("שׁ", "שׁ")]                 // shin + shin dot
    [InlineData("וֹ", "וֹ")]                 // vav + holam
    [InlineData("שּׁ", "שּׁ")]           // shin + dagesh + shin dot
    [InlineData("שּׁ", "שּׁ")]           // the marks the other way round
    [InlineData("בָּ", "בָּ")]     // a point that composes with nothing stays, after the form
    [InlineData("בָ", "בָ")]           // nothing composes
    [InlineData("abc", "abc")]
    public void APointComposesWithItsLetter_WhereTheFontHasTheForm(string typed, string drawn)
    {
        Assert.Equal(drawn, HebrewPresentationForms.Compose(typed, _ => true));
    }

    /// <summary>A form the font has no glyph for is not drawn: the letter and point stay as they were typed.</summary>
    [Fact]
    public void AFormTheFontLacks_IsNotComposed()
    {
        Assert.Equal($"{Bet}{Dagesh}", HebrewPresentationForms.Compose($"{Bet}{Dagesh}", cp => cp != 0xFB31));
        Assert.Equal($"שּ{ShinDot}", HebrewPresentationForms.Compose($"{Shin}{Dagesh}{ShinDot}", cp => cp != 0xFB2C));
    }

    [Fact]
    public void AFormsDecomposition_IsTheLetterAndPoint()
    {
        Assert.Equal($"{Bet}{Dagesh}", HebrewPresentationForms.Decomposition(0xFB31));
        Assert.Equal($"{Shin}{Dagesh}{ShinDot}", HebrewPresentationForms.Decomposition(0xFB2C));
        Assert.Null(HebrewPresentationForms.Decomposition(Bet));
        Assert.Null(HebrewPresentationForms.Decomposition(0x00E9));   // é decomposes too, and is not a Hebrew form
    }

    /// <summary>
    /// The composer draws the form's glyph when the font has one — the fixture draws U+FB31 with its
    /// plain glyph — and records the form as the glyph's code point, which <c>/ToUnicode</c> writes as
    /// the letter and point. Without the form in the font, bet and dagesh are two glyphs as before.
    /// </summary>
    [Fact]
    public void TheComposer_DrawsTheFormsGlyph_AndToUnicodeWritesTheLetterAndPoint()
    {
        var withForm = TrueTypeFontProgram.Parse(F.Build(alsoCovers: "בּ"));
        ShapedGlyphRun run = TextRunComposer.Compose($"{Bet}{Dagesh}", withForm, null);
        Assert.Equal(new[] { F.LatinGlyph }, run.GlyphIds);
        Assert.Equal(0xFB31, run.Glyphs[0].CodePoint);

        string cmap = Encoding.ASCII.GetString(ToUnicodeCMapWriter.Build(new[] { new KeyValuePair<ushort, int>(F.LatinGlyph, 0xFB31) }));
        Assert.Contains("<0004> <05D105BC>", cmap);

        ShapedGlyphRun without = TextRunComposer.Compose($"{Bet}{Dagesh}", TrueTypeFontProgram.Parse(F.Build()), null);
        Assert.Equal(new[] { F.DageshGlyph, F.BetGlyph }, without.GlyphIds);
    }

    /// <summary>An extractor building a visual line reverses a right-to-left glyph's several characters, and nothing else.</summary>
    [Fact]
    public void AGlyphsTextInVisualOrder_IsReversedOnlyWhenRightToLeftAndSeveral()
    {
        Assert.Equal($"{Dagesh}{Bet}", UnicodeBidi.InVisualOrder($"{Bet}{Dagesh}"));
        Assert.Equal($"{Bet}", UnicodeBidi.InVisualOrder($"{Bet}"));
        Assert.Equal("fi", UnicodeBidi.InVisualOrder("fi"));
    }
}
