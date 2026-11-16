// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A seven-glyph TrueType font with a <c>GDEF</c> glyph class definition and a <c>GPOS</c> whose
/// Hebrew script has a <c>mark</c> feature — a mark-to-base lookup and two chained-context rules —
/// and a <c>mkmk</c> feature (a mark-to-mark lookup), for the producers' mark positioning.
/// </summary>
/// <remarks>
/// <para>Glyphs, with advances in a 1000-unit em: 0 <c>.notdef</c> (500); 1 bet U+05D1 (600), a
/// base; 2 qamats U+05B8 (300 — a mark with an advance, as Gisha's are, so its removal shows); 3
/// dagesh U+05BC (0), a mark; 4 <c>a</c> (500), a base the lookups do not cover; 5 the space (250);
/// 6 holam U+05B9 (0), a mark only the context rule positions.</para>
/// <para>Anchors: bet's are (300, −50) for mark class 0 and (300, 300) for class 1; qamats is class
/// 0 with its anchor at (150, 0), dagesh class 1 at (50, 50). For mark-to-mark, qamats attaches to
/// dagesh: dagesh's mark anchor (50, −400), qamats' (150, 0). So qamats on bet sits at
/// (150, −50) from bet's origin, dagesh at (250, 250), and qamats on dagesh at (150, −150).</para>
/// <para>Context (lookup type 8, format 3): holam after bet applies two lookups no feature lists — a
/// mark-to-base lookup anchoring it at (500, 700) on bet, then a single adjustment moving it by
/// (20, 10) — so it sits at (520, 710); after any other glyph it is not positioned. A second rule,
/// whose lookup skips marks, raises an <c>a</c> by 40 and widens it by 30 when the glyph before it, marks skipped, is
/// bet: after bet and qamats it matches only because the qamats is skipped.</para>
/// <para><c>extension</c> wraps every lookup in an extension lookup (type 9), as Arial's and Segoe
/// UI's are, and takes the other encodings too: a format 2 class definition, format 2 coverage
/// tables and format 2 single adjustments, and a 2000-unit em with every advance, anchor and
/// adjustment doubled — so the glyph-space answers are
/// the same and an anchor read unscaled is not. Assembled byte by byte, not through the code under
/// test.</para>
/// </remarks>
public static class MarkAttachmentFont
{
    public const char Bet = 'ב';
    public const char Qamats = 'ָ';
    public const char Dagesh = 'ּ';
    public const char Holam = '\u05B9';
    public const ushort BetGlyph = 1, QamatsGlyph = 2, DageshGlyph = 3, LatinGlyph = 4, SpaceGlyph = 5, HolamGlyph = 6;
    public static readonly int[] Advances = { 500, 600, 300, 0, 500, 250, 0 };

