// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// Reads one page out of a linearized file a client holds only part of — three byte ranges and
    /// no cross-reference table.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the resolver that can say <i>not yet</i>.</b> Everywhere else in this
    /// library an object that cannot be produced comes back as <c>PdfNull</c>, and every caller
    /// reads that as <i>absent</i> — which is right for a complete file and wrong for an arriving
    /// one. Here the third answer is the return value, and it carries
    /// <see cref="PdfProgressivePage.Missing"/>: the ranges to fetch before asking again. A client
    /// that is only told "no" has to guess which bytes it needs.</para>
    ///
    /// <para><b>The route is Annex F's own.</b> The first 1024 bytes give the parameter dictionary,
    /// whose <c>/H</c> names the hint stream; the hint stream's page offset table names every page's
    /// range. So page <i>k</i> of a document that has never been opened costs three requests, and
    /// the third one is the only one whose size depends on the page.</para>
    ///
    /// <para><b>It refuses a file that was not written for progressive access</b> rather than
    /// falling back to reading the whole thing. Falling back would make the expensive case silent:
    /// a caller that asked for one page would download the file and never learn why.</para>
    ///
    /// <para><b>The objects come from the range's own bytes.</b> There is no cross-reference table
    /// to consult, so <c>RawObjectScanner</c> — the same scan the repair tool and FDF import use —
    /// finds the <c>N G obj</c> headers inside what was fetched. A reference leaving the range is
    /// exactly the *not yet* case, and the shared object hint table says where to look.</para>
    /// </remarks>
    public static class PdfProgressivePageReader
    {
        /// <summary>Why the last read on this thread produced no page. <c>null</c> after a clean read.</summary>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        [ThreadStatic] private static string _lastError;

        private static readonly IReadOnlyList<PdfByteRange> Nothing = new PdfByteRange[0];

        /// <summary>
        /// Reads page <paramref name="pageIndex"/> from whatever of the file has arrived.
        /// </summary>
        /// <param name="source">The file, or as much of it as has arrived.</param>
        /// <param name="pageIndex">The page, counted from zero.</param>
        /// <param name="page">
        /// What was read. On <see cref="PdfLinearizationState.NotYetAvailable"/> its
        /// <see cref="PdfProgressivePage.Missing"/> names the ranges to fetch; its
        /// <see cref="PdfProgressivePage.Page"/> may still be set, when the dictionary is here and
        /// only the content is not.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public static PdfLinearizationState TryReadPage(
            IByteRangeSource source, int pageIndex, out PdfProgressivePage page)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            LastError = null;
            page = null;

            // 1. The head: Table F.1, from the first 1024 bytes.
            PdfLinearizationState state = PdfRevisions.ReadLinearization(source, out PdfLinearizationInfo info);
            if (state == PdfLinearizationState.NotYetAvailable)
            {
                page = Pending(pageIndex, Range(0, ParameterHeadBytes));
                return state;
            }

            if (state == PdfLinearizationState.NotLinearized)
            {
                LastError = "the document was not written for progressive access (no /Linearized)";
                return state;
            }

            if (info == null)
            {
                LastError = "the linearization parameter dictionary could not be read";
                return PdfLinearizationState.Linearized;
            }

            // 2. The hint stream: the one range /H names.
            PdfLinearizationState hintState =
                PdfLinearizationHintReader.TryRead(source, out PdfLinearizationHints hints);

            // CLASSIFIED, NOT COVERED: this library's own linearizer places the hint stream within
            // the first few hundred bytes, so the 4 KB head request above always carries it and this
            // branch cannot fire on our output — measured at 378 bytes on a document whose first
            // page was deliberately made 8 KB, which is the fixture that was tried and withdrawn.
            // Annex F does not require that placement, so a producer may put the hint stream past
            // the head; the branch is for those files. It is kept rather than deleted because
            // without it such a file would fail at the next step with no range to fetch.
            if (hintState == PdfLinearizationState.NotYetAvailable)
            {
                page = Pending(pageIndex, Range(info.HintStreamOffset, info.HintStreamLength));
                return hintState;
            }

            if (hints == null)
            {
                LastError = "the hint tables could not be read: " + PdfLinearizationHintReader.LastError;
                return PdfLinearizationState.Linearized;
            }

            if (pageIndex < 0 || pageIndex >= hints.Pages.Count)
            {
                LastError = "page " + pageIndex.ToString(CultureInfo.InvariantCulture)
                            + " is outside the " + hints.Pages.Count.ToString(CultureInfo.InvariantCulture)
                            + " pages the hint tables describe";
                return PdfLinearizationState.Linearized;
            }

            // 3. The page's own range.
            PdfPageHint hint = hints.Pages[pageIndex];
            if (!TryFetch(source, hint.Offset, hint.Length, out byte[] bytes))
            {
                page = Pending(pageIndex, Range(hint.Offset, hint.Length));
                return PdfLinearizationState.NotYetAvailable;
            }

            List<RawObject> objects = ScanObjects(bytes);

            PdfDictionary pageDict = FindPageDictionary(bytes, objects);
            if (pageDict == null)
            {
                LastError = "the range the page offset table names for page "
                            + pageIndex.ToString(CultureInfo.InvariantCulture)
                            + " holds no page object";
                return PdfLinearizationState.Linearized;
            }

            var sections = new ProgressiveSections();
            sections.Add(bytes, objects);

            // The shared-object groups the page names (Table F.3 item 7), fetched once per read when
            // something the page needs is not among its own objects. Annex F puts an object two pages
            // draw in part 8 (or, when the first page is one of them, in part 6); Table F.5 gives each
            // group's length, so a group's range is the sum of the lengths before it from the
            // section's first offset. The ranges are named, as the page's own was, rather than
            // refused: *not yet* means more bytes change the answer.
            List<PdfByteRange> groupsMissing = null;
            void FetchGroups()
            {
                if (groupsMissing != null) return;
                groupsMissing = new List<PdfByteRange>();
                foreach (PdfByteRange range in SharedRanges(hints, hint))
                {
                    if (TryFetch(source, range.Offset, range.Length, out byte[] shared))
                        sections.Add(shared, ScanObjects(shared));
                    else
                        groupsMissing.Add(range);
                }
            }

            // 4. The content stream, when it is not among the page's own objects.
            if (ContentReference(pageDict) is PdfReference contentRef
                && sections.Find(contentRef.ReferenceObjectNumber) == null)
            {
                FetchGroups();
                if (groupsMissing.Count > 0 && sections.Find(contentRef.ReferenceObjectNumber) == null)
                {
                    page = new PdfProgressivePage(pageIndex, pageDict, null, groupsMissing);
                    return PdfLinearizationState.NotYetAvailable;
                }
            }

            // 5. The page's other shared resources — a font, an image, an ExtGState another page also
            // uses lives in the shared section too, and the same groups name it. Every reference the
            // page's /Resources reaches, to a fixed depth of hops, is looked for among the fetched
            // sections; one that is in none of them fetches the page's groups, as the content did.
            // What is still outside after that is reported on the page, never answered as absent: by
            // Annex F a page's objects are its own or shared, so such a reference is the file's defect.
            List<PdfReference> unresolved = ResourceReferencesOutside(pageDict, sections);
            if (unresolved.Count > 0)
            {
                FetchGroups();
                unresolved = ResourceReferencesOutside(pageDict, sections);
                if (unresolved.Count > 0 && groupsMissing.Count > 0)
                {
                    page = new PdfProgressivePage(pageIndex, pageDict, null, groupsMissing);
                    return PdfLinearizationState.NotYetAvailable;
                }
            }

            page = new PdfProgressivePage(pageIndex, pageDict, ContentBytes(pageDict, sections), Nothing,
                error: null, sections, unresolved);
            return PdfLinearizationState.Linearized;
        }

        /// <summary>Reference hops followed from a page's <c>/Resources</c>: the dictionary, a category's entry, its descriptor, its program.</summary>
        private const int ResourceReferenceHops = 4;

        /// <summary>
        /// The references the page's <c>/Resources</c> reach — through dictionaries, arrays and stream
        /// dictionaries, <see cref="ResourceReferenceHops"/> hops deep — that no fetched section holds.
        /// </summary>
        /// <remarks>
        /// Bounded twice: a reference is followed once (a font two categories share, a cycle), and the
        /// objects examined are capped by <see cref="PdfLimits.MaxProgressiveResourceObjects"/> — a
        /// <c>/Font</c> dictionary of a million entries costs the file nothing. Past the cap the rest
        /// is not checked, and <see cref="LastError"/> says so; the page is still answered.
        /// </remarks>
        private static List<PdfReference> ResourceReferencesOutside(PdfDictionary pageDict, ProgressiveSections sections)
        {
            var outside = new List<PdfReference>();
            var seen = new HashSet<int>();
            var queue = new Queue<(PdfObject Value, int Hops)>();
            queue.Enqueue((pageDict[PdfName.Resources], 0));

            int ceiling = PdfLimits.MaxProgressiveResourceObjects;
            int examined = 0;
            while (queue.Count > 0)
            {
                if (++examined > ceiling)
                {
                    LastError = "the page's resources reach more than "
                                + ceiling.ToString(CultureInfo.InvariantCulture)
                                + " objects; those past the ceiling were not checked";
                    break;
                }

                (PdfObject value, int hops) = queue.Dequeue();
                if (value is PdfReference reference)
                {
                    if (!seen.Add(reference.ReferenceObjectNumber)) continue;
                    if (hops >= ResourceReferenceHops) continue;

                    value = sections.Parse(reference.ReferenceObjectNumber);
                    if (value == null)
                    {
                        outside.Add(reference);
                        continue;
                    }
                    hops++;
                }

                // A stream is a dictionary here: its entries (a font program's /Length, an image's
                // /SMask and /ColorSpace) are what the walk follows, never its bytes.
                switch (value)
                {
                    case PdfDictionary dictionary:
                        foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary)
                            queue.Enqueue((entry.Value, hops));
                        break;
                    case PdfArray array:
                        for (int i = 0; i < array.Count; i++) queue.Enqueue((array[i], hops));
                        break;
                }
            }

            return outside;
        }

        /// <summary>The page's <c>/Contents</c> when it is one reference; null for anything else.</summary>
        private static PdfReference ContentReference(PdfDictionary pageDict)
            => pageDict[PdfName.Contents] as PdfReference;

        /// <summary>
        /// The file ranges of the shared-object groups <paramref name="hint"/>'s page references
        /// (Table F.3 item 7), located through Table F.5: a group in the first page's run is at the
        /// first page's offset plus the lengths of the groups before it; one in the shared section
        /// at <see cref="PdfLinearizationHints.FirstSharedObjectOffset"/> plus the lengths of the
        /// shared-section groups before it. An identifier the table does not define, or a group
        /// whose length is not positive, is skipped — a hostile table names nothing fetchable.
        /// </summary>
        private static IEnumerable<PdfByteRange> SharedRanges(PdfLinearizationHints hints, PdfPageHint hint)
        {
            IReadOnlyList<PdfSharedObjectHint> groups = hints.SharedObjects;
            int inFirstPage = Math.Max(0, Math.Min(hints.SharedObjectsInFirstPage, groups.Count));

            // Prefix sums once, so a page naming many groups costs the table's length, not its square.
            var startOf = new long[groups.Count];
            long at = hints.Pages.Count > 0 ? hints.Pages[0].Offset : 0;
            for (int i = 0; i < inFirstPage; i++) { startOf[i] = at; at += Math.Max(0, groups[i].Length); }
            at = hints.FirstSharedObjectOffset;
            for (int i = inFirstPage; i < groups.Count; i++) { startOf[i] = at; at += Math.Max(0, groups[i].Length); }

            var seen = new HashSet<int>();
            foreach (int id in hint.SharedObjectIdentifiers)
            {
                if (id < 0 || id >= groups.Count || !seen.Add(id)) continue;
                long length = groups[id].Length;
                if (length <= 0 || startOf[id] < 0) continue;
                yield return Range(startOf[id], length);
            }
        }

        /// <summary>
        /// Reads page <paramref name="pageIndex"/>, awaiting each range the file needs.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the synchronous protocol driven to completion, not a second
        /// implementation.</b> <see cref="TryReadPage"/> answers *not yet* and names the ranges; this
        /// awaits exactly those and asks again. So the decoding, the ceilings, the refusals and the
        /// Annex F reasoning exist once — a mirrored async decoder would be the drift this
        /// repository treats as a defect waiting to happen.</para>
        ///
        /// <para><b>It reports through <see cref="PdfProgressivePage.Error"/>, not
        /// <see cref="LastError"/>.</b> That slot is <c>[ThreadStatic]</c> and a continuation may
        /// resume on another thread, so a caller awaiting this could read someone else's value or
        /// none at all.</para>
        ///
        /// <para>The round count is bounded because the ranges are: the head, the hint stream, the
        /// page, and the shared-object groups a page needs when its content stream or one of its
        /// resources is one another page also uses (the groups are fetched once, for either). A source
        /// that keeps returning less than it was asked for therefore ends the loop with a reported
        /// reason rather than spinning.</para>
        /// </remarks>
        /// <param name="source">The file, fetched a range at a time.</param>
        /// <param name="pageIndex">The page, counted from zero.</param>
        /// <param name="cancellationToken">Cancels the fetches.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public static async Task<PdfProgressivePage> ReadPageAsync(
            IAsyncByteRangeSource source, int pageIndex,
            CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var buffer = new FetchedRanges(source.Length);

            // One more round than there are ranges, so the last fetch gets a chance to be read.
            for (int round = 0; round < 5; round++)
            {
                PdfLinearizationState state = TryReadPage(buffer, pageIndex, out PdfProgressivePage page);
                string error = LastError;

                if (state != PdfLinearizationState.NotYetAvailable)
                    return page ?? new PdfProgressivePage(pageIndex, null, null, Nothing, error);

                foreach (PdfByteRange range in page.Missing)
                {
                    byte[] bytes = await source.FetchAsync(range.Offset, range.Length, cancellationToken)
                                               .ConfigureAwait(false);
                    buffer.Add(range.Offset, bytes);
                }
            }

            return new PdfProgressivePage(
                pageIndex, null, null, Nothing,
                "the source did not deliver the ranges the page needs after five rounds");
        }

        /// <summary>
        /// What has been fetched so far, presented to the synchronous reader as a source.
        /// </summary>
        /// <remarks>
        /// It answers <c>false</c> for a range it does not hold, which is what makes the synchronous
        /// reader name that range instead of failing — the two halves of the seam meeting.
        /// </remarks>
        private sealed class FetchedRanges : IByteRangeSource
        {
            private readonly List<(long From, long To, byte[] Bytes)> _ranges =
                new List<(long, long, byte[])>();

            internal FetchedRanges(long? length) => Length = length;

            public long? Length { get; }

            internal void Add(long offset, byte[] bytes) =>
                _ranges.Add((offset, offset + (bytes?.Length ?? 0), bytes ?? Array.Empty<byte>()));

            public bool TryRead(long offset, byte[] buffer, int bufferOffset, int count, out int read)
            {
                read = 0;
                if (Length.HasValue && offset >= Length.Value) return true;

                foreach ((long from, long to, byte[] bytes) in _ranges)
                {
                    if (offset < from) continue;

                    long available = to - offset;
                    if (available <= 0) continue;

                    // A range that ends at the file's end satisfies a request running past it; one
                    // that merely ends early does not, or a short fetch would be read as EOF.
                    //
                    // CLASSIFIED, NOT COVERED: forcing this to false survives every fixture, because
                    // no request here ever runs past the end of a delivered range. The head is read
                    // in 1024-byte windows and the parameters parse from the first one, so the
                    // widening reads that would ask for more than a small file holds never happen;
                    // the hint and page ranges are bounded by the file before they are requested.
                    // It fires for a file whose linearization dictionary spans past the first window
                    // AND is shorter than the second — kept because that file is legal, and without
                    // it the reader would treat a short fetch as end of file and decode a fragment.
                    bool reachesEnd = Length.HasValue && to >= Length.Value;
                    if (available < count && !reachesEnd) continue;

                    read = (int)Math.Min(count, available);
                    Array.Copy(bytes, offset - from, buffer, bufferOffset, read);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// How much head the parameter read needs — four Annex F windows, the same as
        /// <c>PdfRevisions</c>'s own read, so a caller told to fetch this can then ask again and get
        /// past step 1.
        /// </summary>
        private static int ParameterHeadBytes => 4 * PdfLimits.LinearizationDictionaryWindowBytes;

        private static PdfProgressivePage Pending(int pageIndex, PdfByteRange range) =>
            new PdfProgressivePage(pageIndex, null, null, new[] { range });

        private static PdfByteRange Range(long offset, long length) => new PdfByteRange(offset, length);

        private static bool TryFetch(IByteRangeSource source, long offset, long length, out byte[] bytes)
        {
            bytes = null;
            if (offset < 0 || length <= 0 || length > int.MaxValue) return false;

            var buffer = new byte[length];
            if (!source.TryRead(offset, buffer, 0, buffer.Length, out int read)) return false;
            if (read < buffer.Length) Array.Resize(ref buffer, read);

            bytes = buffer;
            return true;
        }

        /// <summary>The indirect objects the fetched range holds.</summary>
        private static List<RawObject> ScanObjects(byte[] bytes) =>
            RawObjectScanner.Scan(bytes, parser: null, new List<string>(), out _);

        /// <summary>The <c>/Type /Page</c> object in the fetched bytes.</summary>
        /// <remarks>
        /// <para><b>Selected by <c>/Type</c>, not by position.</b> Annex F says a page's objects are
        /// contiguous, not which one comes first, so taking the first dictionary would depend on a
        /// producer's ordering — and would silently return the content stream's dictionary, or a
        /// font's, as the page.</para>
        ///
        /// <para><b>CLASSIFIED, NOT COVERED.</b> Mutating the test to "any dictionary will do"
        /// survives every fixture here, including page 0, because this library's linearizer always
        /// writes the page object first in its run. The distinction is unobservable on our own
        /// output and real on someone else's, which is the case this reader exists to handle.</para>
        /// </remarks>
        private static PdfDictionary FindPageDictionary(byte[] bytes, List<RawObject> objects)
        {
            foreach (RawObject raw in objects)
            {
                PdfDictionary dict = RawObjectScanner.ReadDictionary(bytes, raw.BodyStart, raw.DictionaryEnd);
                if (dict != null && (dict[PdfName.Type] as PdfName)?.Value == "Page") return dict;
            }

            return null;
        }

        /// <summary>
        /// The page's content stream, decoded, resolved through <c>/Contents</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Table F.3's content offset is a hint, not a locator, and trusting it returns the
        /// wrong bytes without an error.</b> Items 5 and 6 are measured from the start of the page's
        /// objects and a producer may simply declare the whole run — this library's own linearizer
        /// writes offset 0 and the page's full length. Taken as a file position, those numbers would
        /// return the opening bytes of the *file* as every page's content, and a test comparing
        /// against the same wrong range would agree.</para>
        ///
        /// <para>So the hint decides what to <i>fetch</i> and <c>/Contents</c> decides what was
        /// <i>got</i>. The content stream is one of the page's own objects — or, when two pages draw
        /// one stream, a shared object, whose group <c>TryReadPage</c> fetched by its Table F.5
        /// range before calling this — so it is inside a section already in hand.</para>
        /// </remarks>
        private static byte[] ContentBytes(PdfDictionary pageDict, ProgressiveSections sections)
        {
            PdfReference reference = ContentReference(pageDict);
            if (reference == null) return null;

            (byte[] Bytes, RawObject Raw)? found = sections.Find(reference.ReferenceObjectNumber);
            if (found == null)
            {
                LastError = "the page's /Contents object is neither inside the range the hint table names "
                            + "for it nor inside the shared-object groups it lists for the page";
                return null;
            }

            (byte[] bytes, RawObject raw) = found.Value;
            try
            {
                var slice = new byte[raw.End - raw.Start];
                Array.Copy(bytes, raw.Start, slice, 0, slice.Length);

                using var stream = new MemoryStream(slice, writable: false);
                if (!(new Core.Parsing.PdfParser(stream).ParseIndirectObject() is PdfStream content))
                    return null;

                return content.TryGetDecodedData(PdfLimits.MaxDecompressedBytes, out byte[] data)
                    ? data
                    : null;
            }
            catch (Core.PdfException ex)
            {
                LastError = "the page's content stream could not be read: " + ex.Message;
                return null;
            }
        }
    }

    /// <summary>
    /// The ranges fetched for one page — its own run and the shared-object groups it names — with
    /// the <c>N G obj</c> headers <c>RawObjectScanner</c> found in each, and the objects parsed from
    /// them on demand. The resolver behind <see cref="PdfProgressivePage.Resolve"/>.
    /// </summary>
    internal sealed class ProgressiveSections
    {
        private readonly List<(byte[] Bytes, List<RawObject> Objects)> _sections =
            new List<(byte[] Bytes, List<RawObject> Objects)>();

        private readonly Dictionary<int, PdfObject> _parsed = new Dictionary<int, PdfObject>();

        /// <summary>
        /// Each object number's first scanned header, in the order the sections and their objects were
        /// added. Searched linearly, a page's resource check — up to
        /// <see cref="PdfLimits.MaxProgressiveResourceObjects"/> lookups — cost that many walks of every
        /// header the fetched ranges hold.
        /// </summary>
        private readonly Dictionary<int, (byte[] Bytes, RawObject Raw)> _byNumber = new Dictionary<int, (byte[] Bytes, RawObject Raw)>();

        internal void Add(byte[] bytes, List<RawObject> objects)
        {
            _sections.Add((bytes, objects));
            foreach (RawObject raw in objects)
                if (!_byNumber.ContainsKey(raw.ObjectNumber)) _byNumber[raw.ObjectNumber] = (bytes, raw);
        }

        /// <summary>The scanned object numbered <paramref name="objectNumber"/> in any fetched section — the first added — or null.</summary>
        internal (byte[] Bytes, RawObject Raw)? Find(int objectNumber)
            => _byNumber.TryGetValue(objectNumber, out var found) ? found : ((byte[] Bytes, RawObject Raw)?)null;

        /// <summary>
        /// The object numbered <paramref name="objectNumber"/>, parsed from its section's bytes; null
        /// when no section holds it or its bytes do not parse. An object found is parsed once.
        /// </summary>
        /// <remarks>
        /// <b>A miss is not cached.</b> The resource check asks before the shared-object groups are
        /// fetched and again after; a null remembered from the first ask answered the second, and a
        /// font the page had just fetched stayed "unresolved".
        /// </remarks>
        internal PdfObject Parse(int objectNumber)
        {
            if (_parsed.TryGetValue(objectNumber, out PdfObject cached)) return cached;

            (byte[] Bytes, RawObject Raw)? found = Find(objectNumber);
            if (found == null) return null;

            (byte[] bytes, RawObject raw) = found.Value;
            PdfObject value;
            try
            {
                var slice = new byte[raw.End - raw.Start];
                Array.Copy(bytes, raw.Start, slice, 0, slice.Length);
                using var stream = new MemoryStream(slice, writable: false);
                value = new Core.Parsing.PdfParser(stream).ParseIndirectObject();
                if (value is PdfNull) value = null;
            }
            catch (Core.PdfException)
            {
                value = null;
            }

            if (value != null) _parsed[objectNumber] = value;
            return value;
        }
    }
}
