// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Serialises a <see cref="PdfStructureTree"/> into the objects a tagged PDF needs:
    /// <c>/StructTreeRoot</c>, one object per structure element, and the <c>/ParentTree</c> number
    /// tree that maps each page's marked content back to the element owning it.
    ///
    /// <para>Kept out of <see cref="PdfDocumentBuilder.Build"/> so that method stays readable, but
    /// it must run inside it: <c>/Pg</c> needs page object numbers and <c>/StructParents</c> goes
    /// on the page dictionary, and both are allocated there.</para>
    ///
    /// <para>The shape — bare integer in <c>/K</c> for content on the element's own <c>/Pg</c>,
    /// an <c>/MCR</c> dictionary otherwise — is taken from
    /// <c>Accessibility.Tagging.PdfAutoTagger</c>, which does the same job for an existing file.
    /// Two implementations of one format is how they come to disagree about it.</para>
    /// </summary>
    internal static class PdfStructureTreeWriter
    {
        internal sealed class Result
        {
            /// <summary>Object number of the <c>/StructTreeRoot</c>.</summary>
            public int StructTreeRootObjNum;

            /// <summary>Objects to write, in no particular order.</summary>
            public List<(int objNum, PdfObject obj)> Objects = new();

            /// <summary>Per-page <c>/StructParents</c> key, for pages that carry marked content.</summary>
            public Dictionary<PdfContentStreamBuilder, int> PageStructParents = new();
        }

        /// <summary>
        /// Builds the structure objects.
        /// </summary>
        /// <param name="tree">The tree, already pruned.</param>
        /// <param name="pageObjNums">Object number of each page's dictionary, by content builder.</param>
        /// <param name="nextObjNum">Object-number allocator, advanced as objects are created.</param>
        internal static Result Build(PdfStructureTree tree,
            IReadOnlyDictionary<PdfContentStreamBuilder, int> pageObjNums, ref int nextObjNum)
        {
            var result = new Result();

            int rootNum = nextObjNum++;
            result.StructTreeRootObjNum = rootNum;
            int parentTreeNum = nextObjNum++;

            // Each page that carries marked content gets a /StructParents key; the ParentTree maps
            // that key to an array indexed by MCID.
            var pagesWithContent = new List<PdfContentStreamBuilder>();
            foreach (var kv in pageObjNums)
                if (kv.Key.MarkedContentCount > 0) pagesWithContent.Add(kv.Key);

            // Deterministic order: by page object number, so the output does not depend on
            // dictionary enumeration order.
            pagesWithContent.Sort((a, b) => pageObjNums[a].CompareTo(pageObjNums[b]));
            for (int i = 0; i < pagesWithContent.Count; i++)
                result.PageStructParents[pagesWithContent[i]] = i;

            // MCID → owning element, per page. Filled while the element objects are created.
            var owners = new Dictionary<PdfContentStreamBuilder, PdfStructureElement[]>();
            foreach (var page in pagesWithContent)
                owners[page] = new PdfStructureElement[page.MarkedContentCount];

            var elementNums = new Dictionary<PdfStructureElement, int>();
            var parents = new Dictionary<PdfStructureElement, PdfStructureElement>();
            AllocateNumbers(tree.Root, null, ref nextObjNum, elementNums, parents);

            foreach (var kv in elementNums)
                foreach (var (page, mcid) in kv.Key.Content)
                    if (owners.TryGetValue(page, out var slots) && mcid >= 0 && mcid < slots.Length)
                        slots[mcid] = kv.Key;

            Dictionary<PdfStructureElement, byte[]> ids = NoteIdentifiers(elementNums);

            // The element objects themselves.
            foreach (var kv in elementNums)
            {
                var dict = BuildElement(kv.Key, rootNum, elementNums, parents, pageObjNums, ids);
                dict.ObjectNumber = kv.Value;
                dict.GenerationNumber = 0;
                result.Objects.Add((kv.Value, dict));
            }

            // The ParentTree.
            var nums = new PdfArray();
            for (int i = 0; i < pagesWithContent.Count; i++)
            {
                var page = pagesWithContent[i];
                var arr = new PdfArray();
                foreach (var owner in owners[page])
                {
                    // A gap means an MCID nothing claimed. PdfNull keeps the array indexed by MCID
                    // rather than silently shifting every later entry by one.
                    if (owner != null && elementNums.TryGetValue(owner, out int n))
                        arr.Add(new PdfReference(n, 0));
                    else arr.Add(PdfNull.Instance);
                }
                nums.Add(new PdfNumber(i));
                nums.Add(arr);
            }
            var parentTree = new PdfDictionary();
            parentTree.Set(PdfName.Nums, nums);
            parentTree.ObjectNumber = parentTreeNum;
            parentTree.GenerationNumber = 0;
            result.Objects.Add((parentTreeNum, parentTree));

            // The root.
            var rootKids = new PdfArray();
            if (elementNums.TryGetValue(tree.Root, out int rootElementNum))
                rootKids.Add(new PdfReference(rootElementNum, 0));

            var structRoot = new PdfDictionary();
            structRoot.Set(PdfName.Type, PdfName.StructTreeRoot);
            structRoot.Set(PdfName.K, rootKids);
            structRoot.Set(PdfName.ParentTree, new PdfReference(parentTreeNum, 0));
            structRoot.Set(PdfName.ParentTreeNextKey, new PdfNumber(pagesWithContent.Count));
            if (ids.Count > 0) structRoot.Set(PdfName.IDTree, IdTree(ids, elementNums));
            structRoot.ObjectNumber = rootNum;
            structRoot.GenerationNumber = 0;
            result.Objects.Add((rootNum, structRoot));

            return result;
        }

        // Object numbers and parent links are recorded in one walk. Deriving the parent afterwards
        // by searching every element's child list is quadratic and, worse, gives the wrong answer
        // silently if an element is ever reachable from two places.
        private static void AllocateNumbers(PdfStructureElement el, PdfStructureElement parent,
            ref int nextObjNum, Dictionary<PdfStructureElement, int> map,
            Dictionary<PdfStructureElement, PdfStructureElement> parents)
        {
            map[el] = nextObjNum++;
            parents[el] = parent;
            foreach (var child in el.Children)
                AllocateNumbers(child, el, ref nextObjNum, map, parents);
        }

        /// <summary>
        /// An <c>/ID</c> for every <c>Note</c> element, by element: <c>note-</c> and the element's object
        /// number, so unique within the tree and the same on every build of the same document.
        /// </summary>
        /// <remarks>
        /// ISO 14289-1 7.9 gives each Note "a unique entry in the ID key" — ISO 32000-1 Table 323's
        /// element identifier, a byte string no other element of the tree carries, which the structure
        /// tree root's <c>/IDTree</c> maps back to the element (§14.7.2). A note's citation is found from
        /// it, so every producer writing through this tree gets one without asking.
        /// </remarks>
        private static Dictionary<PdfStructureElement, byte[]> NoteIdentifiers(
            Dictionary<PdfStructureElement, int> elementNums)
        {
            var ids = new Dictionary<PdfStructureElement, byte[]>();
            foreach (var kv in elementNums)
                if (string.Equals(kv.Key.Role, PdfStructureTypes.Note, StringComparison.Ordinal))
                    ids[kv.Key] = Encoding.ASCII.GetBytes("note-" + kv.Value.ToString(CultureInfo.InvariantCulture));
            return ids;
        }

        /// <summary>
        /// The structure tree root's <c>/IDTree</c> (ISO 32000-1 Table 322): a name tree from each
        /// identifier to its element, one <c>/Names</c> array in key order (§7.9.6, <see cref="PdfNameTree.CompareKeys"/>).
        /// </summary>
        private static PdfDictionary IdTree(Dictionary<PdfStructureElement, byte[]> ids,
            Dictionary<PdfStructureElement, int> elementNums)
        {
            var entries = new List<(byte[] Key, int ObjNum)>(ids.Count);
            foreach (var kv in ids) entries.Add((kv.Value, elementNums[kv.Key]));
            entries.Sort((a, b) => PdfNameTree.CompareKeys(a.Key, b.Key));

            var names = new PdfArray();
            foreach (var (key, objNum) in entries)
            {
                names.Add(new PdfString(key));
                names.Add(new PdfReference(objNum, 0));
            }
            var tree = new PdfDictionary();
            tree.Set(PdfName.Names, names);
            return tree;
        }

        private static PdfDictionary BuildElement(PdfStructureElement el, int rootNum,
            Dictionary<PdfStructureElement, int> elementNums,
            Dictionary<PdfStructureElement, PdfStructureElement> parents,
            IReadOnlyDictionary<PdfContentStreamBuilder, int> pageObjNums,
            Dictionary<PdfStructureElement, byte[]> ids)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.StructElem);
            dict.Set(PdfName.S, new PdfName(el.Role));
            if (ids.TryGetValue(el, out byte[] id)) dict.Set(PdfName.ID, new PdfString(id));

            // /P is the parent; the root element's parent is the StructTreeRoot itself.
            int parentNum = rootNum;
            if (parents.TryGetValue(el, out var parent) && parent != null
                && elementNums.TryGetValue(parent, out int pn))
                parentNum = pn;
            dict.Set(PdfName.P, new PdfReference(parentNum, 0));

            if (!string.IsNullOrEmpty(el.Alt)) dict.Set(PdfName.Alt, new PdfString(el.Alt));
            if (!string.IsNullOrEmpty(el.ActualText)) dict.Set(PdfName.ActualText, new PdfString(el.ActualText));
            if (!string.IsNullOrEmpty(el.Lang)) dict.Set(PdfName.Lang, new PdfString(el.Lang));
            if (el.Attributes != null) dict.Set(PdfName.A, el.Attributes);

            // The element's own page: the first page it draws on. Content on any OTHER page has to
            // be referenced through an /MCR that names its page explicitly, because a bare integer
            // in /K is interpreted against /Pg — emitting one for the wrong page silently addresses
            // a different piece of content.
            PdfContentStreamBuilder ownPage = null;
            if (el.Content.Count > 0) ownPage = el.Content[0].Page;
            if (ownPage != null && pageObjNums.TryGetValue(ownPage, out int pgNum))
                dict.Set(PdfName.Pg, new PdfReference(pgNum, 0));

            var kids = new PdfArray();
            foreach (var (page, mcid) in el.Content)
            {
                if (ReferenceEquals(page, ownPage)) kids.Add(new PdfNumber(mcid));
                else if (pageObjNums.TryGetValue(page, out int otherPg))
                {
                    var mcr = new PdfDictionary();
                    mcr.Set(PdfName.Type, PdfName.MCR);
                    mcr.Set(PdfName.Pg, new PdfReference(otherPg, 0));
                    mcr.Set(PdfName.MCID, new PdfNumber(mcid));
                    kids.Add(mcr);
                }
            }
            foreach (var child in el.Children)
                if (elementNums.TryGetValue(child, out int cn)) kids.Add(new PdfReference(cn, 0));

            if (kids.Count > 0) dict.Set(PdfName.K, kids);
            return dict;
        }
    }
}
