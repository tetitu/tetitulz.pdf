// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A document's pages by object number, built once: a destination, an outline item and a document
/// part each name their page by reference, and a scan of the pages for each cost destinations ×
/// pages.
/// </summary>
public class PageIndexOfObjectTests
{
    /// <summary>
    /// Each page object answers its index, as a scan of the pages finds it, and anything else -1.
    /// </summary>
    [Fact]
    public void EachPageObject_AnswersItsIndex_AndAnythingElseNone()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pages("3 0 R 4 0 R 5 0 R")));
        Assert.Equal(3, doc.Pages.Count);

        Assert.False(doc.PageIndexBuilt);
        Assert.Equal(0, doc.PageIndexOfObject(3));
        Assert.True(doc.PageIndexBuilt);
        Assert.Equal(1, doc.PageIndexOfObject(4));
        Assert.Equal(2, doc.PageIndexOfObject(5));
        Assert.Equal(-1, doc.PageIndexOfObject(1));     // the catalog: an object, not a page
        Assert.Equal(-1, doc.PageIndexOfObject(99));
        Assert.Equal(-1, doc.PageIndexOfObject(0));
        Assert.Equal(-1, doc.PageIndexOfObject(-3));
    }

    private static byte[] Pages(string kids)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count 3 /MediaBox [0 0 612 792] >>",
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
