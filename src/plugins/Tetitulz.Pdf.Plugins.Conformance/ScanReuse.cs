// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// One validation's page scans and their parsed content, shared by its checks.
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> Each check walks every page through <see cref="PdfXValidator.ScanPage"/> and
    /// parses what it collected, so unshared, a page's content would be decoded and tokenized
    /// once per check: PDF/A on a thousand-page text document measured 4.8–6.0× tokenizing every
    /// page (<c>WholeDocumentWalkBenchmarks</c>), with 22 of 25 sampled stacks in
    /// <c>ContentStreamParser.Parse</c>, reached from eight checks.</para>
    /// <para><b>What is shared.</b> A complete scan of a page, keyed by the page dictionary and the
    /// scan's parameters; and the operations of each content stream a scan holds, keyed by the
    /// decoded array's identity. That identity is why the scans are shared first: a scan made again
    /// decodes new arrays. <see cref="PdfXValidator.PageScan"/> is not changed after
    /// <see cref="PdfXValidator.ScanPage"/> returns it, and no reader changes an operation, so a
    /// shared one answers each check as its own did. A page's own content read outside the scans
    /// (PDF/UA's marked-content walk) is kept the same way, through <see cref="ParseAndKeep"/>.</para>
    /// <para><b>The document ceiling behaves as without the cache.</b> A cached scan is handed out only where
    /// making it again would have completed within the caller's running total, and the total is
    /// charged its bytes as the walk would have charged them. Anywhere else, and for a scan that
    /// came back incomplete, the walk runs uncached.</para>
    /// <para><b>Bounded.</b> Scans are kept while their decoded content totals at most
    /// <see cref="MaxCachedContentBytes"/>. Past that, a new scan is not kept and its streams are not
    /// parsed into the cache, so a document too large to hold costs what an uncached validation
    /// costs, not more memory. Outside a scope, nothing is cached at all.</para>
    /// </remarks>
    internal sealed class ScanReuse : IDisposable
    {
        /// <summary>
        /// The decoded content the cache may hold across one validation. Tokenizing allocates about
        /// twelve times the bytes it reads (<c>TokenizeEveryPage</c>: 40 MB over 3.2 MB), so this is
        /// on the order of 200 MB of parsed operations at most.
        /// </summary>
        internal const long MaxCachedContentBytes = 16L * 1024 * 1024;

        [ThreadStatic] private static ScanReuse _current;

        // The page by identity; under it, the scan's parameters by value.
        private readonly Dictionary<object, Dictionary<(int MaxPage, long MaxDocument, bool Content, bool PrintOnly),
            (PdfXValidator.PageScan Scan, long Bytes)>> _scans =
            new Dictionary<object, Dictionary<(int, long, bool, bool), (PdfXValidator.PageScan, long)>>(PdfReferenceEquality.Instance);

        private readonly Dictionary<object, List<ContentStreamOperation>> _operations =
            new Dictionary<object, List<ContentStreamOperation>>(PdfReferenceEquality.Instance);

        private readonly HashSet<object> _cachedContent = new HashSet<object>(PdfReferenceEquality.Instance);

        private long _cachedBytes;

        /// <summary>
        /// The page annotations one validation reads, all its checks together: each check that walks
        /// every page's <c>/Annots</c> asks <see cref="AdmitAnnotations"/> for each page's array.
        /// </summary>
        /// <remarks>
        /// <b>Pages may share one array</b>, so the pairs a whole-document walk reads are pages ×
        /// entries, two counts the file chooses: six thousand pages naming one array of six thousand
        /// references (600 KB) took 13.5 s through the PDF/A validator, 11 through PDF/X and 8 through
        /// PDF/UA, and the cost grows with the square of the file. One read costs about 25 ns there,
        /// so this is a few seconds a validation; a document past it is reported partial, and no
        /// document that does not share its arrays comes near it.
        /// </remarks>
        internal const long DefaultMaxAnnotationVisits = 256_000_000;

        /// <summary>A test's own ceiling for the scopes this thread opens; 0 for the default.</summary>
        [ThreadStatic] internal static long TestMaxAnnotationVisits;

        private readonly long _maxAnnotationVisits =
            TestMaxAnnotationVisits > 0 ? TestMaxAnnotationVisits : DefaultMaxAnnotationVisits;

        private long _annotationVisits;

        /// <summary>Whether a check of this validation stopped reading annotations at the ceiling.</summary>
        internal bool AnnotationsCut { get; private set; }

        /// <summary>
        /// Whether a check may read <paramref name="annots"/>, a page's <c>/Annots</c>: whole, while
        /// this validation's ceiling lasts. Outside a scope every array is read.
        /// </summary>
        internal static bool AdmitAnnotations(PdfArray annots)
        {
            if (annots == null) return false;
            var scope = _current;
            if (scope == null) return true;
            if (scope.AnnotationsCut || scope._annotationVisits + annots.Count > scope._maxAnnotationVisits)
            {
                scope.AnnotationsCut = true;
                return false;
            }
            scope._annotationVisits += annots.Count;
            return true;
        }

        /// <summary>The notice a validation adds when <see cref="AnnotationsCut"/>, or null.</summary>
        internal static string AnnotationsCutNotice(string checks)
            => _current?.AnnotationsCut == true
                ? $"Annotations were read for at most {_current._maxAnnotationVisits} page entries, all checks together, " +
                  $"so {checks} did not run over every page's annotations: the pages share their /Annots arrays " +
                  "or hold more than any document does."
                : null;

        // A resource dictionary by identity; under it, the walk's parameters by value.
        private readonly Dictionary<object, Dictionary<(int MaxPage, long MaxDocument, bool Content), PdfXValidator.PageScan>> _walks =
            new Dictionary<object, Dictionary<(int, long, bool), PdfXValidator.PageScan>>(PdfReferenceEquality.Instance);

        /// <summary>Resource walks shared by a later page in this scope, for tests: the template's liveness.</summary>
        internal int SharedWalkHits { get; private set; }

        /// <summary>
        /// The walk of <paramref name="resources"/> alone — what a page that names them and shows no
        /// annotation reaches below its own content — made once a scope by <paramref name="walk"/>
        /// and kept while the decoded content it holds fits <see cref="MaxCachedContentBytes"/>.
        /// </summary>
        /// <remarks>
        /// Pages share their <c>/Resources</c> all the time — through the page tree, legitimately — and
        /// each page's scan walked them again: a thousand pages over a forty-thousand-entry
        /// dictionary took PDF/X 25 s, growing with the product. Outside a scope there is no sharing.
        /// </remarks>
        internal PdfXValidator.PageScan SharedWalk(PdfDictionary resources, int maxPageContentBytes,
            long maxDocumentContentBytes, bool collectContent, Func<PdfXValidator.PageScan> walk)
        {
            var key = (maxPageContentBytes, maxDocumentContentBytes, collectContent);
            if (_walks.TryGetValue(resources, out var byParameters) && byParameters.TryGetValue(key, out var known))
            {
                SharedWalkHits++;
                return known;
            }
            var made = walk();
            long fresh = 0;
            foreach (byte[] content in made.ContentStreams)
                if (content != null && !_cachedContent.Contains(content)) fresh += content.Length;
            if (_cachedBytes + fresh > MaxCachedContentBytes) return made;   // used for this page alone
            _cachedBytes += fresh;
            foreach (byte[] content in made.ContentStreams)
                if (content != null) _cachedContent.Add(content);
            if (byParameters == null) _walks[resources] = byParameters = new Dictionary<(int, long, bool), PdfXValidator.PageScan>();
            byParameters[key] = made;
            return made;
        }

        /// <summary>Scans reused in this scope, for tests: the cache's liveness, not its answers.</summary>
        internal int ScanHits { get; private set; }

        /// <summary>Content streams whose parse was reused in this scope.</summary>
        internal int ParseHits { get; private set; }

        /// <summary>
        /// Opens a scope on this thread for one validation, or joins the one already open: a
        /// validator that calls another (the PDF/A validator asks PDF/UA's font use) shares it.
        /// </summary>
        internal static IDisposable Begin()
        {
            // A joined scope hands back a token that closes nothing: only the outermost validation
            // ends the scope, or the inner one's end would drop what the outer one is still using.
            if (_current != null) return Joined.Instance;
            _closedScanHits = -1;
            _current = new ScanReuse();
            return _current;
        }

        /// <summary>The scope open on this thread, or null.</summary>
        internal static ScanReuse Current => _current;

        [ThreadStatic] private static int _closedScanHits;

        /// <summary>
        /// The scans reused by the last scope this thread closed, or -1 while one is open. The evidence
        /// that a validator opens its own scope: with none open around the call, only its own leaves a
        /// count here.
        /// </summary>
        internal static int ClosedScanHits => _closedScanHits;

        /// <summary>The page annotations admitted by the last scope this thread closed: the ceiling's evidence.</summary>
        [ThreadStatic] internal static long ClosedAnnotationVisits;

        /// <summary>
        /// Whether <paramref name="resources"/> is new to <paramref name="seen"/>: a check reading each
        /// page's resource dictionaries reads each dictionary once, however many pages share it — the
        /// pages × entries a shared <c>/Resources</c> costs a per-page loop otherwise. Every check
        /// that asks it dedupes its own findings, so the skip changes no answer; a skip is counted in
        /// the scope (<see cref="ClosedResourceRevisitsSkipped"/>), which is the evidence it happens.
        /// </summary>
        internal static bool FirstVisit(HashSet<PdfDictionary> seen, PdfDictionary resources)
        {
            if (seen.Add(resources)) return true;
            if (_current != null) _current._resourceRevisitsSkipped++;
            return false;
        }

        private long _resourceRevisitsSkipped;

        /// <summary>The resource dictionaries <see cref="FirstVisit"/> skipped in the last scope this thread closed.</summary>
        [ThreadStatic] internal static long ClosedResourceRevisitsSkipped;

        public void Dispose()
        {
            if (!ReferenceEquals(_current, this)) return;
            _closedScanHits = ScanHits;
            ClosedAnnotationVisits = _annotationVisits;
            ClosedResourceRevisitsSkipped = _resourceRevisitsSkipped;
            _current = null;
        }

        private sealed class Joined : IDisposable
        {
            internal static readonly Joined Instance = new Joined();
            public void Dispose() { }
        }

        /// <summary>
        /// A cached complete scan of <paramref name="page"/> under these parameters that making it
        /// again would also have completed, its bytes charged to <paramref name="documentBytes"/>.
        /// </summary>
        internal bool TryGet(PdfDictionary page, int maxPageContentBytes, ref long documentBytes,
            long maxDocumentContentBytes, bool collectContent, bool printElementsOnly,
            out PdfXValidator.PageScan scan)
        {
            scan = null;
            if (page == null) return false;
            if (!_scans.TryGetValue(page, out var byParameters)
                || !byParameters.TryGetValue((maxPageContentBytes, maxDocumentContentBytes, collectContent, printElementsOnly),
                    out var cached))
                return false;
            // The walk refuses at the start once the total has reached the ceiling, and part-way
            // through when a stream would pass it; a cached scan stands for neither.
            if (documentBytes >= maxDocumentContentBytes || documentBytes + cached.Bytes > maxDocumentContentBytes)
                return false;
            documentBytes += cached.Bytes;
            scan = cached.Scan;
            ScanHits++;
            return true;
        }

        /// <summary>Keeps a complete scan that charged <paramref name="bytes"/> to the running total.</summary>
        internal void Keep(PdfDictionary page, int maxPageContentBytes, long maxDocumentContentBytes,
            bool collectContent, bool printElementsOnly, PdfXValidator.PageScan scan, long bytes)
        {
            if (page == null || scan == null || scan.Incomplete != null) return;
            // An array already held — a page's own content kept by ParseAndKeep — is not charged twice.
            long fresh = bytes;
            foreach (byte[] content in scan.ContentStreams)
                if (content != null && _cachedContent.Contains(content)) fresh -= content.Length;
            if (_cachedBytes + fresh > MaxCachedContentBytes) return;
            _cachedBytes += fresh;
            if (!_scans.TryGetValue(page, out var byParameters))
                _scans[page] = byParameters = new Dictionary<(int, long, bool, bool), (PdfXValidator.PageScan, long)>();
            byParameters[(maxPageContentBytes, maxDocumentContentBytes, collectContent, printElementsOnly)] = (scan, bytes);
            foreach (byte[] content in scan.ContentStreams)
                if (content != null) _cachedContent.Add(content);
        }

        /// <summary>
        /// The operations of <paramref name="content"/>: parsed once a scope when the array belongs
        /// to a kept scan, parsed afresh otherwise.
        /// </summary>
        internal static List<ContentStreamOperation> Parse(byte[] content)
        {
            var scope = _current;
            if (scope == null || content == null || !scope._cachedContent.Contains(content))
                return ContentStreamParser.Parse(content);
            if (scope._operations.TryGetValue(content, out var operations))
            {
                scope.ParseHits++;
                return operations;
            }
            operations = ContentStreamParser.Parse(content);
            scope._operations[content] = operations;
            return operations;
        }

        /// <summary>
        /// The operations of a page's own content read outside the scans — PDF/UA's marked-content
        /// walk, which UA02 and UA50 each run: parsed once a scope and kept under the same size
        /// bound, so the other run and a later scan holding the same decoded array (a page with one
        /// content stream, whose decode the stream caches) reuse it.
        /// </summary>
        internal static List<ContentStreamOperation> ParseAndKeep(byte[] content)
        {
            var scope = _current;
            if (scope == null || content == null) return ContentStreamParser.Parse(content);
            if (scope._operations.TryGetValue(content, out var operations))
            {
                scope.ParseHits++;
                return operations;
            }
            operations = ContentStreamParser.Parse(content);
            if (scope._cachedContent.Contains(content) || scope._cachedBytes + content.Length <= MaxCachedContentBytes)
            {
                if (scope._cachedContent.Add(content)) scope._cachedBytes += content.Length;
                scope._operations[content] = operations;
            }
            return operations;
        }
    }
}
