// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Document;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>Tests for PdfAccessibility.Audit and the AccessibilityAuditReport model.</summary>
public class PdfAccessibilityAuditTests
{
    // ---------------------------------------------------------------
    //  Minimal in-memory PDF helpers
    // ---------------------------------------------------------------

    /// <summary>Minimal untagged PDF — no /Lang, no /MarkInfo, no /StructTreeRoot.</summary>
    internal static byte[] MinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", off1);
        sb.AppendFormat("{0:D10} 00000 n \n", off2);
        sb.AppendFormat("{0:D10} 00000 n \n", off3);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary><see cref="MinimalPdf"/> with a page that shows one string in Helvetica.</summary>
    internal static byte[] MinimalPdfWithText()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>\nendobj\n");
        int off4 = sb.Length;
        sb.Append("4 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int off in new[] { off1, off2, off3, off4 }) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Minimal tagged PDF with /Lang, /MarkInfo, /StructTreeRoot, and /Info /Title.
    /// Represents a near-conformant PDF/UA-1 document.
    /// </summary>
    internal static byte[] TaggedPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int off1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> /StructTreeRoot 4 0 R >>\nendobj\n");
        int off2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int off4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /StructTreeRoot /K [5 0 R] /ParentTreeNextKey 0 >>\nendobj\n");
        int off5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /StructElem /S /Document /P 4 0 R >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", off1);
        sb.AppendFormat("{0:D10} 00000 n \n", off2);
        sb.AppendFormat("{0:D10} 00000 n \n", off3);
        sb.AppendFormat("{0:D10} 00000 n \n", off4);
        sb.AppendFormat("{0:D10} 00000 n \n", off5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---------------------------------------------------------------
    //  Null-guard tests
    // ---------------------------------------------------------------

    [Fact]
    public void Audit_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PdfAccessibility.Audit((Stream)null));
    }

