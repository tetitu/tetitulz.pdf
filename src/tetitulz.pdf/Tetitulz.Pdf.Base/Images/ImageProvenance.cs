// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Images
{
    /// <summary>
    /// Stamps "produced by this library" into rendered raster output, using each format's own
    /// standard metadata slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rendered page left the library with no provenance at all: a PNG or JPEG out of
    /// <c>PdfPageRenderer</c> was indistinguishable from one produced by anything else, which is the
    /// first thing a support request needs to establish. PDFs get <c>/Producer</c>
    /// (<see cref="PdfProducer"/>); these are the raster equivalents.
    /// </para>
    /// <para>
    /// <b>Neither stamp can change a pixel.</b> PNG's <c>tEXt</c> is an ancillary chunk and JPEG's
    /// <c>COM</c> is a comment segment — both are skipped by every decoder that does not care about
    /// them. The image data is copied through byte-for-byte; only a header is inserted.
    /// </para>
    /// <para>
    /// <b>Both fail soft.</b> If the input is not the format it claims — truncated, or an encoder
    /// returning something unexpected — the original bytes are returned unchanged rather than a
    /// corrupted image. Losing the stamp is a cosmetic loss; corrupting the render is not.
    /// </para>
    /// </remarks>
    internal static class ImageProvenance
    {
        private const string PngSoftwareKeyword = "Software";

        /// <summary>
        /// Inserts a <c>tEXt</c> chunk (<c>Software</c>) immediately after <c>IHDR</c>, which the
        /// PNG specification requires to be the first chunk.
        /// </summary>
        internal static byte[] StampPng(byte[] png)
        {
            if (png == null || png.Length < 8 + 12) return png;

            // 89 P N G \r \n 1A \n
            if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47 ||
                png[4] != 0x0D || png[5] != 0x0A || png[6] != 0x1A || png[7] != 0x0A)
                return png;

            // First chunk must be IHDR; its length field is big-endian and excludes type+CRC.
            int ihdrLen = ReadBe32(png, 8);
            if (ihdrLen < 0) return png;
            int afterIhdr = 8 + 4 + 4 + ihdrLen + 4;      // len + type + data + crc
            if (afterIhdr > png.Length) return png;
            if (png[12] != (byte)'I' || png[13] != (byte)'H' ||
                png[14] != (byte)'D' || png[15] != (byte)'R') return png;

            byte[] keyword = Encoding.ASCII.GetBytes(PngSoftwareKeyword);
            byte[] text    = Latin1(PdfProducer.Value);
            byte[] data    = new byte[keyword.Length + 1 + text.Length];
            Buffer.BlockCopy(keyword, 0, data, 0, keyword.Length);
            data[keyword.Length] = 0;                     // NUL separates keyword from text
            Buffer.BlockCopy(text, 0, data, keyword.Length + 1, text.Length);

            byte[] type = Encoding.ASCII.GetBytes("tEXt");
            var chunk = new byte[4 + 4 + data.Length + 4];
            WriteBe32(chunk, 0, data.Length);
            Buffer.BlockCopy(type, 0, chunk, 4, 4);
            Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
            WriteBe32(chunk, 8 + data.Length, unchecked((int)Crc32(type, data)));

            var result = new byte[png.Length + chunk.Length];
            Buffer.BlockCopy(png, 0, result, 0, afterIhdr);
            Buffer.BlockCopy(chunk, 0, result, afterIhdr, chunk.Length);
            Buffer.BlockCopy(png, afterIhdr, result, afterIhdr + chunk.Length, png.Length - afterIhdr);
            return result;
        }

        /// <summary>
        /// Inserts a <c>COM</c> (0xFFFE) segment immediately after the <c>SOI</c> marker.
        /// </summary>
        internal static byte[] StampJpeg(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length < 4) return jpeg;
            if (jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;   // not SOI

            byte[] text = Latin1(PdfProducer.Value);
            // Segment length counts the two length bytes themselves but not the marker (§B.1.1.4),
            // and the field is 16-bit, so an absurdly long producer string is simply not stamped.
            int segLen = text.Length + 2;
            if (segLen > 0xFFFF) return jpeg;

            // After the APPn segments, not straight after SOI: JFIF 1.02 requires its APP0 to
            // "immediately follow the SOI marker", and Exif its APP1; a comment at offset 2 would sit
            // where a format sniffer looks for JFIF.
            int at = AfterLeadingAppSegments(jpeg);

            var result = new byte[jpeg.Length + 2 + segLen];
            Buffer.BlockCopy(jpeg, 0, result, 0, at);
            result[at] = 0xFF; result[at + 1] = 0xFE;              // COM
            result[at + 2] = (byte)((segLen >> 8) & 0xFF);
            result[at + 3] = (byte)(segLen & 0xFF);
            Buffer.BlockCopy(text, 0, result, at + 4, text.Length);
            Buffer.BlockCopy(jpeg, at, result, at + 4 + text.Length, jpeg.Length - at);
            return result;
        }

        /// <summary>
        /// The offset just past the APPn segments (FFE0–FFEF) that follow SOI — 2 when there are
        /// none. Stops at the first segment that is not an APPn or whose length would run past the
        /// data, so a malformed header puts the comment no later than the last well-formed APPn.
        /// </summary>
        private static int AfterLeadingAppSegments(byte[] jpeg)
        {
            int at = 2;
            while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] >= 0xE0 && jpeg[at + 1] <= 0xEF)
            {
                int length = (jpeg[at + 2] << 8) | jpeg[at + 3];   // counts its own two bytes
                if (length < 2 || at + 2 + length > jpeg.Length) break;
                at += 2 + length;
            }
            return at;
        }

        /// <summary>
        /// The ICC profile the pixels are in, as PNG's <c>iCCP</c> chunk (PNG §11.3.3.3: a name, a NUL,
        /// compression method 0 and the zlib-compressed profile), straight after <c>IHDR</c>; any <c>sRGB</c>,
        /// <c>gAMA</c>, <c>cHRM</c> or <c>iCCP</c> chunk already there is removed, since each would contradict it.
        /// The image data is copied through unchanged; a file that is not a PNG comes back as it was.
        /// </summary>
        internal static byte[] EmbedIccPng(byte[] png, byte[] profile)
        {
            if (png == null || profile == null || png.Length < 8 + 12) return png;
            if (png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return png;
            if (png[12] != (byte)'I' || png[13] != (byte)'H' || png[14] != (byte)'D' || png[15] != (byte)'R') return png;

            byte[] name = Encoding.ASCII.GetBytes("ICC profile");
            byte[] compressed = Core.IO.Filters.FlateEncodeFilter.Encode(profile);
            var data = new byte[name.Length + 2 + compressed.Length];
            Buffer.BlockCopy(name, 0, data, 0, name.Length);
            // data[name.Length] is the NUL ending the name, data[name.Length + 1] compression method 0.
            Buffer.BlockCopy(compressed, 0, data, name.Length + 2, compressed.Length);
            byte[] type = Encoding.ASCII.GetBytes("iCCP");

            using var result = new System.IO.MemoryStream(png.Length + data.Length + 12);
            result.Write(png, 0, 8);
            int at = 8;
            bool inserted = false;
            while (at + 12 <= png.Length)
            {
                int length = ReadBe32(png, at);
                if (length < 0 || at + 12 + (long)length > png.Length) return png;
                string chunk = Encoding.ASCII.GetString(png, at + 4, 4);
                int total = 12 + length;
                if (chunk != "sRGB" && chunk != "gAMA" && chunk != "cHRM" && chunk != "iCCP")
                    result.Write(png, at, total);
                at += total;
                if (!inserted && chunk == "IHDR")
                {
                    var header = new byte[8];
                    WriteBe32(header, 0, data.Length);
                    Buffer.BlockCopy(type, 0, header, 4, 4);
                    result.Write(header, 0, 8);
                    result.Write(data, 0, data.Length);
                    var crc = new byte[4];
                    WriteBe32(crc, 0, unchecked((int)Crc32(type, data)));
                    result.Write(crc, 0, 4);
                    inserted = true;
                }
            }
            if (at != png.Length) return png;
            return result.ToArray();
        }

        /// <summary>
        /// The ICC profile the pixels are in, as JPEG's <c>APP2</c> <c>ICC_PROFILE</c> segments (ICC.1 Annex B.4:
        /// the identifier and its NUL, a 1-based sequence number, the count, then up to 65 519 bytes of the
        /// profile each), after the leading APPn segments. A file that is not a JPEG, or a profile of more than
        /// 255 segments, comes back as it was.
        /// </summary>
        internal static byte[] EmbedIccJpeg(byte[] jpeg, byte[] profile)
        {
            if (jpeg == null || profile == null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
            const int MaxData = 0xFFFF - 2 - 14;
            int count = Math.Max(1, (profile.Length + MaxData - 1) / MaxData);
            if (count > 255) return jpeg;

            byte[] id = Encoding.ASCII.GetBytes("ICC_PROFILE\0");
            int at = AfterLeadingAppSegments(jpeg);
            using var result = new System.IO.MemoryStream(jpeg.Length + profile.Length + 18 * count);
            result.Write(jpeg, 0, at);
            for (int i = 0; i < count; i++)
            {
                int offset = i * MaxData, n = Math.Min(MaxData, profile.Length - offset);
                int segment = 2 + id.Length + 2 + n;   // counts its own length bytes
                result.WriteByte(0xFF); result.WriteByte(0xE2);
                result.WriteByte((byte)(segment >> 8)); result.WriteByte((byte)segment);
                result.Write(id, 0, id.Length);
                result.WriteByte((byte)(i + 1)); result.WriteByte((byte)count);
                result.Write(profile, offset, n);
            }
            result.Write(jpeg, at, jpeg.Length - at);
            return result.ToArray();
        }

        /// <summary>
        /// The ICC profile the pixels are in, as WebP's <c>ICCP</c> chunk (RFC 9649 §2.7): the file made the
        /// extended form when it is the simple one — a <c>VP8X</c> chunk first, its ICC flag set, the canvas
        /// <paramref name="width"/> × <paramref name="height"/>, its alpha flag from a lossless bitstream's own
        /// hint — and the <c>ICCP</c> chunk straight after it. A file that is not a WebP comes back as it was.
        /// </summary>
        internal static byte[] EmbedIccWebp(byte[] webp, byte[] profile, int width, int height)
        {
            if (webp == null || profile == null || webp.Length < 20) return webp;
            if (Encoding.ASCII.GetString(webp, 0, 4) != "RIFF" || Encoding.ASCII.GetString(webp, 8, 4) != "WEBP") return webp;
            string first = Encoding.ASCII.GetString(webp, 12, 4);
            int firstSize = webp[16] | (webp[17] << 8) | (webp[18] << 16) | (webp[19] << 24);
            if (firstSize < 0 || 20L + firstSize > webp.Length) return webp;

            using var result = new System.IO.MemoryStream(webp.Length + profile.Length + 40);
            result.Write(webp, 0, 12);   // RIFF, its size (patched below), WEBP
            int rest;
            if (first == "VP8X")
            {
                if (firstSize < 10) return webp;
                int end = 20 + firstSize + (firstSize & 1);
                var vp8x = new byte[end - 12];
                Buffer.BlockCopy(webp, 12, vp8x, 0, vp8x.Length);
                vp8x[8] |= 0x20;   // ICC
                result.Write(vp8x, 0, vp8x.Length);
                rest = end;
            }
            else if (first == "VP8 " || first == "VP8L")
            {
                // A lossless bitstream says whether it uses alpha: bit 28 after its 0x2F signature.
                bool alpha = first == "VP8L" && firstSize >= 5 && webp[20] == 0x2F && (webp[24] & 0x10) != 0;
                var vp8x = new byte[18];
                Encoding.ASCII.GetBytes("VP8X").CopyTo(vp8x, 0);
                vp8x[4] = 10;
                vp8x[8] = (byte)(0x20 | (alpha ? 0x10 : 0));
                WriteLe24(vp8x, 12, width - 1);
                WriteLe24(vp8x, 15, height - 1);
                result.Write(vp8x, 0, vp8x.Length);
                rest = 12;
            }
            else return webp;

            var iccp = new byte[8];
            Encoding.ASCII.GetBytes("ICCP").CopyTo(iccp, 0);
            iccp[4] = (byte)profile.Length; iccp[5] = (byte)(profile.Length >> 8); iccp[6] = (byte)(profile.Length >> 16); iccp[7] = (byte)(profile.Length >> 24);
            result.Write(iccp, 0, 8);
            result.Write(profile, 0, profile.Length);
            if ((profile.Length & 1) != 0) result.WriteByte(0);

            // The chunks after (any ICCP already there is the one this replaces).
            int at = rest;
            while (at + 8 <= webp.Length)
            {
                string chunk = Encoding.ASCII.GetString(webp, at, 4);
                int size = webp[at + 4] | (webp[at + 5] << 8) | (webp[at + 6] << 16) | (webp[at + 7] << 24);
                if (size < 0 || at + 8L + size > webp.Length) return webp;
                int total = 8 + size + (size & 1);
                if (at + total > webp.Length) total = webp.Length - at;
                if (chunk != "ICCP") result.Write(webp, at, total);
                at += total;
            }
            byte[] bytes = result.ToArray();
            int riff = bytes.Length - 8;
            bytes[4] = (byte)riff; bytes[5] = (byte)(riff >> 8); bytes[6] = (byte)(riff >> 16); bytes[7] = (byte)(riff >> 24);
            return bytes;
        }

        private static void WriteLe24(byte[] b, int off, int v)
        {
            b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16);
        }

        // PNG tEXt is Latin-1 by specification; a character outside it is dropped rather than
        // mojibake'd, which only matters if the version string ever stops being ASCII.
        private static byte[] Latin1(string s)
        {
            var bytes = new byte[s.Length];
            int n = 0;
            foreach (char c in s) if (c <= 0xFF) bytes[n++] = (byte)c;
            if (n == bytes.Length) return bytes;
            var trimmed = new byte[n];
            Buffer.BlockCopy(bytes, 0, trimmed, 0, n);
            return trimmed;
        }

        private static int ReadBe32(byte[] b, int off) =>
            (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

        private static void WriteBe32(byte[] b, int off, int v)
        {
            b[off]     = (byte)((v >> 24) & 0xFF);
            b[off + 1] = (byte)((v >> 16) & 0xFF);
            b[off + 2] = (byte)((v >> 8) & 0xFF);
            b[off + 3] = (byte)(v & 0xFF);
        }

        // CRC-32/ISO-HDLC over chunk type + data, as PNG §5.3 requires.
        private static uint Crc32(byte[] type, byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            crc = Crc32Update(crc, type);
            crc = Crc32Update(crc, data);
            return crc ^ 0xFFFFFFFFu;
        }

        private static uint Crc32Update(uint crc, byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return crc;
        }
    }
}
