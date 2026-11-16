// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §14.7.5.2: a form XObject painted inside a page's marked-content sequence belongs to it. The
/// form's glyphs take the sequence's MCID and the page's scope when the form opens no sequence of
/// its own, so the structure element naming that MCID gets the form's text. The control, the form
/// painted outside any sequence, gives the element nothing.
/// </summary>
public class FormMcidInheritanceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFormPaintedInsideAMarkedSequence_GivesItsTextToTheSequencesElement(bool inside)
    {
        var blocks = PdfTaggedTextExtractor.Extract(new MemoryStream(Pdf(inside)));

        if (inside)
        {
            var block = Assert.Single(blocks);
            Assert.Equal("P", block.StructureType);
            Assert.Equal("Hello", block.Text);
        }
        else
        {
            Assert.Empty(blocks);
        }
    }

    /// <summary>The form's glyph carries the page's MCID and the page's stream scope in the layout.</summary>
    [Fact]
    public void TheFormsGlyphs_CarryTheEnclosingMcid()
    {
        var page = PdfStructuredTextExtractor.Extract(new MemoryStream(Pdf(inside: true)))[0];
        var h = page.Chars.First(c => c.Char == "H");

        Assert.Equal(0, h.Mcid);
        Assert.Equal(0, h.McidStreamObject);   // the page's own scope, not the form's object
    }

    private static byte[] Pdf(bool inside)
    {
        string content = inside ? "/P << /MCID 0 >> BDC /Fm1 Do EMC\n" : "/Fm1 Do\n";
        const string form = "BT /F1 12 Tf 72 700 Td (Hello) Tj ET\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 /Resources << /XObject << /Fm1 7 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [8 0 R] /ParentTree << /Nums [0 [8 0 R]] >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Length " + form.Length + " >>\nstream\n" + form + "\nendstream",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
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
