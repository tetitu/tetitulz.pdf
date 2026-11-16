// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The WOFF readers, batch 1: the WOFF 1.0 decoder's refusals and directory hints, and the WOFF 2.0
/// reader's two integer encodings, each pinned where the sweep found it untested.
/// </summary>
public class WoffReadersBatch1Tests
{
    private const int Directory = 44, Entry = 20;

    private static byte[] Woff() => MinimalWoff.Wrap(MinimalTrueType.Build("ABC"));

    private static void SetU32(byte[] d, int at, uint v)
    {
        d[at] = (byte)(v >> 24); d[at + 1] = (byte)(v >> 16); d[at + 2] = (byte)(v >> 8); d[at + 3] = (byte)v;
    }

    private static uint U32(byte[] d, int at) => ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];

    // ---------------------------------------------------------------- WOFF 1.0

    /// <summary>
    /// Malformed directory: a compressed table whose declared original length is smaller than its
    /// compressed length (WOFF 1.0 §4 forbids it). It is refused; inflating it anyway accepted a
    /// container no conforming producer writes. The first entry is compressed by the builder.
    /// </summary>
    [Fact]
    public void Malformed_ACompressedLengthAboveTheOriginal_IsRefused()
    {
        byte[] woff = Woff();
        uint compLength = U32(woff, Directory + 8);
        Assert.True(compLength < U32(woff, Directory + 12), "the fixture's first table must be compressed");

        SetU32(woff, Directory + 12, compLength - 1);

        Assert.Null(WoffDecoder.Decode(woff));
    }

    /// <summary>
    /// Hostile directory: a stored table declaring 32 MB in a container of a few hundred bytes. It is
    /// refused before anything is allocated; without the bound the 32 MB copy buffer was allocated
    /// and only then did the copy fail.
    /// </summary>
    [Fact]
    public void Hostile_AStoredTablePastTheContainer_AllocatesNothing()
    {
        byte[] woff = Woff();
        const uint huge = 32u * 1024 * 1024;
        int stored = Directory + Entry;                         // the builder stores every second table
        SetU32(woff, stored + 8, huge);
        SetU32(woff, stored + 12, huge);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] sfnt = WoffDecoder.Decode(woff);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(sfnt);
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes");
    }

    /// <summary>
    /// Hostile container: 65 MB, one stored table filling it, past the 64 MB sfnt budget. It is
    /// refused before the copy; without the check the table was copied whole first. The container is
    /// real bytes, because the budget is a constant and only that size reaches it.
    /// </summary>
    [Fact]
    public void Hostile_AStoredTableOverTheBudget_IsRefusedBeforeItIsCopied()
    {
        const int size = 65 * 1024 * 1024;
        var woff = new byte[size];
        SetU32(woff, 0, 0x774F4646);                            // 'wOFF'
        SetU32(woff, 4, 0x00010000);
        woff[13] = 1;                                           // numTables 1
        woff[Directory] = (byte)'g'; woff[Directory + 1] = (byte)'l'; woff[Directory + 2] = (byte)'y'; woff[Directory + 3] = (byte)'f';
        uint length = (uint)(size - 64);
        SetU32(woff, Directory + 4, 64);
        SetU32(woff, Directory + 8, length);
        SetU32(woff, Directory + 12, length);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] sfnt = WoffDecoder.Decode(woff);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(sfnt);
        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes");
    }

    /// <summary>
    /// The reassembled sfnt's binary-search hints follow OpenType §5.1: searchRange is 16 times the
    /// largest power of two not above numTables, entrySelector its log2, rangeShift the remainder.
    /// Nothing in this tree reads them, which is why they went unchecked.
    /// </summary>
    [Fact]
    public void TheReassembledDirectoryCarriesOpenTypesSearchHints()
    {
        byte[] sfnt = WoffDecoder.Decode(Woff());
        Assert.NotNull(sfnt);

        int numTables = (sfnt[4] << 8) | sfnt[5];
        int power = 1, log2 = 0;
        while (power * 2 <= numTables) { power *= 2; log2++; }

        Assert.True(numTables >= 2, "the hints need more than one table to say anything");
        Assert.Equal(power * 16, (sfnt[6] << 8) | sfnt[7]);
        Assert.Equal(log2, (sfnt[8] << 8) | sfnt[9]);
        Assert.Equal(numTables * 16 - power * 16, (sfnt[10] << 8) | sfnt[11]);
    }

    // ---------------------------------------------------------------- WOFF 2.0 integers

    /// <summary>
    /// UIntBase128 (WOFF2 §4.1): a canonical value reads; a leading 0x80 (a redundant zero) and a
    /// value past 32 bits are refused, as the reference decoder refuses them.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x81, 0x00 }, true, 128u)]
    [InlineData(new byte[] { 0x8F, 0xFF, 0xFF, 0xFF, 0x7F }, true, 0xFFFFFFFFu)]
    [InlineData(new byte[] { 0x80, 0x01 }, false, 0u)]
    [InlineData(new byte[] { 0x90, 0x80, 0x80, 0x80, 0x00 }, false, 0u)]
    public void UIntBase128_ReadsCanonicalValuesAndRefusesTheRest(byte[] bytes, bool ok, uint value)
    {
        var reader = new Woff2Reader(bytes);

        Assert.Equal(ok, reader.TryReadBase128(out uint read));
        Assert.Equal(value, read);
    }

    /// <summary>255UInt16 (WOFF2 §4.2): each of the four forms.</summary>
    [Theory]
    [InlineData(new byte[] { 100 }, 100)]
    [InlineData(new byte[] { 255, 10 }, 263)]          // a byte plus 253
    [InlineData(new byte[] { 254, 10 }, 516)]          // a byte plus 506
    [InlineData(new byte[] { 253, 0x12, 0x34 }, 0x1234)]
    public void UInt16Of255_ReadsEachForm(byte[] bytes, int value)
    {
        var reader = new Woff2Reader(bytes);

        Assert.True(reader.TryRead255UInt16(out int read));
        Assert.Equal(value, read);
    }
}