    [Fact]
    public void Audit_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PdfAccessibility.Audit((string)null));
    }

    // ---------------------------------------------------------------
    //  Audit results on minimal PDF
    // ---------------------------------------------------------------

    /// <summary>
    /// An untagged PDF with no /Lang and no XMP must report the specific things that are wrong
    /// with it, not merely "some issues". Naming the codes is what fails if the audit starts
    /// reporting a different defect, or stops reporting one of these. The language (UA02) is owed
    /// by text: the same page showing a string is charged with it, the empty page is not —
    /// ISO 14289-1 §7.2 asks that the language of text be declared, and this page has none.
    /// </summary>
    [Fact]
    public void Audit_MinimalUntaggedPdf_ReportsTheMissingTagStructureAndTitle_AndTheLanguageOnceItHasText()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(MinimalPdf()));
        var withText = PdfAccessibility.Audit(new MemoryStream(MinimalPdfWithText()));

        var codes = report.Issues.Select(i => i.Code).Distinct().ToList();
        Assert.Contains("UA01", codes);   // not marked as tagged
        Assert.DoesNotContain("UA02", codes);   // no text, so no language owed
        Assert.Contains(withText.Issues, i => i.Code == "UA02");   // its text has no /Lang
        Assert.Contains("UA03", codes);   // no XMP metadata
        Assert.Contains("UA04", codes);   // no title
        Assert.Contains("UA05", codes);   // no structure tree

        // Every one of them blocks conformance.
        Assert.All(report.Issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
        Assert.False(report.IsConforming);
    }

    [Fact]
    public void Audit_MinimalPdf_HasErrorsForMissingStructure()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(MinimalPdf()));
        Assert.True(report.ErrorCount > 0,
            "Untagged PDF without /Lang should have at least one error.");
    }

    [Fact]
    public void Audit_MinimalPdf_IsNotConforming()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(MinimalPdf()));
        Assert.False(report.IsConforming);
    }

    /// <summary>
    /// The predecessor's comment said a tagged PDF "should have fewer errors than an untagged one"
    /// and then asserted nothing at all. This asserts it — and names what the tagged document
    /// stops being accused of (UA01/UA05, the tagging pair) versus what it is still accused of.
    /// </summary>
    [Fact]
    public void Audit_TaggedPdf_HasFewerErrorsAndNoLongerReportsTheTaggingCodes()
    {
        var untagged = PdfAccessibility.Audit(new MemoryStream(MinimalPdfWithText()));
        var tagged   = PdfAccessibility.Audit(new MemoryStream(TaggedPdf()));

        Assert.True(tagged.ErrorCount < untagged.ErrorCount,
            $"tagged={tagged.ErrorCount} should be fewer than untagged={untagged.ErrorCount}");

        var taggedCodes = tagged.Issues.Select(i => i.Code).Distinct().ToList();
        Assert.DoesNotContain("UA01", taggedCodes);   // it *is* marked as tagged
        Assert.DoesNotContain("UA05", taggedCodes);   // it *does* have a structure tree

        // ...but a hollow tree is still called out, so this is not simply "tagged == fine".
        Assert.Contains("UA16", taggedCodes);
    }

    // ---------------------------------------------------------------
    //  Report grouping / model properties
    // ---------------------------------------------------------------

    /// <summary>
    /// The grouping must be a real partition of <c>Issues</c>: every issue in exactly one group,
    /// and every group keyed by the category its members actually carry. "Not null" is satisfied
    /// by an empty dictionary on a report full of issues.
    /// </summary>
    [Fact]
    public void AuditReport_ByCategory_PartitionsEveryIssueUnderItsOwnCategory()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(MinimalPdf()));

        Assert.NotEmpty(report.ByCategory);
        Assert.Equal(report.Issues.Count, report.ByCategory.Sum(g => g.Value.Count));

        foreach (var group in report.ByCategory)
            Assert.All(group.Value, issue => Assert.Equal(group.Key, issue.Category));
    }

    /// <summary>
    /// <c>ByPage</c> groups only the issues that name a page. Every finding on this fixture is
    /// document-level (no /Lang, no structure tree, no XMP), so the grouping is legitimately
    /// <b>empty</b> while <c>Issues</c> is not — which is exactly the state a "not null" assertion
    /// cannot distinguish from a grouping that silently drops everything.
    /// </summary>
    [Fact]
    public void AuditReport_ByPage_GroupsOnlyPageScopedIssues()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(MinimalPdf()));

        Assert.NotEmpty(report.Issues);
        Assert.Equal(report.Issues.Count(i => i.PageNumber.HasValue),
                     report.ByPage.Sum(g => g.Value.Count));

        foreach (var group in report.ByPage)
            Assert.All(group.Value, issue => Assert.Equal(group.Key, issue.PageNumber));
    }

    [Fact]
    public void AuditReport_Counts_SumToTotal()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(MinimalPdf()));
        int sum = report.ErrorCount + report.WarningCount + report.InfoCount;
        Assert.Equal(report.Issues.Count, sum);
    }

    [Fact]
    public void AuditReport_IssueToString_ContainsCode()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(MinimalPdf()));
        if (report.Issues.Count > 0)
        {
            string s = report.Issues[0].ToString();
            Assert.Contains(report.Issues[0].Code, s);
        }
    }

    // ---------------------------------------------------------------
    //  AccessibilityIssue model
    // ---------------------------------------------------------------

    [Fact]
    public void AccessibilityIssue_Constructor_SetsProperties()
    {
        var issue = new AccessibilityIssue(
            IssueSeverity.Error, "UA01", "Not tagged", "Add /MarkInfo",
            IssueCategory.Tagging, pageNumber: 1, wcagLevel: WcagLevel.A);

        Assert.Equal(IssueSeverity.Error,   issue.Severity);
        Assert.Equal("UA01",                 issue.Code);
        Assert.Equal("Not tagged",           issue.Message);
        Assert.Equal("Add /MarkInfo",        issue.Remediation);
        Assert.Equal(IssueCategory.Tagging,  issue.Category);
        Assert.Equal(1,                      issue.PageNumber);
        Assert.Equal(WcagLevel.A,            issue.WcagLevel);
    }

    [Fact]
    public void AccessibilityIssue_NullCode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AccessibilityIssue(IssueSeverity.Error, null, "msg", "", IssueCategory.Structure));
    }

    [Fact]
    public void AccessibilityIssue_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AccessibilityIssue(IssueSeverity.Error, "UA01", null, "", IssueCategory.Structure));
    }

    [Fact]
    public void AccessibilityIssue_WithPage_ToStringContainsPageNumber()
    {
        var issue = new AccessibilityIssue(IssueSeverity.Warning, "UA07", "No alt", "",
            IssueCategory.Images, pageNumber: 3);
        Assert.Contains("p.3", issue.ToString());
    }

    [Fact]
    public void AccessibilityIssue_WithoutPage_ToStringDoesNotContainPagePrefix()
    {
        var issue = new AccessibilityIssue(IssueSeverity.Error, "UA02", "No lang", "",
            IssueCategory.Language);
        Assert.DoesNotContain("p.", issue.ToString());
    }

    // ---------------------------------------------------------------
    //  Remediate (pipeline)
    // ---------------------------------------------------------------

    [Fact]
    public void Remediate_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.Remediate(null, new MemoryStream()));
    }

    [Fact]
    public void Remediate_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.Remediate(new MemoryStream(MinimalPdf()), null));
    }

    /// <summary>
    /// Remediation must leave the document measurably better: the language reaches the catalog and
    /// a re-audit of the produced bytes reports strictly fewer errors, with the two codes the
    /// caller supplied the inputs for (UA02 language, UA04 title) gone.
    ///
    /// <para>What remains is equally deliberate and is asserted too: this fixture's page draws no
    /// content, so there is nothing to tag, and the pipeline refuses to claim otherwise — UA01,
    /// UA03 and UA05 stay open rather than being papered over.</para>
    /// </summary>
    [Fact]
    public void Remediate_MinimalPdf_SetsTheLanguageAndClosesTheLanguageAndTitleFindings()
    {
        byte[] source = MinimalPdf();
        var before = PdfAccessibility.Audit(new MemoryStream(source));

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfAccessibility.Remediate(input, output, new RemediationOptions
        {
            Language = "en-US",
            Title    = "Test Document",
        });

        byte[] produced = output.ToArray();
        Assert.NotEqual(source, produced);

        using (var doc = PdfDocument.Open(new MemoryStream(produced)))
            Assert.Equal("en-US",
                (doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Lang]) as PdfString)?.GetText());

        var after = PdfAccessibility.Audit(new MemoryStream(produced));
        Assert.True(after.ErrorCount < before.ErrorCount,
            $"remediation should reduce the error count, {before.ErrorCount} -> {after.ErrorCount}");

        var afterCodes = after.Issues.Select(i => i.Code).Distinct().ToList();
        Assert.DoesNotContain("UA02", afterCodes);   // language supplied
        Assert.DoesNotContain("UA04", afterCodes);   // title supplied

        // Honestly still open: the page has no content, so it cannot be tagged and is not claimed
        // to be. See GAPS.md 5c.
        Assert.Contains("UA01", afterCodes);
        Assert.Contains("UA05", afterCodes);
    }

    /// <summary>
    /// With every step disabled the document must come through <b>byte for byte</b>. That is the
    /// strongest statement available here and the one that matters: a pipeline that appended an
    /// empty incremental revision, or re-serialised the file, would still "produce output" and
    /// still pass the predecessor's <c>output.Length &gt; 0</c>.
    /// </summary>
    [Fact]
    public void Remediate_WithNoStepsEnabled_CopiesTheDocumentThroughUnchanged()
    {
        byte[] source = MinimalPdf();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfAccessibility.Remediate(input, output, new RemediationOptions
        {
            Language          = null,
            Title             = null,
            AutoTagIfUntagged = false,
            SetFigureAltText  = false,
            FixHeadingHierarchy = false,
            MarkArtifacts     = false,
            FixReadingOrder   = false,
        });

        Assert.Equal(source, output.ToArray());
    }

    [Fact]
    public void RemediationOptions_Defaults_AreReasonable()
    {
        var opts = new RemediationOptions();
        Assert.Equal("en-US", opts.Language);
        Assert.True(opts.AutoTagIfUntagged);
        Assert.True(opts.SetFigureAltText);
        Assert.True(opts.FixHeadingHierarchy);
        Assert.True(opts.MarkArtifacts);
        Assert.True(opts.FixReadingOrder);
        Assert.Equal(50f, opts.HeaderMarginPt);
        Assert.Equal(50f, opts.FooterMarginPt);
    }
}
