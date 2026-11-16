// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An <c>/MCR</c>'s own <c>/Pg</c> names the page its marked content is on (ISO 32000-1 Table 324),
/// over the element's: an element placed on page 2 whose reference names page 1 reads page 1's
/// content — the first page included, which an index answering 0 must not read as "no page".
/// </summary>
public class TaggedTextMcrPageTests
{
    [Fact]
    public void AnMcrsOwnPage_IsWhereItsContentIsRead_TheFirstPageIncluded()
    {
        var blocks = PdfTaggedTextExtractor.Extract(new MemoryStream(Doc(mcrPage: 3)));
        Assert.Equal("FIRST", blocks.Single().Text.Trim());

        // The control: the same reference naming page 2 reads page 2's content.
        var control = PdfTaggedTextExtractor.Extract(new MemoryStream(Doc(mcrPage: 4)));
        Assert.Equal("SECOND", control.Single().Text.Trim());
    }

    /// <summary>
    /// Two pages, each showing marked content 0; one paragraph placed on the second page holding one
    /// marked-content reference to page object <paramref name="mcrPage"/>.
    /// </summary>
    private static byte[] Doc(int mcrPage)
    {
        string first = "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (FIRST) Tj ET EMC";
        string second = "/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (SECOND) Tj ET EMC";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 9 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R /StructParents 0 >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R /StructParents 1 >>",
            $"<< /Length {first.Length} >>\nstream\n{first}\nendstream",
            $"<< /Length {second.Length} >>\nstream\n{second}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /StructElem /S /P /P 9 0 R /Pg 4 0 R /K [<< /Type /MCR /Pg {mcrPage} 0 R /MCID 0 >>] >>",
            "<< /Type /StructTreeRoot /K [8 0 R] >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
