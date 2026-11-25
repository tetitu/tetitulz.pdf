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
/// Pins the four places where <see cref="PdfAccessibility"/>'s pipeline <i>gates</i> a step and the
/// code it gates disagreed about the same construct — the producer/checker-disagreement class, where
/// the report states a positive, verified-sounding fact that the very step it describes contradicts.
///
/// <list type="number">
///   <item><b>Typeless <c>/MCR</c> and <c>/OBJR</c>.</b> ISO 32000-1 Table 324 makes <c>/MCID</c> the
///     only required entry of a marked-content reference and Table 325 makes <c>/Obj</c> the only
///     required entry of an object reference; <c>/Type</c> is Optional in both. Demanding it told a
///     correctly tagged document that its "structure tree reaches no page content", which refused
///     both conformance claims (<c>pdfuaid:part</c> and <c>/MarkInfo &lt;&lt;/Marked true&gt;&gt;</c>)
///     while the audit in the same run reported the tree as perfectly connected.</item>
///   <item><b>The single-text-extraction flag.</b> It was set the moment AutoTag returned, so
///     MarkArtifacts and FixReadingOrder were dropped as "superseded: another layout step already
///     performed the pipeline's single text-extraction pass" even when AutoTag had returned without
///     reading one page of content.</item>
///   <item><b>UA99.</b> The plugin's "this check could not be completed" notice was mapped to an
///     Error carrying advice to consult ISO 14289-1 for a clause that does not exist, so an
///     <i>incomplete</i> report condemned the document — while the deep checker emitted the same
///     code as Info in the same report.</item>
///   <item><b>The heading replay.</b> It folded an unnumbered <c>&lt;H&gt;</c> in as level 1;
///     <c>PdfStructureTreeEditor.FixHeadingHierarchy</c> skips it entirely. The two therefore
///     computed different sequences, and the pipeline skipped the step as "already satisfied" over
///     documents the step would have renumbered.</item>
/// </list>
///
/// <para>Every fixture is built in memory as a <c>byte[]</c>; nothing is read from disk.</para>
/// </summary>
public class PipelineGateAgreementTests
{
    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static byte[] BuildPdf(IReadOnlyList<string> bodies, string extraTrailerEntries = "")
    {
        var sb = new StringBuilder(128 * bodies.Count + 256);
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
          .Append(" /Root 1 0 R").Append(extraTrailerEntries).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    private const string Helvetica =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    // =====================================================================
    //  Fixtures — a genuinely tagged page whose /K child shape varies
    // =====================================================================

    /// <summary>
    /// A single tagged paragraph whose <c>/K</c> child is written as <paramref name="kid"/>. The
    /// <c>/ParentTree</c>, the page's <c>/StructParents</c> and the marked content in the stream are
    /// all real, so the only thing under test is whether the child is recognised as naming content.
    /// </summary>
    private static byte[] TaggedPdfWithKid(string kid) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P <</MCID 0>> BDC\n"
                   + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region.) Tj ET\nEMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K " + kid + " >>",
        "<< /Nums [0 [7 0 R]] >>",
    });

    /// <summary>
    /// The same page, tagged only through an <b>object</b> reference to a link annotation. The
    /// wrapper's <c>/K</c> is written as <paramref name="kid"/> so the typed and typeless spellings
    /// of <c>/OBJR</c> can be compared.
    /// </summary>
    private static byte[] AnnotationTaggedPdfWithKid(string kid) => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 /Tabs /S "
            + "/Annots [9 0 R] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/Artifact BDC\n"
                   + "BT /F1 10 Tf 72 400 Td (See the report.) Tj ET\nEMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 2 >>",
        "<< /Type /StructElem /S /Link /Alt (The annual report) /P 6 0 R /Pg 3 0 R /K " + kid + " >>",
        "<< /Nums [0 [7 0 R] 1 7 0 R] >>",
        "<< /Type /Annot /Subtype /Link /Rect [72 396 200 410] /Border [0 0 0] /StructParent 1 "
            + "/Contents (The annual report) /A << /S /URI /URI (https://example.com/) >> >>",
    });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static RemediationReport Remediate(byte[] input, out byte[] output,
        RemediationOptions options = null)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inMs, outMs,
            options ?? new RemediationOptions { Language = "en-US", Title = "Quarterly Results" });
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
    //  1 — /Type is Optional on /MCR and /OBJR
    // =====================================================================

    [Theory]
    [InlineData("<< /Type /MCR /Pg 3 0 R /MCID 0 >>")]   // the typed spelling, as a control
    [InlineData("<< /Pg 3 0 R /MCID 0 >>")]              // typeless — legal per Table 324
    [InlineData("<< /MCID 0 >>")]                        // typeless and minimal
    [InlineData("0")]                                    // the bare-integer spelling, as a control
    public void Remediate_MarkedContentReference_IsRecognisedWithOrWithoutType(string kid)
    {
        RemediationReport report = Remediate(TaggedPdfWithKid(kid), out byte[] output);

        RemediationStepResult step = Step(report, "SetPdfUaIdentifier");
        Assert.True(step.Applied,
            "/Type is Optional on a marked-content reference (ISO 32000-1 Table 324), so this "
          + "document is tagged and must earn the pdfuaid:part declaration. Step: " + step);

        Assert.Contains("pdfuaid:part", XmpOf(output) ?? string.Empty, StringComparison.Ordinal);

        Assert.True(DeclaresMarked(output),
            "the same gate governs /MarkInfo <</Marked true>>, which asserts the Tagged PDF "
          + "conventions this document really does follow");
    }

    [Theory]
    [InlineData("<< /Type /OBJR /Pg 3 0 R /Obj 9 0 R >>")]  // typed, as a control
    [InlineData("<< /Pg 3 0 R /Obj 9 0 R >>")]              // typeless — legal per Table 325
    [InlineData("<< /Obj 9 0 R >>")]                        // typeless and minimal
    public void Remediate_ObjectReference_IsRecognisedWithOrWithoutType(string kid)
    {
        RemediationReport report = Remediate(AnnotationTaggedPdfWithKid(kid), out byte[] output);

        RemediationStepResult step = Step(report, "SetPdfUaIdentifier");
        Assert.True(step.Applied,
            "/Type is Optional on an object reference (ISO 32000-1 Table 325); /Obj is its only "
          + "required entry. Step: " + step);

        Assert.Contains("pdfuaid:part", XmpOf(output) ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    // No /MCID and no /Obj: nothing here names content, whatever it is.
    [InlineData("<< /Pg 3 0 R >>")]
    // /S is Required on every structure element (Table 323), so a dictionary carrying it is an
    // element — a hollow one — and not a typeless reference, even though it holds an /Obj.
    [InlineData("<< /S /Span /Obj 3 0 R >>")]
    public void RemediateWithoutTagging_KidThatNamesNoContent_StillRefusesTheConformanceClaims(string kid)
    {
        // The over-correction guard: relaxing /Type must not turn every dictionary into content.
        //
        // Tagging is off because the pipeline would otherwise REPAIR this document — its page
        // carries a usable /P <</MCID 0>> sequence, and completing the page adopts it and builds a
        // tree that does reach content, which earns the claim honestly. That is the right outcome
        // and it is asserted elsewhere; what this test is about is the predicate, and the predicate
        // is only observable while the input is held still.
        RemediationReport report = Remediate(TaggedPdfWithKid(kid), out byte[] output,
            new RemediationOptions
            {
                Language = "en-US",
                Title = "Quarterly Results",
                AutoTagIfUntagged = false,
            });

        RemediationStepResult step = Step(report, "SetPdfUaIdentifier");
        Assert.True(step.Skipped, "a tree that names no content earned a PDF/UA claim: " + step);
        Assert.Contains("reaches no page content", step.SkipReason);

        Assert.False(DeclaresMarked(output));
    }

    // =====================================================================
    //  1b — the heading gate reads /RoleMap, as the step it gates does
    // =====================================================================

    /// <summary>
    /// Two headings named through the document's own types, <c>/Chapter</c> mapped to <c>H1</c> and
    /// <c>/Section</c> to <c>H3</c>, so the hierarchy skips a level.
    /// </summary>
    private static byte[] RoleMappedHeadingsPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 /Tabs /S "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/Chapter <</MCID 0>> BDC\nBT /F1 18 Tf 72 700 Td (Chapter) Tj ET\nEMC\n"
                   + "/Section <</MCID 1>> BDC\nBT /F1 14 Tf 72 660 Td (Section) Tj ET\nEMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R 8 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 "
            + "/RoleMap << /Chapter /H1 /Section /H3 >> >>",
        "<< /Type /StructElem /S /Chapter /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Type /StructElem /S /Section /P 6 0 R /Pg 3 0 R /K 1 >>",
        "<< /Nums [0 [7 0 R 8 0 R]] >>",
    });

    /// <summary>
    /// The gate resolves the headings through <c>/RoleMap</c>, as <c>FixHeadingHierarchy</c> does:
    /// the step runs over role-mapped headings rather than being skipped as "no headings".
    /// </summary>
    [Fact]
    public void Remediate_RoleMappedHeadings_AreRenumbered_NotSkippedAsNoHeadings()
    {
        RemediationReport report = Remediate(RoleMappedHeadingsPdf(), out _);

        RemediationStepResult step = Step(report, "FixHeadingHierarchy");
        Assert.DoesNotContain("contains no headings", step.SkipReason ?? string.Empty);
        Assert.True(step.Applied, "the H1 → H3 skip is what FixHeadingHierarchy repairs, and the gate kept it from running: " + step);
    }

    // =====================================================================
    //  2 — "another layout step already extracted the text" must be true
    // =====================================================================

    /// <summary>An untagged page carrying a running head and a running foot to be marked.</summary>
    private static byte[] UntaggedPdfWithRunningHeaders() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("BT /F1 9 Tf 72 762 Td (Acme Corporation Internal) Tj ET\n"
                   + "BT /F1 24 Tf 72 700 Td (Quarterly Results) Tj ET\n"
                   + "BT /F1 10 Tf 72 400 Td (Revenue grew in every region this quarter.) Tj ET\n"
                   + "BT /F1 9 Tf 72 30 Td (Page 1 of 1) Tj ET\n"),
        Helvetica,
    });

    [Fact]
    public void Remediate_WhenAutoTagExtractedNothing_StillRunsTheArtifactPass()
    {
        // AutoTagOptions.RewriteContentStreams = false makes PdfAutoTagger return before it reads a
        // single page's content (it does not even compute the median font size), so nothing in this
        // run has extracted any text when MarkArtifacts is reached.
        var options = new RemediationOptions
        {
            Language = "en-US",
            Title     = "Quarterly Results",
            AutoTagOptions = new AutoTagOptions { RewriteContentStreams = false },
        };

        RemediationReport report = Remediate(UntaggedPdfWithRunningHeaders(), out _, options);

        RemediationStepResult artifacts = Step(report, "MarkArtifacts");

        Assert.DoesNotContain("text-extraction pass", artifacts.SkipReason ?? string.Empty);
        Assert.True(artifacts.Applied,
            "the artifact pass was dropped as superseded by an extraction that never happened, so "
          + "the running head and foot are left untagged and nothing in the report says so. Step: "
          + artifacts);
    }

    [Fact]
    public void Remediate_WhenAutoTagReallyTagged_StillSupersedesTheArtifactPass()
    {
        // The over-correction guard for the same flag: on the same document with content-stream
        // rewriting enabled, AutoTag really does mark the running head and foot, and the artifact
        // pass must still be skipped rather than run a second extraction over the same page.
        RemediationReport report = Remediate(UntaggedPdfWithRunningHeaders(), out _);

        RemediationStepResult artifacts = Step(report, "MarkArtifacts");
        Assert.True(artifacts.Skipped, "the artifact pass ran twice over the same page: " + artifacts);
        Assert.Contains("AutoTag already marked", artifacts.SkipReason);
    }

    // =====================================================================
    //  3 — UA99 says the report is incomplete, not that the document is wrong
    // =====================================================================

    /// <summary>
    /// A document with an <c>/Encrypt</c> dictionary and an XMP packet that is not well-formed XML.
    /// <c>PdfUaChecker</c> cannot decide UA03 over what it must assume is ciphertext, so it reports
    /// UA99 — "the conformance report is incomplete" — which is a statement about the audit, not
    /// about the document. Nothing is really enciphered: the checker's input here is the
    /// <c>/Filter</c>, <c>/R</c> and <c>/P</c> entries and the packet bytes.
    /// </summary>
    private static byte[] EncryptedPdfWithUnreadableXmp() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R "
            + "/MarkInfo << /Marked true >> /Metadata 9 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 /Tabs /S "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 10 Tf 72 400 Td (Tagged text.) Tj ET\nEMC\n"),
        Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [7 0 R]] >>",
        StreamObject("\x01\x02 this packet is not well-formed XML"),
        // /P: every reserved bit set, every grantable permission cleared except bit 10
        // ("extract for accessibility"), so no UA06 is raised and the XMP check still runs.
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3392 "
            + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
            + "/U <2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40> >>",
    },
        " /Encrypt 10 0 R /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]");

    [Fact]
    public void Audit_PluginUa99_IsAnInfoNotice_NotAPdfUa1Violation()
    {
        AccessibilityAuditReport report =
            PdfAccessibility.Audit(new MemoryStream(EncryptedPdfWithUnreadableXmp(), writable: false));

        var ua99 = report.Issues
            .Where(i => string.Equals(i.Code, "UA99", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(ua99);

        // One report cannot carry the same code under two severities: the deep checker's UA99
        // notices are Info because a check that did not run has proved nothing.
        Assert.All(ua99, i => Assert.Equal(IssueSeverity.Info, i.Severity));

        // And the advice must not send the reader to a clause of ISO 14289-1 that does not exist —
        // UA99 is this library's own "the report is incomplete" code.
        Assert.All(ua99, i => Assert.DoesNotContain("ISO 14289-1 for check UA99", i.Remediation));
        Assert.Contains(ua99, i => i.Remediation.IndexOf("incomplete", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // =====================================================================
    //  4 — the heading replay must model the fixer, not a variant of it
    // =====================================================================

    /// <summary>
    /// A tagged page whose <c>&lt;Document&gt;</c> holds one structure element per entry in
    /// <paramref name="roles"/>, in that order, each owning one marked-content sequence.
    /// </summary>
    private static byte[] HeadingPdf(params string[] roles)
    {
        var content = new StringBuilder();
        var kids    = new StringBuilder();
        var bodies  = new List<string>();

        for (int i = 0; i < roles.Length; i++)
        {
            content.Append("/P <</MCID ").Append(i.ToString(CultureInfo.InvariantCulture))
                   .Append(">> BDC\nBT /F1 12 Tf 72 ")
                   .Append((700 - 20 * i).ToString(CultureInfo.InvariantCulture))
                   .Append(" Td (Section ").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                   .Append(") Tj ET\nEMC\n");

            if (i > 0) kids.Append(' ');
            kids.Append((9 + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
        }

        bodies.Add("<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /StructTreeRoot 6 0 R "
                 + "/MarkInfo << /Marked true >> >>");
        bodies.Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
                 + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        bodies.Add(StreamObject(content.ToString()));
        bodies.Add(Helvetica);
        bodies.Add("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>");
        bodies.Add("<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [" + kids + "] >>");
        bodies.Add("<< /Nums [0 [" + kids + "]] >>");

        for (int i = 0; i < roles.Length; i++)
            bodies.Add("<< /Type /StructElem /S /" + roles[i] + " /P 7 0 R /Pg 3 0 R /K "
                     + i.ToString(CultureInfo.InvariantCulture) + " >>");

        return BuildPdf(bodies);
    }

    /// <summary>The <c>/S</c> of every element below the <c>&lt;Document&gt;</c>, in order.</summary>
    private static IReadOnlyList<string> HeadingRolesOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;
        var rootKids = doc.ResolveObject(root[new PdfName("K")]) as PdfArray;
        var document = doc.ResolveObject(rootKids[0]) as PdfDictionary;
        var kids     = doc.ResolveObject(document[new PdfName("K")]) as PdfArray;

        var roles = new List<string>(kids.Count);
        for (int i = 0; i < kids.Count; i++)
        {
            var element = doc.ResolveObject(kids[i]) as PdfDictionary;
            roles.Add((doc.ResolveObject(element[new PdfName("S")]) as PdfName)?.Value);
        }
        return roles;
    }

    private static int FixHeadingsDirectly(byte[] input, out byte[] output)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        int changed = PdfAccessibility.FixHeadingHierarchy(inMs, outMs);
        output = outMs.ToArray();
        return changed;
    }

    private static RemediationOptions HeadingsOnly() => new RemediationOptions
    {
        Language           = null,
        Title              = null,
        AutoTagIfUntagged  = false,
        MarkArtifacts      = false,
        FixReadingOrder    = false,
        SetFigureAltText   = false,
        FixTabOrder        = false,
        SetPdfUaIdentifier = false,
    };

    [Fact]
    public void FixHeadingHierarchy_StrongHeadingFollowedByH2_IsNotReportedAsAlreadySatisfied()
    {
        // <H> takes its level from /Sect nesting, so the fixer ignores it and <H2> becomes the
        // first *numbered* heading — an H1. A replay folding <H> in as level 1 would conclude
        // that H2 followed it legally, and skip the step with a claim the step contradicts.
        byte[] input = HeadingPdf("H", "H2");

        Assert.Equal(1, FixHeadingsDirectly(input, out byte[] directly));
        Assert.Equal(new[] { "H", "H1" }, HeadingRolesOf(directly));

        RemediationReport report = Remediate(input, out byte[] output, HeadingsOnly());
        RemediationStepResult step = Step(report, "FixHeadingHierarchy");

        Assert.False(step.Skipped,
            "the pipeline skipped the very repair a direct call performs: " + step);
        Assert.Equal(new[] { "H", "H1" }, HeadingRolesOf(output));
    }

    [Fact]
    public void FixHeadingHierarchy_OnlyStrongHeadings_SaysTheyAreNotThisStepsToRenumber()
    {
        // Zero numbered headings, but the document is not heading-free: saying "the structure tree
        // contains no headings" would be false.
        RemediationReport report = Remediate(HeadingPdf("H", "H"), out _, HeadingsOnly());
        RemediationStepResult step = Step(report, "FixHeadingHierarchy");

        Assert.True(step.Skipped);
        Assert.Contains("<H>", step.SkipReason);
        Assert.DoesNotContain("contains no headings", step.SkipReason);
    }

    [Theory]
    // Sequences the fixer leaves alone — the pipeline must skip, and its reason must be honest.
    [InlineData("H1", "H2", "H3")]
    [InlineData("H1", "H2", "H3", "H", "H4")]
    [InlineData("H1", "H1", "H2")]
    [InlineData("H")]
    // Sequences the fixer rewrites — the pipeline must run the step.
    [InlineData("H", "H2")]
    [InlineData("H1", "H3")]
    [InlineData("H2", "H4")]
    [InlineData("H1", "H2", "H", "H4")]
    public void FixHeadingHierarchy_PipelineGateAgreesWithTheFixer(params string[] roles)
    {
        // The invariant, stated once over a table: a skip means "a direct call would change
        // nothing", and a run means "a direct call would change something". Either half failing is
        // a producer/checker disagreement — a false report, and in the skip direction a lost fix.
        byte[] input = HeadingPdf(roles);

        int changedDirectly = FixHeadingsDirectly(input, out _);

        RemediationReport report = Remediate(input, out _, HeadingsOnly());
        RemediationStepResult step = Step(report, "FixHeadingHierarchy");

        string sequence = string.Join(",", roles);

        if (changedDirectly == 0)
            Assert.True(step.Skipped,
                sequence + ": a direct FixHeadingHierarchy changes nothing, so the pipeline "
                + "appended a revision for nothing. Step: " + step);
        else
            Assert.False(step.Skipped,
                sequence + ": a direct FixHeadingHierarchy renumbers " + changedDirectly
                + " heading(s), but the pipeline skipped the step. Step: " + step);
    }
}
