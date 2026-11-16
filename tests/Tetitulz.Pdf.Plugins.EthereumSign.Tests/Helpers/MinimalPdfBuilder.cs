// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests.Helpers;

internal static class MinimalPdfBuilder
{
    public static byte[] CreateMinimalPdf(string textContent = "Hello World")
    {
        string contentStream = $"BT /F1 12 Tf 100 700 Td ({textContent}) Tj ET";
        byte[] contentBytes = Encoding.ASCII.GetBytes(contentStream);

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int obj1Offset = pdf.Length;
        pdf.Append("1 0 obj\n");
        pdf.Append("<< /Type /Catalog /Pages 2 0 R >>\n");
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
}
