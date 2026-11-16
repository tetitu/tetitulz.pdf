// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The generic region's context templates, checked against a context formed here independently of
/// the decoder's tables: a bitmap is MQ-encoded with T.88's template, then decoded.
/// </summary>
/// <remarks>
/// A round trip through the decoder's own table would agree with itself whatever the table said.
/// The context below is written from T.88 Figure 6 as an independent decoder forms it (bit 0 is x-1 on the
/// current row, bit 9 is x-3 on the row above), so a decoder reading other neighbours decodes
/// other pixels.
/// </remarks>
public class Jbig2GenericTemplateTests
{
    private const int Width = 40, Height = 24;

    /// <summary>
    /// GBTEMPLATE 3 with the AT pixel at (2, -1). The decoder read row y-1 as x-2 .. x+2 plus the AT
    /// pixel (x+2 twice, x-3 never), so the region came back different from what was encoded.
    /// </summary>
    [Fact]
    public void Template3_DecodesWhatItsSpecifiedContextEncoded()
    {
        byte[] bits = Pattern();

        byte[] stream = Stream(Template3Encode(bits), template: 3, at: new sbyte[] { 2, -1 });
        byte[] decoded = Jbig2Decoder.Decode(stream, null, out int w, out int h);

        Assert.Equal((Width, Height), (w, h));
        Assert.Equal(bits, decoded);
    }

    /// <summary>
    /// Hostile header: the region, the last segment, declares a data length of 2^31. Its body runs
    /// to the end of the data, as a length past the data always did. Cast to int, the length went
    /// negative, the body read refused it, and the page was lost.
    /// </summary>
    [Fact]
    public void Hostile_ALastSegmentLengthPastInt32_RunsToTheEnd()
    {
        byte[] bits = Pattern();

        byte[] stream = Stream(Template3Encode(bits), template: 3, at: new sbyte[] { 2, -1 }, regionLength: 0x80000000u);
        byte[] decoded = Jbig2Decoder.Decode(stream, null, out _, out _);

        Assert.Equal(bits, decoded);
    }

    /// <summary>A deterministic, irregular bitmap: no row repeats and every context occurs.</summary>
    private static byte[] Pattern()
    {
        int stride = (Width + 7) / 8;
        var bits = new byte[stride * Height];
        uint state = 12345;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                state = state * 1103515245 + 12345;
                if (((state >> 16) & 3) == 0) bits[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        return bits;
    }

    private static int Pixel(byte[] bits, int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return 0;
        int stride = (Width + 7) / 8;
        return (bits[y * stride + (x >> 3)] >> (7 - (x & 7))) & 1;
    }

    /// <summary>T.88 Figure 6, as an independent decoder's template-3 decoder forms CONTEXT.</summary>
    private static byte[] Template3Encode(byte[] bits)
    {
        var mq = new MqEncoder(1 << 10);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int context =
                      Pixel(bits, x - 1, y)
                    | Pixel(bits, x - 2, y) << 1
                    | Pixel(bits, x - 3, y) << 2
                    | Pixel(bits, x - 4, y) << 3
                    | Pixel(bits, x + 2, y - 1) << 4      // the AT pixel
                    | Pixel(bits, x + 1, y - 1) << 5
                    | Pixel(bits, x, y - 1) << 6
                    | Pixel(bits, x - 1, y - 1) << 7
                    | Pixel(bits, x - 2, y - 1) << 8
                    | Pixel(bits, x - 3, y - 1) << 9;
                mq.Encode(context, Pixel(bits, x, y));
            }
        return mq.Finish();
    }

    /// <summary>A page-information segment and one immediate lossless generic region.</summary>
    private static byte[] Stream(byte[] coded, int template, sbyte[] at, uint? regionLength = null)
    {
        var s = new MemoryStream();

        var page = new MemoryStream();
        U32(page, Width); U32(page, Height); U32(page, 0); U32(page, 0);
        page.WriteByte(0); page.WriteByte(0); page.WriteByte(0);
        Header(s, 1, 48, (uint)page.Length);
        page.WriteTo(s);

        var region = new MemoryStream();
        U32(region, Width); U32(region, Height); U32(region, 0); U32(region, 0);
        region.WriteByte(0);                                 // OR
        region.WriteByte((byte)(template << 1));             // arithmetic, GBTEMPLATE, TPGDON off
        foreach (sbyte b in at) region.WriteByte(unchecked((byte)b));
        region.Write(coded, 0, coded.Length);
        Header(s, 2, 39, regionLength ?? (uint)region.Length);
        region.WriteTo(s);

        return s.ToArray();
    }

    private static void Header(Stream s, int number, int type, uint length)
    {
        U32(s, number);
        s.WriteByte((byte)type);
        s.WriteByte(0);
        s.WriteByte(1);
        U32(s, unchecked((int)length));
    }

    private static void U32(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
