// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The remediation pipeline run against an <b>encrypted</b> document, in place — the capability the
/// <c>Plan</c> methods exist for.
/// </summary>
/// <remarks>
/// <para><b>One pipeline, not two.</b> Every gate — requested / applicable / superseded, the
/// single-extraction rule, the snapshot refresh between passes — is identical for an encrypted
/// document, so only the step <i>action</i> is substituted. Duplicating the pipeline is the
/// divergence class this repository keeps finding, and these tests assert the two routes agree
/// rather than that the encrypted one merely runs.</para>
/// <para><b>The safety property that matters most is negative:</b> a step with no encrypted route
/// must never fall through to its clear-text writer. That would append a revision the encryption
/// dictionary does not describe, producing a file no reader can open — from an operation the caller
/// asked for in order to improve the document. <c>SkipUnencryptable</c> is the mechanism, and it has
/// <b>no callers</b>: every step has an encrypted route. It is kept because the next step added may
/// need it, and because deleting it would make falling through the path of least resistance.</para>
/// </remarks>
public sealed class EncryptedPipelineOrchestrationTests
{
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";

    public EncryptedPipelineOrchestrationTests() => PdfEncryptionOperations.Register();

    /// <summary>
    /// The headline: an encrypted, untagged document comes back tagged, still encrypted, and still
    /// openable with <b>both</b> passwords.
    /// </summary>
    [Fact]
    public void Remediate_WithAnEncryptedRevisionWriter_TagsInPlaceAndKeepsBothPasswords()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        // The fixture must start untagged, or a /StructTreeRoot below proves nothing.
        Assert.False(HasStructTree(plain));

        byte[] encrypted = Encrypt(plain);
        RemediationReport report = Remediate(encrypted, OwnerPassword, out byte[] result);

        // 1. In place: the original bytes are a prefix, so signatures over them still verify and
        //    the owner password survives -- neither of which a full round trip can promise.
        Assert.True(result.Length > encrypted.Length);
        Assert.Equal(encrypted, result.Take(encrypted.Length).ToArray());

        // 2. It really was tagged, read back through the decryptor.
        Assert.True(HasStructTree(Decrypt(result, UserPassword)));
        Assert.True(HasStructTree(Decrypt(result, OwnerPassword)));

