// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// What an existing <c>/StructTreeRoot</c> already owns, read once so a tagging pass can
    /// <b>extend</b> it rather than supersede it.
    /// </summary>
    /// <remarks>
    /// <para><b>Merging and superseding need different facts, which is why this is a type.</b>
    /// Superseding needs one boolean — does any element name content — and the tagger already has
    /// it. Extending needs three more: which parent-tree keys are taken, which <i>pages</i> those
    /// keys belong to, and the existing <c>/Nums</c> entries themselves, because the merged tree
    /// has to carry them forward.</para>
    ///
    /// <para><b>A tree that cannot be read completely is not merged.</b>
    /// <see cref="PdfNumberTree.TryFlatten(PdfObject, PdfDocument, IList{KeyValuePair{PdfObject, PdfObject}}, ref int)"/>
    /// answers <c>false</c> when a budget or the depth cap
    /// cut the walk short, and a merge built on a truncated read would write a parent tree missing
    /// the entries it could not see — turning a document whose links this pass could not follow
    /// into one whose links no longer exist. <see cref="IsComplete"/> is that answer, and the
    /// caller refuses on it.</para>
    ///
    /// <para><b>The page test is by key, not by walking the tree.</b> ISO 32000-1 §14.7.4.4 makes a
    /// page's <c>/StructParents</c> its key into the parent tree, so "does the existing tree claim
    /// this page" is "is that key present" — one lookup, against a walk of every element and its
    /// <c>/K</c>. A page with no <c>/StructParents</c> is claimed by nothing, whatever the tree
    /// contains, because there is no key by which an element could reach it.</para>
    /// </remarks>
    internal sealed class ExistingStructureTree
    {
        /// <summary>
        /// Entry budget for the parent-tree walk.
        /// </summary>
        /// <remarks>
        /// One entry per page plus one per tagged annotation, so this is generous for any real
        /// document and still bounds a hostile one. Exceeding it makes <see cref="IsComplete"/>
        /// false, which refuses the merge rather than truncating it.
        /// </remarks>
        internal const int MaxParentTreeEntries = 500_000;

        private readonly HashSet<long> _keys = new HashSet<long>();

        private ExistingStructureTree(
            PdfDictionary root, int rootObjectNumber,
            List<KeyValuePair<PdfObject, PdfObject>> entries, bool complete)
        {
            Root = root;
            RootObjectNumber = rootObjectNumber;
            Entries = entries;
            IsComplete = complete;

            long highest = -1;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!PdfNumberTree.TryKey(entries[i].Key, out long key)) continue;
                _keys.Add(key);
                if (key > highest) highest = key;
            }
            HighestKey = highest;
        }

        /// <summary>The existing root dictionary, as parsed. Never modified.</summary>
        internal PdfDictionary Root { get; }

        /// <summary>The root's own object number, or 0 when the catalog held it inline.</summary>
        internal int RootObjectNumber { get; }

        /// <summary>The existing <c>/ParentTree</c> entries, keys and values unresolved.</summary>
        internal List<KeyValuePair<PdfObject, PdfObject>> Entries { get; }

        /// <summary>Whether the parent-tree walk finished rather than hitting a budget or the depth cap.</summary>
        internal bool IsComplete { get; }

        /// <summary>The greatest key in use, or -1 when the tree defines none.</summary>
        internal long HighestKey { get; }

        /// <summary>
        /// Reads the catalog's structure tree, or answers <c>null</c> when there is none.
        /// </summary>
        internal static ExistingStructureTree Read(PdfDocument doc, PdfDictionary catalog)
        {
            if (doc == null || catalog == null) return null;

            PdfObject raw = catalog[PdfName.StructTreeRoot];
            if (raw == null) return null;

            if (doc.ResolveObject(raw) is not PdfDictionary root) return null;

            // On a PdfReference the object number is the reference's own, not the inherited
            // PdfObject.ObjectNumber -- which is 0 for a reference read out of a dictionary.
            int rootNumber = raw is PdfReference reference ? reference.ReferenceObjectNumber : 0;

            var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
            bool complete = true;

            PdfObject parentTree = root[PdfName.ParentTree];
            if (parentTree != null)
            {
                int budget = MaxParentTreeEntries;
                complete = PdfNumberTree.TryFlatten(parentTree, doc, entries, ref budget);
            }

            return new ExistingStructureTree(root, rootNumber, entries, complete);
        }

        /// <summary>Whether <paramref name="key"/> is already defined by the existing parent tree.</summary>
        internal bool DefinesKey(long key) => _keys.Contains(key);

        /// <summary>
        /// Whether the existing tree already reaches <paramref name="pageDict"/>'s marked content.
        /// </summary>
        /// <remarks>
        /// A page with no <c>/StructParents</c> answers <c>false</c>: without a key there is no
        /// route from the parent tree to its content, so whatever the tree holds it does not hold
        /// this page.
        /// </remarks>
        internal bool ClaimsPage(PdfDocument doc, PdfDictionary pageDict)
        {
            if (doc == null || pageDict == null) return false;
            if (doc.ResolveObject(pageDict[PdfName.StructParents]) is not PdfNumber key || !key.IsInteger) return false;

            return DefinesKey(key.LongValue);
        }

        /// <summary>
        /// Whether the tree already owns <paramref name="annot"/>: its <c>/StructParent</c> is a key
        /// the parent tree defines, so an element of the tree reaches it through an <c>/OBJR</c>.
        /// Merging leaves such an annotation alone — re-keying it would give it two parents, the old
        /// element's <c>/OBJR</c> and a new wrapper's.
        /// </summary>
        internal bool ClaimsAnnotation(PdfDocument doc, PdfDictionary annot)
        {
            if (doc == null || annot == null) return false;
            if (doc.ResolveObject(annot[PdfName.StructParent]) is not PdfNumber key || !key.IsInteger) return false;

            return DefinesKey(key.LongValue);
        }

        /// <summary>
        /// The kids the existing root lists, as a fresh array this pass may append to.
        /// </summary>
        /// <remarks>
        /// <b><c>/K</c> may be a single element rather than an array</b> (§14.7.2), and a merge that
        /// assumed an array would drop that element and leave it parented to a root that no longer
        /// names it. The single form is promoted here instead.
        /// </remarks>
        internal PdfArray CloneKids(PdfDocument doc)
        {
            var kids = new PdfArray();

            PdfObject raw = Root[PdfName.K];
            if (raw == null) return kids;

            // Read RAW, not resolved: a single-child /K is a bare reference, and resolving it first
            // yields the dictionary, which is not what the array should carry.
            if (doc?.ResolveObject(raw) is PdfArray array)
            {
                foreach (PdfObject item in array) kids.Add(item);
                return kids;
            }

            kids.Add(raw);
            return kids;
        }
    }
}
