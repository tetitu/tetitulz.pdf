// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The full rewriter — redaction's, templating's and the colour converter's writer — writes every
/// object the catalog reaches, however long the chain of references that reaches it.
/// </summary>
/// <remarks>
/// Its walk recursed through each reference and counted it against
/// <c>PdfLimits.MaxObjectGraphDepth</c>, two levels a link, so an outline's 128th sibling was the
/// last one written: its <c>/Next</c> came out as <c>0 0 R</c> and the bookmarks after it were gone.
/// </remarks>
public sealed class FullRewriteReachabilityTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(300)]
    public void AnOutlineOfManySiblings_IsWrittenWhole(int items)
    {
        using var doc = PdfDocument.Open(new MemoryStream(OutlinePdf(items)));
        using var ms = new MemoryStream();
        Assert.True(PdfFullRewriter.TryFullRewrite(doc, ms));

        using var written = PdfDocument.Open(new MemoryStream(ms.ToArray()));
        var outlines = written.ResolveObject(written.Catalog.Dictionary[PdfName.Outlines]) as PdfDictionary;
        var item = written.ResolveObject(outlines?[PdfName.First]) as PdfDictionary;
        var titles = new List<string>();
        while (item != null && titles.Count <= items)
        {
            titles.Add((item[PdfName.Title] as PdfString)?.GetText());
            item = written.ResolveObject(item[PdfName.Next]) as PdfDictionary;
        }

        Assert.Equal(items, titles.Count);
        Assert.Equal("Item " + items, titles[items - 1]);
    }

    // A one-page document whose outline is `items` siblings, object 5 onwards, each naming the next.
    private static byte[] OutlinePdf(int items)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            $"<< /Type /Outlines /First 5 0 R /Last {4 + items} 0 R /Count {items} >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
        };
        for (int i = 1; i <= items; i++)
            bodies.Add($"<< /Title (Item {i}) /Parent 3 0 R /Dest [4 0 R /Fit]" +
                       (i < items ? $" /Next {5 + i} 0 R" : "") + " >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
