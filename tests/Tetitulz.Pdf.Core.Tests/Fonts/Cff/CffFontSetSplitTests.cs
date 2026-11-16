// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// <see cref="CffFontSet.TryRead(byte[], int, out string)"/>: a font of a CFF font set holding several
/// as a CFF holding it alone — an embedded CFF in PDF holds exactly one font (ISO 32000-2 §9.9) — and
/// <see cref="CffFontSet.TryReadFontNames"/>, the set's fonts by their place in its Name INDEX.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class CffFontSetSplitTests
{
    private static readonly byte[] ThreeFonts =
        MinimalOpenTypeCff.BuildSet(new[] { "First", "Second", "Third" }, new[] { 600, 700, 800 }, new[] { 400, 300, 200 });

    /// <summary>The largest x an outline reaches, rounded: a square's side here, since each starts at the origin.</summary>
    private static double Right(GlyphOutline outline)
        => Math.Round(outline.Segments.Where(s => s.Kind != GlyphOutlineSegmentKind.ClosePath).Max(s => s.X3), 3);

    /// <summary>The names, in the Name INDEX's order.</summary>
    [Fact]
    public void TheFontNames_AreTheNameIndexs()
    {
        Assert.Equal(new[] { "First", "Second", "Third" }, CffFontSet.TryReadFontNames(ThreeFonts, out string reason));
        Assert.Null(reason);
    }

    /// <summary>
    /// Each font is its own: its name alone in its Name INDEX, its advance and outline its CharStrings'
    /// — 600, 700, 800 wide; sides 400, 300, 200 — the String INDEX the set's (the glyph named
    /// <c>A</c>), the built-in encoding the set's (code 65).
    /// </summary>
    [Theory]
    [InlineData(0, "First", 600, 400)]
    [InlineData(1, "Second", 700, 300)]
    [InlineData(2, "Third", 800, 200)]
    public void EachFont_IsACffOfItsOwn(int index, string name, double advance, double side)
    {
        CffFontSet font = CffFontSet.TryRead(ThreeFonts, index, out string reason);
        Assert.True(font != null, reason);

        Assert.Equal(new[] { name }, font.FontNames);
        Assert.Equal(new[] { name }, CffFontSet.TryReadFontNames(font.Data, out _));
        Assert.True(font.TryGetAdvance("A", out double a));
        Assert.Equal(advance, a, 2);
        Assert.Equal(side, Right(font.Outline("A")));
        Assert.Equal("A", font.BuiltInGlyphName(65));
    }

    /// <summary>
    /// The rest of the set keeps its place: every byte from the end of the Global Subr INDEX on is the
    /// set's, at the same offset, and the one-font INDEXes are no longer than the set's.
    /// </summary>
    [Fact]
    public void TheDataAfterTheSharedIndexes_KeepsItsOffsets()
    {
        byte[] second = CffFontSet.TryRead(ThreeFonts, 1, out _).Data;
        int tail = IndexEnd(ThreeFonts, IndexEnd(ThreeFonts, IndexEnd(ThreeFonts, IndexEnd(ThreeFonts, 4))));
        Assert.Equal(ThreeFonts.Length, second.Length);
        Assert.Equal(ThreeFonts.Skip(tail), second.Skip(tail));
        Assert.NotEqual(ThreeFonts.Take(tail), second.Take(tail));
        Assert.True(IndexEnd(second, IndexEnd(second, IndexEnd(second, IndexEnd(second, 4)))) <= tail);
    }

    /// <summary>Where the INDEX at <paramref name="at"/> ends (TN #5176 §5), read here by hand.</summary>
    private static int IndexEnd(byte[] d, int at)
    {
        int count = (d[at] << 8) | d[at + 1];
        if (count == 0) return at + 2;
        int offSize = d[at + 2];
        int last = 0;
        for (int b = 0; b < offSize; b++) last = (last << 8) | d[at + 3 + count * offSize + b];
        return at + 3 + (count + 1) * offSize + last - 1;
    }

    /// <summary>A set of one font is answered as it is.</summary>
    [Fact]
    public void ASetOfOne_IsItself()
    {
        byte[] one = MinimalOpenTypeCff.BuildSet(new[] { "Only" }, new[] { 600 }, new[] { 400 });
        Assert.Same(one, CffFontSet.TryRead(one, 0, out _).Data);
    }

    /// <summary>
    /// A deleted entry (its first byte 0) has no name, and is refused; the fonts around it are read.
    /// </summary>
    [Fact]
    public void ADeletedEntry_IsNoFont()
    {
        byte[] set = MinimalOpenTypeCff.BuildSet(new[] { "First", "Second" }, new[] { 600, 700 }, new[] { 400, 300 }, deletedName: 0);
        Assert.Equal(new[] { null, "Second" }, CffFontSet.TryReadFontNames(set, out _));
        Assert.Null(CffFontSet.TryRead(set, 0, out string reason));
        Assert.Contains("deleted", reason);
        Assert.NotNull(CffFontSet.TryRead(set, 1, out _));
    }

    /// <summary>A synthetic font draws through another font of the set and is refused by name; its neighbour is read.</summary>
    [Fact]
    public void ASyntheticFont_IsRefusedByName()
    {
        byte[] set = MinimalOpenTypeCff.BuildSet(new[] { "Base", "Oblique" }, new[] { 600, 600 }, new[] { 400, 400 }, syntheticFont: 1);
        Assert.Null(CffFontSet.TryRead(set, 1, out string reason));
        Assert.Contains("synthetic", reason);
        Assert.NotNull(CffFontSet.TryRead(set, 0, out _));
    }

    /// <summary>An index the set does not hold is refused, either side.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void AnIndexOutsideTheSet_IsRefused(int index)
    {
        Assert.Null(CffFontSet.TryRead(ThreeFonts, index, out string reason));
        Assert.Contains("none of them", reason);
    }

    /// <summary>
    /// A Top DICT INDEX holding fewer fonts than the Name INDEX names is refused: its count lowered from
    /// two to one. The same set unpatched is read.
    /// </summary>
    [Fact]
    public void FewerTopDictsThanNames_AreRefused_HostileInput()
    {
        byte[] set = MinimalOpenTypeCff.BuildSet(new[] { "First", "Second" }, new[] { 600, 700 }, new[] { 400, 300 });
        Assert.NotNull(CffFontSet.TryRead(set, 0, out _));

        byte[] patched = (byte[])set.Clone();
        int topDicts = IndexEnd(set, 4);
        Assert.Equal(2, patched[topDicts + 1]);
        patched[topDicts + 1] = 1;
        Assert.Null(CffFontSet.TryRead(patched, 0, out string reason));
        Assert.Contains("fewer fonts than its Name INDEX", reason);
    }
}
