// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Artifacts
{
    /// <summary>
    /// Per-page detail for <see cref="PdfArtifactMarker.MarkHeadersAndFootersDetailed"/>.
    ///
    /// <para>
    /// <see cref="ArtifactMarkResult"/> carries only a count. Marking artifacts is a
    /// content-stream edit that is deliberately refused on any page the rewriter cannot
    /// prove safe, so "0 artifacts" has several very different meanings — no header exists,
    /// the page was already marked, the page's content would not scan. This type reports
    /// which of those happened so a caller never has to guess.
    /// </para>
    ///
    /// <para>
    /// <b>Every count describes the document that was written, not the one that was planned.</b>
    /// The incremental writer never throws for a recoverable failure — it reports through
    /// <c>AccessibilityDiagnostics</c> and leaves the output holding just the original bytes — so a
    /// result assembled from the marking pass's own tallies could report "12 artifacts marked" for
    /// a file that is byte-identical to the input. When the revision does not reach the output,
    /// <see cref="ArtifactsMarked"/> and <see cref="PagesChanged"/> are <c>0</c>, the pages whose
    /// rewrite was lost are counted in <see cref="PagesSkipped"/>, and <see cref="Warnings"/>
    /// names the cause.
    /// </para>
    /// </summary>
    public sealed class ArtifactMarkDetail
    {
        /// <summary>Number of marked-content artifact sequences written into page content streams.</summary>
        public int ArtifactsMarked { get; }

        /// <summary>Number of pages examined.</summary>
        public int PagesProcessed { get; }

        /// <summary>Number of pages whose content stream was rewritten.</summary>
        public int PagesChanged { get; }

        /// <summary>
        /// Number of pages examined but left unchanged in the output — either their content could
        /// not be rewritten safely, or the appended revision that carried their rewrite never
        /// reached the output stream. Every one of them is accounted for by at least one entry in
        /// <see cref="Warnings"/>.
        /// </summary>
        public int PagesSkipped { get; }

        /// <summary>
        /// Number of pages that were examined successfully but had nothing to mark — no content
        /// stream at all, or text that all falls outside the header and footer bands. Not a failure.
        /// </summary>
        public int PagesWithNothingToMark { get; }

        /// <summary>
        /// Human-readable reasons for every tolerated failure and every skipped page, in page order.
        /// Empty when the operation ran cleanly. Never null.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; }

        /// <summary>True when at least one page content stream was rewritten.</summary>
        public bool DocumentChanged => PagesChanged > 0;

        public ArtifactMarkDetail(
            int artifactsMarked,
            int pagesProcessed,
            int pagesChanged,
            int pagesSkipped,
            int pagesWithNothingToMark,
            IReadOnlyList<string> warnings)
        {
            ArtifactsMarked        = artifactsMarked;
            PagesProcessed         = pagesProcessed;
            PagesChanged           = pagesChanged;
            PagesSkipped           = pagesSkipped;
            PagesWithNothingToMark = pagesWithNothingToMark;
            Warnings               = warnings ?? EmptyWarnings;
        }

        internal static readonly IReadOnlyList<string> EmptyWarnings = new string[0];

        /// <summary>Narrows this detail to the historical <see cref="ArtifactMarkResult"/> surface.</summary>
        public ArtifactMarkResult ToResult() => new ArtifactMarkResult(ArtifactsMarked);

        public override string ToString() =>
            "ArtifactMark: " + ArtifactsMarked.ToString(CultureInfo.InvariantCulture) +
            " artifacts on " + PagesChanged.ToString(CultureInfo.InvariantCulture) + " of " +
            PagesProcessed.ToString(CultureInfo.InvariantCulture) + " pages (" +
            PagesSkipped.ToString(CultureInfo.InvariantCulture) + " skipped, " +
            PagesWithNothingToMark.ToString(CultureInfo.InvariantCulture) + " with nothing to mark)";
    }

    /// <summary>
    /// Marks running headers and footers as <b>content-level artifacts</b>, so assistive
    /// technology skips them instead of reading a page number aloud on every page.
    ///
    /// <para>
    /// <b>What an artifact actually is.</b> In PDF (ISO 32000-1 §14.8.2) and in PDF/UA-1, an
    /// artifact is a marked-content sequence in the <i>content stream</i> —
    /// <c>/Artifact &lt;&lt;/Type /Pagination /Subtype /Header&gt;&gt; BDC … EMC</c> — and it is
    /// precisely the content that is <i>not</i> reachable from the structure tree. Artifacts carry
    /// no MCID, so they need no structure element, no <c>/K</c> entry and no ParentTree key.
    /// </para>
    ///
    /// <para>
    /// <b>What it does not do.</b> It adds no structure elements of type <c>/S /Artifact</c>.
    /// <c>Artifact</c> is not a standard structure type in PDF 1.7 Table 333 at all, and putting
    /// artifacts <i>into</i> the structure tree inverts their meaning: a validator flags the unknown
    /// role, and the header text remains untagged content in the content stream — the file gets
    /// worse, not better. Nor does it fabricate a <c>StructTreeRoot</c> → <c>Document</c> →
    /// <c>Artifact</c> tree whose elements point at no content.
    /// </para>
    ///
    /// <para>
    /// <b>What it does.</b> For each page it runs
    /// <see cref="PageContentRewriter"/>/<see cref="ContentStreamTagger"/> with a classifier that
    /// returns <c>Artifact("Pagination", "Header")</c> or <c>Artifact("Pagination", "Footer")</c>
    /// for lines inside the margin bands (ISO 32000-1 Table 330: running headers and footers are
    /// <c>/Pagination</c> artifacts with <c>/Subtype /Header</c> or <c>/Footer</c>) and
    /// <c>Unmarked</c> for everything else — body content is deliberately left exactly as it was,
    /// because tagging it is the auto-tagger's job, not this operation's. No structure objects are
    /// created.
    /// </para>
    ///
    /// <para>
    /// <c>/MarkInfo &lt;&lt;/Marked true&gt;&gt;</c> is set <b>only when the document's structure
    /// tree actually reaches page content</b> — the test performed by
    /// <see cref="PdfAccessibility.StructTreeReachesContent"/>, which requires a <c>/ParentTree</c>
    /// and at least one element holding an MCID, an <c>/MCR</c> or an <c>/OBJR</c>. <b>The presence
    /// of a <c>/StructTreeRoot</c> is deliberately <i>not</i> the test</b>: a hollow tree resolves
    /// to a dictionary and would pass it, and stamping the Tagged-PDF claim onto such a document is
    /// exactly the defect this gate exists to prevent. That entry asserts conformance to Tagged PDF
    /// conventions (ISO 32000-1 Table 321, §14.8.1), which requires a logical structure tree wired
    /// to the content — and this operation deliberately creates none. Setting it on an untagged
    /// document would make the file claim something false, and the library's own checker would then
    /// report every page as missing <c>/StructParents</c>: findings manufactured by this step. When
    /// the flag is withheld, a warning says so. Everything is emitted as a single incremental
    /// update.
    /// </para>
    ///
    /// <para>
    /// <b>Safety.</b> A page is skipped — never partially rewritten — when its content already
    /// contains marked content, will not scan safely, is rotated so that the user-space top/bottom
    /// edges are not the visual ones, or has margin bands that would swallow the whole page. Every
    /// skip is recorded in <see cref="ArtifactMarkDetail.Warnings"/> and reported through
    /// <see cref="AccessibilityDiagnostics"/>.
    /// </para>
    /// </summary>
    public static class PdfArtifactMarker
    {
        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Detects running headers and footers (text within <paramref name="headerMarginPt"/> of the
        /// top or <paramref name="footerMarginPt"/> of the bottom of each page) and wraps them in
        /// <c>/Artifact … BDC … EMC</c> marked-content sequences.
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">
        /// Output PDF stream. The original bytes are always written, followed by an appended
        /// revision when anything changed — so the output is a valid PDF even for a no-op run.
        /// If that revision cannot be appended, the returned count is <c>0</c>: the result always
        /// describes the document that reached this stream.
        /// </param>
        /// <param name="headerMarginPt">Points from the top of the page to treat as a header. Default 50. 0 disables header detection.</param>
        /// <param name="footerMarginPt">Points from the bottom of the page to treat as a footer. Default 50. 0 disables footer detection.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.</exception>
        public static ArtifactMarkResult MarkHeadersAndFooters(
            Stream input, Stream output,
            float headerMarginPt = 50f,
            float footerMarginPt = 50f)
            => MarkHeadersAndFootersDetailed(input, output, headerMarginPt, footerMarginPt).ToResult();

        /// <summary>
        /// Same operation as <see cref="MarkHeadersAndFooters"/>, but returns the per-page outcome
        /// (pages changed, pages skipped and why) instead of only a count.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is null.</exception>
        public static ArtifactMarkDetail MarkHeadersAndFootersDetailed(
            Stream input, Stream output,
            float headerMarginPt = 50f,
            float footerMarginPt = 50f)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            // A stale message from an unrelated call must never be mistaken for a diagnosis of
            // this one (CLAUDE.md → Diagnostics Pattern).
            AccessibilityDiagnostics.Clear();

            var warnings = new List<string>();

            byte[] bytes;
            try
            {
                bytes = PdfStructureTreeEditor.ReadAll(input);
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters",
                    "the input stream could not be read; nothing was written to the output", ex);
                return new ArtifactMarkDetail(0, 0, 0, 0, 0, warnings);
            }

            if (bytes.Length == 0)
            {
                Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters",
                    "the input stream was empty; nothing was written to the output");
                return new ArtifactMarkDetail(0, 0, 0, 0, 0, warnings);
            }

            MemoryStream source = null;
            PdfDocument doc = null;
            try
            {
                source = new MemoryStream(bytes);
                doc = PdfDocument.Open(source);
            }
            catch (Exception ex)
            {
                // The document is unreadable, so it cannot be edited — but the caller still gets
                // its bytes back rather than an empty output stream.
                if (doc != null) doc.Dispose();
                if (source != null) source.Dispose();

                Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters",
                    "the document could not be parsed; the original bytes were written unchanged", ex);
                output.Write(bytes, 0, bytes.Length);
                return new ArtifactMarkDetail(0, 0, 0, 0, 0, warnings);
            }

            try
            {
                return MarkCore(doc, bytes, output, headerMarginPt, footerMarginPt, warnings);
            }
            finally
            {
                doc.Dispose();
                source.Dispose();
            }
        }

        // ================================================================
        //  Core
        // ================================================================

        private static ArtifactMarkDetail MarkCore(
            PdfDocument doc, byte[] bytes, Stream output,
            float headerMarginPt, float footerMarginPt, List<string> warnings)
        {
            PlannedMarking plan = BuildPlan(doc, headerMarginPt, footerMarginPt, warnings, out PdfObjectIndex planIndex);
            if (plan == null)
            {
                // BuildPlan already wrote the passthrough copy and warned.
                output.Write(bytes, 0, bytes.Length);
                return new ArtifactMarkDetail(0, 0, 0, 0, 0, warnings);
            }

            if (!WriteRevision(output, bytes, doc, planIndex, plan.Modified, plan.Created, warnings))
                return plan.Discarded;

            if (plan.MarkInfoWarning != null) warnings.Add(plan.MarkInfoWarning);
            return plan.Applied;
        }

        /// <summary>
        /// Computes the marking revision <b>without writing it</b>. Shared by the clear-text
        /// <see cref="MarkCore"/> and by <see cref="Plan"/>, so the two cannot disagree about what
        /// an artifact-marking revision contains. Returns <c>null</c> when nothing can be planned.
        /// </summary>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate, or 0 for "this document's own ceiling"
        /// (the clear-text path, where the document being planned against IS the one being written).
        /// <b>Reserved before anything is minted</b> — see the note at the reservation itself.
        /// </param>
        /// <param name="doc">The document to plan against.</param>
        /// <param name="headerMarginPt">
        /// Height of the top band, in points, whose content is treated as a running header.
        /// </param>
        /// <param name="footerMarginPt">Height of the equivalent bottom band, in points.</param>
        /// <param name="warnings">Accumulates pages that were skipped and why.</param>
        /// <param name="builtIndex">
        /// The object index built while planning, handed back so the caller writes against the same
        /// one rather than rebuilding it.
        /// </param>
        private static PlannedMarking BuildPlan(
            PdfDocument doc, float headerMarginPt, float footerMarginPt,
            List<string> warnings, out PdfObjectIndex builtIndex, int firstFreeObjectNumber = 0)
        {
            builtIndex = null;
            if (doc.IsEncrypted)
            {
                // Rewriting a content stream in an encrypted document would require decrypting it
                // and re-encrypting the replacement with the document's key; this operation does
                // not carry an encryption context, and an incremental update that appends a
                // cleartext content stream to an encrypted file is unreadable.
                Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters",
                    "the document is encrypted; its content streams were left unchanged");
                return null;   // MarkCore writes the passthrough; Plan simply has nothing to append.
            }

            PdfDictionary catalog = doc.Catalog != null ? doc.Catalog.Dictionary : null;
            if (catalog == null)
            {
                Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters",
                    "the document has no readable catalog; nothing could be marked");
                return null;   // MarkCore writes the passthrough; Plan simply has nothing to append.
            }

            PdfObjectIndex index;
            try
            {
                index = PdfObjectIndex.Build(doc);

                // BEFORE any allocation, and that ordering is the whole point. Plan is handed a
                // DECRYPTED COPY of the file the revision is appended to, and decryption unpacks
                // object streams and drops the cross-reference stream — so the copy's ceiling can
                // be LOWER than the target's. Minting first and reserving afterwards (which is how
                // this was written, with a comment claiming it was safe) hands out a number that is
                // live in the target file; the appended object then supersedes it. That was not
                // theoretical: it superseded an object the /Encrypt dictionary needed, and
                // decryption of the result failed with "Unsupported encryption handler:
                // /FlateDecode" — the decryptor reading a content stream where the handler should
                // be.
                if (firstFreeObjectNumber > 1) index.ReserveObjectNumber(firstFreeObjectNumber - 1);
                builtIndex = index;
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters",
                    "the object index could not be built, so no object could be revised safely; " +
                    "the original bytes were written unchanged", ex);
                return null;   // MarkCore writes the passthrough; Plan simply has nothing to append.
            }

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters",
                    "the page tree could not be read; the original bytes were written unchanged", ex);
                return null;   // MarkCore writes the passthrough; Plan simply has nothing to append.
            }

            var modifiedObjects = new Dictionary<int, PdfObject>();
            var newObjects      = new Dictionary<int, PdfObject>();
            var visitedPages    = new HashSet<int>();

            int artifactsMarked = 0;
            int pagesProcessed  = 0;
            int pagesChanged    = 0;
            int pagesSkipped    = 0;
            int pagesNothing    = 0;

            int pageCount = pages != null ? pages.Count : 0;
            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                PdfPage page = pages[pageIndex];
                if (page == null) continue;

                pagesProcessed++;

                PageOutcome outcome = MarkPage(
                    doc, index, page, pageIndex, visitedPages,
                    headerMarginPt, footerMarginPt,
                    modifiedObjects, newObjects, warnings);

                switch (outcome.Kind)
                {
                    case PageOutcomeKind.Changed:
                        pagesChanged++;
                        artifactsMarked += outcome.ArtifactsMarked;
                        break;
                    case PageOutcomeKind.Skipped:
                        pagesSkipped++;
                        break;
                    default:
                        pagesNothing++;
                        break;
                }
            }

            // /MarkInfo <</Marked true>> asserts that the document "conforms to Tagged PDF
            // conventions" (ISO 32000-1 Table 321, §14.8.1) — which requires a logical structure
            // tree that is actually wired to the content. Having written marked content is
            // necessary but nowhere near sufficient: this type deliberately creates no structure
            // elements, so setting the flag on a document with no structure tree — or with a hollow
            // one, which is the same thing to assistive technology — claims something false, and
            // the library's own checker then treats the file as ClaimsTagged and reports every page
            // as missing /StructParents — findings manufactured by this step.
            // Held back rather than added straight away: it describes the document this pass is
            // about to write, so it must not be reported if that write never lands. (The
            // EnsureMarkInfo call cannot be deferred the same way — it contributes to the object
            // set the write is built from.)
            string markInfoWarning = null;

            if (pagesChanged > 0)
            {
                if (StructTreeReachesContent(doc, index, catalog))
                {
                    EnsureMarkInfo(doc, index, catalog, modifiedObjects, warnings);
                }
                else
                {
                    markInfoWarning =
                        "Headers and footers were marked as artifacts in the page content, but "
                        + "/MarkInfo <</Marked true>> was NOT set: the document has no structure tree, or "
                        + "has one that reaches no page content (no /ParentTree, or no element holding an "
                        + "MCID, /MCR or /OBJR). Either way it is not a Tagged PDF and claiming otherwise "
                        + "would be false. Run AutoTag (or another tagger) to build a structure tree — "
                        + "AutoTag supersedes a hollow one; the artifacts marked here are already correct "
                        + "and will be respected.";
                }
            }

            // The counts above describe what this pass *intended* to write. Nothing may be
            // reported to the caller until the revision carrying it has actually reached `output`.
            //
            // The applied/discarded pair is built here rather than at each return so the clear-text
            // path and Plan report identically: a marking that was planned and then lost must read
            // as ZERO artifacts on zero changed pages, with those pages counted as skipped, which
            // keeps PagesChanged + PagesSkipped + PagesWithNothingToMark == PagesProcessed.
            var discarded = new ArtifactMarkDetail(
                0, pagesProcessed, 0, pagesSkipped + pagesChanged, pagesNothing, warnings);
            var applied = new ArtifactMarkDetail(
                artifactsMarked, pagesProcessed, pagesChanged, pagesSkipped, pagesNothing, warnings);

            return new PlannedMarking(
                modifiedObjects, newObjects, applied, discarded, markInfoWarning);
        }

        /// <summary>
        /// Computes the artifact-marking revision <b>without writing it</b>, so a caller that owns
        /// the write can append it to bytes this method never sees — which is how an
        /// <b>encrypted</b> document gets its headers and footers marked.
        /// </summary>
        /// <remarks>
        /// <para>The clear-text entry points append a clear-text revision and therefore refuse an
        /// encrypted document. This assembly cannot decrypt (it does not reference
        /// <c>Tetitulz.Pdf.Encryption</c>, and must not), so the operation travels to the decryptor,
        /// exactly as <c>Tagging.PdfAutoTagger.Plan</c> and
        /// <c>Metadata.PdfUaXmpWriter.Plan</c> already do:</para>
        /// <code>
        /// PdfEncryptedIncrementalUpdate.Run(input, output, password,
        ///     (decrypted, firstFree) =&gt; PdfArtifactMarker.Plan(decrypted, firstFree).Objects);
        /// </code>
        /// <para><b><paramref name="firstFreeObjectNumber"/> is not a convenience.</b> The document
        /// handed in is a <i>decrypted copy</i> of the file the revision is appended to, and
        /// decryption unpacks object streams and drops the cross-reference stream — so the copy's
        /// highest object number can be <i>lower</i> than the original's. Allocating from the copy's
        /// own ceiling would mint a number that is live in the file being written, and the appended
        /// object would silently supersede it.</para>
        /// <para><b>Report through <see cref="ArtifactMarkPlan.Describe"/>, not from the counts
        /// directly</b>: whether the revision reached the file is known only to the caller, and a
        /// marking that was planned and then lost must read as zero artifacts rather than as a
        /// marked document.</para>
        /// </remarks>
        /// <param name="doc">The document to plan against. Never modified.</param>
        /// <param name="firstFreeObjectNumber">
        /// The lowest object number the plan may allocate — the object-number ceiling of the file
        /// the revision will be appended to, plus one.
        /// </param>
        /// <param name="headerMarginPt">Header band height in points.</param>
        /// <param name="footerMarginPt">Footer band height in points.</param>
        /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="firstFreeObjectNumber"/> is below 1 — object number 0 is the free-list
        /// head and can never be written.
        /// </exception>
        public static ArtifactMarkPlan Plan(
            PdfDocument doc,
            int firstFreeObjectNumber,
            float headerMarginPt = 50f,
            float footerMarginPt = 50f)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (firstFreeObjectNumber < 1)
                throw new ArgumentOutOfRangeException(nameof(firstFreeObjectNumber),
                    "Object numbering starts at 1; 0 is the free-list head and can never be written.");

            var warnings = new List<string>();
            PlannedMarking planned = BuildPlan(
                doc, headerMarginPt, footerMarginPt, warnings, out PdfObjectIndex index,
                firstFreeObjectNumber);

            if (planned == null)
            {
                var nothing = new ArtifactMarkDetail(0, 0, 0, 0, 0, warnings);
                return new ArtifactMarkPlan(Array.Empty<IndirectObject>(), nothing, nothing, null);
            }

            var objects = RevisionObjects.From(
                planned.Created, planned.Modified, index, "PdfArtifactMarker.Plan");

            return new ArtifactMarkPlan(
                objects, planned.Applied, planned.Discarded, planned.MarkInfoWarning);
        }

        /// <summary>The output of <see cref="BuildPlan"/> — objects plus both readings of them.</summary>
        private sealed class PlannedMarking
        {
            internal PlannedMarking(
                Dictionary<int, PdfObject> modified, Dictionary<int, PdfObject> created,
                ArtifactMarkDetail applied, ArtifactMarkDetail discarded, string markInfoWarning)
            {
                Modified = modified;
                Created = created;
                Applied = applied;
                Discarded = discarded;
                MarkInfoWarning = markInfoWarning;
            }

            internal Dictionary<int, PdfObject> Modified { get; }
            internal Dictionary<int, PdfObject> Created { get; }
            internal ArtifactMarkDetail Applied { get; }
            internal ArtifactMarkDetail Discarded { get; }
            internal string MarkInfoWarning { get; }
        }

        // ================================================================
        //  Write accounting
        // ================================================================

        /// <summary>
        /// Hands the edit to <see cref="DocumentRevisionWriter"/> and reports whether the
        /// appended revision <em>actually reached</em> <paramref name="output"/>.
        ///
        /// <para>
        /// <b>Why this check exists.</b> The writer is documented never to throw: it catches every
        /// failure — an <see cref="IOException"/> from the caller's stream, an object it had to
        /// skip, a trailer it could not rebuild — records it on
        /// <see cref="AccessibilityDiagnostics"/> and leaves the output holding just the original
        /// bytes. Returning the marking pass's own tallies unconditionally therefore reported
        /// <see cref="ArtifactMarkResult.ArtifactsMarked"/> and
        /// <see cref="ArtifactMarkDetail.DocumentChanged"/> about a file that is byte-for-byte the
        /// input. The same defect existed in <c>PdfAutoTagger</c>, <c>PdfStructureTreeEditor</c>,
        /// <c>PdfReadingOrderAnalyzer</c> and <c>PdfFontEmbeddingRemediator</c>; all of them now
        /// answer the question with the <em>same</em> code, so they cannot drift apart.
        /// </para>
        ///
        /// <para>
        /// <b>The mechanism itself is <see cref="VerifiedWrite"/>.</b> Counting the bytes
        /// <paramref name="output"/> actually accepted is exact where reading
        /// <see cref="AccessibilityDiagnostics.LastError"/> across the call would be a lossy proxy.
        /// What stays here is the <em>message</em>, which has to name what
        /// a caller of <b>this</b> operation is not getting.
        /// </para>
        /// </summary>
        /// <returns>
        /// <c>true</c> when the revision was appended (or when there was legitimately nothing to
        /// append and the original document was copied through); <c>false</c> when the output holds
        /// only — or less than — the original document despite an edit having been planned.
        /// </returns>
        private static bool WriteRevision(
            Stream output, byte[] bytes, PdfDocument doc, PdfObjectIndex index,
            Dictionary<int, PdfObject> modifiedObjects, Dictionary<int, PdfObject> newObjects,
            List<string> warnings)
        {
            if (VerifiedWrite.Write(
                    output, bytes, doc, index, modifiedObjects, newObjects, -1,
                    out bool truncated, out string cause))
            {
                return true;
            }

            string detail = "The incremental revision carrying the artifact marking never reached the "
                + "output stream (" + cause + "), so the output holds "
                + (truncated ? "only part of the original document" : "the original, unmarked document")
                + " and nothing was marked. The /Artifact marked-content sequences this call planned "
                + "are NOT present in the result, so it is reported as zero artifacts on zero changed "
                + "pages rather than as a marked document.";

            Warn(warnings, "PdfArtifactMarker.MarkHeadersAndFooters", detail);
            return false;
        }

        // ================================================================
        //  One page
        // ================================================================

        private enum PageOutcomeKind
        {
            /// <summary>Examined successfully; there was simply nothing in the header/footer bands.</summary>
            NothingToMark = 0,

            /// <summary>Content stream rewritten with one or more artifact sequences.</summary>
            Changed = 1,

            /// <summary>Left untouched because it could not be rewritten safely. A warning was recorded.</summary>
            Skipped = 2,
        }

        private readonly struct PageOutcome
        {
            internal PageOutcome(PageOutcomeKind kind, int artifactsMarked)
            {
                Kind = kind;
                ArtifactsMarked = artifactsMarked;
            }

            internal PageOutcomeKind Kind { get; }
            internal int ArtifactsMarked { get; }

            internal static PageOutcome Nothing => new PageOutcome(PageOutcomeKind.NothingToMark, 0);
            internal static PageOutcome Skip    => new PageOutcome(PageOutcomeKind.Skipped, 0);
            internal static PageOutcome Marked(int count) => new PageOutcome(PageOutcomeKind.Changed, count);
        }

        private static PageOutcome MarkPage(
            PdfDocument doc, PdfObjectIndex index, PdfPage page, int pageIndex,
            HashSet<int> visitedPages, float headerMarginPt, float footerMarginPt,
            IDictionary<int, PdfObject> modifiedObjects, IDictionary<int, PdfObject> newObjects,
            List<string> warnings)
        {
            string where = "page " + (pageIndex + 1).ToString(CultureInfo.InvariantCulture);

            PdfDictionary pageDict;
            try
            {
                pageDict = page.Dictionary;
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + " could not be read and was left unchanged", ex);
                return PageOutcome.Skip;
            }

            if (pageDict == null)
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + " has no dictionary and was left unchanged");
                return PageOutcome.Skip;
            }

            // A page tree may reference the same page object from two places. Rewriting it twice
            // would orphan the first replacement content stream and re-tag the original bytes, so
            // the second visit is a no-op.
            int pageObjectNumber = index.GetObjectNumber(pageDict);
            if (pageObjectNumber > 0 && !visitedPages.Add(pageObjectNumber))
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + " is the same page object as an earlier page (object " +
                    pageObjectNumber.ToString(CultureInfo.InvariantCulture) +
                    "); it was marked once and skipped here");
                return PageOutcome.Skip;
            }

            // No content stream means nothing is drawn — not a failure, just nothing to mark.
            PdfObject contents;
            try
            {
                contents = pageDict[PdfName.Contents];
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + ": /Contents could not be read; the page was left unchanged", ex);
                return PageOutcome.Skip;
            }
            if (contents == null) return PageOutcome.Nothing;

            // Rotation. Header/footer detection works in default user space, where Y grows upward.
            // At /Rotate 90 or 270 the visual top and bottom edges are the left/right edges of user
            // space, so a Y-band test would classify body text as a header — the one failure mode
            // that actively damages accessibility (real content becomes invisible to AT). Refuse.
            // At 180 the page is upside down: the bands are still correct, but the high-Y band is
            // visually the footer, so the subtypes swap.
            int rotation = NormalizeRotation(page, where, warnings);
            if (rotation < 0)
                return PageOutcome.Skip;   // unreadable or malformed /Rotate; already reported

            if (rotation != 0 && rotation != 180)
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + " is rotated " + rotation.ToString(CultureInfo.InvariantCulture) +
                    "°, so its visual top and bottom edges are not the user-space ones; " +
                    "header/footer detection would mis-classify body text, and the page was left unchanged");
                return PageOutcome.Skip;
            }
            bool upsideDown = rotation == 180;

            float top, bottom;
            if (!TryGetVerticalExtent(doc, page, where, out top, out bottom))
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + " has no usable /CropBox or /MediaBox, so the header and footer bands " +
                    "cannot be located; the page was left unchanged");
                return PageOutcome.Skip;
            }

            float header = SanitizeMargin(headerMarginPt);
            float footer = SanitizeMargin(footerMarginPt);
            float height = top - bottom;

            if (header + footer >= height)
            {
                // The bands meet or overlap: every line on the page would become an artifact and
                // the page would disappear from assistive technology entirely.
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + ": the header (" + header.ToString("F1", CultureInfo.InvariantCulture) +
                    " pt) and footer (" + footer.ToString("F1", CultureInfo.InvariantCulture) +
                    " pt) bands cover the whole " + height.ToString("F1", CultureInfo.InvariantCulture) +
                    " pt page, which would mark all of its content as an artifact; the page was left unchanged");
                return PageOutcome.Skip;
            }

            if (header <= 0f && footer <= 0f) return PageOutcome.Nothing;

            var classifier = new HeaderFooterClassifier(top, bottom, header, footer, upsideDown);

            var options = new PageTagOptions
            {
                PageHeight                   = top,
                // Both edges, so the options object describes the page this pass actually measured.
                // The injected HeaderFooterClassifier below closes over `bottom` itself and does not
                // read this, but leaving it at the implicit 0 would describe a page that does not
                // exist to anything that later inspects these options.
                PageBottom                   = bottom,
                HeaderMargin                 = header,
                FooterMargin                 = footer,
                MarkHeadersFootersAsArtifacts = true,
                // Artifacts consume no MCID, so the page needs no ParentTree key and no MCID
                // numbering survives this pass; FirstMcid is irrelevant but must be well-defined.
                FirstMcid                    = 0,
                Classifier                   = classifier.Classify,
            };

            PageRewriteResult rewrite = null;
            try
            {
                // structParentsKey: -1 — an artifact carries no MCID and is never referenced from
                // the structure tree, so the page needs no /StructParents entry for this edit.
                rewrite = PageContentRewriter.RewritePage(
                    doc, index, pageDict, options, -1, modifiedObjects, newObjects);
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + " could not be rewritten and was left unchanged", ex);
                return PageOutcome.Skip;
            }

            if (rewrite == null)
            {
                Warn(warnings, "PdfArtifactMarker.MarkPage",
                    where + ": the page rewriter returned no result; the page was left unchanged");
                return PageOutcome.Skip;
            }

            if (rewrite.Changed)
            {
                int marked = 0;
                var runs = rewrite.Runs;
                if (runs != null)
                {
                    for (int i = 0; i < runs.Count; i++)
                    {
                        var run = runs[i];
                        if (run != null && run.IsArtifact) marked++;
                    }
                }

                if (marked == 0)
                {
                    // The classifier only ever asks for artifacts, so a changed page with no
                    // artifact run means the rewriter and this class disagree about what was
                    // requested. Say so rather than reporting a silent zero.
                    Warn(warnings, "PdfArtifactMarker.MarkPage",
                        where + " was rewritten but reported no artifact runs; " +
                        "its content stream was still replaced");
                }

                return PageOutcome.Marked(marked);
            }

            // Unchanged. Distinguish "there was nothing in the bands" (normal) from "the page could
            // not be rewritten" (worth telling the caller about). The classifier is invoked once per
            // text line, so LinesSeen == 0 means the rewriter refused before classification ran.
            if (classifier.LinesSeen > 0 && classifier.Candidates == 0)
                return PageOutcome.Nothing;

            Warn(warnings, "PdfArtifactMarker.MarkPage",
                where + " was left unchanged: " + (rewrite.SkipReason ?? "no reason was reported."));
            return PageOutcome.Skip;
        }

        // ================================================================
        //  Classification
        // ================================================================

        /// <summary>
        /// Marks <b>only</b> header and footer lines, leaving every other line alone.
        ///
        /// <para>
        /// Body content is deliberately returned as <see cref="RunClassification.Unmarked"/>. This
        /// operation's contract is "mark the pagination artifacts"; tagging body text is
        /// <c>PdfAutoTagger</c>'s job, and doing it here would emit MCIDs that no structure element
        /// references — the exact defect this rewrite removes.
        /// </para>
        ///
        /// <para>
        /// The band geometry is captured per page rather than read from
        /// <see cref="PageTagOptions.PageHeight"/> so that a <c>/MediaBox</c> or <c>/CropBox</c>
        /// with a non-zero lower-left corner (e.g. <c>[0 396 612 792]</c>) is handled correctly:
        /// "distance from the bottom" is <c>y − boxBottom</c>, not <c>y</c>.
        /// </para>
        /// </summary>
        private sealed class HeaderFooterClassifier
        {
            private readonly float _top;
            private readonly float _bottom;
            private readonly float _headerMargin;
            private readonly float _footerMargin;
            private readonly bool  _upsideDown;

            internal HeaderFooterClassifier(
                float top, float bottom, float headerMargin, float footerMargin, bool upsideDown)
            {
                _top          = top;
                _bottom       = bottom;
                _headerMargin = headerMargin;
                _footerMargin = footerMargin;
                _upsideDown   = upsideDown;
            }

            /// <summary>Number of text lines the tagger handed to this classifier.</summary>
            internal int LinesSeen { get; private set; }

            /// <summary>Number of lines classified as a header or footer artifact.</summary>
            internal int Candidates { get; private set; }

            internal RunClassification Classify(RunCandidate candidate)
            {
                LinesSeen++;

                if (candidate == null) return RunClassification.Unmarked;

                float y = candidate.Y;
                if (float.IsNaN(y) || float.IsInfinity(y)) return RunClassification.Unmarked;

                // A line above the top edge or below the bottom edge is not visible at all, so it
                // belongs on the artifact side of the fence too — hence "<=" without a lower bound.
                if (_headerMargin > 0f && (_top - y) <= _headerMargin)
                {
                    Candidates++;
                    return RunClassification.Artifact("Pagination", _upsideDown ? "Footer" : "Header");
                }

                if (_footerMargin > 0f && (y - _bottom) <= _footerMargin)
                {
                    Candidates++;
                    return RunClassification.Artifact("Pagination", _upsideDown ? "Header" : "Footer");
                }

                return RunClassification.Unmarked;
            }
        }

        // ================================================================
        //  Catalog maintenance
        // ================================================================

        /// <summary>
        /// Whether the catalog names a structure tree that actually reaches page content — the
        /// precondition for a document being allowed to declare
        /// <c>/MarkInfo &lt;&lt;/Marked true&gt;&gt;</c>.
        ///
        /// <para>
        /// <b>Presence of <c>/StructTreeRoot</c> is the wrong question:</b> a hollow tree — a
        /// <c>/StructTreeRoot</c> over elements with no <c>/K</c>, no <c>/ParentTree</c> and no
        /// marked content — resolves to a dictionary and so would pass, and this pass would stamp the
        /// Tagged-PDF claim onto a document that is untagged in every sense assistive technology
        /// cares about. <see cref="PdfAccessibility"/> refuses the same write on the same input, and
        /// the two writers must not disagree about one flag, since the artifacts pass runs first.
        /// </para>
        ///
        /// <para>
        /// The answer is delegated to
        /// <see cref="PdfAccessibility.StructTreeReachesContent"/> rather than reimplemented, so
        /// the two writers cannot drift apart again.
        /// </para>
        /// </summary>
        private static bool StructTreeReachesContent(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog)
        {
            try
            {
                PdfObject root = catalog?[PdfName.StructTreeRoot];
                if (root == null) return false;

                var structTreeRoot = root as PdfDictionary ?? doc.ResolveObject(root) as PdfDictionary;
                return PdfAccessibility.StructTreeReachesContent(doc, structTreeRoot, index);
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfArtifactMarker.StructTreeReachesContent",
                    "the catalog's /StructTreeRoot could not be read; the document is treated as untagged " +
                    "and /MarkInfo /Marked is not asserted", ex);
                return false;
            }
        }

        /// <summary>
        /// Whether a possibly-indirect <c>/Marked</c> value is <c>true</c>. A resolve failure is a
        /// broken reference, which is not the same as "the flag is set" — so it answers false and
        /// the caller goes on to write the key.
        /// </summary>
        private static bool ResolveMarkedTrue(PdfDocument doc, PdfObject obj)
        {
            if (obj == null) return false;

            try
            {
                var boolean = obj as PdfBoolean ?? doc.ResolveObject(obj) as PdfBoolean;
                return boolean != null && boolean.Value;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfArtifactMarker.EnsureMarkInfo",
                    "an indirect /MarkInfo /Marked value could not be resolved; it is treated as unset", ex);
                return false;
            }
        }

        /// <summary>
        /// Ensures the catalog carries <c>/MarkInfo &lt;&lt;/Marked true&gt;&gt;</c>.
        ///
        /// <para>
        /// When <c>/MarkInfo</c> already exists, its other entries (<c>/UserProperties</c>,
        /// <c>/Suspects</c>) are preserved: the dictionary is cloned and only <c>/Marked</c> is set.
        /// An indirect <c>/MarkInfo</c> is revised in place so the catalog does not have to change
        /// at all.
        /// </para>
        /// </summary>
        private static void EnsureMarkInfo(
            PdfDocument doc, PdfObjectIndex index, PdfDictionary catalog,
            IDictionary<int, PdfObject> modified, List<string> warnings)
        {
            PdfObject markInfoObj;
            try
            {
                markInfoObj = catalog[PdfName.MarkInfo];
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.EnsureMarkInfo",
                    "the catalog's /MarkInfo entry could not be read; /Marked true was not written", ex);
                return;
            }

            PdfDictionary markInfo = null;
            if (markInfoObj != null)
            {
                try
                {
                    markInfo = markInfoObj as PdfDictionary ?? doc.ResolveObject(markInfoObj) as PdfDictionary;
                }
                catch (Exception ex)
                {
                    Warn(warnings, "PdfArtifactMarker.EnsureMarkInfo",
                        "the existing /MarkInfo could not be resolved; a fresh one was written in its place", ex);
                    markInfo = null;
                }
            }

            // Resolved, not a bare cast. An indirect /Marked read as absent would make this pass
            // rewrite /MarkInfo on a document that already says what it is about to say — a
            // revision that changes nothing, reported as a change. The same rule applies at
            // PdfAccessibility.IsMarkedTrue, and the two writers must not disagree about whether
            // the flag is already set.
            if (markInfo != null && ResolveMarkedTrue(doc, markInfo[PdfName.Marked]))
                return;   // already correct

            // Preferred route: revise the indirect /MarkInfo object, leaving the catalog untouched.
            if (markInfoObj is PdfReference && markInfo != null)
            {
                int markInfoNumber = index.GetObjectNumber(markInfo);
                if (markInfoNumber > 0)
                {
                    PdfDictionary revised = PdfStructureTreeEditor.CloneDict(markInfo);
                    revised[PdfName.Marked] = new PdfBoolean(true);
                    modified[markInfoNumber] = revised;
                    return;
                }
            }

            int catalogNumber = index.GetObjectNumber(catalog);
            if (catalogNumber <= 0)
            {
                Warn(warnings, "PdfArtifactMarker.EnsureMarkInfo",
                    "the catalog's object number is unknown, so /MarkInfo <</Marked true>> could not be " +
                    "written; the artifacts were still marked in the page content");
                return;
            }

            // Build on a catalog revision this operation may already have queued, so two edits to
            // the catalog cannot overwrite each other.
            PdfObject pending;
            PdfDictionary catalogClone = null;
            if (modified.TryGetValue(catalogNumber, out pending))
                catalogClone = pending as PdfDictionary;
            if (catalogClone == null)
                catalogClone = PdfStructureTreeEditor.CloneDict(catalog);

            PdfDictionary newMarkInfo = markInfo != null
                ? PdfStructureTreeEditor.CloneDict(markInfo)
                : new PdfDictionary();
            newMarkInfo[PdfName.Marked] = new PdfBoolean(true);

            catalogClone[PdfName.MarkInfo] = newMarkInfo;
            modified[catalogNumber] = catalogClone;
        }

        // ================================================================
        //  Geometry
        // ================================================================

        /// <summary>
        /// Vertical extent of the visible page in default user space.
        ///
        /// <para>
        /// <c>/CropBox</c> is preferred over <c>/MediaBox</c>: it is the region a reader actually
        /// sees, so it is the region a running header sits at the top of. <see cref="PdfPage"/>
        /// already resolves inheritance from the page tree and falls back to <c>/MediaBox</c>.
        /// </para>
        /// </summary>
        private static bool TryGetVerticalExtent(
            PdfDocument doc, PdfPage page, string where, out float top, out float bottom)
        {
            PdfArray crop = ReadBox(page, cropBox: true, where: where);
            if (TryReadVerticalExtent(doc, crop, where, "/CropBox", out top, out bottom))
                return true;

            PdfArray media = ReadBox(page, cropBox: false, where: where);
            if (!ReferenceEquals(media, crop) &&
                TryReadVerticalExtent(doc, media, where, "/MediaBox", out top, out bottom))
                return true;

            top = 0f;
            bottom = 0f;
            return false;
        }

        private static PdfArray ReadBox(PdfPage page, bool cropBox, string where)
        {
            try
            {
                return cropBox ? page.CropBox : page.MediaBox;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfArtifactMarker.ReadBox",
                    where + ": " + (cropBox ? "/CropBox" : "/MediaBox") +
                    " could not be read; the other page box is tried instead", ex);
                return null;
            }
        }

        private static bool TryReadVerticalExtent(
            PdfDocument doc, PdfArray box, string where, string boxName, out float top, out float bottom)
        {
            top = 0f;
            bottom = 0f;
            if (box == null) return false;

            if (box.Count < 4)
            {
                AccessibilityDiagnostics.Report("PdfArtifactMarker.TryReadVerticalExtent",
                    where + ": " + boxName + " has " + box.Count.ToString(CultureInfo.InvariantCulture) +
                    " elements instead of 4; it cannot describe a rectangle");
                return false;
            }

            float y0, y1;
            if (!TryGetNumber(doc, box[1], where, boxName, out y0)) return false;
            if (!TryGetNumber(doc, box[3], where, boxName, out y1)) return false;

            // A rectangle may be written with either corner first (ISO 32000-1 §7.9.5).
            bottom = Math.Min(y0, y1);
            top    = Math.Max(y0, y1);

            if (top - bottom > 0f) return true;

            AccessibilityDiagnostics.Report("PdfArtifactMarker.TryReadVerticalExtent",
                where + ": " + boxName + " has zero height, so no header or footer band can be located");
            return false;
        }

        /// <summary>
        /// One implementation, in <see cref="PageBoxNumber"/> — shared with
        /// <c>ContentStreamTagger</c>, which reads the same boxes for the same reason.
        /// </summary>
        /// <remarks>
        /// A copy that stopped at <c>double.IsInfinity</c> without re-testing after the cast would
        /// return <c>+Infinity</c>, with a <c>true</c> result, for a coordinate finite as a double but
        /// outside float range (<c>/MediaBox [0 0 612 1e40]</c>). The consequence would surface far
        /// from here: the header arm of the classifier evaluates <c>top - y</c>, which would be
        /// <c>+Infinity</c> for every line, so <b>no line would ever be classified as a header</b>
        /// while footers were still marked normally.
        /// </remarks>
        private static bool TryGetNumber(
            PdfDocument doc, PdfObject obj, string where, string boxName, out float value)
            => PageBoxNumber.TryResolveFinite(
                doc, obj, "PdfArtifactMarker.TryGetNumber", where + ": ", boxName, out value);

        /// <summary>
        /// Effective page rotation normalised to [0, 360), or <c>-1</c> when it cannot be trusted —
        /// unreadable, or not the multiple of 90 that ISO 32000-1 §7.7.3.3 requires. The caller
        /// refuses such a page rather than guessing which edge is the top. Every <c>-1</c> is
        /// reported and warned about here, so the caller does not warn a second time.
        /// </summary>
        private static int NormalizeRotation(PdfPage page, string where, List<string> warnings)
        {
            int rotation;
            try
            {
                rotation = page.Rotate ?? 0;
            }
            catch (Exception ex)
            {
                Warn(warnings, "PdfArtifactMarker.NormalizeRotation",
                    where + ": /Rotate could not be read, so the visual top of the page is unknown; " +
                    "the page was left unchanged", ex);
                return -1;
            }

            rotation = PdfPageRotation.Reduce(rotation);

            if (PdfPageRotation.IsMalformed(rotation))
            {
                Warn(warnings, "PdfArtifactMarker.NormalizeRotation",
                    where + ": /Rotate " + rotation.ToString(CultureInfo.InvariantCulture) +
                    " is not a multiple of 90, which is malformed; the page was left unchanged");
                return -1;
            }

            return rotation;
        }

        private static float SanitizeMargin(float margin)
        {
            if (float.IsNaN(margin) || margin <= 0f) return 0f;
            return margin;
        }

        // ================================================================
        //  Diagnostics
        // ================================================================

        /// <summary>
        /// Records a tolerated failure in both places it belongs: the assembly-wide diagnostics sink
        /// (for hosts subscribed to <see cref="AccessibilityDiagnostics.OnEvent"/>) and the result's
        /// warning list (for the immediate caller). No failure-tolerant path in this class returns
        /// without calling this first.
        /// </summary>
        private static void Warn(List<string> warnings, string operation, string detail, Exception ex = null)
        {
            AccessibilityDiagnostics.Report(operation, detail, ex);
            warnings.Add(ex == null
                ? detail
                : detail + " — " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
