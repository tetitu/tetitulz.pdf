// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A twelve-glyph TrueType font that kerns: a <c>GPOS</c> <c>kern</c> feature for Latin (a glyph-pair
/// and a class-pair adjustment) and for Hebrew (a glyph pair whose lookup skips marks), and a legacy
/// <c>kern</c> table, each optional — for the producers' kerning.
/// </summary>
/// <remarks>
/// <para>Glyphs, with advances in glyph space (1000 units per em; the font's em is 2000, so every
/// design value is doubled and an adjustment read unscaled is twice too large): 0 <c>.notdef</c> (500);
/// 1 <c>A</c> (600); 2 <c>V</c> (600); 3 <c>T</c> (600); 4 <c>o</c> (500); 5 <c>W</c> (700); 6
/// <c>a</c> (500); 7 the space (250); 8 bet U+05D1 (600); 9 vav U+05D5 (300); 10 dagesh U+05BC (0), a
/// mark by its <c>GDEF</c> class; 11 <c>x</c> (500), which nothing kerns.</para>
/// <para><c>GPOS</c>, Latin: lookup 0 is a format 1 pair adjustment — A then V: A's advance −80 and V
/// raised by 15; A then o: o raised by 10, no advance changed; T then o: T's advance −110. Lookup 1 is
/// a format 2 pair adjustment over two first classes (W is 1) and three second classes (o is 1, a is
/// 2): W then a is W's advance −40, W then o nothing. Hebrew: lookup 2, skipping marks, is a format 1
/// pair — bet then vav: bet's advance −50; and a <c>mark</c> feature's single adjustment (lookup 3)
/// raises every dagesh by 100, kerning or not.</para>
/// <para>The legacy <c>kern</c> table (format 0, horizontal) lists pairs as drawn, left then right:
/// A V −70, V A −60, vav bet −45.</para>
/// </remarks>
public static class KerningFont
{
    public const char Bet = 'ב';
    public const char Vav = 'ו';
    public const char Dagesh = 'ּ';
    public const ushort AGlyph = 1, VGlyph = 2, TGlyph = 3, OGlyph = 4, WGlyph = 5, LowerAGlyph = 6,
                        SpaceGlyph = 7, BetGlyph = 8, VavGlyph = 9, DageshGlyph = 10, XGlyph = 11;
    public static readonly int[] Advances = { 500, 600, 600, 600, 500, 700, 500, 250, 600, 300, 0, 500 };
    private const int K = 2;                                     // design units per glyph-space unit

    /// <summary>
    /// The font, with or without its <c>GPOS</c>, legacy <c>kern</c> table and <c>GDEF</c>;
    /// <paramref name="legacyPairCount"/> replaces the legacy table's pair count (a hostile one, say),
    /// and <paramref name="legacyCoverage"/> its subtable's coverage word (0x0001: horizontal, format 0).
    /// </summary>
    /// <summary>The Cyrillic А and В, which share the Latin A's and V's glyphs: a run of another script over the same pairs.</summary>
    public const char CyrillicA = 'А', CyrillicVe = 'В';

