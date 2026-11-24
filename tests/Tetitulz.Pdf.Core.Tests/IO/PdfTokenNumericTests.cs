// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// A number token's integer accessors saturate — to <see cref="int"/> and to <see cref="long"/>
/// alike. Resting on one <c>long.TryParse</c>, a twenty-digit integer would read as 0 through both.
/// </summary>
public sealed class PdfTokenNumericTests
{
    [Theory]
    [InlineData("99999999999999999999", long.MaxValue, int.MaxValue)]
    [InlineData("-99999999999999999999", long.MinValue, int.MinValue)]
    [InlineData("+99999999999999999999", long.MaxValue, int.MaxValue)]
    [InlineData("9223372036854775807", long.MaxValue, int.MaxValue)]
    [InlineData("4294967296", 4294967296L, int.MaxValue)]
    [InlineData("-7", -7L, -7)]
    public void AnIntegerToken_SaturatesToEachRange(string text, long expectedLong, int expectedInt)
    {
        var token = new PdfToken(PdfTokenType.Number, text, 0);

        Assert.Equal(expectedLong, token.LongValue);
        Assert.Equal(expectedInt, token.IntValue);
    }

    [Theory]
    [InlineData("12abc")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData("1.5")]
    public void AValueThatIsNotAnInteger_ReadsAsZero(string text)
    {
        var token = new PdfToken(PdfTokenType.Number, text, 0);

        Assert.Equal(0L, token.LongValue);
        Assert.Equal(0, token.IntValue);
    }

    [Fact]
    public void AnIntegerBeyondLong_ParsesAsTheSaturatedNumber_NotAsZero()
    {
        var parser = new PdfParser(new MemoryStream(
            Encoding.ASCII.GetBytes("<< /Count 99999999999999999999 /N 7 >>")));

        var dict = Assert.IsType<PdfDictionary>(parser.ParseDirectObject());

        Assert.Equal(long.MaxValue, Assert.IsType<PdfNumber>(dict[PdfName.Count]).LongValue);
        Assert.Equal(7L, Assert.IsType<PdfNumber>(dict[PdfName.N]).LongValue);
    }
}
