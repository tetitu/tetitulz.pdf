// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>HtmlToPdfOptions.OnDiagnostic</c> is delivered one subscriber at a time, so a handler that
/// throws costs only its own notification and does not break the conversion.
/// </summary>
/// <remarks>
/// <para>This site had the <c>try</c>/<c>catch</c> — <c>try { _options.OnDiagnostic?.Invoke(d); }
/// catch { }</c> — and it is the half that survives review while fixing nothing that matters. The
/// exception is contained, but one multicast invoke still skips every handler after the one that
/// threw, and nothing in the code or the result says so.</para>
///
/// <para><b>It matters more for this callback than for most.</b> The managed engine's contract, set
/// out in <c>EngineLimitReportingTests</c>, is that it may decline a layout as long as it
/// <i>says</i> so — a caller who is told "this needs the browser engine" has lost nothing, and a
/// caller who is told nothing ships a wrong-looking PDF believing the managed engine handled it. A
/// swallowed fan-out puts the caller in exactly the second position, and the PDF is valid, so
/// nothing downstream contradicts it.</para>
///
/// <para>The trigger is a <c>display:grid</c> with no column template, which the engine stacks and
/// reports. Any reported approximation would do; this one is pinned by its own test, so the fixture
/// cannot quietly stop reaching the feature.</para>
/// </remarks>
public class DiagnosticFanOutTests
{
    /// <summary>
    /// The throwing handler is composed first, so a single multicast invoke never reaches the
    /// collector — and the collector must receive the diagnostic itself, not merely a count.
    /// </summary>
    [Fact]
    public void AThrowingDiagnosticHandler_DoesNotBlindTheOnesAfterIt()
    {
        var seen = new List<BuilderDiagnostic>();

        Action<BuilderDiagnostic> onDiagnostic =
            _ => throw new InvalidOperationException("a caller telemetry handler that throws");
        onDiagnostic += seen.Add;

        Convert(onDiagnostic);

        Assert.Contains(seen, d =>
            d.Kind == BuilderDiagnosticKind.ContentApproximated &&
            d.Detail.Contains("grid", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The exception does not escape into the conversion: a PDF still comes out, and it is a PDF
    /// rather than whatever the stream happened to hold.
    /// </summary>
    [Fact]
    public void AThrowingDiagnosticHandler_DoesNotBreakTheConversion()
    {
        byte[] pdf = Convert(_ => throw new InvalidOperationException("a caller handler that throws"));

        Assert.True(pdf.Length > 8, "the conversion produced no output at all");
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    }

    // ================================================================ fixture

    /// <summary>
    /// Converts a document the managed engine declines to lay out and reports — a
    /// <c>display:grid</c> with no column template — and returns the bytes.
    /// </summary>
    private static byte[] Convert(Action<BuilderDiagnostic> onDiagnostic)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(
            "<div style='display:grid'><div>A</div><div>B</div></div>",
            output,
            new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                OnDiagnostic = onDiagnostic,
            });
        return output.ToArray();
    }
}
