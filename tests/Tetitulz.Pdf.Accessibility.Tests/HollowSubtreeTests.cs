// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Locks shut the residual hole a final end-to-end gate found in the hollow-tagging checks:
/// <b>UA16 only ever looked at leaves</b>, so a structure tree in which every element has an
/// element child was invisible to it however hollow the tree as a whole was. A document could
/// therefore ship a fully populated-looking <c>Document → Sect → P → Span</c> tree that reached
/// zero content and be reported as fine. Only UA17 noticed, by accident, and only when a page
/// happened to draw unmarked text — it cannot notice when the page legitimately draws no text at
/// all, which is precisely the fixture below.
///
/// <para>
/// UA30 asks the transitive question ("does <i>anything</i> under this element reach content?")
/// and reports the highest element of each hollow subtree. Every assertion here is made in
/// <b>both</b> directions: a check that fires on a correctly tagged file is as damaging as one
/// that misses a broken one, so each broken fixture has a healthy twin that asserts silence.
/// </para>
///
/// <para>All fixtures are built in memory as <c>byte[]</c>; nothing is read from disk.</para>
/// </summary>
public class HollowSubtreeTests
{
    // =====================================================================
    //  PDF assembly helpers
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private const string CatalogTail =
        " /MarkInfo << /Marked true >> /Lang (en-US) "
        + "/ViewerPreferences << /DisplayDocTitle true >> >>";

