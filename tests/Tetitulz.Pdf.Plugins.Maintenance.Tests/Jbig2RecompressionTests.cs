// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// JBIG2 re-encoding of bilevel images (<see cref="OptimizationOptions.UseJbig2ForBilevelImages"/>),
/// beside the CCITT pass it shares a method with. As there, the claim is <b>bit-exactness</b>: the
/// rewritten stream is decoded and compared to the original raster, because a smaller wrong image
/// would pass a size assertion.
/// </summary>
public class Jbig2RecompressionTests
{
    private const int Width = 64, Height = 64, Stride = Width / 8;

    /// <summary>A page of repeated glyph boxes: five distinct shapes, forty occurrences.</summary>
    private static byte[] GlyphRaster()
    {
        var raster = new byte[Stride * Height];
        for (int i = 0; i < raster.Length; i++) raster[i] = 0xFF;                 // white (sample 1)
        for (int row = 0; row < 5; row++)
            for (int col = 0; col < 8; col++)
            {
                int x0 = 1 + col * 8, y0 = 2 + row * 12, glyph = (row + col) % 5;
                for (int y = y0; y < y0 + 7; y++)
                    for (int x = x0; x < x0 + 5; x++)
                        if (!(x == x0 + 1 + glyph % 3 && y == y0 + 2 + glyph / 3))   // a hole per glyph
                            raster[y * Stride + x / 8] &= (byte)~(0x80 >> (x % 8));  // black (sample 0)
            }
        return raster;
    }

