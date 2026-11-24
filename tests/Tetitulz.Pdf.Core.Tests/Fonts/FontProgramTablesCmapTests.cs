// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="PdfTrueTypeFont.FontProgramTables"/>'s cmap reading at its edges: the <c>(1,0)</c>
/// Macintosh table, format 6, format 0's last code, and
/// the zero counts that make a table unreadable.
/// </summary>
public class FontProgramTablesCmapTests
{
    /// <summary>The <c>(1,0)</c> subtable is read into <c>MacCmap</c>, and not into the symbol table.</summary>
    [Fact]
    public void AMacintoshSubtableIsReadAsTheMacCmap()
    {
        var tables = PdfTrueTypeFont.FontProgramTables.Parse(Font(1, 0, Format0(c => c == 0x41 ? 7 : 0)));

        Assert.Equal(new Dictionary<int, int> { { 0x41, 7 } }, tables.MacCmap);
        Assert.Null(tables.SymbolCmap);
    }

    /// <summary>
    /// Format 0's array has 256 entries, codes 0 to 255; the last one is a code like any other.
    /// </summary>
    [Fact]
    public void Format0MapsCode255()
    {
        var map = PdfTrueTypeFont.FontProgramTables.Parse(Font(3, 0, Format0(c => c))).SymbolCmap;

        Assert.Equal(255, map[255]);
        Assert.Equal(255, map.Count);   // code 0 maps to glyph 0, which is no mapping
    }

    /// <summary>
    /// Format 6 maps <c>entryCount</c> consecutive codes from <c>firstCode</c>; a zero glyph is no
    /// mapping, and bytes after the array are not entries.
    /// </summary>
    [Fact]
    public void Format6MapsItsTrimmedRangeAndNoMore()
    {
        byte[] subtable = Bytes(
            U16(6), U16(0), U16(0),       // format, length, language
            U16(0x41), U16(3),             // firstCode, entryCount
            U16(5), U16(0), U16(9),        // glyph ids
            U16(11));                      // a fourth value past entryCount

        var map = PdfTrueTypeFont.FontProgramTables.Parse(Font(3, 0, subtable)).SymbolCmap;

        Assert.Equal(new Dictionary<int, int> { { 0x41, 5 }, { 0x43, 9 } }, map);
    }

    /// <summary>
    /// A format 4 subtable declaring no segments is unreadable: no table, not an empty one. The
    /// subtable carries the reserved pad word, so its zero-segment layout fits and only the
    /// segment count can refuse it.
    /// </summary>
    [Fact]
    public void Format4WithNoSegmentsIsRefused()
    {
        byte[] subtable = Bytes(U16(4), U16(16), U16(0), U16(0), U16(0), U16(0), U16(0), U16(0));

        Assert.Null(PdfTrueTypeFont.FontProgramTables.Parse(Font(3, 0, subtable)).SymbolCmap);
    }

    /// <summary>
    /// A font declaring no tables is not a font. One whose directory exactly fills the file is: the
    /// directory fits, even though the table it lists points nowhere.
    /// </summary>
    [Fact]
    public void ADirectoryOfNoTablesIsRefusedAndOneThatExactlyFitsIsRead()
    {
        byte[] none = Bytes(U32(0x00010000), U16(0), U16(0), U16(0), U16(0));
        Assert.Null(PdfTrueTypeFont.FontProgramTables.Parse(none));

        byte[] exact = Bytes(U32(0x00010000), U16(1), U16(16), U16(0), U16(0),
                             Tag("cmap"), U32(0), U32(9999), U32(0));
        Assert.Equal(28, exact.Length);
        var tables = PdfTrueTypeFont.FontProgramTables.Parse(exact);
        Assert.NotNull(tables);
        Assert.Null(tables.SymbolCmap);
    }

    // ------------------------------------------------------------------

    /// <summary>A font holding one <c>cmap</c> table with one encoding record.</summary>
    private static byte[] Font(int platform, int encoding, byte[] subtable)
    {
        byte[] cmap = Bytes(U16(0), U16(1), U16(platform), U16(encoding), U32(12), subtable);
        return Bytes(U32(0x00010000), U16(1), U16(16), U16(0), U16(0),
                     Tag("cmap"), U32(0), U32(28), U32(cmap.Length), cmap);
    }

    private static byte[] Format0(System.Func<int, int> gidOf)
    {
        var glyphs = new byte[256];
        for (int c = 0; c < 256; c++) glyphs[c] = (byte)gidOf(c);
        return Bytes(U16(0), U16(262), U16(0), glyphs);
    }

    private static byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
    private static byte[] U32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    private static byte[] Tag(string t) => System.Text.Encoding.ASCII.GetBytes(t);

    private static byte[] Bytes(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] p in parts) all.AddRange(p);
        return all.ToArray();
    }
}
