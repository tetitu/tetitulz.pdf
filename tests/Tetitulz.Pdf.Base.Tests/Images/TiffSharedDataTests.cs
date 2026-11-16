// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// What a TIFF's fields and strips may cost: a field of an unknown type is dropped rather than
/// allocated at its count, and what one page's strips read together is held to twice the file and
/// the page's raster, an uncompressed strip reading only the rows it supplies.
/// </summary>
public sealed class TiffSharedDataTests
{
    [Fact]
    public void AdversarialFieldOfAnUnknownType_IsDroppedRatherThanAllocatedAtItsCount()
    {
        // StripOffsets of type 13, which TIFF 6.0 does not define: no size, so a count of fifty million
        // "fits" an 86-byte file, and the array of offsets was allocated whole (381 MiB).
        byte[] tiff = OneByOne(stripOffsetsType: 13, stripOffsetsCount: 50_000_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool decoded = TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(decoded);
        Assert.Equal("the page names no strips", TiffImageDecoder.LastError);
        Assert.InRange(allocated, 0, 1L << 20);
    }

    [Fact]
    public void TheSamePageWithALongStripOffsets_Decodes()
    {
        byte[] tiff = OneByOne(stripOffsetsType: 4, stripOffsetsCount: 1);

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out int w, out int h));
        Assert.Equal((1, 1), (w, h));
        Assert.Equal(0x42, rgb[0]);
    }

    [Fact]
    public void AdversarialUncompressedStripsSharingTheWholeData_ReadOnlyTheirOwnRows()
    {
        // 16 000 one-row strips, each naming the whole 128 000-byte data region: every strip copied the
        // region whole, 1.95 GiB for a 256 KB file, quadratic in its length. A strip now reads its row.
        const int n = 16_000;
        var data = new byte[8 * n];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
        byte[] tiff = Strips(width: 8, height: n, compression: 1, rowsPerStrip: 1,
            data, Enumerable.Repeat(0, n).ToArray(), Enumerable.Repeat(data.Length, n).ToArray());

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool decoded = TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out int w, out int h);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(decoded, TiffImageDecoder.LastError);
        Assert.Equal((8, n), (w, h));
        // Every row is the region's first row: each strip starts at the same byte.
        Assert.Equal(data[5], rgb[(1000 * 8 + 5) * 3]);
        Assert.InRange(allocated, 0, 64L << 20);
    }

    [Fact]
    public void AdversarialCompressedStripsEachNamingAMegabyte_AreRefusedPastTheBudget()
    {
        // PackBits strips: one literal byte, the end-of-data code, then a megabyte the decoder never
        // reads — but each strip's range is copied whole first. A hundred strips naming the same
        // megabyte ask for a hundred megabytes of a one-megabyte file.
        byte[] tiff = PackBitsStripsNamingOneRange(strips: 100, rangeBytes: 1 << 20);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool decoded = TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(decoded);
        Assert.StartsWith("the page's strips read more than twice the file's bytes", TiffImageDecoder.LastError);
        Assert.InRange(allocated, 0, 8L << 20);
    }

    [Fact]
    public void TwoCompressedStripsNamingOneRange_AreWithinTheBudgetAndDecode()
    {
        byte[] tiff = PackBitsStripsNamingOneRange(strips: 2, rangeBytes: 1 << 20);

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out int w, out int h), TiffImageDecoder.LastError);
        Assert.Equal((1, 2), (w, h));
        Assert.Equal(0x42, rgb[3]);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A 1 × 1 grey page whose one-byte strip (0x42) is named by a StripOffsets of the given type and count.</summary>
    private static byte[] OneByOne(int stripOffsetsType, int stripOffsetsCount)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(new byte[] { 0x49, 0x49, 42, 0 });
        w.Write(8);
        var tags = new List<(int Tag, int Type, int Count, int Value)>
        {
            (256, 3, 1, 1), (257, 3, 1, 1), (258, 3, 1, 8), (262, 3, 1, 1),
            (273, stripOffsetsType, stripOffsetsCount, 0), (278, 3, 1, 1), (279, 4, 1, 1),
        };
        w.Write((ushort)tags.Count);
        int valueAt = 8 + 2 + tags.Count * 12 + 4;
        // One LONG fits the field, so it is the strip's offset itself; any other count points at it.
        bool inline = stripOffsetsType == 4 && stripOffsetsCount == 1;
        foreach (var (tag, type, count, value) in tags)
        {
            w.Write((ushort)tag); w.Write((ushort)type); w.Write(count);
            w.Write(tag == 273 ? (inline ? valueAt + 4 : valueAt) : value);
        }
        w.Write(0);
        w.Write(valueAt + 4);   // the strip offset, for a field that points at it; then the strip
        w.Write((byte)0x42);
        return ms.ToArray();
    }

    /// <summary>A grey 8-bit page whose strips are <paramref name="offsets"/> and <paramref name="counts"/> into <paramref name="data"/>.</summary>
    private static byte[] Strips(int width, int height, int compression, int rowsPerStrip, byte[] data, int[] offsets, int[] counts)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        const int dataAt = 8;
        w.Write(new byte[] { 0x49, 0x49, 42, 0 });
        int tablesAt = dataAt + data.Length;
        int ifdAt = tablesAt + 8 * offsets.Length;
        w.Write(ifdAt);
        w.Write(data);
        foreach (int o in offsets) w.Write(dataAt + o);
        foreach (int c in counts) w.Write(c);
        var tags = new List<(int Tag, int Type, int Count, int Value)>
        {
            (256, 4, 1, width), (257, 4, 1, height), (258, 3, 1, 8), (259, 3, 1, compression),
            (262, 3, 1, 1), (273, 4, offsets.Length, tablesAt), (277, 3, 1, 1),
            (278, 4, 1, rowsPerStrip), (279, 4, counts.Length, tablesAt + 4 * offsets.Length),
        };
        w.Write((ushort)tags.Count);
        foreach (var (tag, type, count, value) in tags)
        {
            w.Write((ushort)tag); w.Write((ushort)type); w.Write(count); w.Write(value);
        }
        w.Write(0);
        return ms.ToArray();
    }

    /// <summary>One-pixel-wide rows, each a PackBits strip naming the same range: a literal 0x42, end of data, then filler.</summary>
    private static byte[] PackBitsStripsNamingOneRange(int strips, int rangeBytes)
    {
        var range = new byte[rangeBytes];
        range[0] = 0x00;     // a literal run of one byte
        range[1] = 0x42;
        range[2] = 0x80;     // end of data: the rest is never decoded
        return Strips(width: 1, height: strips, compression: 32773, rowsPerStrip: 1,
            range, new int[strips], Enumerable.Repeat(rangeBytes, strips).ToArray());
    }
}
