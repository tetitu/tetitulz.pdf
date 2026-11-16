// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxBlockEncoder"/> against <see cref="JpxBlockDecoder"/> — an exact oracle for tier 1
/// on its own — and against the decoder tests' hand-coded decision streams, which the encoder must
/// reproduce byte for byte, since its every decision and context is the one Tables D.1 to D.5 name.
/// </summary>
public sealed class JpxEncoderBlockTests
{
    private static JpxCodeBlock Block(int width, int height, byte[] data, int passes)
    {
        var block = new JpxCodeBlock(new JpxRect(0, 0, width, height)) { Included = true };
        block.Add(new JpxCodeChunk(new JpxRange(0, data.Length), passes, terminated: false));
        return block;
    }

    private static int[] RoundTrip(int[] coefficients, int width, int height, int orientation)
    {
        byte[] data = JpxBlockEncoder.Encode(coefficients, width, height, orientation, out int planes, out int passes);
        Assert.Equal(passes == 0 ? 0 : 3 * planes - 2, passes);
        if (passes == 0) return new int[coefficients.Length];
        int[] decoded = JpxBlockDecoder.Decode(Block(width, height, data, passes), data, orientation, JpxCodeBlockStyle.None, planes, 0, out string problem);
        Assert.Null(problem);
        return Array.ConvertAll(decoded, JpxBlockDecoder.Truncate);
    }

    /// <summary>
    /// The decoder test's decisions for Table D.6's column — 10, 1, 3, −7 in four planes — coded with
    /// the MQ coder are what the encoder emits for those values, up to the termination: T.88's FLUSH
    /// appends FF AC where T.800's (C.2.9) stops, so the hand stream is the encoder's plus that marker.
    /// </summary>
    [Fact]
    public void TableD6sColumn_EncodesToTheHandCodedDecisions()
    {
        var decisions = new (int Context, int Bit)[]
        {
            (17, 1), (18, 0), (18, 0), (9, 0), (3, 0), (0, 0), (0, 0),
            (3, 0), (14, 0), (0, 0), (0, 1), (9, 1),
            (3, 0), (3, 1), (10, 1), (16, 1), (15, 1),
            (4, 1), (10, 0), (16, 0), (15, 1), (16, 1),
        };
        var coder = new MqEncoder(JpxBlockDecoder.ContextCount);
        JpxBlockContexts.ResetStates(coder);
        foreach ((int context, int bit) in decisions) coder.Encode(context, bit);
        byte[] hand = coder.Finish();

        byte[] encoded = JpxBlockEncoder.Encode(new[] { 10, 1, 3, -7 }, 1, 4, 0, out int planes, out int passes);
        Assert.Equal((4, 10), (planes, passes));
        Assert.Equal(hand, encoded.Concat(new byte[] { 0xFF, 0xAC }).ToArray());
    }

    /// <summary>A single −5: three planes, seven passes, and the decoder returns −(5·2 + 1).</summary>
    [Fact]
    public void ASingleCoefficient_RoundTripsThroughItsPasses()
    {
        byte[] data = JpxBlockEncoder.Encode(new[] { -5 }, 1, 1, 0, out int planes, out int passes);
        Assert.Equal((3, 7), (planes, passes));
        Assert.Equal(new[] { -11 }, JpxBlockDecoder.Decode(Block(1, 1, data, 7), data, 0, JpxCodeBlockStyle.None, 3, 0, out string problem));
        Assert.Null(problem);
    }

    [Fact]
    public void AllZeros_HaveNoPassesAndNoBytes()
    {
        byte[] data = JpxBlockEncoder.Encode(new int[12], 3, 4, 2, out int planes, out int passes);
        Assert.Empty(data);
        Assert.Equal((0, 0), (planes, passes));
    }

