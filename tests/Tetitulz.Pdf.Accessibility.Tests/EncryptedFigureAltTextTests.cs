// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfStructureTreeEditor.PlanFigureAltText"/> — alternate text on the figures of an
/// <b>encrypted</b> document, in place, keeping both passwords.
/// </summary>
/// <remarks>
/// <para><b>Why this was missing.</b> Plan 11's W1-1 row recorded that all of
/// <c>PdfStructureTreeEditor</c>'s operations had gained a <c>Plan</c>, naming five. The type has
/// <b>seven</b>, and the two it omitted include <c>SetFigureAltText</c> — which is step 5 of the
/// nine-step remediation pipeline and the only source of the <c>/Alt</c> that PDF/UA-1 requires on
/// every figure. An encrypted pipeline built on that row would have run eight steps and reported
/// success while leaving every figure undescribed, which is a UA failure rather than a nuance.</para>
/// <para>The fixture is shared with <c>FigureAltTextFabricationTests</c> on purpose. These tests
/// exist to catch the encrypted path describing a different set of figures than the clear-text
/// path, and two separately hand-built fixtures could drift into agreeing.</para>
/// </remarks>
public sealed class EncryptedFigureAltTextTests
{
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";

    // The decryptor is resolved through the registered holder, so a reader path needs it present.
    public EncryptedFigureAltTextTests() => PdfEncryptionOperations.Register();

    /// <summary>
    /// The headline: an encrypted document's figure gets its <c>/Alt</c>, the file stays encrypted,
    /// and <b>both</b> passwords still work — the property that makes this worth having over
    /// <c>PdfEncryptedRoundTrip</c>, which rewrites every byte and so can preserve neither the owner
    /// password nor a signature.
    /// </summary>
    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_DescribesTheFigureAndKeepsBothPasswords()
    {
        byte[] encrypted = Encrypt(FigureAltTextFabricationTests.TaggedPdfWithUndescribedFigure());

        byte[] result = AppendAltText(encrypted, OwnerPassword, "A bar chart of revenue by region");

        // 1. It is still encrypted: the appended revision did not leave the document in clear text.
        Assert.True(result.Length > encrypted.Length);
        Assert.Equal(encrypted, result.Take(encrypted.Length).ToArray());

        // 2. The alt text is there, and is the text asked for -- read back through the decryptor,
        //    which is the only way to see it if the revision really was encrypted.
        Assert.Equal(
            new[] { "A bar chart of revenue by region" },
            FigureAltTexts(result, UserPassword));

        // 3. The owner password still opens it. A round trip cannot promise this.
        Assert.Equal(
            new[] { "A bar chart of revenue by region" },
            FigureAltTexts(result, OwnerPassword));
    }

    /// <summary>
    /// The <c>/Alt</c> must not be readable without the password. A plan that staged a clear-text
    /// string into an encrypted file would still satisfy a "the figure has alt text" assertion made
    /// against the raw bytes, which is why this asserts on the bytes directly.
    /// </summary>
    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_DoesNotLeaveTheAltTextInClearText()
    {
        byte[] encrypted = Encrypt(FigureAltTextFabricationTests.TaggedPdfWithUndescribedFigure());

        byte[] result = AppendAltText(encrypted, OwnerPassword, "Sensitive description");

        Assert.DoesNotContain("Sensitive description", Latin1(result), StringComparison.Ordinal);
        // Control: the same string IS recoverable once decrypted, so the assertion above is
        // measuring encryption rather than the text having never been written.
        Assert.Equal(new[] { "Sensitive description" }, FigureAltTexts(result, UserPassword));
    }

    /// <summary>
    /// The provider's <c>null</c> means "leave this figure alone", and it must be counted as a skip
    /// rather than staged as an empty <c>/Alt</c> — an empty <c>/Alt</c> is itself a UA-1 failure,
    /// and it is worse than none because it looks remediated.
    /// </summary>
    [Fact]
    public void Plan_WhenTheProviderDeclinesEveryFigure_StagesNothingAndCountsTheSkip()
    {
        FigureAltTextPlan plan = PlanOver(
            FigureAltTextFabricationTests.TaggedPdfWithUndescribedFigure(), _ => null);

        Assert.False(plan.WouldChangeDocument);
        Assert.Empty(plan.Objects);
        Assert.Equal(0, plan.PlannedUpdates);
        Assert.Equal(1, plan.Skipped);
        Assert.Equal(0, plan.Failed);
    }

