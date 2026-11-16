// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Pixel-level rendering checks.  These tests exercise the full
/// <see cref="PdfPageRenderer"/> pipeline — not just "did it throw" —
/// by building tiny 100×100 pt pages, rendering them at 72 dpi (so
/// PDF points map 1:1 to PNG pixels), decoding the PNG back into a
/// <see cref="SKBitmap"/>, and sampling specific pixels to verify
/// colour / coverage / clip / blend / transform correctness.
///
/// Conventions
///  • PDF origin is bottom-left; raster origin is top-left, so a
///    PDF-space point (x, y) in a page of height H maps to pixel
///    (x, H - y).  Helper <see cref="Px"/> does the flip.
///  • Colour checks use a ±8 tolerance per channel — SkiaSharp's
///    PNG encoder is lossless but anti-aliased edges sit at half-
///    coverage, so sampled interiors are compared, edges avoided.
///  • All PDFs are built in memory — no fixtures — so these tests
///    stay hermetic across OSes and font configurations.
/// </summary>
public sealed class RenderingSmartTests
{
    // ==================================================================
    //  1. Solid-colour rect — verifies the unit-square → user-space →
    //     raster-pixel chain for DeviceRGB fill.
    // ==================================================================

    [Fact]
    public void SolidRect_FillColour_MatchesPixelExactly()
    {
        // Red rect 40 × 30 positioned at user-space (20, 20) on a 100×100 page.
        // At 72 dpi → PNG is 100 × 100.  Centre of the rect in user space
        // is (40, 35); in raster (Y-flipped) that's pixel (40, 65).
        const string cs = "1 0 0 rg 20 20 40 30 re f";
        byte[] pdf = BuildPage(100, 100, cs);

        using var bmp = Render(pdf);
        AssertPixelRgb(bmp, 40, Px(bmp.Height, 35), 255, 0, 0);
        // Outside the rect should still be the renderer's white page background.
        AssertPixelRgb(bmp, 5, 5, 255, 255, 255, tol: 12);
        AssertPixelRgb(bmp, 95, Px(bmp.Height, 5), 255, 255, 255, tol: 12);
    }

    // ==================================================================
    //  2. CTM translate — cm operator shifts origin; rect painted at
    //     (0, 0) must end up where the translate says.
    // ==================================================================

    [Fact]
    public void CtmTranslate_Rect_LandsAtTranslatedOrigin()
    {
        // Translate by (30, 40), then paint green 10×10 at origin.
        const string cs = "q 1 0 0 1 30 40 cm 0 1 0 rg 0 0 10 10 re f Q";
        byte[] pdf = BuildPage(100, 100, cs);

        using var bmp = Render(pdf);
        // Centre of rect in user space: (35, 45).
        AssertPixelRgb(bmp, 35, Px(bmp.Height, 45), 0, 255, 0);
        // Not at (5,5), which would have been the untranslated centre.
        AssertPixelRgb(bmp, 5, Px(bmp.Height, 5), 255, 255, 255, tol: 12);
    }

    // ==================================================================
    //  3. CTM rotate — 90° CW (in user space, which is 90° CCW on
    //     screen because of the Y flip).  The rect starts vertical,
    //     ends horizontal.
    // ==================================================================

    [Fact]
    public void CtmRotate90_Rect_EndsUpRotated()
    {
        // Translate to (50, 50), rotate 90°, then paint 20-wide × 5-tall blue
        // rect at origin.  After rotation it's 5-wide × 20-tall in user space.
        // PDF rotation matrix for +90° is [cos, sin, -sin, cos, 0, 0] = [0 1 -1 0 0 0].
        const string cs =
            "q 1 0 0 1 50 50 cm 0 1 -1 0 0 0 cm " +
            "0 0 1 rg 0 0 20 5 re f Q";
        byte[] pdf = BuildPage(100, 100, cs);

        using var bmp = Render(pdf);
        // After rotation: rect occupies user-space x∈[-5,0], y∈[0,20] offset by
        // (50,50), so user x∈[45,50], y∈[50,70].  Sample interior (47, 60).
        AssertPixelRgb(bmp, 47, Px(bmp.Height, 60), 0, 0, 255);
        // Pre-rotation position (50..70, 50..55) should NOT be blue.
        AssertPixelRgb(bmp, 60, Px(bmp.Height, 52), 255, 255, 255, tol: 12);
    }

