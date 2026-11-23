// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Parsed content-stream operations, keyed by the decoded bytes they came from.
    /// </summary>
    /// <remarks>
    /// <para>A Form XObject drawn N times, and a Type 3 glyph shown N times, re-tokenise their
    /// content stream every time without this: the decoded <i>bytes</i> are memoised on the
    /// <c>PdfStream</c>, the parsed operations were not. Measured on a page drawing one form 200
    /// times, adding it took the render from <b>38.6 ms / 6.47 MB to 23.1 ms / 1.78 MB</b>.</para>
    ///
    /// <para><b>Lifetime is the caller's choice, and it is a real trade.</b> A
    /// <see cref="PdfRenderContext"/> creates one per render by default, which is the safe
    /// default: entries die with the render. A <see cref="PdfRenderSession"/> creates one per
    /// session so that tiles and repeated page renders share it — worth it there, because each
    /// tile would otherwise re-tokenise the whole page, but it does mean the parsed graph is
    /// retained for the session's life. Parsed operations run tens of times the size of the bytes
    /// they came from, which is why <see cref="MaxBytes"/> is not optional.</para>
    ///
    /// <para><b>Reference identity on the array, not value equality.</b> The key is the instance
    /// <c>PdfStream.GetDecodedData()</c> memoises, so identity is exactly right and hashing
    /// megabytes of content is avoided. A page whose <c>/Contents</c> is an <i>array</i> of streams
    /// has no such instance — <c>PdfPage.GetContentStreamData()</c> concatenates a fresh array per
    /// call. Under <see cref="PdfRenderSession"/> sixteen tiles are sixteen renders of the same page,
    /// so the session memoises the concatenation (<c>PdfPageRenderer.PageContent</c>) and identity is
    /// stable across tiles. Without a session a page's own content is executed once per render, which
    /// is why the memo is session-scoped rather than unconditional.</para>
    ///
    /// <para><b>Not thread-safe.</b> One session or one render belongs to one thread; sharing an
    /// instance across threads needs external synchronisation.</para>
    ///
    /// <para><b>The lists handed out are shared and must be treated as immutable.</b> Nothing in
    /// either renderer assigns to <see cref="ContentStreamOperation.Operator"/> or an element of
    /// <see cref="ContentStreamOperation.Operands"/>, and nothing may start.</para>
    /// </remarks>
    internal sealed class ContentOperationCache
    {
        /// <summary>
        /// Ceiling on retained parsed operations. Well under the decoded-image cache's 64 MB
        /// because this holds managed object graphs rather than pixel buffers.
        /// </summary>
        internal const long MaxBytes = 16L * 1024 * 1024;

        private Dictionary<byte[], List<ContentStreamOperation>> _map;
        private long _bytes;

        /// <summary>Bytes currently charged to the cache — for tests and diagnostics.</summary>
        internal long ChargedBytes => _bytes;

        /// <summary>Entries currently held — for tests and diagnostics.</summary>
        internal int Count => _map?.Count ?? 0;

        /// <summary>How many times <see cref="GetOrParse"/> parsed rather than answered from the cache — for tests.</summary>
        internal int Parses { get; private set; }

        /// <summary>
        /// Returns the parsed operations for <paramref name="data"/>, parsing on first use.
        /// Never returns null; the result is shared and must not be modified.
        /// </summary>
        public List<ContentStreamOperation> GetOrParse(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<ContentStreamOperation>();

            if (_map != null && _map.TryGetValue(data, out var hit)) return hit;

            var ops = ContentStreamParser.Parse(data);
            Parses++;

            // Charge the graph, not the source bytes: the parsed form is what is retained, and it
            // runs tens of times the size of the stream it came from.
            long bytes = 0;
            for (int i = 0; i < ops.Count; i++)
                bytes += 48 + 32L * (ops[i].Operands?.Length ?? 0);

            // Past the ceiling nothing new is added and what is held stays — the entries already
            // present are the ones proven to repeat. No eviction policy, because a wrong one is
            // worse than none and the ceiling exists to bound memory, not to maximise hit rate.
            if (_bytes + bytes <= MaxBytes)
            {
                // Reference identity for the array keys, so equal content never merges two streams.
                _map ??= new Dictionary<byte[], List<ContentStreamOperation>>(Core.Objects.PdfReferenceEquality.Instance);
                _map[data] = ops;
                _bytes += bytes;
            }

            return ops;
        }
    }
}
