// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A six-glyph TrueType font whose Arabic <c>mark</c> feature is one mark-to-ligature lookup (GPOS
/// type 5) placing a fatha on the lam-alef ligature — for the positioning reader's ligature
/// attachment, through the composer that forms the ligature.
/// </summary>
/// <remarks>
/// <para>Glyphs, with advances in a 1000-unit em: 0 <c>.notdef</c> (500); 1 lam-alef U+FEFB (600), a
/// ligature by its <c>GDEF</c> class; 2 fatha U+064E (200 — an advance, so its removal shows), a mark;
/// 3 the initial lam U+FEDF and 4 the final alef U+FE8E (300 each), which shaping needs before it can
/// form the ligature; 5 seen U+0633 (500), a base nothing positions on.</para>
/// <para>The ligature's attach table has two components — lam's, then alef's — with the fatha's class
/// anchored at (450, 700) on the first and (150, 650) on the second; the fatha's own anchor is
/// (100, 0). On the last component the fatha's origin is therefore (50, 650) from the ligature's.
/// <c>nullLastAnchor</c> leaves the second component without an anchor.</para>
/// </remarks>
public static class LigatureMarkFont
{
    public const char LamAlef = 'ﻻ';
    public const char Fatha = 'َ';
    public const ushort LamAlefGlyph = 1, FathaGlyph = 2, LamGlyph = 3, AlefGlyph = 4, SeenGlyph = 5;
    public static readonly int[] Advances = { 500, 600, 200, 300, 300, 500 };

    /// <summary>
    /// With <c>gsubFormsLigature</c>: a seventh glyph (600), the lam-alef the font's own <c>GSUB</c>
    /// <c>rlig</c> forms from the initial lam and the final alef — and the one the mark-to-ligature
    /// lookup then covers, instead of the glyph the <c>cmap</c> maps U+FEFB to. Segoe UI's shape.
    /// </summary>
    public const ushort GsubLamAlefGlyph = 6;

    /// <summary>
    /// With <c>gsubRedrawsAsTwoGlyphs</c>: glyphs 6 and 7 (300 each), the lam piece and the alef piece
    /// the font's <c>rlig</c> substitutes for the initial lam and the final alef one for one — Segoe
    /// UI's shape, where lam-alef is two contextual glyphs and the fatha's anchor is a mark-to-base
    /// anchor on the alef piece, (250, 600); the <c>cmap</c>'s lam-alef has no anchors.
    /// </summary>
    public const ushort LamPieceGlyph = 6, AlefPieceGlyph = 7;

