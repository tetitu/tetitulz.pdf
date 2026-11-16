// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// An <see cref="IByteRangeSource"/> over a stream that holds the whole file — the ordinary
    /// case, so that it reaches the same code a progressive source does.
    /// </summary>
    /// <remarks>
    /// <para><b>It never answers "not yet".</b> Every byte of a complete local file is here, so
    /// <see cref="TryRead"/> answers <c>true</c> for every range and reads short only at the end.
    /// That is the point of having it: the reader above has one path, and the difference between a
    /// finished file and an arriving one lives entirely in the source.</para>
    ///
    /// <para><b>It requires a seekable stream and says so rather than buffering.</b> Buffering the
    /// whole thing is what a caller does when it cannot seek — <c>Core.IO.SeekableStream.Ensure</c>
    /// is that helper, and it also answers who disposes the copy. Doing it silently here would give
    /// this type a hidden memory cost exactly where the caller was trying to avoid holding the file.</para>
    ///
    /// <para>The stream is <b>not</b> disposed by this type, and its position is the caller's: every
    /// read seeks absolutely.</para>
    /// </remarks>
    public sealed class StreamByteRangeSource : IByteRangeSource
    {
        private readonly Stream _stream;

        /// <summary>Wraps a seekable, readable stream holding the complete file.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The stream cannot seek or cannot read.</exception>
        public StreamByteRangeSource(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("The stream is not readable.", nameof(stream));
            if (!stream.CanSeek)
                throw new ArgumentException(
                    "A byte-range source needs a seekable stream; buffer it with Core.IO.SeekableStream.Ensure first.",
                    nameof(stream));

            _stream = stream;
        }

        /// <inheritdoc/>
        public long? Length => _stream.Length;

        /// <inheritdoc/>
        public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || bufferOffset < 0 || bufferOffset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            read = 0;
            if (offset >= _stream.Length) return true;   // past the end is not "not yet"

            _stream.Position = offset;
            read = StreamBytes.Fill(_stream, buffer, bufferOffset, count);
            return true;
        }
    }
}
