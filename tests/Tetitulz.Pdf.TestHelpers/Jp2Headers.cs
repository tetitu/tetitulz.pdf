// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Builds JP2 container fragments byte by byte — the signature box, arbitrary boxes, and a
/// <c>jp2h</c> header naming an enumerated colour space — so a JPEG 2000 header test states its
/// input in the test rather than depending on an encoder. Shared by the Core and Base tests.
/// </summary>
public static class Jp2Headers
{
    /// <summary>
    /// A JP2 signature box and a <c>jp2h</c> superbox holding one <c>colr</c> box with the given
    /// enumerated colour space (METH 1).
    /// </summary>
    public static byte[] WithEnumeratedColourSpace(int enumCs)
    {
        byte[] colr = Box("colr", new byte[] { 1, 0, 0, (byte)(enumCs >> 24), (byte)(enumCs >> 16),
                                               (byte)(enumCs >> 8), (byte)enumCs });
        byte[] jp2h = Box("jp2h", colr);
        return Concat(SignatureBox(), jp2h);
    }

    /// <summary>The 12-byte JP2 signature box (<c>jP  </c>, <c>0D 0A 87 0A</c>).</summary>
    public static byte[] SignatureBox() =>
        new byte[] { 0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A };

    /// <summary>A JP2 container: the signature box, then the given top-level boxes in order.</summary>
    public static byte[] Container(params byte[][] topLevelBoxes) => Concat(SignatureBox(), Concat(topLevelBoxes));

    /// <summary>A <c>jp2h</c> superbox holding the given boxes in order (T.800 I.5.3).</summary>
    public static byte[] Jp2h(params byte[][] boxes) => Box("jp2h", Concat(boxes));

    /// <summary>
    /// An <c>ihdr</c> box (T.800 I.5.3.1): HEIGHT, WIDTH, NC, BPC (bit depth − 1, with 0x80 for
    /// signed, or 255 when the components vary), C (7 is JPEG 2000), UnkC and IPR.
    /// </summary>
    public static byte[] Ihdr(uint height, uint width, int components, int bpc, int compression = 7,
        int unknownColourSpace = 0, int ipr = 0)
    {
        var c = new byte[14];
        WriteU32(c, 0, height);
        WriteU32(c, 4, width);
        c[8] = (byte)(components >> 8); c[9] = (byte)components;
        c[10] = (byte)bpc; c[11] = (byte)compression; c[12] = (byte)unknownColourSpace; c[13] = (byte)ipr;
        return Box("ihdr", c);
    }

    /// <summary>A <c>bpcc</c> box (T.800 I.5.3.2): one BPC_i byte per component.</summary>
    public static byte[] Bpcc(params byte[] depths) => Box("bpcc", depths);

    /// <summary>
    /// A <c>colr</c> box using the enumerated method (METH 1) with the given EnumCS, PREC and
    /// APPROX, followed by an optional EP field (T.801 M.11.7.3.1).
    /// </summary>
    public static byte[] ColrEnumerated(long enumCs, int precedence = 0, int approximation = 0, byte[]? ep = null)
    {
        var c = new byte[7 + (ep?.Length ?? 0)];
        c[0] = 1; c[1] = (byte)precedence; c[2] = (byte)approximation;
        WriteU32(c, 3, (uint)enumCs);
        if (ep != null) Array.Copy(ep, 0, c, 7, ep.Length);
        return Box("colr", c);
    }

    /// <summary>A <c>colr</c> box holding an ICC profile: METH 2 (restricted, T.800 I.5.3.3) or 3 (any, T.801 M.11.7.3.2).</summary>
    public static byte[] ColrIcc(byte[] profile, int method = 2, int precedence = 0, int approximation = 0)
    {
        var c = new byte[3 + profile.Length];
        c[0] = (byte)method; c[1] = (byte)precedence; c[2] = (byte)approximation;
        Array.Copy(profile, 0, c, 3, profile.Length);
        return Box("colr", c);
    }

    /// <summary>
    /// A stand-in ICC profile: a 128-byte header carrying the profile size, the data colour space
    /// signature at offset 16, <c>XYZ </c> as the PCS and <c>acsp</c> at 36, then a zero tag count —
    /// enough for a header reader, nothing for an evaluator.
    /// </summary>
    public static byte[] IccProfile(string dataColourSpace, int length = 132)
    {
        var p = new byte[length];
        WriteU32(p, 0, (uint)length);
        for (int i = 0; i < 4; i++) p[16 + i] = (byte)dataColourSpace[i];
        p[20] = (byte)'X'; p[21] = (byte)'Y'; p[22] = (byte)'Z'; p[23] = (byte)' ';
        p[36] = (byte)'a'; p[37] = (byte)'c'; p[38] = (byte)'s'; p[39] = (byte)'p';
        return p;
    }

