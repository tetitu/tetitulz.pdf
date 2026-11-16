// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// One node of a PDF logical structure tree, as produced by <see cref="StructTreeWalker"/>.
    /// </summary>
    /// <remarks>
    /// Public fields (not properties) are deliberate: these are short-lived value carriers
    /// produced in bulk by a tree walk, and every consumer in this assembly reads them
    /// directly in tight loops.
    /// </remarks>
    internal sealed class StructNode
    {
        /// <summary>
        /// The element's structure type from <c>/S</c> (e.g. <c>"P"</c>, <c>"H1"</c>,
        /// <c>"Figure"</c>), or <c>null</c> for a node that carries no <c>/S</c> — which in
        /// practice means the <c>/StructTreeRoot</c> the walk started from.
        /// </summary>
        /// <remarks>
        /// <b>Several checks rely on that "in practice", so treat null as load-bearing.</b>
        /// <c>RoleMapping</c>, <c>ParentTree</c> and <c>HollowElements</c> all skip a node whose
        /// Role is null, on the reading that it is the root. That is only sound because
        /// <see cref="StructTreeWalker.GetRole"/> resolves <c>/S</c>; otherwise an element with an
        /// indirect <c>/S</c> would arrive here with a null Role and be silently dropped from three
        /// checks.
        /// </remarks>
        public string Role;

        /// <summary>The element dictionary itself. Never null for a node handed to a visitor.</summary>
        public PdfDictionary Dict;

        /// <summary>The parent node, or <c>null</c> for the node the walk started at.</summary>
        public StructNode Parent;

        /// <summary>
        /// The object number this element was parsed from, or <c>-1</c> when the element is a
        /// direct (inline) dictionary with no object number of its own.
        /// </summary>
        public int ObjectNumber;

        /// <summary>
        /// Zero-based page index resolved from <c>/Pg</c>; inherited from
        /// <see cref="Parent"/> when the element has no <c>/Pg</c> of its own, and
        /// <c>-1</c> when neither is available.
        /// </summary>
        public int PageIndex;

        /// <summary>Depth below the node the walk started at (0 for that node).</summary>
        public int Depth;

        /// <summary>
        /// The element's <c>/K</c> children exactly as stored, <b>unresolved</b>: references
        /// stay <see cref="PdfReference"/>, marked-content identifiers stay
        /// <see cref="PdfNumber"/>, and <c>/MCR</c> / <c>/OBJR</c> dictionaries stay
        /// dictionaries. Callers that need MCIDs or object references read them from here.
        /// Never null — an element with no <c>/K</c> gets an empty list.
        /// </summary>
        public IReadOnlyList<PdfObject> RawKids;
    }

    /// <summary>
    /// This assembly's view of a PDF logical structure tree: <see cref="StructNode"/>s in document
    /// (pre-order) order, from the one traversal in <see cref="StructureTreeWalk"/>.
    ///
    /// <para>
    /// <b>Why this exists.</b> A walker that caps recursion <i>depth</i> but tracks no
    /// <i>visited</i> nodes costs 2^depth visits on a <c>/K</c> cycle with branching factor 2 — an
    /// effective hang on untrusted input, which a depth cap does nothing to prevent — and separate
    /// walks each miss a bound another carries. The traversal lives in Base, shared by this
    /// feature, the tagged-text extractor, <c>PdfStructureTreeReader</c> and the PDF/UA checker; what
    /// stays here is the mapping of its nodes to <see cref="StructNode"/> and of its events to
    /// <see cref="AccessibilityDiagnostics"/>.
    /// </para>
    ///
    /// <para>
    /// The visited set is global to the traversal, not per-path: a legitimately shared subtree (a
    /// DAG, which the PDF spec permits) is reported once, at its first reachable position. That is
    /// the deliberate trade — it is what makes the exponential blow-up impossible — and the first
    /// such skip is reported through <see cref="AccessibilityDiagnostics"/> so a caller can tell.
    /// <see cref="StructNode.RawKids"/> aliases the document's array rather than copying it, so the
    /// cost is O(min(edges, <see cref="DefaultMaxEdges"/>)).
    /// </para>
    /// </summary>
    internal static class StructTreeWalker
    {
        /// <summary>Default recursion cap, matching the walkers this type replaces.</summary>
        internal const int DefaultMaxDepth = 100;

        /// <summary>
        /// Default hard ceiling on visited nodes. Well past any real tagged document
        /// (a 10 000-page report lands in the low hundreds of thousands) and low enough
        /// that a hostile tree cannot exhaust memory.
        /// </summary>
        internal static int DefaultMaxNodes => Core.Constants.PdfLimits.MaxStructTreeNodes;

        /// <summary>
        /// Hard ceiling on <c>/K</c> child <em>slots</em> examined across the whole walk.
        ///
        /// <para>
        /// <b>Why a node cap is not enough.</b> The visited set bounds how many times a node is
        /// expanded; it does not bound the cost of expanding one. A document whose structure
        /// elements all point <c>/K</c> at a single shared array of N references costs N per node
        /// over N nodes — O(N²) — while the node counter reaches N exactly once. At N = 100 000
        /// that is 10^10 probes from a ~200 KB compressed file. A real tree has about one edge per
        /// node, so this ceiling is four times the node cap and cannot be reached legitimately.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <b>The number lives in <c>PdfLimits</c>, because there are two walks of this tree.</b>
        /// The extraction side reads the same structure from the same untrusted input, and the two
        /// must not disagree about what bounds it — which they did until comparing them showed the
        /// extractor had the depth cap and the visited set but no edge budget at all.
        /// </remarks>
        internal static int DefaultMaxEdges => Core.Constants.PdfLimits.MaxStructTreeEdges;

        // ------------------------------------------------------------------
        //  Traversal
        // ------------------------------------------------------------------

        /// <summary>
        /// Collects the whole structure tree below <paramref name="structTreeRoot"/> in
        /// document (pre-order) order: a node always precedes its descendants, and siblings
        /// keep their <c>/K</c> order.
        /// </summary>
        /// <param name="structTreeRoot">
        /// The <c>/StructTreeRoot</c> dictionary — or any structure element, to collect just
        /// that subtree. Returns an empty list when null.
        /// </param>
        /// <param name="doc">Document used to resolve indirect <c>/K</c> children.</param>
        /// <param name="index">
        /// Object index used to resolve object numbers and <c>/Pg</c> page indices. May be
        /// null, in which case <see cref="StructNode.ObjectNumber"/> falls back to the
        /// parser-stamped number and <see cref="StructNode.PageIndex"/> is always -1.
        /// </param>
        /// <param name="maxDepth">Recursion cap; nodes deeper than this are not visited.</param>
        /// <param name="maxNodes">Hard ceiling on the number of nodes visited.</param>
        internal static IReadOnlyList<StructNode> Collect(
            PdfDictionary structTreeRoot,
            PdfDocument doc,
            PdfObjectIndex index,
            int maxDepth = DefaultMaxDepth,
            int maxNodes = -1)
            => Collect(structTreeRoot, doc, index, out _, maxDepth, maxNodes);

        /// <summary>
        /// As <see cref="Collect(PdfDictionary,PdfDocument,PdfObjectIndex,int,int)"/>, additionally
        /// reporting whether the returned list is the <em>whole</em> tree.
        /// </summary>
        /// <param name="truncationReason">
        /// Null when every reachable element was visited; otherwise the ceiling that stopped the
        /// walk, phrased for a report. A caller that draws conclusions from the absence of
        /// something — "nothing under here reaches content", "no element claims this MCID" — must
        /// consult this, because on a truncated list that absence is an artefact of the ceiling and
        /// not a fact about the document.
        /// </param>
        /// <remarks>
        /// A <em>revisit</em> is deliberately not truncation. A shared subtree (a DAG, which the
        /// spec permits) is collected once at its first position and nothing is missing from the
        /// list, so reporting it as partial would fire on legal files — the failure mode a
        /// diagnostic channel does not survive.
        /// </remarks>
        /// <param name="structTreeRoot">The catalog's <c>/StructTreeRoot</c>.</param>
        /// <param name="doc">The document, for resolving indirect references.</param>
        /// <param name="index">
        /// Object index for <paramref name="doc"/>, used to identify a node by object number so a
        /// revisit can be recognised.
        /// </param>
        /// <param name="maxDepth">Recursion cap for one branch.</param>
        /// <param name="maxNodes">
        /// Cap on nodes collected in total — which bounds the number of <i>branches</i>, something
        /// a depth cap alone cannot do.
        /// </param>
        internal static IReadOnlyList<StructNode> Collect(
            PdfDictionary structTreeRoot,
            PdfDocument doc,
            PdfObjectIndex index,
            out string truncationReason,
            int maxDepth = DefaultMaxDepth,
            int maxNodes = -1)
        {
            var result = new List<StructNode>();

            if (structTreeRoot == null)
            {
                truncationReason = null;
                return result;
            }

            if (maxDepth < 0) maxDepth = 0;
            if (maxNodes < 0) maxNodes = DefaultMaxNodes;
            if (maxNodes < 1) maxNodes = 1;

            truncationReason = Run(structTreeRoot, doc, index, result.Add, maxDepth, maxNodes);
            return result;
        }

        /// <summary>
        /// Invokes <paramref name="visit"/> once per reachable structure element, in document
        /// (pre-order) order, without materialising the whole tree.
        /// </summary>
        /// <param name="node">
        /// The node to start at — <c>/StructTreeRoot</c> or any structure element. The start
        /// node is always visited, even if it carries no <c>/S</c>. No-ops when null.
        /// </param>
        /// <param name="doc">Document used to resolve indirect <c>/K</c> children.</param>
        /// <param name="index">Object index for object numbers and page indices; may be null.</param>
        /// <param name="visit">Called for each node. No-ops when null. Must not throw.</param>
        /// <param name="maxDepth">Recursion cap; nodes deeper than this are not visited.</param>
        /// <param name="maxNodes">Hard ceiling on the number of nodes visited.</param>
        internal static void Walk(
            PdfDictionary node,
            PdfDocument doc,
            PdfObjectIndex index,
            Action<StructNode> visit,
            int maxDepth = DefaultMaxDepth,
            int maxNodes = -1)
        {
            if (node == null || visit == null) return;
            if (maxDepth < 0) maxDepth = 0;
            if (maxNodes < 0) maxNodes = DefaultMaxNodes;
            if (maxNodes < 1) maxNodes = 1;

            Run(node, doc, index, visit, maxDepth, maxNodes);
        }

        // ------------------------------------------------------------------
        //  Shared helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the structure type from <c>/S</c>, or <c>null</c> when the dictionary is null or
        /// carries no <c>/S</c>.
        /// </summary>
        /// <remarks>
        /// <para><b><c>/S</c> is resolved, and the difference is not cosmetic.</b> §7.3.10 lets any
        /// value be written indirectly, and <c>(node?[PdfName.S] as PdfName)?.Value</c> would not
        /// follow a reference — the indexer does not — so <c>/S 9 0 R</c> would come back
        /// <c>null</c>.</para>
        ///
        /// <para><b>A null role is a sentinel, which is what made it expensive.</b> Three checks use
        /// it to mean "this is the <c>/StructTreeRoot</c>" and skip the node —
        /// <c>PdfDeepAccessibilityChecker.RoleMapping.cs:230</c> says so in a comment,
        /// <c>ParentTree.cs:564</c> and <c>HollowElements.cs:176/424</c> do the same. So a perfectly
        /// legal element was mistaken for the root and dropped from UA14's MCID analysis, from
        /// UA30's hollow-subtree walk and from the role-mapping check, on <i>valid</i> input.</para>
        /// </remarks>
        internal static string GetRole(PdfDictionary node, PdfDocument doc)
            => StructureTreeWalk.RoleOf(node, doc);

        /// <summary>
        /// Whether <paramref name="role"/> is a heading type — <c>"H"</c> or <c>"H&lt;n&gt;"</c>.
        /// </summary>
        internal static bool IsHeading(string role) => HeadingLevel(role) >= 0;

        /// <summary>
        /// Numeric heading level for a structure type:
        /// <list type="bullet">
        ///   <item><description><c>"H"</c> → <b>0</b>, meaning an <i>unnumbered</i> heading.</description></item>
        ///   <item><description><c>"H1"</c>…<c>"H6"</c> → 1…6.</description></item>
        ///   <item><description>anything else → <c>-1</c> (not a heading).</description></item>
        /// </list>
        ///
        /// <para>
        /// <b>Why <c>"H"</c> is 0 and not 1.</b> The walkers this type replaces mapped
        /// <c>"H"</c> to level 1, which mis-handles PDF/UA "strong structure": in a
        /// strongly-structured document <c>/H</c> is the generic heading of whatever section
        /// nesting level it sits at, so calling it an H1 produces phantom
        /// "heading level skipped" findings. Returning 0 marks it as
        /// <i>unnumbered / structure-derived</i> and lets each caller decide — treat it as
        /// the current nesting level, or ignore it in numbered-sequence checks.
        /// </para>
        ///
        /// <para>
        /// PDF/UA-1 only defines H1–H6; PDF 2.0 permits higher levels, and those are returned
        /// as parsed (<c>"H7"</c> → 7) rather than rejected, so a valid PDF 2.0 heading is
        /// never silently treated as a non-heading.
        /// </para>
        /// </summary>
        internal static int HeadingLevel(string role)
        {
            if (string.IsNullOrEmpty(role)) return -1;
            if (role.Length == 1) return role[0] == 'H' ? 0 : -1;
            if (role[0] != 'H') return -1;

            for (int i = 1; i < role.Length; i++)
                if (role[i] < '0' || role[i] > '9') return -1;

            if (!int.TryParse(role.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int level))
                return -1;

            return level >= 1 ? level : -1;
        }

        /// <summary>
        /// Returns the element's <c>/K</c> children as raw (unresolved) objects, in every shape
        /// <c>/K</c> may take — <see cref="StructureTreeWalk.RawKidsOf"/>, with an unresolvable
        /// indirect <c>/K</c> reported through <see cref="AccessibilityDiagnostics"/>. Never null.
        /// </summary>
        internal static IReadOnlyList<PdfObject> GetRawKids(PdfDictionary node, PdfDocument doc)
            => StructureTreeWalk.RawKidsOf(node, doc, ex => AccessibilityDiagnostics.Report(
                "StructTreeWalker.GetRawKids",
                "an indirect /K could not be resolved; the element is treated as having a single reference kid", ex));

        // ------------------------------------------------------------------
        //  The walk
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs the shared traversal from <paramref name="start"/>, handing each node entered to
        /// <paramref name="visit"/> as a <see cref="StructNode"/>, and returns the first ceiling that
        /// stopped part of it, phrased for a report — null for a whole tree.
        /// </summary>
        /// <remarks>
        /// Each ceiling, the first revisit and every unresolvable reference go to
        /// <see cref="AccessibilityDiagnostics"/> as they happen; the truncation reason is set beside
        /// the report so the two cannot disagree.
        /// </remarks>
        private static string Run(PdfDictionary start, PdfDocument doc, PdfObjectIndex index,
            Action<StructNode> visit, int maxDepth, int maxNodes)
        {
            string reason = null;
            int maxEdges = DefaultMaxEdges;

            var walk = new StructureTreeWalk(doc, index)
            {
                MaxDepth = maxDepth,
                MaxNodes = maxNodes,
                MaxEdges = maxEdges,
            };

            walk.Enter = node =>
            {
                var mapped = new StructNode
                {
                    Role = node.Role,
                    Dict = node.Dictionary,
                    Parent = node.Parent?.Tag as StructNode,
                    ObjectNumber = node.ObjectNumber,
                    PageIndex = node.PageIndex,
                    Depth = node.Depth,
                    RawKids = node.RawKids,
                };
                node.Tag = mapped;
                visit(mapped);
            };

            walk.Report = (walkEvent, at, objectNumber, ex) =>
            {
                switch (walkEvent)
                {
                    case StructureTreeWalkEvent.NodeCapReached:
                        reason ??= "the " + N(maxNodes) + "-node structure-tree ceiling";
                        AccessibilityDiagnostics.Report("StructTreeWalker.Walk",
                            "the structure tree exceeded the " + N(maxNodes) +
                            "-node ceiling; the remainder of the tree was not visited and results are partial");
                        break;

                    case StructureTreeWalkEvent.DepthCapReached:
                        reason ??= "the " + N(maxDepth) + "-level structure-tree depth cap";
                        AccessibilityDiagnostics.Report("StructTreeWalker.Walk",
                            "the structure tree is deeper than the " + N(maxDepth) +
                            "-level cap; deeper elements were not visited and results are partial");
                        break;

                    case StructureTreeWalkEvent.EdgeCapReached:
                        reason ??= "the " + N(maxEdges) + "-edge structure-tree ceiling";
                        AccessibilityDiagnostics.Report("StructTreeWalker.Walk",
                            "the structure tree exceeded the " + N(maxEdges) +
                            "-edge ceiling (its /K arrays are shared or enormous); the remainder was " +
                            "not visited and results are partial");
                        break;

                    case StructureTreeWalkEvent.Revisit:
                        // Not truncation: a shared subtree (a DAG, which the spec permits) is collected
                        // once at its first position and nothing is missing from the list.
                        AccessibilityDiagnostics.Report("StructTreeWalker.Walk",
                            "a structure element was reached more than once (object " + N(objectNumber) +
                            ") — a /K cycle or a shared subtree; it was reported at its first position only");
                        break;

                    case StructureTreeWalkEvent.UnresolvableKid:
                        AccessibilityDiagnostics.Report("StructTreeWalker.Walk",
                            "structure child " + N(objectNumber) +
                            " could not be resolved; that subtree was skipped", ex);
                        break;

                    case StructureTreeWalkEvent.UnresolvableKids:
                        AccessibilityDiagnostics.Report("StructTreeWalker.GetRawKids",
                            "an indirect /K could not be resolved; the element is treated as having a single reference kid", ex);
                        break;
                }
            };

            walk.Run(start);
            return reason;
        }

        private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
