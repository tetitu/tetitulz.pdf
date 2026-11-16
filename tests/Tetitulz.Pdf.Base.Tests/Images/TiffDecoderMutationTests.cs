// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// The TIFF decoder over mutated files: the page count and each of the first pages answer "not
/// decoded" with a reason, and no mutation leaves the decoder by an exception.
/// </summary>
public class TiffDecoderMutationTests
{
    private static List<byte[]> Seeds() => new List<byte[]>
    {
        TiffDecoderFindingsTests.Tiff(width: 4, bitsPerSample: 8, samples: 1, photometric: 1, strip: new byte[] { 0, 85, 170, 255 }),
        TiffDecoderFindingsTests.Tiff(width: 2, bitsPerSample: 8, samples: 3, photometric: 2, strip: new byte[] { 255, 0, 0, 0, 255, 0 }),
        TiffDecoderFindingsTests.Tiff(width: 8, bitsPerSample: 1, samples: 1, photometric: 0, strip: new byte[] { 0xA5 }),
        TiffDecoderFindingsTests.Tiff(width: 2, bitsPerSample: 8, samples: 1, photometric: 1, strip: new byte[] { 10, 200 },
            extra: (317, 3, 1, 2)),   // a horizontal predictor
    };

    private static int Read(byte[] data)
    {
        int decoded = 0;
        int pages = TiffImageDecoder.CountPages(data);
        for (int p = 0; p < pages && p < 3; p++)
            if (TiffImageDecoder.TryDecodePage(data, p, out _, out _, out _)) decoded++;
        return decoded;
    }

    [Fact]
    public void MutatedTiffs_AreDeclinedByAnAnswer_NeverByAnException_Hostile()
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
        // Potency: many mutations leave a file that still decodes, so the strip paths were reached.
        Assert.True(decoded > rounds / 20, $"only {decoded} of {rounds} mutated files decoded");
    }
}
