// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    internal static class PdfPageTree
    {
        // A deeply nested /Pages chain (acyclic, so the visited HashSet does NOT
        // catch it) would otherwise recurse until the native stack overflows —
        // uncatchable in .NET.  The cap is larger than the object-graph limit
        // because the page tree is broader than direct-object nesting; the value
        // lives in PdfLimits so every traversal agrees on it.
        private static int MaxPageTreeDepth => PdfLimits.MaxPageTreeDepth;

        public static List<PdfPage> GetPages(PdfDocument document)
        {
            var pages = new List<PdfPage>();
            if (document.Catalog == null)
                return pages;
            var root = document.Catalog.PageTreeRoot;
            if (root == null)
                return pages;

            var inherited = new InheritedAttributes();
            CollectPages(root, document, pages, inherited, new HashSet<PdfDictionary>(), 0);
            return pages;
        }

        /// <summary>
        /// Tracks inheritable page attributes as they propagate down the page tree.
        /// PDF spec §7.7.3.4: /Resources, /MediaBox, /CropBox, /Rotate are inheritable.
        /// Propagated downward during traversal rather than looked up the tree.
        ///
        /// <para>Those four are the whole inheritable set. <c>/UserUnit</c> sits beside them in
        /// Table 30 and is often assumed to join them, but §7.7.3.4 does not list it, so it is
        /// deliberately absent here and read from the page's own dictionary by
        /// <see cref="PdfPage.UserUnit"/>.</para>
        /// </summary>
        internal struct InheritedAttributes
        {
            public PdfDictionary Resources;
            public PdfArray MediaBox;
            public PdfArray CropBox;
            public int? Rotate;
        }

        private static void CollectPages(PdfDictionary node, PdfDocument document,
            List<PdfPage> pages, InheritedAttributes inherited,
            HashSet<PdfDictionary> visited, int depth)
        {
            // Depth cap (acyclic-deep guard).  The visited HashSet only catches
            // cycles; a long straight /Pages → /Pages → … chain still recurses,
            // so cap the depth and surface it as a recoverable parse error.
            if (depth > MaxPageTreeDepth)
                throw new PdfParseException(
                    $"PDF page tree nesting exceeds {MaxPageTreeDepth} levels.");

            if (!visited.Add(node)) return; // cycle guard

            // Resolved. GetName does not follow a reference, and a null `type` makes the
            // `type != PdfName.Pages` clause below hold — which re-opens the very defect that
            // clause was added to close: an emptied branch `<< /Type <indirect> /Count 0
            // /MediaBox [...] >>` with no /Kids satisfies the structural test and is collected as
            // a PHANTOM PAGE, putting every later page index off by one, silently.
            var type = document.ResolveObject(node[PdfName.Type]) as PdfName
                       ?? node.GetName(PdfName.Type);

            // Update inherited attributes from this node (PDF spec §7.7.3.4).
            var current = inherited;

            if (node[PdfName.Resources] != null)
                current.Resources = document.ResolveObject(node[PdfName.Resources]) as PdfDictionary ?? current.Resources;

            // This node's OWN /MediaBox, kept in a local because the leaf test below needs
            // "this node declared one" and `current.MediaBox` by then also holds an INHERITED
            // box — which would make every childless node under a /Pages node that declares a
            // /MediaBox look like a page.
            PdfArray ownMediaBox = null;
            if (node[PdfName.MediaBox] != null)
            {
                ownMediaBox = node.GetArray(PdfName.MediaBox)
                           ?? document.ResolveObject(node[PdfName.MediaBox]) as PdfArray;
                if (ownMediaBox != null) current.MediaBox = ownMediaBox;
            }

            if (node[PdfName.CropBox] != null)
            {
                var cb = node.GetArray(PdfName.CropBox)
                      ?? document.ResolveObject(node[PdfName.CropBox]) as PdfArray;
                if (cb != null) current.CropBox = cb;
            }

            // /Rotate resolves the indirect case exactly as the two boxes above do, and for the
            // same reason: PdfDictionary.GetInteger tests `v is PdfNumber` and never follows a
            // PdfReference, so a producer that shares one rotation object across a batch scan —
            // `/Rotate 12 0 R` on the /Pages node — would inherit as "no rotation" and every page in
            // the file render, print and extract sideways with nothing reported. PdfPage.Rotate
            // resolves the page's own dictionary the same way.
            if (node[PdfName.Rotate] != null)
            {
                var rotateVal = node.GetInteger(PdfName.Rotate)
                             ?? (document.ResolveObject(node[PdfName.Rotate]) as PdfNumber)?.IntValue;
                if (rotateVal.HasValue)
                    current.Rotate = rotateVal.Value;
            }

            // Recognise leaf page nodes by /Type /Page OR by structural
            // signatures when /Type is missing or wrong (some malformed PDFs
            // omit /Type or write it as a literal string).  A node with no
            // /Kids array but with /Contents or /MediaBox is a page.
            // Resolved BEFORE the leaf test, not after it. GetArray does not follow a reference,
            // and the structural test reads "no /Kids" as evidence of a page — so a /Pages node
            // written `/Kids 7 0 R` that also carries its own /MediaBox (both legal, both common)
            // was classified as a PAGE, added to the list, and returned from before its children
            // were ever resolved: the page-tree ROOT became page 0 and the real pages vanished.
            //
            // The comment that stood here asserted the opposite — that resolving late "can never
            // reclassify a node the structural test already accepted as a page… widens the
            // traversal and cannot narrow it". That is true only when the test does not itself
            // depend on /Kids being absent, and it does. Prose asserting a safety property is not
            // the property; PageTreeIndirectKidsTests is.
            var kids = document.ResolveObject(node[PdfName.Kids]) as PdfArray
                       ?? node.GetArray(PdfName.Kids);
            // The structural clause exists for files that OMIT /Type or write it wrongly, so a node
            // that positively declares /Type /Pages must be excluded from it. Without that, an
            // emptied branch — `<< /Type /Pages /Count 0 /MediaBox [...] >>`, exactly what a
            // page-removal tool leaves behind when it hoists the box — has no /Kids and an own
            // /MediaBox, so it satisfied the clause and was collected as a PHANTOM PAGE. Every page
            // index after it is then off by one, silently.
            bool isPage = PdfPageTreeShape.IsLeafPage(
                type, kids,
                hasContents: node[PdfName.Contents] != null,
                hasMediaBox: ownMediaBox != null);
            if (isPage)
            {
                pages.Add(new PdfPage(node, document, current.Resources,
                    current.MediaBox, current.CropBox, current.Rotate, pages.Count));
                return;
            }

            if (kids == null) return;

            for (int i = 0; i < kids.Count; i++)
            {
                var childObj = document.ResolveObject(kids[i]);
                if (childObj is PdfDictionary childDict)
                {
                    CollectPages(childDict, document, pages, current, visited, depth + 1);
                }
            }
        }
    }
}
