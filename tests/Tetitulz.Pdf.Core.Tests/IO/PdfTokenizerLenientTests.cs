// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.IO;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// The tokenizer's lenient readings of malformed input, each pinned. The sweep found none of them
/// under test: removing any one passed the suite.
/// </summary>
public class PdfTokenizerLenientTests
{
    private static PdfToken First(string input)
        => new PdfTokenizer(new MemoryStream(Encoding.Latin1.GetBytes(input))).NextToken();

    /// <summary>
    /// Malformed name: a <c>#</c> with nothing after it is kept as a literal <c>#</c>, as another reader
    /// does, rather than dropped.
    /// </summary>
    [Fact]
    public void Malformed_ABareNumberSignInAName_IsKept()
    {
        PdfToken token = First("/A# ");

        Assert.Equal(PdfTokenType.Name, token.Type);
        Assert.Equal("A#", token.Value);
    }

    /// <summary>
    /// An octal escape above 0377 keeps its low byte (§7.3.4.2: high-order overflow is ignored);
    /// the control is an in-range escape.
    /// </summary>
    [Theory]
    [InlineData(@"(\777)", "ÿ")]
    [InlineData(@"(\101)", "A")]
    public void Malformed_AnOctalEscapePast0377_KeepsItsLowByte(string input, string value)
    {
        Assert.Equal(value, First(input).Value);
    }

    /// <summary>Malformed number: <c>--42</c> reads as -42, as another reader reads it.</summary>
    [Fact]
    public void Malformed_ADoubleMinus_IsOneMinus()
    {
        PdfToken token = First("--42 ");

        Assert.Equal(PdfTokenType.Number, token.Type);
        Assert.Equal("-42", token.Value);
    }

    /// <summary>Malformed: a lone <c>&gt;</c> is read as the end of an empty hex string.</summary>
    [Fact]
    public void Malformed_ALoneGreaterThan_IsAnEmptyHexString()
    {
        PdfToken token = First("> ");

        Assert.Equal(PdfTokenType.HexString, token.Type);
        Assert.Equal(string.Empty, token.Value);
    }

    /// <summary>A sign or point with no digits is not a number; it is a keyword. The control is a number.</summary>
    [Theory]
    [InlineData("+ ", PdfTokenType.Keyword)]
    [InlineData("- ", PdfTokenType.Keyword)]
    [InlineData(". ", PdfTokenType.Keyword)]
    [InlineData("+5 ", PdfTokenType.Number)]
    public void ASignWithoutDigits_IsAKeyword(string input, PdfTokenType type)
    {
        Assert.Equal(type, First(input).Type);
    }

    /// <summary>Malformed hex string: a byte that is not a hex digit is skipped; the digits stand.</summary>
    [Fact]
    public void Malformed_ANonHexByteInAHexString_IsSkipped()
    {
        Assert.Equal("41", First("<4G1>").Value);
    }
}
