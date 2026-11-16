// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The encoding side of T.88's arithmetic procedures, written from the standard for tests: Annex A's
/// integer and symbol-ID coding, and the generic and refinement contexts (as an independent decoder forms them).
/// A stream built here is an oracle the decoder did not write.
/// </summary>
/// <remarks>
/// Each procedure takes a block of contexts of its own. Which number a context has does not matter
/// to the decoder; that each procedure has its own states, and keeps them across calls, does.
/// </remarks>
internal sealed class Jbig2TestEncoder
{
    private readonly MqEncoder _mq = new MqEncoder(1 << 20);
    private int _next;

    /// <summary>A fresh block of <paramref name="size"/> contexts.</summary>
    internal int Block(int size) { int at = _next; _next += size; return at; }

    internal byte[] Finish() => _mq.Finish();

    /// <summary>An integer (Annex A.2), or OOB when <paramref name="value"/> is null.</summary>
    internal void Int(int block, int? value)
    {
        int prev = 1;
        void Bit(int b)
        {
            _mq.Encode(block + prev, b);
            prev = prev < 256 ? (prev << 1) | b : (((prev << 1) | b) & 0x1FF) | 256;
        }
        void Bits(long v, int count) { for (int i = count - 1; i >= 0; i--) Bit((int)((v >> i) & 1)); }

        if (value == null) { Bit(1); Bit(0); Bits(0, 2); return; }
        long m = System.Math.Abs((long)value.Value);
        Bit(value.Value < 0 ? 1 : 0);
        if (m < 4)         { Bit(0); Bits(m, 2); }
        else if (m < 20)   { Bit(1); Bit(0); Bits(m - 4, 4); }
        else if (m < 84)   { Bit(1); Bit(1); Bit(0); Bits(m - 20, 6); }
        else if (m < 340)  { Bit(1); Bit(1); Bit(1); Bit(0); Bits(m - 84, 8); }
        else if (m < 4436) { Bit(1); Bit(1); Bit(1); Bit(1); Bit(0); Bits(m - 340, 12); }
        else               { Bit(1); Bit(1); Bit(1); Bit(1); Bit(1); Bits(m - 4436, 32); }
    }

    /// <summary>A symbol ID in <paramref name="length"/> bits (Annex A.3).</summary>
    internal void Id(int block, int length, int id)
    {
        int prev = 1;
        for (int i = length - 1; i >= 0; i--)
        {
            int b = (id >> i) & 1;
            _mq.Encode(block + prev, b);
            prev = (prev << 1) | b;
        }
    }

    /// <summary>Template 0's default AT pixels A1..A4, as (dx, dy).</summary>
    internal static readonly (int X, int Y)[] DefaultAt = { (3, -1), (-3, -1), (2, -2), (-2, -2) };

    /// <summary>
    /// A generic region, GBTEMPLATE 0, TPGDON off, with the AT pixels <paramref name="at"/> (A1..A4)
    /// or the defaults.
    /// </summary>
    internal void Generic(int block, Bitmap b, (int X, int Y)[] at = null, Bitmap skip = null)
    {
        at ??= DefaultAt;
        for (int y = 0; y < b.H; y++)
            for (int x = 0; x < b.W; x++)
            {
                if (skip != null && skip[x, y] != 0) continue;   // USESKIP: not coded, and 0 in b
                int c = b[x - 1, y] | b[x - 2, y] << 1 | b[x - 3, y] << 2 | b[x - 4, y] << 3
                      | b[x + at[0].X, y + at[0].Y] << 4 | b[x + 2, y - 1] << 5 | b[x + 1, y - 1] << 6
                      | b[x, y - 1] << 7 | b[x - 1, y - 1] << 8 | b[x - 2, y - 1] << 9
                      | b[x + at[1].X, y + at[1].Y] << 10 | b[x + at[2].X, y + at[2].Y] << 11 | b[x + 1, y - 2] << 12
                      | b[x, y - 2] << 13 | b[x - 1, y - 2] << 14 | b[x + at[3].X, y + at[3].Y] << 15;
                _mq.Encode(block + c, b[x, y]);
            }
    }

    /// <summary>
    /// A generic region, GBTEMPLATE 2, TPGDON off, its one AT pixel at (<paramref name="atX"/>, -1),
    /// as an independent decoder's template-2 decoder forms CONTEXT.
    /// </summary>
    internal void Generic2(int block, Bitmap b, int atX, Bitmap skip = null)
    {
        for (int y = 0; y < b.H; y++)
            for (int x = 0; x < b.W; x++)
            {
                if (skip != null && skip[x, y] != 0) continue;
                int c = b[x - 1, y] | b[x - 2, y] << 1 | b[x + atX, y - 1] << 2
                      | b[x + 1, y - 1] << 3 | b[x, y - 1] << 4 | b[x - 1, y - 1] << 5 | b[x - 2, y - 1] << 6
                      | b[x + 1, y - 2] << 7 | b[x, y - 2] << 8 | b[x - 1, y - 2] << 9;
                _mq.Encode(block + c, b[x, y]);
            }
    }

