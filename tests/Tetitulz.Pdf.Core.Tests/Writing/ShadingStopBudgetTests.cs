// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// The shading-stop cap is enforced by the <b>builder</b>, not only by one of its callers.
///
/// <para><b>Why that distinction matters.</b> Each interval between stops becomes its own Type 2
/// sub-function inside a Type 3 stitching function, so the written dictionary grows with the stop
/// count without limit. <c>DocxToPdf</c>'s <c>GradientFill</c> applied the cap before calling in —
/// but <see cref="PdfContentStreamBuilder"/> is public Core API, so a consumer building a document
/// from untrusted input (an SVG carrying thousands of gradient stops, say) reaches the primitive
/// directly and bypassed the guard entirely. <see cref="PdfLimits.MaxShadingStops"/> already
/// documented the behaviour as though the primitive enforced it.</para>
/// </summary>
public class ShadingStopBudgetTests
{
    private static IReadOnlyList<PdfContentStreamBuilder.ShadingStop> Stops(int count)
    {
        var list = new List<PdfContentStreamBuilder.ShadingStop>(count);
        for (int i = 0; i < count; i++)
        {
            double offset = count == 1 ? 0 : i / (double)(count - 1);
            list.Add(new PdfContentStreamBuilder.ShadingStop(offset, (byte)(i % 256), 0, 0));
        }
        return list;
    }

    /// <summary>
    /// A gradient far over the cap is written, truncated, and the truncation is reported — the
    /// builder does not throw and does not silently swallow.
    /// </summary>
    [Fact]
    public void AnOverLongStopListIsTruncatedAndReported()
    {
        var builder = new PdfContentStreamBuilder();

        string name = builder.AddAxialShading(0, 0, 100, 0, Stops(PdfLimits.MaxShadingStops * 40));

        Assert.NotNull(name);
        Assert.NotNull(PdfContentStreamBuilder.LastShadingTruncation);
        Assert.Contains(PdfLimits.MaxShadingStops.ToString(), PdfContentStreamBuilder.LastShadingTruncation);
    }

    /// <summary>
    /// The control. An ordinary gradient reports nothing — without this, a guard that reported
    /// unconditionally would satisfy the test above while distinguishing nothing.
    /// </summary>
    [Fact]
    public void AnOrdinaryStopListIsNotTruncatedAndReportsNothing()
    {
        var builder = new PdfContentStreamBuilder();

        string name = builder.AddAxialShading(0, 0, 100, 0, Stops(4));

        Assert.NotNull(name);
        Assert.Null(PdfContentStreamBuilder.LastShadingTruncation);
    }

    /// <summary>
    /// The slot is cleared at the start of every shading, so a clean gradient after a truncated one
    /// does not inherit the previous message. Per-thread storage is not per-operation scope — the
    /// <c>LastPermsValidation</c> lesson CLAUDE.md records.
    /// </summary>
    [Fact]
    public void TheDiagnosticDoesNotSurviveIntoTheNextShading()
    {
        var builder = new PdfContentStreamBuilder();

        builder.AddAxialShading(0, 0, 100, 0, Stops(PdfLimits.MaxShadingStops * 40));
        Assert.NotNull(PdfContentStreamBuilder.LastShadingTruncation);

        builder.AddRadialShading(50, 50, 25, Stops(3));
        Assert.Null(PdfContentStreamBuilder.LastShadingTruncation);
    }

    /// <summary>The radial entry point goes through the same funnel.</summary>
    [Fact]
    public void TheRadialEntryPointIsBoundedToo()
    {
        var builder = new PdfContentStreamBuilder();

        builder.AddRadialShading(50, 50, 25, Stops(PdfLimits.MaxShadingStops * 40));

        Assert.NotNull(PdfContentStreamBuilder.LastShadingTruncation);
    }
}