    /// <param name="latinScriptTag">
    /// The script the Latin kerning is registered under — <c>latn</c>, or another tag (<c>cyrl</c>)
    /// to show which script a run is looked up as. The Hebrew script keeps its tag.
    /// </param>
    /// <param name="latinKernFeature">
    /// The feature the Latin pair adjustments are listed under: <c>kern</c>, which a reader applies
    /// when asked, or <c>dist</c>, which it applies unasked as a shaper does.
    /// </param>
    public static byte[] Build(bool gpos = true, bool legacyKern = false, bool gdef = true, int? legacyPairCount = null,
                               int legacyCoverage = 0x0001, string latinScriptTag = "latn", string latinKernFeature = "kern")
    {
        int n = Advances.Length;
        var head = new byte[54]; U32(head, 0, 0x00010000); U32(head, 12, 0x5F0F3CF5); U16(head, 18, 1000 * K); U16(head, 50, 1);
        var maxp = new byte[32]; U32(maxp, 0, 0x00010000); U16(maxp, 4, n);
        var hhea = new byte[36]; U32(hhea, 0, 0x00010000); U16(hhea, 4, 800 * K); U16(hhea, 6, unchecked((ushort)(-200 * K))); U16(hhea, 34, n);
        var hmtx = new byte[n * 4]; for (int i = 0; i < n; i++) U16(hmtx, i * 4, Advances[i] * K);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(), ["glyf"] = new byte[0], ["head"] = head, ["hhea"] = hhea,
            ["hmtx"] = hmtx, ["loca"] = new byte[(n + 1) * 4], ["maxp"] = maxp,
        };
        if (gpos) tables["GPOS"] = Gpos(latinScriptTag, latinKernFeature);
        if (legacyKern) tables["kern"] = LegacyKern(legacyPairCount, legacyCoverage);
        if (gdef) tables["GDEF"] = Concat(Words(1, 0, 12, 0, 0, 0), Words(1, DageshGlyph, 1, 3));
        return Assemble(tables);
    }

    private static byte[] Gpos(string latinScriptTag, string latinKernFeature)
    {
        // Lookup 0: pair format 1 — coverage {A, T}; value formats XAdvance / YPlacement.
        byte[] pairs = Concat(
            Words(1, 36, 0x4, 0x2, 2, 14, 28),
            Words(2, VGlyph, -80 * K, 15 * K, OGlyph, 0, 10 * K),
            Words(1, OGlyph, -110 * K, 0),
            Words(1, 2, AGlyph, TGlyph));
        // Lookup 1: pair format 2 — coverage {W}; first classes W → 1; second classes o → 1, a → 2;
        // record [1][2] = −40, every other 0.
        byte[] classes = Concat(
            Words(2, 28, 0x4, 0, 34, 42, 2, 3),
            Words(0, 0, 0, 0, 0, -40 * K),
            Words(1, 1, WGlyph),
            Words(1, WGlyph, 1, 1),
            Words(1, OGlyph, 3, 1, 0, 2));
        // Lookup 2: pair format 1, marks skipped — bet then vav.
        byte[] hebrew = Concat(
            Words(1, 18, 0x4, 0, 1, 12),
            Words(1, VavGlyph, -50 * K),
            Words(1, 1, BetGlyph));

        // Lookup 3: single adjustment format 1 — dagesh raised by 100.
        byte[] raise = Concat(Words(1, 8, 0x2, 100 * K), Words(1, 1, DageshGlyph));

        byte[] Lookup(int type, int flag, byte[] sub) => Concat(Words(type, flag, 1, 8), sub);
        byte[][] lookups = { Lookup(2, 0, pairs), Lookup(2, 0, classes), Lookup(2, 0x8, hebrew), Lookup(1, 0, raise) };

        // Scripts hebr (kern and mark) and the Latin one (kern), in tag order, each a default LangSys.
        byte[] hebrewScript = Concat(Words(4, 0), Words(0, 0xFFFF, 2, 1, 2));
        byte[] latinScript = Concat(Words(4, 0), Words(0, 0xFFFF, 1, 0));
        var scriptsInOrder = new[] { ("hebr", hebrewScript), (latinScriptTag, latinScript) }
            .OrderBy(s => s.Item1, StringComparer.Ordinal).ToArray();
        byte[] scriptList = Concat(Words(2),
            Tag(scriptsInOrder[0].Item1), Words(14), Tag(scriptsInOrder[1].Item1), Words(14 + scriptsInOrder[0].Item2.Length),
            scriptsInOrder[0].Item2, scriptsInOrder[1].Item2);
        // Feature 0 the Latin pairs (lookups 0, 1) under latinKernFeature, 1 the Hebrew kern (lookup 2),
        // 2 mark (lookup 3) — records in tag order, which "dist" and "kern" both keep before "mark".
        byte[] featureList = Concat(Words(3), Tag(latinKernFeature), Words(20), Tag("kern"), Words(28), Tag("mark"), Words(34),
            Words(0, 2, 0, 1), Words(0, 1, 2), Words(0, 1, 3));
        var offsets = new int[lookups.Length];
        int at = 2 + 2 * lookups.Length;
        for (int i = 0; i < lookups.Length; i++) { offsets[i] = at; at += lookups[i].Length; }
        byte[] lookupList = Concat(Words(lookups.Length), Words(offsets), Concat(lookups));

        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
    }

    private static byte[] LegacyKern(int? pairCount, int coverage)
    {
        // Pairs sorted by (left << 16 | right).
        byte[] pairs = Concat(
            Words(AGlyph, VGlyph, -70 * K),
            Words(VGlyph, AGlyph, -60 * K),
            Words(VavGlyph, BetGlyph, -45 * K));
        return Concat(Words(0, 1), Words(0, 14 + pairs.Length, coverage, pairCount ?? 3, 12, 1, 6), pairs);
    }

    private static byte[] Cmap()
    {
        var fixedMap = new SortedDictionary<int, int>
        {
            ['A'] = AGlyph, ['V'] = VGlyph, ['T'] = TGlyph, ['o'] = OGlyph, ['W'] = WGlyph, ['a'] = LowerAGlyph,
            [' '] = SpaceGlyph, [Bet] = BetGlyph, [Vav] = VavGlyph, [Dagesh] = DageshGlyph, ['x'] = XGlyph,
            [CyrillicA] = AGlyph, [CyrillicVe] = VGlyph,
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

    private static byte[] Tag(string t) => Encoding.ASCII.GetBytes(t);
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static void U16(byte[] d, int p, int v) { d[p] = (byte)(v >> 8); d[p + 1] = (byte)v; }
    private static void U32(byte[] d, int p, uint v) { d[p] = (byte)(v >> 24); d[p + 1] = (byte)(v >> 16); d[p + 2] = (byte)(v >> 8); d[p + 3] = (byte)v; }
}