    /// <summary>
    /// A refinement region, GRTEMPLATE 0 with both AT pixels at (-1, -1), TPGRON off, the reference
    /// read at (x - <paramref name="dx"/>, y - <paramref name="dy"/>).
    /// </summary>
    internal void Refinement(int block, Bitmap t, Bitmap r, int dx, int dy, int template = 0)
    {
        for (int y = 0; y < t.H; y++)
            for (int x = 0; x < t.W; x++)
            {
                int rx = x - dx, ry = y - dy;
                if (template == 1)
                {
                    // jbig2_decode_refinement_template1_unopt's CONTEXT.
                    int c1 = t[x - 1, y] | t[x + 1, y - 1] << 1 | t[x, y - 1] << 2 | t[x - 1, y - 1] << 3
                           | r[rx + 1, ry + 1] << 4 | r[rx, ry + 1] << 5 | r[rx + 1, ry] << 6
                           | r[rx, ry] << 7 | r[rx - 1, ry] << 8 | r[rx, ry - 1] << 9;
                    _mq.Encode(block + c1, t[x, y]);
                    continue;
                }
                int c = t[x - 1, y] | t[x + 1, y - 1] << 1 | t[x, y - 1] << 2 | t[x - 1, y - 1] << 3
                      | r[rx + 1, ry + 1] << 4 | r[rx, ry + 1] << 5 | r[rx - 1, ry + 1] << 6
                      | r[rx + 1, ry] << 7 | r[rx, ry] << 8 | r[rx - 1, ry] << 9
                      | r[rx + 1, ry - 1] << 10 | r[rx, ry - 1] << 11 | r[rx - 1, ry - 1] << 12;
                _mq.Encode(block + c, t[x, y]);
            }
    }

    /// <summary>A bitmap for tests, pixel access clipped to 0 outside it.</summary>
    internal sealed class Bitmap
    {
        internal readonly int W, H;
        private readonly bool[] _p;

        internal Bitmap(int w, int h) { W = w; H = h; _p = new bool[w * h]; }

        internal int this[int x, int y]
        {
            get => x < 0 || y < 0 || x >= W || y >= H ? 0 : _p[y * W + x] ? 1 : 0;
            set { if (x >= 0 && y >= 0 && x < W && y < H) _p[y * W + x] = value != 0; }
        }

        internal static Bitmap Random(int w, int h, uint seed)
        {
            var b = new Bitmap(w, h);
            uint state = seed;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    state = state * 1103515245 + 12345;
                    b[x, y] = ((state >> 16) % 3) == 0 ? 1 : 0;
                }
            return b;
        }

        internal Bitmap Clone(int w, int h)
        {
            var c = new Bitmap(w, h);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) c[x, y] = this[x, y];
            return c;
        }

        /// <summary>ORs this bitmap onto <paramref name="page"/> at (x, y).</summary>
        internal void DrawOn(Bitmap page, int x0, int y0)
        {
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                if (this[x, y] != 0) page[x0 + x, y0 + y] = 1;
        }

        /// <summary>Packed rows, MSB first, 1 = black: the decoder's output form.</summary>
        internal byte[] Packed()
        {
            int stride = (W + 7) / 8;
            var bytes = new byte[stride * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                if (this[x, y] != 0) bytes[y * stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            return bytes;
        }
    }

    // ---------------------------------------------------------------- segments

    internal static void Segment(Stream s, int number, int type, byte[] referredTo, byte[] body)
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

    internal static byte[] PageInfo(int w, int h)
    {
        var b = new MemoryStream();
        U32(b, w); U32(b, h); U32(b, 0); U32(b, 0);
        b.WriteByte(0); b.WriteByte(0); b.WriteByte(0);
        return b.ToArray();
    }

    internal static void U32(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    internal static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

    internal static readonly byte[] DefaultGenericAt = { 3, 0xFF, 0xFD, 0xFF, 2, 0xFE, 0xFE, 0xFE };
    internal static readonly byte[] RefinementAt = { 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>
    /// A plain (non-aggregate) arithmetic symbol dictionary of one height class, every symbol
    /// exported.
    /// </summary>
    internal static byte[] PlainDictionary(IReadOnlyList<Bitmap> symbols, (int X, int Y)[] at = null)
    {
        var e = new Jbig2TestEncoder();
        int generic = e.Block(1 << 16), iadh = e.Block(512), iadw = e.Block(512), iaex = e.Block(512);
        // A height class per run of equal heights, each ended by an OOB width (§6.5.5).
        int height = 0, width = 0;
        for (int i = 0; i < symbols.Count; i++)
        {
            Bitmap sym = symbols[i];
            if (i == 0 || sym.H != height)
            {
                if (i > 0) e.Int(iadw, null);
                e.Int(iadh, sym.H - height);
                height = sym.H;
                width = 0;
            }
            e.Int(iadw, sym.W - width);
            width = sym.W;
            e.Generic(generic, sym, at);
        }
        e.Int(iadw, null);
        e.Int(iaex, 0);
        e.Int(iaex, symbols.Count);

        var body = new MemoryStream();
        U16(body, 0);                                        // SDHUFF 0, SDREFAGG 0, SDTEMPLATE 0
        foreach ((int X, int Y) a in at ?? DefaultAt) { body.WriteByte((byte)(sbyte)a.X); body.WriteByte((byte)(sbyte)a.Y); }
        U32(body, symbols.Count); U32(body, symbols.Count);
        byte[] coded = e.Finish();
        body.Write(coded, 0, coded.Length);
        return body.ToArray();
    }

    /// <summary>The fixed part of a text region's body: region information, flags, counts.</summary>
    /// <param name="layout">SBFLAGS bits beyond SBREFINE; TOPLEFT (1 &lt;&lt; 4) by default.</param>
    internal static void TextRegionHeader(Stream body, int w, int h, bool refine, int instances,
        int layout = 1 << 4, int x = 0, int y = 0)
    {
        U32(body, w); U32(body, h); U32(body, x); U32(body, y);
        body.WriteByte(0);                                   // the region onto the page: OR
        U16(body, (refine ? 0x0002 : 0) | layout);           // SBHUFF 0, one strip, SBRTEMPLATE 0
        if (refine) body.Write(RefinementAt, 0, 4);
        U32(body, instances);
    }
}
