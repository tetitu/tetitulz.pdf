// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A structure element the parser cannot read is not an absent one: the walk reports it as an
/// unresolvable kid (by the parse error the null object leaves), the tree reader refuses the tree
/// rather than hand back one missing the subtree, and the tagged extractor says its extraction is
/// incomplete. The control, a readable second element, is read in full with nothing said.
/// </summary>
public class StructureTreeUnreadableElementTests
{
    [Fact]
    public void TheTreeReader_RefusesATreeWithAnUnreadableElement()
    {
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfStructureTreeReader.ReadStructureTree(new MemoryStream(Pdf(secondReadable: false))));
        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
        Assert.Contains("9", ex.Message, StringComparison.Ordinal);

        var tree = PdfStructureTreeReader.ReadStructureTree(new MemoryStream(Pdf(secondReadable: true)));
        Assert.Equal(2, Assert.Single(tree.Children).Children.Count);   // the Document, then its two paragraphs
    }

    [Fact]
    public void TheTaggedExtractor_SaysItsExtractionIsIncomplete()
    {
        var blocks = PdfTaggedTextExtractor.Extract(new MemoryStream(Pdf(secondReadable: false)));
        Assert.Single(blocks);
        Assert.Contains("could not be read", PdfTaggedTextExtractor.LastError ?? "", StringComparison.Ordinal);

        var control = PdfTaggedTextExtractor.Extract(new MemoryStream(Pdf(secondReadable: true)));
        Assert.Equal(2, control.Count);
        Assert.Null(PdfTaggedTextExtractor.LastError);
    }

    /// <summary>
    /// A tagged page with two marked paragraphs; the structure root's <c>/K</c> names elements 8 and
    /// 9, and object 9's body is a stray <c>]</c> when it is to be unreadable.
    /// </summary>
    private static byte[] Pdf(bool secondReadable)
    {
        const string content = "/P << /MCID 0 >> BDC BT /F1 12 Tf 10 100 Td (First) Tj ET EMC\n" +
                               "/P << /MCID 1 >> BDC BT /F1 12 Tf 10 50 Td (Second) Tj ET EMC\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree << /Nums [0 [8 0 R 9 0 R]] >> >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            secondReadable ? "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>" : "]",
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
