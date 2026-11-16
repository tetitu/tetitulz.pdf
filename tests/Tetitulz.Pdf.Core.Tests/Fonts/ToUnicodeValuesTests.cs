// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="ToUnicodeValues.TryFindForbidden"/> — a <c>/ToUnicode</c> value of U+0000, U+FEFF or
/// U+FFFE (ISO 14289-1 7.21.7, ISO 19005-2/3 6.2.11.7.2, ISO 19005-4 6.2.10.7) — and the parser's reading
/// of an empty destination, <c>&lt;&gt;</c>, as no text rather than as U+0000.
/// </summary>
public class ToUnicodeValuesTests
{
    private static byte[] CMap(string body)
        => Encoding.ASCII.GetBytes("begincmap\n1 begincodespacerange\n<00><FF>\nendcodespacerange\n" + body + "\nendcmap\n");

    [Theory]
    [InlineData("1 beginbfchar\n<41><0000>\nendbfchar", 0x41, 0x0000)]
    [InlineData("1 beginbfchar\n<42><FEFF>\nendbfchar", 0x42, 0xFEFF)]
    [InlineData("1 beginbfchar\n<43><0041FFFE>\nendbfchar", 0x43, 0xFFFE)]           // inside a longer value
    [InlineData("1 beginbfrange\n<44><45><FEFF>\nendbfrange", 0x44, 0xFEFF)]
    public void AForbiddenValue_IsFoundWithItsCode(string body, int code, int value)
    {
        Assert.True(ToUnicodeValues.TryFindForbidden(CMap(body), out int foundCode, out char foundValue));
        Assert.Equal(code, foundCode);
        Assert.Equal(value, foundValue);
    }

    [Theory]
    [InlineData("2 beginbfchar\n<41><0041>\n<42><05D0>\nendbfchar")]
    [InlineData("1 beginbfchar\n<41><>\nendbfchar")]                               // empty: no text, not U+0000
    [InlineData("1 beginbfrange\n<41><42><0041>\nendbfrange")]
    public void OrdinaryValues_AreNotForbidden(string body)
        => Assert.False(ToUnicodeValues.TryFindForbidden(CMap(body), out _, out _));

    [Fact]
    public void NoData_IsNotForbidden()
    {
        Assert.False(ToUnicodeValues.TryFindForbidden(null!, out _, out _));
        Assert.False(ToUnicodeValues.TryFindForbidden(new byte[0], out _, out _));
    }

    [Fact]
    public void AnEmptyDestination_MapsTheCodeToNoText()
    {
        var map = CMapParser.Parse(CMap("2 beginbfchar\n<62><>\n<63><0063>\nendbfchar"));
        Assert.True(map.TryGetValue(0x62, out string? empty));
        Assert.Equal("", empty);
        Assert.Equal("c", map[0x63]);
    }
}
