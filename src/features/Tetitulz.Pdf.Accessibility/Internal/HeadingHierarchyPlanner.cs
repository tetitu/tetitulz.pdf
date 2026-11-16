// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// One heading whose <c>/S</c> the fixer would rewrite, and the level it would be rewritten to.
    /// </summary>
    /// <remarks>
    /// Public fields rather than properties, matching <see cref="StructNode"/>: this is a
    /// short-lived value carrier produced in bulk by a tree walk.
    /// </remarks>
    internal sealed class HeadingRenumbering
    {
        /// <summary>The heading element. Its <c>ObjectNumber</c> is always &gt; 0.</summary>
        public StructNode Node;

        /// <summary>The level encoded in the element's current tag (1–6, or higher on PDF 2.0).</summary>
        public int OriginalLevel;

        /// <summary>
        /// The level the element must carry so the sequence never skips. Always different from
        /// <see cref="OriginalLevel"/> — an element that already has the right level is not
        /// listed at all.
        /// </summary>
        public int NewLevel;
    }

    /// <summary>
    /// The outcome of replaying the heading renumbering over a structure tree: everything both
    /// the fixer and the pipeline's pre-flight inspection need, computed once.
    /// </summary>
    internal sealed class HeadingHierarchyPlan
    {
        /// <summary>Number of numbered (<c>H1</c>–<c>H6</c>, or higher) headings encountered.</summary>
        public int NumberedHeadingCount;

        /// <summary>
        /// Number of unnumbered <c>&lt;H&gt;</c> headings encountered. These are never renumbered
        /// (see <see cref="HeadingHierarchyPlanner"/>), but they are counted so a caller can say
        /// "there are headings, none of them this step's to renumber" rather than the false
        /// "there are no headings".
        /// </summary>
        public int StrongHeadingCount;

        /// <summary>
        /// Of <see cref="StrongHeadingCount"/>, the ones nested inside a <c>&lt;Sect&gt;</c> —
        /// i.e. the ones that really do take a level from section nesting. Reported as evidence
        /// that leaving <c>&lt;H&gt;</c> alone was the right call.
        /// </summary>
        public int StrongHeadingsInSectCount;

        /// <summary>
        /// The rewrites, in document order. Empty when the numbered sequence is already unbroken.
        /// </summary>
        public readonly List<HeadingRenumbering> Rewrites = new List<HeadingRenumbering>();

        /// <summary>
        /// Headings whose level is wrong but that cannot be revised, because they are direct
        /// (inline) structure elements with no object number for an incremental update to write
        /// against. In document order. They keep their original tag, and — critically — their
        /// <i>original</i> level is what the rest of the sequence is measured against.
        /// </summary>
        public readonly List<StructNode> Unrevisable = new List<StructNode>();

        /// <summary>Whether running the fixer would actually change a tag.</summary>
        public bool NeedsRenumbering => Rewrites.Count > 0;
    }

    /// <summary>
    /// The single implementation of the PDF/UA heading-renumbering rule for this assembly:
    /// walk the structure elements in document order and decide, for each numbered heading,
    /// what level it must carry so the sequence never skips (H1→H2→H3, never H1→H3).
    ///
    /// <para>
    /// <b>Why this is shared.</b> Two call sites need the answer and they must never disagree:
    /// <c>PdfStructureTreeEditor.FixHeadingHierarchy</c>, which <i>rewrites</i> the tags, and
    /// <c>PdfAccessibility</c>'s pre-flight inspection, which decides whether to run that step at
    /// all and what to tell the caller when it does not. Two copies of the traversal kept in step by
    /// hand would drift — one folding an unnumbered <c>&lt;H&gt;</c> in as level 1, the other
    /// skipping it — and the pipeline would report "already satisfied: the N heading(s) already
    /// form an unbroken level sequence" over documents the very step it describes would renumber,
    /// and in the mirror case append a revision that changes nothing. A fix and a report computed from one function
    /// cannot drift.
    /// </para>
    ///
    /// <para>
    /// <b>Only the weak (numbered) heading model is renumbered.</b> PDF/UA recognises two heading
    /// models: the <i>weak</i> one, where the level is encoded in the tag name
    /// (<c>H1</c>…<c>H6</c>), and the <i>strong</i> one, where a single generic <c>&lt;H&gt;</c>
    /// takes its level from the nesting depth of the enclosing <c>&lt;Sect&gt;</c> elements.
    /// Rewriting an <c>&lt;H&gt;</c> to <c>H1</c>/<c>H2</c>/… would destroy that structure and, in
    /// a document that nests correctly, would turn a conforming file into a non-conforming one.
    /// So <c>&lt;H&gt;</c> is counted (<see cref="HeadingHierarchyPlan.StrongHeadingCount"/>) and
    /// then ignored: it never enters the sequence and never advances the running level —
    /// <see cref="StructTreeWalker.HeadingLevel"/> returns 0 for <c>"H"</c> precisely so that this
    /// case is distinguishable from an H1. In a mixed document the numbered elements are
    /// renumbered among themselves.
    /// </para>
    ///
    /// <para>
    /// This type only decides; it neither writes nor reports. Diagnostics are the caller's, so the
    /// wording can name the operation the user actually invoked.
    /// </para>
    /// </summary>
    internal static class HeadingHierarchyPlanner
    {
        /// <summary>
        /// The heading level a node actually has, testing the <b>raw</b> role and the
        /// <c>/RoleMap</c>-resolved one, or <c>-1</c> when it is not a heading either way.
        /// </summary>
        /// <remarks>
        /// <para><b>This planner was the one fixer that never got converted.</b>
        /// <see cref="RoleMapResolver"/>'s own summary names <c>SetFigureAltText</c>,
        /// <c>FixHeadingHierarchy</c> and the remediation gate as the three sites that compared
        /// <c>node.Role</c> raw; two were converted and this was not. §14.7.3 makes <c>/RoleMap</c>
        /// the sanctioned way for a producer to use its own structure types, so under
        /// <c>/RoleMap &lt;&lt; /Chapter /H1 &gt;&gt;</c> the deep checker — which resolves —
        /// reported the heading problem while <c>FixHeadingHierarchy</c> saw
        /// <c>HeadingLevel("Chapter") == -1</c>, skipped every element, and reported that the
        /// document contains no headings. The audit and the fixer contradicted each other on the
        /// same file.</para>
        ///
        /// <para><b>Raw first, then mapped — never mapped alone.</b> Testing only the resolved role
        /// would let <c>/RoleMap &lt;&lt; /H1 /Div &gt;&gt;</c> turn the check off, which is the
        /// attack <see cref="RoleMapResolver.Means"/> exists to prevent. A real <c>H1</c> stays an
        /// <c>H1</c> however the file remaps it; a custom type additionally counts when the map
        /// says it is one.</para>
        /// </remarks>
        private static int EffectiveHeadingLevel(string role, Dictionary<string, string> roleMap)
        {
            int raw = StructTreeWalker.HeadingLevel(role);
            if (raw >= 0) return raw;

            if (roleMap == null || roleMap.Count == 0) return -1;
            return StructTreeWalker.HeadingLevel(RoleMapResolver.Resolve(role, roleMap));
        }

        /// <summary>
        /// Replays the renumbering over <paramref name="nodes"/>, which must be in document
        /// (pre-order) order — the order <see cref="StructTreeWalker.Collect(Core.Objects.PdfDictionary,Core.Document.PdfDocument,Base.Operations.PdfObjectIndex,int,int)"/> produces, and the
        /// order the never-skip rule is defined over. Never returns null; a null or heading-free
        /// sequence yields an empty plan.
        /// </summary>
        /// <param name="roleMap">
        /// The document's <c>/RoleMap</c> (<see cref="RoleMapResolver.Build"/>). May be null or
        /// empty, which is the same as having none.
        /// </param>
        /// <param name="nodes">
        /// The structure elements in document order — the order the never-skip rule is defined over.
        /// </param>
        internal static HeadingHierarchyPlan Plan(
            IEnumerable<StructNode> nodes, Dictionary<string, string> roleMap = null)
        {
            var plan = new HeadingHierarchyPlan();
            if (nodes == null) return plan;

            // The level actually present in the OUTPUT document so far — not the level the input
            // had. A heading that could not be rewritten must not shift the rest of the sequence.
            int lastActual = 0;

            foreach (StructNode node in nodes)
            {
                if (node == null) continue;

                int level = EffectiveHeadingLevel(node.Role, roleMap);
                if (level < 0) continue;

                if (level == 0)
                {
                    // "H" — unnumbered, its level comes from /Sect nesting. Not an H1, and
                    // deliberately does not touch lastActual.
                    plan.StrongHeadingCount++;
                    if (HasSectionAncestor(node)) plan.StrongHeadingsInSectCount++;
                    continue;
                }

                plan.NumberedHeadingCount++;

                int newLevel = lastActual == 0
                    ? 1                                  // the first numbered heading is always H1
                    : Math.Min(level, lastActual + 1);   // never skip more than one level

                if (newLevel == level)
                {
                    lastActual = level;
                    continue;
                }

                if (node.ObjectNumber <= 0)
                {
                    // A direct (inline) element: an incremental update has no object number to
                    // write against, so the output keeps the original tag and the original level
                    // is what follows on.
                    plan.Unrevisable.Add(node);
                    lastActual = level;
                    continue;
                }

                plan.Rewrites.Add(new HeadingRenumbering
                {
                    Node          = node,
                    OriginalLevel = level,
                    NewLevel      = newLevel,
                });
                lastActual = newLevel;
            }

            return plan;
        }

        /// <summary>
        /// Whether the element is nested inside a <c>&lt;Sect&gt;</c>, which is what makes an
        /// unnumbered <c>&lt;H&gt;</c> part of PDF/UA "strong" heading structure.
        /// </summary>
        private static bool HasSectionAncestor(StructNode node)
        {
            for (StructNode p = node?.Parent; p != null; p = p.Parent)
                if (p.Role == StructureTypes.Sect) return true;
            return false;
        }
    }
}
