// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The encoder's workers (<see cref="JpxParallel"/>): the tiles, a tile's components and every code-block are
/// shared out, and the codestream is the same at any degree — each item writes only its own slot.
/// </summary>
public sealed class JpxEncoderParallelTests
{
    private static byte[] Encode(int[][] planes, int width, int height, bool lossless, JpxEncodeOptions options) =>
        lossless ? JpxEncoder.EncodeLossless(planes, width, height, 8, false, options)
                 : JpxEncoder.EncodeLossy(planes, width, height, 8, false, options);

    /// <summary>
    /// Every coder and allocation — Part 1 and HT, lossless and lossy, with and without a size target, three layers,
    /// one tile and several — writes the same bytes on one worker, on three and on eight; and the image is coded,
    /// a control that the encodes are not trivially equal.
    /// </summary>
    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, true)]
    public void TheCodestream_IsTheSameAtEveryDegree(bool ht, bool lossless, bool target, bool tiled)
    {
        const int width = 70, height = 52;
        int[][] planes = JpxEncoderTestImages.Pattern(width, height, 3, 5);
        JpxEncodeOptions Options(int degree) => new JpxEncodeOptions
        {
            HighThroughput = ht, Levels = 3, Layers = 3, CodeBlockWidthExponent = 3, CodeBlockHeightExponent = 3,
            QuantizationStep = 2, TargetBytes = target ? 2500 : 0,
            TileWidth = tiled ? 32 : 0, TileHeight = tiled ? 24 : 0,
            Workers = new JpxParallel(degree),
        };
        byte[] one = Encode(planes, width, height, lossless, Options(1));
        Assert.Equal(one, Encode(planes, width, height, lossless, Options(3)));
        Assert.Equal(one, Encode(planes, width, height, lossless, Options(8)));
        Assert.True(JpxDecoder.TryDecode(one, 0, out JpxDecodeResult decoded, out string reason), reason);
        if (lossless && !target) Assert.Equal(planes[1], decoded.ComponentPlane(1, out _));
        else Assert.True(JpxEncoderTestImages.Psnr(planes, Enumerable.Range(0, 3).Select(c => decoded.ComponentPlane(c, out _)).ToArray(), 8) > 15);
    }

    /// <summary>
    /// The block pass really is handed to the workers: one tile of one component leaves the block pass the only loop
    /// with more than one item, and with four workers it is given four; on one worker no loop shares anything.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheBlocks_AreSharedOut(bool ht)
    {
        int[][] grey = JpxEncoderTestImages.Pattern(64, 48, 1, 9);
        var four = new JpxParallel(4);
        JpxEncoder.EncodeLossless(grey, 64, 48, 8, false, new JpxEncodeOptions { HighThroughput = ht, Levels = 2, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4, Workers = four });
        Assert.Equal(4, four.MostWorkers);

        var alone = new JpxParallel(1);
        JpxEncoder.EncodeLossless(grey, 64, 48, 8, false, new JpxEncodeOptions { HighThroughput = ht, Levels = 2, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4, Workers = alone });
        Assert.Equal(0, alone.MostWorkers);
    }

    /// <summary>
    /// The public option reaches the workers' degree: a negative budget is refused where the degree is resolved
    /// (<see cref="JpxParallel.Resolve"/>, whose parameter the refusal names), not read as the default; 1 and the
    /// default write the same file.
    /// </summary>
    [Fact]
    public void MaxThreads_IsAThreadBudget()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 30, 1, 3);
        byte[] samples = planes[0].Select(v => (byte)v).ToArray();
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(samples, 40, 30, 1, 8, new JpxEncodingOptions { MaxThreads = -1 }));
        Assert.Equal("maxThreads", refused.ParamName);
        Assert.Equal(JpxImageEncoding.Encode(samples, 40, 30, 1, 8, new JpxEncodingOptions()),
            JpxImageEncoding.Encode(samples, 40, 30, 1, 8, new JpxEncodingOptions { MaxThreads = 1 }));
    }
}
