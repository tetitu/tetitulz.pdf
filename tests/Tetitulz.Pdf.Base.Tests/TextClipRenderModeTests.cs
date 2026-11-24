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
/// The text-clipping render modes (PDF/ISO 32000-2 §9.3.6, text rendering modes 4-7) on
/// the standard-font (SKTypeface) render path.
///
/// Two defects are guarded:
///   1. A draw handling only modes 0/1/2 would paint nothing for a clipping mode AND
///      never feed the glyph outlines into the clip accumulator — the subsequent fill
///      drawn un-clipped (the entire page filled). The PDF Association's
///      OverlappingGlyphClipping.pdf would render as a plain blue rectangle.
///   2. The clip accumulator combines glyph outlines with the non-zero winding number
///      rule the spec mandates, not even-odd. NOTE 2 of §9.3.6:
///      "Due to the use of non-zero winding number rule, the direction of the
///      paths comprising each glyph can cause different output for overlapping
///      glyphs." Even-odd makes overlapping glyphs cancel to nothing.
/// </summary>
public sealed class TextClipRenderModeTests
{
    private static SKBitmap Render(string content)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(contentStream: content);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    // A pixel that is unambiguously the blue fill (0 0 1 rg), tolerant of edge AA.
    private static bool IsBlue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;

    // A pixel that is unambiguously the red stroke (1 0 0 RG).
    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    private static int Count(SKBitmap bmp, System.Func<SKColor, bool> pred)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (pred(bmp.GetPixel(x, y))) n++;
        return n;
    }

    [Fact]
    public void TextRenderMode4_ClipsFollowingFill()
    {
        // Mode 4 = fill text + add to clip. A large glyph defines the clip; the
        // page-filling blue rectangle must then only paint inside the glyph shape.
        using var bmp = Render(
            "BT\n" +
            "/F1 400 Tf\n" +
            "4 Tr\n" +                     // fill + clip
            "1 0 0 1 100 200 Tm\n" +
            "(B) Tj\n" +
            "ET\n" +
            "0 0 1 rg\n" +
            "0 0 612 792 re f\n");

        int blue  = Count(bmp, IsBlue);
        int total = bmp.Width * bmp.Height;

        // Clip produced a non-empty region (the glyph was added to the clip)...
        Assert.True(blue > 1000, $"expected blue glyph fill, got {blue} px");
        // ...but it did NOT fill the whole page (the clip was actually applied).
        // Unclipped, it would fill the entire page (~100%).
        Assert.True(blue < total * 0.30,
            $"fill was not clipped to the glyph: {blue}/{total} px blue");
    }

    [Fact]
    public void TextRenderMode7_ClipOnly_StillClipsFollowingFill()
    {
        // Mode 7 = clip only (no fill/stroke). The glyph must still be accumulated
        // into the clip even though it paints nothing itself.
        using var clipped = Render(
            "BT /F1 400 Tf 7 Tr 1 0 0 1 100 200 Tm (B) Tj ET\n" +
            "0 0 1 rg 0 0 612 792 re f\n");
        using var unclipped = Render("0 0 1 rg 0 0 612 792 re f\n");

        int blueClipped   = Count(clipped, IsBlue);
        int blueUnclipped = Count(unclipped, IsBlue);

        Assert.True(blueClipped > 1000, "mode 7 should leave a glyph-shaped clip");
        Assert.True(blueClipped < blueUnclipped / 2,
            "mode 7 clip must restrict the subsequent fill");
    }

    [Fact]
    public void OverlappingClipGlyphs_UseNonZeroWinding_NotEvenOdd()
    {
        // Draw the SAME glyph twice at the SAME position in clip mode, then fill blue.
        // Non-zero winding rule: every covered region winds ±2 (still non-zero) → it
        // stays filled, so the doubled clip is at least as large as the single clip.
        // Even-odd rule (the defect): every region is crossed an even number of
        // times → it cancels to an empty clip → essentially no blue at all.
        using var single = Render(
            "BT /F1 400 Tf 7 Tr 1 0 0 1 100 200 Tm (B) Tj ET\n" +
            "0 0 1 rg 0 0 612 792 re f\n");
        using var doubled = Render(
            "BT /F1 400 Tf 7 Tr " +
            "1 0 0 1 100 200 Tm (B) Tj " +
            "1 0 0 1 100 200 Tm (B) Tj ET\n" +   // re-set Tm → exact overlap
            "0 0 1 rg 0 0 612 792 re f\n");

        int blueSingle  = Count(single, IsBlue);
        int blueDoubled = Count(doubled, IsBlue);

        Assert.True(blueSingle > 1000, "sanity: single-glyph clip is non-empty");
        // With non-zero winding the overlap survives (and the inner counters even
        // fill in, since they wind -2), so doubled >= single. Even-odd would give ~0.
        Assert.True(blueDoubled >= blueSingle * 0.9,
            $"overlapping clip glyphs cancelled (even-odd?): single={blueSingle} doubled={blueDoubled}");
    }

    [Fact]
    public void TextClipMode_EmptyGlyphOutline_ClipsOutFollowingContent()
    {
        // Mode 7 clip whose only glyph is a space — which has no outline — so the
        // accumulated clip path is empty. Per PDF/ISO 32000-2 §9.4.3 the resulting
        // clip region is empty, and the page-filling blue rectangle drawn after ET
        // must be clipped out entirely.
        //
        // Treating an empty accumulator the same as "no clip requested" would leave
        // the clip unchanged, and the rectangle would fill the whole page. On real
        // card statements that leaks gradient image tiles (masked by text-clip
        // glyphs) as solid "gradient boxes" wherever a blank clip glyph sits between
        // the gradient-filled letters.
        using var bmp = Render(
            "BT /F1 400 Tf 7 Tr 1 0 0 1 100 200 Tm ( ) Tj ET\n" +
            "0 0 1 rg 0 0 612 792 re f\n");

        int blue = Count(bmp, IsBlue);
        Assert.True(blue < 100,
            $"empty text clip must clip out the following fill, but {blue} blue px were drawn");
    }

    [Fact]
    public void TextRenderMode5_StrokesTheText()
    {
        // Mode 5 = stroke text + add to clip. The stroke itself must be painted
        // (a draw ignoring mode 5 would draw nothing).
        using var bmp = Render(
            "1 0 0 RG\n" +     // red stroke
            "6 w\n" +
            "BT /F1 300 Tf 5 Tr 1 0 0 1 100 200 Tm (B) Tj ET\n");

        Assert.True(Count(bmp, IsRed) > 200, "mode 5 must stroke the glyph outline");
    }
}
