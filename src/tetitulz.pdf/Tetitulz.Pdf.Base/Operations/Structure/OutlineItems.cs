// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// The one bounded walk of a document outline's items (§12.3.3): depth first in reading order —
    /// an item, then its <c>/First</c> subtree, then its <c>/Next</c> sibling.
    /// </summary>
    /// <remarks>
    /// <para>One visited set across the outline (by identity) walks an item reached twice — a
    /// <c>/Next</c> that loops, a <c>/First</c> naming an ancestor — once. The recursion into
    /// <c>/First</c> stops at <see cref="PdfLimits.MaxTreeDepth"/>, and the cut is reported once when it
    /// leaves items unvisited; a long flat <c>/Next</c> chain does not deepen it. A <c>/First</c> or
    /// <c>/Next</c> that names an object the parser cannot read ends its list as the outline's end
    /// would, and is reported once too: a reader rebuilding the outline from the walk's items would
    /// otherwise write the prefix back as the whole.</para>
    /// <para>The action dispatcher (outline actions), the PDF/UA-1 checker (outline titles' language),
    /// the PDF/UA-2 structure checker (titles and destinations) and the bookmark reader (the tree a
    /// viewer shows, rebuilt from the depths) all read the outline through it.</para>
    /// </remarks>
    public static class OutlineItems
    {
        /// <summary>
        /// Visits each outline item of <paramref name="catalog"/> until <paramref name="visit"/> answers
        /// <see langword="false"/>. <paramref name="truncation"/>, when given, is told once if the depth
        /// cap left items below it unvisited, and once if an item's <c>/First</c> or <c>/Next</c> could
        /// not be read and the items after it were not visited.
        /// </summary>
        public static void Walk(PdfDocument doc, PdfDictionary catalog, Func<PdfDictionary, bool> visit,
            Action<string> truncation = null)
        {
            if (visit == null) throw new ArgumentNullException(nameof(visit));
            Walk(doc, catalog, (item, _) => visit(item), truncation);
        }

        /// <summary>
        /// Visits each outline item with its depth — 0 for a top-level item, one more for each
        /// <c>/First</c> descended — in reading order, so a caller rebuilding the tree makes an item
        /// at depth <i>d</i> a child of the last item it saw at depth <i>d</i> − 1.
        /// <paramref name="truncation"/> is as for the depthless overload.
        /// </summary>
        public static void Walk(PdfDocument doc, PdfDictionary catalog, Func<PdfDictionary, int, bool> visit,
            Action<string> truncation = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (visit == null) throw new ArgumentNullException(nameof(visit));
            if (!(doc.ResolveObject(catalog?[PdfName.Outlines]) is PdfDictionary root)) return;

            var visited = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            bool stopped = false, cutReported = false, unreadableReported = false;

            // The item an entry names, or null at a list's end — which an unreadable object also is,
            // the parser answering it with the null object; that one is told apart by the parse
            // error it leaves, read at once, and said.
            PdfDictionary ItemNamedBy(PdfObject entry, string slot)
            {
                if (entry == null) return null;
                var resolved = doc.ResolveObject(entry);
                if (resolved is PdfDictionary item) return item;
                if (!unreadableReported && (resolved == null || resolved is PdfNull)
                    && Core.Parsing.PdfParser.LastObjectParseError is string reason)
                {
                    unreadableReported = true;
                    truncation?.Invoke($"an outline item's {slot} names an object that could not be read ({reason}); the items after it were not visited");
                }
                return null;
            }

            void Children(PdfDictionary parent, int depth)
            {
                if (depth >= PdfLimits.MaxTreeDepth)
                {
                    if (!cutReported && parent[PdfName.First] != null)
                    {
                        cutReported = true;
                        truncation?.Invoke($"the outline was cut at {PdfLimits.MaxTreeDepth} levels; items below that depth were not visited");
                    }
                    return;
                }

                var item = ItemNamedBy(parent[PdfName.First], "/First");
                while (!stopped && item != null && visited.Add(item))
                {
                    if (!visit(item, depth)) { stopped = true; return; }
                    Children(item, depth + 1);
                    item = ItemNamedBy(item[PdfName.Next], "/Next");
                }
            }

            Children(root, 0);
        }
    }
}
