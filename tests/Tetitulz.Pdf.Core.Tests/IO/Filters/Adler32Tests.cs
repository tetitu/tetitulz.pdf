// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// <c>FlateEncodeFilter.ComputeAdler32</c>, the one Adler-32 the encoder writes and every Flate
/// decode checks. It reduces its sums once per <c>AdlerBlockBytes</c> rather than per byte, so it is
/// held here to the per-byte definition (RFC 1950 §8.2) on the inputs where a block edge or an
/// overflow would show: lengths either side of the edge, and long runs of 0xFF, which grow the sums
/// fastest.
/// </summary>
public class Adler32Tests
{
    /// <summary>RFC 1950 §8.2 as written: both sums reduced after every byte.</summary>
    private static uint ByDefinition(byte[] data, int count)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < count; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    [Theory]
    [InlineData("", 0x00000001u)]
    [InlineData("a", 0x00620062u)]
    [InlineData("abc", 0x024D0127u)]
    [InlineData("Wikipedia", 0x11E60398u)]
    public void KnownValues(string text, uint expected)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, FlateEncodeFilter.ComputeAdler32(data, data.Length));
    }

    [Fact]
    public void EveryLengthAroundTheBlockEdge_MatchesTheDefinition()
    {
        const int Block = FlateEncodeFilter.AdlerBlockBytes;
        var random = new byte[3 * Block + 64];
        new Random(5).NextBytes(random);
        byte[] ones = Enumerable.Repeat((byte)0xFF, random.Length).ToArray();

        foreach (int edge in new[] { 0, Block, 2 * Block, 3 * Block })
            for (int count = Math.Max(0, edge - 3); count <= edge + 3; count++)
            {
                Assert.Equal(ByDefinition(random, count), FlateEncodeFilter.ComputeAdler32(random, count));
                Assert.Equal(ByDefinition(ones, count), FlateEncodeFilter.ComputeAdler32(ones, count));
            }
    }

    [Fact]
    public void LongRuns_MatchTheDefinition_WithoutOverflow()
    {
        byte[] ones = Enumerable.Repeat((byte)0xFF, 1 << 20).ToArray();   // the fastest-growing sums
        var random = new byte[1 << 20];
        new Random(9).NextBytes(random);

        Assert.Equal(ByDefinition(ones, ones.Length), FlateEncodeFilter.ComputeAdler32(ones, ones.Length));
        Assert.Equal(ByDefinition(random, random.Length), FlateEncodeFilter.ComputeAdler32(random, random.Length));
    }

    [Fact]
    public void OnlyTheFirstCountBytesAreSummed()
    {
        byte[] data = Encoding.ASCII.GetBytes("Wikipedia, and then some more");
        Assert.Equal(0x11E60398u, FlateEncodeFilter.ComputeAdler32(data, 9));
    }
}
