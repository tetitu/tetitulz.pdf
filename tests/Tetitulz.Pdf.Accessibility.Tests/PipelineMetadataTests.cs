// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The remediation pipeline's document metadata. Its <c>/Lang</c> is
/// <see cref="RemediationOptions.Language"/>'s alone: null leaves the entry as it is and says UA02
/// stays open, on both routes; the auto-tagger, which runs first, writes no language of its own
/// default there. Tagging alone keeps that default. Its title reaches <c>dc:title</c>, where
/// ISO 14289-1 7.1 reads it, from the document's own <c>/Info /Title</c> when no title is given.
/// </summary>
public sealed class PipelineMetadataTests
{
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";
    private const string Ua02StaysOpen = "no Language was supplied and the catalog has no /Lang, so UA02 remains open";

    public PipelineMetadataTests() => PdfEncryptionOperations.Register();

    /// <summary>
    /// A document whose title is only in <c>/Info</c>, remediated with no title given: the
    /// identifier step carries it to <c>dc:title</c>, so the after-audit has no UA04. The control
    /// has no title anywhere and keeps UA04 — nothing is made up.
    /// </summary>
    [Fact]
    public void Remediate_NoTitleGiven_TheDocumentsInfoTitleReachesDcTitle()
    {
        RemediationReport titled = Remediate(Untagged(info: "<< /Title (Annual Report) >>"), o => o.Title = null, out byte[] output);
        RemediationReport untitled = Remediate(Untagged(), o => o.Title = null, out _);

        Assert.DoesNotContain(titled.AfterAudit.Issues, i => i.Code == "UA04");
        Assert.Contains(">Annual Report<", XmpOf(output), StringComparison.Ordinal);
        Assert.Contains(untitled.AfterAudit.Issues, i => i.Code == "UA04");
    }

    /// <summary>
    /// Null asks for no language: the output declares none, the metadata step says UA02 stays open,
    /// and the after-audit reports it. The control asks for one, which is written and closes UA02.
    /// </summary>
    [Fact]
    public void Remediate_LanguageNull_DeclaresNoneAndSaysUa02StaysOpen()
    {
        RemediationReport none = Remediate(Untagged(), o => o.Language = null, out byte[] noneOutput);
        RemediationReport hebrew = Remediate(Untagged(), o => o.Language = "he-IL", out byte[] hebrewOutput);

        Assert.True(Assert.Single(none.Steps, s => s.Name == "AutoTag").Applied);
        Assert.Null(LanguageOf(noneOutput));
        Assert.Contains(none.AllWarnings, w => w.Contains(Ua02StaysOpen, StringComparison.Ordinal));
        Assert.Contains(none.AfterAudit.Issues, i => i.Code == "UA02");

        Assert.Equal("he-IL", LanguageOf(hebrewOutput));
        Assert.DoesNotContain(hebrew.AllWarnings, w => w.Contains(Ua02StaysOpen, StringComparison.Ordinal));
        Assert.DoesNotContain(hebrew.AfterAudit.Issues, i => i.Code == "UA02");
    }

    /// <summary>
    /// The tagger's own <see cref="AutoTagOptions.Language"/> does not decide the pipeline's
    /// <c>/Lang</c>, whichever way the two disagree; tagging alone (the control) writes it.
    /// </summary>
    [Fact]
    public void Remediate_TaggersLanguage_DoesNotDecideThePipelines()
    {
        Remediate(Untagged(), o => { o.Language = "he-IL"; o.AutoTagOptions = new AutoTagOptions { Language = "fr-FR" }; },
            out byte[] hebrew);
        Remediate(Untagged(), o => { o.Language = null; o.AutoTagOptions = new AutoTagOptions { Language = "fr-FR" }; },
            out byte[] none);

        using var input = new MemoryStream(Untagged(), writable: false);
        using var output = new MemoryStream();
        PdfAccessibility.AutoTag(input, output, new AutoTagOptions { Language = "fr-FR" });

        Assert.Equal("he-IL", LanguageOf(hebrew));
        Assert.Null(LanguageOf(none));
        Assert.Equal("fr-FR", LanguageOf(output.ToArray()));
    }

    /// <summary>
    /// The default options still declare "en-US" — through the metadata step, which now writes it.
    /// </summary>
    [Fact]
    public void Remediate_DefaultOptions_DeclareEnUsThroughTheMetadataStep()
    {
        RemediationReport report = Remediate(Untagged(), _ => { }, out byte[] output);

        Assert.Equal("en-US", LanguageOf(output));
        RemediationStepResult metadata = Assert.Single(report.Steps, s => s.Name == "SetDocumentMetadata");
        Assert.True(metadata.Applied, metadata.ToString());
    }

