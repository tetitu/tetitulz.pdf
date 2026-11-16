// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A table cell's run read into pieces at its line breaks (<c>TableRenderer.WrapParagraph</c>). Each piece was
/// built by appending w:t elements to a string, and the run's font was resolved again for every piece — and
/// resolving asks the run for its w:rPr, which the SDK finds by searching the run's children — so a run of many
/// w:t and w:br was quadratic in its children (Wave 22, DocxToPdf-4:0). A w:tab in a cell was dropped and the
/// words on either side joined (4:8).
/// </summary>
public sealed class CellRunPiecesTests
{
    private static byte[] CellDocx(string run)
        => HostileDocxInputTests.RawDocx(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\"><w:body>"
            + "<w:tbl><w:tr><w:tc><w:p><w:r>" + run + "</w:r></w:p></w:tc></w:tr></w:tbl><w:p/></w:body></w:document>");

    private static long Allocated(int lines)
    {
        byte[] docx = CellDocx(string.Concat(Enumerable.Repeat("<w:t>a</w:t><w:br/>", lines)));
        long before = GC.GetAllocatedBytesForCurrentThread();
        DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = true });
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// Four times the lines cost about four times as much, not sixteen: 1 000 and 4 000 pairs of w:t and w:br in
    /// one cell's run. Measured after the fix, 11 MB for 2 000 and 23 MB for 4 000; before it, 1.0 GB and 4.1 GB.
    /// </summary>
    [Fact]
    public void ACellRunOfManyLineBreaks_CostsInProportionToItsLines()
    {
        Allocated(50);   // warm
        long thousand = Allocated(1000);
        long fourThousand = Allocated(4000);

        Assert.True(fourThousand < thousand * 8, $"4 000 lines allocated {fourThousand} bytes, 1 000 lines {thousand}");
    }

    /// <summary>A tab in a cell is drawn as a space between its words, and the approximation is said.</summary>
    [Fact]
    public void ATabInACell_IsDrawnAsASpace_AndSaid()
    {
        // The event is process-wide: only this thread's conversion is read.
        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Collect(string m) { if (Environment.CurrentManagedThreadId == thread) reports.Add(m); }
        DocxToPdfConverter.DiagnosticReported += Collect;
        byte[] pdf;
        try { pdf = DocxToPdfConverter.ConvertToBytes(CellDocx("<w:t>left</w:t><w:tab/><w:t>right</w:t>")); }
        finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        string text = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);
        Assert.Contains("left right", text, StringComparison.Ordinal);
        Assert.DoesNotContain("leftright", text, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("TableRenderer.TabInCell", StringComparison.Ordinal));
    }
}
