// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Divides a document's objects into ISO 32000-1 Annex F's file parts, in the *old* object
    /// numbering. This is the decision that makes a file linearized; everything else is bookkeeping.
    /// </summary>
    /// <remarks>
    /// <para><b>The transitive closure of the first page is not the first page.</b> A page dictionary
    /// carries <c>/Parent</c>, the parent's <c>/Kids</c> names every other page, and every page names
    /// its own content — so walking references from page 1 without stopping reaches the whole
    /// document. F.3.7 says exactly which edges to refuse: "All objects that the page object refers
    /// to, to an arbitrary depth, <i>except page tree nodes or other page objects</i>", and "not the
    /// <c>Thumb</c> entry". Without those two exclusions part 6 is the file and the ordering buys
    /// nothing.</para>
    ///
    /// <para><b>An object shared between pages belongs to whichever part a reader reaches first.</b>
    /// F.4.2: "objects that are referenced from the first page shall be located with the first-page
    /// objects (part 6); all other shared objects shall be located in the shared objects section
    /// (part 8)." So sharing is computed against the page closures, and part 6 wins ties — a font
    /// used by every page is part of the first page's download, not a later fetch.</para>
    ///
    /// <para><b>Parts 4 and 9 are separated by what a reader needs to open the document</b>, not by
    /// reachability. F.3.5 lists the catalogue entries that must be up front and says everything else
    /// — "page tree nodes, the document information dictionary, and the definitions for named
    /// destinations" — goes to the end. The page tree in particular is deliberately last: "This
    /// object can be located in this section because the conforming reader never needs to consult
    /// it."</para>
    /// </remarks>
    internal sealed class LinearizationParts
    {
        /// <summary>Document-level objects a reader needs to open the file (part 4).</summary>
        internal List<int> Part4 { get; } = new List<int>();

        /// <summary>The first page's objects (part 6), its page object first.</summary>
        internal List<int> Part6 { get; } = new List<int>();

        /// <summary>
        /// One list per remaining page in page order (part 7), each page's own object first.
        /// </summary>
        internal List<List<int>> Part7 { get; } = new List<List<int>>();

        /// <summary>Objects shared between pages but not reachable from the first (part 8).</summary>
        internal List<int> Part8 { get; } = new List<int>();

        /// <summary>Everything a reader does not need to display a page (part 9).</summary>
        internal List<int> Part9 { get; } = new List<int>();

        /// <summary>
        /// The outline hierarchy, as one contiguous run inside <see cref="Part9"/> — or, when the
        /// catalogue's <c>/PageMode</c> is <c>/UseOutlines</c>, inside <see cref="Part6"/> after the
        /// first page's own objects (F.3.7) — or empty when the document has none.
        /// </summary>
        /// <remarks>
        /// Kept as a separate list because Table F.9's generic hint table describes "a single group
        /// of objects that are located together in the PDF file" by first object number, location,
        /// count and total length — all four of which are only meaningful if the group really is
        /// contiguous. F.3.10 asks for that anyway: part 9's objects "shall be divided into
        /// functional categories" and "objects within each of these categories should be grouped
        /// together".
        /// </remarks>
        internal List<int> OutlineGroup { get; } = new List<int>();

        /// <summary>
        /// Part 9's document-level categories that Table F.9's generic hint table describes, each one
        /// contiguous run, by the primary hint stream's key: <c>O</c> the outline hierarchy (the same run
        /// as <see cref="OutlineGroup"/>), <c>A</c> the article threads' information dictionaries (the
        /// thread dictionaries are part 4's, the beads their pages'), <c>E</c> the named destinations
        /// (<c>/Dests</c> and the <c>/Names</c> tree's <c>/Dests</c>), <c>I</c> the information
        /// dictionary, <c>L</c> the page labels — in that order, each present only when it holds an
        /// object (F.4.5 asks for a table "if needed").
        /// </summary>
        internal List<(string Key, List<int> Objects)> GenericGroups { get; } = new List<(string, List<int>)>();

        /// <summary>
        /// For an extended generic category (Table F.10) — <c>C</c>, the logical structure — the objects in
        /// part 6 or part 8 its objects refer to, whose shared object identifiers its table lists.
        /// </summary>
        internal Dictionary<string, List<int>> GroupSharedReferences { get; } = new Dictionary<string, List<int>>();

        /// <summary>
        /// Each embedded file stream that part 9 holds, as one group: the stream first, then the objects it
        /// references, in old numbering, with the shared section objects they reference (F.4.7, Table F.12).
        /// The groups are placed one after another, which is what lets Table F.12's lengths locate them.
        /// Then each one the first page's section holds, as a group of no objects whose shared reference is
        /// the stream itself.
        /// </summary>
        internal List<(int Stream, List<int> Objects, List<int> Shared)> EmbeddedFileGroups { get; } =
            new List<(int, List<int>, List<int>)>();

        /// <summary>
        /// Each page's thumbnail group (F.4.4, Table F.8), in page order and placed adjacently: the
        /// <c>/Thumb</c> image and the objects only it references, in old numbering, with the page's index.
        /// </summary>
        internal List<(int PageIndex, List<int> Objects)> ThumbnailGroups { get; } = new List<(int, List<int>)>();

        /// <summary>
        /// Table F.7 items 9–12's thumbnail shared objects section: what two or more thumbnails reference and
        /// nothing else does, placed right after the groups.
        /// </summary>
        internal List<int> ThumbnailShared { get; } = new List<int>();

        /// <summary>References the closure walks examined (<see cref="PdfLimits.MaxLinearizationEdges"/>).</summary>
        internal long EdgesExamined { get; private set; }

        /// <summary>The first page's page object, in the old numbering.</summary>
        internal int FirstPageObject { get; private set; }

        /// <summary>
        /// For each page after the first, the objects it references that live in part 6 or part 8 —
        /// the "shared object" set Table F.4 item 4 enumerates.
        /// </summary>
        internal List<List<int>> SharedReferences { get; } = new List<List<int>>();

        /// <summary>Every part in file order, flattened.</summary>
        internal IEnumerable<int> InFileOrder =>
            Part4.Concat(Part6).Concat(Part7.SelectMany(p => p)).Concat(Part8).Concat(Part9);

        internal static LinearizationParts Build(PdfDocument doc, Dictionary<int, PdfObject> allObjects)
        {
            var parts = new LinearizationParts();

            var pageObjects = new List<int>();
            foreach (PdfPage page in doc.Pages)
                pageObjects.Add(page.Dictionary.ObjectNumber);

            // Page tree nodes are found by type rather than by walking /Kids: a malformed tree that
            // does not reach every page would otherwise leave a node inside a page closure, and one
            // page dragging the tree in drags every other page with it.
            var pageTreeNodes = new HashSet<int>();
            foreach (var kvp in allObjects)
            {
                if (kvp.Value is PdfDictionary d &&
                    d[PdfName.Type] is PdfName t && t.Value == "Pages")
                    pageTreeNodes.Add(kvp.Key);
            }

            var blocked = new HashSet<int>(pageTreeNodes);
            foreach (int p in pageObjects) blocked.Add(p);

            // One closure per page, each stopping at page boundaries — and their sum bounded by the reader's own
            // ceiling (PdfLimits.MaxLinearizationSharedReferences): a page's hint entry lists the objects it shares,
            // so pages naming one /Resources of n entries cost, and write, pages × n — unbounded, 800 pages sharing
            // 3 200 small objects would take 9.5 s and write 2.56 million references, hints the reader refuses.
            var walker = new Walker(allObjects);
            var closures = new List<HashSet<int>>();
            var closureOrders = new List<List<int>>();   // each closure in discovery order, the page's object not included
            long referenced = 0;
            foreach (int pageNum in pageObjects)
            {
                List<int> order = walker.Closure(pageNum, blocked);
                var closure = new HashSet<int>(order);
                closures.Add(closure);
                closureOrders.Add(order);
                referenced += closure.Count;
                if (referenced > PdfLimits.MaxLinearizationSharedReferences)
                    throw new Core.PdfException(
                        "Cannot linearize: the pages reach more than "
                        + PdfLimits.MaxLinearizationSharedReferences.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " objects in all, so the hint tables would list more page-to-object references than "
                        + "PdfLimits.MaxLinearizationSharedReferences lets a reader accept.");
            }

            parts.FirstPageObject = pageObjects.Count > 0 ? pageObjects[0] : 0;

            // Part 4 — the catalogue and everything the entries F.3.5 names reach, pages excepted.
            //
            // Everything they reach, not their top-level values: an /AcroForm written directly in the
            // catalogue claims its field widgets and /DR fonts here rather than leaving them counted as
            // first-page and shared-page objects — which a structure check, re-deriving the parts from
            // the whole closure with "open document" taking precedence over any page that also reaches
            // the object, would report. Pages and page tree nodes stay blocked, so a widget's /P cannot
            // drag its page up front.
            var part4 = new HashSet<int>();
            // What /AcroForm reaches: part 4 lists it last, as one run, for the interactive form hint table.
            var interactiveForm = new HashSet<int>();
            var formStarts = new HashSet<int>();   // the form dictionary, which opens the run
            var catalogRef = doc.Trailer[PdfName.Root] as PdfReference;
            if (catalogRef != null)
            {
                int catNum = catalogRef.ReferenceObjectNumber;
                part4.Add(catNum);
                if (allObjects.TryGetValue(catNum, out PdfObject catObj) && catObj is PdfDictionary cat)
                {
                    // The catalogue is blocked too: an object that refers back to it would otherwise bring
                    // everything it reaches — the outline, the structure tree — into part 4 and the form's run.
                    var documentBlocked = new HashSet<int>(blocked) { catNum };
                    foreach (PdfName key in DocumentLevelKeys)
                    {
                        if (key == PdfName.Threads) continue;   // its own rule, below
                        PdfObject value = cat[key];
                        if (value == null) continue;
                        foreach (int start in Nested(value, 0))
                        {
                            if (documentBlocked.Contains(start) || !allObjects.ContainsKey(start)) continue;
                            part4.Add(start);
                            List<int> reached = walker.Closure(start, documentBlocked);
                            part4.UnionWith(reached);
                            if (key == PdfName.AcroForm)
                            {
                                interactiveForm.Add(start);
                                formStarts.Add(start);
                                interactiveForm.UnionWith(reached);
                            }
                        }
                    }

                    // Threads bring their thread dictionaries with them (F.3.5), but not the
                    // information dictionaries or beads those refer to.
                    if (cat[PdfName.Threads] is PdfReference threadsRef &&
                        allObjects.TryGetValue(threadsRef.ReferenceObjectNumber, out PdfObject threadsObj) &&
                        threadsObj is PdfArray threads)
                    {
                        foreach (PdfObject entry in threads)
                            if (entry is PdfReference tr) part4.Add(tr.ReferenceObjectNumber);
                    }
                }
            }

            // How many pages reach each object. Part 4 is excluded first, so a catalogue-level
            // object a page happens to reference stays up front rather than being re-classified.
            var pageCount = new Dictionary<int, int>();
            foreach (HashSet<int> closure in closures)
                foreach (int n in closure)
                {
                    if (part4.Contains(n)) continue;
                    pageCount.TryGetValue(n, out int c);
                    pageCount[n] = c + 1;
                }

            // The interactive form's objects last, as one run, so its hint table (Table F.2 /V, F.4.6) can
            // name them.
            foreach (int n in part4.Where(x => !interactiveForm.Contains(x)).OrderBy(x => x))
                if (allObjects.ContainsKey(n)) parts.Part4.Add(n);
            var formRun = new List<int>();
            foreach (int n in interactiveForm.OrderBy(x => formStarts.Contains(x) ? 0 : 1).ThenBy(x => x))
                if (allObjects.ContainsKey(n)) { parts.Part4.Add(n); formRun.Add(n); }

            var placed = new HashSet<int>(parts.Part4);

            var inPart6 = new HashSet<int>();
            if (closures.Count > 0)
            {
                parts.Part6.Add(pageObjects[0]);
                placed.Add(pageObjects[0]);
                inPart6.Add(pageObjects[0]);
                // The order the closure was discovered in: walking it again confined to itself would find
                // the same breadth-first order and pay for every reference a second time.
                foreach (int n in closureOrders[0])
                    if (!part4.Contains(n) && placed.Add(n)) { parts.Part6.Add(n); inPart6.Add(n); }
            }

            PdfDictionary rootDict = catalogRef != null &&
                allObjects.TryGetValue(catalogRef.ReferenceObjectNumber, out PdfObject root) ? root as PdfDictionary : null;

            // F.3.7: when the catalogue's /PageMode is /UseOutlines the outline hierarchy opens with the
            // first page — a reader shows the bookmarks the document asks to open with before it has
            // the tail — so its run goes into part 6 after the page's objects, inside /E, and the
            // outline hint table names it there. Any other page mode leaves it in part 9 (below).
            if (closures.Count > 0 && rootDict?[PdfName.PageMode] is PdfName pageMode && pageMode.Value == "UseOutlines")
            {
                var outlineRun = new List<int>();
                foreach (int start in References(rootDict[PdfName.Outlines]))
                    foreach (int n in walker.Ordered(start, blocked, part4))
                    {
                        if (!placed.Add(n)) continue;
                        outlineRun.Add(n);
                        parts.Part6.Add(n);
                        inPart6.Add(n);
                    }
                if (outlineRun.Count > 0)
                {
                    parts.OutlineGroup.AddRange(outlineRun);
                    parts.GenericGroups.Add(("O", outlineRun));
                }
            }

            // Part 8 before part 7: an object shared by two later pages must not be claimed by the
            // first of them.
            var shared = new HashSet<int>();
            foreach (var kvp in pageCount)
                if (kvp.Value > 1 && !placed.Contains(kvp.Key)) shared.Add(kvp.Key);

            for (int i = 1; i < closures.Count; i++)
            {
                var pagePart = new List<int> { pageObjects[i] };
                var pageShared = new List<int>();
                placed.Add(pageObjects[i]);

                foreach (int n in closureOrders[i])
                {
                    if (part4.Contains(n)) continue;
                    if (inPart6.Contains(n) || shared.Contains(n)) { pageShared.Add(n); continue; }
                    if (placed.Add(n)) pagePart.Add(n);
                }

                parts.Part7.Add(pagePart);
                parts.SharedReferences.Add(pageShared);
            }

            foreach (int n in shared.OrderBy(x => x))
                if (placed.Add(n)) parts.Part8.Add(n);

            // Part 9, by F.3.10's categories: the page tree first — "the conforming reader never
            // needs to consult it" — then the outline hierarchy as one run, then the rest.
            foreach (int n in pageTreeNodes.OrderBy(x => x))
                if (allObjects.ContainsKey(n) && placed.Add(n)) parts.Part9.Add(n);

            // Each generic category as one run (F.3.10: "objects within each of these categories
            // should be grouped together"), from the references that start it. A category placed
            // already (the outline under /UseOutlines, above) adds no run here.
            void Group(string key, IEnumerable<int> starts, List<int> alsoInto = null)
            {
                var run = new List<int>();
                foreach (int start in starts)
                    foreach (int n in walker.Ordered(start, blocked, part4))
                    {
                        if (!placed.Add(n)) continue;
                        run.Add(n);
                        parts.Part9.Add(n);
                    }
                if (run.Count == 0) return;
                alsoInto?.AddRange(run);
                parts.GenericGroups.Add((key, run));
            }

            // An extended category's references into the shared sections (Table F.10 item 7 on). The
            // sections are final here, so they are a set once: built per call, every embedded file
            // group paid for the whole first page — 13 s for twenty thousand of each, the product growing.
            var sharedSection = new HashSet<int>(parts.Part6.Concat(parts.Part8));
            List<int> SharedReferences(List<int> group)
            {
                var refs = new SortedSet<int>();
                foreach (int n in group)
                    if (allObjects.TryGetValue(n, out PdfObject obj))
                        foreach (int dep in Nested(obj, 0))
                            if (sharedSection.Contains(dep)) refs.Add(dep);
                return refs.ToList();
            }
            void SharedReferencesOf(string key)
            {
                var group = parts.GenericGroups.Find(g => g.Key == key).Objects;
                if (group == null) return;
                parts.GroupSharedReferences[key] = SharedReferences(group);
            }

            Group("O", References(rootDict?[PdfName.Outlines]), parts.OutlineGroup);
            Group("A", ThreadInformation(rootDict, allObjects));
            Group("E", References(rootDict?[PdfName.Dests]).Concat(NameTree(rootDict, allObjects, PdfName.Dests)));
            Group("I", References(doc.Trailer[PdfName.Info]));
            Group("L", References(rootDict?[PdfName.PageLabels]));
            Group("C", References(rootDict?[PdfName.StructTreeRoot]));
            SharedReferencesOf("C");
            Group("R", NameTree(rootDict, allObjects, PdfName.Renditions));
            SharedReferencesOf("R");
            if (formRun.Count > 0)
            {
                parts.GenericGroups.Add(("V", formRun));
                SharedReferencesOf("V");
            }

            // Thumbnails (F.4.4): each page's /Thumb image and the objects it alone references, one group
            // per page in page order, adjacent; then what two or more thumbnails reference and nothing else
            // does, as the thumbnail shared objects section. An object a thumbnail shares with anything
            // else is in neither, and a page whose image is shared has no entry.
            var thumbClosures = new List<(int PageIndex, List<int> Objects)>();
            for (int i = 0; i < pageObjects.Count; i++)
            {
                if (!(allObjects.TryGetValue(pageObjects[i], out PdfObject pageObj) && pageObj is PdfDictionary pageDict)) continue;
                if (!(pageDict[PdfName.Thumb] is PdfReference thumb) || !allObjects.ContainsKey(thumb.ReferenceObjectNumber)
                    || placed.Contains(thumb.ReferenceObjectNumber)) continue;
                var closure = walker.Ordered(thumb.ReferenceObjectNumber, blocked, part4)
                    .Where(n => !placed.Contains(n)).ToList();
                if (closure.Count > 0 && closure[0] == thumb.ReferenceObjectNumber) thumbClosures.Add((i, closure));
            }
            if (thumbClosures.Count > 0)
            {
                var thumbUse = new Dictionary<int, int>();
                foreach (var (_, objs) in thumbClosures)
                    foreach (int n in objs) { thumbUse.TryGetValue(n, out int c); thumbUse[n] = c + 1; }
                var thumbPages = new HashSet<int>(pageObjects);
                var elsewhere = new HashSet<int>();
                foreach (var kv in allObjects)
                {
                    if (thumbUse.ContainsKey(kv.Key)) continue;
                    // A page reaches its thumbnail through /Thumb, which is the one reference that does not count.
                    IEnumerable<int> refs = thumbPages.Contains(kv.Key) && kv.Value is PdfDictionary pd
                        ? pd.Where(e => e.Key.Value != "Thumb").SelectMany(e => Nested(e.Value, 0))
                        : Nested(kv.Value, 0);
                    foreach (int dep in refs) if (thumbUse.ContainsKey(dep)) elsewhere.Add(dep);
                }
                foreach (var (pageIndex, objs) in thumbClosures)
                {
                    if (thumbUse[objs[0]] != 1 || elsewhere.Contains(objs[0])) continue;
                    var run = new List<int>();
                    foreach (int n in objs)
                        if (thumbUse[n] == 1 && !elsewhere.Contains(n) && placed.Add(n)) { run.Add(n); parts.Part9.Add(n); }
                    parts.ThumbnailGroups.Add((pageIndex, run));
                }
                foreach (int n in thumbUse.Where(e => e.Value > 1 && !elsewhere.Contains(e.Key)).Select(e => e.Key).OrderBy(x => x))
                    if (placed.Add(n)) { parts.ThumbnailShared.Add(n); parts.Part9.Add(n); }
            }

            // Each embedded file stream not yet placed, as a group of its own (F.4.7): the stream and the
            // objects it references, adjacent. One the first page's section holds (F.3.7: everything its
            // page object reaches) is an entry of no objects whose one shared reference is the stream
            // (F.12 item 2: "only shared object references") — the shared object table lists the first
            // page's objects — after the groups that hold objects, so the table's first group, the one
            // its header locates, has some. One a later page's section holds is not named: the table
            // locates a group in part 9 or through the shared object table, which lists no later page's
            // objects, and a structure check counts the stream in that page's section, so it cannot move.
            var embeddedByReference = EmbeddedFileStreams.ReferencedBy(allObjects);
            bool IsEmbeddedFile(int n) => allObjects.TryGetValue(n, out PdfObject o) && o is PdfStream
                && (EmbeddedFileStreams.NamesItself(o) || embeddedByReference.Contains(n));
            var inFirstPage = new List<(int Stream, List<int> Objects, List<int> Shared)>();
            foreach (var entry in allObjects.OrderBy(e => e.Key))
            {
                if (!IsEmbeddedFile(entry.Key) || part4.Contains(entry.Key)) continue;
                if (inPart6.Contains(entry.Key))
                {
                    inFirstPage.Add((entry.Key, new List<int>(), new List<int> { entry.Key }));
                    continue;
                }
                if (placed.Contains(entry.Key)) continue;
                var run = new List<int>();
                foreach (int n in walker.Ordered(entry.Key, blocked, part4))
                {
                    if (!placed.Add(n)) continue;
                    run.Add(n);
                    parts.Part9.Add(n);
                }
                if (run.Count == 0 || run[0] != entry.Key) continue;
                parts.EmbeddedFileGroups.Add((entry.Key, run, SharedReferences(run)));
            }
            parts.EmbeddedFileGroups.AddRange(inFirstPage);

            foreach (int n in allObjects.Keys.OrderBy(x => x))
                if (placed.Add(n)) parts.Part9.Add(n);

            parts.EdgesExamined = walker.EdgesExamined;
            return parts;
        }

        /// <summary>The object an entry refers to, when it is a reference.</summary>
        private static IEnumerable<int> References(PdfObject value)
        {
            if (value is PdfReference r) yield return r.ReferenceObjectNumber;
        }

        /// <summary>The article threads' information dictionaries (each thread's <c>/I</c>), when indirect.</summary>
        private static IEnumerable<int> ThreadInformation(PdfDictionary catalog, Dictionary<int, PdfObject> all)
        {
            PdfObject threads = catalog?[PdfName.Threads];
            if (threads is PdfReference tr && all.TryGetValue(tr.ReferenceObjectNumber, out PdfObject resolved)) threads = resolved;
            if (threads is not PdfArray array) yield break;
            foreach (PdfObject entry in array)
            {
                PdfObject thread = entry is PdfReference r && all.TryGetValue(r.ReferenceObjectNumber, out PdfObject t) ? t : entry;
                if (thread is PdfDictionary d && d[PdfName.I] is PdfReference info) yield return info.ReferenceObjectNumber;
            }
        }

        /// <summary>A <c>/Names</c> dictionary category's name tree (<c>/Dests</c>, <c>/Renditions</c>), when indirect.</summary>
        private static IEnumerable<int> NameTree(PdfDictionary catalog, Dictionary<int, PdfObject> all, PdfName category)
        {
            PdfObject names = catalog?[PdfName.Names];
            if (names is PdfReference nr && all.TryGetValue(nr.ReferenceObjectNumber, out PdfObject resolved)) names = resolved;
            if (names is PdfDictionary d && d[category] is PdfReference tree) yield return tree.ReferenceObjectNumber;
        }

        /// <summary>
        /// The catalogue entries F.3.5 requires in part 4 when they are indirect. <c>/Encrypt</c> is
        /// absent because linearization fails closed on an encrypted document.
        /// </summary>
        private static readonly PdfName[] DocumentLevelKeys =
        {
            PdfName.ViewerPreferences, PdfName.Threads, PdfName.OpenAction, PdfName.AcroForm,
        };

        /// <summary>Keys a page closure does not follow.</summary>
        /// <remarks>
        /// <c>/Parent</c> is redundant with the page-tree exclusion and kept for the case of a
        /// parent that does not declare <c>/Type /Pages</c>; <c>/Thumb</c> is F.3.7's own exception,
        /// and its target belongs in part 9 with the other thumbnails.
        /// </remarks>
        private static bool IsBlockedKey(PdfName key)
            => key.Value == "Parent" || key.Value == "Thumb";

        /// <summary>
        /// Every closure walk of one <see cref="Build"/>: each object's references read once, de-duplicated
        /// and kept, and every reference a walk examines charged to
        /// <see cref="PdfLimits.MaxLinearizationEdges"/>.
        /// </summary>
        /// <remarks>
        /// Each walk starts fresh — a page's closure stops at page boundaries, so what one page reaches says
        /// nothing about the next — and what a walk costs is the references of every object it reaches.
        /// Read again by every walk, pages sharing an array of fifty thousand references to one object would
        /// examine fifty thousand each and collect one (2 000 pages: 24 s) — the de-duplication answers that;
        /// pages sharing an array naming every page would examine pages² and collect nothing (4 000 pages:
        /// 4.7 s) — the ceiling answers that.
        /// </remarks>
        private sealed class Walker
        {
            private readonly Dictionary<int, PdfObject> _all;
            private readonly Dictionary<int, int[]> _references = new Dictionary<int, int[]>();
            private readonly Dictionary<int, int[]> _rootReferences = new Dictionary<int, int[]>();
            private long _edgesLeft = PdfLimits.MaxLinearizationEdges;

            internal Walker(Dictionary<int, PdfObject> all) { _all = all; }

            /// <summary>References examined so far — the evidence that a repeated reference is read once.</summary>
            internal long EdgesExamined { get; private set; }

            /// <summary>
            /// What <paramref name="root"/> reaches without crossing <paramref name="blocked"/>, in discovery
            /// order, the root itself not included.
            /// </summary>
            /// <remarks>
            /// Breadth-first, which for a page lands close to F.3.7's recommended order without modelling
            /// it: the page's own keys (<c>/Annots</c>, <c>/Resources</c>, <c>/Contents</c>) come before
            /// anything they in turn reference. For an outline it is a preorder walk of the hierarchy,
            /// which is what F.3.10 asks for. The root's own <c>/Parent</c> and <c>/Thumb</c> are not
            /// followed (<see cref="IsBlockedKey"/>).
            /// </remarks>
            internal List<int> Closure(int root, HashSet<int> blocked)
            {
                var order = new List<int>();
                var queue = new Queue<int>();
                var seen = new HashSet<int> { root };
                queue.Enqueue(root);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();
                    int[] references = ReferencesOf(current, current == root);
                    if (references == null) continue;

                    _edgesLeft -= references.Length;
                    EdgesExamined += references.Length;
                    if (_edgesLeft < 0)
                        throw new Core.PdfException(
                            "Cannot linearize: dividing the document into its parts examines more than "
                            + PdfLimits.MaxLinearizationEdges.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " references (PdfLimits.MaxLinearizationEdges).");

                    foreach (int dep in references)
                    {
                        // A reference to an object that is not in the file is dropped here rather than
                        // carried: a closure holding a number nothing defines would be given a place in
                        // the new numbering and no bytes, and the gap it left in the cross-reference
                        // table would split the single subsection Annex F requires.
                        if (blocked.Contains(dep) || !_all.ContainsKey(dep) || !seen.Add(dep)) continue;
                        order.Add(dep);
                        queue.Enqueue(dep);
                    }
                }

                return order;
            }

            /// <summary>
            /// <see cref="Closure"/> with the root first, and part 4's objects left out of the list (they are
            /// walked through, not placed again).
            /// </summary>
            internal List<int> Ordered(int root, HashSet<int> blocked, HashSet<int> part4)
            {
                var result = new List<int>();
                if (_all.ContainsKey(root) && !part4.Contains(root)) result.Add(root);
                foreach (int n in Closure(root, blocked))
                    if (!part4.Contains(n)) result.Add(n);
                return result;
            }

            /// <summary>An object's distinct references, read once; null when the file does not define it.</summary>
            private int[] ReferencesOf(int number, bool isRoot)
            {
                Dictionary<int, int[]> cache = isRoot ? _rootReferences : _references;
                if (cache.TryGetValue(number, out int[] known)) return known;
                if (!_all.TryGetValue(number, out PdfObject obj)) return null;

                var distinct = new HashSet<int>();
                var list = new List<int>();
                foreach (int n in References(obj, isRoot))
                    if (distinct.Add(n)) list.Add(n);
                int[] references = list.ToArray();
                cache[number] = references;
                return references;
            }
        }

        private static IEnumerable<int> References(PdfObject obj, bool isPageDictionary)
        {
            if (isPageDictionary && obj is PdfDictionary page)
            {
                foreach (var kvp in page)
                {
                    if (IsBlockedKey(kvp.Key)) continue;
                    foreach (int n in Nested(kvp.Value, 0)) yield return n;
                }
                yield break;
            }

            foreach (int n in Nested(obj, 0)) yield return n;
        }

        private static IEnumerable<int> Nested(PdfObject obj, int depth)
        {
            if (depth > PdfLimits.MaxObjectGraphDepth) yield break;
            if (obj is PdfReference r) { yield return r.ReferenceObjectNumber; yield break; }
            if (obj is PdfArray arr)
            {
                foreach (PdfObject item in arr)
                    foreach (int n in Nested(item, depth + 1)) yield return n;
            }
            else if (obj is PdfDictionary dict)
            {
                foreach (var kvp in dict)
                    foreach (int n in Nested(kvp.Value, depth + 1)) yield return n;
            }
        }
    }
}
