// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Removes annotations from a document, and flattens them into the page content.
    /// </summary>
    /// <remarks>
    /// <para>The counterpart to <see cref="PdfAnnotationWriter"/>, which could only ever add.
    /// <c>PdfFormFlattener</c> covers <b>form widgets</b>; everything else — highlights, stamps,
    /// notes, squares, free text — is this class.</para>
    /// <para><b>Removal and flattening are opposite answers to the same question and must not be
    /// confused.</b> Removing an annotation takes its content out of the document. Flattening keeps
    /// the content and takes away the interactivity: the appearance is drawn into the page's own
    /// content stream and the annotation is dropped, so the marks survive in a viewer that ignores
    /// annotations, and can no longer be edited, moved or listed.</para>
    /// <para>Both write an <b>incremental update</b>, so the previous revision is still in the file
    /// and <c>PdfRevisions</c> can recover it. A removal is therefore not redaction: the annotation
    /// and anything it said are still in the earlier revision's bytes. Use
    /// <c>PdfOperations.Redact</c> when the content has to be destroyed.</para>
    /// </remarks>
    public static class PdfAnnotationEditor
    {
        /// <summary>What a removal or flattening pass did.</summary>
        public sealed class AnnotationEditResult
        {
            internal readonly List<string> WarningList = new List<string>();

            /// <summary>How many annotations were removed from, or flattened into, their pages.</summary>
            public int Affected { get; internal set; }

            /// <summary>Annotations that were examined and left alone, with the reason.</summary>
            /// <remarks>
            /// A flattening pass that changes nothing and a document with no annotations look the
            /// same from <see cref="Affected"/> alone; the warnings say which happened.
            /// </remarks>
            public IReadOnlyList<string> Warnings => WarningList;
        }

        // ----------------------------------------------------------------
        //  Removal
        // ----------------------------------------------------------------

        /// <summary>
        /// Removes every annotation a predicate selects.
        /// </summary>
        /// <param name="input">The PDF to edit. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="shouldRemove">
        /// Given the zero-based page index and the annotation dictionary, true to remove it. The
        /// dictionary is the document's own — read it, do not mutate it.
        /// </param>
        /// <returns>What was removed.</returns>
        /// <remarks>
        /// A predicate rather than an index, because an index into <c>/Annots</c> is not stable
        /// across the very edits a caller makes: removing annotation 0 renumbers every other one,
        /// so a loop over indices removes the wrong annotations after the first.
        /// </remarks>
        public static AnnotationEditResult Remove(
            Stream input, Stream output, Func<int, PdfDictionary, bool> shouldRemove)
        {
            if (shouldRemove == null) throw new ArgumentNullException(nameof(shouldRemove));
            return Edit(input, output, shouldRemove, flatten: false);
        }

        /// <summary>
        /// Removes every annotation of the given subtypes — the common case of
        /// <see cref="Remove(Stream, Stream, Func{int, PdfDictionary, bool})"/>.
        /// </summary>
        /// <param name="input">The PDF to edit. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="subtypes">Subtype names, without the leading slash — e.g. "Highlight".</param>
        public static AnnotationEditResult RemoveBySubtype(
            Stream input, Stream output, params string[] subtypes)
        {
            if (subtypes == null || subtypes.Length == 0)
                throw new ArgumentException("Name at least one subtype to remove.", nameof(subtypes));

            // Matched by RESOLVED subtype, inside Edit where the document is in scope. Reading
            // `annot[PdfName.Subtype]` here does not follow an indirect reference, so
            // `/Subtype 9 0 R` (legal, §7.3.10, and what a producer sharing one name object across a
            // thousand annotations writes) would read as null and every one of them be silently
            // skipped by a removal that reports success. The predicate cannot resolve it
            // itself: it is built before the document is open.
            var wanted = new HashSet<string>(subtypes, StringComparer.Ordinal);
            return Edit(input, output, selects: null, flatten: false, wantedSubtypes: wanted);
        }

        // ----------------------------------------------------------------
        //  Flattening
        // ----------------------------------------------------------------

        /// <summary>
        /// Draws every non-widget annotation's appearance into its page's content stream and drops
        /// the annotation.
        /// </summary>
        /// <param name="input">The PDF to flatten. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="shouldFlatten">
        /// Given the page index and the annotation, true to flatten it; null flattens every
        /// annotation that has an appearance.
        /// </param>
        /// <remarks>
        /// <para><b>Widgets are left to <c>PdfFormFlattener</c>.</b> Flattening a form field means
        /// removing the <c>/AcroForm</c> as well, and doing half of that here would leave an
        /// interactive form whose fields have no widgets — a document that is neither filled nor
        /// fillable.</para>
        /// <para><b>An annotation with no appearance is kept, not dropped.</b> There is nothing to
        /// draw in its place, so dropping it would delete the content; the reason is reported.</para>
        /// </remarks>
        public static AnnotationEditResult Flatten(
            Stream input, Stream output, Func<int, PdfDictionary, bool> shouldFlatten = null)
            => Edit(input, output, shouldFlatten ?? ((_, __) => true), flatten: true);

        // ----------------------------------------------------------------
        //  The one pass both use
        // ----------------------------------------------------------------

        /// <param name="input">The PDF to edit. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="selects">
        /// Chooses the annotations to act on. Ignored — and may be null — when
        /// <paramref name="wantedSubtypes"/> is supplied.
        /// </param>
        /// <param name="flatten">Draw the appearance into the page, rather than only dropping it.</param>
        /// <param name="wantedSubtypes">
        /// Match by <b>resolved</b> <c>/Subtype</c> instead of by predicate. A caller-supplied
        /// predicate is built before the document is open and so cannot follow an indirect
        /// reference; this is the seam that lets the match happen where the document is in scope.
        /// </param>
        private static AnnotationEditResult Edit(
            Stream input, Stream output, Func<int, PdfDictionary, bool> selects, bool flatten,
            HashSet<string> wantedSubtypes = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // PdfEncryptedDocumentException, as every other Base writer throws for this. It was a
            // plain PdfException, which a caller catching the encrypted-document type missed.
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Cannot edit annotations in an encrypted document here: the content this " +
                    "writes would be in clear text where the reader expects per-object ciphertext, " +
                    "and this assembly cannot encrypt. Decrypt first " +
                    "(Tetitulz.Pdf.Encryption.PdfDecryptor), edit, then re-encrypt — or use " +
                    "Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run.");

            var result = new AnnotationEditResult();
            var objects = new List<IndirectObject>();
            int nextObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var isolation = new PdfPageContentAppender.StateIsolation();
            var stampedForms = new StampedXObjects(doc);
            // Pages may name one /Annots array, so the entries all pages name are bounded together.
            long documentLeft = PdfLimits.MaxAnnotationsPerDocument;
            bool documentCut = false;

            for (int pageIndex = 0; pageIndex < doc.Pages.Count && !documentCut; pageIndex++)
            {
                var page = doc.Pages[pageIndex];
                var annots = page.GetAnnotations();
                if (annots == null || annots.Count == 0) continue;

                int pageObjNum = page.Dictionary.ObjectNumber;
                if (pageObjNum <= 0)
                {
                    result.WarningList.Add(
                        $"page {pageIndex} is a direct object, which an incremental update cannot " +
                        "replace, so its annotations were left alone");
                    continue;
                }

                var kept = new PdfArray();
                var placements = new StringBuilder();
                var xobjects = new PdfDictionary();
                bool changed = false;
                // Every name the page already uses — the forms earlier pages registered in a
                // dictionary this one shares included — and each one minted here as it is minted, so
                // a placement's form is registered under a name nothing else on the page answers to.
                var takenNames = stampedForms.TakenNames(page);

                for (int i = 0; i < annots.Count; i++)
                {
                    var entry = annots[i];
                    if (documentLeft-- <= 0)
                    {
                        if (!documentCut)
                            result.WarningList.Add(
                                $"the pages name more than {PdfLimits.MaxAnnotationsPerDocument} annotation entries between " +
                                $"them; those from page {pageIndex} on were left as they were");
                        documentCut = true;
                        kept.Add(entry);
                        continue;
                    }
                    var annot = doc.ResolveObject(entry) as PdfDictionary;
                    if (annot == null) { kept.Add(entry); continue; }

                    // Resolved once per annotation and used for both decisions below — the widget
                    // gate had the same raw-indexer defect as RemoveBySubtype's predicate, so a
                    // widget whose /Subtype was indirect was flattened into the page while the
                    // /AcroForm that gave it meaning stayed behind.
                    string subtype = AnnotationSubtypeWalk.ResolvedSubtype(doc, annot);

                    bool wanted = wantedSubtypes != null
                        ? subtype != null && wantedSubtypes.Contains(subtype)
                        : selects(pageIndex, annot);
                    if (!wanted) { kept.Add(entry); continue; }

                    if (!flatten)
                    {
                        changed = true;
                        result.Affected++;
                        continue;                       // dropped from /Annots
                    }

                    if (subtype == PdfAnnotationSubtypes.Widget)
                    {
                        kept.Add(entry);
                        result.WarningList.Add(
                            $"page {pageIndex}: a Widget was left alone — flattening a form field " +
                            "also has to remove the /AcroForm, which is PdfFormFlattener's job");
                        continue;
                    }

                    if (!TryStamp(doc, annot, ref nextObjNum, objects, placements, xobjects, takenNames,
                                  out string why))
                    {
                        kept.Add(entry);
                        result.WarningList.Add($"page {pageIndex}: {why}");
                        continue;
                    }

                    changed = true;
                    result.Affected++;
                }

                if (!changed) continue;

                var updatedPage = PdfPageContentAppender.CloneDictionary(page.Dictionary);

                if (placements.Length > 0)
                {
                    byte[] bytes = Core.Text.Latin1Text.Encode(placements.ToString());
                    int contentObjNum = nextObjNum++;
                    var contentStream = new PdfStream();
                    contentStream.RawData = bytes;
                    contentStream.Set(PdfName.Length, new PdfNumber(bytes.Length));
                    contentStream.ObjectNumber = contentObjNum;
                    contentStream.GenerationNumber = 0;
                    objects.Add(new IndirectObject(contentObjNum, 0, contentStream));

                    // The page's own content bracketed in q … Q, so the placements are drawn in the
                    // page's initial graphics state rather than in a transform its content left on.
                    var contents = PdfPageContentAppender.CopyExistingContents(doc, page.Dictionary);
                    isolation.Wrap(contents, ref nextObjNum, objects);
                    contents.Add(new PdfReference(contentObjNum, 0));
                    updatedPage.Set(PdfName.Contents, contents);

                    stampedForms.Register(page, updatedPage, xobjects);
                }

                // An empty array and an absent key mean the same thing (§12.5.2), so the key goes
                // when nothing survived rather than leaving `/Annots []` behind.
                var clean = new PdfDictionary();
                foreach (var kvp in updatedPage)
                {
                    if (kvp.Key == PdfName.Annots) continue;
                    clean.Set(kvp.Key, kvp.Value);
                }
                if (kept.Count > 0) clean.Set(PdfName.Annots, kept);
                clean.ObjectNumber = pageObjNum;
                clean.GenerationNumber = 0;
                objects.Add(new IndirectObject(pageObjNum, 0, clean));
            }
            stampedForms.Write(objects);

            if (objects.Count == 0)
            {
                // Nothing matched: copy rather than append an empty revision, so a pass that found
                // nothing leaves the file byte-identical instead of growing it.
                ms.Position = 0;
                ms.CopyTo(output);
                return result;
            }

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObjNum);
            return result;
        }

        /// <summary>
        /// Places one annotation's appearance into the page's content, registering the form in
        /// <paramref name="xobjects"/>.
        /// </summary>
        /// <param name="doc">The document.</param>
        /// <param name="annot">The annotation being flattened.</param>
        /// <param name="nextObjNum">Next free object number.</param>
        /// <param name="objects">Receives any object the placement has to write.</param>
        /// <param name="placements">Receives the content-stream fragment.</param>
        /// <param name="xobjects">Receives the form under its resource name.</param>
        /// <param name="takenNames">
        /// Names already used on this page, the page's own and those minted for earlier placements;
        /// the name minted here is added to it.
        /// </param>
        /// <param name="why">Why the annotation could not be flattened.</param>
        private static bool TryStamp(
            PdfDocument doc, PdfDictionary annot, ref int nextObjNum, List<IndirectObject> objects,
            StringBuilder placements, PdfDictionary xobjects, HashSet<string> takenNames, out string why)
        {
            why = null;

            // §12.5.3 — a hidden annotation is not drawn, so flattening it would make invisible
            // content visible. Kept, and reported.
            int flags = PdfAnnotationFlags.FromNumber(doc.ResolveObject(annot[PdfName.F])) ?? 0;
            if (PdfAnnotationFlags.IsHiddenOnScreen(flags))
            {
                why = "the annotation is hidden (/F bit 2), so flattening it would make invisible " +
                      "content visible";
                return false;
            }

            var apDict = doc.ResolveObject(annot[PdfName.AP]) as PdfDictionary;
            if (apDict == null)
            {
                why = "the annotation has no /AP, so there is nothing to draw in its place and it " +
                      "was kept";
                return false;
            }

            var apStream = AnnotationAppearanceStamp.ResolveNormalAppearance(
                annot, apDict, doc, ref nextObjNum, objects, out var normalRef);
            if (apStream == null || normalRef == null)
            {
                why = "the annotation's /AP /N did not resolve to an appearance stream";
                return false;
            }

            if (!AnnotationAppearanceStamp.TryGetRect(doc, annot, out var rect))
            {
                why = "the annotation has no usable /Rect";
                return false;
            }

            // The name was "AnnotXO" + nextObjNum, and nextObjNum only moves when an object is
            // minted. An /AP /N that is already indirect, which is the normal case, mints nothing,
            // so every annotation flattened on one page got the same name. The resource entry was
            // overwritten each time, and every placement drew the last annotation's appearance.
            string name;
            int suffix = 0;
            do name = "AnnotXO" + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);
            while (!takenNames.Add(name));
            placements.Append(AnnotationAppearanceStamp.BuildPlacement(
                doc, apStream, name, rect.X, rect.Y, rect.W, rect.H));
            xobjects.Set(new PdfName(name), normalRef);
            return true;
        }
    }
}
