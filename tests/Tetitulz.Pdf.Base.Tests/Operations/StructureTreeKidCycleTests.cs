// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §14.7.2 lets <c>/K</c> be a single reference as well as an array, and a cycle written in the
/// single-reference spelling must be cut like one written in the array spelling.
/// </summary>
/// <remarks>
/// <para><b>What was wrong.</b> <c>PdfStructureTreeReader</c>'s cycle guard was a
/// <c>HashSet&lt;int&gt;</c> of object numbers, registered only where <c>/K</c> was an array. The
/// single-reference spelling reached the parser through <c>doc.ResolveObject(dict[/K])</c>, which
/// discards the reference before anything can record it, so two elements naming each other recursed
/// to the depth cap and left the public <c>ReadStructureTree</c> as an
/// <c>InvalidOperationException</c> — while <c>PdfTaggedTextExtractor</c>, <c>StructTreeWalker</c>
/// and <c>PdfUaChecker</c>, over the same tree, all stopped at the second visit and returned it.</para>
/// <para><b>Why the assertions are the shape of the tree.</b> "It did not throw" would pass with the
/// reader returning an empty root, which is the failure this guard's crude form (give up on the
/// whole document) already had. The test asserts the elements <i>before</i> the repeat are present
/// and that the repeat itself is absent, so both giving up and following the cycle are red.</para>
/// </remarks>
public class StructureTreeKidCycleTests
{
    [Fact]
    public void SingleReferenceKidCycle_IsCutAtTheRepeat_AndTheElementsBeforeItSurvive()
    {
        // 6 -> 7 -> 6, each /K a bare reference rather than a one-element array.
        byte[] pdf = BuildPdf(new List<string>
        {
            "5 0 obj\n<< /Type /StructTreeRoot /K 6 0 R >>\nendobj\n",
            "6 0 obj\n<< /Type /StructElem /S /Document /K 7 0 R >>\nendobj\n",
            "7 0 obj\n<< /Type /StructElem /S /P /K 6 0 R >>\nendobj\n",
        });

        using var ms = new MemoryStream(pdf);
        PdfStructElement root = PdfStructureTreeReader.ReadStructureTree(ms);

        Assert.Equal("StructTreeRoot", root.ElementType);

        var document = Assert.Single(root.Children);
        Assert.Equal("Document", document.ElementType);

        var paragraph = Assert.Single(document.Children);
        Assert.Equal("P", paragraph.ElementType);

        // The second visit to element 6 is where the walk must stop: following it would have
        // produced another Document node here (and another P under that, and so on).
        Assert.Empty(paragraph.Children);
    }

    /// <summary>
    /// The floor for the test above: an acyclic chain in the same single-reference spelling still
    /// reports every level, so "the cycle was cut" cannot pass by the guard pruning everything.
    /// </summary>
    [Fact]
    public void SingleReferenceKidChain_WithNoCycle_ReportsEveryLevel()
    {
        byte[] pdf = BuildPdf(new List<string>
        {
            "5 0 obj\n<< /Type /StructTreeRoot /K 6 0 R >>\nendobj\n",
            "6 0 obj\n<< /Type /StructElem /S /Document /K 7 0 R >>\nendobj\n",
            "7 0 obj\n<< /Type /StructElem /S /P /K 8 0 R >>\nendobj\n",
            "8 0 obj\n<< /Type /StructElem /S /Span >>\nendobj\n",
        });

        using var ms = new MemoryStream(pdf);
        PdfStructElement root = PdfStructureTreeReader.ReadStructureTree(ms);

        var document = Assert.Single(root.Children);
        var paragraph = Assert.Single(document.Children);
        var span = Assert.Single(paragraph.Children);
        Assert.Equal("Span", span.ElementType);
    }

    // ------------------------------------------------------------------ fixture

    // Objects 1-4 are the catalog/page scaffolding; the caller supplies 5 onwards, numbered
    // consecutively from 5 so the xref rows line up.
    private static byte[] BuildPdf(List<string> structObjects)
    {
        var sb = new StringBuilder("%PDF-1.5\n");
        var offsets = new List<long>();

        offsets.Add(sb.Length);
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Contents 4 0 R /Resources << >> >>\nendobj\n");
        offsets.Add(sb.Length);
        sb.Append("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        foreach (string body in structObjects)
        {
            offsets.Add(sb.Length);
            sb.Append(body);
        }

        long xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (long off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
