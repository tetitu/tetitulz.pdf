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
/// Tests that the remediation pipeline never invents a figure description <i>silently</i>.
///
/// <para>
/// <b>The defect these pin.</b> <see cref="RemediationOptions.SetFigureAltText"/> is true by
/// default and <see cref="RemediationOptions.AltTextFactory"/> is null by default, so the pipeline
/// wrote <c>/Alt (Figure 1)</c> onto every undescribed Figure. That string closes UA07 ("Figure
/// elements missing /Alt") and is not caught by the alt-text quality check, so a document whose
/// only substantive fault was an undescribed image came back as
/// <c>IsConformingAfter = true</c> — with an empty <see cref="RemediationReport.AllWarnings"/>.
/// The caller was never told the description had been machine-invented, and a screen-reader user
/// gets nothing from "Figure 1".
/// </para>
///
/// <para>
/// The auto-tagger already refuses to invent a link <c>/Contents</c> for exactly this reason and
/// warns naming UA22 (see <c>AnnotationStructureTests</c>). Figures keep the placeholder — an
/// undescribed figure is a hard PDF/UA failure, and the numbering at least matches the document —
/// but the pipeline now says so, every time, in the step's warnings and therefore in
/// <c>AllWarnings</c>.
/// </para>
///
/// <para>Every fixture is built in memory; every assertion re-opens the produced document.</para>
/// </summary>
public class FigureAltTextFabricationTests
{
    // =====================================================================
    //  Fixture
    // =====================================================================

    internal static byte[] BuildPdf(IReadOnlyList<string> bodies)
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

