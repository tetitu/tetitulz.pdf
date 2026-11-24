// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Pure-managed JBIG2 (ITU-T T.88 / ISO/IEC 14492) decoder.
    ///
    /// <para><b>Scope — what works.</b> Every region type T.88 defines is implemented; the
    /// remaining limits below are sub-modes, not whole segment types.</para>
    /// <list type="bullet">
    ///   <item>Segment-stream parsing — headers, referred-to lists, page association, length,
    ///         the optional file header, and unknown-length segments (0xFFFFFFFF, terminated by
    ///         the FF AC marker).</item>
    ///   <item>Page information segment, and page composition via the combination operator.</item>
    ///   <item><b>Generic regions</b> both ways: arithmetic (the MQ coder in
    ///         <c>MqDecoder</c>, templates 0-3, with TPGDON) and MMR (delegated to the
    ///         T.6 decoder in <see cref="CcittFaxDecoder"/>).</item>
    ///   <item><b>Refinement regions</b> (§6.3) — standalone segments, and within a symbol
    ///         dictionary.</item>
    ///   <item><b>Symbol dictionaries</b> and <b>text regions</b> in the <b>arithmetic</b> coding
    ///         mode (SDHUFF=0 / SBHUFF=0). The Huffman modes are <b>not</b> usable — see below.</item>
    ///   <item><b>Pattern dictionaries</b>, and <b>halftone regions</b> (Annex C.5's gray-scale
    ///         planes, arithmetic or MMR, HSKIP, HDEFPIXEL and HCOMBOP on a region bitmap).</item>
    ///   <item>/JBIG2Globals concatenation — globals bytes are processed before the per-stream
    ///         data so referenced segments (symbol and pattern dictionaries) resolve.</item>
    /// </list>
    ///
    /// <para><b>Scope — what is not implemented.</b> Anything still unsupported throws, and
    /// <see cref="Jbig2DecodeFilter"/> catches it, returns the input bytes unchanged and records
    /// <c>LastFallbackReason</c>. A caller that needs to distinguish "decoded" from "passed
    /// through" must read that property — the byte array alone cannot tell them apart.</para>
    /// <list type="bullet">
    ///   <item><b>The Huffman coding mode (SDHUFF=1 / SBHUFF=1) now decodes end to end for the
    ///         common case — non-aggregated symbol dictionaries, non-refined text regions,
    ///         standard or user-defined tables alike — and GAPS.md §2a has the full history of how
    ///         it got there across several passes.</b> What remains, precisely: in the Huffman
    ///         mode, symbol dictionary refinement and aggregation (<c>SDREFAGG=1</c>) and text
    ///         region refinement (<c>SBREFINE=1</c>) decode too, as the arithmetic mode does. Their
    ///         refined bitmaps are arithmetic-coded over the RSIZE or BMSIZE bytes the Huffman
    ///         fields name, and the refinement contexts persist across the instances of one segment
    ///         or dictionary; GAPS.md §2a has the record of how that design was checked, against the
    ///         arithmetic twins of the same pictures.</item>
    ///   <item>— <b>the standard Annex B tables (B.1-B.15) are fixed and externally verified</b>,
    ///         never typed from recollection. Every (PREFLEN, RANGELEN, RANGELOW) triple
    ///         was cross-checked between two independent implementations
    ///         (read for their DATA only, not their source),
    ///         codes are computed by <c>AssignCanonicalPrefixes</c> rather than hand-assigned, and
    ///         a real decode — an independent decoder's own self-test byte stream through tables D/B/B/A — is
    ///         pinned by <c>Jbig2StandardHuffmanTableOracleTests</c> alongside the structural
    ///         (Kraft/prefix-free/tiling) tests. The lower-range decode is <c>RangeLow - extra</c>,
    ///         not <c>-(RangeLow + extra)</c>; the independent oracle stream exercises no lower-range
    ///         row, so a dedicated single-entry-table test pins the formula directly.</item>
    ///   <item>— the symbol dictionary's collective bitmap (§6.5.9) is located, decoded (MMR or
    ///         uncompressed) and split into real symbol bitmaps, verified pixel-by-pixel against
    ///         hand-built fixtures — see <c>DecodeSymbolDictionary</c> (<c>internal</c> for exactly
    ///         that testing reason) and GAPS.md §2a.</item>
    ///   <item>— the text region's runcode-coded symbol-ID table (T.88 §7.4.3.1.7) is built and
    ///         used to select each instance's symbol, and the strip/first-S/subsequent-DS
    ///         placement loop mirrors the arithmetic path's already-verified structure — see
    ///         <c>DecodeTextRegionHuffman</c>/<c>BuildSymbolIdHuffmanTable</c>.</item>
    ///   <item>— parsed type-53 user-defined tables are consulted for every "custom" selector
    ///         a symbol dictionary or text region can name (SDHUFFDH/DW/BMSIZE, SBHUFFFS/DS/DT),
    ///         drawn positionally from the segment's referred-to Tables segments — see
    ///         <c>SelectCustomTables</c>. <c>ParseUserDefinedHuffmanTable</c> reads the flags byte
    ///         as T.88 §B.2.1 defines it: bit 0 HTOOB (an unrelated, genuinely optional out-of-band
    ///         line) and bits 1-3 the prefix-size field. Read as two independent optional-tail bits
    ///         (a "low-range flag" at bit 0, a "high-range flag" at bit 1), it would consume one bit
    ///         of that field as a fake flag and skip the lower- and upper-range lines B.2 6-9 make
    ///         <i>mandatory</i>. Cross-checked against two independent implementations' tables, which
    ///         agree with each other and with this file.</item>
    ///   <item><b>Per-instance refinement in a text region (SBREFINE=1)</b> is decoded in both
    ///         coding modes (T.88 §6.4.11): skipped — the AT pixels consumed, every instance a plain
    ///         copy — the decoder would fall out of step at the first refined instance.</item>
    ///   <item><b>Transposed text regions (TRANSPOSED=1) and SBDEFPIXEL=1 decode</b>: a text region
    ///         has a region bitmap, filled with SBDEFPIXEL and drawn on with SBCOMBOP, and
    ///         <c>PlaceTextInstance</c> places an instance in either orientation.
    ///         <para>REFCORNER is honoured (see <c>TextInstanceTopRow</c>) rather than refused,
    ///         because the correction follows from what the corner is named: placing every instance
    ///         as if the stream had said TOPLEFT, a region using either BOTTOM corner would draw
    ///         every symbol <c>height - 1</c> rows too low, by a different amount per glyph, while
    ///         <see cref="Assess"/> reported the segment <c>Decoded</c>.</para></item>
    ///   <item><b>Halftone regions decode every plane</b> — MMR ones too, the T.6 decoder reporting
    ///         where each plane's data ends — so an N-grey halftone does not render with two
    ///         greys.</item>
    ///   <item><b>Symbol-dictionary refinement/aggregation (SDREFAGG=1)</b> decodes in both coding
    ///         modes: a one-instance symbol is a refinement (§6.5.8.2.2) and a larger one an
    ///         aggregate text region (§6.5.8.2.1). The arithmetic path shares its decoders across the
    ///         dictionary; the Huffman path shares its refinement contexts.</item>
    ///   <item><b>Pages of unknown height</b> (0xFFFFFFFF, §7.4.8.2) are sized by their end-of-stripe
    ///         rows (§7.4.10), grown as regions and stripes arrive and trimmed at the end; a region
    ///         reaching below the last stripe is clipped to it.</item>
    ///   <item><b>Bitmap coding contexts used and retained</b> (symbol dictionary flags bits 8 and 9,
    ///         §7.4.2.1.1): a dictionary can start its generic and refinement statistics from the
    ///         last referred dictionary that retained them.</item>
    ///   <item><b>The page-association field</b> is four bytes when the segment flags' bit 6 is set
    ///         (§7.2.4). It was tested at bit 7, and every segment after a four-byte association was
    ///         read from the wrong offset.</item>
    /// </list>
    ///
    /// <para><b>Output convention</b></para>
    /// Packed 1-bit-per-pixel bitmap, MSB-first, stride = ceil(columns/8), in <b>T.88's</b>
    /// polarity: <b>1 = black</b>. That is the codec's own convention and every region
    /// composition below depends on it (a page's default pixel value is 0 = white, and the
    /// OR/AND/XOR combination operators are defined over it).
    /// <para>It is the opposite of what a PDF image expects — DeviceGray 0 is black — so
    /// <see cref="Jbig2DecodeFilter"/> inverts at the PDF boundary. Do not invert here, or the
    /// region combination operators start compositing white onto white.</para>
    /// </summary>
    internal static class Jbig2Decoder
    {
        // ------------------------------------------------------------------
        //  Packed 1bpp bitmap (MSB-first, internal 1 = black)
        // ------------------------------------------------------------------
        internal sealed class Jbig2Bitmap
        {
            public int Width  { get; }
            public int Height { get; }
            public int Stride { get; }   // bytes per row
            public byte[] Data { get; }

            // Region/symbol/halftone dimensions come straight from the segment
            // headers and are otherwise unbounded. A tiny adversarial stream can
            // declare a 100000x100000 generic region whose arithmetic-decode
            // loop runs ~10^10 iterations (the MQ decoder never reports EOF) —
            // a pure CPU/memory DoS. Reject implausible dimensions here, the
            // single allocation choke point, so every region/symbol/halftone
            // path is covered and the filter's try/catch falls back to raw
            // passthrough. 1<<20 per side and 64M pixels comfortably exceed any
            // real scanned page while bounding the work.
            private const int  MaxSide   = 1 << 20;    // 1,048,576
            private const long MaxPixels = 1L << 26;   // 67,108,864

            // internal, not private: a caller about to do expensive work sized by an
            // attacker-controlled width/height (e.g. an MMR decode of a collective bitmap, before
            // any Jbig2Bitmap exists to check) can pre-validate against the same bound this
            // constructor enforces, rather than duplicating the two constants.
            internal static void ValidateDimensions(int width, int height)
            {
                if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide
                    || (long)width * height > MaxPixels)
                    throw new System.IO.InvalidDataException(
                        $"JBIG2 bitmap dimensions out of range: {width}x{height}.");
            }

            public Jbig2Bitmap(int width, int height)
            {
                ValidateDimensions(width, height);
                Width  = width;
                Height = height;
                Stride = (width + 7) / 8;
                Data   = new byte[Stride * Math.Max(1, height)];
            }

            public Jbig2Bitmap(int width, int height, byte[] existingPacked)
            {
                ValidateDimensions(width, height);
                Width  = width;
                Height = height;
                Stride = (width + 7) / 8;
                // Trust packed data size when it matches; otherwise copy defensively.
                if (existingPacked != null && existingPacked.Length == Stride * height)
                    Data = existingPacked;
                else
                {
                    Data = new byte[Stride * Math.Max(1, height)];
                    if (existingPacked != null)
                        Buffer.BlockCopy(existingPacked, 0, Data, 0,
                            Math.Min(existingPacked.Length, Data.Length));
                }
            }

            public int GetPixel(int x, int y)
            {
                if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
                return (Data[y * Stride + (x >> 3)] >> (7 - (x & 7))) & 1;
            }

            public void SetPixel(int x, int y, int value)
            {
                if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
                int idx  = y * Stride + (x >> 3);
                int mask = 0x80 >> (x & 7);
                if (value != 0) Data[idx] |= (byte)mask;
                else            Data[idx] &= (byte)~mask;
            }

            /// <summary>
            /// Draws <paramref name="src"/> onto this bitmap at (<paramref name="dstX"/>,
            /// <paramref name="dstY"/>) using the given combination operator (T.88 §7.4.2).
            /// Supported operators: 0 (OR), 1 (AND), 2 (XOR), 3 (XNOR), 4 (REPLACE).
            /// </summary>
            /// <param name="budget">
            /// Aggregate work budget for the whole decode. Charged the CLIPPED pixel count plus one
            /// unit per call; throws <see cref="WorkBudgetExceededException"/> when spent, which
            /// <c>Jbig2DecodeFilter</c> turns into a reported raw-byte fallback.
            /// </param>
            /// <param name="src">The bitmap to draw.</param>
            /// <param name="dstX">Destination x of <paramref name="src"/>'s left edge.</param>
            /// <param name="dstY">Destination y of <paramref name="src"/>'s top edge.</param>
            /// <param name="combinationOperator">
            /// T.88 §7.4.2 operator: 0 OR, 1 AND, 2 XOR, 3 XNOR, 4 REPLACE.
            /// </param>
            public void DrawAt(Jbig2Bitmap src, int dstX, int dstY, int combinationOperator,
                WorkBudget budget = null)
            {
                // Clip the SOURCE rectangle against this bitmap up front. Walking src.Width ×
                // src.Height unconditionally and bounds-checking every destination pixel, a symbol or
                // halftone cell landing entirely off-page would still cost its full area —
                // and the halftone grid multiplies that by the cell count. Computed in long: dstX
                // and dstY come straight from a segment header, so `Width - dstX` can overflow.
                long sx0 = Math.Max(0L, -(long)dstX);
                long sy0 = Math.Max(0L, -(long)dstY);
                long sx1 = Math.Min(src.Width,  (long)Width  - dstX);
                long sy1 = Math.Min(src.Height, (long)Height - dstY);
                long clippedW = Math.Max(0L, sx1 - sx0);
                long clippedH = Math.Max(0L, sy1 - sy0);

                // The +1 is not incidental: a grid of 64 Mi cells that all clip away is still
                // 64 Mi units of work, and charging only the drawn pixels would make it free.
                budget?.Tick(1 + clippedW * clippedH);
                if (clippedW == 0 || clippedH == 0) return;

                for (int sy = (int)sy0; sy < (int)sy1; sy++)
                {
                    int ty = dstY + sy;
                    for (int sx = (int)sx0; sx < (int)sx1; sx++)
                    {
                        int tx = dstX + sx;

                        int s = src.GetPixel(sx, sy);
                        int d = GetPixel(tx, ty);
                        int r = combinationOperator switch
                        {
                            0 => s | d,                 // OR
                            1 => s & d,                 // AND
                            2 => s ^ d,                 // XOR
                            3 => (~(s ^ d)) & 1,        // XNOR
                            _ => s,                     // REPLACE (op 4) and unknown
                        };
                        SetPixel(tx, ty, r);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        //  Bit-level reader (big-endian, MSB-first for individual bits)
        // ------------------------------------------------------------------
        // Byte-aligned reader (JBIG2 segment headers never split across bits).
        // Kept as a class so Position can be read directly and extended later
        // if we add bit-level arithmetic-decoder support.
        internal sealed class Jbig2BitReader
        {
            private readonly byte[] _data;
            private int _bytePos;

            public Jbig2BitReader(byte[] data) { _data = data ?? Array.Empty<byte>(); }

            public int BytePosition => _bytePos;
            public bool Eof => _bytePos >= _data.Length;
            public int RemainingBytes => Math.Max(0, _data.Length - _bytePos);

            public byte ReadByte()
            {
                if (_bytePos >= _data.Length) throw new EndOfStreamException();
                return _data[_bytePos++];
            }

            public uint ReadUInt32BE()
            {
                if (_bytePos + 4 > _data.Length) throw new EndOfStreamException();
                uint v = ((uint)_data[_bytePos] << 24) | ((uint)_data[_bytePos + 1] << 16)
                       | ((uint)_data[_bytePos + 2] << 8)  | _data[_bytePos + 3];
                _bytePos += 4;
                return v;
            }

            public ushort ReadUInt16BE()
            {
                if (_bytePos + 2 > _data.Length) throw new EndOfStreamException();
                ushort v = (ushort)((_data[_bytePos] << 8) | _data[_bytePos + 1]);
                _bytePos += 2;
                return v;
            }

            public void SkipBytes(int count)
            {
                // In long: a count near int.MaxValue wrapped `_bytePos + count` negative, and the
                // reader's position went negative with it.
                _bytePos = (int)Math.Min(_data.Length, (long)_bytePos + Math.Max(0, count));
            }

            /// <summary>
            /// Skips a segment body of a declared <paramref name="dataLength"/>, clamped to the data.
            /// A length of 2^31 or more cast to a negative int and skipped nothing, so the next
            /// "header" was read from inside the body — where <c>Decode</c> took the body to run to the
            /// end. Every walker skips through this now, and so agrees with it.
            /// </summary>
            public void SkipBody(uint dataLength) => SkipBytes((int)Math.Min(dataLength, int.MaxValue));

            public byte[] ReadBytes(int count)
            {
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                if (_bytePos + count > _data.Length)
                    count = Math.Max(0, _data.Length - _bytePos);
                var result = new byte[count];
                Buffer.BlockCopy(_data, _bytePos, result, 0, count);
                _bytePos += count;
                return result;
            }
        }

        // ------------------------------------------------------------------
        //  Segment header (T.88 §7.2)
        // ------------------------------------------------------------------
        internal sealed class Jbig2SegmentHeader
        {
            public uint   SegmentNumber         { get; set; }
            public byte   SegmentFlags          { get; set; }
            public int    SegmentType           => SegmentFlags & 0x3F;
            public bool   DeferredNonRetain     => (SegmentFlags & 0x40) != 0;
            public bool   PageAssociationIs32Bit => (SegmentFlags & 0x40) != 0;
            public uint[] ReferredToSegments    { get; set; } = Array.Empty<uint>();
            public uint   PageAssociation       { get; set; }
            public uint   DataLength            { get; set; }
            public int    HeaderLength          { get; set; }   // for bookkeeping

            public bool UnknownLength => DataLength == 0xFFFFFFFF;
        }

        /// <summary>
        /// Parses a single segment header at the reader's current position.
        /// Advances the reader to the first byte of the segment data.
        /// </summary>
        internal static Jbig2SegmentHeader ReadSegmentHeader(Jbig2BitReader br)
        {
            int start = br.BytePosition;
            var hdr = new Jbig2SegmentHeader
            {
                SegmentNumber = br.ReadUInt32BE(),
                SegmentFlags  = br.ReadByte(),
            };

            // Referred-to segment count (1-3 bytes, retention flags packed in).
            // Per T.88 §7.2.3: first byte's high 3 bits carry the count (0-4).
            // Count 7 → long form (next 4 bytes = count >> 29 ignored; 3 bytes of
            // retention flags follow).  We read the count, skip retention bits.
            byte rtr = br.ReadByte();
            int refCount;
            if ((rtr & 0xE0) != 0xE0)
            {
                refCount = (rtr >> 5) & 0x07;
                // Retention flags: 5 low bits cover segments 1..5 (bit 0 = this segment).
            }
            else
            {
                // Long form: read 3 more bytes giving the 29-bit count.
                byte b1 = br.ReadByte(), b2 = br.ReadByte(), b3 = br.ReadByte();
                refCount = (int)(((uint)(rtr & 0x1F) << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | b3);
                // Skip retention-flag bytes: ceil((refCount + 1) / 8) bytes.
                int retentionBytes = (refCount + 1 + 7) / 8;
                br.SkipBytes(retentionBytes);
            }

            // Referred-to segment numbers (each uses 1, 2, or 4 bytes depending on
            // this segment's own segment number — §7.2.5: "less than or equal to 256" one byte,
            // "less than or equal to 65536" two). Bounds of 255 and 65535, as some readers have them,
            // would read segment 256's references as two-byte and misparse every header after it.
            int refSize = ReferredToSegmentNumberSize(hdr.SegmentNumber);

            // The long form's count is 29 bits, and it was allocated as read: up to 2 GB of
            // references before the first read could fail. Each one takes refSize bytes, so the
            // bytes that remain bound the count that can be real.
            if (refCount > br.RemainingBytes / refSize)
                throw new InvalidDataException(
                    $"JBIG2 segment {hdr.SegmentNumber}: {refCount} referred-to segments do not fit in the {br.RemainingBytes} bytes left.");
            hdr.ReferredToSegments = new uint[refCount];
            for (int i = 0; i < refCount; i++)
            {
                if (refSize == 1)      hdr.ReferredToSegments[i] = br.ReadByte();
                else if (refSize == 2) hdr.ReferredToSegments[i] = br.ReadUInt16BE();
                else                   hdr.ReferredToSegments[i] = br.ReadUInt32BE();
            }

            // Page association: 1 byte by default, 4 if the flag bit is set.
            hdr.PageAssociation = hdr.PageAssociationIs32Bit
                ? br.ReadUInt32BE()
                : br.ReadByte();

            hdr.DataLength = br.ReadUInt32BE();
            hdr.HeaderLength = br.BytePosition - start;
            return hdr;
        }

        /// <summary>
        /// Bytes per referred-to segment number for a segment numbered <paramref name="segmentNumber"/>
        /// (T.88 §7.2.5): one up to 256, two up to 65536, four beyond.
        /// </summary>
        internal static int ReferredToSegmentNumberSize(uint segmentNumber)
            => segmentNumber <= 256 ? 1 : segmentNumber <= 65536 ? 2 : 4;

        // ------------------------------------------------------------------
        //  Region segment info header (T.88 §7.4.1)
        // ------------------------------------------------------------------
        internal struct Jbig2RegionInfo
        {
            public uint Width;
            public uint Height;
            public uint X;
            public uint Y;
            public byte Flags;             // low 3 bits = combination operator
            public int  CombinationOperator => Flags & 0x07;
        }

        private static Jbig2RegionInfo ReadRegionInfo(Jbig2BitReader br)
        {
            return new Jbig2RegionInfo
            {
                Width  = br.ReadUInt32BE(),
                Height = br.ReadUInt32BE(),
                X      = br.ReadUInt32BE(),
                Y      = br.ReadUInt32BE(),
                Flags  = br.ReadByte(),
            };
        }

        // ------------------------------------------------------------------
        //  Segment type constants (T.88 §7.3)
        // ------------------------------------------------------------------
        private const int SEG_SYMBOL_DICT             = 0;
        private const int SEG_TEXT_REGION_INTERMED    = 4;
        private const int SEG_TEXT_REGION_IMMEDIATE   = 6;
        private const int SEG_TEXT_REGION_LOSSLESS    = 7;
        private const int SEG_PATTERN_DICT            = 16;
        private const int SEG_HALFTONE_REGION_INTER   = 20;
        private const int SEG_HALFTONE_REGION_IMMED   = 22;
        private const int SEG_HALFTONE_REGION_LOSSLESS= 23;
        private const int SEG_GENERIC_REGION_INTER    = 36;
        private const int SEG_GENERIC_REGION_IMMED    = 38;
        private const int SEG_GENERIC_REGION_LOSSLESS = 39;
        private const int SEG_REFINE_REGION_INTER     = 40;
        private const int SEG_REFINE_REGION_IMMED     = 42;
        private const int SEG_REFINE_REGION_LOSSLESS  = 43;
        private const int SEG_PAGE_INFO               = 48;
        private const int SEG_END_OF_PAGE             = 49;
        private const int SEG_END_OF_STRIPE           = 50;
        private const int SEG_END_OF_FILE             = 51;
        private const int SEG_PROFILES                = 52;
        private const int SEG_TABLES                  = 53;
        private const int SEG_EXTENSION               = 62;

        // ------------------------------------------------------------------
        //  Public entry point
        // ------------------------------------------------------------------

        /// <summary>
        /// Decodes a JBIG2 bitstream into a packed 1bpp bitmap, MSB-first, in <b>T.88's own
        /// polarity: 1 = black</b>. Throws on streams we can't handle — the filter wrapper catches
        /// and falls back to raw bytes.
        /// </summary>
        /// <remarks>
        /// <b>This is NOT the PDF sample convention.</b> A 1-bpc DeviceGray sample of 0 is black under the
        /// default <c>/Decode [0 1]</c> (§8.9.5.2), and an <c>/ImageMask</c> paints where the sample
        /// is <b>0</b> (§8.9.6.2) — the opposite. T.88's polarity is kept here because the region
        /// combination operators (OR/AND/XOR onto a page whose default pixel is 0) are defined over
        /// it; <see cref="Jbig2DecodeFilter"/> inverts once, at the PDF boundary. That wrong
        /// attribution is what made the inversion look already done, and every JBIG2 image rendered
        /// in negative for as long as it stood.
        /// </remarks>
        /// <param name="data">The page-specific segment stream from the PDF /Filter chain.</param>
        /// <param name="globals">Optional /JBIG2Globals stream; null when absent.</param>
        /// <param name="pageWidth">Output: decoded page width in pixels.</param>
        /// <param name="pageHeight">Output: decoded page height in pixels.</param>
        internal static byte[] Decode(byte[] data, byte[] globals,
            out int pageWidth, out int pageHeight)
            => Decode(data, globals, null, out pageWidth, out pageHeight);

        /// <summary>
        /// <see cref="Decode(byte[], byte[], out int, out int)"/> whose work budget also spends
        /// <paramref name="parentBudget"/> — the page's image-decoding budget — so a page of streams
        /// that each spend their whole composition budget is bounded as a page (GAPS.md §7c).
        /// </summary>
        internal static byte[] Decode(byte[] data, byte[] globals, WorkBudget parentBudget,
            out int pageWidth, out int pageHeight)
        {
            pageWidth = 0;
            pageHeight = 0;

            // Drop the optional 9-byte JBIG2 "sequential organisation" file header
            // ("\x97\x4A\x42\x32\x0D\x0A\x1A\x0A" + 1 flags byte).  PDF embeddings
            // usually OMIT this header and emit raw segments — so only skip when
            // the magic is present.
            data    = StripFileHeader(data);
            globals = StripFileHeader(globals);

            // Concatenate globals + data so referenced segments resolve in one pass.
            byte[] concatenated = CombineWithGlobals(data, globals);

            // ONE budget for the whole decode. Jbig2Bitmap's dimension check bounds a single
            // ALLOCATION; it does not bound the COMPOSITION, where cell count multiplies pattern area and instance count
            // multiplies symbol area, both across segments that individually pass validation.
            var budget = new WorkBudget(PdfLimits.MaxJbig2CompositionPixels, "JBIG2 decode", false, parentBudget);
            LastWorkUnits = 0;

            var br = new Jbig2BitReader(concatenated);
            Jbig2Bitmap pageBitmap = null;
            // 7.4.8: a page of unknown height (0xFFFFFFFF) is as tall as its end-of-stripe rows say.
            // It grows as regions and stripes arrive, and is trimmed to KnownRows at the end.
            bool unknownHeight = false, defaultOnes = false;
            int knownRows = 0;   // the end-of-stripe rows: the height, when there are any
            int regionRows = 0;  // the lowest row a region reaches: room for it, not the height
            var symbolDicts   = new Dictionary<uint, Jbig2Bitmap[]>();
            var retainedContexts = new Dictionary<uint, Jbig2RetainedContexts>();
            var patternDicts  = new Dictionary<uint, Jbig2Bitmap[]>();
            var regionBitmaps = new Dictionary<uint, Jbig2Bitmap>();       // for refinement refs
            var customTables  = new Dictionary<uint, Jbig2HuffmanTable>(); // segment-type-53 tables

            while (!br.Eof)
            {
                Jbig2SegmentHeader hdr;
                try { hdr = ReadSegmentHeader(br); }
                catch (EndOfStreamException) { break; }

                int bodyStart = br.BytePosition;
                int bodyEnd;
                if (hdr.UnknownLength)
                {
                    // T.88 §7.2.7. One implementation — the walking callers ask the same question. Marker bytes stay OUTSIDE
                    // bodyEnd; they are consumed after the switch below.
                    TryFindUnknownLengthEnd(concatenated, bodyStart, out bodyEnd);
                }
                else
                {
                    // In long, clamped to the data: (int) of a length of 2^31 or more was negative.
                    bodyEnd = (int)Math.Min((long)bodyStart + hdr.DataLength, concatenated.Length);
                }

                switch (hdr.SegmentType)
                {
                    case SEG_PAGE_INFO:
                        pageBitmap = HandlePageInfo(br, hdr, budget, out unknownHeight, out defaultOnes);
                        pageWidth  = pageBitmap.Width;
                        pageHeight = pageBitmap.Height;
                        break;

                    case SEG_END_OF_STRIPE:
                    {
                        // 7.4.10: the row the stripe ends on. On a page of unknown height that row
                        // is what sets the height, so the page grows to hold it.
                        var body = br.ReadBytes(bodyEnd - br.BytePosition);
                        if (unknownHeight && body.Length >= 4 && pageBitmap != null)
                        {
                            long row = ((long)body[0] << 24) | ((long)body[1] << 16) | ((long)body[2] << 8) | body[3];
                            knownRows = (int)Math.Max(knownRows, Math.Min(row + 1, int.MaxValue));
                            pageBitmap = GrowUnknownPage(pageBitmap, Math.Max(knownRows, regionRows), defaultOnes, budget);
                        }
                        break;
                    }

                    case SEG_GENERIC_REGION_IMMED:
                    case SEG_GENERIC_REGION_LOSSLESS:
                    {
                        if (pageBitmap == null)
                            throw new InvalidDataException("JBIG2: generic region before page info");
                        var region = DecodeGenericRegion(br, bodyEnd, budget, out var info);
                        if (unknownHeight)
                        {
                            regionRows = Math.Max(regionRows, (int)info.Y + region.Height);
                            pageBitmap = GrowUnknownPage(pageBitmap, Math.Max(knownRows, regionRows), defaultOnes, budget);
                        }
                        pageBitmap.DrawAt(region, (int)info.X, (int)info.Y, info.CombinationOperator, budget);
                        break;
                    }

                    // An intermediate region is decoded and kept rather than drawn: its one use is as
                    // the reference of a later refinement region that refers to it (T.88 §7.4.7.4).
                    case SEG_GENERIC_REGION_INTER:
                        regionBitmaps[hdr.SegmentNumber] = DecodeGenericRegion(br, bodyEnd, budget, out _);
                        break;

                    case SEG_SYMBOL_DICT:
                    {
                        var body = br.ReadBytes(bodyEnd - br.BytePosition);
                        // Collect referred-to symbol dicts into a flat list.
                        var referred = new List<Jbig2Bitmap>();
                        foreach (uint refNum in hdr.ReferredToSegments)
                            if (symbolDicts.TryGetValue(refNum, out var refArr))
                                referred.AddRange(refArr);
                        try
                        {
                            var decoded = DecodeSymbolDictionary(body, referred, budget,
                                customTables, hdr.ReferredToSegments,
                                LastRetainedContexts(hdr.ReferredToSegments, retainedContexts), out var retained);
                            symbolDicts[hdr.SegmentNumber] = decoded;
                            if (retained != null) retainedContexts[hdr.SegmentNumber] = retained;
                        }
                        catch (NotSupportedException)
                        {
                            // Huffman or other unsupported symbol dict variant — rethrow
                            // so the top-level filter falls back to raw bytes.
                            throw;
                        }
                        break;
                    }

                    case SEG_TEXT_REGION_IMMEDIATE:
                    case SEG_TEXT_REGION_LOSSLESS:
                    {
                        if (pageBitmap == null)
                            throw new InvalidDataException("JBIG2: text region before page info");
                        // Text region body includes region info + text-region flags;
                        // pass the whole body to DecodeTextRegion.
                        var body = br.ReadBytes(bodyEnd - br.BytePosition);
                        // Region X/Y come from region-info header (first 17 bytes of body).
                        int rx = (body[8] << 24) | (body[9] << 16) | (body[10] << 8) | body[11];
                        int ry = (body[12] << 24) | (body[13] << 16) | (body[14] << 8) | body[15];
                        int combOp = body[16] & 0x07;
                        if (unknownHeight)
                        {
                            regionRows = Math.Max(regionRows, ry + RegionInfoHeight(body));
                            pageBitmap = GrowUnknownPage(pageBitmap, Math.Max(knownRows, regionRows), defaultOnes, budget);
                        }
                        DecodeTextRegion(body, pageBitmap, rx, ry, combOp,
                            symbolDicts, hdr.ReferredToSegments, budget, customTables);
                        break;
                    }

                    case SEG_PATTERN_DICT:
                    {
                        var body = br.ReadBytes(bodyEnd - br.BytePosition);
                        try
                        {
                            var patterns = DecodePatternDictionary(body, budget);
                            patternDicts[hdr.SegmentNumber] = patterns;
                        }
                        catch (NotSupportedException) { throw; }
                        break;
                    }

                    case SEG_TABLES:
                    {
                        // Parse user-defined Huffman table for later reference.
                        var body = br.ReadBytes(bodyEnd - br.BytePosition);
                        var table = ParseUserDefinedHuffmanTable(body);
                        if (table != null)
                            customTables[hdr.SegmentNumber] = table;
                        // Parse failure just skips the table — downstream segments
                        // fall back to standard tables.
                        break;
                    }

                    case SEG_HALFTONE_REGION_IMMED:
                    case SEG_HALFTONE_REGION_LOSSLESS:
                    {
                        if (pageBitmap == null)
                            throw new InvalidDataException("JBIG2: halftone region before page info");
                        // Find the referenced pattern dictionary.
                        Jbig2Bitmap[] patterns = null;
                        foreach (uint refNum in hdr.ReferredToSegments)
                            if (patternDicts.TryGetValue(refNum, out var arr))
                                { patterns = arr; break; }
                        if (patterns == null)
                            throw new InvalidDataException("JBIG2: halftone region missing pattern dictionary");

                        var body = br.ReadBytes(bodyEnd - br.BytePosition);
                        int rx = (body[8] << 24) | (body[9] << 16) | (body[10] << 8) | body[11];
                        int ry = (body[12] << 24) | (body[13] << 16) | (body[14] << 8) | body[15];
                        int combOp = body[16] & 0x07;
                        if (unknownHeight)
                        {
                            regionRows = Math.Max(regionRows, ry + RegionInfoHeight(body));
                            pageBitmap = GrowUnknownPage(pageBitmap, Math.Max(knownRows, regionRows), defaultOnes, budget);
                        }
                        DecodeHalftoneRegion(body, pageBitmap, rx, ry, combOp, patterns, budget);
                        break;
                    }

                    case SEG_REFINE_REGION_IMMED:
                    case SEG_REFINE_REGION_LOSSLESS:
                    case SEG_REFINE_REGION_INTER:
                    {
                        if (pageBitmap == null)
                            throw new InvalidDataException("JBIG2: refinement region before page info");
                        // Find the referenced region bitmap (the "reference" for refinement).
                        Jbig2Bitmap reference = null;
                        foreach (uint refNum in hdr.ReferredToSegments)
                            if (regionBitmaps.TryGetValue(refNum, out var bmp))
                                { reference = bmp; break; }

                        var body = br.ReadBytes(bodyEnd - br.BytePosition);
                        var refined = DecodeRefinementSegment(body, pageBitmap, reference,
                            hdr.ReferredToSegments.Length > 0, budget, out var info);
                        if (hdr.SegmentType == SEG_REFINE_REGION_INTER)
                            regionBitmaps[hdr.SegmentNumber] = refined;
                        else
                        {
                            if (unknownHeight)
                            {
                                regionRows = Math.Max(regionRows, (int)info.Y + refined.Height);
                                pageBitmap = GrowUnknownPage(pageBitmap, Math.Max(knownRows, regionRows), defaultOnes, budget);
                            }
                            pageBitmap.DrawAt(refined, (int)info.X, (int)info.Y, info.CombinationOperator, budget);
                        }
                        break;
                    }

                    case SEG_END_OF_PAGE:
                    case SEG_END_OF_FILE:
                        // Nothing to decode; just skip any body bytes.
                        br.SkipBytes(bodyEnd - br.BytePosition);
                        break;

                    default:
                        // Intermediate regions, extensions, profiles, tables — skip.
                        // Immediate region types we don't support throw → filter falls back.
                        br.SkipBytes(bodyEnd - br.BytePosition);
                        if (IsRegionSegmentWeCantDecode(hdr.SegmentType))
                            throw new NotSupportedException(
                                $"JBIG2: segment type {hdr.SegmentType} not supported by this decoder");
                        break;
                }

                // Consume the FF AC end marker for unknown-length segments.
                if (hdr.UnknownLength && br.BytePosition + 1 < concatenated.Length
                    && concatenated[br.BytePosition] == 0xFF
                    && concatenated[br.BytePosition + 1] == 0xAC)
                {
                    br.SkipBytes(2);
                }
            }

            if (pageBitmap == null)
                throw new InvalidDataException("JBIG2: no page information segment found");

            if (unknownHeight)
            {
                // The page is as tall as its end-of-stripe rows say; with none, as its regions reach.
                int finalRows = knownRows > 0 ? knownRows : regionRows;
                if (finalRows == 0) { LastWorkUnits = budget.Spent; return Array.Empty<byte>(); }
                pageBitmap = TrimToRows(pageBitmap, finalRows);
                pageHeight = finalRows;
            }
            LastWorkUnits = budget.Spent;
            return pageBitmap.Data;
        }

        private static bool IsRegionSegmentWeCantDecode(int type) => type switch
        {
            // Every region type is attempted — the top-level filter catches
            // exceptions and falls back to raw bytes. Returning true here is for a
            // type explicitly NOT handled even via the try-path.
            _ => false,
        };

        /// <summary>
        /// Where an unknown-length segment's body ends — T.88 §7.2.7's <c>FF AC</c> terminator.
        /// Returns <c>true</c> when the marker was found, with <paramref name="bodyEnd"/> at the
        /// marker; <c>false</c> when the stream carries none, with <paramref name="bodyEnd"/> at
        /// the end of the data.
        /// </summary>
        /// <remarks>
        /// <para><b>Four walks answered this question and three of them disagreed.</b>
        /// <c>Decode</c> scanned and, finding no marker, kept decoding the remainder;
        /// <c>EnumerateSegments</c> and <c>Diagnose</c> scanned and stopped, dropping every later
        /// segment; <c>Assess</c>'s own second walk did not scan at all, so an unknown-length
        /// segment before the page-info segment made it report <b>page 0×0</b> for a stream whose
        /// page-info segment the <c>Diagnose</c> call two lines above had just listed. Three answers
        /// about where one segment ends, inside one API result.</para>
        ///
        /// <para><b>The decoder's answer is the one that wins, because `Diagnose` documents itself
        /// as "what the decoder would do with every segment".</b> A diagnostic that stops where the
        /// decoder continues is describing a different program.</para>
        ///
        /// <para><b>The marker bytes are NOT part of the body</b>, which is why the caller advances
        /// separately: <c>Decode</c> skips to <paramref name="bodyEnd"/> and then consumes the two
        /// marker bytes after its switch, while the walking callers fold the <c>+ 2</c> into one
        /// skip. Returning a body extent that included the marker would silently grow every
        /// unknown-length body by two bytes.</para>
        /// </remarks>
        private static bool TryFindUnknownLengthEnd(byte[] data, int bodyStart, out int bodyEnd)
        {
            if (data != null)
            {
                for (int scan = bodyStart; scan + 1 < data.Length; scan++)
                {
                    if (data[scan] == 0xFF && data[scan + 1] == 0xAC)
                    {
                        bodyEnd = scan;
                        return true;
                    }
                }
            }

            bodyEnd = data?.Length ?? 0;
            return false;
        }

        /// <summary>
        /// An optional <c>/JBIG2Globals</c> stream concatenated in front of the page data, so a
        /// segment referring to a global one resolves in a single pass.
        /// </summary>
        /// <remarks>
        /// Three copies of this existed and they were not identical: <c>Assess</c>'s and
        /// <c>Diagnose</c>'s null-guarded both arrays, <c>Decode</c>'s did not. The null-tolerant
        /// form is the one kept — it is the weaker precondition, so no caller loses a guarantee.
        /// </remarks>
        private static byte[] CombineWithGlobals(byte[] data, byte[] globals)
        {
            if (globals == null || globals.Length == 0) return data ?? Array.Empty<byte>();

            var combined = new byte[globals.Length + (data?.Length ?? 0)];
            Buffer.BlockCopy(globals, 0, combined, 0, globals.Length);
            if (data != null) Buffer.BlockCopy(data, 0, combined, globals.Length, data.Length);
            return combined;
        }

        private static byte[] StripFileHeader(byte[] data)
        {
            if (data == null || data.Length < 9) return data;
            // File header magic: 97 4A 42 32 0D 0A 1A 0A
            if (data[0] == 0x97 && data[1] == 0x4A && data[2] == 0x42 && data[3] == 0x32
                && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            {
                byte flags = data[8];
                // Bit 0 = sequential (1) vs random access (0).  Bit 1 = number of
                // pages unknown.  If number-of-pages is known there's 4 more bytes.
                int skip = 9;
                if ((flags & 0x02) == 0) skip += 4;
                if (skip >= data.Length) return Array.Empty<byte>();
                var body = new byte[data.Length - skip];
                Buffer.BlockCopy(data, skip, body, 0, body.Length);
                return body;
            }
            return data;
        }

        // ------------------------------------------------------------------
        //  Page-info segment (T.88 §7.4.8)
        // ------------------------------------------------------------------
        private static Jbig2Bitmap HandlePageInfo(Jbig2BitReader br, Jbig2SegmentHeader hdr, WorkBudget budget,
            out bool unknownHeight, out bool defaultOnes)
        {
            // Body: width(4) + height(4) + xres(4) + yres(4) + flags(1) + stripingInfo(2) = 19 bytes.
            uint w = br.ReadUInt32BE();
            uint h = br.ReadUInt32BE();
            /* xres */ br.ReadUInt32BE();
            /* yres */ br.ReadUInt32BE();
            byte pageFlags = br.ReadByte();
            ushort striping = br.ReadUInt16BE();

            // 7.4.8.2: a height of 0xFFFFFFFF means the page's height is set by its end-of-stripe
            // segments. Start from the stripe size (bits 0-14, when bit 15 says the page is striped),
            // and let the decoder grow the page as stripes and regions arrive.
            unknownHeight = h == 0xFFFFFFFF;
            defaultOnes = (pageFlags & 0x04) != 0;
            int initialRows = unknownHeight
                ? Math.Max(1, (striping & 0x8000) != 0 ? striping & 0x7FFF : 1)
                : (int)h;

            // T.88 §7.4.8.5 bit 2 — the page's DEFAULT PIXEL VALUE. A new bitmap is always
            // all-zero, so ignoring the flag would start a page declared black white. That is not a cosmetic difference: a region composited onto it with
            // AND (combination operator 1) to punch white text out of a black field computes
            // `s & d` against d = 0 everywhere, so the whole page decodes to 0, the filter inverts
            // it, and the reader draws solid black instead of black text on white.
            return RegionBitmap((int)w, initialRows, defaultOnes, budget);
        }

        /// <summary>
        /// Grows a page of unknown height so that it holds rows [0, bottom). The page is allocated with
        /// room to spare, since it grows once per stripe, and is trimmed to its final height at the end.
        /// A new row is the page's default pixel.
        /// </summary>
        private static Jbig2Bitmap GrowUnknownPage(Jbig2Bitmap page, int bottom, bool defaultOnes, WorkBudget budget)
        {
            if (bottom <= page.Height) return page;
            int newHeight = Math.Max(bottom, page.Height + page.Height / 2 + 64);
            var grown = RegionBitmap(page.Width, newHeight, defaultOnes, budget);
            Array.Copy(page.Data, grown.Data, page.Data.Length);
            return grown;
        }

        /// <summary>
        /// A page's or a region's own bitmap (§7.4.8, §6.4.5, §6.6.5), filled with its default pixel,
        /// its allocation charged before it is made at <see cref="RegionBitmapPixelsPerUnit"/>: a text
        /// region of no instances or a halftone region whose cells all fall outside it draws nothing,
        /// and a stream of them, or of page-information segments, declared at the bitmap ceiling would
        /// otherwise allocate and fill eight megabytes a segment of some thirty bytes for a unit or two.
        /// </summary>
        private static Jbig2Bitmap RegionBitmap(int width, int height, bool defaultOnes, WorkBudget budget)
        {
            Jbig2Bitmap.ValidateDimensions(width, height);
            budget?.Tick(1 + (long)width * height / RegionBitmapPixelsPerUnit);
            var bitmap = new Jbig2Bitmap(width, height);
            if (defaultOnes) FillWithOnes(bitmap);
            return bitmap;
        }

        /// <summary>The first rows of a page, as a page of that height.</summary>
        private static Jbig2Bitmap TrimToRows(Jbig2Bitmap page, int rows)
        {
            var trimmed = new Jbig2Bitmap(page.Width, rows);
            Array.Copy(page.Data, trimmed.Data, trimmed.Data.Length);
            return trimmed;
        }

        /// <summary>The height field of a region segment's region-information header (7.4.1), read from its body.</summary>
        private static int RegionInfoHeight(byte[] body)
            => (body[4] << 24) | (body[5] << 16) | (body[6] << 8) | body[7];

        /// <summary>Sets every pixel of <paramref name="bitmap"/> to 1 — a page or region whose default
        /// pixel is black.</summary>
        private static void FillWithOnes(Jbig2Bitmap bitmap)
        {
            for (int i = 0; i < bitmap.Data.Length; i++) bitmap.Data[i] = 0xFF;
            // Mask the padding bits past Width in each row. GetPixel bounds-checks x, so they
            // are unreachable through it — but DrawAt and the filter's final inversion read
            // whole bytes, and a set padding bit would surface as ink past the page edge.
            int usedBitsInLastByte = bitmap.Width & 7;
            if (usedBitsInLastByte != 0)
            {
                byte lastMask = (byte)(0xFF << (8 - usedBitsInLastByte));
                for (int y = 0; y < bitmap.Height; y++)
                    bitmap.Data[y * bitmap.Stride + bitmap.Stride - 1] &= lastMask;
            }
        }

        // ------------------------------------------------------------------
        //  Immediate generic region (T.88 §7.4.6) — MMR path only
        // ------------------------------------------------------------------
        private static Jbig2Bitmap DecodeGenericRegion(Jbig2BitReader br, int bodyEnd,
            WorkBudget budget, out Jbig2RegionInfo info)
        {
            info = ReadRegionInfo(br);
            byte grFlags = br.ReadByte();

            bool mmr       = (grFlags & 0x01) != 0;
            int  gbtemplate = (grFlags >> 1) & 0x03;
            bool tpgdon     = (grFlags & 0x08) != 0;

            if (!mmr)
            {
                // Arithmetic-coded generic region (T.88 §6.2).  Template 0 uses
                // 4 AT pixels (8 bytes); templates 1-3 use 1 AT pixel (2 bytes).
                int atByteCount = gbtemplate == 0 ? 8 : 2;
                var atBytes = new sbyte[atByteCount];
                for (int i = 0; i < atByteCount; i++)
                    atBytes[i] = (sbyte)br.ReadByte();

                int remBytes = bodyEnd - br.BytePosition;
                byte[] arithData = br.ReadBytes(remBytes);

                return DecodeArithmeticGenericRegion(
                    arithData, (int)info.Width, (int)info.Height,
                    gbtemplate, tpgdon, atBytes, budget);
            }

            // MMR path: the rest of this segment's body is T.6-encoded bitmap data
            // without EOFB.  Reuse the existing CCITT decoder and pass /Rows=Height
            // with endOfBlock=false so it stops after exactly Height lines.
            //
            // The region's own width and height are hostile input, and the T.6 decoder is sized by
            // them before it reads a bit: a reference line and a current line of one byte per column,
            // and a scan of the reference line per code. The arithmetic path above charges the
            // region's pixels to the budget inside DecodeGenericRegion. Handed the raw header values
            // straight, the decoder would let a 701-byte file declaring a region 2^30 wide allocate
            // two gigabyte lines and scan them for seconds — and a width past int would wrap negative
            // in the cast. The dimensions are validated and the pixels charged here, as every other
            // region's are, before the decoder is sized.
            Jbig2Bitmap.ValidateDimensions((int)info.Width, (int)info.Height);
            budget?.Tick((long)info.Width * info.Height);
            int remaining = bodyEnd - br.BytePosition;
            byte[] encoded = br.ReadBytes(remaining);

            byte[] decoded = CcittFaxDecoder.DecodeGroup4(
                encoded, (int)info.Width, (int)info.Height,
                endOfBlock: false, out _);

            // CcittFaxDecoder returns "1 = black" packed, which is the same
            // convention JBIG2 uses internally.  No inversion needed.
            return new Jbig2Bitmap((int)info.Width, (int)info.Height, decoded);
        }

        // ==================================================================
        //  Arithmetic-coded generic region decoder — template 0 (T.88 §6.2.5.3)
        //
        //  Template 0 context is a 16-bit value packing 16 reference pixels.
        //  The bit positions below are taken from T.88 Figure 11 / Table 6.
        //  Bit numbering: bit 15 = MSB of the context, bit 0 = LSB.
        //
        //     Row y-2:        . X14 X13 X12 X11 .
        //     Row y-1: . X10  X9  X8  X7  X6  X5  X4_AT
        //     Row y:   X3 X2 X1  X0_AT  [P]
        //
        //  (AT denotes adaptive-template pixels whose positions can be
        //   overridden per-segment via the AT bytes in the generic-region
        //   body.  Defaults per T.88 §6.2.5.3 for template 0:
        //     AT1 = (dx=+3,dy=-1), AT2 = (-3,-1), AT3 = (+2,-2), AT4 = (-2,-2))
        //
        //  The code below builds the 16-bit context per pixel from these
        //  reference positions, then asks the MQ decoder for the next bit.
        //  If you find misaligned output against a reference decoder, start
        //  with the Template0RefPositions table — each entry's comment gives
        //  its Figure 11 bit position so corrections land in one spot.
        //
        //  TPGDON (Typical Prediction for Generic Decoding ON) — when the
        //  segment sets this flag, before decoding each row we query a
        //  pseudo-context (T.88 §6.2.5.7 gives 0x9B25 for template 0) to get
        //  an "LTP" (Line Typical Prediction) bit.  The actual LTP state
        //  toggles on each '1' from the decoder; a true LTP means the row is
        //  identical to the previous row so we skip per-pixel decoding.
        // ==================================================================

        // Reference pixel positions for each GBTEMPLATE (dy, dx).  Bit position
        // in the packed context runs from MSB (first entry) down to LSB (last
        // entry).  AT slots are substituted at runtime from the segment header.
        // Positions per T.88 Figure 11 (template 0) / Figure 12 (template 1) /
        // Figure 13 (template 2) / Figure 14 (template 3).

        // Template 0: 16 pixels, 2^16 = 65536 contexts, 4 AT pixels.
        // Layout per T.88 §6.2.5.3 Table 6 / Figure 11 (MSB = index 0):
        //   Row y-2: AT4(x-2), x-1, x, x+1, AT3(x+2)           [indices 0-4]
        //   Row y-1: AT2(x-3), x-2, x-1, x, x+1, x+2, AT1(x+3) [indices 5-11]
        //   Row y:   x-4, x-3, x-2, x-1                         [indices 12-15]
        // AT array indices (used directly in positions[]): AT1=11, AT2=5, AT3=4, AT4=0.
        /// <summary>Template 0's ten context positions, in bit order.</summary>
        /// <remarks>
        /// <b>Internal rather than private because <c>Jbig2EncodeFilter</c> forms the same
        /// context.</b> Encoder and decoder must agree on which neighbours make the context
        /// and in what bit order; two copies would agree until one was corrected, and the
        /// failure mode is a stream only this library can read. With the default AT pixels
        /// this table IS the encoder's.
        /// </remarks>
        internal static readonly (int dy, int dx)[] Template0RefPositions =
        {
            (-2, -2),  // bit 15, index  0 — AT4 slot (default dx=-2, dy=-2)
            (-2, -1),  // bit 14, index  1
            (-2,  0),  // bit 13, index  2
            (-2,  1),  // bit 12, index  3
            (-2,  2),  // bit 11, index  4 — AT3 slot (default dx=+2, dy=-2)
            (-1, -3),  // bit 10, index  5 — AT2 slot (default dx=-3, dy=-1)
            (-1, -2),  // bit  9, index  6
            (-1, -1),  // bit  8, index  7
            (-1,  0),  // bit  7, index  8
            (-1,  1),  // bit  6, index  9
            (-1,  2),  // bit  5, index 10
            (-1,  3),  // bit  4, index 11 — AT1 slot (default dx=+3, dy=-1)
            ( 0, -4),  // bit  3, index 12
            ( 0, -3),  // bit  2, index 13
            ( 0, -2),  // bit  1, index 14
            ( 0, -1),  // bit  0, index 15
        };

        // Template 1: 13 pixels, 2^13 = 8192 contexts, single AT.
        // Layout per T.88 §6.2.5.4 (MSB = index 0):
        //   Row y-2: x-1, x, x+1, x+2              [indices 0-3]
        //   Row y-1: x-2, x-1, x, x+1, x+2, AT1    [indices 4-9]
        //   Row y:   x-3, x-2, x-1                  [indices 10-12]
        // AT1 array index: 9.
        internal static readonly (int dy, int dx)[] Template1RefPositions =
        {
            (-2, -1),  // bit 12, index  0
            (-2,  0),  // bit 11, index  1
            (-2,  1),  // bit 10, index  2
            (-2,  2),  // bit  9, index  3
            (-1, -2),  // bit  8, index  4
            (-1, -1),  // bit  7, index  5
            (-1,  0),  // bit  6, index  6
            (-1,  1),  // bit  5, index  7
            (-1,  2),  // bit  4, index  8
            (-1,  3),  // bit  3, index  9 — AT1 slot (default dx=+3, dy=-1)
            ( 0, -3),  // bit  2, index 10
            ( 0, -2),  // bit  1, index 11
            ( 0, -1),  // bit  0, index 12
        };

        // Template 2: 10 pixels, 2^10 = 1024 contexts, single AT.
        // Layout per T.88 §6.2.5.5 (MSB = index 0):
        //   Row y-2: x-1, x, x+1            [indices 0-2]
        //   Row y-1: x-2, x-1, x, x+1, AT1 [indices 3-7]
        //   Row y:   x-2, x-1               [indices 8-9]
        // AT1 array index: 7.
        internal static readonly (int dy, int dx)[] Template2RefPositions =
        {
            (-2, -1),  // bit 9, index 0
            (-2,  0),  // bit 8, index 1
            (-2,  1),  // bit 7, index 2
            (-1, -2),  // bit 6, index 3
            (-1, -1),  // bit 5, index 4
            (-1,  0),  // bit 4, index 5
            (-1,  1),  // bit 3, index 6
            (-1,  2),  // bit 2, index 7 — AT1 slot (default dx=+2, dy=-1)
            ( 0, -2),  // bit 1, index 8
            ( 0, -1),  // bit 0, index 9
        };

        // Template 3: 10 pixels, 2^10 = 1024 contexts, single AT.
        // Layout per T.88 Figure 6 (MSB = index 0):
        //   Row y-1: x-3, x-2, x-1, x, x+1, AT1  [indices 0-5]
        //   Row y:   x-4, x-3, x-2, x-1          [indices 6-9]
        // AT1 array index: 5.
        // Row y-1 was x-2 .. x+2 with the AT defaulting to x+3: shifted one place right of the
        // figure. With the usual AT bytes (2, -1) that read x+2 twice and x-3 never, so every
        // template-3 generic region decoded wrong. Independent decoders read x-3 .. x+1.
        internal static readonly (int dy, int dx)[] Template3RefPositions =
        {
            (-1, -3),  // bit 9, index 0
            (-1, -2),  // bit 8, index 1
            (-1, -1),  // bit 7, index 2
            (-1,  0),  // bit 6, index 3
            (-1,  1),  // bit 5, index 4
            (-1,  2),  // bit 4, index 5 — AT1 slot (default dx=+2, dy=-1)
            ( 0, -4),  // bit 3, index 6
            ( 0, -3),  // bit 2, index 7
            ( 0, -2),  // bit 1, index 8
            ( 0, -1),  // bit 0, index 9
        };

        /// <summary>
        /// Generic arithmetic-region decoder supporting templates 0-3.
        /// Template 0 uses 16 context bits + 4 AT pixels; templates 1/2/3 use
        /// 13/10/10 bits with a single AT pixel.
        /// </summary>
        private static Jbig2Bitmap DecodeArithmeticGenericRegion(
            byte[] encoded, int width, int height, int gbtemplate, bool tpgdon,
            sbyte[] atPixels, WorkBudget budget)
        {
            if (gbtemplate < 0 || gbtemplate > 3)
                throw new NotSupportedException($"GBTEMPLATE {gbtemplate} invalid");
            var mq = new MqDecoder(encoded, 0, GenericContextCount(gbtemplate));
            return DecodeGenericRegion(mq, 0, width, height, gbtemplate, tpgdon, atPixels, budget);
        }

        /// <summary>Contexts a generic region of <paramref name="gbtemplate"/> uses.</summary>
        private static int GenericContextCount(int gbtemplate) => 1 << (gbtemplate == 0 ? 16 : gbtemplate == 1 ? 13 : 10);

        /// <summary>
        /// What a generic region's setup costs in the work budget's pixel units, charged beside its
        /// area: measured, a one-pixel symbol's decode is about a microsecond of allocation and window
        /// setup against sixteen nanoseconds a pixel, so sixty-four pixels' worth.
        /// </summary>
        internal const int GenericRegionSetupUnits = 64;

        /// <summary>
        /// What one text-region instance costs in the work budget's pixel units before it draws:
        /// its integer decodes and symbol ID are some 350 ns against sixteen nanoseconds a pixel.
        /// Charged for every instance, the ones that name no symbol included.
        /// </summary>
        internal const int TextInstanceUnits = 16;

        /// <summary>
        /// What a refinement region's setup costs in the work budget's pixel units, charged beside its
        /// area: measured, a symbol dictionary's 1 × 1 refined symbol is about half a microsecond, its
        /// four integer decodes and symbol ID (<see cref="TextInstanceUnits"/>) and the region's
        /// position tables and bitmap, so sixteen pixels' worth each.
        /// </summary>
        internal const int RefinementRegionSetupUnits = 16;

        /// <summary>
        /// What a Huffman-coded refinement costs beyond the region's own setup: a fresh arithmetic
        /// decoder and the refinement statistics copied into it and back, 8 192 states each way.
        /// Measured, a Huffman dictionary's 1 × 1 refined symbol is about 4.7 µs; a BMSIZE of 0 reads
        /// no input, so its count is the stream's choice.
        /// </summary>
        internal const int HuffmanRefinementSetupUnits = 256;

        /// <summary>
        /// How many pixels of a page's or a region's own bitmap, allocated and filled with its default
        /// pixel, cost one unit of the work budget: measured, an 8 192 × 8 192 bitmap is 4.4 ms, 0.066 ns
        /// a pixel, against sixteen nanoseconds a unit.
        /// </summary>
        internal const int RegionBitmapPixelsPerUnit = 256;

        /// <summary>
        /// The work units the last <see cref="Decode(byte[], byte[], out int, out int)"/> on this thread spent — its pixels decoded
        /// and composed, its regions' setups, its text instances — against
        /// <see cref="PdfLimits.MaxJbig2CompositionPixels"/>; 0 until a decode completes. The
        /// budget's evidence, for the tests that price a unit.
        /// </summary>
        [ThreadStatic]
        internal static long LastWorkUnits;

        /// <summary>
        /// The generic region procedure (T.88 §6.2) over <paramref name="mq"/>, its contexts from
        /// <paramref name="contextBase"/>. A standalone region gives it a decoder of its own; a symbol
        /// dictionary shares its one decoder across every symbol (§6.5.8.1).
        /// </summary>
        /// <remarks>
        /// <b>A symbol dictionary's symbols take the dictionary's AT pixels.</b> They were decoded by a
        /// copy of this loop that read the template's default positions and never the SDAT bytes, so a
        /// dictionary with other AT positions decoded every symbol wrong. There is one loop now.
        /// </remarks>
        // Internal for Jbig2GenericRegionWindowsTests, which runs the per-position reference loop
        // beside this one over the same decoder and asks for the same pixels.
        internal static Jbig2Bitmap DecodeGenericRegion(MqDecoder mq, int contextBase,
            int width, int height, int gbtemplate, bool tpgdon, sbyte[] atPixels, WorkBudget budget,
            Jbig2Bitmap skip = null)
        {
            (int dy, int dx)[] refTable;
            int[] atSlots;        // context bit-indices that are overridden by AT pixels
            int ltpContext;

            switch (gbtemplate)
            {
                case 0:
                    refTable    = Template0RefPositions;
                    atSlots     = new[] { 11, 5, 4, 0 }; // AT1,AT2,AT3,AT4 array indices
                    ltpContext  = 0x9B25;
                    break;
                case 1:
                    refTable    = Template1RefPositions;
                    atSlots     = new[] { 9 };  // AT1 at array index 9
                    ltpContext  = 0x0795;
                    break;
                case 2:
                    refTable    = Template2RefPositions;
                    atSlots     = new[] { 7 };  // AT1 at array index 7
                    ltpContext  = 0x00E5;
                    break;
                case 3:
                    refTable    = Template3RefPositions;
                    atSlots     = new[] { 5 };  // AT1 at array index 5
                    ltpContext  = 0x0195;
                    break;
                default:
                    throw new NotSupportedException($"GBTEMPLATE {gbtemplate} invalid");
            }

            int contextBits  = refTable.Length;
            // One MQ decision per pixel, and the MQ decoder pads past end-of-data with 0xFF rather
            // than reporting EOF — so a few bytes of input decode a full region regardless. Plus the
            // region's own setup — the bitmap, the runs, the windows — which is a microsecond
            // whatever its size: a symbol dictionary of millions of one-pixel symbols is charged a
            // unit a pixel and costs a microsecond a symbol, so the setup is priced in pixels too.
            // The dimensions are judged before the charge is computed from them: a symbol width summed
            // from negative deltas reached −2 147 483 600, and its product was a negative charge.
            Jbig2Bitmap.ValidateDimensions(width, height);
            budget?.Tick(GenericRegionSetupUnits + (long)width * height);
            var bitmap = new Jbig2Bitmap(width, height);

            // Copy reference positions then override AT slots.
            var positions = new (int dy, int dx)[contextBits];
            Array.Copy(refTable, positions, contextBits);
            for (int i = 0; i < atSlots.Length && i * 2 + 1 < atPixels.Length; i++)
            {
                int slot = atSlots[i];
                if (slot >= 0 && slot < contextBits)
                    positions[slot] = (atPixels[i * 2 + 1], atPixels[i * 2]);
            }

            // The context is formed by sliding windows, one per run of positions that are
            // consecutive in index and in x on one row — with the nominal AT pixels, one run per
            // row (three for every template); an AT pixel moved elsewhere becomes a run of its own.
            // A window holds its run's pixels as bits in index order and gains the pixel after its
            // last as x advances, so each pixel costs one read per run instead of one per position:
            // sixteen bounds-checked reads a pixel was 61 ns, and the composition budget's 128 Mi
            // pixels — which a 98-byte symbol dictionary spends in full — was eight seconds.
            var runs = GenericContextRuns.For(positions);
            int runCount = runs.Count;
            var w = new int[runCount];
            var rowOffset = new int[runCount];
            byte[] data = bitmap.Data;
            int stride = bitmap.Stride;

            bool ltp = false;
            for (int y = 0; y < height; y++)
            {
                if (tpgdon)
                {
                    int bit = mq.Decode(contextBase + ltpContext);
                    if (bit != 0) ltp = !ltp;
                    if (ltp)
                    {
                        if (y > 0)
                            Buffer.BlockCopy(
                                bitmap.Data, (y - 1) * bitmap.Stride,
                                bitmap.Data, y * bitmap.Stride, bitmap.Stride);
                        continue;
                    }
                }

                // Each run's row this line, -1 where it lies outside the bitmap (every pixel 0),
                // and each window as it stands at x = 0.
                for (int r = 0; r < runCount; r++)
                {
                    int ry = y + runs.Dy[r];
                    int off = ry >= 0 && ry < height ? ry * stride : -1;
                    rowOffset[r] = off;
                    int v = 0;
                    for (int k = 0; k < runs.Length[r]; k++)
                    {
                        int px = runs.DxFirst[r] + k;
                        int p = off >= 0 && px >= 0 && px < width ? (data[off + (px >> 3)] >> (7 - (px & 7))) & 1 : 0;
                        v = (v << 1) | p;
                    }
                    w[r] = v;
                }

                int rowOff = y * stride;
                if (runCount == 3 && skip == null)
                {
                    // The nominal templates: one run per row, held in locals. The same loop as below
                    // with the arrays hoisted — the per-pixel cost is the three reads and the decode.
                    int off0 = rowOffset[0], off1 = rowOffset[1], off2 = rowOffset[2];
                    int dxl0 = runs.DxLast[0], dxl1 = runs.DxLast[1], dxl2 = runs.DxLast[2];
                    int sh0 = runs.Shift[0], sh1 = runs.Shift[1], sh2 = runs.Shift[2];
                    int m0 = runs.Mask[0], m1 = runs.Mask[1], m2 = runs.Mask[2];
                    int w0 = w[0], w1 = w[1], w2 = w[2];
                    for (int x = 0; x < width; x++)
                    {
                        int context = (w0 << sh0) | (w1 << sh1) | (w2 << sh2);
                        if (mq.Decode(contextBase + context) != 0)
                            data[rowOff + (x >> 3)] |= (byte)(0x80 >> (x & 7));

                        int px = x + 1 + dxl0;
                        int p = off0 >= 0 && px >= 0 && px < width ? (data[off0 + (px >> 3)] >> (7 - (px & 7))) & 1 : 0;
                        w0 = ((w0 << 1) | p) & m0;
                        px = x + 1 + dxl1;
                        p = off1 >= 0 && px >= 0 && px < width ? (data[off1 + (px >> 3)] >> (7 - (px & 7))) & 1 : 0;
                        w1 = ((w1 << 1) | p) & m1;
                        px = x + 1 + dxl2;
                        p = off2 >= 0 && px >= 0 && px < width ? (data[off2 + (px >> 3)] >> (7 - (px & 7))) & 1 : 0;
                        w2 = ((w2 << 1) | p) & m2;
                    }
                    continue;
                }

                for (int x = 0; x < width; x++)
                {
                    // USESKIP (§6.2.5.7): a skipped pixel is 0 and is not coded at all — but the
                    // windows still move past it.
                    if (skip == null || skip.GetPixel(x, y) == 0)
                    {
                        int context = 0;
                        for (int r = 0; r < runCount; r++) context |= w[r] << runs.Shift[r];
                        if (mq.Decode(contextBase + context) != 0)
                            data[rowOff + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }

                    // Slide: every window gains the pixel after its last. A run on this row ending
                    // at x − 1 gains the pixel just decoded; one reaching to or past x gains a pixel
                    // not yet decoded, which is 0, as the per-position read made it.
                    for (int r = 0; r < runCount; r++)
                    {
                        int off = rowOffset[r];
                        int px = x + 1 + runs.DxLast[r];
                        int p = off >= 0 && px >= 0 && px < width ? (data[off + (px >> 3)] >> (7 - (px & 7))) & 1 : 0;
                        w[r] = ((w[r] << 1) | p) & runs.Mask[r];
                    }
                }
            }

            return bitmap;
        }

        /// <summary>
        /// The runs a generic region's context positions fall into: maximal groups of positions
        /// consecutive in index, on one row and one pixel apart in x — each kept as one sliding
        /// window by <see cref="DecodeGenericRegion(MqDecoder, int, int, int, int, bool, sbyte[], WorkBudget, Jbig2Bitmap)"/>.
        /// A run's bits sit in the context at <see cref="Shift"/> (the position after its last
        /// index, counted from the LSB), its first position at the window's MSB.
        /// </summary>
        internal sealed class GenericContextRuns
        {
            internal int Count;
            internal int[] Dy, DxFirst, DxLast, Length, Shift, Mask;

            internal static GenericContextRuns For((int dy, int dx)[] positions)
            {
                int n = positions.Length;
                var dy = new List<int>(); var dxFirst = new List<int>(); var len = new List<int>(); var shift = new List<int>();
                for (int i = 0; i < n;)
                {
                    int j = i + 1;
                    while (j < n && positions[j].dy == positions[i].dy && positions[j].dx == positions[j - 1].dx + 1) j++;
                    dy.Add(positions[i].dy);
                    dxFirst.Add(positions[i].dx);
                    len.Add(j - i);
                    shift.Add(n - j);
                    i = j;
                }
                var runs = new GenericContextRuns
                {
                    Count = dy.Count,
                    Dy = dy.ToArray(), DxFirst = dxFirst.ToArray(), Length = len.ToArray(), Shift = shift.ToArray(),
                };
                runs.DxLast = new int[runs.Count];
                runs.Mask = new int[runs.Count];
                for (int r = 0; r < runs.Count; r++)
                {
                    runs.DxLast[r] = runs.DxFirst[r] + runs.Length[r] - 1;
                    runs.Mask[r] = (1 << runs.Length[r]) - 1;
                }
                return runs;
            }
        }

        // ==================================================================
        //  Refinement Region Decoder (T.88 §6.3)
        //
        //  Refinement coding uses both a "reference" bitmap (usually a symbol
        //  template) and the partially-decoded output bitmap to provide
        //  context for each pixel.  Template 0 uses 13 context pixels split
        //  between the reference and the output; template 1 uses 10.
        // ==================================================================

        // Reference bitmap positions (pixels from the reference) and current
        // bitmap positions (pixels already decoded) for refinement template 0.
        // Context is 13 bits total.
        private static readonly (int dy, int dx)[] RefineTemplate0ReferencePositions =
        {
            // Reference-bitmap context pixels (9 pixels for GRTEMPLATE=0)
            (-1, -1), (-1, 0), (-1, 1),
            ( 0, -1), ( 0, 0), ( 0, 1),
            ( 1, -1), ( 1, 0), ( 1, 1),
        };

        private static readonly (int dy, int dx)[] RefineTemplate0OutputPositions =
        {
            // Current-output context pixels (4 pixels for GRTEMPLATE=0)
            (-1, -1), (-1, 0), (-1, 1),
            ( 0, -1),
        };

        private static readonly (int dy, int dx)[] RefineTemplate1ReferencePositions =
        {
            (-1,  0), ( 0, -1), ( 0, 0), ( 0, 1), ( 1, 0), ( 1, 1),
        };

        private static readonly (int dy, int dx)[] RefineTemplate1OutputPositions =
        {
            (-1, -1), (-1, 0), (-1, 1), ( 0, -1),
        };

        /// <summary>
        /// Decodes a refinement region against the given reference bitmap.
        /// Width/height are the OUTPUT dimensions.  Reference is positioned
        /// at (refX, refY) relative to the output origin.
        /// </summary>
        // contextBase: where this procedure's contexts start in mq. A text region or symbol
        // dictionary shares one decoder with its integer decoders, and the refinement contexts
        // numbered from 0 shared their states with them.
        private static Jbig2Bitmap DecodeRefinementRegion(
            MqDecoder mq, int contextBase, Jbig2Bitmap reference,
            int width, int height, int grTemplate,
            int refOffsetX, int refOffsetY, bool tpgron,
            sbyte at1x, sbyte at1y, sbyte at2x, sbyte at2y, WorkBudget budget)
        {
            var refPos = grTemplate == 0
                ? RefineTemplate0ReferencePositions
                : RefineTemplate1ReferencePositions;
            var outPos = grTemplate == 0
                ? RefineTemplate0OutputPositions
                : RefineTemplate1OutputPositions;

            int contextBits = refPos.Length + outPos.Length;

            // For template 0, AT1 replaces ref position [0], AT2 replaces out position [0].
            // For template 1, no AT overrides.
            var refPosEff = new (int dy, int dx)[refPos.Length];
            Array.Copy(refPos, refPosEff, refPos.Length);
            var outPosEff = new (int dy, int dx)[outPos.Length];
            Array.Copy(outPos, outPosEff, outPos.Length);
            if (grTemplate == 0)
            {
                refPosEff[0] = (at2y, at2x);
                outPosEff[0] = (at1y, at1x);
            }

            budget?.Tick(RefinementRegionSetupUnits + (long)width * height);
            var bitmap = new Jbig2Bitmap(width, height);
            bool ltp = false;
            int ltpContext = grTemplate == 0 ? 0x0100 : 0x0080;

            for (int y = 0; y < height; y++)
            {
                if (tpgron)
                {
                    int bit = mq.Decode(contextBase + ltpContext);
                    if (bit != 0) ltp = !ltp;
                }

                for (int x = 0; x < width; x++)
                {
                    // TPGRON (T.88 §6.3.5.6): on a typical row, a pixel whose 3x3 reference
                    // neighbourhood is all one value takes that value without being coded, and
                    // every other pixel is decoded as usual. Copying the row from the reference
                    // whole, decoding nothing, would put the MQ stream out of step at the first pixel
                    // that was coded. An independent decoder is the model.
                    if (ltp)
                    {
                        int implicitValue = ImplicitValue(reference, x - refOffsetX, y - refOffsetY);
                        if (implicitValue >= 0)
                        {
                            if (implicitValue != 0) bitmap.SetPixel(x, y, 1);
                            continue;
                        }
                    }

                    int context = 0;
                    // Reference-bitmap pixels
                    foreach (var pos in refPosEff)
                    {
                        int rx = x + pos.dx - refOffsetX;
                        int ry = y + pos.dy - refOffsetY;
                        int p = reference != null ? reference.GetPixel(rx, ry) : 0;
                        context = (context << 1) | p;
                    }
                    // Output-bitmap pixels (already decoded)
                    foreach (var pos in outPosEff)
                    {
                        int rx = x + pos.dx;
                        int ry = y + pos.dy;
                        int p = (ry < 0 || rx < 0 || rx >= width || ry >= height)
                            ? 0 : bitmap.GetPixel(rx, ry);
                        context = (context << 1) | p;
                    }
                    int pixel = mq.Decode(contextBase + context);
                    if (pixel != 0) bitmap.SetPixel(x, y, 1);
                }
            }

            return bitmap;
        }

        /// <summary>
        /// The reference pixel at (<paramref name="x"/>, <paramref name="y"/>) when its eight
        /// neighbours all share its value, else -1.
        /// </summary>
        private static int ImplicitValue(Jbig2Bitmap reference, int x, int y)
        {
            int m = reference != null ? reference.GetPixel(x, y) : 0;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if ((reference != null ? reference.GetPixel(x + dx, y + dy) : 0) != m) return -1;
            return m;
        }

        // ==================================================================
        //  Symbol Dictionary Decoder (T.88 §6.5)
        //
        //  Decodes a symbol dictionary segment body into a list of symbol
        //  bitmaps.  Referred-to dictionaries provide pre-existing symbols;
        //  new symbols are decoded via the arithmetic generic-region decoder
        //  (with optional refinement aggregation).  Export flags pick which
        //  of the combined pool is surfaced to callers (typically text regions
        //  that reference these symbols by ID).
        //
        //  Scope — SDHUFF=0 (arithmetic) decodes here.  SDHUFF=1 routes to
        //  DecodeSymbolDictionaryHuffman below, which decodes the collective
        //  bitmaps and, with SDREFAGG=1, each aggregate symbol too (GAPS.md
        //  §2a). Refinement aggregation in either mode: one instance is a
        //  refinement (§6.5.8.2.2), more an aggregate text region (§6.5.8.2.1).
        // ==================================================================

        // internal, not private: the only way to observe a symbol dictionary's decoded pixels is
        // through a text region that places them on a page, and the Huffman-coded text region
        // half is not implemented — so a test proving the Huffman symbol dictionary decodes real,
        // non-blank pixels (as opposed to merely "does not throw") has no other way to reach them.
        internal static Jbig2Bitmap[] DecodeSymbolDictionary(byte[] body,
            List<Jbig2Bitmap> referredSymbols, WorkBudget budget,
            Dictionary<uint, Jbig2HuffmanTable> customTables = null,
            IReadOnlyList<uint> referredSegmentNumbers = null)
            => DecodeSymbolDictionary(body, referredSymbols, budget, customTables, referredSegmentNumbers,
                null, out _);

        /// <summary>
        /// The arithmetic bitmap-coding contexts a symbol dictionary keeps for a later one to start from
        /// (T.88 7.4.2.1.1, bit 9 "bitmap coding context retained"): the generic region's and, with
        /// refinement aggregation, the refinement region's.
        /// </summary>
        internal sealed class Jbig2RetainedContexts
        {
            internal int[] Generic;
            internal int[] Refinement;
        }

        /// <summary>
        /// The retained contexts of the last referred-to dictionary that kept any, or null.
        /// </summary>
        private static Jbig2RetainedContexts LastRetainedContexts(IReadOnlyList<uint> referred,
            Dictionary<uint, Jbig2RetainedContexts> retained)
        {
            if (referred == null || retained == null) return null;
            Jbig2RetainedContexts found = null;
            foreach (uint n in referred)
                if (retained.TryGetValue(n, out var c)) found = c;
            return found;
        }

        /// <summary>
        /// The symbols an aggregate symbol's instances may name (§6.5.8.2: the input symbols, then
        /// the new symbols decoded before it), as one list a dictionary grows a symbol at a time
        /// through <see cref="AddNewSymbol"/>.
        /// </summary>
        /// <remarks>
        /// A copy of the pool for each symbol would be quadratic in the dictionary's symbol count and
        /// charged to no budget: a symbol's work is priced by its area, and a zero-area symbol is a
        /// few integer decodes, so a kilobyte declaring hundreds of thousands of them spends seconds
        /// copying the pool between them. Grown in place, the pool costs a reference a symbol.
        /// </remarks>
        private static List<Jbig2Bitmap> AggregatePool(List<Jbig2Bitmap> referredSymbols, uint newSymbolCount)
        {
            int referred = referredSymbols?.Count ?? 0;
            var pool = new List<Jbig2Bitmap>((int)Math.Min(referred + (long)newSymbolCount, referred + 4096L));
            if (referredSymbols != null) pool.AddRange(referredSymbols);
            return pool;
        }

        /// <summary>
        /// An aggregate symbol's box (§6.5.8.2.1), its area charged before it is allocated: its
        /// instances pay for what they draw, and the box is kept in the dictionary whatever they
        /// draw, so a stream of two-instance aggregates declared at the bitmap ceiling would otherwise
        /// keep eight megabytes a symbol for a few dozen units.
        /// </summary>
        private static Jbig2Bitmap AggregateBox(int width, int height, WorkBudget budget)
        {
            Jbig2Bitmap.ValidateDimensions(width, height);
            budget?.Tick((long)width * height);
            return new Jbig2Bitmap(width, height);
        }

        /// <summary>A newly decoded symbol, appended to the dictionary's new symbols and to its aggregate pool.</summary>
        private static void AddNewSymbol(List<Jbig2Bitmap> newSymbols, List<Jbig2Bitmap> pool, Jbig2Bitmap symbol)
        {
            newSymbols.Add(symbol);
            pool.Add(symbol);
        }

        internal static Jbig2Bitmap[] DecodeSymbolDictionary(byte[] body,
            List<Jbig2Bitmap> referredSymbols, WorkBudget budget,
            Dictionary<uint, Jbig2HuffmanTable> customTables,
            IReadOnlyList<uint> referredSegmentNumbers,
            Jbig2RetainedContexts contextsIn, out Jbig2RetainedContexts contextsOut)
        {
            var br = new Jbig2BitReader(body);
            ushort flags = br.ReadUInt16BE();
            bool sdhuff     = (flags & 0x0001) != 0;
            bool sdrefagg   = (flags & 0x0002) != 0;
            int sdhuffDh    = (flags >> 2) & 0x3;
            int sdhuffDw    = (flags >> 4) & 0x3;
            bool sdhuffBmSize  = ((flags >> 6) & 0x1) != 0;
            bool sdhuffAggInst = ((flags >> 7) & 0x1) != 0;
            bool sdinbmctx  = ((flags >> 8) & 0x1) != 0;
            // T.88 §7.4.2.1.1: bit 8 "bitmap coding context used", bit 9 "retained", bits 10-11
            // SDTEMPLATE, bit 12 SDRTEMPLATE. This read bit 9, so a SDRTEMPLATE 1 dictionary was
            // taken for template 0 (and read four SDRAT bytes it does not hold), and one with the
            // retained bit set for template 1.
            int sdrtemplate = (flags >> 12) & 0x1;
            int sdtemplate  = (flags >> 10) & 0x3;
            bool sdctxRetained = ((flags >> 9) & 0x1) != 0;
            contextsOut = null;
            _ = sdhuffAggInst; // Huffman-only

            if (sdhuff)
            {
                return DecodeSymbolDictionaryHuffman(br, sdrefagg, sdrtemplate,
                    referredSymbols, sdhuffDh, sdhuffDw, sdhuffBmSize, sdhuffAggInst,
                    customTables, referredSegmentNumbers, budget);
            }

            // Generic region AT pixels (8 bytes for template 0, 2 bytes otherwise).
            int atBytes = sdtemplate == 0 ? 8 : 2;
            var sdAt = new sbyte[atBytes];
            for (int i = 0; i < atBytes; i++) sdAt[i] = (sbyte)br.ReadByte();

            // Refinement AT pixels (4 bytes when SDREFAGG=1 and SDRTEMPLATE=0).
            sbyte[] sdrAt = null;
            if (sdrefagg && sdrtemplate == 0)
            {
                sdrAt = new sbyte[4];
                for (int i = 0; i < 4; i++) sdrAt[i] = (sbyte)br.ReadByte();
            }

            uint sdNumExSyms  = br.ReadUInt32BE();
            uint sdNumNewSyms = br.ReadUInt32BE();
            // Cap so an adversarial count cannot drive a billion-iteration decode
            // loop (the MQ decoder pads past EOF rather than terminating).
            if (sdNumNewSyms > 10_000_000)
                throw new System.IO.InvalidDataException($"JBIG2 symbol count out of range: {sdNumNewSyms}.");

            // Remaining bytes are the arithmetic-coded symbol data.
            byte[] arithData = br.ReadBytes(br.RemainingBytes);

            // Contexts, in one decoder: the generic region's; IADH, IADW, IAAI and IAEX; and, with
            // refinement/aggregate coding, the text-region decoders (§6.5.8.2) after them.
            int sdNumInSyms = referredSymbols?.Count ?? 0;
            int genericContexts = GenericContextCount(sdtemplate);
            int dictionaryContexts = genericContexts + 4 * Jbig2IntegerDecoder.ContextTableSize;
            int symCodeLen = SymbolCodeLength((long)sdNumInSyms + sdNumNewSyms);
            int contextCount = dictionaryContexts + (sdrefagg ? TextRegionContexts.Size(symCodeLen) : 0);

            var mq = new MqDecoder(arithData, 0, contextCount);

            // 7.4.2.2: with the bitmap coding context used, the generic region's statistics start from
            // the ones the last referred dictionary that retained them left. The integer decoders always
            // start fresh.
            if (sdinbmctx && contextsIn != null)
                Array.Copy(contextsIn.Generic, mq.ContextStates, Math.Min(genericContexts, contextsIn.Generic.Length));

            var iadh = new Jbig2IntegerDecoder(mq, genericContexts + 0 * 512);
            var iadw = new Jbig2IntegerDecoder(mq, genericContexts + 1 * 512);
            var iaai = new Jbig2IntegerDecoder(mq, genericContexts + 2 * 512);
            int iaexBase = genericContexts + 3 * 512;

            // §6.5.8.2: the aggregate decoders are created once for the dictionary and keep their
            // statistics from one symbol to the next. SBSYMCODELEN is §6.5.8.2.3's, from the input
            // symbols plus every symbol the dictionary will define, not the pool so far.
            TextRegionContexts textContexts = sdrefagg
                ? new TextRegionContexts(mq, dictionaryContexts, symCodeLen)
                : null;
            if (sdinbmctx && contextsIn?.Refinement != null && textContexts != null)
                Array.Copy(contextsIn.Refinement, 0, mq.ContextStates, textContexts.GrBase,
                    Math.Min(TextRegionContexts.RefinementContexts, contextsIn.Refinement.Length));

            var newSymbols = new List<Jbig2Bitmap>((int)Math.Min(sdNumNewSyms, 4096));
            List<Jbig2Bitmap> pool = sdrefagg ? AggregatePool(referredSymbols, sdNumNewSyms) : null;
            int symbolsDecoded = 0;
            int currentHeight  = 0;

            while (symbolsDecoded < sdNumNewSyms)
            {
                int dh = iadh.Decode();
                if (dh == Jbig2IntegerDecoder.OOB) break;
                currentHeight += dh;

                int symWidth = 0;
                int heightClassCount = 0;
                while (true)
                {
                    // A height class ends on an OOB width, the last one included (§6.5.5, as
                    // independent decoders read it). Stopping after the last symbol without reading
                    // it, the export flags would be decoded from the OOB's bits and come back empty,
                    // and the "export everything" fallback would stand in for them —
                    // right for a dictionary that exports all, wrong for one that does not.
                    int dw = iadw.Decode();
                    if (dw == Jbig2IntegerDecoder.OOB) break;
                    if (symbolsDecoded >= sdNumNewSyms)
                        throw new InvalidDataException(
                            $"JBIG2 symbol dictionary codes more than its {sdNumNewSyms} new symbols.");
                    symWidth += dw;

                    if (!sdrefagg)
                    {
                        // §6.5.8.1: each symbol is a generic region with the dictionary's template
                        // and AT pixels, TPGDON off, on the dictionary's decoder.
                        newSymbols.Add(DecodeGenericRegion(mq, 0, symWidth, currentHeight,
                            sdtemplate, false, sdAt, budget));
                    }
                    else
                    {
                        int refAggNinst = iaai.Decode();
                        if (refAggNinst == 1)
                        {
                            // §6.5.8.2.2: one instance — a symbol ID, a refinement offset, and the
                            // referenced symbol refined to this symbol's size; its decodes priced as a
                            // text instance's.
                            budget?.Tick(TextInstanceUnits);
                            int id = DecodeSymbolId(mq, textContexts.IaidBase, symCodeLen);
                            int rdx = textContexts.Iardx.Decode();
                            int rdy = textContexts.Iardy.Decode();
                            if (id < 0 || id >= pool.Count)
                                throw new InvalidDataException(
                                    $"JBIG2 refinement symbol refers to symbol {id} of {pool.Count}.");
                            AddNewSymbol(newSymbols, pool, DecodeRefinementRegion(mq, textContexts.GrBase, pool[id],
                                symWidth, currentHeight, sdrtemplate,
                                refOffsetX: rdx, refOffsetY: rdy, tpgron: false,
                                RefinementAt(sdrAt, 0), RefinementAt(sdrAt, 1),
                                RefinementAt(sdrAt, 2), RefinementAt(sdrAt, 3), budget));
                        }
                        else if (refAggNinst > 1)
                        {
                            // §6.5.8.2.1: an aggregate — a text region of REFAGGNINST instances into
                            // this symbol's box, with Table 17's fixed parameters: one strip,
                            // refinement on, TOPLEFT, OR, no DS offset.
                            var aggregate = AggregateBox(symWidth, currentHeight, budget);
                            DecodeTextRegionInstances(textContexts, aggregate, 0, 0, combOp: 0,
                                refAggNinst, strips: 1, dsOffset: 0, refCorner: RefCornerTopLeft,
                                transposed: false, refine: true, sdrtemplate, sdrAt, pool, budget);
                            AddNewSymbol(newSymbols, pool, aggregate);
                        }
                        else
                        {
                            throw new InvalidDataException(
                                $"JBIG2 aggregate symbol declares {refAggNinst} instances.");
                        }
                    }
                    symbolsDecoded++;
                    heightClassCount++;
                }
                // No-progress guard: a crafted arithmetic stream can make IADW return OOB on
                // the FIRST inner iteration of every height class while IADH never returns OOB,
                // so `symbolsDecoded` never advances and the outer loop spins forever (the MQ
                // decoder pads 0xFF past EOF and never throws). If a height class produced no
                // symbols, stop — a valid height class always contains at least one symbol.
                if (heightClassCount == 0) break;
            }

            // Combine referred + new, then select exported symbols per IAEX.
            // IAEX decodes a sequence of run-lengths with alternating
            // "exclude / include" semantics (T.88 §6.5.10):
            //   • Start in "exclude" mode.
            //   • Decode a run length R via IAEX.
            //   • Mark the next R symbols with the current mode's flag.
            //   • Toggle mode; repeat until we've processed
            //     (referredSymbols.Count + newSymbols.Count) symbols.
            // The "include" list is the dictionary's exported symbols.
            var allSymbols = new List<Jbig2Bitmap>(
                (referredSymbols?.Count ?? 0) + newSymbols.Count);
            if (referredSymbols != null) allSymbols.AddRange(referredSymbols);
            allSymbols.AddRange(newSymbols);

            var iaex = new Jbig2IntegerDecoder(mq, iaexBase);
            var exported = ExportedSymbols(allSymbols, () => iaex.Decode(), Jbig2IntegerDecoder.OOB, sdNumExSyms);

            if (sdctxRetained)
            {
                var generic = new int[genericContexts];
                Array.Copy(mq.ContextStates, generic, genericContexts);
                int[] refinement = null;
                if (textContexts != null)
                {
                    refinement = new int[TextRegionContexts.RefinementContexts];
                    Array.Copy(mq.ContextStates, textContexts.GrBase, refinement, 0, refinement.Length);
                }
                contextsOut = new Jbig2RetainedContexts { Generic = generic, Refinement = refinement };
            }

            return exported.ToArray();
        }

        /// <summary>
        /// The type-53 ("Tables") segments a symbol dictionary or text region segment refers to,
        /// in the order they appear in its referred-to-segments list — the exact sequence T.88
        /// §7.4.3.1.2/§7.4.3.1.6 draw from positionally, one table per "custom" selector value
        /// encountered, left to right through a fixed field order (a symbol dictionary's
        /// SDHUFFDH → SDHUFFDW → SDHUFFBMSIZE[ → SDHUFFAGGINST]; a text region's SBHUFFFS →
        /// SBHUFFDS → SBHUFFDT[ → the four refinement-delta tables → SBHUFFRSIZE]). Non-Tables
        /// referred-to segments (symbol dictionaries themselves, for a text region) are skipped —
        /// confirmed against an independent decoder, which does the identical filter
        /// (referred-to segment type == 53) and positional pick.
        /// </summary>
        private static Jbig2HuffmanTable[] SelectCustomTables(
            IReadOnlyList<uint> referredSegmentNumbers, Dictionary<uint, Jbig2HuffmanTable> customTables)
        {
            if (referredSegmentNumbers == null || customTables == null || customTables.Count == 0)
                return Array.Empty<Jbig2HuffmanTable>();

            var list = new List<Jbig2HuffmanTable>();
            foreach (uint refNum in referredSegmentNumbers)
                if (customTables.TryGetValue(refNum, out var table))
                    list.Add(table);
            return list.ToArray();
        }

        /// <summary>
        /// Consumes the next table in <paramref name="customTables"/>'s referred-to order,
        /// advancing <paramref name="index"/>. Throws — rather than silently falling back to a
        /// standard table, which would decode the wrong values without saying so — when the
        /// stream selects "custom" more times than it referred to Tables segments.
        /// </summary>
        private static Jbig2HuffmanTable NextCustomTable(
            Jbig2HuffmanTable[] customTables, ref int index, string selectorName)
        {
            if (index >= customTables.Length)
                throw new System.IO.InvalidDataException(
                    $"JBIG2 Huffman {selectorName} selects a custom (type-53) table, but this " +
                    $"segment refers to only {customTables.Length} Tables segment(s) — table " +
                    $"{index} is out of range.");
            return customTables[index++];
        }

        /// <summary>
        /// SDHUFF=1 symbol dictionaries — the non-aggregated case (T.88 §6.5.5-§6.5.10). Decodes
        /// height classes, splits the per-height-class collective bitmap into individual symbols,
        /// and applies the export-flag run-length selection.
        /// </summary>
        /// <remarks>
        /// <para>The collective bitmap is parsed and split, not replaced by an <b>empty
        /// placeholder</b> per symbol — which would throw nothing and give a fully-Huffman stream a
        /// correctly sized,
        /// correctly positioned, <b>entirely blank</b> region reported as a successful decode. The
        /// standard tables it uses are externally verified.</para>
        /// <para><b>Aggregates (SDREFAGG=1, §6.5.8.2).</b> Each symbol is decoded as it is read, not
        /// collected into a collective bitmap: a single instance is a refinement of one referred
        /// symbol, and more are a text region (Table 17's tables) drawn into the symbol's box. The
        /// refined bitmaps are arithmetic-coded over the BMSIZE or RSIZE bytes that precede them.</para>
        /// <para><b>User-defined (customTables) selectors are wired</b>, in the order
        /// an independent decoder consumes them: SDHUFFDH (if 3), then SDHUFFDW
        /// (if 3), then SDHUFFBMSIZE (if its flag bit is set), then SDHUFFAGGINST (if its flag bit
        /// is set, whether or not SDREFAGG is).</para>
        /// <para>Table selection (T.88 Table 14, cross-checked against a second, complete Huffman
        /// JBIG2 implementation since this file has no primary-source copy to read the table from
        /// directly): SDHUFFDH 0→B.4, 1→B.5; SDHUFFDW 0→B.2, 1→B.3; SDHUFFBMSIZE and the §6.5.10
        /// export run-length are always Table B.1 (B.1 for the export case is a genuine, separate
        /// confirmation — the reference implementation whose collective-bitmap procedure this
        /// mirrors left it as an explicit unimplemented TODO with a different table guessed at;
        /// the second implementation's real, working code was checked instead of trusting either
        /// alone).</para>
        /// </remarks>
        private static Jbig2Bitmap[] DecodeSymbolDictionaryHuffman(Jbig2BitReader br,
            bool sdRefAgg, int sdRTemplate, List<Jbig2Bitmap> referredSymbols,
            int sdHuffDh, int sdHuffDw, bool sdHuffBmSize, bool sdHuffAggInst,
            Dictionary<uint, Jbig2HuffmanTable> customTables,
            IReadOnlyList<uint> referredSegmentNumbers, WorkBudget budget)
        {
            // Custom tables are drawn positionally, in referred-to order: DH, DW, BMSIZE, then
            // AGGINST — see SelectCustomTables' remarks.
            var custom = SelectCustomTables(referredSegmentNumbers, customTables);
            int customIndex = 0;

            Jbig2HuffmanTable dhTable = sdHuffDh switch
            {
                0 => StandardTableD, // B.4
                1 => StandardTableE, // B.5
                3 => NextCustomTable(custom, ref customIndex, "SDHUFFDH"),
                _ => throw new System.IO.InvalidDataException(
                    $"JBIG2 Huffman symbol dictionary specifies reserved SDHUFFDH value {sdHuffDh}."),
            };
            Jbig2HuffmanTable dwTable = sdHuffDw switch
            {
                0 => StandardTableB, // B.2
                1 => StandardTableC, // B.3
                3 => NextCustomTable(custom, ref customIndex, "SDHUFFDW"),
                _ => throw new System.IO.InvalidDataException(
                    $"JBIG2 Huffman symbol dictionary specifies reserved SDHUFFDW value {sdHuffDw}."),
            };
            Jbig2HuffmanTable bmSizeTable = sdHuffBmSize
                ? NextCustomTable(custom, ref customIndex, "SDHUFFBMSIZE")
                : StandardTableA; // B.1
            Jbig2HuffmanTable aggInstTable = sdHuffAggInst
                ? NextCustomTable(custom, ref customIndex, "SDHUFFAGGINST")
                : StandardTableA; // B.1

            // T.88 §7.4.2.1.2: SDAT is present only when SDHUFF=0, so none precedes this. SDRAT is
            // present when SDREFAGG=1 and SDRTEMPLATE=0, in either coding mode.
            sbyte[] sdrAt = null;
            if (sdRefAgg && sdRTemplate == 0)
            {
                sdrAt = new sbyte[4];
                for (int i = 0; i < 4; i++) sdrAt[i] = (sbyte)br.ReadByte();
            }

            uint sdNumExSyms  = br.ReadUInt32BE();
            uint sdNumNewSyms = br.ReadUInt32BE();
            if (sdNumNewSyms > 10_000_000)
                throw new System.IO.InvalidDataException($"JBIG2 symbol count out of range: {sdNumNewSyms}.");

            var hr = new MsbBitStream(br.ReadBytes(br.RemainingBytes), 0);

            // §6.5.8.2.3: SBSYMCODELEN from the input symbols plus every symbol this dictionary defines.
            int symCodeLen = SymbolCodeLength((long)(referredSymbols?.Count ?? 0) + sdNumNewSyms);
            var aggregateCoding = sdRefAgg ? AggregateCoding(symCodeLen) : null;
            var grStates = new int[TextRegionContexts.RefinementContexts];

            var newSymbols = new List<Jbig2Bitmap>((int)Math.Min(sdNumNewSyms, 4096));
            List<Jbig2Bitmap> pool = sdRefAgg ? AggregatePool(referredSymbols, sdNumNewSyms) : null;
            int hcHeight = 0;
            int nSymsDecoded = 0;

            // 6.5.5 (4a)-(4d)
            while (nSymsDecoded < sdNumNewSyms)
            {
                int hcdh = dhTable.Decode(hr);
                if (hcdh == Jbig2HuffmanTable.OOB)
                    throw new System.IO.InvalidDataException(
                        "JBIG2 Huffman symbol dictionary: unexpected OOB decoding a height-class delta.");
                hcHeight += hcdh;

                // 6.5.6 / 6.5.7 — per-symbol width deltas within this height class, terminated by
                // OOB. This does NOT break early once SDNUMNEWSYMS is reached: a height class's DW
                // sequence always ends in OOB, even when its last symbol is the dictionary's very last
                // symbol — a decoder that stops reading one symbol early desyncs for everything after it.
                // The guard below is an overshoot check only, for a malformed stream that never signals OOB.
                var widths = new List<int>();
                int totWidth = 0;
                int symWidth = 0;
                while (true)
                {
                    if (nSymsDecoded + widths.Count > sdNumNewSyms)
                        throw new System.IO.InvalidDataException(
                            "JBIG2 Huffman symbol dictionary: height class did not signal OOB " +
                            "before exceeding SDNUMNEWSYMS.");
                    int dw = dwTable.Decode(hr);
                    if (dw == Jbig2HuffmanTable.OOB) break;
                    symWidth += dw;
                    if (!sdRefAgg) totWidth += symWidth;
                    widths.Add(symWidth);

                    // §6.5.8.2: with SDREFAGG=1 each symbol is decoded on its own, as it is read, and
                    // no collective bitmap is formed for it.
                    if (sdRefAgg)
                        AddNewSymbol(newSymbols, pool, DecodeAggregateSymbolHuffman(hr, aggregateCoding, pool,
                            symWidth, hcHeight, sdRTemplate, sdrAt, aggInstTable, symCodeLen, grStates, budget));
                }
                if (widths.Count == 0) break; // no-progress guard (crafted stream, missing OOB)

                if (!sdRefAgg)
                {
                    // 6.5.9 — one collective bitmap per height class, holding every symbol in it side
                    // by side, sized through Table B.1 or a custom SDHUFFBMSIZE table.
                    int bmSize = bmSizeTable.Decode(hr);
                    if (bmSize < 0)
                        throw new System.IO.InvalidDataException(
                            "JBIG2 Huffman symbol dictionary: invalid collective bitmap size.");
                    hr.AlignToByte();

                    // Pre-validate before the expensive decode: the single allocation choke point.
                    Jbig2Bitmap.ValidateDimensions(totWidth, hcHeight);
                    budget?.Tick((long)totWidth * hcHeight);

                    Jbig2Bitmap collective;
                    if (bmSize == 0)
                    {
                        // Uncompressed: exactly height * stride raw bytes follow, MSB-first packed.
                        int stride = (totWidth + 7) / 8;
                        var raw = hr.ReadAlignedBytes(hcHeight * stride);
                        collective = new Jbig2Bitmap(totWidth, hcHeight, raw);
                    }
                    else
                    {
                        // MMR (T.6): exactly BMSIZE bytes, no EOFB.
                        var mmrBytes = hr.ReadAlignedBytes(bmSize);
                        byte[] decoded = CcittFaxDecoder.DecodeGroup4(
                            mmrBytes, totWidth, hcHeight, endOfBlock: false, out _);
                        collective = new Jbig2Bitmap(totWidth, hcHeight, decoded);
                    }

                    // Split by accumulated width — symbol i occupies columns [x, x + widths[i]).
                    int x = 0;
                    foreach (int w in widths)
                    {
                        var glyph = new Jbig2Bitmap(w, hcHeight);
                        glyph.DrawAt(collective, -x, 0, 4 /* REPLACE */, budget);
                        x += w;
                        newSymbols.Add(glyph);
                    }
                }
                nSymsDecoded += widths.Count;
            }

            // 6.5.10 — export flags: alternating exclude/include run lengths over the combined
            // referred + new symbol list, always decoded through Table B.1 for SDHUFF=1. Same
            // fallback as the arithmetic path: if the run-length sequence produces no selections
            // (corrupt stream), export everything, clamped to SDNUMEXSYMS.
            var allSymbols = new List<Jbig2Bitmap>((referredSymbols?.Count ?? 0) + newSymbols.Count);
            if (referredSymbols != null) allSymbols.AddRange(referredSymbols);
            allSymbols.AddRange(newSymbols);

            return ExportedSymbols(allSymbols, () => StandardTableA.Decode(hr), Jbig2HuffmanTable.OOB, sdNumExSyms)
                .ToArray();
        }

        /// <summary>
        /// The symbols a dictionary exports (T.88 §6.5.10): run lengths alternating exclude and
        /// include, the first an exclude, until every symbol is marked — the arithmetic and the
        /// Huffman dictionary alike. A sequence that selects nothing (a corrupt stream, or one not
        /// using export flags) exports everything, clamped to SDNUMEXSYMS.
        /// </summary>
        /// <remarks>
        /// A run of zero advances nothing, and both decoders read past their data rather than fail —
        /// the MQ decoder pads 0xFF, the bit reader zeros — so a stream that decodes zero runs spun
        /// here for ever. Two zero runs in a row end the sequence: they mark nothing either way, and no
        /// export needs them.
        /// </remarks>
        internal static List<Jbig2Bitmap> ExportedSymbols(
            List<Jbig2Bitmap> allSymbols, Func<int> nextRun, int oob, long sdNumExSyms)
        {
            var exported = new List<Jbig2Bitmap>();
            bool include = false;
            int idx = 0, idleRuns = 0;
            while (idx < allSymbols.Count)
            {
                int runLen;
                try { runLen = nextRun(); }
                catch { break; }
                if (runLen == oob || runLen < 0) break;
                if (runLen == 0) { if (++idleRuns == 2) break; }
                else idleRuns = 0;

                int end = (int)Math.Min((long)idx + runLen, allSymbols.Count);
                if (include)
                    for (int i = idx; i < end; i++) exported.Add(allSymbols[i]);
                idx = end;
                include = !include;
            }

            if (exported.Count == 0)
            {
                exported.AddRange(allSymbols);
                if (sdNumExSyms > 0 && exported.Count > sdNumExSyms)
                    exported.RemoveRange((int)sdNumExSyms, exported.Count - (int)sdNumExSyms);
            }
            return exported;
        }

        /// <summary>
        /// The Huffman tables and symbol-ID coding one Huffman text-region decode reads its instances
        /// with: the strip, first-S and S-delta tables, the four refinement-delta tables and the
        /// refinement size table, and how the symbol ID is coded.
        /// </summary>
        /// <remarks>
        /// <c>SymCodes</c> is null when the IDs are fixed-length (<c>SymCodeLen</c> bits each), which is
        /// how a symbol dictionary's aggregate codes them (§6.5.8.2.3); a text region names a runcode
        /// table instead (§7.4.3.1.7).
        /// </remarks>
        private sealed class HuffmanInstanceCoding
        {
            internal HuffmanInstanceCoding(Jbig2HuffmanTable fs, Jbig2HuffmanTable ds, Jbig2HuffmanTable dt,
                Jbig2HuffmanTable rdw, Jbig2HuffmanTable rdh, Jbig2HuffmanTable rdx, Jbig2HuffmanTable rdy,
                Jbig2HuffmanTable rsize, Jbig2HuffmanTable symCodes, int symCodeLen)
            {
                Fs = fs; Ds = ds; Dt = dt;
                Rdw = rdw; Rdh = rdh; Rdx = rdx; Rdy = rdy; Rsize = rsize;
                SymCodes = symCodes; SymCodeLen = symCodeLen;
            }

            internal Jbig2HuffmanTable Fs { get; }
            internal Jbig2HuffmanTable Ds { get; }
            internal Jbig2HuffmanTable Dt { get; }
            internal Jbig2HuffmanTable Rdw { get; }
            internal Jbig2HuffmanTable Rdh { get; }
            internal Jbig2HuffmanTable Rdx { get; }
            internal Jbig2HuffmanTable Rdy { get; }
            internal Jbig2HuffmanTable Rsize { get; }
            internal Jbig2HuffmanTable SymCodes { get; }
            internal int SymCodeLen { get; }
        }

        /// <summary>
        /// Table 17 (§6.5.8.2.1): the fixed tables a symbol dictionary's aggregate instances are read with.
        /// </summary>
        private static HuffmanInstanceCoding AggregateCoding(int symCodeLen) => new HuffmanInstanceCoding(
            fs: StandardTableF,   // B.6
            ds: StandardTableH,   // B.8
            dt: StandardTableK,   // B.11
            rdw: StandardTableO,  // B.15
            rdh: StandardTableO,  // B.15
            rdx: StandardTableO,  // B.15
            rdy: StandardTableO,  // B.15
            rsize: StandardTableA, // B.1
            symCodes: null,
            symCodeLen: symCodeLen);

        /// <summary>
        /// One aggregated symbol of a Huffman-coded dictionary (§6.5.8.2): either a refinement of one
        /// referred symbol (REFAGGNINST = 1), or a text region of REFAGGNINST instances drawn into the
        /// symbol's box.
        /// </summary>
        private static Jbig2Bitmap DecodeAggregateSymbolHuffman(MsbBitStream hr, HuffmanInstanceCoding coding,
            List<Jbig2Bitmap> pool, int symWidth, int symHeight,
            int sdRTemplate, sbyte[] sdrAt, Jbig2HuffmanTable aggInstTable, int symCodeLen, int[] grStates,
            WorkBudget budget)
        {
            int refAggNinst = aggInstTable.Decode(hr);

            if (refAggNinst == 1)
            {
                // §6.5.8.2.2, Huffman: the symbol ID in SBSYMCODELEN bits, the refinement offset in
                // RDX and RDY, and BMSIZE; then the refinement bitmap, arithmetic-coded over BMSIZE bytes.
                budget?.Tick(TextInstanceUnits);
                int id = (int)hr.ReadBits(symCodeLen);
                int rdx = coding.Rdx.Decode(hr);
                int rdy = coding.Rdy.Decode(hr);
                int bmSize = StandardTableA.Decode(hr);
                if (id < 0 || id >= pool.Count)
                    throw new System.IO.InvalidDataException(
                        $"JBIG2 refinement symbol refers to symbol {id} of {pool.Count}.");
                if (bmSize < 0)
                    throw new System.IO.InvalidDataException("JBIG2 refinement bitmap size is negative.");
                hr.AlignToByte();
                byte[] refinementData = hr.ReadAlignedBytes(bmSize);
                return DecodeHuffmanRefinement(refinementData, grStates, pool[id], symWidth, symHeight, sdRTemplate,
                    rdx, rdy, sdrAt, budget);
            }

            if (refAggNinst > 1)
            {
                var aggregate = AggregateBox(symWidth, symHeight, budget);
                DecodeTextInstancesHuffman(hr, coding, aggregate, 0, 0, combOp: 0,
                    numInstances: refAggNinst, logStrips: 0, dsOffset: 0, refCorner: RefCornerTopLeft,
                    transposed: false, refine: true, sdRTemplate, sdrAt, pool, grStates, budget);
                return aggregate;
            }

            throw new System.IO.InvalidDataException($"JBIG2 aggregate symbol declares {refAggNinst} instances.");
        }

        /// <summary>
        /// Decodes one Huffman-coded refinement bitmap from its own RSIZE/BMSIZE bytes. The refinement
        /// context states persist across the refined instances of one segment (or dictionary), as the
        /// arithmetic path's do, and are carried in <paramref name="states"/>; the arithmetic decoder is
        /// new for each bitmap, since each is its own codeword segment.
        /// </summary>
        private static Jbig2Bitmap DecodeHuffmanRefinement(byte[] data, int[] states, Jbig2Bitmap reference,
            int width, int height, int grTemplate, int refOffsetX, int refOffsetY,
            sbyte[] at, WorkBudget budget)
        {
            budget?.Tick(HuffmanRefinementSetupUnits);
            var mq = new MqDecoder(data, 0, data.Length, TextRegionContexts.RefinementContexts);
            Array.Copy(states, mq.ContextStates, states.Length);
            var result = DecodeRefinementRegion(mq, 0, reference, width, height, grTemplate,
                refOffsetX, refOffsetY, tpgron: false,
                RefinementAt(at, 0), RefinementAt(at, 1), RefinementAt(at, 2), RefinementAt(at, 3), budget);
            Array.Copy(mq.ContextStates, states, states.Length);
            return result;
        }

        /// <summary>
        /// The refinement-delta table selector of Table 31 (SBHUFFRDW, RDH, RDX, RDY): 0 → B.14, 1 → B.15,
        /// 3 → a user-defined table. Reserved 2 refuses.
        /// </summary>
        private static Jbig2HuffmanTable RefinementDeltaTable(int selector, Jbig2HuffmanTable[] custom,
            ref int customIndex, string name) => selector switch
        {
            0 => StandardTableN, // B.14
            1 => StandardTableO, // B.15
            3 => NextCustomTable(custom, ref customIndex, name),
            _ => throw new System.IO.InvalidDataException($"JBIG2 Huffman text region specifies reserved {name} value 2."),
        };

        /// <summary>
        /// SBHUFF=1 text regions (T.88 §6.4, §7.4.3.1.5-§7.4.3.1.7), with per-instance refinement
        /// (§6.4.11) when SBREFINE=1.
        /// </summary>
        /// <remarks>
        /// <para>Table selection (T.88 Table 32): SBHUFFFS 0→B.6, 1→B.7; SBHUFFDS 0→B.8, 1→B.9, 2→B.10;
        /// SBHUFFDT 0→B.11, 1→B.12, 2→B.13; SBHUFFRDW/RDH/RDX/RDY 0→B.14, 1→B.15; SBHUFFRSIZE 0→B.1.
        /// A user-defined selector (3) takes the next custom table, in the order FS, DS, DT, RDW, RDH,
        /// RDX, RDY, RSIZE, as an independent decoder consumes them.</para>
        /// </remarks>
        private static void DecodeTextRegionHuffman(Jbig2BitReader br,
            Jbig2Bitmap page, int regionX, int regionY, int combOp,
            Dictionary<uint, Jbig2Bitmap[]> symbolDicts,
            IReadOnlyList<uint> referredSegmentNumbers,
            Dictionary<uint, Jbig2HuffmanTable> customTables,
            int regionWidth, int regionHeight, int logStrips, int sbDsOffset,
            bool sbRefine, int sbRTemplate, int refCorner, bool transposed, WorkBudget budget)
        {
            _ = regionWidth; _ = regionHeight;

            // A fresh selection/index per segment: this is not shared with the symbol dictionary's own
            // table_index, which is a different segment.
            var custom = SelectCustomTables(referredSegmentNumbers, customTables);
            int customIndex = 0;

            ushort huffmanFlags = br.ReadUInt16BE();
            Jbig2HuffmanTable fsTable = (huffmanFlags & 0x3) switch
            {
                0 => StandardTableF, // B.6
                1 => StandardTableG, // B.7
                3 => NextCustomTable(custom, ref customIndex, "SBHUFFFS"),
                _ => throw new System.IO.InvalidDataException(
                    "JBIG2 Huffman text region specifies reserved SBHUFFFS value 2."),
            };
            Jbig2HuffmanTable dsTable = ((huffmanFlags >> 2) & 0x3) switch
            {
                0 => StandardTableH, // B.8
                1 => StandardTableI, // B.9
                2 => StandardTableJ, // B.10
                _ => NextCustomTable(custom, ref customIndex, "SBHUFFDS"),
            };
            Jbig2HuffmanTable dtTable = ((huffmanFlags >> 4) & 0x3) switch
            {
                0 => StandardTableK, // B.11
                1 => StandardTableL, // B.12
                2 => StandardTableM, // B.13
                _ => NextCustomTable(custom, ref customIndex, "SBHUFFDT"),
            };

            Jbig2HuffmanTable rdwTable = null, rdhTable = null, rdxTable = null, rdyTable = null, rsizeTable = null;
            if (sbRefine)
            {
                rdwTable = RefinementDeltaTable((huffmanFlags >> 6) & 0x3, custom, ref customIndex, "SBHUFFRDW");
                rdhTable = RefinementDeltaTable((huffmanFlags >> 8) & 0x3, custom, ref customIndex, "SBHUFFRDH");
                rdxTable = RefinementDeltaTable((huffmanFlags >> 10) & 0x3, custom, ref customIndex, "SBHUFFRDX");
                rdyTable = RefinementDeltaTable((huffmanFlags >> 12) & 0x3, custom, ref customIndex, "SBHUFFRDY");
                rsizeTable = ((huffmanFlags >> 14) & 0x1) == 0
                    ? StandardTableA // B.1
                    : NextCustomTable(custom, ref customIndex, "SBHUFFRSIZE");
            }

            // T.88 §7.2.7: the refinement AT pixels follow the Huffman flags, when SBREFINE=1 and
            // SBRTEMPLATE=0, and precede SBNUMINSTANCES.
            sbyte[] sbrAt = null;
            if (sbRefine && sbRTemplate == 0)
            {
                sbrAt = new sbyte[4];
                for (int i = 0; i < 4; i++) sbrAt[i] = (sbyte)br.ReadByte();
            }

            uint sbNumInstances = br.ReadUInt32BE();
            if (sbNumInstances > 10_000_000)
                throw new System.IO.InvalidDataException($"JBIG2 instance count out of range: {sbNumInstances}.");

            var symbols = new List<Jbig2Bitmap>();
            if (referredSegmentNumbers != null && symbolDicts != null)
                foreach (uint sn in referredSegmentNumbers)
                    if (symbolDicts.TryGetValue(sn, out var arr))
                        symbols.AddRange(arr);
            if (symbols.Count > 10_000_000)
                throw new System.IO.InvalidDataException($"JBIG2 symbol list out of range: {symbols.Count}.");

            var hr = new MsbBitStream(br.ReadBytes(br.RemainingBytes), 0);

            // T.88 §7.4.3.1.7 — the symbol-ID Huffman table, encoded as a run-length-coded list of
            // per-symbol code lengths (one length per referenced symbol, in ID order).
            var symCodes = BuildSymbolIdHuffmanTable(hr, symbols.Count);

            var coding = new HuffmanInstanceCoding(fsTable, dsTable, dtTable,
                rdwTable, rdhTable, rdxTable, rdyTable, rsizeTable, symCodes, 0);
            DecodeTextInstancesHuffman(hr, coding, page, regionX, regionY, combOp,
                sbNumInstances, logStrips, sbDsOffset, refCorner, transposed, sbRefine, sbRTemplate, sbrAt,
                symbols, new int[TextRegionContexts.RefinementContexts], budget);
        }

        /// <summary>
        /// T.88 §6.4.5's instance loop, Huffman coding: strips, the symbol ID, and with
        /// <paramref name="refine"/> each instance's refinement (§6.4.11). Shared by a text region and by a
        /// symbol dictionary's aggregate, so the strip and instance structure is decoded in one place.
        /// </summary>
        /// <remarks>
        /// A refined instance codes RI as one bit, then RDW, RDH, RDX, RDY and RSIZE through the Huffman
        /// tables, byte-aligns, and carries RSIZE bytes of arithmetic-coded refinement bitmap. The refinement
        /// context states persist across the refined instances of one segment, as the arithmetic path's
        /// do; each bitmap's arithmetic decoder is new, since each is its own codeword segment (GAPS.md §2a).
        /// </remarks>
        private static void DecodeTextInstancesHuffman(MsbBitStream hr, HuffmanInstanceCoding coding,
            Jbig2Bitmap target, int originX, int originY, int combOp, long numInstances,
            int logStrips, int dsOffset, int refCorner, bool transposed, bool refine, int rTemplate,
            sbyte[] rAt, IReadOnlyList<Jbig2Bitmap> symbols, int[] grStates, WorkBudget budget)
        {
            int strips = 1 << logStrips;
            int stripT = -coding.Dt.Decode(hr) * strips;
            int firstS = 0;
            long decoded = 0;

            while (decoded < numInstances)
            {
                int dt = coding.Dt.Decode(hr);
                stripT += dt * strips;

                int dfs = coding.Fs.Decode(hr);
                firstS += dfs;
                int curS = firstS;

                while (true)
                {
                    // Every instance is priced before it draws, as the arithmetic loop's are: the bit
                    // reader fills past the data's end with zeros, so the count is the stream's choice.
                    budget?.Tick(TextInstanceUnits);

                    int curT = strips == 1 ? 0 : (int)hr.ReadBits(logStrips);
                    int it = stripT + curT;

                    int id = coding.SymCodes != null
                        ? coding.SymCodes.Decode(hr)
                        : (int)hr.ReadBits(coding.SymCodeLen);
                    if (id < 0 || id >= symbols.Count)
                        throw new System.IO.InvalidDataException(
                            $"JBIG2 Huffman text region: symbol id {id} out of range (0-{symbols.Count - 1}).");

                    Jbig2Bitmap symbol = symbols[id];
                    if (refine && hr.ReadBit() == 1)
                    {
                        int rdw = coding.Rdw.Decode(hr);
                        int rdh = coding.Rdh.Decode(hr);
                        int rdx = coding.Rdx.Decode(hr);
                        int rdy = coding.Rdy.Decode(hr);
                        int rsize = coding.Rsize.Decode(hr);
                        if (rsize < 0)
                            throw new System.IO.InvalidDataException("JBIG2 refinement size is negative.");
                        hr.AlignToByte();
                        byte[] refinementData = hr.ReadAlignedBytes(rsize);

                        symbol = DecodeHuffmanRefinement(refinementData, grStates, symbol,
                            symbol.Width + rdw, symbol.Height + rdh, rTemplate,
                            (rdw >> 1) + rdx, (rdh >> 1) + rdy, rAt, budget);
                    }

                    (int x, int y) = PlaceTextInstance(ref curS, it, symbol.Width, symbol.Height, transposed, refCorner);
                    target.DrawAt(symbol, originX + x, originY + y, combOp, budget);
                    decoded++;

                    // A strip ends on an OOB IDS, the last one included, as in the arithmetic path.
                    int ds = coding.Ds.Decode(hr);
                    if (ds == Jbig2HuffmanTable.OOB) break;
                    if (decoded >= numInstances) break;
                    curS += ds + dsOffset;
                }
            }
        }

        /// <summary>
        /// T.88 §7.4.3.1.7 — decodes the symbol-ID Huffman table a Huffman text region needs to
        /// select which referenced symbol each instance draws.
        /// </summary>
        /// <remarks>
        /// <para>Two layers of Huffman coding, not one. First, 35 <i>runcode</i> lengths (4 raw
        /// bits each — a fixed-width preamble, not itself Huffman-coded) build a small table over
        /// values 0-34. That runcode table is then used to decode <paramref name="symbolCount"/>
        /// per-symbol code <i>lengths</i>: codes 0-31 mean "this symbol's own code length is the
        /// runcode value itself"; 32 means "repeat the previous symbol's length" (2 extra raw bits
        /// give the repeat count, 3-6); 33 means "the next symbols have length 0" (3 extra bits,
        /// count 3-10); 34 is the same but with 7 extra bits (count 11-138). Only once every
        /// symbol's code length is known does the actual symbol-ID table get built — via
        /// <see cref="AssignCanonicalPrefixes"/>, the same canonical-from-PREFLEN construction
        /// every other table in this file uses, never hand-assigned.</para>
        /// <para>A symbol with code length 0 is legitimate — it means that symbol is never
        /// referenced by this text region — and needs no special handling here:
        /// <see cref="Jbig2HuffmanTable.Decode"/>'s scan starts at length 1, so a length-0 entry
        /// can never match a real bit pattern, the same structural guarantee the standard tables'
        /// own occasional zero-length rows already rely on.</para>
        /// </remarks>
        private static Jbig2HuffmanTable BuildSymbolIdHuffmanTable(MsbBitStream hr, int symbolCount)
        {
            var runcodeRows = new List<Jbig2HuffmanEntry>(35);
            for (int i = 0; i < 35; i++)
                runcodeRows.Add(new Jbig2HuffmanEntry
                {
                    PrefixLen = hr.ReadBits(4),
                    Prefix = 0,
                    RangeLow = i,
                    RangeBits = 0,
                });
            AssignCanonicalPrefixes(runcodeRows);
            var runcodeTable = new Jbig2HuffmanTable(runcodeRows.ToArray());

            var symbolLengths = new int[symbolCount];
            int index = 0;
            while (index < symbolCount)
            {
                int code = runcodeTable.Decode(hr);
                if (code < 0 || code >= 35)
                    throw new System.IO.InvalidDataException(
                        $"JBIG2 Huffman text region: invalid symbol-ID runcode {code}.");

                int len, range;
                if (code < 32) { len = code; range = 1; }
                else if (code == 32)
                {
                    if (index < 1)
                        throw new System.IO.InvalidDataException(
                            "JBIG2 Huffman text region: symbol-ID run-length code 32 (repeat " +
                            "previous) with no preceding symbol.");
                    len = symbolLengths[index - 1];
                    range = hr.ReadBits(2) + 3;
                }
                else if (code == 33) { len = 0; range = hr.ReadBits(3) + 3; }
                else /* 34 */         { len = 0; range = hr.ReadBits(7) + 11; }

                if (index + range > symbolCount) range = symbolCount - index;
                for (int r = 0; r < range; r++) symbolLengths[index + r] = len;
                index += range;
            }

            hr.AlignToByte();

            var symbolRows = new List<Jbig2HuffmanEntry>(symbolCount);
            for (int i = 0; i < symbolCount; i++)
                symbolRows.Add(new Jbig2HuffmanEntry
                {
                    PrefixLen = symbolLengths[i],
                    Prefix = 0,
                    RangeLow = i,
                    RangeBits = 0,
                });
            AssignCanonicalPrefixes(symbolRows);
            return new Jbig2HuffmanTable(symbolRows.ToArray());
        }

        // ==================================================================
        //  Text Region Decoder (T.88 §6.4)
        //
        //  Decodes a sequence of symbol references at specific (x, y) positions
        //  into the region bitmap.  For each instance, decodes: strip-Δ,
        //  first-S / subsequent-DS deltas, transposed-T-Δ (within-strip), symbol
        //  ID, and optionally a refinement bitmap.  Copies the symbol bitmap
        //  onto the region via the combination operator.
        //
        //  Scope — SBHUFF=0 decodes here and works.  SBHUFF=1 routes to
        //  DecodeTextRegionHuffman, which REFUSES: the symbol-ID rule and the
        //  Annex B tables it would need are both wrong, so it would draw real
        //  symbols in the wrong places rather than fail visibly.  Per-instance
        //  refinement (SBREFINE=1) is decoded in the arithmetic path
        //  (DecodeTextRegionInstances) and refused in the Huffman one.
        // ==================================================================

        private static void DecodeTextRegion(byte[] body, Jbig2Bitmap page, int regionX, int regionY,
            int combOp, Dictionary<uint, Jbig2Bitmap[]> symbolDicts,
            IReadOnlyList<uint> referredSegmentNumbers, WorkBudget budget,
            Dictionary<uint, Jbig2HuffmanTable> customTables = null)
        {
            var br = new Jbig2BitReader(body);
            // Region info (17 bytes already handled by caller — body starts AFTER it).
            // Actually caller will pass us the body including region-info; parse here.
            uint regionWidth  = br.ReadUInt32BE();
            uint regionHeight = br.ReadUInt32BE();
            /* regionX not used from this dict */ br.ReadUInt32BE();
            /* regionY */ br.ReadUInt32BE();
            /* regionFlags: the caller reads the combination operator */ br.ReadByte();

            ushort sbFlags = br.ReadUInt16BE();
            bool sbHuff    = (sbFlags & 0x0001) != 0;
            bool sbRefine  = (sbFlags & 0x0002) != 0;
            int logSbStrips = (sbFlags >> 2) & 0x03;
            int refCorner   = (sbFlags >> 4) & 0x03;
            bool transposed = ((sbFlags >> 6) & 0x1) != 0;
            int sbCombOp    = (sbFlags >> 7) & 0x3;
            bool sbDefPixel = ((sbFlags >> 9) & 0x1) != 0;
            int sbDsOffset  = (sbFlags >> 10) & 0x1F;
            if ((sbDsOffset & 0x10) != 0) sbDsOffset |= unchecked((int)0xFFFFFFE0); // sign-extend 5 bits
            int sbRTemplate = (sbFlags >> 15) & 0x1;

            // §6.4.5: the region is a bitmap of its own, filled with SBDEFPIXEL; each instance is
            // drawn on it with SBCOMBOP, and the region then goes onto the page with the segment's
            // operator. Earlier, instances went straight onto the page with the segment's operator,
            // unclipped by the region: SBCOMBOP was approximated and SBDEFPIXEL refused. Both are
            // honoured now; TRANSPOSED is handled by PlaceTextInstance.
            var region = RegionBitmap((int)regionWidth, (int)regionHeight, sbDefPixel, budget);

            if (sbHuff)
            {
                DecodeTextRegionHuffman(br, region, 0, 0, sbCombOp,
                    symbolDicts, referredSegmentNumbers, customTables, (int)regionWidth,
                    (int)regionHeight, logSbStrips, sbDsOffset, sbRefine, sbRTemplate,
                    refCorner, transposed, budget);
                page.DrawAt(region, regionX, regionY, combOp, budget);
                return;
            }

            int strips = 1 << logSbStrips;

            // Refinement AT pixels (4 bytes, only when SBREFINE=1 and SBRTEMPLATE=0).
            sbyte[] sbrAt = null;
            if (sbRefine && sbRTemplate == 0)
            {
                sbrAt = new sbyte[4];
                for (int i = 0; i < 4; i++) sbrAt[i] = (sbyte)br.ReadByte();
            }

            uint sbNumInstances = br.ReadUInt32BE();
            // Cap so an adversarial instance count cannot drive a billion-iteration
            // decode loop (the MQ decoder pads past EOF rather than terminating).
            if (sbNumInstances > 10_000_000)
                throw new System.IO.InvalidDataException($"JBIG2 instance count out of range: {sbNumInstances}.");

            // Flatten all referred-to symbols into a single ID-indexed array.
            var symbols = new List<Jbig2Bitmap>();
            if (referredSegmentNumbers != null && symbolDicts != null)
            {
                foreach (uint sn in referredSegmentNumbers)
                    if (symbolDicts.TryGetValue(sn, out var arr))
                        symbols.AddRange(arr);
            }

            // Arithmetic stream begins at the current reader position.
            byte[] arithData = br.ReadBytes(br.RemainingBytes);

            int symCodeLen = SymbolCodeLength(symbols.Count);
            var mq = new MqDecoder(arithData, 0, TextRegionContexts.Size(symCodeLen));
            DecodeTextRegionInstances(new TextRegionContexts(mq, 0, symCodeLen), region, 0, 0,
                sbCombOp, sbNumInstances, strips, sbDsOffset, refCorner, transposed, sbRefine, sbRTemplate,
                sbrAt, symbols, budget);

            page.DrawAt(region, regionX, regionY, combOp, budget);
        }

        /// <summary>
        /// SBSYMCODELEN, ⌈log₂ <paramref name="symbolCount"/>⌉ (T.88 §6.4.4; §6.5.8.2.3 for an
        /// aggregate). One symbol needs no bits.
        /// </summary>
        /// <remarks>
        /// This was never below 1, so a text region over a one-symbol dictionary read a bit per
        /// instance that the stream does not hold. Independent decoders count up from 0, as here.
        /// </remarks>
        internal static int SymbolCodeLength(long symbolCount)
        {
            int length = 0;
            while ((1L << length) < symbolCount) length++;
            return length;
        }

        private static sbyte RefinementAt(sbyte[] at, int index)
            => at != null && index < at.Length ? at[index] : (sbyte)-1;

        /// <summary>
        /// The arithmetic decoders a text region uses (T.88 §6.4, Annex A), in disjoint ranges of one
        /// MQ decoder: the nine integer decoders, IAID's 2^SBSYMCODELEN contexts, and the refinement
        /// contexts. A symbol dictionary's aggregates share one set across every symbol.
        /// </summary>
        private sealed class TextRegionContexts
        {
            internal const int RefinementContexts = 1 << 13;

            internal readonly MqDecoder Mq;
            internal readonly Jbig2IntegerDecoder Iadt, Iafs, Iads, Iait, Iari, Iardw, Iardh, Iardx, Iardy;
            internal readonly int IaidBase, SymCodeLen, GrBase;

            internal static int Size(int symCodeLen)
                => 9 * Jbig2IntegerDecoder.ContextTableSize + IaidSize(symCodeLen) + RefinementContexts;

            private static int IaidSize(int symCodeLen) => 1 << Math.Min(symCodeLen, 16);

            internal TextRegionContexts(MqDecoder mq, int baseContext, int symCodeLen)
            {
                Mq = mq;
                int at = baseContext;
                Jbig2IntegerDecoder Next() { var d = new Jbig2IntegerDecoder(mq, at); at += Jbig2IntegerDecoder.ContextTableSize; return d; }
                Iadt = Next(); Iafs = Next(); Iads = Next(); Iait = Next(); Iari = Next();
                Iardw = Next(); Iardh = Next(); Iardx = Next(); Iardy = Next();
                SymCodeLen = symCodeLen;
                IaidBase = at;
                GrBase = at + IaidSize(symCodeLen);
            }
        }

        /// <summary>
        /// T.88 §6.4.5's instance loop, arithmetic coding: strips, the symbol ID, and with
        /// <paramref name="refine"/> each instance's refinement (§6.4.11).
        /// </summary>
        /// <remarks>
        /// <para><b>Refinement (SBREFINE=1) is decoded.</b> Each instance codes RI; a refined one codes
        /// RDW, RDH, RDX and RDY and a refinement bitmap of the symbol grown by (RDW, RDH), its
        /// reference offset by (⌊RDW/2⌋ + RDX, ⌊RDH/2⌋ + RDY). Drawing every instance as a plain copy would put the
        /// decoder out of step at the first refined instance, and the rest of the region would be
        /// noise.</para>
        /// <para><b>A strip ends on an OOB IDS, the last one included,</b> as in independent decoders.
        /// The loop stopped before the last IDS. At a region's end that is harmless; inside a symbol
        /// dictionary's aggregate the stream goes on, and it was then out of step.</para>
        /// </remarks>
        private static void DecodeTextRegionInstances(TextRegionContexts st, Jbig2Bitmap target,
            int originX, int originY, int combOp, long numInstances, int strips, int dsOffset,
            int refCorner, bool transposed, bool refine, int rTemplate, sbyte[] rAt,
            IReadOnlyList<Jbig2Bitmap> symbols, WorkBudget budget)
        {
            int stripT = -st.Iadt.Decode() * strips;
            int firstS = 0;
            long decoded = 0;

            while (decoded < numInstances)
            {
                int dt = st.Iadt.Decode();
                int dfs = dt == Jbig2IntegerDecoder.OOB ? dt : st.Iafs.Decode();
                if (dfs == Jbig2IntegerDecoder.OOB) break;   // not legal here; stop rather than spin
                stripT += dt * strips;
                firstS += dfs;
                int curS = firstS;

                while (true)
                {
                    // An instance is work before it draws anything — five integer decodes and a
                    // symbol ID, some 350 ns — and one naming no symbol draws nothing, so it was
                    // charged nothing: a 121-byte dictionary declaring billions of aggregate
                    // instances of a symbol it did not have ran for four minutes inside the budget
                    // (the coverage-guided fuzzer, GAPS.md §7c). Priced in pixels, up front.
                    budget?.Tick(TextInstanceUnits);

                    int curT = strips == 1 ? 0 : st.Iait.Decode();
                    int it = stripT + curT;

                    int id = DecodeSymbolId(st.Mq, st.IaidBase, st.SymCodeLen);
                    Jbig2Bitmap symbol = id >= 0 && id < symbols.Count ? symbols[id] : null;

                    if (refine && st.Iari.Decode() == 1)
                    {
                        int rdw = st.Iardw.Decode(), rdh = st.Iardh.Decode();
                        int rdx = st.Iardx.Decode(), rdy = st.Iardy.Decode();
                        int width = (symbol?.Width ?? 0) + rdw, height = (symbol?.Height ?? 0) + rdh;
                        symbol = DecodeRefinementRegion(st.Mq, st.GrBase, symbol, width, height, rTemplate,
                            refOffsetX: (rdw >> 1) + rdx, refOffsetY: (rdh >> 1) + rdy, tpgron: false,
                            RefinementAt(rAt, 0), RefinementAt(rAt, 1),
                            RefinementAt(rAt, 2), RefinementAt(rAt, 3), budget);
                    }

                    if (symbol != null)
                    {
                        (int x, int y) = PlaceTextInstance(ref curS, it, symbol.Width, symbol.Height, transposed, refCorner);
                        target.DrawAt(symbol, originX + x, originY + y, combOp, budget);
                    }
                    decoded++;

                    int ds = st.Iads.Decode();
                    if (ds == Jbig2IntegerDecoder.OOB) break;
                    if (decoded >= numInstances) break;
                    curS += ds + dsOffset;
                }
            }
        }

        /// <summary>
        /// Where a text-region instance goes, relative to the region, and how far it moves CURS
        /// (T.88 §6.4.5 3c.vi–x, as an independent decoder does it).
        /// </summary>
        /// <remarks>
        /// Not transposed, S runs across: the symbol's left edge is CURS before the instance, its
        /// top row follows <see cref="TextInstanceTopRow"/>, and CURS moves by its width − 1.
        /// Transposed, S runs down and T across: the top row is CURS before the instance, the left
        /// column is T, or T − width + 1 for a RIGHT corner, and CURS moves by its height − 1.
        /// </remarks>
        internal static (int X, int Y) PlaceTextInstance(ref int curS, int t, int width, int height,
            bool transposed, int refCorner)
        {
            if (!transposed)
            {
                int x = curS;
                curS += width - 1;
                return (x, TextInstanceTopRow(t, height, refCorner));
            }

            bool right = refCorner == RefCornerBottomRight || refCorner == RefCornerTopRight;
            int y = curS;
            curS += height - 1;
            return (right ? t - width + 1 : t, y);
        }

        /// <summary>
        /// The page row the top of a text-region instance occupies, given the instance's T
        /// coordinate, the symbol's height and REFCORNER (T.88 §6.4.5, non-transposed).
        /// </summary>
        /// <remarks>
        /// <para><b>REFCORNER names which corner of the symbol the (S, T) coordinate is.</b> The
        /// two <c>BOTTOM</c> values therefore put the symbol's <i>last</i> row at T, so its first
        /// row is <c>T - height + 1</c>; the two <c>TOP</c> values put its first row at T
        /// directly. T.88 Table 34 numbers them 0 = BOTTOMLEFT, 1 = TOPLEFT, 2 = BOTTOMRIGHT,
        /// 3 = TOPRIGHT.</para>
        /// <para><b>A caller ignoring REFCORNER</b> would place every instance as if it were
        /// TOPLEFT, so a region using either BOTTOM corner would draw every symbol
        /// <c>height - 1</c> rows too low — and, because glyph heights differ, by a different amount
        /// per symbol, while <see cref="Assess"/> called the segment <c>Decoded</c> — a silent
        /// misplacement.</para>
        /// <para><b>The left/right half needs no adjustment here, and that is a property of the
        /// algorithm rather than an omission.</b> In non-transposed mode CURS advances by
        /// <c>width - 1</c> around the draw: a LEFT corner draws at CURS and then advances, a
        /// RIGHT corner advances and then draws with its right edge at the new CURS. Both leave
        /// the symbol's left edge at the pre-advance CURS and both leave CURS at the same value,
        /// so x is identical for all four corners. Under TRANSPOSED=1 the axes swap and it is
        /// left/right that needs the adjustment, which <see cref="PlaceTextInstance"/> makes.</para>
        /// </remarks>
        internal static int TextInstanceTopRow(int t, int symbolHeight, int refCorner)
            => refCorner == RefCornerBottomLeft || refCorner == RefCornerBottomRight
                ? t - symbolHeight + 1
                : t;

        private const int RefCornerBottomLeft  = 0;
        private const int RefCornerTopLeft     = 1;
        private const int RefCornerBottomRight = 2;
        private const int RefCornerTopRight    = 3;

        /// <summary>
        /// Decodes a symbol-ID value by reading <paramref name="idBits"/> bits
        /// from the MQ stream using successive contexts starting at 1.  Per
        /// T.88 §6.4.5: each bit uses context (prev &lt;&lt; 1) | bit, clamped
        /// into the allocated table.
        /// </summary>
        internal static int DecodeSymbolId(MqDecoder mq, int baseContext, int idBits)
        {
            if (idBits <= 0) return 0;
            int prev = 1;
            int tableSize = 1 << Math.Min(idBits, 16);
            int value = 0;
            for (int i = 0; i < idBits; i++)
            {
                int ctx = baseContext + (prev % tableSize);
                int bit = mq.Decode(ctx);
                value = (value << 1) | bit;
                prev = (prev << 1) | bit;
            }
            return value;
        }

        // ==================================================================
        //  Immediate refinement region segment dispatch (T.88 §7.4.7)
        //
        //  Parses the refinement region segment body, locates the reference
        //  bitmap (provided by the caller via a referred-to segment), and
        //  invokes DecodeRefinementRegion for the actual pixel work.
        // ==================================================================

        /// <summary>
        /// Decodes a refinement region segment (T.88 §7.4.7) and returns the refined bitmap; the
        /// caller draws it (immediate) or keeps it (intermediate).
        /// </summary>
        /// <param name="body">The segment body.</param>
        /// <param name="page">The page, the reference when no region is referred to.</param>
        /// <param name="reference">The referred-to region's bitmap, or <c>null</c>.</param>
        /// <param name="refersToRegion">Whether the segment named a region to refine.</param>
        /// <param name="budget">The decode's work budget.</param>
        /// <param name="info">The region's information field.</param>
        /// <remarks>
        /// <b>With no referred-to region the reference is the page under the region</b> (§7.4.7.4:
        /// "the reference bitmap is ... the current page buffer region"). It was <c>null</c>, read
        /// as all white, so a refinement of what the page already held refined nothing. Independent decoders        /// pass the whole page with a TODO to take the subset; the subset is taken here, which is
        /// the same thing for a region at the page origin.
        /// </remarks>
        private static Jbig2Bitmap DecodeRefinementSegment(
            byte[] body, Jbig2Bitmap page, Jbig2Bitmap reference, bool refersToRegion,
            WorkBudget budget, out Jbig2RegionInfo info)
        {
            var br = new Jbig2BitReader(body);
            info = ReadRegionInfo(br);
            uint width  = info.Width;
            uint height = info.Height;

            byte grFlags = br.ReadByte();
            int grTemplate = grFlags & 0x01;
            bool tpgrOn    = (grFlags & 0x02) != 0;

            sbyte at1x = 0, at1y = 0, at2x = 0, at2y = 0;
            if (grTemplate == 0)
            {
                at1x = (sbyte)br.ReadByte();
                at1y = (sbyte)br.ReadByte();
                at2x = (sbyte)br.ReadByte();
                at2y = (sbyte)br.ReadByte();
            }

            byte[] arithData = br.ReadBytes(br.RemainingBytes);
            int contextBits = grTemplate == 0 ? 13 : 10;
            var mq = new MqDecoder(arithData, 0, 1 << contextBits);

            if (reference == null && !refersToRegion)
            {
                reference = new Jbig2Bitmap((int)width, (int)height);
                reference.DrawAt(page, -(int)info.X, -(int)info.Y, 4, budget);
            }

            return DecodeRefinementRegion(mq, 0, reference,
                (int)width, (int)height, grTemplate,
                refOffsetX: 0, refOffsetY: 0, tpgron: tpgrOn,
                at1x, at1y, at2x, at2y, budget);
        }

        // ==================================================================
        //  Huffman Decoder Framework (T.88 Annex B)
        //
        //  The bit reader feeds a prefix-code table (Jbig2HuffmanTable) whose
        //  entries pair a prefix bit pattern with a range-base value plus a
        //  number of additional value bits to read.  Standard tables B.1..B.15
        //  are provided; user-defined tables communicated through dedicated
        //  "Tables" segments are not yet parsed.  If a Huffman path encounters
        //  a user-defined-table request, it throws → filter falls back.
        // ==================================================================

        internal struct Jbig2HuffmanEntry
        {
            public int PrefixLen;     // bits in the prefix codeword
            public int Prefix;        // codeword value
            public int RangeLow;      // base value
            public int RangeBits;     // extra bits to read (0 = fixed value, -1 = OOB, -2 = 32-bit signed)
            public bool LowerSign;    // true for a lower-range line: decode as (RangeLow - extra), counting downward
        }

        internal sealed class Jbig2HuffmanTable
        {
            public Jbig2HuffmanEntry[] Entries { get; }

            public Jbig2HuffmanTable(Jbig2HuffmanEntry[] entries) { Entries = entries; }

            /// <summary>OOB sentinel used by tables that carry out-of-band termination.</summary>
            public const int OOB = int.MinValue;

            public int Decode(MsbBitStream br)
            {
                int code = 0;
                for (int len = 1; len <= 32; len++)
                {
                    int bit = br.ReadBit();
                    if (bit < 0) throw new System.IO.InvalidDataException("Huffman: unexpected EOF");
                    code = (code << 1) | bit;
                    foreach (var e in Entries)
                    {
                        if (e.PrefixLen == len && e.Prefix == code)
                        {
                            if (e.RangeBits == -1) return OOB;
                            int extra;
                            if (e.RangeBits == -2)
                            {
                                extra = br.ReadBits(32);
                            }
                            else
                            {
                                extra = e.RangeBits > 0 ? br.ReadBits(e.RangeBits) : 0;
                            }
                            // T.88 Annex B: a normal (ascending) or high-range line ADDS the extra
                            // bits to RangeLow; a lower-range line COUNTS DOWNWARD from RangeLow —
                            // i.e. subtracts, not negates. (RangeLow - extra), not -(RangeLow +
                            // extra). Confirmed against an independent decoder: "if (ISLOW)
                            // result -= HTOFFSET; else result += HTOFFSET;" — the two are the same
                            // only when RangeLow is 0, which is never true for a real lower-range
                            // line (its RangeLow is always one less than the ascending sequence's
                            // lowest value).
                            return e.LowerSign ? e.RangeLow - extra : e.RangeLow + extra;
                        }
                    }
                }
                throw new System.IO.InvalidDataException("Huffman: unresolved prefix");
            }
        }

        /// <summary>
        /// Builds a standard table from its (PREFLEN, RANGELEN, RANGELOW[, isLowerRange]) rows —
        /// the shape ITU-T T.88 Annex B itself prints them in. Prefix <i>codes</i> are never
        /// hand-assigned here; <see cref="AssignCanonicalPrefixes"/> computes them from PREFLEN
        /// alone, the same procedure the user-defined-table path
        /// (<see cref="ParseUserDefinedHuffmanTable"/>) already uses. This is deliberate:
        /// hand-assigned prefix codes go unchecked against their own PREFLEN column, and can violate
        /// Kraft equality outright. Letting one procedure compute every code removes that entire class of error — a wrong
        /// PREFLEN is still possible, but a wrong <i>code</i> for a right PREFLEN is not.
        /// A rangeLen of -1 marks an OOB row (no range, no extra bits).
        /// </summary>
        private static Jbig2HuffmanTable BuildStandardTable(params (int prefLen, int rangeLen, int rangeLow, bool isLow)[] rows)
        {
            var list = new List<Jbig2HuffmanEntry>(rows.Length);
            foreach (var r in rows)
            {
                list.Add(new Jbig2HuffmanEntry
                {
                    PrefixLen = r.prefLen,
                    Prefix    = 0,
                    RangeLow  = r.rangeLow,
                    RangeBits = r.rangeLen,
                    LowerSign = r.isLow,
                });
            }
            AssignCanonicalPrefixes(list);
            return new Jbig2HuffmanTable(list.ToArray());
        }

        /// <summary>Row without a lower-range flag — the common case.</summary>
        private static (int prefLen, int rangeLen, int rangeLow, bool isLow) Row(int prefLen, int rangeLen, int rangeLow)
            => (prefLen, rangeLen, rangeLow, false);

        /// <summary>The lower-range row of a table that has one — decodes as (RangeLow - extra).</summary>
        private static (int prefLen, int rangeLen, int rangeLow, bool isLow) LowRow(int prefLen, int rangeLow)
            => (prefLen, 32, rangeLow, true);

        // ==================================================================
        //  T.88 Annex B — standard Huffman tables B.1 through B.15.
        //
        //  Every (PREFLEN, RANGELEN, RANGELOW) triple below was cross-checked between two
        //  independent, mature implementations, read for their DATA only — no source from either
        //  was copied into this file; the triples are facts about the standard, not the two
        //  projects' expression of them. They agree on every row, B.11's third row included
        //  (RANGELOW 4 in both, tiling correctly against the row before it: RANGELOW 2,
        //  RANGELEN 1 → covers [2,3], so the next range starts at 4) — an earlier pass through
        //  this file claimed the two sources disagreed there (RANGELOW 3). Re-verified directly
        //  against both: both read RANGELOW 4 at that row, and always have. The claimed
        //  discrepancy did not exist; it was a misreading in an earlier pass, not a finding, and
        //  this comment is corrected rather than left standing now that it has been rechecked.
        //
        //  Which row (if any) is the lower-range row is taken from one implementation's explicit
        //  tagging, not inferred from position: it marks a lower-range row directly (a 4th tuple
        //  element), while the other computes it from the row's position in the array in a way this
        //  implementation could not independently confirm agreed for tables with no real lower
        //  range (B.11-B.13) — rather than guess, the explicit source is the one trusted for this
        //  bit. The decode semantics for a lower-range row (RangeLow - extra, i.e. counting
        //  downward) are T.88 Annex B's: a lower-range row subtracts the offset from its low bound,
        //  every other row adds it, with no position-dependent inference.
        //
        //  A verified oracle: an independent decoder's own self-test decodes the byte stream
        //  { 0xe9, 0xcb, 0xf4, 0x00 } through tables { B.4, B.2, B.2, B.1 } in that order to
        //  { 8, 5, OOB, 8 } — ported as Jbig2StandardHuffmanTableOracleTests.
        // ==================================================================

        /// <summary>Table B.1 — Standard Huffman Table A.  1-bit prefix = small range.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableA = BuildStandardTable(
            Row(1, 4, 0),
            Row(2, 8, 16),
            Row(3, 16, 272),
            Row(3, 32, 65808));

        /// <summary>Table B.2 — unbounded non-negative integer, with OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableB = BuildStandardTable(
            Row(1, 0, 0),
            Row(2, 0, 1),
            Row(3, 0, 2),
            Row(4, 3, 3),
            Row(5, 6, 11),
            Row(6, 32, 75),
            (6, -1, 0, false)); // OOB

        /// <summary>Table B.3 — signed integer, with a lower range and OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableC = BuildStandardTable(
            Row(8, 8, -256),
            Row(1, 0, 0),
            Row(2, 0, 1),
            Row(3, 0, 2),
            Row(4, 3, 3),
            Row(5, 6, 11),
            LowRow(8, -257),
            Row(7, 32, 75),
            (6, -1, 0, false)); // OOB

        /// <summary>Table B.4 — small non-negative integer starting at 1, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableD = BuildStandardTable(
            Row(1, 0, 1),
            Row(2, 0, 2),
            Row(3, 0, 3),
            Row(4, 3, 4),
            Row(5, 6, 12),
            Row(5, 32, 76));

        /// <summary>Table B.5 — signed integer starting at 1, with a lower range, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableE = BuildStandardTable(
            Row(7, 8, -255),
            Row(1, 0, 1),
            Row(2, 0, 2),
            Row(3, 0, 3),
            Row(4, 3, 4),
            Row(5, 6, 12),
            LowRow(7, -256),
            Row(6, 32, 76));

        /// <summary>Table B.6 — signed integer (symbol-dictionary delta height), with a lower
        /// range, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableF = BuildStandardTable(
            Row(5, 10, -2048),
            Row(4, 9, -1024),
            Row(4, 8, -512),
            Row(4, 7, -256),
            Row(5, 6, -128),
            Row(5, 5, -64),
            Row(4, 5, -32),
            Row(2, 7, 0),
            Row(3, 7, 128),
            Row(3, 8, 256),
            Row(4, 9, 512),
            Row(4, 10, 1024),
            LowRow(6, -2049),
            Row(6, 32, 2048));

        /// <summary>Table B.7 — signed integer (symbol-dictionary delta width), with a lower
        /// range, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableG = BuildStandardTable(
            Row(4, 9, -1024),
            Row(3, 8, -512),
            Row(4, 7, -256),
            Row(5, 6, -128),
            Row(5, 5, -64),
            Row(4, 5, -32),
            Row(4, 5, 0),
            Row(5, 5, 32),
            Row(5, 6, 64),
            Row(4, 7, 128),
            Row(3, 8, 256),
            Row(3, 9, 512),
            Row(3, 10, 1024),
            LowRow(5, -1025),
            Row(5, 32, 2048));

        /// <summary>Table B.8 — text-region delta-S, with a lower range and OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableH = BuildStandardTable(
            Row(8, 3, -15),
            Row(9, 1, -7),
            Row(8, 1, -5),
            Row(9, 0, -3),
            Row(7, 0, -2),
            Row(4, 0, -1),
            Row(2, 1, 0),
            Row(5, 0, 2),
            Row(6, 0, 3),
            Row(3, 4, 4),
            Row(6, 1, 20),
            Row(4, 4, 22),
            Row(4, 5, 38),
            Row(5, 6, 70),
            Row(5, 7, 134),
            Row(6, 7, 262),
            Row(7, 8, 390),
            Row(6, 10, 646),
            LowRow(9, -16),
            Row(9, 32, 1670),
            (2, -1, 0, false)); // OOB

        /// <summary>Table B.9 — text-region delta-S variant, with a lower range and OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableI = BuildStandardTable(
            Row(8, 4, -31),
            Row(9, 2, -15),
            Row(8, 2, -11),
            Row(9, 1, -7),
            Row(7, 1, -5),
            Row(4, 1, -3),
            Row(3, 1, -1),
            Row(3, 1, 1),
            Row(5, 1, 3),
            Row(6, 1, 5),
            Row(3, 5, 7),
            Row(6, 2, 39),
            Row(4, 5, 43),
            Row(4, 6, 75),
            Row(5, 7, 139),
            Row(5, 8, 267),
            Row(6, 8, 523),
            Row(7, 9, 779),
            Row(6, 11, 1291),
            LowRow(9, -32),
            Row(9, 32, 3339),
            (2, -1, 0, false)); // OOB

        /// <summary>Table B.10 — text-region delta-T, with a lower range and OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableJ = BuildStandardTable(
            Row(7, 4, -21),
            Row(8, 0, -5),
            Row(7, 0, -4),
            Row(5, 0, -3),
            Row(2, 2, -2),
            Row(5, 0, 2),
            Row(6, 0, 3),
            Row(7, 0, 4),
            Row(8, 0, 5),
            Row(2, 6, 6),
            Row(5, 5, 70),
            Row(6, 5, 102),
            Row(6, 6, 134),
            Row(6, 7, 198),
            Row(6, 8, 326),
            Row(6, 9, 582),
            Row(6, 10, 1094),
            Row(7, 11, 2118),
            LowRow(8, -22),
            Row(8, 32, 4166),
            (2, -1, 0, false)); // OOB

        /// <summary>Table B.11 — symbol ID code lengths, no lower range, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableK = BuildStandardTable(
            Row(1, 0, 1),
            Row(2, 1, 2),
            Row(4, 0, 4),
            Row(4, 1, 5),
            Row(5, 1, 7),
            Row(5, 2, 9),
            Row(6, 2, 13),
            Row(7, 2, 17),
            Row(7, 3, 21),
            Row(7, 4, 29),
            Row(7, 5, 45),
            Row(7, 6, 77),
            Row(7, 32, 141));

        /// <summary>Table B.12 — symbol ID code lengths variant, no lower range, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableL = BuildStandardTable(
            Row(1, 0, 1),
            Row(2, 0, 2),
            Row(3, 1, 3),
            Row(5, 0, 5),
            Row(5, 1, 6),
            Row(6, 1, 8),
            Row(7, 0, 10),
            Row(7, 1, 11),
            Row(7, 2, 13),
            Row(7, 3, 17),
            Row(7, 4, 25),
            Row(8, 5, 41),
            Row(8, 32, 73));

        /// <summary>Table B.13 — symbol ID code lengths variant, no lower range, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableM = BuildStandardTable(
            Row(1, 0, 1),
            Row(3, 0, 2),
            Row(4, 0, 3),
            Row(5, 0, 4),
            Row(4, 1, 5),
            Row(3, 3, 7),
            Row(6, 1, 15),
            Row(6, 2, 17),
            Row(6, 3, 21),
            Row(6, 4, 29),
            Row(6, 5, 45),
            Row(7, 6, 77),
            Row(7, 32, 141));

        /// <summary>Table B.14 — small closed range [-2, 2], no lower range, no OOB, no unbounded
        /// tail: every codeword resolves directly with no extra bits.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableN = BuildStandardTable(
            Row(3, 0, -2),
            Row(3, 0, -1),
            Row(1, 0, 0),
            Row(3, 0, 1),
            Row(3, 0, 2));

        /// <summary>Table B.15 — signed integer, with a lower range, no OOB.</summary>
        internal static readonly Jbig2HuffmanTable StandardTableO = BuildStandardTable(
            Row(7, 4, -24),
            Row(6, 2, -8),
            Row(5, 1, -4),
            Row(4, 0, -2),
            Row(3, 0, -1),
            Row(1, 0, 0),
            Row(3, 0, 1),
            Row(4, 0, 2),
            Row(5, 1, 3),
            Row(6, 2, 5),
            Row(7, 4, 9),
            LowRow(7, -25),
            Row(7, 32, 25));

        // ==================================================================
        //  User-defined Huffman tables — segment type 53 (T.88 Annex B.2 / §7.4.17)
        //
        //  A "Tables" segment describes a custom Huffman table used by later
        //  segments (symbol dicts, text regions) in place of the standard
        //  tables. Layout, cross-checked between two independent
        //  implementations that read B.2 the same way — after this file's own prior reading of the
        //  flags byte turned out to disagree with both:
        //
        //    Flags (1 byte, B.2.1):
        //      Bit 0:    HTOOB — 1 when the table carries a trailing
        //                out-of-band line (B.2 10); there is no separate
        //                "high-range flag" bit.
        //      Bits 1-3: HTPS − 1 (prefix-size field width, 1..8 bits)
        //      Bits 4-6: HTRS − 1 (range-size field width, 1..8 bits)
        //      Bit 7:    reserved, must be 0
        //    HTLOW (4 bytes, B.2.2): lowest value the table's regular lines cover
        //    HTHIGH (4 bytes, B.2.3): one past the highest value the regular lines cover
        //    B.2 5) Regular lines, PREFLEN (HTPS bits) then RANGELEN (HTRS bits) each,
        //           repeated while the accumulated range is still below HTHIGH.
        //    B.2 6-7) Exactly one lower-range line: PREFLEN (HTPS bits), RANGELEN
        //           fixed at 32, RANGELOW = HTLOW − 1, decoded by counting downward
        //           (RangeLow − extra) — this line is ALWAYS present, not gated by
        //           any flag. Bit 0 is HTOOB, an unrelated flag for a *different*,
        //           genuinely optional row.
        //    B.2 8-9) Exactly one upper-range line: PREFLEN (HTPS bits), RANGELEN
        //           fixed at 32, RANGELOW = HTHIGH, decoded ascending (RangeLow +
        //           extra) — also ALWAYS present. There is no "high-range flag":
        //           bit 1 is the low bit of HTPS, so reading it as a flag would
        //           silently consume one bit of the prefix-
        //           size field as well as misreading whether this line exists.
        //    B.2 10) An out-of-band line — PREFLEN (HTPS bits) only, no range —
        //           present if and only if HTOOB is set.
        //
        //  Our parser extracts the (prefix, length, range_low, range_bits) tuples
        //  and constructs a <see cref="Jbig2HuffmanTable"/>. Any row that doesn't
        //  match the expected structure aborts with null so the caller can fall
        //  back to a standard table.
        // ==================================================================

        /// <summary>
        /// Parses a user-defined Huffman table from a Tables segment body.
        /// Returns null on parse failure (caller falls back to standard tables).
        /// </summary>
        internal static Jbig2HuffmanTable ParseUserDefinedHuffmanTable(byte[] body)
        {
            try
            {
                var br = new Jbig2BitReader(body);
                byte flags = br.ReadByte();
                bool htOob = (flags & 0x01) != 0;
                int htps = ((flags >> 1) & 0x07) + 1;   // prefix-size field width
                int htrs = ((flags >> 4) & 0x07) + 1;   // range-size field width
                int htLow  = (int)br.ReadUInt32BE();
                int htHigh = (int)br.ReadUInt32BE();

                var rows = new List<Jbig2HuffmanEntry>();
                int currentRangeLow = htLow;

                // We need a bit-level reader beyond this point because the row
                // data isn't byte-aligned.  Copy remainder and wrap in the
                // Huffman bit reader.
                var remainder = br.ReadBytes(br.RemainingBytes);
                var hr = new MsbBitStream(remainder, 0);

                // Canonical Huffman: read per-row (prefix-length, range-length),
                // assemble prefix codes in canonical order.  This requires
                // knowing the total number of rows up-front; the spec expresses
                // it by "iterate until the accumulated range exceeds htHigh".
                //
                // htLow/htHigh and every rangeLen are fully attacker-controlled. Without bounds a
                // crafted table is a non-terminating allocation bomb: rangeLen==0 grows the row
                // list ~2.1e9 times to reach a large htHigh, and rangeLen≡31 (mod 32) makes
                // `1 << rangeLen` negative so currentRangeLow never reaches htHigh — an infinite
                // loop. A real user-defined table has at most a few hundred rows; cap far above
                // that and bail to the standard-table fallback (return null) on either pathology.
                const int MaxHuffmanRows = 1 << 16; // 65536 — orders of magnitude over any real table
                while (currentRangeLow < htHigh)
                {
                    if (rows.Count >= MaxHuffmanRows) return null;
                    int prefLen  = hr.ReadBits(htps);
                    int rangeLen = hr.ReadBits(htrs);
                    rows.Add(new Jbig2HuffmanEntry
                    {
                        PrefixLen = prefLen,
                        Prefix    = 0,   // computed below (canonical assignment)
                        RangeLow  = currentRangeLow,
                        RangeBits = rangeLen,
                    });
                    if (rangeLen < 0 || rangeLen >= 31) return null; // 1<<rangeLen would overflow / go non-positive
                    currentRangeLow += 1 << rangeLen;
                }

                // B.2 6-7: the lower-range line — ALWAYS present, never gated by a flag.
                rows.Add(new Jbig2HuffmanEntry
                {
                    PrefixLen = hr.ReadBits(htps),
                    Prefix    = 0,
                    RangeLow  = htLow - 1,
                    RangeBits = 32,   // read 32 extra bits; Decode() then computes RangeLow - extra
                    LowerSign = true,
                });

                // B.2 8-9: the upper-range line — also ALWAYS present.
                rows.Add(new Jbig2HuffmanEntry
                {
                    PrefixLen = hr.ReadBits(htps),
                    Prefix    = 0,
                    RangeLow  = htHigh,
                    RangeBits = 32,   // read 32 extra bits; Decode() then computes RangeLow + extra
                });

                // B.2 10: the out-of-band line — present only when HTOOB is set.
                if (htOob)
                {
                    rows.Add(new Jbig2HuffmanEntry
                    {
                        PrefixLen = hr.ReadBits(htps),
                        Prefix    = 0,
                        RangeBits = -1,   // Decode()'s OOB sentinel
                    });
                }

                // Assign canonical prefixes: sort by (PrefixLen, original index),
                // then assign consecutive codes within each length stratum.
                AssignCanonicalPrefixes(rows);

                return new Jbig2HuffmanTable(rows.ToArray());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Assigns prefix codes to a list of Huffman entries using the
        /// canonical Huffman construction: entries are sorted by code length,
        /// and codes within each length are consecutive integers starting at
        /// the previous length's end shifted up.  Mutates the list in place.
        /// </summary>
        private static void AssignCanonicalPrefixes(List<Jbig2HuffmanEntry> rows)
        {
            // Sort entries in a stable way by (PrefixLen, original index).
            var indexed = new List<(Jbig2HuffmanEntry entry, int origIndex)>(rows.Count);
            for (int i = 0; i < rows.Count; i++) indexed.Add((rows[i], i));
            indexed.Sort((a, b) =>
            {
                int c = a.entry.PrefixLen.CompareTo(b.entry.PrefixLen);
                return c != 0 ? c : a.origIndex.CompareTo(b.origIndex);
            });

            int code = 0;
            int prevLen = 0;
            for (int i = 0; i < indexed.Count; i++)
            {
                var e = indexed[i].entry;
                if (prevLen == 0)
                {
                    code = 0;
                }
                else
                {
                    code = (code + 1) << (e.PrefixLen - prevLen);
                }
                e.Prefix = code;
                rows[indexed[i].origIndex] = e;
                prevLen = e.PrefixLen;
            }
        }

        // ==================================================================
        //  Pattern Dictionary (T.88 §6.7)
        //
        //  A pattern dictionary is a fixed-size array of monochrome patterns
        //  indexed 0..GRAYMAX.  The dictionary body encodes all patterns
        //  concatenated horizontally as a single generic region of dimensions
        //  ((GRAYMAX + 1) × HDPW) wide × HDPH tall.  The decoder splits the
        //  region bitmap into individual patterns after decoding.
        //
        //  Referenced by halftone region segments to render gray-scale
        //  bitmaps over an approximation grid.
        // ==================================================================

        private static Jbig2Bitmap[] DecodePatternDictionary(byte[] body, WorkBudget budget)
        {
            var br = new Jbig2BitReader(body);
            byte flags  = br.ReadByte();
            bool hdMmr      = (flags & 0x01) != 0;
            int  hdTemplate = (flags >> 1) & 0x03;
            _ = hdTemplate;

            byte hdpw = br.ReadByte();
            byte hdph = br.ReadByte();
            uint grayMax = br.ReadUInt32BE();

            // Validate /GRAYMAX: it is the maximum grayscale index, so patternCount =
            // grayMax + 1. A huge value overflows `(int)grayMax + 1` (negative) and
            // `patternCount * hdpw`, then `new Jbig2Bitmap[patternCount]` throws. Legitimate
            // halftone pattern dictionaries have at most a few thousand patterns. Cap and
            // compute the region width in long with a range check.
            if (grayMax > 65535 || hdpw == 0 || hdph == 0)
                return Array.Empty<Jbig2Bitmap>();
            int patternCount = (int)grayMax + 1;
            long regionWidthL = (long)patternCount * hdpw;
            if (regionWidthL > int.MaxValue)
                return Array.Empty<Jbig2Bitmap>();
            int regionWidth  = (int)regionWidthL;
            int regionHeight = hdph;

            byte[] arithData = br.ReadBytes(br.RemainingBytes);

            Jbig2Bitmap region;
            if (hdMmr)
            {
                byte[] decoded = CcittFaxDecoder.DecodeGroup4(
                    arithData, regionWidth, regionHeight, endOfBlock: false, out _);
                region = new Jbig2Bitmap(regionWidth, regionHeight, decoded);
            }
            else
            {
                // Template 0 uses AT pixel at position (-hdpw, 0) by default.
                // AT bytes not serialised in the dict header; use canonical defaults.
                var atBytes = new sbyte[] { (sbyte)-hdpw, 0, -3, -1, 2, -2, -2, -2 };
                region = DecodeArithmeticGenericRegion(arithData, regionWidth, regionHeight,
                    hdTemplate, tpgdon: false, atBytes, budget);
            }

            // Split the concatenated region into individual pattern bitmaps — a second pass over
            // the whole region area, so it is charged separately from the decode above.
            budget?.Tick((long)regionWidth * regionHeight);
            var patterns = new Jbig2Bitmap[patternCount];
            for (int i = 0; i < patternCount; i++)
            {
                var pat = new Jbig2Bitmap(hdpw, hdph);
                for (int y = 0; y < hdph; y++)
                    for (int x = 0; x < hdpw; x++)
                        pat.SetPixel(x, y, region.GetPixel(i * hdpw + x, y));
                patterns[i] = pat;
            }
            return patterns;
        }

        // ==================================================================
        //  Halftone Region (T.88 §6.6)
        //
        //  A halftone region renders a pattern-indexed grid over a rectangular
        //  area.  For each grid cell, the region header specifies a pattern
        //  index; the indexed pattern is drawn at the cell's (X, Y) world
        //  position, offset by the grid vectors HRX/HRY.
        //
        //  Index planes are gray-coded bit planes, decoded most significant
        //  first on one arithmetic decoder (Annex C.5) and XORed downward.
        // ==================================================================

        private static void DecodeHalftoneRegion(byte[] body, Jbig2Bitmap page,
            int regionX, int regionY, int combOp,
            Jbig2Bitmap[] patterns, WorkBudget budget)
        {
            var br = new Jbig2BitReader(body);

            // Region info (17 bytes) — the caller already has x and y.
            int regionWidth  = (int)br.ReadUInt32BE();
            int regionHeight = (int)br.ReadUInt32BE();
            /* regionX */ br.ReadUInt32BE();
            /* regionY */ br.ReadUInt32BE();
            /* regionFlags */ br.ReadByte();

            // Halftone region segment flags.
            byte hFlags = br.ReadByte();
            bool hMmr        = (hFlags & 0x01) != 0;
            int  hTemplate   = (hFlags >> 1) & 0x03;
            bool hEnableSkip = (hFlags & 0x08) != 0;
            int  hCombOp     = (hFlags >> 4) & 0x07;
            bool hDefPixel   = (hFlags & 0x80) != 0;

            int hgw = (int)br.ReadUInt32BE();   // grid width
            int hgh = (int)br.ReadUInt32BE();   // grid height
            int hgx = (int)br.ReadUInt32BE();   // signed, 1/256 pixel
            int hgy = (int)br.ReadUInt32BE();
            // HRX and HRY are UNSIGNED (§7.4.5.1.2.3). They were read
            // signed, so a vector of 32768/256 pixels or more turned round.
            int hrx = br.ReadUInt16BE();
            int hry = br.ReadUInt16BE();

            if (patterns == null || patterns.Length == 0)
                throw new InvalidDataException("JBIG2: halftone region with no pattern dictionary");
            Jbig2Bitmap.ValidateDimensions(hgw, hgh);

            int patternCount = patterns.Length;
            int hpw = patterns[0].Width, hph = patterns[0].Height;
            // HBPP as independent decoders compute it: at least one plane, even for a single pattern.
            int hbpp = 1;
            while (patternCount > (1 << hbpp)) hbpp++;

            // §6.6.5: the region is a bitmap of its own, filled with HDEFPIXEL, onto which each
            // pattern is drawn with HCOMBOP; that bitmap then goes onto the page with the segment's
            // operator. Patterns were drawn straight onto the page — unclipped to the region, with
            // the segment's operator, and HDEFPIXEL refused for want of a bitmap to fill.
            var region = RegionBitmap(regionWidth, regionHeight, hDefPixel, budget);

            // Where each grid cell's pattern goes (§6.6.5.2): the grid vectors are in 1/256 pixel,
            // and the cell (n, m) sits at ((HGX + m·HRY + n·HRX) >> 8, (HGY + m·HRX − n·HRY) >> 8).
            // The shift was missing, and the HRY terms had their signs exchanged, so every cell after
            // the first was drawn hundreds of pixels from where the stream put it.
            long CellX(int n, int m) => ((long)hgx + (long)m * hry + (long)n * hrx) >> 8;
            long CellY(int n, int m) => ((long)hgy + (long)m * hrx - (long)n * hry) >> 8;

            // HSKIP (§6.6.5.1): a cell whose pattern lies wholly outside the region is not coded.
            Jbig2Bitmap skip = null;
            if (hEnableSkip)
            {
                budget?.Tick((long)hgw * hgh);
                skip = new Jbig2Bitmap(hgw, hgh);
                for (int m = 0; m < hgh; m++)
                    for (int n = 0; n < hgw; n++)
                    {
                        long x = CellX(n, m), y = CellY(n, m);
                        if (x + hpw <= 0 || x >= regionWidth || y + hph <= 0 || y >= regionHeight)
                            skip.SetPixel(n, m, 1);
                    }
            }

            byte[] data = br.ReadBytes(br.RemainingBytes);
            var planes = new Jbig2Bitmap[hbpp];
            if (hMmr)
            {
                // Annex C.5 with GSMMR=1: the planes' T.6 data follow one another, most significant
                // first, each starting where the last ended (after an EOFB, if one follows, rounded
                // to a byte). More than one plane was refused
                // because the T.6 decoder did not say where its data ended; it does now.
                int offset = 0;
                for (int j = hbpp - 1; j >= 0; j--)
                {
                    budget?.Tick((long)hgw * hgh);
                    byte[] decoded = CcittFaxDecoder.DecodeGroup4(data, offset, hgw, hgh,
                        endOfBlock: false, out _, out offset);
                    planes[j] = new Jbig2Bitmap(hgw, hgh, decoded);
                }
            }
            else
            {
                // Annex C.5: one arithmetic decoder and one set of generic contexts for every plane,
                // most significant first, with AT1 at x+3 for templates 0-1 and x+2 for 2-3.
                // Every plane is decoded, not plane 0 from the whole remainder.
                var at = new sbyte[] { (sbyte)(hTemplate <= 1 ? 3 : 2), -1, -3, -1, 2, -2, -2, -2 };
                var mq = new MqDecoder(data, 0, GenericContextCount(hTemplate));
                for (int j = hbpp - 1; j >= 0; j--)
                    planes[j] = DecodeGenericRegion(mq, 0, hgw, hgh, hTemplate, tpgdon: false, at, budget, skip);
            }

            // Gray code to binary (C.5 step 3): each plane XOR the one above it.
            for (int j = hbpp - 2; j >= 0; j--)
                for (int k = 0; k < planes[j].Data.Length; k++)
                    planes[j].Data[k] ^= planes[j + 1].Data[k];

            for (int m = 0; m < hgh; m++)
            {
                for (int n = 0; n < hgw; n++)
                {
                    if (skip != null && skip.GetPixel(n, m) != 0) { budget?.Tick(1); continue; }
                    int value = 0;
                    for (int j = hbpp - 1; j >= 0; j--)
                        value = (value << 1) | planes[j].GetPixel(n, m);
                    if (value >= patternCount) value = patternCount - 1;   // as independent decoders clamp

                    long x = CellX(n, m), y = CellY(n, m);
                    region.DrawAt(patterns[value], (int)Math.Max(int.MinValue / 2, Math.Min(int.MaxValue / 2, x)),
                        (int)Math.Max(int.MinValue / 2, Math.Min(int.MaxValue / 2, y)), hCombOp, budget);
                }
            }

            page.DrawAt(region, regionX, regionY, combOp, budget);
        }

        // ==================================================================
        //  Integer arithmetic decoder (ITU-T T.88 Annex A)
        //
        //  Layered on top of the MQ decoder.  Decodes a signed integer by:
        //    1. Reading a sign bit.
        //    2. Reading a variable-length prefix (0 / 10 / 110 / 1110 / 11110 /
        //       11111) that selects one of six magnitude-bit widths (2, 4, 6,
        //       8, 12, 32) with matching offsets (0, 4, 20, 84, 340, 4436).
        //    3. Reading the magnitude bits MSB-first.
        //    4. Applying the sign, with "sign=1, magnitude=0" acting as the
        //       out-of-band (OOB) sentinel.
        //
        //  Each logical IA* variant (IADH, IADW, IAAI, IAID, IAEX, IARDW,
        //  IARDH, IARDX, IARDY, IARI, IAFS, IADS, IAIT, IARDX, …) needs its
        //  own 512-context block so their probability estimates don't
        //  contaminate each other.  Callers allocate disjoint base indices
        //  in the shared MQ decoder's context array.
        //
        //  Used by: text regions (symbol IDs + deltas), refinement regions,
        //  symbol dictionaries (aggregation instance counts).  Not yet wired
        //  to any of those — the class is introduced here standalone so it
        //  can be tested without risking bitmap-level output correctness.
        // ==================================================================

        internal sealed class Jbig2IntegerDecoder
        {
            /// <summary>
            /// Sentinel returned when the decoded value signals "out of band"
            /// (sign = 1, magnitude = 0 per T.88 A.2).  Callers that expect a
            /// regular integer should check for this before using the value.
            /// </summary>
            public const int OOB = int.MinValue;

            /// <summary>Number of contexts each IA decoder reserves.</summary>
            public const int ContextTableSize = 512;

            /// <summary>
            /// Annex A's six value ranges, in the order the prefix selects them: a zero after
            /// <i>n</i> ones (<i>n</i> = 0 to 4) chooses the <i>n</i>th row, five ones the sixth;
            /// each row is the count of magnitude bits that follow and the value they count from.
            /// Stated once, for the decoder below and the encoder (<c>Jbig2IntegerEncoder</c>).
            /// </summary>
            internal static readonly (int Bits, int Offset)[] Ranges =
            {
                (2, 0), (4, 4), (6, 20), (8, 84), (12, 340), (32, 4436),
            };

            /// <summary>
            /// A.2's PREV update after one decision: shift the bit in, and once PREV reaches 256
            /// keep it in [256, 511] with the top bit set. The encoder advances the same way.
            /// </summary>
            internal static int Advance(int prev, int bit)
                => prev < 256
                    ? (prev << 1) | bit
                    : (((prev << 1) | bit) & 0x1FF) | 256;

            private readonly MqDecoder _mq;
            private readonly int _baseContextIndex;

            /// <summary>
            /// Wraps the given <see cref="MqDecoder"/> with an integer
            /// decoder.  <paramref name="baseContextIndex"/> is the starting
            /// index in the MQ's context array for this IA decoder's 512-entry
            /// block; callers provide disjoint bases per IA variant.
            /// </summary>
            public Jbig2IntegerDecoder(MqDecoder mq, int baseContextIndex)
            {
                _mq = mq ?? throw new ArgumentNullException(nameof(mq));
                if (baseContextIndex < 0
                    || baseContextIndex + ContextTableSize > mq.ContextStates.Length)
                    throw new ArgumentOutOfRangeException(nameof(baseContextIndex),
                        "IA decoder block would overflow the MQ context table.");
                _baseContextIndex = baseContextIndex;
            }

            /// <summary>
            /// Decodes one signed integer.  Returns <see cref="OOB"/> for the
            /// out-of-band signal.  Does not throw on ordinary input — the MQ
            /// decoder pads past-EOF with 0xFF so the stream never "runs out".
            /// </summary>
            public int Decode()
            {
                int prev = 1;
                int sign = ReadAndAdvance(ref prev);

                // The prefix: ones select the next range, a zero stops; after the fifth one the
                // last range is chosen with no further bit (Table A.1 through Ranges).
                int range = 0;
                while (range < Ranges.Length - 1 && ReadAndAdvance(ref prev) == 1) range++;
                (int bits, long offset) = Ranges[range];

                long value = 0;
                for (int i = 0; i < bits; i++)
                {
                    int b = ReadAndAdvance(ref prev);
                    value = (value << 1) | (uint)b;
                }

                value += offset;

                if (sign == 1 && value == 0) return OOB;
                if (sign == 1) value = -value;

                // Clamp to int range; leave int.MinValue reserved for OOB so
                // any genuine "very large negative" magnitude gets bumped up
                // by one to avoid colliding with the sentinel.
                if (value >  int.MaxValue) return int.MaxValue;
                if (value <= int.MinValue) return int.MinValue + 1;
                return (int)value;
            }

            // Reads one bit using the current `prev`-derived context, then
            // updates `prev` per T.88 A.2's wrap rule — once prev reaches 256
            // the top bit stays set so the value is clamped into [256, 511].
            private int ReadAndAdvance(ref int prev)
            {
                int bit = _mq.Decode(_baseContextIndex + prev);
                prev = Advance(prev, bit);
                return bit;
            }
        }

        // ------------------------------------------------------------------
        //  Testing hook — walk the segment headers without decoding bodies.
        // ------------------------------------------------------------------
        internal static IReadOnlyList<Jbig2SegmentHeader> EnumerateSegments(byte[] data)
        {
            data = StripFileHeader(data);
            var list = new List<Jbig2SegmentHeader>();
            var br = new Jbig2BitReader(data);
            while (!br.Eof)
            {
                Jbig2SegmentHeader hdr;
                try { hdr = ReadSegmentHeader(br); }
                catch (EndOfStreamException) { break; }
                list.Add(hdr);
                if (hdr.UnknownLength)
                {
                    // T.88 §7.2.7, via the shared rule. `found` decides only the ADVANCE: the two
                    // marker bytes exist to be skipped, and with no marker the body runs to the end
                    // of the stream — which is what Decode does, so stopping here would have made
                    // this walk describe a different program from the one that renders.
                    bool found = TryFindUnknownLengthEnd(data, br.BytePosition, out int bodyEnd);
                    br.SkipBytes(bodyEnd - br.BytePosition + (found ? 2 : 0));
                    continue;
                }
                br.SkipBody(hdr.DataLength);
            }
            return list;
        }

        // ==================================================================
        //  Diagnostic surface
        //
        //  Lets callers and tools inspect a JBIG2 stream without actually
        //  decoding it — useful for triage ("which PDFs can this library
        //  render?") and as documentation of the decoder's current reach.
        // ==================================================================

        /// <summary>
        /// Classifies how this decoder would treat a given segment type.
        /// </summary>
        public enum Jbig2SegmentStatus
        {
            /// <summary>Decoded into the page bitmap (page info / generic region / end markers).</summary>
            Decoded,
            /// <summary>Parsed past but not used — intermediate regions, symbol dictionaries,
            /// tables, profiles, extensions.  Skipping these is safe because nothing
            /// visible in the page depends on them being decoded at this layer.</summary>
            SkippedBenign,
            /// <summary>Immediate page-visible segment the decoder can't produce output for
            /// (text / halftone / refinement / arithmetic-coded generic).  The top-level
            /// filter falls back to raw-byte passthrough when it encounters one of these.</summary>
            Unsupported
        }

        /// <summary>Per-segment diagnostic record returned from <see cref="Diagnose"/>.</summary>
        public sealed class Jbig2SegmentDiagnostic
        {
            public uint               SegmentNumber   { get; }
            public int                SegmentType     { get; }
            public uint               PageAssociation { get; }
            public uint               DataLength      { get; }
            public Jbig2SegmentStatus Status          { get; }
            public string             TypeName        { get; }
            public string             Notes           { get; }

            public Jbig2SegmentDiagnostic(
                uint segmentNumber, int segmentType, uint pageAssociation,
                uint dataLength, Jbig2SegmentStatus status, string typeName, string notes)
            {
                SegmentNumber   = segmentNumber;
                SegmentType     = segmentType;
                PageAssociation = pageAssociation;
                DataLength      = dataLength;
                Status          = status;
                TypeName        = typeName;
                Notes           = notes;
            }
        }

        /// <summary>
        /// Stream-level assessment derived from <see cref="Diagnose"/>.  Gives
        /// callers a quick "will this decode end-to-end?" answer plus parsed
        /// page dimensions from the page-info segment (when present), without
        /// having to sift through the per-segment list themselves.
        /// </summary>
        public sealed class Jbig2DecodeAssessment
        {
            public IReadOnlyList<Jbig2SegmentDiagnostic> Segments { get; }

            /// <summary>True when every segment in the stream would be either
            /// decoded (<see cref="Jbig2SegmentStatus.Decoded"/>) or safely
            /// skipped (<see cref="Jbig2SegmentStatus.SkippedBenign"/>).  Callers
            /// use this to decide whether the current filter will return a real
            /// bitmap or fall back to raw bytes.</summary>
            public bool IsFullyDecodable { get; }

            public int DecodedSegmentCount     { get; }
            public int SkippedSegmentCount     { get; }
            public int UnsupportedSegmentCount { get; }

            /// <summary>Page width in pixels — 0 when the stream had no page-info segment.</summary>
            public int PageWidth  { get; }
            /// <summary>Page height in pixels — 0 when the stream had no page-info segment.</summary>
            public int PageHeight { get; }

            /// <summary>Comma-separated list of distinct blocker descriptions.
            /// Empty string when <see cref="IsFullyDecodable"/> is true.</summary>
            public string BlockerSummary { get; }

            /// <summary>Human-readable one-line summary suitable for logs.</summary>
            public string Summary { get; }

            internal Jbig2DecodeAssessment(
                IReadOnlyList<Jbig2SegmentDiagnostic> segments,
                int pageWidth, int pageHeight)
            {
                Segments   = segments;
                PageWidth  = pageWidth;
                PageHeight = pageHeight;

                int dec = 0, skip = 0, unsup = 0;
                var blockers = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                foreach (var s in segments)
                {
                    switch (s.Status)
                    {
                        case Jbig2SegmentStatus.Decoded:       dec++; break;
                        case Jbig2SegmentStatus.SkippedBenign: skip++; break;
                        case Jbig2SegmentStatus.Unsupported:
                            unsup++;
                            if (!string.IsNullOrEmpty(s.Notes)) blockers.Add(s.Notes);
                            break;
                    }
                }

                DecodedSegmentCount     = dec;
                SkippedSegmentCount     = skip;
                UnsupportedSegmentCount = unsup;
                IsFullyDecodable        = unsup == 0;
                BlockerSummary          = IsFullyDecodable ? string.Empty
                    : string.Join(", ", blockers);

                Summary = IsFullyDecodable
                    ? $"OK — {dec} decoded, {skip} skipped; page {pageWidth}×{pageHeight}"
                    : $"BLOCKED — {unsup} unsupported: {BlockerSummary}"
                      + (pageWidth > 0 ? $"; page {pageWidth}×{pageHeight}" : "");
            }
        }

        /// <summary>
        /// Runs <see cref="Diagnose"/> and produces an aggregate
        /// <see cref="Jbig2DecodeAssessment"/> — usually more convenient for
        /// tooling than iterating the per-segment list.
        /// </summary>
        public static Jbig2DecodeAssessment Assess(byte[] data, byte[] globals = null)
        {
            var segments = Diagnose(data, globals);

            // Parse page dimensions if a page-info segment is present.  We
            // only need the first 8 bytes (width + height) of its body, so
            // use a separate reader on the combined stream.
            int pageWidth = 0, pageHeight = 0;
            byte[] combined = CombineWithGlobals(StripFileHeader(data), StripFileHeader(globals));

            var br = new Jbig2BitReader(combined);
            while (!br.Eof)
            {
                Jbig2SegmentHeader hdr;
                try { hdr = ReadSegmentHeader(br); }
                catch (EndOfStreamException) { break; }

                if (hdr.SegmentType == SEG_PAGE_INFO && !hdr.UnknownLength)
                {
                    try
                    {
                        pageWidth  = (int)br.ReadUInt32BE();
                        pageHeight = (int)br.ReadUInt32BE();
                    }
                    catch { }
                    break;
                }

                // Scanned, not `if (hdr.UnknownLength) break;`: a stream whose unknown-length segment
                // comes BEFORE its page-info segment would report page 0×0, while the Diagnose call
                // at the top of this very method walks past that segment and lists the page-info
                // one. One assessment, two
                // answers. Skipping the body properly is all it takes to agree.
                if (hdr.UnknownLength)
                {
                    bool found = TryFindUnknownLengthEnd(combined, br.BytePosition, out int bodyEnd);
                    br.SkipBytes(bodyEnd - br.BytePosition + (found ? 2 : 0));
                    continue;
                }

                br.SkipBody(hdr.DataLength);
            }

            return new Jbig2DecodeAssessment(segments, pageWidth, pageHeight);
        }

        /// <summary>
        /// Walks a JBIG2 stream (with optional /JBIG2Globals prepended) and returns
        /// a read-only list describing what the decoder would do with every
        /// segment.  This NEVER throws on valid segment structure — malformed
        /// data produces entries with <see cref="Jbig2SegmentStatus.Unsupported"/>
        /// status and a descriptive note.
        /// </summary>
        public static IReadOnlyList<Jbig2SegmentDiagnostic> Diagnose(byte[] data, byte[] globals = null)
        {
            var result = new List<Jbig2SegmentDiagnostic>();
            byte[] combined = CombineWithGlobals(StripFileHeader(data), StripFileHeader(globals));

            var br = new Jbig2BitReader(combined);
            while (!br.Eof)
            {
                Jbig2SegmentHeader hdr;
                try { hdr = ReadSegmentHeader(br); }
                catch (EndOfStreamException)
                {
                    result.Add(new Jbig2SegmentDiagnostic(
                        segmentNumber: 0, segmentType: -1,
                        pageAssociation: 0, dataLength: 0,
                        status: Jbig2SegmentStatus.Unsupported,
                        typeName: "malformed",
                        notes: "stream ended mid-header"));
                    break;
                }

                int bodyStart = br.BytePosition;
                var (status, notes) = ClassifySegment(hdr, combined, bodyStart);
                result.Add(new Jbig2SegmentDiagnostic(
                    segmentNumber:   hdr.SegmentNumber,
                    segmentType:     hdr.SegmentType,
                    pageAssociation: hdr.PageAssociation,
                    dataLength:      hdr.DataLength,
                    status:          status,
                    typeName:        SegmentTypeName(hdr.SegmentType),
                    notes:           notes));

                if (hdr.UnknownLength)
                {
                    // T.88 §7.2.7, via the shared rule. This walk documents itself as "what the
                    // decoder would do with every segment", so where the decoder continues past a
                    // markerless segment, so must this.
                    bool found = TryFindUnknownLengthEnd(combined, br.BytePosition, out int bodyEnd);
                    br.SkipBytes(bodyEnd - br.BytePosition + (found ? 2 : 0));
                }
                else
                {
                    br.SkipBody(hdr.DataLength);
                }
            }
            return result;
        }

        /// <summary>
        /// Classifies a segment without mutating the reader.  For generic regions
        /// we peek at the flags byte (offset 17 from body start = after the 17-byte
        /// region-info header) to distinguish MMR (which we decode) from arithmetic
        /// (which we don't yet).
        /// </summary>
        private static (Jbig2SegmentStatus status, string notes) ClassifySegment(
            Jbig2SegmentHeader hdr, byte[] combinedData, int bodyStart)
        {
            switch (hdr.SegmentType)
            {
                case SEG_PAGE_INFO:
                case SEG_END_OF_PAGE:
                case SEG_END_OF_STRIPE:
                case SEG_END_OF_FILE:
                    return (Jbig2SegmentStatus.Decoded, "structural");

                case SEG_GENERIC_REGION_IMMED:
                case SEG_GENERIC_REGION_LOSSLESS:
                {
                    int flagsOffset = bodyStart + 17;
                    if (hdr.UnknownLength || flagsOffset >= combinedData.Length)
                        return (Jbig2SegmentStatus.Unsupported, "generic region header truncated");
                    byte grFlags = combinedData[flagsOffset];
                    bool mmr = (grFlags & 0x01) != 0;
                    int template = (grFlags >> 1) & 0x03;
                    if (mmr)
                        return (Jbig2SegmentStatus.Decoded, "generic region (MMR)");
                    return (Jbig2SegmentStatus.Decoded,
                        $"generic region (arithmetic, template {template}) — experimental, validate");
                }

                case SEG_TEXT_REGION_IMMEDIATE:
                case SEG_TEXT_REGION_LOSSLESS:
                {
                    int sbFlagsOffset = bodyStart + 17;
                    if (sbFlagsOffset + 1 < combinedData.Length)
                    {
                        // SBFLAGS is a 16-bit big-endian field and SBHUFF is its bit 0, so the
                        // flag lives in the SECOND byte — the same trap the symbol-dictionary
                        // branch below already carries a comment about. This read the FIRST byte,
                        // so a real Huffman text region (which sets the low byte) was reported
                        // Decoded and IsFullyDecodable answered yes for it, while an arithmetic
                        // region whose high byte happened to be odd was reported Unsupported.
                        // Both directions wrong, and the false-decodable one is the harmful one.
                        bool sbHuff   = (combinedData[sbFlagsOffset + 1] & 0x01) != 0;
                        bool sbRefine = (combinedData[sbFlagsOffset + 1] & 0x02) != 0;
                        // Huffman text regions decode for real: the runcode-coded symbol-ID table
                        // (T.88 §7.4.3.1.7) is built and used (GAPS.md §2a), and SBREFINE=1 decodes per
                        // instance (T.88 §6.4.11).
                        return sbHuff
                            ? (Jbig2SegmentStatus.Decoded,
                               "text region (Huffman) — decoded, every mode checked against its arithmetic twin (GAPS §2a)")
                            : (Jbig2SegmentStatus.Decoded,
                               "text region (arithmetic) — experimental, validate");
                    }
                    return (Jbig2SegmentStatus.Unsupported, "text region — truncated header");
                }

                case SEG_HALFTONE_REGION_IMMED:
                case SEG_HALFTONE_REGION_LOSSLESS:
                    return (Jbig2SegmentStatus.Decoded,
                        "halftone region — experimental, validate");

                case SEG_REFINE_REGION_IMMED:
                case SEG_REFINE_REGION_LOSSLESS:
                    return (Jbig2SegmentStatus.Decoded,
                        "refinement region — experimental, validate");

                case SEG_SYMBOL_DICT:
                {
                    if (bodyStart + 1 < combinedData.Length)
                    {
                        // SDFLAGS is a 16-bit big-endian value; SDHUFF (bit 0) and SDREFAGG
                        // (bit 1) both live in the second byte, not the first.
                        bool sdHuff    = (combinedData[bodyStart + 1] & 0x01) != 0;
                        // A Huffman symbol dictionary decodes for real: the collective bitmap is parsed and
                        // split, and with SDREFAGG=1 each aggregate symbol is decoded as it is read (GAPS.md §2a).
                        return (Jbig2SegmentStatus.SkippedBenign,
                            sdHuff
                                ? "symbol dictionary (Huffman) — decoded when referenced by text region"
                                : "symbol dictionary (arithmetic) — decoded when referenced by text region");
                    }
                    return (Jbig2SegmentStatus.SkippedBenign, "symbol dictionary");
                }
                case SEG_PATTERN_DICT:
                    return (Jbig2SegmentStatus.SkippedBenign, "pattern dictionary (unused)");
                case SEG_GENERIC_REGION_INTER:
                case SEG_REFINE_REGION_INTER:
                    return (Jbig2SegmentStatus.Decoded,
                        "intermediate region — decoded and kept for a refinement that refers to it");
                case SEG_TEXT_REGION_INTERMED:
                case SEG_HALFTONE_REGION_INTER:
                    return (Jbig2SegmentStatus.SkippedBenign, "intermediate region (no page output)");
                case SEG_PROFILES:
                    return (Jbig2SegmentStatus.SkippedBenign, "profile");
                case SEG_TABLES:
                    return (Jbig2SegmentStatus.SkippedBenign,
                        "user-defined Huffman tables — parsed, referenced by later segments");
                case SEG_EXTENSION:
                    return (Jbig2SegmentStatus.SkippedBenign, "extension");
                default:
                    return (Jbig2SegmentStatus.SkippedBenign, "unknown / reserved type");
            }
        }

        private static string SegmentTypeName(int type) => type switch
        {
            SEG_SYMBOL_DICT              => "SymbolDictionary",
            SEG_TEXT_REGION_INTERMED     => "TextRegion (intermediate)",
            SEG_TEXT_REGION_IMMEDIATE    => "TextRegion (immediate)",
            SEG_TEXT_REGION_LOSSLESS     => "TextRegion (immediate-lossless)",
            SEG_PATTERN_DICT             => "PatternDictionary",
            SEG_HALFTONE_REGION_INTER    => "HalftoneRegion (intermediate)",
            SEG_HALFTONE_REGION_IMMED    => "HalftoneRegion (immediate)",
            SEG_HALFTONE_REGION_LOSSLESS => "HalftoneRegion (immediate-lossless)",
            SEG_GENERIC_REGION_INTER     => "GenericRegion (intermediate)",
            SEG_GENERIC_REGION_IMMED     => "GenericRegion (immediate)",
            SEG_GENERIC_REGION_LOSSLESS  => "GenericRegion (immediate-lossless)",
            SEG_REFINE_REGION_INTER      => "RefinementRegion (intermediate)",
            SEG_REFINE_REGION_IMMED      => "RefinementRegion (immediate)",
            SEG_REFINE_REGION_LOSSLESS   => "RefinementRegion (immediate-lossless)",
            SEG_PAGE_INFO                => "PageInformation",
            SEG_END_OF_PAGE              => "EndOfPage",
            SEG_END_OF_STRIPE            => "EndOfStripe",
            SEG_END_OF_FILE              => "EndOfFile",
            SEG_PROFILES                 => "Profiles",
            SEG_TABLES                   => "HuffmanTables",
            SEG_EXTENSION                => "Extension",
            _                            => $"Type{type}"
        };
    }
}
