// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Why a name-tree or number-tree walk stopped before it had read the whole tree
    /// (§7.9.6, §7.9.7).
    /// </summary>
    /// <remarks>
    /// <para><b>A <c>bool</c> was not enough, and that is why a second walk existed.</b>
    /// <see cref="PdfNameTree"/> and <see cref="PdfNumberTree"/> answered only "complete or not",
    /// so a caller that has to <i>tell a user</i> what happened could not use them: the deep
    /// accessibility checker writes "The /ParentTree could only be read partially (…)" into a
    /// PDF/UA finding, and "…" has to say which limit was reached. It therefore kept its own
    /// traversal — with its own cycle guard, depth cap and slot budget — which is exactly the
    /// second copy this repository's record says a guard eventually goes missing from.</para>
    ///
    /// <para>Naming the cause is what let that copy be retired without taking the diagnostic with
    /// it. The limits stay the caller's, for the reason <c>PdfLimits</c>' header gives: centralising
    /// a value is not the same as unifying it, and a checker running over untrusted documents
    /// deliberately holds tighter caps than a reader does.</para>
    /// </remarks>
    public enum PdfTreeTruncation
    {
        /// <summary>The whole tree was read.</summary>
        None = 0,

        /// <summary>A <c>/Kids</c> chain went deeper than the caller's depth cap.</summary>
        Depth,

        /// <summary>The caller's entry budget ran out before the leaves did.</summary>
        Entries,

        /// <summary>
        /// The caller's <c>/Kids</c> slot budget ran out. Distinct from <see cref="Entries"/>
        /// because a tree whose nodes share one <c>/Kids</c> array costs slots without producing
        /// entries — the shape neither a depth cap nor a visited set bounds.
        /// </summary>
        KidSlots,

        /// <summary>
        /// A node the tree names could not be parsed. Distinct from a node the file does not
        /// define, which is the null object (§7.3.9) and an empty subtree: an unreadable node hides
        /// whatever it held, so the tree read is partial and a lookup's "no such key" is not an
        /// answer, and the walk does not call the tree whole.
        /// </summary>
        UnreadableNode,
    }
}
