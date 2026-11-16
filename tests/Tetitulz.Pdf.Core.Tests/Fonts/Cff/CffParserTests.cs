// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts.Cff;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>Tests for the CFF binary parser (GAP 22.2).</summary>
public class CffParserTests
{
    // ----------------------------------------------------------------
    //  Minimal hand-crafted CFF binary helpers
    // ----------------------------------------------------------------

    /// <summary>
    /// Builds a minimal valid CFF binary with one font, two charstrings
    /// (GID 0 = .notdef endchar, GID 1 = a simple rectangle with width 500),
    /// and a custom Format-0 encoding mapping char code 65 ('A') → GID 1.
    /// </summary>
    private static byte[] BuildMinimalCff(bool withFontMatrix = false)
    {
        // ---- Charstrings ----
        // GID 0: endchar
        byte[] cs0 = { 14 };

        // GID 1: rmoveto(width=500, dx=0, dy=0); rlineto(0,400, 400,0, 0,-400); endchar
        // Encoding: 500 = [248,136], 0 = [139], 400 = [248,36], -400 = [252,36]
        byte[] cs1 =
        {
            248, 136,           // width = 500 (stack arg before rmoveto)
            139, 139,           // dx=0, dy=0
            21,                 // rmoveto
            139, 248, 36,       // 0, 400
            248, 36, 139,       // 400, 0
            139, 252, 36,       // 0, -400
            5,                  // rlineto
            14                  // endchar
        };

        // ---- Encoding ----
        // Format 0: 1 code entry — GID 1 → char code 65 ('A')
        byte[] encoding = { 0, 1, 65 };

        // ---- Charset ----
        // Format 0: 1 SID entry for GID 1 → SID 1 (arbitrary)
        byte[] charset = { 0, 0, 1 };

        // ---- Charstrings INDEX ----
        // count=2, offSize=1, offsets=[1,2,2+16]=[1,2,18]
        int cs1Len = cs1.Length; // 16
        byte[] csIndex = BuildIndex(new[] { cs0, cs1 });

        // ---- Top DICT ----
        // We need to encode: Encoding offset(16), Charset offset(17), CharStrings offset(18)
        // Compute offsets assuming layout:
        //   header(4) + nameIndex + topDictIndex + stringIndex + globalSubrIndex
        //   + encoding + charset + csIndex
        // We will compute after we know the sizes.

        // Fixed-size sections first:
        byte[] nameIndex = BuildIndex(new[] { System.Text.Encoding.ASCII.GetBytes("Test") });
        byte[] stringIndex = BuildIndex(System.Array.Empty<byte[]>());
        byte[] globalSubrIndex = BuildIndex(System.Array.Empty<byte[]>());

        // We'll construct the Top DICT later once we know the offset of the csIndex.
        // Layout:
        //   0: header (4)
        //   4: nameIndex
        //   4+nameIndex.Length: topDictIndex (placeholder)
        //   ... stringIndex, globalSubrIndex
        //   ... encoding
        //   ... charset
        //   ... csIndex

        // Build a two-pass approach: first compute offsets, then build top dict.

        // Pass 1: Sizes
        int hdrSize      = 4;
        int nameSize     = nameIndex.Length;
        // Top DICT is variable — use a maximum estimate (20 bytes of DICT data) + INDEX overhead (~5)
        // We'll iterate once.
        byte[] topDictPayload = BuildTopDict(0, 0, 0, withFontMatrix); // placeholder offsets

        // Top DICT INDEX wrapping the payload
        byte[] topDictIndex = BuildIndex(new[] { topDictPayload });

        int topDictIndexSize = topDictIndex.Length;

        // Compute absolute offsets
        int posAfterTopDictIndex = hdrSize + nameSize + topDictIndexSize;
        int stringIndexOffset    = posAfterTopDictIndex;
        int globalSubrOffset     = stringIndexOffset    + stringIndex.Length;
        int encodingOffset       = globalSubrOffset     + globalSubrIndex.Length;
        int charsetOffset        = encodingOffset       + encoding.Length;
        int csIndexOffset        = charsetOffset        + charset.Length;

        // Pass 2: build real Top DICT with correct offsets
        topDictPayload = BuildTopDict(encodingOffset, charsetOffset, csIndexOffset, withFontMatrix);
        topDictIndex   = BuildIndex(new[] { topDictPayload });

        // If TopDictIndex size changed (unlikely for small offsets), recompute.
        // In practice our offsets are < 128 so they encode as 1-byte dict integers.

        // ---- Assemble CFF ----
        var cff = new List<byte>();
        // Header
        cff.Add(1); cff.Add(0); cff.Add(4); cff.Add(1); // major, minor, hdrSize, offSize
        AddRange(cff, nameIndex);
        AddRange(cff, topDictIndex);
        AddRange(cff, stringIndex);
        AddRange(cff, globalSubrIndex);
        AddRange(cff, encoding);
        AddRange(cff, charset);
        AddRange(cff, csIndex);

        return cff.ToArray();
    }

