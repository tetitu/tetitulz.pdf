// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A font split out of a CFF font set (<see cref="CffFontSet.TryRead(byte[], int, out string)"/>) read
/// by a reader this repository did not write — SkiaSharp's, over FreeType's CFF driver, the program
/// wrapped as the raster renderer wraps one (<see cref="CffOtfWrapper"/>): each font of the set loads
/// as itself, its two glyphs and its own advance for <c>A</c>.
/// </summary>
public class CffFontSetSplitSkiaTests
{
    private static readonly byte[] Set =
        MinimalOpenTypeCff.BuildSet(new[] { "First", "Second", "Third" }, new[] { 600, 700, 800 }, new[] { 400, 300, 200 });

    private static SKTypeface Load(byte[] cff)
    {
        byte[] otf = CffOtfWrapper.TryBuild(cff, CffParser.Parse(cff));
        Assert.NotNull(otf);
        using var data = SKData.CreateCopy(otf);
        return SKTypeface.FromData(data);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(1, 700)]
    [InlineData(2, 800)]
    public void EachSplitFont_LoadsInSkia_AsItself(int index, float advance)
    {
        CffFontSet font = CffFontSet.TryRead(Set, index, out string reason);
        Assert.True(font != null, reason);

        using var typeface = Load(font.Data);
        Assert.NotNull(typeface);
        Assert.Equal(2, typeface.GlyphCount);
        using var skFont = new SKFont(typeface, 1000);
        Assert.Equal(advance, skFont.GetGlyphWidths(new ushort[] { 1 })[0], 1);
    }

    /// <summary>
    /// The control: the set itself, wrapped the same way, is never its second or third font, which
    /// is what the split answers. FreeType and DirectWrite take no OpenType CFF table holding three
    /// fonts; CoreText takes it as its first font, so where it loads at all it draws the first
    /// font's advance — the others are reached only through the split.
    /// </summary>
    [Fact]
    public void TheWholeSet_IsAtMostItsFirstFontToSkia()
    {
        using var typeface = Load(Set);
        float? advance = null;
        if (typeface != null)
        {
            using var skFont = new SKFont(typeface, 1000);
            advance = skFont.GetGlyphWidths(new ushort[] { 1 })[0];
        }

        Assert.True(advance == null || System.Math.Abs(advance.Value - 600f) < 0.5f,
            $"the whole set loaded as a font whose 'A' advances {advance}, not the first font's 600: " +
            "the reader took a later font of the set, which only the split may hand it");
    }
}
