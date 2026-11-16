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
/// UA15 — the quality of a Figure's <c>/Alt</c>, as opposed to its mere presence (which is UA07's
/// finding).
///
/// <para>
/// <b>Why this file exists.</b> <c>CheckFigureAltTextQuality</c> emits two distinct findings —
/// an empty <c>/Alt</c> and a generic-placeholder <c>/Alt</c> — and the whole test tree contained
/// exactly one mention of "UA15": a negative assertion in <c>EncryptedDocumentAuditTests</c> that
/// it must <i>not</i> be reported on an encrypted document. There was no positive fixture and no
/// does-not-fire fixture, so replacing the body of the check with <c>return;</c> left the suite
/// green, and so would a regression in the placeholder set or in the trimming of the alt string.
/// Every other check in that file has at least one fires / does-not-fire pair; this one now does
/// too.
/// </para>
///
/// <para>All fixtures are built in memory from ASCII bytes; nothing is read from disk.</para>
/// </summary>
public class FigureAltTextQualityTests
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
    /// One page, one <c>&lt;Document&gt;</c> (object 7) holding one <c>&lt;Figure&gt;</c>
    /// (<b>object 8</b>, on page 1 through its <c>/Pg</c>) whose <c>/Alt</c> entry is the only
    /// thing that varies between the cases below.
    /// </summary>
    private static byte[] FigurePdf(string altEntry) => BuildPdf(new List<string>
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> "
        + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Tabs /S >>",

        // 4 — content: one artifact sequence, so no content-level check has anything to say
        StreamObject("/Artifact BMC\nBT /F1 12 Tf 72 700 Td (Decorative) Tj ET\nEMC\n"),

        // 5 — font
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",

        // 6 — structure tree root
        "<< /Type /StructTreeRoot /K [7 0 R] >>",

        // 7 — document
        "<< /Type /StructElem /S /Document /K [8 0 R] >>",

        // 8 — the figure under test
        "<< /Type /StructElem /S /Figure /Pg 3 0 R " + altEntry + " >>",
    });

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    private static IReadOnlyList<AccessibilityIssue> WithCode(
        IEnumerable<AccessibilityIssue> issues, string code)
        => issues.Where(i => string.Equals(i.Code, code, StringComparison.Ordinal)).ToList();

    private static string Describe(IEnumerable<AccessibilityIssue> issues)
        => string.Join(" | ", issues.Select(i => i.ToString()));

    // =====================================================================
    //  Fires
    // =====================================================================

    /// <summary>
    /// An empty <c>/Alt</c> is only right for a purely decorative image, which belongs in an
    /// artifact rather than in a Figure — so it is reported, and located.
    /// </summary>
    [Fact]
    public void Ua15_EmptyAltString_IsReported()
    {
        AccessibilityIssue empty = Assert.Single(WithCode(Check(FigurePdf("/Alt ()")), "UA15"));

        Assert.Equal(IssueSeverity.Warning, empty.Severity);
        Assert.Contains("empty /Alt", empty.Message, StringComparison.Ordinal);
        Assert.Equal(8, empty.ObjectNumber);
        Assert.Equal(1, empty.PageNumber);
        Assert.Equal("1.1.1", empty.WcagCriterion);
    }

    /// <summary>
    /// Whitespace is not alt text. This is the case the check's <c>Trim()</c> exists for, and it
    /// would go unnoticed if the trimming were ever dropped.
    /// </summary>
    [Fact]
    public void Ua15_WhitespaceOnlyAltString_IsReportedAsEmpty()
    {
        AccessibilityIssue empty = Assert.Single(WithCode(Check(FigurePdf("/Alt (   )")), "UA15"));

        Assert.Contains("empty /Alt", empty.Message, StringComparison.Ordinal);
        Assert.Equal(8, empty.ObjectNumber);
    }

    /// <summary>
    /// A placeholder that a producer inserted so the file would pass a presence check. It conveys
    /// nothing, so it is reported separately from the empty case and quotes what it found.
    /// </summary>
    [Fact]
    public void Ua15_GenericPlaceholderAltText_IsReported()
    {
        AccessibilityIssue generic = Assert.Single(WithCode(Check(FigurePdf("/Alt (image)")), "UA15"));

        Assert.Equal(IssueSeverity.Warning, generic.Severity);
        Assert.Contains("generic placeholder alt text", generic.Message, StringComparison.Ordinal);
        Assert.Contains("\"image\"", generic.Message, StringComparison.Ordinal);
        Assert.Equal(8, generic.ObjectNumber);
        Assert.Equal(1, generic.PageNumber);
    }

    /// <summary>The placeholder set is matched case-insensitively; "Figure" is no better than "figure".</summary>
    [Fact]
    public void Ua15_PlaceholderAltTextInAnotherCase_IsStillReported()
    {
        AccessibilityIssue generic = Assert.Single(WithCode(Check(FigurePdf("/Alt (Figure)")), "UA15"));

        Assert.Contains("generic placeholder alt text", generic.Message, StringComparison.Ordinal);
    }

    // =====================================================================
    //  Does not fire
    // =====================================================================

    /// <summary>
    /// Real alt text must be silent. A check that fires on a correct document is worse than one
    /// that never fires, because the false positive is what teaches people to ignore the report.
    /// </summary>
    [Fact]
    public void Ua15_MeaningfulAltText_IsSilent()
    {
        var issues = Check(FigurePdf("/Alt (A bar chart of quarterly revenue)"));

        Assert.True(WithCode(issues, "UA15").Count == 0,
            "UA15 must not fire on a Figure with real alt text. Issues: " + Describe(issues));
    }

    /// <summary>
    /// A Figure with no <c>/Alt</c> at all is UA07's finding, not UA15's. Reporting it here as well
    /// would double-count one defect and give the two codes overlapping meanings.
    /// </summary>
    [Fact]
    public void Ua15_MissingAltEntirely_IsLeftToUa07()
    {
        var issues = Check(FigurePdf(""));

        Assert.True(WithCode(issues, "UA15").Count == 0,
            "A missing /Alt is UA07's finding; UA15 judges the quality of one that is present. " +
            "Issues: " + Describe(issues));
    }
}
