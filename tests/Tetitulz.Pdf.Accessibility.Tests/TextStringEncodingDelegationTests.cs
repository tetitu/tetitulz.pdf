// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>PdfStructureTreeEditor.MakeTextString</c> and <c>PdfAutoTagger.MakeTextString</c> — this
/// assembly's ISO 32000-1 §7.9.2.2 text-string encoders — are one-line delegations to
/// <c>Core.Objects.PdfString(string)</c>, which is its own inverse: Latin-1 for every character
/// ≤ U+00FF would disagree with <c>GetText</c>'s PDFDocEncoding at 0x18-0x1F and 0x80-0xA0.
///
/// <para><b>Everything here writes through the real incremental writer and reads back through the
/// real parser.</b> An in-memory assertion on a <c>PdfString</c> the test constructed itself cannot
/// see the one thing the encoder decides — the <i>serialised form</i>: the constructor emits its
/// UTF-16BE branch as a <b>literal</b> string, not a <b>hex</b> one. <c>RawBytes</c> is identical
/// either way, so only a write-and-reparse can tell them apart, and <c>PdfString.IsHex</c> survives
/// that trip (the parser sets it from the token type), which is what makes the form assertable.</para>
/// </summary>
public class TextStringEncodingDelegationTests
{
    /// <summary>
    /// U+00A0 NO-BREAK SPACE. The single character that proves the two halves of the encoder agree:
    /// Latin-1 writes it as the byte 0xA0, and PDFDocEncoding reads the byte 0xA0 as U+20AC EURO
    /// SIGN. Written as a one-byte string it comes back a *different character*.
    /// </summary>
    private const string NoBreakSpaceTitle = "Price\u00A0List";

    /// <summary>Hebrew — nothing in it is representable in any single-byte PDF encoding.</summary>
    private const string HebrewTitle = "\u05D3\u05D5\u05D7 \u05E9\u05E0\u05EA\u05D9";

    // ================================================================
    //  PdfStructureTreeEditor.MakeTextString, via SetDocumentTitle
    // ================================================================

    /// <summary>
    /// Plain ASCII must stay one byte per character. This is the assertion that stops the vacuous
    /// fix: "always emit UTF-16BE" satisfies every round-trip assertion below while doubling the
    /// size of every title in every document the library writes.
    /// </summary>
    [Fact]
    public void AsciiTitle_IsWrittenAsOneByteperCharacter()
    {
        PdfString title = TitleAfterSetDocumentTitle("Quarterly Report");

        Assert.Equal("Quarterly Report", title.GetText());
        Assert.Equal("Quarterly Report".Length, title.RawBytes.Length);
        Assert.False(title.IsHex);
    }

    /// <summary>
    /// The 0x80-0xA0 band, which is the whole reason the local copies existed. Before the Core fix
    /// this text was written as the byte 0xA0 and read back as "Price€List".
    /// </summary>
    [Fact]
    public void NoBreakSpaceTitle_ComesBackAsANoBreakSpaceAndNotAsAEuroSign()
    {
        PdfString title = TitleAfterSetDocumentTitle(NoBreakSpaceTitle);

        Assert.Equal(NoBreakSpaceTitle, title.GetText());
        // Named explicitly, because equality alone does not say which wrong answer was avoided.
        Assert.DoesNotContain((char)0x20AC, title.GetText()); // EURO SIGN
        AssertUtf16BigEndian(title, NoBreakSpaceTitle);
    }

    [Fact]
    public void HebrewTitle_RoundTripsThroughTheWriterAsUtf16BigEndian()
    {
        PdfString title = TitleAfterSetDocumentTitle(HebrewTitle);

        Assert.Equal(HebrewTitle, title.GetText());
        AssertUtf16BigEndian(title, HebrewTitle);
    }

    /// <summary>
    /// The Core constructor throws <c>ArgumentNullException</c> on null, so the delegation keeps a
    /// <c>?? string.Empty</c>. Several call sites document depending on empty being *written*
    /// rather than refused — <c>SetFieldTooltip</c> refuses a blank tooltip precisely because
    /// <c>MakeTextString</c> would otherwise hand it <c>/TU ()</c> — so an empty title must survive
    /// the trip as an empty string rather than throwing or vanishing.
    /// </summary>
    [Fact]
    public void EmptyTitle_IsWrittenAsAnEmptyStringRatherThanThrowingOrBeingDropped()
    {
        PdfString title = TitleAfterSetDocumentTitle(string.Empty);

        Assert.Equal(string.Empty, title.GetText());
        Assert.Empty(title.RawBytes);
    }

    // ================================================================
    //  The pinned behavioural change: hex -> literal
    // ================================================================