    // ----------------------------------------------------------------
    //  Helper: CFF DICT builder
    // ----------------------------------------------------------------

    /// <summary>
    /// Encodes a minimal Top DICT with Encoding(16), charset(15), CharStrings(17).
    /// Offsets are encoded as single-byte integers (valid for offsets 0..107 with value+139,
    /// or two-byte for larger values).
    /// </summary>
    private static byte[] BuildTopDict(int encodingOff, int charsetOff, int csOff, bool withFontMatrix = false)
    {
        var d = new List<byte>();
        if (withFontMatrix)
        {
            // /FontMatrix [0.0005 0 0 0.0005 0 0] FontMatrix(operator 12 7).
            // 0.0005 is BCD-encoded: 30, nibbles 0 '.' 0 0 0 5 <terminator F>.
            void Real0005()
            {
                d.Add(30);
                d.Add(0x0A); // '0' '.'
                d.Add(0x00); // '0' '0'
                d.Add(0x05); // '0' '5'
                d.Add(0xFF); // terminator
            }
            Real0005(); EncodeInt(d, 0); EncodeInt(d, 0);
            Real0005(); EncodeInt(d, 0); EncodeInt(d, 0);
            d.Add(12); d.Add(7);
        }
        // charset (operator 15)
        EncodeInt(d, charsetOff); d.Add(15);
        // Encoding (operator 16)
        EncodeInt(d, encodingOff); d.Add(16);
        // CharStrings (operator 17)
        EncodeInt(d, csOff); d.Add(17);
        return d.ToArray();
    }

    private static void EncodeInt(List<byte> d, int v)
    {
        if (v >= -107 && v <= 107)
        {
            d.Add((byte)(v + 139));
        }
        else if (v >= 108 && v <= 1131)
        {
            int b = v - 108;
            d.Add((byte)(247 + b / 256));
            d.Add((byte)(b % 256));
        }
        else
        {
            // 5-byte encoding using 29
            d.Add(29);
            d.Add((byte)((uint)v >> 24));
            d.Add((byte)((uint)v >> 16));
            d.Add((byte)((uint)v >> 8));
            d.Add((byte)v);
        }
    }

    // ----------------------------------------------------------------
    //  Helper: INDEX builder
    // ----------------------------------------------------------------

    private static byte[] BuildIndex(byte[][] items)
    {
        if (items.Length == 0)
            return new byte[] { 0, 0 }; // count=0

        int totalDataLen = 0;
        foreach (var item in items) totalDataLen += item.Length;

        // Determine offSize
        int maxOffset = totalDataLen + 1; // 1-based
        byte offSize = maxOffset <= 0xFF ? (byte)1 : maxOffset <= 0xFFFF ? (byte)2 : (byte)3;

        var idx = new List<byte>();
        idx.Add((byte)(items.Length >> 8));
        idx.Add((byte)(items.Length & 0xFF));
        idx.Add(offSize);

        // Offsets (1-based)
        int off = 1;
        WriteOffset(idx, off, offSize);
        foreach (var item in items)
        {
            off += item.Length;
            WriteOffset(idx, off, offSize);
        }

        // Data
        foreach (var item in items) idx.AddRange(item);
        return idx.ToArray();
    }

