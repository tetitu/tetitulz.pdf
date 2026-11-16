// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// A BMP's height field is a signed 32-bit integer whose sign says the row order and whose
/// magnitude is the row count. <c>int.MinValue</c> has no magnitude: <c>Math.Abs</c> of it throws
/// <see cref="OverflowException"/>, which is neither answer of a method whose contract is true or
/// false. Both readers of the field — the decoder and the inspector — answer false for it, and both
/// read the magnitude of an ordinary top-down height (the control).
/// </summary>
public class BmpHeightRangeTests
{
    [Fact]
    public void AHeightOfIntMinValue_IsRefusedByTheDecoderAndTheInspectorAlike()
    {
        byte[] bmp = Header(width: 1, height: int.MinValue, bpp: 24, pixelBytes: 4);

        Assert.False(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out _, out _, out string reason));
        Assert.Null(rgb);
        Assert.Contains("int.MinValue", reason, StringComparison.Ordinal);
        Assert.False(RasterImageInspector.TryInspect(bmp, out _));

        // The control: a top-down one-row image is a height of 1 to both.
        byte[] topDown = Header(width: 1, height: -1, bpp: 24, pixelBytes: 4);
        Assert.True(BmpImageDecoder.TryDecode(topDown, out _, out int w, out int h, out string why), why);
        Assert.Equal((1, 1), (w, h));
        Assert.True(RasterImageInspector.TryInspect(topDown, out RasterImageInfo info));
        Assert.Equal((RasterImageFormat.Bmp, 1, 1), (info.Format, info.Width, info.Height));
    }

    /// <summary>A BITMAPINFOHEADER file: 14-byte file header, 40-byte DIB header, then <paramref name="pixelBytes"/> zero bytes.</summary>
    private static byte[] Header(int width, int height, int bpp, int pixelBytes)
    {
        var b = new byte[54 + pixelBytes];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        Le32(b, 2, b.Length);
        Le32(b, 10, 54);          // pixel data offset
        Le32(b, 14, 40);          // DIB header size
        Le32(b, 18, width);
        Le32(b, 22, height);
        b[26] = 1;                // planes
        b[28] = (byte)bpp; b[29] = (byte)(bpp >> 8);
        Le32(b, 30, 0);           // BI_RGB
        return b;
    }

    private static void Le32(byte[] b, int at, int v)
    {
        b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
    }
}