    /// <summary>
    /// <b>The one thing this consolidation changes on the wire.</b> The old
    /// <c>MakeTextString</c> ended its UTF-16BE branch with <c>new PdfString(bytes, isHex: true)</c>,
    /// so a Hebrew title serialised as <c>/Title &lt;FEFF05D3…&gt;</c>. The Core constructor sets
    /// <c>IsHex = false</c>, so it now serialises as <c>/Title (&lt;FE&gt;&lt;FF&gt;…)</c> —
    /// <c>PdfWriter</c> escapes <c>(</c>, <c>)</c>, <c>\</c>, CR and LF byte-wise, which is what
    /// makes an arbitrary UTF-16BE payload safe inside a literal, and the literal is the shorter of
    /// the two forms.
    ///
    /// <para><b>Both halves of the assertion are load-bearing.</b> "Carries the FE FF marker" and
    /// "was serialised as a literal" was an impossible combination before this change — the old
    /// code produced BOM+hex and the Core constructor produced no-BOM+literal — so asserting either
    /// one alone would pass against the old code too.</para>
    /// </summary>
    [Fact]
    public void AUtf16TitleIsSerialisedAsALiteralStringNotAHexString()
    {
        PdfString title = TitleAfterSetDocumentTitle(HebrewTitle);

        Assert.Equal((byte)0xFE, title.RawBytes[0]);
        Assert.Equal((byte)0xFF, title.RawBytes[1]);
        Assert.False(title.IsHex);
    }

    /// <summary>
    /// The counterpart at the byte level, so a regression says <i>what</i> the writer emitted
    /// rather than only that a flag differed. The appended revision must contain the literal
    /// opening delimiter for <c>/Title</c> and no hex one.
    /// </summary>
    [Fact]
    public void TheProducedFileOpensTheTitleWithALiteralDelimiterNotAHexOne()
    {
        byte[] pdf = SetDocumentTitle(HebrewTitle);

        int at = LastIndexOfTitleKey(pdf);
        Assert.True(at >= 0, "the produced file carries no /Title key at all");

        int i = at + "/Title".Length;
        while (i < pdf.Length && (pdf[i] == ' ' || pdf[i] == '\n' || pdf[i] == '\r')) i++;

        Assert.Equal((byte)'(', pdf[i]);
    }

    // ================================================================
    //  PdfAutoTagger.MakeTextString — the second copy, via AutoTag
    // ================================================================

    /// <summary>
    /// <c>PdfAutoTagger</c> carried its own copy of the encoder, so the delegation has to be
    /// exercised through the tagger as well: the two copies are separate code and a test of one
    /// says nothing about the other.
    /// </summary>
    [Fact]
    public void AutoTaggerHebrewTitle_RoundTripsAsAUtf16BigEndianLiteral()
    {
        using var input  = new MemoryStream(ContentBearingPdfTests.ContentPdf());
        using var output = new MemoryStream();
        PdfAccessibility.AutoTag(input, output, new AutoTagOptions { Title = HebrewTitle });

        PdfString title = InfoTitle(output.ToArray());

        Assert.Equal(HebrewTitle, title.GetText());
        AssertUtf16BigEndian(title, HebrewTitle);
        Assert.False(title.IsHex);
    }

    /// <summary>
    /// The tagger's copy declined to use a single byte for anything outside printable ASCII, so
    /// <c>é</c> went out as a UTF-16BE pair. The Core constructor uses one byte for it, because
    /// PDFDocEncoding and Latin-1 agree at 0xE9. The <i>text</i> is unchanged either way; this pins
    /// that the delegation really happened rather than the old blanket rule surviving.
    /// </summary>
    [Fact]
    public void AutoTaggerLatin1Title_UsesOneByteperCharacterInsteadOfUtf16()
    {
        const string Title = "R\u00E9sum\u00E9";

        using var input  = new MemoryStream(ContentBearingPdfTests.ContentPdf());
        using var output = new MemoryStream();
        PdfAccessibility.AutoTag(input, output, new AutoTagOptions { Title = Title });

        PdfString title = InfoTitle(output.ToArray());

        Assert.Equal(Title, title.GetText());
        Assert.Equal(Title.Length, title.RawBytes.Length);
        Assert.NotEqual((byte)0xFE, title.RawBytes[0]);
    }

    // ================================================================
    //  Harness
    // ================================================================

    private static void AssertUtf16BigEndian(PdfString written, string expected)
    {
        Assert.Equal((byte)0xFE, written.RawBytes[0]);
        Assert.Equal((byte)0xFF, written.RawBytes[1]);
        // BOM + two bytes per UTF-16 code unit: no narrowing happened on the way out.
        Assert.Equal(2 + expected.Length * 2, written.RawBytes.Length);
    }

    private static byte[] SetDocumentTitle(string title)
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentTitle(input, output, title),
                    "SetDocumentTitle reported that it wrote nothing");
        return output.ToArray();
    }

    private static PdfString TitleAfterSetDocumentTitle(string title)
        => InfoTitle(SetDocumentTitle(title));

    private static PdfString InfoTitle(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        Assert.NotNull(info);
        var title = doc.ResolveObject(info[PdfName.Title]) as PdfString;
        Assert.NotNull(title);
        return title;
    }

    /// <summary>
    /// The <b>last</b> occurrence, because an incremental update carries the original bytes along
    /// and the revision this test is about is appended after them.
    /// </summary>
    private static int LastIndexOfTitleKey(byte[] pdf)
    {
        byte[] key = "/Title".Select(c => (byte)c).ToArray();
        for (int i = pdf.Length - key.Length; i >= 0; i--)
        {
            bool hit = true;
            for (int j = 0; j < key.Length; j++)
                if (pdf[i + j] != key[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }
}
