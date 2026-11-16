// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary><see cref="JpxTagTree"/> against the worked example in T.800 §B.10.2's NOTE.</summary>
public sealed class JpxTagTreeTests
{
    /// <summary>Reads thresholds 1, 2, … until the leaf's value is known — what a first inclusion or a zero bit-plane count does.</summary>
    private static int Value(JpxTagTree tree, JpxBitReader bits, int x, int y)
    {
        int threshold = 1;
        while (!tree.Decode(bits, x, y, threshold, out bool exhausted))
        {
            Assert.False(exhausted, "bits ran out");
            threshold++;
        }
        return threshold - 1;
    }

    /// <summary>
    /// The NOTE's Figure B.12 codes, for a four-level tree: q3(0, 0) as 01111 — the root is greater
    /// than zero and in fact one, then one, one, one down the levels; then q3(1, 0) as 001 — greater
    /// than 1, greater than 2, is 3 — its ancestors already known; then q3(2, 0) as 101 — its parent
    /// q2(1, 0) is one, and the leaf is 2. Eleven bits for three values, and not one more.
    /// </summary>
    [Fact]
    public void DecodesFigureB12sFirstRow_InElevenBits()
    {
        byte[] bytes = JpxPacketBits.Bytes("01111 001 101");
        Assert.Equal(new byte[] { 0x79, 0xA0 }, bytes);
        var bits = new JpxBitReader(bytes, 0, bytes.Length);
        var tree = new JpxTagTree(6, 3);   // four levels: 6x3, 3x2, 2x1, 1x1

        Assert.Equal(1, Value(tree, bits, 0, 0));
        Assert.Equal(3, Value(tree, bits, 1, 0));
        Assert.Equal(2, Value(tree, bits, 2, 0));
        Assert.Equal(1, tree.ValueOf(0, 0));
        Assert.Equal(-1, tree.ValueOf(3, 0));

        // Exactly eleven bits were read: what remains is the padding, then nothing.
        Assert.True(bits.TryReadBits(5, out int padding));
        Assert.Equal(0, padding);
        Assert.False(bits.TryReadBit(out _));
    }

    /// <summary>
    /// A query reads only what answers it: with the value 2 coded as 001, the threshold-1 question
    /// consumes one bit and answers "not below 1"; threshold 2 one more; threshold 3 the last, and
    /// the value is then known. Running out of bits is reported, not mistaken for an answer.
    /// </summary>
    [Fact]
    public void Decode_ReadsOnlyTheBitsTheThresholdNeeds()
    {
        byte[] bytes = JpxPacketBits.Bytes("001");
        var bits = new JpxBitReader(bytes, 0, bytes.Length);
        var tree = new JpxTagTree(1, 1);

        Assert.False(tree.Decode(bits, 0, 0, 1, out bool exhausted));
        Assert.False(exhausted);
        Assert.Equal(-1, tree.ValueOf(0, 0));
        Assert.False(tree.Decode(bits, 0, 0, 2, out _));
        Assert.True(tree.Decode(bits, 0, 0, 3, out _));
        Assert.Equal(2, tree.ValueOf(0, 0));
        // Known now: a higher threshold reads nothing more (the padding stays).
        Assert.True(tree.Decode(bits, 0, 0, 10, out _));
        Assert.True(bits.TryReadBits(5, out int padding));
        Assert.Equal(0, padding);

        var empty = new JpxBitReader(new byte[0], 0, 0);
        Assert.False(new JpxTagTree(1, 1).Decode(empty, 0, 0, 1, out exhausted));
        Assert.True(exhausted);
    }
}
