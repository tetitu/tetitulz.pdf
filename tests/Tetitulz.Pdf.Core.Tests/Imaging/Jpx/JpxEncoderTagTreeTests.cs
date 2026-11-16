// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary><see cref="JpxTagTreeEncoder"/> against <see cref="JpxTagTree"/>: what one writes, the other reads, and nothing more.</summary>
public sealed class JpxEncoderTagTreeTests
{
    /// <summary>Reads thresholds 1, 2, … until the leaf's value is known — what a zero bit-plane count does.</summary>
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
    /// A single leaf of value 2 coded against thresholds 1, 2, 3 is 001 — the bits the decoder test
    /// reads for the same value; a 2x1 tree of 1 and 3 codes the root's 1 (01), the first leaf's 1
    /// (1), then the second leaf's 3 above the known root (001): 011001, six bits and no more.
    /// </summary>
    [Fact]
    public void Encode_WritesTheBitsOfTheWorkedExamples()
    {
        var single = new JpxTagTreeEncoder(1, 1);
        single.SetValue(0, 0, 2);
        var bits = new JpxBitWriter();
        for (int t = 1; t <= 3; t++) single.Encode(bits, 0, 0, t);
        Assert.Equal(new byte[] { 0x20 }, bits.Finish());

        var pair = new JpxTagTreeEncoder(2, 1);
        pair.SetValue(0, 0, 1);
        pair.SetValue(1, 0, 3);
        bits = new JpxBitWriter();
        for (int t = 1; t <= 2; t++) pair.Encode(bits, 0, 0, t);
        for (int t = 1; t <= 4; t++) pair.Encode(bits, 1, 0, t);
        Assert.Equal(new byte[] { 0x64 }, bits.Finish());   // 0110 01 00
    }

    /// <summary>A query at a threshold the value exceeds writes only the zeros up to the threshold; the later query finishes the job (§B.10.4's partial tag tree).</summary>
    /// <summary>
    /// A rate allocator builds the tree with "not yet" (the layer count) for every undecided block
    /// and lowers a leaf as a block's first layer is decided. A 2x2 tree whose four leaves start at 4
    /// and are lowered to 1 after threshold 1 was coded must code thresholds 2 and 3 to the bits a
    /// fresh tree with the values 1, 1, 1, 1 codes after the same threshold 1 — the codestream's
    /// own write builds that fresh tree. Lowering a value the emitted bits contradict is refused;
    /// a higher value is ignored.
    /// </summary>
    [Fact]
    public void Lower_CodesTheBitsAFreshTreeWithTheFinalValuesCodes()
    {
        var lowered = new JpxTagTreeEncoder(2, 2);
        var fresh = new JpxTagTreeEncoder(2, 2);
        for (int i = 0; i < 4; i++)
        {
            lowered.SetValue(i % 2, i / 2, 4);
            fresh.SetValue(i % 2, i / 2, 1);
        }
        var a = new JpxBitWriter();
        var b = new JpxBitWriter();
        for (int i = 0; i < 4; i++)
        {
            lowered.Encode(a, i % 2, i / 2, 1);   // "below 1?" — no, for both: the same zeros
            fresh.Encode(b, i % 2, i / 2, 1);
        }
        Assert.Equal(b.BitCount, a.BitCount);
        for (int i = 0; i < 4; i++) lowered.Lower(i % 2, i / 2, 1);
        for (int t = 2; t <= 3; t++)
            for (int i = 0; i < 4; i++)
            {
                lowered.Encode(a, i % 2, i / 2, t);
                fresh.Encode(b, i % 2, i / 2, t);
            }
        Assert.Equal(b.Finish(), a.Finish());
        Assert.True(a.BitCount > 4, "the second round must have coded the root and the leaves");

        // Refused: the leaves are coded to 1 now; a value below what the bits fixed cannot be taken.
        Assert.Throws<InvalidOperationException>(() => lowered.Lower(0, 0, 0));
        // Ignored: a value never rises.
        lowered.Lower(0, 0, 3);
        var c = new JpxBitWriter();
        lowered.Encode(c, 0, 0, 4);
        Assert.Equal(0, c.BitCount);   // already known below 4

        // The stale case that the allocator hit: the same lowering skipped codes different bits.
        var stale = new JpxTagTreeEncoder(2, 2);
        for (int i = 0; i < 4; i++) stale.SetValue(i % 2, i / 2, 4);
        var s = new JpxBitWriter();
        for (int t = 1; t <= 3; t++)
            for (int i = 0; i < 4; i++) stale.Encode(s, i % 2, i / 2, t);
        Assert.NotEqual(b.BitCount, s.BitCount);
    }

