// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// A CFF INDEX's offsets must ascend (CFF §5: item <i>i</i> runs from offset <i>i</i> to offset
/// <i>i</i>+1). A descending pair made one item negative — skipped — and let the next start back at
/// the beginning of the data, so offsets of 1, N, 1, N, … declared half the items as copies of the
/// whole data, and the reader copied every one: 1 000 items of a 120 KB INDEX allocated 120 MB, and a
/// 65 535-item INDEX of a 1 MB font 32 GB. Such an INDEX is refused now; a font whose CharStrings
/// INDEX it is has no glyphs.
/// </summary>
public class CffIndexOffsetOrderTests
{
    private const int GlyphCount = 2001;

    [Fact]
    public void ACharStringsIndexWithDescendingOffsets_IsRefusedWithoutCopyingTheItems()
    {
        var glyphs = MinimalOpenTypeCff.BoxGlyphs(GlyphCount);   // .notdef and GlyphCount boxes
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(glyphs);

        // The control: as built, every glyph is read.
        Assert.Equal(glyphs.Count, CffParser.Parse(cff).Charstrings.Count);

        int index = MinimalOpenTypeCff.CharStringsIndexOffset(cff);
        int count = (cff[index] << 8) | cff[index + 1];
        Assert.Equal(glyphs.Count, count);
        int offSize = cff[index + 2];
        int offsets = index + 3;
        int last = ReadOffset(cff, offsets + count * offSize, offSize);
        Assert.True(last > 100_000, $"the data is {last - 1} bytes");

        // Sawtooth: even items start at 1 and run to the end of the data; odd items run backwards.
        for (int i = 0; i < count; i++)
            WriteOffset(cff, offsets + i * offSize, offSize, (i & 1) == 0 ? 1 : last);

        long before = GC.GetAllocatedBytesForCurrentThread();
        CffFont font = CffParser.Parse(cff);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The INDEX is refused, so the font has no glyphs — where it had GlyphCount / 2 copies of the data.
        Assert.NotNull(font);
        Assert.Empty(font.Charstrings);
        Assert.True(allocated < 4L * cff.Length, $"allocated {allocated} bytes for a {cff.Length}-byte font");
    }

    /// <summary>The locator itself: a descending pair is a refusal, with its ascending control.</summary>
    [Theory]
    [InlineData(1, 3, 5, true)]
    [InlineData(1, 5, 3, false)]
    public void TryLocateIndex_RefusesADescendingOffsetPair(int first, int second, int third, bool located)
    {
        byte[] index = { 0, 2, 1, (byte)first, (byte)second, (byte)third, 0x0E, 0x0E, 0x0E, 0x0E };

        Assert.Equal(located, CffParser.TryLocateIndex(index, 0, out int[] starts, out _, out _));
        if (located) Assert.Equal(2, starts.Length);
    }

    private static int ReadOffset(byte[] d, int at, int offSize)
    {
        int v = 0;
        for (int b = 0; b < offSize; b++) v = (v << 8) | d[at + b];
        return v;
    }

    private static void WriteOffset(byte[] d, int at, int offSize, int value)
    {
        for (int b = offSize - 1; b >= 0; b--) { d[at + b] = (byte)value; value >>= 8; }
    }
}
