// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A hex string's brackets are sought within their own section: a CMap made of short sections
/// whose <c>&lt;</c> never closes costs its length, not its length times its sections.
/// <see cref="CMapParser.LastScanCharactersExamined"/> is the evidence, never a clock.
/// </summary>
public class CMapSectionScanBoundsTests
{
    private const int Sections = 4000;

    private static byte[] Repeated(string section)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Sections; i++) sb.Append(section);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Theory]
    [InlineData("beginbfchar <0041 endbfchar\n")]
    [InlineData("beginbfrange <0041 endbfrange\n")]
    [InlineData("begincidchar <0041 endcidchar\n")]
    [InlineData("begincidrange <0041 endcidrange\n")]
    [InlineData("begincodespacerange <0041 endcodespacerange\n")]
    public void UnclosedHexStringsInEverySection_ScanStaysLinear(string section)
    {
        byte[] cmap = Repeated(section);

        var map = CMapParser.Parse(cmap, out _, out _);

        Assert.Empty(map);
        // Every scan stops at its section's end, so the characters examined are at most a few
        // passes over the text. Sought to the end of the whole text, each section would read the
        // rest of it: about Sections / 2 times the length.
        Assert.InRange(CMapParser.LastScanCharactersExamined, 1, 4L * cmap.Length);
    }

    /// <summary>The acceptance control: the bounded searches still find every bracket a well-formed CMap holds.</summary>
    [Fact]
    public void WellFormedSections_StillMap()
    {
        const string cmap =
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "2 beginbfchar <01> <0041> <02> <0042> endbfchar\n" +
            "1 beginbfrange <10> <12> <0061> endbfrange\n" +
            "1 beginbfrange <20> <21>\n[\n<05D0>\n<05D1>\n]\nendbfrange\n";

        var map = CMapParser.Parse(Encoding.ASCII.GetBytes(cmap), out int maxCodeBytes, out var ranges);

        Assert.Equal(1, maxCodeBytes);
        Assert.Single(ranges);
        Assert.Equal("A", map[0x01]);
        Assert.Equal("B", map[0x02]);
        Assert.Equal("a", map[0x10]);
        Assert.Equal("c", map[0x12]);
        Assert.Equal("א", map[0x20]);
        Assert.Equal("ב", map[0x21]);
    }
}
