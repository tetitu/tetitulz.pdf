// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// Adversarial-input regression tests for the CMap code-review fixes:
///   4. usecmap recursion now has a cycle/depth guard — a self-referential or cyclic
///      registered CMap no longer StackOverflows.
///   7. HexToUnicodeString no longer truncates an odd trailing code-unit remainder.
///
/// These exercise the internal <see cref="CMapParser"/> directly (this assembly has
/// InternalsVisibleTo access), mirroring the existing CMapParserTests pattern.  The
/// plugin-level end-to-end version lives in the CMap plugin test project.
/// </summary>
public class ReviewFixCMap_RecursionAndHexTests
{
    private static byte[] Cmap(string content) => Encoding.ASCII.GetBytes(content);

    // ================================================================
    //  Finding 4 — usecmap cycle / depth guard
    // ================================================================

    [Fact]
    public void Usecmap_SelfReference_DoesNotStackOverflow()
    {
        // A CMap registered under "SelfA" that references itself via usecmap.
        // Pre-fix, parsing it recurses forever → StackOverflowException (uncatchable).
        byte[] self = Cmap("/SelfA usecmap\nbeginbfchar\n<0041> <0061>\nendbfchar\n");
        PredefinedCMapRegistry.Register("SelfA", self);

        // LookupPredefined triggers the lazy parse + usecmap resolution.
        string result = CMapParser.LookupPredefined("SelfA", 0x41);
        Assert.Equal("a", result);
    }

    [Fact]
    public void Usecmap_TwoNodeCycle_DoesNotStackOverflow()
    {
        // CycA → CycB → CycA.  Either direction must terminate.
        byte[] a = Cmap("/CycB usecmap\nbeginbfchar\n<0001> <0041>\nendbfchar\n");
        byte[] b = Cmap("/CycA usecmap\nbeginbfchar\n<0002> <0042>\nendbfchar\n");
        PredefinedCMapRegistry.Register("CycA", a);
        PredefinedCMapRegistry.Register("CycB", b);

        var sw = Stopwatch.StartNew();
        // A's own mapping plus B's merged-in mapping must both be reachable, and the
        // cycle back to A must not recurse forever.
        string fromA = CMapParser.LookupPredefined("CycA", 0x01);
        string fromB = CMapParser.LookupPredefined("CycA", 0x02);
        sw.Stop();

        Assert.Equal("A", fromA);
        Assert.Equal("B", fromB); // merged from CycB via usecmap
        Assert.True(sw.Elapsed < System.TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Usecmap_NameMustBePrecededBySlash()
    {
        // "Foo usecmap" with no leading '/' is not a valid PDF name operand and must be
        // ignored (LastUsecmapName null) rather than mis-captured.
        byte[] data = Cmap("Foo usecmap\nbeginbfchar\n<0041> <0061>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        Assert.Equal("a", map[0x41]);
        Assert.Null(CMapParser.LastUsecmapName);
    }

    [Fact]
    public void Usecmap_ValidSlashName_IsCaptured()
    {
        byte[] data = Cmap("/Identity-H usecmap\nbeginbfchar\n<0041> <0061>\nendbfchar\n");
        CMapParser.Parse(data);
        Assert.Equal("Identity-H", CMapParser.LastUsecmapName);
    }

    // ================================================================
    //  Finding 7 — odd trailing hex unit no longer dropped
    // ================================================================

    [Fact]
    public void BfChar_OddTrailingUnit_NotDropped()
    {
        // Destination "00410041" plus a dangling 2-nibble "41" → "0041" "0041" "0041"
        // after left-padding the remainder.  Pre-fix the trailing "41" was dropped.
        byte[] data = Cmap("beginbfchar\n<0001> <0041004141>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        Assert.True(map.TryGetValue(0x0001, out string s));
        Assert.Equal("AAA", s);
    }

    [Fact]
    public void BfChar_SurrogatePairThenOddTail_BothKept()
    {
        // High+low surrogate (U+10000) followed by a dangling "AB" → U+10000 + U+00AB.
        byte[] data = Cmap("beginbfchar\n<0001> <D800DC00AB>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        Assert.True(map.TryGetValue(0x0001, out string s));
        Assert.Equal(char.ConvertFromUtf32(0x10000) + "«", s);
    }
}
