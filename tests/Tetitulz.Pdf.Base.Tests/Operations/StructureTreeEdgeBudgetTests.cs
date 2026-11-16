// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The ceiling on <c>/K</c> array slots that bounds <see cref="PdfStructureTreeReader"/>'s total work.
/// </summary>
/// <remarks>
/// <para><b>The third walk of the same tree, and the one that lacked the bound.</b>
/// <c>PdfTaggedTextExtractor</c> and the accessibility walk already stop at
/// <see cref="PdfLimits.MaxStructTreeEdges"/>; this reader had a depth cap and a visited set, and
/// neither bounds the cost of an element whose <c>/K</c> is a shared array. Here each slot also
/// allocates — an MCID becomes a leaf element — so n elements over one n-entry array built n²
/// result objects from O(n) bytes of file.</para>
///
/// <para>The assertions are the guard's own evidence — the leaves it built, and the refusal naming
/// the ceiling — never a wall clock.</para>
/// </remarks>
public class StructureTreeEdgeBudgetTests
{
    /// <summary>
    /// The control: the quadratic shape inside the ceiling is read in full. It also proves the
    /// fixture <i>is</i> that shape — every element carries all n MCIDs of the shared array — so
    /// the refusal below is refusing the right thing.
    /// </summary>
    [Fact]
    public void ASharedKidsArrayInsideTheCeilingIsReadInFull()
    {
        const int n = 700;   // 490 000 slots, well inside the 2 000 000 ceiling

        PdfStructElement root = PdfStructureTreeReader.ReadStructureTree(new MemoryStream(SharedMcidTree(n)));

        Assert.Equal(n, root.Children.Count);
        Assert.All(root.Children, element =>
        {
            Assert.Equal("P", element.ElementType);
            Assert.Equal(n, element.Children.Count(c => c.ElementType == "MCID"));
        });
    }

    /// <summary>
    /// Past the ceiling the read is refused, and the refusal names the ceiling — as the depth cap's
    /// does, and for the same reason: a tree cut short would come back looking complete. Without
    /// the ceiling this returns about 2.6 million leaf elements.
    /// </summary>
    [Fact]
    public void ASharedKidsArrayPastTheCeilingIsRefusedNamingTheCeiling()
    {
        int n = (int)Math.Sqrt(PdfLimits.MaxStructTreeEdges) + 200;

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfStructureTreeReader.ReadStructureTree(new MemoryStream(SharedMcidTree(n))));

        Assert.Contains("/K arrays exceed", ex.Message, StringComparison.Ordinal);
        Assert.Contains(PdfLimits.MaxStructTreeEdges.ToString(CultureInfo.InvariantCulture), ex.Message,
                        StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// <paramref name="n"/> structure elements whose <c>/K</c> all point at <b>one shared array</b>
    /// of <paramref name="n"/> MCIDs — the O(n²) shape in O(n) bytes. MCIDs rather than references
    /// to elements, because an element is read once and a repeat returns nothing, while an MCID is
    /// a new leaf every time it is reached.
    /// </summary>
    private static byte[] SharedMcidTree(int n)
    {
        // Objects: 1 catalog, 2 pages, 3 page, 4 contents, 5 struct-tree root, 6 the shared /K
        // array, 7.. the elements that share it.
        const int SharedArray = 6;
        const int FirstElement = 7;

        var mcids = new StringBuilder();
        for (int i = 0; i < n; i++) mcids.Append(i).Append(' ');

        var rootKids = new StringBuilder();
        for (int i = 0; i < n; i++) rootKids.Append(FirstElement + i).Append(" 0 R ");

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 5 >>\nstream\nBT ET\nendstream",
            "<< /Type /StructTreeRoot /K [" + rootKids + "] >>",
            "[" + mcids + "]",
        };
        for (int i = 0; i < n; i++)
            bodies.Add("<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K " + SharedArray + " 0 R >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