    [Fact]
    public void Encode_WritesOnlyWhatTheThresholdNeeds()
    {
        var tree = new JpxTagTreeEncoder(1, 1);
        tree.SetValue(0, 0, 2);
        var bits = new JpxBitWriter();
        tree.Encode(bits, 0, 0, 1);
        Assert.Equal(new byte[] { 0x00 }, bits.Finish());   // one 0 bit: "not below 1"

        bits = new JpxBitWriter();
        tree.Encode(bits, 0, 0, 10);
        Assert.Equal(new byte[] { 0x40 }, bits.Finish());   // 0 then 1: 2 is reached
        bits = new JpxBitWriter();
        tree.Encode(bits, 0, 0, 10);
        Assert.Empty(bits.Finish());                         // known: nothing more
    }

    [Theory]
    [InlineData(1, 1, 3)]
    [InlineData(6, 3, 5)]
    [InlineData(7, 7, 4)]
    [InlineData(16, 2, 9)]
    public void WhatTheEncoderWrites_TheDecoderReadsBack_ToTheBit(int width, int height, int maxValue)
    {
        var random = new Random(width * 31 + height * 7 + maxValue);
        var values = new int[width, height];
        var encoder = new JpxTagTreeEncoder(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                values[x, y] = random.Next(maxValue + 1);
                encoder.SetValue(x, y, values[x, y]);
            }
        var bits = new JpxBitWriter();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int t = 1; t <= values[x, y] + 1; t++) encoder.Encode(bits, x, y, t);
        int bitCount = bits.BitCount;
        byte[] bytes = bits.Finish();
        Assert.True(bitCount >= width * height, $"{bitCount} bits for {width * height} leaves");

        var decoder = new JpxTagTree(width, height);
        var reader = new JpxBitReader(bytes, 0, bytes.Length);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Assert.Equal(values[x, y], Value(decoder, reader, x, y));
        // Every bit written was read: only the padding of the last byte remains, and the header
        // ends where the writer ended it.
        int padding = 0;
        while (reader.TryReadBit(out int bit))
        {
            Assert.Equal(0, bit);
            padding++;
        }
        Assert.True(padding < 8, $"{padding} bits left");
        Assert.Equal(bytes.Length, reader.Align());
        // A byte holds eight bits, or seven after an 0xFF, and a final 0xFF costs one stuffed byte more.
        Assert.InRange(bytes.Length, (bitCount + 7) / 8, (bitCount + 6) / 7 + 1);
    }

    [Fact]
    public void SetValue_IsRefusedOnceCodingBegan_AndNegativeValues()
    {
        var tree = new JpxTagTreeEncoder(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.SetValue(0, 0, -1));
        tree.SetValue(1, 1, 4);
        tree.Encode(new JpxBitWriter(), 0, 0, 1);
        Assert.Throws<InvalidOperationException>(() => tree.SetValue(0, 1, 2));
    }

    /// <summary>
    /// A clone codes on its own: what it emits does not advance the original, so the original
    /// still writes every bit the decoder needs. The rate allocator tries a layer on clones and
    /// keeps the base; a clone sharing the original's state would leave the base's next write a
    /// bit short — which byte padding can hide from a byte count, and this cannot.
    /// </summary>
    [Fact]
    public void Clone_CodesIndependentlyOfTheOriginal()
    {
        var original = new JpxTagTreeEncoder(2, 1);
        original.SetValue(0, 0, 0);
        original.SetValue(1, 0, 2);
        JpxTagTreeEncoder clone = original.Clone();

        var onClone = new JpxBitWriter();
        for (int t = 1; t <= 3; t++) clone.Encode(onClone, 1, 0, t);
        Assert.Equal(4, onClone.BitCount);   // root 0 (1), then the leaf's 2 above it (001)

        var onOriginal = new JpxBitWriter();
        for (int t = 1; t <= 3; t++) original.Encode(onOriginal, 1, 0, t);
        Assert.Equal(4, onOriginal.BitCount);
        Assert.Equal(onClone.Finish(), onOriginal.Finish());

        // And the other way: the clone made after the original coded starts from that state, and codes nothing more for what is known.
        JpxTagTreeEncoder later = original.Clone();
        var onLater = new JpxBitWriter();
        for (int t = 1; t <= 3; t++) later.Encode(onLater, 1, 0, t);
        Assert.Equal(0, onLater.BitCount);
        Assert.Equal(1, Bits(later, 0, 0, 1));   // the other leaf is still uncoded: value 0 below 1 is one bit
    }

    private static int Bits(JpxTagTreeEncoder tree, int x, int y, int threshold)
    {
        var bits = new JpxBitWriter();
        tree.Encode(bits, x, y, threshold);
        return bits.BitCount;
    }
}
