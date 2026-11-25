// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Tests for the tab-order remediation step and for the honesty of the pipeline's skip reasons.
///
/// <para>
/// Two defects are pinned here. First, the audit emitted <b>UA09</b> ("pages with annotations
/// must declare /Tabs /S") together with the advice "set /Tabs /S on every page that has
/// annotations", while no step of <c>Remediate</c> / <c>RemediateWithReport</c> could do it — the
/// only implementation lived in a separate conformance plugin — so a remediated annotated
/// document failed UA09 for ever. Second, a step that did nothing could say so in words that
/// overstated the document's state: <c>SetDocumentMetadata</c> reported "the catalog and /Info
/// already carry the requested values" when the caller had supplied no title at all, while the
/// same run's audit was erroring UA04 for the missing title.
/// </para>
///
/// <para>Every document is assembled in memory as ASCII bytes; nothing is read from disk, and
/// every assertion about the output re-opens it as a PDF rather than grepping its bytes.</para>
/// </summary>
public class TabOrderRemediationTests
{
    // =====================================================================
    //  Fixtures
    // =====================================================================

    /// <summary>Assembles a classic-xref PDF; bodies[0] is object 1 and is always the catalog.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    /// <summary>
    /// A single-page tagged document. <paramref name="pageExtras"/> is spliced into the page
    /// dictionary, so one fixture covers "annotated", "annotated and already tabbed", "annotated
    /// with the legacy /TabOrder key" and "no annotations at all". Object 9 is always the
    /// annotation, referenced or not, so object numbering never shifts between variants.
    /// </summary>
    private static byte[] TaggedPdf(string pageExtras)
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
                + pageExtras + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (Body) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [7 0 R]] >>",
            // PrinterMark: an annotation for UA09's purposes, but exempt from the
            // structure-tree and accessible-description rules, so it adds no unrelated issue.
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 20 20] >>",
        });

    /// <summary>
    /// Annotated, no tab order declared — the input UA09 is reported for.
    /// <para><c>internal</c> rather than <c>private</c> so the encrypted-route tests can use the
    /// same document. A second hand-built annotated fixture would be a second thing to keep in
    /// step with what <c>SetStructureTabOrder</c> actually looks for.</para>
    /// </summary>
    internal static byte[] AnnotatedPdf() => TaggedPdf("/Annots [9 0 R] ");

    /// <summary>Annotated and already conforming.</summary>
    private static byte[] AnnotatedAndTabbedPdf() => TaggedPdf("/Annots [9 0 R] /Tabs /S ");

    /// <summary>Annotated, carrying only the non-spec /TabOrder key earlier versions wrote.</summary>
    private static byte[] LegacyTabOrderPdf() => TaggedPdf("/Annots [9 0 R] /TabOrder /S ");

    /// <summary>No annotations, so PDF/UA-1 does not require /Tabs on this page.</summary>
    private static byte[] UnannotatedPdf() => TaggedPdf(string.Empty);

    /// <summary>Two pages, only the first of which carries an annotation.</summary>
    private static byte[] MixedPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
                + "/Annots [10 0 R] /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (One) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 11 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Type /StructTreeRoot /K [8 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [8 0 R]] >>",
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 20 20] >>",
            StreamObject("", "/Artifact <</Type /Pagination /Subtype /Footer>> BDC\n"
                           + "BT /F1 9 Tf 72 30 Td (2) Tj ET\nEMC\n"),
        });

    /// <summary>An untagged page, so the pipeline's AutoTag step actually runs.</summary>
    private static byte[] UntaggedPdf()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 18 Tf 72 700 Td (Heading) Tj ET\n"
                           + "BT /F1 10 Tf 72 400 Td (Body text on the page.) Tj ET\n"
                           + "BT /F1 8 Tf 72 30 Td (Page 1) Tj ET\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static byte[] SetTabOrder(byte[] input, out int changed)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        changed = PdfAccessibility.SetStructureTabOrder(inMs, outMs);
        return outMs.ToArray();
    }

    /// <summary>
    /// Re-opens a produced document and reads a name-valued key off a page dictionary. Reading
    /// the parsed page — rather than searching the bytes for "/Tabs /S" — is the only assertion
    /// that proves a reader would see the key: a byte scan cannot tell the current revision of
    /// the page object from a superseded one.
    /// </summary>
    private static string ReadPageName(byte[] pdf, int pageIndex, string key)
    {
        using var ms  = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        PdfDictionary page = doc.Pages[pageIndex].Dictionary;
        Assert.NotNull(page);

        PdfObject value = page[new PdfName(key)];
        if (value == null) return null;

        var name = value as PdfName ?? doc.ResolveObject(value) as PdfName;
        return name?.Value;
    }

    private static RemediationReport Remediate(byte[] input, RemediationOptions options)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        return PdfAccessibility.RemediateWithReport(inMs, outMs, options);
    }

    private static RemediationStepResult Step(RemediationReport report, string name)
    {
        List<RemediationStepResult> matches = report.Steps.Where(s => s.Name == name).ToList();
        Assert.True(matches.Count == 1,
            "expected exactly one '" + name + "' step, found " +
            matches.Count.ToString(CultureInfo.InvariantCulture) + " in: " +
            string.Join(", ", report.Steps.Select(s => s.Name)));
        return matches[0];
    }

    /// <summary>The four categories a skip reason is allowed to claim, plus the write-failure one.</summary>
    private static readonly string[] SkipCategories =
    {
        "not requested",
        "already satisfied",
        "not applicable",
        "superseded",
        "nothing could be written",
    };

    // =====================================================================
    //  SetStructureTabOrder — the operation
    // =====================================================================

    [Fact]
    public void SetStructureTabOrder_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetStructureTabOrder(null, new MemoryStream()));
    }

    [Fact]
    public void SetStructureTabOrder_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetStructureTabOrder(new MemoryStream(AnnotatedPdf()), null));
    }

    [Fact]
    public void SetStructureTabOrder_PageWithAnnotations_GainsTabsS()
    {
        byte[] output = SetTabOrder(AnnotatedPdf(), out int changed);

        Assert.Equal(1, changed);
        Assert.Equal("S", ReadPageName(output, 0, "Tabs"));
    }

    [Fact]
    public void SetStructureTabOrder_PageWithoutAnnotations_IsLeftUntouched()
    {
        byte[] input  = UnannotatedPdf();
        byte[] output = SetTabOrder(input, out int changed);

        Assert.Equal(0, changed);

        // PDF/UA-1 7.18.3 requires /Tabs only where there is a tab order to define, and the UA09
        // check agrees, so writing it here would grow the file and close nothing.
        Assert.Null(ReadPageName(output, 0, "Tabs"));
        Assert.Equal(input, output);
    }

    [Fact]
    public void SetStructureTabOrder_PageAlreadyDeclaringTabsS_AppendsNoRevision()
    {
        byte[] input  = AnnotatedAndTabbedPdf();
        byte[] output = SetTabOrder(input, out int changed);

        Assert.Equal(0, changed);

        // Byte-identical, not merely "still valid": an appended revision that revises nothing is
        // pure file growth, and a caller chaining the operations pays for it once per step.
        Assert.Equal(input, output);
        Assert.Equal("S", ReadPageName(output, 0, "Tabs"));
    }

    [Fact]
    public void SetStructureTabOrder_LegacyTabOrderKey_GainsTheSpecKey()
    {
        byte[] output = SetTabOrder(LegacyTabOrderPdf(), out int changed);

        // /TabOrder is not a PDF key and no reader honours it, so a page carrying only it is not
        // yet fixed.
        Assert.Equal(1, changed);
        Assert.Equal("S", ReadPageName(output, 0, "Tabs"));

        // ...and the legacy entry is left alone: removing a key some other tool may read is not
        // this operation's call.
        Assert.Equal("S", ReadPageName(output, 0, "TabOrder"));
    }

    [Fact]
    public void SetStructureTabOrder_MixedPages_ChangesOnlyTheAnnotatedOne()
    {
        byte[] output = SetTabOrder(MixedPdf(), out int changed);

        Assert.Equal(1, changed);
        Assert.Equal("S", ReadPageName(output, 0, "Tabs"));
        Assert.Null(ReadPageName(output, 1, "Tabs"));
    }

    [Fact]
    public void SetStructureTabOrder_AppliedTwice_IsIdempotent()
    {
        byte[] once  = SetTabOrder(AnnotatedPdf(), out int firstChanged);
        byte[] twice = SetTabOrder(once, out int secondChanged);

        Assert.Equal(1, firstChanged);
        Assert.Equal(0, secondChanged);
        Assert.Equal(once, twice);
        Assert.Equal("S", ReadPageName(twice, 0, "Tabs"));
    }

    // =====================================================================
    //  The pipeline closes UA09
    // =====================================================================

    [Fact]
    public void RemediateWithReport_AnnotatedDocument_ClosesUa09()
    {
        byte[] input = AnnotatedPdf();

        RemediationReport report = Remediate(input, new RemediationOptions
        {
            Title = "Tab Order Fixture",
        });

        // The defect this pins: UA09 was reported before and stayed reported after, because no
        // pipeline step could write /Tabs.
        Assert.Contains(report.BeforeAudit.Issues, i => i.Code == "UA09");
        Assert.DoesNotContain(report.AfterAudit.Issues, i => i.Code == "UA09");

        RemediationStepResult step = Step(report, "SetTabOrder");
        Assert.True(step.Applied, "SetTabOrder did not run: " + step);
        Assert.Equal(1, step.ItemsChanged);
    }

    [Fact]
    public void RemediateWithReport_AnnotatedDocument_WritesTabsOnThePageItself()
    {
        byte[] input = AnnotatedPdf();

        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        PdfAccessibility.RemediateWithReport(inMs, outMs, new RemediationOptions
        {
            Title = "Tab Order Fixture",
        });

        Assert.Equal("S", ReadPageName(outMs.ToArray(), 0, "Tabs"));
    }

    // =====================================================================
    //  Skip reasons — one test per category
    // =====================================================================

    [Fact]
    public void SkipReason_NotRequested_TabOrderDisabled_SaysSoAndWarnsUa09StaysOpen()
    {
        RemediationReport report = Remediate(AnnotatedPdf(), new RemediationOptions
        {
            Title       = "Tab Order Fixture",
            FixTabOrder = false,
        });

        RemediationStepResult step = Step(report, "SetTabOrder");
        Assert.True(step.Skipped);
        Assert.StartsWith("not requested", step.SkipReason, StringComparison.Ordinal);
        Assert.Contains("FixTabOrder", step.SkipReason, StringComparison.Ordinal);

        // The warning must be true, not merely present: the after-audit really does still report
        // UA09, which is exactly what the hint tells the caller.
        Assert.Contains(report.AllWarnings, w => w.Contains("UA09", StringComparison.Ordinal));
        Assert.Contains(report.AfterAudit.Issues, i => i.Code == "UA09");
    }

    [Fact]
    public void SkipReason_NotApplicable_NoAnnotations_SaysThePageNeedsNoTabOrder()
    {
        RemediationReport report = Remediate(UnannotatedPdf(), new RemediationOptions
        {
            Title = "Tab Order Fixture",
        });

        RemediationStepResult step = Step(report, "SetTabOrder");
        Assert.True(step.Skipped);
        Assert.StartsWith("not applicable", step.SkipReason, StringComparison.Ordinal);
        Assert.Contains("annotation", step.SkipReason, StringComparison.Ordinal);

        // And the claim is true: nothing in the document needed the key.
        Assert.DoesNotContain(report.BeforeAudit.Issues, i => i.Code == "UA09");
        Assert.DoesNotContain(report.AfterAudit.Issues, i => i.Code == "UA09");
    }

    [Fact]
    public void SkipReason_AlreadySatisfied_TabbedDocument_SaysSoAndIsTrue()
    {
        RemediationReport report = Remediate(AnnotatedAndTabbedPdf(), new RemediationOptions
        {
            Title = "Tab Order Fixture",
        });

        RemediationStepResult step = Step(report, "SetTabOrder");
        Assert.True(step.Skipped);
        Assert.StartsWith("already satisfied", step.SkipReason, StringComparison.Ordinal);
        Assert.Contains("/Tabs /S", step.SkipReason, StringComparison.Ordinal);

        // "already satisfied" is only allowed when it was verified — it was.
        Assert.DoesNotContain(report.BeforeAudit.Issues, i => i.Code == "UA09");
    }

    [Fact]
    public void SkipReason_Superseded_LayoutStepAlreadyExtractedText_SaysSuperseded()
    {
        RemediationReport report = Remediate(UntaggedPdf(), new RemediationOptions
        {
            Title = "Untagged Fixture",
        });

        // AutoTag owns the pipeline's single text-extraction pass on an untagged document, so the
        // other two layout steps are superseded — not "disabled", and not "already satisfied".
        RemediationStepResult artifacts = Step(report, "MarkArtifacts");
        Assert.True(artifacts.Skipped, "MarkArtifacts unexpectedly ran: " + artifacts);
        Assert.StartsWith("superseded", artifacts.SkipReason, StringComparison.Ordinal);
    }

    // =====================================================================
    //  The metadata step must not overstate
    // =====================================================================

    [Fact]
    public void SkipReason_NoTitleSupplied_DoesNotClaimTheDocumentAlreadyHasOne()
    {
        // /Lang and /MarkInfo are already exactly what the defaults would write, and no Title is
        // supplied, so the metadata pass writes nothing — and must not print "the catalog and
        // /Info already carry the requested values" while UA04 is reported in the same run.
        RemediationReport report = Remediate(UnannotatedPdf(), new RemediationOptions
        {
            Language             = "en-US",
            Title                = null,
            AutoTagIfUntagged    = false,
            MarkArtifacts        = false,
            FixReadingOrder      = false,
            SetPdfUaIdentifier   = false,
        });

        RemediationStepResult step = Step(report, "SetDocumentMetadata");
        Assert.True(step.Skipped, "expected the metadata pass to write nothing: " + step);

        Assert.Contains("not requested", step.SkipReason, StringComparison.Ordinal);
        Assert.Contains("no Title was supplied", step.SkipReason, StringComparison.Ordinal);

        // The old wording, and anything else that would read as "a title is present".
        Assert.DoesNotContain("already carry the requested values", step.SkipReason,
            StringComparison.Ordinal);
        Assert.DoesNotContain("/Info /Title already", step.SkipReason, StringComparison.Ordinal);

        // What it verified, it may claim.
        Assert.Contains("already satisfied", step.SkipReason, StringComparison.Ordinal);
        Assert.Contains("/Lang already holds", step.SkipReason, StringComparison.Ordinal);

        // And the consequence is surfaced where a caller will read it.
        Assert.Contains(report.AllWarnings, w => w.Contains("UA04", StringComparison.Ordinal));
        Assert.Contains(report.AfterAudit.Issues, i => i.Code == "UA04");
    }

    [Fact]
    public void SkipReason_NoTitleSupplied_ReportsDisplayDocTitleAsNotApplicable()
    {
        RemediationReport report = Remediate(UnannotatedPdf(), new RemediationOptions
        {
            Language           = "en-US",
            Title              = null,
            AutoTagIfUntagged  = false,
            MarkArtifacts      = false,
            FixReadingOrder    = false,
            SetPdfUaIdentifier = false,
        });

        RemediationStepResult step = Step(report, "SetDocumentMetadata");

        // /ViewerPreferences /DisplayDocTitle is not "already satisfied" and not "not requested":
        // it is meaningless until the document has a title, and the reason says exactly that.
        Assert.Contains("not applicable", step.SkipReason, StringComparison.Ordinal);
        Assert.Contains("DisplayDocTitle", step.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipReason_TitleSupplied_MetadataStepRunsAndRaisesNoTitleWarning()
    {
        // The control for the test above: once a Title is supplied the step applies, and the
        // "UA04 remains open" hint must NOT be raised.
        RemediationReport report = Remediate(UnannotatedPdf(), new RemediationOptions
        {
            Language           = "en-US",
            Title              = "A Real Title",
            AutoTagIfUntagged  = false,
            MarkArtifacts      = false,
            FixReadingOrder    = false,
            SetPdfUaIdentifier = false,
        });

        RemediationStepResult step = Step(report, "SetDocumentMetadata");
        Assert.True(step.Applied, "expected the metadata pass to run: " + step);
        Assert.DoesNotContain(step.Warnings, w => w.Contains("UA04", StringComparison.Ordinal));
    }

    // =====================================================================
    //  Every skip reason states a category
    // =====================================================================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EverySkippedStep_StatesWhichCategoryItFallsIn(bool tagged)
    {
        RemediationReport report = Remediate(
            tagged ? AnnotatedPdf() : UntaggedPdf(),
            new RemediationOptions { Title = "Category Fixture" });

        Assert.NotEmpty(report.SkippedSteps);

        foreach (RemediationStepResult step in report.SkippedSteps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.SkipReason),
                step.Name + " was skipped with no reason");

            Assert.True(
                SkipCategories.Any(c => step.SkipReason.StartsWith(c, StringComparison.Ordinal)),
                step.Name + " skipped with an uncategorised reason: " + step.SkipReason);
        }
    }

    [Fact]
    public void EverySkippedStep_OfAnUnreadableDocument_SaysItIsNotApplicable()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("this is not a PDF at all\n");

        RemediationReport report = Remediate(garbage, new RemediationOptions());

        Assert.NotEmpty(report.SkippedSteps);
        foreach (RemediationStepResult step in report.SkippedSteps)
        {
            // Whether the parse threw or merely produced no catalog, the honest category is the
            // same: there was no document for the step to work on.
            Assert.StartsWith("not applicable", step.SkipReason, StringComparison.Ordinal);
        }

        // The tab-order step exists in the log even when the whole pipeline is skipped, so a
        // caller iterating Steps never has to wonder whether it was silently dropped.
        Assert.Contains(report.Steps, s => s.Name == "SetTabOrder");
    }
}
