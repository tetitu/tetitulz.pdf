// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// A TIFF page's cost is its data's, not its header's: a page whose strips cannot fill the rows it declares is
/// refused before its raster is made, and a strip that decodes short ends the page, refused.
/// </summary>
public class TiffDataCoverageTests
{
    /// <summary>
    /// The hostile shape: 4000 × 4000 grey pixels declared, one strip of one byte. Refused from the byte counts,
    /// before a 16 MB raster and a 48 MB conversion of zeros.
    /// </summary>
    [Fact]
    public void AdversarialHugePageOfOneByte_IsRefusedBeforeItsRaster()
    {
        byte[] tiff = Tiff(4000, 4000, rowsPerStrip: 4000, compression: 1, new[] { new byte[1] });

        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _));
        Assert.Contains("hold 1 of the 16000000 bytes its rows need", TiffImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>One strip of one row for a page of a hundred rows covers one of them: refused, saying so.</summary>
    [Fact]
    public void StripsTooFewForThePagesRows_AreRefused()
    {
        byte[] tiff = Tiff(10, 100, rowsPerStrip: 1, compression: 1, new[] { new byte[10] });

        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _));
        Assert.Contains("1 strips of 1 rows cover fewer than its 100 rows", TiffImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compressed strip that inflates to fewer bytes than its rows need is refused, naming the strip; the same
    /// page with the strip's whole rows decodes (the control).
    /// </summary>
    [Theory]
    [InlineData(50, false)]
    [InlineData(100, true)]
    public void ACompressedStripThatDecodesShort_IsRefused(int bytes, bool decodes)
    {
        byte[] tiff = Tiff(10, 10, rowsPerStrip: 10, compression: 8, new[] { Deflate(new byte[bytes]) });

        Assert.Equal(decodes, TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out int w, out int h));
        if (decodes)
        {
            Assert.Equal((10, 10), (w, h));
            Assert.Equal(300, rgb.Length);
        }
        else Assert.Contains("strip 0 holds 50 of the 100 bytes its rows need", TiffImageDecoder.LastError, StringComparison.Ordinal);
    }

    /// <summary>The uncompressed control: a page whose strips hold every row decodes, and says nothing.</summary>
    [Fact]
    public void APageWhoseStripsHoldEveryRow_Decodes()
    {
        byte[] tiff = Tiff(10, 4, rowsPerStrip: 2, compression: 1, new[] { new byte[20], new byte[20] });

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out _, out int h));
        Assert.Equal(4, h);
        Assert.Equal(120, rgb.Length);
        Assert.Null(TiffImageDecoder.LastError);
    }

    private static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>One little-endian page of 8-bit grey (BlackIsZero), its strips written in order after the header.</summary>
    private static byte[] Tiff(int width, int height, int rowsPerStrip, int compression, byte[][] strips)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x49, 0x49, 42, 0, 0, 0, 0, 0 });
        var offsets = new int[strips.Length];
        for (int i = 0; i < strips.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            ms.Write(strips[i]);
            if ((ms.Length & 1) != 0) ms.WriteByte(0);
        }
        int offsetsAt = (int)ms.Length;
        foreach (int o in offsets) ms.Write(BitConverter.GetBytes(o));
        int countsAt = (int)ms.Length;
        foreach (var s in strips) ms.Write(BitConverter.GetBytes(s.Length));

        bool one = strips.Length == 1;
        var tags = new List<(int Tag, int Type, int Count, int Value)>
        {
            (256, 4, 1, width), (257, 4, 1, height), (258, 3, 1, 8), (259, 3, 1, compression), (262, 3, 1, 1),
            (273, 4, strips.Length, one ? offsets[0] : offsetsAt), (277, 3, 1, 1), (278, 4, 1, rowsPerStrip),
            (279, 4, strips.Length, one ? strips[0].Length : countsAt),
        };
        int ifdAt = (int)ms.Length;
        ms.Write(BitConverter.GetBytes((ushort)tags.Count));
        foreach (var (tag, type, count, value) in tags)
        {
            ms.Write(BitConverter.GetBytes((ushort)tag));
            ms.Write(BitConverter.GetBytes((ushort)type));
            ms.Write(BitConverter.GetBytes(count));
            ms.Write(BitConverter.GetBytes(value));
        }
        ms.Write(new byte[4]);
        byte[] bytes = ms.ToArray();
        BitConverter.GetBytes(ifdAt).CopyTo(bytes, 4);
        return bytes;
    }
}
