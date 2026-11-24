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
/// UA18 must fire on a page that holds structural content <em>items</em>, and only on such a page.
///
/// <para>
/// <b>The false positive this file pins down.</b> ISO 32000-1 Table 30 requires
/// <c>/StructParents</c> only when a page "contains structural content items", and §14.7.4.2 defines
/// such an item as a marked-content sequence identified by an <c>/MCID</c> — that number is the only
/// handle the structure tree has on it. The checker counted every non-artifact <c>BDC</c>/<c>BMC</c>
/// instead, before looking at whether the property list held an <c>/MCID</c> at all. An
/// optional-content wrapper (<c>/OC /MC0 BDC</c>) round a watermark is ordinary producer output that
/// names no MCID and is not an item Table 30 speaks of; the page was nevertheless reported as an
/// Error — "it opens 1 non-artifact marked-content sequence(s) … but has no /StructParents entry" —
/// for content that is entirely an artifact.
/// </para>
///
/// <para>Both fixtures are assembled in memory from ASCII bytes; nothing is read from disk.</para>
/// </summary>
public class OptionalContentStructParentsTests
{
    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
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
    /// A tagged two-page document. Page 1 is an ordinary tagged page carrying <c>/StructParents 0</c>
    /// and one <c>&lt;P&gt;</c>. Page 2 is a divider with <b>no</b> <c>/StructParents</c>, and only
    /// its content stream varies — so any UA18 finding against page 2 is attributable to that
    /// content alone.
    /// </summary>
    private static byte[] TwoPagePdf(string page2Content) => BuildPdf(new List<string>
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 8 0 R /MarkInfo << /Marked true >> "
        + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",

        // 3 — page 1, correctly keyed
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",

        // 4 — page 1 content
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Body) Tj ET\nEMC\n"),

        // 5 — page 2, the divider: NO /StructParents
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 7 0 R >> /Properties << /MC0 10 0 R >> >> "
        + "/Contents 6 0 R /Tabs /S >>",

        // 6 — page 2 content (supplied)
        StreamObject(page2Content),

        // 7 — font
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",

        // 8 — structure tree root
        "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 11 0 R >>",

        // 9 — the single paragraph, claiming MCID 0 on page 1
        "<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R /K [0] >>",

        // 10 — the optional-content group page 2's /OC sequence names
        "<< /Type /OCG /Name (Watermark) >>",

        // 11 — the parent tree
        "<< /Nums [0 [9 0 R]] >>",
    });

    /// <summary>
    /// A watermark inside an optional-content wrapper: the entire page is an artifact, and the
    /// <c>/OC</c> sequence names an OCG, not an MCID.
    /// </summary>
    private const string OptionalContentWrappedArtifact =
        "/OC /MC0 BDC\n/Artifact <</Type /Pagination /Subtype /Watermark>> BDC\n"
        + "BT /F1 10 Tf 72 700 Td (DRAFT) Tj ET\nEMC\nEMC\n";

    /// <summary>
    /// The same page with real structural content: a sequence that <em>does</em> name an MCID, on a
    /// page that still has no <c>/StructParents</c>. This is what UA18 exists for.
    /// </summary>
    private const string RealStructuralContent =
        "/P <</MCID 0>> BDC\nBT /F1 10 Tf 72 700 Td (Section two) Tj ET\nEMC\n";

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    private static string Describe(IEnumerable<AccessibilityIssue> issues)
        => string.Join(" | ", issues.Select(i => i.ToString()));

    // =====================================================================
    //  Does not fire
    // =====================================================================

    /// <summary>
    /// The false positive. Counting the <c>/OC</c> <c>BDC</c> as a structural sequence the moment
    /// its tag is seen not to be <c>/Artifact</c> would report page 2 — whose every drawing operator
    /// is inside an artifact sequence — as an Error for a key that ISO 32000-1 Table 30 does not
    /// require of it.
    /// </summary>
    [Fact]
    public void OptionalContentWrapperRoundAnArtifact_IsNotReportedAsMissingStructParents()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(TwoPagePdf(OptionalContentWrappedArtifact));

        IReadOnlyList<AccessibilityIssue> ua18 = issues
            .Where(i => string.Equals(i.Code, "UA18", StringComparison.Ordinal))
            .ToList();

        Assert.True(ua18.Count == 0,
            "Page 2's content is an /OC wrapper round an /Artifact sequence: it names no /MCID, so it " +
            "holds no structural content item and correctly has no /StructParents. UA18 issues: " +
            Describe(ua18) + " — all issues: " + Describe(issues));
    }

    // =====================================================================
    //  Fires
    // =====================================================================

    /// <summary>
    /// The control that keeps the rule from being a blanket suppression: the same page with a
    /// sequence that really does name an <c>/MCID</c> must still be reported.
    /// </summary>
    [Fact]
    public void PageWithAnMcidBearingSequenceAndNoStructParents_IsStillReported()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(TwoPagePdf(RealStructuralContent));

        AccessibilityIssue ua18 = Assert.Single(issues.Where(i =>
            string.Equals(i.Code, "UA18", StringComparison.Ordinal)));

        Assert.Equal(IssueSeverity.Error, ua18.Severity);
        Assert.Equal(2, ua18.PageNumber);
        Assert.Contains("non-artifact marked-content sequence", ua18.Message, StringComparison.Ordinal);
    }
}
