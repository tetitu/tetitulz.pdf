// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// A file a reader may hold only part of — the seam a progressive, byte-range reader needs and
    /// <see cref="System.IO.Stream"/> cannot provide.
    /// </summary>
    /// <remarks>
    /// <para><b>The whole design is one predicate: which bytes are here yet.</b> A
    /// <see cref="System.IO.Stream"/> has two answers — bytes, or the end — and a download that has
    /// delivered its first kilobyte has to pick one of them for the rest. Picking "the end" makes a
    /// partial file look like a truncated one; picking "block until they arrive" makes every read a
    /// potential stall with no way for the caller to choose otherwise. <see cref="TryRead"/> adds
    /// the third answer, and everything else about progressive access follows from having it.</para>
    ///
    /// <para><b>The contract, which is the part worth reading:</b></para>
    /// <list type="bullet">
    ///   <item><see cref="TryRead"/> returning <c>false</c> means <b>not yet</b> — some byte of the
    ///   requested range has not arrived. It is never "end of file", and a caller that treats it as
    ///   one reports a corrupt document for a download in progress.</item>
    ///   <item><see cref="TryRead"/> returning <c>true</c> with a short count means <b>end of
    ///   file</b>, and nothing else. A source that cannot tell the two apart — because it does not
    ///   know the length yet — must answer <c>false</c> rather than read short, which is why this is
    ///   all-or-nothing for any range inside the file.</item>
    ///   <item><see cref="Length"/> is <c>null</c> while the total is unknown (a chunked transfer
    ///   that has not sent one). It is the producer's statement, not a promise the bytes exist.</item>
    /// </list>
    ///
    /// <para><b>It does not block, and an implementation that does has given the answer away.</b>
    /// A source free to fetch inside <see cref="TryRead"/> can always answer <c>true</c>, at which
    /// point a caller can no longer distinguish "here" from "a round trip away" — which is the one
    /// distinction it implements this interface to get. Fetch outside the call; answer from what has
    /// arrived.</para>
    ///
    /// <para><b>Why an interface with a single implementation in this repository.</b> The one that
    /// matters is the host's: an HTTP <c>Range</c> client, a memory-mapped partial file, a CDN
    /// cache. <see cref="StreamByteRangeSource"/> exists so that the ordinary "I have the whole
    /// file" case reaches the same code, and so the seam has a consumer on the day it lands rather
    /// than becoming a contract nothing dispatches.</para>
    /// </remarks>
    public interface IByteRangeSource
    {
        /// <summary>
        /// The file's total length in bytes, or <c>null</c> while it is not yet known.
        /// </summary>
        /// <remarks>
        /// A known length is what lets a short <see cref="TryRead"/> mean "end of file" rather than
        /// "not yet", so a source that learns its length later starts answering <c>false</c> for
        /// ranges it would otherwise read short.
        /// </remarks>
        long? Length { get; }

        /// <summary>
        /// Reads <paramref name="count"/> bytes from <paramref name="offset"/> if they are here.
        /// </summary>
        /// <param name="offset">Absolute offset in the file, from zero.</param>
        /// <param name="buffer">Receives the bytes.</param>
        /// <param name="bufferOffset">Where in <paramref name="buffer"/> the first byte goes.</param>
        /// <param name="count">How many bytes are wanted.</param>
        /// <param name="read">
        /// How many arrived. Equal to <paramref name="count"/> unless the range ran past the end of
        /// the file, in which case it is the number of bytes that exist.
        /// </param>
        /// <returns>
        /// <c>true</c> when the range was satisfied, wholly or up to the end of the file;
        /// <c>false</c> when some of it has <b>not arrived yet</b>. Never <c>false</c> for a range
        /// that is simply past the end.
        /// </returns>
        bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read);
    }
}
