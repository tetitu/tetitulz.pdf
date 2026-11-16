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
/// <b>It did not, and the result was silent.</b> <c>FillCmap</c> looked up the plain code point,
/// found nothing, and every character fell back to <c>.notdef</c>'s advance — a valid PDF in which
/// EVERY glyph of a symbol font is laid out at one wrong width. Measured on the shipping Windows
/// fonts before the fix: <c>symbol.ttf</c> returned a flat 600 and <c>wingding.ttf</c> a flat 500
/// for every code point, where their own <c>hmtx</c> gives 722 / 631 / 549 / 686 and
/// 587 / 1090 / 747 / 578.
///
/// <para><b>The read side already had the rule</b>, comment and all, in
/// <c>Core.Fonts.PdfTrueTypeFont</c> — so the two halves of this library disagreed about which
/// glyph a byte selects in a symbol font. The fix is applied at the embedder's call site rather
/// than inside <c>TrueTypeFontProgram.TryGetGlyph</c>, because <c>SystemFontResolver.Covers</c>
/// reads that method to ask whether a font genuinely covers a code point, and folding there would
/// make every symbol font claim coverage of all of Latin-1.</para>
///
/// <para><b>Nothing could express this fixture before.</b> <c>MinimalTrueType</c> could build a
/// <c>(3, 0)</c> font, but mapped the literal characters — where the plain lookup succeeds and the
/// defect does not reproduce. <c>BuildSymbolic</c> is the missing shape, and its absence is why
/// this stayed green.</para>
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
