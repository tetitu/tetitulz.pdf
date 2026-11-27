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
/// The audit must never drop a page quietly.
///
/// <para>
/// <b>Why this file exists.</b> Four checks need a decoded, scannable page content stream —
/// UA14's marked-content mapping, UA17 (text outside every marked-content sequence), UA18 (a page
/// with structural content but no <c>/StructParents</c>) and UA24 (image-only pages) — and each
/// began by skipping any page whose content could not be read. For an <i>encrypted</i> document
/// the checker goes to considerable trouble to make that visible: <c>RunContentCheck</c> records
/// what is lost and <c>ReportEncryptionCoverage</c> emits a UA99 issue enumerating exactly which
/// checks did not run, on the stated rule that "a check that neither ran nor said so is
/// indistinguishable from a check that ran and found nothing".
/// </para>
///
/// <para>
/// That rule was broken for every <i>unencrypted</i> page whose content stream is merely broken —
/// a stray delimiter, a PostScript calculator brace, an inline image of indeterminate length, a
/// corrupt Flate payload. The only trace was an <c>AccessibilityDiagnostics</c> event, which is not
/// part of <see cref="AccessibilityAuditReport"/> at all, so the report was byte-identical to that
/// of a document where those four checks ran and found nothing — and said "NO BLOCKING ISSUES
/// DETECTED" for a page whose entire text is outside every marked-content sequence.
/// </para>
///
/// <para>Both fixtures are assembled in memory from ASCII bytes; nothing is read from disk.</para>
/// </summary>
public class ContentCoverageHonestyTests
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
    /// A one-page document that looks tagged from every angle the structural checks can see:
    /// <c>/MarkInfo /Marked true</c>, <c>/Lang</c>, <c>/ViewerPreferences /DisplayDocTitle</c>, a
    /// <c>/StructTreeRoot</c> with a <c>/ParentTree</c>, a page <c>/StructParents 0</c>, and one
    /// <c>&lt;P&gt;</c> element claiming MCID 0. Only the page's content stream differs between
    /// the two fixtures.
    /// </summary>
    private static byte[] OnePagePdf(string pageContent) => BuildPdf(new List<string>
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> "
        + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /StructParents 0 /Tabs /S >>",

        // 4 — content
        StreamObject(pageContent),

        // 5 — font
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",

        // 6 — structure tree root
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",

        // 7 — the single paragraph, claiming MCID 0 on page 1
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0] >>",

        // 8 — the parent tree
        "<< /Nums [0 [7 0 R]] >>",
    });

    /// <summary>
    /// Three untagged text runs followed by a stray <c>&gt;</c>. The scanner stops at the
    /// delimiter and reports an error, so the page becomes unusable to every content-level check —
    /// while the text it draws is outside every marked-content sequence, which is precisely what
    /// UA17 exists to report.
    /// </summary>
    private static string UnscannableContent()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 3; i++)
        {
            sb.Append("BT /F1 12 Tf 72 ")
              .Append((700 - i * 16).ToString(CultureInfo.InvariantCulture))
              .Append(" Td (Untagged line) Tj ET\n");
        }
        return sb.Append(">\n").ToString();
    }

    private const string ScannableContent =
        "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Tagged line) Tj ET\nEMC\n";

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    /// <summary>
    /// The coverage notice, isolated from the other things UA99 is used for (a check that threw,
    /// and the encryption notice) by its message.
    /// </summary>
    private static IReadOnlyList<AccessibilityIssue> CoverageGaps(IEnumerable<AccessibilityIssue> issues)
        => issues.Where(i =>
                string.Equals(i.Code, "UA99", StringComparison.Ordinal) &&
                i.Message.IndexOf("content stream could not be read", StringComparison.Ordinal) >= 0)
           .ToList();

    private static string Describe(IEnumerable<AccessibilityIssue> issues)
        => string.Join(" | ", issues.Select(i => i.ToString()));

    // =====================================================================
    //  Fires
    // =====================================================================

    /// <summary>
    /// The defect this file guards against: a silent audit of this document, with
    /// <see cref="AccessibilityAuditReport.IsConforming"/> true for a page whose entire text is
    /// untagged.
    /// </summary>
    [Fact]
    public void UnscannablePage_IsNamedInTheReportWithTheChecksThatDidNotRun()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(OnePagePdf(UnscannableContent()));

        AccessibilityIssue gap = Assert.Single(CoverageGaps(issues));

        Assert.Equal(IssueSeverity.Info, gap.Severity);
        Assert.Equal(1, gap.PageNumber);
        Assert.Equal(3, gap.ObjectNumber);          // the page object

        // The reason must be the scanner's own, not a generic apology.
        Assert.Contains("Stray '>' delimiter", gap.Message, StringComparison.Ordinal);

        // …and it must name what was consequently not established.
        Assert.Contains("did not run for this page", gap.Message, StringComparison.Ordinal);
        Assert.Contains("UA14's marked-content mapping", gap.Message, StringComparison.Ordinal);
        Assert.Contains("UA17 (content drawn outside every marked-content sequence)",
            gap.Message, StringComparison.Ordinal);
        Assert.Contains("UA24 (image-only, probably scanned pages)", gap.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The notice is Info, exactly like the encryption one: the document has not been shown to be
    /// wrong, so it must not flip <see cref="AccessibilityAuditReport.IsConforming"/> by itself.
    /// What it must do is stop a clean report meaning "checked and fine".
    /// </summary>
    [Fact]
    public void UnscannablePage_CoverageNoticeIsInfoAndDoesNotInventErrors()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(OnePagePdf(UnscannableContent()));

        Assert.All(CoverageGaps(issues), i => Assert.Equal(IssueSeverity.Info, i.Severity));
    }

    // =====================================================================
    //  Does not fire
    // =====================================================================

    /// <summary>
    /// The other direction, which matters just as much: a page whose content scans cleanly must
    /// produce no coverage notice at all. A notice on every document would be as useless as none.
    /// </summary>
    [Fact]
    public void ScannablePage_ProducesNoCoverageNotice()
    {
        IReadOnlyList<AccessibilityIssue> issues = Check(OnePagePdf(ScannableContent));

        Assert.True(CoverageGaps(issues).Count == 0,
            "A page whose content scanned cleanly must not be reported as a coverage gap. Issues: " +
            Describe(issues));
    }

    /// <summary>
    /// Adversarial shape for the recording path itself: a content stream that is nothing but the
    /// unbounded construct. The audit must still terminate and still say which page it could not
    /// judge — one notice, not one per check that skipped the page.
    /// </summary>
    [Fact]
    public void PageWholeContentIsAnUnboundedConstruct_ProducesExactlyOneNotice()
    {
        // An unterminated literal string: the scanner runs to the end of the buffer and reports
        // the construct rather than looping or throwing.
        IReadOnlyList<AccessibilityIssue> issues = Check(OnePagePdf("BT /F1 12 Tf (never closed\n"));

        AccessibilityIssue gap = Assert.Single(CoverageGaps(issues));
        Assert.Equal(1, gap.PageNumber);
        Assert.Contains("did not run for this page", gap.Message, StringComparison.Ordinal);
    }
}
