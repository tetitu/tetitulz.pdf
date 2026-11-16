// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Extraction;

/// <summary>
/// One page's structured text from a document the caller holds open answers what the stream door
/// answers for the page — the door a viewer indexing page by page uses instead of opening the
/// document once a page (Wave 22, SmallPlugins+Viewer-1:3).
/// </summary>
public sealed class StructuredTextOpenDocumentTests
{
    [Fact]
    public void APageFromAnOpenDocument_IsTheStreamDoorsPage()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Quokka Burrow");

        PdfPageTextLayout fromStream = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfPageTextLayout fromDocument = PdfStructuredTextExtractor.Extract(doc, 0);

        Assert.Contains("Quokka", string.Join(" ", fromDocument.Lines.Select(l => l.Text)), System.StringComparison.Ordinal);
        Assert.Equal(fromStream.Lines.Select(l => (l.Text, l.X, l.Y)), fromDocument.Lines.Select(l => (l.Text, l.X, l.Y)));
    }

    /// <summary>A page outside the document is refused, as by the stream door.</summary>
    [Fact]
    public void APageOutsideTheDocument_IsRefused()
    {
        using var doc = PdfDocument.Open(new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf()));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => PdfStructuredTextExtractor.Extract(doc, 1));
    }
}
