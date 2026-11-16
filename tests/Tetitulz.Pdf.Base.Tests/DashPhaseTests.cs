// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Regression tests for negative dash-phase handling (ISO 32000-2 §8.4.3.6):
/// "If the dash phase is negative, it shall be incremented by twice the sum of
/// all lengths in the dash array until it is positive."
///
/// For an odd-length dash array the on/off roles swap on each repeat, so the
/// true repeat period is 2×sum, not sum.  A negative phase must therefore be
/// normalised modulo 2×sum; reducing modulo a single sum lands the phase a
/// half-period off and inverts the dashes.  This is the bug exercised by the
/// PDF Association's Negative-DashPhase.pdf test file.
/// </summary>
public sealed class DashPhaseTests
{
    /// <summary>
    /// Renders a single red, dash-stroked horizontal line whose dash spec is the
    /// supplied <paramref name="dashSpec"/> (e.g. "[ 20 0 0 10 10 ] -1") at 72 DPI
    /// and returns the decoded bitmap.
    /// </summary>
    private static SKBitmap RenderDashedLine(string dashSpec)
    {
        string content =
            "1 0 0 RG\n" +     // red stroke
            "5 w\n" +          // 5-unit line width
            $"{dashSpec} d\n" +
            "20 400 m 400 400 l S";

        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(contentStream: content);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>Counts pixels that differ between two equally sized bitmaps.</summary>
    private static int CountDifferingPixels(SKBitmap a, SKBitmap b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
        int diff = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y))
                    diff++;
        return diff;
    }

    // sum([20 0 0 10 10]) = 40, so the effective (doubled) period is 80.
    // Phase -1 must normalise to -1 + 80 = 79 (NOT -1 + 40 = 39).

    [Fact]
    public void OddLengthArray_NegativePhase_MatchesEquivalentPositivePhase()
    {
        // -1 and 79 are exactly one effective period (80) apart → identical render.
        using var negative = RenderDashedLine("[ 20 0 0 10 10 ] -1");
        using var positive = RenderDashedLine("[ 20 0 0 10 10 ] 79");

        Assert.Equal(0, CountDifferingPixels(negative, positive));
    }

    [Fact]
    public void OddLengthArray_NegativePhase_DoesNotUseSingleSumPeriod()
    {
        // The pre-fix bug reduced -1 modulo the single sum (40) → phase 39, which
        // is half a period off and renders a visibly different (inverted) dash.
        using var negative   = RenderDashedLine("[ 20 0 0 10 10 ] -1");
        using var buggyPhase = RenderDashedLine("[ 20 0 0 10 10 ] 39");

        Assert.True(CountDifferingPixels(negative, buggyPhase) > 0,
            "negative phase -1 must not render like the buggy single-sum phase 39");
    }

    [Fact]
    public void EvenLengthArray_NegativePhase_MatchesEquivalentPositivePhase()
    {
        // sum([10 10]) = 20; even arrays repeat every sum, so -1 ≡ 19.
        using var negative = RenderDashedLine("[ 10 10 ] -1");
        using var positive = RenderDashedLine("[ 10 10 ] 19");

        Assert.Equal(0, CountDifferingPixels(negative, positive));
    }

    [Fact]
    public void EmptyArray_NegativePhase_RendersSolidLine()
    {
        // Spec: an empty dash array is a solid line regardless of phase.
        using var empty = RenderDashedLine("[ ] -1");
        using var solid = RenderDashedLine("[ ] 0");

        Assert.Equal(0, CountDifferingPixels(empty, solid));
    }
}
