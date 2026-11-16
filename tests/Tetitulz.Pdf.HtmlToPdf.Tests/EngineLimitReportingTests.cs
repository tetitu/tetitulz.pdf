// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The managed engine must say when a document asks for a layout it cannot express.
///
/// <para><b>Why this matters more than implementing the layout.</b> The two engines are a deliberate
/// pair rather than a migration: the managed one is fast and light, the Playwright one is powerful
/// and expensive, and the developer chooses per call. Under that model the managed engine's job is
/// to be <i>honest</i>, not complete — a caller who is told "this needs the browser engine" has lost
/// nothing, while a caller who is told nothing ships a wrong-looking PDF.</para>
///
/// <para><b>The defect these close.</b> <c>display: grid</c> and <c>position: absolute</c> were named
/// as unsupported in four separate doc comments while <b>no code looked for either</b>. Both fell
/// through to ordinary block flow and stacked, producing a valid PDF with the right elements present
/// and the wrong geometry — and silence, which under an engine-choice model reads as "the managed
/// engine handled it". That corrupts the caller's decision, not ours.</para>
///
/// <para>These assert on the <b>diagnostic</b> rather than on geometry on purpose: stacking is the
/// documented behaviour and is not changing here. What was missing was the signal.</para>
/// </summary>
public class EngineLimitReportingTests
{
    private static List<string> Approximations(string html)
    {
        var seen = new List<string>();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d =>
            {
                if (d.Kind == BuilderDiagnosticKind.ContentApproximated) seen.Add(d.Detail);
            },
        });
        return seen;
    }

    /// <summary>
    /// A grid with <b>no column template</b> is still stacked, and still says so. The managed
    /// engine takes its track count from the template and cannot infer one from the items.
    ///
    /// <para><b>Renamed and re-aimed on 2026-08-18, not re-asserted under its old name.</b> It was
    /// <c>Grid_IsReportedRatherThanSilentlyStacked</c> and its fixture carried
    /// <c>grid-template-columns:1fr 1fr</c> — which is now laid out as a table, so the old test was
    /// asserting a report for the case that works. The report was narrowed to the declining case,
    /// and this pair is what pins the boundary.</para>
    /// </summary>
    [Theory]
    [InlineData("grid")]
    [InlineData("inline-grid")]
    public void GridWithoutAColumnTemplate_IsReportedRatherThanSilentlyStacked(string display)
    {
        List<string> seen = Approximations(
            $"<div style='display:{display}'><div>A</div><div>B</div></div>");

        Assert.Contains(seen, d => d.Contains("grid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(seen, d => d.Contains("Browser", StringComparison.Ordinal));
    }

    /// <summary>
    /// The other half: a grid the engine <i>can</i> lay out reports nothing about grid. Without
    /// this, narrowing the report could have been reverted with the theory above still green — and
    /// a channel that fires on input the engine handles is one a caller learns to ignore, taking
    /// the genuine reports down with it.
    /// </summary>
    [Theory]
    [InlineData("grid")]
    [InlineData("inline-grid")]
    public void GridWithAColumnTemplate_IsLaidOutAndNotReported(string display)
    {
        List<string> seen = Approximations(
            $"<div style='display:{display};grid-template-columns:1fr 1fr'><div>A</div><div>B</div></div>");

        Assert.DoesNotContain(seen, d => d.Contains("grid", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Positioning the engine leaves in the flow is reported: <c>sticky</c>, and an absolutely
    /// positioned or fixed table, which the table mapping lays out in the flow before the out-of-flow
    /// placement is reached. (Until batch 130 every absolute and fixed element was reported, placed
    /// or not: <see cref="PlacedOutOfFlowPositioning_IsNotReported"/>.)
    /// </summary>
    [Theory]
    [InlineData("<div><div style='position:sticky;top:40pt;left:20pt'>PINNED</div><div>FLOW</div></div>")]
    [InlineData("<table style='position:absolute;top:40pt;left:20pt'><tr><td>PINNED</td></tr></table>")]
    [InlineData("<table style='position:fixed;top:40pt;left:20pt'><tr><td>PINNED</td></tr></table>")]
    public void OutOfFlowPositioningLeftInTheFlow_IsReported(string html)
    {
        Assert.Contains(Approximations(html), d => d.Contains("position", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An absolutely positioned or fixed block placed at its offsets is not reported (batch 130) — it
    /// was told it "stayed in normal flow" while it sat at them. PINNED at <c>left: 20pt</c> is the
    /// evidence the placement happened, beside the silence.
    /// </summary>
    [Theory]
    [InlineData("absolute")]
    [InlineData("fixed")]
    public void PlacedOutOfFlowPositioning_IsNotReported(string position)
    {
        string html = $"<div><div style='position:{position};top:40pt;left:20pt'>PINNED</div><div>FLOW</div></div>";

        Assert.DoesNotContain(Approximations(html), d => d.Contains("position", StringComparison.OrdinalIgnoreCase));
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        var pinned = Tetitulz.Pdf.Base.PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines).SelectMany(l => l.Words).Single(w => w.Text == "PINNED");
        Assert.Equal(20, pinned.X, 0);
    }

    /// <summary>
    /// Presence is not deviation. <c>position: static</c> is the default and <c>relative</c> with no
    /// offsets renders identically, so reporting either would fire on conformant input — and a
    /// channel that fires on conformant input is one a caller learns to ignore, which is worse than
    /// never having reported. This is the same rule the render diagnostics follow.
    /// </summary>
    [Theory]
    [InlineData("<div style='position:static'>A</div>")]
    [InlineData("<div style='position:relative'>A</div>")]
    [InlineData("<div><p>plain</p></div>")]
    public void WhatTheEngineAlreadyDoes_IsNotReported(string html)
    {
        Assert.DoesNotContain(Approximations(html),
            d => d.Contains("position", StringComparison.OrdinalIgnoreCase)
              || d.Contains("grid", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>position: relative</c> <i>with</i> offsets does move the box in CSS and does not here, so
    /// that one is a real deviation and must be reported — the boundary the test above pins from the
    /// other side.
    /// </summary>
    [Fact]
    public void RelativeWithOffsets_IsReported()
    {
        Assert.Contains(Approximations("<div style='position:relative;left:30pt'>SHIFTED</div>"),
            d => d.Contains("position", StringComparison.OrdinalIgnoreCase));
    }
}
