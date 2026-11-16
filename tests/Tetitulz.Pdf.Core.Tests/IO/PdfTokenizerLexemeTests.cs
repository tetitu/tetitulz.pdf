// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// The content-stream parser reads tokens through <c>PdfTokenizer.Lex</c> — a number as its value,
/// a string as its bytes, no <see cref="PdfToken"/> — where it built a token and read the value
/// back from its text (GAPS.md §13b, 2026-10-21). The two readings must agree on every input, so
/// each case reads the same bytes both ways, over a stream and over the array, and compares a
/// double bit for bit (a sign of zero shows).
/// </summary>
public class PdfTokenizerLexemeTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("+5")]
    [InlineData("--42")]
    [InlineData("72")]
    [InlineData("123456789012345678")]          // eighteen digits: read exactly
    [InlineData("1234567890123456789")]         // nineteen: through the text
    [InlineData("-99999999999999999999")]       // saturates
    [InlineData("1.5")]
    [InlineData("-.5")]
    [InlineData(".25")]
    [InlineData("5.")]
    [InlineData("-0.0")]                        // negative zero: through the text
    [InlineData("0.1")]
    [InlineData("3.14159265358979")]            // fifteen digits: the exact division
    [InlineData("3.141592653589793")]           // sixteen: through the text
    [InlineData("0.000000000000001")]           // fifteen digits, all after the point: the exact division
    [InlineData("0.0000000000000000000001")]    // 23 digits: through the text
    [InlineData("-.")]                          // no digits: 0
    [InlineData("+.")]
    public void ANumberLexesToTheTokensOwnValue(string spelled)
    {
        PdfToken token = Tokenizer(spelled).NextToken();
        PdfTokenizer.Lexeme lexeme = new PdfTokenizer(Latin1(spelled)).Lex();

        Assert.Equal(PdfTokenType.Number, token.Type);
        Assert.Equal(token.Type, lexeme.Type);
        Assert.Equal(token.Value.Contains("."), lexeme.IsReal);
        if (lexeme.IsReal)
            Assert.Equal(BitConverter.DoubleToInt64Bits(token.DoubleValue), BitConverter.DoubleToInt64Bits(lexeme.Real));
        else
            Assert.Equal(token.LongValue, lexeme.Integer);
    }

    /// <summary>Twenty thousand seeded decimals of every length, each read both ways.</summary>
    [Fact]
    public void RandomDecimalsLexToTheTokensOwnValue()
    {
        var random = new Random(20261021);
        for (int n = 0; n < 20_000; n++)
        {
            var sb = new StringBuilder();
            if (random.Next(3) == 0) sb.Append('-');
            int intDigits = random.Next(0, 12), fracDigits = random.Next(0, 14);
            for (int i = 0; i < intDigits; i++) sb.Append((char)('0' + random.Next(10)));
            if (fracDigits > 0 || intDigits == 0) sb.Append('.');
            for (int i = 0; i < fracDigits; i++) sb.Append((char)('0' + random.Next(10)));
            if (intDigits + fracDigits == 0) sb.Append('7');
            string spelled = sb.ToString();

            PdfToken token = Tokenizer(spelled).NextToken();
            PdfTokenizer.Lexeme lexeme = new PdfTokenizer(Latin1(spelled)).Lex();
            Assert.True(token.Type == lexeme.Type, spelled);
            if (lexeme.Type != PdfTokenType.Number) continue;
            if (lexeme.IsReal)
                Assert.True(BitConverter.DoubleToInt64Bits(token.DoubleValue) == BitConverter.DoubleToInt64Bits(lexeme.Real),
                    $"{spelled}: token {token.DoubleValue.ToString("R", CultureInfo.InvariantCulture)}, lexeme {lexeme.Real.ToString("R", CultureInfo.InvariantCulture)}");
            else
                Assert.True(token.LongValue == lexeme.Integer, spelled);
        }
    }

    [Theory]
    [InlineData("(plain)")]
    [InlineData(@"(esc \n \r \t \b \f \( \) \\ \x)")]
    [InlineData(@"(oct \101\7\0123 \400)")]
    [InlineData("(nested (paren) here)")]
    [InlineData("(line \\\r\ncontinued)")]
    [InlineData("(high éÿ)")]
    [InlineData("(unterminated")]
    [InlineData("<48656C6C6F>")]
    [InlineData("<4865 6c6 C6F7>")]             // white space and an odd digit
    [InlineData("<zz41>")]                      // invalid digits skipped
    [InlineData(">")]                           // a lone '>': an empty hex string
    public void AStringLexesToTheBytesItsTokenSpells(string spelled)
    {
        PdfToken token = Tokenizer(spelled).NextToken();
        PdfTokenizer.Lexeme lexeme = new PdfTokenizer(Latin1(spelled)).Lex();

        Assert.Equal(token.Type, lexeme.Type);
        byte[] expected = token.Type == PdfTokenType.HexString
            ? PdfConstants.HexStringToBytes(token.Value)
            : Latin1Text.Encode(token.Value);
        Assert.Equal(expected, lexeme.Bytes);
    }

    /// <summary>
    /// The array mode and the stream mode are one tokenizer: the same tokens, positions and text
    /// for a stream that holds every kind, and the content parser's operations unchanged — the
    /// control is the parse a stream-backed tokenizer gives, read token by token.
    /// </summary>
    [Fact]
    public void TheArrayModeReadsAsTheStreamModeDoes()
    {
        const string content = "q 1 0 0 1 72.5 -.25 cm /F1 12 Tf BT (A\\101) Tj [(B) -250 <43>] TJ ET " +
                               "/P <</MCID 3 /Alt (x)>> BDC EMC % comment\n 0.1 0.2 0.3 rg true false null Q";
        var overStream = Tokenizer(content);
        var overArray = new PdfTokenizer(Latin1(content));
        while (true)
        {
            PdfToken a = overStream.NextToken(), b = overArray.NextToken();
            Assert.Equal(a.Type, b.Type);
            Assert.Equal(a.Value, b.Value);
            Assert.Equal(a.Position, b.Position);
            if (a.Type == PdfTokenType.Eof) break;
        }

        var ops = ContentStreamParser.Parse(Latin1(content));
        Assert.Equal(new[] { "q", "cm", "Tf", "BT", "Tj", "TJ", "ET", "BDC", "EMC", "rg", "Q" },
            Array.ConvertAll(ops.ToArray(), o => o.Operator));
        Assert.Equal(-0.25, ((PdfNumber)ops[1].Operands[5]).DoubleValue);
        Assert.Equal(new byte[] { (byte)'A', (byte)'A' }, ((PdfString)ops[4].Operands[0]).RawBytes);
        var tj = (PdfArray)ops[5].Operands[0];
        Assert.Equal(new byte[] { 0x43 }, ((PdfString)tj[2]).RawBytes);
        Assert.Equal(3, ((PdfDictionary)ops[7].Operands[1]).GetInteger(new PdfName("MCID")));
        Assert.Equal(3, ops[9].Operands.Length);
    }

    private static PdfTokenizer Tokenizer(string s) => new PdfTokenizer(new MemoryStream(Latin1(s)));

    private static byte[] Latin1(string s) => Latin1Text.Encode(s);
}
