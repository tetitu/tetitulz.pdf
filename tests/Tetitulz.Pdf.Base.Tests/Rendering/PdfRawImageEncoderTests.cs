// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfRawImageEncoder"/>: raw grey or RGB samples to a PNG, through the one encoder, and
/// decoded back by an independent implementation (SkiaSharp's PNG reader).
/// </summary>
public class PdfRawImageEncoderTests
{
    [Fact]
    public void RgbSamplesDecodeToTheSamePixels()
    {
        byte[] samples =
        {
            255, 0, 0,    0, 255, 0,    0, 0, 255,
            10, 20, 30,   40, 50, 60,   70, 80, 90,
        };

        using var bitmap = SKBitmap.Decode(PdfRawImageEncoder.EncodePng(samples, 3, 2, 3));

        Assert.Equal(3, bitmap.Width);
        Assert.Equal(2, bitmap.Height);
        Assert.Equal(new SKColor(0, 255, 0), bitmap.GetPixel(1, 0));
        Assert.Equal(new SKColor(0, 0, 255), bitmap.GetPixel(2, 0));
        Assert.Equal(new SKColor(10, 20, 30), bitmap.GetPixel(0, 1));
        Assert.Equal(new SKColor(70, 80, 90), bitmap.GetPixel(2, 1));
    }

    [Fact]
    public void GreySamplesDecodeToTheSameGreys()
    {
        byte[] samples = { 0, 128, 255, 64 };

        using var bitmap = SKBitmap.Decode(PdfRawImageEncoder.EncodePng(samples, 2, 2, 1));

        Assert.Equal(new SKColor(0, 0, 0), bitmap.GetPixel(0, 0));
        Assert.Equal(new SKColor(128, 128, 128), bitmap.GetPixel(1, 0));
        Assert.Equal(new SKColor(255, 255, 255), bitmap.GetPixel(0, 1));
        Assert.Equal(new SKColor(64, 64, 64), bitmap.GetPixel(1, 1));
    }

    /// <summary>
    /// Each refusal for its own reason, beside a call one step inside the limit that is accepted.
    /// </summary>
    [Fact]
    public void ARefusalIsForTheReasonItNames()
    {
        byte[] four = { 1, 2, 3, 4 };

        Assert.Throws<ArgumentNullException>(() => PdfRawImageEncoder.EncodePng((byte[])null, 1, 1, 1));
        Assert.Equal("components",
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfRawImageEncoder.EncodePng(four, 1, 1, 2)).ParamName);
        Assert.Equal("width",
            Assert.Throws<ArgumentOutOfRangeException>(() => PdfRawImageEncoder.EncodePng(four, 0, 1, 1)).ParamName);
        Assert.Equal("samples",
            Assert.Throws<ArgumentException>(() => PdfRawImageEncoder.EncodePng(four, 5, 1, 1)).ParamName);

        using var accepted = SKBitmap.Decode(PdfRawImageEncoder.EncodePng(four, 4, 1, 1));
        Assert.Equal(new SKColor(4, 4, 4), accepted.GetPixel(3, 0));
    }
}
