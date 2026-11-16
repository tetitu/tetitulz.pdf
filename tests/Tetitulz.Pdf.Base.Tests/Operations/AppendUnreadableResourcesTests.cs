// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page whose <c>/Resources</c> names an object the parser cannot read is refused by the content
/// appender: the page reads as having no resources, and the merged dictionary it wrote replaced the
/// reference with the stamp's entries alone, so a reader that could parse the object lost the
/// page's fonts. The control, a readable indirect <c>/Resources</c>, is merged and kept.
/// </summary>
public class AppendUnreadableResourcesTests
{
    [Fact]
    public void UnreadableResources_AreRefused_NotReplaced()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageContentAppender.AppendContent(new MemoryStream(Pdf(readable: false)), output, 0, b => b.Rectangle(0, 0, 10, 10).Fill()));

        Assert.Contains("/Resources could not be read", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ReadableIndirectResources_AreKept()
    {
        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContent(new MemoryStream(Pdf(readable: true)), output, 0, b => b.Rectangle(0, 0, 10, 10).Fill());

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var fonts = doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        var f1 = doc.ResolveObject(fonts![new PdfName("F1")]) as PdfDictionary;
        Assert.Equal("Helvetica", (doc.ResolveObject(f1?[PdfName.BaseFont]) as PdfName)?.Value);
        // And the content was appended: the page's /Contents is now the original bracketed with the new stream.
        var contents = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]));
        Assert.True(contents.Count >= 2, $"/Contents holds {contents.Count} stream(s)");
    }

    /// <summary>One page with <c>/Resources 5 0 R</c>; object 5 is a font dictionary or a stray <c>]</c>.</summary>
    private static byte[] Pdf(bool readable)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources 5 0 R >>",
            "<< /Length 32 >>\nstream\nBT /F1 12 Tf 10 10 Td (x) Tj ET\nendstream",
            readable ? "<< /Font << /F1 6 0 R >> >>" : "]",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
