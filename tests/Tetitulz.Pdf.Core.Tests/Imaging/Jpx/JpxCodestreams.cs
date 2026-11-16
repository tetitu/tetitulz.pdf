// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// Builds JPEG 2000 codestreams segment by segment, so a parser test states its input in the test:
/// every marker, length and parameter below is written out as T.800 Annex A lays it out.
/// </summary>
internal static class JpxCodestreams
{
    internal static readonly byte[] Soc = { 0xFF, 0x4F };
    internal static readonly byte[] Eoc = { 0xFF, 0xD9 };

    /// <summary>A marker segment: the marker, a 16-bit length counting itself, then the payload.</summary>
    internal static byte[] Segment(int marker, params byte[] payload)
    {
        int length = payload.Length + 2;
        var s = new byte[4 + payload.Length];
        s[0] = (byte)(marker >> 8); s[1] = (byte)marker;
        s[2] = (byte)(length >> 8); s[3] = (byte)length;
        Array.Copy(payload, 0, s, 4, payload.Length);
        return s;
    }

    /// <summary>SIZ (T.800 §A.5.1). Each component is (Ssiz, XRsiz, YRsiz).</summary>
    internal static byte[] Siz(long xsiz, long ysiz, long xosiz, long yosiz, long xtsiz, long ytsiz,
        long xtosiz, long ytosiz, (int Ssiz, int XR, int YR)[] components, int rsiz = 0, int? lengthOverride = null)
    {
        var p = new List<byte> { (byte)(rsiz >> 8), (byte)rsiz };
        foreach (long v in new[] { xsiz, ysiz, xosiz, yosiz, xtsiz, ytsiz, xtosiz, ytosiz }) AddU32(p, v);
        p.Add((byte)(components.Length >> 8)); p.Add((byte)components.Length);
        foreach (var c in components) { p.Add((byte)c.Ssiz); p.Add((byte)c.XR); p.Add((byte)c.YR); }
        byte[] seg = Segment(0xFF51, p.ToArray());
        if (lengthOverride.HasValue) { seg[2] = (byte)(lengthOverride.Value >> 8); seg[3] = (byte)lengthOverride.Value; }
        return seg;
    }

    /// <summary>A 64x64 single-tile SIZ with the given components (default one unsigned 8-bit component).</summary>
    internal static byte[] Siz64(int components = 1, int rsiz = 0)
    {
        var comps = new (int, int, int)[components];
        for (int i = 0; i < components; i++) comps[i] = (0x07, 1, 1);
        return Siz(64, 64, 0, 0, 64, 64, 0, 0, comps, rsiz);
    }

    /// <summary>COD (T.800 §A.6.1). <c>precincts</c> non-null sets Scod bit 0 and appends them.</summary>
    internal static byte[] Cod(int order = 0, int layers = 1, int mct = 0, int levels = 1, int xcbValue = 4,
        int ycbValue = 4, int style = 0, int transform = 1, byte[]? precincts = null, int scodExtra = 0)
    {
        var p = new List<byte>
        {
            (byte)((precincts != null ? 1 : 0) | scodExtra),
            (byte)order, (byte)(layers >> 8), (byte)layers, (byte)mct,
            (byte)levels, (byte)xcbValue, (byte)ycbValue, (byte)style, (byte)transform,
        };
        if (precincts != null) p.AddRange(precincts);
        return Segment(0xFF52, p.ToArray());
    }

    /// <summary>COC (T.800 §A.6.2): a component index (8 or 16 bits), Scoc, SPcoc.</summary>
    internal static byte[] Coc(int component, bool wide = false, int levels = 1, int xcbValue = 4, int ycbValue = 4,
        int style = 0, int transform = 1, byte[]? precincts = null)
    {
        var p = new List<byte>();
        AddIndex(p, component, wide);
        p.AddRange(new byte[] { (byte)(precincts != null ? 1 : 0), (byte)levels, (byte)xcbValue, (byte)ycbValue, (byte)style, (byte)transform });
        if (precincts != null) p.AddRange(precincts);
        return Segment(0xFF53, p.ToArray());
    }

