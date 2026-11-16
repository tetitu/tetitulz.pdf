// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// The single implementation of "give me this whole stream as a byte array" — and of "fill this
    /// buffer from a stream", <see cref="Fill"/>.
    ///
    /// <para><b>Why this exists.</b> Ten private copies had accumulated across five assemblies, in
    /// three mutually incompatible contracts. Five were textually identical and read from the
    /// stream's <i>current position</i> without rewinding; two rewound first; one rewound and then
    /// restored the caller's position. A helper that silently reads from wherever the position
    /// happens to be is the shape of a defect this repository has already had once — an earlier
    /// seek-normalisation copy that did not rewind produced empty results for years.</para>
    ///
    /// <para><b>The contract, chosen deliberately:</b> a seekable stream is <b>rewound</b> and read
    /// in full; a non-seekable one is read from where it is, because there is no alternative. The
    /// position is <b>not</b> restored. This is the safer of the three behaviours — the copies that
    /// did not rewind differ from it only when handed a seekable non-<see cref="MemoryStream"/>
    /// positioned past zero, which is exactly the case they got wrong.</para>
    /// </summary>
    public static class StreamBytes
    {
        /// <summary>
        /// Reads <paramref name="stream"/> to its end and returns the bytes.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
        public static byte[] ReadAll(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            // A MemoryStream's ToArray() returns the whole buffer regardless of position, so this
            // is the same answer as rewind-then-copy without the copy. It is an optimisation, not
            // a different contract.
            if (stream is MemoryStream direct) return direct.ToArray();

            if (stream.CanSeek) stream.Position = 0;

            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        /// <summary>
        /// Reads from <paramref name="stream"/>'s current position until <paramref name="count"/>
        /// bytes have arrived or the stream ends, and returns how many arrived.
        /// </summary>
        /// <remarks>
        /// <para><b>A single <see cref="Stream.Read(byte[], int, int)"/> may return fewer bytes than
        /// asked for without being at the end</b> — a network stream, a decompressor or a buffered
        /// wrapper does so routinely — so a caller that takes one call's count as "the buffer" reads
        /// short only on some hosts. <c>Stream.ReadExactly</c> answers this on .NET 7+, and Core also
        /// targets netstandard2.0.</para>
        ///
        /// <para><b>One implementation</b>, for the parser and the tokenizer, the signer, the
        /// verifier, the <c>/Contents</c> locator, the incremental writer's cross-reference probe, the
        /// DocMDP analysis, the PDF MAC seal, the font probe, the WOFF 2.0 decoder, the optimizer and
        /// the CMap plugin. What a short count means stays the caller's: the tokenizer returns what it
        /// got, the signer throws, the font probe answers <c>false</c>.</para>
        ///
        /// <para>The position is left after the last byte read.</para>
        /// </remarks>
        /// <param name="stream">The stream to read, from its current position.</param>
        /// <param name="buffer">Receives the bytes.</param>
        /// <param name="offset">Where in <paramref name="buffer"/> the first byte goes.</param>
        /// <param name="count">How many bytes to read at most.</param>
        /// <returns>The number of bytes read: <paramref name="count"/>, unless the stream ended first.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
        public static int Fill(Stream stream, byte[] buffer, int offset, int count)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }
    }
}
