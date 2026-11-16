// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// What <see cref="ImagePlacementScanner"/>'s walk learned about <c>/XObject</c> use: the names
    /// a <c>Do</c> invoked through each resource dictionary, and every dictionary whose
    /// <c>/Resources</c> the walk read — pages and the streams it walked (forms, appearance
    /// streams, tiling patterns, soft-mask groups).
    /// </summary>
    /// <remarks>
    /// It rides on the placement walk rather than a third one: that walk already resolves every
    /// <c>Do</c>, follows a form without <c>/Resources</c> into its invoker's, and sets a reason for
    /// every hole it leaves (an unresolvable <c>Do</c>, a Type 3 font with glyph procedures, a
    /// cycle, a depth or operator ceiling, an exception).
    /// </remarks>
    internal sealed class XObjectUse
    {
        /// <summary>Resource dictionary → the names a <c>Do</c> invoked through it (decoded, §7.3.5).</summary>
        internal readonly Dictionary<object, HashSet<string>> Invoked =
            new Dictionary<object, HashSet<string>>(PdfReferenceEquality.Instance);

        /// <summary>Every page and stream whose <c>/Resources</c> the walk used.</summary>
        internal readonly HashSet<object> Holders = new HashSet<object>(PdfReferenceEquality.Instance);

        /// <summary>Why the evidence is incomplete for this question, beyond the scanner's own reason.</summary>
        internal string Refusal;

        internal void NoteWalked(PdfDictionary resources)
        {
            if (resources != null && !Invoked.ContainsKey(resources))
                Invoked[resources] = new HashSet<string>(StringComparer.Ordinal);
        }

        internal void NoteInvoked(PdfDictionary resources, PdfName name)
        {
            if (resources == null || name == null) return;
            NoteWalked(resources);
            Invoked[resources].Add(name.Value);
        }

        internal void NoteHolder(PdfDictionary holder)
        {
            if (holder != null) Holders.Add(holder);
        }

        /// <summary>
        /// A page the walk reached. Refuses when its content was not read in full — a part that
        /// would not decode, or a <c>/Contents</c> entry that is not a stream, both of which the
        /// shared content reader skips — and when the <c>/Resources</c> it inherits through
        /// <c>/Parent</c> (what a renderer follows) is not the dictionary the page-tree walk handed
        /// down.
        /// </summary>
        internal void NotePage(PdfDictionary page, PdfDictionary resources, PdfDictionary inherited,
            Dictionary<int, PdfObject> objects, bool partUnread)
        {
            if (page == null) return;
            Holders.Add(page);

            if (partUnread)
                Refusal = "a part of a page's content stream could not be decoded";

            PdfObject contents = page[PdfName.Contents];
            if (contents != null)
            {
                PdfObject resolved = ContentScanGraph.Deref(contents, objects);
                if (resolved is PdfArray parts)
                {
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (!(ContentScanGraph.Deref(parts[i], objects) is PdfStream))
                            Refusal = "a page's /Contents array holds an entry that is not a stream";
                    }
                }
                else if (!(resolved is PdfStream))
                {
                    Refusal = "a page's /Contents is not a stream";
                }
            }

            if (ContentScanGraph.Deref(page[PdfName.Resources], objects) is PdfDictionary) return;

            PdfDictionary viaParent = null;
            var node = ContentScanGraph.Deref(page[PdfName.Parent], objects) as PdfDictionary;
            for (int hops = 0; node != null && hops < MaxParentHops; hops++)
            {
                if (ContentScanGraph.Deref(node[PdfName.Resources], objects) is PdfDictionary found)
                {
                    viaParent = found;
                    break;
                }
                node = ContentScanGraph.Deref(node[PdfName.Parent], objects) as PdfDictionary;
            }

            if (!ReferenceEquals(viaParent, inherited ?? resources))
                Refusal = "a page inherits /Resources through /Parent from a different dictionary " +
                          "than the page-tree walk reached it with";
        }

        private const int MaxParentHops = 64;
    }

    /// <summary>
    /// Removes <c>/XObject</c> resource entries no content stream invokes, so that
    /// <see cref="OptimizationOptions.RemoveUnusedObjects"/> then drops the objects they named.
    /// See <see cref="OptimizationOptions.RemoveUnusedXObjectResources"/> for the rules.
    /// </summary>
    internal static class UnusedXObjectResources
    {
        /// <summary>Bounds the census's descent through direct (nested) objects.</summary>
        private const int MaxDirectDepth = 64;

        /// <summary>
        /// Removes the unused entries and returns how many. <paramref name="refusal"/> is non-null
        /// when nothing was removed because the evidence was incomplete.
        /// </summary>
        internal static int Remove(Dictionary<int, PdfObject> objects, PdfDictionary trailer,
            OptimizationOptions options, out string refusal)
        {
            var use = new XObjectUse();
            ImagePlacementScanner.Scan(objects, trailer, options, out string reason, use);
            refusal = reason ?? use.Refusal;
            if (refusal != null) return 0;

            var census = new Census(use, objects);
            census.Visit(trailer, 0);
            foreach (var obj in objects.Values)
            {
                census.Visit(obj, 0);
                if (census.Refusal != null) break;
            }
            if (census.Refusal != null)
            {
                refusal = census.Refusal;
                return 0;
            }

            // A resource dictionary is eligible when every holder of it was walked and nothing else
            // refers to it.
            var eligible = new HashSet<object>(PdfReferenceEquality.Instance);
            foreach (var key in use.Invoked.Keys)
            {
                var resources = (PdfDictionary)key;
                if (census.Ineligible.Contains(resources)) continue;
                if (census.Numbers.TryGetValue(resources, out int number) &&
                    census.Count(census.References, number) != census.Count(census.ResourcesReferences, number))
                    continue;
                eligible.Add(resources);
            }

            // Every name invoked through any walked dictionary that shares the /XObject subdictionary.
            var used = new Dictionary<object, HashSet<string>>(PdfReferenceEquality.Instance);
            foreach (var pair in use.Invoked)
            {
                if (!(ContentScanGraph.Deref(((PdfDictionary)pair.Key)[PdfName.XObject], objects)
                        is PdfDictionary xobjects)) continue;
                if (!used.TryGetValue(xobjects, out var names))
                    used[xobjects] = names = new HashSet<string>(StringComparer.Ordinal);
                names.UnionWith(pair.Value);
            }

            int removed = 0;
            foreach (var pair in used)
            {
                var xobjects = (PdfDictionary)pair.Key;
                if (!census.XObjectHolders.TryGetValue(xobjects, out var holders)) continue;

                bool everyHolderEligible = true;
                foreach (var holder in holders)
                {
                    if (!eligible.Contains(holder)) { everyHolderEligible = false; break; }
                }
                if (!everyHolderEligible) continue;
                if (census.Numbers.TryGetValue(xobjects, out int number) &&
                    census.Count(census.References, number) != census.Count(census.XObjectReferences, number))
                    continue;

                var unused = new List<PdfName>();
                foreach (var name in xobjects.Keys)
                {
                    if (!pair.Value.Contains(name.Value)) unused.Add(name);
                }
                foreach (var name in unused)
                {
                    if (xobjects.Remove(name)) removed++;
                }
            }

            return removed;
        }

        /// <summary>
        /// One pass over every object: how often each object number is referenced, which of those
        /// references are a <c>/Resources</c> or an <c>/XObject</c> value, and who holds each
        /// resource and <c>/XObject</c> dictionary the walk met.
        /// </summary>
        private sealed class Census
        {
            private readonly XObjectUse _use;

            internal readonly Dictionary<int, int> References = new Dictionary<int, int>();
            internal readonly Dictionary<int, int> ResourcesReferences = new Dictionary<int, int>();
            internal readonly Dictionary<int, int> XObjectReferences = new Dictionary<int, int>();
            internal readonly Dictionary<object, int> Numbers =
                new Dictionary<object, int>(PdfReferenceEquality.Instance);
            internal readonly HashSet<object> Ineligible = new HashSet<object>(PdfReferenceEquality.Instance);
            internal readonly Dictionary<object, List<PdfDictionary>> XObjectHolders =
                new Dictionary<object, List<PdfDictionary>>(PdfReferenceEquality.Instance);
            internal string Refusal;

            private readonly Dictionary<int, PdfObject> _map;

            internal Census(XObjectUse use, Dictionary<int, PdfObject> map)
            {
                _use = use;
                _map = map;
            }

            internal int Count(Dictionary<int, int> counts, int number)
                => counts.TryGetValue(number, out int n) ? n : 0;

            private static void Increment(Dictionary<int, int> counts, int number)
                => counts[number] = counts.TryGetValue(number, out int n) ? n + 1 : 1;

            internal void Visit(PdfObject obj, int depth)
            {
                if (obj == null || Refusal != null) return;
                if (depth > MaxDirectDepth)
                {
                    Refusal = "an object nests direct objects more than " + MaxDirectDepth +
                              " levels deep, so its references were not all counted";
                    return;
                }

                switch (obj)
                {
                    case PdfReference reference:
                        Increment(References, reference.ReferenceObjectNumber);
                        break;

                    case PdfArray array:
                        for (int i = 0; i < array.Count; i++) Visit(array[i], depth + 1);
                        break;

                    case PdfDictionary dictionary:
                        foreach (var key in dictionary.Keys)
                        {
                            PdfObject value = dictionary[key];
                            if (key.Value == "Resources") NoteResourcesHolder(dictionary, value);
                            else if (key.Value == "XObject") NoteXObjectHolder(dictionary, value);
                            Visit(value, depth + 1);
                        }
                        break;
                }
            }

            private void NoteResourcesHolder(PdfDictionary holder, PdfObject value)
            {
                if (value is PdfReference reference) Increment(ResourcesReferences, reference.ReferenceObjectNumber);

                if (!(Lookup(value) is PdfDictionary resources)) return;
                if (value is PdfReference r) Numbers[resources] = r.ReferenceObjectNumber;
                if (!_use.Invoked.ContainsKey(resources)) return;

                // A /Pages node's /Resources is used only by the pages that inherit it, and the
                // walk reached every page in the tree.
                if (_use.Holders.Contains(holder) || IsPageTreeNode(holder)) return;
                Ineligible.Add(resources);
            }

            private void NoteXObjectHolder(PdfDictionary holder, PdfObject value)
            {
                if (value is PdfReference reference) Increment(XObjectReferences, reference.ReferenceObjectNumber);

                if (!(Lookup(value) is PdfDictionary xobjects)) return;
                if (value is PdfReference r) Numbers[xobjects] = r.ReferenceObjectNumber;
                if (!XObjectHolders.TryGetValue(xobjects, out var holders))
                    XObjectHolders[xobjects] = holders = new List<PdfDictionary>();
                holders.Add(holder);
            }

            private PdfObject Lookup(PdfObject value) => ContentScanGraph.Deref(value, _map);

            private bool IsPageTreeNode(PdfDictionary holder)
                => ContentScanGraph.Deref(holder[PdfName.Type], _map) is PdfName type && type.Value == "Pages";
        }
    }
}
