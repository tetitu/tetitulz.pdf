// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Plugins.Docx;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// <see cref="XmlTextSanitizer"/> at the unit that owns the rule — XML 1.0 §2.2's <c>Char</c>
/// production.
///
/// <para><b>Why these are here rather than only end to end.</b> All three converters split their
/// text on <c>\n</c> before sanitizing, so no line handed to the sanitizer ever contains one: a
/// mutation deleting the tab/LF/CR exemption from <c>IsLegalScalar</c> <b>survived</b> the
/// converter-level tests untouched. The exemption is still load-bearing — an interior tab reaches
/// the sanitizer through every path, and the XLSX path trims only the leading and trailing ones —
/// so it needs an assertion that can see it.</para>
///
/// <para><b>Every character under test is built from its code point rather than typed into the
/// source.</b> A raw C0 byte is invisible in a diff, and U+FFFE / U+FFFF do not survive the source
/// pipeline at all: written literally they reach the compiled assembly as three U+FFFD each, so the
/// first version of this file tested a string that was not the one it named.</para>
/// </summary>
public class XmlTextSanitizerTests
{
    private static string Wrap(int codePoint) => "A" + (char)codePoint + "B";

    [Theory]
    [InlineData(0x0001)]   // the C0 character that aborted the conversion
    [InlineData(0x0002)]
    [InlineData(0x000B)]   // vertical tab — illegal despite being whitespace
    [InlineData(0x000C)]   // form feed
    [InlineData(0x001F)]   // last illegal C0
    [InlineData(0xFFFE)]   // non-characters
    [InlineData(0xFFFF)]
    [InlineData(0xD800)]   // unpaired high surrogate
    [InlineData(0xDC00)]   // unpaired low surrogate
    public void Sanitize_DropsWhatXmlForbidsAndKeepsTheTextAroundIt(int codePoint)
    {
        Assert.Equal("AB", XmlTextSanitizer.Sanitize(Wrap(codePoint)));
    }

    /// <summary>
    /// Tab, line feed and carriage return are inside XML 1.0's <c>Char</c> production. Stripping the
    /// C0 range wholesale is the obvious-looking implementation, and it would flatten every document
    /// that reaches the sanitizer with its line breaks intact.
    /// </summary>
    [Theory]
    [InlineData(0x0009)]
    [InlineData(0x000A)]
    [InlineData(0x000D)]
    public void Sanitize_KeepsTheWhitespaceXmlAllows(int codePoint)
    {
        string input = Wrap(codePoint);
        Assert.Equal(input, XmlTextSanitizer.Sanitize(input));
    }

    /// <summary>The edges of the two legal BMP ranges, which an off-by-one would eat.</summary>
    [Theory]
    [InlineData(0x0020)]
    [InlineData(0xD7FF)]
    [InlineData(0xE000)]
    [InlineData(0xFFFD)]
    public void Sanitize_KeepsTheEdgesOfTheLegalRanges(int codePoint)
    {
        string input = Wrap(codePoint);
        Assert.Equal(input, XmlTextSanitizer.Sanitize(input));
    }

    /// <summary>An astral character is a legal surrogate PAIR and must survive intact.</summary>
    [Fact]
    public void Sanitize_KeepsAValidSurrogatePair()
    {
        string emoji = "a" + char.ConvertFromUtf32(0x1F600) + "b";
        Assert.Equal(emoji, XmlTextSanitizer.Sanitize(emoji));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain ASCII, nothing to do")]
    public void Sanitize_LeavesCleanTextExactlyAsItWas(string input)
    {
        Assert.Equal(input, XmlTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_OfNull_ReturnsEmptyRatherThanThrowing()
    {
        Assert.Equal(string.Empty, XmlTextSanitizer.Sanitize(null!));
    }

    /// <summary>
    /// <c>ForSpreadsheet</c>'s escape is a scan now, not a pattern over the file's text — held to
    /// the pattern it replaced, overlapping and truncated sequences included.
    /// </summary>
    [Theory]
    [InlineData("AB_x0031_")]
    [InlineData("_x005F_x0031_")]
    [InlineData("_x00_")]
    [InlineData("_xABCD")]
    [InlineData("_xAbCd_")]
    [InlineData("_x0031x and more")]
    [InlineData("_X0031_")]
    [InlineData("__x0031__x00Gf_")]
    [InlineData("no underscores")]
    [InlineData("")]
    public void ForSpreadsheet_AgreesWithThePatternItReplaced(string input)
    {
        string expected = System.Text.RegularExpressions.Regex.Replace(
            XmlTextSanitizer.Sanitize(input), "_(?=x[0-9A-Fa-f]{4}_)", "_x005F_");
        Assert.Equal(expected, XmlTextSanitizer.ForSpreadsheet(input));
    }
}
