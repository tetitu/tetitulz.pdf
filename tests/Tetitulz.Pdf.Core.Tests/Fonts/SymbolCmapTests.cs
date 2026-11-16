// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A font whose only <c>cmap</c> subtable is (3,0) Microsoft Symbol or (1,0) Macintosh Roman must
/// still parse with a usable character map.
///
/// <para><c>ParseCmap</c> accepted only (3,1), (3,10) and (0,3/4/6), and the format dispatch knew
/// only 4, 12 and 6 — so Wingdings, Symbol and most icon webfonts, which ship exactly one of the
/// rejected subtables, produced a program with metrics and glyphs but an EMPTY cmap.
/// <c>TextRunComposer</c> then reported every code point as missing and drew nothing, and
/// <c>SystemFontResolver.Covers</c> rejected the face — while the font in fact had the glyph. The
/// comment above the return even named the (3,0) case as though it were handled.</para>
/// </summary>
public class SymbolCmapTests
{
    [Fact]
    public void Parse_FontWithOnlyAThreeZeroSymbolSubtable_MapsItsCodePoints()
    {
        // An icon font: U+F000–U+F002 → glyphs 1–3, in a format-4 (3,0) subtable.
        byte[] font = BuildFont(new[] { Subtable(3, 0, Format4(0xF000, 0xF002, 1)) });

        var program = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(program);
        Assert.True(program!.TryGetGlyph(0xF000, out ushort g0));
        Assert.Equal(1, g0);
        Assert.True(program.TryGetGlyph(0xF002, out ushort g2));
        Assert.Equal(3, g2);
    }

    [Fact]
    public void Parse_FontWithOnlyAThreeZeroSubtable_SetsTheSymbolicFlag()
    {
        // The pre-existing comment claimed this; nothing did it.
        byte[] font = BuildFont(new[] { Subtable(3, 0, Format4(0xF000, 0xF002, 1)) });

        var program = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(program);
        Assert.Equal(4, program!.Flags & 4);   // bit 3 (value 4) = Symbolic
    }

    [Fact]
    public void Parse_FontWithOnlyAMacRomanFormatZeroSubtable_MapsItsCodePoints()
    {
        // Format 0 had no case in the format dispatch at all, so a (1,0) font mapped nothing.
        var glyphForCode = new byte[256];
        glyphForCode[65] = 7;    // 'A' → glyph 7
        glyphForCode[66] = 8;    // 'B' → glyph 8
        byte[] font = BuildFont(new[] { Subtable(1, 0, Format0(glyphForCode)) });

        var program = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(program);
        Assert.True(program!.TryGetGlyph('A', out ushort a));
        Assert.Equal(7, a);
        Assert.True(program.TryGetGlyph('B', out ushort b));
        Assert.Equal(8, b);
    }

    [Fact]
    public void Parse_FontWithBothUnicodeAndSymbolSubtables_LetsUnicodeWin()
    {
        // The symbol tier is lowest priority and merges only where Unicode left a gap, so a
        // well-formed Unicode font parses exactly as it did before the tier existed.
        byte[] font = BuildFont(new[]
        {
            Subtable(3, 1, Format4(0x0041, 0x0041, 10)),   // 'A' → 10  (Unicode BMP)
            Subtable(3, 0, Format4(0x0041, 0x0042, 90)),   // 'A' → 90, 'B' → 91  (symbol)
        });

        var program = TrueTypeFontProgram.Parse(font);

        Assert.NotNull(program);
        Assert.True(program!.TryGetGlyph('A', out ushort a));
        Assert.Equal(10, a);                     // Unicode mapping preserved, not overwritten
        Assert.True(program.TryGetGlyph('B', out ushort b));
        Assert.Equal(91, b);                     // gap filled from the symbol subtable
    }

    // ------------------------------------------------------------------ font construction

    private static (int platform, int encoding, byte[] body) Subtable(int platform, int encoding, byte[] body)
        => (platform, encoding, body);

    /// <summary>An sfnt whose only table is a cmap holding the given subtables.</summary>
    private static byte[] BuildFont(IReadOnlyList<(int platform, int encoding, byte[] body)> subtables)
    {
        // cmap table: header(4) + 8 bytes per encoding record, then the subtable bodies.
        int recordsLen = 4 + 8 * subtables.Count;
        var cmap = new List<byte>();
        AddU16(cmap, 0);                       // version
        AddU16(cmap, subtables.Count);         // numTables

        int bodyOffset = recordsLen;
        foreach (var st in subtables)
        {
            AddU16(cmap, st.platform);
            AddU16(cmap, st.encoding);
            AddU32(cmap, (uint)bodyOffset);
            bodyOffset += st.body.Length;
        }
        foreach (var st in subtables) cmap.AddRange(st.body);

        // sfnt: header(12) + one 16-byte table directory entry.
        var font = new List<byte>();
        AddU32(font, 0x00010000);              // version
        AddU16(font, 1);                       // numTables
        AddU16(font, 0); AddU16(font, 0); AddU16(font, 0);
        font.AddRange(new[] { (byte)'c', (byte)'m', (byte)'a', (byte)'p' });
        AddU32(font, 0);                       // checksum
        AddU32(font, 28);                      // offset (12 + 16)
        AddU32(font, (uint)cmap.Count);        // length
        font.AddRange(cmap);
        return font.ToArray();
    }

    /// <summary>A format-4 subtable mapping [first..last] to consecutive glyphs from firstGlyph.</summary>
    private static byte[] Format4(int first, int last, int firstGlyph)
    {
        // Two segments: the real one, and the mandatory 0xFFFF terminator.
        const int segCount = 2;
        var b = new List<byte>();
        AddU16(b, 4);                          // format
        AddU16(b, 16 + 8 * segCount);          // length
        AddU16(b, 0);                          // language
        AddU16(b, segCount * 2);               // segCountX2
        AddU16(b, 0); AddU16(b, 0); AddU16(b, 0);   // searchRange, entrySelector, rangeShift

        AddU16(b, last); AddU16(b, 0xFFFF);    // endCode[]
        AddU16(b, 0);                          // reservedPad
        AddU16(b, first); AddU16(b, 0xFFFF);   // startCode[]
        // idDelta maps `first` to `firstGlyph`: glyph = (code + delta) & 0xFFFF.
        AddU16(b, (firstGlyph - first) & 0xFFFF);
        AddU16(b, 1);                          // terminator segment: 0xFFFF + 1 = 0
        AddU16(b, 0); AddU16(b, 0);            // idRangeOffset[]
        return b.ToArray();
    }

    /// <summary>A format-0 subtable: one glyph index per code 0..255.</summary>
    private static byte[] Format0(byte[] glyphForCode)
    {
        var b = new List<byte>();
        AddU16(b, 0);                          // format
        AddU16(b, 262);                        // length
        AddU16(b, 0);                          // language
        b.AddRange(glyphForCode);
        return b.ToArray();
    }

    private static void AddU16(List<byte> b, int v)
    {
        b.Add((byte)(v >> 8)); b.Add((byte)v);
    }

    private static void AddU32(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
    }
}
