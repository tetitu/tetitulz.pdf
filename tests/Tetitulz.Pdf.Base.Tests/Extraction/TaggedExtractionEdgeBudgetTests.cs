// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Extraction;

/// <summary>
/// The ceiling on <c>/K</c> child slots that bounds a tagged-text extraction's total work.
/// </summary>
/// <remarks>
/// <para><b>A depth cap and a visited set do not bound total work, and that is the whole finding.</b>
/// The extractor had both: recursion was capped at <c>PdfLimits.MaxStructTreeDepth</c> and every
/// element was expanded at most once. Neither bounds the cost of expanding <i>one</i> element — a
/// tree whose structure elements all point <c>/K</c> at a single shared array of N references costs
/// N per node over N nodes while every node is expanded exactly once, and the visited set is
/// perfectly satisfied throughout.</para>
///
/// <para><b>It was found by comparing two walks of the same tree.</b>
/// <c>Accessibility.Internal.StructTreeWalker</c> reads the same structure from the same untrusted
/// input and has carried an edge ceiling for exactly this shape, with the reasoning written at the
/// constant; the extraction side had no such bound. The number now lives in
/// <see cref="PdfLimits.MaxStructTreeEdges"/> so the two cannot disagree about it again.</para>
///
/// <para><b>The assertion is the guard's own evidence, never a wall clock.</b> Under a full-solution
/// run elapsed time measures the machine's backlog; what this asserts is that the work is bounded
/// and that the caller is told the extraction is incomplete.</para>
/// </remarks>
public class TaggedExtractionEdgeBudgetTests
{
    /// <summary>
    /// The quadratic shape, at a size that is ruinous without the ceiling and trivial with it.
    /// </summary>
    /// <remarks>
    /// 700 elements sharing one 700-entry array is 490 000 slots — comfortably inside the real
    /// ceiling, so this fixture is the <i>control</i>: it proves the shape is built correctly and
    /// still completes. The refusal case below scales the same shape past the ceiling.
    /// </remarks>
    [Fact]
    public void AdversarialSharedKidsArray_IsBoundedAndCompletes()
    {
        byte[] pdf = SharedKidsTree(700);

        using var input = new MemoryStream(pdf, writable: false);
        var blocks = PdfTaggedTextExtractor.Extract(input);

        // It completes, and it does not claim truncation at a size inside the ceiling.
        Assert.NotNull(blocks);
        Assert.Null(PdfTaggedTextExtractor.LastError);

        // And the fixture really is the quadratic shape: 700 elements sharing a 700-entry array
        // spend 700 slots each. A fixture that had fallen into the depth cap, or whose elements did
        // not share the array, would spend a few hundred and pass every assertion above — which is
        // precisely the failure the refusal case below could not then distinguish from a working
        // ceiling.
        Assert.True(PdfTaggedTextExtractor.LastEdgesExamined >= 700 * 700,
            $"the walk examined only {PdfTaggedTextExtractor.LastEdgesExamined} /K slots, so the "
            + "fixture is not the shared-array shape this ceiling exists for");
    }

