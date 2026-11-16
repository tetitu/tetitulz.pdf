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
}
