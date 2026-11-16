// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// A BMP's bits-per-pixel field is judged before the row stride is computed from it.
/// </summary>
/// <remarks>
/// <para>The field is sixteen bits and was read into the stride arithmetic unchecked: with the
/// width at the pixel ceiling (40 million, height 1) and a depth of 65 535, <c>w * bpp</c> wrapped,
/// the truncation check compared a wrapped stride, and the decoder allocated the whole 120 MB image
/// before its per-pixel switch refused the depth. The refusal now comes first and names the depth —
/// which is how this test tells the early check from the late one, since both answer false.</para>
/// </remarks>
public class BmpDepthGuardTests
{
    [Theory]
    [InlineData(65535)]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(64)]
    public void AnInventedDepth_IsRefusedByName_BeforeTheStrideIsComputed(int bpp)
    {
        byte[] bmp = Header(width: 40_000_000, height: 1, bpp: bpp, pixelBytes: 8);
        Assert.False(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out _, out _, out string reason));
        Assert.Null(rgb);
        Assert.Contains("bits per pixel", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealDepth_StillDecodes()
    {
        // The control: a 1 × 1 24-bpp pixel (B, G, R = 10, 20, 30, padded to four bytes) decodes to RGB.
        byte[] bmp = Header(width: 1, height: 1, bpp: 24, pixelBytes: 4);
        bmp[54] = 10; bmp[55] = 20; bmp[56] = 30;
        Assert.True(BmpImageDecoder.TryDecode(bmp, out byte[] rgb, out int w, out int h, out string reason), reason);
        Assert.Equal((1, 1), (w, h));
        Assert.Equal(new byte[] { 30, 20, 10 }, rgb);
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
