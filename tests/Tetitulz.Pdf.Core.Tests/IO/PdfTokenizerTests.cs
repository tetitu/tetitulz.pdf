// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.IO;

namespace Tetitulz.Pdf.Core.Tests.IO;

public class PdfTokenizerTests
{
    private PdfTokenizer CreateTokenizer(string input)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
        return new PdfTokenizer(stream);
    }

    [Fact]
    public void NextToken_ReadsInteger()
    {
        var tokenizer = CreateTokenizer("42");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Number, token.Type);
        Assert.Equal("42", token.Value);
        Assert.Equal(42, token.IntValue);
    }

    [Fact]
    public void NextToken_ReadsNegativeNumber()
    {
        var tokenizer = CreateTokenizer("-17");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Number, token.Type);
        Assert.Equal("-17", token.Value);
    }

    [Fact]
    public void NextToken_ReadsRealNumber()
    {
        var tokenizer = CreateTokenizer("3.14");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Number, token.Type);
        Assert.Equal(3.14, token.DoubleValue, 5);
    }

    [Fact]
    public void NextToken_ReadsName()
    {
        var tokenizer = CreateTokenizer("/Type");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Name, token.Type);
        Assert.Equal("Type", token.Value);
    }

    [Fact]
    public void NextToken_ReadsNameWithHexEscape()
    {
        var tokenizer = CreateTokenizer("/A#42C");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Name, token.Type);
        Assert.Equal("ABC", token.Value);
    }

    [Fact]
    public void NextToken_ReadsLiteralString()
    {
        var tokenizer = CreateTokenizer("(Hello World)");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.LiteralString, token.Type);
        Assert.Equal("Hello World", token.Value);
    }

    [Fact]
    public void NextToken_ReadsNestedParensInString()
    {
        var tokenizer = CreateTokenizer("(Hello (nested) World)");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.LiteralString, token.Type);
        Assert.Equal("Hello (nested) World", token.Value);
    }

    [Fact]
    public void NextToken_ReadsStringWithEscapes()
    {
        var tokenizer = CreateTokenizer(@"(Hello\nWorld)");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.LiteralString, token.Type);
        Assert.Contains("\n", token.Value);
    }

    [Fact]
    public void NextToken_ReadsHexString()
    {
        var tokenizer = CreateTokenizer("<48656C6C6F>");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.HexString, token.Type);
        Assert.Equal("48656C6C6F", token.Value);
    }

    [Fact]
    public void NextToken_ReadsBooleanTrue()
    {
        var tokenizer = CreateTokenizer("true");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Boolean, token.Type);
        Assert.True(token.BoolValue);
    }

    [Fact]
    public void NextToken_ReadsBooleanFalse()
    {
        var tokenizer = CreateTokenizer("false");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Boolean, token.Type);
        Assert.False(token.BoolValue);
    }

    [Fact]
    public void NextToken_ReadsNull()
    {
        var tokenizer = CreateTokenizer("null");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Null, token.Type);
    }

    [Fact]
    public void NextToken_ReadsDictionaryDelimiters()
    {
        var tokenizer = CreateTokenizer("<< /Type /Page >>");
        var t1 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.DictionaryStart, t1.Type);

        var t2 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Name, t2.Type);
        Assert.Equal("Type", t2.Value);

        var t3 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Name, t3.Type);
        Assert.Equal("Page", t3.Value);

        var t4 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.DictionaryEnd, t4.Type);
    }

    [Fact]
    public void NextToken_ReadsArrayDelimiters()
    {
        var tokenizer = CreateTokenizer("[1 2 3]");
        Assert.Equal(PdfTokenType.ArrayStart, tokenizer.NextToken().Type);
        Assert.Equal(PdfTokenType.Number, tokenizer.NextToken().Type);
        Assert.Equal(PdfTokenType.Number, tokenizer.NextToken().Type);
        Assert.Equal(PdfTokenType.Number, tokenizer.NextToken().Type);
        Assert.Equal(PdfTokenType.ArrayEnd, tokenizer.NextToken().Type);
    }

    [Fact]
    public void NextToken_SkipsComments()
    {
        var tokenizer = CreateTokenizer("% this is a comment\n42");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Number, token.Type);
        Assert.Equal("42", token.Value);
    }

    [Fact]
    public void NextToken_ReadsKeyword()
    {
        var tokenizer = CreateTokenizer("obj");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Keyword, token.Type);
        Assert.Equal("obj", token.Value);
    }

    [Fact]
    public void NextToken_ReadsIndirectReference()
    {
        var tokenizer = CreateTokenizer("10 0 R");
        var t1 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Number, t1.Type);
        Assert.Equal(10, t1.IntValue);

        var t2 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Number, t2.Type);
        Assert.Equal(0, t2.IntValue);

        var t3 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Keyword, t3.Type);
        Assert.Equal("R", t3.Value);
    }

    [Fact]
    public void NextToken_ReadsEofAtEnd()
    {
        var tokenizer = CreateTokenizer("");
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Eof, token.Type);
    }

    [Fact]
    public void PeekToken_DoesNotAdvance()
    {
        var tokenizer = CreateTokenizer("42 99");
        var peek = tokenizer.PeekToken();
        Assert.Equal("42", peek.Value);

        var actual = tokenizer.NextToken();
        Assert.Equal("42", actual.Value);
    }

    [Fact]
    public void NextToken_ReadsOctalEscapeInString()
    {
        var tokenizer = CreateTokenizer("(\\101)"); // \101 = 'A' in octal
        var token = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.LiteralString, token.Type);
        Assert.Equal("A", token.Value);
    }

    /// <summary>
    /// AFP-generated PDFs (e.g. ISIS AfpToPdf) omit whitespace between operators and
    /// their following numeric operands: "g1 0 0 1 0 0 Tm" means "g" + 1 0 0 1 0 0 Tm.
    /// Without the fix, "g1" is read as one keyword, "Tm" gets only 5 args, and
    /// silently ignores the matrix (text rendered at wrong position / not at all).
    /// </summary>
    [Fact]
    public void NextToken_SplitsOperatorAndDigit_AfdPattern()
    {
        // "g1" → keyword "g" then number 1 (no space between operator and its next operand)
        var tokenizer = CreateTokenizer("g1");
        var t1 = tokenizer.NextToken();
        var t2 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Keyword, t1.Type);
        Assert.Equal("g", t1.Value);
        Assert.Equal(PdfTokenType.Number, t2.Type);
        Assert.Equal("1", t2.Value);
    }

    [Fact]
    public void NextToken_SplitsOperatorAndDigit_BT0Pattern()
    {
        // "BT0" → keyword "BT" then number 0
        var tokenizer = CreateTokenizer("BT0");
        var t1 = tokenizer.NextToken();
        var t2 = tokenizer.NextToken();
        Assert.Equal(PdfTokenType.Keyword, t1.Type);
        Assert.Equal("BT", t1.Value);
        Assert.Equal(PdfTokenType.Number, t2.Type);
        Assert.Equal("0", t2.Value);
    }

    [Fact]
    public void NextToken_PreservesD0AndD1_Type3Operators()
    {
        // d0 and d1 are legitimate Type 3 glyph-width operators and must NOT be split.
        var t1 = CreateTokenizer("d0").NextToken();
        Assert.Equal("d0", t1.Value);
        var t2 = CreateTokenizer("d1").NextToken();
        Assert.Equal("d1", t2.Value);
    }
}
