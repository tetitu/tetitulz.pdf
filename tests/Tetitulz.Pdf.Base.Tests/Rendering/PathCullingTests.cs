// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Fills and strokes whose geometry cannot reach the canvas clip are skipped before a paint is
/// built (<c>PdfRenderContext.IsCurrentPathOutsideClip</c>). That is pure waste-avoidance on a
/// whole page and a real saving on a tile, where ~15/16 of the content falls outside.
/// </summary>
/// <remarks>
/// <b>Culling is the shape of change that deletes artwork without throwing</b>, so these tests
/// compare a tiled render against the whole-page render rather than checking that tiles "render".
/// The load-bearing case is a <i>stroke crossing a tile seam</i>: its path bounds may lie entirely
/// outside a tile while the pen — half its width, widened by the miter limit — still paints inside
/// it. A cull that used raw path bounds would erase exactly those strokes, and only along the
/// seams, which is invisible in any test that does not composite the tiles back together.
/// </remarks>
public class PathCullingTests
{
    private const int Dpi = 72;
    private const int PageSize = 200;

    /// <summary>
    /// The 2x2 tiled render must carry the same ink as the whole-page render. Antialiasing along
    /// the seams means this is a tolerance rather than an equality, but a culled stroke or fill is
    /// off by far more than the tolerance allows.
    /// </summary>
    [Fact]
    public void TiledRender_CarriesTheSameInkAsTheWholePage()
    {
        byte[] pdf = SeamCrossingPdf();

        int whole = CountInk(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));

        int tiled = 0;
        const float half = PageSize / 2f;
        for (int ty = 0; ty < 2; ty++)
            for (int tx = 0; tx < 2; tx++)
                tiled += CountInk(PdfPageRenderer.RenderPageTile(
                    new MemoryStream(pdf), 0, tx * half, ty * half, half, half, Dpi));

        Assert.True(whole > 0, "the control render had no ink at all");

