// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// <see cref="PdfString"/>'s text constructor and §7.9.2.2 encoding.
/// <para>
/// The constructor must agree with <see cref="PdfString.GetText"/>, which reads UTF-16BE: encoding
/// everything through Latin-1 — whose encoder fallback substitutes <c>?</c> — would destroy every
/// character outside Latin-1 at the point of writing, silently, so a Hebrew or CJK title, alt text,
/// tooltip or annotation <c>/Contents</c> would become question marks and nothing would report it.
/// </para>
/// </summary>
public class PdfStringUnicodeTests
{
    [Theory]
    [InlineData("שלום עולם")]              // Hebrew
    [InlineData("مرحبا بالعالم")]           // Arabic
    [InlineData("Привет, мир")]             // Cyrillic
    [InlineData("こんにちは世界")]            // CJK
    [InlineData("Ελληνικά")]                // Greek
    [InlineData("Grüße € — ok")]  // Latin-1 mixed with Euro and em-dash
    public void TextConstructor_RoundTripsNonLatin1(string text)
    {
        var s = new PdfString(text);
        Assert.Equal(text, s.GetText());
    }

    /// <summary>
    /// The specific corruption: not merely "different", but replaced by <c>?</c>. Asserted
    /// directly so the test states the defect rather than only its absence.
    /// </summary>
    [Fact]
    public void TextConstructor_HebrewIsNotReplacedWithQuestionMarks()
    {
        var s = new PdfString("שלום");

        Assert.DoesNotContain('?', s.GetText());
        Assert.NotEqual("????", s.GetText());
    }

    /// <summary>
    /// §7.9.2.2: a UTF-16BE text string is introduced by <c>FE FF</c>. Without the marker a
    /// reader cannot tell it from a PDFDocEncoded string, so the round trip above would only work
    /// by accident inside this library.
    /// </summary>
    [Fact]
    public void TextConstructor_NonLatin1_EmitsTheUtf16BigEndianBom()
    {
        var s = new PdfString("שלום");

        Assert.True(s.RawBytes.Length >= 2);
        Assert.Equal(0xFE, s.RawBytes[0]);
        Assert.Equal(0xFF, s.RawBytes[1]);
    }

    /// <summary>
    /// Existing output must not move. A string Latin-1 can represent still produces exactly the
    /// bytes it did before — one per character, no BOM — so this fix cannot change a file that
    /// was already correct.
    /// </summary>
    [Theory]
    [InlineData("Hello, world")]
    [InlineData("Grüße aus München")]
    [InlineData("")]
    public void TextConstructor_Latin1Representable_IsUnchangedOneBytePerChar(string text)
    {
        var s = new PdfString(text);

        Assert.Equal(text.Length, s.RawBytes.Length);
        for (int i = 0; i < text.Length; i++)
            Assert.Equal((byte)text[i], s.RawBytes[i]);
    }

    /// <summary>
    /// Characters outside the BMP are surrogate pairs in a .NET string; UTF-16BE carries them
    /// unchanged, so they must survive too.
    /// </summary>
    [Fact]
    public void TextConstructor_SurrogatePair_RoundTrips()
    {
        const string emoji = "a\U0001F600b";   // U+1F600 GRINNING FACE
        var s = new PdfString(emoji);
        Assert.Equal(emoji, s.GetText());
    }

    // ------------------------------------------------------------------
    //  Through the writer and back — where escaping actually matters
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>The test the in-memory ones above do not replace.</b> UTF-16BE encodes an ASCII
    /// character as two bytes, the low one being its ASCII value — so <c>"("</c> inside an
    /// otherwise-Hebrew string puts a raw <c>0x28</c> into the bytes, and <c>")"</c> a
    /// <c>0x29</c>. A literal-string writer that did not escape those would terminate the string
    /// early and corrupt the file. `PdfWriter` escapes byte by byte, which is why this works —
    /// but nothing asserted it, and the encoding change is what made those bytes possible.
    /// </summary>
    [Theory]
    [InlineData("שלום (עולם)")]                  // Hebrew with parentheses → raw 0x28 / 0x29
    [InlineData("חשבונית \\ 2026")]              // backslash → raw 0x5C
    [InlineData("שורה\nשנייה")]                   // newline → raw 0x0A
    [InlineData("日本語 (テスト)")]
    public void WrittenAndReparsed_SurvivesLiteralStringEscaping(string text)
    {
        byte[] serialized = WriteObject(new PdfString(text));

        using var ms = new MemoryStream(serialized);
        var parser = new PdfParser(ms);
        var parsed = Assert.IsType<PdfString>(parser.ParseDirectObject());

        Assert.Equal(text, parsed.GetText());
    }

    /// <summary>
    /// The control: the escaping really is exercised — the serialized form contains an escape
    /// sequence. Without this the theory above could pass on a writer that emitted hex strings
    /// and never touched the literal-string path.
    /// </summary>
    [Fact]
    public void WrittenHebrewWithParen_IsEscapedInTheSerializedForm()
    {
        byte[] serialized = WriteObject(new PdfString("שלום (עולם)"));
        string asLatin1 = System.Text.Encoding.GetEncoding("iso-8859-1").GetString(serialized);

        Assert.Contains("\\(", asLatin1);
        Assert.Contains("\\)", asLatin1);
    }

    private static byte[] WriteObject(PdfObject obj)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteObject(obj);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    //  CreateUtf8 — the PDF 2.0 opt-in encoding (§7.9.2.2, ISO 32000-2)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("Hello, world")]
    [InlineData("שלום עולם")]
    [InlineData("こんにちは世界")]
    [InlineData("a\U0001F600b")]
    public void CreateUtf8_RoundTrips(string text)
    {
        var s = PdfString.CreateUtf8(text);
        Assert.Equal(text, s.GetText());
    }

    [Fact]
    public void CreateUtf8_EmitsTheUtf8Bom()
    {
        var s = PdfString.CreateUtf8("Hello");

        Assert.True(s.RawBytes.Length >= 3);
        Assert.Equal(0xEF, s.RawBytes[0]);
        Assert.Equal(0xBB, s.RawBytes[1]);
        Assert.Equal(0xBF, s.RawBytes[2]);
    }

    /// <summary>
    /// The default text constructor is unaffected by <see cref="PdfString.CreateUtf8"/> existing
    /// at all — it still writes PDFDocEncoding/UTF-16BE, never a UTF-8 BOM, for a caller that has
    /// not opted in.
    /// </summary>
    [Fact]
    public void DefaultConstructor_NeverEmitsUtf8Bom()
    {
        var s = new PdfString("Hello, world");
        Assert.NotEqual(0xEF, s.RawBytes.Length > 0 ? s.RawBytes[0] : (byte)0);

        var nonLatin1 = new PdfString("שלום");
        Assert.Equal(0xFE, nonLatin1.RawBytes[0]); // UTF-16BE BOM, not UTF-8
    }

    [Fact]
    public void CreateUtf8_WrittenAndReparsed_RoundTrips()
    {
        byte[] serialized = WriteObject(PdfString.CreateUtf8("שלום (עולם) 世界"));

        using var ms = new MemoryStream(serialized);
        var parser = new PdfParser(ms);
        var parsed = Assert.IsType<PdfString>(parser.ParseDirectObject());

        Assert.Equal("שלום (עולם) 世界", parsed.GetText());
    }
}
