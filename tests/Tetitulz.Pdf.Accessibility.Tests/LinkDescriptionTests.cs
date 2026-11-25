// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfStructureTreeEditor.SetLinkDescriptions"/>, its plan for the encrypted route, and
/// the remediation step <see cref="RemediationOptions.LinkDescriptionFactory"/> drives: a link's
/// <c>/Contents</c>, its alternate description under ISO 14289-1 §7.18.5, which UA22 checks.
/// </summary>
public sealed class LinkDescriptionTests
{
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";

    public LinkDescriptionTests() => PdfEncryptionOperations.Register();

    // =====================================================================
    //  Fixtures
    // =====================================================================

    private const string UriLink =
        "<< /Type /Annot /Subtype /Link /Rect [72 395 220 415] /F 4 " +
        "/A << /S /URI /URI (https://example.com/report) >> >>";

    private const string DescribedLink =
        "<< /Type /Annot /Subtype /Link /Rect [72 300 220 320] /F 4 /Contents (Old description) " +
        "/A << /S /URI /URI (https://example.com/old) >> >>";

    private const string HiddenLink =
        "<< /Type /Annot /Subtype /Link /Rect [72 200 220 220] /F 2 " +
        "/A << /S /URI /URI (https://example.com/hidden) >> >>";

    /// <summary>One page of text; the annotations become objects 6, 7, … in order.</summary>
    private static byte[] OnePage(params string[] annotations)
    {
        string annots = annotations.Length == 0
            ? string.Empty
            : "/Annots [" + string.Join(" ", annotations.Select((_, i) => (6 + i).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "] ";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " + annots +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            Stream("BT /F1 12 Tf 72 400 Td (Read the annual report.) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        bodies.AddRange(annotations);
        return Build(bodies);
    }

    private static string Stream(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // =====================================================================
    //  The clear-text writer
    // =====================================================================

    /// <summary>
    /// The headline: the description reaches the annotation's <c>/Contents</c>, and the UA22 the
    /// input carried is gone from the audit of the output.
    /// </summary>
    [Fact]
    public void SetLinkDescriptions_WritesContents_AndClosesUa22()
    {
        byte[] input = OnePage(UriLink);
        Assert.Contains(Audit(input).Issues, i => i.Code == "UA22");

        SetAltTextResult result = Set(input, _ => "Opens the annual report", out byte[] output);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Failed);
        Assert.Equal("Opens the annual report", ContentsOf(output, 6));
        Assert.DoesNotContain(Audit(output).Issues, i => i.Code == "UA22");
    }

    /// <summary>
    /// What the provider decides from: the link's page, its place in page order, its target as
    /// <c>PdfLinkExtractor</c> reads it, and the description it carries now.
    /// </summary>
    [Fact]
    public void SetLinkDescriptions_OffersEachLinksPageTargetAndPresentDescription()
    {
        var seen = new List<LinkContext>();
        Set(OnePage(UriLink, DescribedLink), ctx => { seen.Add(ctx); return null; }, out _);

        Assert.Equal(2, seen.Count);
        Assert.Equal(new[] { 0, 1 }, seen.Select(c => c.LinkIndex));
        Assert.All(seen, c => Assert.Equal(0, c.PageIndex));
        Assert.Equal(PdfLinkKind.Uri, seen[0].Link.Kind);
        Assert.Equal("https://example.com/report", seen[0].Link.Uri);
        Assert.Equal(6, seen[0].Link.ObjectNumber);
        Assert.Null(seen[0].ExistingDescription);
        Assert.Equal("Old description", seen[1].ExistingDescription);
        Assert.Equal(7, seen[1].Link.ObjectNumber);
    }

    /// <summary>
    /// <c>null</c> leaves a link, and so does a blank description — an empty <c>/Contents</c> is
    /// what UA22 reports as missing. Both are skips, and the audit still reports the link.
    /// </summary>
    [Fact]
    public void SetLinkDescriptions_NullOrBlank_LeavesTheLinkAndCountsASkip()
    {
        byte[] input = OnePage(UriLink, UriLink);

        SetAltTextResult result = Set(input, ctx => ctx.LinkIndex == 0 ? null : "   ", out byte[] output);

        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Skipped);
        Assert.Null(ContentsOf(output, 6));
        Assert.Null(ContentsOf(output, 7));
        Assert.Contains(Audit(output).Issues, i => i.Code == "UA22");
    }

    /// <summary>
    /// A hidden link is presented to no reader (the scope UA22 checks), so it is not offered; the
    /// visible one beside it is.
    /// </summary>
    [Fact]
    public void SetLinkDescriptions_HiddenLink_IsNotOffered()
    {
        var offered = new List<int>();
        Set(OnePage(HiddenLink, UriLink), ctx => { offered.Add(ctx.Link.ObjectNumber); return "x"; }, out _);

        Assert.Equal(new[] { 7 }, offered);
    }

    /// <summary>One annotation two pages list is one link, offered once, written once.</summary>
    [Fact]
    public void SetLinkDescriptions_AnnotationTwoPagesList_IsOfferedOnce()
    {
        byte[] input = Build(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [6 0 R] >>",
            "<< >>",
            UriLink,
        });

        var offered = new List<int>();
        SetAltTextResult result = Set(input, ctx => { offered.Add(ctx.PageIndex); return "Opens the report"; }, out byte[] output);

        Assert.Equal(new[] { 0 }, offered);
        Assert.Equal(1, result.Updated);
        Assert.Equal("Opens the report", ContentsOf(output, 6));
    }

