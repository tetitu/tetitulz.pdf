// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Adversarial input: a <c>/PageLabels</c> number tree whose <c>/Kids</c> form a cycle.
/// </summary>
/// <remarks>
/// <para><b>A depth cap is not a cycle guard.</b> The walk carried only
/// <c>PdfLimits.MaxTreeDepth</c> (100), under a comment claiming that bounded "a cyclic or
/// pathologically deep /Kids chain". It bounds the pathologically deep one. It does not bound a
/// cycle whose branching factor exceeds 1: two nodes each holding <c>/Kids [other other]</c> visit
/// 2^100 nodes before any branch reaches depth 100.</para>
///
/// <para>That is the distinction <c>RenderBudget</c> is built on and states in as many words — a
/// depth cap bounds one branch, never the number of branches — and the same shape as the
/// form-XObject doubling chain: no node repeats within a branch, so a depth cap never fires, and
/// the work is exponential in the branching factor.</para>
///
/// <para><b>Reachable from the public API</b>: <c>PdfPageLabels.GetLabels</c>, i.e.
/// <c>PdfOperations.GetPageLabels</c>. §7.9.6's name-tree twin, <c>PdfNameTree</c>, has carried a
/// visited set all along; this is the copy that did not.</para>
/// </remarks>
public class NumberTreeCycleTests
{
    /// <summary>
    /// The guard's own evidence is that the call RETURNS, having visited each node once — never a
    /// wall clock. Two mutually-referencing nodes hold no <c>/Nums</c>, so a correct walk yields no
    /// ranges; a walk with no visited set does not yield at all.
    /// </summary>
    [Fact]
    public void CyclicNumberTreeKids_TerminateInsteadOfBranchingExponentially()
    {
        using var ms = new MemoryStream(CyclicPageLabelsPdf());

        var labels = PdfPageLabels.GetLabels(ms);

        Assert.Empty(labels);
    }

    /// <summary>
    /// The floor. A legitimate two-level number tree still yields its ranges, so the visited set
    /// cannot have been added by refusing to walk at all — and a converging (non-cyclic) tree that
    /// reaches one leaf by two paths must still be read.
    /// </summary>
    [Fact]
    public void NestedNumberTree_StillYieldsItsRanges()
    {
        using var ms = new MemoryStream(NestedPageLabelsPdf());

        var labels = PdfPageLabels.GetLabels(ms);

        Assert.Equal(2, labels.Count);
        Assert.Equal(0, labels[0].StartPageIndex);
        Assert.Equal("r", labels[0].Style);
        Assert.Equal(4, labels[1].StartPageIndex);
        Assert.Equal("D", labels[1].Style);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>Objects 5 and 6 each name the other twice — branching factor 2, no /Nums anywhere.</summary>
    private static byte[] CyclicPageLabelsPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R /PageLabels 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        Stream("q Q\n"),
        "<< /Kids [6 0 R 6 0 R] >>",
        "<< /Kids [5 0 R 5 0 R] >>");

    /// <summary>An intermediate node over two leaves, which a visited set must not prune.</summary>
    private static byte[] NestedPageLabelsPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R /PageLabels 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
        Stream("q Q\n"),
        "<< /Kids [6 0 R 7 0 R] >>",
        "<< /Nums [0 << /S /r >>] >>",
        "<< /Nums [4 << /S /D >>] >>");

    private static string Stream(string data) =>
        $"<< /Length {data.Length} >>\nstream\n{data}endstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
