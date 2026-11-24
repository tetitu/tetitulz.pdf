// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Tests that a CFF font's Top-DICT /FontMatrix scale is honoured when computing glyph
/// advances (and, via DrawGlyphPath, glyph outline size); a hardcoded 1/1000 glyph-space
/// scale would mis-size any font whose em ≠ 1000 units.
/// </summary>
public class FontMatrixScaleTests
{
    private static CffFont MakeFont(float scale)
        => new CffFont(
            charstrings: new List<byte[]> { new byte[] { 14 } }, // GID 0: endchar (no width operand)
            localSubrs: null, globalSubrs: null,
            charCodeToGid: new Dictionary<int, int> { { 65, 0 } },
            defaultWidthX: 600f, nominalWidthX: 0f,
            fontMatrixScale: scale);

    [Fact]
    public void DefaultFontMatrixScale_IsThousandthEm()
    {
        var f = new CffFont(new List<byte[]> { new byte[] { 14 } }, null, null,
            new Dictionary<int, int>(), 0f, 0f);
        Assert.Equal(0.001f, f.FontMatrixScale);
    }

    [Theory]
    [InlineData(-5f)]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidFontMatrixScale_FallsBackToDefault(float bad)
    {
        Assert.Equal(0.001f, MakeFont(bad).FontMatrixScale);
    }

    [Fact]
    public void GlyphSource_DefaultScale_AdvanceUnchanged()
    {
        using var src = new CffGlyphSource(MakeFont(0.001f));
        // 600 glyph-space units at a 1000-unit em == 600/1000 em — returned verbatim.
        Assert.Equal(600f, src.GetAdvanceWidth(65), 2);
    }

    [Fact]
    public void GlyphSource_HalfScale_HalvesAdvance()
    {
        // FontMatrix 0.0005 means a 2000-unit em: the same raw width is half an em.
        using var full = new CffGlyphSource(MakeFont(0.001f));
        using var half = new CffGlyphSource(MakeFont(0.0005f));
        float a = full.GetAdvanceWidth(65);
        float b = half.GetAdvanceWidth(65);
        Assert.True(a > 0f, $"expected non-zero advance, got {a}");
        Assert.Equal(a * 0.5f, b, 3);
    }
}