    /// <summary>
    /// The font; <paramref name="gpos"/> replaces the <c>GPOS</c> table when given (a hostile one, say),
    /// and every character of <paramref name="alsoCovers"/> is drawn with the plain glyph 4 (for a
    /// producer that adds labels of its own).
    /// </summary>
    /// <param name="markFeature">The feature the mark-to-base lookups are listed under: <c>mark</c>, or
    /// an Indic font's <c>abvm</c> / <c>blwm</c>, which a reader must apply the same way.</param>
    public static byte[] Build(bool extension = false, byte[]? gpos = null, bool withGdef = true, string alsoCovers = "",
                               string markFeature = "mark")
    {
        int n = Advances.Length;
        var glyf = new byte[0];
        var loca = new byte[(n + 1) * 4];                       // every glyph empty
        int k = extension ? 2 : 1;                              // design units per glyph-space unit
        var head = new byte[54]; U32(head, 0, 0x00010000); U32(head, 12, 0x5F0F3CF5); U16(head, 18, 1000 * k); U16(head, 50, 1);
        var maxp = new byte[32]; U32(maxp, 0, 0x00010000); U16(maxp, 4, n);
        var hhea = new byte[36]; U32(hhea, 0, 0x00010000); U16(hhea, 4, 800); U16(hhea, 6, unchecked((ushort)-200)); U16(hhea, 34, n);
        var hmtx = new byte[n * 4]; for (int i = 0; i < n; i++) U16(hmtx, i * 4, Advances[i] * k);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(alsoCovers), ["glyf"] = glyf, ["head"] = head, ["hhea"] = hhea,
            ["hmtx"] = hmtx, ["loca"] = loca, ["maxp"] = maxp,
            ["GPOS"] = gpos ?? Gpos(extension, markFeature),
        };
        if (withGdef) tables["GDEF"] = Gdef(extension);
        return Assemble(tables);
    }

    // ------------------------------------------------------------------ GDEF, GPOS

    private static byte[] Gdef(bool format2)
    {
        // Header 1.0 (12 bytes), then the class definition: glyphs 1..6 → 1, 3, 3, 1, 0, 3.
        byte[] classDef = format2
            ? Words(2, 4, 1, 1, 1, 2, 3, 3, 4, 4, 1, 6, 6, 3)
            : Words(1, 1, 6, 1, 3, 3, 1, 0, 3);
        return Concat(Words(1, 0, 12, 0, 0, 0), classDef);
    }

    private static byte[] Gpos(bool extension, string markFeature)
    {
        int k = extension ? 2 : 1;
        byte[] Coverage(params int[] glyphs) => extension
            ? Words(2, 1, glyphs[0], glyphs[glyphs.Length - 1], 0)        // one range; glyphs are consecutive
            : Concat(Words(1, glyphs.Length), Words(glyphs));
        byte[] Anchor(int x, int y) => Words(1, x * k, y * k);

        // A mark-to-base and a mark-to-mark subtable share a layout: a 12-byte header of offsets
        // to two coverages and two arrays, laid out after it in that order.
        byte[] Subtable(int classCount, byte[] markCoverage, byte[] baseCoverage, byte[] markArray, byte[] baseArray)
        {
            int c1 = 12, c2 = c1 + markCoverage.Length, a1 = c2 + baseCoverage.Length, a2 = a1 + markArray.Length;
            return Concat(Words(1, c1, c2, classCount, a1, a2), markCoverage, baseCoverage, markArray, baseArray);
        }

        // A single adjustment of one glyph: format 1, or format 2 with one value record.
        byte[] Single(int glyph, int valueFormat, params int[] values)
        {
            int[] scaled = values.Select(v => v * k).ToArray();
            byte[] head = extension ? Words(2, 8 + 2 * values.Length, valueFormat, 1) : Words(1, 6 + 2 * values.Length, valueFormat);
            return Concat(head, Words(scaled), Coverage(glyph));
        }

        // A chained-context rule, format 3: one backtrack and one input coverage, no lookahead, and
        // its records (sequence index 0, lookup index).
        byte[] Context(int backtrackGlyph, int inputGlyph, params int[] nestedLookups)
        {
            int headLength = 2 + 2 + 2 + 2 + 2 + 2 + 2 + 4 * nestedLookups.Length;
            byte[] back = Coverage(backtrackGlyph), input = Coverage(inputGlyph);
            var records = nestedLookups.SelectMany(l => new[] { 0, l }).ToArray();
            return Concat(Words(3, 1, headLength, 1, headLength + back.Length, 0, nestedLookups.Length), Words(records), back, input);
        }

        byte[] markBase = Subtable(2,
            Coverage(QamatsGlyph, DageshGlyph), Coverage(BetGlyph),
            Concat(Words(2, 0, 10, 1, 16), Anchor(150, 0), Anchor(50, 50)),
            Concat(Words(1, 6, 12), Anchor(300, -50), Anchor(300, 300)));
        byte[] markMark = Subtable(1,
            Coverage(QamatsGlyph), Coverage(DageshGlyph),
            Concat(Words(1, 0, 6), Anchor(150, 0)),
            Concat(Words(1, 4), Anchor(50, -400)));
        byte[] holamBase = Subtable(1,
            Coverage(HolamGlyph), Coverage(BetGlyph),
            Concat(Words(1, 0, 6), Anchor(0, 0)),
            Concat(Words(1, 4), Anchor(500, 700)));

        byte[] Lookup(int type, int flag, byte[] sub) => extension
            ? Concat(Words(9, flag, 1, 8), Words(1, type), Long(8), sub)
            : Concat(Words(type, flag, 1, 8), sub);
        byte[][] lookups =
        {
            Lookup(4, 0, markBase),                              // 0: mark
            Lookup(6, 0, markMark),                              // 1: mkmk
            Lookup(8, 0, Context(BetGlyph, HolamGlyph, 3, 4)),   // 2: mark — holam after bet
            Lookup(4, 0, holamBase),                             // 3: nested only
            Lookup(1, 0, Single(HolamGlyph, 0x3, 20, 10)),       // 4: nested only
            Lookup(8, 0x8, Context(BetGlyph, LatinGlyph, 6)),    // 5: mark, skipping marks — a after bet
            Lookup(1, 0, Single(LatinGlyph, 0x6, 40, 30)),       // 6: nested only
        };

        byte[] scriptList = Concat(Words(1), Tag("hebr"), Words(8), /* Script */ Words(4, 0), /* LangSys */ Words(0, 0xFFFF, 2, 0, 1));
        // Two features in tag order: the mark feature (mark, abvm or blwm — each before mkmk) and mkmk.
        byte[] featureList = Concat(Words(2), Tag(markFeature), Words(14), Tag("mkmk"), Words(24), Words(0, 3, 0, 2, 5), Words(0, 1, 1));
        var offsets = new int[lookups.Length];
        int at = 2 + 2 * lookups.Length;
        for (int i = 0; i < lookups.Length; i++) { offsets[i] = at; at += lookups[i].Length; }
        byte[] lookupList = Concat(Words(lookups.Length), Words(offsets), Concat(lookups));

        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
    }

    // ------------------------------------------------------------------ the rest of the font

    private static byte[] Cmap(string alsoCovers)
    {
        var fixedMap = new Dictionary<int, int> { [0x20] = SpaceGlyph, [0x61] = LatinGlyph, [Qamats] = QamatsGlyph, [Dagesh] = DageshGlyph, [Bet] = BetGlyph, [Holam] = HolamGlyph };
        foreach (char c in alsoCovers) if (!fixedMap.ContainsKey(c)) fixedMap[c] = LatinGlyph;
        (int Code, int Glyph)[] map = fixedMap.OrderBy(e => e.Key).Select(e => (e.Key, e.Value)).ToArray();
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
