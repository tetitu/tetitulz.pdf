// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// <see cref="SrgbTransfer"/>, IEC 61966-2-1's transfer function: a known value, the round trip,
/// the linear segment and its threshold each way, and the clipping.
/// </summary>
public sealed class SrgbTransferTests
{
    /// <summary>Linear 0.5 is sRGB 0.7354 (the 188 of an 8-bit mid grey in light).</summary>
    [Fact]
    public void Encode_HalfLinear_IsTheKnownValue()
    {
        Assert.Equal(0.735357, SrgbTransfer.Encode(0.5), 6);
        Assert.Equal(188, (int)System.Math.Round(SrgbTransfer.Encode(0.5) * 255));
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(0.0031308)]
    [InlineData(0.2)]
    [InlineData(0.9)]
    public void Decode_UndoesEncode(double linear)
        => Assert.Equal(linear, SrgbTransfer.Decode(SrgbTransfer.Encode(linear)), 12);

    /// <summary>Below the threshold both ways are the linear segment, 12.92 × and ÷ 12.92.</summary>
    [Fact]
    public void TheLinearSegment_IsTwelvePointNineTwo()
    {
        Assert.Equal(0.002 * 12.92, SrgbTransfer.Encode(0.002), 12);
        Assert.Equal(0.03 / 12.92, SrgbTransfer.Decode(0.03), 12);
        Assert.Equal(0.0031308, SrgbTransfer.Decode(0.0031308 * 12.92), 12);
    }

    [Theory]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 1)]
    [InlineData(double.NaN, 0)]
    public void OutOfRange_IsClipped(double value, double expected)
    {
        Assert.Equal(expected, SrgbTransfer.Encode(value));
        Assert.Equal(expected, SrgbTransfer.Decode(value));
    }
}
