// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
#if NET
using System.Buffers.Binary;
using System.Runtime.Intrinsics;
#endif
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Shared predictor handling for <c>FlateDecodeFilter</c> and <c>LzwDecodeFilter</c> — both the
    /// two removal algorithms and, the <see cref="Apply"/> entry point that reads
    /// <c>/DecodeParms</c> and chooses between them.
    /// </summary>
    /// <remarks>
    /// The algorithms were shared first and the parameter reading left copied in both filters, which
    /// is the half-consolidation CLAUDE.md warns is worse than none: the remaining copies read as
    /// deliberate. <see cref="Apply"/> closes it.
    /// </remarks>
    internal static class PredictorHelper
    {
        /// <summary>
        /// Why the most recent predictor removal on <b>this thread</b> returned its input
        /// unchanged, or <c>null</c> when the predictor was applied (or none was asked for).
        /// </summary>
        /// <remarks>
        /// <para>Every rejection path here is <c>return data</c>, which hands the caller the
        /// still-differenced bytes with no signal that the predictor was not removed — so
        /// "no predictor" and "predictor present, not applied" were the same observation, and the
        /// image renders as a plausible-looking smear rather than failing. That is the
        /// combination CLAUDE.md's Diagnostics Pattern forbids.</para>
        /// <para>Reset at the start of both entry points, on every path, so a value read after a
        /// call always belongs to that call.</para>
        /// </remarks>
        public static string LastPredictorSkipReason => _lastPredictorSkipReason;

        [ThreadStatic]
        private static string _lastPredictorSkipReason;

        private static byte[] Skip(byte[] data, string reason)
        {
            _lastPredictorSkipReason = reason;
            return data;
        }

        /// <summary>
        /// Applies whatever predictor <paramref name="parameters"/> asks for (§7.4.4.4, Table 10) to
        /// already-decompressed <paramref name="decoded"/> bytes, or returns them unchanged when the
        /// dictionary names none.
        /// </summary>
        /// <param name="decoded">The filter's decompressed output.</param>
        /// <param name="parameters">The stream's <c>/DecodeParms</c>, or <c>null</c>.</param>
        /// <returns>The de-differenced bytes, or <paramref name="decoded"/> unchanged.</returns>
        /// <remarks>
        /// <para><b>Why this exists, given the two algorithms were already shared.</b> An earlier
        /// round moved <c>RemovePngPredictor</c> / <c>RemoveTiffPredictor</c> down here and left the
        /// fifteen lines that <i>read the parameters and choose between them</i> copied verbatim in
        /// <c>FlateDecodeFilter</c> and <c>LzwDecodeFilter</c> — under a comment in each reading
        /// "DRY: delegate to shared PredictorHelper". That is CLAUDE.md's own tell: a comment
        /// asserting two implementations agree is a duplication report, and consolidating part of a
        /// cluster is more dangerous than consolidating none of it, because the remaining copies now
        /// look like they were considered and kept.</para>
        ///
        /// <para><b>What the split could have cost.</b> The four defaults here are not free
        /// choices — Table 10 fixes them at <c>/Predictor 1</c>, <c>/Colors 1</c>,
        /// <c>/BitsPerComponent 8</c> and <c>/Columns 1</c> — and the <c>10..15</c> range covers
        /// every PNG filter type including <c>/Predictor 15</c> ("optimum", per-row). A future fix to
        /// any one of those, or a guard on a non-positive <c>/Columns</c>, applied to whichever
        /// filter the reporter happened to be using would leave the other silently wrong on exactly
        /// the files that use it. Every cross-reference stream in a modern PDF is Flate with
        /// <c>/Predictor 12</c>; LZW is the one a decade-old producer emits — so the two copies are
        /// exercised by different corpora, which is the condition under which a divergence survives
        /// longest.</para>
        /// </remarks>
        public static byte[] Apply(byte[] decoded, PdfDictionary parameters)
        {
            // Reset here too: the two early returns below never reach the removers that reset it,
            // so a stream with no predictor answered with the previous stream's skip reason.
            _lastPredictorSkipReason = null;
            if (parameters == null) return decoded;

            int predictor = parameters.GetInteger(PdfName.Predictor) ?? 1;
            if (predictor <= 1) return decoded;

            int columns = parameters.GetInteger(PdfName.Columns) ?? 1;
            int colors = parameters.GetInteger(PdfName.Colors) ?? 1;
            int bitsPerComponent = parameters.GetInteger(PdfName.BitsPerComponent) ?? 8;

            if (predictor == 2)
                return RemoveTiffPredictor(decoded, columns, colors, bitsPerComponent);
            if (predictor >= 10 && predictor <= 15)
                return RemovePngPredictor(decoded, columns, colors, bitsPerComponent);

            // 3..9 and >15 are undefined by Table 10. Returning the data unchanged is what both
            // copies did; saying so is what neither did, and "no predictor" versus "a predictor
            // this build does not know" were the same observation to a caller.
            return Skip(decoded, $"/Predictor {predictor} is not defined by Table 10");
        }

        /// <summary>
        /// Why <c>/Columns</c>, <c>/Colors</c> and <c>/BitsPerComponent</c> cannot be used, or
        /// <c>null</c> when they can. <paramref name="which"/> names the predictor in the message.
        /// </summary>
        /// <remarks>
        /// <para><b>The ladder was written twice, differing only in that word.</b> Both predictors
        /// reject the same two shapes for the same reason — a non-positive dimension, and one large
        /// enough that <c>columns × colors × bitsPerComponent</c> could overflow the row arithmetic
        /// below and allocate a wrong-sized buffer. Two copies of a bound are two places for it to
        /// be relaxed.</para>
        ///
        /// <para><b>The <c>/Columns</c> bound is deliberately not <c>PdfLimits.MaxCcittColumns</c></b>,
        /// though the number is the same. That one bounds a CCITT decode's <i>amplification</i>;
        /// this bounds a predictor <i>row's</i> geometry. Two values that coincide are not one
        /// value — see GAPS.md §7.</para>
        /// </remarks>
        private static string RejectDimensions(string which, int columns, int colors, int bitsPerComponent)
        {
            if (columns <= 0 || colors <= 0 || bitsPerComponent <= 0)
                return $"{which} predictor not applied: /Columns {columns}, /Colors {colors} " +
                       $"and /BitsPerComponent {bitsPerComponent} must all be positive.";

            if (columns > MaxPredictorColumns || colors > MaxPredictorColors
                || bitsPerComponent > MaxPredictorBitsPerComponent)
                return $"{which} predictor not applied: /Columns {columns}, /Colors {colors} " +
                       $"or /BitsPerComponent {bitsPerComponent} is out of range.";

            return null;
        }

        /// <summary>Largest <c>/Columns</c> a predictor row may declare.</summary>
        private const int MaxPredictorColumns = 1_000_000;

        /// <summary>Largest <c>/Colors</c> a predictor row may declare.</summary>
        private const int MaxPredictorColors = 32;

        /// <summary>Largest <c>/BitsPerComponent</c> a predictor row may declare.</summary>
        private const int MaxPredictorBitsPerComponent = 32;

        /// <summary>Removes PNG predictor bytes from decoded data.</summary>
        public static byte[] RemovePngPredictor(byte[] data, int columns, int colors, int bitsPerComponent)
        {
            _lastPredictorSkipReason = null;

            string reject = RejectDimensions("PNG", columns, colors, bitsPerComponent);
            if (reject != null) return Skip(data, reject);

            // RejectDimensions bounds a row at 128 MB, so these fit an int; and the output is
            // smaller than the input, so its size does too. Two overflow guards here could not fire
            // and were removed.
            int bytesPerPixel = Math.Max(1, (colors * bitsPerComponent + 7) / 8);
            int rowBytes = (int)(((long)columns * colors * bitsPerComponent + 7) / 8);
            int rowWithFilter = rowBytes + 1;
            int rows = data.Length / rowWithFilter;

            int remaining = data.Length - rows * rowWithFilter;
            var output = new byte[rows * rowBytes + Math.Max(0, remaining - 1)];

            // Row 0's predecessor is all zeros. Allocated once and never written; every later row
            // reads its predecessor straight out of `output`, which is where it already is.
            byte[] zeroRow = rows > 0 ? new byte[rowBytes] : Array.Empty<byte>();

            for (int row = 0; row < rows; row++)
            {
                int src = row * rowWithFilter + 1;
                int dst = row * rowBytes;
                byte filterType = data[src - 1];

                byte[] prev = row > 0 ? output : zeroRow;
                int prevOff = row > 0 ? dst - rowBytes : 0;

                UndoPngRow(filterType, data, src, output, dst, rowBytes, bytesPerPixel, prev, prevOff);
            }

            if (remaining > 1)
            {
                int srcRemaining = rows * rowWithFilter + 1;
                int dstRemaining = rows * rowBytes;
                int copyLen = Math.Min(remaining - 1, output.Length - dstRemaining);
                if (copyLen > 0)
                    Buffer.BlockCopy(data, srcRemaining, output, dstRemaining, copyLen);
            }
            return output;
        }

        // ------------------------------------------------------------------------------------
        //  One loop per PNG filter type.
        //
        //  Each of the four is the same arithmetic the per-byte `switch` did, with the branch
        //  hoisted out and only the neighbours that filter actually uses read. The leading
        //  `bytesPerPixel` bytes of a row have no left neighbour, so `a` and `c` are zero there;
        //  rather than testing that per byte, each arm peels those bytes into their own loop with
        //  the zero substituted algebraically:
        //
        //    Sub      a = 0            ->  raw
        //    Average  a = 0            ->  raw + b/2
        //    Paeth    a = c = 0        ->  raw + b
        //
        //  The Paeth identity is the only one that is not obvious. With a = c = 0: p = b,
        //  pa = b, pb = 0, pc = b. The first test (pa <= pb && pa <= pc) holds only when b = 0,
        //  where it returns a = 0 = b; otherwise pb <= pc holds and it returns b. So the
        //  predictor is b in every case.
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// One PNG-predicted row undone (§7.4.4.4): <paramref name="rowBytes"/> bytes from
        /// <paramref name="data"/> at <paramref name="src"/> — the row past its filter-type byte —
        /// into <paramref name="output"/> at <paramref name="dst"/>, against the decoded row before it
        /// in <paramref name="prev"/> at <paramref name="prevOff"/> (all zeros for the first). The
        /// one switch <see cref="RemovePngPredictor"/> runs per row, and a row-at-a-time reader
        /// (the decimating image decode) runs too, so the two cannot disagree.
        /// </summary>
        internal static void UndoPngRow(byte filterType, byte[] data, int src, byte[] output, int dst, int rowBytes,
            int bytesPerPixel, byte[] prev, int prevOff)
        {
            // The filter type is constant for the whole row (§7.4.4.4), so it is switched on
            // once per row rather than once per byte, and each arm reads only the neighbours
            // it actually needs.
            switch (filterType)
            {
                case 1: UndoSub(data, src, output, dst, rowBytes, bytesPerPixel); break;
                case 2: UndoUp(data, src, output, dst, rowBytes, prev, prevOff); break;
                case 3: UndoAverage(data, src, output, dst, rowBytes, bytesPerPixel, prev, prevOff); break;
                case 4: UndoPaeth(data, src, output, dst, rowBytes, bytesPerPixel, prev, prevOff); break;
                default: Buffer.BlockCopy(data, src, output, dst, rowBytes); break;
            }
        }

        private static void UndoSub(byte[] data, int src, byte[] output, int dst, int rowBytes, int bpp)
        {
            int lead = Math.Min(bpp, rowBytes);
            Buffer.BlockCopy(data, src, output, dst, lead);
            for (int i = lead; i < rowBytes; i++)
                output[dst + i] = (byte)(data[src + i] + output[dst + i - bpp]);
        }

        private static void UndoUp(byte[] data, int src, byte[] output, int dst, int rowBytes,
            byte[] prev, int prevOff)
        {
            for (int i = 0; i < rowBytes; i++)
                output[dst + i] = (byte)(data[src + i] + prev[prevOff + i]);
        }

        private static void UndoAverage(byte[] data, int src, byte[] output, int dst, int rowBytes,
            int bpp, byte[] prev, int prevOff)
        {
            int lead = Math.Min(bpp, rowBytes);
            for (int i = 0; i < lead; i++)
                output[dst + i] = (byte)(data[src + i] + (prev[prevOff + i] >> 1));
            for (int i = lead; i < rowBytes; i++)
                output[dst + i] =
                    (byte)(data[src + i] + ((output[dst + i - bpp] + prev[prevOff + i]) >> 1));
        }

        private static void UndoPaeth(byte[] data, int src, byte[] output, int dst, int rowBytes,
            int bpp, byte[] prev, int prevOff)
        {
#if NET
            if (TryUndoPaethVector(data, src, output, dst, rowBytes, bpp, prev, prevOff)) return;
#endif
            UndoPaethScalar(data, src, output, dst, rowBytes, bpp, prev, prevOff);
        }

#if NET
        /// <summary>
        /// <see cref="UndoPaethScalar"/> for 3- and 4-byte pixels, a pixel at a time: each channel of a
        /// pixel is an independent chain, so the pixel's channels go through the predictor together
        /// in one vector, and the left and upper-left pixels stay in registers from one pixel to the
        /// next, where the scalar loop reads each byte's neighbours back from the arrays. The same
        /// predictor, lane by lane (Paeth's three distances, the ties broken a, then b, then c), so
        /// the bytes are the scalar loop's (<c>PaethVectorParityTests</c>). Measured on the rows alone,
        /// about 2.8 ns a byte against the scalar loop's 4.4 (GAPS §13e). False, doing nothing, for any other
        /// depth, a row too short for one vector step, or no vector hardware.
        /// </summary>
        /// <remarks>
        /// A 3-byte pixel is loaded and stored as four bytes: the fourth lane computes nonsense that
        /// only the fourth lane reads, and its store is overwritten by the next pixel's — which is why a
        /// 3-byte row undone in place (the input and the output one buffer, as the PNG decoder has it)
        /// is left to the scalar loop: there the fourth byte is the next pixel's raw byte, not yet read.
        /// Storing three bytes singly instead measured slower than the scalar loop. A vector step runs
        /// only while four bytes remain in the row; the rest of the row is scalar.
        /// </remarks>
        internal static bool TryUndoPaethVector(byte[] data, int src, byte[] output, int dst, int rowBytes,
            int bpp, byte[] prev, int prevOff)
        {
            if ((bpp != 3 && bpp != 4) || !Vector128.IsHardwareAccelerated || rowBytes < bpp + 4) return false;
            // In place, a 3-byte pixel's fourth stored byte would be the next pixel's raw byte, not yet read.
            if (bpp == 3 && ReferenceEquals(data, output)) return false;

            for (int k = 0; k < bpp; k++)
                output[dst + k] = (byte)(data[src + k] + prev[prevOff + k]);

            // The row's three spans taken once, bounds-checked once; each step reads within them.
            ReadOnlySpan<byte> rawRow = data.AsSpan(src, rowBytes);
            ReadOnlySpan<byte> prevRow = prev.AsSpan(prevOff, rowBytes);
            Span<byte> outRow = output.AsSpan(dst, rowBytes);

            Vector128<short> a = Pixel(outRow, 0);
            Vector128<short> c = Pixel(prevRow, 0);
            Vector128<short> low = Vector128.Create((short)0xFF);
            int i = bpp;
            for (; i + 4 <= rowBytes; i += bpp)
            {
                Vector128<short> b = Pixel(prevRow, i);
                Vector128<short> raw = Pixel(rawRow, i);

                Vector128<short> pa = Vector128.Abs(b - c);
                Vector128<short> pb = Vector128.Abs(a - c);
                Vector128<short> pc = Vector128.Abs(a + b - c - c);
                Vector128<short> useA = Vector128.LessThanOrEqual(pa, pb) & Vector128.LessThanOrEqual(pa, pc);
                Vector128<short> useB = Vector128.LessThanOrEqual(pb, pc);
                Vector128<short> pred = Vector128.ConditionalSelect(useA, a, Vector128.ConditionalSelect(useB, b, c));

                a = (raw + pred) & low;
                Vector128<byte> bytes = Vector128.Narrow(a.AsUInt16(), a.AsUInt16());
                BinaryPrimitives.WriteUInt32LittleEndian(outRow.Slice(i), bytes.AsUInt32().ToScalar());
                c = b;
            }

            for (; i < rowBytes; i++)
                output[dst + i] = (byte)(data[src + i] + PaethPredictor(output[dst + i - bpp], prev[prevOff + i], prev[prevOff + i - bpp]));
            return true;
        }

        /// <summary>Four bytes at <paramref name="offset"/>, one a lane in order, widened to 16 bits; bounds-checked as a span is.</summary>
        private static Vector128<short> Pixel(ReadOnlySpan<byte> bytes, int offset)
            => Vector128.WidenLower(Vector128.CreateScalar(BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset))).AsByte()).AsInt16();
