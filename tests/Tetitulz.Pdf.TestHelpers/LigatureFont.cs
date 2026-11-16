// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A nine-glyph TrueType font whose <c>GSUB</c> has one feature — <c>liga</c>, or <c>rlig</c> to show
/// it needs no asking, or <c>dlig</c> or <c>hlig</c> to show each needs its own switch — forming the fi, fl and ffi ligatures,
/// in the shapes a ligature substitution reaches a run in. A text face's f-ligatures are the model.
/// </summary>
/// <remarks>
/// <para>Glyphs, with advances in a 1000-unit em: 0 <c>.notdef</c> (500); 1 f (300); 2 i (250); 3 l
/// (260); 4 the fi ligature (520), which no code point maps to; 5 the fl ligature (540), likewise;
/// 6 the ffi ligature (800), likewise; 7 a (480), which U+0101 maps to as well; 8 the combining
/// acute U+0301 (0), a mark by its <c>GDEF</c> class.</para>
/// <para>Forms. <see cref="Form.Direct"/>: the feature's lookup is the ligature substitution (type 4)
/// itself, its one set under f holding ffi (three components) then fi and fl (two) — longest first,
/// as a font orders them. <see cref="Form.Chain"/>: a chained-context rule (type 6, format 3) — an f
/// before an i or an l — whose record applies the ligature lookup, which no feature lists.
/// <see cref="Form.Extension"/>: the ligature lookup through an extension lookup (type 7).
/// <see cref="Form.IgnoreMarks"/>: the direct lookup flagged IgnoreMarks, so an acute between the f
/// and the i is skipped and the ligature still forms; without the flag it stands in the way.
/// <see cref="Form.FollowedByRule"/>: the direct lookup, then a second lookup in the feature whose
/// chained-context rule turns an a into an l when the glyph <b>before</b> it is the fi ligature — a
/// rule that looks back over the ligature's removed component, which must not be what it sees.</para>
/// </remarks>
public static class LigatureFont
{
    public const ushort FGlyph = 1, IGlyph = 2, LGlyph = 3, FiGlyph = 4, FlGlyph = 5, FfiGlyph = 6, AGlyph = 7, AcuteGlyph = 8;
    public static readonly int[] Advances = { 500, 300, 250, 260, 520, 540, 800, 480, 0 };

    public enum Form { Direct, Chain, Extension, IgnoreMarks, FollowedByRule }

    /// <param name="form">The shape the ligature lookup is reached in.</param>
    /// <param name="feature">The feature listing it: <c>liga</c>, <c>rlig</c>, or one this does not read.</param>
    /// <param name="corruptFfi">
    /// Declare the ffi ligature with 32767 components while holding two: a table that runs past its
    /// end, which a reader must skip — the fi and fl ligatures after it in the set still form.
    /// </param>
    public static byte[] Build(Form form = Form.Direct, string feature = "liga", bool corruptFfi = false)
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
            ["GSUB"] = Gsub(form, feature, corruptFfi),
            // GDEF classes: f, i, l and a bases (1); the ligatures ligatures (2); the acute a mark (3).
            ["GDEF"] = Concat(Words(1, 0, 12, 0, 0, 0), Words(1, FGlyph, 8, 1, 1, 1, 2, 2, 2, 1, 3)),
        };
        return Assemble(tables);
    }

    private static byte[] Gsub(Form form, string feature, bool corruptFfi)
    {
        // Ligature substitution, format 1: coverage {f} at 8; one ligature set at 14 holding, in
        // order, ffi (components f, i), fi (component i) and fl (component l).
        byte[] setFfi = Words(FfiGlyph, corruptFfi ? 0x7FFF : 3, FGlyph, IGlyph);
        byte[] setFi = Words(FiGlyph, 2, IGlyph);
        byte[] setFl = Words(FlGlyph, 2, LGlyph);
        byte[] set = Concat(Words(3, 8, 8 + setFfi.Length, 8 + setFfi.Length + setFi.Length), setFfi, setFi, setFl);
        byte[] ligature = Concat(Words(1, 8, 1, 14), Words(1, 1, FGlyph), set);

        // Chained context, format 3: no backtrack, input {f} at 18, lookahead {i, l} at 24, one
        // record applying lookup 1 at the input.
        byte[] chain = Concat(Words(3, 0, 1, 18, 1, 24, 1, 0, 1), Words(1, 1, FGlyph), Words(1, 2, IGlyph, LGlyph));

        byte[] first = form switch
        {
            Form.Chain => Concat(Words(6, 0, 1, 8), chain),
            Form.Extension => Concat(Words(7, 0, 1, 8), Words(1, 4), Long(8), ligature),
            Form.IgnoreMarks => Concat(Words(4, 0x8, 1, 8), ligature),
            _ => Concat(Words(4, 0, 1, 8), ligature),
        };
        byte[] nested = Concat(Words(4, 0, 1, 8), ligature);
        byte[] scriptList = Concat(Words(1), Tag("DFLT"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        byte[] lookupList, featureList;
        if (form == Form.FollowedByRule)
        {
            // Lookup 2: chained context, format 3 — backtrack {fi} at 18, input {a} at 24, no
            // lookahead, one record applying lookup 3 at the input. Lookup 3: a single substitution
            // by delta, a (7) to l (3).
            byte[] rule = Concat(Words(6, 0, 1, 8), Words(3, 1, 18, 1, 24, 0, 1, 0, 3), Words(1, 1, FiGlyph), Words(1, 1, AGlyph));
            byte[] aToL = Concat(Words(1, 0, 1, 8), Words(1, 6, LGlyph - AGlyph), Words(1, 1, AGlyph));
            lookupList = Concat(Words(4, 10, 10 + first.Length, 10 + first.Length + nested.Length, 10 + first.Length + nested.Length + rule.Length),
                                first, nested, rule, aToL);
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
            // U+0101 (a with macron) shares the a's glyph: a letter outside WinAnsi, so a DOCX run
            // holding it is drawn in the embedded font rather than a standard face. U+03B1 (Greek
            // alpha) shares it too: a letter of another script, so a run can hold a second segment.
            ['a'] = AGlyph, ['f'] = FGlyph, ['i'] = IGlyph, ['l'] = LGlyph, [0x0101] = AGlyph, [0x0301] = AcuteGlyph,
            [0x03B1] = AGlyph,
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
