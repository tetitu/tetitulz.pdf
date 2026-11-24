// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Locks shut the worst defect the accessibility review found: the audit called a <em>hollow</em>
/// file — a structure tree of elements that reference no content, no /ParentTree, and page content
/// with no marked content anywhere — very nearly conformant.
///
/// <para>
/// Every new check is tested in <b>both</b> directions. A checker that fires on everything is as
/// useless as one that fires on nothing: a false positive on a correctly tagged file trains users
/// to ignore the report, which is how the real failures get through. So each fixture below comes
/// in a broken and a correct variant, and the correct variant asserts silence.
/// </para>
///
/// <para>All fixtures are built in memory as <c>byte[]</c>; nothing is read from disk.</para>
/// </summary>
public class AuditHonestyTests
{
    // =====================================================================
    //  PDF assembly helpers
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

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

    private static string UntaggedContent(string text)
        => "BT /F1 12 Tf 72 700 Td (" + text + ") Tj ET\n";

    private static string TaggedContent(string text)
        => "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (" + text + ") Tj ET\nEMC\n";

    private static AccessibilityAuditReport Audit(byte[] pdf)
        => PdfAccessibility.Audit(new MemoryStream(pdf));

    private static IReadOnlyList<AccessibilityIssue> WithCode(AccessibilityAuditReport report, string code)
        => report.Issues.Where(i => string.Equals(i.Code, code, StringComparison.Ordinal)).ToList();

    // =====================================================================
    //  Fixture 1 — the hollow "tagged" document
    // =====================================================================

    /// <summary>
    /// The shape a hollow auto-tagging pass produces, and one an audit must not bless:
    /// the catalog claims <c>/MarkInfo /Marked true</c>, carries a <c>/Lang</c> and a
    /// <c>/StructTreeRoot</c>, and the tree holds a Document element whose children have no
    /// <c>/K</c> at all. There is no <c>/ParentTree</c>, the page has no <c>/StructParents</c>,
    /// and the page content draws text without a single <c>BDC</c>. Nothing on the page belongs
    /// to anything in the tree.
    /// <para>Object numbers are fixed and asserted on: 3 is the page, 8 and 9 are the two
    /// childless structure elements.</para>
    /// </summary>
    private static byte[] HollowTaggedPdf() => BuildPdf(new[]
    {
        // 1 — catalog: every structural box ticked.
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> "
        + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page: no /StructParents, so its (nonexistent) marked content is unreachable.
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Tabs /S >>",

        // 4 — content: draws text, no marked content anywhere.
        StreamObject(UntaggedContent("This text is drawn but belongs to no structure element.")),

        // 5 — font
        HelveticaFont,

        // 6 — structure tree root, with NO /ParentTree.
        "<< /Type /StructTreeRoot /K [7 0 R] >>",

        // 7 — Document element; it has children, so it is not itself empty.
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",

        // 8, 9 — the hollow leaves: no /K, no MCID, no /MCR, no /OBJR.
        "<< /Type /StructElem /S /H1 /P 7 0 R >>",
        "<< /Type /StructElem /S /P /P 7 0 R >>",
    });

    // =====================================================================
    //  Fixture 2 — three pages, correctly tagged, optionally breaking page 2
    // =====================================================================