    /// <summary>
    /// Serialises <paramref name="objectBodies"/> as objects 1..n with a classic cross-reference
    /// table. Everything written is ASCII, so <c>StringBuilder.Length</c> is a valid byte offset.
    /// </summary>
    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(objectBodies.Count);
        for (int i = 0; i < objectBodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(objectBodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objectBodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Page content that draws a decorative rectangle and <b>no text at all</b>, correctly marked
    /// as an artifact. This is the case UA17 cannot speak about: there is no text-showing
    /// operator to be outside a marked-content sequence, so a hollow tree above such a page is
    /// invisible to every content-level check.
    /// </summary>
    private static string ArtifactOnlyContent()
        => "/Artifact BMC\n0.9 0.9 0.9 rg 72 100 200 200 re f\nEMC\n";

    private static string TaggedContent(string text)
        => "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (" + text + ") Tj ET\nEMC\n";

    private static AccessibilityAuditReport Audit(byte[] pdf)
        => PdfAccessibility.Audit(new MemoryStream(pdf));

    private static IReadOnlyList<AccessibilityIssue> WithCode(AccessibilityAuditReport report, string code)
        => report.Issues.Where(i => string.Equals(i.Code, code, StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Runs <paramref name="operation"/> on a background thread and fails if it does not finish
    /// inside <paramref name="budgetMs"/>. An unguarded traversal of a cyclic /K graph does not
    /// return; a wall-clock assertion is the only way to say "must complete promptly" in a test.
    /// </summary>
    private static T RunWithinBudget<T>(Func<T> operation, int budgetMs = 20_000)
    {
        T result = default;
        Exception failure = null;

        var thread = new Thread(() =>
        {
            try { result = operation(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };

        thread.Start();
        Assert.True(thread.Join(budgetMs),
            "the audit did not finish within " + budgetMs.ToString(CultureInfo.InvariantCulture) +
            " ms, which is what an unguarded traversal of a cyclic structure tree looks like");

        if (failure != null) throw new Xunit.Sdk.XunitException("the audit threw: " + failure);
        return result;
    }

    // =====================================================================
    //  Fixture 1 — every element has a child, and the whole tree reaches nothing
    // =====================================================================

    /// <summary>
    /// The gate's demonstration, reproduced exactly: <c>Document → Sect → P → Span</c>, where the
    /// deepest element's <c>/K</c> points back up at the <c>&lt;P&gt;</c>, so <b>every</b> element
    /// has a child and not one of them holds an MCID, an <c>/MCR</c> or an <c>/OBJR</c>. The page
    /// draws a decorative rectangle and no text, so UA17 has nothing to say either.
    /// <para>Object numbers are fixed and asserted on: 7 is the Document, 8/9/10 the elements
    /// below it.</para>
    /// </summary>
    private static byte[] HollowNestedTreePdf() => BuildPdf(new[]
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page: no /StructParents, correctly, because it carries no structural content.
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Tabs /S >>",

        // 4 — content: an artifact rectangle and no text whatsoever.
        StreamObject(ArtifactOnlyContent()),

        // 5 — font
        HelveticaFont,

        // 6 — structure tree root
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 11 0 R /ParentTreeNextKey 0 >>",

        // 7..10 — every element has an element child; none references content.
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
        "<< /Type /StructElem /S /Sect /P 7 0 R /K [9 0 R] >>",
        "<< /Type /StructElem /S /P /P 8 0 R /K [10 0 R] >>",
        "<< /Type /StructElem /S /Span /P 9 0 R /K [9 0 R] >>",

        // 11 — an empty parent tree; there is nothing to look up.
        "<< /Nums [] >>",
    });

    [Fact]
    public void Audit_HollowSubtree_IsInvisibleToTheLeafOnlyAndContentLevelChecks()
    {
        AccessibilityAuditReport report = Audit(HollowNestedTreePdf());

        // This is the hole, asserted as a fact rather than described in a comment: every element
        // has a child, so the per-leaf check says nothing…
        Assert.Empty(WithCode(report, "UA16"));

        // …and the page draws no text, so the content-level checks say nothing either.
        Assert.Empty(WithCode(report, "UA17"));
        Assert.Empty(WithCode(report, "UA18"));
    }

    [Fact]
    public void Audit_HollowSubtree_IsReportedOnceAtTheTopOfTheSubtree()
    {
        AccessibilityAuditReport report = Audit(HollowNestedTreePdf());

        AccessibilityIssue issue = Assert.Single(WithCode(report, "UA30"));

        Assert.Equal(IssueSeverity.Error, issue.Severity);

        // The Document is the highest element of the hollow subtree; reporting the Sect, the P or
        // the Span instead would point a remediator at a symptom.
        Assert.Equal(7, issue.ObjectNumber);

        // Document + Sect + P + Span.
        Assert.Equal(4, issue.InstanceCount);

        Assert.Equal("Document[1]", issue.StructPath);
        Assert.Contains("subtree", issue.Message, StringComparison.Ordinal);
        Assert.Equal("1.3.1", issue.WcagCriterion);
    }

    [Fact]
    public void Audit_HollowSubtree_MakesTheDocumentNonConforming()
    {
        AccessibilityAuditReport report = Audit(HollowNestedTreePdf());

        Assert.False(report.IsConforming,
            "a structure tree that reaches no content at all must never be reported as conforming. " +
            "Issues: " + string.Join(" | ", report.Issues.Select(i => i.ToString())));
    }

    // =====================================================================
    //  Fixture 2 — a deep hollow branch beside a healthy one
    // =====================================================================

    /// <summary>
    /// A Document with two children: a paragraph that points at MCID 0, and a two-level Sect
    /// branch that bottoms out in an empty paragraph. Only the branch is broken, so a check that
    /// reports the Document (too high) or every element in the branch (too noisy) is caught.
    /// <para>Object numbers: 7 Document, 8 the healthy P, 9/10 the hollow Sects, 11 the empty
    /// leaf P.</para>
    /// </summary>
    /// <summary>
    /// The remediation pipeline's "already has a structure tree" skip must send the reader to a code
    /// the audit of the same bytes actually reports.
    ///
    /// <para>It used to name UA16/UA17/UA18 only, and <see cref="HollowNestedTreePdf"/> is exactly the
    /// tree where none of those three fire: every element has an element child, so UA16's per-leaf
    /// question is satisfied all the way down and only UA30's transitive one fails. A reader following
    /// that advice found none of the named codes in the report and would reasonably conclude the tree
    /// was fine — the precise misreading the skip message exists to prevent (plan 11 §5b).</para>
    ///
    /// <para>The assertion is deliberately tied to the <b>report</b> rather than to a literal string:
    /// it checks that the code the message advertises is one this input really produces, so it stays
    /// honest if the checker's numbering or the message's wording changes.</para>
    /// </summary>
    /// <summary>
    /// A tree the pipeline <b>keeps</b> whose only complaint is UA30 — the fixture the skip
    /// message's code list has to be honest about.
    /// </summary>
    /// <remarks>
    /// One healthy paragraph naming MCID 0, so <c>StructTreeNamesContent</c> is true and the
    /// pipeline declines to replace the tree; beside it a Sect branch that reaches nothing and
    /// bottoms out in a cycle rather than in an empty element, so <b>UA16 does not fire</b> and UA30
    /// is the only code the audit produces about the hollowness. That combination is what makes the
    /// message's advice testable: a reader following a list of codes none of which appear concludes
    /// the tree is fine.
    /// <para>Object numbers: 7 Document, 8 the healthy P, 9/10 the hollow Sects, 11 a Span whose
    /// only kid is its own parent.</para>
    /// </remarks>
    private static byte[] KeptTreeWithAHollowBranchPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("This paragraph really is on the page.")),
        HelveticaFont,

        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 12 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",

        // The hollow branch. Its bottom element points back at its own parent rather than holding
        // nothing, so every element has a child and UA16's per-leaf question stays satisfied.
        "<< /Type /StructElem /S /Sect /P 7 0 R /K [10 0 R] >>",
        "<< /Type /StructElem /S /Sect /P 9 0 R /K [11 0 R] >>",
        "<< /Type /StructElem /S /Span /P 10 0 R /K [10 0 R] >>",

        "<< /Nums [0 [8 0 R]] >>",
    });

    [Fact]
    public void SkippingAnExistingHollowTree_NamesACodeTheAuditActuallyReports()
    {
        byte[] pdf = KeptTreeWithAHollowBranchPdf();

        AccessibilityAuditReport audit = RunWithinBudget(() => Audit(pdf));

        // The premise: on this shape UA30 is the only hollow-tree code that fires.
        Assert.NotEmpty(WithCode(audit, "UA30"));
        Assert.Empty(WithCode(audit, "UA16"));

        RemediationReport report = RunWithinBudget(() =>
        {
            using var inMs = new MemoryStream(pdf, false);
            using var outMs = new MemoryStream();
            return PdfAccessibility.RemediateWithReport(inMs, outMs, new RemediationOptions());
        });

        // The pipeline keeps this tree because an element names content — which is the only
        // state in which a skip message exists to be honest about. A tree whose elements name
        // nothing is now tagged rather than described.
        RemediationStepResult autoTag =
            Assert.Single(report.Steps.Where(step => step.Name.IndexOf("tag", StringComparison.OrdinalIgnoreCase) >= 0
                                                    && step.Skipped));

        Assert.Contains("UA30", autoTag.SkipReason, StringComparison.Ordinal);

        // And the advice must not consist solely of codes this input does not produce, which is the
        // defect in its own words rather than a paraphrase of it.
        bool namesAReportedCode = audit.Issues
            .Select(issue => issue.Code)
            .Distinct(StringComparer.Ordinal)
            .Any(code => autoTag.SkipReason.IndexOf(code, StringComparison.Ordinal) >= 0);
        Assert.True(namesAReportedCode,
            "the skip message names no code that the audit of these same bytes reports, so a reader " +
            "who follows it finds nothing and concludes the hollow tree is fine. Message was: " +
            autoTag.SkipReason);
    }

    private static byte[] DeepHollowBranchPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("This paragraph really is on the page.")),
        HelveticaFont,

        // 6 — structure tree root
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 12 0 R /ParentTreeNextKey 1 >>",

        // 7 — Document: one healthy child, one hollow branch.
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",

        // 8 — the healthy paragraph.
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",

        // 9, 10, 11 — the hollow branch.
        "<< /Type /StructElem /S /Sect /P 7 0 R /K [10 0 R] >>",
        "<< /Type /StructElem /S /Sect /P 9 0 R /K [11 0 R] >>",
        "<< /Type /StructElem /S /P /P 10 0 R >>",

        // 12 — parent tree
        "<< /Nums [0 [8 0 R]] >>",
    });

