// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Per-extraction memo for the two things a Form XObject invocation would otherwise redo on
    /// <b>every</b> <c>Do</c>: the parsed operations of its content stream, and the
    /// <see cref="PdfFont"/> map built from the resource dictionary in scope.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it exists.</b> The decoded <i>bytes</i> of a form are memoised on
    /// <see cref="PdfStream.GetDecodedData"/>; the parse of those bytes and the font load were
    /// not, so a page drawing one form 200 times tokenised the same stream 200 times and called
    /// <see cref="PdfFont.Create"/> — which parses <c>/ToUnicode</c>, <c>/Widths</c> and the
    /// descriptor — 200 times per font. This is the same cost the render path closed with
    /// <c>ContentOperationCache</c>, which lives in <c>Base</c> and therefore cannot be reached
    /// from here: <c>Core</c> may not reference <c>Base</c>. If the two are ever unified, this is
    /// the leaf to move <i>down</i> into and the Base type is the one that should be deleted —
    /// never the other direction.</para>
    ///
    /// <para><b>The two keys are different on purpose, and picking one key for both would be
    /// wrong.</b> A form's parsed operations depend only on its content <i>bytes</i>, so the parse
    /// is keyed on the decoded array. Its <i>fonts</i> do not: a form with no <c>/Resources</c> of
    /// its own inherits the resources of whatever invoked it (§8.10.1), so the same form invoked
    /// from a page and from inside another form legitimately sees two different font sets. The
    /// font map is therefore keyed on the <b>resolved resource dictionary instance</b>, which is
    /// exactly what <c>LoadFonts</c> is a function of.</para>
    ///
    /// <para><b>Reference identity, not value equality</b>, for both keys.
    /// <c>PdfStream.GetDecodedData</c> and the parser's object cache each hand back one stable
    /// instance per document, so identity is precise and megabytes of content are never hashed.
    /// A miss is always safe — it costs a re-parse and yields the same answer.</para>
    ///
    /// <para><b>Lifetime is one call to <c>PdfTextExtractor.ExtractText(PdfPage)</c>.</b> That is
    /// deliberate: it is the scope in which a form is invoked repeatedly, it cannot go stale
    /// (nothing mutates the document underneath it), and it cannot retain anything after the call
    /// returns. The whole-document overload extracts page by page, so a form shared by several
    /// pages is parsed once per page rather than once per <c>Do</c>.</para>
    ///
    /// <para><b>Bounded, because parsed operations run tens of times the size of the bytes they
    /// came from.</b> <see cref="MaxOperationBytes"/> caps the retained operation graph and
    /// <see cref="MaxCachedFonts"/> caps the retained font instances. Past either ceiling nothing
    /// new is admitted and what is already held stays — the entries already present are the ones
    /// proven to repeat. There is no eviction policy, because a wrong one is worse than none and
    /// the ceilings exist to bound memory, not to maximise hit rate. Past the ceiling the caller
    /// re-parses and re-loads, so the ceiling costs speed and never correctness.</para>
    ///
    /// <para><b>The lists and dictionaries handed out are shared and must be treated as
    /// immutable.</b> <see cref="ContentStreamOperation"/> is immutable by construction, and
    /// nothing in the extractor assigns into the font map — it only reads it with
    /// <c>TryGetValue</c>. Nothing may start.</para>
    ///
    /// <para><b>Not thread-safe</b>, and it does not need to be: one instance belongs to one
    /// <c>ExtractText</c> call on one thread and never escapes it.</para>
    /// </remarks>
    internal sealed class TextExtractionCache
    {
        /// <summary>
        /// Ceiling on the retained parsed-operation graph. Matches the render path's
        /// <c>ContentOperationCache.MaxBytes</c>: managed object graphs, not pixel buffers.
        /// </summary>
        internal const long MaxOperationBytes = 16L * 1024 * 1024;

        /// <summary>
        /// Ceiling on retained <see cref="PdfFont"/> instances, counted across all cached resource
        /// dictionaries. One bound rather than two because it covers both shapes that could grow
        /// this map: many resource dictionaries, and one dictionary naming many fonts.
        /// </summary>
        internal const int MaxCachedFonts = 256;

        private Dictionary<byte[], List<ContentStreamOperation>> _operations;
        private long _operationBytes;

        private Dictionary<PdfDictionary, Dictionary<string, PdfFont>> _fonts;
        private int _fontCount;

        /// <summary>Operation-graph bytes currently charged — for tests and diagnostics.</summary>
        internal long ChargedOperationBytes => _operationBytes;

        /// <summary>Content streams whose parse is currently held — for tests and diagnostics.</summary>
        internal int OperationEntryCount => _operations?.Count ?? 0;

        /// <summary>Resource dictionaries whose font map is currently held — for tests.</summary>
        internal int FontEntryCount => _fonts?.Count ?? 0;

        /// <summary>
        /// Returns the parsed operations for <paramref name="data"/>, parsing on first use.
        /// Never returns null; the result is shared and must not be modified.
        /// </summary>
        internal List<ContentStreamOperation> GetOrParse(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<ContentStreamOperation>();

            if (_operations != null && _operations.TryGetValue(data, out var hit)) return hit;

            var ops = ContentStreamParser.Parse(data);

            // Charge the graph, not the source bytes: the parsed form is what is retained. The
            // per-operation estimate is the render path's, so the two ceilings mean the same thing.
            long bytes = 0;
            for (int i = 0; i < ops.Count; i++)
                bytes += 48 + 32L * (ops[i].Operands?.Length ?? 0);

            if (_operationBytes + bytes <= MaxOperationBytes)
            {
                // Reference identity: two equal-CONTENT streams are still two streams, and merging
                // them into one entry would hand a caller another stream's parsed operations.
                _operations ??= new Dictionary<byte[], List<ContentStreamOperation>>(
                    Objects.PdfReferenceEquality.Instance);
                _operations[data] = ops;
                _operationBytes += bytes;
            }

            return ops;
        }

        /// <summary>
        /// Returns a previously built font map for <paramref name="resources"/>, or false when
        /// this dictionary has not been loaded yet (or was refused by the ceiling).
        /// </summary>
        internal bool TryGetFonts(PdfDictionary resources, out Dictionary<string, PdfFont> fonts)
        {
            if (resources != null && _fonts != null && _fonts.TryGetValue(resources, out fonts))
                return true;

            fonts = null;
            return false;
        }

        private Dictionary<PdfDictionary, PdfFont> _fontsByDictionary;

        /// <summary>
        /// The font already made from <paramref name="fontDictionary"/>, by reference identity —
        /// so pages whose resource dictionaries differ but name one font object share its
        /// <see cref="PdfFont"/> (its tables and per-code cache) instead of each building its own.
        /// </summary>
        internal bool TryGetFont(PdfDictionary fontDictionary, out PdfFont font)
        {
            font = null;
            return fontDictionary != null && _fontsByDictionary != null
                   && _fontsByDictionary.TryGetValue(fontDictionary, out font);
        }

        /// <summary>Remembers <paramref name="font"/> for its dictionary, up to <see cref="MaxCachedFonts"/> of them.</summary>
        internal void AddFont(PdfDictionary fontDictionary, PdfFont font)
        {
            if (fontDictionary == null || font == null) return;
            _fontsByDictionary ??= new Dictionary<PdfDictionary, PdfFont>(Objects.PdfReferenceEquality.Instance);
            if (_fontsByDictionary.Count < MaxCachedFonts) _fontsByDictionary[fontDictionary] = font;
        }

        /// <summary>Fonts remembered by dictionary.</summary>
        internal int FontObjectCount => _fontsByDictionary?.Count ?? 0;

        private OverlappingTextIndex<string> _spareIndex;

        /// <summary>
        /// An empty duplicate-overlap index: the one a finished page handed back, or a new one. A
        /// page of a whole-document walk grew its index's lists from empty — on a thousand short
        /// pages, the largest thing the extractor allocated.
        /// </summary>
        internal OverlappingTextIndex<string> RentIndex()
        {
            OverlappingTextIndex<string> index = _spareIndex;
            if (index == null) { index = new OverlappingTextIndex<string>(); IndexesMade++; }
            _spareIndex = null;
            index.Comparisons = OverlapComparisons ??= OverlappingTextIndex<string>.NewComparisonBudget();
            return index;
        }

        /// <summary>
        /// The page's duplicate-text comparisons, which every index rented for it — the page's own
        /// and each form's — spends: one budget a page, so forms cannot multiply it. Renewed by
        /// <see cref="BeginPage"/>.
        /// </summary>
        internal Util.WorkBudget OverlapComparisons { get; private set; }

        /// <summary>A new page: its comparisons start from the whole budget.</summary>
        internal void BeginPage() => OverlapComparisons = null;

        /// <summary>Indexes <see cref="RentIndex"/> had to make — the reuse's evidence.</summary>
        internal int IndexesMade { get; private set; }

        /// <summary>Takes <paramref name="index"/> back for the next page, emptied, unless one is already kept or it grew too large.</summary>
        internal void ReturnIndex(OverlappingTextIndex<string> index)
        {
            if (_spareIndex == null && index != null && index.Reset()) _spareIndex = index;
        }

        /// <summary>
        /// Offers a font map built from <paramref name="resources"/> for reuse by later
        /// invocations that resolve to the <i>same</i> dictionary instance.
        /// </summary>
        internal void AddFonts(PdfDictionary resources, Dictionary<string, PdfFont> fonts)
        {
            if (resources == null || fonts == null) return;
            if (_fontCount + fonts.Count > MaxCachedFonts) return;

            // Reference identity, for the same reason as _operations above: two structurally equal
            // /Resources dictionaries are different dictionaries and must not share a font map.
            _fonts ??= new Dictionary<PdfDictionary, Dictionary<string, PdfFont>>(
                Objects.PdfReferenceEquality.Instance);
            if (_fonts.ContainsKey(resources)) return;

            _fonts[resources] = fonts;
            _fontCount += fonts.Count;
        }

    }
}