        // 3. The pipeline says so too -- and the step that did it is AutoTag, not something else.
        RemediationStepResult autoTag = StepNamed(report, "AutoTag");
        Assert.True(autoTag.Applied);
        Assert.True(autoTag.ItemsChanged > 0);
    }

    /// <summary>
    /// Without a writer the behaviour is exactly as before: every step skipped, with a reason, and
    /// the bytes passed through untouched. The capability is opt-in.
    /// </summary>
    [Fact]
    public void Remediate_OnAnEncryptedDocumentWithNoWriter_StillRefusesAndSaysHow()
    {
        byte[] encrypted = Encrypt(ContentBearingPdfTests.ContentPdf());

        using var input  = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(
            input, output, new RemediationOptions { AutoTagIfUntagged = true });

        Assert.Equal(encrypted, output.ToArray());
        Assert.All(report.Steps, s => Assert.False(s.Applied));
        // The refusal must name the way out, or the option is undiscoverable from the failure.
        Assert.Contains(report.Steps,
            s => s.SkipReason != null &&
                 s.SkipReason.Contains("EncryptedRevisionWriter", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>SetDocumentMetadata</c> now has an encrypted route: <c>/Lang</c>, <c>/Info /Title</c> and
    /// <c>/ViewerPreferences /DisplayDocTitle</c> all land, so UA04 and UA13 are reachable for an
    /// encrypted document.
    /// </summary>
    /// <remarks>
    /// This replaced a test pinning the step as unreachable. It was blocked on two things: the pass
    /// hand-builds one revision for four entries, and <c>PlanDocumentTitle</c>'s minting branch had
    /// no trailer channel. The channel shipped first; the pass then became three ordered cycles.
    /// <b>Reading the title back through <c>doc.Trailer[/Info]</c> is the assertion that matters</b>
    /// — a title in an object nothing references is exactly the failure the channel fixed.
    /// </remarks>
    [Fact]
    public void MetadataStep_OnAnEncryptedDocument_WritesLangTitleAndDisplayDocTitle()
    {
        byte[] encrypted = Encrypt(ContentBearingPdfTests.ContentPdf());

        RemediationReport report = Remediate(encrypted, OwnerPassword, out byte[] result,
            o => { o.Language = "en-GB"; o.Title = "Quarterly Report"; });

        RemediationStepResult metadata = StepNamed(report, "SetDocumentMetadata");
        Assert.True(metadata.Applied, metadata.SkipReason);

        byte[] plain = Decrypt(result, UserPassword);
        using var ms = new MemoryStream(plain, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);

        // /Lang on the catalog.
        var lang = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Lang]) as PdfString;
        Assert.NotNull(lang);
        Assert.Equal("en-GB", lang!.GetText());

        // The title, reached the way a reader reaches it -- through the trailer.
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        Assert.NotNull(info);
        var title = doc.ResolveObject(info![PdfName.Title]) as PdfString;
        Assert.NotNull(title);
        Assert.Equal("Quarterly Report", title!.GetText());

        // ...and the reader is told to display it. A title without this still fails UA04.
        var prefs = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.ViewerPreferences]) as PdfDictionary;
        Assert.NotNull(prefs);
        var display = doc.ResolveObject(prefs![PdfName.DisplayDocTitle]) as PdfBoolean;
        Assert.NotNull(display);
        Assert.True(display!.Value);
    }

    /// <summary>
    /// A caller who supplies a writer for a document that is <b>not</b> encrypted gets the
    /// clear-text path, unchanged. The writer is a capability for encrypted input, not a mode.
    /// </summary>
    [Fact]
    public void Remediate_WithAWriterButAnUnencryptedDocument_UsesTheClearTextPathUnchanged()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        bool writerCalled = false;
        var options = new RemediationOptions
        {
            AutoTagIfUntagged = true,
            EncryptedRevisionWriter = (i, o, plan) => { writerCalled = true; },
        };

        using var input  = new MemoryStream(plain, writable: false);
        using var output = new MemoryStream();
        PdfAccessibility.RemediateWithReport(input, output, options);

        Assert.False(writerCalled);
        Assert.True(HasStructTree(output.ToArray()));
    }

    /// <summary>
    /// The two routes must report the same steps as applied. A step that quietly does nothing under
    /// encryption while reporting success is the exact failure this whole split exists to avoid.
    /// </summary>
    [Fact]
    public void Remediate_EncryptedAndClearText_AgreeOnWhichStepsApplied()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        using var clearIn  = new MemoryStream(plain, writable: false);
        using var clearOut = new MemoryStream();
        RemediationReport clear = PdfAccessibility.RemediateWithReport(
            clearIn, clearOut, new RemediationOptions { AutoTagIfUntagged = true });

        RemediationReport enc = Remediate(Encrypt(plain), OwnerPassword, out _);

        // Every step must agree, including SetDocumentMetadata: no step is exempt from this
        // comparison.
        var clearApplied = clear.Steps.Where(s => s.Applied).Select(s => s.Name)
                                .OrderBy(n => n).ToList();
        var encApplied = enc.Steps.Where(s => s.Applied).Select(s => s.Name)
                                .OrderBy(n => n).ToList();

        Assert.Equal(clearApplied, encApplied);
        // Guard against the pair agreeing because both did nothing.
        Assert.NotEmpty(encApplied);
    }

    /// <summary>
    /// Every step the pipeline knows about is accounted for in the report — an encrypted run must
    /// not silently drop one. This is what would have caught the missing alt-text plan.
    /// </summary>
    [Fact]
    public void Remediate_Encrypted_ReportsAllTenSteps()
    {
        RemediationReport report = Remediate(
            Encrypt(ContentBearingPdfTests.ContentPdf()), OwnerPassword, out _);

        Assert.Equal(
            new[]
            {
                "AutoTag", "EmbedFonts", "FixHeadingHierarchy", "FixReadingOrder", "MarkArtifacts",
                "SetDocumentMetadata", "SetFigureAltText", "SetLinkDescriptions", "SetPdfUaIdentifier",
                "SetTabOrder",
            },
            report.Steps.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// A document whose XMP packet already carries the identifier is a step already satisfied on the
    /// encrypted route as on the clear-text one: Plan's empty answer with no error is "nothing to
    /// write", not a failure. Remediated twice, the second report skips the step and fails nothing.
    /// </summary>
    [Fact]
    public void Remediate_Encrypted_ReportsAnAlreadyCorrectIdentifierAsSatisfied_NotFailed()
    {
        RemediationReport first = Remediate(Encrypt(ContentBearingPdfTests.ContentPdf()), OwnerPassword, out byte[] once);
        Assert.True(StepNamed(first, "SetPdfUaIdentifier").Applied, "the first run must write the identifier");

        RemediationReport second = Remediate(once, OwnerPassword, out _);

        RemediationStepResult step = StepNamed(second, "SetPdfUaIdentifier");
        Assert.True(step.Skipped, "the identifier was already written, yet the step reports: " + step);
        Assert.Contains("already satisfied", step.SkipReason);
        Assert.False(second.HasFailedSteps, string.Join(" | ", second.Steps));
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static RemediationStepResult StepNamed(RemediationReport report, string name)
        => Assert.Single(report.Steps.Where(s => s.Name == name));

    private static RemediationReport Remediate(
        byte[] encrypted, string password, out byte[] result,
        Action<RemediationOptions> configure = null)
    {
        var options = new RemediationOptions
        {
            AutoTagIfUntagged = true,
            EncryptedRevisionWriter = (i, o, plan) =>
                PdfEncryptedIncrementalUpdate.Run(i, o, password, (doc, firstFree) =>
                {
                    PlannedRevision r = plan(doc, firstFree);
                    return new EncryptedRevision(r.Objects, r.NewInfoObjectNumber);
                }),
        };
        configure?.Invoke(options);

        using var input  = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();
        RemediationReport report = PdfAccessibility.RemediateWithReport(input, output, options);
        result = output.ToArray();
        return report;
    }

    private static bool HasStructTree(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        return doc.Catalog?.Dictionary?.ContainsKey(PdfName.StructTreeRoot) == true;
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var input = new MemoryStream(encrypted, writable: false);
        return PdfDecryptedInput.ReadDecrypted(input, password);
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
