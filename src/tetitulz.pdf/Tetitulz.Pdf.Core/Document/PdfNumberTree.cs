// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// The single traversal for a PDF <b>number tree</b> (ISO 32000-1 §7.9.7): the
    /// <c>/Kids</c>-and-<c>/Nums</c> structure behind <c>/ParentTree</c> and <c>/PageLabels</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>It is a name tree with integer keys, and it shares that tree's walk.</b> §7.9.7
    /// defines a number tree by reference to §7.9.6, changing only the leaf array's key
    /// (<c>/Nums</c> rather than <c>/Names</c>) and the key type. So the recursion — the
    /// <c>/Kids</c> cycle guard, the depth cap, the shared entry budget and the kid-slot budget —
    /// is <see cref="PdfTreeWalk"/>, called by both faces. Copying that walk to change one
    /// dictionary key is how a guard ends up in one of two places, which is the failure this
    /// repository has recorded against every duplicated traversal it has found.</para>
    ///
    /// <para><b>Keys come back unresolved, like a name tree's.</b> §7.9.7 says they are integers,
    /// and every producer in this tree writes them directly — but §7.3.10 permits an indirect value
    /// anywhere, and resolving here would force every caller to pay for it.
    /// <see cref="TryKey"/> converts one, and <b>says when it cannot</b>: a key that is not a number
    /// is a malformed tree, not an entry to drop quietly.</para>
    ///
    /// <para>A leaf: no static fields, so no type initializer.</para>
    /// </remarks>
    public static class PdfNumberTree
    {
        /// <summary>
        /// Flattens the number tree rooted at <paramref name="root"/> into its (key, value) pairs,
        /// in the order the tree stores them.
        /// </summary>
        /// <param name="root">The number-tree root — a node dictionary or a reference to one.</param>
        /// <param name="doc">Document used to resolve <c>/Kids</c> references.</param>
        /// <param name="into">Receives the pairs. Keys and values are both <b>unresolved</b>.</param>
        /// <param name="remainingEntries">
        /// Shared entry budget, decremented per pair. Pass the same variable across every tree in
        /// one operation, so merging a hundred of them is bounded as a whole rather than each.
        /// </param>
        /// <returns>
        /// <c>false</c> when the walk was cut short by a budget or by the depth cap, so a caller can
        /// report a truncated result rather than silently emitting a tree missing entries.
        /// </returns>
        public static bool TryFlatten(
            PdfObject root, PdfDocument doc,
            IList<KeyValuePair<PdfObject, PdfObject>> into, ref int remainingEntries)
            => PdfTreeWalk.TryFlatten(root, doc, PdfName.Nums, into, ref remainingEntries);

        /// <summary>
        /// <see cref="TryFlatten(PdfObject, PdfDocument, IList{KeyValuePair{PdfObject, PdfObject}}, ref int)"/>
        /// with the caller's own depth and <c>/Kids</c>-slot limits, and the reason the walk stopped.
        /// </summary>
        /// <param name="root">The number-tree root — a node dictionary or a reference to one.</param>
        /// <param name="doc">Document used to resolve <c>/Kids</c> references.</param>
        /// <param name="into">Receives the pairs. Keys and values are both <b>unresolved</b>.</param>
        /// <param name="remainingEntries">Shared entry budget, decremented per pair.</param>
        /// <param name="maxDepth">Deepest <c>/Kids</c> chain to follow.</param>
        /// <param name="maxKidSlots">Total <c>/Kids</c> slots to charge for across the walk.</param>
        /// <param name="truncation">Which limit ended the walk, or <c>None</c>.</param>
        /// <returns><c>false</c> when the walk was cut short; <paramref name="truncation"/> says why.</returns>
        /// <remarks>
        /// <b>This overload exists because a caller that reports to a user needs to say what
        /// happened.</b> Answering only "incomplete" is what kept a second copy of this traversal
        /// alive in the deep accessibility checker, which writes the reason into a PDF/UA finding
        /// and holds deliberately tighter caps than a reader does. Sharing the walk while leaving
        /// the numbers to the caller is what retired that copy without taking its diagnostic or its
        /// stricter limits with it. See <see cref="PdfTreeTruncation"/>.
        /// </remarks>
        public static bool TryFlatten(
            PdfObject root, PdfDocument doc,
            IList<KeyValuePair<PdfObject, PdfObject>> into, ref int remainingEntries,
            int maxDepth, int maxKidSlots, out PdfTreeTruncation truncation)
            => PdfTreeWalk.TryFlatten(root, doc, PdfName.Nums, into, ref remainingEntries,
                maxDepth, maxKidSlots, out truncation);

        /// <summary>
        /// The integer value of a number-tree key.
        /// </summary>
        /// <param name="key">The key as the leaf array holds it.</param>
        /// <param name="value">The key's value when this returns <c>true</c>.</param>
        /// <returns><c>false</c> for anything that is not a <see cref="PdfNumber"/>.</returns>
        /// <remarks>
        /// The counterpart of <see cref="PdfNameTree.KeyText"/>, and it returns a <c>bool</c> rather
        /// than a sentinel because <b>every</b> long is a legal key here — there is no value that
        /// could mean "not a key". A <c>-1</c> convention would collide with a real, if unusual,
        /// entry.
        /// </remarks>
        public static bool TryKey(PdfObject key, out long value)
        {
            if (key is PdfNumber number)
            {
                value = number.LongValue;
                return true;
            }

            value = 0;
            return false;
        }
    }
}
