// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Plugins.Ocr;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Ocr.Tests;

/// <summary>
/// The PNG the plugin writes for Tesseract, decoded by an independent implementation.
/// </summary>
/// <remarks>
/// The only route to it in production runs through the native engine, which a test host does not
/// have — so until this test, the writer's chunks, CRCs and zlib framing were checked by nothing.
/// Its zlib framing is now Core's <c>FlateEncodeFilter</c> at <c>NoCompression</c> rather than a
/// hand-written stored-block loop; SkiaSharp's decoder verifies the framing, the Adler-32 and every
/// chunk CRC, and a single wrong byte in any of them fails the decode.
/// </remarks>
public class TesseractPngTests
{
    [Fact]
    public void AnRgbImageDecodesToTheSamePixels()
    {
        byte[] pixels =
        {
            255, 0, 0,    0, 255, 0,    0, 0, 255,
            10, 20, 30,   40, 50, 60,   70, 80, 90,
        };

        using var bitmap = SKBitmap.Decode(TesseractOcrPlugin.RawToPng(pixels, 3, 2, 3));

        Assert.NotNull(bitmap);
        Assert.Equal(3, bitmap.Width);
        Assert.Equal(2, bitmap.Height);
        Assert.Equal(new SKColor(255, 0, 0), bitmap.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(2, 0));
        Assert.Equal(new SKColor(70, 80, 90), bitmap.GetPixel(2, 1));
    }

    [Fact]
    public void AGreyImageDecodesToTheSamePixels()
    {
        byte[] pixels = { 0, 128, 255, 64 };

        using var bitmap = SKBitmap.Decode(TesseractOcrPlugin.RawToPng(pixels, 2, 2, 1));

        Assert.NotNull(bitmap);
        Assert.Equal(new SKColor(128, 128, 128), bitmap.GetPixel(1, 0));
        Assert.Equal(new SKColor(64, 64, 64), bitmap.GetPixel(1, 1));
    }

    /// <summary>
    /// Larger than one stored deflate block (65,535 bytes), so the framing has to split the data —
    /// the part the hand-written loop existed to get right.
    /// </summary>
    [Fact]
    public void AnImageLargerThanOneStoredBlockDecodes()
    {
        const int width = 300, height = 300;   // 300 * (1 + 900) = 270,300 bytes of scanlines
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i % 251);

        using var bitmap = SKBitmap.Decode(TesseractOcrPlugin.RawToPng(pixels, width, height, 3));

        Assert.NotNull(bitmap);
        int last = (width * height - 1) * 3;
        Assert.Equal(new SKColor(pixels[last], pixels[last + 1], pixels[last + 2]),
            bitmap.GetPixel(width - 1, height - 1));
    }
}
