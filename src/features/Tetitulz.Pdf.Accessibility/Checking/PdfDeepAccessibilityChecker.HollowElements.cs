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
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA16 — Leaf structure elements that reference no content
        // ================================================================

        /// <summary>
        /// Roles for which an <b>empty leaf</b> element is legitimate and must not be reported by
        /// UA16.
        ///
        /// <para>
        /// Read against ISO 32000-1:2008. <c>/K</c> is Optional in
        /// Table 323, so no structure element is required to have children at all, and Table 333
        /// gives every role below a reading under which an empty instance is meaningful:
        /// </para>
        /// <list type="bullet">
        ///   <item><description><c>TD</c> / <c>TH</c> — an empty cell is the standard way to keep
        ///     a grid rectangular, and this checker's own UA12 regularity rule <i>requires</i> the
        ///     grid to be rectangular, so flagging empty cells would make the two checks
        ///     contradict each other.</description></item>
        ///   <item><description><c>Lbl</c> — Table 333 defines it as "a label … used to identify
        ///     the item"; a list numbered by its own body text legitimately carries an empty
        ///     one.</description></item>
        ///   <item><description><c>Private</c> — Table 333: "a grouping element containing private
        ///     content belonging to the producer". Its content is by definition not part of the
        ///     document's logical structure, so it need reference none.</description></item>
        ///   <item><description><c>Artifact</c> — not a PDF 1.7 structure type at all (see the
        ///     remarks on <see cref="StructureTypes.IsStandard"/>, where UA13 reports it) and a PDF 2.0 role
        ///     for content that is by definition never a structural content item. Reporting it
        ///     twice, once as a bad role and once as empty, helps nobody.</description></item>
        /// </list>
        /// <para>independent checkers fail none of these. The exemption is deliberately
        /// <b>leaf-only</b>: UA30 ignores it, so a subtree built entirely out of exempt roles is
        /// still reported when nothing in it reaches content.</para>
        /// </summary>
        private static readonly HashSet<string> RolesAllowedToBeEmpty =
            new HashSet<string>(StringComparer.Ordinal)
            {
                StructureTypes.TableDataCell,       // grid-filling cells
                StructureTypes.TableHeaderCell,
                StructureTypes.Label,               // an empty list label
                StructureTypes.Private,             // producer-defined, carries no content by definition
                StructureTypes.Artifact,            // PDF 2.0 standard role; artifacts are never content items
            };

        /// <summary>
        /// Reports structure elements that are <b>leaves</b> and reference nothing: no
        /// marked-content identifier, no /MCR, no /OBJR, and no child element. Such an element
        /// describes content that does not exist, which is precisely the shape a hollow
        /// auto-tagging pass produces — a handful of elements with empty /K.
        ///
        /// <para>
        /// <b>Scope, stated exactly.</b> This check is per-element and purely local: it says
        /// nothing about an element that <i>has</i> a child element, however hollow that child's
        /// own subtree turns out to be. The transitive question — "does anything under this
        /// element reach content?" — belongs to <see cref="CheckHollowSubtrees"/> (UA30), and the
        /// message below is worded so it cannot be read as making the broader claim.
        /// </para>
        /// </summary>
        private static void CheckEmptyStructureElements(CheckContext ctx)
        {
            var empties = new List<Occurrence>();

            foreach (StructNode node in ctx.Elements)
            {
                // The /StructTreeRoot carries no /S and is not a structure element.
                if (node.Role == null) continue;

                if (RolesAllowedToBeEmpty.Contains(RoleOf(ctx, node) ?? node.Role)) continue;

                // A node with collected children plainly has content beneath it.
                if (ctx.ChildrenOf(node).Count > 0) continue;

                IReadOnlyList<PdfObject> kids = node.RawKids;
                if (kids != null && kids.Count > 0)
                {
                    bool referencesSomething = false;
                    bool unresolvable = false;

                    for (int i = 0; i < kids.Count && !referencesSomething; i++)
                    {
                        PdfObject kid = kids[i];
                        if (kid == null) continue;

                        // A bare integer is an MCID: real content.
                        if (kid is PdfNumber) { referencesSomething = true; break; }

                        if (kid is PdfReference reference)
                        {
                            // StructTreeWalker's visited set is global, so a subtree shared by two
                            // parents produces a StructNode only under the first. The second parent
                            // would otherwise look like an empty leaf — checking the raw reference
                            // against the known element numbers keeps that false positive out.
                            if (ctx.StructElementObjectNumbers.Contains(reference.ReferenceObjectNumber))
                            {
                                referencesSomething = true;
                                break;
                            }
                        }

                        PdfDictionary dict = ResolveKidDictionary(ctx, kid);
                        if (dict == null)
                        {
                            if (kid is PdfReference) unresolvable = true;
                            continue;
                        }

                        // One ladder, Base.Operations.StructKidClassifier's — which resolves /Type,
                        // because an /MCR or /OBJR whose /Type is stored indirectly (§7.3.10) would
                        // otherwise go unrecognised and make this element look empty. Anything it
                        // can name is something this element references; only Unknown is nothing.
                        if (StructKidClassifier.Classify(dict, ctx.Doc) != StructKidKind.Unknown)
                            referencesSomething = true;
                    }

                    // Never accuse an element of being empty when we simply could not read it.
                    if (referencesSomething || unresolvable) continue;
                }

                empties.Add(Occurrence.For(ctx, node));
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA16", IssueCategory.Tagging, WcagLevel.A,
                null, "1.3.1", empties,
                o => "This structure element is a leaf that references no content: it has no child " +
                     "element, no MCID, no /MCR and no /OBJR, so nothing on any page belongs to it.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " structure element(s) are leaves that " +
                    "reference no content — no child element, no MCID, no /MCR and no /OBJR (e.g. " +
                    objects + "). Each describes content it is not connected to.",
                "Point each element's /K at the marked content it describes (an MCID, an /MCR, or an /OBJR " +
                "for an annotation), or delete the element. Re-running auto-tagging on the original, " +
                "untagged document is usually faster than repairing a hollow tree.");
        }

        // ================================================================
        //  UA30 — Structure subtrees that transitively reach no content
        // ================================================================

        /// <summary>
        /// Reports the <b>highest</b> structure element of every subtree that reaches no content
        /// item anywhere beneath it — no MCID, no <c>/MCR</c> and no <c>/OBJR</c>, at any depth.
        ///
        /// <para>
        /// <b>The hole this closes.</b> UA16 is per-leaf, so a tree in which every element has an
        /// element child is invisible to it however hollow the whole thing is: a
        /// <c>Document → Sect → Sect …</c> chain, or a pair of elements whose <c>/K</c> point at
        /// each other, satisfies "has a child element" at every node and reaches content at none.
        /// Such a tree looks fully populated in a structure viewer and describes nothing. UA17 notices
        /// only when a page happens to draw unmarked text — not when the page legitimately has no
        /// text, or when some other page's content is properly marked.
        /// </para>
        ///
        /// <para>
        /// <b>What is reported, and where.</b> Hollowness propagates strictly downward — an
        /// element reaches content exactly when it holds a content item itself or one of its
        /// children does — so the hollow elements of a document form whole subtrees. Reporting
        /// every one of them would emit an issue per element for a defect with a single cause, so
        /// only the topmost element of each hollow subtree is reported, carrying the number of
        /// structure elements it covers in <see cref="AccessibilityIssue.InstanceCount"/>. A
        /// hollow element that is a genuine leaf is left to UA16, which already reports it with
        /// the correct, narrower message; UA30 only speaks about elements that have at least one
        /// child element, which is exactly the case UA16 cannot see.
        /// </para>
        ///
        /// <para>
        /// <b>Exemptions do not apply here.</b> <see cref="RolesAllowedToBeEmpty"/> excuses an
        /// element that is <i>itself</i> empty — an empty <c>&lt;TD&gt;</c> keeps a grid
        /// rectangular. It does not excuse a branch of such elements that collectively reach
        /// nothing: a <c>&lt;TR&gt;</c> of nothing but empty cells is a row that describes no
        /// content, whatever the role of its children.
        /// </para>
        ///
        /// <para>
        /// <b>Cost and safety.</b> Reachability is computed once, in a single pass that builds a
        /// dependency edge per <c>/K</c> slot and then propagates upward with an explicit
        /// worklist — O(elements + edges), no recursion, and no re-walk per element. Cycles are
        /// harmless because a node is enqueued only on the transition to "reaches content", which
        /// happens at most once. <see cref="StructTreeWalker.Collect(PdfDictionary,PdfDocument,PdfObjectIndex,int,int)"/> has already made the
        /// traversal cycle-safe; the extra edge budget here guards the one case its node cap does
        /// not, a <c>/K</c> array shared by many elements, and when that budget is exhausted the
        /// check reports nothing rather than accusing a document it could not read.
        /// </para>
        /// </summary>
        private static void CheckHollowSubtrees(CheckContext ctx)
        {
            IReadOnlyList<StructNode> nodes = ctx.Elements;
            if (nodes == null || nodes.Count == 0) return;

            int count = nodes.Count;

            // Position of each node in the pre-order list. StructNode uses reference equality,
            // which is what is wanted: two structurally identical siblings are two nodes.
            var indexOf = new Dictionary<StructNode, int>(count);
            var indexByObjectNumber = new Dictionary<int, int>(count);
            var indexByDictionary = new Dictionary<PdfObject, int>(PdfObjectIndex.ReferenceIdentityComparer);

            for (int i = 0; i < count; i++)
            {
                StructNode node = nodes[i];
                indexOf[node] = i;

                // First position wins, matching StructTreeWalker: a shared subtree is collected
                // once, under the parent that reached it first.
                if (node.ObjectNumber > 0 && !indexByObjectNumber.ContainsKey(node.ObjectNumber))
                    indexByObjectNumber[node.ObjectNumber] = i;
                if (node.Dict != null && !indexByDictionary.ContainsKey(node.Dict))
                    indexByDictionary[node.Dict] = i;
            }

            // reaches[i] — node i's subtree reaches at least one content item, OR the check could
            // not read part of it (see ClassifyKid: an unreadable child is never held against the
            // document).
            var reaches = new bool[count];

            // dependents[j] — the nodes whose reachability includes node j's. Reversing the edge
            // direction is what turns "does anything under me reach content?" into one upward
            // sweep instead of a walk per element.
            var dependents = new List<int>[count];

            var worklist = new Queue<int>();

            int edgeBudget = StructTreeWalker.DefaultMaxEdges;

            for (int i = 0; i < count; i++)
            {
                StructNode node = nodes[i];

                // Spanning-tree edge: whatever a child reaches, its parent reaches.
                if (node.Parent != null && indexOf.TryGetValue(node.Parent, out int parentIndex))
                    AddDependent(dependents, i, parentIndex);

                IReadOnlyList<PdfObject> kids = node.RawKids;
                if (kids == null) continue;

                for (int k = 0; k < kids.Count; k++)
                {
                    // Every slot costs budget. StructTreeWalker.DefaultMaxEdges exists because a
                    // /K array aliased by many elements is quadratic in the number of slots while
                    // the node count stays linear; re-enumerating /K here without a budget would
                    // reintroduce exactly that.
                    if (edgeBudget <= 0)
                    {
                        AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.CheckHollowSubtrees",
                            "the structure tree exceeded the " +
                            StructTreeWalker.DefaultMaxEdges.ToString(CultureInfo.InvariantCulture) +
                            "-edge ceiling while computing content reachability; no hollow-subtree finding " +
                            "was emitted, because a partial reachability result would accuse elements whose " +
                            "content was simply not examined");

                        // The diagnostics event above is not part of the report. Without this the
                        // audit would return a UA30-free result for a document UA30 never examined.
                        ReportStructureWalkGap(ctx,
                            "the " + StructTreeWalker.DefaultMaxEdges.ToString(CultureInfo.InvariantCulture) +
                            "-edge structure-tree ceiling while computing which elements reach content",
                            "UA30 (structure subtrees that transitively reach no content) did not run.");
                        return;
                    }
                    edgeBudget--;

                    switch (ClassifyKid(ctx, kids[k], indexByObjectNumber, indexByDictionary, out int target))
                    {
                        case KidKind.Content:
                            // A content item, or a child this check could not read. Either way the
                            // element is not reported.
                            if (!reaches[i]) { reaches[i] = true; worklist.Enqueue(i); }
                            break;

                        case KidKind.Element:
                            AddDependent(dependents, target, i);
                            break;
                    }
                }
            }

            // Upward propagation. A node enters the queue only when its flag flips false → true,
            // so each node is dequeued at most once and a /K cycle simply terminates.
            while (worklist.Count > 0)
            {
                List<int> parents = dependents[worklist.Dequeue()];
                if (parents == null) continue;

                for (int i = 0; i < parents.Count; i++)
                {
                    int dependent = parents[i];
                    if (reaches[dependent]) continue;
                    reaches[dependent] = true;
                    worklist.Enqueue(dependent);
                }
            }

            // Subtree sizes over the spanning tree. Pre-order guarantees a parent's index is
            // lower than every descendant's, so one reverse sweep totals them.
            var subtreeSize = new int[count];
            for (int i = 0; i < count; i++) subtreeSize[i] = 1;
            for (int i = count - 1; i >= 1; i--)
            {
                StructNode parent = nodes[i].Parent;
                if (parent != null && indexOf.TryGetValue(parent, out int parentIndex) && parentIndex < i)
                    subtreeSize[parentIndex] += subtreeSize[i];
            }

            var roots = new List<Occurrence>();
            var rootSizes = new List<int>();

            for (int i = 0; i < count; i++)
            {
                StructNode node = nodes[i];

                // The /StructTreeRoot carries no /S; it is not a structure element and cannot be
                // the thing a remediator repairs.
                if (node.Role == null) continue;
                if (reaches[i]) continue;

                // A hollow leaf is UA16's finding, reported there with the narrower message.
                if (ctx.ChildrenOf(node).Count == 0) continue;

                // Only the top of each hollow subtree. A parent that also reaches nothing is the
                // higher — and more useful — place to report, so defer to it. A parent with no
                // /S is the structure tree root, which is not reportable, so a top-level element
                // under it is still the highest reportable element.
                StructNode parent = node.Parent;
                if (parent != null && parent.Role != null)
                {
                    if (!indexOf.TryGetValue(parent, out int parentIndex) || !reaches[parentIndex]) continue;
                }

                var occurrence = Occurrence.For(ctx, node);
                occurrence.Detail = node.Role;
                roots.Add(occurrence);
                rootSizes.Add(subtreeSize[i]);
            }

            if (roots.Count == 0) return;

            const string remediation =
                "Point the elements in this subtree at the marked content they describe — each /K at an " +
                "MCID, an /MCR, or an /OBJR for an annotation — or delete the subtree outright. A branch " +
                "that reaches no content is not repaired by adding more elements to it; re-running " +
                "auto-tagging on the original, untagged document is usually faster than repairing a " +
                "hollow tree by hand.";

            if (roots.Count <= IndividualReportLimit)
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    Occurrence occurrence = roots[i];
                    int size = rootSizes[i];

                    ctx.Issues.Add(new AccessibilityIssue(
                        IssueSeverity.Error, "UA30",
                        "The whole <" + occurrence.Detail + "> subtree rooted at this element reaches no " +
                        "content: none of the " + size.ToString(CultureInfo.InvariantCulture) +
                        " structure element(s) in it — at any depth — has an MCID, an /MCR or an /OBJR, so " +
                        "nothing on any page belongs to any of them. The elements below it are present but " +
                        "connect to nothing, which is why the per-element check (UA16) does not report them.",
                        remediation, IssueCategory.Tagging,
                        occurrence.PageNumber, WcagLevel.A, occurrence.ObjectNumber,
                        occurrence.StructPath, null, "1.3.1", null)
                        .Aggregating(size));
                }
                return;
            }

            int total = 0;
            for (int i = 0; i < rootSizes.Count; i++) total += rootSizes[i];

            ctx.Issues.Add(new AccessibilityIssue(
                IssueSeverity.Error, "UA30",
                roots.Count.ToString(CultureInfo.InvariantCulture) + " structure subtree(s) reach no content " +
                "at all (e.g. " + DescribeOccurrences(roots) + ") — " +
                total.ToString(CultureInfo.InvariantCulture) + " structure element(s) between them, none of " +
                "which has an MCID, an /MCR or an /OBJR at any depth. Those branches are present but connect " +
                "to nothing.",
                remediation, IssueCategory.Tagging,
                roots[0].PageNumber, WcagLevel.A, roots[0].ObjectNumber,
                roots[0].StructPath, null, "1.3.1", null)
                .Aggregating(total));
        }

        private static void AddDependent(List<int>[] dependents, int source, int dependent)
        {
            List<int> list = dependents[source];
            if (list == null) dependents[source] = list = new List<int>(2);
            list.Add(dependent);
        }

        /// <summary>How one raw <c>/K</c> entry bears on its element's content reachability.</summary>
        private enum KidKind
        {
            /// <summary>Nothing that can carry content — a name, a string, a null.</summary>
            Ignore,

            /// <summary>
            /// A content item (an MCID integer, an <c>/MCR</c> or an <c>/OBJR</c>) — <b>or</b> a
            /// child this check could not read, which is treated identically so an unreadable
            /// document is never accused of being hollow.
            /// </summary>
            Content,

            /// <summary>Another collected structure element, whose reachability this one inherits.</summary>
            Element,
        }

        /// <summary>
        /// Classifies one raw <c>/K</c> entry. The recognition rules deliberately mirror
        /// <see cref="CheckEmptyStructureElements"/> exactly — including treating an unresolvable
        /// reference and a structure element the walk did not collect as "cannot be held against
        /// the document" — so UA16 and UA30 can never disagree about what counts as content.
        /// </summary>
        /// <param name="target">
        /// Index of the referenced element when the result is <see cref="KidKind.Element"/>;
        /// otherwise -1.
        /// </param>
        /// <param name="ctx">Per-check state: the document, the options and the findings list.</param>
        /// <param name="kid">The <c>/K</c> entry to classify — an element, an MCID, an OBJR or a ref.</param>
        /// <param name="indexByObjectNumber">Maps an element's object number to its index in the collected list.</param>
        /// <param name="indexByDictionary">
        /// The same map for elements reached as direct dictionaries, which have no object number.
        /// </param>
        private static KidKind ClassifyKid(
            CheckContext ctx,
            PdfObject kid,
            Dictionary<int, int> indexByObjectNumber,
            Dictionary<PdfObject, int> indexByDictionary,
            out int target)
        {
            target = -1;
            if (kid == null) return KidKind.Ignore;

            // A bare integer is a marked-content identifier: real content on a real page.
            if (kid is PdfNumber) return KidKind.Content;

            if (kid is PdfReference reference)
            {
                // The common case, and the one that makes a shared subtree or a /K cycle work:
                // the reference names an element the walk already collected, so this element
                // inherits whatever that one reaches. UA16 stops at "it references something";
                // resolving it properly is the whole point of the transitive check.
                if (indexByObjectNumber.TryGetValue(reference.ReferenceObjectNumber, out int known))
                {
                    target = known;
                    return KidKind.Element;
                }
            }
            else if (kid is PdfDictionary directDict && indexByDictionary.TryGetValue(directDict, out int knownDirect))
            {
                target = knownDirect;
                return KidKind.Element;
            }

            PdfDictionary dict = kid as PdfDictionary ?? ResolveKidDictionary(ctx, kid);
            if (dict == null)
            {
                // A reference that would not resolve is evidence of nothing. Never accuse.
                return kid is PdfReference ? KidKind.Content : KidKind.Ignore;
            }

            // One ladder, Base.Operations.StructKidClassifier's — which resolves /Type, because an
            // /MCR or /OBJR whose /Type is stored indirectly (§7.3.10) would otherwise go
            // unrecognised.
            //
            // The FOLDING is this check's own and must not move into the shared classifier: an
            // Element here is one the walk did NOT collect (it hit the node, depth or edge cap, or
            // is unreachable for a reason this check cannot see), so it is unknown rather than
            // hollow and is treated as content-bearing rather than reported. UA16 and UA30 both
            // depend on that, and the shared classifier has no vocabulary for "could not read it".
            //
            // Testing /MCID before /S would be harmless only because both branches return
            // Content. Do not read "both branches agree" as "the order does not
            // matter": the shared ladder puts /S first, which is what makes an element carrying both
            // an element.
            return StructKidClassifier.Classify(dict, ctx.Doc) == StructKidKind.Unknown
                ? KidKind.Ignore
                : KidKind.Content;
        }
    }
}