    [Fact]
    public void Audit_DeepHollowBranch_ReportsOnlyTheBranchAndOnlyAtItsRoot()
    {
        AccessibilityAuditReport report = Audit(DeepHollowBranchPdf());

        AccessibilityIssue issue = Assert.Single(WithCode(report, "UA30"));

        // The outer Sect: high enough to describe the whole broken branch, low enough not to
        // accuse the Document, which does reach content through its other child.
        Assert.Equal(9, issue.ObjectNumber);
        Assert.Equal(3, issue.InstanceCount);          // Sect + Sect + P
        Assert.Contains("Sect", issue.StructPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_DeepHollowBranch_DoesNotAccuseTheHealthyAncestorOrTheInnerElements()
    {
        AccessibilityAuditReport report = Audit(DeepHollowBranchPdf());

        IReadOnlyList<AccessibilityIssue> ua30 = WithCode(report, "UA30");

        foreach (int objectNumber in new[] { 7, 10, 11 })
            Assert.DoesNotContain(ua30, i => i.ObjectNumber == objectNumber);
    }

    /// <summary>
    /// The leaf-level rule is unchanged: the empty <c>&lt;P&gt;</c> at the bottom of the branch is
    /// still UA16's finding, reported against its own object. UA30 is an addition, not a
    /// replacement — a remediator gets "this branch is dead" and "this element in particular is
    /// empty" as two separate, separately actionable statements.
    /// </summary>
    [Fact]
    public void Audit_DeepHollowBranch_StillReportsTheEmptyLeafAsUa16()
    {
        AccessibilityAuditReport report = Audit(DeepHollowBranchPdf());

        AccessibilityIssue leaf = Assert.Single(WithCode(report, "UA16"));

        Assert.Equal(11, leaf.ObjectNumber);
        Assert.Equal(IssueSeverity.Error, leaf.Severity);
    }

    // =====================================================================
    //  Fixture 3 — two independent hollow branches
    // =====================================================================

    /// <summary>
    /// Two sibling branches are hollow and one is healthy. "Report the highest such element"
    /// means one issue <i>per branch</i>, not one per document: collapsing both into a single
    /// finding at the Document would name an element that does reach content.
    /// </summary>
    private static byte[] TwoHollowBranchesPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("Real content.")),
        HelveticaFont,

        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 13 0 R /ParentTreeNextKey 1 >>",

        // 7 — Document: healthy P, then two hollow Sect branches.
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R 10 0 R] >>",
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
        "<< /Type /StructElem /S /Sect /P 7 0 R /K [11 0 R] >>",
        "<< /Type /StructElem /S /Sect /P 7 0 R /K [12 0 R] >>",
        "<< /Type /StructElem /S /P /P 9 0 R >>",
        "<< /Type /StructElem /S /P /P 10 0 R >>",

