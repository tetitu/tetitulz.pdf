// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// Locks in the structural-detection fix from §40.P10:
/// <see cref="Tetitulz.Pdf.Core.Document.PdfPageTree"/> recognises page leaf
/// nodes that omit <c>/Type /Page</c> but carry <c>/Contents</c> or
/// <c>/MediaBox</c> with no <c>/Kids</c>.  Many real-world PDF generators
/// omit <c>/Type</c> on page nodes; viewers like another reader and another library accept
/// such documents.
/// </summary>
public class PageTreeStructuralTests
{
    [Fact]
    public void PageTree_LeafWithoutTypeButWithContents_IsRecognised()
    {
        // Build a minimal PDF whose page node has /Contents but no /Type.
        byte[] pdf = BuildPdfWithUntypedPageNode();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.Single(doc.Pages);
    }

    [Fact]
    public void PageTree_LeafWithoutTypeButWithMediaBox_IsRecognised()
    {
        byte[] pdf = BuildPdfWithUntypedPageNodeOnlyMediaBox();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.Single(doc.Pages);
    }

    // ==================================================================

    private static byte[] BuildPdfWithUntypedPageNode()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        // Object 1: catalog
        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // Object 2: pages root with one kid
        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Object 3: page leaf — NO /Type entry, but has /Contents and /MediaBox.
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n");

        // Object 4: empty content stream
        long o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        long xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithUntypedPageNodeOnlyMediaBox()
    {
        // Same shape but page node has /MediaBox only (no /Contents).
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>\nendobj\n");

        long xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
