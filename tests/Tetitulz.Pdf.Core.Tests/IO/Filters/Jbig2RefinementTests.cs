// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Refinement regions (T.88 §6.3, §7.4.7): the reference a refinement refines, and TPGRON. The
/// streams are encoded here with contexts written out as an independent decoder forms them, not with the
/// decoder's tables, so a decoder that disagrees decodes other pixels.
/// </summary>
public class Jbig2RefinementTests
{
    private const int PageW = 64, PageH = 32, RegionX = 8, RegionY = 4, W = 40, H = 20;

    /// <summary>
    /// A generic region draws P at (8, 4), or (intermediate) is kept unseen; a refinement there
    /// with TPGRON on then refines it into T and replaces what is under it.
    /// <list type="bullet">
    /// <item>Through the page: the refinement names no region, so its reference is the page under
    /// it (§7.4.7.4), not a null reference read as white.</item>
    /// <item>Through an intermediate region: the refinement names segment 2, a generic region
    /// decoded and kept (§7.4.7.4), not skipped.</item>
    /// </list>
    /// Both also need TPGRON read as the standard says: copying the reference's row instead of
    /// decoding the pixels whose neighbourhood is not uniform puts the arithmetic decoder out of step.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefinementWithTpgronRefinesItsReference(bool viaIntermediateRegion)
    {
        byte[] p = Pattern(W, H, seed: 7, density: 3);
        byte[] t = Refined(p);

        var s = new MemoryStream();
        Segment(s, 1, 48, null, PageInfo());
        Segment(s, 2, viaIntermediateRegion ? 36 : 38, null,
            GenericRegionBody(Template3Encode(p, W, H)));
        Segment(s, 3, 42, viaIntermediateRegion ? new byte[] { 2 } : null,
            RefinementBody(RefinementTemplate0Encode(t, p)));

        byte[] page = Jbig2Decoder.Decode(s.ToArray(), null, out int w, out int h);

        Assert.Equal((PageW, PageH), (w, h));
        byte[] expected = new byte[(PageW + 7) / 8 * PageH];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (Pixel(t, W, H, x, y) != 0) Set(expected, PageW, x + RegionX, y + RegionY);
        Assert.Equal(expected, page);
        Assert.NotEqual(p, t);                     // potency: the refinement changed something
    }

    /// <summary>
    /// An intermediate refinement region (type 40) is kept for a later segment, not drawn: the
    /// page keeps P where the immediate one would have put T.
    /// </summary>
    [Fact]
    public void AnIntermediateRefinementIsKeptNotDrawn()
    {
        byte[] p = Pattern(W, H, seed: 7, density: 3);

        var s = new MemoryStream();
        Segment(s, 1, 48, null, PageInfo());
        Segment(s, 2, 38, null, GenericRegionBody(Template3Encode(p, W, H)));
        Segment(s, 3, 40, null, RefinementBody(RefinementTemplate0Encode(Refined(p), p)));

        byte[] page = Jbig2Decoder.Decode(s.ToArray(), null, out _, out _);

        byte[] expected = new byte[(PageW + 7) / 8 * PageH];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (Pixel(p, W, H, x, y) != 0) Set(expected, PageW, x + RegionX, y + RegionY);
        Assert.Equal(expected, page);
    }

    // ---------------------------------------------------------------- bitmaps

