// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="TrueTypeFontProgram.GetAdvance"/> — the <c>hmtx</c>-derived per-glyph advance,
/// used by <c>TextRunComposer</c> for layout and by <c>Type0FontBuilder</c> to write a CID font's
/// own <c>/W</c> array and <c>/DW</c> default. Had no direct test before this: every existing test
/// touching <see cref="TrueTypeFontProgram.Parse"/> asserted <c>TryGetGlyph</c> or a metric other
/// than the advance.
/// </summary>
/// <remarks>
/// <b>That gap hid a real bug.</b> The <c>hhea</c> table reader located <c>numberOfHMetrics</c> 4
/// bytes past its real offset (34, not 38 — 13 <c>int16</c> fields after ascender/descender, not
/// 15), reading whatever followed the table instead. In a well-formed font that is
/// zero-padding, so <c>numberOfHMetrics</c> came back 0, <c>_glyphWidths</c> was never populated,
/// and every glyph fell back to <see cref="TrueTypeFontProgram.GetAdvance"/>'s flat default —
/// silently discarding the font's real metrics for every embedded TrueType program this library
/// ever wrote or measured. Found via <c>SimpleFontWidthProbeTests</c>, built for an unrelated
/// PDF/A check, when a fixture's known, distinct advances came back as one flat number.
/// </remarks>
public class TrueTypeFontProgramHmtxTests
{
    [Fact]
    public void GetAdvance_ReadsTheRealHmtxValue_NotTheFlatFallback()
    {
        // Chars sort 'A' < 'B': glyph 1 is 'A' (MappedGlyphAdvance), glyph 2 is 'B' (+10).
        byte[] font = MinimalTrueType.BuildWithDistinctWidths("AB");
        var program = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(program);
        Assert.True(program!.TryGetGlyph('A', out ushort gidA));
        Assert.True(program.TryGetGlyph('B', out ushort gidB));

        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, program.GetAdvance(gidA));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance + 10, program.GetAdvance(gidB));
    }

    [Fact]
    public void GetAdvance_NotdefGlyph_ReadsItsOwnDistinctAdvance()
    {
        byte[] font = MinimalTrueType.BuildWithDistinctWidths("AB");
        var program = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(program);
        Assert.Equal(MinimalTrueType.NotdefAdvance, program!.GetAdvance(0));
    }

    /// <summary>
    /// The guard: at the wrong offset, every advance (including .notdef's own distinct one) comes
    /// back identical, because the array is never populated. Pinning three distinct values, all
    /// non-500 (the flat fallback), is what a "read the right offset" claim needs — three equal
    /// numbers would pass either way.
    /// </summary>
    [Fact]
    public void GetAdvance_ThreeGlyphsHaveThreeDistinctAdvances()
    {
        byte[] font = MinimalTrueType.BuildWithDistinctWidths("AB");
        var program = TrueTypeFontProgram.Parse(font);
        Assert.NotNull(program);
        program!.TryGetGlyph('A', out ushort gidA);
        program.TryGetGlyph('B', out ushort gidB);

        int notdef = program.GetAdvance(0);
        int a = program.GetAdvance(gidA);
        int b = program.GetAdvance(gidB);

        Assert.NotEqual(notdef, a);
        Assert.NotEqual(a, b);
        Assert.NotEqual(notdef, b);
        const int FlatFallback = 500; // GetAdvance's own "_glyphWidths empty" default
        Assert.NotEqual(FlatFallback, notdef);
        Assert.NotEqual(FlatFallback, a);
        Assert.NotEqual(FlatFallback, b);
    }
}
