// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Filters;

/// <summary>
/// The JBIG2 encoder's streams read by a decoder this repository did not write (<see cref="IndependentImageRender"/>): a PDF whose one image is the stream and the pixels are compared to the input.
/// </summary>
/// <remarks>
/// <para><b>Why an external decoder.</b> <c>Jbig2SymbolEncoderTests</c> round-trips through this
/// library's own decoder and asserts the segments the stream declares; a rule both halves read the same
/// wrong way — a context layout, a sign convention in a delta, the order of a header field — passes
/// every one of those. The renderer shares no code with either half.</para>
/// <para><b>One page, one image, drawn one pixel per point</b>, rendered at scale 1 with image
/// smoothing off, so a rendered pixel is an image pixel and the comparison is exact rather than a
/// similarity.</para>
/// </remarks>
public class Jbig2IndependentRenderTests
{
    [Fact]
    public void AnIndependentRendererDrawsTheSymbolStreamToTheInputPixels()
    {
        const int width = 300, height = 120, stride = 38;
        byte[] bits = Page(width, height, stride);
        byte[] jbig2 = Jbig2EncodeFilter.EncodeWithSymbols(bits, width, height, stride);
        Assert.NotNull(jbig2);
        Assert.Contains((byte)0x00, jbig2);   // a stream, not a refusal

        AssertPdfiumRenders(bits, jbig2, width, height, stride);
    }

    [Fact]
    public void AnIndependentRendererDrawsTheGenericStreamToTheInputPixels()
    {
        const int width = 300, height = 120, stride = 38;
        byte[] bits = Page(width, height, stride);
        byte[] jbig2 = Jbig2EncodeFilter.Encode(bits, width, height, stride);
        Assert.NotNull(jbig2);

        AssertPdfiumRenders(bits, jbig2, width, height, stride);
    }

    private static void AssertPdfiumRenders(byte[] bits, byte[] jbig2, int width, int height, int stride)
    {
        byte[,] gray = IndependentImageRender.RenderGray(jbig2, "/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode", width, height);

        int mismatches = 0;
        var first = "";
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int black = (bits[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1;
                int rendered = gray[y, x] < 128 ? 1 : 0;
                if (black != rendered && mismatches++ == 0) first = $"({x},{y}): input {black}, the independent renderer {rendered}";
            }
        Assert.True(mismatches == 0, $"{mismatches} pixels differ from the input; first at {first}");
    }
    /// <summary>Repeated glyph boxes with distinct holes, a rule, and a lone dot: symbols, a residual, a one-pixel symbol.</summary>
    private static byte[] Page(int width, int height, int stride)
    {
        var bits = new byte[stride * height];
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 12; col++)
            {
                int glyph = (row + col) % 5;
                int x = 6 + col * 24, y = 6 + row * 22;
                for (int yy = y; yy < y + 15; yy++)
                    for (int xx = x; xx < x + 11; xx++)
                        bits[yy * stride + (xx >> 3)] |= (byte)(0x80 >> (xx & 7));
                int hx = x + 2 + glyph, hy = y + 3 + glyph;
                bits[hy * stride + (hx >> 3)] &= (byte)~(0x80 >> (hx & 7));
            }
        for (int x = 10; x < 290; x++)
            for (int y = 100; y < 103; y++)
                bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        bits[110 * stride + (150 >> 3)] |= (byte)(0x80 >> (150 & 7));
        return bits;
    }
}