    /// <summary>
    /// Three tagged pages. With <paramref name="breakSecondPage"/> the middle page loses its
    /// <c>/StructParents</c>, its marked content and its structure element, while pages 1 and 3
    /// stay correct — so a checker that reports the wrong page, or reports 0-based page indices,
    /// is caught.
    /// </summary>
    private static byte[] TaggedThreePagePdf(bool breakSecondPage)
    {
        string Page(int contentObject, int structParentsKey) =>
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 9 0 R >> >> /Contents "
            + contentObject.ToString(CultureInfo.InvariantCulture) + " 0 R"
            + (structParentsKey >= 0
                ? " /StructParents " + structParentsKey.ToString(CultureInfo.InvariantCulture)
                : string.Empty)
            + " /Tabs /S >>";

        string documentKids = breakSecondPage ? "[10 0 R 13 0 R]" : "[10 0 R 15 0 R 13 0 R]";
        string nums = breakSecondPage
            ? "[0 [10 0 R] 2 [13 0 R]]"
            : "[0 [10 0 R] 1 [15 0 R] 2 [13 0 R]]";

        var objects = new List<string>
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 12 0 R /MarkInfo << /Marked true >> "
            + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

            // 2 — page tree
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",

            // 3, 4, 5 — pages; page 2 loses its key when broken.
            Page(6, 0),
            Page(7, breakSecondPage ? -1 : 1),
            Page(8, 2),

            // 6, 7, 8 — content; page 2 loses its marked content when broken.
            StreamObject(TaggedContent("Page one is tagged.")),
            StreamObject(breakSecondPage
                ? UntaggedContent("Page two draws text that no BDC ever marks.")
                : TaggedContent("Page two is tagged.")),
            StreamObject(TaggedContent("Page three is tagged.")),

            // 9 — font
            HelveticaFont,

            // 10 — paragraph on page 1
            "<< /Type /StructElem /S /P /P 11 0 R /Pg 3 0 R /K 0 >>",

            // 11 — Document element
            "<< /Type /StructElem /S /Document /P 12 0 R /K " + documentKids + " >>",

            // 12 — structure tree root
            "<< /Type /StructTreeRoot /K [11 0 R] /ParentTree 14 0 R /ParentTreeNextKey 3 >>",

            // 13 — paragraph on page 3
            "<< /Type /StructElem /S /P /P 11 0 R /Pg 5 0 R /K 0 >>",

            // 14 — parent tree
            "<< /Nums " + nums + " >>",
        };

        // 15 — paragraph on page 2. Only exists in the healthy variant; it is the last object,
        // so omitting it leaves every other object number unchanged.
        if (!breakSecondPage)
            objects.Add("<< /Type /StructElem /S /P /P 11 0 R /Pg 4 0 R /K 0 >>");

