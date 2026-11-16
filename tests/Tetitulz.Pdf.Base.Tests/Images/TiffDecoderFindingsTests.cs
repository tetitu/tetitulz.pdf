// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// The imaging decoders, batch 1: what reading the TIFF decoder found.
/// </summary>
public class TiffDecoderFindingsTests
{
    /// <summary>
    /// FillOrder 2 packs bits least significant first. It was read and never applied: a pixel row
    /// came out mirrored within each byte. Pixel 0 is the one set here.
    /// </summary>
    [Fact]
    public void Tiff_FillOrder2_IsReadLeastSignificantBitFirst()
    {
        byte[] tiff = Tiff(width: 8, bitsPerSample: 1, samples: 1, photometric: 1, strip: new byte[] { 0b0000_0001 },
                           extra: (266, 3, 1, 2));

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out _, out _), TiffImageDecoder.LastError);

        Assert.Equal(255, rgb[0]);
        Assert.Equal(0, rgb[7 * 3]);
    }

    /// <summary>The control: FillOrder 1, the default, reads the same byte most significant first.</summary>
    [Fact]
    public void Tiff_FillOrder1_IsReadMostSignificantBitFirst()
    {
        byte[] tiff = Tiff(width: 8, bitsPerSample: 1, samples: 1, photometric: 1, strip: new byte[] { 0b0000_0001 });

        Assert.True(TiffImageDecoder.TryDecodePage(tiff, 0, out byte[] rgb, out _, out _));

        Assert.Equal(0, rgb[0]);
        Assert.Equal(255, rgb[7 * 3]);
    }

    /// <summary>
    /// A photometric interpretation the expansion does not know is refused by name. Separated CMYK
    /// was read as RGB from C, M and Y, an inverted image with no error.
    /// </summary>
    [Theory]
    [InlineData(5, 4)]   // separated CMYK
    [InlineData(6, 3)]   // YCbCr
    [InlineData(8, 3)]   // CIELab
    public void Tiff_AnUnknownPhotometricIsRefusedByName(int photometric, int samples)
    {
        byte[] tiff = Tiff(width: 1, bitsPerSample: 8, samples: samples, photometric: photometric, strip: new byte[samples]);

        Assert.False(TiffImageDecoder.TryDecodePage(tiff, 0, out _, out _, out _));
        Assert.Contains($"PhotometricInterpretation {photometric} is not decoded", TiffImageDecoder.LastError);
    }

    /// <summary>One page, little-endian, a single strip; <paramref name="extra"/> adds one SHORT tag.</summary>
    internal static byte[] Tiff(int width, int bitsPerSample, int samples, int photometric, byte[] strip,
                               (int Tag, int Type, int Count, int Value)? extra = null)
    {
        const int stripAt = 8;
        var tags = new List<(int Tag, int Type, int Count, int Value)>
        {
            (256, 3, 1, width), (257, 3, 1, 1), (258, 3, 1, bitsPerSample), (259, 3, 1, 1),
            (262, 3, 1, photometric), (273, 4, 1, stripAt), (277, 3, 1, samples),
            (278, 3, 1, 1), (279, 4, 1, strip.Length),
        };
        if (extra.HasValue) tags.Add(extra.Value);
        tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));

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
            ms.Write(BitConverter.GetBytes(value));   // a SHORT sits in the low half on little-endian
        }
        ms.Write(new byte[4]);
        return ms.ToArray();
    }
}