    // ==================================================================
    //  4. Clipping — W n constrains subsequent paints to the clip rect.
    //     A huge coloured rect painted AFTER the clip must only colour
    //     the clipped region, everything outside stays white.
    // ==================================================================

    [Fact]
    public void ClipPath_LimitsFillToClipRegion()
    {
        // Clip to 30×30 rect at (35, 35), then fill the WHOLE page magenta.
        const string cs =
            "q 35 35 30 30 re W n " +
            "1 0 1 rg 0 0 100 100 re f Q";
        byte[] pdf = BuildPage(100, 100, cs);

        using var bmp = Render(pdf);
        // Inside clip: magenta.
        AssertPixelRgb(bmp, 50, Px(bmp.Height, 50), 255, 0, 255);
        // Outside clip: untouched white.
        AssertPixelRgb(bmp, 10, Px(bmp.Height, 10), 255, 255, 255, tol: 12);
        AssertPixelRgb(bmp, 90, Px(bmp.Height, 90), 255, 255, 255, tol: 12);
    }

    // ==================================================================
    //  5. Even-odd vs non-zero fill — two overlapping rects using f*
    //     (even-odd) produce a hole at the overlap; f (non-zero) fills
    //     the whole union.
    // ==================================================================

    [Fact]
    public void EvenOddFill_OverlappingRects_HollowsOutIntersection()
    {
        // Outer rect 80×80 at (10,10), inner rect 40×40 at (30,30) — both
        // clockwise, so non-zero winding fills both; even-odd fills the
        // outer band only and leaves the inner 40×40 hollow.
        const string cs =
            "1 0 0 rg 10 10 80 80 re 30 30 40 40 re f*";
        byte[] pdf = BuildPage(100, 100, cs);

        using var bmp = Render(pdf);
        // In the "band" between outer and inner — red.
        AssertPixelRgb(bmp, 20, Px(bmp.Height, 50), 255, 0, 0);
        // In the hole — white (untouched).
        AssertPixelRgb(bmp, 50, Px(bmp.Height, 50), 255, 255, 255, tol: 12);
    }

    // ==================================================================
    //  6. Transparency — semi-opaque red over solid cyan should give a
    //     predictable Porter-Duff SrcOver result.
    //     0.5 red (255,0,0,128) over cyan (0,255,255,255)
    //        = (128, 128, 128) approximately.
    // ==================================================================

    [Fact]
    public void AlphaBlend_SemiRedOverCyan_ProducesBlendedGrey()
    {
        // /ExtGState sets constant alpha (/ca for non-stroke).
        const string cs =
            "/GSfull gs 0 1 1 rg 0 0 100 100 re f " +      // cyan full-page
            "/GShalf gs 1 0 0 rg 20 20 60 60 re f";        // half-alpha red on top
        byte[] pdf = BuildPageWithExtGStates(100, 100, cs,
            "<< /GSfull << /ca 1 >> /GShalf << /ca 0.5 >> >>");

        using var bmp = Render(pdf);
        // Centre of the blended region.  Exact values depend on whether the
        // renderer treats alpha as linear or gamma-correct; allow ±20.
        var px = bmp.GetPixel(50, Px(bmp.Height, 50));
        Assert.InRange(px.Red,   100, 200);
        Assert.InRange(px.Green, 100, 200);
        Assert.InRange(px.Blue,  100, 200);
        // Cyan region outside the red overlay stays cyan.
        AssertPixelRgb(bmp, 5, Px(bmp.Height, 5), 0, 255, 255, tol: 12);
    }

    // ==================================================================
    //  7. Multi-page — 3 pages, 3 distinct fill colours.  Rendering each
    //     page index must yield the corresponding colour.
    // ==================================================================

