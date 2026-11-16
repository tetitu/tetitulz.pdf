// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>Tests for Hebrew encoding support.</summary>
public class HebrewEncodingTests
{
    [Fact]
    public void Cp1255_AlefAt0xE0()
    {
        var enc = StandardEncodings.Cp1255Encoding;
        Assert.True(enc.TryGetValue(0xE0, out char c));
        Assert.Equal('\u05D0', c); // HEBREW LETTER ALEF
    }

    [Fact]
    public void Cp1255_TavAt0xFA()
    {
        var enc = StandardEncodings.Cp1255Encoding;
        Assert.True(enc.TryGetValue(0xFA, out char c));
        Assert.Equal('\u05EA', c); // HEBREW LETTER TAV
    }

    [Fact]
    public void Cp1255_ShekelSign()
    {
        var enc = StandardEncodings.Cp1255Encoding;
        Assert.True(enc.TryGetValue(0xA4, out char c));
        Assert.Equal('\u20AA', c); // NEW SHEKEL SIGN
    }

    [Fact]
    public void Iso8859_8_AlefAt0xE0()
    {
        var enc = StandardEncodings.Iso8859_8Encoding;
        Assert.True(enc.TryGetValue(0xE0, out char c));
        Assert.Equal('\u05D0', c);
    }

    [Fact]
    public void GlyphMap_AlefMapsToU05D0()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("alef", out char c));
        Assert.Equal('\u05D0', c);
    }

    [Fact]
    public void GlyphMap_FinalKafMapsToU05DA()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("finalkaf", out char c));
        Assert.Equal('\u05DA', c);
    }

    [Fact]
    public void GlyphMap_ShinMapsToU05E9()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("shin", out char c));
        Assert.Equal('\u05E9', c);
    }

    [Fact]
    public void GlyphMap_DageshMapsToU05BC()
    {
        Assert.True(StandardEncodings.TryGetGlyphUnicode("dagesh", out char c));
        Assert.Equal('\u05BC', c);
    }

    [Fact]
    public void GlyphMap_AllHebrewLettersPresent()
    {
        var letters = new[]
        {
            ("alef", '\u05D0'), ("bet", '\u05D1'), ("gimel", '\u05D2'),
            ("dalet", '\u05D3'), ("he", '\u05D4'), ("vav", '\u05D5'),
            ("zayin", '\u05D6'), ("het", '\u05D7'), ("tet", '\u05D8'),
            ("yod", '\u05D9'), ("finalkaf", '\u05DA'), ("kaf", '\u05DB'),
            ("lamed", '\u05DC'), ("finalmem", '\u05DD'), ("mem", '\u05DE'),
            ("finalnun", '\u05DF'), ("nun", '\u05E0'), ("samekh", '\u05E1'),
            ("ayin", '\u05E2'), ("finalpe", '\u05E3'), ("pe", '\u05E4'),
            ("finaltsadi", '\u05E5'), ("tsadi", '\u05E6'), ("qof", '\u05E7'),
            ("resh", '\u05E8'), ("shin", '\u05E9'), ("tav", '\u05EA'),
        };
        foreach (var (name, expected) in letters)
        {
            Assert.True(StandardEncodings.TryGetGlyphUnicode(name, out char c),
                $"Missing glyph name '{name}'");
            Assert.Equal(expected, c);
        }
    }
}
