// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The cursor members of <see cref="MsbBitStream"/> that the JBIG2 tests do not reach: peek, skip,
/// seek, align and the aligned byte read, each at its clamp.
/// </summary>
public class MsbBitStreamTests
{
    [Fact]
    public void PeekBits_DoesNotConsume_AndZeroFillsPastTheEnd()
    {
        var r = new MsbBitStream(new byte[] { 0xA5 });            // 1010 0101
        Assert.Equal(0b101, r.PeekBits(3));
        Assert.Equal(0, r.Position);
        r.SkipBits(4);
        Assert.Equal(0b0101_0000, r.PeekBits(8));               // four real bits, then four zeros
        Assert.Equal(4, r.Position);
        Assert.Equal(0b0101, r.ReadBits(4));
    }

    [Fact]
    public void PeekBits_RefusesMoreThan24OrANegativeCount_And24IsAllowed()
    {
        var r = new MsbBitStream(new byte[] { 0xFF, 0xFF, 0xFF });
        Assert.Equal(0xFFFFFF, r.PeekBits(24));
        Assert.Equal(0, r.PeekBits(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.PeekBits(25));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.PeekBits(-1));
    }

    [Fact]
    public void SkipBits_CarriesIntoTheNextByte_IgnoresANonPositiveCount_AndClampsAtTheEnd()
    {
        var r = new MsbBitStream(new byte[] { 0x00, 0xF0 });
        r.SkipBits(6);
        r.SkipBits(3);                                          // 6 + 3 = byte 1, bit 1
        Assert.Equal(9, r.Position);
        r.SkipBits(0);
        r.SkipBits(-5);
        Assert.Equal(9, r.Position);
        Assert.Equal(0b111, r.ReadBits(3));                     // bits 1-3 of 0xF0

        r.SkipBits(100);
        Assert.Equal(16, r.Position);
        Assert.True(r.Eof);
        Assert.Equal(-1, r.ReadBit());
    }

    [Fact]
    public void SetPosition_SeeksBackward_ClampsANegativeToZero_AndPastTheEndToTheEnd()
    {
        var r = new MsbBitStream(new byte[] { 0x80, 0x01 });
        r.SetPosition(15);
        Assert.Equal(1, r.ReadBit());
        r.SetPosition(0);
        Assert.Equal(1, r.ReadBit());
        r.SetPosition(-3);
        Assert.Equal(0, r.Position);
        r.SetPosition(999);
        Assert.Equal(16, r.Position);
        Assert.True(r.Eof);
    }

    [Fact]
    public void AlignToByte_MovesToTheNextBoundary_AndIsANoOpWhenAligned()
    {
        var r = new MsbBitStream(new byte[] { 0x00, 0x00, 0x00 });
        r.AlignToByte();
        Assert.Equal(0, r.Position);
        r.ReadBit();
        r.AlignToByte();
        Assert.Equal(8, r.Position);
        r.AlignToByte();
        Assert.Equal(8, r.Position);
    }

    [Fact]
    public void ReadAlignedBytes_ReadsAndAdvances_ClampsAShortRead_AndRefusesANegativeCount()
    {
        var r = new MsbBitStream(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2 }, r.ReadAlignedBytes(2));
        Assert.Equal(16, r.Position);
        Assert.Equal(new byte[] { 3 }, r.ReadAlignedBytes(5));
        Assert.Equal(Array.Empty<byte>(), r.ReadAlignedBytes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.ReadAlignedBytes(-1));
    }

    [Fact]
    public void AdversarialReadAlignedBytes_ACountNearIntMaxPastTheFirstByte_IsClampedToWhatRemains()
    {
        // A JBIG2 BMSIZE near int.MaxValue read at byte 1: the int sum wrapped negative, passed the
        // clamp, and two gigabytes were allocated for a three-byte stream.
        var r = new MsbBitStream(new byte[] { 1, 2, 3 });
        r.ReadAlignedBytes(1);

        Assert.Equal(new byte[] { 2, 3 }, r.ReadAlignedBytes(int.MaxValue));
        Assert.Equal(24, r.Position);
    }

    [Fact]
    public void ANullBufferIsEmpty_AndANegativeOffsetStartsAtZero()
    {
        var empty = new MsbBitStream(null);
        Assert.Equal(0, empty.TotalBits);
        Assert.True(empty.Eof);
        Assert.Equal(-1, empty.ReadBit());

        var r = new MsbBitStream(new byte[] { 0x80, 0x40 }, -4);
        Assert.Equal(0, r.Position);
        Assert.Equal(16, r.TotalBits);
        Assert.Equal(1, r.ReadBit());

        var offset = new MsbBitStream(new byte[] { 0x00, 0x40 }, 1);
        Assert.Equal(8, offset.Position);
        Assert.Equal(0b01, offset.ReadBits(2));
    }

    [Fact]
    public void ReadBits_ReadsZerosPastTheEnd()
    {
        var r = new MsbBitStream(new byte[] { 0xC0 });
        r.SkipBits(6);
        Assert.Equal(0b0000, r.ReadBits(4));
        var full = new MsbBitStream(new byte[] { 0xFF });
        Assert.Equal(0b1111_1111_00, full.ReadBits(10));
    }
}
