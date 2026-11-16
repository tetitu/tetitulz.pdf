// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// Gives a caller a seekable view of a stream — and, unlike the four copies it replaces, says
    /// whether the view is a buffer it must dispose or the caller's own stream it must not.
    /// </summary>
    /// <remarks>
    /// <para><b>Four implementations existed</b>, in three assemblies:
    /// <c>Base.PdfHelpers.EnsureSeekable</c> (always rewinds),
    /// <c>Signing.SigningHelpers.EnsureSeekable</c> (rewinds only when asked, and defaults to
    /// <i>not</i>), <c>Encryption.PdfPublicKeyEncryptor.EnsureSeekable</c> (never rewinds) and
    /// <c>Base.Operations.Metadata.PdfMetadataWriter.EnsureSeekable</c> (always copies unless the
    /// input already <i>is</i> a <see cref="MemoryStream"/>). Roughly a dozen further private
    /// one-line forwarders point at the first two. This lives in Core rather than in Base because
    /// <c>Signing</c> and <c>Encryption</c> may not reference Base — the same reason
    /// <c>IncrementalUpdateWriter</c> and <c>SfntBuilder</c> are here.</para>
    ///
    /// <para><b>The rewind stays a parameter</b> rather than being unified. It is the one place the
    /// copies differed on purpose: verification must read from byte 0, while the signing and
    /// timestamping paths deliberately read from the caller's current position.</para>
    ///
    /// <para><b>The ownership question is the point.</b> A helper that returns
    /// <c>Stream</c> alone leaves the caller unable to tell a fresh buffer from its own argument,
    /// so the honest options are to leak the buffer or to dispose the caller's stream. Every call
    /// site in this repository took the first — except three, which hand-wrote
    /// <c>ReferenceEquals(seekable, input)</c> to work it out. <see cref="Ensure(Stream, bool, out bool)"/>
    /// answers it directly.</para>
    ///
    /// <para><b>Before reaching for this, check whether the consumer already handles it.</b>
    /// <c>PdfDocument.Open(Stream)</c> buffers a non-seekable stream into a
    /// <see cref="MemoryStream"/> <i>and owns the copy</i>, and <c>PdfParser</c> seeks absolutely
    /// from <c>startxref</c> so it never reads from the incoming position. Wrapping its argument in
    /// a seekable view therefore bought nothing and cost a buffer nobody disposed.</para>
    /// </remarks>
    public static class SeekableStream
    {
        /// <summary>
        /// A seekable view of <paramref name="stream"/>, with <paramref name="owned"/> telling the
        /// caller whether it must dispose the result.
        /// </summary>
        /// <param name="stream">The stream to view. Never disposed by this method.</param>
        /// <param name="rewind">
        /// Rewind an already-seekable stream to 0. A stream that had to be copied always starts at
        /// 0, because the copy holds only what could still be read.
        /// </param>
        /// <param name="owned">
        /// <c>true</c> when the result is a fresh buffer the caller must dispose; <c>false</c> when
        /// it is <paramref name="stream"/> itself, which belongs to whoever passed it in.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static Stream Ensure(Stream stream, bool rewind, out bool owned)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (stream.CanSeek)
            {
                if (rewind) stream.Position = 0;
                owned = false;
                return stream;
            }

            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            owned = true;
            return buffer;
        }

        /// <summary>
        /// A seekable view, with the ownership answer discarded.
        /// </summary>
        /// <remarks>
        /// Use it only where the result is consumed and dropped
        /// within the call — <see cref="Ensure(Stream, bool, out bool)"/> is what lets a caller
        /// release the buffer it caused to be allocated.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static Stream Ensure(Stream stream, bool rewind = true) => Ensure(stream, rewind, out _);

        /// <summary>
        /// A rewound <see cref="MemoryStream"/> holding <paramref name="stream"/>'s bytes, reusing
        /// the stream itself when it already is a seekable <see cref="MemoryStream"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Distinct from <see cref="Ensure(Stream, bool, out bool)"/> on purpose.</b>
        /// <c>Ensure</c> hands back any seekable stream as-is; this one guarantees a
        /// <see cref="MemoryStream"/>, which is what a caller needs when it must hand the same
        /// bytes to a parser and then read them back, or write them out again. Several call sites
        /// wrote that contract out by hand rather than having somewhere to put it.</para>
        ///
        /// <para><b>The rewind before the copy is the point.</b> <see cref="Stream.CopyTo(Stream)"/>
        /// starts at the <i>current</i> position, so a seekable <c>FileStream</c> that a host had
        /// already read from — sniffing a header, say — produced a copy missing exactly the bytes
        /// already consumed. That is a truncated and therefore corrupt document. Unlike a missing rewind on the stream itself, no
        /// downstream absolute seek can recover it: the bytes are not in the buffer.</para>
        /// </remarks>
        /// <param name="stream">The stream to buffer. Never disposed by this method.</param>
        /// <param name="owned">
        /// <c>true</c> when the result is a fresh buffer the caller may dispose; <c>false</c> when
        /// it is <paramref name="stream"/> itself.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static MemoryStream Buffered(Stream stream, out bool owned)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            if (stream is MemoryStream seekable && seekable.CanSeek)
            {
                seekable.Position = 0;
                owned = false;
                return seekable;
            }

            if (stream.CanSeek) stream.Position = 0;

            var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            owned = true;
            return buffer;
        }

        /// <summary>A rewound <see cref="MemoryStream"/>, with the ownership answer discarded.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static MemoryStream Buffered(Stream stream) => Buffered(stream, out _);

        /// <summary>
        /// Rewinds <paramref name="stream"/> when it can seek, and returns it either way — no copy,
        /// nothing to dispose.
        /// </summary>
        /// <remarks>
        /// For a consumer that already handles a non-seekable stream itself, which
        /// <c>PdfDocument.Open</c> does. Distinguishing this from <see cref="Ensure(Stream, bool)"/>
        /// is what turns "leak a buffer" into "allocate nothing".
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static Stream Rewound(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (stream.CanSeek) stream.Position = 0;
            return stream;
        }
    }
}
