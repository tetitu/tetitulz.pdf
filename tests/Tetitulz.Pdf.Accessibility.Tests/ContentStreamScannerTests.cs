// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Byte-level tests for <see cref="ContentStreamScanner"/>.
///
/// <para>
/// The scanner exists to answer one question — <em>where does each operation begin and end?</em> —
/// and everything downstream (marked-content splicing) trusts those offsets absolutely. An offset
/// that lands one byte inside a literal string, a comment or an inline-image payload does not
/// produce a test failure somewhere: it produces a silently corrupted customer document. So these
/// tests are written against the four classic traps (strings, hex strings, comments, inline
/// images) plus the adversarial inputs that must be refused rather than guessed at.
/// </para>
///
/// <para>
/// Everything here is self-contained: each test builds its own <c>byte[]</c>, so nothing depends
/// on a corpus, a file on disk, or the state left behind by another test.
/// </para>
/// </summary>
public class ContentStreamScannerTests
{
    // =====================================================================
    //  Helpers
    // =====================================================================

    /// <summary>Latin-1 round-trips bytes 0x00-0xFF 1:1, so a test can express binary payloads inline.</summary>
    private static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

    private static string Text(byte[] bytes, int start, int length)
        => Encoding.Latin1.GetString(bytes, start, length);

    private static byte[] Cat(params byte[][] parts)
    {
        int total = 0;
        foreach (byte[] p in parts) total += p.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (byte[] p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    /// <summary>The exact bytes a token claims to occupy, decoded for readable assertions.</summary>
    private static string Slice(byte[] content, ContentToken token)
        => Text(content, token.StartOffset, token.EndOffset - token.StartOffset);

    private static string[] Ops(IReadOnlyList<ContentToken> tokens)
    {
        var ops = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) ops[i] = tokens[i].Operator;
        return ops;
    }

    private static ContentToken Single(IReadOnlyList<ContentToken> tokens, string op)
    {
        ContentToken found = default;
        int count = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!string.Equals(tokens[i].Operator, op, StringComparison.Ordinal)) continue;
            found = tokens[i];
            count++;
        }
        Assert.Equal(1, count);
        return found;
    }

    private static string OperandText(ContentToken token, int index)
    {
        Assert.True(index < token.Operands.Count,
            "operand " + index + " missing from " + token.Operator + " (" + token.Operands.Count + " operands)");
        var s = token.Operands[index] as PdfString;
        Assert.NotNull(s);
        return s.GetText();
    }

    // =====================================================================
    //  Degenerate input
    // =====================================================================

    [Fact]
    public void ScanDetailed_NullContent_IsSafeAndEmpty()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(null);

        Assert.True(result.IsSafe);
        Assert.Null(result.Error);
        Assert.Empty(result.Tokens);
        Assert.Null(ContentStreamScanner.LastScanError);
    }

    [Fact]
    public void ScanDetailed_ZeroByteContent_IsSafeAndEmpty()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(new byte[0]);

        Assert.True(result.IsSafe);
        Assert.Empty(result.Tokens);
        Assert.False(result.HasMarkedContent);
        Assert.False(result.HasInlineImage);
        Assert.Equal(0, result.TextObjectCount);
    }

    [Fact]
    public void ScanDetailed_WhitespaceOnly_IsSafeAndEmpty()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii(" \t\r\n\f \0 "));

        Assert.True(result.IsSafe);
        Assert.Empty(result.Tokens);
    }

    [Fact]
    public void ScanDetailed_CommentOnly_IsSafeAndEmpty()
    {
        // Nothing but a comment: the "operators" inside it must not be tokenised.
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("% BT ET EMC (unbalanced\n"));

        Assert.True(result.IsSafe);
        Assert.Empty(result.Tokens);
        Assert.False(result.HasMarkedContent);
    }

    // =====================================================================
    //  Token byte ranges — the whole point of the scanner
    // =====================================================================

    [Fact]
    public void Scan_SimpleTextObject_ReportsExactByteRangeForEveryOperation()
    {
        const string source = "BT /F1 12 Tf 72 700 Td (Hi) Tj ET";
        byte[] content = Ascii(source);

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "BT", "Tf", "Td", "Tj", "ET" }, Ops(tokens));

        // Each token must cover its operands AND its keyword, and nothing else. If any of these
        // ranges is off by a byte, a splice at that offset lands inside another operation.
        Assert.Equal("BT",        Slice(content, tokens[0]));
        Assert.Equal("/F1 12 Tf", Slice(content, tokens[1]));
        Assert.Equal("72 700 Td", Slice(content, tokens[2]));
        Assert.Equal("(Hi) Tj",   Slice(content, tokens[3]));
        Assert.Equal("ET",        Slice(content, tokens[4]));

        // Ranges must be non-overlapping and in stream order.
        for (int i = 1; i < tokens.Count; i++)
            Assert.True(tokens[i - 1].EndOffset <= tokens[i].StartOffset,
                "token " + i + " starts before the previous one ends");
    }

    [Fact]
    public void Scan_OperandsAreAttachedToTheOperatorThatFollowsThem()
    {
        byte[] content = Ascii("BT /F1 12 Tf 72 700 Td (Hi) Tj ET");
        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Empty(tokens[0].Operands);                       // BT
        Assert.Equal(2, tokens[1].Operands.Count);              // /F1 12 Tf
        Assert.Equal("F1", ((PdfName)tokens[1].Operands[0]).Value);
        Assert.Equal(12d, ((PdfNumber)tokens[1].Operands[1]).DoubleValue);
        Assert.Equal(2, tokens[2].Operands.Count);              // 72 700 Td
        Assert.Equal(700d, ((PdfNumber)tokens[2].Operands[1]).DoubleValue);
        Assert.Equal("Hi", OperandText(tokens[3], 0));          // (Hi) Tj
        Assert.Empty(tokens[4].Operands);                       // ET
    }

    [Fact]
    public void Scan_KeywordStopsAtADigit_SoBt0MeansBtThenZero()
    {
        // Producers that omit whitespace before a number are common; the keyword reader must stop
        // at the digit, otherwise "BT0" becomes an unknown operator and the text object vanishes.
        byte[] content = Ascii("BT0 700 Td (X) Tj ET");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "BT", "Td", "Tj", "ET" }, Ops(tokens));
        Assert.Equal("BT", Slice(content, tokens[0]));
        Assert.Equal("0 700 Td", Slice(content, tokens[1]));
        Assert.Equal(1, ContentStreamScanner.ScanDetailed(content).TextObjectCount);
    }

    /// <summary>
    /// A name's <c>#XX</c> escapes decode as <b>UTF-8</b>, the same rule the document tokenizer
    /// uses - this scanner is the tree's second name reader and the two must not disagree.
    /// </summary>
    /// <remarks>
    /// <para>Both route through <c>PdfNameEscape.Decode</c>. If they diverged, a tag name would
    /// mean one thing to the parser that reads the structure tree and another to the tagger that
    /// writes into the page - and marked content is matched to structure BY that name.</para>
    ///
    /// <para>The Latin-1 half is asserted beside it, because it is the branch that keeps a legacy
    /// file reading as it always did, and a test of the UTF-8 branch alone cannot see that a
    /// branch was chosen at all.</para>
    /// </remarks>
    [Fact]
    public void Scan_ANameEscapeDecodesAsUtf8_WithLatin1ForBytesThatAreNotUtf8()
    {
        // C3 A9 is the UTF-8 for e-acute; a lone E9 is not valid UTF-8 and is Latin-1 e-acute.
        byte[] content = Ascii("/Caf#C3#A9 /Caf#E9 12 Tf");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);
        var token = Assert.Single(tokens);

        Assert.Equal(3, token.Operands.Count);
        Assert.Equal("Café", ((PdfName)token.Operands[0]).Value);
        Assert.Equal("Café", ((PdfName)token.Operands[1]).Value);

        // ...and they are the same name, which is the point: one is four bytes on disk and the
        // other three, and both denote the resource a producer meant.
        Assert.Equal(((PdfName)token.Operands[0]).Value, ((PdfName)token.Operands[1]).Value);
    }

    /// <summary>
    /// A name longer than the reader's initial byte buffer, so the growth path runs - and it is
    /// non-ASCII across the resize, where a buffer bug and a decode bug would look the same.
    /// </summary>
    [Fact]
    public void Scan_ALongNameGrowsTheBufferWithoutCorruptingIt()
    {
        // 20 x e-acute in UTF-8 = 40 bytes, past the 16-byte initial buffer twice over.
        string escaped = string.Concat(System.Linq.Enumerable.Repeat("#C3#A9", 20));
        byte[] content = Ascii("/" + escaped + " 12 Tf");

        var token = Assert.Single(ContentStreamScanner.Scan(content));

        Assert.Equal(new string('é', 20), ((PdfName)token.Operands[0]).Value);
    }

    [Fact]
    public void Scan_D0IsTheDocumentedDigitException()
    {
        byte[] content = Ascii("1000 0 d0 0 0 0 0 0 0 d1");

        Assert.Equal(new[] { "d0", "d1" }, Ops(ContentStreamScanner.Scan(content)));
    }

    // =====================================================================
    //  Literal strings — nesting, escapes, and operator-looking payloads
    // =====================================================================

    [Fact]
    public void Scan_LiteralStringWithNestedParens_DoesNotEndEarly()
    {
        byte[] content = Ascii("BT /F1 10 Tf 72 400 Td (a (b (c) d) e) Tj ET");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "BT", "Tf", "Td", "Tj", "ET" }, Ops(tokens));
        Assert.Equal("a (b (c) d) e", OperandText(tokens[3], 0));
        Assert.Equal("(a (b (c) d) e) Tj", Slice(content, tokens[3]));
    }

    [Fact]
    public void Scan_LiteralStringWithEscapedParensAndBackslash_IsBounded()
    {
        byte[] content = Ascii(@"BT /F1 10 Tf 72 400 Td (open \( close \) slash \\ done) Tj ET");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "BT", "Tf", "Td", "Tj", "ET" }, Ops(tokens));
        Assert.Equal(@"open ( close ) slash \ done", OperandText(tokens[3], 0));
    }

    [Fact]
    public void Scan_LiteralStringWithOctalAndControlEscapes_Decodes()
    {
        byte[] content = Ascii(@"(\101\102\103\n\t) Tj");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "Tj" }, Ops(tokens));
        Assert.Equal("ABC\n\t", OperandText(tokens[0], 0));
    }

    [Fact]
    public void Scan_LiteralStringLineContinuation_DropsTheEol()
    {
        byte[] content = Ascii("(one\\\ntwo) Tj");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal("onetwo", OperandText(tokens[0], 0));
    }

    /// <summary>
    /// The single most dangerous false positive: a page whose text literally reads "BT ET EMC".
    /// Treating those bytes as operators makes the tagger think the page is already marked (so it
    /// silently refuses every such page) or, worse, splice inside the string.
    /// </summary>
    [Fact]
    public void Scan_StringContainingOperatorText_ProducesNoOperatorsAndNoMarkedContentFlag()
    {
        byte[] content = Ascii("BT /F1 10 Tf 72 400 Td (BT ET EMC BDC BMC BI ID EI) Tj ET");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe);
        Assert.Equal(new[] { "BT", "Tf", "Td", "Tj", "ET" }, Ops(result.Tokens));
        Assert.False(result.HasMarkedContent);
        Assert.False(result.HasInlineImage);
        Assert.Equal(1, result.TextObjectCount);
        Assert.Equal("BT ET EMC BDC BMC BI ID EI", OperandText(result.Tokens[3], 0));
    }

    [Fact]
    public void Scan_UnterminatedLiteralString_IsReportedAsUnsafe()
    {
        byte[] content = Ascii("BT /F1 10 Tf 72 400 Td (never closed Tj ET");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.NotNull(result.Error);
        Assert.Contains("Unterminated literal string", result.Error, StringComparison.Ordinal);
        Assert.Equal(result.Error, ContentStreamScanner.LastScanError);

        // The partial token list must not be mistaken for a usable one.
        Assert.NotEmpty(result.Tokens);
        Assert.DoesNotContain("ET", Ops(result.Tokens));
    }

    [Fact]
    public void Scan_TrailingBackslashInsideString_IsUnterminatedNotSilentlyAccepted()
    {
        byte[] content = Ascii("(abc\\");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.Contains("Unterminated literal string", result.Error, StringComparison.Ordinal);
    }

    // =====================================================================
    //  Hex strings vs dictionaries
    // =====================================================================

    [Fact]
    public void Scan_HexStringOperand_IsDecodedAndBounded()
    {
        byte[] content = Ascii("BT /F1 10 Tf 72 400 Td <48656C6C6F> Tj ET");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "BT", "Tf", "Td", "Tj", "ET" }, Ops(tokens));
        Assert.Equal("Hello", OperandText(tokens[3], 0));
        Assert.Equal("<48656C6C6F> Tj", Slice(content, tokens[3]));
    }

    [Fact]
    public void Scan_HexStringWithWhitespaceAndOddDigitCount_FollowsTheSpec()
    {
        // 7.3.4.3: whitespace is ignored, and a final odd digit is padded with a trailing zero.
        byte[] content = Ascii("<48 65 6C 6C 6F 7> Tj");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Single(tokens);
        Assert.Equal("Hellop", OperandText(tokens[0], 0));   // 0x7 padded to 0x70 == 'p'
    }

    [Fact]
    public void Scan_UnterminatedHexString_IsReportedAsUnsafe()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("BT <4865"));

        Assert.False(result.IsSafe);
        Assert.Contains("Unterminated hex string", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_DoubleAngleIsADictionaryNotAHexString()
    {
        byte[] content = Ascii("/P <</MCID 4 /Alt (x)>> BDC (t) Tj EMC");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe);
        Assert.Equal(new[] { "BDC", "Tj", "EMC" }, Ops(result.Tokens));
        Assert.True(result.HasMarkedContent);

        ContentToken bdc = result.Tokens[0];
        Assert.Equal(2, bdc.Operands.Count);
        Assert.Equal("P", ((PdfName)bdc.Operands[0]).Value);
        var properties = Assert.IsType<PdfDictionary>(bdc.Operands[1]);
        Assert.Equal(4d, ((PdfNumber)properties[new PdfName("MCID")]).DoubleValue);
        Assert.Equal("/P <</MCID 4 /Alt (x)>> BDC", Slice(content, bdc));
    }

    [Fact]
    public void Scan_ArrayOperandOfTJ_IsBounded()
    {
        byte[] content = Ascii("BT [(A) -250 (B) -250 (C)] TJ ET");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "BT", "TJ", "ET" }, Ops(tokens));
        Assert.Equal("[(A) -250 (B) -250 (C)] TJ", Slice(content, tokens[1]));
        var array = Assert.IsType<PdfArray>(tokens[1].Operands[0]);
        Assert.Equal(5, array.Count);
    }

    // =====================================================================
    //  Comments
    // =====================================================================

    [Fact]
    public void Scan_CommentAtEndOfLine_IsSkippedEntirely()
    {
        byte[] content = Ascii(
            "% leading BT ET EMC (unbalanced\n" +
            "BT /F1 10 Tf 72 400 Td (X) Tj % trailing BT ET (also unbalanced\n" +
            "ET\n");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe);
        Assert.Equal(new[] { "BT", "Tf", "Td", "Tj", "ET" }, Ops(result.Tokens));
        Assert.False(result.HasMarkedContent);
        Assert.Equal(1, result.TextObjectCount);
    }

    [Fact]
    public void Scan_CommentAtEofWithoutTrailingNewline_IsSkippedAndDoesNotFail()
    {
        byte[] content = Ascii("BT /F1 10 Tf 72 400 Td (X) Tj ET\n% no newline after me BT ET EMC (unbalanced");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "BT", "Tf", "Td", "Tj", "ET" }, Ops(result.Tokens));
        Assert.False(result.HasMarkedContent);
    }

    [Fact]
    public void Scan_CommentTerminatedByCarriageReturnOnly_IsSkipped()
    {
        byte[] content = Ascii("% mac line ending BT ET\rBT (X) Tj ET");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe);
        Assert.Equal(new[] { "BT", "Tj", "ET" }, Ops(result.Tokens));
        Assert.Equal(1, result.TextObjectCount);
    }

    // =====================================================================
    //  Inline images — the construct that breaks naive scanners
    // =====================================================================

    /// <summary>
    /// The payload deliberately contains the literal bytes "BT", "ET", "EMC" and even a
    /// whitespace-delimited "EI". A declared <c>/L</c> is authoritative, so the scanner must jump
    /// the whole payload and stop at the <em>real</em> terminator — not at the decoy.
    /// </summary>
    [Fact]
    public void ScanDetailed_InlineImageWithDeclaredLength_SkipsThePayloadIncludingADecoyEi()
    {
        byte[] payload = Cat(
            Ascii("BT ET EI EMC BDC ("),
            new byte[] { 0x80, 0xFF, 0x0A, 0x25, 0x00 },
            Ascii(" ) more BT"));

        byte[] header = Ascii("q\nBI /W 4 /H 4 /BPC 8 /CS /G /L " + payload.Length + " ID ");
        byte[] tail = Ascii("\nEI\nQ\n");
        byte[] content = Cat(header, payload, tail);

        int biOffset = 2;                                    // just past "q\n"
        int eiOffset = header.Length + payload.Length + 1;   // +1 skips the '\n' before EI
        Assert.Equal("BI", Text(content, biOffset, 2));      // fixture self-check
        Assert.Equal("EI", Text(content, eiOffset, 2));      // fixture self-check

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasInlineImage);

        // Nothing from inside the payload may surface as an operation.
        Assert.Equal(new[] { "q", "BI", "Q" }, Ops(result.Tokens));
        Assert.False(result.HasMarkedContent);
        Assert.Equal(0, result.TextObjectCount);

        // The BI token must swallow the payload and end exactly one byte past "EI".
        ContentToken bi = Single(result.Tokens, "BI");
        Assert.Equal(biOffset, bi.StartOffset);
        Assert.Equal(eiOffset + 2, bi.EndOffset);
        Assert.True(bi.EndOffset > header.Length + payload.Length,
            "the whole payload must lie inside the BI token's byte range");
    }

    /// <summary>
    /// No <c>/L</c>: the scanner falls back to "whitespace-delimited EI followed by printable
    /// ASCII". A decoy "EI" that is followed by a high byte is image data, not the terminator.
    /// </summary>
    [Fact]
    public void ScanDetailed_InlineImageWithoutDeclaredLength_IgnoresAnEiFollowedByBinary()
    {
        byte[] payload = Cat(
            Ascii("BT ET EMC % ( unbalanced"),
            new byte[] { 0x0A, 0x45, 0x49, 0xB0, 0xC3, 0x89, 0xFF },   // "\nEI" + high byte = decoy
            Ascii(" tail"));

        byte[] header = Ascii("q\nBI /W 4 /H 4 /BPC 8 /CS /G ID ");
        byte[] tail = Ascii("\nEI\nQ\n");
        byte[] content = Cat(header, payload, tail);

        int decoyOffset = header.Length + "BT ET EMC % ( unbalanced".Length + 1;
        Assert.Equal("EI", Text(content, decoyOffset, 2));   // fixture self-check
        int eiOffset = header.Length + payload.Length + 1;
        Assert.Equal("EI", Text(content, eiOffset, 2));      // fixture self-check

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasInlineImage);
        Assert.Equal(new[] { "q", "BI", "Q" }, Ops(result.Tokens));
        Assert.False(result.HasMarkedContent);

        ContentToken bi = Single(result.Tokens, "BI");
        Assert.Equal(eiOffset + 2, bi.EndOffset);
        Assert.True(bi.EndOffset > decoyOffset + 2,
            "the decoy EI inside binary image data must not terminate the inline image");
    }

    [Fact]
    public void ScanDetailed_InlineImageWithNoTerminator_IsRefusedRatherThanGuessed()
    {
        byte[] content = Cat(
            Ascii("q\nBI /W 4 /H 4 /BPC 8 /CS /G ID "),
            new byte[] { 0x80, 0x81, 0x82, 0x83, 0x84, 0x85 });

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.Contains("no locatable EI terminator", result.Error, StringComparison.Ordinal);
        Assert.Equal(result.Error, ContentStreamScanner.LastScanError);
    }

    [Fact]
    public void ScanDetailed_InlineImageWithLengthPastEndOfStream_FallsBackAndStillRefuses()
    {
        // A declared length the scanner can prove wrong must not be trusted — and with no real
        // terminator to fall back on, the only safe answer is "unsafe".
        byte[] content = Cat(
            Ascii("BI /W 4 /H 4 /L 99999 ID "),
            new byte[] { 0x80, 0x81, 0x82 });

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.Contains("no locatable EI terminator", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanDetailed_InlineImageEndingBeforeItsIdKeyword_IsUnsafe()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("q BI /W 4 /H 4"));

        Assert.False(result.IsSafe);
        Assert.Contains("Inline image", result.Error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    //  Inline images glued to the operator before them
    // ---------------------------------------------------------------------

    /// <summary>
    /// Adversarial input. A producer that omits the whitespace in <c>"q BI"</c> emits the single
    /// keyword <c>"qBI"</c> — exactly the class of input the concatenated-operator splitter exists
    /// to handle. If the inline-image test compares the <em>whole</em> keyword against <c>"BI"</c>
    /// it misses the image, the scan continues into the payload, and every byte of image data is
    /// tokenised as operators. Three safety nets fail together: <c>HasInlineImage</c> stays false
    /// (so the tagger's uncertain-image refusal never fires), the cross-check against Core's parser
    /// agrees because it makes the identical mistake, and re-scanning the spliced output is
    /// self-consistent. The result is <c>BDC</c>/<c>EMC</c> written into the image samples while
    /// the dictionary's <c>/L</c> still declares the original payload length.
    ///
    /// <para>
    /// The payload here is the literal bytes <c>BT(A)TjET</c> — nine bytes of perfectly valid
    /// operator text that must never surface as operations.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("q")]    // Save
    [InlineData("Q")]    // Restore
    [InlineData("n")]    // EndPath
    [InlineData("f")]    // Fill
    public void ScanDetailed_InlineImageGluedToThePrecedingOperator_StillSkipsThePayload(string glued)
    {
        const string payload = "BT(A)TjET";                       // 9 bytes, all of it operator text
        string source = glued + "BI /W 3 /H 3 /BPC 8 /CS /G /L " + payload.Length +
                        " ID " + payload + " EI Q\n";
        byte[] content = Ascii(source);

        int dataStart = source.IndexOf(" ID ", StringComparison.Ordinal) + 4;
        Assert.Equal(payload, Text(content, dataStart, payload.Length));   // fixture self-check

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasInlineImage);
        Assert.False(result.HasUncertainInlineImage);              // /L settled it exactly

        // The glued operator is still emitted, and nothing from the payload is.
        Assert.Equal(new[] { glued, "BI", "Q" }, Ops(result.Tokens));
        Assert.Equal(0, result.TextObjectCount);
        Assert.False(result.HasMarkedContent);

        // Byte ranges: the leading operator owns one byte, the image swallows the payload.
        Assert.Equal(0, result.Tokens[0].StartOffset);
        Assert.Equal(1, result.Tokens[0].EndOffset);

        ContentToken bi = Single(result.Tokens, "BI");
        Assert.Equal(1, bi.StartOffset);
        Assert.Equal("EI", Text(content, bi.EndOffset - 2, 2));
        Assert.True(bi.EndOffset > dataStart + payload.Length,
            "the whole payload must lie inside the BI token's byte range");

        // The decisive assertion: no token may claim a byte inside the image samples. A splice at
        // such an offset is what corrupts the page.
        foreach (ContentToken token in result.Tokens)
        {
            if (string.Equals(token.Operator, "BI", StringComparison.Ordinal)) continue;
            Assert.True(token.EndOffset <= dataStart || token.StartOffset >= dataStart + payload.Length,
                token.ToString() + " overlaps the inline image payload");
        }
    }

    /// <summary>
    /// Same glue, but with no <c>/L</c> and a filtered image whose payload does not obey the filter
    /// it names, so the length can only be guessed. The image must still be detected — and the guess
    /// must be surfaced through <see cref="ContentScanResult.HasUncertainInlineImage"/>, which is the
    /// flag the tagger uses to refuse the page rather than splice at an offset it cannot prove.
    ///
    /// <para><b>The payload is load-bearing, not incidental.</b> <c>/AHx</c> is self-delimiting, so
    /// a well-formed hex payload ending in <c>&gt;</c> would now be bounded exactly and this test
    /// would assert the opposite of its own name. <c>"BT ET\n"</c> is not hex ('T' is not a hex
    /// digit) and carries no <c>&gt;</c>, so the measuring scan declines it and the heuristic runs —
    /// which is the state under test.</para>
    /// </summary>
    [Fact]
    public void ScanDetailed_GluedInlineImageWithGuessedLength_SetsTheUncertainFlag()
    {
        byte[] content = Cat(
            Ascii("qBI /W 4 /H 4 /BPC 8 /CS /G /F /AHx ID "),
            new byte[] { 0x42, 0x54, 0x20, 0x45, 0x54, 0x0A },   // "BT ET\n" as raw sample bytes
            Ascii("EI Q\n"));

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasInlineImage);
        Assert.True(result.HasUncertainInlineImage);
        Assert.Equal(new[] { "q", "BI", "Q" }, Ops(result.Tokens));
        Assert.Equal(0, result.TextObjectCount);
    }

    // ---------------------------------------------------------------------
    //  Self-delimiting filters — the payload length is read, not guessed
    // ---------------------------------------------------------------------

    /// <summary>
    /// The payoff of measuring a self-delimiting payload is binary: with the length known,
    /// <see cref="ContentScanResult.HasUncertainInlineImage"/> is false and the page becomes
    /// taggable. Before this, every filtered inline image without a declared <c>/L</c> made its
    /// whole page permanently untaggable.
    ///
    /// <para>
    /// The <c>/A85</c> fixture is the one the heuristic provably cannot survive: an ASCII85 payload
    /// is printable ASCII by construction and both <c>E</c> and <c>I</c> are in the base-85
    /// alphabet, so the <c>" EI "</c> four bytes in satisfies every condition the heuristic tests.
    /// Reading the <c>~&gt;</c> end-of-data marker (§7.4.3) settles it instead.
    /// </para>
    /// </summary>
    [Fact]
    public void ScanDetailed_Ascii85InlineImage_IsBoundedExactlyDespiteADecoyEiInsideThePayload()
    {
        byte[] payload = Ascii("8;Xa EI 9M~>");
        byte[] header  = Ascii("q\nBI /W 2 /H 2 /BPC 8 /CS /G /F /A85 ID ");
        byte[] content = Cat(header, payload, Ascii("\nEI\nQ\n"));

        int decoyOffset = header.Length + 5;                 // the "EI" inside the samples
        int realEi      = header.Length + payload.Length + 1;
        Assert.Equal("EI", Text(content, decoyOffset, 2));   // fixture self-check
        Assert.Equal("EI", Text(content, realEi, 2));        // fixture self-check

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasInlineImage);
        Assert.False(result.HasUncertainInlineImage);
        Assert.Equal(new[] { "q", "BI", "Q" }, Ops(result.Tokens));

        ContentToken bi = Single(result.Tokens, "BI");
        Assert.Equal(realEi + 2, bi.EndOffset);
        Assert.True(bi.EndOffset > decoyOffset + 2,
            "the decoy EI inside the ASCII85 samples must not terminate the inline image");
    }

    /// <summary>
    /// <c>/AHx</c>'s <c>&gt;</c> (§7.4.2), with no whitespace between it and the <c>EI</c> — a form
    /// the heuristic cannot recognise at all, since it requires whitespace before the terminator.
    /// The scan used to run to the end of the stream and take the <c>Q</c> with it.
    /// </summary>
    [Fact]
    public void ScanDetailed_AsciiHexInlineImage_IsBoundedExactlyWithNoWhitespaceBeforeEi()
    {
        byte[] header  = Ascii("q\nBI /W 2 /H 2 /BPC 8 /CS /G /F /AHx ID ");
        byte[] content = Cat(header, Ascii("00FF00FF>EI\nQ\n"));

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasInlineImage);
        Assert.False(result.HasUncertainInlineImage);
        Assert.Equal(new[] { "q", "BI", "Q" }, Ops(result.Tokens));

        ContentToken bi = Single(result.Tokens, "BI");
        Assert.Equal(header.Length + "00FF00FF>EI".Length, bi.EndOffset);
    }

    /// <summary>
    /// The scanner and Core's <c>ContentStreamParser</c> must agree about where the image ends —
    /// that agreement is what the tagger's cross-check tests, and it is worthless if one side knows
    /// a boundary the other has to guess at. Asserting it here means a fix applied to only one of
    /// the two call sites cannot pass.
    /// </summary>
    /// <remarks>
    /// Both rows are chosen so the heuristic gets them <em>wrong</em>, because a row it happens to
    /// get right cannot detect a fix applied to only one of the two call sites: <c>/A85</c> hides a
    /// decoy <c>EI</c> in its samples, and <c>/AHx</c> puts no whitespace before the real <c>EI</c>,
    /// which the heuristic requires.
    /// </remarks>
    [Theory]
    [InlineData("/A85", "8;Xa EI 9M~>", "\n")]
    [InlineData("/AHx", "00FF00FF>",    "")]
    public void ScanDetailed_SelfDelimitingImage_EndsWhereCoreParserEndsIt(
        string filter, string payload, string beforeEi)
    {
        byte[] content = Cat(
            Ascii("q\nBI /W 2 /H 2 /BPC 8 /CS /G /F " + filter + " ID "),
            Ascii(payload),
            Ascii(beforeEi + "EI\nQ\n"));

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);
        var operations = Tetitulz.Pdf.Core.Text.ContentStreamParser.Parse(content);

        Assert.False(result.HasUncertainInlineImage);
        Assert.Equal(new[] { "q", "BI", "Q" }, Ops(result.Tokens));
        Assert.Equal(new[] { "q", "BI", "Q" }, operations.Select(o => o.Operator).ToArray());

        // Same bytes on both sides: the scanner's BI token spans them, the parser materialised them.
        var image = Assert.IsType<PdfStream>(Assert.Single(operations[1].Operands));
        Assert.Equal(payload, Encoding.Latin1.GetString(image.RawData));
    }

    /// <summary>
    /// A keyword that ends in <c>BI</c> but does not decompose into known operators leaves the
    /// scanner unable to say where the image header begins. Refusing the page is the only honest
    /// answer: guessing walks into the payload and corrupts the image.
    /// </summary>
    [Fact]
    public void ScanDetailed_UndecomposableKeywordEndingInBi_IsRefusedRatherThanTokenisedThrough()
    {
        byte[] content = Ascii("zzBI /W 3 /H 3 /BPC 8 /CS /G /L 9 ID BT(A)TjET EI Q\n");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.Contains("does not decompose", result.Error, StringComparison.Ordinal);
        Assert.Equal(result.Error, ContentStreamScanner.LastScanError);
        Assert.DoesNotContain("BT", Ops(result.Tokens));
    }

    /// <summary>
    /// The mirror-image risk: over-refusing. A keyword that merely <em>contains</em> "BI" and does
    /// <b>not</b> decompose into known operators is not an inline image (an image's parameter
    /// dictionary starts with '/' or whitespace, both of which end a keyword), so it must stay one
    /// opaque token and the stream must stay safe.
    ///
    /// <para>Note the precondition — <i>does not decompose</i>. A keyword that <em>does</em>
    /// decompose with <c>BI</c> as one of its parts is covered by
    /// <see cref="ScanDetailed_DecomposableKeywordWhoseBiOpensNoReadableImage_IsRefused"/>, not
    /// here: <c>BI</c> is only ever produced by the two literal bytes of the begin-inline-image
    /// operator, so a decomposition that contains it cannot be dismissed as a coincidence.</para>
    /// </summary>
    [Theory]
    [InlineData("q xBIy Q")]
    [InlineData("q zzBIzz Q")]
    public void ScanDetailed_KeywordMerelyContainingBi_IsNotTreatedAsAnInlineImage(string source)
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii(source));

        Assert.True(result.IsSafe, result.Error);
        Assert.False(result.HasInlineImage);
        Assert.Equal(3, result.Tokens.Count);
        Assert.Equal("q", result.Tokens[0].Operator);
        Assert.Equal("Q", result.Tokens[2].Operator);
    }

    /// <summary>
    /// <c>"BIG"</c> decomposes into the known operators <c>BI</c> + <c>G</c>, so the scanner reads
    /// it as an inline image whose header it then cannot parse (there is no <c>ID</c>). It must
    /// refuse the stream rather than resume tokenising at the byte after the <c>BI</c>.
    ///
    /// <para><b>Why refusal and not "it was just an odd operator".</b> The splitter only ever
    /// produces a <c>BI</c> part from the two literal bytes <c>B</c><c>I</c>, and no other PDF
    /// operator begins with <c>I</c>, so a decomposition containing <c>BI</c> is either a real
    /// inline image or a stream the scanner has already mis-read. Both answers forbid splicing:
    /// the whole purpose of this scanner is to hand the tagger byte offsets it can safely write
    /// at, and an image whose extent is unknown has no such offsets. A page carrying an operator
    /// like <c>BIG</c> is malformed to begin with — leaving it untagged costs one page, whereas
    /// guessing costs the image.</para>
    /// </summary>
    [Fact]
    public void ScanDetailed_DecomposableKeywordWhoseBiOpensNoReadableImage_IsRefused()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("q BIG Q"));

        Assert.False(result.IsSafe);
        Assert.NotNull(result.Error);
        Assert.Equal(result.Error, ContentStreamScanner.LastScanError);
        Assert.False(result.HasInlineImage);
    }

    /// <summary>
    /// <c>"BIID"</c> — a parameter-less inline image whose <c>ID</c> is glued to its <c>BI</c>.
    /// The first <c>BI</c> in the decomposition must win, so that the bytes after it are re-read as
    /// image header rather than emitted as a stray <c>ID</c> operator.
    /// </summary>
    [Fact]
    public void ScanDetailed_IdGluedToBi_IsStillOneInlineImage()
    {
        byte[] content = Ascii("q BIID BT ET EI Q\n");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasInlineImage);
        Assert.Equal(new[] { "q", "BI", "Q" }, Ops(result.Tokens));
        Assert.Equal(0, result.TextObjectCount);
    }

    // =====================================================================
    //  Compatibility sections, marked content, XObjects
    // =====================================================================

    [Fact]
    public void ScanDetailed_BxExSection_IsFlaggedAsACompatibilitySection()
    {
        byte[] content = Ascii("q BX /MyExt 3 myop EX Q BT (t) Tj ET");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasCompatibilitySection);
        Assert.Contains("BX", Ops(result.Tokens));
        Assert.Contains("EX", Ops(result.Tokens));
    }

    [Fact]
    public void ScanDetailed_NoBxEx_DoesNotFlagACompatibilitySection()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("BT (t) Tj ET"));

        Assert.False(result.HasCompatibilitySection);
    }

    [Theory]
    [InlineData("/Span <</MCID 0>> BDC (t) Tj EMC")]
    [InlineData("/Tag BMC (t) Tj EMC")]
    public void ScanDetailed_ExistingMarkedContent_IsDetected(string source)
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii(source));

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasMarkedContent);
    }

    [Fact]
    public void ScanDetailed_MarkedContentPoints_AreDetectedSeparatelyFromSequences()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(
            Ascii("/MyTag MP /MyTag <</A 1>> DP BT (t) Tj ET"));

        Assert.True(result.IsSafe, result.Error);
        Assert.True(result.HasMarkedContentPoints);
        Assert.False(result.HasMarkedContent);
    }

    [Fact]
    public void ScanDetailed_CountsXObjectInvocationsAndTextObjects()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(
            Ascii("q /Fm0 Do Q q /Fm1 Do Q BT (a) Tj ET BT (b) Tj ET BT (c) Tj ET"));

        Assert.True(result.IsSafe, result.Error);
        Assert.Equal(2, result.XObjectInvocations);
        Assert.Equal(3, result.TextObjectCount);
    }

    // =====================================================================
    //  Whitespace-less operator runs
    // =====================================================================

    [Fact]
    public void Scan_ConcatenatedOperators_AreSplitWithExactByteRanges()
    {
        const string source = "q /Im0 DoQQ";
        byte[] content = Ascii(source);

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "q", "Do", "Q", "Q" }, Ops(tokens));
        Assert.Equal("/Im0 Do", Slice(content, tokens[1]));
        Assert.Equal("Q", Slice(content, tokens[2]));
        Assert.Equal("Q", Slice(content, tokens[3]));
        Assert.Equal(source.Length, tokens[3].EndOffset);
        Assert.Equal(1, ContentStreamScanner.ScanDetailed(content).XObjectInvocations);
    }

    /// <summary>
    /// Splitting must be all-or-nothing. If "BTx" were split into <c>BT</c> + leftover, an
    /// unrecognised extension operator would masquerade as a text-object opener and the tagger
    /// would try to wrap a text object that does not exist.
    /// </summary>
    [Fact]
    public void Scan_UnknownKeywordThatStartsWithAnOperator_StaysOneOpaqueToken()
    {
        byte[] content = Ascii("BTx (t) Tj");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.Equal(new[] { "BTx", "Tj" }, Ops(result.Tokens));
        Assert.Equal(0, result.TextObjectCount);
    }

    // =====================================================================
    //  Refusals: constructs whose bounds cannot be established
    // =====================================================================

    [Theory]
    [InlineData("BT (x) Tj ) ET", ")")]
    [InlineData("BT (x) Tj ] ET", "]")]
    [InlineData("BT (x) Tj > ET", ">")]
    public void ScanDetailed_StrayClosingDelimiter_IsUnsafe(string source, string delimiter)
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii(source));

        Assert.False(result.IsSafe);
        Assert.Contains("Stray '" + delimiter + "'", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ 1 2 add }")]
    [InlineData("BT (x) Tj } ET")]
    public void ScanDetailed_PostScriptCalculatorBrace_IsUnsafe(string source)
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii(source));

        Assert.False(result.IsSafe);
        Assert.Contains("PostScript calculator brace", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanDetailed_UnterminatedArray_IsUnsafe()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("BT [(A) -250 (B) TJ ET"));

        Assert.False(result.IsSafe);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ScanDetailed_UnterminatedDictionary_IsUnsafe()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("/P <</MCID 0 BDC"));

        Assert.False(result.IsSafe);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ScanDetailed_DictionaryKeyThatIsNotAName_IsUnsafe()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("/P << 1 2 >> BDC"));

        Assert.False(result.IsSafe);
        Assert.Contains("Dictionary key is not a name", result.Error, StringComparison.Ordinal);
    }

    // =====================================================================
    //  Adversarial input — DoS / stack-exhaustion guards
    // =====================================================================

    /// <summary>
    /// Adversarial input required by CLAUDE.md for every recursion guard. Without the depth cap
    /// this input recurses once per '[' — 100,000 frames of
    /// <c>TryReadOperand</c>/<c>TryReadArray</c> — and blows the stack, which cannot be caught.
    /// With the cap the scanner must return an ordinary "unsafe" result, promptly.
    /// </summary>
    [Fact]
    public void ScanDetailed_DeeplyNestedArrays_AreRefusedInsteadOfOverflowingTheStack()
    {
        byte[] content = Ascii(new string('[', 100_000));

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.Contains("Array nesting exceeds", result.Error, StringComparison.Ordinal);
    }

    /// <summary>Same guard on the dictionary side: <c>&lt;&lt;/K &lt;&lt;/K &lt;&lt;/K …</c>.</summary>
    [Fact]
    public void ScanDetailed_DeeplyNestedDictionaries_AreRefusedInsteadOfOverflowingTheStack()
    {
        var sb = new StringBuilder(500_000);
        for (int i = 0; i < 100_000; i++) sb.Append("<</K ");
        byte[] content = Ascii(sb.ToString());

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.Contains("Dictionary nesting exceeds", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A megabyte of unclosed '(' is bounded work (one linear pass), but it must terminate with a
    /// diagnosis rather than looping or returning a token list that claims to describe it.
    /// </summary>
    [Fact]
    public void ScanDetailed_HugeUnterminatedString_TerminatesWithADiagnosis()
    {
        byte[] content = Ascii("BT " + new string('(', 500_000));

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.Contains("Unterminated literal string", result.Error, StringComparison.Ordinal);
    }

    // =====================================================================
    //  Diagnostics slot
    // =====================================================================

    [Fact]
    public void LastScanError_IsClearedByASubsequentSuccessfulScan()
    {
        ContentStreamScanner.ScanDetailed(Ascii("BT (unterminated"));
        Assert.NotNull(ContentStreamScanner.LastScanError);

        ContentStreamScanner.ScanDetailed(Ascii("BT (fine) Tj ET"));
        Assert.Null(ContentStreamScanner.LastScanError);
    }

    [Fact]
    public void ScanDetailed_ReportsFailuresToTheAccessibilityDiagnosticsSink()
    {
        // OnEvent is process-wide and other test classes run in parallel, so the collector is
        // locked and the assertion only looks for *our* message rather than for an exact set.
        var gate = new object();
        var seen = new List<string>();
        Action<string> handler = m => { lock (gate) seen.Add(m); };

        AccessibilityDiagnostics.OnEvent += handler;
        try
        {
            ContentStreamScanner.ScanDetailed(Ascii("BT (unterminated"));
        }
        finally
        {
            AccessibilityDiagnostics.OnEvent -= handler;
        }

        lock (gate)
        {
            Assert.Contains(seen, m =>
                m.IndexOf("ContentStreamScanner", StringComparison.Ordinal) >= 0 &&
                m.IndexOf("Unterminated literal string", StringComparison.Ordinal) >= 0);
        }
    }

    /// <summary>
    /// <see cref="AccessibilityDiagnostics.LastError"/> is <c>[ThreadStatic]</c>, so a clean scan
    /// must leave this thread's slot empty — "nothing to report" has to be distinguishable from
    /// "something failed and was tolerated".
    /// </summary>
    [Fact]
    public void ScanDetailed_CleanStream_LeavesTheDiagnosticsSlotEmpty()
    {
        AccessibilityDiagnostics.Clear();

        ContentScanResult result = ContentStreamScanner.ScanDetailed(
            Ascii("BT /F1 10 Tf 72 400 Td (X) Tj ET"));

        Assert.True(result.IsSafe);
        Assert.Null(AccessibilityDiagnostics.LastError);
    }

    [Fact]
    public void ScanDetailed_UnsafeStream_FillsTheThreadLocalDiagnosticsSlot()
    {
        AccessibilityDiagnostics.Clear();

        ContentStreamScanner.ScanDetailed(Ascii("BT (unterminated"));

        Assert.NotNull(AccessibilityDiagnostics.LastError);
        Assert.Contains("ContentStreamScanner", AccessibilityDiagnostics.LastError, StringComparison.Ordinal);
    }

    // =====================================================================
    //  Text-showing operator coverage
    // =====================================================================

    [Fact]
    public void Scan_QuoteAndDoubleQuoteShowOperators_AreRecognised()
    {
        // ' and " are operators Core's own operator set omits; the scanner must still bound them.
        byte[] content = Ascii("BT /F1 10 Tf 12 TL 72 400 Td (A) Tj (B) ' 1 2 (C) \" ET");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.True(result.IsSafe, result.Error);
        Assert.Equal(new[] { "BT", "Tf", "TL", "Td", "Tj", "'", "\"", "ET" }, Ops(result.Tokens));
        Assert.Equal("(B) '", Slice(content, result.Tokens[5]));
        Assert.Equal("1 2 (C) \"", Slice(content, result.Tokens[6]));
    }

    [Fact]
    public void Scan_BooleanAndNullOperands_AreReadAsOperandsNotOperators()
    {
        byte[] content = Ascii("true false null /GS0 gs");

        IReadOnlyList<ContentToken> tokens = ContentStreamScanner.Scan(content);

        Assert.Equal(new[] { "gs" }, Ops(tokens));
        Assert.Equal(4, tokens[0].Operands.Count);
        Assert.Same(PdfBoolean.True, tokens[0].Operands[0]);
        Assert.Same(PdfBoolean.False, tokens[0].Operands[1]);
        Assert.Same(PdfNull.Instance, tokens[0].Operands[2]);
    }

    // =====================================================================
    //  Determinism
    // =====================================================================

    [Fact]
    public void ScanDetailed_IsDeterministic_ForTwoEqualButDistinctBuffers()
    {
        const string source = "BT /F1 10 Tf 72 400 Td (a (b) c) Tj ET\n% comment\nq /Im0 DoQQ";
        ContentScanResult first = ContentStreamScanner.ScanDetailed(Ascii(source));
        ContentScanResult second = ContentStreamScanner.ScanDetailed(Ascii(source));

        Assert.Equal(Ops(first.Tokens), Ops(second.Tokens));
        for (int i = 0; i < first.Tokens.Count; i++)
        {
            Assert.Equal(first.Tokens[i].StartOffset, second.Tokens[i].StartOffset);
            Assert.Equal(first.Tokens[i].EndOffset, second.Tokens[i].EndOffset);
        }
    }
}
