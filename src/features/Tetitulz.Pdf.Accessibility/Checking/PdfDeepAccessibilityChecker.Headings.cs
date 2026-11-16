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
        //  UA10 — Heading hierarchy (numbered and strong models)
        // ================================================================

        /// <summary>
        /// Validates heading structure under <b>both</b> PDF/UA models.
        ///
        /// <para>The numbered ("weak") model uses H1…H6 and must not skip a level. The strong
        /// model uses a single generic &lt;H&gt; whose level is implied by its Sect/Part/Art
        /// nesting depth — which is why <see cref="StructTreeWalker.HeadingLevel"/> returns
        /// <c>0</c> for <c>"H"</c> rather than 1. Understanding only the numbered model would emit a
        /// spurious "no H1" warning on every correctly built strongly-structured document.</para>
        ///
        /// <para>PDF/UA-1 forbids using both models in one document, so a mixture is reported as
        /// its own finding rather than being resolved arbitrarily.</para>
        /// </summary>
        private static void CheckHeadingHierarchy(CheckContext ctx)
        {
            // Roles are resolved through /RoleMap first (see RoleOf): a producer's own heading
            // type mapped to /H2 is an H2, and treating it as an unknown role silently removed it
            // from the hierarchy this check validates.
            var headings = new List<StructNode>();
            for (int i = 0; i < ctx.Elements.Count; i++)
                if (StructTreeWalker.IsHeading(RoleOf(ctx, ctx.Elements[i])))
                    headings.Add(ctx.Elements[i]);

            if (headings.Count == 0) return;

            var strong = new List<StructNode>();
            var numbered = new List<StructNode>();
            foreach (StructNode h in headings)
            {
                if (StructTreeWalker.HeadingLevel(RoleOf(ctx, h)) == 0) strong.Add(h);
                else numbered.Add(h);
            }

            if (strong.Count > 0 && numbered.Count > 0)
            {
                // Report against the minority model — that is the one the producer most likely
                // introduced by accident.
                List<StructNode> minority = strong.Count <= numbered.Count ? strong : numbered;
                string minorityName = ReferenceEquals(minority, strong) ? "<H>" : "H1–H6";

                EmitOccurrences(ctx, IssueSeverity.Error, "UA10", IssueCategory.Navigation, WcagLevel.A,
                    null, "1.3.1",
                    minority.Select(n => Occurrence.For(ctx, n)).ToList(),
                    o => "Document mixes strong heading structure (<H> nested in <Sect>) with numbered " +
                         "headings (H1–H6); this " + minorityName + " element is part of the minority model. " +
                         "PDF/UA-1 requires one heading model throughout a document.",
                    (count, objects) =>
                        "Document mixes strong heading structure (<H> nested in <Sect>) with numbered headings " +
                        "(H1–H6). " + count.ToString(CultureInfo.InvariantCulture) + " element(s) belong to the " +
                        minorityName + " model (e.g. " + objects + "). PDF/UA-1 requires one heading model " +
                        "throughout a document.",
                    "Convert the whole document to one model: either every heading is <H> nested in <Sect>, " +
                    "or every heading is H1–H6 with no skipped levels.");
                return;
            }

            if (strong.Count > 0) CheckStrongHeadings(ctx, strong);
            else CheckNumberedHeadings(ctx, numbered);
        }

        /// <summary>
        /// Strong model: the effective level of an &lt;H&gt; is its Sect/Part/Art nesting depth,
        /// normalised so the shallowest heading in the document is level 1. Two things are wrong
        /// in that model — skipping a level, and putting more than one &lt;H&gt; directly inside
        /// the same sectioning container.
        /// </summary>
        private static void CheckStrongHeadings(CheckContext ctx, List<StructNode> headings)
        {
            var depths = new int[headings.Count];
            int minDepth = int.MaxValue;

            for (int i = 0; i < headings.Count; i++)
            {
                int depth = 0;
                for (StructNode a = headings[i].Parent; a != null; a = a.Parent)
                {
                    string ancestorRole = RoleOf(ctx, a);
                    if (ancestorRole != null && SectioningRoles.Contains(ancestorRole)) depth++;
                }

                depths[i] = depth;
                if (depth < minDepth) minDepth = depth;
            }

            var skips = new List<Occurrence>();
            int previous = 0;
            for (int i = 0; i < headings.Count; i++)
            {
                int level = depths[i] - minDepth + 1;
                if (previous > 0 && level > previous + 1)
                {
                    var occurrence = Occurrence.For(ctx, headings[i]);
                    occurrence.Detail = "effective level " + level.ToString(CultureInfo.InvariantCulture) +
                                        " follows level " + previous.ToString(CultureInfo.InvariantCulture);
                    skips.Add(occurrence);
                }
                previous = level;
            }

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA10", IssueCategory.Navigation, WcagLevel.A,
                null, "1.3.1", skips,
                o => "Strong heading structure skips a level: " + o.Detail +
                     ". The level of an <H> is its Sect/Part/Art nesting depth, so a section was nested " +
                     "more than one level deeper than its parent without an intervening heading.",
                (count, objects) =>
                    "Strong heading structure skips a level in " + count.ToString(CultureInfo.InvariantCulture) +
                    " place(s) (e.g. " + objects + "). The level of an <H> is its Sect/Part/Art nesting depth.",
                "Nest sections one level at a time, or introduce the missing intermediate <Sect>/<H> pair.");

            // PDF/UA strong structure: a section carries at most one heading.
            var multi = new List<Occurrence>();
            var counted = new Dictionary<StructNode, int>();
            var reported = new HashSet<StructNode>();

            foreach (StructNode h in headings)
            {
                StructNode container = h.Parent;
                if (container == null) continue;

                // ISO 14289-1 7.4.4: "each node in the structure tree shall contain at most one child
                // H tag" — every node, as rule 7.4.4-1 reads it, not only the sectioning ones (a <Document> or a <Div> holding two <H> is the
                // same fault: the second heading has no section of its own).

                counted.TryGetValue(container, out int n);
                counted[container] = n + 1;

                if (n + 1 == 2 && reported.Add(container))
                {
                    var occurrence = Occurrence.For(ctx, container);
                    occurrence.Detail = container.Role == null ? "container" : DescribeRole(ctx, container);
                    multi.Add(occurrence);
                }
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA10", IssueCategory.Navigation, WcagLevel.A,
                null, "1.3.1", multi,
                o => "This " + o.Detail + " directly contains more than one <H> (ISO 14289-1 7.4.4). In strong heading " +
                     "structure a section has exactly one heading; further headings need their own " +
                     "nested <Sect>.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " sectioning element(s) directly contain " +
                    "more than one <H> (e.g. " + objects + "). In strong heading structure a section has " +
                    "exactly one heading.",
                "Wrap each additional heading and its content in its own nested <Sect>.");
        }

        /// <summary>
        /// Numbered model: levels must not skip, and a document with headings should start at H1.
        /// </summary>
        private static void CheckNumberedHeadings(CheckContext ctx, List<StructNode> headings)
        {
            var skips = new List<Occurrence>();
            int previous = 0;

            foreach (StructNode h in headings)
            {
                int level = StructTreeWalker.HeadingLevel(RoleOf(ctx, h));
                if (level < 1) continue;

                if (previous > 0 && level > previous + 1)
                {
                    var occurrence = Occurrence.For(ctx, h);
                    occurrence.Detail = "H" + previous.ToString(CultureInfo.InvariantCulture) +
                                        " is followed directly by " + DescribeRole(ctx, h);
                    skips.Add(occurrence);
                }
                previous = level;
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA10", IssueCategory.Navigation, WcagLevel.A,
                null, "1.3.1", skips,
                o => "Heading hierarchy skips a level (ISO 14289-1 7.4.2): " + o.Detail +
                     ". Screen readers rely on sequential heading levels to build a document outline.",
                (count, objects) =>
                    "Heading hierarchy skips levels in " + count.ToString(CultureInfo.InvariantCulture) +
                    " place(s) (e.g. " + objects + "). Screen readers rely on sequential heading levels.",
                "Ensure heading levels increment by at most one (H1→H2→H3). " +
                "Use PdfAccessibility.FixHeadingHierarchy() to renumber levels automatically.");

            bool hasH1 = headings.Any(h => StructTreeWalker.HeadingLevel(RoleOf(ctx, h)) == 1);
            if (!hasH1)
            {
                StructNode first = headings[0];
                ctx.Issues.Add(Locate(ctx,
                    Issue(IssueSeverity.Error, "UA10",
                        "Document has headings but no H1 element; the shallowest heading is " +
                        (first.Role == null ? "unknown" : DescribeRole(ctx, first)) +
                        ". ISO 14289-1 7.4.2 starts numbered headings at H1.",
                        "Add an H1 structure element wrapping the main document title, or renumber the " +
                        "existing headings so the outline starts at H1.",
                        IssueCategory.Navigation, WcagLevel.A)
                    .MappedTo(null, "1.3.1"),
                    first));
            }
        }
    }
}