    internal static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    /// <summary>
    /// A genuinely tagged one-page document — <c>/ParentTree</c>, <c>/StructParents</c>, marked
    /// content — whose single Figure element carries no <c>/Alt</c>. That undescribed image is the
    /// document's only substantive accessibility fault, which is what makes the silent
    /// fabrication so damaging: it is the difference between a truthful report and a clean one.
    /// </summary>
    /// <remarks><b>internal</b> rather than private because <c>EncryptedFigureAltTextTests</c>
    /// needs the same fixture: the encrypted path must describe exactly the figures the
    /// clear-text path describes, and two hand-built fixtures could drift apart and hide
    /// precisely the divergence those tests exist to catch.</remarks>
    internal static byte[] TaggedPdfWithUndescribedFigure() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
            + "/ViewerPreferences << /DisplayDocTitle true >> /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
            + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        StreamObject("/P <</MCID 0>> BDC\n"
                   + "BT /F1 10 Tf 72 700 Td (Revenue grew in every region.) Tj ET\nEMC\n"
                   + "/Figure <</MCID 1>> BDC\n"
                   + "0.5 0.5 0.5 rg 72 400 200 100 re f\nEMC\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
        "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
        "<< /Type /StructElem /S /Figure /P 7 0 R /Pg 3 0 R /K 1 /BBox [72 400 272 500] >>",
        "<< /Nums [0 [8 0 R 9 0 R]] >>",
    });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static RemediationReport Remediate(
        byte[] input, RemediationOptions options, out byte[] output)
    {
        using var inMs  = new MemoryStream(input, writable: false);
        using var outMs = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(inMs, outMs, options);
        output = outMs.ToArray();
        return report;
    }

    private static RemediationOptions DefaultOptions()
        => new RemediationOptions { Language = "en-US", Title = "Quarterly Report" };

    private static RemediationStepResult AltTextStep(RemediationReport report)
        => Assert.Single(report.Steps.Where(s => s.Name == "SetFigureAltText"));

    /// <summary>
    /// Re-opens the produced document and reads <c>/Alt</c> off the first Figure element the
    /// structure tree holds. Reading the parsed graph — not the raw bytes — is what proves the
    /// value is in the current revision rather than a superseded one.
    /// </summary>
    private static string FirstFigureAltText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var root = doc.ResolveObject(
            doc.Catalog.Dictionary[new PdfName("StructTreeRoot")]) as PdfDictionary;
        Assert.NotNull(root);

        var pending = new Queue<PdfDictionary>();
        pending.Enqueue(root);

        // The fixture's tree is three deep and acyclic; a bounded loop keeps the helper honest
        // even so.
        for (int guard = 0; guard < 64 && pending.Count > 0; guard++)
        {
            PdfDictionary element = pending.Dequeue();

            if ((element[new PdfName("S")] as PdfName)?.Value == "Figure")
                return (doc.ResolveObject(element[new PdfName("Alt")]) as PdfString)?.GetText();

            PdfObject k = doc.ResolveObject(element[new PdfName("K")]);
            var kids = k as PdfArray;

            if (kids != null)
            {
                for (int i = 0; i < kids.Count; i++)
                    if (doc.ResolveObject(kids[i]) is PdfDictionary kid) pending.Enqueue(kid);
            }
            else if (k is PdfDictionary single)
            {
                pending.Enqueue(single);
            }
        }

        return null;
    }

    // =====================================================================
    //  No factory — the placeholder is written, and it is declared
    // =====================================================================

    [Fact]
    public void Pipeline_WithNoAltTextFactory_WritesThePlaceholder()
    {
        RemediationReport report = Remediate(
            TaggedPdfWithUndescribedFigure(), DefaultOptions(), out byte[] output);

        RemediationStepResult step = AltTextStep(report);
        Assert.True(step.Applied, "the alt-text step did not run: " + step);
        Assert.Equal(1, step.ItemsChanged);

        // Pins the exact text, so a future change to the placeholder has to come here first.
        Assert.Equal("Figure 1", FirstFigureAltText(output));
    }

    [Fact]
    public void Pipeline_WithNoAltTextFactory_WarnsThatTheDescriptionWasGenerated()
    {
        RemediationReport report = Remediate(
            TaggedPdfWithUndescribedFigure(), DefaultOptions(), out _);

        RemediationStepResult step = AltTextStep(report);

        Assert.Contains(step.Warnings, w => w.Contains("generated placeholder"));

        // The warning has to reach AllWarnings — that is the list a caller reads to find out what
        // is still wrong. Without it, a clean after-audit is the only thing they see.
        Assert.Contains(report.AllWarnings,
            w => w.Contains("AltTextFactory") && w.Contains("UA07"));
    }

    [Fact]
    public void Pipeline_WithNoAltTextFactory_WarnsInTheVeryRunThatClosesUa07()
    {
        // The damaging case is not "the warning exists somewhere" but "the warning exists in the
        // run where the check goes green": UA07 disappearing is what a caller triages on, so the
        // placeholder disclosure has to be attached to that same run or it is never read.
        RemediationReport report = Remediate(
            TaggedPdfWithUndescribedFigure(), DefaultOptions(), out _);

        Assert.Contains("UA07", report.BeforeAudit.Issues.Select(i => i.Code));
        Assert.DoesNotContain("UA07", report.AfterAudit.Issues.Select(i => i.Code));

        // Asserted against the step's own warnings, not report.HasWarnings: unrelated steps warn
        // on this fixture too, so the report-wide flag would be true with the defect present and
        // the test would prove nothing.
        Assert.NotEmpty(AltTextStep(report).Warnings);
    }

    // =====================================================================
    //  With a factory — the caller's text is written, and no placeholder warning
    // =====================================================================

    [Fact]
    public void Pipeline_WithAnAltTextFactory_WritesTheCallersDescription()
    {
        RemediationOptions options = DefaultOptions();
        options.AltTextFactory = ctx =>
            string.IsNullOrEmpty(ctx.ExistingAltText)
                ? "Bar chart of quarterly revenue by region."
                : null;

        RemediationReport report = Remediate(
            TaggedPdfWithUndescribedFigure(), options, out byte[] output);

        Assert.True(AltTextStep(report).Applied);
        Assert.Equal("Bar chart of quarterly revenue by region.", FirstFigureAltText(output));
    }

    [Fact]
    public void Pipeline_WithAnAltTextFactory_DoesNotWarnAboutPlaceholders()
    {
        RemediationOptions options = DefaultOptions();
        options.AltTextFactory = _ => "Bar chart of quarterly revenue by region.";

        RemediationReport report = Remediate(TaggedPdfWithUndescribedFigure(), options, out _);

        Assert.DoesNotContain(report.AllWarnings, w => w.Contains("generated placeholder"));
    }
}
