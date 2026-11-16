// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A CMap is a PostScript resource, so a line break inside a section is white space like any other:
/// several entries may share one line.
/// </summary>
/// <remarks>
/// <para><b>Both CMap parsers split each section into lines and read one entry from each.</b>
/// <c>CMapParser</c> (a font's <c>/ToUnicode</c>) and <c>CMapCodec</c> (a Type 0 font's encoding)
/// kept only the first entry of a line, so a CMap writing its entries together lost all but one
/// mapping, and a codespace list written together lost the code widths after the first.</para>
///
/// <para>Another reader (<c>parseBfChar</c>, <c>parseCodespaceRange</c> in <c>src/core/cmap.js</c>) and
/// an independent implementation (<c>CMapParser.parseBeginbfchar</c> and its siblings) both read a section as a stream of
/// tokens, whatever the line breaks.</para>
/// </remarks>
public class CMapEntriesSharingALineTests
{
    private static byte[] CMap(string body) => Encoding.ASCII.GetBytes(
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
        "/CMapName /Test def /CMapType 2 def\n" + body + "\nendcmap end end\n");

    [Fact]
    public void ToUnicode_BfcharEntriesOnOneLine_AreAllMapped()
    {
        var map = CMapParser.Parse(CMap(
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "3 beginbfchar <41> <05D0> <42> <05D1> <43> <05D2> endbfchar"));

        Assert.Equal("א", map[0x41]);
        Assert.Equal("ב", map[0x42]);
        Assert.Equal("ג", map[0x43]);
    }

    [Fact]
    public void ToUnicode_CodespaceRangesOnOneLine_AreAllRead()
    {
        CMapParser.Parse(CMap(
            "2 begincodespacerange <00> <80> <8140> <9FFC> endcodespacerange\n" +
            "1 beginbfchar\n<41> <0041>\nendbfchar"),
            out int maxCodeBytes, out var ranges);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(2, maxCodeBytes);
        Assert.Equal(new byte[] { 0x81, 0x40 }, ranges[1].low);
    }

    [Fact]
    public void ToUnicode_CidcharEntriesOnOneLine_AreAllReversed()
    {
        var map = CMapParser.Parse(CMap(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "2 begincidchar <3042> 843 <3044> 845 endcidchar"));

        Assert.Equal("あ", map[843]);
        Assert.Equal("い", map[845]);
    }

    [Fact]
    public void ToUnicode_CidrangeEntriesOnOneLine_AreAllReversed()
    {
        var map = CMapParser.Parse(CMap(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "2 begincidrange <0041> <0042> 100 <0061> <0062> 200 endcidrange"));

        Assert.Equal("B", map[101]);
        Assert.Equal("b", map[201]);
    }

    [Fact]
    public void ToUnicode_WhiteSpaceInsideAHexString_IsIgnored()
    {
        // PostScript and PDF hex strings both ignore white space between the digits.
        var map = CMapParser.Parse(CMap(
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "1 beginbfchar\n<4 1> <05 D0>\nendbfchar\n" +
            "1 beginbfrange\n<6 1> <6 2> <05 D1>\nendbfrange"));

        Assert.Equal("א", map[0x41]);
        Assert.Equal("ב", map[0x61]);
        Assert.Equal("ג", map[0x62]);
    }

    [Fact]
    public void Encoding_CodespaceRangesOnOneLine_SplitMixedWidthCodes()
    {
        // One-byte codes up to 0x80 and two-byte codes from 0x8140, written on one line. Read as
        // one range, 0x81 0x40 falls outside it and is split into two one-byte codes.
        var codec = CMapCodec.Parse(CMap(
            "2 begincodespacerange <00> <80> <8140> <9FFC> endcodespacerange\n" +
            "1 begincidrange\n<00> <80> 1\nendcidrange\n" +
            "1 begincidrange\n<8140> <9FFC> 500\nendcidrange"));

        var codes = codec.Decode(new byte[] { 0x41, 0x81, 0x40 });

        Assert.Equal(new[] { 1, 2 }, codes.Select(c => c.ByteLength));
        Assert.Equal(new[] { 0x41, 0x8140 }, codes.Select(c => c.Value));
        // A one-byte fallback also yields [1, 2] here, so each code must have matched a range.
        Assert.All(codes, c => Assert.True(c.CodespaceMatched));
    }

    [Fact]
    public void Encoding_CidrangeEntriesOnOneLine_AreAllApplied()
    {
        var codec = CMapCodec.Parse(CMap(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "2 begincidrange <0020> <0030> 1 <0100> <0110> 700 endcidrange"));

        Assert.Equal(1 + (0x25 - 0x20), codec.ToCid(0x25, 2));
        Assert.Equal(700 + (0x105 - 0x100), codec.ToCid(0x105, 2));
    }

    [Fact]
    public void Encoding_CidcharEntriesOnOneLine_AreAllApplied()
    {
        var codec = CMapCodec.Parse(CMap(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "2 begincidchar <0041> 34 <0042> 35 endcidchar"));

        Assert.Equal(34, codec.ToCid(0x41, 2));
        Assert.Equal(35, codec.ToCid(0x42, 2));
    }

    [Fact]
    public void Encoding_EntriesWithNoWhiteSpaceBetweenTokens_AreRead()
    {
        // "<" is a PostScript delimiter, so a number ends where the next hex string begins.
        var codec = CMapCodec.Parse(CMap(
            "1 begincodespacerange <0000><FFFF> endcodespacerange\n" +
            "2 begincidchar <0041>34<0042>35 endcidchar"));

        Assert.Equal(34, codec.ToCid(0x41, 2));
        Assert.Equal(35, codec.ToCid(0x42, 2));
    }

    [Fact]
    public void ToUnicode_MalformedUnterminatedHexString_EndsItsSectionOnly()
    {
        // The destination's closing bracket is missing. Nothing after it in the section can be
        // paired, so the section stops there; the next section still maps, and nothing throws.
        var map = CMapParser.Parse(CMap(
            "1 begincodespacerange <00> <FF> endcodespacerange\n" +
            "1 beginbfchar\n<41> <05D0\nendbfchar\n" +
            "1 beginbfrange\n<61> <62> <05D1>\nendbfrange"));

        Assert.False(map.ContainsKey(0x41));
        Assert.Equal("ב", map[0x61]);
    }

    [Fact]
    public void Encoding_AnEntrySplitAcrossLines_IsStillRead()
    {
        // The other half of "a line break is white space": one cidrange entry over three lines.
        var codec = CMapCodec.Parse(CMap(
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "1 begincidrange\n<0100>\n<0110>\n700\nendcidrange"));

        Assert.Equal(700 + 5, codec.ToCid(0x105, 2));
    }
}
