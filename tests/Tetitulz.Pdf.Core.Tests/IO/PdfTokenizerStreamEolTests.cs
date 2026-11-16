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
/// Where a stream's data begins after the <c>stream</c> keyword (§7.3.8.1: an end of line, CR LF
/// or LF). The tokenizer consumed whatever single byte followed the keyword, end of line or not.
/// </summary>
public class PdfTokenizerStreamEolTests
{
    /// <summary>
    /// The three end-of-line forms, spaces before one (as an independent implementation reads them), and no end of line
    /// at all, where the first data byte stays. The last two rows lost a byte before.
    /// </summary>
    [Theory]
    [InlineData("stream\r\nABC", 8)]
    [InlineData("stream\nABC", 7)]
    [InlineData("stream\rABC", 7)]
    [InlineData("stream  \r\nABC", 10)]
    [InlineData("stream(BC", 6)]
    public void SkipStreamEol_LandsOnTheFirstDataByte(string input, long dataStart)
    {
        var tokenizer = new PdfTokenizer(new MemoryStream(Encoding.ASCII.GetBytes(input)));
        Assert.Equal("stream", tokenizer.NextToken().Value);

        Assert.Equal(dataStart, tokenizer.SkipStreamEol());
    }

    /// <summary>The public reader skips the same end of line, through the same method.</summary>
    [Fact]
    public void ReadStreamData_SkipsSpacesBeforeTheEndOfLine()
    {
        var tokenizer = new PdfTokenizer(new MemoryStream(Encoding.ASCII.GetBytes("stream \r\nABC")));
        tokenizer.NextToken();

        Assert.Equal(Encoding.ASCII.GetBytes("ABC"), tokenizer.ReadStreamData(3));
    }

    /// <summary>
    /// Malformed but common: <c>stream␠CR LF</c>. The space was taken for the end of line and the
    /// CR LF became data, so <c>RawData</c> began with two bytes the stream does not hold; a Flate
    /// stream lost its zlib header. The data is the three bytes the dictionary declares now.
    /// </summary>
    [Fact]
    public void ASpaceBeforeTheEndOfLine_IsNotData()
    {
        const string text = "1 0 obj\n<</Length 3>>\nstream \r\nABC\r\nendstream\nendobj\n";
        var parser = new PdfParser(new MemoryStream(Encoding.ASCII.GetBytes(text)));

        var stream = Assert.IsType<PdfStream>(parser.ParseIndirectObject());

        Assert.Equal(Encoding.ASCII.GetBytes("ABC"), stream.RawData);
    }
}
