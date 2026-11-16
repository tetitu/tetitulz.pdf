// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// <c>CffParser</c>'s bounds, batch 1: offsets a DICT or an INDEX declares near
/// <c>int.MaxValue</c>, where an <c>int</c> sum wrapped negative and passed its check.
/// </summary>
public class CffParserBoundsTests
{
    private static readonly byte[] DefaultWidth100 = { 239, 20 };   // 100 defaultWidthX

    // ---------------------------------------------------------------- Private DICT

    /// <summary>
    /// Hostile Top DICT: a Private DICT at offset <c>int.MaxValue</c>. <c>offset + size</c> wrapped
    /// negative, passed <c>&lt;= d.Length</c>, and the copy threw: the whole font was lost over an
    /// entry that only supplies default widths. It is skipped now.
    /// </summary>
    [Fact]
    public void Cff1_APrivateDictPastIntMaxValue_IsSkippedNotFatal()
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(2),
            privateDict: DefaultWidth100, privateOffsetOverride: int.MaxValue);

        CffFont font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.Equal(0f, font.DefaultWidthX);
    }

    /// <summary>The control: the same Private DICT where it really is supplies its width.</summary>
    [Fact]
    public void Cff1_APrivateDictInsideTheData_IsRead()
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(2), privateDict: DefaultWidth100);

        Assert.Equal(100f, CffParser.Parse(cff).DefaultWidthX);
    }

    /// <summary>The CFF2 parser's copy of the same check, with its control.</summary>
    [Theory]
    [InlineData(26, 100f)]
    [InlineData(int.MaxValue, 0f)]
    public void Cff2_APrivateDictPastIntMaxValue_IsSkippedNotFatal(int privateOffset, float defaultWidth)
    {
        var cff = new List<byte> { 2, 0, 5, 0, 13 };              // header, topDictLength 13
        cff.AddRange(new byte[] { 159, 17 });                      // CharStrings at 20
        cff.AddRange(new byte[] { 29, 0, 0, 0, 2 });               // Private size 2
        cff.Add(29);
        cff.AddRange(new[] { (byte)(privateOffset >> 24), (byte)(privateOffset >> 16), (byte)(privateOffset >> 8), (byte)privateOffset });
        cff.Add(18);
        cff.AddRange(new byte[] { 0, 0 });                         // Global Subr INDEX, empty
        cff.AddRange(new byte[] { 0, 1, 1, 1, 2, 14 });            // CharStrings: one endchar
        cff.AddRange(DefaultWidth100);                             // the real Private DICT, at 26

        CffFont font = CffParser.Parse(cff.ToArray());

        Assert.NotNull(font);
        Assert.True(font.IsCff2);
        Assert.Equal(defaultWidth, font.DefaultWidthX);
    }

    /// <summary>The CID-keyed parser's copy, in a Font DICT.</summary>
    [Fact]
    public void Cid_AFontDictPrivatePastIntMaxValue_IsSkippedNotFatal()
    {
        CffFont font = CffParser.Parse(MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, privateOffsetOverride: int.MaxValue));

        Assert.NotNull(font);
        Assert.Equal(2, font.Charstrings.Count);
    }

    // ---------------------------------------------------------------- Encoding

    /// <summary>
    /// Hostile Top DICT: an Encoding at <c>int.MaxValue</c>. <c>offset + 1 &lt; d.Length</c> wrapped
    /// and passed, the read threw, and the font was lost. The encoding is ignored now and the
    /// identity map stands in, as it does for any encoding that maps nothing.
    /// </summary>
    [Fact]
    public void Cff1_AnEncodingPastIntMaxValue_IsIgnoredNotFatal()
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.BoxGlyphs(2), encodingOffsetOverride: int.MaxValue);

        CffFont font = CffParser.Parse(cff);

        Assert.NotNull(font);
        Assert.Equal(1, font.CharCodeToGid[1]);
    }

    // ---------------------------------------------------------------- FDSelect

    /// <summary>
    /// FDSelect gives every glyph FD 1 of a one-entry FDArray. The per-glyph branch clamped such an
    /// index; the shared branch, taken when every glyph agrees, indexed with it and threw, so the
    /// font was lost. Both clamp now, in one place.
    /// </summary>
    [Fact]
    public void Cid_AnFdIndexPastTheFdArray_ClampsToTheLastFontDict()
    {
        CffFont font = CffParser.Parse(MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, fdIndex: 1));

        Assert.NotNull(font);
        Assert.Equal(2, font.Charstrings.Count);
    }

    /// <summary>
    /// Hostile FDArray: 2 000 Font DICTs naming one Private DICT whose Subrs INDEX holds a 64 KiB item.
    /// Each Font DICT copied the Private DICT and the whole INDEX, every copy kept until the parse ended —
    /// 2 000 × 64 KiB here, 65 535 copies of the program at most. A Private DICT is read once a font.
    /// </summary>
    [Fact]
    public void Cid_FontDictsSharingOnePrivateDict_ReadItOnce_Hostile()
    {
        byte[] cff = MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, fdCount: 2000, localSubr: new byte[64 * 1024]);
        Assert.NotNull(CffParser.Parse(cff));

        long before = GC.GetAllocatedBytesForCurrentThread();
        CffFont font = CffParser.Parse(cff);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(font);
        Assert.InRange(allocated, 0, 16L * 1024 * 1024);
    }

    /// <summary>
    /// Hostile FDArray: 200 Font DICTs naming 200 distinct Private DICT spans, every one naming the one
    /// Subrs INDEX, a 64 KiB item. The INDEX is read once, by its position, whichever Private DICT names it.
    /// </summary>
    [Fact]
    public void Cid_DistinctPrivateDictsNamingOneSubrsIndex_ReadItOnce_Hostile()
    {
        byte[] cff = MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, fdCount: 200, localSubr: new byte[64 * 1024], distinctPrivateSizes: true);
        Assert.NotNull(CffParser.Parse(cff));

        long before = GC.GetAllocatedBytesForCurrentThread();
        CffFont font = CffParser.Parse(cff);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(font);
        Assert.InRange(allocated, 0, 4L * 1024 * 1024);
    }

    /// <summary>
    /// Hostile FDArray: 2 000 Font DICTs each naming a span one byte longer from the one Private DICT on.
    /// Distinct spans are each copied, and their total past the program's length is no font's own — a
    /// font's Private DICTs never overlap — so the font is refused. The control names one 136-byte span
    /// 2 000 times: read once, it is within the bound that 2 000 copies would cross.
    /// </summary>
    [Fact]
    public void Cid_FontDictsNamingOverlappingPrivateSpans_AreRefused_Hostile()
    {
        Assert.Null(CffParser.Parse(MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, fdCount: 2000, localSubr: new byte[64], distinctPrivateSizes: true)));

        byte[] shared = MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, fdCount: 2000, localSubr: new byte[64], privatePadding: 128);
        Assert.True(2000L * 136 > shared.Length);
        Assert.NotNull(CffParser.Parse(shared));
    }

    /// <summary>
    /// Hostile FDArray: 200 Font DICTs, each with its own Private DICT naming its own Subrs INDEX — 200
    /// INDEX headers 11 bytes apart in one block, every item running to the block's end, so 200 distinct
    /// INDEXes at distinct offsets share one 64 KiB of bytes and their items sum to about 200 copies of
    /// it. A font's own Subrs INDEXes never overlap, so distinct ones are bounded by the program's
    /// length and the font is refused. The control is the same block with one header: one INDEX, read.
    /// </summary>
    [Fact]
    public void Cid_OverlappingSubrsIndexesAtDistinctOffsets_AreBoundedByTheProgramLength_Hostile()
    {
        byte[] hostile = MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, fdCount: 200, localSubr: new byte[64 * 1024], overlappingSubrsHeaders: 200);
        Assert.True(200L * 64 * 1024 > hostile.Length);
        Assert.Null(CffParser.Parse(hostile));
        Assert.Equal(CffParser.CffFailure.Malformed, CffParser.LastFailureReason);

        byte[] one = MinimalOpenTypeCff.BuildCidKeyed(new[] { 5 }, fdCount: 1, localSubr: new byte[64 * 1024], overlappingSubrsHeaders: 1);
        CffFont font = CffParser.Parse(one);
        Assert.NotNull(font);
        Assert.Equal(2, font.Charstrings.Count);
    }

    /// <summary>An FDSelect format 3 table: its ranges, then its sentinel.</summary>
    private static byte[] FdSelect3(params (int First, int Fd)[] ranges)
    {
        var bytes = new List<byte> { 3, (byte)((ranges.Length - 1) >> 8), (byte)(ranges.Length - 1) };
        for (int i = 0; i < ranges.Length; i++)
        {
            bytes.Add((byte)(ranges[i].First >> 8));
            bytes.Add((byte)ranges[i].First);
            if (i < ranges.Length - 1) bytes.Add((byte)ranges[i].Fd);
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// Hostile FDSelect: 2 000 format 3 ranges alternating between glyph 0 and the last glyph. Each one
    /// refilled the map from 0, a pass over the glyphs a range — 65 535 of them over a 65 535-glyph font
    /// is two billion stores a parse. The ranges ascend or the table ends, so the stores stay within
    /// the glyph count.
    /// </summary>
    [Fact]
    public void Cid_AnFdSelectWhoseRangesDoNotAscend_StoresNoMoreThanTheGlyphCount_Hostile()
    {
        const int glyphs = 1000;
        var ranges = new List<(int, int)>();
        for (int i = 0; i < 2000; i++) ranges.Add((i % 2 == 0 ? 0 : glyphs, 0));
        ranges.Add((glyphs, 0));

        long stores = CffParser.ParseFdSelect(FdSelect3(ranges.ToArray()), 0, glyphs, new int[glyphs]);

        Assert.InRange(stores, 0, glyphs);
    }

    /// <summary>The control: ranges that ascend fill every glyph once, each with its range's FD.</summary>
    [Fact]
    public void Cid_AnFdSelectWhoseRangesAscend_FillsEachGlyphOnce()
    {
        var map = new int[1000];

        long stores = CffParser.ParseFdSelect(FdSelect3((0, 0), (500, 1), (1000, 0)), 0, 1000, map);

        Assert.Equal(1000, stores);
        Assert.Equal(0, map[499]);
        Assert.Equal(1, map[500]);
        Assert.Equal(1, map[999]);
    }

    // ---------------------------------------------------------------- INDEX

    /// <summary>
    /// An INDEX sought at <c>int.MaxValue - 1</c>: <c>pos + 2 &gt; d.Length</c> wrapped and passed,
    /// and the count read threw. <c>CffSubsetter</c> reaches this method outside <c>Parse</c>'s catch.
    /// </summary>
    [Fact]
    public void TryLocateIndex_APositionNearIntMaxValue_IsRefusedNotThrown()
    {
        Assert.False(CffParser.TryLocateIndex(new byte[16], int.MaxValue - 1, out _, out _, out _));
    }

    /// <summary>
    /// An item whose offsets are 1 and <c>0x7FFFFFFF</c>: <c>start + length</c> wrapped negative,
    /// passed, and the copying reader allocated a 2 GB item before the copy failed. The item is out
    /// of range now, with a control whose item is real.
    /// </summary>
    [Theory]
    [InlineData(0x7FFFFFFF, -1, 0)]
    [InlineData(2, 11, 1)]
    public void TryLocateIndex_AnItemPastIntMaxValue_IsOutOfRange(int lastOffset, int start, int length)
    {
        byte[] index =
        {
            0, 1, 4, 0, 0, 0, 1,
            (byte)(lastOffset >> 24), (byte)(lastOffset >> 16), (byte)(lastOffset >> 8), (byte)lastOffset,
            0x0E,
        };

        Assert.True(CffParser.TryLocateIndex(index, 0, out int[] starts, out int[] lengths, out _));
        Assert.Equal(start, starts[0]);
        Assert.Equal(length, lengths[0]);
    }

    /// <summary>
    /// A Name INDEX whose four-byte last offset has its top bit set. As an <c>int</c> it was
    /// negative, so skipping the INDEX moved <i>backwards</i> — here onto a decoy Top DICT INDEX in the
    /// header's padding, which the walk then read as the font's. It is past the data now, and the
    /// walk stops there.
    /// </summary>
    [Fact]
    public void SkipIndex_ALastOffsetWithItsTopBitSet_DoesNotWalkBackwards()
    {
        byte[] cff = HeaderWithDecoy(nameIndexLastOffset: unchecked((int)0xFFFFFF9C), realTopDict: false);

        Assert.False(CffParser.TryLocateCharStrings(cff, out _, out _, out _, out string reason));
        Assert.Equal("the Top DICT INDEX could not be read", reason);
    }

    /// <summary>The control: an honest Name INDEX followed by the same Top DICT is walked.</summary>
    [Fact]
    public void SkipIndex_AnHonestNameIndex_IsWalked()
    {
        byte[] cff = HeaderWithDecoy(nameIndexLastOffset: 1, realTopDict: true);

        Assert.True(CffParser.TryLocateCharStrings(cff, out int[] starts, out _, out int indexOffset, out string reason), reason);
        Assert.Equal(40, indexOffset);
        Assert.Single(starts);
    }

    /// <summary>
    /// A CFF1 whose header is 100 bytes. The padding holds a Top DICT INDEX at 10 naming a
    /// CharStrings INDEX at 40. The Name INDEX sits at 100 with one item; its offsets end at 111.
    /// <paramref name="realTopDict"/> appends a copy of the Top DICT INDEX there.
    /// </summary>
    private static byte[] HeaderWithDecoy(int nameIndexLastOffset, bool realTopDict)
    {
        byte[] topDictIndex = { 0, 1, 1, 1, 7, 29, 0, 0, 0, 40, 17 };

        var cff = new byte[111];
        cff[0] = 1; cff[2] = 100; cff[3] = 1;
        topDictIndex.CopyTo(cff, 10);
        new byte[] { 0, 1, 1, 1, 2, 14 }.CopyTo(cff, 40);
        new byte[]
        {
            0, 1, 4, 0, 0, 0, 1,
            (byte)(nameIndexLastOffset >> 24), (byte)(nameIndexLastOffset >> 16),
            (byte)(nameIndexLastOffset >> 8), (byte)nameIndexLastOffset,
        }.CopyTo(cff, 100);

        if (!realTopDict) return cff;
        var withTop = new byte[cff.Length + topDictIndex.Length];
        cff.CopyTo(withTop, 0);
        topDictIndex.CopyTo(withTop, cff.Length);
        return withTop;
    }
}
