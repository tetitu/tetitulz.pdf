// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <see cref="MsbBitWriter"/> against <see cref="MsbBitStream"/>, and against the bit order itself.
/// </summary>
/// <remarks>
/// <b>Bit order is the kind of mistake that produces output which decodes to something.</b> A
/// writer that packed least-significant-bit-first would round-trip perfectly through a reader that
/// did the same, so a round trip cannot see it — the first assertion here is therefore against a
/// spelled-out byte, not against a reader. The reader comparison comes second, because the two do
/// have to agree: three producers write through this class and <c>MsbBitStream</c> reads two of
/// them back.
/// </remarks>
public class MsbBitWriterTests
{
    /// <summary>The high-order bit of the first value lands in the high-order bit of byte 0.</summary>
    [Fact]
    public void BitsArePackedHighOrderFirst()
    {
        var w = new MsbBitWriter(16);
        w.WriteBits(0b101, 3);
        w.WriteBits(0b01, 2);
        w.WriteBits(0b111, 3);

        Assert.Equal(new byte[] { 0b10101111 }, w.ToArray());
    }

    /// <summary>Unused bits in the final byte are zero.</summary>
    [Fact]
    public void APartialFinalByteIsZeroPadded()
    {
        var w = new MsbBitWriter(16);
        w.WriteBits(0b11, 2);

        Assert.Equal(new byte[] { 0b11000000 }, w.ToArray());
    }

    /// <summary>
    /// <see cref="MsbBitWriter.AlignToByte"/> pads to the next byte boundary and does nothing when
    /// already there.
    /// </summary>
    /// <remarks>
    /// The idempotence half is what Annex F's hint tables rely on: several of their item groups are
    /// empty, and an aligned writer must emit no byte for them at all — a pad that always advanced
    /// would make every empty group cost a byte and put every later group at the wrong offset.
    /// </remarks>
    [Fact]
    public void AligningPadsToTheNextByteAndIsANoOpWhenAlreadyAligned()
    {
        var w = new MsbBitWriter(16);
        w.WriteBits(0b101, 3);
        w.AlignToByte();
        Assert.Equal(8, w.BitCount);

        w.AlignToByte();
        w.AlignToByte();
        Assert.Equal(8, w.BitCount);
        Assert.Single(w.ToArray());

        w.WriteBits(0xFF, 8);
        w.AlignToByte();
        Assert.Equal(new byte[] { 0b10100000, 0xFF }, w.ToArray());
    }

    /// <summary>Aligning an empty writer produces no bytes at all.</summary>
    [Fact]
    public void AligningBeforeAnythingIsWrittenProducesNoBytes()
    {
        var w = new MsbBitWriter(16);
        w.AlignToByte();

        Assert.Empty(w.ToArray());
        Assert.Equal(0, w.BitCount);
    }

    /// <summary>A 32-bit value keeps its top bit, which an <c>int</c> would have used for the sign.</summary>
    /// <remarks>
    /// Annex F's hint tables carry unsigned 32-bit byte offsets, so the parameter is a
    /// <see cref="long"/>. Written through a signed shift the top bit of <c>0xFFFFFFFF</c> would
    /// still come out right, but every bit below it would be sign-extended garbage — hence a value
    /// with a set top bit and a distinctive tail.
    /// </remarks>
    [Fact]
    public void AFullWidthUnsignedValueSurvivesIntact()
    {
        var w = new MsbBitWriter(16);
        w.WriteBits(0x80ABCDEFL, 32);

        Assert.Equal(new byte[] { 0x80, 0xAB, 0xCD, 0xEF }, w.ToArray());
    }

    /// <summary>Widths that are not byte multiples round-trip through the reader.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(17)]
    public void TheReaderGetsBackWhatTheWriterPutIn(int width)
    {
        int mask = (1 << width) - 1;
        var values = new List<int>();
        var w = new MsbBitWriter(16);
        for (int i = 0; i < 40; i++)
        {
            int value = (int)(i * 2654435761u & (uint)mask);
            values.Add(value);
            w.WriteBits(value, width);
        }

        var reader = new MsbBitStream(w.ToArray());
        foreach (int expected in values)
            Assert.Equal(expected, reader.ReadBits(width));
    }

    /// <summary>A capacity below the floor still works; undersizing is a cost, not a defect.</summary>
    [Fact]
    public void AnUndersizedCapacityStillGrows()
    {
        var w = new MsbBitWriter(0);
        for (int i = 0; i < 100; i++) w.WriteBits(i & 1, 1);

        Assert.Equal(100, w.BitCount);
        Assert.Equal(13, w.ToArray().Length);
    }
}
