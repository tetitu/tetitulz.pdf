// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// The GIF and BMP decoders and the header inspector over mutated files. An image is whatever a
/// caller's user uploaded, and each of these answers "not decoded" with a reason: no mutation
/// leaves one of them by an exception.
/// </summary>
public class RasterDecoderMutationTests
{
    private static List<byte[]> Seeds()
    {
        int[] indices = { 0, 1, 2, 3, 3, 2, 1, 0, 0, 0, 1, 1, 2, 2, 3, 3 };
        byte[][] rows =
        {
            new byte[] { 255, 0, 0 }, new byte[] { 0, 255, 0 },
            new byte[] { 0, 0, 255 }, new byte[] { 255, 255, 255 },
        };
        return new List<byte[]>
        {
            GifBmpDecoderTests.MakeGif(4, 4, indices),
            GifBmpDecoderTests.MakeGif(4, 4, indices, transparentIndex: 1, interlaced: true),
            GifBmpDecoderTests.MakeGif(4, 4, indices, extraFrame: true),
            GifBmpDecoderTests.MakeBmp24(2, 2, rows),
            GifBmpDecoderTests.MakeBmp24(2, 2, rows, topDown: true),
            GifBmpDecoderTests.MakeBmp8(2, 2, new byte[] { 0, 1, 2, 3 }),
        };
    }

    private static int Read(byte[] data)
    {
        int decoded = 0;
        if (GifImageDecoder.TryDecode(data, out _, out _, out _, out _, out _)) decoded++;
        if (BmpImageDecoder.TryDecode(data, out _, out _, out _, out _)) decoded++;
        if (PngImageDecoder.TryDecode(data, out _)) decoded++;
        RasterImageInspector.TryInspect(data, out _);
        return decoded;
    }

    [Fact]
    public void MutatedImages_AreDeclinedByAnAnswer_NeverByAnException_Hostile()
    {
        int rounds = int.TryParse(Environment.GetEnvironmentVariable("TETITULZ_MUTATION_ROUNDS"), out int asked) ? asked : 6000;
        List<byte[]> seeds = Seeds();
        var random = new Random(20261114);

        int whole = 0;
        foreach (byte[] seed in seeds) whole += Read(seed);
        Assert.Equal(seeds.Count, whole);

        int decoded = 0;
        string failure = null;
        for (int round = 0; round < rounds && failure == null; round++)
        {
            byte[] data = ByteMutator.Mutate(seeds[round % seeds.Count], random);
            try { decoded += Read(data); }
            catch (Exception ex)
            {
                failure = $"round {round} (seed {round % seeds.Count}, {data.Length} bytes): {ex.GetType().Name}: {ex.Message}\n"
                          + Convert.ToBase64String(data) + "\n" + ex.StackTrace;
            }
        }

        Assert.Null(failure);
        // Potency: many mutations leave a file that still decodes, so the pixel paths were reached.
        Assert.True(decoded > rounds / 20, $"only {decoded} of {rounds} mutated images decoded");
    }
}
