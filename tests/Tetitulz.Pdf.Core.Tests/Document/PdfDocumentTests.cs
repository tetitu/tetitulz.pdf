// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Tests.Helpers;

namespace Tetitulz.Pdf.Core.Tests.Document;

public class PdfDocumentTests
{
    [Fact]
    public void Open_MinimalPdf_ReturnsDocument()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.NotNull(doc);
        Assert.Equal(1, doc.Version.Major);
        Assert.Equal(4, doc.Version.Minor);
    }

    [Fact]
    public void Open_MinimalPdf_HasOnePage()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf();
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.Single(doc.Pages);
    }

    [Fact]
    public void Open_MinimalPdf_HasCatalog()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf();
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.NotNull(doc.Catalog);
        Assert.Equal("Catalog", doc.Catalog.Type?.Value);
    }

    [Fact]
    public void Open_MinimalPdf_PageHasMediaBox()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf();
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        var page = doc.Pages[0];
        Assert.NotNull(page.MediaBox);
        Assert.Equal(4, page.MediaBox.Count);
        Assert.Equal(612, page.MediaBox.GetDouble(2));
        Assert.Equal(792, page.MediaBox.GetDouble(3));
    }

    [Fact]
    public void Open_MultiPagePdf_CorrectPageCount()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMultiPagePdf(5);
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.Equal(5, doc.Pages.Count);
    }

    [Fact]
    public void Open_MinimalPdf_HasTrailer()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf();
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        // /Size is "one greater than the largest object number" (§7.5.5). The fixture has five
        // objects, so 6 — a value, not merely a non-null.
        Assert.NotNull(doc.Trailer);
        Assert.Equal(6, doc.Trailer.GetInteger(PdfName.Size));
        Assert.NotNull(doc.Trailer[PdfName.Root]);
    }

    [Fact]
    public void Open_MinimalPdf_PageHasContentStream()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Test Content");
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        // The content stream must be the one the fixture wrote — a non-empty byte array could be
        // any other object in the file, or a decode that silently produced garbage.
        var page = doc.Pages[0];
        string content = System.Text.Encoding.ASCII.GetString(page.GetContentStreamData());

        Assert.Contains("Test Content", content);
        Assert.Contains("Tj", content);
    }

    [Fact]
    public void Open_MinimalPdf_NoAcroForm()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf();
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.Null(doc.AcroForm);
    }

    [Fact]
    public void Open_MinimalPdf_HasMetadata()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf();
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        // This fixture has no /Info dictionary, so the contract is "usable but empty": a metadata
        // object is still returned, and every field reads back as null rather than throwing or
        // reporting a fabricated value. Asserting the emptiness is what distinguishes that from an
        // implementation that invents a Producer — "not null" alone cannot.
        Assert.NotNull(doc.Metadata);
        Assert.Null(doc.Metadata.Title);
        Assert.Null(doc.Metadata.Author);
        Assert.Null(doc.Metadata.Producer);
    }

    [Fact]
    public void Open_CatalogVersionHigherThanHeader_OverridesEffectiveVersion()
    {
        // §7.5.2: a header of 1.4 with a catalog /Version of 2.0 must report 2.0 — the case a
        // PDF 2.0 file with a conservative header for older-reader compatibility relies on.
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf(catalogVersion: "2.0");
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.Equal(2, doc.Version.Major);
        Assert.Equal(0, doc.Version.Minor);
    }

    [Fact]
    public void Open_CatalogVersionLowerThanHeader_DoesNotDowngrade()
    {
        // A catalog /Version that is LOWER than the header must not win — only a raise is
        // trusted, so a corrupt or adversarial override can't silently downgrade version-gated
        // behaviour (e.g. AES-256's PDF 2.0 minimum) elsewhere in the library.
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf(catalogVersion: "1.0");
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.Equal(1, doc.Version.Major);
        Assert.Equal(4, doc.Version.Minor);
    }

    [Fact]
    public void Open_MalformedCatalogVersion_IgnoredWithoutThrowing()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf(catalogVersion: "not-a-version");
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.Equal(1, doc.Version.Major);
        Assert.Equal(4, doc.Version.Minor);
    }

    [Fact]
    public void Open_NoCatalogVersion_UsesHeaderVersion()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf();
        using var stream = new MemoryStream(pdfBytes);
        using var doc = PdfDocument.Open(stream);

        Assert.Equal(1, doc.Version.Major);
        Assert.Equal(4, doc.Version.Minor);
    }
}
