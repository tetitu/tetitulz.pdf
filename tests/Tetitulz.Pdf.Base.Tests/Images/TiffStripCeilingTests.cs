// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// A TIFF strip is inflated no further than its rows need. It went through each filter at the
/// decompression ceiling, 512 MiB, and every byte past the strip's own rows was thrown away — a strip
/// of a few kilobytes inflating to tens of megabytes for a 100 × 100 page, and strips may share one
/// offset.
/// </summary>
public sealed class TiffStripCeilingTests
{
    [Fact]
    public void AdversarialStripInflatingPastItsRows_IsRefusedWithoutInflatingItWhole()
    {
        byte[] bomb = FlateEncodeFilter.Encode(new byte[20 * 1024 * 1024]);   // 20 MB of zeros, ~20 KB
        byte[] tiff = GreyDeflateTiff(100, 100, bomb);

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool decoded = TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(decoded);
        Assert.Contains("could not be decoded", TiffImageDecoder.LastError ?? "", StringComparison.Ordinal);
        Assert.InRange(allocated, 0, 4L * 1024 * 1024);   // was the whole 20 MB and more
    }

    /// <summary>The control: the same page with a strip of exactly its rows decodes, every pixel.</summary>
    [Fact]
    public void AStripOfItsOwnRows_Decodes()
    {
        var rows = new byte[100 * 100];
        for (int i = 0; i < rows.Length; i++) rows[i] = (byte)(i % 251);
        byte[] tiff = GreyDeflateTiff(100, 100, FlateEncodeFilter.Encode(rows));

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out int w, out int h));
        Assert.Equal((100, 100), (w, h));
        Assert.Equal(rows[4321], rgb[4321 * 3]);
    }

    /// <summary>One page, little-endian, 8-bit grey, Deflate (8), one strip of every row.</summary>
    private static byte[] GreyDeflateTiff(int width, int height, byte[] strip)
    {
        const int stripAt = 8;
        var tags = new List<(int Tag, int Type, int Count, int Value)>
        {
            (256, 3, 1, width), (257, 3, 1, height), (258, 3, 1, 8), (259, 3, 1, 8),
            (262, 3, 1, 1), (273, 4, 1, stripAt), (277, 3, 1, 1),
            (278, 3, 1, height), (279, 4, 1, strip.Length),
        };
        var ms = new MemoryStream();
        int ifdAt = stripAt + strip.Length + (strip.Length & 1);
        ms.Write(new byte[] { 0x49, 0x49, 42, 0 });
        ms.Write(BitConverter.GetBytes(ifdAt));
        ms.Write(strip);
        if ((strip.Length & 1) != 0) ms.WriteByte(0);
        ms.Write(BitConverter.GetBytes((ushort)tags.Count));
        foreach (var (tag, type, count, value) in tags)
        {
            ms.Write(BitConverter.GetBytes((ushort)tag));
            ms.Write(BitConverter.GetBytes((ushort)type));
            ms.Write(BitConverter.GetBytes(count));
            ms.Write(BitConverter.GetBytes(value));
        }
        ms.Write(new byte[4]);
        return ms.ToArray();
    }
}
