// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// A font holding only a <c>GDEF</c> (version 1.2) and a <c>GPOS</c> whose <c>mark</c> feature is one
/// context rule, written in any of the six forms OpenType has for it — context (lookup type 7) and
/// chained context (type 8), each by glyph (format 1), by class (format 2) and by coverage (format 3)
/// — for the positioning reader's rule matching and its mark-skipping flags. Glyph ids are used
/// directly; the font has no <c>cmap</c>.
/// </summary>
/// <remarks>
/// <para>Glyphs: 0 <c>.notdef</c>; 1 B, 2 A, 3 C, 4 D — bases; 5 M1 and 6 M2 — marks. M1 is of
/// mark-attachment class 1 and in mark glyph set 0; M2 is of mark-attachment class 2 and in no set.</para>
/// <para>The rule. Context: the input B then A — in formats 2 and 3, B then A's class or coverage,
/// which D shares — raises the second input glyph by 10 through a nested single adjustment (lookup 1,
/// which no feature lists). Chained: the same input between a backtrack C and a lookahead C. Format 1
/// and 2 rule sets hold a non-matching rule before the matching one, and a format 1 subtable covers C
/// too with a null rule set, as a format 2 one covers C, whose class has none; the chained format 2 rule reads its backtrack and lookahead through a
/// class definition of their own, in which C is class 1 (in the input's, C is class 3).</para>
/// <para><c>flag</c> is the rule's lookup flag; with UseMarkFilteringSet (0x10) the lookup names mark
/// glyph set 0. <c>selfRecursive</c> makes each rule's record apply the rule's own lookup at its first
/// glyph, where it matches again — a rule that calls itself without end, but for the reader's bound.</para>
/// </remarks>
public static class ContextLookupFont
{
    public const ushort B = 1, A = 2, C = 3, D = 4, M1 = 5, M2 = 6;

    public enum Form { Context1, Context2, Context3, Chain1, Chain2, Chain3 }

    public static byte[] Build(Form form, int flag = 0, bool selfRecursive = false)
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["GDEF"] = Gdef(),
            ["GPOS"] = Gpos(form, flag, selfRecursive ? 0 : 1, selfRecursive ? 0 : 1),
        };
        return Assemble(tables);
    }

    private static byte[] Gdef()
    {
        // 1.2 header: glyph classes at 14, mark-attachment classes at 32, mark glyph sets at 42.
        return Concat(
            Words(1, 2, 14, 0, 0, 32, 42),
            Words(1, B, 6, 1, 1, 1, 1, 3, 3),            // B A C D bases, M1 M2 marks
            Words(1, M1, 2, 1, 2),                        // M1 attachment class 1, M2 class 2
            Words(1, 1), Long(8),                         // one mark glyph set, its coverage 8 bytes on
            Words(1, 1, M1));
    }

    private static byte[] Rule(Form form, int s, int l) => form switch
    {
        Form.Context1 => Concat(
            Words(1, 10, 2, 18, 0),                       // coverage {B, C}; rule sets: B's, C's null
            Words(1, 2, B, C),
            Words(2, 6, 18),                              // B's set: two rules
            Words(3, 1, C, C, s, l),                      // B C C — does not match
            Words(2, 1, A, s, l)),                        // B A
        Form.Context2 => Concat(
            Words(2, 16, 24, 4, 0, 38, 0, 0),             // class sets 0–3; only class 1 (B) has rules
            Words(1, 2, B, C),                            // C is covered, and its class (3) has no rules
            Words(1, B, 4, 1, 2, 3, 2),                   // B 1, A 2, C 3, D 2
            Words(2, 6, 18),
            Words(3, 1, 3, 3, s, l),                      // classes 1 3 3 — does not match
            Words(2, 1, 2, s, l)),                        // classes 1 2
        Form.Context3 => Concat(
            Words(3, 2, 1, 14, 20, s, l),
            Words(1, 1, B),
            Words(1, 2, A, D)),
        Form.Chain1 => Concat(
            Words(1, 10, 2, 18, 0),
            Words(1, 2, B, C),
            Words(2, 6, 20),
            Words(0, 2, C, 0, 1, s, l),                   // B C — does not match
            Words(1, C, 2, A, 1, C, 1, s, l)),            // C | B A | C
        Form.Chain2 => Concat(
            Words(2, 20, 40, 26, 40, 4, 0, 48, 0, 0),     // backtrack and lookahead share a class definition
            Words(1, 1, B),
            Words(1, B, 4, 1, 2, 3, 2),                   // input classes: B 1, A 2, C 3, D 2
            Words(1, C, 1, 1),                            // context classes: C 1
            Words(2, 6, 20),
            Words(0, 2, 3, 0, 1, s, l),                   // classes 1 3 — does not match
            Words(1, 1, 2, 2, 1, 1, 1, s, l)),            // 1 | 1 2 | 1
        _ => Concat(
            Words(3, 1, 22, 2, 28, 34, 1, 42, 1, s, l),
            Words(1, 1, C),
            Words(1, 1, B),
            Words(1, 2, A, D),
            Words(1, 1, C)),
    };

    private static byte[] Gpos(Form form, int flag, int sequence, int lookup)
    {
        int type = form <= Form.Context3 ? 7 : 8;
        byte[] rule = (flag & 0x10) != 0
            ? Concat(Words(type, flag, 1, 10, 0), Rule(form, sequence, lookup))
            : Concat(Words(type, flag, 1, 8), Rule(form, sequence, lookup));
        byte[] raise = Concat(Words(1, 0, 1, 8), Words(1, 8, 0x2, 10), Words(1, 3, A, C, D));
        byte[][] lookups = { rule, raise };

        byte[] scriptList = Concat(Words(1), Tag("DFLT"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        byte[] featureList = Concat(Words(1), Tag("mark"), Words(8), Words(0, 1, 0));
        var offsets = new int[lookups.Length];
        int at = 2 + 2 * lookups.Length;
        for (int i = 0; i < lookups.Length; i++) { offsets[i] = at; at += lookups[i].Length; }
        byte[] lookupList = Concat(Words(lookups.Length), Words(offsets), Concat(lookups));

        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);
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
