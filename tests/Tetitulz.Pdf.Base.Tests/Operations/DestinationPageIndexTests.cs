// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A destination's page is found through the document's index of its pages by object number: a
/// scan of the pages for each destination cost links, outline items and named destinations × pages.
/// </summary>
public class DestinationPageIndexTests
{
    [Fact]
    public void ADestinationsPage_IsFoundThroughTheDocumentsIndex()
    {
        using var doc = PdfDocument.Open(new MemoryStream(ThreePages()));
        Assert.False(doc.PageIndexBuilt);

        var dest = new PdfArray { new PdfReference(5, 0), new PdfName("Fit") };
        Assert.Equal(2, PdfDestinationPage.IndexFromDestArray(doc, dest));
        Assert.True(doc.PageIndexBuilt);

        Assert.Equal(0, PdfDestinationPage.IndexOfPage(doc, new PdfReference(3, 0)));
        Assert.Equal(PdfDestinationPage.NotFound, PdfDestinationPage.IndexOfPage(doc, new PdfReference(1, 0)));
        Assert.Equal(1, PdfDestinationPage.IndexOfPage(doc, new PdfNumber(1)));
    }

    private static byte[] ThreePages()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
