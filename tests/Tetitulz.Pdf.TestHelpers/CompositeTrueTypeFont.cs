// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A six-glyph TrueType font with one composite glyph, for the three glyph walks that read one:
/// Base's <c>PdfFontSubsetter</c>, Core's <c>TrueTypeSubsetter</c> and the Maintenance plugin's
/// <c>TrueTypeGlyphPruner</c>. <see cref="MinimalTrueType"/> carries no composite glyph.
/// </summary>
/// <remarks>
/// <para>Glyphs: 0 <c>.notdef</c>; 1 for <c>B</c>; 2 unused; 3 for <c>A</c>, a composite over glyph
/// 4; 4 the component; 5 large and unused. A format 4 <c>cmap</c> maps <c>A</c> and <c>B</c>.</para>
/// <para><b>The malformed variant</b> sets <c>MORE_COMPONENTS</c> on glyph 3's only record, and glyph
/// 4's first bytes, read as a record, are flags <c>0x0001</c> and glyph index 5. A walk that reads
/// past the composite's end therefore adds glyph 5, which nothing draws.</para>
/// <para>Assembled here, not through <c>SfntBuilder</c>, so the fixture does not depend on code it
/// is used to test.</para>
/// </remarks>
public static class CompositeTrueTypeFont
{
    /// <summary>The composite glyph's id.</summary>
    public const int Composite = 3;

    /// <summary>Its component's id.</summary>
    public const int Component = 4;

    /// <summary>The unused glyph a walk past the composite's end names.</summary>
    public const int PastTheEnd = 5;

    public static byte[] Build(bool malformed)
    {
        var glyphs = new List<byte[]>
        {
            Simple(12), Simple(20), Simple(40),
            CompositeGlyph(Component, more: malformed),
            ComponentGlyph(),
            Simple(52),
        };

        var glyf = new MemoryStream();
        var loca = new byte[(glyphs.Count + 1) * 4];
        for (int i = 0; i < glyphs.Count; i++)
        {
            U32(loca, i * 4, (uint)glyf.Length);
            glyf.Write(glyphs[i], 0, glyphs[i].Length);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        U32(loca, glyphs.Count * 4, (uint)glyf.Length);

        var head = new byte[54]; U32(head, 0, 0x00010000); U32(head, 12, 0x5F0F3CF5); U16(head, 18, 1000); U16(head, 50, 1);
        var maxp = new byte[32]; U32(maxp, 0, 0x00010000); U16(maxp, 4, glyphs.Count);
        var hhea = new byte[36]; U32(hhea, 0, 0x00010000); U16(hhea, 34, glyphs.Count);
        var hmtx = new byte[glyphs.Count * 4]; for (int i = 0; i < glyphs.Count; i++) U16(hmtx, i * 4, 500);

        return Assemble(new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(), ["glyf"] = glyf.ToArray(), ["head"] = head, ["hhea"] = hhea,
            ["hmtx"] = hmtx, ["loca"] = loca, ["maxp"] = maxp,
        });
    }

    /// <summary>The number of glyphs the font's <c>maxp</c> declares.</summary>
    public static int NumGlyphs(byte[] font) => R16(font, Tables(font)["maxp"].Offset + 4);

    /// <summary>The byte length of glyph <paramref name="gid"/> in the font's <c>glyf</c>.</summary>
    public static int GlyphLength(byte[] font, int gid) { var (s, e) = Glyph(font, gid); return e - s; }

    /// <summary>The first component index of composite glyph <paramref name="gid"/>.</summary>
    public static int ComponentOf(byte[] font, int gid) => R16(font, Glyph(font, gid).Start + 12);

    private static byte[] Simple(int length) { var g = new byte[length]; U16(g, 0, 1); return g; }

    private static byte[] CompositeGlyph(int component, bool more)
    {
        var g = new byte[16];
        U16(g, 0, 0xFFFF);
        U16(g, 10, more ? 0x0020 : 0);
        U16(g, 12, component);
        return g;
    }

    private static byte[] ComponentGlyph() { var g = Simple(24); U16(g, 2, PastTheEnd); return g; }

    private static byte[] Cmap()
    {
        ushort[] end = { 0x41, 0x42, 0xFFFF }, start = { 0x41, 0x42, 0xFFFF };
        short[] delta = { (short)(Composite - 0x41), (short)(1 - 0x42), 1 };
        int segs = end.Length, length = 16 + segs * 8;
        var sub = new byte[length];
        U16(sub, 0, 4); U16(sub, 2, length); U16(sub, 6, segs * 2);
        U16(sub, 8, 4); U16(sub, 10, 1); U16(sub, 12, 2);
        for (int i = 0; i < segs; i++)
        {
            U16(sub, 14 + i * 2, end[i]);
            U16(sub, 16 + segs * 2 + i * 2, start[i]);
            U16(sub, 16 + segs * 4 + i * 2, (ushort)delta[i]);
        }
        var cmap = new byte[12 + length];
        U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, 1); U32(cmap, 8, 12);
        Buffer.BlockCopy(sub, 0, cmap, 12, length);
        return cmap;
    }

    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int directory = 12 + tables.Count * 16;
        long total = directory;
        foreach (var table in tables.Values) total += (table.Length + 3) & ~3;

        var font = new byte[total];
        U32(font, 0, 0x00010000);
        U16(font, 4, tables.Count);
        int record = 12, dataAt = directory;
        foreach (var pair in tables)
        {
            Encoding.ASCII.GetBytes(pair.Key, 0, 4, font, record);
            U32(font, record + 8, (uint)dataAt);
            U32(font, record + 12, (uint)pair.Value.Length);
            Buffer.BlockCopy(pair.Value, 0, font, dataAt, pair.Value.Length);
            dataAt += (pair.Value.Length + 3) & ~3;
            record += 16;
        }
        return font;
    }

    private static Dictionary<string, (int Offset, int Length)> Tables(byte[] font)
    {
        var t = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (int i = 0; i < R16(font, 4); i++)
        {
            int rec = 12 + i * 16;
            t[Encoding.ASCII.GetString(font, rec, 4)] = ((int)R32(font, rec + 8), (int)R32(font, rec + 12));
        }
        return t;
    }

    private static (int Start, int End) Glyph(byte[] font, int gid)
    {
        var t = Tables(font);
        bool longLoca = R16(font, t["head"].Offset + 50) == 1;
        int loca = t["loca"].Offset, glyf = t["glyf"].Offset;
        long Off(int i) => longLoca ? R32(font, loca + i * 4) : R16(font, loca + i * 2) * 2L;
        return (glyf + (int)Off(gid), glyf + (int)Off(gid + 1));
    }

    private static int R16(byte[] d, int p) => (d[p] << 8) | d[p + 1];
    private static uint R32(byte[] d, int p) => (uint)((d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]);
    private static void U16(byte[] d, int p, int v) { d[p] = (byte)(v >> 8); d[p + 1] = (byte)v; }
    private static void U32(byte[] d, int p, uint v) { d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v; }
}