    /// <summary>
    /// A <c>pclr</c> box (T.800 I.5.3.4): NE entries of NPC columns. <paramref name="columnDepths"/>
    /// holds each column's B_i (bit depth − 1, 0x80 for signed); <paramref name="entries"/> holds
    /// each entry's values, written in the low bits of ceil(bits / 8) bytes.
    /// </summary>
    public static byte[] Pclr(byte[] columnDepths, params long[][] entries)
    {
        var c = new System.Collections.Generic.List<byte>
        {
            (byte)(entries.Length >> 8), (byte)entries.Length, (byte)columnDepths.Length,
        };
        c.AddRange(columnDepths);
        foreach (long[] entry in entries)
        {
            for (int j = 0; j < columnDepths.Length; j++)
            {
                int bytes = ((columnDepths[j] & 0x7F) + 1 + 7) / 8;
                for (int k = bytes - 1; k >= 0; k--) c.Add((byte)(entry[j] >> (8 * k)));
            }
        }
        return Box("pclr", c.ToArray());
    }

    /// <summary>A <c>cmap</c> box (T.800 I.5.3.5): (CMP, MTYP, PCOL) per channel.</summary>
    public static byte[] Cmap(params (int component, int mappingType, int paletteColumn)[] channels)
    {
        var c = new byte[4 * channels.Length];
        for (int i = 0; i < channels.Length; i++)
        {
            c[4 * i] = (byte)(channels[i].component >> 8); c[4 * i + 1] = (byte)channels[i].component;
            c[4 * i + 2] = (byte)channels[i].mappingType; c[4 * i + 3] = (byte)channels[i].paletteColumn;
        }
        return Box("cmap", c);
    }

    /// <summary>A <c>cdef</c> box (T.800 I.5.3.6): N, then (Cn, Typ, Asoc) per description.</summary>
    public static byte[] Cdef(params (int channel, int type, int association)[] descriptions)
    {
        var c = new byte[2 + 6 * descriptions.Length];
        c[0] = (byte)(descriptions.Length >> 8); c[1] = (byte)descriptions.Length;
        for (int i = 0; i < descriptions.Length; i++)
        {
            int o = 2 + 6 * i;
            c[o] = (byte)(descriptions[i].channel >> 8); c[o + 1] = (byte)descriptions[i].channel;
            c[o + 2] = (byte)(descriptions[i].type >> 8); c[o + 3] = (byte)descriptions[i].type;
            c[o + 4] = (byte)(descriptions[i].association >> 8); c[o + 5] = (byte)descriptions[i].association;
        }
        return Box("cdef", c);
    }

    /// <summary>A <c>jplh</c> superbox (T.801 M.11.7) holding the given boxes.</summary>
    public static byte[] Jplh(params byte[][] boxes) => Box("jplh", Concat(boxes));

    /// <summary>A <c>jp2c</c> box (T.800 I.5.4) holding a raw codestream.</summary>
    public static byte[] Jp2c(byte[] codestream) => Box("jp2c", codestream);

    /// <summary>
    /// A whole JP2 file: the signature box, a <c>jp2h</c> holding <paramref name="headerBoxes"/> in
    /// order, then the codestream in a <c>jp2c</c> box — what a PDF consumer test wraps a lossless
    /// fixture in to give it a palette, a channel definition or a colour specification.
    /// </summary>
    public static byte[] Jp2File(byte[] codestream, params byte[][] headerBoxes) =>
        Container(Jp2h(headerBoxes), Jp2c(codestream));

    /// <summary>An <c>opct</c> box (T.801 M.11.7.6) with the given Otyp; 0 and 1 carry no further fields.</summary>
    public static byte[] Opct(int opacityType) => Box("opct", new[] { (byte)opacityType });

    private static void WriteU32(byte[] b, int at, uint v)
    {
        b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
    }

    /// <summary>One box with a 32-bit length: LBox, TBox (<paramref name="fourCc"/>), content.</summary>
    public static byte[] Box(string fourCc, byte[] content)
    {
        var box = new byte[8 + content.Length];
        int len = box.Length;
        box[0] = (byte)(len >> 24); box[1] = (byte)(len >> 16);
        box[2] = (byte)(len >> 8);  box[3] = (byte)len;
        for (int i = 0; i < 4; i++) box[4 + i] = (byte)fourCc[i];
        Array.Copy(content, 0, box, 8, content.Length);
        return box;
    }

    /// <summary>The given byte arrays, end to end.</summary>
    public static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var all = new byte[total];
        int o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, all, o, p.Length); o += p.Length; }
        return all;
    }
}
