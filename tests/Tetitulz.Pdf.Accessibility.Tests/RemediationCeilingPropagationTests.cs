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
/// The remediation pipeline's two audits run under the <b>caller's</b> content ceilings.
///
/// <para><b>The defect.</b> <c>PdfAccessibility.RemediateWithReport</c> called the private
/// <c>AuditBytes(byte[])</c> overload for both the before- and the after-audit, and that overload
/// hardwires <see cref="PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes"/> (8 MiB) and
/// <see cref="PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes"/> (128 MiB).
/// <see cref="RemediationOptions"/> carried no ceiling at all, so there was no way to say
/// otherwise: a host that had raised the limits for its own
/// <see cref="PdfAccessibility.Audit(Stream,int,long)"/> call — the additive overload that exists
/// for exactly that — still got a pipeline report computed at the defaults. Its oversized pages
/// were <i>refused</i> rather than examined, and the after-audit that decides whether remediation
/// worked was measuring a document it had only partly read.</para>
///
/// <para><b>Why a refusal is not visible without these tests.</b> A refused page is named in a
/// UA99 <b>Info</b> issue, never dropped in silence — that is the honesty rule the whole feature
/// is built on — but Info does not flip
/// <see cref="AccessibilityAuditReport.IsConforming"/> (deliberately: "not established" is not
/// "wrong", and every site that emits one of these says so). So the difference between "this page
/// was checked and is clean" and "this page was never read" is entirely in the issue list. That is
/// what is asserted below, from both directions: the ceiling test asserts the page is named
/// <i>and</i> that the content-level finding it would have produced is absent; the control asserts
/// the same document under the defaults produces that finding. Asserting only the absence of the
/// UA99 would pass with page content never scanned at all.</para>
/// </summary>
public class RemediationCeilingPropagationTests
{
    // The image-bearing page's content is padded well past this, and nowhere near the 8 MiB
    // default — so this number can only reach the audit through RemediationOptions.
    private const int LowPageCeiling = 1_200;

    // =====================================================================
    //  1. RemediationOptions.MaxPageContentBytes reaches BOTH audits
    // =====================================================================

