// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfStructElement.PageIndex"/> resolves a structure element's <c>/Pg</c> reference to
/// the page it marks up.
/// </summary>
/// <remarks>
/// <para>
/// It was an <b>unimplemented stub</b> until 2026-08-12: <c>FindPageIndex</c> had an empty loop body
/// and returned -1 unconditionally, and <c>BuildPageRefIndex</c> filled a map with
/// <c>map[i] = i</c> — page index to page index, not a mapping from anything a <c>/Pg</c> reference
/// carries — which was then threaded through the entire recursion and never read. Every element this
/// reader produced reported <c>PageIndex == -1</c>.
/// </para>
/// <para>
/// The fixture puts the two structure elements on <b>different</b> pages, in reverse document order.
/// Both halves matter: a same-page fixture cannot tell a real lookup from a hard-coded 0, and
/// reversing the order means an implementation that returns the element's ordinal rather than its
/// page still fails.
/// </para>
/// </remarks>
public class StructureTreePageIndexTests
{
    [Fact]
    public void ReadStructureTree_ResolvesEachElementToItsOwnPage()
    {
        using var ms = new MemoryStream(BuildTaggedTwoPagePdf());
        var root = PdfStructureTreeReader.ReadStructureTree(ms);

        Assert.NotNull(root);
        var paragraphs = Flatten(root).Where(e => e.ElementType == "P").ToList();
        Assert.Equal(2, paragraphs.Count);

        // Element order in /K is [on page 2, on page 1] — so a reader returning the ordinal
        // instead of the page would produce 0,1 and fail here.
        Assert.Equal(1, paragraphs[0].PageIndex);
        Assert.Equal(0, paragraphs[1].PageIndex);
    }

    private static System.Collections.Generic.IEnumerable<PdfStructElement> Flatten(PdfStructElement e)
    {
        yield return e;
        foreach (var c in e.Children ?? System.Array.Empty<PdfStructElement>())
            foreach (var d in Flatten(c))
                yield return d;
    }

    /// <summary>
    /// Two pages (objects 3 and 4) and a struct tree whose two <c>P</c> elements point at them via
    /// <c>/Pg</c>, deliberately listed page-2-first.
    /// </summary>
    private static byte[] BuildTaggedTwoPagePdf()
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> >>\nendobj\n");
        Obj("5 0 obj\n<< /Type /StructTreeRoot /K [6 0 R 7 0 R] >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /StructElem /S /P /P 5 0 R /Pg 4 0 R >>\nendobj\n");  // page 2
        Obj("7 0 obj\n<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R >>\nendobj\n");  // page 1

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
