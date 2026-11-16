// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Reads what a JPEG 2000 image says about itself without decoding it: which framing it uses
    /// (a JP2 container or a raw codestream), the image grid and component count, each component's
    /// precision and sign, and how many of its channels are colour.
    /// </summary>
    /// <remarks>
    /// <para><b>One reader for the library.</b> Base's image layer and the JPEG 2000 plugin each
    /// carried a JP2 box walker, and they disagreed on the 64-bit box length: the plugin refused
    /// any length above 4 GiB, Base read it and clamped it to the data. This reader clamps, because
    /// a length past the end of the data describes a truncated file whose earlier bytes are still
    /// what they say they are. The JP2 Header box's <i>contents</i> — colour specifications,
    /// palette, mapping, channel definitions — are <see cref="Jp2ColourDescription"/>'s, which
    /// walks through <see cref="TryReadBoxHeader"/> and <see cref="TryFindTopLevelBox"/> here.</para>
    ///
    /// <para><b>Public because a plugin needs it.</b> Core's <c>InternalsVisibleTo</c> names no
    /// plugin, and a decode plugin may read these headers to bound a decode before it
    /// allocates.</para>
    ///
    /// <para><b>The codestream is located by walking boxes, never by scanning for its first
    /// bytes.</b> A scan for the SOC/SIZ pattern can match inside an XML, UUID or ICC-profile box
    /// that precedes the codestream, and a wrong precision silently rescales every sample.</para>
    ///
    /// <para><b>Every walk is bounded</b> so a malformed container cannot spin: 64 top-level boxes,
    /// and the specification's 16 384 components. The JP2 Header box's contents — its own bounded
    /// walk, and the palette, mapping and definition boxes each sized by its own length — are
    /// <see cref="Jp2ColourDescription"/>'s.</para>
    /// </remarks>
    public static class JpxHeader
    {
        /// <summary>How many boxes one walk reads — the top level, or a superbox's children — before it stops: the one ceiling for both walks.</summary>
        internal const int MaxBoxesPerWalk = 64;
        private const int MaxTopLevelBoxes = MaxBoxesPerWalk;

        /// <summary>True for the 12-byte JP2 signature box that opens a JP2 container.</summary>
        /// <param name="data">The image's JPEG 2000 bytes.</param>
        public static bool IsJp2Container(byte[] data) =>
            data != null && data.Length >= 12
            && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x0C
            && data[4] == 0x6A && data[5] == 0x50 && data[6] == 0x20 && data[7] == 0x20;

        /// <summary>True for a raw codestream, which opens with the SOC marker <c>FF 4F</c>.</summary>
        /// <param name="data">The image's JPEG 2000 bytes.</param>
        public static bool IsRawCodestream(byte[] data) =>
            data != null && data.Length >= 2 && data[0] == 0xFF && data[1] == 0x4F;

        /// <summary>True for either JPEG 2000 framing.</summary>
        /// <param name="data">The image's bytes.</param>
        public static bool IsJpx(byte[] data) => IsJp2Container(data) || IsRawCodestream(data);

        /// <summary>
        /// Reads the image grid the codestream declares — the reference-grid extent
        /// (<c>Xsiz - XOsiz</c> by <c>Ysiz - YOsiz</c>, ISO/IEC 15444-1 §A.5.1) and the component
        /// count <c>Csiz</c> — <b>without decoding anything</b>.
        /// </summary>
        /// <remarks>
        /// This exists so a ceiling can be applied <i>before</i> a codec allocates: a codestream may
        /// declare a 65535×65535 grid in a few kilobytes. The values are the codestream's own claim,
        /// which is exactly what a pre-decode ceiling needs; a file that lies downwards is a matter
        /// for the geometry check after the decode.
        /// </remarks>
        /// <param name="data">A JP2 container or a raw codestream; both are located.</param>
        /// <param name="width">Reference-grid width, <c>Xsiz - XOsiz</c>. Positive on success.</param>
        /// <param name="height">Reference-grid height, <c>Ysiz - YOsiz</c>. Positive on success.</param>
        /// <param name="components">Declared <c>Csiz</c>, 1–16 384.</param>
        /// <returns>False when the codestream cannot be located, or its SIZ segment is truncated or
        /// self-contradictory — in which case nothing may be concluded about the size.</returns>
        public static bool TryReadImageSize(byte[] data, out long width, out long height, out int components)
        {
            width = height = 0;
            components = 0;

            if (!TryReadSiz(data, out JpxImageSize siz)) return false;

            // §A.5.1 requires 0 <= XOsiz < Xsiz and 0 <= YOsiz < Ysiz.
            if (siz.Xsiz <= siz.XOsiz || siz.Ysiz <= siz.YOsiz) return false;

            width      = siz.Xsiz - siz.XOsiz;
            height     = siz.Ysiz - siz.YOsiz;
            components = siz.Components.Length;
            return true;
        }

        /// <summary>
        /// The JP2 header's colour specifications (<c>colr</c> boxes, T.800 I.5.3.3 and T.801
        /// M.11.7.2) in file order, and whether the header holds a Bits Per Component box — what a
        /// conformance rule about the file asks (ISO 19005-2 §6.2.8.3), without decoding a sample.
        /// False for a raw codestream, which has no header, or a header that cannot be read.
        /// </summary>
        public static bool TryReadColourSpecifications(byte[] data, out JpxColourSpecificationSummary[] specifications, out bool bitsPerComponentBox)
        {
            specifications = null;
            bitsPerComponentBox = false;
            if (!IsJp2Container(data) || !Jp2ColourDescription.TryRead(data, out Jp2ColourDescription header, out _)) return false;
            var list = new JpxColourSpecificationSummary[header.ColourSpecifications.Length];
            for (int i = 0; i < list.Length; i++)
            {
                Jp2ColourSpecification spec = header.ColourSpecifications[i];
                list[i] = new JpxColourSpecificationSummary(spec.Method, spec.Precedence, spec.Approximation,
                    spec.Method == Jp2ColourSpecification.EnumeratedMethod ? spec.EnumeratedSpace : -1, spec.IccDataColourSpace);
            }
            specifications = list;
            bitsPerComponentBox = header.HasBitsPerComponentBox;
            return true;
        }

        /// <summary>
        /// Reads each component's sample precision in bits (<c>Ssiz + 1</c>, ISO/IEC 15444-1 Table
        /// A.11) and whether any component is signed.
        /// </summary>
        /// <param name="data">A JP2 container or a raw codestream; both are located.</param>
        /// <param name="precision">One entry per component in the codestream, 1–38 bits.</param>
        /// <param name="anySigned">
        /// True when any component's <c>Ssiz</c> sign bit is set. A fact about the codestream only;
        /// how a particular decoder normalises signed samples is that decoder's business.
        /// </param>
        /// <returns>False when the codestream cannot be located or its SIZ segment is truncated.</returns>
        public static bool TryReadComponentPrecision(byte[] data, out int[] precision, out bool anySigned)
        {
            precision = null;
            anySigned = false;

            if (!TryReadSiz(data, out JpxImageSize siz)) return false;

            var bits = new int[siz.Components.Length];
            for (int c = 0; c < bits.Length; c++)
            {
                bits[c] = siz.Components[c].Precision;
                if (siz.Components[c].IsSigned) anySigned = true;
            }

            precision = bits;
            return true;
        }

        /// <summary>
        /// Reads how many channels of a JP2 image are <i>colour</i>, so a four-channel image can be
        /// told apart from CMYK without guessing: the <c>colr</c> box names the colour space, and
        /// failing that the <c>cdef</c> box says which channels are colour and which are opacity.
        /// </summary>
        /// <remarks>
        /// Answers from <see cref="Jp2ColourDescription"/>, the one reader of the JP2 Header box, so
        /// a malformed header (a truncated box, a palette without a mapping) reads as "does not say"
        /// here and is refused with a reason there.
        /// </remarks>
        /// <param name="data">The image's JPEG 2000 bytes.</param>
        /// <param name="colourComponents">The colour-channel count; 0 when this returns false.</param>
        /// <returns>False for a raw codestream (it has no boxes to read) and for any header that
        /// does not say.</returns>
        public static bool TryReadColourComponents(byte[] data, out int colourComponents)
        {
            colourComponents = 0;
            if (!Jp2ColourDescription.TryRead(data, out Jp2ColourDescription header, out _)) return false;
            colourComponents = header.ColourChannelCount();
            return colourComponents > 0;
        }

        /// <summary>
        /// Whether the codestream uses HT code-blocks (ITU-T T.814 | ISO/IEC 15444-15, HTJ2K): its CAP
        /// marker declares Pcap<sup>15</sup> (T.814 §A.3), or a COD or COC — in the main header or any
        /// tile-part header — sets code-block style bit 6 (§A.4). Read from the markers alone, nothing
        /// decoded — what a conformance rule needs (ISO 19005-2 §6.2.8.3 admits only the JPX baseline,
        /// whose codestreams are T.800's, T.801 M.9.2.3).
        /// </summary>
        /// <param name="data">A JP2 container or a raw codestream; both are located.</param>
        /// <param name="highThroughput">True when HT code-blocks are declared or used.</param>
        /// <returns>False when the codestream cannot be located or its header ends before it should;
        /// nothing may be concluded then.</returns>
        public static bool TryReadHighThroughput(byte[] data, out bool highThroughput)
        {
            highThroughput = false;
            if (!TryLocateCodestream(data, out long start, out long end)) return false;
            if (!TryReadSiz(data, out JpxImageSize siz)) return false;
            int componentIndexBytes = siz.Components.Length < 257 ? 1 : 2;

            long pos = start + 2;   // past SOC
            long tilePartEnd = -1;  // inside a tile-part header: where its tile-part ends
            int markers = 0;
            while (pos + 4 <= end)
            {
                if (++markers > MaxHeaderMarkers) return false;
                int marker = ReadU16(data, pos);
                if (marker == 0xFFD9) return true;                       // EOC
                if (marker == 0xFF93)                                   // SOD: skip the tile-part's data
                {
                    if (tilePartEnd <= pos) return false;
                    pos = tilePartEnd;
                    continue;
                }
                long length = ReadU16(data, pos + 2);
                long segStart = pos + 4, segEnd = pos + 2 + length;
                if (length < 2 || segEnd > end) return false;
                switch (marker)
                {
                    case 0xFF90:   // SOT: Isot(2) Psot(4) TPsot(1) TNsot(1)
                        if (length < 10) return false;
                        long psot = ReadU32(data, segStart + 2);
                        tilePartEnd = psot == 0 ? end : pos + psot;
                        if (tilePartEnd > end || tilePartEnd <= segEnd) return false;
                        break;
                    case 0xFF50:   // CAP: Pcap(4); Pcap15 is its 15th most significant bit
                        if (length >= 6 && (ReadU32(data, segStart) & (1L << (32 - 15))) != 0) highThroughput = true;
                        break;
                    case 0xFF52:   // COD: Scod(1), SGcod(4), then SPcod: levels, xcb, ycb, style
                        if (length >= 2 + 1 + 4 + 4 && (data[segStart + 1 + 4 + 3] & 0x40) != 0) highThroughput = true;
                        break;
                    case 0xFF53:   // COC: Ccoc(1 or 2), Scoc(1), then SPcoc: levels, xcb, ycb, style
                        if (length >= 2 + componentIndexBytes + 1 + 4 && (data[segStart + componentIndexBytes + 1 + 3] & 0x40) != 0) highThroughput = true;
                        break;
                }
                if (highThroughput) return true;
                pos = segEnd;
            }
            return true;
        }

        /// <summary>How many marker segments <see cref="TryReadHighThroughput"/> reads before it gives up: a header is a few dozen; a stream of tiny segments would otherwise be walked to its end.</summary>
        private const int MaxHeaderMarkers = 1 << 20;

        /// <summary>
        /// Where a contiguous codestream is: all of <c>data</c> for a raw codestream, else the content
        /// of the JP2 container's first <c>jp2c</c> box (ISO/IEC 15444-1 §I.5.4). False when neither
        /// is found — a fragmented codestream included, which <see cref="TryLocateCodestream(byte[], out byte[], out long, out long, out string)"/> assembles.
        /// </summary>
        internal static bool TryLocateCodestream(byte[] data, out long start, out long end)
        {
            start = end = 0;
            if (data == null || data.Length < 4) return false;
            if (IsRawCodestream(data))
            {
                end = data.Length;
                return true;
            }
            return TryFindTopLevelBox(data, Jp2BoxTypes.Jp2c, out start, out end);
        }

        /// <summary>
        /// The codestream's bytes: <paramref name="buffer"/> is <paramref name="data"/> itself,
        /// <c>[start, end)</c> the codestream, for a raw codestream or a contiguous codestream box;
        /// for a JPX whose first codestream is a Fragment Table (T.801 M.11.3 — the baseline lets a
        /// file be fragmented, M.9.2.5) the listed fragments concatenated into a new buffer, which
        /// the whole of is the codestream. False, with the reason, when there is neither, when a
        /// fragment lies in another file or outside this one, or when the list asks for more than
        /// <see cref="PdfLimits.MaxDecompressedBytes"/> — a fragment list is a request to allocate.
        /// </summary>
        internal static bool TryLocateCodestream(byte[] data, out byte[] buffer, out long start, out long end, out string reason)
        {
            buffer = data;
            reason = null;
            if (TryLocateCodestream(data, out start, out end)) return true;
            start = end = 0;
            if (data == null || data.Length < 4 || !IsJp2Container(data))
            {
                reason = "no JPEG 2000 codestream: neither a raw codestream nor a JP2 container with a contiguous codestream box";
                return false;
            }
            if (!TryFindTopLevelBox(data, Jp2BoxTypes.Ftbl, out long tableStart, out long tableEnd))
            {
                reason = "no JPEG 2000 codestream: the JP2 container has neither a contiguous codestream box nor a fragment table";
                return false;
            }
            if (!TryAssembleFragments(data, tableStart, tableEnd, out buffer, out reason)) return false;
            end = buffer.Length;
            return true;
        }

        /// <summary>The fragment list's byte ranges, checked and copied in order into one buffer (T.801 M.11.3.1, Table M.17).</summary>
        private static bool TryAssembleFragments(byte[] data, long tableStart, long tableEnd, out byte[] buffer, out string reason)
        {
            buffer = null;
            if (!TryReadBoxHeader(data, tableStart, out uint type, out long listStart, out long listEnd) || type != Jp2BoxTypes.Flst || listEnd > tableEnd)
            {
                reason = "the fragment table's first box is not a fragment list (T.801 M.11.3)";
                return false;
            }
            const int entryBytes = 8 + 4 + 2;
            if (listEnd - listStart < 2)
            {
                reason = "the fragment list is truncated before its fragment count";
                return false;
            }
            int count = ReadU16(data, listStart);
            if (count == 0)
            {
                reason = "the fragment list names no fragments";
                return false;
            }
            if (listEnd - listStart - 2 < (long)count * entryBytes)
            {
                reason = $"the fragment list declares {count} fragment(s) but holds {(listEnd - listStart - 2) / entryBytes}";
                return false;
            }
            long ceiling = PdfLimits.MaxDecompressedBytes;
            long total = 0;
            long pos = listStart + 2;
            for (int i = 0; i < count; i++, pos += entryBytes)
            {
                long offsetHigh = ReadU32(data, pos), offsetLow = ReadU32(data, pos + 4);
                long length = ReadU32(data, pos + 8);
                int dataReference = ReadU16(data, pos + 12);
                if (dataReference != 0)
                {
                    reason = $"fragment {i} of the codestream is in another file (data reference {dataReference}); only fragments within this file are read";
                    return false;
                }
                if (offsetHigh != 0 || offsetLow + length > data.Length)
                {
                    reason = $"fragment {i} of the codestream ({length} byte(s) at offset {(offsetHigh << 32) | offsetLow}) lies outside the {data.Length}-byte file";
                    return false;
                }
                total += length;
                if (total > ceiling)
                {
                    reason = $"the fragment list asks for at least {total} bytes of codestream; PdfLimits.MaxDecompressedBytes is {ceiling}";
                    return false;
                }
            }
            buffer = new byte[total];
            pos = listStart + 2;
            long at = 0;
            for (int i = 0; i < count; i++, pos += entryBytes)
            {
                int offset = (int)ReadU32(data, pos + 4);
                int length = (int)ReadU32(data, pos + 8);
                Buffer.BlockCopy(data, offset, buffer, (int)at, length);
                at += length;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// The content range of the first top-level box of the given type (ISO/IEC 15444-1 §I.4),
        /// within the bounded walk; a superbox's children are not searched. The one top-level walk:
        /// the codestream, the JP2 Header box and a compositing layer's header are all found through it.
        /// </summary>
        internal static bool TryFindTopLevelBox(byte[] data, uint boxType, out long contentStart, out long contentEnd)
        {
            contentStart = contentEnd = 0;
            if (data == null) return false;

            long pos = 0;
            for (int i = 0; i < MaxTopLevelBoxes; i++)
            {
                if (!TryReadBoxHeader(data, pos, out uint type, out long start, out long end)) return false;
                if (type == boxType)
                {
                    contentStart = start;
                    contentEnd = end;
                    return true;
                }
                pos = end;
            }
            return false;
        }

        /// <summary>
        /// Reads one box header (ISO/IEC 15444-1 §I.4): LBox, TBox and the optional 64-bit XLBox.
        /// LBox 1 means the length is in XLBox; LBox 0 means the box runs to the end of the data. The
        /// content range is clamped to the data.
        /// </summary>
        internal static bool TryReadBoxHeader(byte[] data, long pos, out uint type,
            out long contentStart, out long contentEnd)
        {
            type = 0;
            contentStart = contentEnd = 0;
            if (pos < 0 || pos + 8 > data.Length) return false;

            long length = ReadU32(data, pos);
            type = (uint)ReadU32(data, pos + 4);
            long headerLength = 8;

            if (length == 1)
            {
                if (pos + 16 > data.Length) return false;
                // A length at or above 2^63 reads negative and is refused below with the rest.
                length = (ReadU32(data, pos + 8) << 32) | ReadU32(data, pos + 12);
                headerLength = 16;
            }
            else if (length == 0)
            {
                length = data.Length - pos;
            }

            if (length < headerLength) return false;

            contentStart = pos + headerLength;
            contentEnd = length > data.Length - pos ? data.Length : pos + length;
            return contentStart <= contentEnd;
        }

        /// <summary>
        /// Locates the codestream and reads its SIZ marker segment through the one SIZ reader,
        /// <see cref="JpxImageSize.TryRead"/>, which checks the segment's own structure but not the
        /// tiling — a precision query needs the former only.
        /// </summary>
        private static bool TryReadSiz(byte[] data, out JpxImageSize siz)
        {
            siz = null;
            if (!TryLocateCodestream(data, out data, out long start, out long end, out _) || start + 4 > end) return false;

            // SOC (FF4F) is immediately followed by SIZ (FF51) — ISO/IEC 15444-1 §A.4.1.
            if (data[start] != 0xFF || data[start + 1] != 0x4F
                || data[start + 2] != 0xFF || data[start + 3] != 0x51) return false;

            return JpxImageSize.TryRead(data, start + 4, end, out siz, out _);
        }

        internal static long ReadU32(byte[] d, long i) => IO.BigEndian.U32(d, i);

        internal static int ReadU16(byte[] d, long i) => IO.BigEndian.U16(d, i);
    }
}
