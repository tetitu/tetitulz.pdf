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
