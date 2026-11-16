// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary><see cref="JpxBitReader"/> against T.800 §B.10.1's bit-stuffing routine.</summary>
public sealed class JpxBitReaderTests
{
    /// <summary>After a 0xFF byte the next byte's MSB is a stuffed zero: 0xFF 0x55 reads as fifteen bits, not sixteen.</summary>
    [Fact]
    public void ReadsMsbFirst_AndSkipsTheStuffedBitAfterFF()
    {
        var reader = new JpxBitReader(new byte[] { 0xFF, 0x55 }, 0, 2);
        Assert.True(reader.TryReadBits(15, out int value));
        Assert.Equal(0x7FD5, value);   // 1111 1111, then 101 0101 (0x55 without its MSB)
        Assert.False(reader.TryReadBit(out _));
    }

    /// <summary>
    /// A header ending on 0xFF is followed by a stuffed byte the encoder had to emit; aligning
    /// consumes it. A header ending on any other byte consumes only that byte's remaining bits.
    /// </summary>
    [Fact]
    public void Align_ConsumesTheStuffedByteAfterATrailingFF()
    {
        var afterFF = new JpxBitReader(new byte[] { 0xFF, 0x00, 0xAB }, 0, 3);
        Assert.True(afterFF.TryReadBits(8, out int all));
        Assert.Equal(0xFF, all);
        Assert.Equal(2L, afterFF.Align());

        var plain = new JpxBitReader(new byte[] { 0x80, 0xAB }, 0, 2);
        Assert.True(plain.TryReadBit(out int bit));
        Assert.Equal(1, bit);
        Assert.Equal(1L, plain.Align());
    }

    /// <summary>A marker is skipped only at a byte boundary, and only when it is the one asked for.</summary>
    [Fact]
    public void TrySkipMarker_OnlyAtAByteBoundary()
    {
        var aligned = new JpxBitReader(new byte[] { 0xFF, 0x92, 0x01 }, 0, 3);
        Assert.False(aligned.TrySkipMarker(0xFF91));
        Assert.True(aligned.TrySkipMarker(0xFF92));
        Assert.Equal(2L, aligned.Position);
        Assert.False(aligned.TrySkipMarker(0xFF92));

        var midByte = new JpxBitReader(new byte[] { 0xFF, 0x92 }, 0, 2);
        Assert.True(midByte.TryReadBit(out _));
        Assert.False(midByte.TrySkipMarker(0xFF92));
    }

    /// <summary>The test helper's stuffing and the reader's agree, so a header written as bits round-trips.</summary>
    [Fact]
    public void PacketBits_RoundTripThroughTheReader()
    {
        byte[] bytes = JpxPacketBits.Bytes("11111111 1010 1");   // 0xFF, then a stuffed zero before 1010 1
        Assert.Equal(new byte[] { 0xFF, 0x54 }, bytes);          // 0 1010 100
        var reader = new JpxBitReader(bytes, 0, bytes.Length);
        Assert.True(reader.TryReadBits(13, out int value));
        Assert.Equal(0x1FF5, value);

        Assert.Equal(new byte[] { 0xFF, 0x00 }, JpxPacketBits.Bytes("11111111"));   // a trailing 0xFF needs its stuffed byte
    }
}
