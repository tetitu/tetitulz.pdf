// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.Intrinsics;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Filters;

/// <summary>
/// The Paeth row undone a pixel at a time in vector lanes (3- and 4-byte pixels) is byte for byte
/// the per-byte predictor of the PNG specification: every row length from the shortest the vector
/// path takes up, every tie the predictor breaks, rows of noise and rows of flat colour.
/// </summary>
public sealed class PaethVectorParityTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void TheVectorRow_IsThePredictorsRow_AtEveryLength(int bpp)
    {
        var random = new Random(bpp * 7919);
        for (int rowBytes = bpp + 4; rowBytes <= 3 * 64 + 5; rowBytes++)
            for (int trial = 0; trial < 20; trial++)
            {
                // Noise, and rows of a few values, where the three distances tie often.
                int spread = trial % 3 == 0 ? 256 : trial % 3 == 1 ? 3 : 1;
                byte[] data = Bytes(random, rowBytes + 2, spread);
                byte[] prev = Bytes(random, rowBytes + 1, spread);

                byte[] expected = Reference(data, 1, rowBytes, bpp, prev, 1);
                byte[] actual = new byte[rowBytes + 6];
                bool vector = PredictorHelper.TryUndoPaethVector(data, 1, actual, 3, rowBytes, bpp, prev, 1);

                Assert.Equal(Vector128.IsHardwareAccelerated, vector);
                Assert.Equal(expected, actual.AsSpan(3, rowBytes).ToArray());
                Assert.Equal(new byte[3], actual.AsSpan(0, 3).ToArray());          // nothing written before the row
                Assert.Equal(new byte[3], actual.AsSpan(3 + rowBytes).ToArray());  // nor past it
            }
    }

    /// <summary>
    /// A row undone in place — the input and the output one buffer, as the PNG decoder has it — is the
    /// predictor's row: a 4-byte pixel in vector lanes (each store is exactly the pixel), a 3-byte one
    /// by the scalar loop, since its four-byte store would overwrite the next pixel's raw byte unread.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ARowUndoneInPlace_IsThePredictorsRow(int bpp)
    {
        var random = new Random(bpp * 31);
        for (int rowBytes = bpp + 4; rowBytes <= 120; rowBytes++)
        {
            byte[] prev = Bytes(random, rowBytes, 256);
            byte[] row = Bytes(random, rowBytes, 256);
            byte[] expected = Reference(row, 0, rowBytes, bpp, prev, 0);

            PredictorHelper.UndoPngRow(4, row, 0, row, 0, rowBytes, bpp, prev, 0);
            Assert.Equal(expected, row);

            bool vector = PredictorHelper.TryUndoPaethVector(new byte[rowBytes], 0, new byte[rowBytes], 0, rowBytes, bpp, prev, 0);
            bool inPlace = PredictorHelper.TryUndoPaethVector(row, 0, row, 0, rowBytes, bpp, prev, 0);
            Assert.Equal(Vector128.IsHardwareAccelerated, vector);
            Assert.Equal(Vector128.IsHardwareAccelerated && bpp == 4, inPlace);
        }
    }

    [Fact]
    public void TheScalarRow_IsThePredictorsRow_TooAndOtherDepthsStayScalar()
    {
        var random = new Random(17);
        foreach (int bpp in new[] { 1, 2, 3, 4, 6, 8 })
            for (int rowBytes = 1; rowBytes < 40; rowBytes++)
            {
                byte[] data = Bytes(random, rowBytes, 256);
                byte[] prev = Bytes(random, rowBytes, 256);
                byte[] actual = new byte[rowBytes];
                PredictorHelper.UndoPaethScalar(data, 0, actual, 0, rowBytes, bpp, prev, 0);
                Assert.Equal(Reference(data, 0, rowBytes, bpp, prev, 0), actual);

                if (bpp != 3 && bpp != 4)
                    Assert.False(PredictorHelper.TryUndoPaethVector(data, 0, new byte[rowBytes], 0, rowBytes, bpp, prev, 0));
            }
    }

    [Fact]
    public void AWholeImage_ThroughThePredictorRemoval_IsTheReferences()
    {
        var random = new Random(5);
        const int columns = 97, colors = 3, rows = 23;
        int rowBytes = columns * colors;
        var encoded = new byte[rows * (rowBytes + 1)];
        random.NextBytes(encoded);
        for (int r = 0; r < rows; r++) encoded[r * (rowBytes + 1)] = 4;   // every row Paeth

        byte[] decoded = PredictorHelper.RemovePngPredictor(encoded, columns, colors, 8);

        var prev = new byte[rowBytes];
        for (int r = 0; r < rows; r++)
        {
            byte[] row = Reference(encoded, r * (rowBytes + 1) + 1, rowBytes, colors, prev, 0);
            Assert.Equal(row, decoded.AsSpan(r * rowBytes, rowBytes).ToArray());
            prev = row;
        }
    }

    /// <summary>The PNG specification's predictor, a byte at a time, written out here independently.</summary>
    private static byte[] Reference(byte[] data, int src, int rowBytes, int bpp, byte[] prev, int prevOff)
    {
        var row = new byte[rowBytes];
        for (int i = 0; i < rowBytes; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0;
            int b = prev[prevOff + i];
            int c = i >= bpp ? prev[prevOff + i - bpp] : 0;
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
            row[i] = (byte)(data[src + i] + pred);
        }
        return row;
    }

    private static byte[] Bytes(Random random, int length, int spread)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = (byte)(spread == 1 ? 200 : random.Next(spread) * (256 / spread));
        return bytes;
    }
}
