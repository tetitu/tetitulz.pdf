// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Handles the DCTDecode (JPEG) stream filter per ISO 32000 §7.4.8.
    /// The PDF DCTDecode filter wraps a standard JPEG bitstream — no PDF-specific
    /// framing is added, so the raw bytes are already a valid JPEG file.
    /// This filter is a passthrough: the raw JPEG bytes are returned as-is so
    /// that the image subsystem or caller can process the JPEG directly.
    /// </summary>
    public sealed class DctDecodeFilter : StreamFilter
    {
        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            // The stream data is a raw JPEG bitstream. Return as-is.
            return data;
        }

        // ISO/IEC 10918-1 marker codes, which §7.4.7 defers to for DCTDecode.
        private const byte MarkerPrefix = 0xFF;
        private const byte StuffedByte  = 0x00;   // FF 00 inside entropy-coded data is a literal FF
        private const byte Tem          = 0x01;   // TEM — standalone, no segment
        private const byte RestartFirst = 0xD0;   // RST0 … RST7 — standalone
        private const byte RestartLast  = 0xD7;
        private const byte Soi          = 0xD8;
        private const byte Eoi          = 0xD9;
        private const byte Sos          = 0xDA;

        /// <summary>
        /// Where a JPEG codestream beginning at <paramref name="start"/> ends: the index just past
        /// its <c>EOI</c> marker (ISO/IEC 10918-1 §B.2), or -1 when the bytes ran out or lost marker
        /// sync first. For a stream lying inside a longer byte run — an inline image's payload, a
        /// PostScript program's <c>currentfile /DCTDecode filter</c> source — the bytes after it are
        /// the embedder's.
        /// </summary>
        /// <remarks>
        /// It is walked segment by segment and <b>not</b> searched for <c>FF D9</c>. Entropy-coded
        /// scan data cannot contain a bare <c>FF D9</c> — §B.1.1.5 byte-stuffs every <c>FF</c> as
        /// <c>FF 00</c> — but an APP0/APP1 segment routinely holds a JFIF or EXIF <em>thumbnail</em>,
        /// which is a whole embedded JPEG and does end in <c>FF D9</c>. A search finds that one first
        /// and ends the image inside its own header. Every segment declares its own length, so
        /// stepping over them is both exact and cheap.
        /// </remarks>
        public static int FindEnd(byte[] data, int start, int count)
        {
            CheckEmbeddedRange(data, start, count);
            int limit = start + count;

            if (count < 4 || data[start] != MarkerPrefix || data[start + 1] != Soi) return -1;

            int i = start + 2;
            while (i + 1 < limit)
            {
                if (data[i] != MarkerPrefix) return -1;   // lost marker sync — bounds nothing

                // §B.1.1.2: any number of FF fill bytes may precede a marker code.
                int m = i + 1;
                while (m < limit && data[m] == MarkerPrefix) m++;
                if (m >= limit) return -1;
                byte marker = data[m];

                if (marker == Eoi) return m + 1;

                if (marker == Tem || marker == Soi ||
                    (marker >= RestartFirst && marker <= RestartLast))
                {
                    i = m + 1;                                // standalone marker, no segment
                    continue;
                }

                if (m + 2 >= limit) return -1;
                int segment = (data[m + 1] << 8) | data[m + 2];
                if (segment < 2) return -1;                   // the length counts its own two bytes
                long next = (long)m + 1 + segment;
                if (next > limit) return -1;                  // segment claims more bytes than exist
                i = (int)next;

                if (marker != Sos) continue;

                // Entropy-coded data follows a scan header and has no length of its own: skip to the
                // next byte that is really a marker. FF 00 is a stuffed literal FF, FF FF is fill,
                // and RSTn are restart markers inside the scan — none of them end it.
                while (i + 1 < limit)
                {
                    if (data[i] != MarkerPrefix) { i++; continue; }
                    byte follower = data[i + 1];
                    if (follower == StuffedByte) { i += 2; continue; }
                    if (follower == MarkerPrefix) { i++; continue; }
                    if (follower >= RestartFirst && follower <= RestartLast) { i += 2; continue; }
                    break;                                    // a real marker — back to the segment walk
                }
            }
            return -1;
        }
    }
}
