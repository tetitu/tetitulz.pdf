// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A name- or number-tree read cut short by a limit is <b>reported</b>. The destination writers
/// already refused on a partial read; the two readers returned what they had and said nothing, which
/// is indistinguishable from a document holding less.
/// </summary>
/// <remarks>
/// Each fixture is a root whose <c>/Kids</c> are a shallow leaf and a chain deeper than
/// <see cref="PdfLimits.MaxTreeDepth"/>, so the walk reaches the shallow entries — the potency half —
/// and then stops at the cap.
/// </remarks>
public class TreeReadTruncationReportTests
{
    private static int TooDeep => PdfLimits.MaxTreeDepth + 20;

    [Fact]
    public void ADestinationListCutShortByTheTreeDepthCapIsReported()
    {
        var listed = PdfNamedDestinations.ListDestinations(new MemoryStream(DestsTree(TooDeep)));

        Assert.Equal(new[] { "shallow" }, listed.Select(d => d.Name));
        Assert.Contains("incomplete", PdfNamedDestinations.LastListError ?? "", StringComparison.Ordinal);
    }

    /// <summary>The control, and the reset: a complete read after a truncated one clears the report.</summary>
    [Fact]
    public void ACompleteDestinationListReportsNothing_EvenAfterATruncatedOne()
    {
        PdfNamedDestinations.ListDestinations(new MemoryStream(DestsTree(TooDeep)));
        Assert.NotNull(PdfNamedDestinations.LastListError);

        var listed = PdfNamedDestinations.ListDestinations(new MemoryStream(DestsTree(3)));

        Assert.Equal(new[] { "shallow", "deep" }, listed.Select(d => d.Name));
        Assert.Null(PdfNamedDestinations.LastListError);
    }

    [Fact]
    public void APageLabelReadCutShortByTheTreeDepthCapIsReported()
    {
        var ranges = PdfPageLabels.GetLabels(new MemoryStream(LabelsTree(TooDeep)));

        Assert.Equal(new[] { 0 }, ranges.Select(r => r.StartPageIndex));
        Assert.Contains("incomplete", PdfPageLabels.LastReadError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void ACompletePageLabelReadReportsNothing_EvenAfterATruncatedOne()
    {
        PdfPageLabels.GetLabels(new MemoryStream(LabelsTree(TooDeep)));
        Assert.NotNull(PdfPageLabels.LastReadError);

        var ranges = PdfPageLabels.GetLabels(new MemoryStream(LabelsTree(3)));

        Assert.Equal(new[] { 0, 1 }, ranges.Select(r => r.StartPageIndex));
        Assert.Null(PdfPageLabels.LastReadError);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// A <c>/Dests</c> name tree: <c>(shallow)</c> in a leaf under the root, <c>(deep)</c> at the
    /// bottom of a <c>/Kids</c> chain <paramref name="depth"/> nodes long.
    /// </summary>
    private static byte[] DestsTree(int depth) => Chain(
        "/Names << /Dests 4 0 R >>",
        "<< /Names [(shallow) [3 0 R /Fit]] >>",
        "<< /Names [(deep) [3 0 R /Fit]] >>",
        depth);

    /// <summary>A <c>/PageLabels</c> number tree: page 0's range shallow, page 1's at the bottom.</summary>
    private static byte[] LabelsTree(int depth) => Chain(
        "/PageLabels 4 0 R",
        "<< /Nums [0 << /S /r >>] >>",
        "<< /Nums [1 << /S /D >>] >>",
        depth);

    /// <summary>
    /// Objects: 1 catalog, 2 pages, 3 page, 4 the tree root, 5 the shallow leaf, then the chain —
    /// <paramref name="depth"/> nodes each with one kid, the last being the deep leaf.
    /// </summary>
    private static byte[] Chain(string catalogEntry, string shallowLeaf, string deepLeaf, int depth)
    {
        const int FirstChainNode = 6;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalogEntry + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Kids [5 0 R " + FirstChainNode + " 0 R] >>",
            shallowLeaf,
        };
        for (int i = 0; i < depth - 1; i++)
            bodies.Add("<< /Kids [" + (FirstChainNode + i + 1) + " 0 R] >>");
        bodies.Add(deepLeaf);
        return FormPdfFixtures.Build(bodies.ToArray());
    }
}
