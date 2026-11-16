// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Two readers decode a format-0 <c>cmap</c> subtable — <c>TrueTypeFontProgram.ParseCmap0</c> and
/// <c>PdfTrueTypeFont</c>'s <c>ReadFormat0</c> — and they must agree on a <b>truncated</b> one.
/// </summary>
/// <remarks>
/// <para>A format-0 subtable is a 256-byte glyph index array. <c>ParseCmap0</c> loops while bytes
/// remain and keeps what it finds; <c>ReadFormat0</c> demanded all 256 and returned <c>null</c>
/// otherwise.</para>
///
/// <para>So for a symbolic font whose subtable runs past end of file, <c>SymbolCmap</c> /
/// <c>MacCmap</c> came back null and <b>§9.6.6.4 byte-code glyph selection fell through entirely</b>
/// — the font drew nothing through that path — while the other reader mapped it happily.</para>
///
/// <para><b>All-or-nothing is the wrong trade for a table whose entries are independent.</b> The
/// codes that are present are still correct; discarding them loses information the file plainly
/// contains, and does so silently.</para>
/// </remarks>
public class TruncatedCmapFormat0Tests
{
    /// <summary>
    /// <b>The defect.</b> A truncated subtable yields the codes that are present, in both readers.
    /// </summary>
    [Theory]
    [InlineData(64)]
    [InlineData(200)]
    [InlineData(255)]
    public void ATruncatedFormat0SubtableYieldsThePrefixThatIsPresent(int availableBytes)
    {
        var map = PdfTrueTypeFont.FontProgramTables.Parse(Font(availableBytes))?.SymbolCmap;

        Assert.True(map != null, "the truncated subtable was discarded entirely");

        // Code c maps to gid c + 1, so every available code has a non-zero gid and is kept.
        Assert.Equal(availableBytes, map.Count);
        Assert.Equal(1, map[0]);
        Assert.Equal(availableBytes, map[availableBytes - 1]);
        Assert.False(map.ContainsKey(availableBytes), "a code past the truncation was invented");
    }

    /// <summary>
    /// <b>The control.</b> A complete subtable still maps all 256 codes, so the fix cannot be
    /// "accept anything and return whatever".
    /// </summary>
    [Fact]
    public void ACompleteFormat0SubtableStillMapsEveryCode()
    {
        var map = PdfTrueTypeFont.FontProgramTables.Parse(Font(256))?.SymbolCmap;

        Assert.True(map != null);
        // gid = (c + 1) & 0xFF, so code 255 gets gid 0 and is the one code correctly omitted.
        Assert.Equal(255, map.Count);
        Assert.Equal(255, map[254]);
        Assert.False(map.ContainsKey(255));
    }

    /// <summary>
    /// The floor at the other end: a subtable with no glyph array at all is still refused, because
    /// there is nothing to keep.
    /// </summary>
    [Theory]
    [InlineData(0)]
    public void ASubtableWithNoGlyphArrayIsStillRefused(int availableBytes)
        => Assert.Null(PdfTrueTypeFont.FontProgramTables.Parse(Font(availableBytes))?.SymbolCmap);

    // ------------------------------------------------------------------

    /// <summary>
    /// A minimal sfnt carrying one <c>cmap</c> with a single (3,0) format-0 subtable whose glyph
    /// array is <paramref name="availableBytes"/> long instead of 256. Code <c>c</c> maps to
    /// <c>c + 1</c>, so every entry is distinguishable.
    /// </summary>
    private static byte[] Font(int availableBytes)
    {
        var b = new List<byte>();

        void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void U32(long v)
        {
            b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16));
            b.Add((byte)(v >> 8)); b.Add((byte)v);
        }

        // Offset table: one table, 'cmap'.
        U32(0x00010000); U16(1); U16(16); U16(0); U16(0);
        b.Add((byte)'c'); b.Add((byte)'m'); b.Add((byte)'a'); b.Add((byte)'p');
        U32(0);                       // checksum
        int cmapOffsetAt = b.Count; U32(0);   // offset — patched below
        U32(0);                       // length — not read by either reader

        int cmapStart = b.Count;
        b[cmapOffsetAt] = (byte)(cmapStart >> 24);
        b[cmapOffsetAt + 1] = (byte)(cmapStart >> 16);
        b[cmapOffsetAt + 2] = (byte)(cmapStart >> 8);
        b[cmapOffsetAt + 3] = (byte)cmapStart;

        // cmap header: version 0, one encoding record, (3,0) at offset 12.
        U16(0); U16(1);
        U16(3); U16(0); U32(12);

        // Subtable: format 0, length, language, then the (possibly short) glyph array.
        U16(0); U16(262); U16(0);
        for (int c = 0; c < availableBytes; c++) b.Add((byte)((c + 1) & 0xFF));

        return b.ToArray();
    }
}
