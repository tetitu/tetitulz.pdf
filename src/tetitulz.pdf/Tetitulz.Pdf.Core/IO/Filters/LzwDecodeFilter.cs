// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    public sealed class LzwDecodeFilter : StreamFilter
    {
        /// <summary>
        /// Why the most recent <see cref="Decode"/> on <b>this thread</b> stopped before the end
        /// of its input, or <c>null</c> when the whole stream decoded.
        /// </summary>
        /// <remarks>
        /// <para>The decoder is deliberately lenient — a corrupt code stops it and it returns what
        /// it has, as lenient readers do — but leniency plus silence is the combination CLAUDE.md's
        /// Diagnostics Pattern forbids: a short-but-valid stream and a stream that aborted three
        /// bytes in were byte-for-byte indistinguishable to the caller. A page whose content
        /// stream is corrupt at offset 4 000 of 60 000 simply renders with a tenth of its content
        /// and no thread of diagnosis. <c>CcittFaxDecodeFilter</c> and <c>Jbig2DecodeFilter</c>
        /// both expose their equivalent.</para>
        /// <para>Reset at the start of <see cref="Decode"/>, on every path.</para>
        /// </remarks>
        public static string LastTruncationReason => _lastTruncationReason;

        [ThreadStatic]
        private static string _lastTruncationReason;

        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            _lastTruncationReason = null;

            int earlyChange = 1;
            if (parameters != null)
            {
                earlyChange = parameters.GetInteger(PdfName.EarlyChange) ?? 1;
            }

            byte[] decompressed = DecompressLzw(data, 0, data.Length, earlyChange, MaxOutputBytes, out _);

            // Shared with FlateDecodeFilter; see PredictorHelper.Apply. LZW is the leg exercised by
            // older producers, so a divergence between the two copies would have shown up on a
            // different corpus from the one that finds Flate bugs — which is how it survives.
            return PredictorHelper.Apply(decompressed, parameters);
        }

        /// <summary>
        /// Decodes an LZW stream that lies inside a longer byte run — a PostScript program's
        /// <c>currentfile /LZWDecode filter</c> source — and says where it ended, so the bytes after
        /// it stay the embedder's. No predictor is applied: the caller has no <c>/DecodeParms</c>.
        /// </summary>
        /// <param name="data">The bytes the stream lies in.</param>
        /// <param name="start">Index of the stream's first byte.</param>
        /// <param name="count">How many bytes from <paramref name="start"/> the stream may occupy.</param>
        /// <param name="earlyChange">§7.4.4.2's <c>/EarlyChange</c>; 1 is the default on both sides.</param>
        /// <param name="maxOutputBytes">Ceiling on the decoded size; crossing it throws, as <see cref="Decode"/> does.</param>
        /// <param name="end">
        /// Index just past the byte holding the last bit of the EOD code, or -1 when the bytes ran
        /// out or a corrupt code stopped the decode first — <see cref="LastTruncationReason"/> then
        /// says which, and the bytes returned are what was decoded up to that point.
        /// </param>
        public static byte[] DecodeEmbedded(byte[] data, int start, int count, int earlyChange,
                                            long maxOutputBytes, out int end)
        {
            CheckEmbeddedRange(data, start, count);
            _lastTruncationReason = null;
            return DecompressLzw(data, start, count, earlyChange, maxOutputBytes, out end);
        }

        // Defence-in-depth against LZW decompression bombs.  Real PDF streams
        // almost never expand past a few tens of MB per segment; 512 MiB is
        // comfortably above legitimate use and well below "exhaust memory".
        // A caller with a smaller ceiling of its own passes it in through
        // StreamFilter.MaxOutputBytes.
        private static byte[] DecompressLzw(byte[] data, int start, int count, int earlyChange,
                                            long maxDecompressedBytes, out int end)
            => DecompressLzwTo(data, start, count, earlyChange, maxDecompressedBytes, out end, null);

        /// <summary>
        /// Decodes an LZW stream's whole run from <paramref name="data"/>, handing the decoded bytes to
        /// <paramref name="write"/> as they are produced, a run of strings at a time (buffer, offset, count —
        /// the buffer reused after the call returns; what was decoded before a corrupt code stopped the
        /// decode is handed over, what preceded a refusal at the ceiling need not be), under the same
        /// ceiling and leniency as <see cref="Decode"/>, no predictor
        /// applied — for a caller that consumes the output as it comes (the raster renderer's decimating
        /// decode). <see cref="LastTruncationReason"/> says whether a corrupt code stopped it.
        /// </summary>
        internal static void DecodeTo(byte[] data, int earlyChange, long maxOutputBytes, Action<byte[], int, int> write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            _lastTruncationReason = null;
            DecompressLzwTo(data, 0, data.Length, earlyChange, maxOutputBytes, out _, write);
        }

        /// <summary>
        /// The decode: each code's string written into <c>buffer</c> at <c>pos</c> — handed to
        /// <paramref name="write"/> and the scratch reused when there is a sink, else kept in chunks and
        /// returned as one array (<c>null</c> with a sink).
        /// </summary>
        private static byte[] DecompressLzwTo(byte[] data, int start, int count, int earlyChange,
                                              long maxDecompressedBytes, out int end, Action<byte[], int, int> write)
        {
            const int ClearTable = 256;
            const int EndOfData = 257;

            // Where the stream ended, for a caller whose bytes continue after it: set only when the
            // EOD code was read whole, within the run.
            end = -1;
            int limit = start + count;

            long written = 0;

            // The table is CHAIN-CODED rather than a List<byte[]>.
            //
            // Entry i is "the string of entry Prefix[i], followed by Suffix[i]", with Length[i]
            // its total byte count; codes 0..255 are the single bytes and are their own roots.
            // A list-of-arrays form would allocate a fresh array for every entry — up to 4 096 per
            // clear cycle, each one byte longer than the last, so the memory a cycle costs would be
            // quadratic in the dictionary size — and rebuild 256 single-byte arrays on EVERY
            // ClearTable code. Measured on a 512 KB payload,
            // 4.541 ms and 6.05 MB (FilterBenchmarks.LzwDecode).
            //
            // The three arrays are allocated once per decode and the roots are written once;
            // a ClearTable is then just `tableCount = 258`, which is the whole point.
            const int MaxCodes = 1 << 12;   // the 12-bit code space; see the cap below
            var prefix = new int[MaxCodes];
            var suffix = new byte[MaxCodes];
            var lengths = new int[MaxCodes];

            for (int i = 0; i < 256; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
                lengths[i] = 1;
            }
            lengths[ClearTable] = 0;   // not strings; Length 0 marks a code that decodes to nothing
            lengths[EndOfData] = 0;
            int tableCount = 258;

            // Where each string is written. An entry grows by one byte per table slot, so no string
            // (with the one byte the special case below adds) exceeds MaxCodes + 1, the room kept free
            // at `pos`. With a sink it is a buffer handed over whenever its free room runs short, and at
            // the end; without one the strings are written where they stay — a chunk sized from the
            // stream, then chunks of ChunkBytes, joined into one array at the end. A MemoryStream
            // written a string at a time cost a call per code and 182 MB of growth and copies for 48 MB
            // of output; a sink called a string at a time, a call per code — strings average two bytes
            // in a photograph's samples — made the decimating decode slower than the whole one.
            const int Room = MaxCodes + 1;
            List<byte[]> fullChunks = null;
            List<int> fullLengths = null;
            byte[] buffer = write != null
                ? new byte[SinkBytes]
                : new byte[(int)Math.Min(ChunkBytes, Math.Max(4 * Room, (long)count * 3))];
            int pos = 0;

            // Walks a code's chain backwards into the buffer at `pos`, which is why the chain form
            // costs no allocation: the bytes are produced on demand instead of being stored per entry.
            int Materialise(int code)
            {
                int len = lengths[code];
                int at = pos + len;
                int c = code;
                while (c >= 0 && at > pos)
                {
                    buffer[--at] = suffix[c];
                    c = prefix[c];
                }
                return len;
            }

            // The string at `pos` is done: kept, and the room renewed when it runs short — the buffer
            // handed to the sink and reused, or kept as a chunk and a new one begun.
            void Emit(int len)
            {
                written += len;
                pos += len;
                if (buffer.Length - pos >= Room) return;
                if (write != null)
                {
                    write(buffer, 0, pos);
                    pos = 0;
                }
                else
                {
                    (fullChunks ??= new List<byte[]>()).Add(buffer);
                    (fullLengths ??= new List<int>()).Add(pos);
                    buffer = new byte[ChunkBytes];
                    pos = 0;
                }
            }

            // Hoist the total-bit count once so both end-of-data bounds checks use
            // the same long arithmetic.  Computing it inline as `data.Length * 8`
            // overflows to a negative int for data.Length >= 268435456 (256 MiB),
            // which would terminate the decode prematurely.
            long totalBits = (long)limit * 8;

            long bitPos = (long)start * 8;
            int codeSize = 9;
            int prevCode = -1;

            // The codes are read through a bit accumulator refilled a byte at a time — MSB first,
            // a byte past the run read as zeros, which no code uses: one cut by the run's end is
            // refused below by bitPos. Reading each code a bit at a time through ReadBits, a
            // division and a remainder per bit, was most of the decode: 924 ms for 48 MB of
            // samples, the same with a sink that kept nothing.
            ulong bits = 0;
            int bitCount = 0;
            int nextByte = start;
            int NextCode()
            {
                while (bitCount < codeSize)
                {
                    bits = (bits << 8) | (nextByte < limit ? data[nextByte] : 0u);
                    nextByte++;
                    bitCount += 8;
                }
                bitCount -= codeSize;
                bitPos += codeSize;
                return (int)(bits >> bitCount) & ((1 << codeSize) - 1);
            }

            while (true)
            {
                int code = NextCode();

                // A code cut by the end of the run is not a code, EOD included: it bounds nothing.
                if (bitPos > totalBits)
                    break;
                if (code == EndOfData)
                {
                    end = (int)((bitPos + 7) / 8);
                    break;
                }

                if (code == ClearTable)
                {
                    tableCount = 258;
                    codeSize = 9;
                    prevCode = -1;

                    code = NextCode();

                    if (bitPos > totalBits)
                        break;
                    if (code == EndOfData)
                    {
                        end = (int)((bitPos + 7) / 8);
                        break;
                    }

                    if (code < 256)
                    {
                        if (written + 1 > maxDecompressedBytes)
                            throw new System.IO.InvalidDataException(
                                $"LZWDecode: output exceeded {maxDecompressedBytes} bytes");
                        buffer[pos] = (byte)code;
                        Emit(1);
                        prevCode = code;
                    }
                    else if (code != ClearTable)
                    {
                        // After a ClearTable the table holds the 256 single bytes and the two
                        // control codes, so 258 or more names nothing — a corruption: the decode
                        // stops there and says so, as for every other code.
                        _lastTruncationReason =
                            $"LZWDecode stopped at input bit {bitPos - codeSize} (byte " +
                            $"{(bitPos - codeSize) / 8} of {limit}): code {code} follows a " +
                            $"ClearTable, where only a single byte, ClearTable or EndOfData can. " +
                            $"{written} byte(s) were produced; the rest of the stream is missing.";
                        break;
                    }
                    continue;
                }

                int entryLength;
                if (code < tableCount && lengths[code] > 0)
                {
                    entryLength = Materialise(code);
                }
                else if (code == tableCount && prevCode >= 0)
                {
                    // Special case: the code names the entry about to be added, which is the
                    // previous string followed by its own first byte.
                    entryLength = Materialise(prevCode);
                    buffer[pos + entryLength] = buffer[pos];
                    entryLength++;
                }
                else
                {
                    // Corrupt/invalid LZW code — stop and return what we have (lenient, as other
                    // readers are), but SAY SO: the truncated buffer is otherwise indistinguishable
                    // from a stream that legitimately ended here.
                    _lastTruncationReason =
                        $"LZWDecode stopped at input bit {bitPos - codeSize} (byte " +
                        $"{(bitPos - codeSize) / 8} of {limit}): code {code} is neither in " +
                        $"the {tableCount}-entry table nor the next entry to be added. " +
                        $"{written} byte(s) were produced; the rest of the stream is missing.";
                    break;
                }

                if (written + entryLength > maxDecompressedBytes)
                    throw new System.IO.InvalidDataException(
                        $"LZWDecode: output exceeded {maxDecompressedBytes} bytes");
                byte firstByte = buffer[pos];
                Emit(entryLength);

                if (prevCode >= 0)
                {
                    // The 12-bit code space addresses at most 4096 entries.  Once the
                    // table is full a conformant encoder MUST emit ClearTable before
                    // reusing codes; a crafted stream that never does would otherwise
                    // keep appending unaddressable entries (indices > 4095) and bloat
                    // memory without bound.  Stop growing the table at the cap.
                    if (tableCount < MaxCodes)
                    {
                        prefix[tableCount] = prevCode;
                        suffix[tableCount] = firstByte;
                        lengths[tableCount] = lengths[prevCode] + 1;
                        tableCount++;

                        // Increase code size when table reaches threshold
                        int threshold = (1 << codeSize) - earlyChange;
                        if (tableCount >= threshold && codeSize < 12)
                        {
                            codeSize++;
                        }
                    }
                }

                prevCode = code;
            }

            if (write != null)
            {
                if (pos > 0) write(buffer, 0, pos);
                return null;
            }
            var result = new byte[written];
            int at = 0;
            if (fullChunks != null)
                for (int i = 0; i < fullChunks.Count; i++)
                {
                    Buffer.BlockCopy(fullChunks[i], 0, result, at, fullLengths[i]);
                    at += fullLengths[i];
                }
            Buffer.BlockCopy(buffer, 0, result, at, pos);
            return result;
        }

        /// <summary>
        /// The chunk the whole decode keeps its output in before joining it, the first one aside (sized
        /// from the stream, no larger): a fixed size, so the room the last one leaves unused is bounded —
        /// chunks that doubled could leave half their total unused (2.9 times the output allocated).
        /// </summary>
        private const int ChunkBytes = 1 << 20;

        /// <summary>The buffer a sink is handed the output in, a run of strings at a time — below the large-object heap.</summary>
        private const int SinkBytes = 64 << 10;

        /// <summary>
        /// Reads <paramref name="numBits"/> MSB-first from <paramref name="bitPos"/>, returning the
        /// bits read so far once the position runs past the end of <paramref name="data"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="bitPos"/> is a <c>long</c> to match <c>totalBits</c>. As an <c>int</c>,
        /// past ~268 MB of input <c>bitPos</c> would wrap negative, the caller's
        /// <c>bitPos &gt; totalBits</c> guard compared a negative int against a positive long and
        /// stayed false, and <c>data[byteIndex]</c> then threw <c>IndexOutOfRangeException</c> —
        /// straight out through <c>FilterPipeline.Decode</c> and <c>PdfStream.GetDecodedData</c>,
        /// neither of which catches it, as an untyped runtime exception rather than the
        /// <c>InvalidDataException</c> every other bomb guard in this folder raises. The negative
        /// check below is kept as defence in depth.
        /// </remarks>
        /// <remarks>
        /// <para>One implementation, in <see cref="MsbBitReader"/>. The narrowing cast is safe for
        /// every caller here: <c>numBits</c> is the LZW code size, initialised to 9 and bounded by
        /// <c>codeSize &lt; 12</c>, so the value cannot exceed 12 bits. It is also
        /// <b>exact at 32</b>, returning <c>-1</c> — which is what <c>(int)</c> of <c>0xFFFFFFFF</c>
        /// is.</para>
        /// </remarks>
        internal static int ReadBits(byte[] data, long bitPos, int numBits)
            => (int)MsbBitReader.ReadBits(data, bitPos, numBits);

        // Predictor methods moved to PredictorHelper.cs (DRY refactoring)
    }
}
