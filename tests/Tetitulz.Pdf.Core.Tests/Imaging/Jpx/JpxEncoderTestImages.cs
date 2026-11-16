// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>Deterministic images for the encoder tests: a gradient with noise from a linear congruential generator, as the the reference decoder fixtures were made.</summary>
internal static class JpxEncoderTestImages
{
    internal static int[][] Pattern(int width, int height, int components, int seed, int precision = 8)
    {
        var planes = new int[components][];
        int mask = (1 << precision) - 1;
        int scale = precision > 8 ? 1 << (precision - 8) : 1;
        for (int c = 0; c < components; c++)
        {
            var plane = new int[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                    plane[y * width + x] = ((x * (6 + 2 * c) + y * (3 + c) + (seed >> 16) % 17 + 40 * c) * scale + (seed >> 8) % scale) & mask;
                }
            planes[c] = plane;
        }
        return planes;
    }

    /// <summary>The same plane at every component, offset so the three differ.</summary>
    internal static int[][] Flat(int width, int height, int components, int value)
    {
        var planes = new int[components][];
        for (int c = 0; c < components; c++)
        {
            planes[c] = new int[width * height];
            for (int i = 0; i < planes[c].Length; i++) planes[c][i] = value;
        }
        return planes;
    }

    /// <summary>Peak signal-to-noise ratio in dB over every sample of every plane, for samples of <paramref name="precision"/> bits; infinite when equal.</summary>
    internal static double Psnr(int[][] expected, int[][] actual, int precision)
    {
        double sum = 0;
        long count = 0;
        for (int c = 0; c < expected.Length; c++)
            for (int i = 0; i < expected[c].Length; i++)
            {
                double d = expected[c][i] - actual[c][i];
                sum += d * d;
                count++;
            }
        if (sum == 0) return double.PositiveInfinity;
        double peak = (1 << precision) - 1;
        return 10 * Math.Log10(peak * peak / (sum / count));
    }
}