        "<< /Nums [0 [8 0 R]] >>",
    });

    [Fact]
    public void Audit_TwoHollowBranches_ReportsOneIssuePerBranch()
    {
        AccessibilityAuditReport report = Audit(TwoHollowBranchesPdf());

        List<int> reported = WithCode(report, "UA30")
            .Where(i => i.ObjectNumber.HasValue)
            .Select(i => i.ObjectNumber.Value)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(new[] { 9, 10 }, reported);

        foreach (AccessibilityIssue issue in WithCode(report, "UA30"))
            Assert.Equal(2, issue.InstanceCount);      // Sect + its empty P
    }

    // =====================================================================
    //  Fixture 4 — a correctly tagged document must stay silent
    // =====================================================================

    /// <summary>
    /// Real MCIDs, a real /ParentTree and a real /StructParents. Nothing here is hollow, so the
    /// new check must say nothing at all — the direction that matters most, because a checker
    /// that fires on good files trains people to ignore it.
    /// </summary>
    private static byte[] WellTaggedPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("A properly tagged paragraph.")),
        HelveticaFont,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [8 0 R]] >>",
    });

    [Fact]
    public void Audit_ProperlyTaggedDocument_ReportsNoHollowSubtree()
    {
        AccessibilityAuditReport report = Audit(WellTaggedPdf());

        Assert.Empty(WithCode(report, "UA30"));
        Assert.Empty(WithCode(report, "UA16"));
    }

    // =====================================================================
    //  Fixture 5 — /OBJR is content
    // =====================================================================

    /// <summary>
    /// A <c>&lt;Link&gt;</c> whose only child is an <c>/OBJR</c> pointing at a link annotation.
    /// An /OBJR <i>is</i> a reference to content, so neither the element nor its Document ancestor
    /// may be reported. The /OBJR is stored indirectly, which is the path that has to resolve the
    /// object before it can classify it.
    /// </summary>
    private static byte[] ObjrOnlyPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Annots [10 0 R] /Tabs /S >>",
        StreamObject(ArtifactOnlyContent()),
        HelveticaFont,

        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",

        // 8 — the Link element; its whole content is the annotation reference in object 11.
        "<< /Type /StructElem /S /Link /P 7 0 R /Pg 3 0 R /K [11 0 R] /Alt (Tetitulz home page) >>",

        // 9 — parent tree, keyed by the annotation's /StructParent.
        "<< /Nums [0 [8 0 R]] >>",

        // 10 — the annotation itself
        "<< /Type /Annot /Subtype /Link /Rect [72 700 260 720] /StructParent 0 /Border [0 0 0] "
        + "/Contents (Tetitulz home page) >>",

        // 11 — the object reference, stored indirectly
        "<< /Type /OBJR /Pg 3 0 R /Obj 10 0 R >>",
    });

    [Fact]
    public void Audit_ElementWhoseOnlyContentIsAnObjr_IsNotReported()
    {
        AccessibilityAuditReport report = Audit(ObjrOnlyPdf());

        Assert.Empty(WithCode(report, "UA30"));
        Assert.Empty(WithCode(report, "UA16"));
    }

    // =====================================================================
    //  Fixture 6 — /MCR is content
    // =====================================================================

    /// <summary>
    /// A paragraph whose <c>/K</c> is a single, direct <c>/MCR</c> dictionary rather than a bare
    /// MCID integer. It references marked content just as much, so nothing may be reported.
    /// </summary>
    private static byte[] McrOnlyPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("Reached through an /MCR.")),
        HelveticaFont,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
        "<< /Type /StructElem /S /P /P 7 0 R /K << /Type /MCR /Pg 3 0 R /MCID 0 >> >>",
        "<< /Nums [0 [8 0 R]] >>",
    });

    [Fact]
    public void Audit_ElementWhoseOnlyContentIsAnMcr_IsNotReported()
    {
        AccessibilityAuditReport report = Audit(McrOnlyPdf());

        Assert.Empty(WithCode(report, "UA30"));
        Assert.Empty(WithCode(report, "UA16"));
    }

    // =====================================================================
    //  Fixture 7 — a cyclic /K graph
    // =====================================================================

    /// <summary>
    /// <c>Document → Sect → Sect → Document</c>. Reachability over a cyclic graph is where a naive
    /// "ask each element whether anything below it reaches content" implementation either
    /// recurses forever or goes exponential; the propagation must flip each element's flag at
    /// most once and terminate.
    /// </summary>
    private static byte[] CyclicStructureTreePdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Tabs /S >>",
        StreamObject(ArtifactOnlyContent()),
        HelveticaFont,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 0 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
        "<< /Type /StructElem /S /Sect /P 7 0 R /K [9 0 R] >>",
        "<< /Type /StructElem /S /Sect /P 8 0 R /K [7 0 R] >>",   // back to the Document
        "<< /Nums [] >>",
    });

    [Fact]
    public void Audit_CyclicStructureTree_CompletesPromptlyAndStillReportsTheHollowSubtree()
    {
        byte[] pdf = CyclicStructureTreePdf();

        AccessibilityAuditReport report = RunWithinBudget(() => Audit(pdf));

        Assert.NotNull(report);
        Assert.NotEmpty(report.Issues);   // a usable report, not an empty one

        AccessibilityIssue issue = Assert.Single(WithCode(report, "UA30"));
        Assert.Equal(7, issue.ObjectNumber);
        Assert.Equal(3, issue.InstanceCount);   // Document + both Sects
    }

    /// <summary>
    /// The other direction for the cycle: the same shape, but one element in the loop points at a
    /// real MCID. The cycle must not be reported at all — it reaches content, however tangled.
    /// </summary>
    private static byte[] CyclicButContentBearingPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("Inside the loop, but real.")),
        HelveticaFont,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
        "<< /Type /StructElem /S /Sect /P 7 0 R /K [9 0 R] >>",
        // MCID 0 *and* a back-edge to the Document. Written reference-first so the "7 0 R"
        // token run cannot be misread against the neighbouring integer.
        "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K [7 0 R 0] >>",
        "<< /Nums [0 [9 0 R]] >>",
    });

    [Fact]
    public void Audit_CyclicStructureTreeThatReachesContent_IsNotReported()
    {
        byte[] pdf = CyclicButContentBearingPdf();

        AccessibilityAuditReport report = RunWithinBudget(() => Audit(pdf));

        Assert.Empty(WithCode(report, "UA30"));
    }

    // =====================================================================
    //  Fixture 8 — roles that may legitimately be empty
    // =====================================================================

    /// <summary>
    /// A table whose single row holds nothing but empty <c>&lt;TD&gt;</c> cells. Each cell is
    /// individually exempt — an empty cell is how a grid stays rectangular, which this library's
    /// own table-regularity check requires — but the branch as a whole reaches no content, and a
    /// table that describes nothing is a real defect. The exemption is leaf-only by design.
    /// </summary>
    private static byte[] HollowTablePdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("Body text outside the table.")),
        HelveticaFont,

        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 13 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",

        // 9, 10, 11, 12 — Table > TR > TD, TD, every cell empty.
        "<< /Type /StructElem /S /Table /P 7 0 R /K [10 0 R] >>",
        "<< /Type /StructElem /S /TR /P 9 0 R /K [11 0 R 12 0 R] >>",
        "<< /Type /StructElem /S /TD /P 10 0 R >>",
        "<< /Type /StructElem /S /TD /P 10 0 R >>",

        "<< /Nums [0 [8 0 R]] >>",
    });

    [Fact]
    public void Audit_TableOfNothingButEmptyCells_IsReportedEvenThoughEveryCellIsExempt()
    {
        AccessibilityAuditReport report = Audit(HollowTablePdf());

        // The per-leaf exemption still holds: no cell is individually accused.
        Assert.Empty(WithCode(report, "UA16"));

        // But the branch as a whole reaches nothing, and that is reported at the Table.
        AccessibilityIssue issue = Assert.Single(WithCode(report, "UA30"));
        Assert.Equal(9, issue.ObjectNumber);
        Assert.Equal(4, issue.InstanceCount);   // Table + TR + two TDs
    }

    /// <summary>
    /// The healthy twin: the same table with one cell that does carry content. The other cell is
    /// still empty and still exempt, and nothing at all may be reported — this is the false
    /// positive that would make the exemption pointless.
    /// </summary>
    private static byte[] TableWithOneEmptyCellPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("Cell text.")),
        HelveticaFont,

        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 12 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
        "<< /Type /StructElem /S /Table /P 7 0 R /K [9 0 R] >>",
        "<< /Type /StructElem /S /TR /P 8 0 R /K [10 0 R 11 0 R] >>",
        "<< /Type /StructElem /S /TD /P 9 0 R /Pg 3 0 R /K 0 >>",
        "<< /Type /StructElem /S /TD /P 9 0 R >>",                 // the deliberately empty cell
        "<< /Nums [0 [10 0 R]] >>",
    });

    [Fact]
    public void Audit_TableWithOneEmptyCell_IsNotReported()
    {
        AccessibilityAuditReport report = Audit(TableWithOneEmptyCellPdf());

        Assert.Empty(WithCode(report, "UA30"));
        Assert.Empty(WithCode(report, "UA16"));
    }

    // =====================================================================
    //  Fixture 9 — the leaf-level rule is untouched
    // =====================================================================

    /// <summary>
    /// A Document with one healthy paragraph and one empty one. This is UA16's own case: one UA16
    /// against the empty leaf, and
    /// <b>no</b> UA30, because the Document itself reaches content and an empty leaf is not a
    /// subtree.
    /// </summary>
    private static byte[] OneEmptyLeafParagraphPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R" + CatalogTail,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",
        StreamObject(TaggedContent("The paragraph that is real.")),
        HelveticaFont,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
        "<< /Type /StructElem /S /P /P 7 0 R >>",                  // empty, and not exempt
        "<< /Nums [0 [8 0 R]] >>",
    });

    [Fact]
    public void Audit_EmptyLeafParagraph_IsStillUa16AndNotUa30()
    {
        AccessibilityAuditReport report = Audit(OneEmptyLeafParagraphPdf());

        AccessibilityIssue leaf = Assert.Single(WithCode(report, "UA16"));
        Assert.Equal(9, leaf.ObjectNumber);
        Assert.Equal(IssueSeverity.Error, leaf.Severity);

        Assert.Empty(WithCode(report, "UA30"));
    }

    /// <summary>
    /// UA16's message must state the rule it actually implements. It is a per-leaf check, and it
    /// said "Structure element references no content" — a claim broad enough to be read as
    /// covering the whole subtree, which is exactly the thing it could not see. A reader who
    /// trusts the message must not be misled about the scope of the finding.
    /// </summary>
    [Fact]
    public void Audit_Ua16Message_SaysItIsAboutALeaf()
    {
        AccessibilityAuditReport report = Audit(OneEmptyLeafParagraphPdf());

        AccessibilityIssue leaf = Assert.Single(WithCode(report, "UA16"));
        Assert.Contains("leaf", leaf.Message, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    //  An untagged document has no structure tree to be hollow
    // =====================================================================

    [Fact]
    public void Audit_UntaggedDocument_ReportsNoHollowSubtree()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject("BT /F1 12 Tf 72 700 Td (Plain untagged text.) Tj ET\n"),
            HelveticaFont,
        });

        AccessibilityAuditReport report = Audit(pdf);

        // UA01 already says the document is not tagged; inventing a hollow-subtree finding on top
        // of that would be noise about a tree that does not exist.
        Assert.Empty(WithCode(report, "UA30"));
    }
}
