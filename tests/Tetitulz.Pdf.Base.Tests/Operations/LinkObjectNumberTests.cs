// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfLink.ObjectNumber"/> and <see cref="PdfLinkExtractor.ExtractLinks(PdfDocument)"/>:
/// which annotation a link is, for a writer that revises it, over a document the caller holds.
/// </summary>
public sealed class LinkObjectNumberTests
{
    /// <summary>
    /// An indirect link names its object; a direct one, which nothing can supersede alone, is 0.
    /// The two overloads answer one question and are asked together.
    /// </summary>
    [Fact]
    public void ExtractLinks_NamesTheIndirectAnnotation_AndZeroForADirectOne()
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R " +
                "<< /Type /Annot /Subtype /Link /Rect [0 50 40 60] /A << /S /URI /URI (https://b.example/) >> >>] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 40 10] /A << /S /URI /URI (https://a.example/) >> >>");

        var fromStream = PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var fromDocument = PdfLinkExtractor.ExtractLinks(doc);

        Assert.Equal(new[] { 4, 0 }, fromStream.Select(l => l.ObjectNumber));
        Assert.Equal(new[] { "https://a.example/", "https://b.example/" }, fromStream.Select(l => l.Uri));
        Assert.Equal(fromStream.Select(l => (l.ObjectNumber, l.Uri, l.PageIndex)),
                     fromDocument.Select(l => (l.ObjectNumber, l.Uri, l.PageIndex)));
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
