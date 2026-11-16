// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Threading;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// A per-document ceiling on the decoded bytes <see cref="Objects.PdfStream"/> keeps memoized.
    /// A stream caches its decoded data for its own lifetime, which is the document's; a
    /// whole-document pass that decodes each stream once — rendering every page, extracting all text,
    /// a validator — therefore held the <i>sum</i> of every stream's decoded content, not the largest
    /// (DoS finding F3). Past this ceiling a stream decodes without caching, so the retained total is
    /// bounded while decodes under it (a page repainted in tiles, render plus text on one document)
    /// stay cached and cheap.
    /// </summary>
    /// <remarks>
    /// One instance per parsed document, referenced by every stream the parser builds. A stream with
    /// no budget — one a writer built, not the parser — always caches, as it always did. The default
    /// ceiling is generous enough that every real document stays fully cached and byte-for-byte
    /// unchanged; only a pathological one that retains hundreds of megabytes of decoded content starts
    /// decoding uncached. Shared across the threads a batch render splits a document over, so the
    /// charge is atomic; a check-then-charge race can overshoot by one stream's bytes, which is
    /// harmless against a ceiling in the hundreds of megabytes.
    /// </remarks>
    internal sealed class DecodeMemoBudget
    {
        private readonly long _ceiling;
        private long _used;

        public DecodeMemoBudget(long ceiling) => _ceiling = ceiling;

        /// <summary>
        /// Whether <paramref name="bytes"/> of a fresh decode may be retained — <see langword="true"/>
        /// while the document is under the ceiling (charging them), <see langword="false"/> once it is
        /// reached, so the caller returns the decode without caching it.
        /// </summary>
        public bool TryRetain(long bytes)
        {
            if (Interlocked.Read(ref _used) >= _ceiling) return false;
            Interlocked.Add(ref _used, bytes);
            return true;
        }
    }
}
