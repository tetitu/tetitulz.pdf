// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// Tests for CFF2 / variable-font parsing and the Type 2 charstring
/// blend operator (opcode 16) — closes GAPS.md R13 deferral.
/// </summary>
public class CffParserCff2Tests
{
    // ------------------------------------------------------------------
    //  Minimal CFF2 binary builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal valid CFF2 binary with one charstring (endchar).
    /// Layout:
    ///   [0..4]  CFF2 header: major=2, minor=0, hdrSize=5, topDictLength=2
    ///   [5..6]  TOP DICT: integer(charStringsOffset=9), op17
    ///   [7..8]  GlobalSubr INDEX (empty): count=0
    ///   [9..14] CharStrings INDEX: count=1, offSize=1, offsets=[1,2], data=[endchar]
    /// </summary>
    private static byte[] BuildMinimalCff2()
    {
        // Charstrings offset = 9 (hdrSize + topDictLen + 2 empty globalSubrs = 5+2+2=9).
        // Integer 9 in DICT encoding: b0 = 9+139 = 148 = 0x94.
        return new byte[]
        {
            // CFF2 header
            0x02, 0x00, 0x05, 0x00, 0x02,
            // TOP DICT: push 9, op=17(0x11)
            0x94, 0x11,
            // GlobalSubr INDEX (empty)
            0x00, 0x00,
            // CharStrings INDEX: 1 glyph, 1 byte (endchar=14=0x0E)
            0x00, 0x01, 0x01, 0x01, 0x02, 0x0E,
        };
    }

    /// <summary>
    /// Builds a CFF2 binary with a VariationStore whose default ItemVariationData references
    /// <paramref name="regionCount"/> regions, so <c>BlendRegionCount == regionCount</c> is
    /// returned from Parse(). The <c>blend</c> operator removes one delta per region, NOT per
    /// axis — so the region count (not the axis count) is the value the evaluator needs.
    ///
    /// Layout:
    ///   [0..4]   CFF2 header: hdrSize=5, topDictLen=4
    ///   [5..8]   TOP DICT: push(charStringsOff=11), op17, push(varStoreOff=17), op24
    ///   [9..10]  GlobalSubr INDEX (empty)
    ///   [11..16] CharStrings INDEX (1 endchar glyph)
    ///   [17..]   ItemVariationStore { format, regionListOffset(u32), ivdCount, ivdOffsets[1] }
    ///            followed by ItemVariationData0 { itemCount, wordDeltaCount, regionIndexCount, … }
    /// </summary>
    private static byte[] BuildCff2WithVariationStore(int regionCount)
    {
        var topDict = new byte[] { 0x96, 0x11, 0x9C, 0x18 }; // charStrings=11(op17), varStore=17(op24)
        var csIndex = new byte[] { 0x00, 0x01, 0x01, 0x01, 0x02, 0x0E }; // 1 endchar glyph

        // ItemVariationData0: itemCount(0) + wordDeltaCount(0) + regionIndexCount + regionIndexes.
        var ivd0 = new List<byte> { 0x00, 0x00, 0x00, 0x00,
            (byte)((regionCount >> 8) & 0xFF), (byte)(regionCount & 0xFF) };
        for (int i = 0; i < regionCount; i++) { ivd0.Add(0x00); ivd0.Add((byte)i); }

        // ItemVariationStore header (12 bytes): IVD0 follows immediately at offset 12.
        var store = new List<byte>
        {
            0x00, 0x01,             // format = 1
            0x00, 0x00, 0x00, 0x00, // regionListOffset (u32) — not needed for the region-count read
            0x00, 0x01,             // ItemVariationDataCount = 1
            0x00, 0x00, 0x00, 0x0C, // ItemVariationDataOffsets[0] (u32) = 12
        };
        store.AddRange(ivd0);
        var varStore = store.ToArray();

        var result = new byte[5 + topDict.Length + 2 + csIndex.Length + varStore.Length];
        int off = 0;
        result[off++] = 0x02; result[off++] = 0x00; result[off++] = 0x05;
        result[off++] = 0x00; result[off++] = (byte)topDict.Length;
        Array.Copy(topDict, 0, result, off, topDict.Length); off += topDict.Length;
        result[off++] = 0x00; result[off++] = 0x00; // GlobalSubrs (empty)
        Array.Copy(csIndex, 0, result, off, csIndex.Length); off += csIndex.Length;
        Array.Copy(varStore, 0, result, off, varStore.Length);
        return result;
    }

