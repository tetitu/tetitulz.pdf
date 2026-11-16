// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary><see cref="JpxBitWriter"/> against <see cref="JpxBitReader"/> and the bit-stuffing rule of T.800 §B.10.1.</summary>
public sealed class JpxEncoderBitWriterTests
{
    private static byte[] Write(string bits)
    {
        var writer = new JpxBitWriter();
        foreach (char ch in bits)
            if (ch == '0' || ch == '1') writer.WriteBit(ch - '0');
        return writer.Finish();
    }

    /// <summary>The tier-2 decoder test's first packet header, 1 1 001 1100 0 0100, is CE 10 — the same bytes the test helper packs.</summary>
    [Fact]
    public void PacksMsbFirst_AndPadsTheLastByte()
    {
        Assert.Equal(new byte[] { 0xCE, 0x10 }, Write("1 1 001 1100 0 0100"));
        Assert.Equal(JpxPacketBits.Bytes("1 1 001 111100011 110 11001000"), Write("1 1 001 111100011 110 11001000"));
        Assert.Empty(Write(""));
    }

    /// <summary>After a 0xFF byte the next byte holds seven bits under a stuffed zero; eight ones then 1 0 1 make FF 50.</summary>
    [Fact]
    public void StuffsAZeroAfterAnFF()
    {
        Assert.Equal(new byte[] { 0xFF, 0x50 }, Write("11111111 101"));
        // Without the rule the sixteen ones would be FF FF; with it, FF 7F 80.
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x80 }, Write("11111111 11111111"));
    }

    /// <summary>A header ending exactly on 0xFF gets its stuffed byte: the last byte in a packet header is never 0xFF.</summary>
    [Fact]
    public void AFinalFF_IsFollowedByItsStuffedByte()
    {
        Assert.Equal(new byte[] { 0xFF, 0x00 }, Write("11111111"));
        Assert.Equal(new byte[] { 0x0F, 0xFF, 0x00 }, Write("00001111 11111111"));
    }

    [Fact]
    public void WriteBits_TakesTheValueMsbFirst_AndTheReaderReturnsIt()
    {
        var writer = new JpxBitWriter();
        writer.WriteBits(0b1011, 4);
        writer.WriteBits(200, 8);
        writer.WriteBits(0x7FFF_FFFF, 31);
        byte[] bytes = writer.Finish();

        var reader = new JpxBitReader(bytes, 0, bytes.Length);
        Assert.True(reader.TryReadBits(4, out int a));
        Assert.True(reader.TryReadBits(8, out int b));
        Assert.True(reader.TryReadBits(31, out int c));
        Assert.Equal((0b1011, 200, 0x7FFF_FFFF), (a, b, c));
        Assert.Equal(bytes.Length, reader.Align());
    }

    /// <summary>Random headers, read back bit for bit; the reader's Align lands where the writer's Finish ended, stuffed bytes included.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WhatIsWritten_IsReadBack_AndAlignEndsWhereFinishDid(int seed)
    {
        var random = new Random(seed);
        var writer = new JpxBitWriter();
        var bits = new List<int>();
        for (int i = 0; i < 500; i++)
        {
            int bit = random.Next(10) < 8 ? 1 : 0;   // skewed towards ones, so 0xFF bytes happen
            bits.Add(bit);
            writer.WriteBit(bit);
        }
        byte[] bytes = writer.Finish();
        Assert.Contains((byte)0xFF, bytes);

        var reader = new JpxBitReader(bytes, 0, bytes.Length);
        foreach (int expected in bits)
        {
            Assert.True(reader.TryReadBit(out int bit));
            Assert.Equal(expected, bit);
        }
        Assert.Equal(bytes.Length, reader.Align());
    }
}
