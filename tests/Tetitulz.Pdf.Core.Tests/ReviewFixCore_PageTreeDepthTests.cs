// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// The page-tree recursion depth cap.
///
/// A cycle guard (a visited <c>HashSet</c>) alone does NOT catch a deep but
/// *acyclic* /Pages → /Pages → … chain: a document with thousands of nested
/// /Pages nodes would recurse until the native stack overflowed — uncatchable
/// in .NET. <see cref="PdfPageTree"/>.CollectPages threads a depth counter and
/// throws <see cref="PdfParseException"/> past 1024 levels.
/// </summary>
public class ReviewFixCore_PageTreeDepthTests
{
    [Fact]
    public void DeepAcyclicPageChain_ThrowsPdfParseException_NoStackOverflow()
    {
        // 4000 nested /Pages nodes is well past the 1024 cap and would blow the
        // stack without it.  Each node is a distinct dictionary object (distinct
        // reference), so the cycle guard never fires.
        byte[] pdf = BuildDeepPageChainPdf(4000);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        // Accessing Pages walks the tree.  We expect a recoverable parse
        // exception, NOT a process-killing StackOverflowException.
        var ex = Assert.Throws<PdfParseException>(() => { _ = doc.Pages; });
        Assert.Contains("page tree nesting exceeds", ex.Message);
    }

    [Fact]
    public void ShallowPageChain_StillResolvesNormally()
    {
        // A modest chain (depth 8) is well under the cap and must still yield
        // the single real page at the bottom — proves the cap didn't break
        // legitimate nested page trees.
        byte[] pdf = BuildDeepPageChainPdf(8);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// Builds a PDF whose page tree is a straight chain of <paramref name="depth"/>
    /// /Pages nodes, the last of which has a single real /Page leaf as its kid.
    /// The chain is acyclic (every node is a unique object), so only a depth cap
    /// — not the cycle guard — can stop the traversal.
    /// </summary>
    private static byte[] BuildDeepPageChainPdf(int depth)
    {
        // Object layout:
        //   1            = Catalog (/Pages 2 0 R)
        //   2 .. depth+1 = the chain of /Pages nodes
        //   depth+2      = the single /Page leaf
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int totalObjects = depth + 3; // objects 0..depth+2
        var offsets = new int[totalObjects];

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int leafObj = depth + 2;
        for (int i = 0; i < depth; i++)
        {
            int objNum = 2 + i;
            int kidObj = (i == depth - 1) ? leafObj : objNum + 1;
            offsets[objNum] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n", objNum);
            sb.AppendFormat("<< /Type /Pages /Kids [{0} 0 R] /Count 1 >>\n", kidObj);
            sb.Append("endobj\n");
        }

        // The single real page.
        offsets[leafObj] = sb.Length;
        sb.AppendFormat("{0} 0 obj\n", leafObj);
        sb.AppendFormat("<< /Type /Page /Parent {0} 0 R /MediaBox [0 0 612 792] >>\n", depth + 1);
        sb.Append("endobj\n");

        int xrefOffset = sb.Length;
        sb.Append("xref\n");
        sb.AppendFormat("0 {0}\n", totalObjects);
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i < totalObjects; i++)
            sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);

        sb.Append("trailer\n");
        sb.AppendFormat("<< /Size {0} /Root 1 0 R >>\n", totalObjects);
        sb.AppendFormat("startxref\n{0}\n", xrefOffset);
        sb.Append("%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