    // ------------------------------------------------------------------
    //  CFF2 parsing tests
    // ------------------------------------------------------------------

    /// <summary>
    /// The siblings below cover <c>IsCff2</c>, the charstring count and the failure reason, so the
    /// gap this fills is the <b>content</b>: the parser must return the charstring bytes the TOP
    /// DICT's offset actually points at (a single <c>endchar</c>), not an empty or invented one.
    ///
    /// <para>It also pins the version dispatch from the other side. Flipping the header's major
    /// version to 1 must stop the binary being read as CFF2 — otherwise <c>IsCff2</c> is decoration
    /// rather than a decision, and every CFF2-only behaviour downstream is reached by accident.</para>
    ///
    /// <para>The predecessor asserted <c>NotNull</c>, which its three siblings each imply.</para>
    /// </summary>
    [Fact]
    public void Parse_Cff2_ReadsTheCharstringTheTopDictPointsAt()
    {
        var font = CffParser.Parse(BuildMinimalCff2());

        Assert.NotNull(font);
        Assert.True(font.IsCff2);
        Assert.Equal(new byte[] { 0x0E }, Assert.Single(font.Charstrings));   // endchar

        // Same bytes, major version 1: must NOT be taken for CFF2.
        byte[] asCff1 = BuildMinimalCff2();
        asCff1[0] = 0x01;
        var parsedAsCff1 = CffParser.Parse(asCff1);
        Assert.True(parsedAsCff1 == null || !parsedAsCff1.IsCff2,
            "a header claiming major version 1 was still parsed as CFF2");
    }

    [Fact]
    public void Parse_Cff2_SetsIsCff2True()
    {
        byte[] data = BuildMinimalCff2();
        var font = CffParser.Parse(data);
        Assert.NotNull(font);
        Assert.True(font.IsCff2);
    }

    [Fact]
    public void Parse_Cff2_HasOneCharstring()
    {
        byte[] data = BuildMinimalCff2();
        var font = CffParser.Parse(data);
        Assert.NotNull(font);
        Assert.Single(font.Charstrings);
    }

    [Fact]
    public void Parse_Cff2_LastFailureIsNone()
    {
        byte[] data = BuildMinimalCff2();
        CffParser.Parse(data);
        Assert.Equal(CffParser.CffFailure.None, CffParser.LastFailureReason);
    }

    [Fact]
    public void Parse_Cff2_BlendRegionCount_FromVariationStore()
    {
        // 3 regions → blend removes 3 deltas per blended value (region count, not axis count).
        byte[] data = BuildCff2WithVariationStore(regionCount: 3);
        var font = CffParser.Parse(data);
        Assert.NotNull(font);
        Assert.True(font.IsCff2);
        Assert.Equal(3, font.BlendRegionCount);
    }

