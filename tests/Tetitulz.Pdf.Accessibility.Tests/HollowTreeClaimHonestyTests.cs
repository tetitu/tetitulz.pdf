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
/// Tests that <c>RemediateWithReport</c> never turns "the catalog has a <c>/StructTreeRoot</c>"
/// into "the document is tagged".
///
/// <para>
/// <b>The defect these pin.</b> Two of the pipeline's writes are conformance <i>claims</i> rather
/// than repairs — <c>/MarkInfo &lt;&lt;/Marked true&gt;&gt;</c> (ISO 32000-1 §14.7.1: "this
/// document follows the Tagged PDF conventions") and the <c>pdfuaid:part</c> XMP declaration
/// (a self-asserted PDF/UA-1 conformance claim). Both were gated on nothing but the
/// <i>presence</i> of a <c>/StructTreeRoot</c>. A hollow tree — elements with no <c>/K</c>, no
/// <c>/ParentTree</c>, no marked content — passes that gate while being untagged in every sense
/// assistive technology cares about, and the pipeline knew it: the same run's audit reports
/// UA14/UA16/UA30 against the tree. The emitted file therefore carried a PDF/UA-1 conformance
/// claim baked into a document the very same call audited as non-conformant, and any consumer
/// that triages on the XMP identifier read the broken file as conformant.
/// </para>
///
/// <para>
/// The third defect is the AutoTag skip reason, which told the caller "AutoTag never replaces"
/// an existing tree. That is false of the public <c>PdfAccessibility.AutoTag</c> API — it
/// supersedes an existing tree and warns that it did — and it was printed on exactly the input
/// class (a hollow tree) where that API is the fix. The round trip below asserts both halves.
/// </para>
///
/// <para>Every fixture is built in memory; every assertion re-opens the produced document.</para>
/// </summary>
public class HollowTreeClaimHonestyTests
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

    private static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    private const string Helvetica =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>A running head, a heading, body text and a running foot — all unmarked.</summary>
    private const string UnmarkedContent =
        "BT /F1 9 Tf 72 762 Td (Acme Corporation Internal) Tj ET\n"
        + "BT /F1 24 Tf 72 700 Td (Quarterly Results) Tj ET\n"
        + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region this quarter.) Tj ET\n"
        + "BT /F1 9 Tf 72 30 Td (Page 1 of 1) Tj ET\n";

    /// <summary>
    /// The hollow tree, exactly as a "tagged" file produced by a broken exporter looks: the
    /// catalog points at a <c>/StructTreeRoot</c>, the root points at a <c>/Document</c>, and the
    /// Document has no <c>/K</c> at all. There is no <c>/ParentTree</c>, and no page content is
    /// inside a marked-content sequence. Nothing here is reachable by assistive technology.
    /// </summary>
    private static byte[] HollowTaggedPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject(UnmarkedContent),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] >>",
        "<< /Type /StructElem /S /Document /P 6 0 R >>",
    });

    /// <summary>
    /// The healthy twin: the same page, genuinely tagged — a <c>/ParentTree</c>, a page
    /// <c>/StructParents</c>, and a <c>/P</c> element holding the MCID the content stream marks.
    /// <c>/MarkInfo</c> is deliberately absent so the pipeline has a reason to write it.
    /// </summary>
    private static byte[] ProperlyTaggedPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P <</MCID 0>> BDC\n"
                   + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region.) Tj ET\nEMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [7 0 R]] >>",
    });

    /// <summary>
    /// The same tagged page as <see cref="ProperlyTaggedPdf"/> with the <c>/ParentTree</c> removed:
    /// elements name content, but a reader cannot get from the page back into the structure.
    /// </summary>
    /// <remarks>
    /// <b>This is the document the two gates disagree about, and the disagreement is the point.</b>
    /// The conformance claims are withheld — <c>StructTreeReachesContent</c> is false without a
    /// <c>/ParentTree</c>, and calling this document tagged would be untrue. The tree is
    /// nevertheless kept, because <c>StructTreeNamesContent</c> is true: a human wrote those
    /// element-to-MCID links, the missing parent tree is a repairable omission, and overwriting the
    /// links to fix the omission trades a fixable defect for an unfixable loss.
    /// </remarks>
    private static byte[] TaggedWithNoParentTreePdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P <</MCID 0>> BDC\n"
                   + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region.) Tj ET\nEMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
    });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static RemediationReport Remediate(byte[] input, out byte[] output)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inMs, outMs,
            new RemediationOptions { Language = "en-US", Title = "Quarterly Results" });
        output = outMs.ToArray();
        return report;
    }

    /// <summary>
    /// Remediates without the auto-tagger, which is what leaves a hollow tree hollow.
    /// </summary>
    /// <remarks>
    /// <b>The claim gates are only reachable when the pipeline cannot repair the input.</b> With
    /// <c>AutoTagIfUntagged</c> at its default the pipeline now tags a document whose elements name
    /// no content, so the tree is no longer hollow by the time <c>/MarkInfo</c> and the
    /// <c>pdfuaid:part</c> declaration are decided — and a test asserting they are withheld would be
    /// asserting the tagger failed. Turning the tagger off is the honest way to hold the input
    /// still: the gates then face exactly the document this file is about.
    /// </remarks>
    private static RemediationReport RemediateWithoutTagging(byte[] input, out byte[] output)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inMs, outMs,
            new RemediationOptions
            {
                Language = "en-US",
                Title = "Quarterly Results",
                AutoTagIfUntagged = false,
            });
        output = outMs.ToArray();
        return report;
    }

    private static RemediationStepResult Step(RemediationReport report, string name)
        => Assert.Single(report.Steps.Where(s => s.Name == name));

    private static bool DeclaresMarked(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var markInfo = doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("MarkInfo")]) as PdfDictionary;
        return (doc.ResolveObject(markInfo?[new PdfName("Marked")]) as PdfBoolean)?.Value == true;
    }

    private static string XmpOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var metadata = doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("Metadata")]) as PdfStream;
        return metadata == null ? null : Encoding.UTF8.GetString(metadata.GetDecodedData());
    }

    // =====================================================================
    //  /MarkInfo /Marked is a claim, not a formality
    // =====================================================================

    [Fact]
    public void RemediateWithoutTagging_HollowStructureTree_DoesNotDeclareTheDocumentTagged()
    {
        RemediateWithoutTagging(HollowTaggedPdf(), out byte[] output);

        Assert.False(DeclaresMarked(output),
            "/MarkInfo /Marked true asserts the Tagged PDF conventions. The tree in this document "
          + "has no /ParentTree and no element naming any content, so nothing in it is reachable "
          + "by assistive technology and the assertion would be false.");
    }

    [Fact]
    public void RemediateWithoutTagging_HollowStructureTree_SaysWhyMarkedWasLeftUnwritten()
    {
        RemediationReport report = RemediateWithoutTagging(HollowTaggedPdf(), out _);

        Assert.Contains(report.AllWarnings,
            w => w.Contains("reaches no page content") && w.Contains("UA14"));
    }

    // =====================================================================
    //  The pdfuaid:part declaration is a conformance claim
    // =====================================================================

    [Fact]
    public void RemediateWithoutTagging_HollowStructureTree_WritesNoPdfUaIdentifier()
    {
        RemediationReport report = RemediateWithoutTagging(HollowTaggedPdf(), out byte[] output);

        RemediationStepResult step = Step(report, "SetPdfUaIdentifier");
        Assert.True(step.Skipped, "the identifier step ran: " + step);
        Assert.Contains("reaches no page content", step.SkipReason);

        string xmp = XmpOf(output);
        Assert.True(xmp == null || !xmp.Contains("pdfuaid"),
            "the emitted document declares PDF/UA conformance over a hollow structure tree: " + xmp);
    }

    [Fact]
    public void RemediateWithoutTagging_HollowStructureTree_NamesTheConsequenceOfSkippingTheIdentifier()
    {
        RemediationReport report = RemediateWithoutTagging(HollowTaggedPdf(), out _);

        // The consequence must reach AllWarnings, not just the step list — that is what a caller
        // reads to find out what is still wrong.
        Assert.Contains(report.AllWarnings, w => w.Contains("UA03"));
    }

    [Fact]
    public void RemediateWithoutTagging_HollowStructureTree_ReportsTheDocumentAsStillNonConforming()
    {
        RemediationReport report = RemediateWithoutTagging(HollowTaggedPdf(), out _);

        Assert.False(report.IsConformingAfter,
            "a hollow tree must never be remediated into a conforming report: " + report);
    }

    // =====================================================================
    //  Over-correction guard: a real tree must still get both writes
    // =====================================================================

    [Fact]
    public void Remediate_TreeThatReachesContent_StillDeclaresMarkedAndWritesTheIdentifier()
    {
        RemediationReport report = Remediate(ProperlyTaggedPdf(), out byte[] output);

        Assert.True(DeclaresMarked(output),
            "a genuinely tagged document must still be declared tagged: " + report);

        RemediationStepResult step = Step(report, "SetPdfUaIdentifier");
        Assert.True(step.Applied, "the identifier step did not run: " + step);

        string xmp = XmpOf(output);
        Assert.NotNull(xmp);
        Assert.Contains("pdfuaid:part", xmp, StringComparison.Ordinal);
    }

    // =====================================================================
    //  The AutoTag skip reason must describe the pipeline, not the API
    // =====================================================================

    /// <summary>
    /// The pipeline now repairs a hollow tree instead of describing why it will not.
    /// </summary>
    /// <remarks>
    /// <b>This replaces a test that asserted the skip.</b> The skip reason used to end by telling
    /// the caller to call <c>PdfAccessibility.AutoTag</c> by hand on exactly this input class; the
    /// pipeline now does it, so the advice has become the behaviour. The supersession is reported
    /// rather than silent, which is the property worth pinning: a tree disappearing without a word
    /// is the failure mode a caller cannot audit.
    /// </remarks>
    [Fact]
    public void Remediate_HollowStructureTree_TagsItAndReportsTheSupersession()
    {
        RemediationReport report = Remediate(HollowTaggedPdf(), out byte[] output);

        RemediationStepResult step = Step(report, "AutoTag");
        Assert.True(step.Applied, "the pipeline did not tag a tree that names no content: " + step);

        Assert.Contains(report.AllWarnings,
            w => w.Contains("superseded", StringComparison.Ordinal));

        // And the repair is real, not just reported: the produced tree has the /ParentTree the
        // input lacked, which is what makes the content reachable from the page.
        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;
        Assert.NotNull(root);
        Assert.NotNull(doc.ResolveObject(root![new PdfName("ParentTree")]) as PdfDictionary);
    }

    /// <summary>
    /// A tree whose elements name content is kept, and the skip reason says so in terms a reader
    /// can act on.
    /// </summary>
    /// <remarks>
    /// The gate is <c>StructTreeNamesContent</c> — an element naming an MCID — and deliberately not
    /// the stricter <c>StructTreeReachesContent</c>, which also demands a <c>/ParentTree</c>. A
    /// missing parent tree is a repairable omission; the element-to-content links are not
    /// recoverable once a generated tree has overwritten them.
    /// </remarks>
    [Fact]
    public void Remediate_TreeThatNamesContent_IsKeptAndTheSkipReasonSaysWhy()
    {
        RemediationReport report = Remediate(ProperlyTaggedPdf(), out _);

        RemediationStepResult step = Step(report, "AutoTag");
        Assert.True(step.Skipped, "the pipeline replaced a tree a human built: " + step);

        // The old wording made a false claim about the public API (see the round trip below).
        Assert.DoesNotContain("AutoTag never", step.SkipReason);
        Assert.Contains("already name page content", step.SkipReason);

        // …and it must point at the operation that replaces one regardless.
        Assert.Contains("PdfAccessibility.AutoTag", step.SkipReason);
    }

    /// <summary>
    /// A tree whose elements name content but which has no <c>/ParentTree</c> is kept, and the
    /// conformance claims are still withheld.
    /// </summary>
    /// <remarks>
    /// <b>Both halves in one test, because they are the two gates disagreeing on purpose.</b> The
    /// replace gate asks "did a human make this" and answers yes; the claim gate asks "can a reader
    /// navigate it" and answers no. Collapsing them either way is a real defect: one gate for both
    /// questions either destroys authored links to fix a missing parent tree, or declares a
    /// document tagged that assistive technology cannot enter.
    /// </remarks>
    [Fact]
    public void Remediate_TreeNamingContentWithNoParentTree_IsKeptButEarnsNoClaim()
    {
        RemediationReport report = Remediate(TaggedWithNoParentTreePdf(), out byte[] output);

        RemediationStepResult step = Step(report, "AutoTag");
        Assert.True(step.Skipped,
            "a tree whose elements name content was replaced because it lacked a /ParentTree: " + step);

        // The authored link survives: the element still names MCID 0 on the page.
        using var doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;
        Assert.NotNull(root);

        var kids = doc.ResolveObject(root![new PdfName("K")]) as PdfArray;
        var element = doc.ResolveObject(Assert.Single(kids!)) as PdfDictionary;
        Assert.Equal("P", (doc.ResolveObject(element![new PdfName("S")]) as PdfName)?.Value);
        Assert.Equal(0, (doc.ResolveObject(element[new PdfName("K")]) as PdfNumber)?.IntValue);

        // And the claim is still withheld, because the page cannot be navigated back into it.
        Assert.False(DeclaresMarked(output),
            "a tree with no /ParentTree cannot be entered from the page, so the document must not "
          + "be declared tagged: " + report);
    }

    [Fact]
    public void AutoTag_OnAHollowTaggedDocument_SupersedesTheTreeAndSaysSo()
    {
        // The fact the skip reason used to deny: AutoTag *does* replace an existing tree.
        byte[] input = HollowTaggedPdf();

        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(new MemoryStream(input, writable: false), output);

        Assert.True(result.ContentMarked);
        Assert.True(result.ElementsAdded > 0);
        Assert.Contains(result.Warnings, w => w.Contains("superseded"));

        byte[] produced = output.ToArray();

        using var doc = PdfDocument.Open(new MemoryStream(produced, writable: false));
        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;

        Assert.NotNull(root);

        // The generated tree, not the hollow one: it defines the /ParentTree the input lacked.
        Assert.NotNull(doc.ResolveObject(root[new PdfName("ParentTree")]) as PdfDictionary);
    }

    [Fact]
    public void AutoTag_ThenRemediate_ClosesTheClaimTheHollowInputCouldNotEarn()
    {
        // End to end: the escape hatch the skip reason now names really does unblock the two
        // writes the pipeline refused, which is what makes the advice actionable rather than
        // decorative.
        using var tagged = new MemoryStream();
        PdfAccessibility.AutoTag(new MemoryStream(HollowTaggedPdf(), writable: false), tagged);

        RemediationReport report = Remediate(tagged.ToArray(), out byte[] output);

        Assert.True(DeclaresMarked(output));
        Assert.True(Step(report, "SetPdfUaIdentifier").Applied, "identifier not written: " + report);
    }
}
