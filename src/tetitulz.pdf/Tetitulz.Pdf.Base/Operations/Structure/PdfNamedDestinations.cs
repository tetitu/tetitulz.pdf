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
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Reads and writes named destinations in a PDF.
    ///
    /// <para>
    /// Named destinations are stored in the document catalog in two possible locations:
    /// <list type="number">
    ///   <item>
    ///     <term>/Dests dictionary (PDF 1.1, obsolete but still common)</term>
    ///     <description>
    ///       <c>/Dests &lt;&lt; /name [pageRef /XYZ left top zoom] ... &gt;&gt;</c>
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>/Names /Dests name tree (PDF 1.2+)</term>
    ///     <description>
    ///       <c>/Names &lt;&lt; /Dests &lt;&lt; /Names [(name) [pageRef /XYZ ...] ...] &gt;&gt; &gt;&gt;</c>
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    ///
    /// This class reads both locations and writes to the /Names /Dests name tree (preferred).
    /// </summary>
    public static class PdfNamedDestinations
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Why the last <c>ListDestinations</c> on this thread returned fewer destinations
        /// than the document holds; <c>null</c> when it read them all.
        /// </summary>
        /// <remarks>
        /// <para>The <c>/Dests</c> name tree's walk stops at <see cref="PdfLimits.MaxNameTreeEntries"/>
        /// entries or <see cref="PdfLimits.MaxTreeDepth"/> levels. <see cref="SetDestination"/> and
        /// <see cref="RemoveDestination"/> refuse when it does, because a rewrite from a partial read
        /// destroys what was not read; the lister returned what it had and said nothing, so a list
        /// cut short looked exactly like a document with fewer destinations.</para>
        /// <para><c>[ThreadStatic]</c>: read it on the thread that made the call.</para>
        /// </remarks>
        public static string LastListError
        {
            get => _lastListError;
            private set => _lastListError = value;
        }

        [ThreadStatic]
        private static string _lastListError;

        /// <summary>
        /// Returns all named destinations in the document.
        /// Reads both the legacy /Dests dictionary and the /Names /Dests name tree.
        /// </summary>
        /// <remarks>A list cut short by a tree limit is reported through <see cref="LastListError"/>.</remarks>
        /// <exception cref="Core.PdfEncryptedDocumentException">
        /// The document is encrypted: it exposes no catalog, and an empty list would claim a
        /// document without destinations. Use <see cref="ListDestinations(Stream, string)"/>.
        /// </exception>
        public static IReadOnlyList<NamedDestination> ListDestinations(Stream input)
        {
            LastListError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            EncryptedRead.Refuse(doc, "the named destinations", "ListDestinations(Stream, string)");
            return List(doc);
        }

        /// <summary>
        /// <see cref="ListDestinations(Stream)"/> for a document that may be encrypted, opened with
        /// <paramref name="password"/> (user or owner; ignored when the document is not encrypted).
        /// </summary>
        /// <remarks>
        /// Decrypts through <c>Base.Encryption.PdfDecryptedInput</c>, so
        /// <c>PdfEncryptionOperations.Register()</c> must have run.
        /// </remarks>
        public static IReadOnlyList<NamedDestination> ListDestinations(Stream input, string password)
        {
            LastListError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var decrypted = Encryption.PdfDecryptedInput.OpenDecrypted(input, password);
            using var doc = PdfDocument.Open(decrypted);
            return List(doc);
        }

        private static IReadOnlyList<NamedDestination> List(PdfDocument doc)
        {
            var destinations = CollectDestinations(doc, out bool complete);
            if (!complete)
                LastListError = "the /Dests name tree exceeds the entry budget or the depth limit, " +
                                "so the list of destinations is incomplete";
            return destinations;
        }

        /// <summary>
        /// Adds or replaces a named destination that points to a specific page.
        /// Writes to the /Names /Dests name tree (flat leaf node).
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Output PDF stream.</param>
        /// <param name="name">Destination name (must be unique; replaces any existing entry with same name).</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="left">X position in points (PDF user space). Null = inherit current.</param>
        /// <param name="top">Y position in points (PDF user space). Null = inherit current.</param>
        /// <param name="zoom">Zoom factor (1.0 = 100 %). Null = inherit current.</param>
        public static void SetDestination(
            Stream input, Stream output,
            string name, int pageIndex,
            double? left = null, double? top = null, double? zoom = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (name == null) throw new ArgumentNullException(nameof(name));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            List<IndirectObject> objects = SetDestinationObjects(doc, ref nextObjNum, name, pageIndex, left, top, zoom);
            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
        }

        /// <summary>
        /// The objects <see cref="SetDestination"/> appends, without writing them: for a caller that
        /// writes the revision itself — <c>Tetitulz.Pdf.Encryption.PdfEncryptedDestinations</c>, which
        /// plans against a decrypted copy and appends to the encrypted original.
        /// </summary>
        /// <param name="document">The document to plan against; not an encrypted one.</param>
        /// <param name="firstFreeObjectNumber">The first object number the revision may allocate.</param>
        /// <param name="name">Destination name; replaces any existing entry of that name.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="left">X position in points. Null = inherit current.</param>
        /// <param name="top">Y position in points. Null = inherit current.</param>
        /// <param name="zoom">Zoom factor. Null = inherit current.</param>
        /// <exception cref="Core.PdfEncryptedDocumentException"><paramref name="document"/> is encrypted.</exception>
        public static IReadOnlyList<IndirectObject> PlanSetDestination(
            PdfDocument document, int firstFreeObjectNumber,
            string name, int pageIndex,
            double? left = null, double? top = null, double? zoom = null)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (name == null) throw new ArgumentNullException(nameof(name));
            int next = firstFreeObjectNumber;
            return SetDestinationObjects(document, ref next, name, pageIndex, left, top, zoom);
        }

        private static List<IndirectObject> SetDestinationObjects(
            PdfDocument doc, ref int nextObjNum, string name, int pageIndex,
            double? left, double? top, double? zoom)
        {
            RefuseEncrypted(doc, "add a named destination to");

            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            // [page /XYZ left top zoom], null for "keep the viewer's current value".
            var destArr = XyzDestination.Build(XyzDestination.PageOf(doc, pageIndex), left, top, zoom);

            var objects = new List<IndirectObject>();

            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the named destination");

            var catalog = doc.Catalog.Dictionary;

            // The whole /Dests name tree, read before anything is written: rewriting it from a
            // partial read would destroy the entries the read did not reach, so a truncated walk
            // refuses here instead.
            var entries = CatalogNameTree.ReadForRewrite(doc, catalog, PdfName.Dests);

            // "Replaces any existing entry with same name" has to mean both stores, or it is not
            // a replace. A name held by the legacy catalog /Dests dictionary (§12.3.2.3) is not
            // reachable from the name tree written below, so leaving it in place produced two
            // live destinations under one name — ListDestinations returned the name twice and a
            // viewer resolved whichever store it consulted first.
            PdfDictionary updatedCatalog = null;
            RemoveFromLegacyDests(doc, catalog, name, objects, ref updatedCatalog);

            entries.RemoveAll(e => e.Text == name);
            entries.Add(new CatalogNameTree.Entry(new PdfString(name), destArr));
            updatedCatalog = CatalogNameTree.Write(doc, catalog, updatedCatalog,
                PdfName.Dests, entries, objects, ref nextObjNum);

            if (updatedCatalog != null)
                objects.Add(new IndirectObject(catalogObjNum, 0, updatedCatalog));
            return objects;
        }

        /// <summary>
        /// Removes a named destination by name.
        /// Searches both the /Dests legacy dict and the /Names /Dests name tree.
        /// </summary>
        /// <returns>True if the destination was found and removed.</returns>
        public static bool RemoveDestination(Stream input, Stream output, string name)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (name == null) throw new ArgumentNullException(nameof(name));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            List<IndirectObject> objects = RemoveDestinationObjects(doc, ref nextObjNum, name);
            if (objects == null) { ms.Position = 0; ms.CopyTo(output); return false; }

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
            return true;
        }

        /// <summary>
        /// The objects <see cref="RemoveDestination"/> appends, without writing them, or
        /// <c>null</c> when the document holds no destination of that name: for a caller that
        /// writes the revision itself, as <see cref="PlanSetDestination"/> is.
        /// </summary>
        /// <exception cref="Core.PdfEncryptedDocumentException"><paramref name="document"/> is encrypted.</exception>
        public static IReadOnlyList<IndirectObject> PlanRemoveDestination(
            PdfDocument document, int firstFreeObjectNumber, string name)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (name == null) throw new ArgumentNullException(nameof(name));
            int next = firstFreeObjectNumber;
            return RemoveDestinationObjects(document, ref next, name);
        }

        private static List<IndirectObject> RemoveDestinationObjects(PdfDocument doc, ref int nextObjNum, string name)
        {
            // Before the catalog check below: an encrypted document has no loaded catalog, and
            // taking that exit reported "not found" for a destination the file may well hold.
            RefuseEncrypted(doc, "remove a named destination from");

            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return null;
            // A /Root written directly is refused, not answered "not found": the destination may be
            // there, and no revision can carry the catalog.
            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the rebuilt name tree");

            var objects = new List<IndirectObject>();
            PdfDictionary updatedCatalog = null;

            // The whole /Names /Dests tree, /Kids included, read before anything is written. This
            // once shared a leaf-only reader, so removing one destination from a two-level tree
            // deleted all the others with it; a walk cut short by a limit refuses for the same
            // reason.
            var entries = CatalogNameTree.ReadForRewrite(doc, catalog, PdfName.Dests);

            // ---- 1. The legacy catalog /Dests dictionary (PDF 1.1, §12.3.2.3). ----
            // CollectDestinations reads it, so ListDestinations reports a name that lives only
            // here — and this method used to look exclusively in the name tree, take the
            // "no /Names" early exit, and return false for a destination the same class had
            // just listed. Reporting "not found" while leaving it in the file is the failure
            // this closes; the doc comment above has promised both stores all along.
            bool removed = RemoveFromLegacyDests(doc, catalog, name, objects, ref updatedCatalog);

            // ---- 2. The /Names /Dests name tree. ----
            if (entries.RemoveAll(e => e.Text == name) > 0)
            {
                removed = true;
                updatedCatalog = CatalogNameTree.Write(doc, catalog, updatedCatalog,
                    PdfName.Dests, entries, objects, ref nextObjNum);
            }

            if (!removed) return null;

            if (updatedCatalog != null)
                objects.Add(new IndirectObject(catalogObjNum, 0, updatedCatalog));
            return objects;
        }

        /// <summary>
        /// Takes <paramref name="name"/> out of the legacy catalog <c>/Dests</c> dictionary
        /// (§12.3.2.3), superseding it in place when it is indirect and in a catalog clone when it
        /// is direct. Returns whether it held the name.
        /// </summary>
        private static bool RemoveFromLegacyDests(PdfDocument doc, PdfDictionary catalog, string name,
            List<IndirectObject> objects, ref PdfDictionary updatedCatalog)
        {
            var legacyObj = catalog[PdfName.Dests];
            var legacyDict = doc.ResolveObject(legacyObj) as PdfDictionary;
            var nameKey = new PdfName(name);
            if (legacyDict == null || legacyDict[nameKey] == null) return false;

            var updatedLegacy = PdfPageContentAppender.CloneDictionary(legacyDict);
            updatedLegacy.Remove(nameKey);

            if (legacyObj is PdfReference legacyRef)
            {
                objects.Add(new IndirectObject(legacyRef.ReferenceObjectNumber, 0, updatedLegacy));
            }
            else
            {
                updatedCatalog ??= PdfPageContentAppender.CloneDictionary(catalog);
                updatedCatalog.Set(PdfName.Dests, updatedLegacy);
            }
            return true;
        }

        /// <summary>
        /// Refuses to write into an encrypted document: the destination and the rebuilt tree would
        /// be clear text where a reader expects per-object ciphertext.
        /// </summary>
        private static void RefuseEncrypted(PdfDocument doc, string what)
            => EncryptedRevision.Refuse(doc, what, "the name tree");

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        internal static IReadOnlyList<NamedDestination> CollectDestinations(PdfDocument doc)
            => CollectDestinations(doc, out _);

        /// <param name="doc">The document to read.</param>
        /// <param name="complete"><c>false</c> when the name tree's walk stopped at a limit.</param>
        internal static IReadOnlyList<NamedDestination> CollectDestinations(PdfDocument doc, out bool complete)
        {
            complete = true;
            var result = new List<NamedDestination>();
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return result;

            // Legacy /Dests dictionary in catalog.
            var legacyDestsObj = catalog[PdfName.Dests];
            if (legacyDestsObj != null)
            {
                var legacyDests = doc.ResolveObject(legacyDestsObj) as PdfDictionary;
                if (legacyDests != null)
                {
                    foreach (var kvp in legacyDests)
                    {
                        // No /Type skip: its value is a name, which ResolveDestArray already turns
                        // away — the skip that stood here could not change a result.
                        var destArr = ResolveDestArray(kvp.Value, doc);
                        if (destArr != null)
                            result.Add(ParseDestination(kvp.Key.Value, destArr, doc));
                    }
                }
            }

            // /Names /Dests name tree.
            var namesObj = catalog[PdfName.Names];
            if (namesObj != null)
            {
                var namesDict = doc.ResolveObject(namesObj) as PdfDictionary;
                if (namesDict != null)
                {
                    var destsNode = namesDict[PdfName.Dests];
                    if (destsNode != null)
                    {
                        var destTree = doc.ResolveObject(destsNode) as PdfDictionary;
                        if (destTree != null)
                            complete = WalkNameTree(destTree, doc, result);
                    }
                }
            }

            return result;
        }

        // Bounds name-tree /Kids recursion at PdfLimits.MaxTreeDepth; a cyclic or
        // pathologically deep /Kids chain would otherwise stack-overflow
        // (uncatchable in .NET).
        // Traversal is Core.Document.PdfNameTree (GAPS §15c): one implementation across the three
        // rings that walk name trees, with the /Kids cycle guard and the entry budget. The
        // destination value stays unresolved until ResolveDestArray, which is where
        // this type's own /D-indirection handling lives. Returns false when the walk stopped at a
        // limit, which the entries it did reach cannot show.
        private static bool WalkNameTree(PdfDictionary node, PdfDocument doc,
            List<NamedDestination> result)
        {
            var flat = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            bool complete = PdfNameTree.TryFlatten(node, doc, flat, ref budget);

            foreach (var entry in flat)
            {
                // KeyText accepts PdfName as well as PdfString, matching what this walker
                // tolerated before: §7.9.6 says keys are strings, but real files carry both.
                string n = PdfNameTree.KeyText(entry.Key);
                if (n == null) continue;

                var destArr = ResolveDestArray(entry.Value, doc);
                if (destArr != null)
                    result.Add(ParseDestination(n, destArr, doc));
            }
            return complete;
        }

        // The writers' read of the tree is CatalogNameTree.ReadForRewrite, which follows /Kids:
        // reading only the root's own /Names array, SetDestination would write a leaf holding
        // only the new entry, adding one destination silently deleting every other one.

        private static PdfArray ResolveDestArray(PdfObject obj, PdfDocument doc)
        {
            if (obj is PdfArray arr) return arr;
            // Resolve indirect references first (name tree values are often stored as N G R
            // pointing directly to the destination array).
            var resolved = doc.ResolveObject(obj);
            if (resolved is PdfArray resolvedArr) return resolvedArr;
            // Some entries are dicts with a /D key (e.g. action dicts).
            if (resolved is PdfDictionary d)
            {
                var inner = d[PdfName.Dest] ?? d[PdfName.D];
                if (inner is PdfArray a2) return a2;
                if (inner != null && doc.ResolveObject(inner) is PdfArray a3) return a3;
            }
            return null;
        }

        private static NamedDestination ParseDestination(string name, PdfArray destArr, PdfDocument doc)
        {
            // One implementation, in Operations.PdfDestinationPage.
            int pageIndex = PdfDestinationPage.IndexFromDestArray(doc, destArr);

            string type = (destArr.Count > 1) ? (destArr[1] as PdfName)?.Value : null;
            return new NamedDestination(name, pageIndex, type, destArr);
        }
    }

    /// <summary>A named destination entry in a PDF document.</summary>
    public sealed class NamedDestination
    {
        /// <summary>The destination name (key in the name tree).</summary>
        public string Name { get; }
        /// <summary>Zero-based page index, or -1 if unresolved.</summary>
        public int PageIndex { get; }
        /// <summary>Destination type keyword (XYZ, Fit, FitH, FitV, FitR, FitB, ...).</summary>
        public string DestinationType { get; }
        /// <summary>The raw destination array from the PDF.</summary>
        public PdfArray DestinationArray { get; }

        internal NamedDestination(string name, int pageIndex, string type, PdfArray arr)
        {
            Name = name;
            PageIndex = pageIndex;
            DestinationType = type;
            DestinationArray = arr;
        }
    }
}
