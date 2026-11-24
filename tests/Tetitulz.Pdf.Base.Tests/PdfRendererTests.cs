// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Integration tests for the pure-C# PDF page renderer (gap 12.1).
///
/// Each test renders a purpose-built minimal PDF and makes assertions on:
///   - Return type / size / format validity
///   - Page dimensions at the requested DPI
///   - Pixel colour at known coordinates
///   - Correct handling of edge inputs (invalid page index, bad DPI, null stream, etc.)
///
/// Pixel colour validation uses SkiaSharp to decode the PNG output so we stay
/// fully within the managed stack (no Python / external tool required).
/// </summary>
public sealed class PdfRendererTests
{
    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    /// <summary>Decodes PNG bytes produced by the renderer into an SKBitmap.</summary>
    private static SKBitmap DecodePng(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>
    /// Samples the pixel at (x, y) in the decoded bitmap and returns its SKColor.
    /// SkiaSharp origin is top-left; PDF origin is bottom-left.
    /// Use <paramref name="fromBottom"/> = true when giving a PDF y-coordinate.
    /// </summary>
    private static SKColor SamplePixel(SKBitmap bmp, int x, int y, bool fromBottom = false)
    {
        int sy = fromBottom ? bmp.Height - 1 - y : y;
        sy = Math.Clamp(sy, 0, bmp.Height - 1);
        x  = Math.Clamp(x,  0, bmp.Width  - 1);
        return bmp.GetPixel(x, sy);
    }

    /// <summary>Renders page 0 of an in-memory PDF at 72 DPI (1 px per point).</summary>
    private static byte[] Render72(byte[] pdfBytes, int pageIndex = 0)
    {
        using var ms = new MemoryStream(pdfBytes);
        return PdfOperations.RenderPageToPng(ms, pageIndex, dpi: 72);
    }

    // ================================================================
    //  1. Basic API contract
    // ================================================================

    [Fact]
    public void RenderPage_Stream_ReturnsPngBytes()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        byte[] png = Render72(pdf);

        Assert.NotNull(png);
        Assert.True(png.Length > 100, "PNG should contain data");
        // PNG magic bytes: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }

    [Fact]
    public void RenderPage_FilePath_ReturnsSameAsSteam()
    {
        byte[] pdf  = MinimalPdfBuilder.CreateMinimalPdf();
        string path = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(path, pdf);
            byte[] fromFile   = PdfOperations.RenderPageToPng(path, 0, dpi: 72);
            byte[] fromStream = Render72(pdf);
            // Both should be valid PNGs of the same size.
            using var b1 = DecodePng(fromFile);
            using var b2 = DecodePng(fromStream);
            Assert.Equal(b1.Width,  b2.Width);
            Assert.Equal(b1.Height, b2.Height);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RenderAllPages_ReturnsOneArrayEntryPerPage()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        using var ms = new MemoryStream(pdf);
        byte[][] pages = PdfOperations.RenderAllPagesToPng(ms, dpi: 72);
        Assert.Equal(3, pages.Length);
        foreach (var p in pages)
        {
            Assert.NotNull(p);
            Assert.True(p.Length > 0);
        }
    }

    // ================================================================
    //  2. Output dimensions
    // ================================================================

    [Theory]
    [InlineData(72,  612, 792)]   // 1 px/pt  → 612 × 792
    [InlineData(144, 1224, 1584)] // 2 px/pt
    [InlineData(36,  306, 396)]   // 0.5 px/pt
    public void RenderPage_DimensionsMatchDpi(int dpi, int expectedW, int expectedH)
    {
        byte[] pdf = MinimalPdfBuilder.CreateBlankPdf();
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi);
        using var bmp = DecodePng(png);

        Assert.Equal(expectedW, bmp.Width);
        Assert.Equal(expectedH, bmp.Height);
    }

    [Theory]
    [InlineData(90,  792, 612)]  // portrait ↔ landscape
    [InlineData(270, 792, 612)]
    [InlineData(180, 612, 792)]  // upside-down; dimensions unchanged
    public void RenderPage_Rotation_AffectsDimensions(int rotate, int expectedW, int expectedH)
    {
        byte[] pdf = MinimalPdfBuilder.CreateRotatedPdf(rotate);
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        Assert.Equal(expectedW, bmp.Width);
        Assert.Equal(expectedH, bmp.Height);
    }

    /// <summary>
    /// <c>/Rotate</c> must turn the <b>content</b>, not merely the canvas.
    /// </summary>
    /// <remarks>
    /// The dimensions-only theory above cannot see this: its fixture
    /// (<c>MinimalPdfBuilder.CreateRotatedPdf</c>) is a full-page uniform grey fill, so a renderer
    /// that sized the bitmap correctly and then drew the page unrotated — or rotated it the wrong
    /// way — passes every case. This uses an asymmetric mark instead: a black square in the
    /// bottom-left corner of an otherwise white page, whose corner after rotation is arithmetic
    /// rather than a matter of taste.
    /// </remarks>
    [Theory]
    // /Rotate turns the page CLOCKWISE for display, so bottom-left goes to top-left at 90°.
    [InlineData(0,   false, true)]    // bottom-left
    [InlineData(90,  false, false)]   // top-left
    [InlineData(180, true,  false)]   // top-right
    [InlineData(270, true,  true)]    // bottom-right
    public void RenderPage_Rotation_MovesTheContentToTheExpectedCorner(
        int rotate, bool expectRight, bool expectBottom)
    {
        byte[] pdf = RotatedCornerMarkPdf(rotate);
        using var bmp = Rendering.PixelAssert.Render(pdf);

        int inset = 20;
        int x = expectRight  ? bmp.Width  - inset : inset;
        int y = expectBottom ? bmp.Height - inset : inset;

        Assert.True(Rendering.PixelAssert.IsInk(bmp.GetPixel(x, y)),
            $"/Rotate {rotate}: expected the mark at ({x},{y}) of {bmp.Width}x{bmp.Height}");

        // …and only there, so a renderer that paints the whole page cannot pass.
        int oppX = expectRight  ? inset : bmp.Width  - inset;
        int oppY = expectBottom ? inset : bmp.Height - inset;
        Assert.False(Rendering.PixelAssert.IsInk(bmp.GetPixel(oppX, oppY)),
            $"/Rotate {rotate}: the opposite corner ({oppX},{oppY}) should be blank");
    }

