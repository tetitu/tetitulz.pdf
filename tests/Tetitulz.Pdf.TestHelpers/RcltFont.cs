// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A five-glyph TrueType font whose <c>GSUB</c> has one feature — <c>rclt</c>, or <c>calt</c> to show
/// it is not read — substituting an alternate for the initial beh, in any of the shapes a one-for-one
/// substitution reaches a glyph in. Dubai's beh before a final yeh is the model.
/// </summary>
/// <remarks>
/// <para>Glyphs, with advances in a 1000-unit em: 0 <c>.notdef</c> (500); 1 initial beh U+FE91 (400);
/// 2 final yeh U+FEF2 (450); 3 the beh's alternate (300), which no code point maps to; 4 final beh
/// U+FE90 (500).</para>
/// <para>Forms. <see cref="Form.Chain"/>: a chained-context rule (type 6, format 3) — the initial beh
/// before a final yeh — whose record applies lookup 1, a single substitution (format 2) no feature
/// lists. <see cref="Form.Context"/>: a context rule (type 5, format 3), the beh then the yeh, applying
/// the same. <see cref="Form.Single1"/> and <see cref="Form.Single2"/>: the feature's own lookup is a
/// single substitution, by delta (+2) or by array, everywhere. <see cref="Form.Extension"/>: the chained
/// rule through an extension lookup (type 7).</para>
/// </remarks>
public static class RcltFont
{
    public const ushort InitialBehGlyph = 1, FinalYehGlyph = 2, AlternateGlyph = 3, FinalBehGlyph = 4;
    public static readonly int[] Advances = { 500, 400, 450, 300, 500 };

    public enum Form { Chain, Context, Single1, Single2, Extension }

    public static byte[] Build(Form form = Form.Chain, string feature = "rclt", bool cursiveOnAlternate = false,
                               bool markAlternate = false)
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
            ["GSUB"] = Gsub(form, feature, markAlternate),
        };
        if (cursiveOnAlternate) tables["GPOS"] = Gpos();
        // GDEF classes: the behs and the yeh bases, the alternate a mark.
        if (markAlternate) tables["GDEF"] = Concat(Words(1, 0, 12, 0, 0, 0), Words(1, InitialBehGlyph, 4, 1, 1, 3, 1));
        return Assemble(tables);
    }

    /// <summary>
    /// A <c>curs</c> lookup (type 3, RightToLeft) joining only the alternate — it exits at (10, 100) —
    /// to the final yeh, which enters at (430, 0): as Dubai's joins are on its alternates.
    /// </summary>
    private static byte[] Gpos()
    {
        // Coverage at 14 ({yeh, alternate}); the yeh's record: entry at 22, no exit; the
        // alternate's: no entry, exit at 28.
        byte[] sub = Concat(Words(1, 14, 2, 22, 0, 0, 28),Words(1, 2, FinalYehGlyph, AlternateGlyph),
                            Words(1, 430, 0), Words(1, 10, 100));
        byte[] lookup = Concat(Words(3, 0x1, 1, 8), sub);
        byte[] scriptList = Concat(Words(1), Tag("DFLT"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        byte[] featureList = Concat(Words(1), Tag("curs"), Words(8), Words(0, 1, 0));
        byte[] lookupList = Concat(Words(1, 4), lookup);
        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
    }

    private static byte[] Gsub(Form form, string feature, bool markAlternate)
    {
        // Chained context, format 3: no backtrack, input {beh} at 18, lookahead {yeh} at 24, one
        // record applying lookup 1 at the input.
        byte[] chain = Concat(Words(3, 0, 1, 18, 1, 24, 1, 0, 1), Words(1, 1, InitialBehGlyph), Words(1, 1, FinalYehGlyph));
        // Context, format 3: two input glyphs, coverages at 14 ({beh}) and 20 ({yeh}), one record.
        byte[] context = Concat(Words(3, 2, 1, 14, 20, 0, 1),Words(1, 1, InitialBehGlyph), Words(1, 1, FinalYehGlyph));
        byte[] single2 = Concat(Words(2, 8, 1, AlternateGlyph), Words(1, 1, InitialBehGlyph));
        byte[] single1 = Concat(Words(1, 6, AlternateGlyph - InitialBehGlyph), Words(1, 1, InitialBehGlyph));

        byte[] first = form switch
        {
            Form.Chain => Concat(Words(6, 0, 1, 8), chain),
            Form.Context => Concat(Words(5, 0, 1, 8), context),
            Form.Single1 => Concat(Words(1, 0, 1, 8), single1),
            Form.Single2 => Concat(Words(1, 0, 1, 8), single2),
            _ => Concat(Words(7, 0, 1, 8), Words(1, 6), Long(8), chain),
        };
        byte[] nested = Concat(Words(1, 0, 1, 8), single2);
        byte[] scriptList = Concat(Words(1), Tag("DFLT"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        byte[] lookupList, featureList;
        if (markAlternate)
        {
            // A third lookup, also in the feature, flagged IgnoreMarks: it would turn the alternate
            // into the final beh, but the alternate is a mark by its GDEF class once it is drawn.
            byte[] later = Concat(Words(1, 0x8, 1, 8), Words(2, 8, 1, FinalBehGlyph), Words(1, 1, AlternateGlyph));
            lookupList = Concat(Words(3, 8, 8 + first.Length, 8 + first.Length + nested.Length), first, nested, later);
            featureList = Concat(Words(1), Tag(feature), Words(8), Words(0, 2, 0, 2));
        }
        else
        {
            lookupList = Concat(Words(2, 6, 6 + first.Length), first, nested);
            featureList = Concat(Words(1), Tag(feature), Words(8), Words(0, 1, 0));
        }
        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
    }

    private static byte[] Cmap()
    {
        var fixedMap = new SortedDictionary<int, int>
        {
            [0xFE90] = FinalBehGlyph, [0xFE91] = InitialBehGlyph, [0xFEF2] = FinalYehGlyph,
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
