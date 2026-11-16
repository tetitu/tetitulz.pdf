// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// The one page-tree walk the optimizer's scanners share, carrying §7.7.3.4's inheritable
    /// <c>/Resources</c> down to each leaf.
    /// </summary>
    /// <remarks>
    /// <para>One walk, so the scanners agree on what each page inherits: on a file that hoists
    /// <c>/Resources</c> onto the <c>/Pages</c> node — common producers' output, older Word exporters —
    /// a walk that did not thread inherited resources would see a null resource dictionary on every
    /// page and record no image placement, and <c>TargetDpi</c> would silently do nothing while
    /// reporting a completed scan.</para>
    /// <para>Leaf classification stays in <see cref="PageTreeShape"/> — this walk decides which
    /// nodes to visit, not what a page is.</para>
    ///
    /// <para><b>The optimizer walks through here too.</b> A walk over <i>object numbers</i> —
    /// starting at <c>catalog[/Pages] is PdfReference</c> and pushing only
    /// <c>kids[i] is PdfReference</c> — would end at a page-tree node written <b>directly</b>
    /// (malformed under Table 28, read without complaint by Core). That is why
    /// <see cref="PageEntry.ObjectNumber"/> exists: the optimizer needs a number for its diagnostics,
    /// and a directly-written page honestly has none.</para>
    /// </remarks>
    internal static class PageTreeWalk
    {
        /// <summary>One leaf page and the <c>/Resources</c> in force for it.</summary>
        internal readonly struct PageEntry
        {
            internal readonly PdfDictionary Page;

            /// <summary>
            /// The nearest ancestor's <c>/Resources</c>, or <c>null</c> when no ancestor defines
            /// one. The page's own entry, when it has one, takes precedence and is read by the
            /// caller — this is the fallback §7.7.3.4 prescribes.
            /// </summary>
            internal readonly PdfDictionary InheritedResources;

            /// <summary>
            /// The object number the reference that reached this page named, or <c>-1</c> when the
            /// page dictionary is written directly inside its parent's <c>/Kids</c>.
            ///
            /// <para><b>-1 is an answer, not a failure.</b> A directly-written page has no object
            /// number, and a caller that prints one must say so rather than print <c>-1</c> as
            /// though it were a slot the reader could go and look at.</para>
            /// </summary>
            internal readonly int ObjectNumber;

            /// <summary>
            /// Each §7.7.3.4 inheritable attribute (<see cref="PdfPageTreeShape.InheritableAttributes"/>) as
            /// its nearest ancestor stores it — raw, a reference left a reference — or <c>null</c> when no
            /// ancestor stores any. Down the <c>/Kids</c> chain the walk arrived by, which is the one Core
            /// inherits along; the page's own entries are the caller's to read.
            /// </summary>
            internal readonly IReadOnlyDictionary<PdfName, PdfObject> InheritedAttributes;

            internal PageEntry(PdfDictionary page, PdfDictionary inherited, int objectNumber,
                IReadOnlyDictionary<PdfName, PdfObject> inheritedAttributes)
            {
                Page = page;
                InheritedResources = inherited;
                ObjectNumber = objectNumber;
                InheritedAttributes = inheritedAttributes;
            }
        }

        /// <summary>
        /// Yields every leaf page reachable from the catalog, depth-first, with the resource
        /// dictionary it inherits. Cycle-guarded by object number.
        /// </summary>
        internal static IEnumerable<PageEntry> Enumerate(
            Dictionary<int, PdfObject> objects, PdfDictionary catalog)
        {
            var stack = new Stack<(PdfObject Node, PdfDictionary Inherited, Dictionary<PdfName, PdfObject> Attributes)>();
            if (catalog?[PdfName.Pages] != null) stack.Push((catalog[PdfName.Pages], null, null));

            var visited = new HashSet<int>();

            while (stack.Count > 0)
            {
                var (node, inherited, attributes) = stack.Pop();

                int objectNumber = -1;
                if (node is PdfReference reference)
                {
                    if (!visited.Add(reference.ReferenceObjectNumber)) continue;
                    objectNumber = reference.ReferenceObjectNumber;
                    node = ContentScanGraph.Deref(node, objects);
                }
                if (!(node is PdfDictionary dictionary)) continue;

                var own = ContentScanGraph.Deref(dictionary[PdfName.Resources], objects) as PdfDictionary ?? inherited;
                var type = ContentScanGraph.Deref(dictionary[PdfName.Type], objects) as PdfName;
                var kids = ContentScanGraph.Deref(dictionary[PdfName.Kids], objects) as PdfArray;

                // Classified structurally, exactly as Core's PdfPageTree does — see PageTreeShape
                // for why "/Type is not /Pages, so it is a page" silently loses whole subtrees.
                if (PageTreeShape.IsLeafPage(type, kids,
                        hasContents: dictionary[PdfName.Contents] != null,
                        hasMediaBox: ContentScanGraph.Deref(dictionary[PdfName.MediaBox], objects) is PdfArray))
                {
                    yield return new PageEntry(dictionary, own, objectNumber, attributes);
                }
                else if (kids != null)
                {
                    // A node's own entries replace its ancestors' for its subtree; copied only when it
                    // has one, so a tree that stores them at its root shares one dictionary throughout.
                    Dictionary<PdfName, PdfObject> forKids = attributes;
                    foreach (PdfName key in PdfPageTreeShape.InheritableAttributes)
                    {
                        PdfObject value = dictionary[key];
                        if (value == null) continue;
                        if (ReferenceEquals(forKids, attributes))
                            forKids = attributes == null
                                ? new Dictionary<PdfName, PdfObject>()
                                : new Dictionary<PdfName, PdfObject>(attributes);
                        forKids[key] = value;
                    }
                    for (int i = 0; i < kids.Count; i++) stack.Push((kids[i], own, forKids));
                }
            }
        }
    }
}
