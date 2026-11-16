// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Images
{
    /// <summary>
    /// Every image XObject a document's pages reach — through their own <c>/Resources</c>, and
    /// through the form XObjects, tiling patterns, Type 3 fonts and annotation appearance streams
    /// under them, nested up to <see cref="PdfLimits.MaxFormXObjectDepth"/> — one entry per object,
    /// with the pages and resource names that reach it.
    /// </summary>
    /// <remarks>
    /// <para><b>One walk for two operations.</b> <see cref="PdfImageReplacer"/> built this index to
    /// find and replace an image wherever it is drawn, and <c>PdfColorSpaceConverter</c> iterated
    /// the pages' own <c>/XObject</c> dictionaries and nothing deeper — so an RGB image inside a
    /// form XObject (a letterhead, a logo placed by a layout tool) survived a conversion that
    /// reported the document converted, and an image two pages shared was converted, and counted,
    /// twice. Both now read this.</para>
    ///
    /// <para><b>Every container that paints with resources of its own</b> is walked: a form XObject
    /// (§8.10), a tiling pattern (§8.7.3.1, <c>/PatternType 1</c>), a Type 3 font's glyph
    /// procedures (§9.6.4, the font's <c>/Resources</c>) and each annotation appearance stream,
    /// every state of <c>/N</c>, <c>/R</c> and <c>/D</c> (§12.5.5), on the page whose <c>/Annots</c>
    /// lists it — or a grayscale conversion would leave an RGB logo on a stamp, in a tiling pattern
    /// or in a Type 3 glyph behind, and <see cref="PdfImageReplacer.FindImages(System.IO.Stream)"/>
    /// not list the image to replace.
    /// A shading pattern has no resources and draws no image.</para>
    ///
    /// <para><b>Inline images are not here</b>, and cannot be: they have no object to list, convert
    /// or replace. A page that draws one is not what either caller can change.</para>
    ///
    /// <para>A form that references itself, or a cycle of peers, is visited at most once per page
    /// walk; without that an adversarial file loops until the depth cap once per entry point.</para>
    /// </remarks>
    internal sealed class DocumentImageIndex
    {
        private readonly Dictionary<int, Entry> _byObject = new Dictionary<int, Entry>();

        /// <summary>
        /// How many containers — form XObjects, tiling patterns, Type 3 fonts and appearance streams
        /// — the walk descended into: the cycle guard's evidence. A test of the guard cannot assert
        /// on the answer: without it a self-referencing form is walked again at every level down to
        /// the depth cap and its image is still found once, so only the amount of work shows the
        /// guard is there.
        /// </summary>
        internal int FormsVisited { get; private set; }

        /// <summary>
        /// How many times the walk read a resource dictionary's entries: the shared-dictionary
        /// guard's evidence, for the same reason <see cref="FormsVisited"/> is the cycle guard's.
        /// </summary>
        internal int ResourcesWalked { get; private set; }

        /// <summary>
        /// The <c>/Annots</c> entries this index may still examine, across every page it walks:
        /// pages may name one array, so pages × entries is a product the file chooses on both
        /// sides, bounded by <see cref="PdfLimits.MaxAnnotationsPerDocument"/> as
        /// <c>AnnotationSubtypeWalk.Entries</c> bounds it.
        /// </summary>
        private long _annotationsLeft = PdfLimits.MaxAnnotationsPerDocument;

        /// <summary>
        /// Why the walk stopped examining annotations, or null when it examined every entry; the
        /// same sentence goes to <see cref="DocumentEditDiagnostics"/>.
        /// </summary>
        internal string AnnotationCut { get; private set; }

        /// <summary>The resource dictionaries this page walk has read.</summary>
        /// <remarks>
        /// The container guard stops a form from being entered twice, but not a resource dictionary
        /// many containers share from being read once per container. N forms whose
        /// <c>/Resources</c> is one dictionary naming all N cost N×N entry reads on every page:
        /// 50 000 small forms, a file of a few megabytes, ran to billions. A dictionary read once has
        /// nothing new to give. Met again nearer the page, it is still not read, for the reason the
        /// containers under it would not be entered again either: the guard is per page walk, not
        /// per depth.
        /// </remarks>
        private HashSet<PdfDictionary> _walked;

        /// <summary>
        /// Walks every page. <paramref name="onResources"/>, when given, sees each resource
        /// dictionary the walk visits — a page's, then each container's under it — with the page
        /// index, so a caller reading another resource category walks the same dictionaries.
        /// <paramref name="onContainer"/> sees each container itself — a form XObject, a tiling
        /// pattern, a Type 3 font, an appearance stream — once per page walk, for what a container
        /// declares of its own (a transparency <c>/Group</c>).
        /// </summary>
        internal static DocumentImageIndex Build(PdfDocument doc, Action<int, PdfDictionary> onResources = null,
            Action<int, PdfDictionary> onContainer = null)
        {
            var index = new DocumentImageIndex();
            for (int p = 0; p < doc.Pages.Count; p++) index.WalkOnePage(doc, p, onResources, onContainer);
            return index;
        }

        /// <summary>The same walk over one page — its resources, its containers and its annotations' appearances — for a caller that needs no other.</summary>
        internal static void WalkPage(PdfDocument doc, int pageIndex, Action<int, PdfDictionary> onResources,
            Action<int, PdfDictionary> onContainer = null)
            => new DocumentImageIndex().WalkOnePage(doc, pageIndex, onResources, onContainer);

        private void WalkOnePage(PdfDocument doc, int p, Action<int, PdfDictionary> onResources, Action<int, PdfDictionary> onContainer)
        {
            var visited = new HashSet<int>();
            _walked = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            Walk(doc, doc.Pages[p].Resources, p, 0, false, visited, onResources, onContainer);
            WalkAppearances(doc, doc.Pages[p].Dictionary, p, visited, onResources, onContainer);
        }

        /// <summary>
        /// The same walk from one container — a form XObject, a tiling pattern, a Type 3 font —
        /// instead of a page: the container itself to <paramref name="onContainer"/>, then every
        /// resource dictionary and container under it, each once. The page index passed is -1.
        /// </summary>
        internal static void WalkContainer(PdfDocument doc, PdfDictionary container,
            Action<int, PdfDictionary> onResources, Action<int, PdfDictionary> onContainer)
        {
            if (container == null) return;
            var index = new DocumentImageIndex { _walked = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance) };
            index.Descend(doc, container, -1, 0, new HashSet<int>(), onResources, onContainer);
        }

        /// <summary>
        /// Each appearance stream of each annotation the page lists: <c>/N</c>, <c>/R</c> and
        /// <c>/D</c>, each a stream or a dictionary of appearance states (§12.5.5). Every state
        /// counts — a check box draws its off state as surely as its on one.
        /// </summary>
        private void WalkAppearances(
            PdfDocument doc, PdfDictionary page, int pageIndex, HashSet<int> visited,
            Action<int, PdfDictionary> onResources, Action<int, PdfDictionary> onContainer)
        {
            if (!(doc.ResolveObject(page?[PdfName.Annots]) is PdfArray annots)) return;

            for (int i = 0; i < annots.Count; i++)
            {
                if (--_annotationsLeft < 0)
                {
                    if (AnnotationCut == null)
                    {
                        AnnotationCut = $"the pages name more than {PdfLimits.MaxAnnotationsPerDocument} annotation entries between them; " +
                                        $"the appearance streams from page {pageIndex} on were not walked";
                        DocumentEditDiagnostics.Report("DocumentImageIndex.Build", AnnotationCut);
                    }
                    return;
                }
                if (!(doc.ResolveObject(annots[i]) is PdfDictionary annot)) continue;
                if (!(doc.ResolveObject(annot[PdfName.AP]) is PdfDictionary ap)) continue;

                foreach (var key in AppearanceKeys)
                {
                    var entry = doc.ResolveObject(ap[key]);
                    if (entry is PdfStream stream)
                    {
                        Descend(doc, stream, pageIndex, 0, visited, onResources, onContainer);
                    }
                    else if (entry is PdfDictionary states)
                    {
                        foreach (var state in states)
                            if (doc.ResolveObject(state.Value) is PdfStream stateStream)
                                Descend(doc, stateStream, pageIndex, 0, visited, onResources, onContainer);
                    }
                }
            }
        }

        private static readonly PdfName[] AppearanceKeys = { PdfName.N, PdfName.R, PdfName.D };

        /// <summary>
        /// Walks the <c>/Resources</c> of a container that paints with its own — once per page walk,
        /// by object number, which is what stops a cycle.
        /// </summary>
        private void Descend(
            PdfDocument doc, PdfDictionary container, int pageIndex, int depth, HashSet<int> visited,
            Action<int, PdfDictionary> onResources, Action<int, PdfDictionary> onContainer)
        {
            if (container.ObjectNumber > 0 && !visited.Add(container.ObjectNumber)) return;
            FormsVisited++;
            onContainer?.Invoke(pageIndex, container);
            Walk(doc, doc.ResolveObject(container[PdfName.Resources]) as PdfDictionary,
                 pageIndex, depth + 1, true, visited, onResources, onContainer);
        }

        private void Walk(
            PdfDocument doc, PdfDictionary resources, int pageIndex, int depth,
            bool viaForm, HashSet<int> visitedForms, Action<int, PdfDictionary> onResources,
            Action<int, PdfDictionary> onContainer)
        {
            if (resources == null || depth > PdfLimits.MaxFormXObjectDepth) return;
            if (!_walked.Add(resources)) return;
            ResourcesWalked++;
            onResources?.Invoke(pageIndex, resources);

            if (doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary xobj)
            {
                foreach (var kvp in xobj)
                {
                    if (!(doc.ResolveObject(kvp.Value) is PdfStream stream)) continue;
                    var subtype = PdfResolve.Name(doc, stream, PdfName.Subtype);

                    if (subtype == PdfName.Image)
                        Add(stream, kvp.Key.Value, pageIndex, viaForm);
                    else if (subtype == PdfName.Form)
                        Descend(doc, stream, pageIndex, depth, visitedForms, onResources, onContainer);
                }
            }

            // A tiling pattern paints its cell with resources of its own (§8.7.3.1).
            if (doc.ResolveObject(resources[PdfName.Pattern]) is PdfDictionary patterns)
            {
                foreach (var kvp in patterns)
                    if (doc.ResolveObject(kvp.Value) is PdfStream pattern
                        && PdfResolve.Int(doc, pattern, PdfName.PatternType, 0) == 1)
                        Descend(doc, pattern, pageIndex, depth, visitedForms, onResources, onContainer);
            }

            // A Type 3 font's glyph procedures paint with the font's /Resources (§9.6.4). One with
            // none uses the resources of what shows the text, which this walk is already in.
            if (doc.ResolveObject(resources[PdfName.Font]) is PdfDictionary fonts)
            {
                foreach (var kvp in fonts)
                    if (doc.ResolveObject(kvp.Value) is PdfDictionary font
                        && PdfResolve.Name(doc, font, PdfName.Subtype) == PdfName.Type3)
                        Descend(doc, font, pageIndex, depth, visitedForms, onResources, onContainer);
            }
        }

        private void Add(PdfStream stream, string resourceName, int pageIndex, bool viaForm)
        {
            int objNum = stream.ObjectNumber;
            if (objNum <= 0) return;
            if (!_byObject.TryGetValue(objNum, out var entry))
            {
                entry = new Entry(stream);
                _byObject[objNum] = entry;
            }
            // Sets beside the lists: one image under many names, or on many pages, was a linear search a
            // reference — twenty thousand names on each of five hundred pages took the PDF/A validator's
            // transparency scan 278 s.
            if (entry.PageSet.Add(pageIndex)) entry.Pages.Add(pageIndex);
            if (entry.NameSet.Add(resourceName)) entry.Names.Add(resourceName);
            if (!viaForm) entry.ReachedDirectly = true;
        }

        /// <summary>Every image found, in object-number order.</summary>
        internal IEnumerable<KeyValuePair<int, Entry>> Entries
        {
            get
            {
                var numbers = new List<int>(_byObject.Keys);
                numbers.Sort();
                foreach (int n in numbers)
                    yield return new KeyValuePair<int, Entry>(n, _byObject[n]);
            }
        }

        internal PdfStream Find(int objNum) =>
            _byObject.TryGetValue(objNum, out var e) ? e.Stream : null;

        internal string PrimaryName(int objNum) =>
            _byObject.TryGetValue(objNum, out var e) && e.Names.Count > 0
                ? e.Names[0]
                : objNum.ToString();

        internal IReadOnlyList<int> PagesReferencing(int objNum)
        {
            if (!_byObject.TryGetValue(objNum, out var e)) return new List<int>();
            e.Pages.Sort();
            return e.Pages;
        }

        internal IReadOnlyList<DocumentImageInfo> ToInfoList(PdfDocument doc)
        {
            var list = new List<DocumentImageInfo>();
            foreach (var kvp in _byObject)
            {
                var e = kvp.Value;
                e.Pages.Sort();
                e.Names.Sort(StringComparer.Ordinal);
                list.Add(new DocumentImageInfo(
                    kvp.Key,
                    PdfResolve.Int(doc, e.Stream, PdfName.Width, 0),
                    PdfResolve.Int(doc, e.Stream, PdfName.Height, 0),
                    e.Pages, e.Names, !e.ReachedDirectly));
            }
            list.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));
            return list;
        }

        internal sealed class Entry
        {
            internal Entry(PdfStream stream) { Stream = stream; }

            internal PdfStream Stream { get; }
            internal List<int> Pages { get; } = new List<int>();
            internal List<string> Names { get; } = new List<string>();
            internal HashSet<int> PageSet { get; } = new HashSet<int>();
            internal HashSet<string> NameSet { get; } = new HashSet<string>(StringComparer.Ordinal);
            internal bool ReachedDirectly { get; set; }
        }
    }
}
