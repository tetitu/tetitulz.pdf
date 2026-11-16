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
/// Tests that a document which could not actually be tagged is never made to <i>claim</i> that it
/// was.
///
/// <para>
/// <b>The defect these pin.</b> A page whose only content is a running header and footer produces
/// two artifacts and zero taggable text runs, so <c>AutoTag</c> correctly writes no structure
/// tree — but it still stamped <c>/MarkInfo /Marked true</c> on the catalog, and the pipeline then
/// stamped a <c>pdfuaid:part</c> XMP declaration on top. Every PDF/UA checker decides "does this
/// document claim to be tagged" from <c>/StructTreeRoot != null || /MarkInfo /Marked</c>, so those
/// two writes:
/// </para>
/// <list type="number">
///   <item><description><b>hid</b> the true findings — UA01/UA05 ("not tagged") disappeared from
///     the after-audit;</description></item>
///   <item><description><b>manufactured</b> a false one — the structure-dependent checks switched
///     on, so an annotation on that page was reported as UA23 (unreachable from the structure
///     tree) even though UA23 was absent from the before-audit.</description></item>
/// </list>
/// <para>
/// Remediation was therefore trading a real violation for a fabricated one. <c>/Marked true</c> is
/// not "some marked content exists": ISO 32000-1 §14.7.1 makes it an assertion that the document
/// follows the Tagged PDF conventions, which require a structure tree. Artifacts alone never
/// appear in one.
/// </para>
///
/// <para>Every fixture is built in memory; every assertion re-opens the produced document and
/// walks the parsed graph.</para>
/// </summary>
public class UntaggedClaimHonestyTests
{
    // =====================================================================
    //  Fixtures
    // =====================================================================

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
    /// A running header at y=762 and a running footer at y=30 — both inside the default
    /// header/footer margins, so the tagger classifies both as pagination artifacts and no
    /// taggable text run survives.
    /// </summary>
    private const string ArtifactOnlyText =
        "BT /F1 9 Tf 72 762 Td (Acme Corporation Internal) Tj ET\n"
        + "BT /F1 9 Tf 72 30 Td (Page 1 of 1) Tj ET\n";

