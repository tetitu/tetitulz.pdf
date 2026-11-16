// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A78 — PDF/A-4: no <c>/ActualText</c> holds a Private Use code point (ISO 19005-4 6.2.10.8), in a
/// structure element or a marked-content property list; A79 — PDF/A-2 onwards: the XMP metadata stream
/// is UTF-8 (19005-2/3 6.6.2.1, 19005-4 6.7.2.1). The published profile's rules of those numbers.
/// </summary>
public class PdfAActualTextAndXmpEncodingTests
{
    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A78

    private static List<PdfAViolation> InElement(string actualText, int part)
        => PdfAXObjectRuleTests.Coded(PdfAXObjectRuleTests.Document(" /StructTreeRoot 5 0 R", "", "",
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /Type /StructElem /S /Span /P 5 0 R /ActualText " + actualText + " >>"), part, "A78");

    [Theory]
    [InlineData("<FEFFE29C004100420043>", 4, "the /Span element (object 6) holds U+E29C")]
    [InlineData("<FEFFF8FF>", 4, "holds U+F8FF")]
    [InlineData("<FEFFDB80DC00>", 4, "holds U+F0000")]                  // plane 15, through surrogates
    [InlineData("<FEFFDBFFDFFD>", 4, "holds U+10FFFD")]                 // plane 16's last
    [InlineData("<FEFF00410042>", 4, null)]
    [InlineData("<FEFFF900>", 4, null)]                                 // just past the BMP's area
    [InlineData("(ABC)", 4, null)]
    [InlineData("<FEFFE29C>", 2, null)]                                 // PDF/A-4's rule alone
    public void A78_AStructureElementsActualText_HoldsNoPrivateUse(string actualText, int part, string? expected)
        => AssertFinding(InElement(actualText, part), expected);

    [Theory]
    [InlineData("/Span << /ActualText <FEFFE000> >> BDC EMC", "", "page 1: a marked-content sequence holds U+E000")]
    [InlineData("/Span /P0 BDC EMC", " /Resources << /Properties << /P0 5 0 R >> >>", "page 1: a marked-content sequence holds U+F8FF")]
    [InlineData("/Span << /ActualText (x) >> BDC EMC", "", null)]
    public void A78_AMarkedContentSequencesActualText_HoldsNoPrivateUse(string content, string pageExtra, string? expected)
        => AssertFinding(PdfAXObjectRuleTests.Coded(PdfAXObjectRuleTests.Document("", pageExtra, content,
            "<< /ActualText <FEFFF8FF> >>"), 4, "A78"), expected);

    // -------------------------------------------------------------- A79

    private static List<PdfAViolation> WithMetadata(byte[] packet, int part)
    {
        string hex = Convert.ToHexString(packet) + ">";
        return PdfAXObjectRuleTests.Coded(PdfAXObjectRuleTests.Document(" /Metadata 5 0 R", "", "",
            "<< /Type /Metadata /Subtype /XML /Filter /ASCIIHexDecode /Length " + hex.Length + " >>\nstream\n" + hex + "\nendstream"), part, "A79");
    }

    private const string Xmp = "<?xpacket begin='' id='W5M0MpCehiHzreSzNTczkc9d'?><x:xmpmeta xmlns:x='adobe:ns:meta/'></x:xmpmeta><?xpacket end='w'?>";

    [Fact]
    public void A79_AUtf16Packet_IsNotUtf8()
    {
        byte[] utf16 = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Xmp)).ToArray();
        AssertFinding(WithMetadata(utf16, 4), "a UTF-16 byte-order mark");
        AssertFinding(WithMetadata(utf16, 2), "a UTF-16 byte-order mark");
        Assert.Empty(WithMetadata(utf16, 1));                            // PDF/A-1 has no such rule
        byte[] utf16le = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Xmp)).ToArray();
        AssertFinding(WithMetadata(utf16le, 3), "a UTF-16 byte-order mark");
    }

    [Fact]
    public void A79_AUtf32Packet_IsNotUtf8()
    {
        byte[] utf32be = new byte[] { 0, 0, 0xFE, 0xFF }.Concat(new UTF32Encoding(true, false).GetBytes(Xmp)).ToArray();
        AssertFinding(WithMetadata(utf32be, 2), "a UTF-32 byte-order mark");
        byte[] utf32le = Encoding.UTF32.GetPreamble().Concat(Encoding.UTF32.GetBytes(Xmp)).ToArray();
        AssertFinding(WithMetadata(utf32le, 2), "a UTF-32 byte-order mark");
    }

    [Fact]
    public void A79_BytesThatAreNotUtf8_AreReported_AndUtf8Passes()
    {
        byte[] latin1 = Encoding.Latin1.GetBytes(Xmp.Replace("<x:xmpmeta", "<!-- café --><x:xmpmeta", StringComparison.Ordinal));
        AssertFinding(WithMetadata(latin1, 2), "bytes that are not UTF-8");
        byte[] utf8 = Encoding.UTF8.GetBytes(Xmp.Replace("<x:xmpmeta", "<!-- café --><x:xmpmeta", StringComparison.Ordinal));
        Assert.Empty(WithMetadata(utf8, 2));
        byte[] utf8Bom = Encoding.UTF8.GetPreamble().Concat(utf8).ToArray();
        Assert.Empty(WithMetadata(utf8Bom, 4));
    }
}