#endif

        internal static void UndoPaethScalar(byte[] data, int src, byte[] output, int dst, int rowBytes,
            int bpp, byte[] prev, int prevOff)
        {
            int lead = Math.Min(bpp, rowBytes);
            for (int i = 0; i < lead; i++)
                output[dst + i] = (byte)(data[src + i] + prev[prevOff + i]);

            for (int i = lead; i < rowBytes; i++)
            {
                int a = output[dst + i - bpp];
                int b = prev[prevOff + i];
                int c = prev[prevOff + i - bpp];

                // Branchless, because the branches here are the whole cost. Measured on an
                // 810 KB image: this loop ran at 7.5 ns/byte where Up managed 0.95 and the TIFF
                // running sum 1.04 — and those two rule out every explanation except branch
                // misprediction. Up allocates the same buffers (slightly more, in fact) so it is
                // not allocation, and TIFF carries the tightest possible dependency chain
                // (every byte waits on its immediate predecessor) so it is not the chain either.
                // What is left is three data-dependent comparisons per byte over image noise,
                // which predicts no better than chance.
                //
                //   pa = |p-a|, and so on, via the sign-mask identity |x| = (x ^ s) - s.
                //   maskA = -1 unless (pa <= pb && pa <= pc); maskB = -1 unless (pb <= pc).
                //   Both differences are bounded by +/-510, so neither subtraction can overflow.
                int p = a + b - c;

                int pa = p - a; int s = pa >> 31; pa = (pa ^ s) - s;
                int pb = p - b; s = pb >> 31; pb = (pb ^ s) - s;
                int pc = p - c; s = pc >> 31; pc = (pc ^ s) - s;

                int maskA = ((pb - pa) | (pc - pa)) >> 31;
                int maskB = (pc - pb) >> 31;

                int pred = (~maskA & a) | (maskA & ((~maskB & b) | (maskB & c)));
                output[dst + i] = (byte)(data[src + i] + pred);
            }
        }

        /// <summary>Removes TIFF predictor (predictor=2) from decoded data.</summary>
        public static byte[] RemoveTiffPredictor(byte[] data, int columns, int colors, int bitsPerComponent)
        {
            _lastPredictorSkipReason = null;

            string reject = RejectDimensions("TIFF", columns, colors, bitsPerComponent);
            if (reject != null) return Skip(data, reject);

            // At least one byte and at most 128 MB, by RejectDimensions.
            int rowBytes = (int)(((long)columns * colors * bitsPerComponent + 7) / 8);
            int rows = data.Length / rowBytes;

            // The TIFF horizontal differencing predictor operates on whole samples,
            // so the granularity depends on BitsPerComponent.  The byte-granular
            // path below is only correct for 8-bpc; 16-bpc samples must be added as
            // big-endian ushorts, and sub-byte depths (1/2/4) would require bit-level
            // accumulation.  Branch accordingly rather than corrupting the output.
            if (bitsPerComponent == 16)
                return RemoveTiffPredictor16(data, rowBytes, colors);

            if (bitsPerComponent != 8)
            {
                // 1, 2 and 4 bits per component. ISO 32000-1 §7.4.4.4 permits these and a 4-bpc
                // indexed image with /Predictor 2 is ordinary, spec-conformant input — returned still
                // differenced, the image would render as a horizontal colour smear
                // indistinguishable from an image that simply looked like that. Sub-byte differencing is the same running sum, done over bit fields.
                if (bitsPerComponent == 1 || bitsPerComponent == 2 || bitsPerComponent == 4)
                    return RemoveTiffPredictorSubByte(data, rowBytes, rows, colors,
                        bitsPerComponent, columns);

                return Skip(data, $"TIFF predictor not applied: /BitsPerComponent " +
                                  $"{bitsPerComponent} is not 1, 2, 4, 8 or 16.");
            }

            // 8-bpc: one byte per sample. A last row shorter than the rest is decoded as far as it
            // goes: it was left out, and its bytes came back as zeros in a buffer the input's length.
            var output = new byte[data.Length];
            for (int row = 0; row * rowBytes < data.Length; row++)
            {
                int off = row * rowBytes;
                UndoTiffRow(data, off, output, off, Math.Min(rowBytes, data.Length - off), colors, 8);
            }
            return output;
        }

        /// <summary>
        /// Undoes the TIFF predictor (2) on one row of 8- or 16-bit samples: <paramref name="length"/>
        /// bytes from <paramref name="data"/> at <paramref name="src"/> into <paramref name="output"/> at
        /// <paramref name="dst"/>, each sample the running sum with the same component of the pixel
        /// before it (<paramref name="colors"/> samples a pixel) — 16-bit samples big-endian, an odd
        /// trailing byte kept as it is. The whole-buffer removal and the decimating decode alike.
        /// </summary>
        internal static void UndoTiffRow(byte[] data, int src, byte[] output, int dst, int length, int colors, int bitsPerComponent)
        {
            if (bitsPerComponent == 16)
            {
                int samples = length / 2;
                if ((length & 1) != 0) output[dst + length - 1] = data[src + length - 1];
                for (int s = 0; s < samples; s++)
                {
                    int cur = (data[src + s * 2] << 8) | data[src + s * 2 + 1];
                    if (s >= colors)
                    {
                        int prevPos = dst + (s - colors) * 2;
                        cur = (cur + ((output[prevPos] << 8) | output[prevPos + 1])) & 0xFFFF;
                    }
                    output[dst + s * 2] = (byte)(cur >> 8);
                    output[dst + s * 2 + 1] = (byte)(cur & 0xFF);
                }
                return;
            }
            // The first pixel has no left neighbour, so its bytes are copied and the rest is a
            // running sum. Peeled rather than tested per byte, as the PNG arms are.
            int lead = Math.Min(colors, length);
            Buffer.BlockCopy(data, src, output, dst, lead);
            for (int col = lead; col < length; col++)
                output[dst + col] = (byte)(data[src + col] + output[dst + col - colors]);
        }

        /// <summary>
        /// TIFF predictor removal for 1, 2 and 4 bits per component (ISO 32000-1 §7.4.4.4).
        /// </summary>
        /// <remarks>
        /// Same running sum as the 8-bpc path, over bit fields instead of bytes: each sample is
        /// added to the sample <paramref name="colors"/> positions earlier in the row and wrapped
        /// modulo 2^bpc. Rows are independent and each starts on a byte boundary, so the trailing
        /// padding bits of a row are never carried into the next one.
        /// </remarks>
        private static byte[] RemoveTiffPredictorSubByte(byte[] data, int rowBytes, int rows,
            int colors, int bitsPerComponent, int columns)
        {
            var output = new byte[data.Length];
            Buffer.BlockCopy(data, 0, output, 0, data.Length);

            // Nothing to decode, so nothing is sized from the dimensions. The row buffer below was
            // allocated first, from /Columns × /Colors alone: /Colors 32 /BitsPerComponent 4
            // /Columns 1000000 over a ten-byte stream bought 128 MB (32 M samples as ints) for zero
            // rows, reached from any Flate or LZW stream's /DecodeParms. With a row present the data holds at least one row's bytes, so the buffer — a
            // byte per sample, since a sample is at most four bits — is at most eight times the data.
            if (rows == 0) return output;

            int mask = (1 << bitsPerComponent) - 1;
            int samplesPerRow = columns * colors;
            var row = new byte[samplesPerRow];

            for (int r = 0; r < rows; r++)
            {
                int rowStart = r * rowBytes;

                for (int s = 0; s < samplesPerRow; s++)
                {
                    int bitPos = s * bitsPerComponent;
                    int byteIndex = rowStart + (bitPos >> 3);
                    if (byteIndex >= data.Length) { row[s] = 0; continue; }
                    int shift = 8 - bitsPerComponent - (bitPos & 7);
                    int cur = (data[byteIndex] >> shift) & mask;
                    row[s] = (byte)(s >= colors ? (cur + row[s - colors]) & mask : cur);
                }

                for (int s = 0; s < samplesPerRow; s++)
                {
                    int bitPos = s * bitsPerComponent;
                    int byteIndex = rowStart + (bitPos >> 3);
                    if (byteIndex >= output.Length) break;
                    int shift = 8 - bitsPerComponent - (bitPos & 7);
                    output[byteIndex] = (byte)((output[byteIndex] & ~(mask << shift))
                                               | (row[s] << shift));
                }
            }
            return output;
        }

        /// <summary>
        /// TIFF predictor removal for 16-bpc samples.  Each component is a big-endian
        /// ushort differenced against the same component in the previous pixel.
        /// </summary>
        private static byte[] RemoveTiffPredictor16(byte[] data, int rowBytes, int colors)
        {
            var output = new byte[data.Length];

            // A last row shorter than the rest is decoded as far as its whole samples go, and an
            // odd trailing byte is kept as it is — never zeros.
            for (int row = 0; row * rowBytes < data.Length; row++)
            {
                int off = row * rowBytes;
                UndoTiffRow(data, off, output, off, Math.Min(rowBytes, data.Length - off), colors, 16);
            }
            return output;
        }

        /// <summary>Paeth predictor function (PNG spec).</summary>
        public static byte PaethPredictor(byte a, byte b, byte c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }
    }
}
