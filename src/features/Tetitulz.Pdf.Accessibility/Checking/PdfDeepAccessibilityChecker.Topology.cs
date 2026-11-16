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
        //  Topology: parent → children, sibling ordinals, structure paths
        // ================================================================

        /// <summary>
        /// Derives the child lists and the human-readable structure paths from the pre-order node
        /// list produced by <see cref="StructTreeWalker.Collect(PdfDictionary,PdfDocument,PdfObjectIndex,int,int)"/>. Pre-order guarantees a parent
        /// is always seen before its children, so a single forward pass suffices.
        /// </summary>
        private static void BuildTopology(CheckContext ctx)
        {
            var nodes = ctx.Elements;
            var ordinals = new Dictionary<StructNode, int>();
            var roleCounts = new Dictionary<StructNode, Dictionary<string, int>>();

            for (int i = 0; i < nodes.Count; i++)
            {
                StructNode node = nodes[i];
                if (node.ObjectNumber > 0) ctx.StructElementObjectNumbers.Add(node.ObjectNumber);

                StructNode parent = node.Parent;
                if (parent == null) continue;

                if (!ctx.Children.TryGetValue(parent, out var list))
                {
                    list = new List<StructNode>();
                    ctx.Children[parent] = list;
                }
                list.Add(node);

                if (!roleCounts.TryGetValue(parent, out var counts))
                {
                    counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    roleCounts[parent] = counts;
                }
                string role = node.Role ?? "?";
                counts.TryGetValue(role, out int n);
                counts[role] = n + 1;
                ordinals[node] = n + 1;
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                StructNode node = nodes[i];
                StructNode parent = node.Parent;

                // The /StructTreeRoot itself carries no /S; it contributes no path segment.
                string segment = node.Role;
                if (parent == null)
                {
                    ctx.Paths[node] = segment ?? string.Empty;
                    continue;
                }

                string parentPath = ctx.Paths.TryGetValue(parent, out string pp) ? pp : string.Empty;
                string role = segment ?? "?";

                // ALWAYS index, 1-based among same-role siblings. See the remarks on
                // AccessibilityIssue.StructPath: emitting the index only when the role repeats
                // made the same element's path depend on how many siblings it happened to have,
                // so "TR[2]/TD" and "TR[1]/TD[1]" described the same shape and neither could be
                // used as a key or compared between two revisions of a document.
                int ordinal = ordinals.TryGetValue(node, out int o) ? o : 1;
                role = role + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";

                ctx.Paths[node] = parentPath.Length == 0 ? role : parentPath + "/" + role;
            }

            // Deep trees produce paths no human will read; keep the informative tail.
            var keys = new List<StructNode>(ctx.Paths.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                string p = ctx.Paths[keys[i]];
                if (p != null && p.Length > MaxStructPathLength)
                    ctx.Paths[keys[i]] = "…/" + p.Substring(p.Length - MaxStructPathLength);
            }
        }

        /// <summary>
        /// Collects every object number an /OBJR in the structure tree points at, with the element
        /// holding the /OBJR, so annotation reachability (UA23) and nesting (UA32) are a probe rather
        /// than a second tree walk per annotation.
        /// </summary>
        private static Dictionary<int, StructNode> CollectObjrOwners(CheckContext ctx)
        {
            var owners = new Dictionary<int, StructNode>();
            var nodes = ctx.Elements;
            if (nodes == null) return owners;

            for (int i = 0; i < nodes.Count; i++)
            {
                IReadOnlyList<PdfObject> kids = nodes[i].RawKids;
                if (kids == null) continue;

                for (int k = 0; k < kids.Count; k++)
                {
                    PdfDictionary dict = ResolveKidDictionary(ctx, kids[k]);
                    if (dict == null) continue;

                    // Resolved, not a bare cast: any dictionary value may be indirect (§7.3.10),
                    // and the typeless form is legal — see IsObjectReference.
                    string type = (ResolveObject(ctx.Doc, dict[PdfName.Type]) as PdfName)?.Value;
                    if (!IsObjectReference(ctx, dict)) continue;

                    PdfObject obj = dict[PdfName.Obj];
                    int n = obj is PdfReference r ? r.ReferenceObjectNumber : GetObjectNumber(ctx, obj, obj as PdfDictionary);
                    if (n > 0 && !owners.ContainsKey(n)) owners[n] = nodes[i];
                }
            }

            return owners;
        }

        /// <summary>
        /// Whether a resolved <c>/K</c> child dictionary is an <b>object reference</b> (<c>/OBJR</c>).
        ///
        /// <para>
        /// <b><c>/Type</c> is Optional here.</b> ISO 32000-1 Table 325 makes <c>/Obj</c> the only
        /// Required entry of an object reference dictionary and says of <c>/Type</c> only "if
        /// present, shall be OBJR". Demanding it, a perfectly legal tagged file that omits it would
        /// be told twice over that it was broken: its <c>&lt;Link&gt;</c>/<c>&lt;Form&gt;</c>
        /// wrappers reported as leaves referencing no content (UA16/UA30) and the annotations they
        /// wrap as reachable from no <c>/OBJR</c> (UA23) — with remediation advice telling the author
        /// to add an <c>/OBJR</c> they already had. The <c>/MCR</c> side has the same fallback,
        /// through <c>dict[/MCID] != null</c>.
        /// </para>
        ///
        /// <para>
        /// The typeless form additionally requires <c>/S</c> to be <i>absent</i>: <c>/S</c> is
        /// Required on every structure element (Table 323), so a dictionary carrying it is an
        /// element and not an object reference, whatever else it holds.
        /// </para>
        ///
        /// <para><b>The ladder is <c>Base.Operations.StructKidClassifier</c>'s</b>, shared with the
        /// eight other classifications in this feature and with the Conformance plugin's. That
        /// changes one shape's verdict, deliberately: this copy asked "is the resolved <c>/Type</c>
        /// <c>OBJR</c>?" <b>first</b>, so <c>&lt;&lt; /Type /StructElem /Obj 12 0 R &gt;&gt;</c> was
        /// not an object reference here while the plugin's twin said it was — on the same file.
        /// Table 323 makes <c>/S</c> Required on an element, so what the dictionary <i>carries</i>
        /// outranks what it calls itself, and the widening can only remove false UA16/UA23/UA30
        /// findings.</para>
        /// </summary>
        /// <param name="ctx">Supplies the document, so an indirect <c>/Type</c> is followed.</param>
        /// <param name="dict">The resolved child dictionary.</param>
        private static bool IsObjectReference(CheckContext ctx, PdfDictionary dict)
            => StructKidClassifier.Classify(dict, ctx?.Doc) == StructKidKind.ObjectReference;

        /// <summary>
        /// Resolves a raw /K child to a dictionary, skipping references that are already known to
        /// be structure elements (the common case, and by far the most expensive to re-resolve).
        /// Returns null for MCID integers and anything that does not resolve.
        /// </summary>
        private static PdfDictionary ResolveKidDictionary(CheckContext ctx, PdfObject kid)
        {
            if (kid == null) return null;
            if (kid is PdfDictionary direct) return direct;
            if (kid is not PdfReference reference) return null;
            if (ctx.StructElementObjectNumbers.Contains(reference.ReferenceObjectNumber)) return null;

            try
            {
                return ctx.Doc.ResolveObject(reference) as PdfDictionary;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.ResolveKidDictionary",
                    "structure child " + reference.ReferenceObjectNumber.ToString(CultureInfo.InvariantCulture) +
                    " could not be resolved; it was treated as absent", ex);
                return null;
            }
        }
    }
}
