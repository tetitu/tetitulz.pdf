// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// A symbolic TrueType font's <c>(3, 0)</c> cmap is conventionally populated at
/// <c>0xF000 + code</c>, and the embedder must find its glyphs there.
/// </summary>
/// <remarks>
/// <b>Silent otherwise.</b> Looking up the plain code point would find nothing, and every
/// character fall back to <c>.notdef</c>'s advance — a valid PDF in which EVERY glyph of a symbol
/// font is laid out at one wrong width: on the shipping Windows fonts, a flat 600 for
/// <c>symbol.ttf</c> and a flat 500 for <c>wingding.ttf</c>, where their own <c>hmtx</c> gives
/// 722 / 631 / 549 / 686 and 587 / 1090 / 747 / 578.
///
/// <para><b>The read side has the same rule</b>, in <c>Core.Fonts.PdfTrueTypeFont</c>, so the
/// two halves of this library agree about which glyph a byte selects in a symbol font. The fold
/// is at the embedder's call site rather than inside <c>TrueTypeFontProgram.TryGetGlyph</c>,
/// because <c>SystemFontResolver.Covers</c> reads that method to ask whether a font genuinely
/// covers a code point, and folding there would make every symbol font claim coverage of all of
/// Latin-1.</para>
///
/// <para><b>The fixture's shape matters.</b> A <c>(3, 0)</c> font mapping the literal characters
/// lets the plain lookup succeed, so the defect does not reproduce; <c>BuildSymbolic</c> keys the
/// glyphs at <c>0xF000 + code</c>.</para>
/// </remarks>
public class SymbolicFontWidthTests
{
    private const string Characters = "HeloZ";

    /// <summary>
    /// The advances <c>MinimalTrueType.BuildWithDistinctWidths</c> assigns, in sorted character
    /// order — distinct on purpose, so a wrong width is a wrong NUMBER rather than a wrong shape.
    /// </summary>
    public static IEnumerable<object[]> ExpectedWidths => new[]
    {
        new object[] { 'H', 610 },
        new object[] { 'Z', 620 },
        new object[] { 'e', 630 },
        new object[] { 'l', 640 },
        new object[] { 'o', 650 },
    };

    [Theory]
    [MemberData(nameof(ExpectedWidths))]
    public void ASymbolicFontsWidthsComeFromItsOwnHmtxNotFromNotdef(char c, int expected)
    {
        var metrics = PdfFontEmbedder.GetFontMetrics(MinimalTrueType.BuildSymbolic(Characters));

        Assert.NotNull(metrics);
        Assert.Equal(expected, metrics!.GetWidth(c));
    }

    /// <summary>
    /// The control: the same characters in an ordinary <c>(3, 1)</c> font already worked, so the
    /// theory above is about the PUA fold and not about the width table.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExpectedWidths))]
    public void ANonSymbolicFontIsUnaffected(char c, int expected)
    {
        var metrics = PdfFontEmbedder.GetFontMetrics(MinimalTrueType.BuildWithDistinctWidths(Characters));

        Assert.NotNull(metrics);
        Assert.Equal(expected, metrics!.GetWidth(c));
    }

    /// <summary>
    /// A character the symbolic font does not map must still fall back — the fold must not invent
    /// a glyph for everything. Without this, a lookup that returned some fixed glyph would satisfy
    /// every case above.
    /// </summary>
    [Fact]
    public void ACharacterTheSymbolicFontDoesNotMapDoesNotBorrowAnotherGlyphsWidth()
    {
        var metrics = PdfFontEmbedder.GetFontMetrics(MinimalTrueType.BuildSymbolic(Characters));

        Assert.NotNull(metrics);
        int unmapped = metrics!.GetWidth('Q');   // not in "HeloZ"

        Assert.DoesNotContain(unmapped, new[] { 610, 620, 630, 640, 650 });
    }
}
