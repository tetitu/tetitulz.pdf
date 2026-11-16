// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Locks in the §38.4 PdfTextExtractor Form-XObject recursion guard:
/// the extractor must terminate cleanly on a circular Form-XObject
/// reference graph (A → B → A) instead of stack-overflowing.
/// </summary>
public class TextExtractorRecursionTests
{
    [Fact]
    public void ExtractText_CircularFormXObject_DoesNotStackOverflow()
    {
        byte[] pdf = BuildPdfWithCircularFormXObjects();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        // Should return cleanly even though the page references Form XObject A,
        // which references B in its content stream, which references A back.
        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);
        Assert.NotNull(text);
    }

    /// <summary>
    /// Builds a PDF where the page Form-XObject resource graph is cyclic:
    /// page → /XObject /A; A's content stream invokes "/B Do"; B's content
    /// stream invokes "/A Do".
    /// </summary>
    private static byte[] BuildPdfWithCircularFormXObjects()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Page references both XObjects in resources, content stream invokes A.
        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Contents 4 0 R " +
                  "/Resources << /XObject << /A 5 0 R /B 6 0 R >> >> >>\nendobj\n");

        // Content stream: invokes /A
        const string pageContent = "/A Do\n";
        long o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}endstream\nendobj\n",
            pageContent.Length, pageContent);

        // Form XObject A: invokes /B (uses the page's resources by inheritance,
        // but we also list /B in A's own resource dict to keep the cycle tight).
        const string aContent = "/B Do\n";
        long o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] " +
            "/Resources << /XObject << /B 6 0 R >> >> " +
            "/Length {0} >>\nstream\n{1}endstream\nendobj\n",
            aContent.Length, aContent);

        // Form XObject B: invokes /A → cycle
        const string bContent = "/A Do\n";
        long o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] " +
            "/Resources << /XObject << /A 5 0 R >> >> " +
            "/Length {0} >>\nstream\n{1}endstream\nendobj\n",
            bContent.Length, bContent);

        long xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
