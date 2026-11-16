// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA31 — Structure element nesting (ISO 14289-1 7.2)
        // ================================================================

        /// <summary>
        /// UA31 — the parent and child rules ISO 14289-1 7.2 takes from ISO 32000-1 Tables 337 to 340,
        /// as the published 7.2 rules read them, over role-map-resolved roles: a TR's parent is a Table,
        /// THead, TBody or TFoot; a THead, TBody or TFoot's is a Table, and its children are TRs; a TH
        /// or TD's parent is a TR; an LI's is an L, an LBody's an LI, a TOCI's a TOC; a Table holds only
        /// TR, THead, TBody, TFoot and Caption, at most one THead, one TFoot and one Caption, a TBody
        /// beside a THead or a TFoot, and its Caption first or last; a TOC holds only TOC, TOCI and
        /// Caption, a TOC's or an L's Caption first. Children are counted by their standard roles.
        /// </summary>
        /// <remarks>
        /// What UA11 and UA12 already judge stays theirs: a TR's cells (UA12), an L's and an LI's
        /// children (UA11) — so no element is reported twice for one fault.
        /// </remarks>
        private static void CheckStructureNesting(CheckContext ctx)
        {
            var findings = new List<Occurrence>();
            foreach (StructNode node in ctx.Elements)
            {
                string role = RoleOf(ctx, node);
                if (role == null) continue;
                string parent = node.Parent == null ? null : RoleOf(ctx, node.Parent);
                string problem = NestingParentProblem(role, parent) ?? NestingKidsProblem(role, StandardKidRoles(ctx, node));
                if (problem == null) continue;
                var occurrence = Occurrence.For(ctx, node);
                occurrence.Detail = DescribeRole(ctx, node) + " " + problem;
                findings.Add(occurrence);
            }

            EmitOccurrences(ctx, IssueSeverity.Error, "UA31", IssueCategory.Structure, WcagLevel.A,
                null, "1.3.1", findings,
                o => "Structure element " + o.Detail + " (ISO 14289-1 7.2; ISO 32000-1 Tables 337–340).",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " structure element(s) are nested where ISO 14289-1 7.2 " +
                    "does not allow them (e.g. " + objects + ").",
                "Restructure the tree so each element sits under the parent its type requires and holds the children it allows.");
        }

        /// <summary>The roles of an element's children whose (role-map-resolved) role is standard, in order.</summary>
        private static List<string> StandardKidRoles(CheckContext ctx, StructNode node)
        {
            var roles = new List<string>();
            foreach (StructNode kid in ctx.ChildrenOf(node))
            {
                string role = RoleOf(ctx, kid);
                if (role != null && StructureTypes.IsStandard(role)) roles.Add(role);
            }
            return roles;
        }

        /// <summary>What is wrong with an element's parent, or <see langword="null"/>.</summary>
        private static string NestingParentProblem(string role, string parent)
        {
            switch (role)
            {
                case StructureTypes.TableRow:
                    return parent == StructureTypes.Table || parent == StructureTypes.TableHead
                        || parent == StructureTypes.TableBody || parent == StructureTypes.TableFoot
                        ? null : "is not inside a Table, THead, TBody or TFoot" + Under(parent);
                case StructureTypes.TableHead:
                case StructureTypes.TableBody:
                case StructureTypes.TableFoot:
                    return parent == StructureTypes.Table ? null : "is not inside a Table" + Under(parent);
                case StructureTypes.TableHeaderCell:
                case StructureTypes.TableDataCell:
                    return parent == StructureTypes.TableRow ? null : "is not inside a TR" + Under(parent);
                case StructureTypes.ListItem:
                    return parent == StructureTypes.List ? null : "is not inside an L" + Under(parent);
                case StructureTypes.ListBody:
                    return parent == StructureTypes.ListItem ? null : "is not inside an LI" + Under(parent);
                case StructureTypes.Toci:
                    return parent == StructureTypes.Toc ? null : "is not inside a TOC" + Under(parent);
                default:
                    return null;
            }
        }

        private static string Under(string parent) => parent == null ? " (it is under the root)" : " (it is under " + parent + ")";

        /// <summary>What is wrong with an element's children, or <see langword="null"/>.</summary>
        private static string NestingKidsProblem(string role, List<string> kids)
        {
            switch (role)
            {
                case StructureTypes.Table:
                {
                    string other = kids.FirstOrDefault(k => k != StructureTypes.TableRow && k != StructureTypes.TableHead
                        && k != StructureTypes.TableBody && k != StructureTypes.TableFoot && k != StructureTypes.Caption);
                    if (other != null) return "holds a " + other + "; a Table holds only TR, THead, TBody, TFoot and Caption";
                    int heads = kids.Count(k => k == StructureTypes.TableHead);
                    int feet = kids.Count(k => k == StructureTypes.TableFoot);
                    bool body = kids.Contains(StructureTypes.TableBody);
                    if (heads > 1) return "holds " + heads.ToString(CultureInfo.InvariantCulture) + " THead elements; a Table holds at most one";
                    if (feet > 1) return "holds " + feet.ToString(CultureInfo.InvariantCulture) + " TFoot elements; a Table holds at most one";
                    if ((heads > 0 || feet > 0) && !body) return "holds a " + (heads > 0 ? "THead" : "TFoot") + " but no TBody";
                    int captions = kids.Count(k => k == StructureTypes.Caption);
                    if (captions > 1) return "holds " + captions.ToString(CultureInfo.InvariantCulture) + " Captions; a Table holds at most one";
                    int at = kids.IndexOf(StructureTypes.Caption);
                    if (at > 0 && at < kids.Count - 1) return "holds its Caption between other children; it comes first or last";
                    return null;
                }
                case StructureTypes.TableHead:
                case StructureTypes.TableBody:
                case StructureTypes.TableFoot:
                {
                    string other = kids.FirstOrDefault(k => k != StructureTypes.TableRow);
                    return other == null ? null : "holds a " + other + "; a " + role + " holds only TR";
                }
                case StructureTypes.Toc:
                {
                    string other = kids.FirstOrDefault(k => k != StructureTypes.Toc && k != StructureTypes.Toci && k != StructureTypes.Caption);
                    if (other != null) return "holds a " + other + "; a TOC holds only TOC, TOCI and Caption";
                    return kids.Skip(1).Contains(StructureTypes.Caption) ? "holds a Caption after its first child; it comes first" : null;
                }
                case StructureTypes.List:
                    return kids.Skip(1).Contains(StructureTypes.Caption) ? "holds a Caption after its first child; it comes first" : null;
                default:
                    return null;
            }
        }
    }
}
