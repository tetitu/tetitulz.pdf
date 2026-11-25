// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A DOCX table row lays out at most <see cref="PdfLimits.MaxDocxTableColumns"/> cells: a row taller than a page
/// strokes every cell's borders on every page it spans, so cells times pages, both the file's, could write
/// gigabytes of borders. The cells past the limit are dropped and reported.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class TableColumnCapTests
{
    private static (string Text, List<string> Reports) Convert(int cells)
    {
        string row = string.Concat(Enumerable.Range(1, cells).Select(i => $"<w:tc><w:p><w:r><w:t>cell{i}</w:t></w:r></w:p></w:tc>"));
        byte[] docx = HostileDocxInputTests.RawDocx(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\"><w:body>"
            + "<w:tbl><w:tr>" + row + "</w:tr></w:tbl><w:p/></w:body></w:document>");

        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Collect(string m) { if (Environment.CurrentManagedThreadId == thread) reports.Add(m); }
        DocxToPdfConverter.DiagnosticReported += Collect;
        byte[] pdf;
        try { pdf = DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = true }); }
        finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return (Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc), reports);
    }

    [Fact]
    public void ARowPastTheColumnLimit_DrawsTheLimit_AndSaysSo()
    {
        try
        {
            PdfLimits.MaxDocxTableColumns = 3;
            var (text, reports) = Convert(5);

            Assert.Contains("cell3", text, StringComparison.Ordinal);
            Assert.DoesNotContain("cell4", text, StringComparison.Ordinal);
            Assert.Contains(reports, r => r.Contains("TableRenderer.TooManyColumns", StringComparison.Ordinal));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: at the limit every cell is drawn and nothing is said.</summary>
    [Fact]
    public void ARowAtTheColumnLimit_DrawsEveryCell()
    {
        try
        {
            PdfLimits.MaxDocxTableColumns = 5;
            var (text, reports) = Convert(5);

            Assert.Contains("cell5", text, StringComparison.Ordinal);
            Assert.DoesNotContain(reports, r => r.Contains("TableRenderer.TooManyColumns", StringComparison.Ordinal));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