    /// <summary>An artifact-only page, optionally carrying a /Link annotation as object 6.</summary>
    private static byte[] ArtifactOnlyDocument(bool withAnnotation)
    {
        string annots = withAnnotation ? " /Annots [6 0 R]" : string.Empty;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                + " /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R" + annots + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            StreamObject(string.Empty, ArtifactOnlyText),
        };
        if (withAnnotation)
            bodies.Add("<< /Type /Annot /Subtype /Link /Rect [72 400 300 420] /F 4"
                     + " /Contents (Example) /A << /S /URI /URI (https://example.org) >> >>");
        return BuildPdf(bodies);
    }

    /// <summary>A page with a real heading and body text, so AutoTag does build a tree.</summary>
    private static byte[] TaggableDocument()
        => BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                + " /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            StreamObject(string.Empty,
                "BT /F1 18 Tf 72 700 Td (Heading) Tj ET\n"
              + "BT /F1 10 Tf 72 400 Td (Body text on the page.) Tj ET\n"),
        });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static PdfDictionary Catalog(byte[] pdf, out PdfDocument doc)
    {
        doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.Catalog.Dictionary;
    }

    private static bool DeclaresMarked(PdfDocument doc, PdfDictionary catalog)
    {
        var markInfo = doc.ResolveObject(catalog[new PdfName("MarkInfo")]) as PdfDictionary;
        return (doc.ResolveObject(markInfo?[new PdfName("Marked")]) as PdfBoolean)?.Value == true;
    }

    private static string[] Codes(AccessibilityAuditReport report)
        => report.Issues.Select(i => i.Code).Distinct().ToArray();

    // =====================================================================
    //  AutoTag must not claim /Marked when it wrote no structure tree
    // =====================================================================

    [Fact]
    public void AutoTag_ArtifactOnlyPage_WritesNoStructureTree()
    {
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(
            new MemoryStream(ArtifactOnlyDocument(withAnnotation: false)), output);

        // Precondition for everything below: artifacts were marked, nothing was taggable.
        Assert.Equal(0, result.ElementsAdded);
        Assert.False(result.ContentMarked);
        Assert.True(result.ArtifactCount > 0);

        var catalog = Catalog(output.ToArray(), out PdfDocument doc);
        using (doc)
            Assert.Null(catalog[new PdfName("StructTreeRoot")]);
    }

    [Fact]
    public void AutoTag_ArtifactOnlyPage_DoesNotDeclareMarkInfoMarked()
    {
        using var output = new MemoryStream();
        PdfAccessibility.AutoTag(new MemoryStream(ArtifactOnlyDocument(withAnnotation: false)), output);

        var catalog = Catalog(output.ToArray(), out PdfDocument doc);
        using (doc)
            Assert.False(DeclaresMarked(doc, catalog),
                "/MarkInfo /Marked true asserts Tagged PDF conformance, which requires a structure "
              + "tree; this document has none.");
    }

    [Fact]
    public void AutoTag_ArtifactOnlyPage_WarnsThatMarkedWasLeftUnset()
    {
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(
            new MemoryStream(ArtifactOnlyDocument(withAnnotation: false)), output);

        Assert.Contains(result.Warnings, w => w.Contains("/MarkInfo /Marked"));
    }

    [Fact]
    public void AutoTag_TaggablePage_StillDeclaresMarkInfoMarked()
    {
        // The guard must not regress the normal path: a document that really was tagged still
        // declares itself tagged.
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(new MemoryStream(TaggableDocument()), output);

        Assert.True(result.ContentMarked);
        Assert.True(result.ElementsAdded > 0);

        var catalog = Catalog(output.ToArray(), out PdfDocument doc);
        using (doc)
        {
            Assert.NotNull(catalog[new PdfName("StructTreeRoot")]);
            Assert.True(DeclaresMarked(doc, catalog));
        }
    }

    // =====================================================================
    //  The audit consequence: no hidden truth, no manufactured violation
    // =====================================================================

    [Fact]
    public void Remediate_ArtifactOnlyPage_StillReportsTheDocumentAsUntagged()
    {
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(
            new MemoryStream(ArtifactOnlyDocument(withAnnotation: false)), output, new RemediationOptions());

        // UA01 is the "document is not tagged" finding. It is true before and it is still true
        // after — remediation could not fix it, so it must not make it disappear.
        Assert.Contains("UA01", Codes(report.BeforeAudit));
        Assert.Contains("UA01", Codes(report.AfterAudit));
    }

    [Fact]
    public void Remediate_ArtifactOnlyPageWithAnnotation_DoesNotIntroduceUa23()
    {
        byte[] input = ArtifactOnlyDocument(withAnnotation: true);
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(
            new MemoryStream(input), output, new RemediationOptions());

        Assert.DoesNotContain("UA23", Codes(report.BeforeAudit));
        Assert.DoesNotContain("UA23", Codes(report.AfterAudit));

        // Non-vacuity: the annotation really is still there to be judged.
        var catalog = Catalog(output.ToArray(), out PdfDocument doc);
        using (doc)
        {
            var page = doc.Pages[0].Dictionary;
            var annots = doc.ResolveObject(page[new PdfName("Annots")]) as PdfArray;
            Assert.NotNull(annots);
            Assert.Single(annots);
        }
    }

    // =====================================================================
    //  The pdfuaid:part declaration is a conformance claim, not a formality
    // =====================================================================

    [Fact]
    public void Remediate_ArtifactOnlyPage_SkipsPdfUaIdentifierWithAStatedReason()
    {
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(
            new MemoryStream(ArtifactOnlyDocument(withAnnotation: false)), output, new RemediationOptions());

        RemediationStepResult step = Assert.Single(
            report.Steps.Where(s => s.Name == "SetPdfUaIdentifier"));

        Assert.True(step.Skipped);
        Assert.Contains("no structure tree", step.SkipReason);
        // The consequence must reach AllWarnings, not just the step list.
        Assert.Contains(report.AllWarnings, w => w.Contains("UA03"));
    }

    [Fact]
    public void Remediate_ArtifactOnlyPage_LeavesUa03OpenRatherThanClaimingConformance()
    {
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(
            new MemoryStream(ArtifactOnlyDocument(withAnnotation: false)), output, new RemediationOptions());

        // UA03 is the missing pdfuaid:part declaration. Leaving it open is the honest outcome for
        // a document that could not be tagged.
        Assert.Contains("UA03", Codes(report.AfterAudit));
    }

    [Fact]
    public void Remediate_TaggablePage_StillWritesThePdfUaIdentifier()
    {
        // Guard against over-correction: the normal path must still close UA03.
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(
            new MemoryStream(TaggableDocument()), output, new RemediationOptions());

        RemediationStepResult step = Assert.Single(
            report.Steps.Where(s => s.Name == "SetPdfUaIdentifier"));

        Assert.True(step.Applied);
        Assert.DoesNotContain("UA03", Codes(report.AfterAudit));
    }
}
