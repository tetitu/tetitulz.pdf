// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// <see cref="JpegSamples.TryDecode(byte[], int, long, int, int, out int, out int, out int)"/> with a
/// target decodes below the frame at the DCT scale <see cref="ImageDownsampling.CodecSize"/> chooses:
/// the headroom above the target kept, the target covered, the samples still the image's.
/// </summary>
public sealed class JpegSamplesReducedDecodeTests
{
    private static byte[] Jpeg(int width, int height, SKColor colour, SKColorType type = SKColorType.Rgba8888)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, type, SKAlphaType.Opaque));
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }

    [Fact]
    public void A_target_far_below_the_frame_decodes_it_reduced_with_the_headroom_kept()
    {
        byte[] jpeg = Jpeg(2000, 1500, new SKColor(200, 40, 90));

        byte[] samples = JpegSamples.TryDecode(jpeg, 10_000, 100_000_000, 200, 150,
            out int width, out int height, out int components);

        Assert.NotNull(samples);
        Assert.Equal(3, components);
        Assert.True(width < 2000 && height < 1500, $"decoded at {width}x{height}, the frame's size");
        Assert.True(width >= ImageDownsampling.Wanted(200), $"{width} keeps less than the headroom over 200");
        Assert.True(height >= 150);
        Assert.Equal((long)width * height * 3, samples.Length);
        // The samples are still the image's: the solid colour, to the codec's rounding.
        int mid = (height / 2 * width + width / 2) * 3;
        Assert.InRange(samples[mid], 196, 204);
        Assert.InRange(samples[mid + 1], 36, 44);
        Assert.InRange(samples[mid + 2], 86, 94);
    }

    [Fact]
    public void No_target_or_one_the_frame_cannot_cover_with_headroom_decodes_in_full()
    {
        byte[] jpeg = Jpeg(2000, 1500, new SKColor(200, 40, 90));

        JpegSamples.TryDecode(jpeg, 10_000, 100_000_000, out int w0, out int h0, out _);
        Assert.Equal((2000, 1500), (w0, h0));

        JpegSamples.TryDecode(jpeg, 10_000, 100_000_000, 0, 0, out int w1, out int h1, out _);
        Assert.Equal((2000, 1500), (w1, h1));

        // 1200 with its 2x headroom is past the frame: nothing to reduce.
        JpegSamples.TryDecode(jpeg, 10_000, 100_000_000, 1200, 900, out int w2, out int h2, out _);
        Assert.Equal((2000, 1500), (w2, h2));
    }

    [Fact]
    public void A_grey_frame_is_reduced_too_and_keeps_one_component()
    {
        byte[] jpeg = Jpeg(1600, 1600, new SKColor(120, 120, 120), SKColorType.Gray8);

        byte[] samples = JpegSamples.TryDecode(jpeg, 10_000, 100_000_000, 100, 100,
            out int width, out int height, out int components);

        Assert.NotNull(samples);
        Assert.Equal(1, components);
        Assert.True(width < 1600 && width >= ImageDownsampling.Wanted(100), $"decoded at {width}x{height}");
        Assert.Equal((long)width * height, samples.Length);
        Assert.InRange(samples[samples.Length / 2], 116, 124);
    }
}
