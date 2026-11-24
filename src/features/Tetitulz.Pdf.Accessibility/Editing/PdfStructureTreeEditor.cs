// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Accessibility.Editing
{
    /// <summary>
    /// Edits the logical structure tree and document metadata of an existing PDF via an
    /// appended incremental update. Allows app developers to:
    /// <list type="bullet">
    ///   <item>Set or update /Alt (alternate text) on Figure structure elements.</item>
    ///   <item>Set /ActualText on structure elements of a given type.</item>
    ///   <item>Fix a weak (H1..H6) heading hierarchy by renumbering levels.</item>
    ///   <item>Set the document language (/Lang) on the catalog.</item>
    ///   <item>Set the document title (/Info /Title) and /ViewerPreferences /DisplayDocTitle.</item>
    ///   <item>Set /TU (tooltip) on a form field.</item>
    ///   <item>Set /Contents (alternate description) on link annotations.</item>
    ///   <item>Set /Tabs /S (structure tab order) on the pages that carry annotations.</item>
    /// </list>
    /// All operations produce a new output PDF via incremental update; the input is never modified.
    ///
    /// <para>
    /// <b>Failure reporting.</b> Every path that produces a no-op — an encrypted document, a
    /// missing catalog, an untagged document, an element whose object number cannot be
    /// determined — copies the input through unchanged <i>and</i> reports why through
    /// <see cref="AccessibilityDiagnostics"/>. A caller that gets <c>0</c> / <c>false</c> back
    /// can read <see cref="AccessibilityDiagnostics.LastError"/> to tell "nothing to do" from
    /// "something was wrong and was tolerated".
    /// </para>
    ///
    /// <para>
    /// <b>Encrypted documents are refused, not edited.</b> Re-encrypting an appended revision
    /// is out of scope for this type, so an encrypted input is copied through verbatim and the
    /// operation reports a failure result.
    /// </para>
    ///
    /// <para>
    /// <b>Every method here obeys one invariant: a reported count / <c>true</c> implies the output
    /// differs from the input.</b> The incremental writer is documented never to throw — it catches
    /// every failure, reports it through <see cref="AccessibilityDiagnostics"/> and leaves the
    /// output holding just the original bytes — so returning the <i>planned</i> edit unconditionally
    /// would tell the caller "7 headings renumbered" about a file that is byte-for-byte the input.
    /// Each operation reports what the bytes support: zero / <c>false</c> when the revision did not
    /// land, with the reason in <see cref="LastWriteError"/>. See
    /// <see cref="WriteVerifiedRevision"/> for the mechanism, which is the same one
    /// <c>PdfAutoTagger</c> and <c>PdfArtifactMarker</c> use.
    /// </para>
    /// </summary>
    public static class PdfStructureTreeEditor
    {
        /// <summary>
        /// The <c>/Tabs</c> <i>value</i> <c>/S</c> ("structure order"): tab order follows the
        /// logical structure tree (ISO 32000-1 Table 30). Aliased separately from
        /// <see cref="PdfName.S"/>, which at every other site in this assembly is the
        /// structure-element <i>key</i>, so a reader is not left wondering whether the two uses
        /// are related. Same instance, so it costs nothing.
        /// </summary>
        private static readonly PdfName _structureTabOrder = PdfName.S;

        /// <summary>Shared empty map, so the common "no new objects" call allocates nothing.</summary>
        private static readonly Dictionary<int, PdfObject> _noObjects = new Dictionary<int, PdfObject>();

        /// <summary>
        /// Recursion cap for the AcroForm /Kids tree. <see cref="PdfLimits.MaxTreeDepth"/> is the
        /// repository-wide cap for the four <c>/Kids</c>-linked balanced trees — name, number,
        /// outline and field — and carries the same value this constant always had.
        /// </summary>
        private static int MaxFieldTreeDepth => PdfLimits.MaxTreeDepth;

        /// <summary>
        /// Total traversal-step ceiling for the AcroForm /Kids tree. Separate from the depth cap
        /// and from the visited set, because neither bounds the work done by a <em>shared</em>
        /// kids array (see <see cref="FieldWalkState.Budget"/>).
        /// </summary>
        private const int MaxFieldTreeNodes = 200_000;

        /// <summary>
        /// One <see cref="PdfObjectIndex"/> per open <see cref="PdfDocument"/>.
        ///
        /// <para>
        /// The index is a snapshot of the parsed document, and none of the operations in this
        /// assembly mutate the parsed object graph (they clone dictionaries and write the
        /// clones into an appended revision), so it stays valid for the document's lifetime.
        /// Caching it keeps the legacy <see cref="FindObjectNumber"/> shim — which callers
        /// still invoke inside per-page and per-element loops — at O(1) per lookup instead of
        /// rebuilding the whole index on every call.
        /// </para>
        /// </summary>
        private static readonly ConditionalWeakTable<PdfDocument, PdfObjectIndex> _indexCache =
            new ConditionalWeakTable<PdfDocument, PdfObjectIndex>();

        // ================================================================
        //  Write-outcome diagnostics
        // ================================================================

        /// <summary>
        /// Why the most recent operation on <b>this thread</b> reported <c>0</c> / <c>false</c>
        /// despite having an edit to write, or <c>null</c> when the last operation's revision
        /// reached the output (or when there was legitimately nothing to append).
        ///
        /// <para>
        /// The operations on this type return bare counts and booleans, so they have nowhere to
        /// carry a warning. This is that channel: it is set exactly when the appended revision this
        /// call built never reached the output stream — a caller's <see cref="Stream"/> that threw,
        /// a trailer that could not be rebuilt — and the reported count was therefore reduced to
        /// zero so it still describes the produced document.
        /// </para>
        ///
        /// <para>
        /// <c>[ThreadStatic]</c> per CLAUDE.md's diagnostics rule, so concurrent operations on a
        /// server host cannot read each other's failures. Cleared at the start of every public
        /// operation on this type.
        /// </para>
        /// </summary>
        public static string LastWriteError => _lastWriteError;

        [ThreadStatic]
        private static string _lastWriteError;

        /// <summary>
        /// Resets the per-thread diagnostics slots so a stale message from an earlier, unrelated
        /// call is never mistaken for a diagnosis of this one.
        /// </summary>
        private static void BeginOperation()
        {
            AccessibilityDiagnostics.Clear();
            _lastWriteError = null;
        }

        /// <summary>
        /// Opens the document, or copies <paramref name="bytes"/> through to
        /// <paramref name="output"/> and returns null when the parser refuses it.
        /// </summary>
        /// <remarks>
        /// Every operation on this type opens the document through here, so a document the parser
        /// cannot open is copied through unchanged and reported — never a throw before a single byte
        /// reaches <paramref name="output"/>, which would leave a caller who chains these editors
        /// stream-to-stream (the pattern <c>PdfAccessibility</c>'s own class doc demonstrates) with a
        /// zero-byte file. That is this type's promise that "every path that produces a no-op copies
        /// the input through unchanged and reports why", as <c>PdfArtifactMarker</c>,
        /// <c>PdfUaXmpWriter</c>, <c>PdfReadingOrderAnalyzer.Fix</c> and
        /// <c>PdfFontEmbeddingRemediator</c> keep it.
        ///
        /// <para>A <c>using</c> on the null return is a no-op, which is what lets each call site stay
        /// two lines.</para>
        /// </remarks>
        private static PdfDocument OpenOrCopy(MemoryStream ms, byte[] bytes, Stream output, string op)
        {
            try
            {
                return PdfDocument.Open(ms);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(op,
                    "the document could not be opened, so nothing was changed; the input was copied " +
                    "to the output unchanged", ex);
                output.Write(bytes, 0, bytes.Length);
                return null;
            }
        }

        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Sets alt text on Figure structure elements.
        /// <para>
        /// The <paramref name="altTextProvider"/> callback receives a <see cref="FigureContext"/>
        /// for each Figure found. Return a non-null string to set /Alt; return null to skip.
        /// </para>
        ///
        /// <para>
        /// <b>Invariant: <see cref="SetAltTextResult.Updated"/> &gt; 0 implies the output differs
        /// from the input.</b> If the appended revision never reaches <paramref name="output"/>,
        /// the figures it would have updated are reported as <see cref="SetAltTextResult.Failed"/>
        /// instead, <c>Updated</c> is <c>0</c>, and <see cref="LastWriteError"/> says why.
        /// </para>
        /// </summary>
        public static SetAltTextResult SetFigureAltText(
            Stream input, Stream output,
            Func<FigureContext, string> altTextProvider)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (altTextProvider == null) throw new ArgumentNullException(nameof(altTextProvider));

            const string Op = "PdfStructureTreeEditor.SetFigureAltText";
            BeginOperation();

            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return new SetAltTextResult(0, 0, 0);

            var strTree = GetStructTreeRootOrReport(doc, Op, "no figure received alt text");
            if (strTree == null)
            {
                output.Write(bytes, 0, bytes.Length);
                return new SetAltTextResult(0, 0, 0);
            }

            var index = GetOrBuildIndex(doc);

            var (modified, updated, skipped, failed) =
                CollectFigureAltTextRevisions(doc, index, strTree, altTextProvider, Op);

            // `updated` counts what this pass planned. Nothing may be reported until the revision
            // carrying it has actually reached `output`: a figure whose /Alt is not in the produced
            // file did not receive alt text, it failed to.
            if (!WriteAndVerify(output, bytes, doc, index, modified, _noObjects, Op,
                    "the figure alternate text"))
            {
                return new SetAltTextResult(0, skipped, failed + updated);
            }

            return new SetAltTextResult(updated, skipped, failed);
        }

        /// <summary>
        /// Stages the <c>/Alt</c> revisions <see cref="SetFigureAltText(Stream,Stream,Func{FigureContext,string})"/>
        /// would write, without writing anything — the encrypted-document path.
        /// </summary>
        /// <remarks>
        /// <para>Every accessibility writer writes a <b>clear-text</b> incremental update, which an
        /// encrypted document cannot accept. This assembly cannot decrypt one either: it does not
        /// reference <c>Tetitulz.Pdf.Encryption</c>, and it must not, since that would drag the
        /// crypto stack into its package graph. So the work is split — this returns the objects, and
        /// <c>Encryption.PdfEncryptedIncrementalUpdate.Run</c> encrypts and appends them.</para>
        /// <para><b>This plan never mints an object</b>, so the ceiling is reserved purely
        /// defensively. That is not a reason to omit the reservation: <c>PdfArtifactMarker</c>
        /// reserved <i>after</i> minting and superseded a live object the <c>/Encrypt</c> dictionary
        /// needed, producing an undecryptable document. Reserving first is unconditional here so the
        /// next edit to this method cannot reintroduce that.</para>
        /// <para><b>The provider is the caller's code and is called exactly once per figure, in
        /// document order</b>, with the same <see cref="FigureContext"/> the clear-text path builds.
        /// A figure index it sees here therefore means the same thing it would there — which matters
        /// because the default provider in the remediation pipeline derives its text from that
        /// index, so a divergence would silently renumber every figure's description.</para>
        /// </remarks>
        /// <param name="doc">A decrypted document. Not modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The first object number free in the <b>target</b> document. See the remarks on
        /// <c>PdfEncryptedIncrementalUpdate</c>: the decrypted copy's own ceiling can be lower.
        /// </param>
        /// <param name="altTextProvider">Returns the alt text for a figure, or <c>null</c> to skip it.</param>
        public static FigureAltTextPlan PlanFigureAltText(
            PdfDocument doc, int firstFreeObjectNumber, Func<FigureContext, string> altTextProvider)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (altTextProvider == null) throw new ArgumentNullException(nameof(altTextProvider));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanFigureAltText";
            BeginOperation();

            PdfDictionary strTree = GetStructTreeRootOrReport(doc, Op, "no figure received alt text");
            if (strTree == null) return FigureAltTextPlan.Nothing;

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null) return FigureAltTextPlan.Nothing;
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            var (modified, updated, skipped, failed) =
                CollectFigureAltTextRevisions(doc, index, strTree, altTextProvider, Op);

            if (modified.Count == 0)
            {
                // Nothing to append, but the skip/fail counts are still the caller's evidence about
                // why -- returning Nothing here would erase a document whose every figure failed.
                return new FigureAltTextPlan(Array.Empty<IndirectObject>(), 0, skipped, failed);
            }

            var objects = RevisionObjects.From(modified, index, "PdfStructureTreeEditor.PlanFigureAltText");

            return new FigureAltTextPlan(objects, updated, skipped, failed);
        }


        /// <summary>
        /// The per-figure decision every <c>/Alt</c> writer makes: walk the structure tree, select
        /// the figures, ask the caller's provider, and accumulate the revised element dictionaries
        /// with the counts that explain what happened to the rest.
        /// </summary>
        /// <remarks>
        /// <para><b>One walk, because the two callers must be identical.</b> <see cref="SetFigureAltText(Stream,Stream,Func{FigureContext,string})"/>
        /// writes a clear-text incremental update; <see cref="PlanFigureAltText"/> stages the same
        /// objects for the encrypted route. They diverge only in what they do with the result: a document remediated through the
        /// encrypted route must not end up with a different set of figures described than the same
        /// document remediated clear, and a figure index that meant something different between them
        /// would silently renumber every figure's description, because the remediation pipeline's
        /// default provider derives its text from that index.</para>
        /// <para>Two rules stated in one place rather than asserted in two comments: the provider is
        /// called <b>exactly once per figure, in document order</b>, and a figure is selected by
        /// <c>/RoleMap</c> resolution rather than by its raw role — the latter is why a document
        /// carrying <c>/RoleMap &lt;&lt; /Illustration /Figure &gt;&gt;</c> was once reported by the
        /// audit and walked past by the step that fixes it, leaving <c>IsConforming</c> unreachable
        /// however many times remediation was re-run. A check and its fixer must agree on what an
        /// element is.</para>
        /// </remarks>
        /// <param name="doc">The document to read. Not modified — the revisions are clones.</param>
        /// <param name="index">
        /// Object index for <paramref name="doc"/>, supplying each element's object number and
        /// generation.
        /// </param>
        /// <param name="strTree">The catalog's <c>/StructTreeRoot</c>, already resolved by the caller.</param>
        /// <param name="altTextProvider">
        /// Returns the alt text for a figure, or <c>null</c> to leave that figure alone. Called
        /// exactly once per figure, in document order.
        /// </param>
        /// <param name="op">
        /// Names the calling entry point in any diagnostic. It is a parameter rather than a constant
        /// because it is the one thing that legitimately differs between the two paths: a caller
        /// reading the sink needs to know which route failed.
        /// </param>
        private static (Dictionary<int, PdfObject> Modified, int Updated, int Skipped, int Failed)
            CollectFigureAltTextRevisions(
                PdfDocument doc,
                PdfObjectIndex index,
                PdfDictionary strTree,
                Func<FigureContext, string> altTextProvider,
                string op)
        {
            var modified = new Dictionary<int, PdfObject>();
            int figIdx = 0, updated = 0, skipped = 0, failed = 0;

            var roleMap = RoleMapResolver.Build(doc);

            foreach (var node in StructTreeWalker.Collect(strTree, doc, index))
            {
                if (!RoleMapResolver.Means(node.Role, roleMap, StructureTypes.Figure)) continue;

                string existingAlt = GetTextValue(doc, node.Dict, PdfName.Alt);
                var ctx = new FigureContext(figIdx, node.PageIndex, existingAlt);
                int thisFigure = figIdx;
                figIdx++;

                string newAlt;
                try
                {
                    newAlt = altTextProvider(ctx);
                }
                catch (Exception ex)
                {
                    // A throwing provider costs that figure and no other: abandoning the rest would
                    // make one bad callback undo the whole pass.
                    failed++;
                    AccessibilityDiagnostics.Report(op,
                        "the caller's alt-text provider threw for figure " +
                        thisFigure.ToString(CultureInfo.InvariantCulture) +
                        "; that figure was left unchanged", ex);
                    continue;
                }

                if (newAlt == null) { skipped++; continue; }

                if (node.ObjectNumber <= 0)
                {
                    failed++;
                    AccessibilityDiagnostics.Report(op,
                        "figure " + thisFigure.ToString(CultureInfo.InvariantCulture) +
                        " is a direct (inline) structure element with no object number of its own, " +
                        "so an incremental update cannot revise it; its /Alt was not set");
                    continue;
                }

                var clone = CloneDict(node.Dict);
                clone[PdfName.Alt] = MakeTextString(newAlt);
                modified[node.ObjectNumber] = clone;
                updated++;
            }

            return (modified, updated, skipped, failed);
        }

        /// <summary>Convenience overload that sets the same alt text on all figures that lack one.</summary>
        public static SetAltTextResult SetFigureAltText(
            Stream input, Stream output, string altText)
        {
            return SetFigureAltText(input, output, ctx =>
                string.IsNullOrEmpty(ctx.ExistingAltText) ? altText : null);
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Sets the alternate description of link annotations — their <c>/Contents</c>, where
        /// ISO 14289-1 §7.18.5 puts it, which is what closes <b>UA22</b>.
        /// <para>
        /// The <paramref name="descriptionProvider"/> receives a <see cref="LinkContext"/> for each
        /// link a reader is presented, in page order; it returns the description, or <c>null</c> to
        /// leave the link as it is. A blank description is refused and counted as skipped: an empty
        /// <c>/Contents</c> is what UA22 reports as missing.
        /// </para>
        /// <para>
        /// <b>Invariant: <see cref="SetAltTextResult.Updated"/> &gt; 0 implies the output differs
        /// from the input.</b> If the appended revision never reaches <paramref name="output"/>, the
        /// links it would have described are counted as <see cref="SetAltTextResult.Failed"/>,
        /// <c>Updated</c> is <c>0</c>, and <see cref="LastWriteError"/> says why. An encrypted
        /// document is copied through; <see cref="PlanLinkDescriptions"/> is its route.
        /// </para>
        /// </summary>
        public static SetAltTextResult SetLinkDescriptions(
            Stream input, Stream output, Func<LinkContext, string> descriptionProvider)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (descriptionProvider == null) throw new ArgumentNullException(nameof(descriptionProvider));

            const string Op = "PdfStructureTreeEditor.SetLinkDescriptions";
            BeginOperation();

            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return new SetAltTextResult(0, 0, 0);

            if (ReportIfEncrypted(doc, Op, "no link received a description"))
            {
                output.Write(bytes, 0, bytes.Length);
                return new SetAltTextResult(0, 0, 0);
            }

            var index = GetOrBuildIndex(doc);

            var (modified, updated, skipped, failed) =
                CollectLinkDescriptionRevisions(doc, descriptionProvider, Op);

            if (!WriteAndVerify(output, bytes, doc, index, modified, _noObjects, Op,
                    "the link descriptions"))
            {
                return new SetAltTextResult(0, skipped, failed + updated);
            }

            return new SetAltTextResult(updated, skipped, failed);
        }

        /// <summary>
        /// Stages the <c>/Contents</c> revisions
        /// <see cref="SetLinkDescriptions(Stream,Stream,Func{LinkContext,string})"/> would write,
        /// without writing anything — the encrypted-document path, appended by
        /// <c>Encryption.PdfEncryptedIncrementalUpdate.Run</c>.
        /// </summary>
        /// <remarks>
        /// One walk serves both routes, so an encrypted document and the same document in clear
        /// text offer the provider the same links under the same indexes. Nothing is minted; the
        /// target's ceiling is reserved first all the same, for the reason recorded on
        /// <see cref="PlanFigureAltText"/>.
        /// </remarks>
        /// <param name="doc">A decrypted document. Not modified.</param>
        /// <param name="firstFreeObjectNumber">The first object number free in the <b>target</b> document.</param>
        /// <param name="descriptionProvider">Returns a link's description, or <c>null</c> to leave it.</param>
        public static FigureAltTextPlan PlanLinkDescriptions(
            PdfDocument doc, int firstFreeObjectNumber, Func<LinkContext, string> descriptionProvider)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (descriptionProvider == null) throw new ArgumentNullException(nameof(descriptionProvider));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanLinkDescriptions";
            BeginOperation();

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null) return FigureAltTextPlan.Nothing;
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            var (modified, updated, skipped, failed) =
                CollectLinkDescriptionRevisions(doc, descriptionProvider, Op);

            if (modified.Count == 0)
                return new FigureAltTextPlan(Array.Empty<IndirectObject>(), 0, skipped, failed);

            var objects = RevisionObjects.From(modified, index, Op);
            return new FigureAltTextPlan(objects, updated, skipped, failed);
        }

        /// <summary>
        /// The per-link decision both link-description writers make: the document's links through
        /// <c>PdfLinkExtractor</c> (its ceilings, its target reading), each annotation once, those
        /// a reader is presented — <c>PdfUaAnnotationScope</c>, the scope UA22 checks — offered to
        /// the provider in page order, and the revised annotation dictionaries gathered with the
        /// counts that say what happened to the rest.
        /// </summary>
        /// <remarks>
        /// A link held as a direct dictionary in <c>/Annots</c> has no object number, so an
        /// incremental update cannot revise it alone; it is counted as failed and said, never
        /// offered — a description the provider wrote for it would not reach the file.
        /// </remarks>
        private static (Dictionary<int, PdfObject> Modified, int Updated, int Skipped, int Failed)
            CollectLinkDescriptionRevisions(
                PdfDocument doc, Func<LinkContext, string> provider, string op)
        {
            var modified = new Dictionary<int, PdfObject>();
            int linkIdx = 0, updated = 0, skipped = 0, failed = 0, direct = 0;

            IReadOnlyList<PdfLink> links =
                PdfLinkExtractor.ExtractLinks(doc);
            string cut = PdfLinkExtractor.LastError;
            if (cut != null)
                AccessibilityDiagnostics.Report(op, cut + " The links past that point were not offered a description.");

            var seen = new HashSet<int>();
            foreach (PdfLink link in links)
            {
                if (link.ObjectNumber <= 0) { direct++; continue; }
                if (!seen.Add(link.ObjectNumber)) continue;

                if (!(doc.GetObject(link.ObjectNumber) is PdfDictionary annot))
                {
                    failed++;
                    AccessibilityDiagnostics.Report(op,
                        "link annotation " + link.ObjectNumber.ToString(CultureInfo.InvariantCulture) +
                        " 0 R could not be read again; it was not given a description");
                    continue;
                }

                int? flags = PdfAnnotationFlags.FromNumber(doc.ResolveObject(annot[PdfName.F]));
                PdfArray rect = doc.ResolveObject(annot[PdfName.Rect]) as PdfArray;
                PdfArray cropBox = link.PageIndex < doc.Pages.Count ? doc.Pages[link.PageIndex].CropBox : null;
                if (!Plugins.Conformance.PdfUaAnnotationScope.IsPresentedToReader(
                        PdfAnnotationSubtypes.Link, flags, rect, cropBox, doc.ResolveObject)) continue;

                var ctx = new LinkContext(linkIdx, link.PageIndex, link, GetTextValue(doc, annot, PdfName.Contents));
                int thisLink = linkIdx;
                linkIdx++;

                string description;
                try
                {
                    description = provider(ctx);
                }
                catch (Exception ex)
                {
                    failed++;
                    AccessibilityDiagnostics.Report(op,
                        "the caller's link-description provider threw for link " +
                        thisLink.ToString(CultureInfo.InvariantCulture) + "; that link was left unchanged", ex);
                    continue;
                }

                if (description == null) { skipped++; continue; }
                if (string.IsNullOrWhiteSpace(description))
                {
                    skipped++;
                    AccessibilityDiagnostics.Report(op,
                        "the description supplied for link " + thisLink.ToString(CultureInfo.InvariantCulture) +
                        " is empty or white space, which UA22 reports as missing; that link was left unchanged");
                    continue;
                }

                PdfDictionary clone = CloneDict(annot);
                clone[PdfName.Contents] = MakeTextString(description);
                modified[link.ObjectNumber] = clone;
                updated++;
            }

            if (direct > 0)
            {
                failed += direct;
                AccessibilityDiagnostics.Report(op,
                    direct.ToString(CultureInfo.InvariantCulture) + " link annotation(s) are direct " +
                    "dictionaries inside /Annots, with no object number an incremental update can " +
                    "revise; they were not offered a description");
            }

            return (modified, updated, skipped, failed);
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Stages the <c>/ActualText</c> revisions
        /// <see cref="SetActualText(Stream,Stream,string,Func{int,string,string})"/> would write,
        /// without writing anything — the encrypted-document path.
        /// </summary>
        /// <remarks>
        /// <para>The last of this type's seven operations to gain a <c>Plan</c>. It is <b>not</b> in
        /// the remediation pipeline, so nothing was blocked on it; it is here so the set is complete
        /// and a caller reaching for <c>/ActualText</c> on an encrypted document is not the one
        /// person who finds a hole.</para>
        /// <para>The provider is the caller's code, called once per matching element in document
        /// order with the same <c>(index, existing)</c> pair the clear-text path passes — and, as
        /// there, <b>an exception from it propagates</b>. That is deliberately unlike
        /// <c>PlanFigureAltText</c>, which tolerates a throwing provider and counts a failure:
        /// matching each clear-text sibling exactly is what stops the two routes from describing
        /// different sets of elements.</para>
        /// <para>Nothing is minted, so the ceiling reservation is defensive — unconditional anyway,
        /// for the reason recorded on <c>PlanFigureAltText</c>.</para>
        /// </remarks>
        /// <param name="doc">A decrypted document. Not modified.</param>
        /// <param name="firstFreeObjectNumber">First object number free in the <b>target</b>.</param>
        /// <param name="elementType">Structure element type to match, e.g. <c>"Span"</c>.</param>
        /// <param name="actualTextProvider">
        /// Given the match index and any existing <c>/ActualText</c>, returns the replacement, or
        /// <c>null</c> to leave that element alone.
        /// </param>
        public static StructureTreePlan PlanActualText(
            PdfDocument doc, int firstFreeObjectNumber,
            string elementType, Func<int, string, string> actualTextProvider)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (elementType == null) throw new ArgumentNullException(nameof(elementType));
            if (actualTextProvider == null) throw new ArgumentNullException(nameof(actualTextProvider));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanActualText";
            BeginOperation();

            PdfDictionary strTree = GetStructTreeRootOrReport(doc, Op, "no element received /ActualText");
            if (strTree == null) return StructureTreePlan.Nothing;

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null) return StructureTreePlan.Nothing;
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            var (modified, count) =
                CollectActualTextRevisions(doc, index, strTree, elementType, actualTextProvider, Op);

            if (modified.Count == 0) return StructureTreePlan.Nothing;

            var objects = RevisionObjects.From(modified, index, "PdfStructureTreeEditor.PlanActualText");

            return new StructureTreePlan(objects, newInfoObjectNumber: -1, itemsChanged: count);
        }

        /// <summary>
        /// Sets /ActualText on structure elements matching the given element type.
        ///
        /// <para>
        /// <b>Invariant: a returned count &gt; 0 implies the output differs from the input.</b> If
        /// the appended revision never reaches <paramref name="output"/>, this returns <c>0</c> and
        /// <see cref="LastWriteError"/> says why.
        /// </para>
        /// </summary>
        public static int SetActualText(
            Stream input, Stream output,
            string elementType,
            Func<int, string, string> actualTextProvider)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (elementType == null) throw new ArgumentNullException(nameof(elementType));
            if (actualTextProvider == null) throw new ArgumentNullException(nameof(actualTextProvider));

            const string Op = "PdfStructureTreeEditor.SetActualText";
            BeginOperation();

            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return 0;

            var strTree = GetStructTreeRootOrReport(doc, Op, "no element received /ActualText");
            if (strTree == null) { output.Write(bytes, 0, bytes.Length); return 0; }

            var index = GetOrBuildIndex(doc);

            var (modified, count) =
                CollectActualTextRevisions(doc, index, strTree, elementType, actualTextProvider, Op);

            // Only what the produced document actually contains may be counted.
            if (!WriteAndVerify(output, bytes, doc, index, modified, _noObjects, Op,
                    "the /ActualText values"))
            {
                return 0;
            }

            return count;
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// The per-element <c>/ActualText</c> pass shared by <see cref="SetActualText"/> (which
        /// writes an incremental update) and <see cref="PlanActualText"/> (which stages
        /// <c>IndirectObject</c>s for an encrypted document).
        /// </summary>
        /// <remarks>
        /// <para><b>The sibling of <see cref="CollectFigureAltTextRevisions"/>, and it was found the
        /// same way.</b> The two routes duplicated the whole decision — walk the tree, match the
        /// element type, read any existing value, call the provider, skip a <c>null</c> answer, skip
        /// a direct (inline) element that has no object number to revise, clone and set. They
        /// differ only in what they do with the result, which is why that stays at the call
        /// sites.</para>
        ///
        /// <para><b>Why the two must agree.</b> A document remediated through the encrypted route
        /// has to end up with the same set of elements described as the same document remediated
        /// clear — and <paramref name="actualTextProvider"/> is handed a running <i>index</i>, so a
        /// disagreement about which elements are skipped does not merely lose one value: it
        /// renumbers every element after it, and the caller's provider silently answers for the
        /// wrong one.</para>
        ///
        /// <para><paramref name="op"/> is a parameter because it is the one thing that legitimately
        /// differs: a caller reading <c>AccessibilityDiagnostics</c> needs to know which route
        /// skipped an element.</para>
        /// </remarks>
        private static (Dictionary<int, PdfObject> Modified, int Count) CollectActualTextRevisions(
            PdfDocument doc,
            PdfObjectIndex index,
            PdfDictionary strTree,
            string elementType,
            Func<int, string, string> actualTextProvider,
            string op)
        {
            var modified = new Dictionary<int, PdfObject>();
            int count = 0;

            foreach (StructNode node in StructTreeWalker.Collect(strTree, doc, index))
            {
                if (node.Role != elementType) continue;

                string existing = GetTextValue(doc, node.Dict, PdfName.ActualText);

                // The provider is the caller's code: an exception from it is the caller's problem
                // and propagates, on both routes alike.
                string newText = actualTextProvider(count, existing);
                if (newText == null) continue;

                if (node.ObjectNumber <= 0)
                {
                    AccessibilityDiagnostics.Report(op,
                        "a <" + elementType + "> element is a direct (inline) structure element with no " +
                        "object number of its own, so an incremental update cannot revise it; " +
                        "its /ActualText was not set");
                    continue;
                }

                PdfDictionary clone = CloneDict(node.Dict);
                clone[PdfName.ActualText] = MakeTextString(newText);
                modified[node.ObjectNumber] = clone;
                count++;
            }

            return (modified, count);
        }

        /// <summary>
        /// Sets the document language (/Lang) in the catalog.
        ///
        /// <para>
        /// <paramref name="languageTag"/> is expected to be a BCP 47 language tag
        /// (<c>^[A-Za-z]{1,8}(-[A-Za-z0-9]{1,8})*$</c>, e.g. <c>en-US</c>). An empty or
        /// malformed tag is still written — the value is the caller's choice — but it is
        /// reported through <see cref="AccessibilityDiagnostics"/> first, because an empty or
        /// unparseable /Lang is itself a PDF/UA-1 failure rather than a fix.
        /// </para>
        ///
        /// <para>
        /// <b>Invariant: <c>true</c> implies the output differs from the input.</b> If the appended
        /// revision never reaches <paramref name="output"/>, this returns <c>false</c> and
        /// <see cref="LastWriteError"/> says why.
        /// </para>
        /// </summary>
        public static bool SetDocumentLanguage(Stream input, Stream output, string languageTag)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (languageTag == null) throw new ArgumentNullException(nameof(languageTag));

            const string Op = "PdfStructureTreeEditor.SetDocumentLanguage";
            BeginOperation();

            if (languageTag.Length == 0)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the language tag is empty; an empty /Lang is itself a PDF/UA-1 violation " +
                    "(natural language must be determinable). It was written as requested");
            }
            else if (!IsWellFormedLanguageTag(languageTag))
            {
                AccessibilityDiagnostics.Report(Op,
                    "the language tag \"" + languageTag + "\" is not a well-formed BCP 47 tag " +
                    "(expected subtags of 1-8 alphanumerics separated by '-', the first alphabetic); " +
                    "it was written as requested but assistive technology may ignore it");
            }

            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return false;

            if (ReportIfEncrypted(doc, Op, "the document language was not set"))
            {
                output.Write(bytes, 0, bytes.Length);
                return false;
            }

            var catalog = GetCatalogOrReport(doc, Op, "the document language was not set");
            if (catalog == null) { output.Write(bytes, 0, bytes.Length); return false; }

            var index    = GetOrBuildIndex(doc);
            var modified = new Dictionary<int, PdfObject>();

            int catalogObjNum = ResolveCatalogObjectNumber(doc, index, catalog);
            if (catalogObjNum > 0)
            {
                var newCatalog = CloneDict(catalog);
                newCatalog[PdfName.Lang] = MakeTextString(languageTag);
                modified[catalogObjNum] = newCatalog;
            }
            else
            {
                // Returning true here would tell the caller the language was set on a document
                // that was copied through byte for byte, with no readable diagnostic outside this
                // assembly. "false" is the only honest answer.
                AccessibilityDiagnostics.Report(Op,
                    "the /Catalog's object number could not be determined, so /Lang could not be " +
                    "attached to it; the input was copied to the output unchanged");
                output.Write(bytes, 0, bytes.Length);
                return false;
            }

            // Same reasoning as the branch above: "true" about a byte-identical output is the lie
            // this check exists to prevent, and a lost revision produces exactly that output.
            return WriteAndVerify(output, bytes, doc, index, modified, _noObjects, Op,
                "the document language");
        }

        // ================================================================
        //  Plan* - the encrypted route
        //
        //  The Stream/Stream entry points above append a CLEAR-TEXT revision and therefore refuse
        //  an encrypted document. This assembly cannot decrypt (no reference to
        //  Tetitulz.Pdf.Encryption, and there must not be one), so the operation travels to the
        //  decryptor:
        //
        //      PdfEncryptedIncrementalUpdate.Run(input, output, password,
        //          (decrypted, firstFree) =>
        //              PdfStructureTreeEditor.PlanDocumentLanguage(decrypted, firstFree, tag).Objects);
        //
        //  Each Plan mirrors one entry point and stages the SAME edit, from the same few lines that
        //  entry point already had, so there is no second implementation to drift.
        // ================================================================

        /// <summary>
        /// Computes the <c>/Lang</c> edit <b>without writing it</b>, for a caller that owns the
        /// write - which is how an <b>encrypted</b> document gets a document language.
        /// </summary>
        /// <remarks>
        /// <b>This edit mints nothing</b> - it revises the catalog, which the document already has -
        /// so <paramref name="firstFreeObjectNumber"/> is reserved as defence in depth rather than
        /// out of need. <see cref="PlanDocumentTitle"/> is the opposite case and says so.
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate - the ceiling of the file the revision
        /// will be appended to, plus one.
        /// </param>
        /// <param name="languageTag">BCP 47 tag to write.</param>
        public static StructureTreePlan PlanDocumentLanguage(
            PdfDocument doc, int firstFreeObjectNumber, string languageTag)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (languageTag == null) throw new ArgumentNullException(nameof(languageTag));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanDocumentLanguage";
            BeginOperation();

            PdfDictionary catalog = GetCatalogOrReport(doc, Op, "the document language was not set");
            if (catalog == null) return StructureTreePlan.Nothing;

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null) return StructureTreePlan.Nothing;
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            int catalogObjNum = ResolveCatalogObjectNumber(doc, index, catalog);
            if (catalogObjNum <= 0)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the /Catalog's object number could not be determined, so /Lang could not be " +
                    "attached to it; nothing was planned");
                return StructureTreePlan.Nothing;
            }

            PdfDictionary newCatalog = CloneDict(catalog);
            newCatalog[PdfName.Lang] = MakeTextString(languageTag);

            return new StructureTreePlan(
                new[] { new IndirectObject(catalogObjNum, index.GetGeneration(catalogObjNum), newCatalog) },
                newInfoObjectNumber: -1);
        }

        /// <summary>
        /// Computes the <c>/Info /Title</c> edit <b>without writing it</b>, for a caller that owns
        /// the write - which is how an <b>encrypted</b> document gets the title UA04 requires.
        /// </summary>
        /// <remarks>
        /// <para><b>This edit CAN mint</b>: a document with no <c>/Info</c> dictionary gets a new
        /// one, so <paramref name="firstFreeObjectNumber"/> is load-bearing and is reserved before
        /// anything is allocated. <c>PdfArtifactMarker</c> reserved after minting instead and
        /// produced a document that would not decrypt at all.</para>
        /// <para><b>The caller must carry <see cref="StructureTreePlan.NewInfoObjectNumber"/> into
        /// the appended trailer</b> when it is not <c>-1</c>, or the new <c>/Info</c> is written and
        /// unreachable - a title nobody can read.</para>
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate - the ceiling of the file the revision
        /// will be appended to, plus one.
        /// </param>
        /// <param name="title">Title to write.</param>
        public static StructureTreePlan PlanDocumentTitle(
            PdfDocument doc, int firstFreeObjectNumber, string title)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (title == null) throw new ArgumentNullException(nameof(title));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanDocumentTitle";
            BeginOperation();

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null)
            {
                AccessibilityDiagnostics.Report(Op,
                    "no object index could be built, so the title could not be planned");
                return StructureTreePlan.Nothing;
            }

            // BEFORE the AllocateObjectNumber below.
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            var modified = new Dictionary<int, PdfObject>();
            var created  = new Dictionary<int, PdfObject>();
            int newInfoId = -1;

            PdfObject infoObj = doc.Trailer != null ? doc.Trailer[PdfName.Info] : null;
            PdfDictionary info = infoObj as PdfDictionary
                                 ?? (infoObj != null ? doc.ResolveObject(infoObj) as PdfDictionary : null);

            if (info != null)
            {
                int infoObjNum = infoObj is PdfReference infoRef
                    ? infoRef.ReferenceObjectNumber
                    : index.GetObjectNumber(info);

                if (infoObjNum <= 0)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "the /Info dictionary is direct (inline) in the trailer and has no object " +
                        "number, so an incremental update cannot revise it; nothing was planned");
                    return StructureTreePlan.Nothing;
                }

                PdfDictionary newInfo = CloneDict(info);
                newInfo[PdfName.Title] = MakeTextString(title);
                modified[infoObjNum] = newInfo;
            }
            else
            {
                newInfoId = index.AllocateObjectNumber();
                var freshInfo = new PdfDictionary();
                freshInfo[PdfName.Title] = MakeTextString(title);
                created[newInfoId] = freshInfo;
            }

            var objects = RevisionObjects.From(
                created, modified, index, Op);

            return new StructureTreePlan(objects, newInfoId);
        }

        private static void RequireCeiling(int firstFreeObjectNumber)
        {
            if (firstFreeObjectNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(firstFreeObjectNumber),
                    "Object numbering starts at 1; 0 is the free-list head and can never be written.");
        }

        // ----------------------------------------------------------------

        /// <summary>Sets the document title in /Info and in /ViewerPreferences /DisplayDocTitle.
        /// Creates a new /Info dictionary if the PDF does not have one.
        ///
        /// <para>
        /// <b>Invariant: <c>true</c> implies the output differs from the input.</b> Two things have
        /// to hold for that, and both are checked: something was actually staged for writing (every
        /// branch that could not write reports and falls through), <i>and</i> the appended revision
        /// carrying it reached <paramref name="output"/>. The second check is the one that matters
        /// against a failing sink — the edit set is non-empty there too, and returning <c>true</c>
        /// on the strength of that alone is how a caller ends up believing a byte-identical file
        /// has a title. <see cref="LastWriteError"/> says why when it is <c>false</c>.
        /// </para>
        /// </summary>
        public static bool SetDocumentTitle(Stream input, Stream output, string title)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (title == null) throw new ArgumentNullException(nameof(title));

            const string Op = "PdfStructureTreeEditor.SetDocumentTitle";
            BeginOperation();

            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return false;

            if (ReportIfEncrypted(doc, Op, "the document title was not set"))
            {
                output.Write(bytes, 0, bytes.Length);
                return false;
            }

            var index      = GetOrBuildIndex(doc);
            var modified   = new Dictionary<int, PdfObject>();
            var newObjects = new Dictionary<int, PdfObject>();
            int newInfoId  = -1;

            // ---- /Info /Title — revise the existing dictionary, or create one ----
            PdfObject infoObj = null;
            try
            {
                infoObj = doc.Trailer?[PdfName.Info];
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the trailer could not be read; the document title is written to a newly created /Info dictionary", ex);
            }

            if (infoObj != null)
            {
                var info = ResolveDict(doc, infoObj);
                if (info == null)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "the trailer's /Info does not resolve to a dictionary; the title was not written to /Info");
                }
                else
                {
                    int infoObjNum = infoObj is PdfReference infoRef
                        ? infoRef.ReferenceObjectNumber
                        : (index != null ? index.GetObjectNumber(info) : -1);

                    if (infoObjNum > 0)
                    {
                        var newInfo = CloneDict(info);
                        newInfo[PdfName.Title] = MakeTextString(title);
                        modified[infoObjNum] = newInfo;
                    }
                    else
                    {
                        AccessibilityDiagnostics.Report(Op,
                            "the /Info dictionary is direct (inline) in the trailer and has no object number, " +
                            "so an incremental update cannot revise it; the title was not written to /Info");
                    }
                }
            }
            else if (index != null)
            {
                // No /Info dict in this PDF — create one and record the new object number so
                // the writer emits the /Info reference in the appended trailer. The number
                // comes from the shared index so it can never collide with the cross-reference
                // stream's own object number.
                newInfoId = index.AllocateObjectNumber();
                var newInfo = new PdfDictionary();
                newInfo[PdfName.Title] = MakeTextString(title);
                newObjects[newInfoId] = newInfo;
            }
            else
            {
                AccessibilityDiagnostics.Report(Op,
                    "the document has no /Info dictionary and no object index could be built, so a " +
                    "collision-free object number could not be allocated; the title was not written to /Info");
            }

            // ---- Catalog /ViewerPreferences /DisplayDocTitle ----
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the document has no readable /Catalog; /ViewerPreferences /DisplayDocTitle was not set");
            }
            else
            {
                int catalogObjNum = ResolveCatalogObjectNumber(doc, index, catalog);
                var newCatalog = CloneDict(catalog);

                var vpObj = catalog[PdfName.ViewerPreferences];
                var vp    = vpObj != null ? ResolveDict(doc, vpObj) : null;
                var newVp = CloneDict(vp);
                newVp[PdfName.DisplayDocTitle] = new PdfBoolean(true);

                if (vpObj is PdfReference vpRef)
                {
                    // /ViewerPreferences is its own indirect object — revise it in place.
                    if (vpRef.ReferenceObjectNumber > 0)
                        modified[vpRef.ReferenceObjectNumber] = newVp;
                    else
                        AccessibilityDiagnostics.Report(Op,
                            "the /ViewerPreferences reference names object number " +
                            vpRef.ReferenceObjectNumber.ToString(CultureInfo.InvariantCulture) +
                            ", which is not a valid indirect object; /DisplayDocTitle was not set");
                }
                else
                {
                    newCatalog[PdfName.ViewerPreferences] = newVp;
                }

                if (catalogObjNum > 0)
                {
                    modified[catalogObjNum] = newCatalog;
                }
                else
                {
                    AccessibilityDiagnostics.Report(Op,
                        "the /Catalog's object number could not be determined; " +
                        "/ViewerPreferences /DisplayDocTitle was not set");
                }
            }

            bool written = WriteAndVerify(output, bytes, doc, index, modified, newObjects, Op,
                "the document title", newInfoId);

            // Every branch above that could not write reported and fell through, so an empty edit
            // set means nothing was changed. A non-empty one only means something was *planned* —
            // hence the write outcome, which is what the produced bytes support.
            return written && (modified.Count > 0 || newObjects.Count > 0);
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Computes the heading-renumbering edit <b>without writing it</b>, for a caller that owns
        /// the write - which is how an <b>encrypted</b> document gets a conformant heading
        /// hierarchy (UA13's never-skip-a-level rule).
        /// </summary>
        /// <remarks>
        /// <para><b>Mints nothing</b>: every object it emits revises a structure element the
        /// document already has, so the ceiling is defence in depth and the <c>Run</c> trailer
        /// limitation recorded on <see cref="PlanDocumentTitle"/> does not apply.</para>
        /// <para>The renumbering decision itself is <c>HeadingHierarchyPlanner.Plan</c>, exactly as
        /// the clear-text path uses - this method only turns its rewrites into objects, so the two
        /// routes cannot disagree about which heading becomes which level. Headings the planner
        /// could not revise (direct/inline elements with no object number) are reported through
        /// <c>AccessibilityDiagnostics</c> and keep their original level, in both routes.</para>
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate - the ceiling of the file the revision
        /// will be appended to, plus one.
        /// </param>
        public static StructureTreePlan PlanHeadingHierarchy(PdfDocument doc, int firstFreeObjectNumber)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanHeadingHierarchy";
            BeginOperation();

            PdfDictionary strTree = GetStructTreeRootOrReport(doc, Op, "no heading level was changed");
            if (strTree == null) return StructureTreePlan.Nothing;

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null) return StructureTreePlan.Nothing;
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            HeadingHierarchyPlan headings =
                HeadingHierarchyPlanner.Plan(StructTreeWalker.Collect(strTree, doc, index), RoleMapResolver.Build(doc));

            foreach (StructNode node in headings.Unrevisable)
            {
                AccessibilityDiagnostics.Report(Op,
                    "heading <" + node.Role + "> is a direct (inline) structure element with no object " +
                    "number of its own, so an incremental update cannot revise it; it keeps its original level");
            }

            var modified = new Dictionary<int, PdfObject>();
            foreach (HeadingRenumbering rewrite in headings.Rewrites)
            {
                PdfDictionary clone = CloneDict(rewrite.Node.Dict);
                clone[PdfName.S] = new PdfName(
                    "H" + rewrite.NewLevel.ToString(CultureInfo.InvariantCulture));
                modified[rewrite.Node.ObjectNumber] = clone;
            }

            if (modified.Count == 0) return StructureTreePlan.Nothing;

            var objects = RevisionObjects.From(modified, index, Op);

            return new StructureTreePlan(objects, newInfoObjectNumber: -1);
        }

        /// <summary>
        /// Fixes the heading hierarchy in the structure tree by renumbering heading levels
        /// so they never skip (H1→H2→H3, never H1→H3). Returns the number of elements whose
        /// /S was rewritten.
        ///
        /// <para>
        /// <b>Only the weak (numbered) heading model is renumbered.</b> PDF/UA recognises two
        /// heading models: the <i>weak</i> one, where the level is encoded in the tag name
        /// (<c>H1</c>…<c>H6</c>), and the <i>strong</i> one, where a single generic
        /// <c>&lt;H&gt;</c> takes its level from the nesting depth of the enclosing
        /// <c>&lt;Sect&gt;</c> elements. Rewriting an <c>&lt;H&gt;</c> to <c>H1</c>/<c>H2</c>/…
        /// would destroy that structure and, in a document that nests correctly, would turn a
        /// conforming file into a non-conforming one. So <c>&lt;H&gt;</c> elements are left
        /// untouched and their presence is reported through
        /// <see cref="AccessibilityDiagnostics"/>. In a mixed document only the numbered
        /// elements are renumbered, among themselves.
        /// </para>
        ///
        /// <para>
        /// <b>The rule itself lives in <see cref="HeadingHierarchyPlanner"/>, not here.</b>
        /// <c>PdfAccessibility</c>'s remediation pipeline replays the same traversal to decide
        /// whether running this step would change anything, and the two answers must agree — a
        /// fixer and its own pre-flight check that disagree produce a report contradicted by the
        /// step it describes. They are one function, and this method only writes what that function
        /// decided.
        /// </para>
        ///
        /// <para>
        /// <b>Invariant: a returned count &gt; 0 implies the output differs from the input.</b> The
        /// plan's <c>Rewrites.Count</c> is what this pass <i>intended</i> to renumber; it is
        /// returned only once the appended revision carrying it has reached
        /// <paramref name="output"/>. Otherwise this returns <c>0</c> and
        /// <see cref="LastWriteError"/> says why.
        /// </para>
        /// </summary>
        public static int FixHeadingHierarchy(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            const string Op = "PdfStructureTreeEditor.FixHeadingHierarchy";
            BeginOperation();

            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return 0;

            var strTree = GetStructTreeRootOrReport(doc, Op, "no heading level was changed");
            if (strTree == null) { output.Write(bytes, 0, bytes.Length); return 0; }

            var index = GetOrBuildIndex(doc);

            // Document (pre-order) order is what the never-skip rule is defined over, and
            // StructTreeWalker.Collect preserves it.
            HeadingHierarchyPlan plan =
                HeadingHierarchyPlanner.Plan(StructTreeWalker.Collect(strTree, doc, index), RoleMapResolver.Build(doc));

            if (plan.StrongHeadingCount > 0)
            {
                AccessibilityDiagnostics.Report(Op,
                    "PDF/UA strong heading structure was detected: " +
                    plan.StrongHeadingCount.ToString(CultureInfo.InvariantCulture) + " <H> element(s) (" +
                    plan.StrongHeadingsInSectCount.ToString(CultureInfo.InvariantCulture) +
                    " of them nested inside <Sect>) take their level from section nesting rather than " +
                    "from the tag name, so they were left unchanged; " +
                    (plan.NumberedHeadingCount > 0
                        ? "only the " + plan.NumberedHeadingCount.ToString(CultureInfo.InvariantCulture) +
                          " numbered H1-H6 element(s) were considered for renumbering"
                        : "there are no numbered H1-H6 elements to renumber"));
            }

            // Headings the plan wanted to renumber but could not: the output keeps their original
            // tag, and the planner already carried that original level forward, so the rest of the
            // sequence is measured against what the file really contains.
            foreach (StructNode node in plan.Unrevisable)
            {
                AccessibilityDiagnostics.Report(Op,
                    "heading <" + node.Role + "> is a direct (inline) structure element with no object " +
                    "number of its own, so an incremental update cannot revise it; it keeps its original level");
            }

            var modified = new Dictionary<int, PdfObject>();

            foreach (HeadingRenumbering rewrite in plan.Rewrites)
            {
                var clone = CloneDict(rewrite.Node.Dict);
                clone[PdfName.S] = new PdfName(
                    "H" + rewrite.NewLevel.ToString(CultureInfo.InvariantCulture));
                modified[rewrite.Node.ObjectNumber] = clone;
            }

            // The plan describes the document this pass is about to write. A heading whose new /S
            // is not in the produced file was not renumbered, whatever the plan says.
            if (!WriteAndVerify(output, bytes, doc, index, modified, _noObjects, Op,
                    "the renumbered heading levels"))
            {
                return 0;
            }

            return plan.Rewrites.Count;
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Computes the <c>/TU</c> tooltip edit <b>without writing it</b>, for a caller that owns
        /// the write - which is how a form field in an <b>encrypted</b> document gets the
        /// accessible name UA08 requires.
        /// </summary>
        /// <remarks>
        /// <para><b>Mints nothing</b>: it revises the field dictionary the document already has, so
        /// the ceiling is defence in depth and the <c>Run</c> trailer limitation recorded on
        /// <see cref="PlanDocumentTitle"/> does not apply.</para>
        /// <para><b>An empty tooltip is refused, not written</b>, exactly as the clear-text path
        /// refuses it. <c>MakeTextString</c> turns null or empty into an empty <c>PdfString</c>, so
        /// the field would be given <c>/TU ()</c> and the operation would look successful - while
        /// <c>AcroFormFieldWalk</c>'s accessible-name rule still treats a blank <c>/TU</c> as
        /// missing, so a later audit re-reports UA08 against the same field with nothing saying why
        /// the "fix" did nothing.</para>
        /// <para><b>An empty plan means the field was not found</b>, which is not the same as
        /// "nothing to do" - the reason is on <c>AccessibilityDiagnostics</c>, and a caller that
        /// appends nothing should treat the tooltip as not set.</para>
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate - the ceiling of the file the revision
        /// will be appended to, plus one.
        /// </param>
        /// <param name="fieldPartialName">Partial name (<c>/T</c>) of the field to tooltip.</param>
        /// <param name="tooltip">The accessible name to write.</param>
        public static StructureTreePlan PlanFieldTooltip(
            PdfDocument doc, int firstFreeObjectNumber, string fieldPartialName, string tooltip)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            // A null name would compare equal to the null a field with no /T yields, so the first
            // UNNAMED field would be tooltipped instead - the defect the clear-text path records.
            if (fieldPartialName == null) throw new ArgumentNullException(nameof(fieldPartialName));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanFieldTooltip";
            BeginOperation();

            if (string.IsNullOrWhiteSpace(tooltip))
            {
                AccessibilityDiagnostics.Report(Op,
                    "the tooltip is null, empty or whitespace; writing it would set a /TU the accessible-name " +
                    "rule still treats as missing, so nothing was planned");
                return StructureTreePlan.Nothing;
            }

            PdfDictionary catalog = GetCatalogOrReport(doc, Op, "no form field tooltip was set");
            if (catalog == null) return StructureTreePlan.Nothing;

            PdfObject acroFormObj = catalog[PdfName.AcroForm];
            PdfDictionary acroForm = acroFormObj != null ? ResolveDict(doc, acroFormObj) : null;
            if (acroForm == null)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the document has no /AcroForm dictionary (it contains no form fields); nothing was planned");
                return StructureTreePlan.Nothing;
            }

            PdfObject fieldsObj = acroForm[PdfName.Fields];
            PdfArray fields = fieldsObj as PdfArray ?? ResolveArray(doc, fieldsObj);
            if (fields == null)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the /AcroForm has no readable /Fields array; nothing was planned");
                return StructureTreePlan.Nothing;
            }

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null) return StructureTreePlan.Nothing;
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            var state = new FieldWalkState
            {
                Doc        = doc,
                Index      = index,
                TargetName = fieldPartialName,
                Tooltip    = tooltip,
                Modified   = new Dictionary<int, PdfObject>(),
            };

            if (!FindAndSetTooltip(fields, state, 0))
            {
                AccessibilityDiagnostics.Report(Op,
                    "no form field with the partial name \"" + fieldPartialName +
                    "\" was found; nothing was planned");
                return StructureTreePlan.Nothing;
            }

            if (state.Modified.Count == 0) return StructureTreePlan.Nothing;

            var objects = RevisionObjects.From(state.Modified, index, Op);

            return new StructureTreePlan(objects, newInfoObjectNumber: -1);
        }

        /// <summary>
        /// Sets a /TU (tooltip / accessible name) on a specific form field identified by
        /// its partial name (/T). Returns true if the field was found and updated.
        ///
        /// <para>
        /// <b>Invariant: <c>true</c> implies the output differs from the input.</b> Finding the
        /// field is not enough — the appended revision carrying its /TU has to reach
        /// <paramref name="output"/>. If it does not, this returns <c>false</c> and
        /// <see cref="LastWriteError"/> says why.
        /// </para>
        /// </summary>
        public static bool SetFieldTooltip(Stream input, Stream output, string fieldPartialName, string tooltip)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            // A null name would compare equal to the null GetTextValue returns for a field with no
            // /T, so the first UNNAMED field in the tree would be silently tooltipped instead.
            if (fieldPartialName == null) throw new ArgumentNullException(nameof(fieldPartialName));

            const string Op = "PdfStructureTreeEditor.SetFieldTooltip";
            BeginOperation();

            byte[] bytes = ReadAll(input);

            // Refused rather than written, and this is the difference between a fix and the
            // appearance of one: MakeTextString turns null/empty into an empty PdfString, so the
            // field would be given `/TU ()` and the method return true — while AcroFormFieldWalk's
            // NeedsAccessibleName treats a blank /TU as still missing, so a subsequent audit would
            // re-report UA08 against the same field with nothing saying why the "successful" fix
            // did nothing. SetDocumentLanguage already diagnoses exactly this
            // case for an empty tag.
            if (string.IsNullOrWhiteSpace(tooltip))
            {
                AccessibilityDiagnostics.Report(Op,
                    "the tooltip supplied for form field \"" + fieldPartialName + "\" is null, empty " +
                    "or whitespace; a blank /TU is what UA08 reports as missing, so nothing was " +
                    "written and the input was copied to the output unchanged");
                output.Write(bytes, 0, bytes.Length);
                return false;
            }

            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return false;

            if (ReportIfEncrypted(doc, Op, "no form field tooltip was set"))
            {
                output.Write(bytes, 0, bytes.Length);
                return false;
            }

            var catalog = GetCatalogOrReport(doc, Op, "no form field tooltip was set");
            if (catalog == null) { output.Write(bytes, 0, bytes.Length); return false; }

            var acroFormObj = catalog[PdfName.AcroForm];
            var acroForm    = acroFormObj != null ? ResolveDict(doc, acroFormObj) : null;
            if (acroForm == null)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the document has no /AcroForm dictionary (it contains no form fields); " +
                    "the input was copied to the output unchanged");
                output.Write(bytes, 0, bytes.Length);
                return false;
            }

            var fieldsObj = acroForm[PdfName.Fields];
            var fields    = fieldsObj as PdfArray ?? ResolveArray(doc, fieldsObj);
            if (fields == null)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the /AcroForm has no readable /Fields array; the input was copied to the output unchanged");
                output.Write(bytes, 0, bytes.Length);
                return false;
            }

            var state = new FieldWalkState
            {
                Doc        = doc,
                Index      = GetOrBuildIndex(doc),
                TargetName = fieldPartialName,
                Tooltip    = tooltip,
                Modified   = new Dictionary<int, PdfObject>(),
            };

            bool found = FindAndSetTooltip(fields, state, 0);

            if (!found)
            {
                AccessibilityDiagnostics.Report(Op,
                    "no form field with the partial name \"" + (fieldPartialName ?? "(null)") +
                    "\" was found; the input was copied to the output unchanged");
            }

            // `found` says the field was located and a revision was staged for it; only the write
            // outcome says the produced document actually carries the /TU.
            bool written = WriteAndVerify(output, bytes, doc, state.Index, state.Modified, _noObjects,
                Op, "the form field tooltip");

            return found && written;
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Stages <c>/Tabs /S</c> onto every annotated page that does not already carry it, and
        /// returns how many pages were staged. Shared by
        /// <see cref="SetStructureTabOrder(Stream, Stream)"/> and
        /// <see cref="PlanStructureTabOrder"/>, so the clear-text and encrypted routes cannot
        /// disagree about which pages need it.
        /// </summary>
        private static int StageStructureTabOrder(
            PdfDocument doc, PdfObjectIndex index, IReadOnlyList<PdfPage> pages,
            Dictionary<int, PdfObject> modified, string Op)
        {
            int changed = 0;

            for (int i = 0; i < pages.Count; i++)
            {
                PdfDictionary pageDict;
                try
                {
                    pageDict = pages[i].Dictionary;
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " could not be read; it was left unchanged", ex);
                    continue;
                }

                if (pageDict == null)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " has no dictionary; it was left unchanged");
                    continue;
                }

                // /Annots is not an inheritable key (ISO 32000-1 Table 30), so the page's own
                // entry is the whole answer — the same read the UA09 checker performs.
                var annots = ResolveArray(doc, pageDict[PdfName.Annots]);
                if (annots == null || annots.Count == 0) continue;

                // Already in structure tab order — nothing to set on this page.
                var tabs = ResolveName(doc, pageDict[PdfName.Tabs]);
                if (tabs != null && tabs.Value == _structureTabOrder.Value) continue;

                int objectNumber = index != null ? index.GetPageObjectNumber(i) : -1;
                if (objectNumber <= 0 && index != null) objectNumber = index.GetObjectNumber(pageDict);

                if (objectNumber <= 0)
                {
                    AccessibilityDiagnostics.Report(Op,
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " carries annotations but its object number could not be determined, so an " +
                        "incremental update cannot revise it; its /Tabs was not set");
                    continue;
                }

                var clone = CloneDict(pageDict);
                clone[PdfName.Tabs] = _structureTabOrder;
                modified[objectNumber] = clone;
                changed++;
            }
            return changed;
        }

        /// <summary>
        /// Computes the <c>/Tabs /S</c> edit <b>without writing it</b>, for a caller that owns the
        /// write - which is how an <b>encrypted</b> document gets UA09-conformant tab order.
        /// </summary>
        /// <remarks>
        /// <b>This edit mints nothing</b>: every object it emits is a revision of a page the
        /// document already has, so the ceiling is reserved as defence in depth. Unlike
        /// <see cref="PlanDocumentTitle"/>, nothing here has to be reachable from the trailer, so
        /// it is not affected by the <c>Run</c> limitation that one records.
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate - the ceiling of the file the revision
        /// will be appended to, plus one.
        /// </param>
        public static StructureTreePlan PlanStructureTabOrder(PdfDocument doc, int firstFreeObjectNumber)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            RequireCeiling(firstFreeObjectNumber);

            const string Op = "PdfStructureTreeEditor.PlanStructureTabOrder";
            const string Consequence = "no page received /Tabs /S";
            BeginOperation();

            if (GetCatalogOrReport(doc, Op, Consequence) == null) return StructureTreePlan.Nothing;

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the page tree could not be read; nothing was planned and " + Consequence, ex);
                return StructureTreePlan.Nothing;
            }

            if (pages == null || pages.Count == 0) return StructureTreePlan.Nothing;

            PdfObjectIndex index = GetOrBuildIndex(doc);
            if (index == null) return StructureTreePlan.Nothing;
            index.ReserveObjectNumber(firstFreeObjectNumber - 1);

            var modified = new Dictionary<int, PdfObject>();
            StageStructureTabOrder(doc, index, pages, modified, Op);
            if (modified.Count == 0) return StructureTreePlan.Nothing;

            var objects = RevisionObjects.From(modified, index, Op);

            return new StructureTreePlan(objects, newInfoObjectNumber: -1);
        }

        /// <summary>
        /// Sets <c>/Tabs /S</c> — "tab order follows the logical structure tree", ISO 32000-1
        /// Table 30 — on every page that carries at least one annotation and does not already
        /// declare it. Returns the number of pages changed.
        ///
        /// <para>
        /// <b>The condition matches the UA09 checker exactly.</b> PDF/UA-1 (ISO 14289-1, 7.18.3)
        /// requires the key only on pages that contain annotations, and
        /// <c>PdfUaChecker</c>'s UA09 test flags exactly those pages, so this method touches
        /// exactly the same set. A page with no <c>/Annots</c> (or an empty one) is left alone:
        /// writing the key there would append a revision, grow the file and change nothing the
        /// checker looks at. A checker and a fixer that disagree about which pages are in scope
        /// is what let the <c>/TabOrder</c> defect survive unnoticed, so the two conditions are
        /// deliberately kept identical.
        /// </para>
        ///
        /// <para>
        /// <b>Only the spec key counts as already-fixed.</b> A page carrying the legacy
        /// <c>/TabOrder /S</c> is treated as
        /// <i>not yet fixed</i> and receives a real <c>/Tabs /S</c> — <c>/TabOrder</c> is not a
        /// PDF key and no reader honours it. The legacy entry is <b>not</b> removed: deleting a
        /// key some other tool may read is not this method's call, and an unknown page key is
        /// harmless to conforming readers.
        /// </para>
        ///
        /// <para>
        /// <b>The contract with the UA09 check.</b> This method writes <c>/Tabs /S</c>, and only
        /// <c>/Tabs /S</c> satisfies ISO 14289-1 7.18.3. An annotated page that carries the legacy
        /// key alone is non-conformant and <i>is</i> reported by the UA09 check — running this
        /// method is what clears it. So a page fixed here is not re-reported, and a page that was
        /// never fixed here is reported exactly once; the checker and this writer agree on both
        /// what counts as satisfied and which pages are in scope.
        /// </para>
        ///
        /// <para>
        /// Every no-op path reports its reason through <see cref="AccessibilityDiagnostics"/>
        /// and copies the input through unchanged; an encrypted document is refused outright,
        /// exactly as with the other operations on this type. When no page needs the key, no
        /// revision is appended and the output is byte-identical to the input.
        /// </para>
        ///
        /// <para>
        /// <b>Invariant: a returned count &gt; 0 implies the output differs from the input.</b> The
        /// per-page tally is returned only once the appended revision carrying it has reached
        /// <paramref name="output"/>; otherwise this returns <c>0</c> and
        /// <see cref="LastWriteError"/> says why.
        /// </para>
        /// </summary>
        /// <param name="input">Readable stream containing the source PDF. Never modified.</param>
        /// <param name="output">Writable stream receiving the whole document.</param>
        /// <returns>The number of pages that were given <c>/Tabs /S</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or
        /// <paramref name="output"/> is null.</exception>
        public static int SetStructureTabOrder(Stream input, Stream output)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            const string Op = "PdfStructureTreeEditor.SetStructureTabOrder";
            const string Consequence = "no page received /Tabs /S";
            BeginOperation();

            byte[] bytes = ReadAll(input);
            using var ms  = new MemoryStream(bytes);
            using PdfDocument doc = OpenOrCopy(ms, bytes, output, Op);
            if (doc == null) return 0;

            if (ReportIfEncrypted(doc, Op, Consequence))
            {
                output.Write(bytes, 0, bytes.Length);
                return 0;
            }

            if (GetCatalogOrReport(doc, Op, Consequence) == null)
            {
                output.Write(bytes, 0, bytes.Length);
                return 0;
            }

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the page tree could not be read; the input was copied to the output unchanged and " +
                    Consequence, ex);
                output.Write(bytes, 0, bytes.Length);
                return 0;
            }

            if (pages == null || pages.Count == 0)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the document has no readable pages; the input was copied to the output unchanged and " +
                    Consequence);
                output.Write(bytes, 0, bytes.Length);
                return 0;
            }

            var index    = GetOrBuildIndex(doc);
            var modified = new Dictionary<int, PdfObject>();
            int changed  = StageStructureTabOrder(doc, index, pages, modified, Op);

            // A page whose /Tabs /S is not in the produced document did not receive it, so the
            // pre-write tally may only be returned once the revision has landed.
            if (!WriteAndVerify(output, bytes, doc, index, modified, _noObjects, Op,
                    "the /Tabs /S entries"))
            {
                return 0;
            }

            return changed;
        }

        // ================================================================
        //  Bail-out reporting
        // ================================================================

        /// <summary>
        /// Reports (and returns true) when the document is encrypted. Editing an encrypted
        /// document would require re-encrypting the appended objects, which this type does not
        /// do; the caller copies the input through unchanged instead.
        /// </summary>
        private static bool ReportIfEncrypted(PdfDocument doc, string operation, string consequence)
        {
            if (doc == null || !doc.IsEncrypted) return false;

            AccessibilityDiagnostics.Report(operation,
                "the document is encrypted and re-encrypting an appended revision is out of scope; " +
                "the input was copied to the output unchanged and " + consequence);
            return true;
        }

        private static PdfDictionary GetCatalogOrReport(PdfDocument doc, string operation, string consequence)
        {
            PdfDictionary catalog = null;
            try
            {
                catalog = doc?.Catalog?.Dictionary;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(operation,
                    "the document catalog could not be read; the input was copied to the output unchanged and " +
                    consequence, ex);
                return null;
            }

            if (catalog == null)
            {
                AccessibilityDiagnostics.Report(operation,
                    "the document has no readable /Catalog; the input was copied to the output unchanged and " +
                    consequence);
            }

            return catalog;
        }

        /// <summary>
        /// Resolves /Catalog → /StructTreeRoot, reporting the exact reason for every failure
        /// (encrypted, no catalog, untagged) instead of returning a bare null.
        /// </summary>
        private static PdfDictionary GetStructTreeRootOrReport(PdfDocument doc, string operation, string consequence)
        {
            if (ReportIfEncrypted(doc, operation, consequence)) return null;

            var catalog = GetCatalogOrReport(doc, operation, consequence);
            if (catalog == null) return null;

            var strTree = Core.Document.EntryRead.Resolve(doc.ResolveObject, catalog[PdfName.StructTreeRoot],
                out string unreadable) as PdfDictionary;
            if (strTree == null)
            {
                // An unreadable tree is not an untagged document, and saying so sends the reader to
                // the wrong fix (Core.Document.EntryRead).
                AccessibilityDiagnostics.Report(operation, unreadable != null
                    ? "the document's /StructTreeRoot could not be read (" + unreadable + "), so there is " +
                      "no logical structure this can edit; the input was copied to the output unchanged and " + consequence
                    : "the document has no /StructTreeRoot — it is untagged, so there is no logical " +
                      "structure to edit; the input was copied to the output unchanged and " + consequence);
            }

            return strTree;
        }

        // ================================================================
        //  Structure / field helpers
        // ================================================================

        /// <summary>Mutable state threaded through the AcroForm /Kids walk.</summary>
        private sealed class FieldWalkState
        {
            internal PdfDocument Doc;
            internal PdfObjectIndex Index;
            internal string TargetName;
            internal string Tooltip;
            internal Dictionary<int, PdfObject> Modified;

            /// <summary>
            /// Cycle guard. A /Kids cycle in untrusted input would otherwise be bounded only
            /// by the depth cap, which for a branching cycle still costs 2^depth visits.
            /// </summary>
            internal readonly HashSet<PdfObject> Visited =
                new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

            internal bool DepthReported;

            /// <summary>
            /// Remaining traversal steps. Bounds total work, which the visited set does not:
            /// see the comment at the top of <see cref="FindAndSetTooltip"/>'s loop.
            /// </summary>
            internal int Budget = MaxFieldTreeNodes;

            internal bool BudgetReported;
        }

        private static bool FindAndSetTooltip(PdfArray fields, FieldWalkState state, int depth)
        {
            if (fields == null) return false;

            if (depth > MaxFieldTreeDepth)
            {
                if (!state.DepthReported)
                {
                    state.DepthReported = true;
                    AccessibilityDiagnostics.Report("PdfStructureTreeEditor.SetFieldTooltip",
                        "the AcroForm field tree is deeper than the " +
                        MaxFieldTreeDepth.ToString(CultureInfo.InvariantCulture) +
                        "-level cap; deeper fields were not searched");
                }
                return false;
            }

            for (int i = 0; i < fields.Count; i++)
            {
                // A node budget, spent per iteration. The visited set alone bounds how many
                // distinct fields are expanded but not the total work: N field dictionaries that
                // all point their /Kids at one shared N-entry array expand N times over N entries,
                // which is O(N²) resolve-and-probe operations for a file of a few tens of KB.
                if (state.Budget <= 0)
                {
                    if (!state.BudgetReported)
                    {
                        state.BudgetReported = true;
                        AccessibilityDiagnostics.Report("PdfStructureTreeEditor.SetFieldTooltip",
                            "the AcroForm field traversal stopped after " +
                            MaxFieldTreeNodes.ToString(CultureInfo.InvariantCulture) +
                            " steps; the remaining fields were not searched");
                    }
                    return false;
                }
                state.Budget--;

                PdfObject raw = fields[i];
                int knownNumber = raw is PdfReference fieldRef ? fieldRef.ReferenceObjectNumber : -1;

                var field = ResolveDict(state.Doc, raw);
                if (field == null) continue;
                if (!state.Visited.Add(field)) continue; // already walked (cycle or shared node)

                string partialName = GetTextValue(state.Doc, field, PdfName.T);
                if (partialName == state.TargetName)
                {
                    int objNum = knownNumber > 0
                        ? knownNumber
                        : (state.Index != null ? state.Index.GetObjectNumber(field) : -1);

                    if (objNum > 0)
                    {
                        var clone = CloneDict(field);
                        clone[PdfName.TU] = MakeTextString(state.Tooltip);
                        state.Modified[objNum] = clone;
                        return true;
                    }

                    AccessibilityDiagnostics.Report("PdfStructureTreeEditor.SetFieldTooltip",
                        "the form field named \"" + (state.TargetName ?? "(null)") +
                        "\" is a direct (inline) dictionary with no object number, so an incremental " +
                        "update cannot revise it; its /TU was not set");
                }

                var kidsObj = field[PdfName.Kids];
                var kids    = kidsObj as PdfArray ?? ResolveArray(state.Doc, kidsObj);
                if (kids != null && FindAndSetTooltip(kids, state, depth + 1))
                    return true;
            }

            return false;
        }

        // ================================================================
        //  Write verification — the assembly's single mechanism
        // ================================================================

        /// <summary>
        /// Hands an edit to <see cref="DocumentRevisionWriter"/> and reports whether the
        /// appended revision <em>actually reached</em> <paramref name="output"/>.
        ///
        /// <para>
        /// <b>Why this exists.</b> The writer is documented never to throw: it catches every
        /// failure — an <see cref="IOException"/> from the caller's stream, an object it had to
        /// skip, a trailer it could not rebuild — records it on
        /// <see cref="AccessibilityDiagnostics"/> and leaves the output holding just the original
        /// bytes. A caller that returns its <i>plan</i> afterwards would report a remediated document
        /// about a file that is byte-for-byte the input. This is the one check, shared with
        /// <c>PdfAutoTagger</c> and <c>PdfArtifactMarker</c>, instead of one copy per writer.
        /// </para>
        ///
        /// <para>
        /// <b>The mechanism itself is <see cref="VerifiedWrite"/>.</b> Counting the bytes
        /// <paramref name="output"/> actually accepted is exact where reading the diagnostic slot is
        /// a lossy proxy; that reasoning, and the counting stream, live in one place for all four
        /// writers in this assembly. What stays here is the <em>message</em> — which edit was lost,
        /// and what the caller is therefore being told instead.
        /// </para>
        /// </summary>
        /// <param name="output">Destination for the whole document; the writer emits the original bytes itself.</param>
        /// <param name="originalBytes">The complete original PDF.</param>
        /// <param name="doc">The parsed original.</param>
        /// <param name="index">Index over <paramref name="doc"/>; may be null.</param>
        /// <param name="modifiedObjects">Existing objects being revised, keyed by object number. May be null.</param>
        /// <param name="newObjects">Objects being added, keyed by object number. May be null.</param>
        /// <param name="newInfoObjectNumber">Object number of a newly written <c>/Info</c>, or -1.</param>
        /// <param name="operation">Stable <c>"Type.Method"</c> identifier used in the diagnostic.</param>
        /// <param name="whatWasPlanned">
        /// Noun phrase naming the edit, e.g. <c>"the renumbered heading levels"</c>. Spliced into
        /// the failure message so the caller is told which edit was lost.
        /// </param>
        /// <param name="failureDetail">
        /// The reason the revision did not land, or <c>null</c> when this method returns
        /// <c>true</c>. Already reported through <see cref="AccessibilityDiagnostics"/>.
        /// </param>
        /// <returns>
        /// <c>true</c> when the revision was appended, or when there was legitimately nothing to
        /// append and the original document was copied through; <c>false</c> when the output holds
        /// only — or less than — the original document despite an edit having been planned.
        /// </returns>
        internal static bool WriteVerifiedRevision(
            Stream output,
            byte[] originalBytes,
            PdfDocument doc,
            PdfObjectIndex index,
            IReadOnlyDictionary<int, PdfObject> modifiedObjects,
            IReadOnlyDictionary<int, PdfObject> newObjects,
            int newInfoObjectNumber,
            string operation,
            string whatWasPlanned,
            out string failureDetail)
            // The mechanism itself lives in Base, next to the byte-counting write it
            // reports on, so the font remediator does not reach into a structure-tree editor to
            // write a font program. The diagnostic surfaces here: Base's sink is forwarded into
            // AccessibilityDiagnostics, so PdfAccessibility.LastDiagnostic and DiagnosticReported
            // observe it.
            => VerifiedWrite.WriteOrExplain(
                output, originalBytes, doc, index, modifiedObjects, newObjects,
                newInfoObjectNumber, operation, whatWasPlanned, out failureDetail);

        /// <summary>
        /// <see cref="WriteVerifiedRevision"/> for this type's own operations: additionally records
        /// the failure in <see cref="LastWriteError"/>, which is the only warning channel the bare
        /// <c>int</c> / <c>bool</c> return types have.
        /// </summary>
        private static bool WriteAndVerify(
            Stream output,
            byte[] originalBytes,
            PdfDocument doc,
            PdfObjectIndex index,
            IReadOnlyDictionary<int, PdfObject> modifiedObjects,
            IReadOnlyDictionary<int, PdfObject> newObjects,
            string operation,
            string whatWasPlanned,
            int newInfoObjectNumber = -1)
        {
            bool ok = WriteVerifiedRevision(
                output, originalBytes, doc, index, modifiedObjects, newObjects,
                newInfoObjectNumber, operation, whatWasPlanned, out string failureDetail);

            _lastWriteError = ok ? null : failureDetail;
            return ok;
        }

        // ================================================================
        //  Shared helpers (also used by PdfAutoTagger / PdfArtifactMarker /
        //  PdfReadingOrderAnalyzer)
        // ================================================================

        // One implementation, in Core.IO.StreamBytes. It rewinds a seekable stream before
        // reading; this copy read from wherever the position happened to be.
        internal static byte[] ReadAll(Stream s) => StreamBytes.ReadAll(s);

        /// <summary>
        /// One implementation, in <c>Core.Objects.PdfDictionary.CloneShallow</c>, with the
        /// null tolerance <b>stated rather than inherited</b> — the same shape
        /// <c>PdfFontEmbeddingRemediator.Helpers</c> and <c>PdfAccessibility</c> already use.
        /// </summary>
        /// <remarks>
        /// <para>This was the sixteenth copy, and the last one in the tree. It differed from the
        /// survivor in a way no call site here can currently reach but nothing prevents:
        /// <c>CloneShallow</c> goes through <c>NewLike</c>, which tests <c>source is PdfStream</c>
        /// <b>first</b> and carries <c>RawData</c> and <c>StreamOffset</c> across, while this body
        /// built a bare <c>PdfDictionary</c> and copied entries — flattening a stream into a plain
        /// dictionary that keeps the stream's keys, including a now-lying <c>/Length</c>, and that
        /// <c>PdfWriter</c> emits with no <c>stream</c>/<c>endstream</c> at all. Every route in
        /// here reaches this method through an <c>as PdfDictionary</c> cast, and a
        /// <c>PdfStream</c> satisfies it.</para>
        ///
        /// <para><b>The tell was one key cloned two ways inside one assembly:</b>
        /// <c>PdfAccessibility</c> clones <c>/MarkInfo</c> through <c>CloneShallow</c> and
        /// <c>PdfArtifactMarker</c> cloned the same <c>/MarkInfo</c> through this.</para>
        /// </remarks>
        internal static PdfDictionary CloneDict(PdfDictionary src)
            => src == null ? new PdfDictionary() : PdfDictionary.CloneShallow(src);

        internal static PdfDictionary ResolveDict(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            if (obj is PdfDictionary direct) return direct;
            if (doc == null) return null;

            try
            {
                return doc.ResolveObject(obj) as PdfDictionary;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfStructureTreeEditor.ResolveDict",
                    "an indirect object could not be resolved to a dictionary; it is treated as absent", ex);
                return null;
            }
        }

        // One implementation, in Internal.Resolve.
        private static PdfArray ResolveArray(PdfDocument doc, PdfObject obj)
            => Internal.Resolve.Array(doc, obj, "PdfStructureTreeEditor.ResolveArray");

        /// <summary>
        /// Reads a name-valued entry, resolving it when the producer stored it as an indirect
        /// object (legal for every value type; rare but real for page keys such as /Tabs).
        /// </summary>
        private static PdfName ResolveName(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return null;
            if (obj is PdfName direct) return direct;
            if (doc == null) return null;

            try
            {
                return doc.ResolveObject(obj) as PdfName;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfStructureTreeEditor.ResolveName",
                    "an indirect object could not be resolved to a name; it is treated as absent", ex);
                return null;
            }
        }

        /// <summary>
        /// Reads a text-string entry, resolving it when the producer stored it as an indirect
        /// object (legal for every string-valued key, and used by real-world producers for
        /// long /Alt values).
        /// </summary>
        private static string GetTextValue(PdfDocument doc, PdfDictionary dict, PdfName key)
        {
            PdfObject value = dict?[key];
            if (value == null) return null;

            var str = value as PdfString;
            if (str == null && doc != null)
            {
                try
                {
                    str = doc.ResolveObject(value) as PdfString;
                }
                catch (Exception ex)
                {
                    AccessibilityDiagnostics.Report("PdfStructureTreeEditor.GetTextValue",
                        "an indirect /" + key.Value + " value could not be resolved; it is treated as absent", ex);
                    return null;
                }
            }

            return str?.GetText();
        }

        /// <summary>
        /// Object number of the document catalog. The trailer's /Root reference is the
        /// authoritative answer and works even when the catalog instance is not in the index;
        /// the index is the fallback.
        /// </summary>
        private static int ResolveCatalogObjectNumber(PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog)
        {
            try
            {
                if (doc?.Trailer?[PdfName.Root] is PdfReference rootRef && rootRef.ReferenceObjectNumber > 0)
                    return rootRef.ReferenceObjectNumber;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfStructureTreeEditor.ResolveCatalogObjectNumber",
                    "the trailer's /Root could not be read; the object index is used instead", ex);
            }

            return index != null ? index.GetObjectNumber(catalog) : -1;
        }

        // ================================================================
        //  PDF text strings
        // ================================================================

        /// <summary>
        /// Builds a PDF text string (ISO 32000-1 §7.9.2.2) that round-trips
        /// <paramref name="text"/> exactly.
        ///
        /// <para><b>One implementation, in Core</b> — <see cref="PdfString(string)"/>, which is its
        /// own inverse under <see cref="PdfString.GetText"/>: <c>RequiresUtf16</c> consults the very
        /// table <c>GetText</c> decodes with, so a byte the decoder would translate is by
        /// construction a byte the encoder does not emit.</para>
        ///
        /// <para>Its UTF-16BE branch is a <i>literal</i> string, so a Hebrew title serialises as
        /// <c>(&lt;FE&gt;&lt;FF&gt;…)</c> rather than <c>&lt;FEFF…&gt;</c>. Both are §7.9.2.2 text
        /// strings, both read back identically, and the literal is the shorter of the two.
        /// <c>PdfWriter</c> escapes <c>(</c>, <c>)</c>, <c>\</c>, CR and LF byte-wise, which is what
        /// makes an arbitrary UTF-16BE payload safe inside a literal.</para>
        ///
        /// <para><b>Null and empty stay an empty string rather than throwing</b>, which the Core
        /// constructor does for null: callers document that behaviour (see
        /// <c>PlanFieldTooltip</c> and <c>SetFieldTooltip</c>, which refuse a blank tooltip
        /// precisely <i>because</i> this method would otherwise hand them <c>/TU ()</c>).</para>
        /// </summary>
        internal static PdfString MakeTextString(string text) => new PdfString(text ?? string.Empty);

        /// <summary>
        /// Whether <paramref name="tag"/> matches the BCP 47 shape
        /// <c>^[A-Za-z]{1,8}(-[A-Za-z0-9]{1,8})*$</c>. Hand-rolled rather than a
        /// <see cref="System.Text.RegularExpressions.Regex"/> so the check is allocation-free
        /// and strictly linear in the tag length.
        /// </summary>
        private static bool IsWellFormedLanguageTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;

            int i = 0;

            // Primary subtag: 1-8 ALPHA.
            int start = i;
            while (i < tag.Length && IsAlpha(tag[i])) i++;
            int length = i - start;
            if (length < 1 || length > 8) return false;

            // Zero or more "-" subtags of 1-8 alphanumerics.
            while (i < tag.Length)
            {
                if (tag[i] != '-') return false;
                i++;

                start = i;
                while (i < tag.Length && IsAlphaNumeric(tag[i])) i++;
                length = i - start;
                if (length < 1 || length > 8) return false;
            }

            return true;
        }

        private static bool IsAlpha(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsAlphaNumeric(char c) => IsAlpha(c) || (c >= '0' && c <= '9');

        // ================================================================
        //  Object index
        // ================================================================

        /// <summary>
        /// The <see cref="PdfObjectIndex"/> for <paramref name="doc"/>, built on first use and
        /// cached for the document's lifetime. Returns null only when the index cannot be
        /// built at all — every consumer treats a null index as "no object numbers known".
        /// </summary>
        internal static PdfObjectIndex GetOrBuildIndex(PdfDocument doc)
        {
            if (doc == null) return null;

            try
            {
                return _indexCache.GetValue(doc, static d => PdfObjectIndex.Build(d));
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfStructureTreeEditor.GetOrBuildIndex",
                    "the document object index could not be built; every object-number lookup returns -1", ex);
                return null;
            }
        }

        /// <summary>
        /// <b>Legacy shim.</b> Delegates to <see cref="PdfObjectIndex.GetObjectNumber"/> via a
        /// per-document cached index. The original implementation walked the entire
        /// cross-reference table calling <c>GetObject</c> on every entry, once per lookup, and
        /// was called inside per-page and per-element loops.
        ///
        /// <para>
        /// New code should build one <see cref="PdfObjectIndex"/> per operation
        /// (<see cref="GetOrBuildIndex"/>) and call <c>GetObjectNumber</c> on it directly;
        /// this wrapper exists only so call sites that have not migrated yet still compile.
        /// </para>
        /// </summary>
        internal static int FindObjectNumber(PdfDocument doc, PdfDictionary dict)
        {
            if (doc == null || dict == null) return -1;

            PdfObjectIndex index = GetOrBuildIndex(doc);
            return index != null ? index.GetObjectNumber(dict) : -1;
        }
    }
}

