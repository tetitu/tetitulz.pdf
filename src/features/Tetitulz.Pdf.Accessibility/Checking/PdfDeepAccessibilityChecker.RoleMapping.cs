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
        //  UA13 — Role mapping
        // ================================================================

        /// <summary>
        /// Validates /RoleMap. A custom type may map to another custom type, which is itself
        /// mapped (PDF 32000-1 §14.8.4) — so the chain is followed, with a cycle guard, until a
        /// standard type is reached. Remapping a <i>standard</i> type is separately reported:
        /// PDF/UA-1 requires standard types to keep their standard semantics.
        /// </summary>
        private static void CheckRoleMapping(CheckContext ctx)
        {
            if (ctx.StructTree == null) return;

            PdfDictionary rolemap = ResolveDictionary(ctx.Doc, ctx.StructTree[PdfName.RoleMap]);

            int rolemapObject = rolemap == null
                ? -1
                : GetObjectNumber(ctx, ctx.StructTree[PdfName.RoleMap], rolemap);
            int anchor = rolemapObject > 0 ? rolemapObject : ctx.StructTreeObjectNumber;

            // One reader, in Core.Document.PdfRoleMap — which records a non-name target as null
            // rather than dropping the entry, precisely so UA13 below can report "X → (not a name)".
            var direct = ctx.StructTree == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : PdfRoleMap.Build(ctx.StructTree, o => ResolveObject(ctx.Doc, o));

            // Runs even with no /RoleMap at all, so an element tagged /S /Artifact in a file with no
            // /RoleMap is reported: a role check that only looked at the role map would miss it.
            CheckElementRoles(ctx, direct);

            if (rolemap == null) return;   // No role map: nothing further to validate.

            var unresolved = new List<string>();
            var cyclic = new List<string>();
            var remappedStandard = new List<string>();
            var remappedLater = new List<string>();     // a PDF 2.0 type: not standard in PDF/UA-1's ISO 32000-1

            foreach (var kvp in direct)
            {
                // A standard type as a key is remapped whatever the value, itself included: /LI /LI is
                // a remapping (and a circular one — rule 7.1-6), not a no-op.
                if (IsStandardRole(kvp.Key) && kvp.Value != null)
                {
                    (Core.Constants.PdfStructureTypes.IsStandardInIso32000Part1(kvp.Key) ? remappedStandard : remappedLater)
                        .Add(kvp.Key + " → " + kvp.Value);
                }

                switch (ResolveRoleChain(direct, kvp.Key))
                {
                    case RoleChainResult.Standard: break;
                    case RoleChainResult.Cycle:    cyclic.Add(kvp.Key); break;
                    default:
                        unresolved.Add(kvp.Value == null ? kvp.Key + " → (not a name)" : kvp.Key + " → " + kvp.Value);
                        break;
                }
            }

            if (unresolved.Count > 0)
            {
                ctx.Issues.Add(Issue(IssueSeverity.Error, "UA13",
                    "Custom role(s) in /RoleMap do not resolve to a standard PDF structure type: " +
                    JoinCapped(unresolved) + ".",
                    "Ensure every /RoleMap chain terminates at a standard structure type from " +
                    "PDF 32000-1 Table 333 (P, H1, L, Table, Figure, …) or PDF 32000-2 Table 364. " +
                    "Note /Artifact is not a structure type — decorative content is marked as an " +
                    "artifact in the content stream, never as a structure element.",
                    IssueCategory.Structure, WcagLevel.A)
                    .AtObject(anchor)
                    .MappedTo(null, "1.3.1")
                    .Aggregating(unresolved.Count));
            }

            if (cyclic.Count > 0)
            {
                ctx.Issues.Add(Issue(IssueSeverity.Error, "UA13",
                    "Custom role(s) in /RoleMap form a mapping cycle and can never resolve to a " +
                    "standard structure type: " + JoinCapped(cyclic) + ".",
                    "Break the cycle so every chain terminates at a standard structure type.",
                    IssueCategory.Structure, WcagLevel.A)
                    .AtObject(anchor)
                    .MappedTo(null, "1.3.1")
                    .Aggregating(cyclic.Count));
            }

            // ISO 14289-1 7.1: standard tags (ISO 32000-1 14.8.4) shall not be remapped — an error
            // (rule 7.1-7). A type only ISO 32000-2 made standard (Title, Aside, …) is a custom
            // type in a PDF/UA-1 file, so remapping it is advice at most.
            if (remappedLater.Count > 0)
                ctx.Issues.Add(Issue(IssueSeverity.Warning, "UA13",
                    "Structure type(s) ISO 32000-2 made standard are remapped in /RoleMap: " + JoinCapped(remappedLater) +
                    ". PDF/UA-1 reads them as custom types, but a PDF 2.0 consumer will not.",
                    "Use a role name ISO 32000-2 does not define, or the standard type itself.",
                    IssueCategory.Structure, WcagLevel.A)
                    .AtObject(anchor)
                    .MappedTo(null, "1.3.1")
                    .Aggregating(remappedLater.Count));

            if (remappedStandard.Count > 0)
            {
                ctx.Issues.Add(Issue(IssueSeverity.Error, "UA13",
                    "Standard structure type(s) are remapped in /RoleMap: " + JoinCapped(remappedStandard) +
                    ". A standard type must keep its standard meaning (ISO 14289-1 7.1); remapping it changes how every " +
                    "consumer interprets the document.",
                    "Remove these /RoleMap entries. If the content really is something else, change the " +
                    "elements' /S instead of redefining a standard type.",
                    IssueCategory.Structure, WcagLevel.A)
                    .AtObject(anchor)
                    .MappedTo(null, "1.3.1")
                    .Aggregating(remappedStandard.Count));
            }
        }

        /// <summary>
        /// Reports structure elements whose <c>/S</c> is neither a standard structure type nor a
        /// key in <c>/RoleMap</c>. Such an element has no defined meaning to any consumer.
        ///
        /// <para>This is the check that catches <c>/S /Artifact</c>: "Artifact" is not a structure
        /// type, and a producer that emits it generally emits no /RoleMap either — so the role-map
        /// validation above never even ran. Roles that <i>are</i> role-map keys are deliberately
        /// skipped here, because the chain check already judges where they lead.</para>
        /// </summary>
        private static void CheckElementRoles(CheckContext ctx, Dictionary<string, string> rolemap)
        {
            var unknown = new List<Occurrence>();

            foreach (StructNode node in ctx.Elements)
            {
                if (node.Role == null) continue;              // the /StructTreeRoot
                if (IsStandardRole(node.Role)) continue;
                if (rolemap.ContainsKey(node.Role)) continue; // judged by ResolveRoleChain

                var occurrence = Occurrence.For(ctx, node);
                occurrence.Detail = node.Role;
                unknown.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA13", IssueCategory.Structure, WcagLevel.A,
                null, "1.3.1", unknown,
                o => "Structure element uses type /" + o.Detail + ", which is neither a standard structure " +
                     "type nor a key in /RoleMap, so no consumer can know what it means.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " structure element(s) use a type that is " +
                    "neither a standard structure type nor a key in /RoleMap (e.g. " + objects + ").",
                "Use a standard structure type, or add a /RoleMap entry mapping this type to one. Note " +
                "/Artifact is not a structure type: decorative content is marked as an artifact in the " +
                "content stream with /Artifact BMC … EMC, never as a structure element.");
        }

        /// <summary>
        /// The document's <c>/RoleMap</c>, through the one reader in
        /// <see cref="PdfRoleMap.Build"/>. It takes the <c>/StructTreeRoot</c> dictionary rather
        /// than the document because <c>PdfDocument.Load</c> leaves <c>Catalog</c> null for an
        /// encrypted file, and this checker audits those.
        /// </summary>
        private static Dictionary<string, string> ReadRoleMap(CheckContext ctx)
            => ctx.StructTree == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : PdfRoleMap.Build(ctx.StructTree, o => ResolveObject(ctx.Doc, o));

        /// <summary>
        /// Follows an element's role through <c>/RoleMap</c> to the standard type it means, or
        /// returns the original role when the chain does not terminate at a standard type.
        /// Cycle-guarded.
        /// </summary>
        /// <remarks>
        /// <para><b>This deliberately does not call <see cref="PdfRoleMap.Resolve"/>, which the
        /// other three readers share.</b> That one stops at the chain's terminus; this one stops
        /// at the first <i>standard</i> type, which is the stronger rule — it is what makes
        /// <c>/RoleMap &lt;&lt; /Custom /Figure /Figure /Div &gt;&gt;</c> resolve <c>/Custom</c> to
        /// <c>Figure</c> rather than to <c>Div</c>, so a file cannot hide a figure's missing alt
        /// text by remapping <c>/Figure</c>. The two coincide on any conforming file, because
        /// §14.7.4 forbids remapping a standard type and so a chain cannot pass through one.</para>
        ///
        /// <para>Returning the <b>original</b> role when no standard type is reached is part of the
        /// same rule: an unresolvable chain is reported by UA13, and canonicalising to whatever it
        /// happens to end at would let the file rename the element for every other check.</para>
        ///
        /// <para>Moving this rule into Core needs the standard-type vocabulary that
        /// <see cref="IsStandardRole"/> owns, which is a separate decision. Until then the stronger
        /// rule stays where the vocabulary is.</para>
        /// </remarks>
        private static string ResolveRole(CheckContext ctx, StructNode node)
        {
            string role = node?.Role;
            if (role == null || IsStandardRole(role)) return role;

            Dictionary<string, string> map = ctx.RoleMap;
            if (map.Count == 0) return role;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string current = role;

            while (seen.Add(current) && map.TryGetValue(current, out string next))
            {
                // A null target is an entry whose value was not a name — PdfRoleMap keeps it so
                // UA13 can report it. There is nothing to follow, and Dictionary.TryGetValue(null)
                // throws, so the chain ends here.
                if (next == null) break;
                if (IsStandardRole(next)) return next;
                current = next;
            }

            return role;
        }

        /// <summary>
        /// The structure type an element <b>means</b>: its <c>/S</c> resolved through
        /// <c>/RoleMap</c>, memoised per node.
        ///
        /// <para>
        /// <b>Every structural rule must test this, never <see cref="StructNode.Role"/>.</b>
        /// ISO 32000-1 §14.7.3 / §14.8.4 make <c>/RoleMap</c> the sanctioned mechanism for a
        /// producer to use its own structure types, and a conforming consumer must treat a
        /// <c>/Bullet</c> mapped to <c>/LI</c> exactly as an <c>/LI</c>. Comparing the raw name
        /// does not merely miss a defect — it <i>manufactures</i> one: UA11 reported every
        /// role-mapped list item as an illegal child of <c>&lt;L&gt;</c>, UA12 reported every
        /// role-mapped cell as an illegal child of <c>&lt;TR&gt;</c> plus a table with no headers,
        /// and the real substructure of those elements was then never validated at all. The
        /// sibling <c>PdfUaChecker.CanonicalRole</c> in the Conformance plugin has always resolved
        /// the role, so the two checkers in this repository disagreed about the same file.
        /// </para>
        ///
        /// <para>Standard roles — the overwhelming majority — return immediately without touching
        /// either dictionary, so the cache only ever holds the custom ones.</para>
        /// </summary>
        private static string RoleOf(CheckContext ctx, StructNode node)
        {
            string role = node?.Role;
            if (role == null || IsStandardRole(role)) return role;

            Dictionary<StructNode, string> cache = ctx.ResolvedRoles;
            if (cache.TryGetValue(node, out string resolved)) return resolved;

            resolved = ResolveRole(ctx, node);
            cache[node] = resolved;
            return resolved;
        }

        /// <summary>
        /// How a role is named in a finding: the type actually written in the file, plus what it
        /// resolves to when <c>/RoleMap</c> makes those different. The raw name is what the reader
        /// will search the file for, so it always leads.
        /// </summary>
        private static string DescribeRole(CheckContext ctx, StructNode node)
        {
            string role = node?.Role;
            if (role == null) return "(no /S)";

            string resolved = RoleOf(ctx, node);
            return string.Equals(resolved, role, StringComparison.Ordinal)
                ? role
                : role + " (role-mapped to " + resolved + ")";
        }

        private enum RoleChainResult { Standard, Unresolved, Cycle }

        private static RoleChainResult ResolveRoleChain(Dictionary<string, string> rolemap, string role)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string current = role;

            while (true)
            {
                if (current == null) return RoleChainResult.Unresolved;
                if (IsStandardRole(current)) return RoleChainResult.Standard;
                if (!seen.Add(current)) return RoleChainResult.Cycle;
                if (!rolemap.TryGetValue(current, out string next)) return RoleChainResult.Unresolved;
                current = next;
            }
        }

        /// <summary>
        /// Whether a structure type is standard.
        /// </summary>
        /// <remarks>
        /// <b>The set lives in <see cref="StructureTypes"/>, because the auto-tagger asks the same
        /// question for the opposite reason</b> — it must not adopt a producer's marked-content tag
        /// as an element's <c>/S</c> unless that tag is one this checker would accept. Two copies of
        /// the list would be two places for the answers to diverge, and the divergence would show up
        /// as a document this library tags and then reports as untaggable.
        /// </remarks>
        private static bool IsStandardRole(string role) => StructureTypes.IsStandard(role);
    }
}