    /// <summary>
    /// A throwing provider is tolerated per figure and counted as a failure — matching the
    /// clear-text path exactly, so the two routes cannot describe different sets of figures.
    /// </summary>
    [Fact]
    public void Plan_WhenTheProviderThrows_CountsAFailureInsteadOfAbandoningThePlan()
    {
        FigureAltTextPlan plan = PlanOver(
            FigureAltTextFabricationTests.TaggedPdfWithUndescribedFigure(),
            _ => throw new InvalidOperationException("provider is broken"));

        Assert.False(plan.WouldChangeDocument);
        Assert.Equal(0, plan.PlannedUpdates);
        Assert.Equal(0, plan.Skipped);
        // The count is what tells a caller "your provider failed" apart from "there were no
        // figures" -- returning an empty plan for both is how a silent UA failure ships.
        Assert.Equal(1, plan.Failed);
    }

    /// <summary>
    /// The figure index the provider sees must match the clear-text path's, because the pipeline's
    /// default provider derives the description from it ("Figure 1"). A divergence here would
    /// renumber every fabricated description without changing any count.
    /// </summary>
    [Fact]
    public void Plan_PassesTheSameFigureContextTheClearTextPathBuilds()
    {
        var seen = new List<(int Index, int Page, string Existing)>();

        PlanOver(FigureAltTextFabricationTests.TaggedPdfWithUndescribedFigure(),
            ctx => { seen.Add((ctx.FigureIndex, ctx.PageIndex, ctx.ExistingAltText)); return "x"; });

        var one = Assert.Single(seen);
        Assert.Equal(0, one.Index);
        Assert.Equal(0, one.Page);
        Assert.Null(one.Existing);
    }

    /// <summary>A ceiling below the document's own numbering is refused, not silently accepted.</summary>
    [Fact]
    public void Plan_WithAnInvalidCeiling_Throws()
    {
        byte[] plain = FigureAltTextFabricationTests.TaggedPdfWithUndescribedFigure();
        using var ms = new MemoryStream(plain, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);

        Assert.ThrowsAny<ArgumentException>(
            () => PdfStructureTreeEditor.PlanFigureAltText(doc, 0, _ => "x"));
    }

    [Fact]
    public void Plan_WithANullProvider_Throws()
    {
        byte[] plain = FigureAltTextFabricationTests.TaggedPdfWithUndescribedFigure();
        using var ms = new MemoryStream(plain, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);

        Assert.Throws<ArgumentNullException>(
            () => PdfStructureTreeEditor.PlanFigureAltText(doc, 100, null));
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static FigureAltTextPlan PlanOver(byte[] plain, Func<FigureContext, string> provider)
    {
        using var ms = new MemoryStream(plain, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        return PdfStructureTreeEditor.PlanFigureAltText(doc, 1000, provider);
    }

    private static byte[] AppendAltText(byte[] encrypted, string password, string altText)
    {
        using var input  = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();

        PdfEncryptedIncrementalUpdate.Run(input, output, password,
            (doc, firstFree) =>
                PdfStructureTreeEditor.PlanFigureAltText(doc, firstFree, _ => altText).Objects);

        return output.ToArray();
    }

    /// <summary>Every <c>/Figure</c> element's <c>/Alt</c>, read back through the decryptor.</summary>
    private static List<string> FigureAltTexts(byte[] encrypted, string password)
    {
        using var input = new MemoryStream(encrypted, writable: false);
        using MemoryStream decrypted = PdfDecryptedInput.OpenDecrypted(input, password);
        using PdfDocument doc = PdfDocument.Open(decrypted);

        var found = new List<string>();
        for (int n = 1; n <= 60; n++)
        {
            if (doc.GetObject(n) is not PdfDictionary d) continue;
            if (d[PdfName.S] is not PdfName role || role.Value != "Figure") continue;
            if (d[PdfName.Alt] is PdfString alt) found.Add(alt.GetText());
        }
        return found;
    }

    private static string Latin1(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length);
        foreach (byte b in bytes) sb.Append((char)b);
        return sb.ToString();
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
