// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Core.Metadata
{
    /// <summary>
    /// The <c>xpacket</c> processing instructions that wrap an XMP packet (ISO 16684-1 §7.3.2).
    /// </summary>
    /// <remarks>
    /// <para><b>The <c>begin</c> attribute's value <i>is</i> a byte-order mark</b> — that is its
    /// entire purpose. A scanner locating a packet inside an arbitrary file reads those bytes to
    /// decide the encoding and byte order before it can parse anything, so the attribute must carry
    /// U+FEFF itself and not a description of it.</para>
    ///
    /// <para><b>One writer of this header.</b> Built with a <b>verbatim</b> interpolated string
    /// (<c>$@"…"</c>) containing <c>\xEF\xBB\xBF</c>, in which a backslash is a backslash, the
    /// attribute would be the twelve literal characters <c>\xEF\xBB\xBF</c> rather than a BOM. The
    /// XML still parses, because it is only an attribute value, so nothing in this library would
    /// notice; what breaks is an XMP toolkit or a PDF/A/PDF/UA validator that reads the header to
    /// determine the encoding.</para>
    ///
    /// <para>The <c>end</c> attribute is <c>"w"</c> (writable) rather than <c>"r"</c>: these packets
    /// are followed by padding so a later in-place edit can grow the XMP without moving the rest of
    /// the file.</para>
    /// </remarks>
    public static class XmpPacket
    {
        /// <summary>U+FEFF, the byte-order mark the <c>begin</c> attribute carries.</summary>
        public const string ByteOrderMark = "﻿";

        /// <summary>
        /// The packet identifier every XMP packet carries, fixed by ISO 16684-1.
        /// </summary>
        public const string PacketId = "W5M0MpCehiHzreSzNTczkc9d";

        // ── Namespace URIs ──────────────────────────────────────────────────────────
        //
        // These were literals in five files across three assemblies — Base's tagged-document
        // builder, the Conformance plugin's packet writer, checker and two converters, and the
        // Accessibility feature's PDF/UA writer (which had them as consts, but in a feature neither
        // of the others may reference). A namespace URI is a fixed string that only ever fails
        // SILENTLY: bind the wrong one and the packet still parses, still validates as XML, and
        // simply declares nothing a reader recognises.

        /// <summary>The <c>x:xmpmeta</c> wrapper namespace.</summary>
        public const string XmpMetaNamespace = "adobe:ns:meta/";

        /// <summary>RDF, as XMP requires it (ISO 16684-1 §6).</summary>
        public const string RdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

        /// <summary>Dublin Core — <c>dc:title</c>, <c>dc:creator</c>, <c>dc:description</c>.</summary>
        public const string DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";

        /// <summary>XMP Basic — <c>xmp:CreatorTool</c>, <c>xmp:ModifyDate</c>.</summary>
        public const string XmpBasicNamespace = "http://ns.adobe.com/xap/1.0/";

        /// <summary>
        /// Adobe's PDF extension schema, <c>pdfx:</c> — the custom document properties a PDF's
        /// <c>/Info</c> carries beyond the predefined keys, which Acrobat writes here by the key's name.
        /// </summary>
        internal const string PdfxNamespace = "http://ns.adobe.com/pdfx/1.3/";

        /// <summary>PDF/UA identification (ISO 14289-1) — <c>pdfuaid:part</c>.</summary>
        public const string PdfUaIdNamespace = "http://www.aiim.org/pdfua/ns/id/";

        /// <summary>PDF/A identification (ISO 19005) — <c>pdfaid:part</c>, <c>pdfaid:conformance</c>.</summary>
        public const string PdfAIdNamespace = "http://www.aiim.org/pdfa/ns/id/";

        /// <summary>
        /// The opening processing instruction, byte-order mark included.
        /// </summary>
        public static string Header =>
            "<?xpacket begin=\"" + ByteOrderMark + "\" id=\"" + PacketId + "\"?>";

        /// <summary>
        /// The closing processing instruction. <c>"w"</c> marks the packet writable in place.
        /// </summary>
        public const string Trailer = "<?xpacket end=\"w\"?>";

        /// <summary>
        /// Decodes an XMP packet's bytes to text, honouring any byte-order mark.
        /// </summary>
        /// <remarks>
        /// <para>XMP permits UTF-8, UTF-16LE and UTF-16BE (ISO 16684-1 §7.3.2) — which is what the
        /// <c>begin</c> attribute's byte-order mark exists to signal. Anything without a recognised
        /// BOM is treated as UTF-8, which is what ISO 32000-1 requires of <c>/Metadata</c>. Trailing
        /// NUL bytes are trimmed: some producers pad the stream with them, and they would otherwise
        /// make the packet fail to parse and cost the caller every property in it.</para>
        ///
        /// <para><b>One decoding for every reader of a packet</b> — <c>PdfXValidator</c> and
        /// <c>PdfUaChecker</c> included. <c>Encoding.UTF8.GetString(bytes)</c> does <b>not</b> throw
        /// on UTF-16 input — it substitutes — so a <c>catch</c> never fires and the interleaved NUL
        /// bytes make the search for <c>GTS_PDFXVersion</c> or <c>dc:title</c> fail: a
        /// <i>conforming</i> file with a UTF-16 packet reported as non-conforming, a false
        /// accusation rather than a missed check.</para>
        ///
        /// <para>The two sites that hand the raw bytes to an <see cref="System.Xml.XmlReader"/> over
        /// a <c>MemoryStream</c> are correct as they are and were left alone: the reader does its
        /// own BOM and declaration-based detection. Only a caller that decodes to a string first
        /// needs this.</para>
        /// </remarks>
        public static string DecodeText(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            string text;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            else
                text = Encoding.UTF8.GetString(bytes);

            return text.TrimEnd('\0');
        }

        /// <summary>
        /// Wraps <paramref name="body"/> — the <c>x:xmpmeta</c> element — in the two processing
        /// instructions, optionally followed by <paramref name="paddingBytes"/> of whitespace before
        /// the trailer so a later in-place edit has room to grow.
        /// </summary>
        public static string Wrap(string body, int paddingBytes = 0)
        {
            var sb = new StringBuilder(Header.Length + (body?.Length ?? 0) + paddingBytes + 32);
            sb.Append(Header).Append('\n');
            sb.Append(body);
            if (body != null && !body.EndsWith("\n")) sb.Append('\n');

            for (int i = 0; i < paddingBytes; i++)
            {
                sb.Append(' ');
                if ((i + 1) % 80 == 0) sb.Append('\n');
            }
            if (paddingBytes > 0) sb.Append('\n');

            sb.Append(Trailer);
            return sb.ToString();
        }
    }
}
