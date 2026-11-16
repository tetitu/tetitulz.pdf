// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Where the forms a flattener stamps onto its pages are registered: in the <c>/XObject</c>
    /// dictionary the page already reads, superseded once for every page of the revision that reads
    /// it — or, for a page whose <c>/Resources</c> is written directly (in its own dictionary or in
    /// a <c>/Pages</c> node's), in a copy of them on the page.
    /// </summary>
    /// <remarks>
    /// <para>Each flattened page was given a direct copy of its resources with its forms added, so
    /// pages inheriting one dictionary each carried the whole of it: 800 pages over 800 XObjects
    /// wrote 10 MB from 371 KB, pages × entries. The names a page's forms take are new to every page
    /// that reads the dictionary — <see cref="TakenNames"/> holds what this revision has added to
    /// it, through this page's resources or through another page's resources naming the same
    /// <c>/XObject</c> object — so the names the other pages gain are names nothing of theirs uses.</para>
    /// <para>Shared by <c>PdfFormFlattener</c> and <c>PdfAnnotationEditor.Flatten</c>, one per
    /// revision.</para>
    /// </remarks>
    internal sealed class StampedXObjects
    {
        private readonly PdfDocument _doc;
        private readonly Dictionary<int, PdfDictionary> _superseded = new Dictionary<int, PdfDictionary>();

        internal StampedXObjects(PdfDocument doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Every resource name <paramref name="page"/> answers to, with the forms this revision has
        /// registered in the dictionary it reads them from.
        /// </summary>
        internal HashSet<string> TakenNames(PdfPage page)
        {
            var names = new HashSet<string>(PdfPageContentAppender.ResourceNames(page.Resources, _doc), StringComparer.Ordinal);
            int number = SharedResourcesNumber(page);
            PdfDictionary registered = number > 0 ? XObjectsOf(number, create: false) : null;
            if (registered != null)
                foreach (var kvp in registered) names.Add(kvp.Key.Value);
            return names;
        }

        /// <summary>
        /// Registers <paramref name="forms"/> (name → form) for <paramref name="page"/>: in the
        /// shared dictionary, leaving <paramref name="updatedPage"/>'s <c>/Resources</c> as it is,
        /// or in a copy set on <paramref name="updatedPage"/>.
        /// </summary>
        internal void Register(PdfPage page, PdfDictionary updatedPage, PdfDictionary forms)
        {
            int number = SharedResourcesNumber(page);
            PdfDictionary shared = number > 0 ? XObjectsOf(number, create: true) : null;
            if (shared != null)
            {
                foreach (var kvp in forms) shared.Set(kvp.Key, kvp.Value);
                return;
            }

            var resources = PdfPageContentAppender.MergeResources(page.Resources, null, _doc);
            var merged = new PdfDictionary();
            if (_doc.ResolveObject(resources[PdfName.XObject]) is PdfDictionary existing)
                foreach (var kvp in existing) merged.Set(kvp.Key, kvp.Value);
            foreach (var kvp in forms) merged.Set(kvp.Key, kvp.Value);
            resources.Set(PdfName.XObject, merged);
            updatedPage.Set(PdfName.Resources, resources);
        }

        /// <summary>Adds the superseded dictionaries to the revision.</summary>
        internal void Write(List<IndirectObject> objects)
        {
            foreach (var kvp in _superseded) objects.Add(new IndirectObject(kvp.Key, 0, kvp.Value));
        }

        /// <summary>
        /// The object number of the <c>/Resources</c> the page reads, when it is an indirect object —
        /// named by the page or inherited — else 0. The parser stamps a number on a top-level
        /// indirect object alone, so a dictionary written in a <c>/Pages</c> node carries none.
        /// </summary>
        private int SharedResourcesNumber(PdfPage page)
        {
            PdfObject raw = page.Dictionary[PdfName.Resources];
            if (raw is PdfReference reference) return reference.ReferenceObjectNumber;
            return raw == null && page.Resources != null ? page.Resources.ObjectNumber : 0;
        }

        /// <summary>
        /// The superseded <c>/XObject</c> dictionary of resources object <paramref name="resourcesNumber"/>:
        /// the resources' own direct one, written with them, or the object it names, copied once.
        /// </summary>
        private PdfDictionary XObjectsOf(int resourcesNumber, bool create)
        {
            PdfDictionary resources = Superseded(resourcesNumber, create);
            if (resources == null)
            {
                // Resources not yet superseded may still name an /XObject object another page's
                // resources share, superseded through them: the names registered there are taken
                // for this page too, or the next form minted takes one of them and replaces its
                // appearance in the one copy both pages read.
                if (!create && _doc.GetObject(resourcesNumber) is PdfDictionary original && !(original is PdfStream)
                    && original[PdfName.XObject] is PdfReference namedByOriginal)
                    return Superseded(namedByOriginal.ReferenceObjectNumber, create: false);
                return null;
            }

            if (resources[PdfName.XObject] is PdfReference named)
            {
                PdfDictionary target = Superseded(named.ReferenceObjectNumber, create);
                if (target != null || !create) return target;
            }
            else if (resources[PdfName.XObject] is PdfDictionary direct)
            {
                return direct;
            }
            else if (!create)
            {
                return null;
            }

            var fresh = new PdfDictionary();
            resources.Set(PdfName.XObject, fresh);
            return fresh;
        }

        /// <summary>The revision's copy of dictionary object <paramref name="number"/>.</summary>
        private PdfDictionary Superseded(int number, bool create)
        {
            if (_superseded.TryGetValue(number, out PdfDictionary known)) return known;
            if (!create || !(_doc.GetObject(number) is PdfDictionary original) || original is PdfStream) return null;

            var copy = PdfDictionary.CloneShallow(original);
            _superseded[number] = copy;
            return copy;
        }
    }
}
