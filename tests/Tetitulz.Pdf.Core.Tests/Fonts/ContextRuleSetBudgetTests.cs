// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Every context rule a subtable trial tries is one of <see cref="OpenTypeMarkPositioning.MaxSteps"/>.
/// A chained-context rule set may list 65 535 rule offsets, all naming one rule; walked uncounted,
/// each trial multiplies the rule count, and each rule's input array (up to 65 535 glyphs) is
/// allocated before it is compared. Reachable from any font a producer shapes with — an HTML
/// <c>@font-face</c>, a DOCX font. The evidence is <see cref="OpenTypeMarkPositioning.RulesTried"/>
/// and the calling thread's allocation, never a clock.
/// </summary>
public sealed class ContextRuleSetBudgetTests
{
    private const ushort Covered = 1;
    private const int Rules = 30_000;

    [Fact]
    public void RuleSetOfManyRules_IsTriedWithinTheStepBudget()
    {
        // A rule of two input glyphs that never matches: it fits the run, so only the step count stops the walk.
        OpenTypeMarkPositioning gsub = OpenTypeMarkPositioning.TryLoadSubstitution(Font(Rules, inputCount: 2));
        Assert.NotNull(gsub);
        ushort[] run = Enumerable.Repeat(Covered, 64).ToArray();   // 64 trials x 30 000 rules, unbounded

        OpenTypeMarkPositioning.RulesTried = 0;
        ushort[] glyphs = gsub.Substitute("latn", run).Glyphs;

        Assert.Equal(run, glyphs);
        Assert.InRange(OpenTypeMarkPositioning.RulesTried, Rules, OpenTypeMarkPositioning.MaxSteps);
    }

    [Fact]
    public void RuleLongerThanTheRun_AllocatesNothingPerRule()
    {
        // 65 535 input glyphs: an int[65 535] a rule, 30 000 rules a glyph, had each been allocated.
        OpenTypeMarkPositioning gsub = OpenTypeMarkPositioning.TryLoadSubstitution(Font(Rules, inputCount: 65535));
        Assert.NotNull(gsub);
        ushort[] run = Enumerable.Repeat(Covered, 4).ToArray();

        OpenTypeMarkPositioning.RulesTried = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        ushort[] glyphs = gsub.Substitute("latn", run).Glyphs;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(run, glyphs);
        Assert.Equal(4L * Rules, OpenTypeMarkPositioning.RulesTried);   // every rule was reached
        // A rule's matchers are a few hundred bytes (about 27 MB here); its input array would be
        // 256 KB, about 31 GB over the 120 000 rules tried.
        Assert.InRange(allocated, 0, 256L * 1024 * 1024);
    }

    /// <summary>
    /// A bare sfnt holding one <c>GSUB</c>: <c>DFLT</c> with one <c>rclt</c> feature, one chained-context
    /// lookup (format 1) covering glyph 1, whose one rule set lists <paramref name="rules"/> offsets to
    /// one rule — no backtrack, <paramref name="inputCount"/> input glyphs (the rest 0, which no glyph
    /// of the run is), no lookahead, no records.
    /// </summary>
    private static byte[] Font(int rules, int inputCount)
    {
        var b = new List<byte>();
        void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void Tag(string s) { foreach (char c in s) b.Add((byte)c); }
        void Set16(int at, int v) { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }

        U16(1); U16(0);
        int scriptListAt = b.Count; U16(0);
        int featureListAt = b.Count; U16(0);
        int lookupListAt = b.Count; U16(0);

        Set16(scriptListAt, b.Count);
        U16(1); Tag("DFLT"); U16(8);
        U16(4); U16(0);
        U16(0); U16(0xFFFF); U16(1); U16(0);

        Set16(featureListAt, b.Count);
        U16(1); Tag("rclt"); U16(8);
        U16(0); U16(1); U16(0);

        Set16(lookupListAt, b.Count);
        U16(1); U16(4);
        U16(6); U16(0); U16(1); U16(8);                 // chained context, one subtable
        U16(1); U16(8); U16(1); U16(14);                // format 1, coverage, one rule set
        U16(1); U16(1); U16(Covered);                   // coverage format 1
        U16(rules);
        for (int k = 0; k < rules; k++) U16(2 + 2 * rules);
        U16(0);
        U16(inputCount);
        for (int k = 1; k < inputCount; k++) U16(0);
        U16(0); U16(0);

        byte[] gsub = b.ToArray();
        var ms = new MemoryStream();
        void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void W32(long v) { W16((int)(v >> 16)); W16((int)(v & 0xFFFF)); }
        W32(0x00010000); W16(1); W16(16); W16(0); W16(0);
        foreach (char c in "GSUB") ms.WriteByte((byte)c);
        W32(0); W32(28); W32(gsub.Length);
        ms.Write(gsub, 0, gsub.Length);
        while (ms.Length % 4 != 0) ms.WriteByte(0);
        return ms.ToArray();
    }
}
