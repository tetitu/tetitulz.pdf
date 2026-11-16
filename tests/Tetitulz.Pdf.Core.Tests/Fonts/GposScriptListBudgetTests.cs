// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// The OpenType layout reader's walk over a <c>GPOS</c> script list is bounded by
/// <see cref="PdfLimits.MaxOpenTypeScriptSteps"/>. The script count, a language system's
/// feature-index count and a feature's lookup count are three 16-bit fields the font chooses, and
/// the walk was their product: a 320 KB font asked for 4 × 10¹³ reads from every producer that
/// embedded it.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public class GposScriptListBudgetTests
{
    private const ushort Glyph = 1;

    /// <summary>
    /// Ten thousand scripts sharing one language system of 65 535 feature indices, every one naming a
    /// feature of 65 535 lookup indices. The walk stops at the budget, and the legacy <c>kern</c>
    /// table beside the <c>GPOS</c>, read after it, is still there.
    /// </summary>
    [Fact]
    public void AHostileScriptList_IsBoundedByTheStepBudget()
    {
        byte[] font = Font(HostileGpos(scripts: 10_000, featureIndices: 65_535, lookupIndices: 65_535), LegacyKern());

        OpenTypeMarkPositioning positioning = OpenTypeMarkPositioning.TryLoad(font, 1000);

        Assert.NotNull(positioning);
        Assert.True(positioning.CanKern);
    }

    /// <summary>
    /// The budget is the parameter. Two scripts, three steps each (the script, its one feature index,
    /// the feature's one lookup index): at two, the first script's lookup index is over the budget and
    /// nothing is registered, so there is no positioning; at three, the first script's <c>kern</c> is
    /// registered and the second's <c>curs</c> is not; at the default, both are.
    /// </summary>
    [Theory]
    [InlineData(2, false, false)]
    [InlineData(3, true, false)]
    [InlineData(PdfLimits.DefaultMaxOpenTypeScriptSteps, true, true)]
    public void TheScriptsReadBeforeTheBudgetRunsOut_AreKept(int budget, bool firstScriptRead, bool secondScriptRead)
    {
        PdfLimits.MaxOpenTypeScriptSteps = budget;
        try
        {
            OpenTypeMarkPositioning positioning = OpenTypeMarkPositioning.TryLoad(Font(TwoScriptGpos()), 1000);

            if (!firstScriptRead)
            {
                Assert.Null(positioning);
                return;
            }
            Assert.NotNull(positioning);
            Assert.True(positioning.CanKern);
            Assert.Equal(secondScriptRead, positioning.HasCursive("arab"));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    // ------------------------------------------------------------------ fixtures

    private static byte[] Font(byte[] gpos, byte[] kern = null)
    {
        var tables = new List<(string tag, byte[] data)> { ("GPOS", gpos) };
        if (kern != null) tables.Add(("kern", kern));
        return SfntBuilder.Build(0x00010000, tables);
    }

    /// <summary>One lookup: type 1 format 1, an x-advance of 50 on glyph 1 — enough for the reader to count it.</summary>
    private static byte[] LookupList()
        => Concat(Words(1, 4),
                  Words(1, 0, 1, 8),                 // type 1, no flags, one subtable at 8
                  Words(1, 8, 0x4, 50),              // format 1, coverage at 8, xAdvance
                  Words(1, 1, Glyph));               // coverage format 1, one glyph

    /// <summary><c>DFLT</c> with a <c>kern</c> feature, <c>arab</c> with a <c>curs</c> feature, both over lookup 0.</summary>
    private static byte[] TwoScriptGpos()
    {
        byte[] scriptList = Concat(
            Words(2), Tag("DFLT"), Words(14), Tag("arab"), Words(26),
            Words(4, 0), Words(0, 0xFFFF, 1, 0),      // DFLT: default LangSys, feature index 0
            Words(4, 0), Words(0, 0xFFFF, 1, 1));     // arab: default LangSys, feature index 1
        byte[] featureList = Concat(
            Words(2), Tag("kern"), Words(14), Tag("curs"), Words(20),
            Words(0, 1, 0),                           // kern: lookup 0
            Words(0, 1, 0));                          // curs: lookup 0
        return Gpos(scriptList, featureList);
    }

    private static byte[] HostileGpos(int scripts, int featureIndices, int lookupIndices)
    {
        int scriptTable = 2 + 6 * scripts;
        var scriptList = new List<byte>(scriptTable + 10 + 2 * featureIndices);
        scriptList.AddRange(Words(scripts));
        for (int i = 0; i < scripts; i++)
        {
            // Distinct tags, or the reader skips a script it has already registered.
            scriptList.AddRange(Tag(new string(new[] { 'a', (char)('a' + i / 676 % 26), (char)('a' + i / 26 % 26), (char)('a' + i % 26) })));
            scriptList.AddRange(Words(scriptTable));
        }
        scriptList.AddRange(Words(4, 0));                                   // the one script table: default LangSys
        scriptList.AddRange(Words(0, 0xFFFF, featureIndices));
        for (int i = 0; i < featureIndices; i++) scriptList.AddRange(Words(0));   // every index names feature 0

        var featureList = new List<byte>(8 + 4 + 2 * lookupIndices);
        featureList.AddRange(Words(1));
        featureList.AddRange(Tag("kern"));
        featureList.AddRange(Words(8));
        featureList.AddRange(Words(0, lookupIndices));
        for (int i = 0; i < lookupIndices; i++) featureList.AddRange(Words(0));  // every index names lookup 0

        return Gpos(scriptList.ToArray(), featureList.ToArray());
    }

    private static byte[] Gpos(byte[] scriptList, byte[] featureList)
    {
        int scripts = 10, features = scripts + scriptList.Length, lookups = features + featureList.Length;
        return Concat(Words(1, 0, scripts, features, lookups), scriptList, featureList, LookupList());
    }

    /// <summary>A format 0 horizontal <c>kern</c> table with one pair.</summary>
    private static byte[] LegacyKern()
        => Words(0, 1,                                 // version 0, one subtable
                 0, 20, 0x0001,                        // subtable version, length, horizontal format 0
                 1, 6, 0, 0,                           // one pair; search fields
                 Glyph, 2, 0xFFCE);                    // glyph 1 before glyph 2: -50

    private static byte[] Words(params int[] values)
    {
        var bytes = new byte[2 * values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[2 * i] = (byte)(values[i] >> 8);
            bytes[2 * i + 1] = (byte)values[i];
        }
        return bytes;
    }

    private static byte[] Tag(string tag) => Encoding.ASCII.GetBytes(tag);

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] p in parts) total += p.Length;
        var all = new byte[total];
        int at = 0;
        foreach (byte[] p in parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
        return all;
    }
}
