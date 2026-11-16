// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Decodes a PDF RunLengthDecode (PackBits) stream per ISO 32000 §7.4.5.
    /// Each run is led by a length byte L:
    ///   - L in [0,127]  → copy the next (L+1) literal bytes verbatim
    ///   - L in [129,255] → repeat the next single byte (257-L) times
    ///   - L == 128      → end-of-data marker
    /// </summary>
    public sealed class RunLengthDecodeFilter : StreamFilter
    {
        // §7.4.5's control bytes. Named because a bare 128 in a loop reads as an arbitrary
        // threshold rather than as the spec's EOD.
        private const int EndOfData  = 128;
        private const int MaxLiteral = 127;

        // Defence-in-depth against decompression-bomb PDFs.  A worst-case
        // run-length stream expands ~128× per byte of input (a 2-byte "repeat
        // 128 times" run); capping the decompressed output at 512 MiB stops
        // adversarial streams from exhausting memory while staying above the
        // largest page-image size any real PDF would use.  A caller with a
        // smaller ceiling of its own lowers it through StreamFilter.MaxOutputBytes.
        private long MaxDecompressedBytes => MaxOutputBytes;

        // The two checks in Run are `>`, not `>=`. They were the only `>=` among the three
        // expanding filters — FlateDecodeFilter and LzwDecodeFilter both use `>` — which made the
        // ceiling EXCLUSIVE here and inclusive there. MaxOutputBytes exists so a caller can thread
        // its own ceiling down, and the accessibility tagger and the PDF/X validator both size
        // theirs from /DL: a stream whose /DL is exactly n and which decodes to exactly n bytes
        // was refused, and the page reported as unscannable, for being precisely the declared size.

        public override byte[] Decode(byte[] data, PdfDictionary parameters)
            => Run(data, 0, data.Length, MaxDecompressedBytes, out _, measureOnly: false);

        /// <summary>
        /// Decodes a RunLength stream that lies inside a longer byte run — a PostScript program's
        /// <c>currentfile /RunLengthDecode filter</c> source, an inline image's payload — and says
        /// where it ended, so the bytes after it stay the embedder's.
        /// </summary>
        /// <param name="data">The bytes the stream lies in.</param>
        /// <param name="start">Index of the stream's first byte.</param>
        /// <param name="count">How many bytes from <paramref name="start"/> the stream may occupy.</param>
        /// <param name="maxOutputBytes">
        /// Ceiling on the decoded size; crossing it throws, as <see cref="Decode"/> does through
        /// <see cref="StreamFilter.MaxOutputBytes"/>.
        /// </param>
        /// <param name="end">
        /// Index just past the EOD byte, or -1 when the bytes ran out before it: the decoded bytes
        /// are then whatever the runs held, and nothing says how much of the input was the stream's.
        /// </param>
        public static byte[] DecodeEmbedded(byte[] data, int start, int count, long maxOutputBytes, out int end)
        {
            CheckEmbeddedRange(data, start, count);
            return Run(data, start, count, maxOutputBytes, out end, measureOnly: false);
        }

        /// <summary>
        /// Where a RunLength stream beginning at <paramref name="start"/> ends: the index just past
        /// its EOD byte, or -1 when the bytes ran out first. Walks the runs without copying them —
        /// each length byte says how far to step — through the same reading of §7.4.5 as the decode.
        /// </summary>
        public static int FindEnd(byte[] data, int start, int count)
        {
            CheckEmbeddedRange(data, start, count);
            Run(data, start, count, long.MaxValue, out int end, measureOnly: true);
            return end;
        }

        /// <summary>
        /// The one walk over the runs: decoding into a buffer, or — <paramref name="measureOnly"/> —
        /// stepping over them to find the end. Returns null when measuring.
        /// </summary>
        private static byte[] Run(byte[] data, int start, int count, long maxOutputBytes, out int end,
                                  bool measureOnly)
        {
            end = -1;
            int limit = start + count;

            using (var output = measureOnly ? null : new MemoryStream())
            {
                int i = start;
                while (i < limit)
                {
                    int length = data[i++];

                    if (length == EndOfData)
                    {
                        end = i;
                        break;
                    }

                    if (length <= MaxLiteral)
                    {
                        // Copy (length + 1) literal bytes
                        int run = length + 1;
                        if (i + run > limit) run = limit - i; // clamp on truncated stream
                        if (run <= 0) break;
                        if (output != null)
                        {
                            if (output.Length + run > maxOutputBytes)
                                throw new System.IO.InvalidDataException(
                                    $"RunLengthDecode: output exceeded {maxOutputBytes} bytes");
                            output.Write(data, i, run);
                        }
                        i += run;
                    }
                    else
                    {
                        // Repeat next byte (257 - length) times
                        int run = 257 - length;
                        if (i >= limit)
                            break;
                        byte b = data[i++];
                        if (output != null)
                        {
                            if (output.Length + run > maxOutputBytes)
                                throw new System.IO.InvalidDataException(
                                    $"RunLengthDecode: output exceeded {maxOutputBytes} bytes");
                            for (int j = 0; j < run; j++)
                                output.WriteByte(b);
                        }
                    }
                }

                return output?.ToArray();
            }
        }
    }
}