    /// <summary>QCD without quantisation: guard bits in Sqcd, one 8-bit exponent per sub-band (3·levels + 1).</summary>
    internal static byte[] QcdNone(int levels = 1, int guard = 1, int exponent = 8)
    {
        var p = new List<byte> { (byte)(guard << 5) };
        for (int i = 0; i < 3 * levels + 1; i++) p.Add((byte)(exponent << 3));
        return Segment(0xFF5C, p.ToArray());
    }

    /// <summary>QCD with the raw Sqcd and step-size bytes.</summary>
    internal static byte[] Qcd(int sqcd, params byte[] steps)
    {
        var p = new List<byte> { (byte)sqcd };
        p.AddRange(steps);
        return Segment(0xFF5C, p.ToArray());
    }

    /// <summary>QCC: a component index, then Sqcd and step sizes as for QCD.</summary>
    internal static byte[] Qcc(int component, bool wide, int sqcd, params byte[] steps)
    {
        var p = new List<byte>();
        AddIndex(p, component, wide);
        p.Add((byte)sqcd);
        p.AddRange(steps);
        return Segment(0xFF5D, p.ToArray());
    }

    /// <summary>RGN (T.800 §A.6.3): a component index, Srgn, SPrgn.</summary>
    internal static byte[] Rgn(int component, int shift, bool wide = false, int style = 0)
    {
        var p = new List<byte>();
        AddIndex(p, component, wide);
        p.Add((byte)style);
        p.Add((byte)shift);
        return Segment(0xFF5E, p.ToArray());
    }

    /// <summary>POC (T.800 §A.6.6); component indices are 8 or 16 bits.</summary>
    internal static byte[] Poc(bool wide, params (int Rs, int Cs, int Lye, int Re, int Ce, int Order)[] entries)
    {
        var p = new List<byte>();
        foreach (var e in entries)
        {
            p.Add((byte)e.Rs);
            AddIndex(p, e.Cs, wide);
            p.Add((byte)(e.Lye >> 8)); p.Add((byte)e.Lye);
            p.Add((byte)e.Re);
            AddIndex(p, e.Ce, wide);
            p.Add((byte)e.Order);
        }
        return Segment(0xFF5F, p.ToArray());
    }

    /// <summary>
    /// A tile-part: SOT (Lsot 10, Isot, Psot, TPsot, TNsot), the header segments, SOD, the data.
    /// Psot covers exactly this tile-part unless <c>psot</c> overrides it.
    /// </summary>
    internal static byte[] TilePart(int isot, int tpsot, byte[] header, byte[] data, long? psot = null, int lsot = 10, int tnsot = 0)
    {
        long length = psot ?? 12 + header.Length + 2 + data.Length;
        var p = new List<byte> { 0xFF, 0x90, (byte)(lsot >> 8), (byte)lsot, (byte)(isot >> 8), (byte)isot };
        AddU32(p, length);
        p.Add((byte)tpsot);
        p.Add((byte)tnsot);
        p.AddRange(header);
        p.Add(0xFF); p.Add(0x93);
        p.AddRange(data);
        return p.ToArray();
    }

    internal static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (var p in parts) all.AddRange(p);
        return all.ToArray();
    }

    /// <summary>The smallest valid codestream: one 64x64 8-bit component, one tile-part of five data bytes.</summary>
    internal static byte[] Minimal() =>
        Concat(Soc, Siz64(), Cod(), QcdNone(), TilePart(0, 0, Array.Empty<byte>(), new byte[] { 1, 2, 3, 4, 5 }), Eoc);

    private static void AddU32(List<byte> p, long v)
    {
        p.Add((byte)(v >> 24)); p.Add((byte)(v >> 16)); p.Add((byte)(v >> 8)); p.Add((byte)v);
    }

    private static void AddIndex(List<byte> p, int index, bool wide)
    {
        if (wide) p.Add((byte)(index >> 8));
        p.Add((byte)index);
    }
}
