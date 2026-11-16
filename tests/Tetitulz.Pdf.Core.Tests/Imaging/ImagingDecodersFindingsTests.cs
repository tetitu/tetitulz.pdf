// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// The imaging decoders, batch 1: what reading the PNG, GIF and BMP decoders found (TIFF's are in Base.Tests, beside its decoder).
/// </summary>
public class ImagingDecodersFindingsTests
{
    // ---------------------------------------------------------------- PNG

    /// <summary>
    /// 16-bit colour channels round to the nearest byte, as the greyscale channel already did. They
    /// took each sample's high byte: 0x00FF came out 0 rather than 1, and 0x10FF as 0x10 rather than
    /// 0x11. The colour type and its channel layout are each a row.
    /// </summary>
    [Theory]
    [InlineData(2, new[] { 0x00FF, 0x10FF, 0xFFFF }, new byte[] { 1, 0x11, 255 }, null)]
    [InlineData(6, new[] { 0x00FF, 0x10FF, 0xFFFF, 0x00FF }, new byte[] { 1, 0x11, 255 }, (byte)1)]
    [InlineData(4, new[] { 0x10FF, 0x00FF }, new byte[] { 0x11, 0x11, 0x11 }, (byte)1)]
    public void Png16_ColourChannelsRoundToTheNearestByte(int colourType, int[] samples, byte[] rgb, byte? alpha)
    {
        Assert.True(PngImageDecoder.TryDecode(Png16(colourType, samples), out DecodedPng png), PngImageDecoder.LastError);

        Assert.Equal(rgb, png.Rgb);
        if (alpha.HasValue) Assert.Equal(alpha.Value, png.Alpha[0]);
    }

    /// <summary>The control: at 8 bits a sample is its own byte.</summary>
    [Fact]
    public void Png8_ColourChannelsAreUnchanged()
    {
        Assert.True(PngImageDecoder.TryDecode(Png(2, 8, new byte[] { 0, 1, 0x80, 0xFF }), out DecodedPng png));
        Assert.Equal(new byte[] { 1, 0x80, 0xFF }, png.Rgb);
    }

    private static byte[] Png16(int colourType, int[] samples)
    {
        var row = new List<byte> { 0 };   // filter type None
        foreach (int s in samples) { row.Add((byte)(s >> 8)); row.Add((byte)s); }
        return Png(colourType, 16, row.ToArray());
    }

    /// <summary>A 1x1 PNG of <paramref name="colourType"/> whose one filtered row is
    /// <paramref name="row"/>. CRCs are zero: the decoder does not check them.</summary>
    private static byte[] Png(int colourType, int bitDepth, byte[] row)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            ms.Write(Be32(data.Length));
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]);
        }
        Chunk("IHDR", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, (byte)bitDepth, (byte)colourType, 0, 0, 0 });
        var z = new MemoryStream();
        using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zl.Write(row);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] Be32(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    // ---------------------------------------------------------------- GIF

    /// <summary>
    /// A frame whose pixel data ends early decodes as far as it goes, and now says so. It came back
    /// true with its tail at palette entry 0 and nothing to tell it from a whole image.
    /// </summary>
    [Fact]
    public void Gif_ATruncatedFrame_SaysHowFarItGot()
    {
        Assert.True(GifImageDecoder.TryDecode(Gif(width: 2), out _, out _, out _, out _, out _));
        Assert.Contains("ended after 1 of 2 pixels", GifImageDecoder.LastError);
    }

    /// <summary>The control: the same stream for a one-pixel frame is whole, and says nothing.</summary>
    [Fact]
    public void Gif_AWholeFrame_HasNoError()
    {
        Assert.True(GifImageDecoder.TryDecode(Gif(width: 1), out byte[] rgb, out _, out _, out _, out _));
        Assert.Null(GifImageDecoder.LastError);
        Assert.Equal(new byte[] { 10, 20, 30 }, rgb);
    }

    /// <summary>The slot belongs to the call: a whole frame after a truncated one reads clean.</summary>
    [Fact]
    public void Gif_TheErrorIsClearedByTheNextDecode()
    {
        GifImageDecoder.TryDecode(Gif(width: 2), out _, out _, out _, out _, out _);
        Assert.NotNull(GifImageDecoder.LastError);

        Assert.True(GifImageDecoder.TryDecode(Gif(width: 1), out _, out _, out _, out _, out _));
        Assert.Null(GifImageDecoder.LastError);
    }

    /// <summary>A refusal names its reason; it was a bare <c>false</c>.</summary>
    [Fact]
    public void Gif_ARefusal_NamesItsReason()
    {
        byte[] gif = Gif(width: 1);
        gif[0] = (byte)'X';

        Assert.False(GifImageDecoder.TryDecode(gif, out _, out _, out _, out _, out _));
        Assert.Contains("GIF signature", GifImageDecoder.LastError);
    }

    /// <summary>
    /// A GIF89a with a two-entry global table (entry 0 is 10, 20, 30) and one frame of
    /// <paramref name="width"/> by 1 whose LZW data, at minimum code size 2, is ClearTable (4),
    /// index 0, EndOfData (5), packed least significant bit first: one pixel.
    /// </summary>
    private static byte[] Gif(int width)
    {
        var ms = new MemoryStream();
        ms.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        ms.Write(new byte[] { (byte)width, 0, 1, 0, 0x80, 0, 0 });
        ms.Write(new byte[] { 10, 20, 30, 40, 50, 60 });
        ms.Write(new byte[] { 0x2C, 0, 0, 0, 0, (byte)width, 0, 1, 0, 0 });
        ms.Write(new byte[] { 2, 2, 0x44, 0x01, 0 });
        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    // ---------------------------------------------------------------- BMP

    /// <summary>
    /// Hostile header: a height of int.MinValue, which <c>Math.Abs</c> cannot negate. It threw
    /// OverflowException out of a method whose contract is false with a reason.
    /// </summary>
    [Fact]
    public void Bmp_AnIntMinValueHeight_IsRefusedNotThrown()
    {
        var b = new byte[60];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(1).CopyTo(b, 18);
        BitConverter.GetBytes(int.MinValue).CopyTo(b, 22);
        BitConverter.GetBytes((short)1).CopyTo(b, 26);
        BitConverter.GetBytes((short)24).CopyTo(b, 28);

        Assert.False(BmpImageDecoder.TryDecode(b, out _, out _, out _, out string reason));
        Assert.Contains("int.MinValue", reason);
    }
}
