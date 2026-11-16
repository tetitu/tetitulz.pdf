// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// The single traversal for a PDF <b>name tree</b> (ISO 32000-1 §7.9.6): the
    /// <c>/Kids</c>-and-<c>/Names</c> structure behind <c>/Dests</c>, <c>/EmbeddedFiles</c>,
    /// <c>/JavaScript</c>, <c>/AP</c> and the rest of the catalog's name dictionary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The key <c>/Names</c> means two different things, and confusing them is the classic bug
    /// here.</b> On the <b>catalog</b> it is the <i>name dictionary</i> — keyed by category
    /// (<c>/Dests</c>, <c>/EmbeddedFiles</c>, <c>/JavaScript</c>, …), each value a name-tree
    /// <b>root</b>. On a name-tree <b>node</b> it is the leaf array
    /// <c>[key0 value0 key1 value1 …]</c>, with <c>/Kids</c> holding intermediate node references.
    /// This type walks the second; callers pick the category off the first and pass the root in.
    /// </para>
    /// <para>
    /// <b>Why it lives in Core.</b> Six open-coded copies existed across `Base.Operations`,
    /// `Plugins.PageEdits`, `Plugins.Maintenance` and `Plugins.Conformance` — three rings — and only
    /// one of them guarded against a <c>/Kids</c> cycle or bounded its entry count. A plugin cannot
    /// reference a feature and two plugins may not reference each other, so the shared code goes
    /// <i>down</i> rather than sideways; Core is the only ring all of them already reference. See
    /// GAPS.md §15c.
    /// </para>
    /// <para>
    /// <b>Values come back unresolved</b> — normally the <see cref="PdfReference"/> the leaf holds —
    /// because callers need different things from them: one remaps through an object map, another
    /// resolves to a file-spec dictionary, another collects the referenced graph. Resolving here
    /// would force every caller to pay for it and would lose the reference identity that two of them
    /// need. <see cref="KeyText"/> is offered for the callers that want the key as text.
    /// </para>
    /// <para>
    /// <b>Both guards are load-bearing on untrusted input.</b> The depth cap alone does not bound a
    /// <c>/Kids</c> cycle cheaply: a two-node cycle is shallow, so it would spin to
    /// <c>PdfLimits.MaxTreeDepth</c> once per entry point. The visited set stops it at the
    /// second visit. The entry budget is threaded by <c>ref</c> so it spans <i>every</i> category and
    /// <i>every</i> source document in one operation — a per-tree cap would not bound merging a
    /// hundred trees.
    /// </para>
    /// <para>
    /// <b>The walk itself is <c>PdfTreeWalk</c>, shared with <see cref="PdfNumberTree"/>.</b> §7.9.7
    /// defines a number tree by reference to §7.9.6 and changes only the leaf array's key and its
    /// key type, so a second copy of this recursion would differ from it in one string — and this
    /// file's own history is that a duplicated traversal is where a guard goes missing.
    /// </para>
    /// </remarks>
    public static class PdfNameTree
    {
        /// <summary>
        /// Flattens the name tree rooted at <paramref name="root"/> into its (key, value) pairs, in
        /// the order the tree stores them.
        /// </summary>
        /// <param name="root">The name-tree root — a node dictionary or a reference to one.</param>
        /// <param name="doc">Document used to resolve <c>/Kids</c> references.</param>
        /// <param name="into">Receives the pairs. Keys and values are both <b>unresolved</b>.</param>
        /// <param name="remainingEntries">
        /// Shared entry budget, decremented per pair. Pass the same variable across every category
        /// and document in one operation.
        /// </param>
        /// <returns>
        /// <c>false</c> when the walk was cut short by <paramref name="remainingEntries"/> or by
        /// <see cref="PdfLimits.MaxTreeDepth"/>, so a caller can report a truncated result rather
        /// than silently emitting a tree that is missing entries. <c>true</c> when the whole tree
        /// was visited — including the vacuous cases (null root, empty tree).
        /// </returns>
        public static bool TryFlatten(
            PdfObject root, PdfDocument doc,
            IList<KeyValuePair<PdfObject, PdfObject>> into, ref int remainingEntries)
            => PdfTreeWalk.TryFlatten(root, doc, PdfName.Names, into, ref remainingEntries);

        /// <summary>
        /// The text form of a name-tree key, or <c>null</c> when the object is neither a string nor
        /// a name.
        /// </summary>
        /// <remarks>
        /// §7.9.6 says keys are <b>strings</b>. <see cref="PdfName"/> is accepted because real files
        /// carry it; a caller that must round-trip the
        /// key's exact bytes should read <see cref="PdfString.RawBytes"/> off the key itself rather
        /// than using this, since text conversion is lossy for a key that is not valid
        /// PDFDocEncoding or UTF-16BE.
        /// </remarks>
        public static string KeyText(PdfObject key) =>
            key is PdfString s ? s.GetText()
            : key is PdfName n ? n.Value
            : null;

        /// <summary>
        /// Orders two name-tree keys the way §7.9.6 requires: "sorted lexically in ascending order
        /// by key. Shorter keys shall appear before longer ones beginning with the same byte
        /// sequence", with keys compared "on a simple byte-by-byte basis".
        /// </summary>
        /// <remarks>
        /// <para><b>Not <c>string.Compare(…, StringComparison.Ordinal)</c> over the decoded
        /// text.</b> A key is stored either PDFDocEncoded or as UTF-16BE behind an <c>FE FF</c>
        /// marker (§7.9.2.2), and the two sort differently as text than as bytes — every UTF-16BE
        /// key begins 0xFE, so by byte value it sorts after every PDFDocEncoded key, which is the
        /// order a reader's binary search over the leaf assumes.</para>
        /// <para>This lived in <c>Plugins.PageEdits</c>, where merge and split used it, while the
        /// two Base writers that rebuild a name tree (named destinations, attachments) sorted by
        /// decoded text. One rule, so it moved down to the ring all three reach.</para>
        /// </remarks>
        public static int CompareKeys(byte[] a, byte[] b)
        {
            if (a == null) return b == null ? 0 : -1;
            if (b == null) return 1;
            int n = System.Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            }
            return a.Length.CompareTo(b.Length);
        }
    }
}
