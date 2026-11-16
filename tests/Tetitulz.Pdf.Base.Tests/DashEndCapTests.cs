// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Regression tests for end-cap rendering at a dashed path's terminal point when
/// the path ends exactly at (or a hair past) a dash-period boundary — the PDF 2.0
/// "end before bend" case (ISO 32000-2 §8.4.3.6, PDF Association's
/// Dashing-EndBeforeBend.pdf).
///
/// Each dash segment is an independent open subpath and receives end-cap
/// processing.  When a path ends right where a new ON dash begins, that ON dash
/// has (near) zero length; Skia drops sub-pixel dashes, so the end cap would never
/// render.  <c>DrawDashEndpointCaps</c> paints it explicitly.
///
/// The bug: coordinates such as 79.99999 make the contour length a hair past a
/// dash-period multiple (e.g. 600.00001), but that excess is below float epsilon
/// at those magnitudes, so the computed phase collapses to exactly 0.  The old
/// <c>phaseAtEnd &gt; 0f</c> guard then skipped the cap entirely, leaving the
/// right-end tips of round/square-capped dashed paths blank.
/// </summary>
public sealed class DashEndCapTests
{
    private const int Dpi = 72;          // 1 device px == 1 PDF unit
    private const float PageH = 792f;    // MinimalPdfBuilder MediaBox height

    /// <summary>Renders a 10-wide [20 20] dash-stroked path with the given line-cap.</summary>
    private static SKBitmap Render(int lineCap, string path)
    {
        string content =
            "0 0 1 RG\n" +     // blue stroke
            "10 w\n" +
            "[20 20] 0 d\n" +
            $"{lineCap} J\n" +
            path;
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(contentStream: content);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>Counts non-white pixels inside a device-space rectangle.</summary>
    private static int ColoredPixels(SKBitmap bmp, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.Red < 240 || p.Green < 240 || p.Blue < 240) n++;
            }
        return n;
    }

    // A vertical line of length 600 with a [20 20] dash (period 40) ends exactly at
    // phase 0: the last ON dash is [560,580] and the endpoint sits 20 units (one OFF
    // gap) beyond it.  Round/square caps must paint a mark at the terminal point;
    // a butt cap leaves it blank.  The endpoint (100,700) maps to device (100, 92).
    private const string VerticalLine = "100 100 m 100 700 l S";

    [Theory]
    [InlineData(1)]   // round
    [InlineData(2)]   // square
    public void TerminalDashEndsAtPeriodBoundary_RoundOrSquare_PaintsEndCap(int lineCap)
    {
        using var bmp = Render(lineCap, VerticalLine);
        // Box around the terminal point (device 100,92), inside the trailing OFF gap
        // so the only thing that can appear here is the synthetic end cap.
        int cap = ColoredPixels(bmp, 88, 82, 112, 102);
        Assert.True(cap > 0,
            $"line cap {lineCap}: expected an end cap at the path terminal point, found none");
    }

    [Fact]
    public void TerminalDashEndsAtPeriodBoundary_ButtCap_PaintsNothing()
    {
        using var bmp = Render(0, VerticalLine);
        // Butt caps have no extent, so the zero-length terminal ON dash is invisible.
        int cap = ColoredPixels(bmp, 88, 82, 112, 102);
        Assert.Equal(0, cap);
    }

    [Fact]
    public void RoundCap_TerminalIsADotSeparatedFromTheLastDash()
    {
        using var bmp = Render(1, VerticalLine);
        // Synthetic dot at device y≈92 (PDF y=700).
        Assert.True(ColoredPixels(bmp, 88, 82, 112, 102) > 0, "missing terminal dot");
        // A gap must separate it from the last rendered dash, whose round cap ends at
        // device y≈107 (PDF y=685).  Rows 100..105 fall in that gap and must be blank.
        Assert.Equal(0, ColoredPixels(bmp, 88, 100, 112, 106));
    }

    // Mirrors the real Dashing-EndBeforeBend.pdf: an L-shaped staple whose final leg
    // ends at X.99999, making the contour length 600.00001 → 600.0f (phase collapses
    // to 0).  The right-end tip must still show the round cap.  Endpoint (500,99.99999)
    // maps to device (500, ~692).
    [Fact]
    public void EndBeforeBend_LShape_RoundCap_PaintsRightTipCap()
    {
        using var bmp = Render(1, "100 100 m 100 200 l 500 200 l 500 99.99999 l S");
        int devY = (int)(PageH - 100f);   // ≈ 692
        int cap = ColoredPixels(bmp, 488, devY - 10, 512, devY + 10);
        Assert.True(cap > 0,
            "end-before-bend right-end tip is missing its round end cap");
    }
}
