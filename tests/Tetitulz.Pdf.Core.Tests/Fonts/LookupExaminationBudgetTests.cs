// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// One run's application examines at most <see cref="OpenTypeMarkPositioning.MaxExaminationsPerGlyph"/>
/// lookup positions a glyph. <c>MaxSteps</c> counts subtable trials and a skipped position makes
/// none, so a font whose lookups all skip every glyph cost its registered lookups times the run's
/// length with nothing counting — reachable from an HTML <c>@font-face</c>.
/// </summary>
public sealed class LookupExaminationBudgetTests
{
    private const ushort Base = 1;

    [Fact]
    public void AdversarialLookupsSkippingEveryGlyph_AreExaminedWithinTheBudget()
    {
        const int lookups = 4000, glyphs = 2000;   // eight million positions, unbounded
        OpenTypeMarkPositioning gpos = OpenTypeMarkPositioning.TryLoad(Font(lookups, ignoreBases: true), 1000);
        Assert.NotNull(gpos);

        OpenTypeMarkPositioning.PositionsExamined = 0;
        gpos.Apply("latn", Enumerable.Repeat(Base, glyphs).ToArray());

        Assert.Equal((long)glyphs * OpenTypeMarkPositioning.MaxExaminationsPerGlyph, OpenTypeMarkPositioning.PositionsExamined);
    }

    /// <summary>
    /// The control: the same lookups applying — no skip flag — raise every glyph by 10 each, and a
    /// font of a few lookups is examined in full.
    /// </summary>
    [Fact]
    public void LookupsThatApply_AreExaminedInFull()
    {
        OpenTypeMarkPositioning gpos = OpenTypeMarkPositioning.TryLoad(Font(3, ignoreBases: false), 1000);
        Assert.NotNull(gpos);

        OpenTypeMarkPositioning.PositionsExamined = 0;
        int[] raised = gpos.Apply("latn", Enumerable.Repeat(Base, 5).ToArray()).YPlacement.ToArray();

        Assert.Equal(15L, OpenTypeMarkPositioning.PositionsExamined);
        Assert.All(raised, y => Assert.Equal(30, y));
    }

    /// <summary>The substitution side, the same loop over <c>GSUB</c>'s always-applied <c>rclt</c>.</summary>
    [Fact]
    public void AdversarialSubstitutionsSkippingEveryGlyph_AreExaminedWithinTheBudget()
    {
        const int lookups = 4000, glyphs = 2000;
        OpenTypeMarkPositioning gsub = OpenTypeMarkPositioning.TryLoadSubstitution(Font(lookups, ignoreBases: true, substitution: true));
        Assert.NotNull(gsub);

        OpenTypeMarkPositioning.PositionsExamined = 0;
        gsub.Substitute("latn", Enumerable.Repeat(Base, glyphs).ToArray());

        Assert.Equal((long)glyphs * OpenTypeMarkPositioning.MaxExaminationsPerGlyph, OpenTypeMarkPositioning.PositionsExamined);
    }

    /// <summary>The control: unskipped, the first substitution takes glyph 1 to 2, which the rest do not cover.</summary>
    [Fact]
    public void SubstitutionsThatApply_AreExaminedInFull()
    {
        OpenTypeMarkPositioning gsub = OpenTypeMarkPositioning.TryLoadSubstitution(Font(3, ignoreBases: false, substitution: true));
        Assert.NotNull(gsub);

        OpenTypeMarkPositioning.PositionsExamined = 0;
        ushort[] substituted = gsub.Substitute("latn", Enumerable.Repeat(Base, 5).ToArray()).Glyphs;

        Assert.Equal(15L, OpenTypeMarkPositioning.PositionsExamined);
        Assert.All(substituted, g => Assert.Equal(2, g));
    }

    /// <summary>
    /// A <c>GDEF</c> making glyph 1 a base, and a <c>GPOS</c> whose <c>mark</c> feature lists
    /// <paramref name="lookups"/> lookups — one shared table, a single adjustment raising glyph 1 by
    /// 10 — or, for <paramref name="substitution"/>, a <c>GSUB</c> whose <c>rclt</c> lists them, a
    /// single substitution of glyph 1 by 2; under IgnoreBaseGlyphs when <paramref name="ignoreBases"/>.
    /// </summary>
    private static byte[] Font(int lookups, bool ignoreBases, bool substitution = false)
    {
        byte[] gdef = Concat(Words(1, 0, 12, 0, 0, 0), Words(1, Base, 1, 1));

        byte[] scriptList = Concat(Words(1), Tag("DFLT"), Words(8), Words(4, 0), Words(0, 0xFFFF, 1, 0));
        byte[] featureList = Concat(Words(1), Tag(substitution ? "rclt" : "mark"), Words(8), Words(0, lookups),
                                    Words(Enumerable.Range(0, lookups).ToArray()));
        byte[] subtable = substitution
            ? Concat(Words(1, 6, 1), Words(1, 1, Base))             // format 1: glyph 1 → 1 + 1
            : Concat(Words(1, 8, 0x2, 10), Words(1, 1, Base));      // format 1: YPlacement 10
        byte[] lookup = Concat(Words(1, ignoreBases ? 0x2 : 0, 1, 8), subtable);
        int shared = 2 + 2 * lookups;
        byte[] lookupList = Concat(Words(lookups), Words(Enumerable.Repeat(shared, lookups).ToArray()), lookup);

        int scripts = 10, features = scripts + scriptList.Length, lookupsAt = features + featureList.Length;
        byte[] layout = Concat(Words(1, 0, scripts, features, lookupsAt), scriptList, featureList, lookupList);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["GDEF"] = gdef,
            [substitution ? "GSUB" : "GPOS"] = layout,
        };
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
