// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The remediation pipeline audits against the part it is asked to <b>declare</b>.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> Both of <c>RemediateWithReport</c>'s audits called the private
/// <c>AuditBytes</c> overload without a target, so they ran PDF/UA-1 unconditionally — while
/// <see cref="RemediationOptions.PdfUaPart"/> could be set to 2 and would write a PDF/UA-2
/// identifier. A caller asking for a PDF/UA-2 declaration therefore got one over a document that
/// had only ever been judged against PDF/UA-1, and a report whose "after" audit pronounced it
/// conforming.</para>
///
/// <para><b>Why that is the serious kind of wrong.</b> The two parts are not nested: ISO 14289-2
/// prohibits things ISO 14289-1 permits — the bare <c>H</c> heading type and the <c>Note</c>
/// structure type among them — so a clean PDF/UA-1 audit is not evidence about PDF/UA-2 even
/// informally. The file already refuses to stamp <c>pdfuaid:part</c> onto a hollow structure tree
/// for exactly this reason: declaring conformance that was never verified is a misstatement baked
/// into the document. This is that rule applied to the part number.</para>
///
/// <para><b>What makes these tests able to fail</b> is that the two checkers emit disjoint code
/// spaces — PDF/UA-1 issues are <c>UA00</c>–<c>UA30</c>, PDF/UA-2's are <c>UA2-…</c> — so which
/// checker ran is visible in the report rather than inferred.</para>
/// </remarks>
public class RemediationTargetPropagationTests
{
    private static RemediationReport Remediate(byte[] pdf, RemediationOptions options)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        return PdfAccessibility.RemediateWithReport(input, output, options);
    }

    private static bool HasUa2Codes(AccessibilityAuditReport report) =>
        report.Issues.Any(i => i.Code != null && i.Code.StartsWith("UA2-", System.StringComparison.Ordinal));

    // =====================================================================
    //  Declaring part 2 audits against part 2
    // =====================================================================

    /// <summary>
    /// Asking for a PDF/UA-2 declaration makes both audits PDF/UA-2 audits.
    /// </summary>
    [Fact]
    public void RemediateWithReport_DeclaringPart2_AuditsAgainstPart2()
    {
        RemediationReport report = Remediate(
            MinimalPdfBuilder.CreateMultiPagePdf(2),
            new RemediationOptions { PdfUaPart = 2 });

        Assert.Equal(PdfUaVersion.Ua2, report.BeforeAudit.ConformanceTarget);
        Assert.Equal(PdfUaVersion.Ua2, report.AfterAudit.ConformanceTarget);
    }

    /// <summary>
    /// And it is the PDF/UA-2 <i>checker</i> that ran, not the PDF/UA-1 one relabelled — asserted
    /// on the codes, which the two do not share.
    /// </summary>
    [Fact]
    public void RemediateWithReport_DeclaringPart2_RunsTheUa2Checks()
    {
        RemediationReport report = Remediate(
            MinimalPdfBuilder.CreateMultiPagePdf(2),
            new RemediationOptions { PdfUaPart = 2 });

        Assert.True(HasUa2Codes(report.BeforeAudit),
                    "the before-audit carries no UA2- codes: " +
                    string.Join(", ", report.BeforeAudit.Issues.Select(i => i.Code).Distinct()));
    }

    // =====================================================================
    //  The default is unchanged
    // =====================================================================

    /// <summary>
    /// The control: default options still audit against PDF/UA-1, so this change moves nothing for
    /// a caller who did not ask for part 2.
    /// </summary>
    [Fact]
    public void RemediateWithReport_ByDefault_AuditsAgainstPart1()
    {
        RemediationReport report = Remediate(
            MinimalPdfBuilder.CreateMultiPagePdf(2), new RemediationOptions());

        Assert.Equal(PdfUaVersion.Ua1, report.BeforeAudit.ConformanceTarget);
        Assert.False(HasUa2Codes(report.BeforeAudit),
                     "a default run produced UA2- codes, so the PDF/UA-2 checker ran unasked");
    }

    // =====================================================================
    //  Target overrides, in both directions
    // =====================================================================

    /// <summary>
    /// <see cref="RemediationOptions.Target"/> set explicitly wins over the declared part — which is
    /// a legitimate thing to want: judging against the stricter part while declaring nothing.
    /// </summary>
    [Fact]
    public void RemediateWithReport_WithAnExplicitTarget_UsesItRatherThanThePart()
    {
        RemediationReport report = Remediate(
            MinimalPdfBuilder.CreateMultiPagePdf(2),
            new RemediationOptions { PdfUaPart = 1, Target = PdfUaVersion.Ua2 });

        Assert.Equal(PdfUaVersion.Ua2, report.BeforeAudit.ConformanceTarget);
    }

    /// <summary>The other direction: declaring 2 while deliberately auditing against 1.</summary>
    [Fact]
    public void RemediateWithReport_WithATargetBelowTheDeclaredPart_HonoursTheTarget()
    {
        RemediationReport report = Remediate(
            MinimalPdfBuilder.CreateMultiPagePdf(2),
            new RemediationOptions { PdfUaPart = 2, Target = PdfUaVersion.Ua1 });

        Assert.Equal(PdfUaVersion.Ua1, report.BeforeAudit.ConformanceTarget);
    }

    /// <summary>
    /// <see cref="PdfUaVersion.Auto"/> is resolved per audit from the document's own declaration,
    /// and never reaches the report as <c>Auto</c> — a report must name the part it judged.
    /// </summary>
    [Fact]
    public void RemediateWithReport_WithAutoTarget_ResolvesToARealPart()
    {
        RemediationReport report = Remediate(
            MinimalPdfBuilder.CreateMultiPagePdf(2),
            new RemediationOptions { Target = PdfUaVersion.Auto });

        Assert.NotEqual(PdfUaVersion.Auto, report.BeforeAudit.ConformanceTarget);
        Assert.NotEqual(PdfUaVersion.Auto, report.AfterAudit.ConformanceTarget);
    }
}
