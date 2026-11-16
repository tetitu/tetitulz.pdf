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
        //  UA11 — List structure (PDF/UA-1 7.6)
        // ================================================================

        private static void CheckListStructure(CheckContext ctx)
        {
            var badListChild   = new List<Occurrence>();
            var badLiChild     = new List<Occurrence>();
            var lblWithoutBody = new List<Occurrence>();
            var lblAfterBody   = new List<Occurrence>();
            var emptyLi        = new List<Occurrence>();

            // Roles go through /RoleMap (RoleOf) before they are judged: a <Bullet> mapped to /LI
            // *is* an LI, and comparing the raw /S turned a conformant role-mapped list into a
            // pair of Errors while leaving the real Lbl/LBody ordering unvalidated.
            foreach (StructNode list in ctx.Elements)
            {
                if (RoleOf(ctx, list) != StructureTypes.List) continue;

                foreach (StructNode child in ctx.ChildrenOf(list))
                {
                    string childRole = RoleOf(ctx, child);
                    if (childRole == StructureTypes.ListItem || childRole == StructureTypes.Caption) continue;
                    var occurrence = Occurrence.For(ctx, child);
                    occurrence.Detail = DescribeRole(ctx, child);
                    badListChild.Add(occurrence);
                }

                foreach (StructNode li in ctx.ChildrenOf(list))
                {
                    if (RoleOf(ctx, li) != StructureTypes.ListItem) continue;

                    int firstLbl = -1;
                    int firstBody = -1;
                    IReadOnlyList<StructNode> liKids = ctx.ChildrenOf(li);

                    for (int i = 0; i < liKids.Count; i++)
                    {
                        string role = RoleOf(ctx, liKids[i]);
                        if (role == StructureTypes.Label) { if (firstLbl < 0) firstLbl = i; }
                        else if (role == StructureTypes.ListBody) { if (firstBody < 0) firstBody = i; }
                        else
                        {
                            var occurrence = Occurrence.For(ctx, liKids[i]);
                            occurrence.Detail = DescribeRole(ctx, liKids[i]);
                            badLiChild.Add(occurrence);
                        }
                    }

                    if (firstLbl < 0 && firstBody < 0) { emptyLi.Add(Occurrence.For(ctx, li)); continue; }

                    // A label with nothing to label is meaningless to a screen reader.
                    if (firstLbl >= 0 && firstBody < 0) lblWithoutBody.Add(Occurrence.For(ctx, li));

                    // Reading order inside an LI is Lbl then LBody; reversing them makes the
                    // bullet or number read after the item text.
                    if (firstLbl >= 0 && firstBody >= 0 && firstLbl > firstBody)
                        lblAfterBody.Add(Occurrence.For(ctx, li));
                }
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA11", IssueCategory.Lists, WcagLevel.A,
                null, "1.3.1", badListChild,
                o => "List (L) element contains a child of type " + o.Detail +
                     "; PDF/UA-1 allows only LI (and an optional Caption) as children of L.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " child element(s) of List (L) elements are " +
                    "neither LI nor Caption (e.g. " + objects + "). PDF/UA-1 allows only LI and Caption under L.",
                "Fix the structure tree so each L only contains LI children (plus at most one Caption).");

            EmitOccurrences(ctx, IssueSeverity.Error, "UA11", IssueCategory.Lists, WcagLevel.A,
                null, "1.3.1", badLiChild,
                o => "List item (LI) contains a child of type " + o.Detail +
                     "; PDF/UA-1 allows only Lbl and LBody as children of LI.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " child element(s) of LI elements are neither " +
                    "Lbl nor LBody (e.g. " + objects + "). PDF/UA-1 allows only Lbl and LBody under LI.",
                "Move the content into an LBody; nested lists belong inside the LBody, not directly under LI.");

            // Advice, not a conformance error: ISO 32000-1 Table 336 lets an LI hold labels, list
            // bodies or both, in no stated order (rule 7.2-20 regex reads it so, and its
            // 7.2-t17-pass-c is an LI with a Lbl alone). The two readings below stay as warnings.
            EmitOccurrences(ctx, IssueSeverity.Warning, "UA11", IssueCategory.Lists, WcagLevel.A,
                null, "1.3.1", lblWithoutBody,
                o => "List item (LI) has a Lbl (label) but no LBody; the label has no content to introduce.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " LI element(s) have a Lbl but no LBody " +
                    "(e.g. " + objects + "); the label has no content to introduce.",
                "Add an LBody containing the item's content after each Lbl.");

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA11", IssueCategory.Lists, WcagLevel.A,
                null, "1.3.1", lblAfterBody,
                o => "List item (LI) places its LBody before its Lbl; the bullet or number would be " +
                     "announced after the item text.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " LI element(s) place LBody before Lbl " +
                    "(e.g. " + objects + "); the bullet or number would be announced after the item text.",
                "Reorder each LI's /K so Lbl precedes LBody.");

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA11", IssueCategory.Lists, WcagLevel.A,
                null, "1.3.1", emptyLi,
                o => "List item (LI) has neither a Lbl (label) nor an LBody (content) child.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " LI element(s) are missing both Lbl and " +
                    "LBody children (e.g. " + objects + ").",
                "Add Lbl and LBody sub-elements to each LI.");
        }
    }
}