    /// <summary>
    /// A title the encrypted route wrote — <c>/Info</c>, <c>/DisplayDocTitle</c> and <c>dc:title</c> —
    /// is found already there on a second run, which appends no title revision; the first run (the
    /// control) wrote it.
    /// </summary>
    [Fact]
    public void Remediate_EncryptedTitleWrittenOnce_IsAlreadySatisfiedOnTheSecondRun()
    {
        RemediationReport first = Remediate(Encrypt(Untagged()), o => { o.Title = "Annual Report"; Writer(o); }, out byte[] once);
        RemediationReport second = Remediate(once, o => { o.Title = "Annual Report"; Writer(o); }, out _);

        Assert.True(Assert.Single(first.Steps, s => s.Name == "SetDocumentMetadata").ItemsChanged > 0);
        RemediationStepResult again = Assert.Single(second.Steps, s => s.Name == "SetDocumentMetadata");
        Assert.Equal(0, again.ItemsChanged);
        Assert.Contains("/Info /Title (with /DisplayDocTitle and dc:title) already holds", again.SkipReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The encrypted route carries the document's own <c>/Info /Title</c> to <c>dc:title</c> as the
    /// clear-text route does, on a page with nothing to tag — where no PDF/UA identifier is written
    /// to carry it. The control has no title anywhere and gets none.
    /// </summary>
    [Fact]
    public void Remediate_EncryptedNoTitleGiven_TheDocumentsInfoTitleReachesDcTitle()
    {
        Remediate(Encrypt(Untagged(info: "<< /Title (Annual Report) >>", content: "")), o => { o.Title = null; Writer(o); },
            out byte[] titled);
        Remediate(Encrypt(Untagged(content: "")), o => { o.Title = null; Writer(o); }, out byte[] untitled);

        string titledXmp = XmpOf(PdfDecryptedInput.ReadDecrypted(new MemoryStream(titled), UserPassword));
        string untitledXmp = XmpOf(PdfDecryptedInput.ReadDecrypted(new MemoryStream(untitled), UserPassword));
        Assert.Contains(">Annual Report<", titledXmp, StringComparison.Ordinal);
        Assert.True(untitledXmp == null || !untitledXmp.Contains("dc:title", StringComparison.Ordinal));
    }

    /// <summary>
    /// The encrypted route says what the clear-text route says: with no language asked for, the
    /// decrypted document is read and UA02 is said to stay open; with one (the control), it is
    /// written and nothing is said.
    /// </summary>
    [Fact]
    public void Remediate_EncryptedLanguageNull_SaysUa02StaysOpenAsTheClearTextRouteDoes()
    {
        RemediationReport none = Remediate(Encrypt(Untagged()), o => { o.Language = null; Writer(o); }, out byte[] noneOutput);
        RemediationReport hebrew = Remediate(Encrypt(Untagged()), o => { o.Language = "he-IL"; Writer(o); }, out byte[] hebrewOutput);

        Assert.True(Assert.Single(none.Steps, s => s.Name == "AutoTag").Applied);
        RemediationStepResult metadata = Assert.Single(none.Steps, s => s.Name == "SetDocumentMetadata");
        Assert.Contains(metadata.Warnings, w => w.StartsWith(Ua02StaysOpen, StringComparison.Ordinal));
        Assert.Null(LanguageOf(PdfDecryptedInput.ReadDecrypted(new MemoryStream(noneOutput), UserPassword)));

        Assert.Equal("he-IL", LanguageOf(PdfDecryptedInput.ReadDecrypted(new MemoryStream(hebrewOutput), UserPassword)));
        Assert.DoesNotContain(hebrew.AllWarnings, w => w.Contains(Ua02StaysOpen, StringComparison.Ordinal));
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    /// <summary>One untagged page of text, no /Lang.</summary>
    /// <summary>
    /// One untagged page of <paramref name="content"/>, no /Lang; <paramref name="info"/>, when given,
    /// is object 6 and the trailer's /Info.
    /// </summary>
    private static byte[] Untagged(string info = null, string content = "BT /F1 12 Tf 72 700 Td (Hello world.) Tj ET")
    {
        string Content = content;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + Content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + Content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (info != null) bodies.Add(info);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        string size = (bodies.Count + 1).ToString(CultureInfo.InvariantCulture);
        sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R").Append(info != null ? " /Info 6 0 R" : "").Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string XmpOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        var stream = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata]) as PdfStream;
        return stream == null ? null : Encoding.UTF8.GetString(stream.GetDecodedData());
    }

    private static void Writer(RemediationOptions o)
        => o.EncryptedRevisionWriter = (i, w, plan) =>
            PdfEncryptedIncrementalUpdate.Run(i, w, OwnerPassword, (doc, firstFree) =>
            {
                PlannedRevision r = plan(doc, firstFree);
                return new EncryptedRevision(r.Objects, r.NewInfoObjectNumber);
            });

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

    private static string LanguageOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        var catalog = (PdfDictionary)doc.ResolveObject(doc.Trailer[PdfName.Root]);
        return (doc.ResolveObject(catalog[PdfName.Lang]) as PdfString)?.GetText();
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
