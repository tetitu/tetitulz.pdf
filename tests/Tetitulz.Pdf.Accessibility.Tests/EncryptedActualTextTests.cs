// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfStructureTreeEditor.PlanActualText"/> — <c>/ActualText</c> on an <b>encrypted</b>
/// document, in place. The seventh and last of this type's operations to gain a <c>Plan</c>.
/// </summary>
/// <remarks>
/// <para>It is not in the remediation pipeline, so nothing was blocked on it. It exists so the set
/// is complete: a caller reaching for <c>/ActualText</c> on an encrypted document should not be the
/// one person who discovers a hole, and "six of seven" is the state GAPS §5c recorded as the
/// remaining item.</para>
/// <para><c>/ActualText</c> is what a screen reader announces <i>instead of</i> the marked content —
/// the standard use is a ligature or a stylised span whose glyphs do not spell the word. Reading it
/// back through the decryptor is the assertion that matters: a plan that staged clear-text into an
/// encrypted file would still satisfy a "the element has /ActualText" check made on raw bytes.</para>
/// </remarks>
public sealed class EncryptedActualTextTests
{
    private const string UserPassword  = "user-pw";
    private const string OwnerPassword = "owner-pw";

    public EncryptedActualTextTests() => PdfEncryptionOperations.Register();

    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_SetsActualTextAndKeepsBothPasswords()
    {
        byte[] encrypted = Encrypt(TaggedPdfWithSpan());

        byte[] result = AppendActualText(encrypted, OwnerPassword, "Span", "fluffy");

        // In place: the original bytes survive byte-for-byte.
        Assert.True(result.Length > encrypted.Length);
        Assert.Equal(encrypted, result.Take(encrypted.Length).ToArray());

        Assert.Equal(new[] { "fluffy" }, ActualTexts(result, UserPassword));
        Assert.Equal(new[] { "fluffy" }, ActualTexts(result, OwnerPassword));
    }

    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_DoesNotLeaveTheTextInClearText()
    {
        byte[] encrypted = Encrypt(TaggedPdfWithSpan());

        byte[] result = AppendActualText(encrypted, OwnerPassword, "Span", "Confidential");

        Assert.DoesNotContain("Confidential", Latin1(result), StringComparison.Ordinal);
        // Control: it IS there once decrypted, so the assertion above measures encryption rather
        // than the text never having been written.
        Assert.Equal(new[] { "Confidential" }, ActualTexts(result, UserPassword));
    }

    /// <summary>
    /// Only elements of the requested type are touched. Matching on the wrong type would silently
    /// annotate the whole document, which a count-only assertion would not notice.
    /// </summary>
    [Fact]
    public void Plan_MatchesOnlyTheRequestedElementType()
    {
        StructureTreePlan spans = PlanOver(TaggedPdfWithSpan(), "Span", (_, __) => "x");
        StructureTreePlan paras = PlanOver(TaggedPdfWithSpan(), "P", (_, __) => "x");
        StructureTreePlan none  = PlanOver(TaggedPdfWithSpan(), "Table", (_, __) => "x");

        Assert.Equal(1, spans.ItemsChanged);
        Assert.Equal(1, paras.ItemsChanged);
        Assert.False(none.WouldChangeDocument);
    }

    /// <summary>A provider returning null leaves the element alone rather than writing empty text.</summary>
    [Fact]
    public void Plan_WhenTheProviderDeclines_StagesNothing()
    {
        StructureTreePlan plan = PlanOver(TaggedPdfWithSpan(), "Span", (_, __) => null);

        Assert.False(plan.WouldChangeDocument);
        Assert.Equal(0, plan.ItemsChanged);
    }

    /// <summary>
    /// A throwing provider propagates, matching the clear-text path exactly. This differs from
    /// <c>PlanFigureAltText</c>, which tolerates and counts — the point is that each plan matches
    /// its own clear-text sibling, not that all plans behave alike.
    /// </summary>
    [Fact]
    public void Plan_WhenTheProviderThrows_PropagatesLikeTheClearTextPath()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PlanOver(TaggedPdfWithSpan(), "Span",
                (_, __) => throw new InvalidOperationException("provider is broken")));
    }

    [Fact]
    public void Plan_WithANullProviderOrType_Throws()
    {
        using var ms = new MemoryStream(TaggedPdfWithSpan(), writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);

        Assert.Throws<ArgumentNullException>(
            () => PdfStructureTreeEditor.PlanActualText(doc, 100, "Span", null));
        Assert.Throws<ArgumentNullException>(
            () => PdfStructureTreeEditor.PlanActualText(doc, 100, null, (_, __) => "x"));
    }

    // =====================================================================

    private static StructureTreePlan PlanOver(
        byte[] plain, string type, Func<int, string, string> provider)
    {
        using var ms = new MemoryStream(plain, writable: false);
        using PdfDocument doc = PdfDocument.Open(ms);
        return PdfStructureTreeEditor.PlanActualText(doc, 1000, type, provider);
    }

    private static byte[] AppendActualText(
        byte[] encrypted, string password, string type, string text)
    {
        using var input  = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();

        PdfEncryptedIncrementalUpdate.Run(input, output, password,
            (doc, firstFree) =>
                PdfStructureTreeEditor.PlanActualText(doc, firstFree, type, (_, __) => text).Objects);

        return output.ToArray();
    }

    private static List<string> ActualTexts(byte[] encrypted, string password)
    {
        using var input = new MemoryStream(encrypted, writable: false);
        using MemoryStream decrypted = PdfDecryptedInput.OpenDecrypted(input, password);
        using PdfDocument doc = PdfDocument.Open(decrypted);

        var found = new List<string>();
        for (int n = 1; n <= 60; n++)
        {
            if (doc.GetObject(n) is not PdfDictionary d) continue;
            if (d[PdfName.ActualText] is PdfString s) found.Add(s.GetText());
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

    /// <summary>A tagged page with one &lt;P&gt; containing one &lt;Span&gt; — two distinct types,
    /// so a type filter that matched everything would be visible.</summary>
    private static byte[] TaggedPdfWithSpan() =>
        FigureAltTextFabricationTests.BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            FigureAltTextFabricationTests.StreamObject(
                "/P <</MCID 0>> BDC\nBT /F1 10 Tf 72 700 Td (The cat sat.) Tj ET\nEMC\n"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K [9 0 R] >>",
            "<< /Type /StructElem /S /Span /P 8 0 R /Pg 3 0 R /K 0 >>",
            "<< /Nums [0 [8 0 R 9 0 R]] >>",
        });
}
