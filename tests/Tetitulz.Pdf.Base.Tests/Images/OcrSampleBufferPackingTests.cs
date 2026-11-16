// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// How <c>OcrSampleBuffer.TryPack</c> reads a rendered page: through the bitmap's buffer a row at a
/// time (<c>BitmapPixels.Read</c>), where it used to call <c>GetPixel</c> once per pixel. On a 300
/// dpi letter page that measured about 120 ms against 35 ms, the same 25 MB output either way.
/// </summary>
/// <remarks>
/// The facade tests (<see cref="OcrSampleBufferTests"/>) pin what a plugin is handed; these pin
/// that the faster read hands it the same bytes on an opaque page, where the difference it does have
/// lies, and which route ran — the two routes agree on an opaque page, so only a liveness probe can
/// say the buffer was read at all.
/// </remarks>
public class OcrSampleBufferPackingTests
{
    /// <summary>
    /// An opaque page packs exactly as <c>GetPixel</c> reads it, in either 8888 byte order, every
    /// pixel — and through the buffer, with no fallback.
    /// </summary>
    [Theory]
    [InlineData(SKColorType.Rgba8888)]
    [InlineData(SKColorType.Bgra8888)]
    public void AnOpaquePage_PacksAsGetPixelReadsIt_ThroughTheBuffer(SKColorType colorType)
    {
        using var bitmap = Gradient(colorType);

        BitmapPixels.ResetCounters();
        byte[] packed = OcrSampleBuffer.TryPack(bitmap, out long required);

        Assert.Equal(1, BitmapPixels.BufferWalks);
        Assert.Equal(0, BitmapPixels.PropertyFallbacks);
        Assert.Equal(required, packed.LongLength);
        Assert.Equal(ByGetPixel(bitmap), packed);
    }

    /// <summary>
    /// A pixel that is neither opaque nor invisible is un-premultiplied through Skia's integer table:
    /// stored red 7 at alpha 14 reads 127, where <c>GetPixel</c>, rounding in float, reads 128 — the
    /// one-level residue <c>StraightPixelLayout</c> documents. Both are pinned, so a SkiaSharp upgrade
    /// that moves either is loud.
    /// </summary>
    [Fact]
    public void APartlyTransparentPixel_IsUnpremultipliedByTheIntegerTable()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul));
        var span = bitmap.GetPixelSpan();
        span[0] = 7; span[1] = 11; span[2] = 0; span[3] = 14;

        Assert.Equal(new byte[] { 127, 200, 0 }, OcrSampleBuffer.TryPack(bitmap, out _));
        SKColor viaGetPixel = bitmap.GetPixel(0, 0);
        Assert.Equal((128, 200, 0), (viaGetPixel.Red, viaGetPixel.Green, viaGetPixel.Blue));
    }

    /// <summary>
    /// A pixel format the buffer read cannot address (RGB 565) falls back to <c>GetPixel</c>, counted,
    /// and packs the same bytes.
    /// </summary>
    [Fact]
    public void AFormatTheBufferCannotRead_FallsBackToGetPixel()
    {
        using var bitmap = Gradient(SKColorType.Rgb565);

        BitmapPixels.ResetCounters();
        byte[] packed = OcrSampleBuffer.TryPack(bitmap, out _);

        Assert.Equal(0, BitmapPixels.BufferWalks);
        Assert.Equal(1, BitmapPixels.PropertyFallbacks);
        Assert.Equal(ByGetPixel(bitmap), packed);
    }

    /// <summary>A 37 × 23 opaque gradient: every row and column differs, so a row or stride slip shows.</summary>
    private static SKBitmap Gradient(SKColorType colorType)
    {
        var alpha = colorType == SKColorType.Rgb565 ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var bitmap = new SKBitmap(new SKImageInfo(37, 23, colorType, alpha));
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 7), (byte)(y * 11), (byte)((x + y) * 5), 255));
        return bitmap;
    }

    private static byte[] ByGetPixel(SKBitmap bitmap)
    {
        var bytes = new byte[bitmap.Width * bitmap.Height * 3];
        int o = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                SKColor c = bitmap.GetPixel(x, y);
                bytes[o++] = c.Red; bytes[o++] = c.Green; bytes[o++] = c.Blue;
            }
        return bytes;
    }
}