    [Fact]
    public void MultiPage_EachPageRendersItsOwnContentStream()
    {
        var streams = new[]
        {
            "1 0 0 rg 0 0 100 100 re f",   // red page
            "0 1 0 rg 0 0 100 100 re f",   // green page
            "0 0 1 rg 0 0 100 100 re f",   // blue page
        };
        byte[] pdf = BuildMultiPage(100, 100, streams);

        using var bmpR = Render(pdf, page: 0);
        using var bmpG = Render(pdf, page: 1);
        using var bmpB = Render(pdf, page: 2);

        AssertPixelRgb(bmpR, 50, 50, 255, 0, 0);
        AssertPixelRgb(bmpG, 50, 50, 0, 255, 0);
        AssertPixelRgb(bmpB, 50, 50, 0, 0, 255);
    }

    // ==================================================================
    //  8. SVG vs PNG parity — the same filled red rect must appear in
    //     both outputs with matching coordinates.
    // ==================================================================

    [Fact]
    public void SvgRenderer_ProducesElementThatMatchesRasterFill()
    {
        const string cs = "1 0 0 rg 25 25 50 50 re f";
        byte[] pdf = BuildPage(100, 100, cs);

        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        Assert.Contains("<svg", svg);
        // Either a <rect> element or a <path> emitting the same fill —
        // renderers differ, but the #FF0000 colour (any case, rgb(...) or
        // hex) must be present.
        bool hasRed =
            svg.IndexOf("#ff0000", StringComparison.OrdinalIgnoreCase) >= 0 ||
            svg.IndexOf("rgb(255,0,0)", StringComparison.OrdinalIgnoreCase) >= 0 ||
            svg.IndexOf("rgb(255, 0, 0)", StringComparison.OrdinalIgnoreCase) >= 0;
        Assert.True(hasRed, $"Expected red fill in SVG; first 400 chars: {svg.Substring(0, Math.Min(400, svg.Length))}");

        // Cross-check against raster render — same pixel should be red.
        using var bmp = Render(pdf);
        AssertPixelRgb(bmp, 50, Px(bmp.Height, 50), 255, 0, 0);
    }

    // ==================================================================
    //  9. GSave/GRestore isolation — CTM and colour set inside q…Q must
    //     not leak out.  A translate + red inside, then a rect drawn
    //     AFTER Q at the original origin, must land at the original
    //     origin and be the default (black).
    // ==================================================================

    [Fact]
    public void QRestoresState_CtmAndColourLeakNotPermitted()
    {
        const string cs =
            "q 1 0 0 1 60 60 cm 1 0 0 rg 0 0 10 10 re f Q " +   // red rect at (60..70, 60..70)
            "0 g 5 5 10 10 re f";                                // black rect at (5..15, 5..15)
        byte[] pdf = BuildPage(100, 100, cs);

        using var bmp = Render(pdf);
        // Red inside q…Q survived.
        AssertPixelRgb(bmp, 65, Px(bmp.Height, 65), 255, 0, 0);
        // Black outside q…Q at original origin — not red (no CTM leak),
        // and dark (not white).
        var px = bmp.GetPixel(10, Px(bmp.Height, 10));
        Assert.InRange(px.Red,   0, 64);
        Assert.InRange(px.Green, 0, 64);
        Assert.InRange(px.Blue,  0, 64);
    }

    // ==================================================================
    // 10. Line-width scaling under CTM — non-scaled "w" combined with
    //     a scaling cm should visibly differ from the same line without
    //     the scale.  This catches a class of bugs where the renderer
    //     applies line width in raw device pixels instead of scaling
    //     with the CTM.
    // ==================================================================