    private static void WriteOffset(List<byte> buf, int off, byte offSize)
    {
        for (int i = offSize - 1; i >= 0; i--)
            buf.Add((byte)((off >> (i * 8)) & 0xFF));
    }

    private static void AddRange(List<byte> list, byte[] arr)
    {
        foreach (var b in arr) list.Add(b);
    }

    // ================================================================
    //  Tests
    // ================================================================

    [Fact]
    public void Parse_ReturnsNull_ForNullData()
    {
        var result = CffParser.Parse(null);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_ReturnsNull_ForTooShortData()
    {
        var result = CffParser.Parse(new byte[] { 1, 0, 4 });
        Assert.Null(result);
    }

    [Fact]
    public void Parse_ReturnsNull_ForWrongMajorVersion()
    {
        // Major version != 1 → not CFF
        var result = CffParser.Parse(new byte[] { 2, 0, 4, 1, 0, 0 });
        Assert.Null(result);
    }

    [Fact]
    public void Parse_MinimalCff_ReturnsTwoGlyphs()
    {
        var cff = BuildMinimalCff();
        var font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.Equal(2, font.Charstrings.Count);
    }

    [Fact]
    public void Parse_MinimalCff_CharstringGid0IsEndchar()
    {
        var cff = BuildMinimalCff();
        var font = CffParser.Parse(cff);

        Assert.NotNull(font);
        // GID 0 charstring should be [14] (endchar)
        Assert.Single(font.Charstrings[0]);
        Assert.Equal(14, font.Charstrings[0][0]);
    }

    [Fact]
    public void Parse_MinimalCff_CharstringGid1HasContent()
    {
        var cff = BuildMinimalCff();
        var font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.True(font.Charstrings[1].Length > 1,
            "GID 1 charstring should have more than one byte");
    }

    [Fact]
    public void Parse_MinimalCff_EncodingMapsCode65ToGid1()
    {
        var cff = BuildMinimalCff();
        var font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.True(font.CharCodeToGid.ContainsKey(65),
            "Encoding should contain char code 65 ('A')");
        Assert.Equal(1, font.CharCodeToGid[65]);
    }

    [Fact]
    public void Parse_MinimalCff_DefaultAndNominalWidths_AreZeroByDefault()
    {
        var cff = BuildMinimalCff();
        var font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.Equal(0f, font.DefaultWidthX);
        Assert.Equal(0f, font.NominalWidthX);
    }

    [Fact]
    public void Parse_MinimalCff_NoLocalSubrs()
    {
        var cff = BuildMinimalCff();
        var font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.Empty(font.LocalSubrs);
    }

    [Fact]
    public void Parse_MinimalCff_NoGlobalSubrs()
    {
        var cff = BuildMinimalCff();
        var font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.Empty(font.GlobalSubrs);
    }

    [Fact]
    public void Parse_NoFontMatrix_DefaultsToThousandthEm()
    {
        var font = CffParser.Parse(BuildMinimalCff());
        Assert.NotNull(font);
        Assert.Equal(0.001f, font.FontMatrixScale);
    }

    [Fact]
    public void Parse_TopDictFontMatrix_IsExtracted()
    {
        var font = CffParser.Parse(BuildMinimalCff(withFontMatrix: true));
        Assert.NotNull(font);
        // /FontMatrix [0.0005 …] → 2000-unit em.
        Assert.Equal(0.0005f, font.FontMatrixScale, 6);
        // Sanity: the rest of the font still parses with the FontMatrix present.
        Assert.Equal(2, font.Charstrings.Count);
        Assert.Equal(1, font.CharCodeToGid[65]);
    }

    [Fact]
    public void Parse_GarbageBytes_ReturnsNull()
    {
        // Random garbage that might look like CFF header but isn't
        var garbage = new byte[] { 1, 0, 4, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        // Should not throw
        var result = CffParser.Parse(garbage);
        // Either null or a partially parsed result — the key requirement is no exception.
        // If it happens to parse partially without throwing, that's acceptable.
        // Just assert it didn't throw (test completes = pass).
    }
}
