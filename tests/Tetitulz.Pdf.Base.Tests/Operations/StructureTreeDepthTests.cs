// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Locks in §43.SEC-4: PdfStructureTreeReader enforces a 1000-level
/// depth cap on the /StructTreeRoot → /K chain so adversarial PDFs
/// with deeply-nested struct elements can't cause stack-overflow DoS
/// during tag-tree extraction.
///
/// Real tag trees rarely exceed double-digit depth; 1000 is generous.
/// </summary>
public class StructureTreeDepthTests
{
    [Fact]
    public void ReadStructureTree_DeepLinearChain_ThrowsBeforeStackOverflow()
    {
        // Build a struct tree where every element has /K = the next element,
        // forming a 5000-deep linear chain.  Each unique dict bypasses the
        // cycle detector, so the depth bound is the only line of defence.
        byte[] pdf = BuildPdfWithDeepStructTree(depth: 5000);
        using var ms = new MemoryStream(pdf);

        // The reader must throw a managed exception before recursing 5000
        // frames deep.  Pre-§43 this would have stack-overflowed.
        var ex = Assert.ThrowsAny<System.Exception>(() =>
            PdfStructureTreeReader.ReadStructureTree(ms));

        // The actual message is "Structure tree depth exceeds 1000…".
        Assert.Contains("depth", ex.Message,
            System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a PDF whose StructTreeRoot kicks off a chain of `depth`
    /// nested struct elements via /K.
    /// </summary>
    private static byte[] BuildPdfWithDeepStructTree(int depth)
    {
        // Layout:
        //   1 = catalog
        //   2 = pages
        //   3 = page
        //   4 = content stream
        //   5 = StructTreeRoot, /K → 6
        //   6..6+depth-1 = struct elements, each /K → next
        var sb = new StringBuilder();
        sb.Append("%PDF-1.5\n");

        long o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>\nendobj\n");

        long o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        long o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>\nendobj\n");

        long o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        long o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /StructTreeRoot /K 6 0 R >>\nendobj\n");

        var elementOffsets = new long[depth];
        for (int i = 0; i < depth; i++)
        {
            int objNum = 6 + i;
            int nextNum = 6 + i + 1;
            elementOffsets[i] = sb.Length;
            sb.Append(objNum);
            sb.Append(" 0 obj\n<< /Type /StructElem /S /P");
            if (i < depth - 1)
            {
                sb.Append(" /K ");
                sb.Append(nextNum);
                sb.Append(" 0 R");
            }
            sb.Append(" >>\nendobj\n");
        }

        long xref = sb.Length;
        int totalObjs = 5 + depth;
        sb.Append("xref\n0 ").Append(totalObjs + 1).Append("\n");
        sb.Append("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        for (int i = 0; i < depth; i++)
            sb.AppendFormat("{0:D10} 00000 n \n", elementOffsets[i]);

        sb.Append("trailer\n<< /Size ").Append(totalObjs + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
