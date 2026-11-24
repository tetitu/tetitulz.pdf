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