    [Fact]
    public void Parse_Cff2_BlendRegionCount_ZeroWhenNoVariationStore()
    {
        byte[] data = BuildMinimalCff2();
        var font = CffParser.Parse(data);
        Assert.NotNull(font);
        Assert.Equal(0, font.BlendRegionCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Parse_Cff2_BlendRegionCount_MatchesItemVariationData(int regionCount)
    {
        var font = CffParser.Parse(BuildCff2WithVariationStore(regionCount));
        Assert.NotNull(font);
        Assert.Equal(regionCount, font.BlendRegionCount);
    }

    [Fact]
    public void ReadCff2BlendRegionCount_MalformedStore_ReturnsZeroWithoutThrowing()
    {
        // Truncated / out-of-range stores must yield 0, never throw or index out of bounds.
        Assert.Equal(0, CffParser.ReadCff2BlendRegionCount(null, 0));
        Assert.Equal(0, CffParser.ReadCff2BlendRegionCount(new byte[] { 0, 1, 0, 0 }, 0)); // too short
        Assert.Equal(0, CffParser.ReadCff2BlendRegionCount(new byte[64], 10_000));          // offset past end
        // ivdOffsets[0] points past the buffer → 0.
        var bad = new byte[12];
        bad[6] = 0x00; bad[7] = 0x01;                 // ivdCount = 1
        bad[8] = 0x7F; bad[9] = 0xFF;                 // ivd0 offset huge
        Assert.Equal(0, CffParser.ReadCff2BlendRegionCount(bad, 0));
    }

    [Fact]
    public void Parse_Cff2_WithCharCodeToGid_PreservesIsCff2()
    {
        var font = CffParser.Parse(BuildMinimalCff2());
        Assert.NotNull(font);
        var remapped = font.WithCharCodeToGid(new Dictionary<int, int> { [65] = 0 });
        Assert.True(remapped.IsCff2);
        Assert.Equal(font.BlendRegionCount, remapped.BlendRegionCount);
    }

    [Fact]
    public void Parse_Cff1_IsCff2_IsFalse()
    {
        // Ensure CFF1 fonts still have IsCff2=false after the CFF2 changes.
        // Build a minimal CFF1 stream (major=1).
        var font = CffParser.Parse(BuildMinimalCff1());
        if (font != null) // may be null if minimal stream fails, but IsCff2 must be false
            Assert.False(font.IsCff2);
    }

    // Minimal CFF1: just enough to get a non-null parse result.
    private static byte[] BuildMinimalCff1()
    {
        // Use the same structure as CffParserTests.BuildMinimalCff — abbreviated here.
        // A CFF1 with 1 charstring (endchar) and no encoding.
        var cs0 = new byte[] { 14 };
        var csIndex = BuildIndex(new[] { cs0 });
        // Top DICT: charStrings offset, encoded as integer + op 17.
        // We need the offset to charStrings relative to start.
        // Header: major=1 minor=0 hdrSize=4 offSize=1 → 4 bytes.
        // Name INDEX (count=0) → 2 bytes.
        // Top DICT INDEX: count=1 offSize=1 offsets=[1,?] data=[encoded_offset + 0x11]
        // String INDEX (count=0) → 2 bytes.
        // Global Subr INDEX (count=0) → 2 bytes.
        // Then CharStrings INDEX.
        // Simplest: hardcode known offsets.
        // header(4) + NameINDEX(2) + TopDictINDEX + StringINDEX(2) + GlobalSubrINDEX(2) + CharStrings
        // TopDictINDEX: [0x00,0x01,0x01,0x01,topDictLen+1, topDictBytes...]
        // charStringsOffset = 4+2+5+topDictLen+2+2 = 15+topDictLen
        // TopDict: encode charStringsOffset as integer + byte 0x11
        // If topDictLen=2 then charStringsOffset=17 → 17+139=156=0x9C
        byte[] topDictBody = { 0x9C, 0x11 }; // integer 17, op 17
        var topDictIndex = BuildIndex(new[] { topDictBody });
        var nameIndex = new byte[] { 0x00, 0x00 };
        var strIndex  = new byte[] { 0x00, 0x00 };
        var gsubrIdx  = new byte[] { 0x00, 0x00 };
        // header
        var header = new byte[] { 0x01, 0x00, 0x04, 0x01 };
        var result = new byte[header.Length + nameIndex.Length + topDictIndex.Length
                              + strIndex.Length + gsubrIdx.Length + csIndex.Length];
        int p = 0;
        Array.Copy(header,      0, result, p, header.Length);      p += header.Length;
        Array.Copy(nameIndex,   0, result, p, nameIndex.Length);   p += nameIndex.Length;
        Array.Copy(topDictIndex,0, result, p, topDictIndex.Length);p += topDictIndex.Length;
        Array.Copy(strIndex,    0, result, p, strIndex.Length);    p += strIndex.Length;
        Array.Copy(gsubrIdx,    0, result, p, gsubrIdx.Length);    p += gsubrIdx.Length;
        Array.Copy(csIndex,     0, result, p, csIndex.Length);
        return result;
    }

    private static byte[] BuildIndex(IList<byte[]> items)
    {
        if (items.Count == 0) return new byte[] { 0x00, 0x00 };
        int totalData = 0;
        foreach (var item in items) totalData += item.Length;
        var result = new byte[2 + 1 + (items.Count + 1) + totalData]; // count(2)+offSize(1)+offsets+data
        result[0] = 0x00; result[1] = (byte)items.Count; // count
        result[2] = 0x01; // offSize=1
        int off = 1;
        for (int i = 0; i < items.Count; i++) { result[3 + i] = (byte)off; off += items[i].Length; }
        result[3 + items.Count] = (byte)off;
        int dataPos = 3 + items.Count + 1;
        foreach (var item in items) { Array.Copy(item, 0, result, dataPos, item.Length); dataPos += item.Length; }
        return result;
    }
}
