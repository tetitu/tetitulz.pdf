// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Writes a whole document out as a <b>single revision containing reachable objects only</b>,
    /// and replaces a page's content stream in a way that leaves no copy of the old bytes.
    ///
    /// <para><b>Why this exists as a shared type.</b> Two operations need exactly this — redaction
    /// and template token replacement — and they need it for the same reason: both destroy content
    /// that must not survive anywhere in the output. An incremental update cannot do that, because
    /// the superseded bytes remain in the previous revision (which is what makes
    /// <c>PdfRevisions</c> able to recover them). Copying the walk into the second caller is the
    /// failure mode this repo keeps finding — one caller gets fixed and the other silently does not
    /// — so it lives here and both call it.</para>
    ///
    /// <para><b>The trade this makes, stated once.</b> A full rewrite renumbers every object, so
    /// every existing signature is invalidated. That is not an implementation shortcoming; it is
    /// the direct consequence of the requirement to destroy data. Preserve the signature <i>or</i>
    /// remove the data — never both.</para>
    /// </summary>
    internal static class PdfFullRewriter
    {
        /// <summary>
        /// Replaces the page's content with <paramref name="replacement"/>.
        ///
        /// <para>The first stream carries everything and the remaining parts of a <c>/Contents</c>
        /// array are <b>emptied</b> — not left alone. The replacement was computed against the
        /// concatenation of all parts, so writing it into the first while leaving the others would
        /// duplicate their content; and leaving the originals in place would keep the very bytes the
        /// caller is trying to destroy.</para>
        ///
        /// <para><b>A content stream shared with another page is cloned, never edited in place.</b>
        /// Two pages may legally reference one <c>/Contents</c> object — a 40-page report whose
        /// pages 2–40 share one boilerplate stream and differ only in <c>/Annots</c> is the ordinary
        /// case. Stripping that object applied one page's edit to every page using it: redacting a
        /// rectangle on page 5 removed the same glyphs from 38 pages the caller never named and
        /// painted page 5's cover box across all of them, in page-5 coordinates. This is the hazard
        /// the engines already handle for shared Form XObjects (<c>FormInvocationCounter</c> +
        /// <c>FormCloneContext</c>), and <c>/Contents</c> had no equivalent check.</para>
        ///
        /// <para><b>The same reduced promise applies as for a shared form:</b> the original object
        /// survives for the pages still using it, so its content stays in the file. Redaction
        /// removes data from the pages the caller named, not from the document.</para>
        /// </summary>
        /// <param name="allocateObjectNumber">
        /// Allocator for a clone's object number, collision-free against the document. Required —
        /// without it a shared stream is <b>refused</b> rather than edited in place, because
        /// silently altering unnamed pages is the worse of the two failures.
        /// </param>
        /// <param name="extras">
        /// Sink for the clone, which the document has never heard of and which
        /// <see cref="TryFullRewrite"/> would otherwise not resolve.
        /// </param>
        /// <returns>
        /// False when the page has no content stream to substitute, or when a shared stream could
        /// not be cloned because no allocator was supplied.
        /// </returns>
        /// <param name="page">The page whose content stream is replaced.</param>
        /// <param name="doc">The document <paramref name="page"/> belongs to.</param>
        /// <param name="replacement">
        /// The new, already-encoded content stream bytes. Replaces the page's whole
        /// <c>/Contents</c>, so a multi-part array is collapsed to one stream.
        /// </param>
        /// <param name="usage">
        /// Which pages use which content streams, built once for an operation that substitutes many
        /// pages and kept current here; null reads it from every page for this call alone.
        /// </param>
        internal static bool TrySubstitutePageContent(
            PdfPage page, PdfDocument doc, byte[] replacement,
            Func<int> allocateObjectNumber = null,
            IDictionary<int, PdfObject> extras = null,
            ContentUsage usage = null)
        {
            var contentsObj = page.Dictionary[PdfName.Contents];

            // Object numbers are carried alongside the streams: only a stream reached by an
            // indirect reference can be shared, and the number is what identifies the sharing.
            //
            // Which is why the reference is NOT resolved before this ladder, and the missing case
            // is handled inside it instead. §7.3.10 lets `/Contents` be an indirect reference to an
            // ARRAY of streams; demanding a PdfStream here would leave such a page with no parts
            // and the callers throwing "no content stream to redact" over a page with perfectly
            // ordinary content.
            //
            // Resolving up front fixes that and breaks something worse: `/Contents 4 0 R` then
            // arrives as a bare PdfStream, matches the `direct` branch, and is recorded with
            // objNum 0 — which reads as "not shared", so a stream two pages share is edited IN
            // PLACE and the other page is damaged. `Redaction_OnAPageSharingItsContentStream…`
            // caught it. **Resolve before you branch when the branch is about TYPE; do not when
            // the branch also carries IDENTITY.**
            var parts = new List<(int objNum, PdfStream stream)>();
            if (contentsObj is PdfReference r)
            {
                var target = doc.ResolveObject(r);
                if (target is PdfStream s) parts.Add((r.ReferenceObjectNumber, s));
                else if (target is PdfArray viaRef) AddArrayParts(viaRef, doc, parts);
            }
            else if (contentsObj is PdfArray arr) AddArrayParts(arr, doc, parts);
            else if (contentsObj is PdfStream direct) parts.Add((0, direct));

            if (parts.Count == 0) return false;

            usage ??= new ContentUsage(doc);
            bool anyShared = false;
            // `objNum > 0` marks a DIRECT stream as never shared. Removing it is an equivalent
            // mutation on any parsed file: §7.3.8 makes every stream indirect, so a direct /Contents
            // stream exists only in a document built in memory, and the usage holds 0 only if
            // another page names the malformed `0 0 R`.
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].objNum > 0 && usage.UsedByAnotherPage(page, parts[i].objNum)) { anyShared = true; break; }

            if (!anyShared)
            {
                StripStream(parts[0].stream, replacement);
                for (int i = 1; i < parts.Count; i++) StripStream(parts[i].stream, Array.Empty<byte>());
                return true;
            }

            if (allocateObjectNumber == null || extras == null) return false;

            // One fresh private stream carries the whole replacement, and this page alone points
            // at it. The originals keep their bytes for the pages that still reference them; any
            // that nothing else references becomes unreachable and TryFullRewrite drops it, which
            // is the same guarantee the in-place strip gave for an unshared stream.
            var clone = new PdfStream();
            StripStream(clone, replacement);
            int cloneNum = allocateObjectNumber();
            extras[cloneNum] = clone;
            usage.Release(page);
            page.Dictionary.Set(PdfName.Contents, new PdfReference(cloneNum, 0));
            usage.Add(page);
            return true;
        }

        /// <summary>
        /// How many page listings use each content stream, read from every page once: a page's
        /// <c>/Contents</c> streams are shared when a page with another dictionary uses them too.
        /// </summary>
        /// <remarks>
        /// <para>The question was answered by walking every page for each page substituted — the
        /// edited pages times the document's, which for a term on every page of a large document is
        /// its pages squared. One count serves an operation; a substitution that points a page at a
        /// clone takes the page's old streams out of it and adds the clone.</para>
        /// <para>Compared by dictionary identity, as the walk compared: the parser caches one
        /// instance per object number, so a page listed twice in the tree is the same dictionary
        /// twice, and is not another user of its own streams.</para>
        /// </remarks>
        internal sealed class ContentUsage
        {
            private readonly PdfDocument _doc;
            private readonly Dictionary<int, int> _listingsUsing = new Dictionary<int, int>();
            private readonly Dictionary<PdfDictionary, int> _listingsOf =
                new Dictionary<PdfDictionary, int>(PdfReferenceEquality.Instance);

            /// <summary>Page dictionaries read on the calling thread: the evidence of one walk.</summary>
            [ThreadStatic] internal static long PagesRead;

            internal ContentUsage(PdfDocument doc)
            {
                _doc = doc;
                var pages = doc.Pages;
                for (int p = 0; p < pages.Count; p++)
                {
                    var dict = pages[p]?.Dictionary;
                    if (dict == null) continue;
                    _listingsOf[dict] = _listingsOf.TryGetValue(dict, out int k) ? k + 1 : 1;
                    foreach (int n in Streams(dict)) _listingsUsing[n] = _listingsUsing.TryGetValue(n, out int c) ? c + 1 : 1;
                }
            }

            /// <summary>Whether a page whose dictionary is not <paramref name="page"/>'s uses stream <paramref name="objNum"/>.</summary>
            internal bool UsedByAnotherPage(PdfPage page, int objNum)
            {
                if (!_listingsUsing.TryGetValue(objNum, out int users)) return false;
                int own = page.Dictionary != null && _listingsOf.TryGetValue(page.Dictionary, out int k)
                          && Streams(page.Dictionary).Contains(objNum) ? k : 0;
                return users > own;
            }

            internal void Release(PdfPage page) => Count(page, -1);

            internal void Add(PdfPage page) => Count(page, +1);

            private void Count(PdfPage page, int sign)
            {
                if (page.Dictionary == null || !_listingsOf.TryGetValue(page.Dictionary, out int k)) return;
                foreach (int n in Streams(page.Dictionary))
                    _listingsUsing[n] = (_listingsUsing.TryGetValue(n, out int c) ? c : 0) + sign * k;
            }

            // A page's content streams by number, read as the walk read them: the elements of a
            // /Contents array (resolved first, so an indirect array counts its streams, not itself),
            // else the referenced stream.
            private HashSet<int> Streams(PdfDictionary pageDict)
            {
                PagesRead++;
                var used = new HashSet<int>();
                var raw = pageDict[PdfName.Contents];
                var c = _doc?.ResolveObject(raw) ?? raw;
                if (c is PdfArray ca)
                {
                    for (int i = 0; i < ca.Count; i++)
                        if (ca[i] is PdfReference er) used.Add(er.ReferenceObjectNumber);
                }
                else if (raw is PdfReference cr) used.Add(cr.ReferenceObjectNumber);
                return used;
            }
        }

        /// <summary>
        /// Collects the stream parts of a <c>/Contents</c> array, keeping each element's object
        /// number so the sharing check downstream can still identify it. Shared by the direct and
        /// indirect spellings of the array, which is the whole reason it is a method.
        /// </summary>
        private static void AddArrayParts(
            PdfArray arr, PdfDocument doc, List<(int objNum, PdfStream stream)> parts)
        {
            for (int i = 0; i < arr.Count; i++)
                if (doc.ResolveObject(arr[i]) is PdfStream s)
                    parts.Add(((arr[i] as PdfReference)?.ReferenceObjectNumber ?? 0, s));
        }

        /// <summary>
        /// Replaces a stream's bytes with unfiltered content, dropping the filter keys that would
        /// otherwise describe the old encoding — including <c>/DL</c>, whose stale value would make
        /// a size-bounded reader mis-budget the stream.
        /// </summary>
        internal static void StripStream(PdfStream s, byte[] raw)
        {
            s.RawData = raw;
            s.Remove(PdfName.Filter);
            s.Remove(PdfName.DecodeParms);
            s.Remove(PdfName.DL);
            s.Set(PdfName.Length, new PdfNumber(raw.Length));
        }

        /// <summary>
        /// Writes every object reachable from the catalog (and <c>/Info</c>) as one fresh revision,
        /// renumbered from 1. Unreachable objects — including anything the caller detached — are
        /// simply not written, which is what guarantees no residual copy of removed content.
        /// </summary>
        /// <param name="extraObjects">
        /// Objects the caller has minted that are not in the document's cross-reference table — an
        /// embedded font program and its descendants, say. Their numbers must not collide with the
        /// document's own. They are resolved ahead of the parser, so a caller may also use this to
        /// override an existing object without mutating the loaded document.
        ///
        /// <para>This exists because <see cref="PdfDocument"/> has no "add an indirect object" API:
        /// the rewrite walks reachability through <see cref="PdfDocument.GetObject"/>, so a new
        /// object would otherwise be unreachable and silently dropped — the caller's font would
        /// vanish and the page would reference a free entry.</para>
        /// </param>
        /// <param name="doc">The document to rewrite.</param>
        /// <param name="output">
        /// Receives the single-revision rewrite. Unreachable objects are not carried over, which is
        /// what makes this — and not an incremental update — the writer redaction must use.
        /// </param>
        /// <returns>False when the document has no resolvable catalog to start from.</returns>
        internal static bool TryFullRewrite(PdfDocument doc, Stream output,
                                            IReadOnlyDictionary<int, PdfObject> extraObjects = null)
        {
            int rootNum = (doc.Trailer[PdfName.Root] as PdfReference)?.ReferenceObjectNumber
                          ?? doc.Catalog?.Dictionary?.ObjectNumber ?? 0;
            if (rootNum <= 0) return false;
            int infoNum = (doc.Trailer[PdfName.Info] as PdfReference)?.ReferenceObjectNumber ?? -1;

            WriteReachable(doc, output, rootNum, infoNum, extraObjects, RewrittenFileId(doc), "PdfFullRewriter.TryFullRewrite");
            return true;
        }

        /// <summary>
        /// The rewrite's <c>/ID</c> (§14.4, required by ISO 32000-2 Table 15): the document's first
        /// half kept, since it names the file whatever its revision, and a new second half, since the
        /// content changed; both new when the document carries none a reader can use.
        /// </summary>
        private static PdfArray RewrittenFileId(PdfDocument doc)
        {
            var fresh = PdfTrailerBuilder.NewFileId();
            var existing = PdfFileId.Existing(doc.Trailer?[PdfName.ID], doc.ResolveObject);
            if (existing == null) return fresh;

            var id = new PdfArray();
            id.Add(existing[0]);
            id.Add(fresh[1]);
            return id;
        }

        /// <summary>
        /// Writes every object reachable from <paramref name="rootNum"/> (and
        /// <paramref name="infoNum"/>, when positive) as a new file, renumbered from 1 — the
        /// document's objects and the caller's <paramref name="extraObjects"/> alike, so the root may
        /// be one of those: a catalog of new pages that name the document's fonts and images, which
        /// are then copied with them.
        /// </summary>
        /// <param name="doc">The document whose objects the root reaches.</param>
        /// <param name="output">Receives the new file.</param>
        /// <param name="rootNum">The catalog's object number, the document's or an extra's.</param>
        /// <param name="infoNum">The <c>/Info</c> dictionary's, or 0 or less for none.</param>
        /// <param name="extraObjects">As for <see cref="TryFullRewrite"/>.</param>
        /// <param name="fileId">The trailer's <c>/ID</c>, or <see langword="null"/> for none.</param>
        /// <param name="operation">Who is writing, for the unreadable-object report.</param>
        internal static void WriteReachable(PdfDocument doc, Stream output, int rootNum, int infoNum,
                                            IReadOnlyDictionary<int, PdfObject> extraObjects,
                                            PdfArray fileId, string operation)
        {
            // Extras resolve AHEAD of the parser, which is what lets a caller mint objects the
            // document has never heard of — and is also why a colliding number is dangerous rather
            // than merely wrong: the real object would be silently replaced, and every reference to
            // it would now point at the caller's. A collision is a caller bug, so it fails loudly
            // here instead of producing a plausible document.
            if (extraObjects != null && extraObjects.Count > 0)
            {
                foreach (var kvp in extraObjects)
                {
                    if (kvp.Key <= 0)
                        throw new ArgumentException(
                            $"extraObjects contains object number {kvp.Key}; numbers must be positive.",
                            nameof(extraObjects));

                    var existing = doc.GetObject(kvp.Key);
                    if (existing != null && !(existing is PdfNull))
                        throw new ArgumentException(
                            $"extraObjects reuses object number {kvp.Key}, which the document already " +
                            "defines; allocate above the document's highest object number.",
                            nameof(extraObjects));
                }
            }

            PdfObject Get(int n) =>
                extraObjects != null && extraObjects.TryGetValue(n, out var extra) ? extra : doc.GetObject(n);

            var reachable = new HashSet<int>();
            Collect(rootNum, Get, reachable);
            if (infoNum > 0) Collect(infoNum, Get, reachable);

            var ordered = new List<int>(reachable);
            ordered.Sort();
            var map = new Dictionary<int, int>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++) map[ordered[i]] = i + 1;

            var writer = new PdfWriter(output);
            writer.WriteHeader(doc.Version);
            var xref = new List<(int objNum, long offset)>();

            // A reachable object that cannot be parsed is absent from the output, and its
            // references dangle there; the output then differs from the input in a way the caller
            // cannot tell from "the input never had it". Reported through the Base sink — a null
            // object with no parse error is what the file holds (§7.3.9) and is not counted.
            int unreadable = 0;
            string firstUnreadable = null;

            foreach (int orig in ordered)
            {
                var obj = Get(orig);
                if (obj == null || obj is PdfNull)
                {
                    if (Core.Parsing.PdfParser.LastObjectParseError is string reason)
                    {
                        if (unreadable++ == 0) firstUnreadable = $"object {orig}: {reason}";
                    }
                    continue;
                }
                var clone = CloneRemap(obj, map, 0);
                clone.ObjectNumber = map[orig];
                clone.GenerationNumber = 0;
                long offset = writer.WriteIndirectObject(map[orig], 0, clone);
                xref.Add((map[orig], offset));
            }

            if (unreadable > 0)
                DocumentEditDiagnostics.Report(operation,
                    $"{unreadable} reachable object(s) could not be read and are absent from the output " +
                    $"(the first: {firstUnreadable})");

            xref.Sort((a, b) => a.objNum.CompareTo(b.objNum));
            xref.Insert(0, (0, 0));
            long xrefOffset = writer.WriteXrefTable(xref);

            var trailer = new PdfDictionary();
            trailer.Set(PdfName.Size, new PdfNumber(ordered.Count + 1));
            trailer.Set(PdfName.Root, new PdfReference(map[rootNum], 0));
            if (infoNum > 0 && map.TryGetValue(infoNum, out int newInfo))
                trailer.Set(PdfName.Info, new PdfReference(newInfo, 0));
            if (fileId != null) trailer.Set(PdfName.ID, fileId);
            writer.WriteTrailer(trailer, xrefOffset);
        }

        // Every object reachable from objNum, through a work list: a reference is a step to take, not
        // a level of depth. The walk recursed through each reference and counted it against
        // MaxObjectGraphDepth, so a chain of objects — an outline's /Next, a bead thread, an action's
        // /Next — was cut about 128 links in, and what followed was not written: the references to
        // it came out as 0 0 R.
        private static void Collect(int objNum, Func<int, PdfObject> get, HashSet<int> set)
        {
            var pending = new Stack<int>();
            pending.Push(objNum);
            while (pending.Count > 0)
            {
                int n = pending.Pop();
                if (n <= 0 || !set.Add(n)) continue;
                var obj = get(n);
                if (obj != null && !(obj is PdfNull)) CollectRefs(obj, pending, 0);
            }
        }

        // The references one object holds; the depth is its own nesting, which the parser bounds too.
        private static void CollectRefs(PdfObject obj, Stack<int> pending, int depth)
        {
            if (depth > PdfLimits.MaxObjectGraphDepth || obj == null) return;
            switch (obj)
            {
                case PdfReference r: pending.Push(r.ReferenceObjectNumber); break;
                case PdfStream s:
                    foreach (var kvp in s) { if (kvp.Key.Value == "Parent") continue; CollectRefs(kvp.Value, pending, depth + 1); }
                    break;
                case PdfDictionary d:
                    foreach (var kvp in d) { if (kvp.Key.Value == "Parent") continue; CollectRefs(kvp.Value, pending, depth + 1); }
                    break;
                case PdfArray a:
                    for (int i = 0; i < a.Count; i++) CollectRefs(a[i], pending, depth + 1);
                    break;
            }
        }

        private static PdfObject CloneRemap(PdfObject obj, Dictionary<int, int> map, int depth)
        {
            if (depth > PdfLimits.MaxObjectGraphDepth) return obj;
            switch (obj)
            {
                case PdfReference r:
                    return new PdfReference(map.TryGetValue(r.ReferenceObjectNumber, out int nn) ? nn : 0, 0);
                case PdfStream s:
                    var ns = new PdfStream();
                    foreach (var kvp in s) ns.Set(kvp.Key, CloneRemap(kvp.Value, map, depth + 1));
                    ns.RawData = s.RawData;
                    return ns;
                case PdfDictionary d:
                    var nd = new PdfDictionary();
                    foreach (var kvp in d) nd.Set(kvp.Key, CloneRemap(kvp.Value, map, depth + 1));
                    return nd;
                case PdfArray a:
                    var na = new PdfArray();
                    for (int i = 0; i < a.Count; i++) na.Add(CloneRemap(a[i], map, depth + 1));
                    return na;
                default:
                    return obj;
            }
        }
    }
}
