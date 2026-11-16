// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>Tests for structure-tree editing operations exposed via PdfAccessibility.</summary>
public class PdfStructureTreeEditorTests
{
    // ---------------------------------------------------------------
    //  SetDocumentLanguage
    // ---------------------------------------------------------------

    [Fact]
    public void SetDocumentLanguage_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetDocumentLanguage(null, new MemoryStream(), "en-US"));
    }

    [Fact]
    public void SetDocumentLanguage_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetDocumentLanguage(
                new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null, "en-US"));
    }

    /// <summary>
    /// The language must land in the produced document's catalog <c>/Lang</c> — the only place a
    /// reader or a PDF/UA checker looks (UA02). The predecessor discarded the return value and
    /// asserted only that bytes came back, which a no-op passthrough satisfies.
    /// </summary>
    [Fact]
    public void SetDocumentLanguage_WritesTheLanguageToTheCatalog()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();

        bool ok = PdfAccessibility.SetDocumentLanguage(input, output, "en-US");

        Assert.True(ok);
        Assert.Equal("en-US", CatalogLanguage(output.ToArray()));
    }

    /// <summary>
    /// Setting the language on a document that already has one must <b>replace</b> it, not append
    /// a second entry that leaves a reader to pick. The old name — "ReturnsTrueOrProducesBytes" —
    /// offered two alternative outcomes and so committed to neither.
    /// </summary>
    [Fact]
    public void SetDocumentLanguage_OnADocumentThatAlreadyHasOne_ReplacesIt()
    {
        byte[] withEnglish;
        using (var i = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()))
        using (var o = new MemoryStream())
        {
            Assert.True(PdfAccessibility.SetDocumentLanguage(i, o, "en-US"));
            withEnglish = o.ToArray();
        }
        Assert.Equal("en-US", CatalogLanguage(withEnglish));

        using var input  = new MemoryStream(withEnglish);
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentLanguage(input, output, "fr-FR"));

        // The catalog resolves to exactly one language, and it is the new one.
        Assert.Equal("fr-FR", CatalogLanguage(output.ToArray()));
    }

    // ---------------------------------------------------------------
    //  SetDocumentTitle
    // ---------------------------------------------------------------

    [Fact]
    public void SetDocumentTitle_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetDocumentTitle(null, new MemoryStream(), "Title"));
    }

    [Fact]
    public void SetDocumentTitle_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetDocumentTitle(
                new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null, "Title"));
    }

    /// <summary>
    /// The title must land in <c>/Info /Title</c> of the produced document — that is what closes
    /// UA04, and a title nobody can read back has not been set.
    /// </summary>
    [Fact]
    public void SetDocumentTitle_WritesTheTitleToTheInfoDictionary()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();

        bool ok = PdfAccessibility.SetDocumentTitle(input, output, "My Accessible Document");

        Assert.True(ok);
        Assert.Equal("My Accessible Document", InfoTitle(output.ToArray()));
    }

    // ---------------------------------------------------------------
    //  SetFigureAltText (string overload)
    // ---------------------------------------------------------------

    [Fact]
    public void SetFigureAltText_String_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetFigureAltText(null, new MemoryStream(), "alt"));
    }

    [Fact]
    public void SetFigureAltText_String_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetFigureAltText(
                new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null, "alt"));
    }

    [Fact]
    public void SetFigureAltText_String_NoPdfFigures_ReturnsZeroUpdated()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        var result = PdfAccessibility.SetFigureAltText(input, output, "Decorative image");
        // No Figure elements exist in minimal PDF
        Assert.Equal(0, result.Updated);
        Assert.True(output.Length > 0);
    }

    // ---------------------------------------------------------------
    //  SetFigureAltText (callback overload)
    // ---------------------------------------------------------------

    [Fact]
    public void SetFigureAltText_Callback_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetFigureAltText(null, new MemoryStream(),
                ctx => $"Figure {ctx.FigureIndex + 1}"));
    }

    [Fact]
    public void SetFigureAltText_Callback_NoPdfFigures_ReturnsZero()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        SetAltTextResult result = PdfAccessibility.SetFigureAltText(
            input, output,
            ctx => $"Figure {ctx.FigureIndex + 1}");
        Assert.Equal(0, result.Updated);
        Assert.True(output.Length > 0);
    }

    // ---------------------------------------------------------------
    //  SetActualText
    // ---------------------------------------------------------------

    [Fact]
    public void SetActualText_MinimalPdf_ProducesOutput()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        int count = PdfAccessibility.SetActualText(
            input, output, "P", (i, existing) => existing ?? $"Para {i}");
        Assert.True(output.Length > 0);
        Assert.True(count >= 0);
    }

    // ---------------------------------------------------------------
    //  FixHeadingHierarchy
    // ---------------------------------------------------------------

    [Fact]
    public void FixHeadingHierarchy_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.FixHeadingHierarchy(null, new MemoryStream()));
    }

    [Fact]
    public void FixHeadingHierarchy_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.FixHeadingHierarchy(
                new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null));
    }

    [Fact]
    public void FixHeadingHierarchy_MinimalPdf_ProducesOutput()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        int fixed_ = PdfAccessibility.FixHeadingHierarchy(input, output);
        Assert.True(output.Length > 0);
        Assert.True(fixed_ >= 0);
    }

    /// <summary>
    /// A document whose heading hierarchy needs no repair must come back <b>byte for byte</b> with
    /// a reported count of zero.
    ///
    /// <para>This is the feature-wide invariant read from the quiet end: <i>a reported count above
    /// zero implies the output differs from the input</i>. A fixer that appended an empty revision
    /// — or renumbered headings that were already correct — would still "produce output" and still
    /// satisfy the predecessor.</para>
    /// </summary>
    [Fact]
    public void FixHeadingHierarchy_NothingToRepair_ReportsZeroAndLeavesTheBytesAlone()
    {
        byte[] source = PdfAccessibilityAuditTests.TaggedPdf();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();

        int renumbered = PdfAccessibility.FixHeadingHierarchy(input, output);

        Assert.Equal(0, renumbered);
        Assert.Equal(source, output.ToArray());
    }

    // ---------------------------------------------------------------
    //  SetFieldTooltip
    // ---------------------------------------------------------------

    [Fact]
    public void SetFieldTooltip_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetFieldTooltip(null, new MemoryStream(), "Name", "Full name"));
    }

    [Fact]
    public void SetFieldTooltip_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.SetFieldTooltip(
                new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null,
                "Name", "Full name"));
    }

    [Fact]
    public void SetFieldTooltip_NoFormFields_ReturnsFalse()
    {
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        bool ok = PdfAccessibility.SetFieldTooltip(input, output, "Name", "Full name");
        // No fields in minimal PDF, so returns false but still copies bytes
        Assert.False(ok);
        Assert.True(output.Length > 0);
    }

    // ---------------------------------------------------------------
    //  FigureContext model
    // ---------------------------------------------------------------

    [Fact]
    public void FigureContext_Constructor_SetsProperties()
    {
        var ctx = new FigureContext(3, 1, "existing alt");
        Assert.Equal(3,             ctx.FigureIndex);
        Assert.Equal(1,             ctx.PageIndex);
        Assert.Equal("existing alt", ctx.ExistingAltText);
    }

    [Fact]
    public void SetAltTextResult_Constructor_SetsProperties()
    {
        var r = new SetAltTextResult(updated: 2, skipped: 1, failed: 0);
        Assert.Equal(2, r.Updated);
        Assert.Equal(1, r.Skipped);
        Assert.Equal(0, r.Failed);
    }

    // ------------------------------------------------------------------
    //  Read-back helpers — these editors are only meaningful if the produced document can be
    //  reopened and the value found where a reader would look for it.
    // ------------------------------------------------------------------

    private static string CatalogLanguage(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return (doc.ResolveObject(doc.Catalog?.Dictionary[PdfName.Lang]) as PdfString)?.GetText();
    }

    private static string InfoTitle(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        return (doc.ResolveObject(info?[PdfName.Title]) as PdfString)?.GetText();
    }
}
