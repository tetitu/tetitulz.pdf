// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// The decoded length of each stream a listing measures, or why it has none — measured once a
    /// stream, under the listing's ceiling.
    /// </summary>
    /// <remarks>
    /// The 3D and rich-media listers measured each annotation's stream as they came to it, and a
    /// decode refused at the ceiling is not cached by the stream: one asset past 64 MiB, named by a
    /// thousand annotation entries, was inflated to the ceiling a thousand times. A stream is now
    /// measured the first time a listing meets it and answered from that after.
    /// </remarks>
    internal sealed class DecodedLengths
    {
        private readonly long _ceiling;
        private readonly Dictionary<PdfStream, (int Length, string Reason)> _known =
            new Dictionary<PdfStream, (int, string)>(PdfReferenceEquality.Instance);

        /// <summary>Streams decoded on the calling thread: the evidence that each is decoded once.</summary>
        [System.ThreadStatic] internal static long Decodes;

        internal DecodedLengths(long ceiling) => _ceiling = ceiling;

        /// <summary>The stream's decoded length, or 0 with <paramref name="reason"/> saying why it has none.</summary>
        internal int Of(PdfStream stream, string unreadable, out string reason)
        {
            if (!_known.TryGetValue(stream, out var known))
            {
                Decodes++;
                known = stream.TryGetDecodedData(_ceiling, out var data, out string why)
                    ? (data?.Length ?? 0, null)
                    : (0, why ?? unreadable);
                _known[stream] = known;
            }
            reason = known.Reason;
            return known.Length;
        }
    }
}