    /// <summary>A 200x200 black square at the origin of a 400x600 page, with the given /Rotate.</summary>
    private static byte[] RotatedCornerMarkPdf(int rotate)
    {
        const string cs = "0 g 0 0 200 200 re f";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] /Rotate {rotate}" +
            " /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {cs.Length} >>\nstream\n{cs}\nendstream",
        };

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ================================================================
    //  3. Background / blank page
    // ================================================================

    [Fact]
    public void RenderPage_BlankPdf_AllPixelsWhite()
    {
        byte[] pdf = MinimalPdfBuilder.CreateBlankPdf();
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // Sample corners and centre.
        foreach (var (x, y) in new[] { (0,0),(611,0),(0,791),(611,791),(306,396) })
        {
            var col = bmp.GetPixel(x, y);
            Assert.Equal(255, col.Red);
            Assert.Equal(255, col.Green);
            Assert.Equal(255, col.Blue);
        }
    }

    // ================================================================
    //  4. Color fill rendering
    // ================================================================

    [Fact]
    public void RenderPage_RedRectangle_LeftHalfIsRed_RightHalfIsWhite()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithRedRectangle();
        using var ms  = new MemoryStream(pdf);
        // Use 72 DPI → 1 px/pt.  Red rect covers x ∈ [0, 306].
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // Left half: red (sample near x=10, bottom quarter of page)
        var leftPx = SamplePixel(bmp, 10, 20, fromBottom: true);
        Assert.True(leftPx.Red > 200,   $"Expected red at left; got {leftPx}");
        Assert.True(leftPx.Green < 50,  $"Expected red at left; got {leftPx}");
        Assert.True(leftPx.Blue  < 50,  $"Expected red at left; got {leftPx}");

        // Right half: white
        var rightPx = SamplePixel(bmp, 400, 20, fromBottom: true);
        Assert.True(rightPx.Red   > 200, $"Expected white at right; got {rightPx}");
        Assert.True(rightPx.Green > 200, $"Expected white at right; got {rightPx}");
        Assert.True(rightPx.Blue  > 200, $"Expected white at right; got {rightPx}");
    }

    [Fact]
    public void RenderPage_GrayFill_PixelsAreGray()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithGrayFill();
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // 50% gray → r == g == b ≈ 127–128, all within ±10.
        var px = SamplePixel(bmp, 306, 396, fromBottom: false);
        Assert.True(Math.Abs(px.Red - 128) < 15,   $"Expected ~128 red; got {px}");
        Assert.True(Math.Abs(px.Green - 128) < 15, $"Expected ~128 green; got {px}");
        Assert.True(Math.Abs(px.Blue - 128) < 15,  $"Expected ~128 blue; got {px}");
        // Must be neutral (r ≈ g ≈ b).
        Assert.True(Math.Abs(px.Red - px.Green) < 10, $"Expected neutral gray; got {px}");
    }

    [Fact]
    public void RenderPage_CmykMagentaFill_PixelsAreMagenta()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithCmykFill();
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // CMYK 0 1 0 0 → RGB ≈ (255, 0, 255) magenta.
        var px = SamplePixel(bmp, 10, 10, fromBottom: true);
        Assert.True(px.Red   > 200, $"Expected magenta (high R); got {px}");
        Assert.True(px.Green < 50,  $"Expected magenta (low G); got {px}");
        Assert.True(px.Blue  > 200, $"Expected magenta (high B); got {px}");
    }

    [Fact]
    public void RenderPage_ColorStateStack_TwoColors()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithColorStateStack();
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // Left half = red, right half = blue.
        var left  = SamplePixel(bmp, 10,  10, fromBottom: true);
        var right = SamplePixel(bmp, 500, 10, fromBottom: true);

        Assert.True(left.Red  > 200 && left.Green < 50 && left.Blue < 50,
            $"Left half should be red; got {left}");
        Assert.True(right.Blue > 200 && right.Red < 50 && right.Green < 50,
            $"Right half should be blue; got {right}");
    }

    // ================================================================
    //  5. Path / stroke rendering
    // ================================================================

    [Fact]
    public void RenderPage_StrokedRect_BorderPixelsDifferFromBackground()
    {
        // A 2pt black border at x=50. At 72 DPI the rect starts at pixel 50.
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithStrokedRect();
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // Interior should still be white.
        var interior = SamplePixel(bmp, 300, 400, fromBottom: false);
        Assert.True(interior.Red > 200 && interior.Green > 200 && interior.Blue > 200,
            $"Interior should be white; got {interior}");

        // The renderer should produce a non-trivial image (not all white).
        bool foundDark = false;
        for (int x = 48; x <= 54 && !foundDark; x++)
            for (int y = 48; y <= 54 && !foundDark; y++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red < 100 && px.Green < 100 && px.Blue < 100)
                    foundDark = true;
            }
        Assert.True(foundDark, "Expected dark pixels near the rectangle border");
    }

    [Fact]
    public void RenderPage_Circle_CentreIsPaintedGreen()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithCircle();
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // Circle centre in PDF = (306, 396); at 72 DPI that's pixel (306, 396) from top.
        // PDF y=396 → bitmap row = 792 - 396 = 396 from top.
        var centre = bmp.GetPixel(306, 396);
        Assert.True(centre.Green > 150 && centre.Red < 100 && centre.Blue < 100,
            $"Circle centre should be green; got {centre}");

        // Well outside the circle (top-left corner) should be white.
        var corner = bmp.GetPixel(5, 5);
        Assert.True(corner.Red > 200 && corner.Green > 200 && corner.Blue > 200,
            $"Page corner should be white; got {corner}");
    }

    // ================================================================
    //  6. Text rendering (smoke only – pixel-exact text is font-dependent)
    // ================================================================

    [Fact]
    public void RenderPage_WithText_ProducesNonWhitePixels()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Hello World");
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        // At least some pixels should be non-white (the text glyphs).
        bool foundDark = false;
        for (int y = 0; y < bmp.Height && !foundDark; y++)
            for (int x = 0; x < bmp.Width && !foundDark; x++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red < 150 || px.Green < 150 || px.Blue < 150)
                    foundDark = true;
            }
        Assert.True(foundDark, "Rendered page with text should contain non-white pixels");
    }

    // ================================================================
    //  7. Image XObject rendering
    // ================================================================

    /// <summary>
    /// The DCTDecode image must be decoded and placed the way the CTM says, not merely survived.
    /// The fixture's JPEG is a two-axis ramp — red rising left→right, green rising top→bottom in
    /// <b>image</b> space, blue flat at 128 — so sampling the four corners of the placed box pins
    /// down the decode, the colour space and the vertical flip in one go.
    ///
    /// <para>The sibling test asserts only that <i>some</i> pixel is non-white, which a grey
    /// placeholder rectangle would also satisfy; the predecessor of this one asserted nothing at
    /// all beyond the absence of an exception.</para>
    /// </summary>
    [Fact]
    public void RenderPage_WithJpegImage_DecodesTheRampAndPlacesItWhereTheCtmSays()
    {
        // `q 100 0 0 100 100 600 cm /Im0 Do Q` → the 100×100 image covers (100,600)-(200,700),
        // 1:1 with device pixels at 72 dpi. Image row 0 is the TOP of that box.
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(100, 100);
        using var bmp = Rendering.PixelAssert.Render(pdf);

        AssertRamp(bmp, 108f, 692f, new SKColor(21,  21,  128), "top-left");
        AssertRamp(bmp, 192f, 692f, new SKColor(237, 21,  128), "top-right");
        AssertRamp(bmp, 108f, 608f, new SKColor(21,  237, 128), "bottom-left");
        AssertRamp(bmp, 192f, 608f, new SKColor(237, 237, 128), "bottom-right");

        // Nothing painted outside the box the CTM defined.
        Assert.False(Rendering.PixelAssert.IsInk(Rendering.PixelAssert.At(bmp, 90f, 650f)),
            "ink left of the image box");
        Assert.False(Rendering.PixelAssert.IsInk(Rendering.PixelAssert.At(bmp, 210f, 650f)),
            "ink right of the image box");

        static void AssertRamp(SKBitmap bmp, float x, float y, SKColor want, string where)
        {
            var got = Rendering.PixelAssert.At(bmp, x, y);
            Assert.True(Rendering.PixelAssert.Near(got, want, tolerance: 30),
                $"JPEG ramp {where} should be ≈{want}, got {got}");
        }
    }

    [Fact]
    public void RenderPage_WithJpegImage_ProducesNonWhitePixels()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(100, 100);
        using var ms  = new MemoryStream(pdf);
        byte[] png    = PdfOperations.RenderPageToPng(ms, 0, dpi: 72);
        using var bmp = DecodePng(png);

        bool found = false;
        for (int y = 0; y < bmp.Height && !found; y++)
            for (int x = 0; x < bmp.Width && !found; x++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red < 250 || px.Green < 250 || px.Blue < 250) found = true;
            }
        Assert.True(found, "Page with image should contain non-white pixels");
    }

    // ================================================================
    //  8. Multi-page rendering
    // ================================================================

    [Fact]
    public void RenderAllPages_ThreePagePdf_ReturnsThreePngs()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        string tmp  = TempFiles.NewPath();
        try
        {
            File.WriteAllBytes(tmp, pdf);
            byte[][] pages = PdfOperations.RenderAllPagesToPng(tmp, dpi: 72);
            Assert.Equal(3, pages.Length);
            foreach (var p in pages)
            {
                using var bmp = DecodePng(p);
                Assert.Equal(612, bmp.Width);
                Assert.Equal(792, bmp.Height);
            }
        }
        finally { File.Delete(tmp); }
    }

    // ================================================================
    //  9. Error handling / edge cases
    // ================================================================

    [Fact]
    public void RenderPage_NegativePageIndex_ThrowsArgumentOutOfRange()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOperations.RenderPageToPng(ms, -1));
    }

    [Fact]
    public void RenderPage_PageIndexBeyondCount_ThrowsArgumentOutOfRange()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(); // 1 page
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOperations.RenderPageToPng(ms, 5));
    }

    [Fact]
    public void RenderPage_ZeroDpi_ThrowsArgumentOutOfRange()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOperations.RenderPageToPng(ms, 0, dpi: 0));
    }

    [Fact]
    public void RenderPage_NegativeDpi_ThrowsArgumentOutOfRange()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfOperations.RenderPageToPng(ms, 0, dpi: -1));
    }

    [Fact]
    public void RenderPage_NullStream_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfOperations.RenderPageToPng((Stream)null!, 0));
    }

    [Fact]
    public void RenderPage_NullPath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfOperations.RenderPageToPng((string)null!, 0));
    }

    // ================================================================
    //  10. GraphicsState unit tests
    // ================================================================

    [Fact]
    public void GraphicsState_Clone_IsDeepCopy()
    {
        var gs = new Tetitulz.Pdf.Base.Rendering.GraphicsState();
        gs.CTM         = [2, 0, 0, 2, 10, 20];
        gs.FillColor   = SKColors.Red;
        gs.DashPattern = [5, 3];
        gs.Tm          = [1, 0, 0, 1, 100, 200];

        var clone = gs.Clone();

        // Mutate the original and verify clone's deep-copied fields are unaffected.
        gs.CTM[0]         = 99;
        gs.DashPattern[0] = 99;
        gs.FillColor      = SKColors.Blue;

        Assert.Equal(2f,  clone.CTM[0]);
        Assert.Equal(5f,  clone.DashPattern[0]);
        Assert.Equal(SKColors.Red, clone.FillColor);

        // PDF spec §9.3.1: Tm/Tlm are NOT part of the graphics state and are intentionally
        // NOT deep-copied by Clone().  They are preserved by PdfRenderContext.PopState()
        // outside the graphics-state stack mechanism.
    }

    [Fact]
    public void GraphicsState_Compose_IdentityLeavesMatrixUnchanged()
    {
        float[] id = [1, 0, 0, 1, 0, 0];
        float[] m  = [2, 1, -1, 3, 5, 7];

        float[] result = Tetitulz.Pdf.Base.Rendering.GraphicsState.Compose(m, id);
        for (int i = 0; i < 6; i++)
            Assert.Equal(m[i], result[i], precision: 4);
    }

    [Fact]
    public void GraphicsState_Compose_TranslationAccumulates()
    {
        float[] t1 = [1, 0, 0, 1, 10, 20];
        float[] t2 = [1, 0, 0, 1, 30, 40];
        // t1 * t2 should translate by (40, 60).
        float[] result = Tetitulz.Pdf.Base.Rendering.GraphicsState.Compose(t1, t2);
        Assert.Equal(40f, result[4], precision: 4);
        Assert.Equal(60f, result[5], precision: 4);
    }

    [Fact]
    public void GraphicsState_Transform_AppliesCorrectly()
    {
        float[] m = [2, 0, 0, 3, 10, 20]; // scale+translate
        var (x, y) = Tetitulz.Pdf.Base.Rendering.GraphicsState.Transform(m, 5, 4);
        // x' = 2*5 + 0*4 + 10 = 20
        // y' = 0*5 + 3*4 + 20 = 32
        Assert.Equal(20f, x, precision: 4);
        Assert.Equal(32f, y, precision: 4);
    }

    [Fact]
    public void GraphicsState_ToSKMatrix_MapsFieldsCorrectly()
    {
        float[] m = [1, 2, 3, 4, 5, 6];
        var skm = Tetitulz.Pdf.Base.Rendering.GraphicsState.ToSKMatrix(m);
        Assert.Equal(1f, skm.ScaleX);
        Assert.Equal(2f, skm.SkewY);
        Assert.Equal(3f, skm.SkewX);
        Assert.Equal(4f, skm.ScaleY);
        Assert.Equal(5f, skm.TransX);
        Assert.Equal(6f, skm.TransY);
        Assert.Equal(1f, skm.Persp2);
    }

    // ================================================================
    //  11. FontCache unit tests
    // ================================================================

    /// <summary>
    /// A subset prefix (<c>ABCDEF+</c>) must be stripped before the family is looked up, so
    /// <c>ABCDEF+Helvetica</c> resolves to whatever <c>Helvetica</c> resolves to.
    ///
    /// <para><b>The predecessors of these four tests exercised nothing.</b> They passed the font
    /// name as <c>GetTypeface</c>'s first argument, which is a <b>PDF resource name</b> (<c>/F0</c>)
    /// looked up in the resource dictionary — with <c>null</c> resources and <c>null</c> document
    /// there is nothing to look up, so every one of them could only ever return the fallback
    /// typeface. Their comments described a name mapping the call could not reach. These now drive
    /// the mapping through the real path: a font dictionary whose <c>/BaseFont</c> carries the
    /// name.</para>
    /// </summary>
    [Fact]
    public void FontCache_SubsetPrefix_IsStrippedBeforeFamilyLookup()
    {
        Assert.Equal(ResolveBaseFont("Helvetica"), ResolveBaseFont("ABCDEF+Helvetica"));
    }

    /// <summary>
    /// PostScript font names must be normalised to the installed family: the foundry suffix is
    /// stripped and the PascalCase name de-camelCased. These are the names a large share of real
    /// PDFs actually carry.
    ///
    /// <para><b>This test exposed a real defect.</b> The resolution chain accepted the result of
    /// <c>SKTypeface.FromFamilyName</c> whenever it was non-null — but on Windows that call never
    /// returns null: an unknown family yields the system default. So the first, unnormalised lookup
    /// always "succeeded" with Segoe UI and the entire PostScript-name derivation below it was
    /// unreachable. <c>ArialMT</c> rendered as Segoe UI rather than Arial.</para>
    /// </summary>
    /// <para><b>Host-gated.</b> The property is only observable where the target families exist.
    /// On a stock Ubuntu runner there is no Arial, so "ArialMT" and a nonsense name both
    /// legitimately resolve to DejaVu Sans and the assertion would fail on a correct renderer.
    /// The test skips visibly there rather than being loosened into something that cannot
    /// fail.</para>
    [TestInfrastructure.SystemFontTheory("Arial", "Times New Roman", "Courier New")]
    [InlineData("ArialMT",           "Arial")]
    [InlineData("ABCDEF+ArialMT",    "Arial")]
    [InlineData("TimesNewRomanPSMT", "Times New Roman")]
    [InlineData("CourierNewPSMT",    "Courier New")]
    public void FontCache_PostScriptName_ResolvesToTheInstalledFamily(string baseFont, string family)
    {
        Assert.Equal(family, ResolveBaseFont(baseFont));
    }

    /// <summary>
    /// The style suffix after the last '-' must survive normalisation: "TimesNewRomanPS-BoldMT"
    /// is Times New Roman **bold**, not Times New Roman regular and not the fallback.
    /// </summary>
    [TestInfrastructure.SystemFontFact("Times New Roman")]
    public void FontCache_PostScriptName_KeepsTheBoldStyle()
    {
        var (familyName, weight) = WithResolvedTypeface(
            "TimesNewRomanPS-BoldMT", tf => (tf.FamilyName, tf.FontWeight));

        Assert.Equal("Times New Roman", familyName);
        Assert.True(weight >= 600, $"expected a bold weight, got {weight}");
    }

    [Fact]
    public void FontCache_IsSystemFallbackFont_True_WhenEmbeddedFontHasUnknownFilter()
    {
        // Build a PDF with a TrueType font whose /FontFile2 stream has /Filter /XXXDecode.
        // The font bytes are undecodable so FontCache must fall back to a system font and
        // record the key in _systemFallbackKeys so IsSystemFallbackFont returns true.
        byte[] dummyFontBytes = new byte[32]; // garbage — XXXDecode passthrough won't be a valid TTF
        var pdf = BuildPdfWithUnknownFilterFont(dummyFontBytes, "/WinAnsiEncoding");
        using var ms = new System.IO.MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        var page = doc.Pages[0];
        var resources = page.Resources;

        using var cache = new Tetitulz.Pdf.Base.Rendering.FontCache();
        var tf = cache.GetTypeface("F0", resources, doc);
        Assert.NotNull(tf); // must return a system-font substitute, not null

        bool isFallback = cache.IsSystemFallbackFont("F0", resources, doc);
        Assert.True(isFallback,
            "Font with /Filter/XXXDecode on FontFile2 must be marked as a system fallback.");
    }

    /// <summary>Builds a minimal PDF with font /F0 whose FontFile2 has /Filter /XXXDecode.</summary>
    private static byte[] BuildPdfWithUnknownFilterFont(byte[] fontData, string encoding)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");

        // object 1 = catalog
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // object 2 = pages
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // object 3 = page
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50]\n" +
                  "   /Contents 4 0 R\n" +
                  "   /Resources << /Font << /F0 5 0 R >> >>\n>>\nendobj\n");

        // object 4 = content stream: draw "AB" in red
        string cs = "BT 1 0 0 rg /F0 10 Tf 10 20 Td (AB) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        // object 5 = font dict
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /BCDEEE+Arial\n" +
                  $"   /Encoding {encoding}\n" +
                  "   /FirstChar 32 /LastChar 66\n" +
                  // Widths: tiny value (1) for all entries — should NOT be used when fallback active
                  "   /Widths [1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1 1]\n" +
                  "   /FontDescriptor 6 0 R\n>>\nendobj\n");

        // object 6 = font descriptor
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /FontDescriptor /FontName /BCDEEE+Arial\n" +
                  "   /Flags 32 /Ascent 728 /Descent -210 /CapHeight 716 /ItalicAngle 0\n" +
                  "   /StemV 80 /FontBBox [-665 -210 2000 728]\n" +
                  "   /FontFile2 7 0 R\n>>\nendobj\n");

        // object 7 = font file with unknown filter (XXXDecode)
        int o7 = sb.Length;
        sb.AppendFormat("7 0 obj\n<< /Filter /XXXDecode /Length {0} /Length1 {0} >>\nstream\n",
            fontData.Length);
        int xref = sb.Length + fontData.Length + "\nendstream\nendobj\n".Length;
        // We'll finalize the bytes after embedding the font data.

        // Build raw bytes combining text + fontData
        var header = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
        var fontStream = System.Text.Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
        var combined = new byte[header.Length + fontData.Length + fontStream.Length];
        header.CopyTo(combined, 0);
        fontData.CopyTo(combined, header.Length);
        fontStream.CopyTo(combined, header.Length + fontData.Length);

        var sb2 = new System.Text.StringBuilder(System.Text.Encoding.ASCII.GetString(combined));
        int xrefOff = sb2.Length;
        sb2.Append($"xref\n0 8\n0000000000 65535 f \n");
        sb2.AppendFormat("{0:D10} 00000 n \n", o1);
        sb2.AppendFormat("{0:D10} 00000 n \n", o2);
        sb2.AppendFormat("{0:D10} 00000 n \n", o3);
        sb2.AppendFormat("{0:D10} 00000 n \n", o4);
        sb2.AppendFormat("{0:D10} 00000 n \n", o5);
        sb2.AppendFormat("{0:D10} 00000 n \n", o6);
        sb2.AppendFormat("{0:D10} 00000 n \n", o7);
        sb2.Append($"trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{xrefOff}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb2.ToString());
    }

    /// <summary>
    /// FlateDecode-recovery fallback: when a font stream has an unknown filter but its
    /// bytes are valid FlateDecode-compressed font data, FontCache must recover the
    /// typeface instead of falling back to the system font.
    /// </summary>
    [Fact]
    public void FontCache_UnknownFilterFont_FlatDecodeRecovery_LoadsTypeface()
    {
        // Compress the default SkiaSharp font bytes with zlib (= FlateDecode).
        byte[] compressedFontBytes;
        using (var defaultTf = SKTypeface.Default)
        using (var skStream = defaultTf.OpenStream(out _))
        {
            var fontBytes = new byte[skStream.Length];
            skStream.Read(fontBytes, fontBytes.Length);

            using var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x9C); // zlib default-compression header
            using (var deflate = new System.IO.Compression.DeflateStream(ms,
                System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                deflate.Write(fontBytes, 0, fontBytes.Length);

            // Adler-32 checksum (big-endian) for valid zlib framing.
            const int Mod = 65521;
            int a = 1, b = 0;
            foreach (byte byt in fontBytes) { a = (a + byt) % Mod; b = (b + a) % Mod; }
            int adler = (b << 16) | a;
            ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16));
            ms.WriteByte((byte)(adler >>  8)); ms.WriteByte((byte) adler);

            compressedFontBytes = ms.ToArray();
        }

        // Build a PDF where the font's /FontFile2 has /Filter /RenamedDecode (unknown)
        // but the raw bytes are the compressed font.  Use a binary-safe builder so
        // the font bytes are not corrupted by ASCII encoding.
        var pdf = BuildPdfWithBinaryFontStream(compressedFontBytes);
        using var docMs = new MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(docMs);

        var page = doc.Pages[0];
        var resources = page.Resources;

        using var cache = new Tetitulz.Pdf.Base.Rendering.FontCache();
        var tf = cache.GetTypeface("F0", resources, doc);
        Assert.NotNull(tf);

        // The embedded font was recovered via FlateDecode fallback — must NOT be
        // a system-fallback key.
        bool isFallback = cache.IsSystemFallbackFont("F0", resources, doc);
        Assert.False(isFallback,
            "Font whose unknown-filter bytes are valid FlateDecode data must be loaded " +
            "from the embedded bytes, not treated as a system-font substitute.");
    }

    /// <summary>
    /// Builds a minimal PDF where /F0 has a /FontFile2 with /Filter /RenamedDecode
    /// (unknown) containing <paramref name="fontStreamBytes"/> verbatim.
    /// Uses raw byte concatenation so binary font data is not corrupted by string encoding.
    /// </summary>
    private static byte[] BuildPdfWithBinaryFontStream(byte[] fontStreamBytes)
    {
        // We use a unique base font name that will NOT be found as a system font so
        // we can reliably test whether the embedded font was loaded.
        const string baseFontName = "/ZZTEST+UnlikelyFontNameXYZ";

        byte[] Txt(string s) => System.Text.Encoding.ASCII.GetBytes(s);
        var parts = new System.Collections.Generic.List<byte[]>();
        var offsets = new int[9]; // object offsets: 0=unused, 1..7, xref

        parts.Add(Txt("%PDF-1.4\n"));

        offsets[1] = parts.Sum(p => p.Length);
        parts.Add(Txt("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"));

        offsets[2] = parts.Sum(p => p.Length);
        parts.Add(Txt("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"));

        offsets[3] = parts.Sum(p => p.Length);
        parts.Add(Txt("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50]\n" +
                      "   /Contents 4 0 R /Resources << /Font << /F0 5 0 R >> >>\n>>\nendobj\n"));

        string cs = "BT /F0 10 Tf 10 20 Td (AB) Tj ET";
        offsets[4] = parts.Sum(p => p.Length);
        parts.Add(Txt($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n"));

        offsets[5] = parts.Sum(p => p.Length);
        parts.Add(Txt($"5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont {baseFontName}\n" +
                      "   /Encoding /WinAnsiEncoding /FirstChar 65 /LastChar 66\n" +
                      "   /Widths [600 600] /FontDescriptor 6 0 R\n>>\nendobj\n"));

        offsets[6] = parts.Sum(p => p.Length);
        parts.Add(Txt($"6 0 obj\n<< /Type /FontDescriptor /FontName {baseFontName}\n" +
                      "   /Flags 32 /Ascent 728 /Descent -210 /CapHeight 716\n" +
                      "   /ItalicAngle 0 /StemV 80 /FontBBox [0 0 1000 1000]\n" +
                      "   /FontFile2 7 0 R\n>>\nendobj\n"));

        // Object 7: font stream with /Filter /RenamedDecode (unknown) but valid FlateDecode data.
        offsets[7] = parts.Sum(p => p.Length);
        parts.Add(Txt($"7 0 obj\n<< /Filter /RenamedDecode /Length {fontStreamBytes.Length}" +
                      $" /Length1 {fontStreamBytes.Length} >>\nstream\n"));
        parts.Add(fontStreamBytes);                        // binary — must not go through string
        parts.Add(Txt("\nendstream\nendobj\n"));

        int xrefOff = parts.Sum(p => p.Length);
        var xref = new System.Text.StringBuilder();
        xref.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++)
            xref.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        xref.Append($"trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n{xrefOff}\n%%EOF\n");
        parts.Add(Txt(xref.ToString()));

        int total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(result, pos); pos += p.Length; }
        return result;
    }

    [Fact]
    public void FontCache_IsSystemFallbackFont_True_WhenFontDictUnresolvable()
    {
        // Simulates the ObjStm-with-unknown-filter case: the font resource is an indirect
        // reference to an object that can't be decoded (object 5 is declared in the xref
        // but its offset points to garbage, making LookupFontDict return null).
        // LoadTypeface must still mark this key as a system fallback so that
        // IsSystemFallbackFont returns true and SkiaSharp widths are used for spacing.
        byte[] pdf = BuildPdfWithMissingFontDict();
        using var ms = new System.IO.MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        var page = doc.Pages[0];
        var resources = page.Resources;

        using var cache = new Tetitulz.Pdf.Base.Rendering.FontCache();
        var tf = cache.GetTypeface("F0", resources, doc);
        Assert.NotNull(tf); // must return a system font, not null

        bool isFallback = cache.IsSystemFallbackFont("F0", resources, doc);
        Assert.True(isFallback,
            "Font whose dict is unresolvable (e.g. in an unreadable ObjStm) must be " +
            "marked as a system fallback so SkiaSharp widths are used for spacing.");
    }

    /// <summary>
    /// PDF where the page's /Font /F0 entry points to object 5, but object 5 is not
    /// defined (simulating an ObjStm whose /Filter is unknown, making the contained
    /// objects unresolvable).
    /// </summary>
    private static byte[] BuildPdfWithMissingFontDict()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        // Page references /F0 → object 5, which is NOT defined in the xref.
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50]\n" +
                  "   /Contents 4 0 R\n" +
                  "   /Resources << /Font << /F0 5 0 R >> >>\n>>\nendobj\n");
        string cs = "BT /F0 10 Tf 10 20 Td (AB) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xrefOff = sb.Length;
        // xref: only objects 0-4 defined; object 5 is absent (free or missing).
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xrefOff}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void FontCache_GetWMode_ReturnsOne_ForType0WithIdentityVEncoding()
    {
        // Type0 fonts with /Encoding /Identity-V declare vertical writing mode via the
        // encoding name on the Type0 dict itself.  ComputeWMode must check that dict, not
        // just the descendant CIDFont, otherwise the 90° glyph-scale rotation is skipped
        // and vertical CJK text renders horizontally.
        byte[] pdf = BuildPdfWithIdentityVFont();
        using var ms = new System.IO.MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        var resources = doc.Pages[0].Resources;
        using var cache = new Tetitulz.Pdf.Base.Rendering.FontCache();

        int wMode = cache.GetWMode("F0", resources, doc);
        Assert.Equal(1, wMode);
    }

    /// <summary>Minimal PDF with a Type0 font using /Encoding /Identity-V.</summary>
    private static byte[] BuildPdfWithIdentityVFont()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300]\n" +
                  "   /Contents 4 0 R\n" +
                  "   /Resources << /Font << /F0 5 0 R >> >>\n>>\nendobj\n");
        string cs = "BT /F0 1 Tf ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        // object 5 = Type0 font with /Encoding /Identity-V
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /KozMinPro-Regular-Identity-V\n" +
                  "   /Encoding /Identity-V /DescendantFonts [6 0 R] >>\nendobj\n");
        // object 6 = CIDFont (descendant) — no /WMode or /Encoding here (typical real-world case)
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /KozMinPro-Regular\n" +
                  "   /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >>\n" +
                  "   /DW 1000 >>\nendobj\n");
        int xrefOff = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xrefOff}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Two different unresolvable cases, two deliberately different fallbacks — and neither may be
    /// mistaken for a successful lookup.
    ///
    /// <list type="bullet">
    ///   <item>A <c>null</c> resource name never reaches name resolution, so it returns the
    ///     library's own <c>FallbackTypeface()</c>.</item>
    ///   <item>A resource that names a family nobody has runs the full resolution chain, fails
    ///     every match, and ends on the <i>platform</i> substitute.</item>
    /// </list>
    ///
    /// <para>The load-bearing assertion is the last one: an unknown family must not land on the
    /// same family as a resolvable PostScript name. Without it the tests above would pass no matter
    /// what the resolver did — which is precisely how the Segoe-UI-for-everything defect survived.</para>
    /// </summary>
    /// <para><b>Host-gated</b> for the same reason as the theory above: where Arial and Times New
    /// Roman are absent, a resolvable PostScript name and an unresolvable one *correctly* share
    /// the one fallback face, so the inequality cannot hold.</para>
    [TestInfrastructure.SystemFontFact("Arial", "Times New Roman")]
    public void FontCache_UnresolvableNames_FallBackWithoutImpersonatingAResolvedFont()
    {
        string viaNullFamily;
        using (var cache = new Tetitulz.Pdf.Base.Rendering.FontCache())
        {
            var viaNull = cache.GetTypeface(null, null, null);
            Assert.NotNull(viaNull);
            // Read inside the using: the cache owns and disposes this typeface.
            viaNullFamily = viaNull.FamilyName;
        }
        Assert.False(string.IsNullOrEmpty(viaNullFamily));

        string viaUnknown = ResolveBaseFont("ZZTotallyUnknownFontFamily");
        Assert.False(string.IsNullOrEmpty(viaUnknown));

        // The control for every PostScript-name assertion above.
        Assert.NotEqual(viaUnknown, ResolveBaseFont("ArialMT"));
        Assert.NotEqual(viaUnknown, ResolveBaseFont("TimesNewRomanPSMT"));
    }

    [Fact]
    public void FontCache_DecodeText_NullFont_ReturnsAscii()
    {
        using var cache = new Tetitulz.Pdf.Base.Rendering.FontCache();
        string text = cache.DecodeText(null, new byte[] { 72, 101, 108, 108, 111 }, null, null);
        Assert.Equal("Hello", text);
    }

    [Fact]
    public void FontCache_DecodeText_NullFont_DecodesTheHighHalfAsWindows1252()
    {
        // 0x80 is the euro sign in CP-1252 and a C1 control in ISO-8859-1: the byte that tells the
        // two decoders apart. 0xE9 is é in both — the control.
        using var cache = new Tetitulz.Pdf.Base.Rendering.FontCache();
        string text = cache.DecodeText(null, new byte[] { 0x80, 0xE9 }, null, null);
        Assert.Equal("€é", text);
    }

    // ================================================================
    //  11. Visual showcase — four-quadrant colour render
    // ================================================================
    // Exercises: coloured fills, text at known positions, text inside
    // q/Q blocks (verifies the §9.3.1 Tm-preservation fix), line drawing.
    // When TETITULZ_PDF_DUMP_RENDERS is set, the rendered PNG is also written to
    // %TEMP%\tetitulz_renders\render_showcase.png for viewing.
    // ================================================================

    [Fact]
    public void Render_FourQuadrantShowcase_PixelsMatchExpectedColors()
    {
        byte[] pdf = BuildShowcasePdf();
        byte[] png = Render72(pdf);

        // Persist for visual inspection only when TETITULZ_PDF_DUMP_RENDERS is set.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TETITULZ_PDF_DUMP_RENDERS")))
        {
            string dumpDir = Path.Combine(Path.GetTempPath(), "tetitulz_renders");
            Directory.CreateDirectory(dumpDir);
            File.WriteAllBytes(Path.Combine(dumpDir, "render_showcase.png"), png);
        }

        using var bmp = DecodePng(png);

        // ---- quadrant centre pixels (at 72 DPI: 1 px per PDF point) ----
        // Top-left (crimson):   PDF (153, 594)  → screen (153, 792-594) = (153, 198)
        AssertColorClose(bmp, 153, 198, r: 204, g: 26,  b: 26,  tolerance: 30);
        // Top-right (navy):     PDF (459, 594)  → screen (459, 198)
        AssertColorClose(bmp, 459, 198, r: 26,  g: 26,  b: 204, tolerance: 30);
        // Bottom-left (green):  PDF (153, 198)  → screen (153, 594)
        AssertColorClose(bmp, 153, 594, r: 26,  g: 153, b: 26,  tolerance: 30);
        // Bottom-right (orange):PDF (459, 198)  → screen (459, 594)
        AssertColorClose(bmp, 459, 594, r: 230, g: 128, b: 0,   tolerance: 30);
        // Centre white band:    PDF (250, 410)  → screen (250, 382) — clear of both divider lines
        AssertColorClose(bmp, 250, 382, r: 255, g: 255, b: 255, tolerance: 10);
    }

    /// <summary>
    /// Builds a 612×792 PDF with four coloured quadrants, a white horizontal
    /// band in the centre, text, and a text block inside a q/Q pair.
    /// </summary>
    private static byte[] BuildShowcasePdf()
    {
        // Content stream — all coordinates in PDF user space (origin = bottom-left).
        string cs =
            // ---- four background quadrants ----
            // Top-left: crimson   (y 396..792, x 0..306)
            "0.8 0.1 0.1 rg  0 396 306 396 re f " +
            // Top-right: navy
            "0.1 0.1 0.8 rg  306 396 306 396 re f " +
            // Bottom-left: green
            "0.1 0.6 0.1 rg  0 0 306 396 re f " +
            // Bottom-right: orange
            "0.9 0.5 0.0 rg  306 0 306 396 re f " +

            // ---- white centre band (y 366..426) ----
            "1 1 1 rg  0 366 612 60 re f " +

            // ---- thin black divider lines ----
            "0 0 0 RG  1 w " +
            "306 0 m 306 792 l S " +   // vertical centre
            "0 396 m 612 396 l S " +   // horizontal centre

            // ---- title text in centre band ----
            "0 0 0 rg " +
            "BT /F1 18 Tf 148 385 Td (Tetitulz.Pdf Renderer Test) Tj ET " +

            // ---- q/Q Tm-preservation test ----
            // Start a text block, write "A", enter q/Q, write "B" inside
            // (changes colour), exit Q, write "C" — all three should appear
            // on the same baseline because Tm is NOT restored by Q.
            "BT /F1 14 Tf 50 740 Td " +
            "(A) Tj " +          // char A
            "q " +               // save graphics state
            "1 0 0 rg " +        // switch to red inside q
            "20 0 Td " +
            "(B) Tj " +          // char B (red, 20 pts right of A)
            "Q " +               // restore graphics state — Tm must NOT revert
            "0 0 0 rg " +
            "20 0 Td " +
            "(C) Tj " +          // char C must be 40 pts right of A
            "ET " +

            // ---- corner labels ----
            "1 1 1 rg " +
            "BT /F1 11 Tf " +
            "20 760 Td (top-left) Tj " +
            "340 760 Td (top-right) Tj " +
            "20 20 Td (bottom-left) Tj " +
            "340 20 Td (bottom-right) Tj " +
            "ET";

        return MinimalPdfBuilder.CreateMinimalPdf(contentStream: cs);
    }

    /// <summary>Asserts that a pixel's RGB channels are within <paramref name="tolerance"/>.</summary>
    private static void AssertColorClose(SKBitmap bmp, int x, int y,
        int r, int g, int b, int tolerance)
    {
        int sx = Math.Clamp(x, 0, bmp.Width  - 1);
        int sy = Math.Clamp(y, 0, bmp.Height - 1);
        SKColor px = bmp.GetPixel(sx, sy);
        Assert.True(Math.Abs(px.Red   - r) <= tolerance,
            $"Pixel ({x},{y}) Red={px.Red} expected≈{r} ±{tolerance}");
        Assert.True(Math.Abs(px.Green - g) <= tolerance,
            $"Pixel ({x},{y}) Green={px.Green} expected≈{g} ±{tolerance}");
        Assert.True(Math.Abs(px.Blue  - b) <= tolerance,
            $"Pixel ({x},{y}) Blue={px.Blue} expected≈{b} ±{tolerance}");
    }

    // ------------------------------------------------------------------
    //  Font-name resolution helpers
    //
    //  GetTypeface's first argument is a PDF *resource* name, resolved through the page's
    //  resource dictionary to a font dict whose /BaseFont carries the actual font name. Passing
    //  a family name there with null resources reaches no name-resolution code at all — which is
    //  why the tests these replaced could not fail.
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="baseFont"/> through a real <c>FontCache</c> and projects whatever
    /// the caller needs out of the typeface <b>while the cache is still alive</b>.
    ///
    /// <para><b>Why a callback rather than returning the typeface.</b> <c>FontCache.Dispose</c>
    /// disposes every typeface it handed out, so a helper that returned one out of its own
    /// <c>using</c> scope handed back a freed native object; the caller then read
    /// <c>FamilyName</c> from it and disposed it a second time. That survived on Windows only by
    /// luck: a <c>SKTypeface.FromFamilyName</c> result is also referenced by Skia's font manager,
    /// so releasing our reference did not actually free it. A typeface loaded from a file with
    /// <c>SKTypeface.FromData</c> has no such second owner — disposing it frees it for real, and
    /// the next read segfaults. It crashed the whole Linux test host (190 tests never ran) the
    /// first time the file-based fallback in <c>FontCache</c> supplied one of these.</para>
    /// </summary>
    private static T WithResolvedTypeface<T>(string baseFont, System.Func<SKTypeface, T> read)
    {
        using var ms = new System.IO.MemoryStream(BuildPdfWithBaseFont(baseFont));
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        using var cache = new Tetitulz.Pdf.Base.Rendering.FontCache();

        var tf = cache.GetTypeface("F0", doc.Pages[0].Resources, doc);
        Assert.NotNull(tf);
        return read(tf);
    }

    private static string ResolveBaseFont(string baseFont)
        => WithResolvedTypeface(baseFont, tf => tf.FamilyName);

    /// <summary>A one-page PDF whose only font resource /F0 has the given /BaseFont.</summary>
    private static byte[] BuildPdfWithBaseFont(string baseFont)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]" +
                  " /Contents 4 0 R /Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n");
        const string cs = "BT /F0 12 Tf 10 100 Td (Hi) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /{0}" +
                        " /Encoding /WinAnsiEncoding >>\nendobj\n", baseFont);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
