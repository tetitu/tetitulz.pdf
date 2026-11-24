// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Metadata;

/// <summary>
/// An XMP packet decodes to the same text whichever of the three encodings ISO 16684-1 permits it
/// was written in.
/// </summary>
/// <remarks>
/// <para>UTF-8, UTF-16LE and UTF-16BE are all legal (§7.3.2) — signalling which is the whole reason
/// the <c>xpacket</c> header carries a byte-order mark.</para>
///
/// <para><b>Two of the four decoders in the tree assumed UTF-8</b>: <c>PdfXValidator</c> and
/// <c>PdfUaChecker</c> both called <c>Encoding.UTF8.GetString(bytes)</c> outright. The decisive
/// detail is that <b>this does not throw</b> on UTF-16 input — it substitutes — so their
/// <c>catch</c> blocks never fired, and the interleaved NUL bytes simply made the search for
/// <c>GTS_PDFXVersion</c> or <c>dc:title</c> fail. A <i>conforming</i> file was then reported as
/// non-conforming: a false accusation rather than a missed check, which is the worst thing a
/// validator can do when it is wrong.</para>
///
/// <para><b>The equality across encodings is the assertion</b>, not the UTF-8 case alone — a
/// decoder that returned mojibake for UTF-16 would pass any test that only checked UTF-8.</para>
/// </remarks>
public class XmpDecodeTests
{
    private const string Packet =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta><rdf:RDF><rdf:Description>" +
        "<pdfaid:part>2</pdfaid:part><dc:title>GTS_PDFXVersion PDF/X-1a:2001</dc:title>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    /// <summary>All three legal encodings decode to identical text.</summary>
    [Fact]
    public void EveryLegalEncodingDecodesToTheSameText()
    {
        string fromUtf8 = XmpPacket.DecodeText(Encoding.UTF8.GetBytes(Packet));
        string fromUtf8Bom = XmpPacket.DecodeText(WithBom(Encoding.UTF8, Packet));
        string fromUtf16Le = XmpPacket.DecodeText(WithBom(Encoding.Unicode, Packet));
        string fromUtf16Be = XmpPacket.DecodeText(WithBom(Encoding.BigEndianUnicode, Packet));

        Assert.Equal(Packet, fromUtf8);
        Assert.Equal(Packet, fromUtf8Bom);
        Assert.Equal(Packet, fromUtf16Le);
        Assert.Equal(Packet, fromUtf16Be);
    }

    /// <summary>
    /// The string a validator searches for is findable in every encoding. This is the assertion the
    /// two broken decoders would have failed: the bytes were all present, and
    /// <c>Contains</c> still said no because of the interleaved NULs.
    /// </summary>
    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public void AVersionStringIsFindableWhicheverEncodingWasUsed(string encoding)
    {
        string text = XmpPacket.DecodeText(Encode(encoding, Packet));

        Assert.Contains("PDF/X-1a:", text, System.StringComparison.Ordinal);
        Assert.Contains("pdfaid:part", text, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The control that pins <i>why</i> a decoder that reads every packet as UTF-8 fails: reading
    /// UTF-16 bytes as UTF-8 does not throw, it yields NUL-interleaved
    /// text in which the search fails.
    /// </summary>
    [Fact]
    public void ReadingUtf16AsUtf8DoesNotThrowButLosesTheSearch()
    {
        byte[] utf16 = WithBom(Encoding.Unicode, Packet);

        string naive = Encoding.UTF8.GetString(utf16);   // the old behaviour, verbatim

        Assert.DoesNotContain("PDF/X-1a:", naive, System.StringComparison.Ordinal);
        Assert.Contains("PDF/X-1a:", XmpPacket.DecodeText(utf16), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Trailing NUL padding — which some producers emit — is trimmed, because it would otherwise
    /// make the packet fail to parse and cost the caller every property in it.
    /// </summary>
    [Fact]
    public void TrailingNulPaddingIsTrimmed()
    {
        var padded = new List<byte>(Encoding.UTF8.GetBytes(Packet));
        padded.AddRange(new byte[] { 0, 0, 0, 0 });

        Assert.Equal(Packet, XmpPacket.DecodeText(padded.ToArray()));
    }

    /// <summary>Nothing in, empty out — never null, so a caller can search it unguarded.</summary>
    [Fact]
    public void NoBytesDecodeToAnEmptyString()
    {
        Assert.Equal(string.Empty, XmpPacket.DecodeText(null));
        Assert.Equal(string.Empty, XmpPacket.DecodeText(new byte[0]));
    }

    private static byte[] Encode(string which, string text) => which switch
    {
        "utf8" => Encoding.UTF8.GetBytes(text),
        "utf8-bom" => WithBom(Encoding.UTF8, text),
        "utf16le" => WithBom(Encoding.Unicode, text),
        _ => WithBom(Encoding.BigEndianUnicode, text),
    };

    private static byte[] WithBom(Encoding encoding, string text)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(text);
        var all = new byte[preamble.Length + body.Length];
        preamble.CopyTo(all, 0);
        body.CopyTo(all, preamble.Length);
        return all;
    }
}
