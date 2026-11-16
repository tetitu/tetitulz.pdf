// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// The recursion shared by <see cref="PdfNameTree"/> (§7.9.6) and <see cref="PdfNumberTree"/>
    /// (§7.9.7): <c>/Kids</c> to descend, a leaf array of alternating keys and values, and the two
    /// budgets plus the cycle guard that make either safe on untrusted input.
    /// </summary>
    /// <remarks>
    /// <b>The two tree kinds differ by one dictionary key.</b> §7.9.7 defines a number tree by
    /// reference to §7.9.6 and changes the leaf array's name and its key type; nothing about the
    /// traversal changes. Parameterising the leaf name is what keeps the <c>/Kids</c> cycle guard,
    /// the depth cap and both budgets in one place — the alternative is two copies that differ in a
    /// string, and this repository's record is that the second copy is where a guard goes missing.
    ///
    /// <para>Internal: the public faces are the two tree types, which is where a caller looks.</para>
    /// </remarks>
    internal static class PdfTreeWalk
    {
        /// <summary>
        /// Flattens the tree rooted at <paramref name="root"/> into its (key, value) pairs, in the
        /// order the tree stores them.
        /// </summary>
        /// <param name="root">The tree root - a node dictionary or a reference to one.</param>
        /// <param name="doc">Document used to resolve references.</param>
        /// <param name="leafArrayKey"><c>/Names</c> for a name tree, <c>/Nums</c> for a number one.</param>
        /// <param name="into">Receives the pairs, both sides unresolved.</param>
        /// <param name="remainingEntries">Shared entry budget, decremented per pair.</param>
        internal static bool TryFlatten(
            PdfObject root, PdfDocument doc, PdfName leafArrayKey,
            IList<KeyValuePair<PdfObject, PdfObject>> into, ref int remainingEntries)
            => TryFlatten(root, doc, leafArrayKey, into, ref remainingEntries,
                PdfLimits.MaxTreeDepth, PdfLimits.MaxTreeKidSlots, out _);

        /// <summary>
        /// <see cref="TryFlatten(PdfObject, PdfDocument, PdfName, IList{KeyValuePair{PdfObject, PdfObject}}, ref int)"/>
        /// with the caller's own limits, and the reason the walk stopped.
        /// </summary>
        /// <param name="root">The tree root - a node dictionary or a reference to one.</param>
        /// <param name="doc">Document used to resolve references.</param>
        /// <param name="leafArrayKey"><c>/Names</c> for a name tree, <c>/Nums</c> for a number one.</param>
        /// <param name="into">Receives the pairs, both sides unresolved.</param>
        /// <param name="remainingEntries">Shared entry budget, decremented per pair.</param>
        /// <param name="maxDepth">Deepest <c>/Kids</c> chain to follow.</param>
        /// <param name="maxKidSlots">Total <c>/Kids</c> slots to charge for, across the whole walk.</param>
        /// <param name="truncation">Which limit ended the walk, or <c>None</c>.</param>
        /// <remarks>
        /// <b>The limits are parameters because they legitimately differ.</b> A conformance checker
        /// running over untrusted input holds tighter caps than a reader does, and that difference
        /// is the whole reason it kept a second copy of this traversal rather than calling it. The
        /// guards are shared; the numbers are the caller's. <c>PdfLimits</c>' header draws the same
        /// distinction: centralising a value is not the same as unifying it.
        /// </remarks>
        internal static bool TryFlatten(
            PdfObject root, PdfDocument doc, PdfName leafArrayKey,
            IList<KeyValuePair<PdfObject, PdfObject>> into, ref int remainingEntries,
            int maxDepth, int maxKidSlots, out PdfTreeTruncation truncation)
        {
            truncation = PdfTreeTruncation.None;
            if (root == null || doc == null || into == null) return true;

            // Per call, unlike remainingEntries, which the caller shares across documents: this one
            // bounds a single walk's iteration cost, not the size of the result the caller is
            // accumulating. See PdfLimits.MaxTreeKidSlots.
            int remainingKidSlots = maxKidSlots;
            return Flatten(root, doc, leafArrayKey, into, ref remainingEntries,
                ref remainingKidSlots, new HashSet<int>(), 0, maxDepth, ref truncation);
        }

        private static bool Flatten(
            PdfObject node, PdfDocument doc, PdfName leafArrayKey,
            IList<KeyValuePair<PdfObject, PdfObject>> into, ref int remainingEntries,
            ref int remainingKidSlots, HashSet<int> visited, int depth,
            int maxDepth, ref PdfTreeTruncation truncation)
        {
            if (node == null || node is PdfNull) return true;
            // First cause wins: a tree that exhausts one budget usually goes on to exhaust the
            // next, and the FIRST limit reached is the one that describes what happened.
            if (depth > maxDepth)
            {
                if (truncation == PdfTreeTruncation.None) truncation = PdfTreeTruncation.Depth;
                return false;
            }

            if (node is PdfReference reference)
            {
                // Bounded by the visited set rather than by depth alone: a two-node cycle is
                // shallow, so a depth cap would spin to MaxTreeDepth once per entry point.
                if (!visited.Add(reference.ReferenceObjectNumber)) return true;

                // A null object with no parse error is what the file holds, and an empty subtree;
                // a null WITH one is a node this walk could not read, and the tree is partial —
                // read immediately, since the slot belongs to the most recent GetObject only.
                PdfObject target = doc.GetObject(reference.ReferenceObjectNumber);
                if ((target == null || target is PdfNull) && Parsing.PdfParser.LastObjectParseError != null)
                {
                    if (truncation == PdfTreeTruncation.None) truncation = PdfTreeTruncation.UnreadableNode;
                    return false;
                }

                return Flatten(target, doc, leafArrayKey,
                    into, ref remainingEntries, ref remainingKidSlots, visited, depth + 1,
                    maxDepth, ref truncation);
            }

            if (!(node is PdfDictionary dict)) return true;

            bool complete = true;

            if (doc.ResolveObject(dict[leafArrayKey]) is PdfArray leaf)
            {
                // Table 36 / Table 37: the leaf array alternates key, value. An odd trailing element
                // is a key with no value, which is dropped rather than paired with whatever follows.
                for (int i = 0; i + 1 < leaf.Count; i += 2)
                {
                    if (remainingEntries <= 0)
                    {
                        if (truncation == PdfTreeTruncation.None) truncation = PdfTreeTruncation.Entries;
                        complete = false;
                        break;
                    }
                    remainingEntries--;
                    into.Add(new KeyValuePair<PdfObject, PdfObject>(leaf[i], leaf[i + 1]));
                }
            }

            if (doc.ResolveObject(dict[PdfName.Kids]) is PdfArray kids)
            {
                for (int i = 0; i < kids.Count; i++)
                {
                    // Charged per SLOT, before the recursion and whether or not the kid turns out
                    // to be fresh. The visited set stops a repeat kid being re-expanded but not this
                    // loop running over it, and that loop is the whole cost when every node shares
                    // one /Kids array — see PdfLimits.MaxTreeKidSlots.
                    if (remainingKidSlots <= 0)
                    {
                        if (truncation == PdfTreeTruncation.None) truncation = PdfTreeTruncation.KidSlots;
                        complete = false;
                        break;
                    }
                    remainingKidSlots--;

                    if (!Flatten(kids[i], doc, leafArrayKey, into, ref remainingEntries,
                                 ref remainingKidSlots, visited, depth + 1, maxDepth, ref truncation))
                        complete = false;
                }
            }

            return complete;
        }
    }
}