    /// <summary>
    /// A page over the caller's per-page ceiling is named — with that caller's number — in the
    /// before-audit <b>and</b> in the after-audit.
    /// </summary>
    /// <remarks>
    /// <see cref="Assert.NotSame(object,object)"/> on the two reports is load-bearing: when the
    /// pipeline changes nothing, <c>RemediateWithReport</c> reuses the before-audit object for the
    /// after-audit, and "both audits honour the ceiling" would then be one audit asserted twice.
    /// </remarks>
    [Fact]
    public void RemediateWithReport_WithALowPageCeiling_NamesTheRefusedPageInBothAudits()
    {
        byte[] pdf = ImageAndTextPagesPdf();

        RemediationReport report = Remediate(pdf, new RemediationOptions
        {
            MaxPageContentBytes = LowPageCeiling,
        });

        Assert.NotSame(report.BeforeAudit, report.AfterAudit);

        foreach (AccessibilityAuditReport audit in new[] { report.BeforeAudit, report.AfterAudit })
        {
            AccessibilityIssue gap = Assert.Single(CoverageGaps(audit));

            Assert.Equal(IssueSeverity.Info, gap.Severity);   // not established is not "wrong"
            Assert.Equal(1, gap.PageNumber);                  // the oversized page, named
            Assert.Contains(LowPageCeiling.ToString(CultureInfo.InvariantCulture),
                            gap.Message, StringComparison.Ordinal);
            Assert.Contains("did not run for this page", gap.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The other half of the same claim: the content-level check the UA99 says did not run really
    /// did not run. Page 1 draws an image and no text, which is UA24 — and under the low ceiling no
    /// UA24 is reported for it, because its content was never scanned.
    /// </summary>
    [Fact]
    public void RemediateWithReport_WithALowPageCeiling_DoesNotReportTheContentLevelFindingForThatPage()
    {
        byte[] pdf = ImageAndTextPagesPdf();

        RemediationReport report = Remediate(pdf, new RemediationOptions
        {
            MaxPageContentBytes = LowPageCeiling,
        });

        Assert.DoesNotContain(report.BeforeAudit.Issues, IsImageOnlyPageOne);
        Assert.DoesNotContain(report.AfterAudit.Issues,  IsImageOnlyPageOne);
    }

    /// <summary>
    /// The control that makes the two tests above mean anything: the <b>same document</b> under the
    /// default ceilings is examined in full — no coverage notice, and the UA24 finding that only a
    /// scanned page can produce.
    /// </summary>
    /// <remarks>
    /// Without this, "refuse every page" would satisfy both tests above. This is what separates
    /// "refused" from "examined and clean" — the distinction the ceilings exist to preserve and the
    /// one a caller loses when the ceiling it configured is discarded.
    /// </remarks>
    [Fact]
    public void RemediateWithReport_WithTheDefaultCeilings_ExaminesTheSamePageInFull()
    {
        byte[] pdf = ImageAndTextPagesPdf();

        RemediationReport report = Remediate(pdf, new RemediationOptions());

        Assert.Empty(CoverageGaps(report.BeforeAudit));
        Assert.Empty(CoverageGaps(report.AfterAudit));
        Assert.Contains(report.BeforeAudit.Issues, IsImageOnlyPageOne);
    }

    // =====================================================================
    //  2. RemediationOptions.MaxDocumentContentBytes reaches the audits too
    // =====================================================================

    /// <summary>
    /// The document-wide budget is threaded as well, and it is a separate value: a budget spent by
    /// page 1 refuses page 2 with the budget as the stated reason.
    /// </summary>
    /// <remarks>
    /// <para>Both ceilings are needed. The per-page one alone bounds nothing across a document —
    /// a thousand pages just under it is the same cost with an extra loop — so a host raising one
    /// normally raises the other, and a fix that threaded only <c>MaxPageContentBytes</c> would
    /// leave the second half of the row open.</para>
    /// <para>The after-audit is asserted only for the presence of a budget refusal, not for the
    /// page number: remediation appends marked content, so the tagged document spends the same
    /// budget at a different page.</para>
    /// </remarks>
    [Fact]
    public void RemediateWithReport_WithALowDocumentBudget_NamesThePageTheBudgetCut()
    {
        string firstPage = ImagePageContent();
        byte[] pdf = ImageAndTextPagesPdf();

        // Exactly one page's worth: page 1 fits precisely and spends it to zero, so page 2 is
        // never decoded at all.
        RemediationReport report = Remediate(pdf, new RemediationOptions
        {
            MaxDocumentContentBytes = firstPage.Length,
        });

        AccessibilityIssue gap = Assert.Single(BudgetGaps(report.BeforeAudit));
        Assert.Equal(IssueSeverity.Info, gap.Severity);
        Assert.Equal(2, gap.PageNumber);
        Assert.Contains(firstPage.Length.ToString(CultureInfo.InvariantCulture),
                        gap.Message, StringComparison.Ordinal);

        Assert.NotEmpty(BudgetGaps(report.AfterAudit));
    }

    /// <summary>
    /// The negative control for the budget: the same document under the default budget refuses no
    /// page for want of it.
    /// </summary>
    [Fact]
    public void RemediateWithReport_WithTheDefaultBudget_RefusesNoPageForWantOfIt()
    {
        RemediationReport report = Remediate(ImageAndTextPagesPdf(), new RemediationOptions());

        Assert.Empty(BudgetGaps(report.BeforeAudit));
        Assert.Empty(BudgetGaps(report.AfterAudit));
    }

    // =====================================================================
    //  3. The defaults are the checker's own, so nothing changed for callers
    // =====================================================================

    /// <summary>
    /// A caller that sets neither property gets exactly the ceilings the pipeline hardwired before,
    /// so this is an additive change and not a behaviour change.
    /// </summary>
    /// <remarks>
    /// Defaulting these to <c>0</c> — "disabled", the checker's documented meaning for a
    /// non-positive value — would have turned a DoS ceiling off for every existing caller while
    /// every other test in this file still passed. That is the mistake this pins.
    /// </remarks>
    [Fact]
    public void RemediationOptions_DefaultsToTheDeepCheckersOwnCeilings()
    {
        var options = new RemediationOptions();

        Assert.Equal(PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes, options.MaxPageContentBytes);
        Assert.Equal(PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes, options.MaxDocumentContentBytes);
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static RemediationReport Remediate(byte[] pdf, RemediationOptions options)
    {
        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        return PdfAccessibility.RemediateWithReport(input, output, options);
    }

    /// <summary>The per-page refusal notice, isolated from UA99's other uses by its message.</summary>
    private static IReadOnlyList<AccessibilityIssue> CoverageGaps(AccessibilityAuditReport audit)
        => audit.Issues.Where(i =>
                string.Equals(i.Code, "UA99", StringComparison.Ordinal) &&
                i.Message.IndexOf("content stream could not be read", StringComparison.Ordinal) >= 0)
           .ToList();

    /// <summary>The document-wide budget refusal, which carries its own wording.</summary>
    private static IReadOnlyList<AccessibilityIssue> BudgetGaps(AccessibilityAuditReport audit)
        => audit.Issues.Where(i =>
                string.Equals(i.Code, "UA99", StringComparison.Ordinal) &&
                i.Message.IndexOf("already spent by earlier pages", StringComparison.Ordinal) >= 0)
           .ToList();

    /// <summary>
    /// UA24 for page 1 — "draws an image and no text". It can only be produced from a scanned
    /// content stream, which is exactly why it is the positive evidence used here.
    /// </summary>
    private static bool IsImageOnlyPageOne(AccessibilityIssue issue)
        => string.Equals(issue.Code, "UA24", StringComparison.Ordinal) && issue.PageNumber == 1;

    /// <summary>
    /// Page 1's content: one image XObject invocation and no text at all, padded with balanced
    /// <c>q</c>/<c>Q</c> pairs well past <see cref="LowPageCeiling"/>. The padding draws nothing, so
    /// it cannot change the UA24 verdict — it only changes the page's size.
    /// </summary>
    private static string ImagePageContent()
    {
        var sb = new StringBuilder("q 200 0 0 200 100 400 cm /Im0 Do Q\n");
        while (sb.Length <= LowPageCeiling + 500) sb.Append("q Q\n");
        return sb.ToString();
    }

    /// <summary>
    /// Two pages: an oversized image-only page 1, and a small text page 2 that gives the
    /// auto-tagger something to do — without which the pipeline would change nothing and the
    /// after-audit would be the before-audit object.
    /// </summary>
    private static byte[] ImageAndTextPagesPdf() => BuildPdf(new List<string>
    {
        // 1 — catalog. Deliberately untagged, so the pipeline's AutoTag step runs and the
        //     after-audit is a genuinely separate audit of a different document.
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",

        // 3 — page 1: image only, oversized
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /XObject << /Im0 7 0 R >> >> /Contents 4 0 R >>",

        // 4 — page 1 content
        StreamObject(ImagePageContent()),

        // 5 — page 2: a little text, comfortably inside every ceiling here
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 8 0 R >> >> /Contents 6 0 R >>",

        // 6 — page 2 content
        StreamObject("BT /F1 12 Tf 72 700 Td (Page two of the fixture) Tj ET\n"),

        // 7 — image XObject. Only its dictionary is read here; the samples are never decoded.
        "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray "
        + "/BitsPerComponent 8 /Length 1 >>\nstream\n \nendstream",

        // 8 — font, deliberately not embedded
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    });

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
}
