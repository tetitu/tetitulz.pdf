// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>Tests for CMapParser edge-cases.</summary>
public class CMapParserTests
{
    private static byte[] Cmap(string content)
        => Encoding.ASCII.GetBytes(content);

    // ------------------------------------------------------------------
    //  beginbfchar
    // ------------------------------------------------------------------

    [Fact]
    public void BfChar_Simple_Mapped()
    {
        byte[] data = Cmap("beginbfchar\n<0041> <0061>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        Assert.True(map.TryGetValue(0x41, out string s));
        Assert.Equal("a", s);
    }

    [Fact]
    public void BfChar_MultiCharDestination_Mapped()
    {
        // Destination is a 2-code-unit string (e.g. ligature ff = 0066 0066)
        byte[] data = Cmap("beginbfchar\n<0001> <00660066>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        Assert.True(map.TryGetValue(0x0001, out string s));
        Assert.Equal("ff", s);
    }

    // ------------------------------------------------------------------
    //  beginbfrange — simple single-codepoint destination
    // ------------------------------------------------------------------

    [Fact]
    public void BfRange_Simple_MapsRange()
    {
        byte[] data = Cmap("beginbfrange\n<0020> <0022> <0041>\nendbfrange\n");
        var map = CMapParser.Parse(data);
        Assert.Equal("A",  map[0x20]);
        Assert.Equal("B",  map[0x21]);
        Assert.Equal("C",  map[0x22]);
    }

    // ------------------------------------------------------------------
    //  beginbfrange — multi-char destination (only last unit increments)
    // ------------------------------------------------------------------

    [Fact]
    public void BfRange_MultiCharDestination_LastUnitIncrements()
    {
        // Destination prefix 0066 is fixed; last unit starts at 0041 and increments.
        byte[] data = Cmap("beginbfrange\n<0001> <0003> <00660041>\nendbfrange\n");
        var map = CMapParser.Parse(data);
        Assert.Equal("fA", map[0x0001]);   // 0066 0041
        Assert.Equal("fB", map[0x0002]);   // 0066 0042
        Assert.Equal("fC", map[0x0003]);   // 0066 0043
    }

    // ------------------------------------------------------------------
    //  beginbfrange — array form
    // ------------------------------------------------------------------

    [Fact]
    public void BfRange_ArrayForm_EachElementMapped()
    {
        byte[] data = Cmap("beginbfrange\n<0001> <0003> [<0041> <0042> <0043>]\nendbfrange\n");
        var map = CMapParser.Parse(data);
        Assert.Equal("A", map[0x0001]);
        Assert.Equal("B", map[0x0002]);
        Assert.Equal("C", map[0x0003]);
    }

    // ------------------------------------------------------------------
    //  Multiple sections
    // ------------------------------------------------------------------

    [Fact]
    public void BfChar_AndBfRange_BothParsed()
    {
        byte[] data = Cmap(
            "beginbfchar\n<00FF> <03B1>\nendbfchar\n" +
            "beginbfrange\n<0020> <0021> <0041>\nendbfrange\n");
        var map = CMapParser.Parse(data);
        Assert.Equal("α", map[0xFF]);
        Assert.Equal("A", map[0x20]);
        Assert.Equal("B", map[0x21]);
    }

    // ------------------------------------------------------------------
    //  Surrogate codepoint guard (dompdf 3.1.0 + CPDF produces these)
    // ------------------------------------------------------------------

    [Fact]
    public void BfRange_SurrogateDestination_DoesNotThrow()
    {
        // Destination range starts in the surrogate half (0xD800) — must not throw.
        byte[] data = Cmap("beginbfrange\n<0001> <0003> <D800>\nendbfrange\n");
        var map = CMapParser.Parse(data);
        // Surrogate entries are silently skipped; the map should be empty.
        Assert.Empty(map);
    }

    [Fact]
    public void BfChar_SurrogateDestination_DoesNotThrow()
    {
        // A bfchar entry pointing to a surrogate code point must not throw.
        byte[] data = Cmap("beginbfchar\n<0042> <DBFF>\nendbfchar\n");
        var map = CMapParser.Parse(data);
        // Invalid destination is dropped; no entry for 0x42.
        Assert.False(map.ContainsKey(0x42));
    }

    [Fact]
    public void BfRangeWithArray_SurrogateDestination_DoesNotThrow()
    {
        // Array-style bfrange with a surrogate destination must not throw.
        byte[] data = Cmap("beginbfrange\n<0010> <0011> [<DC00> <0041>]\nendbfrange\n");
        var map = CMapParser.Parse(data);
        // 0x0010 → DC00 (skipped / empty), 0x0011 → 'A' (valid)
        Assert.Equal("A", map[0x11]);
    }
}
