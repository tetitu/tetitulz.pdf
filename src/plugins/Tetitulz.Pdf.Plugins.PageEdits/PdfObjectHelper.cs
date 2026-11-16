// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    internal static class PdfObjectHelper
    {
        /// <summary>
        /// Folds the inheritable page attributes (/MediaBox, /Resources, /Rotate,
        /// /CropBox — PDF §7.7.3.4) that a leaf page inherited from its (now
        /// discarded) /Pages ancestors onto the cloned page dictionary, but only
        /// when the page does not already define its own. The source values come
        /// from the resolved <see cref="PdfPage"/> getters; references within them
        /// are remapped through <paramref name="objectMap"/>. Without this, a
        /// merged/split page whose size or resources lived on an intermediate node
        /// renders blank or mis-sized.
        /// </summary>
        /// <remarks>
        /// <b>An inherited value the source holds as an indirect object is written as a reference
        /// to it</b>, when the output carries that object (<see cref="InheritedObjectNumber"/>).
        /// Copying the resolved value onto every page would make pages inheriting one
        /// <c>/Resources</c> each carry the whole dictionary: pages × entries in the output — 40 MB
        /// from 455 KB at eight hundred of each — where the source states it once.
        /// </remarks>
        internal static void MaterializeInheritedPageAttributes(
            PdfDictionary clonedPage, PdfPage srcPage,
            Dictionary<int, int> objectMap, int newPagesTreeObjNum, PdfDocument doc = null,
            InheritedCopies shared = null)
        {
            if (clonedPage == null || srcPage == null) return;

            if (!clonedPage.ContainsKey(PdfName.MediaBox) && srcPage.MediaBox != null)
                clonedPage.Set(PdfName.MediaBox, Inherited(srcPage.MediaBox, objectMap, newPagesTreeObjNum, doc, shared));

            if (!clonedPage.ContainsKey(PdfName.Resources) && srcPage.Resources != null)
                clonedPage.Set(PdfName.Resources, Inherited(srcPage.Resources, objectMap, newPagesTreeObjNum, doc, shared));

            if (!clonedPage.ContainsKey(PdfName.Rotate) && srcPage.Rotate.HasValue)
                clonedPage.Set(PdfName.Rotate, new PdfNumber(srcPage.Rotate.Value));

            if (!clonedPage.ContainsKey(PdfName.CropBox) && srcPage.CropBox != null)
                clonedPage.Set(PdfName.CropBox, Inherited(srcPage.CropBox, objectMap, newPagesTreeObjNum, doc, shared));
        }

        /// <summary>
        /// A reference to the copy of <paramref name="value"/> when it is an indirect object the output
        /// carries, or to the one copy <paramref name="shared"/> writes of a direct value several pages
        /// inherit; else a copy of it.
        /// </summary>
        private static PdfObject Inherited(
            PdfObject value, Dictionary<int, int> objectMap, int newPagesTreeObjNum, PdfDocument doc,
            InheritedCopies shared)
        {
            int number = InheritedObjectNumber(value, doc);
            if (number > 0 && objectMap.TryGetValue(number, out int mapped))
                return new PdfReference(mapped, 0);
            return (PdfObject)shared?.ReferenceTo(value, objectMap, newPagesTreeObjNum)
                   ?? DeepClone(value, objectMap, newPagesTreeObjNum);
        }

        /// <summary>
        /// The copies one output makes of the <b>direct</b> inherited values its pages share: each
        /// written once, as an object of its own, and named by every page that inherits it.
        /// </summary>
        /// <remarks>
        /// <para>A <c>/Pages</c> node's direct <c>/Resources</c> is one instance however many pages
        /// inherit it, and copying it onto each would make pages × entries in the output from
        /// pages + entries in the input — 20 000 pages under a 20 000-entry dictionary, 400 million
        /// copies. The indirect spelling is carried as its object (<see cref="InheritedObjectNumber"/>).</para>
        /// <para>A box is copied onto each page when it is the four numbers of a rectangle (§7.9.5) —
        /// a constant per page — and shared like a dictionary when it holds more. A value one page
        /// alone inherits is copied.</para>
        /// </remarks>
        internal sealed class InheritedCopies
        {
            private readonly Dictionary<object, int> _inheritors = new Dictionary<object, int>(PdfReferenceEquality.Instance);
            private readonly Dictionary<object, int> _numbers = new Dictionary<object, int>(PdfReferenceEquality.Instance);
            private readonly Func<int> _mint;
            private readonly List<(int objNum, PdfObject obj)> _into;

            /// <param name="mint">The output's next free object number, each call.</param>
            /// <param name="into">The output's objects, which each copy joins.</param>
            internal InheritedCopies(Func<int> mint, List<(int objNum, PdfObject obj)> into)
            {
                _mint = mint;
                _into = into;
            }

            /// <summary>Counts what <paramref name="page"/> inherits, before any page is copied.</summary>
            internal void Count(PdfPage page, PdfDocument doc)
            {
                if (page?.Dictionary == null) return;
                if (!page.Dictionary.ContainsKey(PdfName.Resources)) Note(page.Resources, doc);
                if (!page.Dictionary.ContainsKey(PdfName.MediaBox)) Note(page.MediaBox, doc);
                if (!page.Dictionary.ContainsKey(PdfName.CropBox)) Note(page.CropBox, doc);
            }

            private void Note(PdfObject value, PdfDocument doc)
            {
                if (value == null || InheritedObjectNumber(value, doc) > 0) return;
                if (!(value is PdfDictionary || value is PdfArray array && array.Count > 4)) return;
                _inheritors.TryGetValue(value, out int pages);
                _inheritors[value] = pages + 1;
            }

            /// <summary>
            /// A reference to the one copy of <paramref name="value"/>, written the first time it is
            /// asked for, or <c>null</c> when fewer than two pages inherit it.
            /// </summary>
            internal PdfReference ReferenceTo(PdfObject value, Dictionary<int, int> objectMap, int newPagesTreeObjNum)
            {
                if (value == null || !_inheritors.TryGetValue(value, out int pages) || pages < 2) return null;
                if (!_numbers.TryGetValue(value, out int number))
                {
                    number = _mint();
                    var copy = DeepClone(value, objectMap, newPagesTreeObjNum);
                    copy.ObjectNumber = number;
                    copy.GenerationNumber = 0;
                    _into.Add((number, copy));
                    _numbers[value] = number;
                }
                return new PdfReference(number, 0);
            }
        }

        /// <summary>
        /// The object number <paramref name="value"/> — an inherited attribute, as a
        /// <see cref="PdfPage"/> getter resolved it — is stored under, or -1 when it is not an
        /// indirect object of <paramref name="doc"/> (or there is no document): the number the
        /// parser stamped on it, which it stamps on a top-level indirect object alone
        /// (<c>PdfParser.StampIdentity</c>) — a direct value inside an indirect <c>/Pages</c> node
        /// carries none.
        /// </summary>
        internal static int InheritedObjectNumber(PdfObject value, PdfDocument doc)
        {
            if (value == null || doc == null) return -1;
            return value.ObjectNumber > 0 ? value.ObjectNumber : -1;
        }

        /// <summary>
        /// Writes every remapped object, then the page tree, then the catalog, and finishes with the
        /// cross-reference table — returning its offset for the caller's trailer.
        /// </summary>
        /// <remarks>
        /// <para>Shared by <c>PdfMerger</c> and <c>PdfSplitter</c>. The one ordering constraint that
        /// is real: the page tree and the catalog are written <b>after</b> the remapped objects,
        /// because their numbers are allocated last.</para>
        ///
        /// <para>The free-list head <c>(0, 0)</c> is not seeded into the entries:
        /// <see cref="PdfWriter.WriteXrefTable(List{ValueTuple{int, long}}, int, bool)"/> sorts them
        /// by object number and inserts object 0 itself when <c>includeObject0</c> is set, its
        /// default and what both callers use.</para>
        ///
        /// <para>The trailer stays with the caller. The two operations legitimately disagree about
        /// it — merge writes a <c>/ID</c> and split does not — and folding that difference in here
        /// would silently pick a winner.</para>
        /// </remarks>
        internal static long WriteObjectsPagesCatalogAndXref(
            PdfWriter writer,
            List<(int objNum, PdfObject obj)> remappedObjects,
            int pagesTreeObjNum, PdfDictionary pagesDict,
            int catalogObjNum, PdfDictionary catalogDict)
        {
            var xrefEntries = new List<(int objNum, long offset)>();

            foreach (var (objNum, obj) in remappedObjects)
                xrefEntries.Add((objNum, writer.WriteIndirectObject(objNum, 0, obj)));

            xrefEntries.Add((pagesTreeObjNum, writer.WriteIndirectObject(pagesTreeObjNum, 0, pagesDict)));
            xrefEntries.Add((catalogObjNum,   writer.WriteIndirectObject(catalogObjNum,   0, catalogDict)));

            return writer.WriteXrefTable(xrefEntries);
        }

        /// <summary>True for the document catalog (<c>/Type /Catalog</c>).</summary>
        /// <param name="obj">The candidate object.</param>
        /// <param name="doc">The <b>source</b> document, so an indirect <c>/Type</c> can be followed.</param>
        /// <remarks>
        /// <c>/Type</c> is resolved, as the two predicates beside it resolve it: read raw, a catalog
        /// whose <c>/Type</c> is indirect would not be recognised, and the merger — which walks every
        /// object of every input and skips the catalogs because it writes its own — would copy it
        /// into the output as a second, unreachable one.
        /// </remarks>
        internal static bool IsCatalogObject(PdfObject obj, PdfDocument doc = null)
            => obj is PdfDictionary dict && Resolve(doc, dict[PdfName.Type]) as PdfName == PdfName.Catalog;

        /// <summary>
        /// True for a leaf page dictionary: <c>/Type /Page</c>, or a page the document's page list
        /// holds.
        /// </summary>
        /// <remarks>
        /// <para>Used by the splitter to refuse to follow a reference into a page it is not extracting,
        /// and by <see cref="DeepClone"/> to point a copied page's <c>/Parent</c> at the new page
        /// tree. The references that reach a page from outside the page tree — an annotation's
        /// <c>/Dest</c>, a widget's <c>/P</c>, a named destination — look like any other
        /// reference.</para>
        /// <para><b>Core's page list is the authority, not <c>/Type</c>.</b> It also takes a leaf with
        /// no <c>/Type</c>, or one written as a string (<c>PdfPageTreeShape.IsLeafPage</c>), so a link
        /// to such a page that the extract dropped copied the page and its content stream into it,
        /// and a merged or split one kept a <c>/Parent</c> that pointed at nothing.</para>
        /// </remarks>
        /// <param name="doc">
        /// The <b>source</b> document, so an indirect <c>/Type</c> can be followed and the page list
        /// read. Optional only because the predicate is meaningful without it for the ordinary direct
        /// spelling; every caller in this assembly has a document in hand and passes one.
        /// </param>
        /// <param name="obj">The candidate object.</param>
        internal static bool IsPageObject(PdfObject obj, PdfDocument doc = null)
        {
            if (!(obj is PdfDictionary dict)) return false;
            if (Resolve(doc, dict[PdfName.Type]) as PdfName == PdfName.Page) return true;
            return doc != null && dict.ObjectNumber > 0 && PageObjectNumbers(doc).Contains(dict.ObjectNumber);
        }

        // The object numbers of each document's pages, read once: its page list is fixed once read.
        private static readonly ConditionalWeakTable<PdfDocument, HashSet<int>> PageNumbersByDocument =
            new ConditionalWeakTable<PdfDocument, HashSet<int>>();

        /// <summary>The object numbers of <paramref name="doc"/>'s pages, as Core's page list holds them.</summary>
        internal static HashSet<int> PageObjectNumbers(PdfDocument doc) =>
            PageNumbersByDocument.GetValue(doc, d =>
            {
                var numbers = new HashSet<int>();
                foreach (var page in d.Pages)
                {
                    int number = page?.Dictionary?.ObjectNumber ?? 0;
                    if (number > 0) numbers.Add(number);
                }
                return numbers;
            });

        /// <remarks>
        /// <para><b>Unresolved, the two predicates would fail in opposite directions, which is why
        /// both resolve.</b> A page whose <c>/Type</c> is indirect would read as "not a page" — so the
        /// splitter would follow references into pages it is not extracting and pull their objects
        /// into the output. And a <c>/Pages</c> node read as "not a pages node" would not be skipped
        /// by the collectors, so the source page tree would be cloned into a document that is
        /// building its own.</para>
        /// </remarks>
        internal static bool IsPagesTreeNode(PdfObject obj, PdfDocument doc = null)
        {
            if (obj is PdfDictionary dict)
            {
                var type = Resolve(doc, dict[PdfName.Type]) as PdfName;
                return type == PdfName.Pages;
            }
            return false;
        }

        /// <summary>Follows an indirect reference; §7.3.10 permits one for any value.</summary>
        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
            => obj is PdfReference && doc != null ? doc.ResolveObject(obj) : obj;

        /// <summary>
        /// Follows an indirect reference <b>chain</b> through a map of <b>cloned</b> objects,
        /// returning what it was handed when the chain cannot be followed.
        /// </summary>
        /// <param name="obj">The value to follow.</param>
        /// <param name="byNum">Output object number → cloned object. Every caller builds one.</param>
        /// <remarks>
        /// <para><b>The cloned map, not a source document.</b> <c>DeepClone</c> has already rewritten
        /// every reference into the output numbering, so an object number here means something
        /// different than it did in its origin, and neither source document can answer for it.</para>
        ///
        /// <para><b>A chain, not a hop.</b> §7.3.10 lets an indirect object's body be itself a
        /// reference — <c>5 0 obj 6 0 R endobj</c>. A single hop would read a <c>/Dest</c> two levels
        /// from <c>[null /Fit]</c> as live, so a link would survive a split pointing at a page the
        /// output does not contain (<c>IndirectDestinationChainTests</c>), and a destination name two
        /// levels deep would keep the key a colliding merge gave the other document. Merge and split
        /// share this one helper.</para>
        ///
        /// <para><b>The return-on-miss contract is kept deliberately.</b> The shared loops in
        /// Maintenance return <c>null</c> when the map has no entry, and Core's
        /// <c>PdfParser.ResolveObject</c> returns <c>PdfNull.Instance</c>; the callers here rely on
        /// "give me back what I passed", so adopting either would change every call site.</para>
        /// </remarks>
        internal static PdfObject DerefCloned(PdfObject obj, Dictionary<int, PdfObject> byNum)
        {
            // Bounded: a cloned map can contain a cycle, and this runs over caller-supplied bytes.
            int guard = 0;
            while (obj is PdfReference reference && guard++ < MaxReferenceHops)
            {
                if (!byNum.TryGetValue(reference.ReferenceObjectNumber, out var target)) return obj;
                obj = target;
            }
            return obj;
        }

        /// <summary>Matches the bound the equivalent helpers in <c>Plugins.Maintenance</c> use.</summary>
        private const int MaxReferenceHops = 32;

        // Direct-nesting depth cap (defense-in-depth; mirrors
        // PdfPageContentAppender.DeepCloneDictionary's maxDepth). Indirect cycles
        // are bounded by objectMap, but deeply nested direct arrays/dicts would
        // otherwise stack-overflow on untrusted input.
        private static int MaxCloneDepth => PdfLimits.MaxObjectGraphDepth;

        /// <summary>
        /// How many subtrees the depth caps in this assembly have dropped since
        /// <see cref="ResetDepthTruncations"/>, on the calling thread.
        /// </summary>
        /// <remarks>
        /// The caps substitute <see cref="PdfNull"/>, which is a legal object — so an output with a
        /// dropped subtree is indistinguishable from one whose source never had it. Merge and split
        /// surface this next to their name-tree warning, which reports the same class of tolerated
        /// loss.
        /// </remarks>
        [ThreadStatic] internal static int DepthTruncations;

        internal static void ResetDepthTruncations() => DepthTruncations = 0;

        /// <summary>
        /// How many values <see cref="DeepClone"/> has copied on the calling thread: the evidence of
        /// what an operation copied, which its output — the same either way — cannot show.
        /// </summary>
        [ThreadStatic] internal static long ClonedValues;

        /// <param name="obj">The object to copy.</param>
        /// <param name="objectMap">Source object number → output object number.</param>
        /// <param name="newPagesTreeObjNum">The output page tree, which a copied page's <c>/Parent</c> is pointed at.</param>
        /// <param name="depth">Direct-nesting depth, for <see cref="MaxCloneDepth"/>.</param>
        /// <param name="doc">
        /// The <b>source</b> document, so a page whose <c>/Type</c> is indirect is still recognised, as
        /// <see cref="IsPageObject"/> recognises it: a page missed here would keep its source
        /// <c>/Parent</c> — a node never copied — which the reference branch turns into <c>null</c>. Not passed down the recursion: a page is always an indirect object of
        /// its own (<c>/Kids</c> holds indirect references, §7.7.3.3), so only a top-level call can be
        /// handed one.
        /// </param>
        /// <param name="rebuiltKeys">
        /// Keys of the top-level dictionary the caller writes itself: each is copied as a
        /// <see cref="PdfNull"/> placeholder, so it keeps its place among the keys. Not passed down.
        /// </param>
        internal static PdfObject DeepClone(PdfObject obj, Dictionary<int, int> objectMap, int newPagesTreeObjNum,
                                            int depth = 0, PdfDocument doc = null, PdfName[] rebuiltKeys = null)
        {
            ClonedValues++;
            if (obj == null || obj is PdfNull)
                return PdfNull.Instance;
            if (depth > MaxCloneDepth)
            {
                DepthTruncations++;
                return PdfNull.Instance;
            }

            if (obj is PdfReference reference)
            {
                int origObjNum = reference.ReferenceObjectNumber;
                if (objectMap.TryGetValue(origObjNum, out int newObjNum))
                    return new PdfReference(newObjNum, 0);
                return PdfNull.Instance;
            }

            if (obj is PdfStream stream)
            {
                var cloned = new PdfStream();
                cloned.RawData = stream.RawData;
                foreach (var kvp in stream)
                {
                    var clonedValue = IsRebuilt(kvp.Key, rebuiltKeys)
                        ? PdfNull.Instance
                        : DeepClone(kvp.Value, objectMap, newPagesTreeObjNum, depth + 1);
                    cloned.Set(kvp.Key, clonedValue);
                }
                if (cloned.RawData != null)
                    cloned.Set(PdfName.Length, new PdfNumber(cloned.RawData.Length));
                return cloned;
            }

            if (obj is PdfDictionary dict)
            {
                var cloned = new PdfDictionary();
                bool isPageDict = IsPageObject(dict, doc);

                foreach (var kvp in dict)
                {
                    if (isPageDict && kvp.Key == PdfName.Parent)
                    {
                        cloned.Set(PdfName.Parent, new PdfReference(newPagesTreeObjNum, 0));
                        continue;
                    }

                    var clonedValue = IsRebuilt(kvp.Key, rebuiltKeys)
                        ? PdfNull.Instance
                        : DeepClone(kvp.Value, objectMap, newPagesTreeObjNum, depth + 1);
                    cloned.Set(kvp.Key, clonedValue);
                }
                return cloned;
            }

            if (obj is PdfArray array)
            {
                var cloned = new PdfArray();
                for (int i = 0; i < array.Count; i++)
                {
                    cloned.Add(DeepClone(array[i], objectMap, newPagesTreeObjNum, depth + 1));
                }
                return cloned;
            }

            return obj;
        }

        private static bool IsRebuilt(PdfName key, PdfName[] rebuiltKeys) =>
            rebuiltKeys != null && Array.IndexOf(rebuiltKeys, key) >= 0;

        /// <summary>
        /// Gives the copy of a top-level object its output number — unless <see cref="DeepClone"/>
        /// handed back an instance it did not make.
        /// </summary>
        /// <remarks>
        /// A number, string, name or boolean comes back as the source holds it: shared with that
        /// document's object cache, and for <c>true</c>, <c>false</c> and a reference to nothing (the
        /// null) with the whole process. A stamp there would renumber it for every later reader —
        /// the stamp <c>PdfParser.StampIdentity</c> refuses to make. The writer takes the number from its
        /// caller, so an unstamped value is written the same.
        /// </remarks>
        internal static void Stamp(PdfObject cloned, PdfObject original, int objectNumber)
        {
            if (cloned == null || ReferenceEquals(cloned, original) || cloned is PdfNull) return;
            cloned.ObjectNumber = objectNumber;
            cloned.GenerationNumber = 0;
        }

        // ------------------------------------------------------------------
        //  Name trees (ISO 32000-1 §7.9.6) — shared by merge and split
        // ------------------------------------------------------------------
        //
        // The key /Names carries two distinct shapes, and confusing them is the classic bug here
        // (the clearest description in the repo is on PdfSanitizer.SanitizeNameTree):
        //   • On the CATALOG it is the *name dictionary* — keyed by category (/Dests,
        //     /EmbeddedFiles, /JavaScript, …), each value a name-tree ROOT.
        //   • On a name-tree NODE it is the leaf array [key0 value0 key1 value1 …]; /Kids holds
        //     the intermediate node references.
        // Both merge and split rebuild the trees as one flat leaf per category, so neither has to
        // reconcile the /Limits of intermediate nodes it did not author — a tempting shortcut that
        // leaves the surviving /Limits describing a key range the node no longer holds.

        /// <summary>
        /// Flattens the name tree rooted at <paramref name="root"/> into its (key, value) pairs, in
        /// the order the tree stores them. Values are returned <b>unresolved</b> — normally the
        /// <see cref="PdfReference"/> the leaf holds — because the two callers need different things
        /// from them (merge remaps through an object map, split collects the referenced graph).
        /// </summary>
        /// <returns>
        /// <c>false</c> when the walk was cut short by <paramref name="remainingEntries"/> or by
        /// <see cref="PdfLimits.MaxTreeDepth"/>, so the caller can report a truncated result rather
        /// than silently emitting a name tree that is missing entries. The budget is shared across
        /// every category and every source document by passing one counter through all the calls:
        /// a per-tree cap would not bound merging a hundred trees.
        /// </returns>
        internal static bool TryFlattenNameTree(
            PdfObject root, PdfDocument doc,
            List<KeyValuePair<PdfString, PdfObject>> into, ref int remainingEntries)
        {
            if (root == null || doc == null || into == null) return true;

            // The traversal itself is Core.Document.PdfNameTree. What stays here is merge/split's
            // own requirement: a PdfString key, because both
            // rebuild the tree and must preserve the key's exact bytes (see PdfNameTree.CompareKeys and
            // AppendToNameKey). A non-string key is dropped rather than coerced.
            //
            // A dropped non-string key consumes an entry of the budget, because the walker counts
            // what it visits rather than what survives this filter. Conforming files have string
            // keys (§7.9.6); a malformed one truncates marginally earlier, the safe direction for a
            // DoS guard.
            var pairs = new List<KeyValuePair<PdfObject, PdfObject>>();
            bool complete = PdfNameTree.TryFlatten(root, doc, pairs, ref remainingEntries);

            foreach (var pair in pairs)
            {
                if (doc.ResolveObject(pair.Key) is PdfString key)
                    into.Add(new KeyValuePair<PdfString, PdfObject>(key, pair.Value));
            }

            return complete;
        }

        // Key ORDER is Core.Document.PdfNameTree.CompareKeys (§7.9.6, byte by byte), shared with
        // the Base writers that rebuild a name tree.

        // ISO-8859-1 maps bytes 0x00–0xFF 1:1 to code points, so it gives a lossless string view of
        // arbitrary key bytes — used only as a dictionary/set key, never as text shown to anyone.
        //
        // Core.Text.Latin1Text rather than Encoding.Latin1: the latter is .NET 5+, and this assembly
        // also ships a netstandard2.0 leg. Same byte-to-char mapping, same empty string for null.
        internal static string NameKeyLookup(byte[] keyBytes) =>
            Core.Text.Latin1Text.Decode(keyBytes);

        /// <summary>
        /// Appends <paramref name="suffix"/> to a name-tree key, preserving the key's own encoding.
        /// </summary>
        /// <remarks>
        /// A key carrying §7.9.2.2's <c>FE FF</c> marker is UTF-16BE, so the suffix has to be
        /// UTF-16BE too — appending ASCII bytes to it produces a string whose tail decodes as CJK
        /// noise, i.e. a destination name no human wrote and no reader can match.
        /// </remarks>
        internal static byte[] AppendToNameKey(byte[] key, string suffix)
        {
            key = key ?? Array.Empty<byte>();
            bool utf16 = key.Length >= 2 && key[0] == 0xFE && key[1] == 0xFF;
            byte[] tail = utf16 ? Encoding.BigEndianUnicode.GetBytes(suffix) : Encoding.ASCII.GetBytes(suffix);
            var result = new byte[key.Length + tail.Length];
            Buffer.BlockCopy(key, 0, result, 0, key.Length);
            Buffer.BlockCopy(tail, 0, result, key.Length, tail.Length);
            return result;
        }

        /// <summary>
        /// Resolves a destination (§12.3.2) to the object number of the page it lands on, or 0 when
        /// it cannot be resolved.
        /// </summary>
        /// <remarks>
        /// Handles the explicit array form <c>[page /Fit …]</c> and the byte-string form, which is
        /// looked up in <paramref name="namedDestinations"/> — the document's flattened
        /// <c>/Names /Dests</c> tree. The legacy PDF 1.1 <c>/Dests</c> <i>name dictionary</i> form
        /// (a destination written as a name object rather than a string) is deliberately NOT
        /// resolved: this plugin does not carry that dictionary over, so pretending to resolve it
        /// would keep a bookmark whose destination the output no longer contains.
        /// </remarks>
        internal static int DestinationPageObjectNumber(
            PdfObject dest, PdfDocument doc, Dictionary<string, PdfObject> namedDestinations)
        {
            // A destination reaches the page through at most a handful of hops
            // (string → reference → << /D [ref /Fit] >>), so a small step budget both terminates
            // the walk and refuses to follow a name-to-name chain, which §12.3.2.3 does not define
            // and a crafted file would happily make circular.
            bool nameResolved = false;
            for (int step = 0; step < 6 && dest != null && !(dest is PdfNull); step++)
            {
                switch (dest)
                {
                    case PdfReference indirect:
                        dest = doc.GetObject(indirect.ReferenceObjectNumber);
                        continue;

                    case PdfArray explicitDest:
                        return explicitDest.Count > 0 && explicitDest[0] is PdfReference pageRef
                            ? pageRef.ReferenceObjectNumber
                            : 0;

                    case PdfDictionary destDict:
                        // A named destination may map to << /D [page /Fit] >> rather than to the array.
                        dest = destDict[PdfName.D];
                        continue;

                    case PdfString named when !nameResolved:
                        nameResolved = true;
                        if (namedDestinations == null ||
                            !namedDestinations.TryGetValue(NameKeyLookup(named.RawBytes), out var target))
                            return 0;
                        dest = target;
                        continue;

                    default:
                        return 0;
                }
            }
            return 0;
        }
        /// <summary>
        /// The single-level <c>/Pages</c> node and the <c>/Catalog</c> that points at it, for a
        /// newly assembled document.
        /// </summary>
        /// <remarks>
        /// Shared by <c>PdfMerger</c> and <c>PdfSplitter</c>. <c>/Count</c> is the length of
        /// <c>/Kids</c>, as §7.7.3.2 requires, by construction.
        ///
        /// <para>The two diverge immediately AFTER this block: the merger concatenates every source's
        /// <c>/AcroForm /Fields</c> into one combined form, the splitter carries over a single
        /// source's.</para>
        /// </remarks>
        /// <param name="kids">
        /// The page objects, in order. <c>/Count</c> is taken from this, never supplied separately.
        /// </param>
        /// <param name="pagesTreeObjNum">Object number the <c>/Pages</c> node will be written under.</param>
        /// <param name="catalogObjNum">Object number the catalog will be written under.</param>
        internal static (PdfDictionary Pages, PdfDictionary Catalog) BuildPageTreeAndCatalog(
            PdfArray kids, int pagesTreeObjNum, int catalogObjNum)
        {
            // The /Pages half is Core.Writing.PdfPageTreeWriter's, shared with Core, Base and
            // DocxToPdf: shared code goes down into a leaf.
            var pages = PdfPageTreeWriter.BuildRoot(kids, pagesTreeObjNum);

            var catalog = new PdfDictionary();
            catalog.Set(PdfName.Type, PdfName.Catalog);
            catalog.Set(PdfName.Pages, new PdfReference(pagesTreeObjNum, 0));
            catalog.ObjectNumber = catalogObjNum;
            catalog.GenerationNumber = 0;

            return (pages, catalog);
        }

    }
}
