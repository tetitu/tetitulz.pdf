// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Document;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Scanner behaviour, asserted on the token stream itself — the three tokenizer bugs the scanner's
/// own remarks name are each pinned by a test that fails if the handling is removed.
/// </summary>
public sealed class PsScannerTests
{
    private static List<PsToken> Scan(string source)
    {
        PostScriptDiagnostics.Clear();
        return new PsScanner(Encoding.ASCII.GetBytes(source)).ReadAll();
    }

    // ==================================================================
    //  Edges
    // ==================================================================

    /// <summary>A stray <c>&gt;</c> as the very last byte is skipped, not read past.</summary>
    [Fact]
    public void AStrayGreaterThanAtTheEndOfInputIsSkipped()
    {
        var tokens = Scan("abc >");

        Assert.Single(tokens);
        Assert.Equal("abc", tokens[0].Text);
    }

    /// <summary>The hex digit 9 is a digit: <c>&lt;99&gt;</c> is the byte 0x99.</summary>
    [Fact]
    public void TheHexDigitNineIsADigit()
    {
        var tokens = Scan("<99>");

        Assert.Equal(new byte[] { 0x99 }, tokens.Single().Bytes);
    }

    /// <summary>
    /// <c>s8W-!</c> encodes exactly 0xFFFFFFFF, the largest value a base-85 group may hold, so it
    /// is four 0xFF bytes and not an overflow.
    /// </summary>
    [Fact]
    public void TheLargestBase85GroupIsKept()
    {
        var tokens = Scan("<~s8W-!~>");

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, tokens.Single().Bytes);
    }

    // ==================================================================
    //  Delimiters
    // ==================================================================

    /// <summary>
    /// PLRM §3.1: a delimiter ends a token with no whitespace needed. A scanner that splits only on
    /// whitespace produces the single name "abc/def" here.
    /// </summary>
    [Fact]
    public void Delimiter_EndsANameWithoutWhitespace()
    {
        var tokens = Scan("abc/def");

        Assert.Equal(2, tokens.Count);
        Assert.Equal(PsTokenKind.ExecutableName, tokens[0].Kind);
        Assert.Equal("abc", tokens[0].Text);
        Assert.Equal(PsTokenKind.LiteralName, tokens[1].Kind);
        Assert.Equal("def", tokens[1].Text);
    }

    [Fact]
    public void Delimiter_EndsANumberWithoutWhitespace()
    {
        var tokens = Scan("1 2 add}");

        Assert.Equal(4, tokens.Count);
        Assert.Equal(PsTokenKind.Integer, tokens[0].Kind);
        Assert.Equal("add", tokens[2].Text);
        Assert.Equal(PsTokenKind.ProcClose, tokens[3].Kind);
    }

    // ==================================================================
    //  Strings
    // ==================================================================

    /// <summary>
    /// A brace inside a string literal is not a brace. This is the case that desynchronises
    /// PdfFunctionEvaluator's brace-matching tokenizer, which is correct for Type 4 (no strings)
    /// and unusable here.
    /// </summary>
    [Fact]
    public void LiteralString_BraceInsideIsNotAProcedureDelimiter()
    {
        var tokens = Scan("(a { b) 42");

        Assert.Equal(2, tokens.Count);
        Assert.Equal(PsTokenKind.String, tokens[0].Kind);
        Assert.Equal("a { b", Encoding.ASCII.GetString(tokens[0].Bytes!));
        Assert.Equal(PsTokenKind.Integer, tokens[1].Kind);
        Assert.Equal(42, tokens[1].Number);
    }

    [Fact]
    public void LiteralString_NestedParensAreBalancedAndKept()
    {
        var tokens = Scan("(outer (inner) tail)");

        Assert.Single(tokens);
        Assert.Equal("outer (inner) tail", Encoding.ASCII.GetString(tokens[0].Bytes!));
    }

    [Theory]
    [InlineData(@"(a\nb)", "a\nb")]
    [InlineData(@"(a\rb)", "a\rb")]
    [InlineData(@"(a\tb)", "a\tb")]
    [InlineData(@"(a\\b)", @"a\b")]
    [InlineData(@"(a\(b)", "a(b")]
    [InlineData(@"(a\)b)", "a)b")]
    [InlineData(@"(\101\102)", "AB")]      // octal escapes
    [InlineData(@"(\7)", "")]        // one-digit octal
    [InlineData(@"(a\zb)", "azb")]         // "any other" → the character itself
    public void LiteralString_EscapesAreDecoded(string source, string expected)
    {
        var tokens = Scan(source);

        Assert.Single(tokens);
        Assert.Equal(expected, Encoding.ASCII.GetString(tokens[0].Bytes!));
    }

    [Fact]
    public void LiteralString_BackslashNewlineIsALineContinuation()
    {
        var tokens = Scan("(one\\\ntwo)");

        Assert.Single(tokens);
        Assert.Equal("onetwo", Encoding.ASCII.GetString(tokens[0].Bytes!));
    }

    [Fact]
    public void HexString_IsDecodedAndWhitespaceInsideIsIgnored()
    {
        var tokens = Scan("<4A 4B\n4C>");

        Assert.Single(tokens);
        Assert.Equal("JKL", Encoding.ASCII.GetString(tokens[0].Bytes!));
    }

    /// <summary>PLRM §3.2.2: an odd final hex digit is padded with 0, so &lt;4A5&gt; is 0x4A 0x50.</summary>
    [Fact]
    public void HexString_OddDigitCountIsPaddedWithZero()
    {
        var tokens = Scan("<4A5>");

        Assert.Single(tokens);
        Assert.Equal(new byte[] { 0x4A, 0x50 }, tokens[0].Bytes);
    }

    [Fact]
    public void DictOpen_IsDistinguishedFromAHexString()
    {
        var tokens = Scan("<< /K 1 >>");

        Assert.Equal(4, tokens.Count);
        Assert.Equal(PsTokenKind.DictOpen, tokens[0].Kind);
        Assert.Equal(PsTokenKind.DictClose, tokens[3].Kind);
    }

    // ==================================================================
    //  Comments
    // ==================================================================

    [Fact]
    public void Comment_RunsToEndOfLineAndIsDropped()
    {
        var tokens = Scan("1 % this is 999 ignored\n2");

        Assert.Equal(2, tokens.Count);
        Assert.Equal(1, tokens[0].Number);
        Assert.Equal(2, tokens[1].Number);
    }

    /// <summary>A '%' inside a string is content, not the start of a comment.</summary>
    [Fact]
    public void Comment_PercentInsideAStringIsContent()
    {
        var tokens = Scan("(50% off) 7");

        Assert.Equal(2, tokens.Count);
        Assert.Equal("50% off", Encoding.ASCII.GetString(tokens[0].Bytes!));
        Assert.Equal(7, tokens[1].Number);
    }

    // ==================================================================
    //  Names
    // ==================================================================

    [Fact]
    public void LiteralName_AndExecutableName_AreDistinguished()
    {
        var tokens = Scan("/foo foo");

        Assert.Equal(PsTokenKind.LiteralName, tokens[0].Kind);
        Assert.Equal(PsTokenKind.ExecutableName, tokens[1].Kind);
        Assert.Equal("foo", tokens[0].Text);
        Assert.Equal("foo", tokens[1].Text);
    }

    [Fact]
    public void ImmediateName_IsRecognisedAsItsOwnKind()
    {
        var tokens = Scan("//foo");

        Assert.Single(tokens);
        Assert.Equal(PsTokenKind.ImmediateName, tokens[0].Kind);
        Assert.Equal("foo", tokens[0].Text);
    }

    // ==================================================================
    //  Numbers
    // ==================================================================

    [Theory]
    [InlineData("123", 123)]
    [InlineData("-98", -98)]
    [InlineData("+17", 17)]
    [InlineData("16#FF", 255)]
    [InlineData("2#1000", 8)]
    [InlineData("8#777", 511)]
    public void Integer_FormsAreParsed(string source, long expected)
    {
        var tokens = Scan(source);

        Assert.Single(tokens);
        Assert.Equal(PsTokenKind.Integer, tokens[0].Kind);
        Assert.Equal(expected, (long)tokens[0].Number);
    }

    [Theory]
    [InlineData("-1.5", -1.5)]
    [InlineData(".002", 0.002)]
    [InlineData("4.", 4.0)]
    [InlineData("1E6", 1000000.0)]
    [InlineData("1.0E-3", 0.001)]
    public void Real_FormsAreParsed(string source, double expected)
    {
        var tokens = Scan(source);

        Assert.Single(tokens);
        Assert.Equal(PsTokenKind.Real, tokens[0].Kind);
        Assert.Equal(expected, tokens[0].Number, 10);
    }

    /// <summary>
    /// Anything that is not one of the three number syntaxes is a name — including the near-misses
    /// that a lenient parser would silently accept as 0.
    /// </summary>
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("e5")]
    [InlineData("16#GG")]
    [InlineData("1#0")]     // radix < 2
    public void NonNumericToken_IsAName(string source)
    {
        var tokens = Scan(source);

        Assert.Single(tokens);
        Assert.Equal(PsTokenKind.ExecutableName, tokens[0].Kind);
        Assert.Equal(source, tokens[0].Text);
    }

    // ==================================================================
    //  Adversarial input
    // ==================================================================

    /// <summary>
    /// An unterminated '(' followed by a large tail must not become one enormous allocation, and
    /// must not hang. The scanner truncates at the cap and reports.
    /// </summary>
    [Fact]
    public void UnterminatedStringLiteral_DoesNotHangAndIsTruncated()
    {
        string source = "(" + new string('x', 2_000_000);

        var tokens = Scan(source);

        Assert.Single(tokens);
        Assert.True(tokens[0].Bytes!.Length <= PsLimits.MaxSingleStringLength + 1,
            $"string was not truncated: {tokens[0].Bytes.Length} bytes");
        Assert.Contains("truncated", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void UnterminatedHexString_DoesNotHangAndIsReported()
    {
        var tokens = Scan("<4A4B4C");

        Assert.Single(tokens);
        Assert.Equal("JKL", Encoding.ASCII.GetString(tokens[0].Bytes!));
        Assert.Contains("unterminated", PostScriptDiagnostics.LastError);
    }

    /// <summary>
    /// A long run of stray delimiters must not recurse once per character — that overflows the
    /// stack, which is uncatchable. The scanner loops instead.
    ///
    /// <para>Note the separators: <c>&gt;&gt;</c> is a legitimate DictClose token, so a solid run of
    /// '&gt;' is 100 000 valid tokens rather than garbage. Only an isolated '&gt;' is stray.</para>
    /// </summary>
    [Fact]
    public void MalformedStrayDelimiters_DoNotOverflowTheStack()
    {
        var sb = new StringBuilder(400_000);
        for (int i = 0; i < 200_000; i++) sb.Append("> ");
        sb.Append('5');

        var tokens = Scan(sb.ToString());

        Assert.Single(tokens);
        Assert.Equal(5, tokens[0].Number);
    }

    /// <summary>An unbalanced ')' outside a string is skipped, not treated as string content.</summary>
    [Fact]
    public void MalformedUnbalancedCloseParen_IsSkipped()
    {
        var tokens = Scan(") 5");

        Assert.Single(tokens);
        Assert.Equal(5, tokens[0].Number);
        Assert.Contains("unbalanced", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void CorruptBinaryGarbage_ScansWithoutThrowingAndTerminates()
    {
        // Deterministic pseudo-random bytes — a fixed seed, because a conversion has to be
        // reproducible for its tests to mean anything.
        var bytes = new byte[64 * 1024];
        int state = 12345;
        for (int i = 0; i < bytes.Length; i++)
        {
            state = unchecked(state * 1103515245 + 12345);
            bytes[i] = (byte)(state >> 16);
        }

        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() => new PsScanner(bytes).ReadAll());

        Assert.Null(ex);
    }

    // ==================================================================
    //  Binary sections
    // ==================================================================

    /// <summary>
    /// An eexec section's encrypted bytes must never be tokenised: among 4 KB of ciphertext there
    /// will be bytes that look like '{', '(' and operator names, and interpreting them corrupts
    /// everything after. The scanner skips to the standard zero-run trailer.
    /// </summary>
    [Fact]
    public void EexecBinarySection_IsSkippedNotInterpreted()
    {
        string source = "eexec ";
        var sb = new StringBuilder(source);
        sb.Append("{{{ (unterminated showpage ");      // would wreck the stream if tokenised
        sb.Append(new string('0', 512));               // the conventional trailer
        sb.Append(" cleartomark 99");

        PostScriptDiagnostics.Clear();
        var scanner = new PsScanner(Encoding.ASCII.GetBytes(sb.ToString()));

        // The scanner consumes the section itself, the moment it scans the name `eexec` — it has
        // to, because the whole input is tokenised before any of it executes, so an operator acting
        // at execution time would find the bytes already behind it.
        var tokens = scanner.ReadAll();

        Assert.True(scanner.SkippedBinarySection);
        Assert.Equal("eexec", tokens[0].Text);

        // Nothing from inside the section became a token.
        Assert.DoesNotContain(tokens, t => t.Kind == PsTokenKind.ProcOpen);
        Assert.DoesNotContain(tokens, t => t.Text == "showpage");

        // `cleartomark` is part of the program's trailer and is consumed with it — leaving it
        // behind would execute it against a stack that has no mark, raising unmatchedmark on every
        // document that embeds a font.
        Assert.DoesNotContain(tokens, t => t.Text == "cleartomark");

        // …and scanning resumes at the real PostScript that follows.
        Assert.Contains(tokens, t => t.Kind == PsTokenKind.Integer && t.Number == 99);
    }

    // ==================================================================
    //  %%BeginData / %%BeginBinary (DSC 3.0 §5.7)
    // ==================================================================

    private static (List<PsToken> Tokens, PsScanner Scanner) ScanWithDsc(string source)
    {
        var scanner = new PsScanner(Encoding.Latin1.GetBytes(source), null, new DscScanner());
        return (scanner.ReadAll(), scanner);
    }

    /// <summary>
    /// An ASCII (or Hex) data section is program text: the <c>image</c> that reads the data is
    /// normally the section's first line, so skipping the section by its count skipped the operator
    /// with it. The comment exists for document managers, not interpreters; the interpreter runs it.
    /// </summary>
    [Theory]
    [InlineData("%%BeginData: 7 ASCII Bytes\n1 2 add\n%%EndData\n99")]
    [InlineData("%%BeginData: 1 Hex Lines\n1 2 add\n%%EndData\n99")]
    public void BeginData_AsciiSection_IsInterpretedNotSkipped(string source)
    {
        var (tokens, scanner) = ScanWithDsc(source);

        Assert.Contains(tokens, t => t.Kind == PsTokenKind.ExecutableName && t.Text == "add");
        Assert.Contains(tokens, t => t.Kind == PsTokenKind.Integer && t.Number == 99);
        Assert.False(scanner.SkippedBinarySection);
    }

    /// <summary>A binary section (the default type) is skipped by its byte count, as before.</summary>
    [Theory]
    [InlineData("%%BeginData: 8 Binary Bytes\n{{{(junk\n99")]
    [InlineData("%%BeginData: 8\n{{{(junk\n99")]
    [InlineData("%%BeginBinary: 8\n{{{(junk\n99")]
    public void BeginData_BinarySection_IsSkippedByByteCount(string source)
    {
        var (tokens, scanner) = ScanWithDsc(source);

        Assert.True(scanner.SkippedBinarySection);
        Assert.DoesNotContain(tokens, t => t.Kind == PsTokenKind.ProcOpen);
        Assert.Contains(tokens, t => t.Kind == PsTokenKind.Integer && t.Number == 99);
    }

    /// <summary>
    /// A count in <c>Lines</c> is a line count: two lines of binary are skipped whole — with LF,
    /// CR LF or CR endings — where skipping two <i>bytes</i> left the rest to be tokenised.
    /// </summary>
    [Theory]
    [InlineData("%%BeginData: 2 Binary Lines\n{{{\n(((\n99")]
    [InlineData("%%BeginData: 2 Binary Lines\r\n{{{\r\n(((\r\n99")]
    [InlineData("%%BeginData: 2 Binary Lines\r{{{\r(((\r99")]
    public void BeginData_BinaryLines_IsSkippedByLineCount(string source)
    {
        var (tokens, scanner) = ScanWithDsc(source);

        Assert.True(scanner.SkippedBinarySection);
        Assert.DoesNotContain(tokens, t => t.Kind == PsTokenKind.ProcOpen);
        Assert.DoesNotContain(tokens, t => t.Kind == PsTokenKind.String);
        Assert.Contains(tokens, t => t.Kind == PsTokenKind.Integer && t.Number == 99);
    }
}