    /// <summary>Left half black, right half white: one component wider than tall, where G4 is at its best.</summary>
    private static byte[] HalfBlackRaster()
    {
        var raster = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Stride; x++)
                raster[y * Stride + x] = x < Stride / 2 ? (byte)0x00 : (byte)0xFF;
        return raster;
    }

    /// <summary>Pseudo-random pixels from a fixed seed: nothing to predict and nothing to repeat.</summary>
    private static byte[] NoiseRaster()
    {
        var raster = new byte[Stride * Height];
        uint s = 2463534242;
        for (int i = 0; i < raster.Length; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; raster[i] = (byte)s; }
        return raster;
    }

    private static OptimizationOptions Jbig2Only() => new OptimizationOptions
    {
        UseJbig2ForBilevelImages = true,
        CompressStreams = false,
        RemoveDuplicates = false,
        RemoveUnusedObjects = false,
    };

    private static OptimizationOptions BothPasses()
    {
        var o = Jbig2Only();
        o.UseCcittForBilevelImages = true;
        return o;
    }

    [Fact]
    public void Optimize_ReEncodesARepeatedGlyphImageAsJbig2_AndPreservesEverySample()
    {
        byte[] raster = GlyphRaster();
        byte[] pdf = CcittRecompressionTests.BuildPdfWithBilevelImage(raster);

        var result = CcittRecompressionTests.Optimize(pdf, Jbig2Only(), out byte[] optimized);

        Assert.Equal(1, result.BilevelImagesRecompressed);
        Assert.Equal(1, result.BilevelImagesRecompressedAsJbig2);
        Assert.Equal("JBIG2Decode", CcittRecompressionTests.FilterNameOf(optimized));
        Assert.Equal(raster, CcittRecompressionTests.DecodedImageSamples(optimized));
        Assert.True(CcittRecompressionTests.StoredImageLength(optimized) < raster.Length,
            "the JBIG2 stream is not shorter than the raw raster");
    }

    /// <summary>
    /// With both passes on, each image takes the shorter: the glyph page JBIG2, the half-black square
    /// G4 — the two outcomes from one option set, so neither pass can be winning by default.
    /// </summary>
    [Fact]
    public void Optimize_WithBothPasses_KeepsTheShorterOfG4AndJbig2PerImage()
    {
        var glyphs = CcittRecompressionTests.Optimize(
            CcittRecompressionTests.BuildPdfWithBilevelImage(GlyphRaster()), BothPasses(), out byte[] glyphPdf);
        Assert.Equal("JBIG2Decode", CcittRecompressionTests.FilterNameOf(glyphPdf));
        Assert.Equal(1, glyphs.BilevelImagesRecompressedAsJbig2);
        Assert.Equal(GlyphRaster(), CcittRecompressionTests.DecodedImageSamples(glyphPdf));

        var half = CcittRecompressionTests.Optimize(
            CcittRecompressionTests.BuildPdfWithBilevelImage(HalfBlackRaster()), BothPasses(), out byte[] halfPdf);
        Assert.Equal("CCITTFaxDecode", CcittRecompressionTests.FilterNameOf(halfPdf));
        Assert.Equal(1, half.BilevelImagesRecompressed);
        Assert.Equal(0, half.BilevelImagesRecompressedAsJbig2);
        Assert.Equal(HalfBlackRaster(), CcittRecompressionTests.DecodedImageSamples(halfPdf));
    }

    /// <summary>
    /// An ordered-dithered gradient is a dithered picture in miniature: many small components of
    /// many shapes, which the symbol form codes shape by shape and placement by placement, and a
    /// pattern the generic region's context model predicts well — where G4, with a run change every
    /// few pixels, codes worse than Flate. The pass takes the generic JBIG2 form: the stream's second
    /// segment is a generic region (type 39), not a symbol dictionary (type 0).
    /// </summary>
    [Fact]
    public void Optimize_TakesTheGenericJbig2FormWhereSymbolsDoNotPay()
    {
        int[,] bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
        var raster = new byte[Stride * Height];
        for (int i = 0; i < raster.Length; i++) raster[i] = 0xFF;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int level = (x * 16) / Width;                              // a horizontal ramp, 0..15
                if (level > bayer[y % 4, x % 4])
                    raster[y * Stride + x / 8] &= (byte)~(0x80 >> (x % 8));  // black
            }

        var result = CcittRecompressionTests.Optimize(
            CcittRecompressionTests.BuildPdfWithBilevelImage(raster), BothPasses(), out byte[] optimized);

        Assert.Equal(1, result.BilevelImagesRecompressedAsJbig2);
        Assert.Equal("JBIG2Decode", CcittRecompressionTests.FilterNameOf(optimized));
        byte[] stored = CcittRecompressionTests.StoredImageBytes(optimized);
        // Segment 1 is the 11-byte header and 19-byte body of the page information; byte 34 is the
        // type of segment 2.
        Assert.Equal(39, stored[34] & 0x3F);
        Assert.Equal(raster, CcittRecompressionTests.DecodedImageSamples(optimized));
    }

    /// <summary>Noise has no symbols and no structure; neither form shrinks it, and it is left as it is.</summary>
    [Fact]
    public void Optimize_LeavesANoiseImageAlone()
    {
        byte[] raster = NoiseRaster();
        byte[] pdf = CcittRecompressionTests.BuildPdfWithBilevelImage(raster);

        var result = CcittRecompressionTests.Optimize(pdf, BothPasses(), out byte[] optimized);

        Assert.Equal(0, result.BilevelImagesRecompressed);
        Assert.Null(CcittRecompressionTests.FilterNameOf(optimized));
        Assert.Equal(raster, CcittRecompressionTests.DecodedImageSamples(optimized));
    }

    /// <summary>The preset every caller is pointed at takes the glyph page as JBIG2, and the samples survive.</summary>
    [Fact]
    public void Optimize_LosslessPreset_TakesTheGlyphPageAsJbig2()
    {
        byte[] raster = GlyphRaster();
        var result = CcittRecompressionTests.Optimize(
            CcittRecompressionTests.BuildPdfWithBilevelImage(raster), OptimizationOptions.Lossless, out byte[] optimized);

        Assert.Equal(1, result.BilevelImagesRecompressedAsJbig2);
        Assert.Equal("JBIG2Decode", CcittRecompressionTests.FilterNameOf(optimized));
        Assert.Equal(raster, CcittRecompressionTests.DecodedImageSamples(optimized));
    }

    /// <summary>With the option off and CCITT off, a bilevel image is not touched.</summary>
    [Fact]
    public void Optimize_DoesNotTouchBilevelImagesWhenTheJbig2OptionIsOff()
    {
        var options = Jbig2Only();
        options.UseJbig2ForBilevelImages = false;

        var result = CcittRecompressionTests.Optimize(
            CcittRecompressionTests.BuildPdfWithBilevelImage(GlyphRaster()), options, out byte[] optimized);

        Assert.Equal(0, result.BilevelImagesRecompressed);
        Assert.Null(CcittRecompressionTests.FilterNameOf(optimized));
    }
}
