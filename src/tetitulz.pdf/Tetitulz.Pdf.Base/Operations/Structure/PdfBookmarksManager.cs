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
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Reads and writes the /Outlines (bookmarks) tree of a PDF.
    /// </summary>
    public static class PdfBookmarksManager
    {
        // ----------------------------------------------------------------
        //  Read
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the flat list of bookmarks (top-level outline items) from the document.
        /// Returns an empty list if the document has no bookmarks.
        /// </summary>
        public static IReadOnlyList<PdfBookmark> ReadBookmarks(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the bookmarks", "PdfOperations.GetBookmarks(Stream, string)");
            return ReadBookmarks(doc);
        }

        [ThreadStatic] private static string _lastTruncation;

        /// <summary>
        /// Why the most recent read <b>on this thread</b> returned an incomplete outline — the items
        /// below <see cref="PdfLimits.MaxTreeDepth"/> levels were not visited, or an item named an
        /// object the parser could not read and the items after it were not — or null when it is
        /// complete. Cleared at the start of every read. A caller writing the outline back with
        /// <see cref="WriteBookmarks"/> while this is set replaces the whole with the part it read.
        /// </summary>
        public static string LastTruncation => _lastTruncation;

        internal static IReadOnlyList<PdfBookmark> ReadBookmarks(PdfDocument doc)
        {
            _lastTruncation = null;
            if (doc.Catalog?.Dictionary == null) return Array.AsReadOnly(new PdfBookmark[0]);

            // Build named-destination index once so every outline item can resolve a
            // named-dest bookmark in O(1) without re-walking the name tree each time.
            var namedDests = BuildNamedDestIndex(doc);

            // The tree, rebuilt from the one walk's reading order and depths: an item at depth d is
            // the last child of the last item seen at depth d − 1, so the lists down the path from
            // the top to the last item are the ones still open.
            var top = new List<PdfBookmark>();
            var open = new List<List<PdfBookmark>>();   // open[d]: the children of the last item at depth d
            OutlineItems.Walk(doc, doc.Catalog.Dictionary, (item, depth) =>
            {
                var children = new List<PdfBookmark>();
                (depth == 0 ? top : open[depth - 1]).Add(ReadItem(item, doc, depth, children, namedDests));
                while (open.Count > depth) open.RemoveAt(open.Count - 1);
                open.Add(children);
                return true;
            }, cut => _lastTruncation = cut);
            return top;
        }

        /// <summary>One outline item as a bookmark; <paramref name="children"/> is filled by the walk after it.</summary>
        private static PdfBookmark ReadItem(PdfDictionary itemDict, PdfDocument doc, int depth, List<PdfBookmark> children,
            Dictionary<string, NamedDestination> namedDests)
        {
            string title = GetStringValue(itemDict, PdfName.Title, doc);
            int? pageNumber = ResolvePageNumber(itemDict, doc, namedDests, out PdfArray destination);
            float? top = pageNumber.HasValue ? PdfDestinationPage.TopAsFloat(doc, destination) : null;

            // Table 153: a positive /Count is an open item, a negative or absent one closed. Table
            // 154: /F bit 1 italic, bit 2 bold — a flag word, so its low 32 bits.
            bool isOpen = doc.ResolveObject(itemDict[PdfName.Count]) is PdfNumber count
                          && count.IsInteger && count.LongValue > 0;
            int flags = (doc.ResolveObject(itemDict[PdfName.F]) as PdfNumber)?.FlagWord ?? 0;

            return new PdfBookmark(title, pageNumber, depth, children, top, isOpen, ReadColor(itemDict, doc),
                isBold: (flags & 2) != 0, isItalic: (flags & 1) != 0);
        }

        private static Dictionary<string, NamedDestination> BuildNamedDestIndex(PdfDocument doc)
        {
            var map = new Dictionary<string, NamedDestination>(StringComparer.Ordinal);
            foreach (var nd in PdfNamedDestinations.CollectDestinations(doc))
                if (nd.PageIndex >= 0 && !map.ContainsKey(nd.Name))
                    map[nd.Name] = nd;
            return map;
        }

        // ----------------------------------------------------------------
        //  Write / replace
        // ----------------------------------------------------------------

        /// <summary>
        /// Replaces the entire /Outlines tree with the supplied list of bookmarks
        /// (top-level only; child items are written recursively).
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF.</param>
        /// <param name="output">Writable stream for the updated PDF.</param>
        /// <param name="bookmarks">Top-level bookmark list to write.</param>
        public static void WriteBookmarks(
            Stream input, Stream output,
            IReadOnlyList<PdfBookmark> bookmarks)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (bookmarks == null) throw new ArgumentNullException(nameof(bookmarks));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRevision.Refuse(doc, "write the bookmarks of", "the outline");

            // Refused before anything is built: over a direct /Root the outline was written with
            // nothing pointing to it, and the call returned as if it had worked.
            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the bookmarks");

            int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();
            var objects = new List<IndirectObject>();

            // The outline root's object number: the existing root's, superseded at its own generation
            // and named by the items at it (the incremental writer keeps a superseded object's
            // generation, so a /Parent at generation 0 would name no object), else a new one.
            int outlineRootObjNum, outlineRootGeneration;
            PdfReference outlineRootRef;
            var existingOutlineRef = doc.Catalog.Dictionary[PdfName.Outlines] as PdfReference;
            if (existingOutlineRef != null)
            {
                outlineRootObjNum = existingOutlineRef.ReferenceObjectNumber;
                outlineRootGeneration = ExistingObjectGeneration.Of(doc.CrossReferenceTable, outlineRootObjNum, existingOutlineRef);
                outlineRootRef = ExistingObjectGeneration.ReferenceTo(doc.CrossReferenceTable, outlineRootObjNum);
            }
            else
            {
                outlineRootObjNum = ++maxObjNum;
                outlineRootGeneration = 0;
                outlineRootRef = new PdfReference(outlineRootObjNum, 0);
            }

            // Build all outline item objects and collect them.
            int totalCount = 0;
            int firstChildObjNum = -1;
            int lastChildObjNum = -1;

            if (bookmarks.Count > 0)
            {
                // Allocate object numbers for all items up front.
                var allItems = FlattenItems(bookmarks);
                var objNums = new int[allItems.Count];
                for (int i = 0; i < allItems.Count; i++)
                    objNums[i] = ++maxObjNum;

                // Build item dictionaries.
                BuildItemObjects(
                    allItems, objNums, outlineRootRef, doc,
                    objects, out firstChildObjNum, out lastChildObjNum, out totalCount);
            }

            // Build the outline root.
            var outlineRoot = new PdfDictionary();
            outlineRoot.Set(PdfName.Type, PdfName.Outlines);
            if (firstChildObjNum > 0)
            {
                outlineRoot.Set(PdfName.First, new PdfReference(firstChildObjNum, 0));
                outlineRoot.Set(PdfName.Last, new PdfReference(lastChildObjNum, 0));
                outlineRoot.Set(PdfName.Count, new PdfNumber(totalCount));
            }
            outlineRoot.ObjectNumber = outlineRootObjNum;
            outlineRoot.GenerationNumber = outlineRootGeneration;
            objects.Add(new IndirectObject(outlineRootObjNum, outlineRootGeneration, outlineRoot));

            // Update catalog if /Outlines reference is new or changed, through the trailer's /Root
            // reference below (/Root is a *trailer* key; a catalog has none).
            if (existingOutlineRef == null)
            {
                // Need to update the catalog to add /Outlines reference.
                var updatedCatalog = CloneDictionary(doc.Catalog.Dictionary);
                updatedCatalog.Set(PdfName.Outlines, new PdfReference(outlineRootObjNum, 0));
                updatedCatalog.ObjectNumber = catalogObjNum;
                updatedCatalog.GenerationNumber = 0;
                objects.Add(new IndirectObject(catalogObjNum, 0, updatedCatalog));
            }

            int newSize = maxObjNum + 1;
            var trailer = BuildTrailer(doc.Trailer, newSize);
            ms.Position = 0;
            IncrementalUpdateWriter.WriteUpdate(
                ms, output, objects, trailer,
                doc.Parser.StartXrefOffset,
                newSize);
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        private static void BuildItemObjects(
            List<(PdfBookmark item, int parentIndex)> allItems,
            int[] objNums,
            PdfReference outlineRootRef,
            PdfDocument doc,
            List<IndirectObject> objects,
            out int firstTopObjNum,
            out int lastTopObjNum,
            out int totalTopCount)
        {
            firstTopObjNum = -1;
            lastTopObjNum = -1;
            totalTopCount = 0;

            // We need to find first/last children for each parent and assign
            // /Prev, /Next, /First, /Last, /Parent, /Count correctly.
            // allItems is a list of (bookmark, parentIndex) where parentIndex == -1 means top-level.

            // Group by parent index, and record each item's position within its sibling list in
            // the same pass, not with siblings.IndexOf(i) inside the per-item loop below — a linear
            // scan per item, O(n^2) comparisons for a flat outline (~1.25 x 10^9 for a 50 000-entry
            // import). It is known here for free: the item is being appended, so its index is the count before the append.
            //
            // The pass this replaces also filled childrenByParent[-1] with one -999 placeholder
            // per item and then immediately Clear()ed the dictionary and rebuilt it — a wasted
            // allocation pass on every call, and a list of sentinels that never meant anything.
            var childrenByParent = new Dictionary<int, List<int>>(); // parentIndex -> indices into allItems
            var posInParent = new int[allItems.Count];
            for (int i = 0; i < allItems.Count; i++)
            {
                int parentIdx = allItems[i].parentIndex;
                if (!childrenByParent.TryGetValue(parentIdx, out var siblingList))
                    childrenByParent[parentIdx] = siblingList = new List<int>();
                posInParent[i] = siblingList.Count;
                siblingList.Add(i);
            }

            // Build dict for each item.
            var builtDicts = new PdfDictionary[allItems.Count];
            for (int i = 0; i < allItems.Count; i++)
                builtDicts[i] = new PdfDictionary();

            // The descendants each item shows when open (Table 153's /Count): its children, and an
            // open child's own. The list is in pre-order, so walking it backwards finds every child's
            // figure before its parent's.
            var visible = new int[allItems.Count];
            for (int i = allItems.Count - 1; i >= 0; i--)
                if (childrenByParent.TryGetValue(i, out var kids))
                    foreach (int k in kids)
                        visible[i] += 1 + (allItems[k].item.IsOpen ? visible[k] : 0);

            for (int i = 0; i < allItems.Count; i++)
            {
                var (bm, parentIdx) = allItems[i];
                var dict = builtDicts[i];

                // /Title
                dict.Set(PdfName.Title, new PdfString(bm.Title ?? string.Empty));

                // /Parent: the root at its own generation, a new item at 0.
                dict.Set(PdfName.Parent, parentIdx < 0 ? outlineRootRef : new PdfReference(objNums[parentIdx], 0));

                // /Dest — page number (0-based) -> [page /XYZ null null null]. A page the document
                // does not have gets no destination; one with no object number of its own is named
                // by its index, as every other destination writer names it.
                if (bm.PageNumber.HasValue)
                {
                    int pageNum = bm.PageNumber.Value;
                    if (pageNum >= 0 && pageNum < doc.Pages.Count)
                        dict.Set(PdfName.Dest, XyzDestination.Build(
                            XyzDestination.PageOf(doc, pageNum), left: null, top: bm.Top, zoom: null));
                }

                // /C and /F (Tables 153, 154): written only when they say something, as a reader
                // takes their absence for black and plain.
                if (bm.Color != null)
                    dict.Set(PdfName.C, PdfArray.OfNumbers(bm.Color[0], bm.Color[1], bm.Color[2]));
                int flags = (bm.IsItalic ? 1 : 0) | (bm.IsBold ? 2 : 0);
                if (flags != 0) dict.Set(PdfName.F, new PdfNumber(flags));

                // /Prev and /Next within siblings. The position came from the grouping pass; every
                // item was appended to its parent's list there, so the key is always present.
                var siblings = childrenByParent[parentIdx];
                int posInSiblings = posInParent[i];
                if (posInSiblings > 0)
                    dict.Set(PdfName.Prev, new PdfReference(objNums[siblings[posInSiblings - 1]], 0));
                if (posInSiblings < siblings.Count - 1)
                    dict.Set(PdfName.Next, new PdfReference(objNums[siblings[posInSiblings + 1]], 0));

                // /First and /Last for children.
                if (childrenByParent.ContainsKey(i) && childrenByParent[i].Count > 0)
                {
                    var myChildren = childrenByParent[i];
                    dict.Set(PdfName.First, new PdfReference(objNums[myChildren[0]], 0));
                    dict.Set(PdfName.Last, new PdfReference(objNums[myChildren[myChildren.Count - 1]], 0));
                    // Positive for an open item, negative for a closed one (Table 153), so a round
                    // trip keeps open the outline the author left open.
                    dict.Set(PdfName.Count, new PdfNumber(bm.IsOpen ? visible[i] : -visible[i]));
                }

                dict.ObjectNumber = objNums[i];
                dict.GenerationNumber = 0;

                objects.Add(new IndirectObject(objNums[i], 0, dict));
            }

            // Top-level items (parentIdx == -1).
            if (childrenByParent.TryGetValue(-1, out var topItems) && topItems.Count > 0)
            {
                firstTopObjNum = objNums[topItems[0]];
                lastTopObjNum = objNums[topItems[topItems.Count - 1]];
                // Table 152: the root's /Count is every item shown — the top level and what the
                // open ones show beneath them.
                foreach (int t in topItems)
                    totalTopCount += 1 + (allItems[t].item.IsOpen ? visible[t] : 0);
            }
        }

        /// <summary>
        /// Flattens the bookmark tree into a list of (item, parentIndex) pairs.
        /// parentIndex == -1 means top-level.
        /// </summary>
        private static List<(PdfBookmark item, int parentIndex)> FlattenItems(
            IReadOnlyList<PdfBookmark> bookmarks)
        {
            var result = new List<(PdfBookmark, int)>();
            FlattenRecursive(bookmarks, -1, result);
            return result;
        }

        private static void FlattenRecursive(
            IReadOnlyList<PdfBookmark> items,
            int parentIndex,
            List<(PdfBookmark, int)> result)
        {
            foreach (var bm in items)
            {
                int myIndex = result.Count;
                result.Add((bm, parentIndex));
                if (bm.Children != null && bm.Children.Count > 0)
                    FlattenRecursive(bm.Children, myIndex, result);
            }
        }

        private static PdfDictionary ResolveOutlineRoot(PdfDocument doc)
        {
            var outlinesRef = doc.Catalog?.Dictionary[PdfName.Outlines] as PdfReference;
            if (outlinesRef == null) return null;
            return doc.ResolveObject(outlinesRef) as PdfDictionary;
        }

        /// <summary>
        /// An item's <c>/C</c> as red, green and blue — null unless it is three finite numbers, each
        /// clamped to 0–1, so a file's colour never reaches the bookmark's constructor as a refusal.
        /// </summary>
        private static float[] ReadColor(PdfDictionary itemDict, PdfDocument doc)
        {
            if (!(doc.ResolveObject(itemDict[PdfName.C]) is PdfArray c) || c.Count != 3) return null;
            var rgb = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!(doc.ResolveObject(c[i]) is PdfNumber n)) return null;
                double v = n.DoubleValue;
                if (double.IsNaN(v) || double.IsInfinity(v)) return null;
                // Clamped before the cast: a real finite as a double (10^40) is infinite as a float,
                // and the constructor would refuse the file's bookmark for it.
                rgb[i] = (float)Math.Max(0, Math.Min(1, v));
            }
            return rgb;
        }

        /// <summary>
        /// The item's page, and in <paramref name="destination"/> the explicit destination array that
        /// names it (its own, or the named destination's) — null when there is none.
        /// </summary>
        private static int? ResolvePageNumber(PdfDictionary itemDict, PdfDocument doc,
            Dictionary<string, NamedDestination> namedDests, out PdfArray destination)
        {
            destination = null;
            var destObj = itemDict[PdfName.Dest];

            // Many PDFs use /A << /S /GoTo /D [...] >> instead of a direct /Dest entry.
            if (destObj == null)
            {
                var action = doc.ResolveObject(itemDict[PdfName.A]) as PdfDictionary;
                if (action != null && (doc.ResolveObject(action[PdfName.S]) as PdfName)?.Value == "GoTo")
                    destObj = action[PdfName.D];
            }

            if (destObj == null) return null;

            PdfObject resolved = doc.ResolveObject(destObj);

            // Explicit destination array: [pageRef /XYZ ...] or [pageIndex /Fit ...]
            // One implementation, in Operations.PdfDestinationPage. An explicit destination that
            // names no page yields null here rather than falling through to the named-destination
            // lookup below.
            if (resolved is PdfArray arr && arr.Count > 0)
            {
                int index = PdfDestinationPage.IndexFromDestArray(doc, arr);
                if (index < 0) return null;
                destination = arr;
                return index;
            }

            // Named destination: a string or name that indexes into /Names /Dests or /Dests.
            string name = (resolved as PdfName)?.Value
                          ?? (resolved as PdfString)?.GetText()
                          ?? (destObj as PdfName)?.Value
                          ?? (destObj as PdfString)?.GetText();
            if (name != null && namedDests.TryGetValue(name, out NamedDestination named))
            {
                destination = named.DestinationArray;
                return named.PageIndex;
            }

            return null;
        }

        /// <remarks>
        /// <c>doc</c> is a parameter because the raw read here was <b>destructive over a round
        /// trip</b>, not merely lossy. A null title makes <c>ReadBookmarks</c> return a blank
        /// sidebar — and <c>WriteBookmarks</c> replaces the ENTIRE <c>/Outlines</c> tree from the
        /// supplied list, so a read-edit-write cycle writes the blanks back and the titles are gone
        /// from the document. The caller sees an outline of empty entries and no error.
        ///
        /// <para>The call site is one line above <c>ResolvePageNumber</c>, which resolves
        /// <c>/A</c>, <c>/S</c>, <c>/D</c> and <c>/Dest</c> properly.</para>
        /// </remarks>
        private static string GetStringValue(PdfDictionary dict, PdfName key, PdfDocument doc)
        {
            var obj = doc != null ? doc.ResolveObject(dict[key]) : dict[key];
            return (obj as PdfString)?.GetText();
        }

        // One implementation, in Core.Objects.PdfDictionary.CloneShallow.
        private static PdfDictionary CloneDictionary(PdfDictionary source) => PdfDictionary.CloneShallow(source);

        // One implementation, in Core.Writing.PdfTrailerBuilder.
        private static PdfDictionary BuildTrailer(PdfDictionary original, int newSize)
            => PdfTrailerBuilder.ForUpdate(original, newSize);
    }

    /// <summary>
    /// Represents a single bookmark (outline item) in a PDF document.
    /// </summary>
    public sealed class PdfBookmark
    {
        /// <summary>Bookmark display text.</summary>
        public string Title { get; }

        /// <summary>
        /// Zero-based page number this bookmark links to, or null if the destination
        /// is a named destination or cannot be resolved.
        /// </summary>
        public int? PageNumber { get; }

        /// <summary>Nesting depth (0 = top-level).</summary>
        public int Depth { get; }

        /// <summary>Child bookmarks.</summary>
        public IReadOnlyList<PdfBookmark> Children { get; }

        /// <summary>
        /// Where on <see cref="PageNumber"/> the bookmark lands: the destination's top in user space
        /// (<c>/XYZ</c>, <c>/FitH</c>, <c>/FitBH</c>, <c>/FitR</c>), or null for the page's top.
        /// </summary>
        public float? Top { get; }

        /// <summary>Whether the item's children are shown: a positive <c>/Count</c> (ISO 32000-1 Table 153).</summary>
        public bool IsOpen { get; }

        /// <summary>
        /// The title's colour, <c>/C</c>: red, green and blue from 0 to 1, or null for the viewer's own
        /// (an entry that is not three numbers is ignored; each number is clamped).
        /// </summary>
        public IReadOnlyList<float> Color { get; }

        /// <summary>The title is bold: <c>/F</c> bit 2 (Table 154).</summary>
        public bool IsBold { get; }

        /// <summary>The title is italic: <c>/F</c> bit 1 (Table 154).</summary>
        public bool IsItalic { get; }

        public PdfBookmark(string title, int? pageNumber, int depth,
            IReadOnlyList<PdfBookmark> children = null)
            : this(title, pageNumber, depth, children, top: null, isOpen: false, color: null,
                isBold: false, isItalic: false)
        {
        }

        /// <summary>A bookmark with its position, open state and title style — what a viewer shows and the writer writes back.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="top"/> is NaN or infinite.</exception>
        /// <exception cref="ArgumentException"><paramref name="color"/> is not three finite numbers.</exception>
        public PdfBookmark(string title, int? pageNumber, int depth, IReadOnlyList<PdfBookmark> children,
            float? top, bool isOpen, IReadOnlyList<float> color, bool isBold, bool isItalic)
        {
            if (top.HasValue) Tetitulz.Pdf.Core.Util.NumericArguments.RequireFinite(top.Value, nameof(top));
            if (color != null)
            {
                if (color.Count != 3)
                    throw new ArgumentException("A bookmark colour is three numbers: red, green and blue.", nameof(color));
                var rgb = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    Tetitulz.Pdf.Core.Util.NumericArguments.RequireFinite(color[i], nameof(color));
                    rgb[i] = Math.Max(0f, Math.Min(1f, color[i]));
                }
                color = Array.AsReadOnly(rgb);
            }
            Title = title;
            PageNumber = pageNumber;
            Depth = depth;
            Children = children ?? Array.AsReadOnly(new PdfBookmark[0]);
            Top = top;
            IsOpen = isOpen;
            Color = color;
            IsBold = isBold;
            IsItalic = isItalic;
        }
    }
}

