// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExportOptions.MaxPages"/>: a parameter, unlimited by default.
/// A document longer than the limit ends at the last whole page within it — its footer drawn, the page count it
/// shows the pages written — and a <see cref="BuilderDiagnosticKind.PageLimitReached"/> diagnostic says so.
/// </summary>
public class PageLimitTests
{
    /// <summary>Eighty paragraphs of a few lines, a footer of "page N of M", and nothing else: about ten pages.</summary>
    private static Document LongDocument()
    {
        var doc = new Document();
        var section = new Section();
        var footer = new Footer();
        var line = new Paragraph();
        line.Runs.Add(new TextRun { Text = "page " + PdfExporter.PagePlaceholder + " of " + PdfExporter.NumPagesPlaceholder });
        footer.Paragraphs.Add(line);
        section.Footer = footer;
        for (int i = 0; i < 80; i++)
        {
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = $"Paragraph {i}: " + string.Concat(Enumerable.Repeat("words that fill a line ", 12)) });
            section.Children.Add(p);
        }
        doc.Sections.Add(section);
        return doc;
    }

    private static int Pages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.Pages.Count;
    }

    private static string PageText(byte[] pdf, int index)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc.Pages[index]);
    }

    [Fact]
    public void ADocumentPastTheLimit_EndsAtTheLimitWholeAndSaysSo()
    {
        var seen = new List<BuilderDiagnostic>();
        byte[] all = LongDocument().ToPdf(new PdfExportOptions());
        Assert.True(Pages(all) > 4, $"the control runs past the limit: {Pages(all)} pages");   // potency

        byte[] limited = LongDocument().ToPdf(new PdfExportOptions { MaxPages = 3, OnDiagnostic = seen.Add });
        Assert.Equal(3, Pages(limited));
        Assert.Contains("page 3 of 3", PageText(limited, 2), StringComparison.Ordinal);   // the last page whole, the count the pages written
        Assert.Single(seen, d => d.Kind == BuilderDiagnosticKind.PageLimitReached);
    }

    /// <summary>A document within the limit is untouched and nothing is said; a limit of exactly its length the edge.</summary>
    [Fact]
    public void ADocumentWithinTheLimit_IsWholeAndSilent()
    {
        int length = Pages(LongDocument().ToPdf(new PdfExportOptions()));
        var seen = new List<BuilderDiagnostic>();
        byte[] atLimit = LongDocument().ToPdf(new PdfExportOptions { MaxPages = length, OnDiagnostic = seen.Add });
        Assert.Equal(length, Pages(atLimit));
        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.PageLimitReached);
    }

    /// <summary>
    /// An export's <see cref="PdfExportOptions.OnDiagnostic"/> hears that export's reports and no later one's — not one
    /// made outside any export: the handler is the thread's for the export's duration, then the previous one is back.
    /// </summary>
    [Fact]
    public void AnExportsHandler_HearsOnlyThatExport()
    {
        var first = new List<BuilderDiagnostic>();
        LongDocument().ToPdf(new PdfExportOptions { MaxPages = 2, OnDiagnostic = first.Add });
        Assert.Single(first, d => d.Kind == BuilderDiagnosticKind.PageLimitReached);   // potency: it hears its own

        // Outside any export: the first export's handler is no longer the thread's.
        BuilderDiagnostics.ReportDroppedOpacity(0.5, "a test's report", "made outside an export");
        Assert.DoesNotContain(first, d => d.Detail.Contains("a test's report", StringComparison.Ordinal));

        var second = new List<BuilderDiagnostic>();
        LongDocument().ToPdf(new PdfExportOptions { MaxPages = 2 });
        LongDocument().ToPdf(new PdfExportOptions { MaxPages = 2, OnDiagnostic = second.Add });
        Assert.Single(first, d => d.Kind == BuilderDiagnosticKind.PageLimitReached);
        Assert.Single(second, d => d.Kind == BuilderDiagnosticKind.PageLimitReached);
    }

    [Fact]
    public void ANegativeLimit_IsRefused_AndZeroIsNoLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfExportOptions { MaxPages = -1 });
        Assert.Equal(0, new PdfExportOptions().MaxPages);
    }
}