    /// <param name="nullLastAnchor">Leave the ligature's second component without an anchor.</param>
    /// <param name="gsubFormsLigature">
    /// Add a <c>GSUB</c> whose <c>rlig</c> forms <see cref="GsubLamAlefGlyph"/> from <see cref="LamGlyph"/>
    /// and <see cref="AlefGlyph"/>, and move the mark-to-ligature coverage onto that glyph: the
    /// <c>cmap</c>'s lam-alef then has no anchors at all.
    /// </param>
    /// <param name="gsubRedrawsAsTwoGlyphs">
    /// Add a <c>GSUB</c> whose <c>rlig</c> substitutes <see cref="LamPieceGlyph"/> and
    /// <see cref="AlefPieceGlyph"/> for the two forms one for one, and a mark-to-base lookup anchoring
    /// the fatha on the alef piece; the <c>cmap</c>'s lam-alef keeps no anchors.
    /// </param>
    /// <param name="gsubLeavesLamAlefAlone">
    /// Add a <c>GSUB</c> whose one <c>rlig</c> lookup covers <see cref="SeenGlyph"/> alone — a font with
    /// rules that say nothing about lam and alef, which must still draw the encoded ligature.
    /// </param>
    public static byte[] Build(bool nullLastAnchor = false, bool gsubFormsLigature = false, bool gsubRedrawsAsTwoGlyphs = false,
                               bool gsubLeavesLamAlefAlone = false)
    {
        int[] advances = gsubFormsLigature ? Advances.Concat(new[] { 600 }).ToArray()
                       : gsubRedrawsAsTwoGlyphs ? Advances.Concat(new[] { 300, 300 }).ToArray() : Advances;
        int n = advances.Length;
        var head = new byte[54]; U32(head, 0, 0x00010000); U32(head, 12, 0x5F0F3CF5); U16(head, 18, 1000); U16(head, 50, 1);
        var maxp = new byte[32]; U32(maxp, 0, 0x00010000); U16(maxp, 4, n);
        var hhea = new byte[36]; U32(hhea, 0, 0x00010000); U16(hhea, 4, 800); U16(hhea, 6, unchecked((ushort)-200)); U16(hhea, 34, n);
        var hmtx = new byte[n * 4]; for (int i = 0; i < n; i++) U16(hmtx, i * 4, advances[i]);

        byte[] classes = gsubFormsLigature ? Words(1, LamAlefGlyph, 6, 2, 3, 1, 1, 1, 2)
                       : gsubRedrawsAsTwoGlyphs ? Words(1, LamAlefGlyph, 7, 2, 3, 1, 1, 1, 1, 1)
                       : Words(1, LamAlefGlyph, 5, 2, 3, 1, 1, 1);
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["cmap"] = Cmap(), ["glyf"] = new byte[0], ["head"] = head, ["hhea"] = hhea,
            ["hmtx"] = hmtx, ["loca"] = new byte[(n + 1) * 4], ["maxp"] = maxp,
            ["GDEF"] = Concat(Words(1, 0, 12, 0, 0, 0), classes),
            ["GPOS"] = Gpos(nullLastAnchor, gsubFormsLigature ? GsubLamAlefGlyph : LamAlefGlyph, gsubRedrawsAsTwoGlyphs),
        };
        if (gsubFormsLigature) tables["GSUB"] = Gsub(LigatureLookup());
        else if (gsubRedrawsAsTwoGlyphs) tables["GSUB"] = Gsub(TwoPiecesLookup());
        else if (gsubLeavesLamAlefAlone) tables["GSUB"] = Gsub(SeenOnlyLookup());
        return Assemble(tables);
    }

    /// <summary>A single substitution (type 1, format 2) of <see cref="SeenGlyph"/> for itself — rules that touch no lam or alef.</summary>
    private static byte[] SeenOnlyLookup()
    {
        byte[] sub = Concat(Words(2, 8, 1, SeenGlyph), Words(1, 1, SeenGlyph));
        return Concat(Words(1, 0, 1, 8), sub);
    }

    /// <summary>
    /// A ligature substitution (type 4): the set under <see cref="LamGlyph"/> holds one ligature,
    /// <see cref="GsubLamAlefGlyph"/>, whose second component is <see cref="AlefGlyph"/>.
    /// </summary>
    private static byte[] LigatureLookup()
    {
        // LigatureSubst format 1: coverage at 8 (format 1, one glyph), one ligature set at 14; the set
        // holds one ligature 4 bytes on: ligature glyph, component count 2, the second component.
        byte[] sub = Concat(Words(1, 8, 1, 14), Words(1, 1, LamGlyph), Words(1, 4), Words(GsubLamAlefGlyph, 2, AlefGlyph));
        return Concat(Words(4, 0, 1, 8), sub);
    }

    /// <summary>
    /// A single substitution (type 1, format 2): <see cref="LamGlyph"/> → <see cref="LamPieceGlyph"/>,
    /// <see cref="AlefGlyph"/> → <see cref="AlefPieceGlyph"/>.
    /// </summary>
    private static byte[] TwoPiecesLookup()
    {
        // SingleSubst format 2: coverage at 10 (format 1, two glyphs), two substitutes in coverage order.
        byte[] sub = Concat(Words(2, 10, 2, LamPieceGlyph, AlefPieceGlyph), Words(1, 2, LamGlyph, AlefGlyph));
        return Concat(Words(1, 0, 1, 8), sub);
    }

    /// <summary>A <c>GSUB</c> with one <c>rlig</c> feature under <c>arab</c> listing <paramref name="lookup"/>.</summary>
    private static byte[] Gsub(byte[] lookup)
    {
        byte[] scriptList = Concat(Words(1), Tag("arab"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        byte[] featureList = Concat(Words(1), Tag("rlig"), Words(8), Words(0, 1, 0));
        byte[] lookupList = Concat(Words(1, 4), lookup);
        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
    }

    private static byte[] Gpos(bool nullLastAnchor, ushort ligatureGlyph, bool markToBaseOnAlefPiece)
    {
        // Mark-to-ligature, format 1: mark coverage at 12, ligature coverage at 18, one class, the
        // mark array at 24 (one record, its anchor 6 bytes on), the ligature array at 36.
        byte[] markArray = Concat(Words(1, 0, 6), Words(1, 100, 0));
        // Ligature array: one attach table, 4 bytes on; it: two components, an anchor offset each
        // (from the attach table), then the anchors.
        byte[] attach = Concat(Words(2, 6, nullLastAnchor ? 0 : 12), Words(1, 450, 700), Words(1, 150, 650));
        byte[] ligatureArray = Concat(Words(1, 4), attach);
        byte[] sub = Concat(Words(1, 12, 18, 1, 24, 36), Words(1, 1, FathaGlyph), Words(1, 1, ligatureGlyph), markArray, ligatureArray);
        byte[] lookup = Concat(Words(5, 0, 1, 8), sub);

        byte[] scriptList = Concat(Words(1), Tag("arab"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        if (!markToBaseOnAlefPiece)
        {
            byte[] featureList = Concat(Words(1), Tag("mark"), Words(8), Words(0, 1, 0));
            byte[] lookupList = Concat(Words(1, 4), lookup);
            int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
            return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
        }

        // Mark-to-base, format 1: mark coverage at 12, base coverage at 18, one class, the mark array
        // at 24 (one record — class and offset — its anchor 6 bytes on: (100, 0)), the base array at 36
        // (one base record of one anchor offset, the anchor 4 bytes on: (250, 600) on the alef piece).
        byte[] baseSub = Concat(Words(1, 12, 18, 1, 24, 36), Words(1, 1, FathaGlyph), Words(1, 1, AlefPieceGlyph),
                                Words(1, 0, 6), Words(1, 100, 0), Words(1, 4), Words(1, 250, 600));
        byte[] baseLookup = Concat(Words(4, 0, 1, 8), baseSub);
        {
            byte[] featureList = Concat(Words(1), Tag("mark"), Words(8), Words(0, 2, 0, 1));
            byte[] lookupList = Concat(Words(2, 6, 6 + lookup.Length), lookup, baseLookup);
            int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
            return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
        }
    }

    private static byte[] Cmap()
    {
        var fixedMap = new SortedDictionary<int, int>
        {
            [0x0633] = SeenGlyph, [Fatha] = FathaGlyph, [0xFE8E] = AlefGlyph, [0xFEDF] = LamGlyph, [LamAlef] = LamAlefGlyph,
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