    [Fact]
    public void StrokeUnderScale_LineWidthScalesWithCtm()
    {
        // Baseline: 1 px stroke, horizontal line 40 pt long.
        const string baseline = "0 g 1 w 30 50 m 70 50 l S";
        byte[] basePdf = BuildPage(100, 100, baseline);

        // Scaled: identical stroke, but CTM zoom 4× — stroke width should
        // render as ~4 px wide instead of 1 px.
        const string scaled = "q 4 0 0 4 -112.5 -150 cm 0 g 1 w 30 50 m 70 50 l S Q";
        byte[] scalePdf = BuildPage(100, 100, scaled);

        using var baseBmp  = Render(basePdf);
        using var scaleBmp = Render(scalePdf);

        // Count black pixels in the column that hits the line — a 1 px stroke
        // produces ≤2 fully-dark pixels (+AA); a 4 px stroke produces ≥3.
        int baseDark  = CountDarkPixelsInColumn(baseBmp,  50);
        int scaleDark = CountDarkPixelsInColumn(scaleBmp, 50);

        Assert.True(baseDark  >= 1, $"Baseline stroke not rendered: dark={baseDark}");
        Assert.True(scaleDark >  baseDark,
            $"Expected scaled stroke darker than baseline (base={baseDark}, scaled={scaleDark})");
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static SKBitmap Render(byte[] pdf, int page = 0)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, page, 72);
        return SKBitmap.Decode(png)
            ?? throw new InvalidOperationException("PNG decode returned null");
    }

    /// <summary>Flip a PDF user-space Y coord to a raster Y coord for a bitmap.</summary>
    private static int Px(int bitmapHeight, float pdfY)
        => (int)Math.Round(bitmapHeight - pdfY);

    private static void AssertPixelRgb(SKBitmap bmp, int x, int y,
        int r, int g, int b, int tol = 8)
    {
        Assert.InRange(x, 0, bmp.Width  - 1);
        Assert.InRange(y, 0, bmp.Height - 1);
        var px = bmp.GetPixel(x, y);
        bool ok = Math.Abs(px.Red   - r) <= tol
               && Math.Abs(px.Green - g) <= tol
               && Math.Abs(px.Blue  - b) <= tol;
        Assert.True(ok,
            $"Pixel ({x},{y}) expected RGB≈({r},{g},{b})±{tol}, got ({px.Red},{px.Green},{px.Blue})");
    }

    private static int CountDarkPixelsInColumn(SKBitmap bmp, int x)
    {
        // Anti-aliased 1-pixel strokes never produce *fully*-black pixels;
        // SkiaSharp distributes energy across 2 rows at ~50% coverage each,
        // so the per-channel threshold must be set to "noticeably darker than
        // white" rather than "near-zero".  Pixels below avg 192 are considered
        // dark — that's a conservative bound that admits AA grey but rejects
        // the page background (255,255,255).
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            var p = bmp.GetPixel(x, y);
            if (p.Red < 192 && p.Green < 192 && p.Blue < 192) n++;
        }
        return n;
    }

    // ------------------ PDF builders (in-memory ASCII) -----------------

    private static byte[] BuildPage(float w, float h, string contentStream)
        => BuildPageImpl(w, h, contentStream, resources: "<< >>");

    private static byte[] BuildPageWithExtGStates(float w, float h,
        string contentStream, string extGStateDict)
        => BuildPageImpl(w, h, contentStream, resources: $"<< /ExtGState {extGStateDict} >>");

    private static byte[] BuildPageImpl(float w, float h, string contentStream, string resources)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0} {1}]" +
            " /Contents 4 0 R /Resources {2} >>\nendobj\n", w, h, resources);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildMultiPage(float w, float h, string[] streams)
    {
        // Layout: 1 = Catalog, 2 = Pages, 3..3+n-1 = Page dicts, then n content streams.
        int n = streams.Length;
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int oCatalog = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int oPages = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [");
        for (int i = 0; i < n; i++) sb.AppendFormat("{0} 0 R ", 3 + i);
        sb.AppendFormat("] /Count {0} >>\nendobj\n", n);

        int[] pageOffsets    = new int[n];
        int[] contentOffsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            pageOffsets[i] = sb.Length;
            sb.AppendFormat(
                "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {1} {2}]" +
                " /Contents {3} 0 R /Resources << >> >>\nendobj\n",
                3 + i, w, h, 3 + n + i);
        }
        for (int i = 0; i < n; i++)
        {
            contentOffsets[i] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n<< /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
                3 + n + i, streams[i].Length, streams[i]);
        }

        int xref = sb.Length;
        int total = 1 + 1 + (2 * n); // catalog + pages + n pages + n streams
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", total + 1);
        sb.AppendFormat("{0:D10} 00000 n \n", oCatalog);
        sb.AppendFormat("{0:D10} 00000 n \n", oPages);
        for (int i = 0; i < n; i++) sb.AppendFormat("{0:D10} 00000 n \n", pageOffsets[i]);
        for (int i = 0; i < n; i++) sb.AppendFormat("{0:D10} 00000 n \n", contentOffsets[i]);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", total + 1);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