    private static byte[] Pattern(int w, int h, uint seed, int density)
    {
        var bits = new byte[(w + 7) / 8 * h];
        uint state = seed;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                state = state * 1103515245 + 12345;
                if ((state >> 16) % (uint)density == 0) Set(bits, w, x, y);
            }
        return bits;
    }

    /// <summary>
    /// The target: P with every pixel whose 3x3 neighbourhood in P is mixed flipped on odd rows.
    /// Even rows equal P, so they can be coded as typical; odd rows cannot where they differ.
    /// </summary>
    private static byte[] Refined(byte[] p)
    {
        var t = (byte[])p.Clone();
        for (int y = 1; y < H; y += 2)
            for (int x = 0; x < W; x++)
                if (Implicit(p, x, y) < 0 && (x + y) % 3 == 0)
                {
                    if (Pixel(p, W, H, x, y) != 0) Clear(t, W, x, y); else Set(t, W, x, y);
                }
        return t;
    }

    private static int Pixel(byte[] bits, int w, int h, int x, int y)
        => x < 0 || y < 0 || x >= w || y >= h ? 0 : (bits[y * ((w + 7) / 8) + (x >> 3)] >> (7 - (x & 7))) & 1;

    private static void Set(byte[] bits, int w, int x, int y) => bits[y * ((w + 7) / 8) + (x >> 3)] |= (byte)(0x80 >> (x & 7));

    private static void Clear(byte[] bits, int w, int x, int y) => bits[y * ((w + 7) / 8) + (x >> 3)] &= (byte)~(0x80 >> (x & 7));

    /// <summary>An independent decoder's implicit_value: the pixel when its eight neighbours match it, else -1.</summary>
    private static int Implicit(byte[] r, int x, int y)
    {
        int m = Pixel(r, W, H, x, y);
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (Pixel(r, W, H, x + dx, y + dy) != m) return -1;
        return m;
    }

    // ---------------------------------------------------------------- encoders

    /// <summary>GBTEMPLATE 3, AT (2, -1), as an independent decoder's template-3 decoder forms CONTEXT.</summary>
    private static byte[] Template3Encode(byte[] bits, int w, int h)
    {
        var mq = new MqEncoder(1 << 10);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = Pixel(bits, w, h, x - 1, y) | Pixel(bits, w, h, x - 2, y) << 1
                      | Pixel(bits, w, h, x - 3, y) << 2 | Pixel(bits, w, h, x - 4, y) << 3
                      | Pixel(bits, w, h, x + 2, y - 1) << 4 | Pixel(bits, w, h, x + 1, y - 1) << 5
                      | Pixel(bits, w, h, x, y - 1) << 6 | Pixel(bits, w, h, x - 1, y - 1) << 7
                      | Pixel(bits, w, h, x - 2, y - 1) << 8 | Pixel(bits, w, h, x - 3, y - 1) << 9;
                mq.Encode(c, Pixel(bits, w, h, x, y));
            }
        return mq.Finish();
    }

    /// <summary>
    /// GRTEMPLATE 0 with both AT pixels at (-1, -1), as an independent decoder's template-0 refinement decoder
    /// forms CONTEXT; TPGRON on, its start context 0x100 (the reference's centre alone). Even rows
    /// are coded as typical.
    /// </summary>
    private static byte[] RefinementTemplate0Encode(byte[] t, byte[] r)
    {
        var mq = new MqEncoder(1 << 13);
        bool ltp = false;
        for (int y = 0; y < H; y++)
        {
            bool typical = y % 2 == 0;
            mq.Encode(0x100, typical != ltp ? 1 : 0);
            ltp = typical;

            for (int x = 0; x < W; x++)
            {
                if (ltp && Implicit(r, x, y) >= 0) continue;
                int c = Pixel(t, W, H, x - 1, y) | Pixel(t, W, H, x + 1, y - 1) << 1
                      | Pixel(t, W, H, x, y - 1) << 2 | Pixel(t, W, H, x - 1, y - 1) << 3
                      | Pixel(r, W, H, x + 1, y + 1) << 4 | Pixel(r, W, H, x, y + 1) << 5
                      | Pixel(r, W, H, x - 1, y + 1) << 6 | Pixel(r, W, H, x + 1, y) << 7
                      | Pixel(r, W, H, x, y) << 8 | Pixel(r, W, H, x - 1, y) << 9
                      | Pixel(r, W, H, x + 1, y - 1) << 10 | Pixel(r, W, H, x, y - 1) << 11
                      | Pixel(r, W, H, x - 1, y - 1) << 12;
                mq.Encode(c, Pixel(t, W, H, x, y));
            }
        }
        return mq.Finish();
    }

    // ---------------------------------------------------------------- segments

    private static byte[] PageInfo()
    {
        var b = new MemoryStream();
        U32(b, PageW); U32(b, PageH); U32(b, 0); U32(b, 0);
        b.WriteByte(0); b.WriteByte(0); b.WriteByte(0);
        return b.ToArray();
    }

    private static void RegionInfo(Stream b, int op)
    {
        U32(b, W); U32(b, H); U32(b, RegionX); U32(b, RegionY);
        b.WriteByte((byte)op);
    }

    private static byte[] GenericRegionBody(byte[] coded)
    {
        var b = new MemoryStream();
        RegionInfo(b, 0);
        b.WriteByte(3 << 1);                     // arithmetic, GBTEMPLATE 3
        b.WriteByte(2); b.WriteByte(0xFF);       // AT (2, -1)
        b.Write(coded, 0, coded.Length);
        return b.ToArray();
    }

    private static byte[] RefinementBody(byte[] coded)
    {
        var b = new MemoryStream();
        RegionInfo(b, 4);                        // REPLACE
        b.WriteByte(0x02);                       // GRTEMPLATE 0, TPGRON on
        b.WriteByte(0xFF); b.WriteByte(0xFF);    // AT1 (-1, -1)
        b.WriteByte(0xFF); b.WriteByte(0xFF);    // AT2 (-1, -1)
        b.Write(coded, 0, coded.Length);
        return b.ToArray();
    }

    private static void Segment(Stream s, int number, int type, byte[] referredTo, byte[] body)
    {
        U32(s, number);
        s.WriteByte((byte)type);
        int count = referredTo?.Length ?? 0;
        s.WriteByte((byte)(count << 5));
        if (referredTo != null) s.Write(referredTo, 0, referredTo.Length);
        s.WriteByte(1);
        U32(s, body.Length);
        s.Write(body, 0, body.Length);
    }

    private static void U32(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }
}
