// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Metadata;

/// <summary>
/// The <c>xpacket</c> header's <c>begin</c> attribute carries a real byte-order mark.
/// </summary>
/// <remarks>
/// <para>That is the attribute's entire purpose (ISO 16684-1 §7.3.2): a scanner locating an XMP
/// packet inside an arbitrary file reads those bytes to decide the encoding and byte order before
/// it can parse anything.</para>
///
/// <para><b>Four places wrote this header and one of them got it wrong.</b>
/// <c>PdfTaggedDocumentBuilder</c> built it with a <b>verbatim</b> interpolated string
/// (<c>$@"…"</c>) containing <c>\xEF\xBB\xBF</c> — in which a backslash is just a backslash — so the
/// emitted attribute was the twelve literal characters <c>\xEF\xBB\xBF</c> instead of a BOM.</para>
///
/// <para><b>Nothing in this library could notice.</b> The packet is still well-formed XML, because
/// the wrong value is only an attribute's text; what breaks is an XMP toolkit or a PDF/A / PDF/UA
/// validator that reads the header to determine the encoding. And it was the <i>tagged</i>-document
/// builder — the output most likely of all to be handed to such a validator.</para>
///
/// <para>These assert on the <b>encoded bytes</b>, since "is this a BOM" is a question about bytes
/// and a string comparison of the source literal is the thing that went wrong in the first place.</para>
/// </remarks>
public class XmpPacketHeaderTests
{
    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// The header's bytes contain the UTF-8 BOM, and contain no backslash — the exact shape of the
    /// defect, which a length or substring check on the whole header would miss.
    /// </summary>
    [Fact]
    public void TheHeaderCarriesARealByteOrderMark()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(XmpPacket.Header);

        Assert.Contains(Utf8Bom, Split(bytes));
        Assert.DoesNotContain((byte)'\\', bytes);
    }

    /// <summary>
    /// The BOM sits inside the <c>begin</c> attribute rather than merely somewhere in the header,
    /// so a stray U+FEFF elsewhere could not satisfy the test above on its own.
    /// </summary>
    [Fact]
    public void TheByteOrderMarkIsTheBeginAttributesValue()
    {
        Assert.StartsWith("<?xpacket begin=\"﻿\" id=\"", XmpPacket.Header, System.StringComparison.Ordinal);
        Assert.EndsWith("\"?>", XmpPacket.Header, System.StringComparison.Ordinal);
        Assert.Contains(XmpPacket.PacketId, XmpPacket.Header, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The packet is marked writable, which is what lets a later in-place edit grow the XMP into
    /// its padding rather than moving the rest of the file.
    /// </summary>
    [Fact]
    public void ThePacketIsMarkedWritable()
        => Assert.Equal("<?xpacket end=\"w\"?>", XmpPacket.Trailer);

    /// <summary>
    /// <see cref="XmpPacket.Wrap"/> puts the body between the two instructions and pads before the
    /// trailer, never after it — padding after the end instruction is outside the packet.
    /// </summary>
    [Fact]
    public void WrapPutsPaddingInsideThePacket()
    {
        string packet = XmpPacket.Wrap("<x:xmpmeta/>", paddingBytes: 100);

        Assert.StartsWith(XmpPacket.Header, packet, System.StringComparison.Ordinal);
        Assert.EndsWith(XmpPacket.Trailer, packet, System.StringComparison.Ordinal);

        int body = packet.IndexOf("<x:xmpmeta/>", System.StringComparison.Ordinal);
        int trailer = packet.IndexOf(XmpPacket.Trailer, System.StringComparison.Ordinal);
        Assert.True(body > 0 && body < trailer, "the body must sit between the two instructions");
        Assert.True(trailer - body > 100, "the padding must precede the end instruction");
    }

    /// <summary>Every three-byte window of <paramref name="bytes"/>, for the BOM search above.</summary>
    private static byte[][] Split(byte[] bytes)
    {
        var windows = new byte[bytes.Length >= 3 ? bytes.Length - 2 : 0][];
        for (int i = 0; i + 2 < bytes.Length; i++)
            windows[i] = new[] { bytes[i], bytes[i + 1], bytes[i + 2] };
        return windows;
    }
}
