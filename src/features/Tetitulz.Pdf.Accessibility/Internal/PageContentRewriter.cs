// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// What <see cref="PageContentRewriter.RewritePage"/> did to one page.
    ///
    /// <para>
    /// Public fields (not properties) match the convention already used by
    /// <see cref="TaggedRun"/> and <see cref="PageTagResult"/>: these are short-lived value
    /// carriers read directly in tight loops by the auto-tagger and the artifact marker.
    /// </para>
    /// </summary>
    internal sealed class PageRewriteResult
    {
        /// <summary>False when the page was left byte-for-byte untouched.</summary>
        public bool Changed;

        /// <summary>
        /// Why the page was skipped; <c>null</c> only when <see cref="Changed"/> is true.
        /// Always populated on a skip so the caller can raise an honest warning instead of
        /// silently reporting success.
        /// </summary>
        public string SkipReason;

        /// <summary>
        /// Object number of the freshly written single content stream, or <c>-1</c> when the
        /// page was not changed.
        /// </summary>
        public int NewContentObjectNumber = -1;

        /// <summary>
        /// Object number of the page dictionary, or <c>-1</c> when it could not be determined
        /// (which is itself a skip reason). Populated even on a skip when it is known.
        /// </summary>
        public int PageObjectNumber = -1;

        /// <summary>
        /// Marked-content runs the tagger produced, in content order. Never null; empty when the
        /// page was skipped.
        /// </summary>
        public IReadOnlyList<TaggedRun> Runs = ContentStreamTagger.EmptyRuns;

        /// <summary>
        /// First MCID still free on this page — i.e. the page's MCID count when tagging started
        /// at 0. Equals the requested <c>FirstMcid</c> when nothing was tagged.
        /// </summary>
        public int NextMcid;

        /// <summary>
        /// True when the page invokes an XObject with <c>Do</c>. Text drawn inside a Form XObject
        /// lives in a different stream that this pass does <em>not</em> rewrite, so a page that is
        /// both <see cref="Changed"/> and this is only <b>partially</b> tagged.
        ///
        /// <para>
        /// Callers must surface this. A half-tagged page that is reported as tagged is how a
        /// document ends up asserting <c>/MarkInfo /Marked true</c> while still carrying unmarked
        /// real content (ISO 14289-1 §7.1) — a false conformance claim with no warning attached.
        /// </para>
        /// </summary>
        public bool ContainsXObjectInvocations;

        /// <summary>
        /// Resource names passed to <c>Do</c> on this page. Lets the caller resolve which of them
        /// are Form XObjects — <see cref="ContainsXObjectInvocations"/> alone is also true for an
        /// image, which has no text to leave untagged.
        /// </summary>
        public IReadOnlyList<string> InvokedXObjectNames = System.Array.Empty<string>();

        /// <summary>The names of <see cref="InvokedXObjectNames"/> drawn inside an existing <c>/Artifact</c> sequence (<c>PageTagResult.NamesInsideArtifacts</c>).</summary>
        public IReadOnlyCollection<string> NamesInsideArtifacts = System.Array.Empty<string>();

        /// <summary>The names drawn inside another existing sequence, or every name when the page's marked content could not be read (<c>PageTagResult.NamesInsideSequences</c>).</summary>
        public IReadOnlyCollection<string> NamesInsideSequences = System.Array.Empty<string>();

        /// <summary>The names drawn outside every existing sequence (<c>PageTagResult.NamesOutsideSequences</c>).</summary>
        public IReadOnlyCollection<string> NamesOutsideSequences = System.Array.Empty<string>();
    }

    /// <summary>
    /// Turns "this page should be tagged" into the two object-graph edits that actually make it
    /// so: a rewritten content stream carrying <c>BDC</c>/<c>EMC</c> marked content, and a page
    /// dictionary that points at it and carries a <c>/StructParents</c> key.
    ///
    /// <para>
    /// <b>Why this is a shared helper.</b> Both the auto-tagger (which marks paragraphs and
    /// headings) and the artifact marker (which marks running headers/footers) need exactly this
    /// sequence, and both would otherwise re-implement the three things that are easy to get
    /// wrong:
    /// </para>
    /// <list type="number">
    ///   <item><description><b>The <c>/Contents</c> array trap.</b>
    ///     <see cref="ContentStreamTagger"/>'s byte offsets are offsets into the
    ///     <em>concatenation</em> of the page's content streams. Writing the spliced bytes back
    ///     into the individual array parts would put a <c>BDC</c> from part 2 into part 1 and
    ///     destroy the page. The only correct move is to replace <c>/Contents</c> — array or
    ///     single stream — with one reference to one new stream, which is what this type does.
    ///     </description></item>
    ///   <item><description><b>Copy-on-write of the page dictionary.</b> The parsed page
    ///     dictionary belongs to the document being read; mutating it in place would corrupt
    ///     the very object the incremental writer is about to serialise. Every edit goes onto a
    ///     clone, and a clone another step already registered is extended rather than
    ///     replaced.</description></item>
    ///   <item><description><b>One allocator.</b> Object numbers come from the shared
    ///     <see cref="PdfObjectIndex"/>, so two callers in one operation can never mint the same
    ///     number.</description></item>
    /// </list>
    ///
    /// <para>
    /// Nothing here throws for a recoverable condition. An undecodable stream, a page whose
    /// content is already marked, a page with no object number — each is reported through
    /// <see cref="AccessibilityDiagnostics"/> and returned as
    /// <see cref="PageRewriteResult.Changed"/> = false with a
    /// <see cref="PageRewriteResult.SkipReason"/>.
    /// </para>
    /// </summary>
    internal static class PageContentRewriter
    {
        /// <summary>
        /// The page-dictionary key that names this page's entry in the structure tree's parent
        /// number tree (ISO 32000-1 §14.7.4.4). Not present in <see cref="PdfName"/>'s constant
        /// set, so it is interned here once for the whole assembly.
        /// </summary>
        internal static readonly PdfName StructParents = PdfName.StructParents;

        private const string RewriteOperation = "PageContentRewriter.RewritePage";
        private const string StructParentsOperation = "PageContentRewriter.SetStructParents";

        // ================================================================
        //  Content rewriting
        // ================================================================

        /// <summary>
        /// Concatenates the page's <c>/Contents</c>, runs <see cref="ContentStreamTagger.TagPage"/>
        /// over it and — when the tagger produced a changed stream — registers one new
        /// content-stream object plus a revised page dictionary.
        ///
        /// <para>On success:</para>
        /// <list type="bullet">
        ///   <item><description><paramref name="newObjects"/> gains the spliced content stream,
        ///     Flate-encoded when that is smaller, with a correct <c>/Length</c>;</description></item>
        ///   <item><description><paramref name="modifiedObjects"/> gains a clone of the page
        ///     dictionary whose <c>/Contents</c> is a reference to that stream (replacing a single
        ///     stream <em>or</em> an array) and whose <c>/StructParents</c> is
        ///     <paramref name="structParentsKey"/> when that is non-negative.</description></item>
        /// </list>
        /// </summary>
        /// <param name="doc">The document being tagged. Never modified.</param>
        /// <param name="index">Shared object index — the sole allocator of new object numbers.</param>
        /// <param name="pageDict">The page dictionary as parsed. Never modified.</param>
        /// <param name="options">Page geometry, thresholds and the injected classifier.</param>
        /// <param name="structParentsKey">
        /// Parent-tree key to stamp onto the page, or a negative value to leave <c>/StructParents</c>
        /// alone. The key is only written when the page is actually changed, so a caller may pass
        /// its next candidate key and consume it only if <see cref="PageRewriteResult.Changed"/>
        /// comes back true.
        /// </param>
        /// <param name="modifiedObjects">Revisions of existing objects, keyed by object number.</param>
        /// <param name="newObjects">Newly allocated objects, keyed by object number.</param>
        /// <param name="compressRewrittenStream">
        /// When true (the default, and what the contract describes) the new content stream is
        /// Flate-encoded. Pass false to keep it readable — useful for debugging and for the
        /// <c>AutoTagOptions.CompressRewrittenStreams</c> opt-out. Compression is skipped
        /// automatically when it would not shrink the stream.
        /// </param>
        internal static PageRewriteResult RewritePage(
            PdfDocument doc,
            PdfObjectIndex index,
            PdfDictionary pageDict,
            PageTagOptions options,
            int structParentsKey,
            IDictionary<int, PdfObject> modifiedObjects,
            IDictionary<int, PdfObject> newObjects,
            bool compressRewrittenStream = true)
        {
            var result = new PageRewriteResult
            {
                Runs     = ContentStreamTagger.EmptyRuns,
                NextMcid = options != null ? options.FirstMcid : 0,
            };

            if (doc == null || pageDict == null || index == null || modifiedObjects == null || newObjects == null)
            {
                result.SkipReason = "The page could not be rewritten: the document, page dictionary, "
                                  + "object index or object map was missing.";
                AccessibilityDiagnostics.Report(RewriteOperation, result.SkipReason);
                return result;
            }

            if (options == null) options = new PageTagOptions();
            result.NextMcid = options.FirstMcid;

            try
            {
                return RewriteCore(doc, index, pageDict, options, structParentsKey,
                                   modifiedObjects, newObjects, compressRewrittenStream, result);
            }
            catch (Exception ex)
            {
                // The two object maps are only written at the very end of RewriteCore, so an
                // escape from here leaves them exactly as they were found.
                AccessibilityDiagnostics.Report(RewriteOperation,
                    "an unexpected failure aborted the rewrite; the page was left unmodified", ex);

                result.Changed                = false;
                result.NewContentObjectNumber = -1;
                result.Runs                   = ContentStreamTagger.EmptyRuns;
                result.NextMcid               = options.FirstMcid;
                result.SkipReason             = "Content-stream rewriting failed unexpectedly: " + ex.Message;
                return result;
            }
        }

        private static PageRewriteResult RewriteCore(
            PdfDocument doc,
            PdfObjectIndex index,
            PdfDictionary pageDict,
            PageTagOptions options,
            int structParentsKey,
            IDictionary<int, PdfObject> modifiedObjects,
            IDictionary<int, PdfObject> newObjects,
            bool compressRewrittenStream,
            PageRewriteResult result)
        {
            // ---- 1. the page must be nameable, or nothing can reference the rewrite ----
            int pageObjectNumber = ResolveObjectNumber(index, pageDict);
            result.PageObjectNumber = pageObjectNumber;

            if (pageObjectNumber <= 0)
            {
                result.SkipReason = "The page dictionary has no object number, so a revised copy of it "
                                  + "could not be written.";
                AccessibilityDiagnostics.Report(RewriteOperation, result.SkipReason);
                return result;
            }

            // ---- 2. decode the whole /Contents -----------------------------------------
            // The ceiling travels on the options so the caller's AutoTagOptions bounds the decode
            // and not merely the analysis that follows it; ConcatenatePageContent substitutes its
            // own default for a non-positive value, so there is no unbounded path here.
            byte[] content = ContentStreamTagger.ConcatenatePageContent(
                doc, pageDict, options.MaxConcatenatedContentBytes);
            if (content == null)
            {
                string detail = ContentStreamTagger.LastTagError ?? "(no detail)";
                result.SkipReason = "The page's content stream could not be decoded: " + detail;
                AccessibilityDiagnostics.Report(RewriteOperation,
                    "the page's content stream could not be decoded, so it was left unmodified — " + detail);
                return result;
            }

            if (content.Length == 0)
            {
                result.SkipReason = "The page has no content-stream bytes to tag.";
                return result;
            }

            // ---- 3. splice in the marked content ---------------------------------------
            PageTagResult tagged = ContentStreamTagger.TagPage(content, options);
            if (tagged == null)
            {
                result.SkipReason = "The content-stream tagger returned no result for this page.";
                AccessibilityDiagnostics.Report(RewriteOperation, result.SkipReason);
                return result;
            }

            result.ContainsXObjectInvocations = tagged.ContainsXObjectInvocations;
            result.InvokedXObjectNames        = tagged.InvokedXObjectNames;
            result.NamesInsideArtifacts       = tagged.NamesInsideArtifacts;
            result.NamesInsideSequences       = tagged.NamesInsideSequences;
            result.NamesOutsideSequences      = tagged.NamesOutsideSequences;

            if (!tagged.Changed || tagged.NewContent == null || tagged.NewContent.Length == 0)
            {
                result.Runs       = tagged.Runs ?? ContentStreamTagger.EmptyRuns;
                result.NextMcid   = tagged.NextMcid;
                result.SkipReason = tagged.SkipReason
                                    ?? ContentStreamTagger.LastTagError
                                    ?? "The content-stream tagger declined to modify this page.";
                return result;
            }

            // ---- 4. build the replacement stream ---------------------------------------
            int contentObjectNumber = index.AllocateObjectNumber();
            if (contentObjectNumber <= 0)
            {
                result.SkipReason = "No object number could be allocated for the rewritten content stream.";
                AccessibilityDiagnostics.Report(RewriteOperation, result.SkipReason);
                return result;
            }

            PdfStream stream = BuildContentStream(tagged.NewContent, compressRewrittenStream);
            stream.ObjectNumber     = contentObjectNumber;
            stream.GenerationNumber = 0;

            // ---- 5. build the revised page ---------------------------------------------
            PdfDictionary page = CloneForEdit(pageDict, pageObjectNumber, index, modifiedObjects);
            page.Set(PdfName.Contents, new PdfReference(contentObjectNumber, 0));
            if (structParentsKey >= 0)
                page.Set(StructParents, new PdfNumber(structParentsKey));

            // ---- 6. commit both edits together -----------------------------------------
            newObjects[contentObjectNumber]   = stream;
            modifiedObjects[pageObjectNumber] = page;

            result.Changed                = true;
            result.NewContentObjectNumber = contentObjectNumber;
            result.Runs                   = tagged.Runs ?? ContentStreamTagger.EmptyRuns;
            result.NextMcid               = tagged.NextMcid;
            result.SkipReason             = null;
            return result;
        }

        // ================================================================
        //  /StructParents only
        // ================================================================

        /// <summary>
        /// Stamps <c>/StructParents</c> onto a clone of <paramref name="pageDict"/> without
        /// touching its content stream.
        ///
        /// <para>
        /// Used for a page that needs a parent-tree key but whose content must not be respliced —
        /// most often a page that already carries marked content of its own, which
        /// <see cref="ContentStreamTagger"/> deliberately refuses to re-mark.
        /// </para>
        /// </summary>
        internal static void SetStructParents(
            PdfDocument doc,
            PdfObjectIndex index,
            PdfDictionary pageDict,
            int structParentsKey,
            IDictionary<int, PdfObject> modifiedObjects)
        {
            if (doc == null || pageDict == null || index == null || modifiedObjects == null)
            {
                AccessibilityDiagnostics.Report(StructParentsOperation,
                    "/StructParents was not set: the document, page dictionary, object index or "
                    + "object map was missing");
                return;
            }

            if (structParentsKey < 0)
            {
                AccessibilityDiagnostics.Report(StructParentsOperation,
                    "/StructParents was not set: the requested key (" +
                    structParentsKey.ToString(CultureInfo.InvariantCulture) + ") is negative");
                return;
            }

            try
            {
                int pageObjectNumber = ResolveObjectNumber(index, pageDict);
                if (pageObjectNumber <= 0)
                {
                    AccessibilityDiagnostics.Report(StructParentsOperation,
                        "/StructParents was not set: the page dictionary has no object number");
                    return;
                }

                PdfDictionary page = CloneForEdit(pageDict, pageObjectNumber, index, modifiedObjects);
                page.Set(StructParents, new PdfNumber(structParentsKey));
                modifiedObjects[pageObjectNumber] = page;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(StructParentsOperation,
                    "/StructParents could not be set; the page was left unmodified", ex);
            }
        }

        // ================================================================
        //  Helpers
        // ================================================================

        /// <summary>What <see cref="TagFormContent"/> did, or why it declined.</summary>
        internal sealed class FormRewriteResult
        {
            internal bool Changed;
            internal string SkipReason;
            internal IReadOnlyList<TaggedRun> Runs = ContentStreamTagger.EmptyRuns;
            internal int NextMcid;

            /// <summary>
            /// The marked-up content, already Flate-encoded when that made it smaller, and its
            /// <c>/Length</c> and <c>/Filter</c> set to match. Null unless <see cref="Changed"/>.
            ///
            /// <para>
            /// Carries no <c>/StructParents</c> and no object number: those differ per copy, and
            /// <see cref="EmitTaggedForm"/> stamps them. One payload therefore serves every page
            /// drawing the form.
            /// </para>
            /// </summary>
            internal PdfStream Payload;
        }

        /// <summary>
        /// Marks a form's content <b>once</b>, without deciding where the result will be written.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Separated from the emit step because one form's tagged bytes serve <i>every</i> page that
        /// draws it. Nothing in the classification depends on which page is drawing: a form's
        /// options come from <c>BuildFormTagOptions</c>, which switches header/footer artifact
        /// detection off — the only page-space measurement — leaving the median font size, the
        /// heading ratios and the paragraph gap, all of which are document-level. The first MCID is
        /// 0 for every form. So tagging a shared form once per invoking page would produce the same
        /// answer each time, and the cost of duplication is output bytes rather than analysis.
        /// </para>
        /// </remarks>
        internal static FormRewriteResult TagFormContent(
            PdfStream form, byte[] decodedContent, PageTagOptions tagOptions, bool compressRewrittenStream)
        {
            var result = new FormRewriteResult();

            if (form == null || decodedContent == null)
            {
                result.SkipReason = "the form or its decoded content was missing.";
                AccessibilityDiagnostics.Report(RewriteOperation, result.SkipReason);
                return result;
            }

            PageTagResult tagged = ContentStreamTagger.TagPage(decodedContent, tagOptions);
            if (tagged == null)
            {
                result.SkipReason = "the content-stream tagger returned no result for this form.";
                AccessibilityDiagnostics.Report(RewriteOperation, result.SkipReason);
                return result;
            }

            result.Runs     = tagged.Runs ?? ContentStreamTagger.EmptyRuns;
            result.NextMcid = tagged.NextMcid;

            if (!tagged.Changed || tagged.NewContent == null || tagged.NewContent.Length == 0)
            {
                result.SkipReason = tagged.SkipReason
                                    ?? ContentStreamTagger.LastTagError
                                    ?? "the content-stream tagger declined to modify this form.";
                return result;
            }

            result.Payload = BuildContentStream(tagged.NewContent, compressRewrittenStream);
            result.Changed = true;
            return result;
        }

        /// <summary>
        /// Builds one stream carrying <paramref name="tagged"/>'s marked-up bytes at
        /// <paramref name="objectNumber"/>, with its own <c>/StructParents</c>.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="template"/> must always be the <b>original parsed form</b>, never a
        /// revision this method produced earlier. Every copy takes its <c>/BBox</c>, <c>/Matrix</c>,
        /// <c>/Resources</c> and <c>/Group</c> from the same source; feeding one copy the previous
        /// one would carry the previous page's <c>/StructParents</c> across, silently pointing two
        /// pages at one parent-tree entry — precisely the collision duplication exists to avoid.
        /// </para>
        /// <para>The three keys describing the <i>old</i> bytes (<c>/Length</c>, <c>/Filter</c>,
        /// <c>/DecodeParms</c>) are deliberately not copied: they are defined against content that
        /// no longer exists, and the payload carries the correct ones.</para>
        /// </remarks>
        internal static PdfStream EmitTaggedForm(
            PdfStream template, FormRewriteResult tagged,
            int objectNumber, int generation, int structParentsKey)
        {
            PdfStream rewritten = ClonePayload(tagged.Payload);

            foreach (KeyValuePair<PdfName, PdfObject> entry in template)
            {
                if (entry.Key.Equals(PdfName.Length) ||
                    entry.Key.Equals(PdfName.Filter) ||
                    entry.Key.Equals(PdfName.DecodeParms)) continue;
                if (rewritten[entry.Key] == null) rewritten.Set(entry.Key, entry.Value);
            }

            rewritten.Set(StructParents, new PdfNumber(structParentsKey));
            rewritten.ObjectNumber     = objectNumber;
            rewritten.GenerationNumber = generation;
            return rewritten;
        }

        /// <summary>
        /// A fresh stream over the same already-encoded bytes.
        /// </summary>
        /// <remarks>
        /// The payload is shared by reference and never mutated; what must not be shared is the
        /// <see cref="PdfStream"/> itself, since each copy carries a different object number and a
        /// different <c>/StructParents</c>.
        /// </remarks>
        private static PdfStream ClonePayload(PdfStream payload)
        {
            var stream = new PdfStream();
            foreach (KeyValuePair<PdfName, PdfObject> entry in payload) stream.Set(entry.Key, entry.Value);
            stream.RawData = payload.RawData;
            return stream;
        }

        // ================================================================
        //  Repointing a page's XObject resource entry at its own copy
        // ================================================================

        /// <summary>
        /// Points every name in <paramref name="names"/> at <paramref name="targetObjectNumber"/>,
        /// in <b>copies</b> of the page's <c>/Resources</c> and <c>/XObject</c> dictionaries
        /// attached to the page itself. Returns false, with a reason, having changed nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The copies are made unconditionally, and that is the whole point.</b> A
        /// <c>/Resources</c> dictionary is routinely shared: inherited from a <c>/Pages</c> node by
        /// every page beneath it, reached through one indirect reference from several pages, or
        /// holding one <c>/XObject</c> sub-dictionary that distinct <c>/Resources</c> both name.
        /// Proving a given instance unshared would need a whole-document reachability walk — pages,
        /// <c>/Pages</c> nodes, form and Type 3 font <c>/Resources</c>, tiling patterns, soft-mask
        /// groups, annotation <c>/AP</c> streams, the AcroForm <c>/DR</c> — and would still only
        /// establish "unshared as far as I looked". Copying when it was not needed costs a
        /// duplicated dictionary in the output; mutating when it was shared repoints a page this
        /// call was never asked about, at content carrying another page's structure.
        /// </para>
        /// <para>
        /// <b>The whole <c>/Resources</c> is copied, not just its <c>/XObject</c> entry.</b> A page's
        /// own <c>/Resources</c> <i>replaces</i> the inherited one rather than merging with it
        /// (ISO 32000-1 §7.7.3.4), so giving an inheriting page a dictionary holding only
        /// <c>/XObject</c> would take away its fonts.
        /// </para>
        /// <para>
        /// <b>Every name is validated before anything is written.</b> A partial repoint is worse
        /// than none: the page would draw the copy under one name and the original under another.
        /// </para>
        /// </remarks>
        internal static bool TryRepointXObjectNames(
            PdfDocument doc,
            PdfObjectIndex index,
            PdfDictionary pageDict,
            PdfDictionary effectiveResources,
            IReadOnlyList<string> names,
            int targetObjectNumber,
            IDictionary<int, PdfObject> modifiedObjects,
            out string failureReason)
        {
            if (modifiedObjects == null || targetObjectNumber <= 0)
            {
                failureReason = "the edit set or the target object number was missing";
                return false;
            }

            if (!CanRepointXObjectNames(doc, index, pageDict, effectiveResources, names,
                                        out int pageObjectNumber, out PdfDictionary xobjects,
                                        out failureReason))
                return false;

            // Past this point nothing can fail, so the edits below cannot leave a half-repointed page.
            PdfDictionary page = CloneForEdit(pageDict, pageObjectNumber, index, modifiedObjects);

            // Copy from what the page reaches NOW, not from the dictionary it was parsed with. A
            // page drawing two shared forms is repointed twice, and the second call must extend the
            // first call's copy — rebuilding from the parsed resources would put the first form's
            // name back to the original and leave that page drawing a copy it does not own.
            PdfDictionary current = doc.ResolveObject(page[PdfName.Resources]) as PdfDictionary
                                    ?? effectiveResources;
            PdfDictionary currentXObjects =
                doc.ResolveObject(current[PdfName.XObject]) as PdfDictionary ?? xobjects;

            PdfDictionary resourcesCopy = ShallowCopy(current);
            PdfDictionary xobjectsCopy  = ShallowCopy(currentXObjects);

            for (int i = 0; i < names.Count; i++)
                xobjectsCopy.Set(new PdfName(names[i]), new PdfReference(targetObjectNumber, 0));

            resourcesCopy.Set(PdfName.XObject, xobjectsCopy);
            page.Set(PdfName.Resources, resourcesCopy);

            modifiedObjects[pageObjectNumber] = page;
            return true;
        }

        /// <summary>
        /// Everything <see cref="TryRepointXObjectNames"/> needs in order to be sure it will
        /// succeed, without writing anything.
        /// </summary>
        /// <remarks>
        /// Exists so a caller substituting one form's copy across several pages can establish that
        /// <b>every</b> page can be repointed before repointing any of them. Duplicating a form for
        /// some of its pages and leaving the rest drawing the tagged original is the one outcome
        /// worse than not duplicating at all: those pages would render marked content whose owning
        /// structure element names a different page.
        /// </remarks>
        internal static bool CanRepointXObjectNames(
            PdfDocument doc,
            PdfObjectIndex index,
            PdfDictionary pageDict,
            PdfDictionary effectiveResources,
            IReadOnlyList<string> names,
            out int pageObjectNumber,
            out PdfDictionary xobjects,
            out string failureReason)
        {
            pageObjectNumber = 0;
            xobjects         = null;
            failureReason    = null;

            if (doc == null || index == null || pageDict == null || names == null || names.Count == 0)
            {
                failureReason = "the page, object index or resource names were missing";
                return false;
            }

            pageObjectNumber = ResolveObjectNumber(index, pageDict);
            if (pageObjectNumber <= 0)
            {
                failureReason = "the page dictionary has no object number, so a revised copy of it "
                              + "could not be written";
                return false;
            }

            if (effectiveResources == null)
            {
                failureReason = "the page has no resource dictionary to attach the copy to";
                return false;
            }

            xobjects = doc.ResolveObject(effectiveResources[PdfName.XObject]) as PdfDictionary;
            if (xobjects == null)
            {
                failureReason = "the page's resources hold no /XObject dictionary";
                return false;
            }

            for (int i = 0; i < names.Count; i++)
            {
                if (xobjects[new PdfName(names[i])] == null)
                {
                    failureReason = "the page's /XObject dictionary has no entry named /" + names[i];
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// A direct dictionary holding the same entries, by reference.
        /// </summary>
        /// <remarks>
        /// Direct rather than a new indirect object: either way there is exactly one copy per
        /// repointed page, and an object number buys nothing while costing an xref entry.
        /// </remarks>
        private static PdfDictionary ShallowCopy(PdfDictionary source)
        {
            var copy = new PdfDictionary(Math.Max(4, source.Count + 1));
            foreach (KeyValuePair<PdfName, PdfObject> entry in source) copy.Set(entry.Key, entry.Value);
            return copy;
        }

        private static PdfStream BuildContentStream(byte[] content, bool compress)
        {
            var stream = new PdfStream();
            byte[] payload = content;

            if (compress)
            {
                byte[] encoded = null;
                try
                {
                    encoded = FlateEncodeFilter.Encode(content);
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(RewriteOperation,
                        "the rewritten content stream could not be Flate-encoded; it was written uncompressed", ex);
                    encoded = null;
                }

                // A short stream can inflate under zlib; an uncompressed content stream is
                // equally valid, so only take the encoded form when it is actually smaller.
                if (encoded != null && encoded.Length > 0 && encoded.Length < content.Length)
                {
                    payload = encoded;
                    stream.Set(PdfName.Filter, PdfName.FlateDecode);
                }
            }

            stream.RawData = payload;
            stream.Set(PdfName.Length, new PdfNumber(payload.Length));
            return stream;
        }

        /// <summary>
        /// Returns a dictionary that is safe to edit for <paramref name="objectNumber"/>.
        ///
        /// <para>
        /// When another step of the same operation already registered a revision of this object,
        /// that revision is extended (via a further clone, so the map entry is only replaced once
        /// the caller commits) rather than discarded — otherwise the second edit would silently
        /// undo the first.
        /// </para>
        /// </summary>
        private static PdfDictionary CloneForEdit(
            PdfDictionary original, int objectNumber, PdfObjectIndex index,
            IDictionary<int, PdfObject> modifiedObjects)
        {
            PdfDictionary source = original;
            if (modifiedObjects.TryGetValue(objectNumber, out PdfObject existing) && existing is PdfDictionary pending)
                source = pending;

            var clone = new PdfDictionary(Math.Max(4, source.Count + 2));
            foreach (var entry in source)
                clone.Set(entry.Key, entry.Value);

            clone.ObjectNumber     = objectNumber;
            clone.GenerationNumber = index.GetGeneration(objectNumber);
            return clone;
        }

        private static int ResolveObjectNumber(PdfObjectIndex index, PdfDictionary dict)
            => StagedObject.ObjectNumberOf(index, dict);
    }
}
