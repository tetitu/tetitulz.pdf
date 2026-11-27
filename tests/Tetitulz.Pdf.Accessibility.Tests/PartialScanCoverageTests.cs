// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A page the checker only half-read must be named in the report.
///
/// <para>
/// <b>The gap this file closes.</b> <c>ReportContentCoverage</c> makes an unreadable page visible,
/// but it is gated on <c>PageInfo.ContentUsable</c> — <c>ContentError == null &amp;&amp;
/// Scan != null &amp;&amp; Scan.IsSafe</c>, all three facts about the page's own <c>/Contents</c>.
/// A page whose <c>/Contents</c> decodes perfectly can still invoke a Form XObject that will not
/// resolve, will not decode, will not scan safely or does not fit the audit's remaining form budget.
/// <c>ContentUsable</c> is then still true, so the coverage notice says nothing, while
/// <c>FormXObjectScanIncomplete</c> makes UA24 and UA14's mapping skip the page outright and leaves
/// UA17/UA18 reporting from counts that are known lower bounds.
/// </para>
///
/// <para>
/// The result was a report byte-identical to one for a page that was scanned in full and found
/// clean — no UA99, no Info issue, no page reference — and
/// <see cref="AccessibilityAuditReport.IsConforming"/> could be true for a document the checker
/// never finished looking at. Two XML doc comments in the checker asserted the opposite ("stated in
/// the report by ReportContentCoverage", "the affected pages are marked as incompletely scanned"):
/// the marking existed but never left the checker.
/// </para>
///
/// <para>Both fixtures are assembled in memory from ASCII bytes; nothing is read from disk.</para>
/// </summary>
public class PartialScanCoverageTests
{
    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private static string StreamObjectWith(string entries, string content)
        => "<< " + entries + " /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

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
    /// A tagged one-page document whose content stream decodes perfectly and draws exactly two
    /// things: one Image XObject and one Form XObject. Only object 6 — the form — differs between
    /// the two fixtures, so any difference in the report is attributable to it alone.
    /// </summary>
    private static byte[] ImageAndFormPdf(string formObjectBody) => BuildPdf(new List<string>
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R /MarkInfo << /Marked true >> "
        + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /XObject << /Im0 5 0 R /Fx0 6 0 R >> >> "
        + "/Contents 4 0 R /StructParents 0 /Tabs /S >>",

        // 4 — content: perfectly well-formed, and every check can read it
        StreamObject("/Im0 Do\n/Fx0 Do\n"),

        // 5 — a 1×1 image
        StreamObjectWith("/Type /XObject /Subtype /Image /Width 1 /Height 1 "
                         + "/ColorSpace /DeviceGray /BitsPerComponent 8", "A"),

        // 6 — the form (supplied)
        formObjectBody,

        // 7 — structure tree root
        "<< /Type /StructTreeRoot /K [8 0 R] /ParentTree 9 0 R >>",

        // 8 — a Figure claiming MCID 0 on page 1
        "<< /Type /StructElem /S /Figure /Alt (A photograph of a bridge) /P 7 0 R /Pg 3 0 R /K [0] >>",

        // 9 — the parent tree
        "<< /Nums [0 [8 0 R]] >>",
    });

    /// <summary>
    /// A Form XObject dictionary that is not a stream. <c>SummariseTokens</c> resolves its
    /// <c>/Subtype /Form</c> and descends, <c>DescendIntoForm</c> resolves it to something that is
    /// not a <c>PdfStream</c>, and the page is marked as incompletely scanned. Deterministic and
    /// needs no binary payload — unlike a corrupt Flate stream, whose failure mode depends on the
    /// inflater.
    /// </summary>
    private const string UnreadableForm =
        "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] >>";

    private static readonly string ReadableForm =
        StreamObjectWith("/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "q Q\n");

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    /// <summary>The partial-scan notice, isolated from every other use of UA99 by its message.</summary>
    private static IReadOnlyList<AccessibilityIssue> PartialNotices(IEnumerable<AccessibilityIssue> issues)
        => issues.Where(i =>
                string.Equals(i.Code, "UA99", StringComparison.Ordinal) &&
                i.Message.IndexOf("scanned only in part", StringComparison.Ordinal) >= 0)
           .ToList();

    private static string Describe(IEnumerable<AccessibilityIssue> issues)
        => string.Join(" | ", issues.Select(i => i.ToString()));

    // =====================================================================
    //  Fires
    // =====================================================================

    /// <summary>
    /// The defect: an issue list with no mention of page 1 at all, so a page that might be a scan,
    /// might hold untagged text and might hold unmapped marked content would be indistinguishable
    /// from a clean one.
    /// </summary>
    [Fact]
    public void PageWithAnUnreadableFormXObject_IsNamedInTheReport()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(ImageAndFormPdf(UnreadableForm));

        AccessibilityIssue notice = Assert.Single(PartialNotices(issues));

        Assert.Equal(IssueSeverity.Info, notice.Severity);
        Assert.Equal(1, notice.PageNumber);
        Assert.Equal(3, notice.ObjectNumber);        // the page object

        // The reason must name what was not read, not offer a generic apology.
        Assert.Contains("Form XObjects it invokes could not be read", notice.Message,
            StringComparison.Ordinal);

        // …and it must name what was consequently not established. UA24 and UA14's mapping skipped
        // the page outright; UA17 ran, but on counts that cannot include the form's operators.
        Assert.Contains("UA24 (image-only, probably scanned pages)", notice.Message,
            StringComparison.Ordinal);
        Assert.Contains("UA14's marked-content mapping", notice.Message, StringComparison.Ordinal);
        Assert.Contains("UA17 (content drawn outside every marked-content sequence)", notice.Message,
            StringComparison.Ordinal);
        Assert.Contains("lower bounds", notice.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice is Info, exactly like the encryption and unreadable-content ones: the document has
    /// not been shown to be wrong, only not fully examined, so it must not flip
    /// <see cref="AccessibilityAuditReport.IsConforming"/> by itself.
    /// </summary>
    [Fact]
    public void PartialScanNoticeIsInfoAndInventsNoErrors()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(ImageAndFormPdf(UnreadableForm));

        Assert.All(PartialNotices(issues), i => Assert.Equal(IssueSeverity.Info, i.Severity));

        // UA24 must still be withheld: "no text" is not established when the form that might draw
        // it was never read. Withholding it is correct — saying nothing at all was not.
        Assert.DoesNotContain(issues, i => string.Equals(i.Code, "UA24", StringComparison.Ordinal));
    }

    // =====================================================================
    //  Does not fire
    // =====================================================================

    /// <summary>
    /// The other direction, which matters just as much: the identical document with a readable form
    /// must produce no partial-scan notice — and must produce the UA24 finding the other fixture
    /// withholds, which is what proves the two fixtures differ only in readability.
    /// </summary>
    [Fact]
    public void PageWithAReadableFormXObject_ProducesNoNoticeAndStillReportsUa24()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(ImageAndFormPdf(ReadableForm));

        Assert.True(PartialNotices(issues).Count == 0,
            "A page whose Form XObjects all read cleanly must not be reported as partially scanned. " +
            "Issues: " + Describe(issues));

        Assert.Contains(issues, i => string.Equals(i.Code, "UA24", StringComparison.Ordinal));
    }
}
