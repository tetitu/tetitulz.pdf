// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// Encodes a PDF <c>RunLengthDecode</c> (PackBits) stream — ISO 32000-1 §7.4.5, the exact
    /// inverse of <see cref="RunLengthDecodeFilter"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>It is not here to make files smaller, and measurement says so.</b> A zlib wrapper's
    /// eleven bytes of overhead look as if they would lose to a one-byte terminator on the short
    /// appearance streams a filled form produces. <b>Measured across
    /// eight real content and appearance streams, RunLength won none of them</b> — not the 33-byte
    /// border, not the 50-byte widget, not the 7 600-byte page. It cannot: on data without runs it
    /// costs one byte per 128 plus the terminator, so it is strictly larger than the input, and
    /// <c>PdfOptimizer</c> already keeps the original bytes whenever a candidate does not beat
    /// them. Wiring it in would add an encode per stream for a win that does not exist.</para>
    ///
    /// <para><b>What it is for</b> is a producer that must emit a <i>named</i> filter — a consumer
    /// of this library targeting a reader or a profile that expects one — and completing the
    /// encode side of a filter this assembly already decodes. That is a public-API reason, not an
    /// internal one, which is why there is no internal caller and the tests exercise it directly.
    /// Its correctness is checked by decoding its output with <b>an independent decoder</b>, not with the decoder next
    /// door, which would have to share a mistake to disagree.</para>
    ///
    /// <para><b>Runs are what the format is for, and a run of two is not worth one.</b> A repeat
    /// record costs two bytes and replaces <i>n</i>; a literal run costs one byte plus <i>n</i>. So
    /// two identical bytes cost the same either way, and breaking a literal run to encode them
    /// costs an extra length byte for the literal that follows. The threshold is therefore three,
    /// not two — the version that used two produced <b>larger</b> output than the input on ordinary
    /// text, which is the failure mode a compressor must not have.</para>
    ///
    /// <para><b>The result is never blindly smaller, and callers must check.</b> §7.4.5's worst
    /// case is incompressible data, which costs one extra byte per 128 — plus the terminator. The
    /// encoder does not refuse those inputs, because "smaller" is the caller's comparison to make
    /// against every other filter it is considering.</para>
    /// </remarks>
    public static class RunLengthEncodeFilter
    {
        /// <summary>The §7.4.5 end-of-data marker.</summary>
        private const int EndOfData = 128;

        /// <summary>Longest literal or repeat run one length byte can describe.</summary>
        private const int MaxRun = 128;

        /// <summary>
        /// Shortest repeat worth its own record. Two identical bytes cost two bytes either way, and
        /// interrupting a literal run to encode them adds a length byte to whatever follows.
        /// </summary>
        private const int MinRepeat = 3;

        /// <summary>
        /// The RunLength-encoded form of <paramref name="data"/>, terminated by the EOD marker.
        /// </summary>
        /// <param name="data">The bytes to encode. <c>null</c> and empty both yield a bare EOD.</param>
        /// <remarks>
        /// An empty input still produces the one-byte terminator rather than an empty stream: a
        /// zero-length RunLength stream is not the encoding of nothing, it is a truncated stream,
        /// and the two must not look alike to a reader.
        /// </remarks>
        public static byte[] Encode(byte[] data)
        {
            using var output = new MemoryStream();

            int i = 0;
            int n = data?.Length ?? 0;

            while (i < n)
            {
                int repeat = RunLengthAt(data, i, n);

                if (repeat >= MinRepeat)
                {
                    // 257 - L repeats, so L = 257 - count and count is in [2, 128].
                    output.WriteByte((byte)(257 - repeat));
                    output.WriteByte(data[i]);
                    i += repeat;
                    continue;
                }

                // A literal run reaches to the next repeat worth encoding, or to the cap.
                int literal = 1;
                while (i + literal < n &&
                       literal < MaxRun &&
                       RunLengthAt(data, i + literal, n) < MinRepeat)
                    literal++;

                output.WriteByte((byte)(literal - 1));
                output.Write(data, i, literal);
                i += literal;
            }

            output.WriteByte(EndOfData);
            return output.ToArray();
        }

        /// <summary>
        /// How many times the byte at <paramref name="at"/> repeats, capped at <see cref="MaxRun"/>.
        /// </summary>
        private static int RunLengthAt(byte[] data, int at, int n)
        {
            byte b = data[at];
            int count = 1;
            while (at + count < n && count < MaxRun && data[at + count] == b) count++;
            return count;
        }
    }
}
