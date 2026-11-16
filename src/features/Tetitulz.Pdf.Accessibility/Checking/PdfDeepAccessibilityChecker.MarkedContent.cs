// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    /// <summary>
    /// Extended PDF/UA-1 accessibility checker that supplements the base <c>PdfUaChecker</c>
    /// (which owns UA00–UA09) with deeper structural, content, font and annotation checks.
    ///
    /// <list type="bullet">
    ///   <item>UA06 — The security handler of an <i>encrypted</i> document must permit content
    ///     extraction for accessibility (ISO 32000-1 Table 22, /P bit 10). Encryption itself is
    ///     not a PDF/UA-1 failure.</item>
    ///   <item>UA10 — Heading hierarchy, for both the numbered (H1…H6) and the "strong"
    ///     (&lt;H&gt; nested in &lt;Sect&gt;) PDF/UA models, including the mixed-model failure.</item>
    ///   <item>UA11 — List structure: L → LI → Lbl + LBody, in that order (PDF/UA-1 7.6).</item>
    ///   <item>UA12 — Table structure: Table &gt; TR &gt; TD/TH nesting, TH addressability
    ///     (/Scope or /Headers), and table <i>regularity</i> once /ColSpan and /RowSpan are
    ///     resolved (Matterhorn 15-003).</item>
    ///   <item>UA13 — Role mapping: every element's /S must be standard or resolve — through
    ///     role-map chains — to a standard type, and standard types must not be remapped.</item>
    ///   <item>UA14 — ParentTree coverage <i>and the mapping it encodes</i>: every page's
    ///     /StructParents and every annotation's /StructParent must be present in the ParentTree
    ///     and resolve to the right kind of object — and, beyond the shape of those pointers,
    ///     every MCID a structure element names must really occur on its page, the page's
    ///     /ParentTree array must name that element at that index, and every marked-content
    ///     sequence the page draws must be claimed by some element
    ///     (see <see cref="CheckMarkedContentMapping"/>).</item>
    ///   <item>UA15 — Figure alt-text quality: empty or placeholder /Alt.</item>
    ///   <item>UA16 — <i>Leaf</i> structure elements that reference no content at all.</item>
    ///   <item>UA17 — Pages that draw text belonging to no marked-content sequence.</item>
    ///   <item>UA18 — Pages that carry structural content but no /StructParents, in a document
    ///     that claims to be tagged.</item>
    ///   <item>UA19 — /MarkInfo /Suspects true.</item>
    ///   <item>UA20 — Text that can produce no Unicode (no /ToUnicode, no usable encoding).</item>
    ///   <item>UA21 — Fonts that are not embedded.</item>
    ///   <item>UA22 — Link annotations with no alternate description.</item>
    ///   <item>UA23 — Annotations that are not reachable from the structure tree.</item>
    ///   <item>UA24 — Image-only (likely scanned) pages.</item>
    ///   <item>UA30 — Structure <i>subtrees</i> whose every element, transitively, reaches no
    ///     content: the aggregate finding UA16 cannot make because UA16 is per-leaf.</item>
    ///   <item>UA99 — A check could not be completed; its findings are missing (Info).</item>
    /// </list>
    ///
    /// <para><b>Why UA16–UA19 exist.</b> Before they were added the audit could not tell a
    /// properly tagged document from one whose structure tree points at nothing: a file with a
    /// handful of empty structure elements, no MCIDs and no ParentTree produced a single
    /// Info-level remark and therefore looked very nearly conformant. Those four checks, plus
    /// raising the missing-/ParentTree finding from Info to Error, are what make hollow tagging
    /// fail the audit.</para>
    ///
    /// <para><b>Why UA30 exists on top of them.</b> UA16 asks a purely <i>local</i> question — is
    /// <i>this</i> element a leaf that points at nothing — so a tree in which every element has an
    /// element child slips past it even when the tree as a whole reaches zero content. That shape
    /// is the original hollow-tagging defect wearing a disguise, and only UA17 caught it, by
    /// accident, and only when a page happened to draw unmarked text. UA30 asks the
    /// <i>transitive</i> question instead and reports the highest element of each hollow subtree,
    /// so a remediator gets one actionable entry per broken branch rather than one per element.</para>
    ///
    /// <para><b>Reporting policy.</b> Up to <see cref="IndividualReportLimit"/> occurrences of a
    /// finding are reported one issue each, fully located (page, object number, structure path).
    /// Beyond that they collapse into a single issue carrying
    /// <see cref="AccessibilityIssue.InstanceCount"/> and the first few object numbers in its
    /// message, so a pathological document cannot flood the report while
    /// <see cref="AccessibilityAuditReport.TotalInstanceCount"/> still reflects reality.</para>
    ///
    /// <para><b>Robustness.</b> Every individual check runs inside
    /// <see cref="RunCheck"/>: a failure in one can never take down the audit. It is surfaced as
    /// a UA99 Info issue and through <see cref="AccessibilityDiagnostics"/> — never swallowed.</para>
    ///
    /// <para><b>Encrypted documents are audited, not refused.</b> PDF/UA-1 permits encryption as
    /// long as the security handler grants accessibility extraction, and dictionary structure —
    /// element roles, /K linkage, the /ParentTree, /StructParents, table geometry — is never
    /// ciphertext, so it remains fully checkable. One UA06 Error and a stop would fail a conformant
    /// file, give a non-conformant one half an audit, and contradict the sibling
    /// <c>PdfUaChecker</c>, which reads /P and keeps auditing, in one combined report. The two
    /// halves apply the same rule (see <see cref="CheckEncryption"/>) and the audit continues either way.
    /// What genuinely cannot be read — text and content streams — is named once, exactly, by
    /// <see cref="ReportEncryptionCoverage"/>; no check is silently omitted.</para>
    ///
    /// <para><b>Nor is a page whose content is merely broken.</b> The same rule applies with no
    /// encryption in sight: a stray delimiter, a PostScript calculator brace or a corrupt Flate
    /// payload leaves a page unscannable, and the four checks that need decoded content
    /// (UA14's marked-content mapping, UA17, UA18, UA24) must then skip it. Each such skip is
    /// recorded and named per page by <see cref="ReportContentCoverage"/>, so a report can never
    /// again be byte-identical to one in which those checks ran and found nothing.</para>
    ///
    /// <para><b>Nor is a page that was only half-read.</b> A page whose own <c>/Contents</c> decodes
    /// perfectly can still invoke a Form XObject that will not resolve or decode, or name a property
    /// list that is not in <c>/Properties</c>. Its counts are then lower bounds while
    /// <c>ContentUsable</c> is still true, so <see cref="ReportContentCoverage"/> — which is gated on
    /// that flag — says nothing about it. <see cref="ReportPartialContentCoverage"/> covers exactly
    /// that gap, for the same reason and at the same severity.</para>
    /// </summary>
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA17 — Page content drawn without marked content
        // ================================================================

        /// <summary>
        /// Reports text that is inside no marked-content sequence at all — neither tagged nor
        /// marked as an artifact. ISO 14289-1 §7.1 requires <em>all</em> content to be one or the
        /// other, so the test is per text-showing operator, not per page.
        ///
        /// <para>
        /// <b>Why not "the page contains no BDC".</b> That weaker condition is satisfied by a
        /// single artifact sequence anywhere on the page, which then immunises a hundred untagged
        /// <c>Tj</c> runs beside it — the exact hollow-tagging case this check exists to catch.
        /// </para>
        ///
        /// <para>
        /// Checked only when the document claims to be tagged; on a plainly untagged document UA01
        /// already says so once and repeating it per page would drown the report. Text inside Form
        /// XObjects is included (see <see cref="SummariseOperators"/>).
        /// </para>
        /// </summary>
        private static void CheckUnmarkedPageContent(CheckContext ctx)
        {
            if (!ctx.ClaimsTagged) return;

            var unmarked = new List<Occurrence>();

            foreach (PageInfo page in ctx.Pages)
            {
                if (ContentUnreadable(ctx, page, CheckNameUa17)) continue;

                // The counts below are lower bounds when a Form XObject went unread: the operators
                // inside it were not seen at all, so "N of M" understates both numbers and a page
                // whose only untagged text lives in that form yields no finding here. The check
                // still reports what it did see — that text really is untagged — but the page has
                // to appear in the coverage notice, or its silence reads as a clean result.
                if (page.FormXObjectScanIncomplete)
                    NotePartialContentGap(ctx, page, CheckNameUa17, checkRan: true);

                if (page.UnmarkedTextShowingOperators == 0) continue;

                var occurrence = Occurrence.ForPage(page);
                occurrence.Detail = page.UnmarkedTextShowingOperators.ToString(CultureInfo.InvariantCulture)
                    + (page.Scan.HasMarkedContent
                        ? " of " + page.TextShowingOperators.ToString(CultureInfo.InvariantCulture) +
                          " text-showing operator(s) are outside every marked-content sequence"
                        : " text-showing operator(s), and the page contains no marked content at all");
                unmarked.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA17", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", unmarked,
                o => "Page draws text that belongs to no marked-content sequence (" + o.Detail +
                     "). That text is neither tagged nor marked as an artifact, so it is invisible to " +
                     "assistive technology whatever the structure tree claims.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " page(s) draw text that belongs to no " +
                    "marked-content sequence (e.g. " + objects + "). That text is neither tagged nor marked " +
                    "as an artifact, whatever the structure tree claims.",
                "Wrap each run of real content in /P <</MCID n>> BDC … EMC (or the appropriate tag) and " +
                "mark decorative content as /Artifact BMC … EMC, then point the structure tree at those MCIDs.");
        }

        // ================================================================
        //  UA18 — Pages with no /StructParents
        // ================================================================

        /// <summary>
        /// Reports a page that carries content items belonging in the structure tree but has no
        /// <c>/StructParents</c> key to look them up by.
        ///
        /// <para>
        /// ISO 32000-1 Table 30 makes the key <i>"required if the page contains structural content
        /// items"</i> — so a page whose content is entirely artifacts (a section divider, a blank
        /// page carrying only a running footer) correctly has none, and must not be reported. The
        /// test is therefore "does this page have non-artifact content", not "does this page have
        /// any content".
        /// </para>
        ///
        /// <para>
        /// <b>And a content <i>item</i> is a sequence that names an <c>/MCID</c></b> (§14.7.4.2):
        /// that number is the only handle the structure tree has on it, so a sequence without one
        /// cannot be looked up in the <c>/ParentTree</c> whatever key the page carries. Counting
        /// every non-artifact <c>BDC</c> instead — before the property list had been examined —
        /// reported an <c>/OC</c> wrapper round a watermark as untracked structural content. See
        /// <see cref="PageInfo.StructuralMarkedSequences"/>.
        /// </para>
        /// </summary>
        private static void CheckPageStructParents(CheckContext ctx)
        {
            if (!ctx.ClaimsTagged) return;

            var missing = new List<Occurrence>();

            foreach (PageInfo page in ctx.Pages)
            {
                if (page.Dict == null) continue;
                if (page.Dict[NameStructParents] != null) continue;

                // Only accuse a page when its content was actually readable: an undecodable or
                // unscannable stream is evidence of nothing — but the report has to say that the
                // page went unjudged, or "no UA18 finding" reads as "the key is correctly absent".
                if (ContentUnreadable(ctx, page, CheckNameUa18)) continue;

                // Both counts the test below rests on are lower bounds when part of the page went
                // unread — an unreadable Form XObject may hold the structural sequences, and an
                // unresolvable named property list hides the /MCID that makes a sequence structural
                // at all. Either way "no structural content" is not established, so the page is
                // named rather than quietly cleared.
                if (page.FormXObjectScanIncomplete || page.MarkedContentIdsIncomplete)
                    NotePartialContentGap(ctx, page, CheckNameUa18, checkRan: true);

                // No structural content items => the key is correctly absent. Untagged text
                // counts, because a tagged document's text is supposed to be structural (and
                // UA17 reports it separately).
                if (page.StructuralMarkedSequences == 0 && page.UnmarkedTextShowingOperators == 0) continue;

                var occurrence = Occurrence.ForPage(page);

                // Name the evidence. The rule fires on either condition, and the two are very
                // different repairs: the first page really does have marked content to key, the
                // second has text that is not marked at all and needs UA17 fixed first.
                occurrence.Detail = page.StructuralMarkedSequences > 0
                    ? "it opens " + page.StructuralMarkedSequences.ToString(CultureInfo.InvariantCulture) +
                      " non-artifact marked-content sequence(s) naming an /MCID"
                    : "it draws " + page.UnmarkedTextShowingOperators.ToString(CultureInfo.InvariantCulture) +
                      " text-showing operator(s) that belong in the structure tree but are not marked at all";
                missing.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA18", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", missing,
                o => "Page carries content that belongs in the structure tree — " + o.Detail +
                     " — but has no /StructParents entry, so none of it can be looked up in the /ParentTree. " +
                     "ISO 32000-1 Table 30 requires the key on any page that holds structural content items, " +
                     "and this document declares itself tagged.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " page(s) carry content that belongs in the " +
                    "structure tree but have no /StructParents entry (e.g. " + objects + "), so none of it can " +
                    "be looked up in the /ParentTree even though the document declares itself tagged.",
                "Give every page that holds structural content items a /StructParents key and add the matching " +
                "/Nums entry to the /ParentTree. A page whose content is entirely artifacts correctly has no " +
                "such key and is not reported here.");
        }

        // ================================================================
        //  UA19 — /MarkInfo /Suspects
        // ================================================================

        private static void CheckSuspectTagging(CheckContext ctx)
        {
            if (ctx.MarkInfo == null) return;

            // Resolved, not a bare cast: /Suspects may itself be an indirect reference — the false
            // UA01 an unresolved `/MarkInfo 9 0 R` would produce, one dictionary deeper, and
            // silent, hiding a document that has declared its
            // own tagging unreliable.
            if ((ResolveObject(ctx.Doc, ctx.MarkInfo[PdfName.Suspects]) as PdfBoolean)?.Value != true) return;

            int objectNumber = GetObjectNumber(ctx, ctx.Catalog[PdfName.MarkInfo], ctx.MarkInfo);

            ctx.Issues.Add(Issue(IssueSeverity.Error, "UA19",
                "/MarkInfo /Suspects is true: the document itself declares that its tagging may not " +
                "reflect the logical content. PDF/UA forbids /Suspects true — a conforming file must " +
                "stand behind its own tagging.",
                "Review and correct the tagging, then set /Suspects to false (or remove the key). Do not " +
                "simply clear the flag: it was set because a producer could not vouch for the structure.",
                IssueCategory.Tagging, WcagLevel.A)
                .AtObject(objectNumber)
                .MappedTo(null, "1.3.1"));
        }
    }
}