    /// <summary>The run-length mode (Table D.5): a clear column with its first significant sample third codes the run-length 1, the index 10, and the sign — the decoder test's stream.</summary>
    [Fact]
    public void ACleanColumn_UsesTheRunLengthContext()
    {
        var coder = new MqEncoder(JpxBlockDecoder.ContextCount);
        JpxBlockContexts.ResetStates(coder);
        foreach ((int context, int bit) in new[] { (17, 1), (18, 1), (18, 0), (9, 0), (3, 1), (10, 0) }) coder.Encode(context, bit);
        byte[] hand = coder.Finish();
        byte[] encoded = JpxBlockEncoder.Encode(new[] { 0, 0, 1, 1 }, 1, 4, 0, out int planes, out int passes);
        Assert.Equal((1, 1), (planes, passes));
        Assert.Equal(hand, encoded.Concat(new byte[] { 0xFF, 0xAC }).ToArray());
    }

    [Theory]
    [InlineData(1, 1, 0, 1)]
    [InlineData(3, 5, 1, 2)]
    [InlineData(5, 3, 2, 3)]
    [InlineData(8, 8, 3, 4)]
    [InlineData(64, 64, 0, 5)]
    [InlineData(64, 64, 3, 6)]
    [InlineData(17, 9, 1, 7)]
    public void RandomBlocks_RoundTripExactly_InEveryOrientation(int width, int height, int orientation, int seed)
    {
        var random = new Random(seed);
        var coefficients = new int[width * height];
        int range = 1 << random.Next(1, 12);
        for (int i = 0; i < coefficients.Length; i++)
            coefficients[i] = random.Next(4) == 0 ? 0 : random.Next(-range, range);
        coefficients[0] = -range - 1;   // at least one coefficient, negative and at the top plane
        Assert.Equal(coefficients, RoundTrip(coefficients, width, height, orientation));
    }

    /// <summary>The orientation changes the contexts (Table D.1), so a stream coded for one is not read as the same values under another.</summary>
    [Fact]
    public void TheOrientation_IsPartOfTheCode()
    {
        var random = new Random(9);
        var coefficients = new int[64];
        for (int i = 0; i < 64; i++) coefficients[i] = random.Next(-40, 40);
        byte[] hl = JpxBlockEncoder.Encode(coefficients, 8, 8, 1, out int planes, out int passes);
        int[] asHh = JpxBlockDecoder.Decode(Block(8, 8, hl, passes), hl, 3, JpxCodeBlockStyle.None, planes, 0, out _);
        Assert.NotEqual(coefficients, Array.ConvertAll(asHh, JpxBlockDecoder.Truncate));
        Assert.Equal(coefficients, RoundTrip(coefficients, 8, 8, 1));
    }

    /// <summary>Thirty planes are the most an <c>int</c> band holds; the thirty-first decodes exactly through the <c>long</c> form, and the <c>int</c> form says why it cannot.</summary>
    [Fact]
    public void AnIntBandHoldsThirtyPlanes_AWideBandMore_AndTheWrongCountIsRefused()
    {
        Assert.Throws<ArgumentException>(() => JpxBlockEncoder.Encode(new int[5], 2, 3, 0, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxBlockEncoder.Encode(new[] { int.MinValue }, 1, 1, 0, out _, out _));
        Assert.Equal(new[] { (1 << 29) + 5 }, RoundTrip(new[] { (1 << 29) + 5 }, 1, 1, 0));   // thirty planes: the most an int band holds

        byte[] data = JpxBlockEncoder.Encode(new[] { 1 << 30 }, 1, 1, 0, out int planes, out int passes);
        Assert.Equal(31, planes);
        long[] wide = JpxBlockDecoder.DecodeWide(Block(1, 1, data, passes), data, 0, JpxCodeBlockStyle.None, planes, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(1L << 30, JpxBlockDecoder.Truncate(wide[0]));
        JpxBlockDecoder.Decode(Block(1, 1, data, passes), data, 0, JpxCodeBlockStyle.None, planes, 0, out problem);
        Assert.Contains("31 bit-planes to decode; at most 30", problem, StringComparison.Ordinal);
    }
}