    /// <summary>
    /// A link held as a direct dictionary has no object number to revise: it is counted as failed
    /// and not offered, while the indirect link beside it is described.
    /// </summary>
    [Fact]
    public void SetLinkDescriptions_DirectLink_IsCountedFailedAndNotOffered()
    {
        byte[] input = Build(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R " + UriLink + "] >>",
            UriLink,
        });

        var offered = new List<int>();
        SetAltTextResult result = Set(input, ctx => { offered.Add(ctx.Link.ObjectNumber); return "Opens the report"; }, out _);

        Assert.Equal(new[] { 4 }, offered);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.Failed);
    }

    /// <summary>
    /// The clear-text writer and the plan answer one question, so they are asked together: the
    /// same counts, and the plan's staged annotation carrying the text the writer wrote.
    /// </summary>
    [Fact]
    public void SetAndPlan_AgreeOnWhatIsDescribed()
    {
        byte[] input = OnePage(UriLink, DescribedLink, HiddenLink);
        Func<LinkContext, string> provider = ctx => ctx.ExistingDescription == null ? "New " + ctx.LinkIndex : null;

        SetAltTextResult set = Set(input, provider, out byte[] output);

        using var ms = new MemoryStream(input, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        FigureAltTextPlan plan = PdfStructureTreeEditor.PlanLinkDescriptions(doc, 1000, provider);

        Assert.Equal((1, 1, 0), (set.Updated, set.Skipped, set.Failed));
        Assert.Equal((set.Updated, set.Skipped, set.Failed), (plan.PlannedUpdates, plan.Skipped, plan.Failed));
        var staged = Assert.Single(plan.Objects);
        Assert.Equal(6, staged.ObjectNumber);
        Assert.Equal("New 0", ((PdfString)((PdfDictionary)staged.Object)[PdfName.Contents]).GetText());
        Assert.Equal("New 0", ContentsOf(output, 6));
    }

    /// <summary>
    /// The links still undescribed after the step: one the provider declined (null) or answered
    /// blank that has no <c>/Contents</c> of its own. A link it declined because it already has one
    /// needs nothing and is not counted. The writer and the plan agree, and a lost revision, which
    /// adds no description, keeps the count.
    /// </summary>
    [Fact]
    public void LeftWithoutDescription_CountsDeclinedLinksWithNoContentsOfTheirOwn()
    {
        byte[] input = OnePage(UriLink, UriLink, DescribedLink);
        Func<LinkContext, string> provider = ctx => ctx.LinkIndex == 1 ? "  " : null;

        SetAltTextResult set = Set(input, provider, out _);

        using var ms = new MemoryStream(input, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        FigureAltTextPlan plan = PdfStructureTreeEditor.PlanLinkDescriptions(doc, 1000, provider);

        Assert.Equal(3, set.Skipped);
        Assert.Equal(2, set.LeftWithoutDescription);
        Assert.Equal(2, plan.LeftWithoutDescription);
        Assert.Equal(2, plan.Describe(revisionWasAppended: true).LeftWithoutDescription);
        Assert.Equal(2, plan.Describe(revisionWasAppended: false).LeftWithoutDescription);
    }

    [Fact]
    public void SetLinkDescriptions_NullProvider_Throws()
        => Assert.Throws<ArgumentNullException>(() => Set(OnePage(UriLink), null, out _));

    // =====================================================================
    //  Encrypted
    // =====================================================================

    /// <summary>
    /// Through the encrypted writer the description lands encrypted: readable once decrypted,
    /// absent from the raw bytes, the original revision kept byte for byte.
    /// </summary>
    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_DescribesTheLinkEncrypted()
    {
        byte[] encrypted = Encrypt(OnePage(UriLink));

        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();
        PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
            (doc, firstFree) => PdfStructureTreeEditor.PlanLinkDescriptions(doc, firstFree, _ => "Secret link text").Objects);
        byte[] result = output.ToArray();

        Assert.Equal(encrypted, result.Take(encrypted.Length).ToArray());
        Assert.DoesNotContain("Secret link text", Encoding.Latin1.GetString(result), StringComparison.Ordinal);
        Assert.Equal("Secret link text", ContentsOf(PdfDecryptedInput.ReadDecrypted(new MemoryStream(result), UserPassword), 6));
    }

    /// <summary>The clear-text writer copies an encrypted document through rather than edit it.</summary>
    [Fact]
    public void SetLinkDescriptions_Encrypted_CopiesThrough()
    {
        byte[] encrypted = Encrypt(OnePage(UriLink));

        SetAltTextResult result = Set(encrypted, _ => "x", out byte[] output);

        Assert.Equal(0, result.Updated);
        Assert.Equal(encrypted, output);
    }

    // =====================================================================
    //  The remediation step
    // =====================================================================

    /// <summary>
    /// With a factory the step runs and UA22 is closed in the after-audit; without one (the
    /// control) the step is skipped and UA22 remains.
    /// </summary>
    [Fact]
    public void Remediate_WithAFactory_ClosesUa22_AndWithoutOneLeavesIt()
    {
        byte[] input = OnePage(UriLink);

        RemediationReport described = Remediate(input, o => o.LinkDescriptionFactory = _ => "Opens the annual report", out byte[] output);
        RemediationReport left = Remediate(input, _ => { }, out _);

        RemediationStepResult ran = Assert.Single(described.Steps, s => s.Name == "SetLinkDescriptions");
        Assert.True(ran.Applied, ran.ToString());
        Assert.Equal(1, ran.ItemsChanged);
        Assert.DoesNotContain(described.AfterAudit.Issues, i => i.Code == "UA22");
        Assert.Equal("Opens the annual report", ContentsOf(output, 6));

        RemediationStepResult skipped = Assert.Single(left.Steps, s => s.Name == "SetLinkDescriptions");
        Assert.True(skipped.Skipped);
        Assert.Contains("LinkDescriptionFactory", skipped.SkipReason);
        Assert.Contains(left.AfterAudit.Issues, i => i.Code == "UA22");
    }

    /// <summary>
    /// The auto-tagger's UA22 warning — a link it tagged has no <c>/Contents</c> — is said only
    /// where nothing after it will describe the link. With a factory the link step follows, the
    /// tagger is silent and the run carries no such warning; without one (the control) the tagger
    /// says it, as it does when tagging alone.
    /// </summary>
    [Fact]
    public void Remediate_WithAFactory_TheTaggerLeavesTheLinkToTheLinkStep()
    {
        byte[] input = OnePage(UriLink);

        RemediationReport described = Remediate(input, o => o.LinkDescriptionFactory = _ => "Opens the annual report", out _);
        RemediationReport left = Remediate(input, _ => { }, out _);

        RemediationStepResult tagged = Assert.Single(described.Steps, s => s.Name == "AutoTag");
        Assert.True(tagged.Applied, tagged.ToString());
        Assert.DoesNotContain(described.AllWarnings, w => w.Contains("no /Contents alternate description", StringComparison.Ordinal));

        RemediationStepResult taggedAlone = Assert.Single(left.Steps, s => s.Name == "AutoTag");
        Assert.True(taggedAlone.Applied, taggedAlone.ToString());
        Assert.Contains(taggedAlone.Warnings, w => w.StartsWith("1 Link annotation(s) have no /Contents alternate description", StringComparison.Ordinal));
    }

    /// <summary>
    /// A link the factory gives no description is said once, by the link step, after it ran: the
    /// undescribed link counted and the one that keeps its own <c>/Contents</c> not, and the
    /// after-audit agrees that UA22 is open. The tagger says nothing of either.
    /// </summary>
    [Fact]
    public void Remediate_FactoryDecliningALink_TheLinkStepSaysWhatIsStillOpen()
    {
        byte[] input = OnePage(UriLink, DescribedLink);

        RemediationReport report = Remediate(input, o => o.LinkDescriptionFactory = _ => null, out _);

        RemediationStepResult tagged = Assert.Single(report.Steps, s => s.Name == "AutoTag");
        Assert.True(tagged.Applied, tagged.ToString());
        string warning = Assert.Single(report.AllWarnings, w => w.Contains("no /Contents alternate description", StringComparison.Ordinal));
        Assert.StartsWith("[SetLinkDescriptions] 1 link annotation(s) have no /Contents alternate description",
            warning, StringComparison.Ordinal);
        Assert.Contains(report.AfterAudit.Issues, i => i.Code == "UA22");
    }

    /// <summary>The step's encrypted route: the description is written, the file stays encrypted.</summary>
    [Fact]
    public void Remediate_Encrypted_WritesTheDescriptionThroughTheWriter()
    {
        byte[] encrypted = Encrypt(OnePage(UriLink));

        RemediationReport report = Remediate(encrypted, o =>
        {
            o.LinkDescriptionFactory = _ => "Opens the annual report";
            o.EncryptedRevisionWriter = (i, w, plan) =>
                PdfEncryptedIncrementalUpdate.Run(i, w, OwnerPassword, (doc, firstFree) =>
                {
                    PlannedRevision r = plan(doc, firstFree);
                    return new EncryptedRevision(r.Objects, r.NewInfoObjectNumber);
                });
        }, out byte[] output);

        RemediationStepResult step = Assert.Single(report.Steps, s => s.Name == "SetLinkDescriptions");
        Assert.True(step.Applied, step.ToString());
        Assert.Equal(1, step.ItemsChanged);
        byte[] decrypted = PdfDecryptedInput.ReadDecrypted(new MemoryStream(output), UserPassword);
        Assert.Equal("Opens the annual report", LinkContents(decrypted).Single());
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static SetAltTextResult Set(byte[] input, Func<LinkContext, string> provider, out byte[] output)
    {
        using var i = new MemoryStream(input, writable: false);
        using var o = new MemoryStream();
        SetAltTextResult result = PdfAccessibility.SetLinkDescriptions(i, o, provider);
        output = o.ToArray();
        return result;
    }

    private static RemediationReport Remediate(byte[] input, Action<RemediationOptions> configure, out byte[] output)
    {
        var options = new RemediationOptions();
        configure(options);
        using var i = new MemoryStream(input, writable: false);
        using var o = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(i, o, options);
        output = o.ToArray();
        return report;
    }

    private static AccessibilityAuditReport Audit(byte[] pdf)
        => PdfAccessibility.Audit(new MemoryStream(pdf, writable: false));

    private static string ContentsOf(byte[] pdf, int objectNumber)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        var annot = (PdfDictionary)doc.GetObject(objectNumber);
        return (doc.ResolveObject(annot[PdfName.Contents]) as PdfString)?.GetText();
    }

    /// <summary>Every <c>/Contents</c> of the annotations the pages list, read as a reader reaches them.</summary>
    private static List<string> LinkContents(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        var found = new List<string>();
        foreach (PdfPage page in doc.Pages)
        {
            PdfArray annots = page.GetAnnotations();
            if (annots == null) continue;
            foreach (PdfObject entry in annots)
                if (doc.ResolveObject(entry) is PdfDictionary a && (doc.ResolveObject(a[PdfName.Contents]) is PdfString s))
                    found.Add(s.GetText());
        }
        return found;
    }

    private static byte[] Encrypt(byte[] plain)
    {
        using var input  = new MemoryStream(plain, writable: false);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }
}
