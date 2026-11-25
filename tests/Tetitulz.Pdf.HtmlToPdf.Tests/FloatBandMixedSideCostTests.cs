// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.HtmlToPdf;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A long run of alternately-sided floats whose exact mapping fails is mapped in one linear pass,
/// not by re-attempting the exact map over every shrinking tail (HtmlToPdf-2:2).
/// </summary>
/// <remarks>
/// When the exact float map failed for a mixed-side run, the mapper reduced the run to its first
/// same-side segment and returned — false, when that segment was a lone float — so the caller
/// re-entered on the tail and re-attempted the exact map, re-mapping and re-measuring every float.
/// Over an alternating run of n floats that is O(n²): <see cref="HtmlToBuilderMapper.FloatsExactlyMappedCount"/>
/// reached n(n+1)/2. The run is now segmented in a single call, so the count is linear. The byte
/// output is unchanged — the segments go into the document in the same order with the same shapes —
/// which is why this asserts the <i>work</i>, not the result, and pins the result separately.
/// </remarks>
public class FloatBandMixedSideCostTests
{
    // Empty floated divs map to no block, so the exact map abandons the whole run — the fallback the
    // quadratic lived in. Alternating sides make each same-side segment a single float.
    private static string Markup(int n)
    {
        var sb = new StringBuilder("<html><body><div>");
        for (int i = 0; i < n; i++)
            sb.Append(i % 2 == 0 ? "<div style='float:left'></div>" : "<div style='float:right'></div>");
        return sb.Append("</div></body></html>").ToString();
    }

    private static (int Work, byte[] Pdf) Convert(int n)
    {
        byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(Markup(n), new HtmlToPdfOptions());
        return (HtmlToBuilderMapper.FloatsExactlyMappedCount, pdf);
    }

    [Fact]
    public void AnAlternatingFloatRun_IsMappedInLinearWork()
    {
        var (work20, _) = Convert(20);
        var (work200, _) = Convert(200);

        // Linear: ~n floats reach the exact mapper once (the single whole-run attempt). A quadratic
        // walk reaches n(n+1)/2 — 20 100 at n=200 and 210 at n=20 — and fails both the absolute cap
        // and the ×10-for-×10 growth check below.
        Assert.True(work200 <= 3 * 200, $"expected ~linear work, got {work200} for 200 floats");

        // Ten times the floats costs about ten times the work, not a hundred times. (20→200 is ×10;
        // the quadratic ratio is 20100/210 ≈ 96.)
        Assert.True(work200 < work20 * 20, $"work grew super-linearly: {work20} → {work200}");
    }

    [Fact]
    public void TheMixedFloatRun_ProducesTheSameDocument_WhateverItsLength_Shape()
    {
        // The control for the cost test: segmenting the run in one pass must not change what is
        // produced. A run of alternating empty floats collapses to the same near-empty page however
        // long it is, so two very different lengths give byte-identical output (the /ID is masked).
        var (_, a) = Convert(4);
        var (_, b) = Convert(400);
        Assert.Equal(
            Tetitulz.Pdf.TestHelpers.PdfContentProbe.NormalizedRaw(a),
            Tetitulz.Pdf.TestHelpers.PdfContentProbe.NormalizedRaw(b));
    }
}
