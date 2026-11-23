// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>What XML 1.0 forbids goes, one U+FFFD each; what it allows stays, the same string.</summary>
public class XmlLegalTextTests
{
    private const char Replacement = (char)0xFFFD;

    [Theory]
    [InlineData((char)0x01)]
    [InlineData((char)0x0B)]
    [InlineData((char)0x1F)]
    [InlineData((char)0xFFFE)]
    [InlineData((char)0xFFFF)]
    [InlineData((char)0xD800)]
    [InlineData((char)0xDC00)]
    public void ACharacterXmlForbidsIsReplaced(char forbidden)
    {
        string result = XmlLegalText.Replace("a" + forbidden + "b", out bool changed);

        Assert.True(changed);
        Assert.Equal("a" + Replacement + "b", result);
    }

    [Fact]
    public void AHighSurrogateAtTheEndIsReplaced()
    {
        string result = XmlLegalText.Replace("a" + (char)0xD83D, out bool changed);

        Assert.True(changed);
        Assert.Equal("a" + Replacement, result);
    }

    [Fact]
    public void WhatXmlAllowsIsTheSameString()
    {
        string text = "a\tb\nc\r" + char.ConvertFromUtf32(0x1F600) + (char)0x05D0 + (char)0xFFFD;

        string result = XmlLegalText.Replace(text, out bool changed);

        Assert.False(changed);
        Assert.Same(text, result);
    }

    /// <summary>The pair before the first replacement and the pair after it are both kept whole.</summary>
    [Fact]
    public void APairOnEitherSideOfAReplacementIsKept()
    {
        string pair = char.ConvertFromUtf32(0x1F600);

        string result = XmlLegalText.Replace(pair + (char)0x01 + pair, out bool changed);

        Assert.True(changed);
        Assert.Equal(pair + Replacement + pair, result);
    }

    [Fact]
    public void NullIsTheEmptyString()
    {
        Assert.Equal("", XmlLegalText.Replace(null, out bool changed));
        Assert.False(changed);
    }

    /// <summary>
    /// The other answer, for text read out of a PDF: what XML forbids is removed — a control character, a lone half
    /// of a pair, U+FFFE — while tab, line feed, carriage return and a whole pair stay. Legal text comes back as the
    /// same instance.
    /// </summary>
    [Theory]
    [InlineData("a\u0001b", "ab")]
    [InlineData("a\tb\nc\rd", "a\tb\nc\rd")]
    [InlineData("😀\u0001😀", "😀😀")]
    public void Remove_DropsWhatXmlForbids_AndKeepsTheRest(string input, string expected)
    {
        Assert.Equal(expected, XmlLegalText.Remove(input));
    }

    /// <summary>
    /// U+FFFE and U+FFFF are not XML <c>Char</c> though <c>XmlConvert.IsXmlChar</c> admits them, and a lone half of a pair is not
    /// one either. Built in code: an attribute's string argument does not carry them intact.
    /// </summary>
    [Fact]
    public void Remove_DropsTheTwoNoncharactersXmlForbids()
    {
        string input = "x" + (char)0xFFFE + "y" + (char)0xFFFF;
        Assert.Equal("xy", XmlLegalText.Remove(input));
        Assert.Equal("x" + (char)0xFFFD + "y" + (char)0xFFFD, XmlLegalText.Replace(input, out bool changed));
        Assert.Equal("xy", XmlLegalText.Remove("x" + (char)0xD800 + "y"));   // a lone half, each way round
        Assert.Equal("xy", XmlLegalText.Remove("x" + (char)0xDC00 + "y"));
        Assert.True(changed);
    }

    [Fact]
    public void Remove_LeavesLegalTextAsItIs()
    {
        const string legal = "שלום, world\t1";
        Assert.Same(legal, XmlLegalText.Remove(legal));
        Assert.Equal("", XmlLegalText.Remove(null));
    }

    /// <summary>The SVG export's escape drops by the same rule: it is one rule, not two that agree.</summary>
    [Fact]
    public void TheSvgEscape_RemovesByTheSameRule()
    {
        Assert.Equal("ab&amp;", XmlEscape.EscapeText("a\u0001\uD800b&"));
    }
}
