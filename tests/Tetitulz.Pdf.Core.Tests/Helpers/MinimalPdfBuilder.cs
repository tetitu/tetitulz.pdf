// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;

namespace Tetitulz.Pdf.Core.Tests.Helpers;

/// <summary>
/// Builds minimal valid PDF files in-memory for testing.
/// </summary>
internal static class MinimalPdfBuilder
{
    /// <summary>
    /// Creates a minimal 1-page PDF with the given text content.
    /// </summary>
    public static byte[] CreateMinimalPdf(string textContent = "Hello World", string catalogVersion = null)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.ASCII, 1024, leaveOpen: true);

        // Content stream
        string contentStream = $"BT /F1 12 Tf 100 700 Td ({textContent}) Tj ET";
        byte[] contentBytes = Encoding.ASCII.GetBytes(contentStream);

        // Calculate object offsets
        writer.Flush();

        var sb = new StringBuilder();

        // Header
        sb.AppendLine("%PDF-1.4");
        sb.AppendLine("%\xe2\xe3\xcf\xd3");

        // Object 1: Catalog
        string obj1Marker = sb.Length.ToString(); // won't be exact, but we build manually
        // Let's build with exact byte offsets instead

        ms.Position = 0;
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1Offset = pdf.Length;
        pdf.Append("1 0 obj\n");
        pdf.Append(catalogVersion == null
            ? "<< /Type /Catalog /Pages 2 0 R >>\n"
            : $"<< /Type /Catalog /Pages 2 0 R /Version /{catalogVersion} >>\n");
        pdf.Append("endobj\n");

        int obj2Offset = pdf.Length;
        pdf.Append("2 0 obj\n");
        pdf.Append("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\n");
        pdf.Append("endobj\n");

        int obj3Offset = pdf.Length;
        pdf.Append("3 0 obj\n");
        pdf.Append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ");
        pdf.Append("/Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\n");
        pdf.Append("endobj\n");

        int obj4Offset = pdf.Length;
        pdf.Append("4 0 obj\n");
        pdf.AppendFormat("<< /Length {0} >>\n", contentBytes.Length);
        pdf.Append("stream\n");
        pdf.Append(contentStream);
        pdf.Append("\nendstream\n");
        pdf.Append("endobj\n");

        int obj5Offset = pdf.Length;
        pdf.Append("5 0 obj\n");
        pdf.Append("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\n");
        pdf.Append("endobj\n");

        int xrefOffset = pdf.Length;
        pdf.Append("xref\n");
        pdf.Append("0 6\n");
        pdf.AppendFormat("0000000000 65535 f \n");
        pdf.AppendFormat("{0:D10} 00000 n \n", obj1Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj2Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj3Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj4Offset);
        pdf.AppendFormat("{0:D10} 00000 n \n", obj5Offset);

        pdf.Append("trailer\n");
        pdf.Append("<< /Size 6 /Root 1 0 R /Info 6 0 R >>\n");
        pdf.AppendFormat("startxref\n{0}\n", xrefOffset);
        pdf.Append("%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>
    /// Creates a multi-page PDF.
    /// </summary>
    public static byte[] CreateMultiPagePdf(int pageCount)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        // Calculate object numbers:
        // 1 = Catalog
        // 2 = Pages
        // 3 = Font
        // 4..4+pageCount-1 = Page objects
        // 4+pageCount..4+2*pageCount-1 = Content stream objects

        int fontObjNum = 3;
        int firstPageObj = 4;
        int firstContentObj = firstPageObj + pageCount;
        int totalObjects = firstContentObj + pageCount;

        var offsets = new int[totalObjects + 1];

        // Object 1: Catalog
        offsets[1] = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // Object 2: Pages
        offsets[2] = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [");
        for (int i = 0; i < pageCount; i++)
        {
            if (i > 0) pdf.Append(" ");
            pdf.AppendFormat("{0} 0 R", firstPageObj + i);
        }
        pdf.AppendFormat("] /Count {0} >>\nendobj\n", pageCount);

        // Object 3: Font
        offsets[3] = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // Content streams first (so we know lengths)
        var contentTexts = new string[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            contentTexts[i] = $"BT /F1 12 Tf 100 700 Td (Page {i + 1}) Tj ET";
        }

        // Page objects
        for (int i = 0; i < pageCount; i++)
        {
            offsets[firstPageObj + i] = pdf.Length;
            pdf.AppendFormat("{0} 0 obj\n", firstPageObj + i);
            pdf.Append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] ");
            pdf.AppendFormat("/Contents {0} 0 R ", firstContentObj + i);
            pdf.AppendFormat("/Resources << /Font << /F1 {0} 0 R >> >> >>\n", fontObjNum);
            pdf.Append("endobj\n");
        }

        // Content stream objects
        for (int i = 0; i < pageCount; i++)
        {
            offsets[firstContentObj + i] = pdf.Length;
            pdf.AppendFormat("{0} 0 obj\n", firstContentObj + i);
            pdf.AppendFormat("<< /Length {0} >>\n", contentTexts[i].Length);
            pdf.Append("stream\n");
            pdf.Append(contentTexts[i]);
            pdf.Append("\nendstream\nendobj\n");
        }

        // Xref
        int xrefOffset = pdf.Length;
        pdf.Append("xref\n");
        pdf.AppendFormat("0 {0}\n", totalObjects);
        pdf.Append("0000000000 65535 f \n");
        for (int i = 1; i < totalObjects; i++)
        {
            pdf.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        }

        pdf.Append("trailer\n");
        pdf.AppendFormat("<< /Size {0} /Root 1 0 R >>\n", totalObjects);
        pdf.AppendFormat("startxref\n{0}\n", xrefOffset);
        pdf.Append("%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
