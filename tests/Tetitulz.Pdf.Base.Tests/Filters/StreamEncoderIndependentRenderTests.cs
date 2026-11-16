// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Filters;

/// <summary>
/// The stream encoders' output read by a decoder this repository did not write (<see cref="IndependentImageRender"/>):
/// CCITT Group 4 (the redactor writes bilevel images with it), LZW in both
/// <c>/EarlyChange</c> settings, and RunLength. Each round-tripped only through this library's own
/// decoders until 2026-10-18, and three barcode writers that night were found unreadable by others
/// for exactly that reason: a writer and its reader can share a mistake.
/// </summary>
public class StreamEncoderIndependentRenderTests
{
    private const int BilevelWidth = 200, BilevelHeight = 60;

    /// <summary>Group 4 with <c>/BlackIs1 false</c>: a 0 sample is black, as DeviceGray reads it.</summary>
    [Fact]
    public void AnIndependentRendererReadsGroup4_WithBlackAsZero()
    {
        byte[] samples = Bilevel(blackIsOne: false, out int stride);
        byte[] g4 = CcittFaxEncoder.Encode(samples, BilevelWidth, BilevelHeight, blackIs1: false);
        byte[,] gray = IndependentImageRender.RenderGray(g4,
            $"/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns {BilevelWidth} /Rows {BilevelHeight} >>",
            BilevelWidth, BilevelHeight);
        AssertBilevel(samples, stride, blackIsOne: false, gray);
    }

    /// <summary>Group 4 with <c>/BlackIs1 true</c>, drawn through <c>/Decode [1 0]</c> so a 1 sample shows black.</summary>
    [Fact]
    public void AnIndependentRendererReadsGroup4_WithBlackAsOne()
    {
        byte[] samples = Bilevel(blackIsOne: true, out int stride);
        byte[] g4 = CcittFaxEncoder.Encode(samples, BilevelWidth, BilevelHeight, blackIs1: true);
        byte[,] gray = IndependentImageRender.RenderGray(g4,
            $"/ColorSpace /DeviceGray /BitsPerComponent 1 /Decode [1 0] /Filter /CCITTFaxDecode /DecodeParms << /K -1 /Columns {BilevelWidth} /Rows {BilevelHeight} /BlackIs1 true >>",
            BilevelWidth, BilevelHeight);
        AssertBilevel(samples, stride, blackIsOne: true, gray);
    }

    /// <summary>LZW at each <c>/EarlyChange</c>, over enough grey samples to cross the 9-, 10- and 11-bit code widths.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void AnIndependentRendererReadsLzw(int earlyChange)
    {
        const int width = 96, height = 64;
        byte[] samples = Grey(width, height);
        byte[] lzw = LzwEncodeFilter.Encode(samples, earlyChange);
        string parms = earlyChange == 1 ? "" : " /DecodeParms << /EarlyChange 0 >>";
        AssertGrey(samples, width, height, IndependentImageRender.RenderGray(lzw,
            "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /LZWDecode" + parms, width, height));
    }

    /// <summary>RunLength: runs and literals both, the grey samples holding long repeats and noise.</summary>
    [Fact]
    public void AnIndependentRendererReadsRunLength()
    {
        const int width = 96, height = 64;
        byte[] samples = Grey(width, height);
        byte[] rle = RunLengthEncodeFilter.Encode(samples);
        AssertGrey(samples, width, height, IndependentImageRender.RenderGray(rle,
            "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /RunLengthDecode", width, height));
    }

    /// <summary>Text-like strokes, a rule and scattered dots, one bit a pixel; <paramref name="blackIsOne"/> says which bit value is black.</summary>
    private static byte[] Bilevel(bool blackIsOne, out int stride)
    {
        stride = (BilevelWidth + 7) / 8;
        var bits = new byte[stride * BilevelHeight];
        bool Black(int x, int y) =>
            (y > 8 && y < 22 && (x / 7) % 3 == 0) || (y == 40 && x > 10 && x < 190) || ((x * 31 + y * 17) % 97 == 0);
        for (int y = 0; y < BilevelHeight; y++)
            for (int x = 0; x < BilevelWidth; x++)
                if (Black(x, y) == blackIsOne) bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        return bits;
    }

    private static void AssertBilevel(byte[] samples, int stride, bool blackIsOne, byte[,] gray)
    {
        int mismatches = 0;
        string first = "";
        for (int y = 0; y < BilevelHeight; y++)
            for (int x = 0; x < BilevelWidth; x++)
            {
                bool one = ((samples[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1) == 1;
                bool black = one == blackIsOne;
                if (black != (gray[y, x] < 128) && mismatches++ == 0) first = $"({x},{y})";
            }
        Assert.True(mismatches == 0, $"{mismatches} pixels differ from the input; first at {first}");
        Assert.True(Count(gray, v => v < 128) > 500, "too few black pixels to tell a decode from a blank");
    }

    /// <summary>Gradients, flat runs and noise.</summary>
    private static byte[] Grey(int width, int height)
    {
        var samples = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                samples[y * width + x] = (byte)(y < 16 ? x * 2 : y < 32 ? 200 : ((x * 73 + y * 151) ^ (x * y)) & 0xFF);
        return samples;
    }

    private static void AssertGrey(byte[] samples, int width, int height, byte[,] gray)
    {
        int worst = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                worst = Math.Max(worst, Math.Abs(gray[y, x] - samples[y * width + x]));
        Assert.True(worst <= 1, $"a rendered grey differs from its sample by {worst}");
    }

    private static int Count(byte[,] gray, Func<byte, bool> predicate)
    {
        int n = 0;
        foreach (byte v in gray) if (predicate(v)) n++;
        return n;
    }
}