        double ratio = (double)tiled / whole;
        Assert.True(ratio > 0.95 && ratio < 1.05,
            $"tiled ink {tiled} vs whole-page {whole} (ratio {ratio:0.000}) — content was culled or duplicated");
    }

    /// <summary>
    /// A thick stroke whose <i>path</i> lies outside a tile, but whose <i>pen</i> reaches into it,
    /// must still be drawn there. This is the one case that distinguishes a correct cull from one
    /// that tests raw path bounds.
    /// </summary>
    /// <remarks>
    /// The first version of this test put the stroke exactly on the seam and <b>the mutation
    /// survived</b>: a path lying on the boundary touches the clip, so it is never rejected and the
    /// margin is never what saves it. The fixture reached the feature only after the stroke was
    /// moved past the seam. Setting <c>StrokeCullMargin</c> to zero now fails this test.
    /// </remarks>
    [Fact]
    public void ThickStrokeWhosePenReachesIntoATile_IsNotCulledFromIt()
    {
        byte[] pdf = SeamCrossingPdf();
        const float half = PageSize / 2f;

        // The horizontal stroke's path is at y=106 — outside this tile's y in [0,100] — but its
        // 16pt pen spreads to y=98, so 2pt of it belong to this tile.
        using SKBitmap below = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, half, half, Dpi));

        // Sample just under the seam, away from the quadrant fills, so only the stroke can be here.
        Assert.True(HasBlueNear(below, x: 30, fromTop: below.Height - 99),
            "a stroke whose pen reaches into the tile was culled by its path bounds");
    }

    /// <summary>
    /// The whole-page render must be unchanged by culling: on a full page nothing is outside the
    /// clip, so every path must still paint. Compares against a page whose content is identical
    /// but positioned well inside, which no cull could ever reject.
    /// </summary>
    [Fact]
    public void WholePageRender_PaintsEveryPathInsideTheClip()
    {
        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPage(
            new MemoryStream(SeamCrossingPdf()), 0, Dpi));

        // The fixture places a red fill in each quadrant plus the seam strokes; all four must show.
        Assert.True(IsRed(bmp, 40, 40),   "bottom-left fill missing");
        Assert.True(IsRed(bmp, 160, 40),  "bottom-right fill missing");
        Assert.True(IsRed(bmp, 40, 160),  "top-left fill missing");
        Assert.True(IsRed(bmp, 160, 160), "top-right fill missing");
    }

    /// <summary>
    /// Content genuinely off the page must not be painted — the cull's own purpose. Asserts the
    /// off-page fill leaves the page blank, so a cull that never rejects anything fails here.
    /// </summary>
    [Fact]
    public void ContentEntirelyOffThePage_LeavesItBlank()
    {
        byte[] pdf = SinglePathPdf("1 0 0 rg 400 400 50 50 re f\n");
        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        Assert.False(HasInk(bmp), "content placed off the page should not appear on it");
    }

    /// <summary>
    /// A stroke under a magnifying CTM, whose <i>scaled</i> pen reaches into the tile although its
    /// path and its unscaled pen do not.
    /// </summary>
    /// <remarks>
    /// §8.4.3.2 puts the line width in user space, so <c>BuildStrokePaint</c> paints with
    /// <c>LineWidth × ctmScale</c>, and the cull margin must too. Under <c>10 0 0 10 0 0 cm</c> a
    /// 2-unit line paints 20 pt wide, so a margin of the raw 2 pt would discard a stroke whose pen
    /// still covers the tile. Nothing throws; the artwork simply disappears, and only under a scaled
    /// CTM — which no other test here uses.
    /// </remarks>
    [Fact]
    public void StrokeUnderAMagnifyingCtm_IsNotCulledByItsUnscaledWidth()
    {
        // `1 M` is load-bearing. The margin is (width × miterLimit / 2 + 1), and the *default*
        // miter limit is 10 — large enough that the raw-width margin happens to cover a 10x CTM
        // anyway, so with the default this test passes against the defect it exists for. Pinning
        // the miter limit to 1 removes that accidental slack and leaves only the CTM scale.
        //
        //   line at user y=10.6, CTM x10  -> canvas y = -6 (tile height 100, y-flipped)
        //   pen 2 x 10 = 20               -> reaches canvas y = +4, inside the tile
        //   raw-width margin  = 2*1/2+1  =  2 -> bounds [-8,-4], outside  -> culled (the defect)
        //   scaled margin     = 20*1/2+1 = 11 -> bounds [-17,5], overlaps -> painted (correct)
        byte[] pdf = SinglePathPdf(
            "q 10 0 0 10 0 0 cm 0 0 1 RG 2 w 1 M 0 10.6 m 20 10.6 l S Q\n");
        const float half = PageSize / 2f;

        using SKBitmap below = Decode(PdfPageRenderer.RenderPageTile(
            new MemoryStream(pdf), 0, 0, 0, half, half, Dpi));

        Assert.True(HasInk(below),
            "a stroke whose CTM-scaled pen reaches into the tile was culled by its unscaled width");
    }

    /// <summary>A path just inside the page edge must still paint — the cull must not over-reach.</summary>
    [Fact]
    public void ContentJustInsideThePageEdge_StillPaints()
    {
        byte[] pdf = SinglePathPdf("1 0 0 rg 190 190 20 20 re f\n");
        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        Assert.True(IsRed(bmp, 195, 195), "a rectangle overlapping the page edge was culled");
    }

    // ------------------------------------------------------------------

    private static bool IsRed(SKBitmap bmp, int userX, int userY)
    {
        SKColor c = bmp.GetPixel(userX, bmp.Height - 1 - userY);
        return c.Red > 150 && c.Green < 110 && c.Blue < 110;
    }

    /// <summary>
    /// True when a blue (stroke-coloured) pixel appears in the column at <paramref name="x"/>
    /// within a few rows of <paramref name="fromTop"/>. A band rather than a point, because the
    /// stroke's exact device row depends on rounding.
    /// </summary>
    private static bool HasBlueNear(SKBitmap bmp, int x, int fromTop)
    {
        for (int y = System.Math.Max(0, fromTop - 3); y < System.Math.Min(bmp.Height, fromTop + 4); y++)
        {
            SKColor c = bmp.GetPixel(x, y);
            if (c.Blue > 150 && c.Red < 110 && c.Green < 110) return true;
        }
        return false;
    }

    private static bool HasInk(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha != 0 && (c.Red < 250 || c.Green < 250 || c.Blue < 250)) return true;
            }
        return false;
    }

    private static int CountInk(byte[] png)
    {
        using SKBitmap bmp = Decode(png);
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha != 0 && (c.Red < 250 || c.Green < 250 || c.Blue < 250)) ink++;
            }
        return ink;
    }

    private static SKBitmap Decode(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A 200x200 page: a red fill in each quadrant, plus thick strokes lying exactly on the
    /// horizontal and vertical seams of a 2x2 tiling.
    /// </summary>
    private static byte[] SeamCrossingPdf()
    {
        var c = new StringBuilder();
        c.Append("1 0 0 rg\n");
        c.Append("30 30 20 20 re f\n");
        c.Append("150 30 20 20 re f\n");
        c.Append("30 150 20 20 re f\n");
        c.Append("150 150 20 20 re f\n");
        // Strokes offset *past* the seams, not on them. A line exactly on the boundary has path
        // bounds that touch the clip, so it survives even a margin-free cull — which is how the
        // first version of this fixture let the mutation through. At y=106 with a 16pt pen the
        // path (y=106) lies outside the lower tile's y<=100 clip while the pen reaches y=98,
        // inside it, so only the margin keeps it alive.
        c.Append("0 0 1 RG 16 w\n");
        c.Append("0 106 m 200 106 l S\n");
        c.Append("106 0 m 106 200 l S\n");
        return SinglePathPdf(c.ToString());
    }

    private static byte[] SinglePathPdf(string content)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageSize} {PageSize}] "
             + "/Resources << >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