        return BuildPdf(objects);
    }

    // =====================================================================
    //  Fixture 3 — one correctly tagged page with a configurable /MarkInfo
    // =====================================================================

    /// <summary>
    /// A single correctly tagged page whose catalog <c>/MarkInfo</c> entry is supplied verbatim,
    /// so the same document can be audited with the entry absent, direct, carrying
    /// <c>/Suspects</c>, or stored indirectly as <c>/MarkInfo 10 0 R</c>.
    /// </summary>
    /// <param name="markInfoEntry">Catalog text such as
    /// <c>"/MarkInfo &lt;&lt; /Marked true &gt;&gt;"</c>, or an empty string to omit it.</param>
    /// <param name="withMarkInfoObject">Emit object 10 as <c>&lt;&lt; /Marked true &gt;&gt;</c>,
    /// for the indirect case.</param>
    private static byte[] SinglePageTaggedPdf(string markInfoEntry, bool withMarkInfoObject = false)
    {
        var objects = new List<string>
        {
            // 1 — catalog
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R " + markInfoEntry
            + " /Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

            // 2 — page tree
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

            // 3 — page
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",

            // 4 — properly marked content
            StreamObject(TaggedContent("A properly tagged paragraph.")),

            // 5 — font
            HelveticaFont,

            // 6 — structure tree root
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",

            // 7 — Document element
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",

            // 8 — paragraph, pointing at MCID 0 on page 3 0 R
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",

            // 9 — parent tree
            "<< /Nums [0 [8 0 R]] >>",
        };

        if (withMarkInfoObject) objects.Add("<< /Marked true >>");   // 10

        return BuildPdf(objects);
    }

    // =====================================================================
    //  The hollow document must not pass
    // =====================================================================

    [Fact]
    public void Audit_HollowTaggedPdf_IsNotConforming()
    {
        AccessibilityAuditReport report = Audit(HollowTaggedPdf());

        Assert.False(report.IsConforming,
            "a document whose structure tree describes content it is not connected to must never " +
            "be reported as conforming. Issues: " +
            string.Join(" | ", report.Issues.Select(i => i.ToString())));
        Assert.True(report.HasBlockingIssues);
        Assert.True(report.ErrorCount > 0);
    }

    [Fact]
    public void Audit_HollowTaggedPdf_ReportsEmptyStructureElementsAsErrors()
    {
        AccessibilityAuditReport report = Audit(HollowTaggedPdf());

        IReadOnlyList<AccessibilityIssue> ua16 = WithCode(report, "UA16");
        Assert.Equal(2, ua16.Count);   // objects 8 and 9; the Document element has children

        foreach (AccessibilityIssue issue in ua16)
            Assert.Equal(IssueSeverity.Error, issue.Severity);

        List<int> reported = ua16
            .Where(i => i.ObjectNumber.HasValue)
            .Select(i => i.ObjectNumber.Value)
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(new[] { 8, 9 }, reported);
    }

    [Fact]
    public void Audit_HollowTaggedPdf_ReportsUnmarkedPageContentAsAnError()
    {
        AccessibilityAuditReport report = Audit(HollowTaggedPdf());

        AccessibilityIssue ua17 = Assert.Single(WithCode(report, "UA17"));
        Assert.Equal(IssueSeverity.Error, ua17.Severity);
        Assert.Equal(1, ua17.PageNumber);
    }

    [Fact]
    public void Audit_HollowTaggedPdf_ReportsMissingStructParentsAsAnError()
    {
        AccessibilityAuditReport report = Audit(HollowTaggedPdf());

        AccessibilityIssue ua18 = Assert.Single(WithCode(report, "UA18"));
        Assert.Equal(IssueSeverity.Error, ua18.Severity);
        Assert.Equal(1, ua18.PageNumber);
        Assert.Equal(3, ua18.ObjectNumber);   // the page object
    }

    [Fact]
    public void Audit_HollowTaggedPdf_MissingParentTreeIsAnErrorNotInfo()
    {
        AccessibilityAuditReport report = Audit(HollowTaggedPdf());

        AccessibilityIssue missing = Assert.Single(
            report.Issues.Where(i => i.Message.IndexOf("has no /ParentTree", StringComparison.Ordinal) >= 0));

        // Reporting this as Info is precisely what let a hollow tree look conformant: without a
        // /ParentTree no marked-content identifier can ever be mapped back to structure.
        Assert.Equal(IssueSeverity.Error, missing.Severity);
        Assert.Equal("UA14", missing.Code);
    }

    // =====================================================================
    //  Issues must say WHERE
    // =====================================================================

    [Fact]
    public void Audit_HollowTaggedPdf_IssuesCarryLocations()
    {
        AccessibilityAuditReport report = Audit(HollowTaggedPdf());

        Assert.Contains(report.Issues, i => i.PageNumber.HasValue);
        Assert.Contains(report.Issues, i => i.ObjectNumber.HasValue);

        // ByPage was permanently empty before: nothing in the pipeline ever set a page number,
        // so a remediation UI could not point at anything.
        Assert.NotEmpty(report.ByPage);
        Assert.NotEmpty(report.PagesWithIssues);
        Assert.Contains(1, report.PagesWithIssues);

        // Every located issue must also be reachable through the grouping.
        foreach (AccessibilityIssue issue in report.Issues.Where(i => i.PageNumber.HasValue))
            Assert.Contains(issue, report.ByPage[issue.PageNumber.Value]);

        Assert.NotEmpty(report.ByCode);
        Assert.Equal(report.Issues.Count,
            report.DocumentLevelIssues.Count + report.ByPage.Sum(kv => kv.Value.Count));
    }

    [Fact]
    public void Audit_ThreePages_PageNumbersAreOneBasedAndNameTheBrokenPage()
    {
        AccessibilityAuditReport report = Audit(TaggedThreePagePdf(breakSecondPage: true));

        AccessibilityIssue ua17 = Assert.Single(WithCode(report, "UA17"));
        Assert.Equal(IssueSeverity.Error, ua17.Severity);
        Assert.Equal(2, ua17.PageNumber);

        AccessibilityIssue ua18 = Assert.Single(WithCode(report, "UA18"));
        Assert.Equal(IssueSeverity.Error, ua18.Severity);
        Assert.Equal(2, ua18.PageNumber);

        // Pages 1 and 3 are correctly tagged, so every hollowness finding must name page 2 and
        // only page 2. UA16 must not fire at all: every element here references content.
        Assert.Empty(WithCode(report, "UA16"));
        foreach (AccessibilityIssue issue in report.Issues.Where(IsHollownessCheck))
            Assert.Equal(2, issue.PageNumber);

        // A 0-based regression would show up as page 0 or page 1 here; an off-by-one the other
        // way as page 4.
        foreach (AccessibilityIssue issue in report.Issues.Where(i => i.PageNumber.HasValue))
            Assert.InRange(issue.PageNumber.Value, 1, 3);
    }

    private static bool IsHollownessCheck(AccessibilityIssue issue)
        => issue.Code == "UA16" || issue.Code == "UA17" || issue.Code == "UA18";

    // =====================================================================
    //  /MarkInfo /Suspects
    // =====================================================================

    [Fact]
    public void Audit_SuspectsTrue_IsReportedAsAnError()
    {
        AccessibilityAuditReport report =
            Audit(SinglePageTaggedPdf("/MarkInfo << /Marked true /Suspects true >>"));

        AccessibilityIssue ua19 = Assert.Single(WithCode(report, "UA19"));
        Assert.Equal(IssueSeverity.Error, ua19.Severity);
        Assert.False(report.IsConforming);
    }

    [Fact]
    public void Audit_SuspectsAbsent_DoesNotReportUa19()
    {
        AccessibilityAuditReport report = Audit(SinglePageTaggedPdf("/MarkInfo << /Marked true >>"));
        Assert.Empty(WithCode(report, "UA19"));
    }

    // =====================================================================
    //  Indirect /MarkInfo was a real false positive
    // =====================================================================

    [Fact]
    public void Audit_IndirectMarkInfo_DoesNotReportUa01()
    {
        // `/MarkInfo 10 0 R` is how most producers write it; the old bare cast reported every
        // such file as untagged.
        AccessibilityAuditReport report =
            Audit(SinglePageTaggedPdf("/MarkInfo 10 0 R", withMarkInfoObject: true));

        Assert.Empty(WithCode(report, "UA01"));
    }

    [Fact]
    public void Audit_MissingMarkInfo_StillReportsUa01()
    {
        // The other direction: UA01 must still fire when the entry really is absent, otherwise
        // the test above would also pass for a check that stopped working.
        AccessibilityAuditReport report = Audit(SinglePageTaggedPdf(string.Empty));

        AccessibilityIssue ua01 = Assert.Single(WithCode(report, "UA01"));
        Assert.Equal(IssueSeverity.Error, ua01.Severity);
    }

    // =====================================================================
    //  A correct file must stay silent
    // =====================================================================

    [Fact]
    public void Audit_ValidTaggedSinglePage_FiresNoneOfTheHollownessChecks()
    {
        AccessibilityAuditReport report = Audit(SinglePageTaggedPdf("/MarkInfo << /Marked true >>"));

        Assert.Empty(WithCode(report, "UA16"));
        Assert.Empty(WithCode(report, "UA17"));
        Assert.Empty(WithCode(report, "UA18"));
        Assert.DoesNotContain(report.Issues,
            i => i.Message.IndexOf("has no /ParentTree", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public void Audit_ValidTaggedThreePages_FiresNoneOfTheHollownessChecks()
    {
        AccessibilityAuditReport report = Audit(TaggedThreePagePdf(breakSecondPage: false));

        Assert.Empty(WithCode(report, "UA16"));
        Assert.Empty(WithCode(report, "UA17"));
        Assert.Empty(WithCode(report, "UA18"));
    }

    /// <summary>
    /// The two fixtures differ only in page 2, so the difference in the report must be exactly
    /// the page-2 findings. This is what proves the checks are discriminating rather than
    /// firing on document shape.
    /// </summary>
    [Fact]
    public void Audit_BreakingOnePage_AddsErrorsRatherThanChangingTheWholeVerdict()
    {
        AccessibilityAuditReport healthy = Audit(TaggedThreePagePdf(breakSecondPage: false));
        AccessibilityAuditReport broken = Audit(TaggedThreePagePdf(breakSecondPage: true));

        Assert.True(broken.ErrorCount > healthy.ErrorCount,
            "breaking page 2 must add errors: healthy=" + healthy.ErrorCount +
            ", broken=" + broken.ErrorCount);

        Assert.False(broken.IsConforming);
    }
}
