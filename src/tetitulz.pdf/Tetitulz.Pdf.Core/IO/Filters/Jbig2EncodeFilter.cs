// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Encodes a bilevel image as a PDF-embedded JBIG2 stream: a page-information segment and either
    /// one immediate lossless generic region (<see cref="Encode"/>), or a symbol dictionary, a text
    /// region placing every occurrence of each distinct component, and a generic region for what is
    /// not a symbol (<see cref="EncodeWithSymbols"/>) — arithmetic-coded with template 0, lossless
    /// either way.
    /// </summary>
    /// <remarks>
    /// <para><b>Two forms, both exact.</b> The generic region beats G4 on the same page, because an
    /// adaptive context model conditioned on ten neighbours predicts better than a run-length code.
    /// The symbol form is where JBIG2's large win on scanned text comes from — a glyph bitmap that
    /// recurs is coded once and placed by its coordinates — and it is the form JBIG2 earned its
    /// reputation for substituting one character for another with; here two components are one symbol
    /// only when their bitmaps are identical (<see cref="Jbig2SymbolEncoder"/>), so neither form loses
    /// a pixel. Which is smaller depends on the page: a typed page favours symbols, a drawing or a
    /// halftone the generic region; a producer that cares encodes both and keeps the shorter.</para>
    ///
    /// <para><b>The context is formed from the decoder's own table.</b> Encoder and decoder must
    /// agree on which ten neighbours make the context and in what bit order; two copies of that
    /// table would agree until one was corrected, and the failure mode is a stream only this
    /// library can read. With the default AT pixels the positions are exactly
    /// <c>Jbig2Decoder.Template0RefPositions</c>, so the encoder uses it directly.</para>
    ///
    /// <para><b>The round trip is checked through the decoder, and the decoder is checked by the
    /// world.</b> An encode-then-decode test shares whatever the two halves both believe — the
    /// blind spot every A-versus-B comparison has. What closes it here is that the decoder already
    /// reads JBIG2 produced by other encoders in real PDFs: a context template it got wrong would
    /// have failed on those long before this existed; and the symbol form is also handed to an independent decoder
    /// where the test machine has it.</para>
    /// </remarks>
    public static class Jbig2EncodeFilter
    {
        /// <summary>Symbol dictionary.</summary>
        private const int SegmentSymbolDictionary = 0;

        /// <summary>Immediate text region.</summary>
        private const int SegmentTextRegion = 6;

        /// <summary>Immediate lossless generic region.</summary>
        private const int SegmentGenericRegionLossless = 39;

        /// <summary>Page information.</summary>
        private const int SegmentPageInfo = 48;

        /// <summary>
        /// Encodes a 1-bit-per-pixel image, MSB first, one row per <paramref name="stride"/> bytes,
        /// as one generic region.
        /// </summary>
        /// <param name="bits">
        /// The samples. A set bit is a <b>black</b> pixel, which is JBIG2's convention and the
        /// opposite of an unfiltered PDF <c>/DeviceGray</c> sample — a caller holding the latter
        /// inverts, or declares <c>/Decode [1 0]</c>.
        /// </param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="stride">Bytes per row; at least <c>(width + 7) / 8</c>.</param>
        /// <returns>The embedded JBIG2 stream, or <c>null</c> when the input is not usable.</returns>
        public static byte[] Encode(byte[] bits, int width, int height, int stride)
        {
            if (!IsUsable(bits, width, height, stride)) return null;

            byte[] coded = EncodeGenericRegion(bits, width, height, stride);

            using var output = new MemoryStream();
            WritePageInfo(output, width, height);
            WriteGenericRegion(output, 2, 0, 0, width, height, coded);
            return output.ToArray();
        }

        /// <summary>
        /// Encodes the image with a symbol dictionary: each distinct connected component once, every
        /// occurrence as a text-region instance, and what is too large to be a symbol as a generic
        /// region — the same pixels back, bit for bit, and on a page of repeated glyphs a far shorter
        /// stream than <see cref="Encode"/>'s.
        /// </summary>
        /// <inheritdoc cref="Encode"/>
        /// <returns>
        /// The embedded JBIG2 stream; <see cref="Encode"/>'s single generic region when the image holds
        /// no symbol-sized component (a blank page, a drawing of large shapes only, a halftone of too
        /// many runs); <c>null</c> when the input is not usable.
        /// </returns>
        public static byte[] EncodeWithSymbols(byte[] bits, int width, int height, int stride)
        {
            if (!IsUsable(bits, width, height, stride)) return null;

            Jbig2SymbolEncoder.Plan plan = Jbig2SymbolEncoder.Analyse(bits, width, height, stride);
            if (plan == null) return Encode(bits, width, height, stride);

            byte[] dictionary = Jbig2SymbolEncoder.CodeSymbolDictionary(plan.Symbols);
            byte[] text = Jbig2SymbolEncoder.CodeTextRegion(plan.Symbols, plan.Instances);

            using var output = new MemoryStream();
            WritePageInfo(output, width, height);
            WriteSegment(output, 2, SegmentSymbolDictionary, Array.Empty<int>(),
                Jbig2SymbolEncoder.SymbolDictionaryBody(plan.Symbols.Count, dictionary));
            WriteSegment(output, 3, SegmentTextRegion, new[] { 2 },
                Jbig2SymbolEncoder.TextRegionBody(width, height, plan.Instances.Count, text));

            if (plan.HasResidual)
            {
                // The residual, cropped to its own bounds, as a generic region drawn where it was.
                int rx = plan.ResidualMinX, ry = plan.ResidualMinY;
                int rw = plan.ResidualMaxX - rx + 1, rh = plan.ResidualMaxY - ry + 1;
                int rStride = (rw + 7) / 8;
                var residual = new byte[rStride * rh];
                for (int y = 0; y < rh; y++)
                    for (int x = 0; x < rw; x++)
                        if (GetPixel(plan.Residual, plan.ResidualStride, rx + x, ry + y) == 1)
                            residual[y * rStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                WriteGenericRegion(output, 4, rx, ry, rw, rh, EncodeGenericRegion(residual, rw, rh, rStride));
            }
            return output.ToArray();
        }

        private static bool IsUsable(byte[] bits, int width, int height, int stride)
        {
            if (bits == null || width <= 0 || height <= 0) return false;
            if (stride < (width + 7) / 8) return false;
            if ((long)stride * height > bits.Length) return false;
            return true;
        }

        /// <summary>
        /// The arithmetic-coded region body: one MQ decision per pixel, in raster order, on a coder
        /// of its own.
        /// </summary>
        private static byte[] EncodeGenericRegion(byte[] bits, int width, int height, int stride)
        {
            var mq = new MqEncoder(1 << Jbig2Decoder.Template0RefPositions.Length);
            CodeGenericBitmap(mq, 0, bits, stride, width, height);
            return mq.Finish();
        }

        /// <summary>
        /// Codes a bitmap as a template-0 generic region on <paramref name="mq"/>, its contexts from
        /// <paramref name="contextBase"/> — the page's own region and every symbol of a dictionary
        /// alike, which is why the dictionary's symbols share one coder with the dictionary's integer
        /// contexts after them.
        /// </summary>
        /// <remarks>
        /// <b>The context reads pixels the decoder will already have decoded</b> — everything above
        /// the current row, and to its left on the current one. That is what makes the model
        /// symmetrical: the encoder must look only at what the decoder can know, which is why the
        /// positions all have a negative <c>dy</c>, or a zero <c>dy</c> and a negative <c>dx</c>.
        /// </remarks>
        internal static void CodeGenericBitmap(MqEncoder mq, int contextBase, byte[] bits, int stride, int width, int height)
        {
            (int dy, int dx)[] positions = Jbig2Decoder.Template0RefPositions;
            int contextBits = positions.Length;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int context = 0;
                    for (int i = 0; i < contextBits; i++)
                    {
                        int ry = y + positions[i].dy;
                        int rx = x + positions[i].dx;

                        // Outside the region reads as white, which is what the decoder does.
                        int p = (ry < 0 || rx < 0 || rx >= width || ry >= height)
                            ? 0
                            : GetPixel(bits, stride, rx, ry);

                        context = (context << 1) | p;
                    }

                    mq.Encode(contextBase + context, GetPixel(bits, stride, x, y));
                }
            }
        }

        private static int GetPixel(byte[] bits, int stride, int x, int y)
            => (bits[(y * stride) + (x >> 3)] >> (7 - (x & 7))) & 1;

        // ==============================================================
        //  Segments
        // ==============================================================

        private static void WritePageInfo(Stream s, int width, int height)
        {
            var body = new MemoryStream();
            WriteU32(body, width);
            WriteU32(body, height);
            WriteU32(body, 0);                  // x resolution, unknown
            WriteU32(body, 0);                  // y resolution, unknown
            body.WriteByte(0);                  // flags: default pixel white, OR combination
            WriteU16(body, 0);                  // striping information

            WriteSegment(s, 1, SegmentPageInfo, Array.Empty<int>(), body.ToArray());
        }

        private static void WriteGenericRegion(Stream s, int number, int x, int y, int width, int height, byte[] coded)
        {
            var body = new MemoryStream();
            WriteRegionInfo(body, width, height, x, y);

            // Generic region flags: MMR off, GBTEMPLATE 0, TPGDON off.
            body.WriteByte(0);
            WriteTemplate0At(body);
            body.Write(coded, 0, coded.Length);

            WriteSegment(s, number, SegmentGenericRegionLossless, Array.Empty<int>(), body.ToArray());
        }

        /// <summary>The region segment information field (§7.4.1): size, position, external combination OR.</summary>
        internal static void WriteRegionInfo(Stream s, int width, int height, int x, int y)
        {
            WriteU32(s, width);
            WriteU32(s, height);
            WriteU32(s, x);
            WriteU32(s, y);
            s.WriteByte(0);                     // external combination operator: OR
        }

        /// <summary>
        /// The four AT pixels at template 0's defaults — which is what makes the encoder's context
        /// table and <c>Jbig2Decoder.Template0RefPositions</c> the same table.
        /// </summary>
        internal static void WriteTemplate0At(Stream s)
        {
            WriteAt(s, 3, -1);
            WriteAt(s, -3, -1);
            WriteAt(s, 2, -2);
            WriteAt(s, -2, -2);
        }

        private static void WriteAt(Stream s, int x, int y)
        {
            s.WriteByte(unchecked((byte)(sbyte)x));
            s.WriteByte(unchecked((byte)(sbyte)y));
        }

        /// <summary>
        /// A segment: its header (§7.2) — number, type, the referred-to segments (at most four, one
        /// byte each, since every number written here is below 256), a one-byte page association, the
        /// body length — then its body.
        /// </summary>
        private static void WriteSegment(Stream s, int number, int type, IReadOnlyList<int> referredTo, byte[] body)
        {
            WriteU32(s, number);

            // Flags: type in bits 0-5; bit 6 clear means the page association is one byte.
            s.WriteByte((byte)type);

            // Referred-to segment count in the top three bits, retain bits clear below.
            s.WriteByte((byte)(referredTo.Count << 5));
            foreach (int referred in referredTo) s.WriteByte((byte)referred);

            s.WriteByte(1);                     // page association
            WriteU32(s, body.Length);
            s.Write(body, 0, body.Length);
        }

        internal static void WriteU16(Stream s, int v) => BigEndian.Write16(s, v);

        // The low 32 bits.
        internal static void WriteU32(Stream s, long v) => BigEndian.Write32(s, unchecked((uint)v));
    }
}
