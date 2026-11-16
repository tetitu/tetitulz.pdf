// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    public sealed class FlateDecodeFilter : StreamFilter
    {
        /// <summary>
        /// Why the most recent <see cref="Decode"/> on <b>this thread</b> returned less than the
        /// whole stream, or <c>null</c> when the stream decoded whole.
        /// </summary>
        /// <remarks>
        /// <para><see cref="DeflateStream"/> stops at the end of its input without a word when the
        /// final block never arrived, so a stream cut short at a block boundary came back as the
        /// bytes it had, as if they were the whole — the shape <see cref="LzwDecodeFilter.LastTruncationReason"/>
        /// reports for LZW. The inflater does not say
        /// whether it read the final block; what the zlib framing (RFC 1950) says is the Adler-32 of
        /// the whole uncompressed stream, in the four bytes after the deflate data. A stream whose
        /// trailer is missing or disagrees with the bytes produced is truncated or corrupt, and this
        /// says so; the bytes are still returned, as every lenient decoder here returns what it has.
        /// Trailing bytes after the trailer (a producer's end-of-line inside <c>/Length</c>) are
        /// allowed for: the trailer is looked for in the last <see cref="TrailerSearchBytes"/> bytes.</para>
        /// <para><b>A raw deflate stream (no zlib header) carries no trailer and is not checked</b>:
        /// nothing in it says where it should have ended. Input of one byte, which no deflate
        /// stream is, decodes to nothing and is reported here; it was returned undecoded as the
        /// output.</para>
        /// <para>Reset at the start of <see cref="Decode"/>, on every path.</para>
        /// </remarks>
        public static string LastTruncationReason => _lastTruncationReason;

        [ThreadStatic]
        private static string _lastTruncationReason;

        /// <summary>
        /// How far back from the end of the input the Adler-32 trailer is looked for: the four
        /// trailer bytes, then up to this many bytes of whatever a producer wrote after them.
        /// </summary>
        private const int TrailerSearchBytes = 4 + 32;

        public override byte[] Decode(byte[] data, PdfDictionary parameters)
        {
            _lastTruncationReason = null;

            byte[] decompressed = Decompress(data, MaxOutputBytes);

            // §7.4.4.4 predictor handling is identical for Flate and LZW, and lives in one place,
            // the lines that choose between the algorithms included.
            return PredictorHelper.Apply(decompressed, parameters);
        }

        /// <summary>
        /// Decodes a Flate stream that lies inside a longer byte run — a PostScript program's
        /// <c>currentfile /FlateDecode filter</c> source — and says where it ended, which nothing in
        /// the deflate format states and <see cref="DeflateStream"/> does not report. No predictor is
        /// applied: the caller has no <c>/DecodeParms</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>The end is measured, not estimated.</b> The inflater reads from its source only
        /// when it has used what it was given, and stops once the final block is in. A first pass
        /// hands it the bytes in chunks and collects the output; the end lies in the last chunk it
        /// took. A second pass hands it everything before that chunk at once, then one byte at a
        /// time, and the byte it stops asking at is the last byte of the deflate data. Under the
        /// zlib framing PostScript's filter requires (RFC 1950; PLRM §3.13.3), the four Adler-32
        /// bytes after it are the stream's too, and are checked against the output.</para>
        /// <para>A stream whose final block never arrives, whose trailer does not fit before the
        /// run ends, or that took every byte of the run without finishing has no end to report:
        /// <paramref name="end"/> is -1, the bytes produced are returned, and
        /// <see cref="LastTruncationReason"/> says why — the shape every lenient decoder here keeps.
        /// A trailer that disagrees with the output is reported the same way, with the end still
        /// where the framing puts it.</para>
        /// </remarks>
        /// <param name="data">The bytes the stream lies in.</param>
        /// <param name="start">Index of the stream's first byte — the zlib header's, when there is one.</param>
        /// <param name="count">How many bytes from <paramref name="start"/> the stream may occupy.</param>
        /// <param name="maxOutputBytes">Ceiling on the decoded size; crossing it throws, as <see cref="Decode"/> does.</param>
        /// <param name="end">Index just past the stream — past its Adler-32 trailer, or past the final block of a raw deflate stream — or -1.</param>
        public static byte[] DecodeEmbedded(byte[] data, int start, int count, long maxOutputBytes, out int end)
        {
            CheckEmbeddedRange(data, start, count);
            _lastTruncationReason = null;
            end = -1;

            if (count < 2)
            {
                // No deflate stream is one byte (an empty raw one is two).
                _lastTruncationReason = $"FlateDecode: a {count}-byte stream holds no deflate data; nothing was produced.";
                return Array.Empty<byte>();
            }

            int limit = start + count;
            int offset = (data[start] & 0x0F) == 0x08 ? start + 2 : start;

            byte[] output;
            int deflateEnd;
            try
            {
                output = InflateMeasured(data, offset, limit, maxOutputBytes, out deflateEnd);
            }
            catch (PdfException) { throw; }
            catch (Exception) when (offset != start)
            {
                // Not zlib-framed after all: the two bytes were deflate data. Decompress makes the
                // same retreat.
                offset = start;
                try { output = InflateMeasured(data, start, limit, maxOutputBytes, out deflateEnd); }
                catch (PdfException) { throw; }
                catch (Exception ex) { throw new PdfException("Failed to decompress Flate stream.", ex); }
            }
            catch (Exception ex) { throw new PdfException("Failed to decompress Flate stream.", ex); }

            if (deflateEnd < 0)
            {
                _lastTruncationReason =
                    $"FlateDecode: the final deflate block never arrived within the {count} input byte(s); " +
                    $"{output.Length} byte(s) were produced, and may be a prefix.";
                return output;
            }

            // Raw deflate: nothing follows the final block.
            if (offset == start)
            {
                end = deflateEnd;
                return output;
            }

            if (deflateEnd + 4 > limit)
            {
                _lastTruncationReason =
                    "FlateDecode: the deflate data ended, but the zlib Adler-32 trailer does not fit in the " +
                    $"{limit - deflateEnd} byte(s) after it; the stream is truncated.";
                return output;
            }

            uint adler  = FlateEncodeFilter.ComputeAdler32(output, output.Length);
            uint stored = (uint)(data[deflateEnd] << 24 | data[deflateEnd + 1] << 16 |
                                 data[deflateEnd + 2] << 8 | data[deflateEnd + 3]);
            if (stored != adler)
                _lastTruncationReason =
                    $"FlateDecode: the zlib Adler-32 trailer ({stored:X8}) disagrees with the {output.Length} " +
                    $"byte(s) produced ({adler:X8}); the stream is corrupt.";
            end = deflateEnd + 4;
            return output;
        }

        /// <summary>
        /// Decodes a zlib stream (RFC 1950) that must be one: the two-byte header — deflate, a
        /// window of at most 32 KiB, the check bits, no preset dictionary — the deflate data, and
        /// the Adler-32 of what it produced. Anything else throws; nothing is retried as raw
        /// deflate and nothing is reported on <see cref="LastTruncationReason"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Decode"/> and <see cref="DecodeEmbedded"/> read what a damaged or
        /// loosely written file holds: a stream whose header does not lead to deflate data is tried
        /// again from its first byte as raw deflate, and a trailer that disagrees is said, not
        /// refused. That is the wrong reading for a caller that admits a file only when every
        /// reader sees the same bytes in it — a stream this library reads by retreating is one a
        /// zlib reader refuses, so the two show different documents. This is the reading with no
        /// retreat in it.
        /// </remarks>
        /// <param name="data">The bytes the stream lies in.</param>
        /// <param name="start">Index of the zlib header's first byte.</param>
        /// <param name="count">How many bytes from <paramref name="start"/> the stream may occupy.</param>
        /// <param name="maxOutputBytes">Ceiling on the decoded size; crossing it throws.</param>
        /// <param name="end">Index just past the stream's Adler-32 trailer. A caller that allows nothing after the stream compares it with the end of the run.</param>
        /// <exception cref="PdfException">The run does not begin with one complete, self-consistent zlib stream.</exception>
        public static byte[] DecodeZlib(byte[] data, int start, int count, long maxOutputBytes, out int end)
        {
            CheckEmbeddedRange(data, start, count);
            _lastTruncationReason = null;

            // Header, an empty final block at the least, trailer.
            if (count < 2 + 2 + 4)
                throw new PdfException($"FlateDecode: {count} byte(s) are fewer than a zlib stream's header, data and trailer.");

            int cmf = data[start], flg = data[start + 1];
            if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0)
                throw new PdfException("FlateDecode: the stream does not begin with a zlib header (RFC 1950 §2.2).");
            if ((flg & 0x20) != 0)
                throw new PdfException("FlateDecode: the zlib header names a preset dictionary, which this stream cannot have.");

            int limit = start + count;
            byte[] output;
            int deflateEnd;
            try
            {
                output = InflateMeasured(data, start + 2, limit, maxOutputBytes, out deflateEnd);
            }
            catch (PdfException) { throw; }
            catch (Exception ex) { throw new PdfException("FlateDecode: the deflate data after the zlib header is not valid.", ex); }

            if (deflateEnd < 0)
                throw new PdfException("FlateDecode: the final deflate block never arrived; the zlib stream is truncated.");
            if (deflateEnd + 4 > limit)
                throw new PdfException("FlateDecode: the zlib Adler-32 trailer does not fit after the deflate data; the stream is truncated.");

            uint adler = FlateEncodeFilter.ComputeAdler32(output, output.Length);
            uint stored = (uint)(data[deflateEnd] << 24 | data[deflateEnd + 1] << 16 |
                                 data[deflateEnd + 2] << 8 | data[deflateEnd + 3]);
            if (stored != adler)
                throw new PdfException(
                    $"FlateDecode: the zlib Adler-32 trailer ({stored:X8}) disagrees with the {output.Length} " +
                    $"byte(s) produced ({adler:X8}); the stream is corrupt.");

            end = deflateEnd + 4;
            return output;
        }

        /// <summary>
        /// How many bytes the measuring pass hands the inflater at a time. The exact pass re-walks
        /// at most this many one at a time, so it bounds the second pass's cost per stream.
        /// </summary>
        private const int MeasureChunkBytes = 4096;

        /// <summary>
        /// How many reads the inflater made of its input, over both passes, in the last measuring
        /// inflate on this thread: the evidence that measuring where the deflate data ends costs a
        /// few thousand reads a megabyte, never one a byte of the stream.
        /// </summary>
        internal static long LastMeasureReads => _measureReads;

        [ThreadStatic] private static long _measureReads;

        /// <summary>
        /// Inflates <paramref name="data"/> from <paramref name="offset"/> to <paramref name="limit"/>
        /// and finds the index just past the final deflate block, or -1 when the stream never
        /// finished within the run. See <see cref="DecodeEmbedded"/> for the two passes.
        /// </summary>
        private static byte[] InflateMeasured(byte[] data, int offset, int limit, long maxOutputBytes,
                                              out int deflateEnd)
        {
            _measureReads = 0;
            var coarse = new MeteredInput(data, offset, limit, MeasureChunkBytes);
            byte[] output;
            try { output = DecompressFrom(coarse, limit - offset, maxOutputBytes); }
            finally { _measureReads = coarse.Reads; }

            // The final block ended inside the last chunk taken — or, when every byte was taken, it
            // may never have ended. Hand everything before that chunk at once, then a byte at a time:
            // the inflater stops asking at the byte that completes it. When every byte was taken it is
            // also offered one byte past the run, and only the inflater knows whether it wants it — a
            // stream it finished leaves it nothing to ask for. One pass answers both questions; a
            // separate pass for the second would inflate the whole stream a third time.
            bool tookAll = coarse.Handed == limit - offset;
            var fine = new MeteredInput(data, offset, limit, 1)
            {
                FirstChunk = coarse.Handed - coarse.LastChunk,
                OfferOnePastTheEnd = tookAll,
            };
            bool finished;
            try
            {
                Drain(fine);
                finished = !fine.AskedPastTheEnd;
            }
            catch (Exception) when (tookAll)
            {
                // The bytes of the run inflated once already; only the byte past it, which is not
                // the stream's, can make them fail now. Whatever the inflater made of it, it asked.
                finished = false;
            }
            finally
            {
                _measureReads = coarse.Reads + fine.Reads;
            }

            deflateEnd = finished && fine.Handed <= coarse.Handed ? offset + fine.Handed : -1;
            return output;
        }

        /// <summary>Inflates to the end, discarding the output; the first pass already kept it and enforced the ceiling.</summary>
        private static void Drain(Stream input)
        {
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var scratch = new byte[MaxSegmentBytes];
            while (deflate.Read(scratch, 0, scratch.Length) > 0) { }
        }

        /// <summary>
        /// Feeds the inflater a bounded run of bytes, so many per read, and counts what it took.
        /// <see cref="DeflateStream"/> reads from its source only when the inflater has used what it
        /// was given, and not at all once the final block is in — so the count is where the deflate
        /// data ends, to the precision of one read.
        /// </summary>
        private sealed class MeteredInput : Stream
        {
            private readonly byte[] _data;
            private readonly int _start, _limit, _chunk;
            private int _pos;

            public MeteredInput(byte[] data, int start, int limit, int chunk)
            {
                _data = data; _start = start; _limit = limit; _chunk = chunk; _pos = start;
            }

            /// <summary>
            /// Bytes handed over before the per-read size applies: a run already known to be inside
            /// the stream. It goes over in reads as large as the caller asks for, however many that
            /// takes — <see cref="DeflateStream"/> asks for its own buffer's worth, a few KB, so a run
            /// of megabytes is many reads. Spending it on the first read alone would feed the rest of
            /// the run one byte per read.
            /// </summary>
            public int FirstChunk { get; set; }

            /// <summary>When set, a read at the end of the run hands one byte that is not in it, once. An inflater that asks for it had not finished.</summary>
            public bool OfferOnePastTheEnd { get; set; }

            /// <summary>Whether the inflater asked for a byte past the run.</summary>
            public bool AskedPastTheEnd { get; private set; }

            /// <summary>Bytes of the run handed over so far.</summary>
            public int Handed => _pos - _start;

            /// <summary>Size of the most recent read that handed bytes over.</summary>
            public int LastChunk { get; private set; }

            /// <summary>How many reads were made of this input.</summary>
            public long Reads { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Reads++;
                int size = FirstChunk > 0 ? FirstChunk : _chunk;

                int n = Math.Min(Math.Min(size, count), _limit - _pos);
                if (n <= 0)
                {
                    if (!OfferOnePastTheEnd || AskedPastTheEnd || count == 0) return 0;
                    AskedPastTheEnd = true;
                    buffer[offset] = 0;
                    return 1;
                }

                if (FirstChunk > 0) FirstChunk -= n;
                Buffer.BlockCopy(_data, _pos, buffer, offset, n);
                _pos += n;
                LastChunk = n;
                return n;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        /// <summary>
        /// Largest inflate segment. Kept below the 85&#160;000-byte large-object threshold, so every
        /// allocation this loop makes except the one result array is a cheap gen-0 one.
        /// </summary>
        /// <remarks>
        /// <para>The previous shape inflated through an 80&#160;KB scratch buffer into a
        /// <b>default-capacity</b> <c>MemoryStream</c>, whose capacity starts at zero and doubles,
        /// then copied the lot once more in <c>ToArray</c>. Measured on the most-executed decode in
        /// the library, that is 3.1–4.0× the output size per call for streams of tens of KB and up —
        /// and <b>184×</b> for a 452-byte one, where the scratch buffer alone was the whole cost.</para>
        ///
        /// <para><b>Segments rather than one presized buffer, and the measurement is why.</b> The
        /// obvious fix — one buffer sized at a multiple of the compressed length, doubled when the
        /// guess is low — was written first and measured at 3.49× on a 1 MB content stream against
        /// the original's 3.50×, with <i>more</i> gen-2 collections, because PDF content routinely
        /// deflates 20:1 and every doubling past 85&#160;000 bytes is another large-object
        /// allocation. Segments cost ~2× regardless of the ratio, and exactly one of the
        /// allocations is large.</para>
        /// </remarks>
        private const int MaxSegmentBytes = 64 * 1024;

        /// <summary>
        /// Floor for the first segment, matching <c>MemoryStream</c>'s own minimum capacity. Below
        /// this the sizing question does not exist.
        /// </summary>
        private const int MinSegmentBytes = 256;

        /// <summary>
        /// First guess at the decoded size, as a multiple of the compressed length: zlib on PDF
        /// content typically achieves 2–4×, so four times the input is a reasonable first segment
        /// for the small streams that dominate a document by count.
        /// </summary>
        /// <remarks>
        /// <para><b>It is a hint, never a cap.</b> Segments keep being added for as long as the
        /// inflater produces bytes, so a stream that decodes to a thousand times its compressed
        /// length decodes completely — only <see cref="StreamFilter.MaxOutputBytes"/> refuses, and
        /// it refuses with the message it always did. A sizing hint that could truncate would be
        /// the one failure this codebase refuses everywhere: a prefix returned as if it were the
        /// whole stream.</para>
        ///
        /// <para><b>Why the compressed length and not <c>/DL</c>.</b> The decoded length a producer
        /// may declare lives on the <i>stream</i> dictionary; a filter is handed
        /// <c>/DecodeParms</c>, so <c>/DL</c> is not reachable from here. The compressed length is,
        /// and it has the property a hostile <c>/DL</c> lacks: it is bounded by bytes the caller is
        /// already holding, so no declaration in the file can make this allocate more than the
        /// input paid for. It is clamped to one segment in any case.</para>
        /// </remarks>
        private const int AssumedCompressionRatio = 4;

        /// <summary>
        /// <c>Array.MaxLength</c>, which does not exist on the netstandard2.0 target this assembly
        /// also builds for.
        /// </summary>
        private const int MaxArrayLength = 0x7FFFFFC7;

        private static byte[] Decompress(byte[] data, long maxOutputBytes)
        {
            if (data.Length == 0) return data;
            if (data.Length == 1)
            {
                // No deflate stream is one byte (an empty raw one is two), so the byte is not
                // the output: it was returned as such, undecoded.
                _lastTruncationReason = "FlateDecode: a 1-byte stream holds no deflate data; nothing was produced.";
                return Array.Empty<byte>();
            }
            int offset = (data[0] & 0x0F) == 0x08 ? 2 : 0;

            try
            {
                byte[] output = DecompressFrom(
                    new MemoryStream(data, offset, data.Length - offset), data.Length - offset, maxOutputBytes);
                if (offset == 2) CheckTrailer(data, output);
                return output;
            }
            catch (PdfException) { throw; }
            catch
            {
                try { return DecompressFrom(new MemoryStream(data), data.Length, maxOutputBytes); }
                catch (PdfException) { throw; }
                catch (Exception ex) { throw new PdfException("Failed to decompress Flate stream.", ex); }
            }
        }

        /// <summary>
        /// Writes <see cref="LastTruncationReason"/> when the zlib trailer — the big-endian Adler-32
        /// of the whole uncompressed stream (RFC 1950 §2.2) — is not found within the last
        /// <see cref="TrailerSearchBytes"/> bytes of <paramref name="data"/>, which is what a stream
        /// cut short, or corrupt in a way the inflater did not refuse, looks like from outside.
        /// </summary>
        private static void CheckTrailer(byte[] data, byte[] output)
        {
            uint adler = FlateEncodeFilter.ComputeAdler32(output, output.Length);
            byte b0 = (byte)(adler >> 24), b1 = (byte)(adler >> 16), b2 = (byte)(adler >> 8), b3 = (byte)adler;
            // The trailer cannot overlap the two header bytes.
            int first = Math.Max(2, data.Length - TrailerSearchBytes);
            for (int i = data.Length - 4; i >= first; i--)
                if (data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3) return;

            _lastTruncationReason =
                $"FlateDecode: the zlib Adler-32 trailer for the {output.Length} byte(s) produced was not found " +
                $"in the last {Math.Min(TrailerSearchBytes, data.Length - 2)} of the {data.Length} input byte(s); " +
                "the stream is truncated or corrupt, and what was produced may be a prefix.";
        }

        /// <summary>
        /// Inflates into a chain of gen-0-sized segments and assembles them into one exactly-sized
        /// array. Byte-for-byte the same output as the <c>MemoryStream</c> loop it replaces, and the
        /// same refusal at the same ceiling; only the allocation profile differs.
        /// </summary>
        private static byte[] DecompressFrom(Stream input, int compressedLength, long maxOutputBytes)
        {
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);

            // Only the last segment is ever partly filled: each is filled to capacity before the
            // next is allocated, so `total` is the sum of the full ones plus `used`.
            List<byte[]> full = null;
            byte[] current = new byte[FirstSegmentBytes(compressedLength, maxOutputBytes)];
            int used = 0;
            long total = 0;

            while (true)
            {
                if (used == current.Length)
                {
                    (full ??= new List<byte[]>()).Add(current);
                    // Geometric while the segments are small — a stream whose first guess was low
                    // reaches a useful read size in a few steps — then flat, because doubling past
                    // MaxSegmentBytes is exactly the large-object traffic being removed.
                    current = new byte[Math.Min(current.Length * 2, MaxSegmentBytes)];
                    used = 0;
                }

                int read = deflate.Read(current, used, current.Length - used);
                if (read <= 0) break;
                used += read;
                total += read;

                // Checked as soon as the bytes exist, so the ceiling bounds what is actually
                // allocated rather than being noticed once it has been: at most one segment
                // beyond it is ever allocated, against the doubling MemoryStream's up to 2x.
                if (total > maxOutputBytes)
                    throw new PdfException(
                        $"Flate stream exceeds the {maxOutputBytes}-byte decode limit for this call.");

                // A .NET array cannot hold the result past this point. Unreachable through the
                // ceiling, whose default is 512 MB; reachable only by a caller that raised
                // MaxOutputBytes above 2 GiB.
                if (total > MaxArrayLength)
                    throw new PdfException(
                        $"Flate stream exceeds the {MaxArrayLength}-byte maximum length of a .NET array.");
            }

            if (full == null)
            {
                // The common case by a wide margin: one segment held the whole stream.
                if (used == current.Length) return current;
                var only = new byte[used];
                Buffer.BlockCopy(current, 0, only, 0, used);
                return only;
            }

            var result = new byte[(int)total];
            int position = 0;
            for (int i = 0; i < full.Count; i++)
            {
                Buffer.BlockCopy(full[i], 0, result, position, full[i].Length);
                position += full[i].Length;
            }
            if (used > 0) Buffer.BlockCopy(current, 0, result, position, used);
            return result;
        }

        /// <summary>
        /// Sizes the first segment from the compressed length, clamped to one segment and to the
        /// caller's own ceiling — allocating past a limit that is about to refuse the stream would
        /// defeat the point of having one.
        /// </summary>
        private static int FirstSegmentBytes(int compressedLength, long maxOutputBytes)
        {
            long hint = (long)compressedLength * AssumedCompressionRatio;
            if (hint < MinSegmentBytes) hint = MinSegmentBytes;
            if (hint > MaxSegmentBytes) hint = MaxSegmentBytes;

            // One byte past the ceiling is all that is needed to detect it being crossed.
            if (maxOutputBytes < MaxSegmentBytes && hint > maxOutputBytes) hint = maxOutputBytes + 1;
            if (hint < 1) hint = 1;
            return (int)hint;
        }
    }
}