    /// <summary>
    /// Past the ceiling the walk stops and <b>says so</b>: a truncated structure walk returns fewer
    /// blocks than the document has, and "this document's structure is small" and "we stopped
    /// reading it" are otherwise indistinguishable in the output.
    /// </summary>
    /// <remarks>
    /// The fixture is ~1 500 elements over a shared array of the same size — about 2.2 million
    /// slots against a 2 000 000 ceiling — which is a few hundred kilobytes of PDF. That ratio is
    /// the point: the cost is quadratic in the file's size, so a hostile document buys enormous
    /// work for very few bytes.
    /// </remarks>
    [Fact]
    public void AdversarialSharedKidsArray_PastTheCeiling_IsRefusedAndReported()
    {
        int n = (int)Math.Sqrt(PdfLimits.MaxStructTreeEdges) + 200;
        byte[] pdf = SharedKidsTree(n);

        using var input = new MemoryStream(pdf, writable: false);
        var blocks = PdfTaggedTextExtractor.Extract(input);

        // The walk STOPPED, it did not merely notice — and nothing about the ANSWER can say so.
        // Setting the flag and carrying on returns the same blocks (every further slot resolves to
        // the already-visited leaf) and the same report; the two differ only in the work done. So
        // the liveness is asserted directly, the way CMapCodec.UsesCidRangeIndex is.
        //
        // The margin is n because unwinding costs at most one slot per frame the return passes
        // through, and there are fewer frames than elements. Without the return the walk spends
        // n² ≈ 2.6 million slots against a 2 000 000 ceiling, which is well outside it.
        Assert.True(
            PdfTaggedTextExtractor.LastEdgesExamined <= PdfLimits.MaxStructTreeEdges + n,
            $"the walk examined {PdfTaggedTextExtractor.LastEdgesExamined} /K slots against a "
            + $"{PdfLimits.MaxStructTreeEdges} ceiling, so it reported truncation and kept walking");

        Assert.Contains("incomplete", PdfTaggedTextExtractor.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains(
            PdfLimits.MaxStructTreeEdges.ToString(CultureInfo.InvariantCulture),
            PdfTaggedTextExtractor.LastError ?? "",
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary tagged document is not affected: it spends one slot per element, so the ceiling
    /// is four orders of magnitude away and nothing is reported.
    /// </summary>
    [Fact]
    public void AnOrdinaryTaggedDocumentIsUnaffected()
    {
        using var input = new MemoryStream(TestHelpers.MinimalPdfBuilder.CreatePdfWithStructureTree(),
                                           writable: false);

        PdfTaggedTextExtractor.Extract(input);

        Assert.Null(PdfTaggedTextExtractor.LastError);
    }

    /// <summary>
    /// An element whose <c>/K</c> names one marked-content sequence twenty thousand times holds its
    /// text once, and a second element naming the same sequence holds none of it (§14.7.4.4: a
    /// sequence has one parent). Each slot appended the whole text, slots × text in memory.
    /// </summary>
    [Fact]
    public void AdversarialMcidRepeatedInK_IsTextOnce()
    {
        const int repeats = 20_000;
        var k = new StringBuilder();
        for (int i = 0; i < repeats; i++) k.Append("0 ");

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 8 0 R >> >> /StructParents 0 >>",
            Stream("BT /F1 12 Tf 72 700 Td /P << /MCID 0 >> BDC (Once) Tj EMC ET"),
            "<< /Type /StructTreeRoot /K [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K [" + k + "] >>",
            "<< /Type /StructElem /S /Span /P 5 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var blocks = PdfTaggedTextExtractor.Extract(new MemoryStream(Assemble(bodies), writable: false));

        var p = Assert.Single(blocks, b => b.StructureType == "P");
        Assert.Equal("Once", p.Text);
        Assert.DoesNotContain(blocks, b => b.StructureType == "Span");
    }

    // ----------------------------------------------------------------

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// <paramref name="n"/> structure elements whose <c>/K</c> all point at <b>one shared array</b>
    /// of <paramref name="n"/> references to a single leaf — the O(n²) shape, in O(n) bytes.
    /// </summary>
    /// <remarks>
    /// <b>The array points at a leaf, not at the elements themselves, and that is deliberate.</b>
    /// An array of the sibling elements makes each one descend into the next, so the tree nests n
    /// deep and the <i>depth</i> cap fires long before the edge budget — a fixture that looks
    /// quadratic and is actually bounded by a different guard. Pointing every slot at one
    /// already-visited leaf keeps the depth at three and spends n slots per element, which is the
    /// cost this ceiling exists to bound.
    /// </remarks>
    private static byte[] SharedKidsTree(int n)
    {
        // Objects: 1 catalog, 2 pages, 3 page, 4 contents, 5 struct-tree root, 6 the shared /K
        // array, 7 the leaf every slot points at, 8.. the elements that share the array.
        const int SharedArray = 6;
        const int Leaf = 7;
        const int FirstElement = 8;

        var kids = new StringBuilder();
        for (int i = 0; i < n; i++) kids.Append(Leaf).Append(" 0 R ");

        var rootKids = new StringBuilder();
        for (int i = 0; i < n; i++) rootKids.Append(FirstElement + i).Append(" 0 R ");

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            Stream("BT ET"),
            "<< /Type /StructTreeRoot /K [" + rootKids + "] >>",
            "[" + kids + "]",
            "<< /Type /StructElem /S /Span /P 5 0 R >>",
        };

        for (int i = 0; i < n; i++)
            bodies.Add("<< /Type /StructElem /S /P /P 5 0 R /K " + SharedArray + " 0 R >>");

        var sb = new StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string content) =>
        "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n"
        + content + "\nendstream";
}
