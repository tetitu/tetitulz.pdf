// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A six-glyph TrueType font whose <c>curs</c> feature (default script) is one cursive attachment
/// lookup (GPOS type 3) joining beh's contextual forms and a Latin x — for the positioning reader's
/// cursive joins, through the composer that shapes the forms and lays them out.
/// </summary>
/// <remarks>
/// <para>Glyphs, with advances in a 1000-unit em: 0 <c>.notdef</c> (500); 1 initial beh U+FE91 (400);
/// 2 final beh U+FE90 (500); 3 fatha U+064E (200), a mark by its <c>GDEF</c> class; 4 medial beh U+FE92
/// (400); 5 x (600), joining left to right.</para>
/// <para>Anchors: the initial form exits at (20, 100); the medial enters at (380, 0) and exits at
/// (20, 100); the final enters at (480, 0). Each exit is on the letter's left and 100 above its
/// entry, so a join lowers the earlier letter (with the RightToLeft flag) or raises the later one.
/// The x enters at (50, 0) and exits at (550, 100), on its right.</para>
/// <para><c>flag</c> is the lookup's flag — 9, RightToLeft and IgnoreMarks, by default, as Dubai's
/// are. <c>extension</c> wraps the lookup in an extension lookup (type 9), as Dubai's are;
/// <c>gdef</c> false leaves the font without a <c>GDEF</c>.</para>
/// </remarks>
public static class CursiveFont
{
    public const ushort InitialGlyph = 1, FinalGlyph = 2, FathaGlyph = 3, MedialGlyph = 4, LatinGlyph = 5;
    public static readonly int[] Advances = { 500, 400, 500, 200, 400, 600 };

    public static byte[] Build(int flag = 9, bool extension = false, bool gdef = true, bool bothOrientations = false)
    {
        int n = Advances.Length;
        var head = new byte[54]; U32(head, 0, 0x00010000); U32(head, 12, 0x5F0F3CF5); U16(head, 18, 1000); U16(head, 50, 1);
        var maxp = new byte[32]; U32(maxp, 0, 0x00010000); U16(maxp, 4, n);
        var hhea = new byte[36]; U32(hhea, 0, 0x00010000); U16(hhea, 4, 800); U16(hhea, 6, unchecked((ushort)-200)); U16(hhea, 34, n);
        var hmtx = new byte[n * 4]; for (int i = 0; i < n; i++) U16(hmtx, i * 4, Advances[i]);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(), ["glyf"] = new byte[0], ["head"] = head, ["hhea"] = hhea,
            ["hmtx"] = hmtx, ["loca"] = new byte[(n + 1) * 4], ["maxp"] = maxp,
            ["GPOS"] = Gpos(flag, extension, bothOrientations),
        };
        if (gdef) tables["GDEF"] = Concat(Words(1, 0, 12, 0, 0, 0), Words(1, InitialGlyph, 5, 1, 1, 3, 1, 1));
        return Assemble(tables);
    }

    private static byte[] Gpos(int flag, bool extension, bool bothOrientations)
    {
        // Cursive attachment, format 1: coverage at 22 ({1, 2, 4, 5}), an entry-exit record per
        // covered glyph, then the anchors at 34 to 64.
        byte[] sub = Concat(
            Words(1, 22, 4, 0, 34, 52, 0, 40, 46, 58, 64),
            Words(1, 4, InitialGlyph, FinalGlyph, MedialGlyph, LatinGlyph),
            Words(1, 20, 100),                            // the initial's exit
            Words(1, 380, 0),                             // the medial's entry
            Words(1, 20, 100),                            // the medial's exit
            Words(1, 480, 0),                             // the final's entry
            Words(1, 50, 0),                              // x's entry
            Words(1, 550, 100));                          // x's exit
        byte[] lookup = extension
            ? Concat(Words(9, flag, 1, 8), Words(1, 3), Long(8), sub)
            : Concat(Words(3, flag, 1, 8), sub);

        byte[] scriptList = Concat(Words(1), Tag("DFLT"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        if (bothOrientations)
        {
            // A second lookup, the same joins with the RightToLeft flag inverted, so each pair hangs
            // both ways: a cycle of cursive parents.
            byte[] other = Concat(Words(3, flag ^ 0x1, 1, 8), sub);
            byte[] features2 = Concat(Words(1), Tag("curs"), Words(8), Words(0, 2, 0, 1));
            byte[] lookups2 = Concat(Words(2, 6, 6 + lookup.Length), lookup, other);
            int s2 = 10, f2 = s2 + scriptList.Length, l2 = f2 + features2.Length;
            return Concat(Words(1, 0, s2, f2, l2), scriptList, features2, lookups2);
        }
        byte[] featureList = Concat(Words(1), Tag("curs"), Words(8), Words(0, 1, 0));
        byte[] lookupList = Concat(Words(1, 4), lookup);
        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
    }

    private static byte[] Cmap()
    {
        var fixedMap = new SortedDictionary<int, int>
        {
            ['x'] = LatinGlyph, [0x064E] = FathaGlyph, [0xFE90] = FinalGlyph, [0xFE91] = InitialGlyph, [0xFE92] = MedialGlyph,
        };
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

    private static byte[] Long(uint v) { var b = new byte[4]; U32(b, 0, v); return b; }
    private static byte[] Tag(string t) => Encoding.ASCII.GetBytes(t);
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static void U16(byte[] d, int p, int v) { d[p] = (byte)(v >> 8); d[p + 1] = (byte)v; }
    private static void U32(byte[] d, int p, uint v) { d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v; }
}
