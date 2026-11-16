// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A six-glyph TrueType font that maps the Arabic letters beh and yeh but none of their presentation
/// forms, and reaches the joining forms only through its <c>GSUB</c> <c>init</c> and <c>fina</c>
/// features — as web fonts and subsets often do.
/// </summary>
/// <remarks>
/// <para>Glyphs, with advances in a 1000-unit em: 0 <c>.notdef</c> (500); 1 beh U+0628 (500); 2 yeh
/// U+064A (520); 3 initial beh (400); 4 final yeh (450); 5 final beh (480). Only 1 and 2 have code
/// points.</para>
/// <para>Lookup 0, the <c>init</c> feature's: beh to the initial beh. Lookup 1, the <c>fina</c>
/// feature's: beh to the final beh and yeh to the final yeh. Neither lookup knows which position a
/// glyph is in, so applied anywhere else each would draw the wrong form — <c>init</c> turning a final
/// beh initial. <c>gsub</c> false leaves the font with no <c>GSUB</c>.</para>
/// </remarks>
public static class GsubJoiningFont
{
    public const ushort BehGlyph = 1, YehGlyph = 2, InitialBehGlyph = 3, FinalYehGlyph = 4, FinalBehGlyph = 5;
    public static readonly int[] Advances = { 500, 500, 520, 400, 450, 480 };

    /// <param name="gsub">False leaves the font with no <c>GSUB</c>.</param>
    /// <param name="cmap">The code points mapped, each to a glyph; beh U+0628 to
    /// <see cref="BehGlyph"/> and yeh U+064A to <see cref="YehGlyph"/> when null. A letter mapped to
    /// a glyph takes that glyph's substitutions.</param>
    /// <param name="scriptTag">The script the joining features are registered under: <c>arab</c>, or
    /// <c>DFLT</c> for a font that lists them under its default script alone.</param>
    public static byte[] Build(bool gsub = true, IReadOnlyDictionary<int, int>? cmap = null, string scriptTag = "arab")
    {
        int n = Advances.Length;
        var head = new byte[54]; U32(head, 0, 0x00010000); U32(head, 12, 0x5F0F3CF5); U16(head, 18, 1000); U16(head, 50, 1);
        var maxp = new byte[32]; U32(maxp, 0, 0x00010000); U16(maxp, 4, n);
        var hhea = new byte[36]; U32(hhea, 0, 0x00010000); U16(hhea, 4, 800); U16(hhea, 6, unchecked((ushort)-200)); U16(hhea, 34, n);
        var hmtx = new byte[n * 4]; for (int i = 0; i < n; i++) U16(hmtx, i * 4, Advances[i]);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(cmap),["glyf"] = new byte[0], ["head"] = head, ["hhea"] = hhea,
            ["hmtx"] = hmtx, ["loca"] = new byte[(n + 1) * 4], ["maxp"] = maxp,
        };
        if (gsub) tables["GSUB"] = Gsub(scriptTag);
        return Assemble(tables);
    }

    private static byte[] Gsub(string scriptTag)
    {
        // Single substitutions, format 2: coverage 8 bytes after the header-and-array.
        byte[] init = Concat(Words(1, 0, 1, 8), Words(2, 8, 1, InitialBehGlyph), Words(1, 1, BehGlyph));
        byte[] fina = Concat(Words(1, 0, 1, 8), Words(2, 10, 2, FinalBehGlyph, FinalYehGlyph), Words(1, 2, BehGlyph, YehGlyph));
        byte[] lookupList = Concat(Words(2, 6, 6 + init.Length), init, fina);

        byte[] scriptList = Concat(Words(1), Tag(scriptTag), Words(8), Words(4, 0), Words(0, 0xFFFF, 2, 0, 1));
        // Two features: init (lookup 0), fina (lookup 1); each record's table 6 bytes long.
        byte[] featureList = Concat(Words(2), Tag("fina"), Words(14), Tag("init"), Words(20),
                                    Words(0, 1, 1), Words(0, 1, 0));
        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
    }

    private static byte[] Cmap(IReadOnlyDictionary<int, int>? mapped)
    {
        var fixedMap = mapped != null
            ? new SortedDictionary<int, int>(mapped.ToDictionary(e => e.Key, e => e.Value))
            : new SortedDictionary<int, int> { [0x0628] = BehGlyph, [0x064A] = YehGlyph };
        (int Code, int Glyph)[] map = fixedMap.Select(e => (e.Key, e.Value)).ToArray();
        int segs = map.Length + 1, length = 16 + segs * 8;
        var sub = new byte[length];
        U16(sub, 0, 4); U16(sub, 2, length); U16(sub, 6, segs * 2);
        for (int i = 0; i < segs; i++)
        {
            int code = i < map.Length ? map[i].Code : 0xFFFF;
            int delta = i < map.Length ? map[i].Glyph - code : 1;
            U16(sub, 14 + i * 2, code);
            U16(sub, 16 + segs * 2 + i * 2, code);
            U16(sub, 16 + segs * 4 + i * 2, delta & 0xFFFF);
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

    private static byte[] Words(params int[] values)
    {
        var b = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) U16(b, i * 2, values[i] & 0xFFFF);
        return b;
    }

    private static byte[] Tag(string t) => Encoding.ASCII.GetBytes(t);
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static void U16(byte[] d, int p, int v) { d[p] = (byte)(v >> 8); d[p + 1] = (byte)v; }
    private static void U32(byte[] d, int p, uint v) { d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v; }
}
