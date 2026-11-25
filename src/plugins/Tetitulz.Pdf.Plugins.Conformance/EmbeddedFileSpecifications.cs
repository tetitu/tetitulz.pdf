// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The file specifications through which a document embeds files, each once: the entries of the
    /// catalog's <c>/Names /EmbeddedFiles</c> tree, then each <c>/FileAttachment</c> annotation's
    /// <c>/FS</c> dictionary, page by page, whether or not a reader is shown the annotation. PDF/A's
    /// A67 (ISO 19005-2/3 6.8, -4 6.9: the rules sit on every file specification of an embedded file,
    /// wherever it is) and PDF/UA-1's UA59 (ISO 14289-1 7.11 names both places, and 7.18.7 holds a file
    /// attachment annotation to 7.11) read them here.
    /// </summary>
    internal static class EmbeddedFileSpecifications
    {
        /// <summary>What <see cref="Read"/> found.</summary>
        internal sealed class Reading
        {
            /// <summary>Each specification with where it was found, in the order above.</summary>
            internal readonly List<(string Name, PdfDictionary Spec)> Specifications = new List<(string, PdfDictionary)>();

            /// <summary>Whether the name dictionary has an <c>/EmbeddedFiles</c> tree at all.</summary>
            internal bool HasNameTree;

            /// <summary><see langword="false"/> when the tree was not read in full, so a specification may be missing.</summary>
            internal bool NameTreeComplete = true;
        }

        /// <summary>
        /// Reads the specifications <paramref name="catalog"/> and <paramref name="pages"/> hold, every
        /// value resolved through <paramref name="resolve"/>. The catalog and the page dictionaries are
        /// the caller's, since a checker that reads an encrypted document finds them without the
        /// document's own catalog, which it does not load; a page's annotations are read under the
        /// validation's <see cref="ScanReuse.AdmitAnnotations"/> ceiling.
        /// </summary>
        internal static Reading Read(PdfDocument doc, PdfDictionary catalog, IReadOnlyList<PdfDictionary> pages,
            Func<PdfObject, PdfObject> resolve)
        {
            var reading = new Reading();
            if (catalog == null) return reading;

            var tree = resolve((resolve(catalog[PdfName.Names]) as PdfDictionary)?[PdfName.EmbeddedFiles]) as PdfDictionary;
            if (tree != null)
            {
                reading.HasNameTree = true;
                var entries = new List<KeyValuePair<PdfObject, PdfObject>>();
                int budget = PdfLimits.MaxNameTreeEntries;
                reading.NameTreeComplete = PdfNameTree.TryFlatten(tree, doc, entries, ref budget);
                foreach (var entry in entries)
                    if (resolve(entry.Value) is PdfDictionary named)
                        reading.Specifications.Add(("(" + (PdfNameTree.KeyText(entry.Key) ?? "?") + ")", named));
            }

            // A specification both named and attached is read once.
            var seen = new HashSet<object>(PdfReferenceEquality.Instance);
            foreach (var (_, named) in reading.Specifications) seen.Add(named);
            for (int p = 0; pages != null && p < pages.Count; p++)
            {
                if (!(resolve(pages[p]?[PdfName.Annots]) is PdfArray annots) || !ScanReuse.AdmitAnnotations(annots)) continue;
                for (int a = 0; a < annots.Count; a++)
                    if (resolve(annots[a]) is PdfDictionary annot
                        && (resolve(annot[PdfName.Subtype]) as PdfName)?.Value == PdfAnnotationSubtypes.FileAttachment
                        && resolve(annot[PdfName.FS]) is PdfDictionary attached
                        && seen.Add(attached))
                        reading.Specifications.Add(("a /FileAttachment annotation's on page " + (p + 1).ToString(CultureInfo.InvariantCulture), attached));
            }
            return reading;
        }
    }
}
