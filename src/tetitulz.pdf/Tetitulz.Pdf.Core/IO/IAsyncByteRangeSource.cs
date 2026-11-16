// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using System.Threading.Tasks;

namespace Tetitulz.Pdf.Core.IO
{
    /// <summary>
    /// A file whose byte ranges are fetched asynchronously — an HTTP <c>Range</c> client, a remote
    /// store, anything where a range costs a round trip.
    /// </summary>
    /// <remarks>
    /// <para><b>It has no "not yet" answer, and that is the whole difference from
    /// <see cref="IByteRangeSource"/>.</b> The third answer exists because a synchronous call cannot
    /// wait: it must say *the bytes are not here* and hand the decision back. An asynchronous one
    /// can simply take longer, so <i>not yet</i> becomes <i>await</i> and the interface collapses to
    /// "fetch this range".</para>
    ///
    /// <para><b>A short result means end of file</b>, exactly as it does on the synchronous seam —
    /// so a source that does not yet know the file's length must await the bytes rather than return
    /// what it has. Cancellation and failure are the transport's own business and travel as they
    /// normally do, through the token and through exceptions.</para>
    ///
    /// <para>Nothing in this library implements it: the implementation that matters is the host's.
    /// What consumes it is <c>Base.Operations.Documents.PdfProgressivePageReader.ReadPageAsync</c>,
    /// which drives the same protocol the synchronous reader exposes — so the two share the
    /// decoding, the ceilings and the refusals rather than mirroring them.</para>
    /// </remarks>
    public interface IAsyncByteRangeSource
    {
        /// <summary>
        /// The file's total length in bytes, or <c>null</c> while it is not yet known.
        /// </summary>
        long? Length { get; }

        /// <summary>
        /// Fetches <paramref name="length"/> bytes from <paramref name="offset"/>, awaiting them.
        /// </summary>
        /// <param name="offset">Absolute offset in the file, from zero.</param>
        /// <param name="length">How many bytes are wanted.</param>
        /// <param name="cancellationToken">Cancels the fetch.</param>
        /// <returns>
        /// The bytes. Fewer than asked for means the range ran past the end of the file; an empty
        /// array means it began past the end.
        /// </returns>
        Task<byte[]> FetchAsync(long offset, long length, CancellationToken cancellationToken);
    }
}
