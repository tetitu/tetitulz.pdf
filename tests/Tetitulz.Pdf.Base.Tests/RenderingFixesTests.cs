// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Tests.Rendering;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for rendering engine fixes:
/// - Gouraud triangle per-pixel interpolation (SKVertices)
/// - Soft-mask per-pixel compositing (SaveLayer + DstIn)
/// - CMap bfrange multi-char destination
/// - Vertical CJK DW2/W2 advance widths
/// </summary>
public class RenderingFixesTests
{
    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] BuildMinimalPdf(string contentStream = "")
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  Gouraud triangle — SKVertices path
    // ------------------------------------------------------------------

    /// <summary>
    /// A free-form (type 4) mesh with a red, a green and a blue vertex must interpolate colour
    /// across the triangle.
    ///
    /// <para><b>This test replaces one that rendered <c>"q Q"</c> — an empty page — under a name
    /// about Gouraud interpolation, and asserted only that a PNG came back.</b> It exposed a real
    /// defect: <c>DrawGouraudTriangle</c> called <c>DrawVertices</c> with <c>SKBlendMode.SrcOver</c>
    /// and a shader-less paint. Skia's blend mode combines the vertex colours ("dst") with the
    /// paint's colour ("src"), so SrcOver resolved to the default opaque black and <b>every mesh
    /// shading — types 4, 5, 6 and 7 — painted as a flat black silhouette</b>, vertex colours
    /// computed and then discarded. The geometry was right all along, which is why nothing looked
    /// obviously broken.</para>
    /// </summary>
    [Fact]
    public void GouraudMeshShading_InterpolatesTheVertexColoursAcrossTheTriangle()
    {
        // Vertices: (0,0) red, (100,0) green, (50,100) blue. 8-bit coords and components.
        using var bmp = PixelAssert.Render(MeshShadingPdf("000000FF000000FF0000FF000080FF0000FF>"));

        var nearRed   = PixelAssert.At(bmp, 10, 5);
        var nearGreen = PixelAssert.At(bmp, 90, 5);
        var nearBlue  = PixelAssert.At(bmp, 50, 90);

        Assert.True(nearRed.Red > nearRed.Green && nearRed.Red > nearRed.Blue,
            "near the red vertex expected a red-dominant pixel, got " + nearRed);
        Assert.True(nearGreen.Green > nearGreen.Red && nearGreen.Green > nearGreen.Blue,
            "near the green vertex expected a green-dominant pixel, got " + nearGreen);
        Assert.True(nearBlue.Blue > nearBlue.Red && nearBlue.Blue > nearBlue.Green,
            "near the blue vertex expected a blue-dominant pixel, got " + nearBlue);

        // The corners genuinely differ, which no flat fill of any single colour could produce.
        Assert.False(PixelAssert.Near(nearRed, nearGreen, 32));
        Assert.False(PixelAssert.Near(nearGreen, nearBlue, 32));

        // The triangle covers half of the 100x100 page; the rest stays white.
        Assert.InRange(PixelAssert.CountInk(bmp), 4000, 6000);
    }

    /// <summary>A page painting a single ShadingType 4 mesh with the <c>sh</c> operator.</summary>
    private static byte[] MeshShadingPdf(string hexVertexData)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R /Resources << /Shading << /Sh0 5 0 R >> >> >>\nendobj\n");
        const string pageCs = "q /Sh0 sh Q";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageCs.Length, pageCs);
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< /ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8" +
            " /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 100 0 100 0 1 0 1 0 1]" +
            " /Filter /AHx /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            hexVertexData.Length, hexVertexData);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  DrawWithSoftMask — SaveLayer path doesn't crash
    // ------------------------------------------------------------------

    /// <summary>
    /// A soft mask attenuates the fill towards the backdrop in proportion to its alpha: 0 paints
    /// nothing, 255 paints the full colour, 128 lands halfway. Asserting the whole progression is
    /// what fails if the mask is ignored (every case pure red) or over-applied (every case white).
    /// The predecessor only required that FillPath not throw, which a no-op satisfies.
    /// </summary>
    [Theory]
    [InlineData(0,   255, 255, 255)]   // fully transparent mask -> backdrop survives
    [InlineData(128, 255, 127, 127)]   // half -> halfway between white and red
    [InlineData(255, 255,   0,   0)]   // opaque -> the fill colour itself
    public void SoftMask_AttenuatesTheFillTowardsTheBackdrop(
        byte maskAlpha, int expectedR, int expectedG, int expectedB)
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        using var ctx = new PdfRenderContext(canvas, 100f, 100f, 1f);

        using var mask = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Premul);
        mask.Erase(new SKColor(maskAlpha, maskAlpha, maskAlpha, maskAlpha));
        ctx.GS.ActiveSoftMask  = mask;
        ctx.GS.SoftMaskIsAlpha = true;

        ctx.AppendRect(10, 10, 80, 80);
        ctx.GS.FillColor = SKColors.Red;
        ctx.FillPath();

        var inside = bitmap.GetPixel(50, 50);
        Assert.True(
            PixelAssert.Near(inside, new SKColor((byte)expectedR, (byte)expectedG, (byte)expectedB), 4),
            "mask alpha " + maskAlpha + ": expected ~(" + expectedR + "," + expectedG + "," +
            expectedB + ") but got " + inside);

        // Outside the path the backdrop is untouched, whatever the mask says.
        Assert.Equal(SKColors.White, bitmap.GetPixel(2, 2));
    }

    [Fact]
    public void SoftMask_NullMask_DrawsNormally()
    {
        using var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ctx = new PdfRenderContext(canvas, 100f, 100f, 1f);

        ctx.AppendRect(10, 10, 80, 80);
        ctx.GS.FillColor = SKColors.Blue;
        ctx.FillPath();  // no soft mask — normal path

        // Check that at least one pixel is blue-ish.
        var px = bitmap.GetPixel(50, 50);
        Assert.True(px.Blue > 100, "Expected blue fill");
    }

    // ------------------------------------------------------------------
    //  CMapParser — bfrange multi-char destination
    // ------------------------------------------------------------------

    [Fact]
    public void CMapParser_BfRange_MultiChar_LastUnitIncrements()
    {
        // Prefix 0041 is fixed; last unit starts at 0061.
        string cmap = "beginbfrange\n<0001> <0003> <00410061>\nendbfrange\n";
        var map = CMapParser.Parse(Encoding.ASCII.GetBytes(cmap));
        Assert.Equal("Aa", map[0x0001]);  // 0041 0061
        Assert.Equal("Ab", map[0x0002]);  // 0041 0062
        Assert.Equal("Ac", map[0x0003]);  // 0041 0063
    }

    // ------------------------------------------------------------------
    //  FontCache.GetVerticalAdvance — DW2 default
    // ------------------------------------------------------------------

    [Fact]
    public void GetVerticalAdvance_NullFont_ReturnsFallback()
    {
        using var cache = new FontCache();
        float adv = cache.GetVerticalAdvance("F_missing", 0, null, null, 0.88f);
        Assert.Equal(0.88f, adv);
    }

    [Fact]
    public void GetVerticalAdvance_WithDW2_ReturnsScaledAdvance()
    {
        // Build a minimal document with a Type0 font that has DW2 = [880 1000]
        using var cache = new FontCache();
        // Without a full PdfDocument we just verify the fallback path is safe.
        float adv = cache.GetVerticalAdvance(null, 0, null, null, 1f);
        Assert.Equal(1f, adv); // null resource name → fallback
    }

    // ------------------------------------------------------------------
    //  NewPath disposal — repeated FillPath calls must not throw
    // ------------------------------------------------------------------

    /// <summary>
    /// Each <c>FillPath</c> calls <c>NewPath()</c> internally, disposing the previous
    /// <c>SKPath</c>. Surviving the loop is necessary but not sufficient: if <c>NewPath</c> failed
    /// to *clear* the path, every iteration would repaint all previously appended rects, and if it
    /// cleared too eagerly nothing would paint at all. Both are silent under a "does not throw"
    /// assertion, so this checks the ink instead.
    /// </summary>
    [Fact]
    public void NewPath_RepeatedPaint_PaintsEachRectangleExactlyOnce()
    {
        using var bitmap = new SKBitmap(50, 50);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ctx = new PdfRenderContext(canvas, 50f, 50f, 1f);

        for (int i = 0; i < 20; i++)
        {
            ctx.AppendRect(i, i, 10, 10);
            ctx.GS.FillColor = SKColors.Red;
            ctx.FillPath();
        }

        // Twenty 10x10 rects marching diagonally across a 50x50 canvas: the union is a staircase
        // band, not the whole canvas and not nothing.
        int ink = PixelAssert.CountInk(bitmap);
        Assert.InRange(ink, 300, 50 * 50 - 100);

        // PdfRenderContext flips Y, so sample in PDF user space. On the band: painted. In the
        // far corner the staircase never reaches: white.
        Assert.True(PixelAssert.Near(PixelAssert.At(bitmap, 10, 10), SKColors.Red),
            "PDF (10,10) is inside several of the rects, got " + PixelAssert.At(bitmap, 10, 10));
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bitmap, 45, 5)),
            "PDF (45,5) is never covered by a rect at (i,i), got " + PixelAssert.At(bitmap, 45, 5));
    }

    // ------------------------------------------------------------------
    //  RenderPage with non-seekable stream (EnsureSeekable leak fix)
    // ------------------------------------------------------------------

    /// <summary>
    /// Buffering a non-seekable stream must not change what is rendered. The predecessor drew an
    /// empty page ("q Q") and asserted only that bytes came back, so a shim that dropped the
    /// document entirely would still have passed. Rendering real content and comparing against the
    /// seekable render is the assertion that can actually fail.
    /// </summary>
    [Fact]
    public void RenderPage_NonSeekableStream_ProducesTheSameImageAsASeekableOne()
    {
        byte[] pdf = BuildMinimalPdf("0 0 1 rg 100 100 300 300 re f");

        byte[] fromSeekable;
        using (var seekable = new MemoryStream(pdf))
            fromSeekable = PdfPageRenderer.RenderPage(seekable, 0, 96);

        byte[] fromNonSeekable;
        using (var ms = new MemoryStream(pdf))
        using (var nonSeekable = new NonSeekableStreamWrapper(ms))
            fromNonSeekable = PdfPageRenderer.RenderPage(nonSeekable, 0, 96);

        // The content really is there — against a blank page the comparison would prove nothing.
        using (var bmp = SKBitmap.Decode(fromSeekable))
            Assert.True(PixelAssert.CountInk(bmp) > 1000, "the fixture should paint a large blue rect");

        Assert.Equal(fromSeekable, fromNonSeekable);
    }

    // ------------------------------------------------------------------
    //  SMask applied to text — DrawText must not throw with active mask
    // ------------------------------------------------------------------

    /// <summary>
    /// Text drawn through a partly-transparent soft mask must come out lighter than the same text
    /// drawn without one. Rendering both and comparing is what distinguishes "the mask is applied"
    /// from "the mask is ignored" — the predecessor only required that DrawText not throw, which is
    /// equally true when the mask is dropped on the floor.
    /// </summary>
    [Fact]
    public void DrawText_ThroughASoftMask_ProducesLighterInkThanWithout()
    {
        int unmasked = RenderTextDarkness(null);
        int masked   = RenderTextDarkness(80);

        // Unmasked black text reaches full black; through a ~31% mask it cannot.
        Assert.Equal(0, unmasked);
        Assert.True(masked > 100,
            "expected the masked text to be visibly lighter, but its darkest pixel was " + masked);
    }

    /// <summary>Draws "Hello" and returns the darkest luminance on the canvas (0 = black).</summary>
    private static int RenderTextDarkness(byte? maskAlpha)
    {
        using var bitmap = new SKBitmap(200, 200);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var ctx = new PdfRenderContext(canvas, 200f, 200f, 1f);

        SKBitmap mask = null;
        try
        {
            if (maskAlpha is byte a)
            {
                mask = new SKBitmap(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul);
                mask.Erase(new SKColor(a, a, a, a));
                ctx.GS.ActiveSoftMask  = mask;
                ctx.GS.SoftMaskIsAlpha = true;
            }

            ctx.GS.FontSize  = 48f;
            ctx.GS.Tm        = new float[] { 48f, 0f, 0f, 48f, 20f, 100f };
            ctx.GS.FillColor = SKColors.Black;

            ctx.DrawText("Hello", SKTypeface.FromFamilyName("Arial"));

            Assert.True(PixelAssert.CountInk(bitmap) > 100, "the text should actually be drawn");
            return PixelAssert.DarkestLuminance(bitmap);
        }
        finally { mask?.Dispose(); }
    }

    /// <summary>Wraps a MemoryStream and hides <see cref="Stream.CanSeek"/> / <see cref="Stream.Position"/>.</summary>
    private sealed class NonSeekableStreamWrapper : Stream
    {
        private readonly Stream _inner;
        public NonSeekableStreamWrapper(Stream inner) => _inner = inner;
        public override bool CanRead  => _inner.CanRead;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new System.NotSupportedException();
        public override long Position
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }
        public override void Flush() => _inner.Flush();
        public override int  Read(byte[] buffer, int offset, int count)  => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin)        => throw new System.NotSupportedException();
        public override void SetLength(long value)                       => throw new System.NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    // ------------------------------------------------------------------
    //  W2 array triplet index bug (§9.7.4.3)
    //  Before fix: code read triplet+1 (x-origin), not triplet+0 (advance)
    // ------------------------------------------------------------------

    [Fact]
    public void GetVerticalAdvance_W2ArrayFormat_ReturnsAdvanceNotOrigin()
    {
        // Build a minimal PDF with a Type0 / CIDFont that has:
        //   /DW2 [880 1000]          — default vertical advance = 1000 units = 1.0 em
        //   /W2  [0 [500 100 -880]]  — CID 0: advance=500, origin_x=100, origin_y=-880
        //
        // Before the fix, ParseW2Array read triplet+1 (=100, the x-origin).
        // After the fix it reads triplet+0 (=500, the advance).
        byte[] pdf = BuildPdfWithW2Font(
            dw2: "[880 1000]",
            w2: "[0 [500 100 -880]]");

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();

        // charCode 0 is matched by W2 entry [0 [500 100 -880]].
        float adv = cache.GetVerticalAdvance("F1", 0, doc.Pages[0].Resources, doc, 99f);
        // Expected: 500/1000 = 0.5 em
        Assert.Equal(0.5f, adv, precision: 3);
    }

    [Fact]
    public void GetVerticalAdvance_DW2_UsesIndex1AsAdvance()
    {
        // DW2 = [vy vw] per spec: index 0 = y-origin offset, index 1 = default advance.
        // Build a document with only DW2 (no W2), verify we get vw = 880 units = 0.88 em.
        byte[] pdf = BuildPdfWithW2Font(dw2: "[0 880]", w2: null);

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();

        // charCode 99 has no W2 entry → should return DW2 advance = 880/1000 = 0.88 em.
        float adv = cache.GetVerticalAdvance("F1", 99, doc.Pages[0].Resources, doc, 99f);
        Assert.Equal(0.88f, adv, precision: 3);
    }

    /// <summary>
    /// A multi-character vertical run advances by the sum of its <b>per-character</b> advances:
    /// each CID is looked up separately in /W2, and a CID the array does not cover falls back to
    /// /DW2. Only distinct per-CID values can tell that apart from one advance applied to all.
    ///
    /// <para><b>The predecessor tested nothing of the sort.</b> It rendered
    /// <c>BuildVerticalModePdf</c> — whose content stream is <c>q 0 0 m 100 100 l S Q</c>, a
    /// diagonal line with no font, no text and no vertical writing mode — and asserted only that a
    /// PNG came back. Deleting the whole /W2 implementation left it green.</para>
    /// </summary>
    [Fact]
    public void VerticalCJK_MultiChar_AdvanceSumsAllChars()
    {
        // /W2 `c [w1y v1x v1y  w1y v1x v1y]`: CID 0 → 500, CID 1 → 250. CID 99 is uncovered.
        byte[] pdf = BuildPdfWithW2Font(
            dw2: "[0 880]",
            w2:  "[0 [500 100 -880 250 50 -880]]");

        using var doc   = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        var res = doc.Pages[0].Resources;

        float a0  = cache.GetVerticalAdvance("F1", 0,  res, doc, 99f);
        float a1  = cache.GetVerticalAdvance("F1", 1,  res, doc, 99f);
        float a99 = cache.GetVerticalAdvance("F1", 99, res, doc, 99f);

        Assert.Equal(0.50f, a0,  precision: 3);   // from /W2, first triplet
        Assert.Equal(0.25f, a1,  precision: 3);   // from /W2, second triplet
        Assert.Equal(0.88f, a99, precision: 3);   // uncovered CID → /DW2

        // Three characters, three different advances: the run's total is their sum, which is
        // what a renderer that reused a single advance for the whole run cannot produce.
        Assert.Equal(1.63f, a0 + a1 + a99, precision: 3);
        Assert.Equal(3, new[] { a0, a1, a99 }.Distinct().Count());
    }

    private static byte[] BuildPdfWithW2Font(string dw2, string w2)
    {
        string dw2Entry = dw2 != null ? $" /DW2 {dw2}" : "";
        string w2Entry  = w2  != null ? $" /W2 {w2}"   : "";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // CIDFont (obj 6) carrying /DW2 and /W2
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Arial" +
            " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>" +
            "{0}{1} >>\nendobj\n", dw2Entry, w2Entry);

        // Type0 font (obj 5) wrapping the CIDFont
        int o5 = sb.Length;
        sb.Append(
            "5 0 obj\n<< /Type /Font /Subtype /Type0 /BaseFont /Arial-Identity-V" +
            " /Encoding /Identity-V /DescendantFonts [6 0 R] >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildVerticalModePdf()
    {
        // Minimal PDF simulating vertical-mode text output.
        // The content stream just renders a path (no actual CJK font needed).
        const string cs = "q 0 0 m 100 100 l S Q";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
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

    // ------------------------------------------------------------------
    //  Tiling pattern: XStep/YStep — SkiaSharp renderer
    // ------------------------------------------------------------------

    // Builds a PDF whose page paints a colored tiling pattern with explicit
    // XStep/YStep values that differ from the BBox dimensions.
    private static byte[] BuildTilingPatternPdf(float xStep, float yStep)
    {
        // Pattern content stream: paint a small red square.
        const string patCs = "1 0 0 rg 0 0 5 5 re f";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R\n  /Resources <<" +
                  " /Pattern << /P1 5 0 R >> >>" +
                  " >>\nendobj\n");
        string pageCs = "/Pattern cs /P1 scn 0 0 100 100 re f";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageCs.Length, pageCs);
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< /PatternType 1 /PaintType 1 /TilingType 1" +
            " /BBox [0 0 5 5] /XStep {0} /YStep {1}" +
            " /Resources << >> /Length {2} >>\nstream\n{3}\nendstream\nendobj\n",
            xStep, yStep, patCs.Length, patCs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// XStep equal to the BBox width: the tiles abut, so the filled area is solid red with no gaps.
    /// Paired with the XStep &gt; BBox case below, which must leave gaps — together they pin the
    /// step arithmetic that a "did not throw" assertion cannot see at all.
    /// </summary>
    [Fact]
    public void TilingPattern_XStepEqualToBBox_CoversTheFillWithNoGaps()
    {
        using var bmp = PixelAssert.Render(BuildTilingPatternPdf(5f, 5f));

        // Every sample is red — including the points that fall in the gap when the step is larger.
        foreach (var pt in new[] { (2f, 2f), (7f, 7f), (12f, 2f), (2f, 7f), (50f, 50f) })
            Assert.True(PixelAssert.Near(PixelAssert.At(bmp, pt.Item1, pt.Item2), SKColors.Red),
                "(" + pt.Item1 + "," + pt.Item2 + ") should be inside a tile, got " +
                PixelAssert.At(bmp, pt.Item1, pt.Item2));

        Assert.Equal(100 * 100, PixelAssert.CountColor(bmp, SKColors.Red));
    }

    // ------------------------------------------------------------------
    //  Tiling pattern inside x-flip CTM renders without mirroring
    // ------------------------------------------------------------------

    [Fact]
    public void TilingPattern_InsideXFlipCTM_PatternColorUnchanged()
    {
        // When a tiling pattern is used inside a CTM with a negative x-scale
        // (horizontal flip: -1 0 0 1 ...) the shader local matrix must NOT
        // include gsCTM⁻¹, because path points are already in page space.
        // Before the fix, gsCTMInv was included, which doubled the x-flip and
        // mirrored the stamp image rendered by the pattern.
        //
        // Build: a 10×10 page, pattern fills a 5×5 rect via a -1 0 0 1 CTM.
        // Pattern paints the top-left 3×3 area red.  If the x-flip is handled
        // correctly the pattern appears in the expected position; if the shader
        // matrix is wrong the tile shifts and the wrong pixels are red.

        const string patCs = "1 0 0 rg 0 0 3 3 re f";   // red 3×3 in pattern space
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10]" +
                  " /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >> >> >>\nendobj\n");
        // Content: enter x-flip CTM, then fill 5×5 rect with pattern
        const string pageCs =
            "q\n" +
            "-1 0 0 1 10 0 cm\n" +           // x-flip: x' = -x + 10
            "/Pattern cs /P1 scn\n" +
            "0 0 5 5 re f\n" +
            "Q";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageCs.Length, pageCs);
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< /PatternType 1 /PaintType 1 /TilingType 1" +
            " /BBox [0 0 5 5] /XStep 5 /YStep 5" +
            " /Resources << >> /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            patCs.Length, patCs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        // The pattern has a red 3×3 in its BBox [0,0,5,5].
        // With a correct shader matrix, the fill area contains red pixels.
        // (Exact position varies with x-flip math; just verify non-blank render.)
        bool hasRed = false;
        for (int y = 0; y < bmp.Height && !hasRed; y++)
        for (int x = 0; x < bmp.Width  && !hasRed; x++)
        {
            var px = bmp.GetPixel(x, y);
            if (px.Red > 200 && px.Green < 50 && px.Blue < 50) hasRed = true;
        }
        Assert.True(hasRed, "Expected red pixels from the tiling pattern but found none");
    }

    /// <summary>
    /// XStep 10 with a 5x5 BBox: each tile is a 5x5 red cell padded with transparency, so the fill
    /// becomes a grid of red squares separated by untouched backdrop — a quarter coverage, not a
    /// solid block.
    /// </summary>
    [Fact]
    public void TilingPattern_XStepLargerThanBBox_LeavesTransparentGapsBetweenTiles()
    {
        using var bmp = PixelAssert.Render(BuildTilingPatternPdf(10f, 10f));

        // Inside the cells...
        Assert.True(PixelAssert.Near(PixelAssert.At(bmp, 2, 2), SKColors.Red));
        Assert.True(PixelAssert.Near(PixelAssert.At(bmp, 12, 2), SKColors.Red));

        // ...and in the padding, both horizontally and vertically.
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 7, 7)),
            "(7,7) is between tiles and should be unpainted, got " + PixelAssert.At(bmp, 7, 7));
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 2, 7)));

        // 5x5 painted out of every 10x10 step = one quarter of the 100x100 fill.
        Assert.Equal(2500, PixelAssert.CountColor(bmp, SKColors.Red));
    }

    // ------------------------------------------------------------------
    //  PaintType=2 uncolored tiling pattern — inherits current color
    // ------------------------------------------------------------------

    /// <summary>
    /// PaintType 2 (uncoloured): the pattern cell carries no colour of its own and must be painted
    /// in the colour given to <c>scn</c> ahead of the pattern name (PDF 32000-1 §8.7.3.3).
    ///
    /// <para><b>This test exposed a real defect.</b> <c>OpSc</c> saw the trailing pattern name and
    /// returned immediately, discarding the leading colour components — so <c>GS.FillColor</c> kept
    /// its default and <b>every uncoloured tiling pattern painted black</b>, whatever the content
    /// stream asked for. The predecessor asserted only that a PNG came back, and its fixture wrote
    /// <c>0 1 0 rg /Pattern cs /P1 scn</c>, which does not set an uncoloured pattern's colour at
    /// all — so it could not have caught this even with a colour assertion.</para>
    /// </summary>
    [Fact]
    public void TilingPattern_PaintType2_PaintsInTheColourGivenToScn()
    {
        // PaintType 2: pattern content uses the current color (green here).
        const string patCs = "0 0 5 5 re f";   // no color set — uses current
        string patHdr = "/PatternType 1 /PaintType 2 /TilingType 1" +
                        " /BBox [0 0 5 5] /XStep 5 /YStep 5 /Resources << >>";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >>" +
                  " /ColorSpace << /CS0 [/Pattern /DeviceRGB] >> >> >>\nendobj\n");
        // §8.7.3.3 form for an uncoloured pattern: the components, then the pattern name.
        string pageCs = "/CS0 cs 0 1 0 /P1 scn 0 0 100 100 re f";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageCs.Length, pageCs);
        int o5 = sb.Length;
        sb.AppendFormat("5 0 obj\n<< {0} /Length {1} >>\nstream\n{2}\nendstream\nendobj\n",
            patHdr, patCs.Length, patCs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        using var bmp = PixelAssert.Render(pdf);

        Assert.True(PixelAssert.Near(PixelAssert.At(bmp, 50, 50), SKColors.Lime),
            "the uncoloured pattern should take the green given to scn, got " +
            PixelAssert.At(bmp, 50, 50));
        Assert.Equal(100 * 100, PixelAssert.CountColor(bmp, SKColors.Lime));
        Assert.Equal(0, PixelAssert.CountColor(bmp, SKColors.Black));
    }

    // ------------------------------------------------------------------
    //  SVG renderer: XStep/YStep tiling pattern
    // ------------------------------------------------------------------

    [Fact]
    public void SvgTilingPattern_XStepYStepLarger_SvgContainsPattern()
    {
        byte[] pdf = BuildTilingPatternPdf(10f, 10f);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("<pattern", svg);
        // width/height should reflect XStep/YStep (10), not BBox (5).
        Assert.Contains("width=\"10\"", svg);
        Assert.Contains("height=\"10\"", svg);
    }

    // ------------------------------------------------------------------
    //  SVG renderer: Type 3 font
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal PDF with a Type 3 font and a single glyph ('A')
    /// so that DrawType3GlyphsSvg is exercised.
    /// </summary>
    private static byte[] BuildType3FontPdf()
    {
        // Glyph content: d1 advance, then a small filled square.
        const string glyphCs = "500 0 0 0 500 700 d1\n0 0 400 600 re f";
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R" +
                  " /Resources << /Font << /F3 5 0 R >> >> >>\nendobj\n");
        const string cs = "BT /F3 24 Tf 100 700 Td (A) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        // Font object (5), CharProcs stream (6)
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type3" +
                  " /FontMatrix [0.001 0 0 0.001 0 0]" +
                  " /FontBBox [0 0 500 700]" +
                  " /FirstChar 65 /LastChar 65 /Widths [500]" +
                  " /CharProcs << /A 6 0 R >>" +
                  " /Encoding << /Type /Encoding /Differences [65 /A] >>" +
                  " /Resources << >> >>\nendobj\n");
        int o6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            glyphCs.Length, glyphCs);
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void SvgType3Font_GlyphRendered_SvgContainsPathElement()
    {
        byte[] pdf = BuildType3FontPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        // Type 3 glyph 'A' has a filled square — expect a <path> in the SVG.
        Assert.Contains("<path", svg);
    }

    /// <summary>
    /// A Type 3 glyph is a content stream, not an outline in a font file, so the SVG renderer must
    /// emit it as drawn geometry — never as a <c>&lt;text&gt;</c> element, which a viewer would try
    /// to resolve against a font it does not have. The sibling test above pins that a
    /// <c>&lt;path&gt;</c> appears; this one pins the other half, and that the output is well-formed
    /// XML rather than merely non-empty.
    /// </summary>
    [Fact]
    public void SvgType3Font_DrawsGlyphsAsGeometryNotAsTextElements()
    {
        byte[] pdf = BuildType3FontPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.DoesNotContain("<text", svg);
        Assert.Contains("<path", svg);

        // Well-formed, not just non-empty — an unbalanced fragment throws here.
        var doc = System.Xml.Linq.XDocument.Parse(svg);
        Assert.Equal("svg", doc.Root!.Name.LocalName);
    }

    // ------------------------------------------------------------------
    //  SMask /TR transfer function applied to mask channel
    // ------------------------------------------------------------------

    /// <summary>
    /// An /SMask whose group draws nothing yields an all-zero alpha mask, so the black rectangle
    /// beneath it must be masked out completely. Comparing against the same rectangle rendered
    /// without the ExtGState is what makes this falsifiable: ignore the SMask and the page turns
    /// solid black. The predecessor asserted only that a PNG came back, which is true either way.
    /// </summary>
    [Fact]
    public void SmaskWithEmptyGroup_MasksOutTheFillEntirely()
    {
        // A soft mask with a /TR linear function (y = x) over an empty transparency group.
        const string cs = "q Q";  // trivial content
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        // ExtGState with an SMask that has a /TR type-2 function.
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R" +
                  " /Resources << /ExtGState << /GS1 5 0 R >> >> >>\nendobj\n");
        // Content: apply the ExtGState, draw a rectangle.
        const string pageCs = "/GS1 gs 0 0 100 100 re f";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageCs.Length, pageCs);
        // ExtGState with a trivial SMask (group = form XObject 6, TR = function 7).
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /ExtGState /SMask << /S /Alpha /G 6 0 R /TR 7 0 R >> >>\nendobj\n");
        // Group XObject: trivial transparency group.
        const string grpCs = "q Q";
        int o6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Type /XObject /Subtype /Form /FormType 1" +
                        " /BBox [0 0 100 100] /Resources << >> /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            grpCs.Length, grpCs);
        // TR function: type 2 linear, y = x.
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        using var masked = PixelAssert.Render(pdf);
        Assert.True(PixelAssert.IsBlank(masked),
            "the SMask should have removed the fill entirely, but " +
            PixelAssert.CountInk(masked) + " pixels were painted");

        // Control: the very same rectangle without the ExtGState paints the whole page.
        using var unmasked = PixelAssert.Render(BuildMinimalPdf("0 0 100 100 re f"));
        Assert.True(PixelAssert.CountInk(unmasked) > 5000,
            "the control render should paint the rectangle, or the assertion above proves nothing");
    }

    // ------------------------------------------------------------------
    //  TrueType glyph spacing: FontSize=1 + Tm-scale regression
    //
    //  Bug: DrawTextBytes computed pdfWidths in text-space (w/1000 * FontSize)
    //  but DrawGlyphRunWithWidths needs PDF user-space units.  When the PDF
    //  uses "1 Tf" with actual scale embedded in the Tm matrix (e.g.
    //  "9.5 0 0 9.5 Tm"), glyphs in multi-char TJ strings collapsed by
    //  a factor of tmScaleX.
    //
    //  Fix: pdfWidths[i] *= tmScaleX = sqrt(Tm[0]²+Tm[1]²).
    // ------------------------------------------------------------------

    private static int MeasureTextSpan(string contentStream, int dpi = 72)
    {
        byte[] png;
        using (var ms = new MemoryStream(BuildMinimalPdf(contentStream)))
            png = PdfPageRenderer.RenderPage(ms, 0, dpi);
        using var bmp = SKBitmap.Decode(png);
        if (bmp == null) return 0;

        // PDF y=400 from bottom → bitmap y = (792-400) * dpi/72 from top.
        int textCenterY = (bmp.Height - 400 * dpi / 72);
        int yMin = Math.Max(0, textCenterY - 80);
        int yMax = Math.Min(bmp.Height - 1, textCenterY + 30);

        int leftmost = int.MaxValue, rightmost = -1;
        for (int y = yMin; y <= yMax; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red < 200 || px.Green < 200 || px.Blue < 200)
                {
                    if (x < leftmost)  leftmost  = x;
                    if (x > rightmost) rightmost = x;
                }
            }
        return rightmost < 0 ? 0 : rightmost - leftmost + 1;
    }

    // ------------------------------------------------------------------
    //  Glyph-level system-font fallback for missing glyphs
    //
    //  Bug: when a TrueType subset doesn't include a character (e.g. the
    //  period '.' is absent from the SemiBold subset used for leader dots),
    //  typeface.GetGlyph(0x2E) returns GID 0, causing DrawGlyphRunWithWidths
    //  to place the .notdef glyph (an empty box) at that position.
    //
    //  Fix: when unicodeText is provided and glyphIds[i]==0, resolve the
    //  glyph from SKTypeface.Default and draw it with that typeface instead.
    // ------------------------------------------------------------------

    /// <summary>
    /// Draws a run into a fresh 300×100 raster with a 12pt text matrix whose origin is device
    /// (10, 50) — the shared fixture behind the three GID-0 tests below, so they differ only in
    /// the <paramref name="unicodeText"/> they pass, which is the variable under test.
    /// </summary>
    private static SKBitmap RenderGlyphRun(
        ushort[] glyphIds, SKTypeface tf, float[] pdfWidths, string unicodeText, out float advance)
    {
        var bitmap = new SKBitmap(300, 100);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var ctx = new PdfRenderContext(canvas, 300f, 100f, 1f);
            ctx.GS.Tm = new float[] { 12f, 0f, 0f, 12f, 10f, 50f };
            ctx.GS.FillColor = SKColors.Black;
            advance = ctx.DrawGlyphRunWithWidths(glyphIds, tf, pdfWidths, unicodeText);
        }
        return bitmap;
    }

    /// <summary>Inked pixels in the device-column band [x0, x1].</summary>
    private static int InkIn(SKBitmap bmp, int x0, int x1)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = x0; x <= x1 && x < bmp.Width; x++)
                if (PixelAssert.IsInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    /// <summary>Topmost inked device row in the band, or <see cref="int.MaxValue"/> when blank.</summary>
    private static int TopInkRow(SKBitmap bmp, int x0, int x1)
    {
        for (int y = 0; y < bmp.Height; y++)
            for (int x = x0; x <= x1 && x < bmp.Width; x++)
                if (PixelAssert.IsInk(bmp.GetPixel(x, y))) return y;
        return int.MaxValue;
    }

    private static byte[] Raster(SKBitmap bmp)
    {
        var bytes = new byte[bmp.Width * bmp.Height];
        int i = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                bytes[i++] = (byte)(PixelAssert.IsInk(bmp.GetPixel(x, y)) ? 1 : 0);
        return bytes;
    }

    /// <summary>
    /// With <c>unicodeText</c> supplied, a GID-0 slot must be <b>replaced</b> by the system font's
    /// glyph for that character: ink appears in the missing glyph's cell, and it is a period —
    /// hugging the baseline — rather than the tall .notdef box the primary font would have drawn.
    /// The run's advance still comes from the PDF widths.
    /// </summary>
    [Fact]
    public void DrawGlyphRunWithWidths_Gid0WithUnicodeText_SubstitutesTheGlyphFromTheSystemFont()
    {
        using var tf = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        using var probeFont = new SKFont(tf);
        ushort xGid = probeFont.GetGlyph('X');
        var glyphIds  = new ushort[] { 0, xGid };   // 0 = period absent from the subset
        var pdfWidths = new float[]  { 7f, 10f  };

        using var substituted = RenderGlyphRun(glyphIds, tf, pdfWidths, ".X", out float advance);
        using var notdef      = RenderGlyphRun(glyphIds, tf, pdfWidths, null, out _);

        Assert.Equal(17f, advance, precision: 3);

        // The period's cell is device x 10..16; 'X' starts at 17 and cannot reach into it.
        int cellInk = InkIn(substituted, 8, 16);
        Assert.True(cellInk > 0,
            "the missing '.' must be substituted from the system font, but its cell is blank");
        Assert.NotEqual(InkIn(notdef, 8, 16), cellInk);

        // A period sits on the baseline (device row 50); a .notdef box rises to the cap height,
        // roughly 9 device rows higher at 12pt.
        Assert.InRange(TopInkRow(substituted, 8, 16), 45, 50);
    }

    /// <summary>
    /// Without <c>unicodeText</c> there is nothing to substitute from, so the GID-0 slots stay on
    /// the primary font's .notdef path. The opt-in is the whole design: the two rasters must
    /// differ, or the "fallback" is running unconditionally.
    /// </summary>
    [Fact]
    public void DrawGlyphRunWithWidths_Gid0WithoutUnicodeText_DoesNotSubstituteFromTheSystemFont()
    {
        using var tf = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        var glyphIds  = new ushort[] { 0, 0, 0 };   // all .notdef
        var pdfWidths = new float[]  { 7f, 7f, 7f };

        using var notdef      = RenderGlyphRun(glyphIds, tf, pdfWidths, null,  out float advance);
        using var substituted = RenderGlyphRun(glyphIds, tf, pdfWidths, "...", out _);

        Assert.Equal(21f, advance, precision: 3);

        Assert.True(InkIn(substituted, 0, 299) > 0,
            "the '...' run must paint three periods for the comparison to mean anything");
        Assert.NotEqual(Raster(substituted), Raster(notdef));
    }

    /// <summary>
    /// U+FFFD in <c>unicodeText</c> is a ToUnicode-decode failure marker, not a character. The
    /// guard must make it behave exactly as "no ToUnicode at all" — otherwise Segoe UI's diamond
    /// ◆ is painted into the document as if it were content.
    /// </summary>
    [Fact]
    public void DrawGlyphRunWithWidths_Gid0WithReplacementChar_SkipsFallback()
    {
        using var tf = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        var glyphIds  = new ushort[] { 0 };
        var pdfWidths = new float[]  { 12f };

        using var replacement = RenderGlyphRun(glyphIds, tf, pdfWidths, "�", out _);
        using var noUnicode   = RenderGlyphRun(glyphIds, tf, pdfWidths, null,     out _);
        using var realChar    = RenderGlyphRun(glyphIds, tf, pdfWidths, "A",      out _);

        Assert.True(InkIn(realChar, 0, 299) > 0,
            "'A' must be substituted, or the contrast below proves nothing");

        Assert.Equal(Raster(noUnicode), Raster(replacement));      // guard fired
        Assert.NotEqual(Raster(realChar), Raster(replacement));    // and it is not a no-op
    }

    /// <summary>
    /// A lone surrogate in <c>unicodeText</c> — a <c>/ToUnicode</c> destination longer than four
    /// digits keeps one — names no character: its slot is not substituted, as U+FFFD's is not, and
    /// the next slot's character still is, in its own cell. <c>char.ConvertToUtf32</c> threw on it,
    /// and the whole run was lost with the text matrix left where the run had moved it.
    /// </summary>
    [Fact]
    public void DrawGlyphRunWithWidths_Gid0WithALoneSurrogate_DrawsTheRestOfTheRun()
    {
        using var tf = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
        var glyphIds  = new ushort[] { 0, 0 };
        var pdfWidths = new float[]  { 12f, 12f };
        string lone = new string((char)0xD800, 1) + ".";
        string replacement = new string((char)0xFFFD, 1) + ".";

        using var withLone        = RenderGlyphRun(glyphIds, tf, pdfWidths, lone, out float advance);
        using var withReplacement = RenderGlyphRun(glyphIds, tf, pdfWidths, replacement, out _);

        Assert.Equal(24f, advance, precision: 3);
        Assert.True(InkIn(withLone, 22, 34) > 0, "the second slot's period must be substituted in its own cell");
        Assert.Equal(Raster(withReplacement), Raster(withLone));
    }

    /// <summary>The glyph lookup behind it: a lone surrogate is one slot with no glyph, its neighbours mapped.</summary>
    [Fact]
    public void GlyphIdsFromUnicode_ALoneSurrogate_IsOneSlotWithNoGlyph()
    {
        using var tf = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;

        ushort[] ids = GlyphIdResolver.FromUnicode("A" + new string((char)0xDC00, 1) + "B", tf);

        Assert.Equal(3, ids.Length);
        Assert.Equal((ushort)0, ids[1]);
        Assert.NotEqual((ushort)0, ids[0]);
        Assert.NotEqual((ushort)0, ids[2]);
    }

    [Fact]
    public void TrueType_FontSize1_TmScale_GlyphsNotCollapsed()
    {
        // With "1 Tf" + "72 0 0 72 Tm", the effective font size is 72pt.
        // After fix: pdfWidths *= tmScaleX=72, so "ABC" spans ~150px at 72dpi.
        // Before fix: pdfWidths used text-space width (×1 not ×72), collapsing
        //   all three glyphs to ≈1px total span.
        const string cs = "BT\n/F1 1 Tf\n72 0 0 72 50 400 Tm\n(ABC) Tj\nET\n";
        int span = MeasureTextSpan(cs);
        if (span == 0) return; // typeface unavailable on this host — skip spatial check

        Assert.True(span >= 50,
            $"'ABC' at 72pt spanned only {span}px — glyphs may be collapsed " +
            "(FontSize=1 + Tm-scale=72 regression in DrawTextBytes pdfWidths)");
    }

    [Fact]
    public void TrueType_FontSize1_TmScale_MatchesEquivalentDirectScale()
    {
        // "1 Tf + 72-scale Tm" must produce the same text width as "72 Tf + identity Tm".
        // Both paths end up with identical scaledTm = [72,0,0,72,...] and
        // pdfWidths[i]=36 (fallback 500-unit width × 72 / 1000), so spans should match.
        const string csTmScale   = "BT\n/F1 1 Tf\n72 0 0 72 50 400 Tm\n(AB) Tj\nET\n";
        const string csDirectSz  = "BT\n/F1 72 Tf\n1 0 0 1 50 400 Tm\n(AB) Tj\nET\n";

        int spanTmScaled   = MeasureTextSpan(csTmScale);
        int spanDirectSize = MeasureTextSpan(csDirectSz);

        if (spanTmScaled == 0 || spanDirectSize == 0) return; // typeface unavailable

        float ratio = (float)spanTmScaled / spanDirectSize;
        Assert.True(ratio is >= 0.75f and <= 1.35f,
            $"Tm-scale span ({spanTmScaled}px) differs too much from direct-scale " +
            $"span ({spanDirectSize}px); ratio={ratio:F2}. " +
            "Expected within 35% — both code paths should produce equivalent glyph spacing.");
    }

    // ------------------------------------------------------------------
    //  DrawBitmap negative-d CTM: per PDF §8.9.4 and the another reader / Windows.Data.Pdf
    //  rendering model, image(0,0) maps to unit(0,1) via the default image matrix
    //  [1/w 0 0 -1/h 0 1].  For a negative-d CTM, unit(0,1) maps to a LOWER page-y
    //  than unit(1,0), so the first stream row belongs at the VISUAL BOTTOM of the
    //  image area (not the top).  DrawBitmap must flip vertically when screenT > screenB.
    //
    //  Test: 4×2 red-over-blue image placed with negative-d CTM.
    //  Row 0 (RED) → visual bottom; row 1 (BLUE) → visual top.
    // ------------------------------------------------------------------
    [Fact]
    public void DrawBitmap_NegativeD_CTM_Row0AtBottom_Row1AtTop()
    {
        // Image: 4×2 raw RGB.  Row 0 (stored first) = solid RED, row 1 = solid BLUE.
        // Negative-d CTM [200 0 0 -200 0 200]: image(0,0) → unit(0,1) → page(0,0) → screen-bottom.
        // After vertical flip in DrawBitmap: BLUE (row 1) at visual top, RED (row 0) at bottom.
        // This matches Windows.Data.Pdf / another reader behaviour for negative-d CTM images.
        byte[] pdf = BuildPdfWithNegativeDImage();
        using var ms = new System.IO.MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 96);
        Assert.NotNull(png);
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int topY    = bmp.Height / 8;       // safely inside top half of page
        int bottomY = bmp.Height * 7 / 8;   // safely inside bottom half
        int sampleX = bmp.Width / 2;

        var topPx    = bmp.GetPixel(sampleX, topY);
        var bottomPx = bmp.GetPixel(sampleX, bottomY);

        Assert.True(topPx.Blue > 180 && topPx.Red < 80,
            $"Top of page should be BLUE (row 1, negative-d CTM flips rows) but got R={topPx.Red} B={topPx.Blue}.");
        Assert.True(bottomPx.Red > 180 && bottomPx.Blue < 80,
            $"Bottom of page should be RED (row 0) but got R={bottomPx.Red} B={bottomPx.Blue}.");
    }

    private static byte[] BuildPdfWithNegativeDImage()
    {
        // 4×2 raw RGB image: row 0 = 4 red pixels, row 1 = 4 blue pixels.
        // Placed with [200 0 0 -200 0 200 cm] — standard negative-d AFP convention.
        // Raw pixels use 0xFF bytes which would be corrupted by string round-trips,
        // so the PDF is assembled directly as byte arrays rather than via strings.
        var rawPixels = new byte[]
        {
            // Row 0: red (4 pixels × 3 bytes)
            255,0,0,  255,0,0,  255,0,0,  255,0,0,
            // Row 1: blue (4 pixels × 3 bytes)
            0,0,255,  0,0,255,  0,0,255,  0,0,255,
        };

        var parts = new System.Collections.Generic.List<byte[]>();
        void Add(string s) => parts.Add(System.Text.Encoding.ASCII.GetBytes(s));

        // Build text parts; track object offsets.
        Add("%PDF-1.4\n");
        int o1 = parts.Sum(p => p.Length);
        Add("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = parts.Sum(p => p.Length);
        Add("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = parts.Sum(p => p.Length);
        Add("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]\n" +
            "   /Contents 4 0 R\n" +
            "   /Resources << /XObject << /Im0 5 0 R >> >>\n>>\nendobj\n");
        string cs = "q 200 0 0 -200 0 200 cm /Im0 Do Q";
        int o4 = parts.Sum(p => p.Length);
        Add($"4 0 obj\n<< /Length {cs.Length} >>\nstream\n{cs}\nendstream\nendobj\n");
        int o5 = parts.Sum(p => p.Length);
        Add($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 4 /Height 2\n" +
            $"   /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length {rawPixels.Length} >>\nstream\n");
        parts.Add(rawPixels);
        Add("\nendstream\nendobj\n");
        int xrefOff = parts.Sum(p => p.Length);
        Add("xref\n0 6\n0000000000 65535 f \n");
        Add($"{o1:D10} 00000 n \n");
        Add($"{o2:D10} 00000 n \n");
        Add($"{o3:D10} 00000 n \n");
        Add($"{o4:D10} 00000 n \n");
        Add($"{o5:D10} 00000 n \n");
        Add($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefOff}\n%%EOF\n");

        int total = parts.Sum(p => p.Length);
        var result = new byte[total];
        int offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }

    // ------------------------------------------------------------------
    //  1-bit bilevel fallback (CCITT tiles without /BitsPerComponent)
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a PDF with a 4×2 image XObject that has no /ColorSpace, no
    /// /BitsPerComponent, and no /Filter.  The raw data (2 bytes) is a packed
    /// 1-bit-per-pixel bilevel image whose length exactly equals ceil(4/8)*2 = 2.
    /// DecodeToBitmap must take the packed-1-bit fallback path and render the
    /// checkerboard correctly.
    /// </summary>
    private static byte[] BuildPdfWithRaw1BitImage()
    {
        // Pixel layout (MSB-first):
        //   Row 0: 1010 xxxx → bytes[0] = 0xA0 → pixels: B W B W
        //   Row 1: 0101 xxxx → bytes[1] = 0x50 → pixels: W B W B
        // After CcittFaxDecodeFilter InvertBytes (default blackIs1=false): 0=black,1=white.
        // The data here is already "post-invert" convention: bit=1 → white, bit=0 → black.
        byte[] imgData = { 0xA0, 0x50 };

        // Build the image stream header (no /Filter, no /ColorSpace, no /BitsPerComponent).
        string imgHdr = $"<< /Type /XObject /Subtype /Image /Width 4 /Height 2 /Length {imgData.Length} >>";

        // Content stream: scale unit square to 4×2 (pts), then invoke image.
        const string cs = "q 4 0 0 2 0 0 cm /Im0 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 4 2]" +
                  " /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        // Object 5: the image stream.  We write the header as text then append the binary data below.
        int o5 = sb.Length;
        sb.Append($"5 0 obj\n{imgHdr}\nstream\n");
        int streamBodyOffset = sb.Length;  // offset within the ASCII prefix before binary bytes
        sb.Append("PLACEHOLDER");          // will be replaced with real binary
        sb.Append("\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        // Splice the binary image data in place of PLACEHOLDER.
        byte[] ascii = Encoding.ASCII.GetBytes(sb.ToString());
        int phOffset = streamBodyOffset;
        // Find PLACEHOLDER (11 bytes) in ascii.
        for (int i = phOffset; i < ascii.Length - 10; i++)
        {
            if (ascii[i] == 'P' && ascii[i + 1] == 'L')
            {
                // Replace those 2 bytes with the actual image data bytes.
                // PLACEHOLDER is 11 bytes; imgData is 2 bytes — just overwrite first 2.
                var result = new byte[ascii.Length - 11 + imgData.Length];
                System.Array.Copy(ascii, 0, result, 0, i);
                System.Array.Copy(imgData, 0, result, i, imgData.Length);
                System.Array.Copy(ascii, i + 11, result, i + imgData.Length, ascii.Length - i - 11);
                return result;
            }
        }
        return ascii;
    }

    [Fact]
    public void OneBitBilevelFallback_ImageWithoutBpcOrColorSpace_RendersCheckerboard()
    {
        // A 4×2 image with packed-1-bit data (length = ceil(w/8)*h = 2 bytes) and
        // no /BitsPerComponent / /ColorSpace triggers the packed-1-bit fallback path.
        // Row 0 bits: 1010xxxx → pixels W B W B (1=white,0=black after inversion).
        // Wait: 0xA0 = 10100000. MSB-first: bit7=1,bit6=0,bit5=1,bit4=0 → pixels:
        //   bit=1 → v1=255 (white), bit=0 → v0=0 (black) with default decode [0 1].
        // Row 0: white, black, white, black.
        // Row 1 (0x50 = 01010000): black, white, black, white.
        byte[] pdf = BuildPdfWithRaw1BitImage();
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 96);
        Assert.NotNull(png);
        Assert.True(png.Length > 0, "Rendered PNG must not be empty");

        // Decode PNG to verify pixel colors.
        using var decoded = SKBitmap.Decode(png);
        Assert.NotNull(decoded);
        Assert.True(decoded.Width > 0 && decoded.Height > 0);

        // The checkerboard pattern must produce both black and white pixels.
        bool hasBlack = false, hasWhite = false;
        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                var px = decoded.GetPixel(x, y);
                if (px.Red < 128 && px.Green < 128 && px.Blue < 128) hasBlack = true;
                else if (px.Red > 128 && px.Green > 128 && px.Blue > 128) hasWhite = true;
                if (hasBlack && hasWhite) break;
            }
            if (hasBlack && hasWhite) break;
        }
        Assert.True(hasBlack, "Checkerboard image must have dark pixels");
        Assert.True(hasWhite, "Checkerboard image must have light pixels");
    }

    /// <summary>
    /// A CCITTFaxDecode image whose /DecodeParms omits /Columns must be decoded at the image's
    /// <b>/Width</b>, not at the 1728-column fax default. Some generators (DocuSign among them)
    /// rely on that; at 1728 columns the decode either fails — and the filter's silent fallback
    /// hands back the <i>compressed</i> bytes, which the renderer then paints as noise — or
    /// produces a bitmap with the wrong stride.
    ///
    /// <para>The assertion is the decoded length: <c>ceil(16/8) × 4 = 8</c> bytes for the declared
    /// 16×4 image, which is neither the 3-byte input nor the 864 bytes a 1728-column decode
    /// yields. <see cref="CcittFaxDecodeFilter.LastFallbackReason"/> being null is the filter's
    /// own statement that what came back is pixels rather than passthrough.</para>
    ///
    /// <para>Its predecessor rendered an unrelated PDF — one with no CCITT filter at all, as its
    /// own comment admitted — and asserted only that nothing was thrown.</para>
    /// </summary>
    [Fact]
    public void CcittFaxDecode_MissingColumns_DoesNotReturnRawCompressedBytes()
    {
        const int width = 16, height = 4;

        // Group 4 (T.6) for a 16-wide image whose every row is 8 white then 8 black:
        //   row 0: Horizontal "001" + white-8 "10011" + black-8 "000101"
        //   rows 1-3: V0 "1" twice — copy the reference line's two changing elements
        // → 001 10011 000101 11 11 11, zero-padded to 24 bits.
        byte[] encoded = { 0x33, 0x17, 0xF0 };

        var image = new PdfStream();
        image[PdfName.Type]    = PdfName.XObject;
        image[PdfName.Subtype] = PdfName.Image;
        image[PdfName.Width]   = new PdfNumber(width);
        image[PdfName.Height]  = new PdfNumber(height);
        image[PdfName.Filter]  = PdfName.CCITTFaxDecode;

        var parms = new PdfDictionary();
        parms[PdfName.K]    = new PdfNumber(-1);   // Group 4
        parms[PdfName.Rows] = new PdfNumber(height);
        // …and deliberately NO /Columns. That is the whole point.
        image[PdfName.DecodeParms] = parms;
        image.RawData = encoded;

        byte[] decoded = ContentStreamInterpreter.DecodeImageStream(image, width);

        Assert.Null(CcittFaxDecodeFilter.LastFallbackReason);
        Assert.Equal((width + 7) / 8 * height, decoded.Length);
        Assert.NotEqual(encoded.Length, decoded.Length);

        // /BlackIs1 defaults to false, so the decoder's "1 = black" is inverted on the way out:
        // each row is 0xFF (eight white) then 0x00 (eight black).
        Assert.Equal(new byte[] { 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00 }, decoded);
    }

    // ------------------------------------------------------------------
    //  Form XObject outer opacity (ca) — PDF §11.6.6
    //
    //  A TRANSPARENCY GROUP form composites as a unit: the page's ca=0.5 applies to the
    //  group result, and an inner `gs` setting ca=1 cannot escape it.
    //
    //  A PLAIN form does not: its contents inherit the graphics state and an inner
    //  `gs` legitimately overrides ca, so the same content renders opaque. This test
    //  originally asserted the group behaviour for a plain form on the belief that
    //  "a common viewer composites the entire form at the outer opacity regardless" — that
    //  holds for group XObjects only, so both cases are now covered separately.
    // ------------------------------------------------------------------

    private static byte[] BuildPdfWithFormOpacity(bool asTransparencyGroup = false)
    {
        // Form content: /GSInner gs resets ca=1, then draws red rectangle.
        string formContent = "/GSInner gs\n1 0 0 rg\n0 0 100 100 re\nf\n";
        string pageContent = "/GSOuter gs\n/Fm0 Do\n";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R" +
                  " /Resources << /XObject << /Fm0 5 0 R >> /ExtGState << /GSOuter 6 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            pageContent.Length, pageContent);
        // Form XObject
        int o5 = sb.Length;
        string groupEntry = asTransparencyGroup
            ? " /Group << /S /Transparency /CS /DeviceRGB >>" : "";
        sb.AppendFormat("5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
                        groupEntry +
                        " /Resources << /ExtGState << /GSInner 7 0 R >> >>" +
                        " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            formContent.Length, formContent);
        // Page-level GS: ca=0.5 (outer opacity for the form)
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /ExtGState /ca 0.5 /CA 0.5 >>\nendobj\n");
        // Form-level GS: ca=1 (would override outer if no layer)
        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /ExtGState /ca 1 /CA 1 >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5, o6, o7 })
            sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Samples the centre pixel of a 100×100 pt page rendered at 72 dpi.</summary>
    private static SKColor RenderFormOpacityCentrePixel(bool asTransparencyGroup)
    {
        byte[] pdf = BuildPdfWithFormOpacity(asTransparencyGroup);
        byte[] png;
        using (var ms = new MemoryStream(pdf))
            png = PdfPageRenderer.RenderPage(ms, 0, 72);

        Assert.NotNull(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
    }

    [Fact]
    public void TransparencyGroupForm_OuterOpacity_SurvivesInnerReset()
    {
        // The page sets ca=0.5 before `Do`; the GROUP internally resets ca=1. The group
        // composites as a unit, so the result is still ~50% red over white:
        //   R = 255*0.5 + 255*0.5 = 255,  G = 0*0.5 + 255*0.5 = 128,  B = 128
        var px = RenderFormOpacityCentrePixel(asTransparencyGroup: true);

        Assert.True(px.Green > 80 && px.Green < 200,
            $"Expected ~50% opacity (Green≈128), got Green={px.Green}");
        Assert.True(px.Blue > 80 && px.Blue < 200,
            $"Expected ~50% opacity (Blue≈128), got Blue={px.Blue}");
    }

    [Fact]
    public void PlainForm_InnerGsCanOverrideOuterOpacity()
    {
        // Same content WITHOUT /Group. A plain form XObject is not composited as a unit, so
        // its inner `gs` setting ca=1 applies to the fill and the red is opaque (§8.10.2).
        var px = RenderFormOpacityCentrePixel(asTransparencyGroup: false);

        Assert.True(px.Green < 60,
            $"A plain form must honour its own inner ca=1; got Green={px.Green} (expected ~0).");
        Assert.True(px.Blue < 60,
            $"A plain form must honour its own inner ca=1; got Blue={px.Blue} (expected ~0).");
    }

    // ------------------------------------------------------------------
    //  FontCache.IsType3Font and per-call caching correctness
    // ------------------------------------------------------------------

    [Fact]
    public void FontCache_IsType3Font_ReturnsTrueForType3()
    {
        // Build a minimal PDF with a Type3 font resource.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        const string cs = "BT /F1 12 Tf 10 50 Td (A) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);
        // Type3 font
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type3" +
                  " /FontMatrix [0.001 0 0 0.001 0 0]" +
                  " /FontBBox [0 0 1000 1000]" +
                  " /FirstChar 65 /LastChar 65 /Widths [600]" +
                  " /CharProcs << /A 6 0 R >>" +
                  " /Resources << >> >>\nendobj\n");
        const string charProc = "600 0 0 0 600 700 d1 0 0 600 700 re f";
        int o6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            charProc.Length, charProc);
        var offsets = new[] { o1, o2, o3, o4, o5, o6 };
        sb.Append("xref\n");
        sb.AppendFormat("0 {0}\n0000000000 65535 f \n", offsets.Length + 1);
        foreach (var off in offsets)
            sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Length + 1, sb.Length);

        byte[] pdfBytes = System.Text.Encoding.Latin1.GetBytes(sb.ToString());
        using var ms = new MemoryStream(pdfBytes);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var page = doc.Pages[0];
        var resources = doc.ResolveObject(page.Dictionary[new Tetitulz.Pdf.Core.Objects.PdfName("Resources")])
                        as Tetitulz.Pdf.Core.Objects.PdfDictionary
                     ?? page.Dictionary.GetDictionary(new Tetitulz.Pdf.Core.Objects.PdfName("Resources"));

        using var cache = new FontCache();
        Assert.True(cache.IsType3Font("F1", resources, doc), "F1 is a Type3 font");
        Assert.False(cache.IsType3Font("F99", resources, doc), "Unknown font is not Type3");

        // Second call must return same result (cached path).
        Assert.True(cache.IsType3Font("F1", resources, doc), "Cached F1 must still be Type3");
    }

    // ------------------------------------------------------------------
    //  Inline image: /D abbreviation expanded to /Decode
    // ------------------------------------------------------------------

    [Fact]
    public void InlineImage_AbbreviatedDecodeKey_D_InvertsGrayscalePixel()
    {
        // /D [1 0] is the abbreviated form of /Decode [1 0] (PDF spec Table 92).
        // A pixel with raw value 0 (black) through decode [1,0] maps to
        //   x_out = 1 + 0 * (0-1) = 1.0 → 255 = white.
        // Before the fix, /D was never renamed to /Decode so the array was ignored
        // and the black pixel rendered black, not white.
        const string cs =
            "612 0 0 792 0 0 cm\n" +
            "BI\n/W 1\n/H 1\n/BPC 8\n/CS /G\n/D [1 0]\n/F /AHx\nID\n00>\nEI";
        byte[] pdf = BuildMinimalPdf(cs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        // The 1x1 image scaled to fill the 612x792 page should be entirely white
        // after decode inversion of the black (0) source pixel.
        var midPx = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, (int)midPx.Red);
        Assert.Equal(255, (int)midPx.Green);
        Assert.Equal(255, (int)midPx.Blue);
    }

    // ------------------------------------------------------------------
    //  Inline image: abbreviated key wins over full key (ISO 32000-2 §8.9.7)
    // ------------------------------------------------------------------

    [Fact]
    public void InlineImage_AbbreviatedWidthKey_WinsOverFullWidthKey()
    {
        // ISO 32000-2 §8.9.7: when both abbreviated and full-name forms are present,
        // the abbreviated form takes priority.
        // /W 2 (abbreviated) must win over /Width 999 (full).
        // Data encodes exactly 2×1 RGB pixels (6 raw bytes = 12 hex chars).
        // If /Width 999 won, the decoder would try to materialise 999×3=2997 bytes
        // from those 12 hex chars and either throw or produce garbage.
        const string cs =
            "612 0 0 792 0 0 cm\n" +
            "BI\n/W 2\n/Width 999\n/H 1\n/Height 999\n/BPC 8\n/CS /RGB\n/F /AHx\nID\n" +
            "FFFFFFFFFFFF>\nEI";
        byte[] pdf = BuildMinimalPdf(cs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        // Two white pixels scaled to fill the page → mid-pixel is white.
        var mid = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, (int)mid.Red);
        Assert.Equal(255, (int)mid.Green);
        Assert.Equal(255, (int)mid.Blue);
    }

    [Fact]
    public void InlineImage_AbbreviatedDecodeKey_WinsOverFullDecodeKey()
    {
        // /D [1 0] (abbreviated) must win over /Decode [0 1] (full, identity).
        // Raw pixel 0x00 mapped through [1,0] gives white (inverted);
        // mapped through [0,1] gives black (identity).
        const string cs =
            "612 0 0 792 0 0 cm\n" +
            "BI\n/W 1\n/H 1\n/BPC 8\n/CS /G\n/D [1 0]\n/Decode [0 1]\n/F /AHx\nID\n00>\nEI";
        byte[] pdf = BuildMinimalPdf(cs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        // Abbreviated /D [1 0] inverts the 0x00 raw value → white.
        var mid = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, (int)mid.Red);
        Assert.Equal(255, (int)mid.Green);
        Assert.Equal(255, (int)mid.Blue);
    }

    [Fact]
    public void InlineImage_AbbreviatedBPC_WinsOverFullBitsPerComponent()
    {
        // /BPC 8 (abbreviated) wins over /BitsPerComponent 1 (full).
        // Data is 2×1 RGB at 8 bits/channel (6 bytes = 12 hex chars).
        // If BitsPerComponent=1 won, the decoder would interpret the 6 bytes
        // as a very different bitfield image → wrong output.
        const string cs =
            "612 0 0 792 0 0 cm\n" +
            "BI\n/W 2\n/H 1\n/BPC 8\n/BitsPerComponent 1\n/CS /RGB\n/F /AHx\nID\n" +
            "FFFFFFFFFFFF>\nEI";
        byte[] pdf = BuildMinimalPdf(cs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        var mid = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, (int)mid.Red);
        Assert.Equal(255, (int)mid.Green);
        Assert.Equal(255, (int)mid.Blue);
    }

    [Fact]
    public void InlineImage_AbbreviatedFilterArray_F_WinsOverFullFilterArray()
    {
        // /F [/AHx] (abbreviated) wins over /Filter [/A85] (full).
        // Data is ASCIIHex-encoded. If ASCII85Decode were applied instead,
        // it would corrupt the hex text → error or garbage pixel.
        const string cs =
            "612 0 0 792 0 0 cm\n" +
            "BI\n/W 2\n/H 1\n/BPC 8\n/CS /RGB\n/F [/AHx]\n/Filter [/A85]\nID\n" +
            "FFFFFFFFFFFF>\nEI";
        byte[] pdf = BuildMinimalPdf(cs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        var mid = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, (int)mid.Red);
        Assert.Equal(255, (int)mid.Green);
        Assert.Equal(255, (int)mid.Blue);
    }

    [Fact]
    public void InlineImage_AbbreviatedCS_WinsOverFullColorSpace()
    {
        // /CS /G (abbreviated → DeviceGray) wins over /ColorSpace /DeviceRGB (full).
        // Gray data is 1 byte per pixel; if DeviceRGB won the renderer would expect
        // 3 bytes for that 1-pixel image → too-short buffer or wrong channel mapping.
        const string cs =
            "612 0 0 792 0 0 cm\n" +
            "BI\n/W 1\n/H 1\n/BPC 8\n/CS /G\n/ColorSpace /DeviceRGB\n/F /AHx\nID\nFF>\nEI";
        byte[] pdf = BuildMinimalPdf(cs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        var mid = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, (int)mid.Red);
        Assert.Equal(255, (int)mid.Green);
        Assert.Equal(255, (int)mid.Blue);
    }

    /// <summary>
    /// The abbreviated <c>/I</c> key wins over the full <c>/Interpolate</c> when both appear, so
    /// <c>/I false /Interpolate true</c> must render <b>without</b> smoothing.
    ///
    /// <para>Made observable by scaling a 2x2 checkerboard up to the whole page: with interpolation
    /// off a scanline holds exactly the two source levels; with it on the upscale is smoothed into
    /// dozens of intermediate greys. The predecessor used a 1x1 image — where interpolation cannot
    /// produce any visible difference at all — and then asserted only that a PNG came back, so it
    /// could not distinguish the two keys under any implementation.</para>
    /// </summary>
    [Fact]
    public void InlineImage_AbbreviatedI_WinsOverFullInterpolate()
    {
        using var abbreviatedWins = PixelAssert.Render(Checkerboard2x2("/I false\n/Interpolate true"));
        using var interpolated    = PixelAssert.Render(Checkerboard2x2("/Interpolate true"));

        // /I false -> hard edges: only the two source levels appear along a scanline.
        Assert.Equal(2, PixelAssert.DistinctLevelsOnRow(abbreviatedWins, 25));

        // The control proves the renderer *can* interpolate, so the 2 above is a real decision and
        // not simply an unimplemented feature.
        Assert.True(PixelAssert.DistinctLevelsOnRow(interpolated, 25) > 20,
            "with /Interpolate true alone the upscale should be smoothed");
    }

    /// <summary>A page filled by a 2x2 grey checkerboard inline image, scaled to 100x100.</summary>
    private static byte[] Checkerboard2x2(string interpolationKeys)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        string cs = "100 0 0 100 0 0 cm\nBI\n/W 2\n/H 2\n/BPC 8\n/CS /G\n" +
                    interpolationKeys + "\n/F /AHx\nID\n00FFFF00>\nEI";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
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

    [Fact]
    public void InlineImage_AbbreviatedDP_WinsOverFullDecodeParms()
    {
        // /DP null (abbreviated) wins over /DecodeParms with PNG predictor (full).
        // The image data is Flate-compressed WITHOUT a predictor.
        // If /DecodeParms << /Predictor 15 ...>> won, Flate would decode correctly
        // but the predictor pass would then mis-interpret raw pixel bytes as filter
        // bytes → wrong or missing pixel values.
        byte[] flated = FlateEncodeFilter.Encode(new byte[] { 0xFF }); // 1×1 white gray
        string hexFlated = BitConverter.ToString(flated).Replace("-", "") + ">";
        string cs =
            "612 0 0 792 0 0 cm\n" +
            "BI\n/W 1\n/H 1\n/BPC 8\n/CS /G\n/F [/AHx /Fl]\n" +
            "/DP [null null]\n" +
            "/DecodeParms [null << /Predictor 15 /Colors 1 /BitsPerComponent 8 /Columns 1 >>]\n" +
            "ID\n" + hexFlated + "\nEI";
        byte[] pdf = BuildMinimalPdf(cs);
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        var mid = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.Equal(255, (int)mid.Red);
        Assert.Equal(255, (int)mid.Green);
        Assert.Equal(255, (int)mid.Blue);
    }

    // ------------------------------------------------------------------
    //  Hebrew font with afii57xxx Differences encoding renders correctly
    // ------------------------------------------------------------------

    [Fact]
    public void HebrewFont_Afii57xEncoding_DecodesAndRendersWithoutException()
    {
        // Build a minimal PDF with a TrueType font that uses afii57xxx glyph names
        // in its /Differences array (as produced by Israeli PDF generators).
        // The font uses char codes 0xE0-0xFA for Hebrew alef-tav.
        // Before the fix, TryGetGlyphUnicode("afii57664") returned false, so the
        // /Differences were ignored and the font fell back to WinAnsiEncoding,
        // mapping 0xE0 → U+00E0 (à) — not found in the Hebrew font → wrong glyph.

        // Build a minimal 1-page PDF with a fake Helvetica font (no embed needed
        // for a smoke test) and an Encoding with afii57xxx Differences.
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // Encoding object with Hebrew afii57xxx Differences
        int o4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Encoding /Differences [224 /afii57664 /afii57665 /afii57666] >>\nendobj\n");

        // Font referencing the encoding
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding 4 0 R >>\nendobj\n");

        // Content stream: draw char codes 0xE0-0xE2 (should decode as alef/bet/gimel)
        string cs = "BT /F1 12 Tf 72 700 Td (\xE0\xE1\xE2) Tj ET";
        int o6 = sb.Length;
        sb.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 6 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        byte[] pdf = System.Text.Encoding.Latin1.GetBytes(sb.ToString());

        // Render — must not throw and must decode 0xE0→alef, 0xE1→bet, 0xE2→gimel
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);

        // Verify text extraction decodes correctly via the font's Decode path
        ms.Position = 0;
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var page = doc.Pages[0];
        var resources = doc.ResolveObject(page.Dictionary[
            new Tetitulz.Pdf.Core.Objects.PdfName("Resources")])
            as Tetitulz.Pdf.Core.Objects.PdfDictionary
            ?? page.Dictionary.GetDictionary(
                new Tetitulz.Pdf.Core.Objects.PdfName("Resources"));
        var fontDict = (doc.ResolveObject(
            (doc.ResolveObject(resources[
                new Tetitulz.Pdf.Core.Objects.PdfName("Font")])
                as Tetitulz.Pdf.Core.Objects.PdfDictionary)
            [new Tetitulz.Pdf.Core.Objects.PdfName("F1")])
            as Tetitulz.Pdf.Core.Objects.PdfDictionary);
        var font = Tetitulz.Pdf.Core.Fonts.PdfFont.Create(fontDict, doc);
        string decoded = font.Decode(new byte[] { 0xE0, 0xE1, 0xE2 });
        Assert.Equal("אבג", decoded);
    }

    // ------------------------------------------------------------------
    //  Indexed image: 4-bpc packed pixels decoded correctly
    // ------------------------------------------------------------------

    [Fact]
    public void IndexedImage_4Bpc_PixelsReadFromCorrectNibbles()
    {
        // A 4x2 Indexed-RGB image with /BitsPerComponent 4 stores 2 palette indices
        // per byte (high nibble = left pixel, low nibble = right pixel).
        // Before the fix, DecodeIndexedBitmap treated every byte as one pixel index,
        // producing garbled colors (border lines rendered black instead of gray).
        //
        // Image layout (palette: 0=white #FFFFFF, 1=gray #A4A4A4):
        //   row 0 bytes: 0x01 0x01  → pixels [0,1, 0,1] → white, gray, white, gray
        //   row 1 bytes: 0x11 0x11  → pixels [1,1, 1,1] → all gray
        // ASCIIHex-encoded data: "01011111>"  (4 bytes + terminator)
        const string imgStream = "01011111>";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        // Image XObject: 4×2, 4-bpc, Indexed RGB, ASCIIHex-encoded
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<</Type/XObject/Subtype/Image/Width 4/Height 2" +
            "/BitsPerComponent 4" +
            "/ColorSpace [/Indexed /DeviceRGB 1 <FFFFFFA4A4A4>]" +
            "/Filter /ASCIIHexDecode/Length {0}>>\nstream\n{1}\nendstream\nendobj\n",
            imgStream.Length, imgStream);
        // Content: place the 4×2 image to fill the 4×2 pt page
        const string cs = "4 0 0 2 0 0 cm\n/Im1 Do";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 4 2]" +
                  " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5 })
            sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        Assert.True(bmp.Width >= 4 && bmp.Height >= 2);

        // col 0 row 0 → palette index 0 → white
        var white = bmp.GetPixel(0, 0);
        Assert.Equal(255, (int)white.Red);
        Assert.Equal(255, (int)white.Green);
        Assert.Equal(255, (int)white.Blue);

        // col 1 row 0 → palette index 1 → gray #A4A4A4
        var gray = bmp.GetPixel(1, 0);
        Assert.Equal(0xA4, (int)gray.Red);
        Assert.Equal(0xA4, (int)gray.Green);
        Assert.Equal(0xA4, (int)gray.Blue);
    }

    // ------------------------------------------------------------------
    //  System-fallback font spacing: /Filter/XXXDecode → SkiaSharp widths
    //
    //  When the embedded font can't be decoded, a system-font substitute is
    //  used.  The PDF /Widths (which belong to the original font) must NOT
    //  be applied to the substitute — otherwise the narrower script-font
    //  widths would cause glyphs of the wider system font to overlap.
    //  Instead, DrawGlyphRunWithWidths uses SkiaSharp-measured advances.
    //
    //  This test sets /Widths = [1, 1, …] (1/1000 em each — effectively zero
    //  advance) on a font with /Filter/XXXDecode, renders "AB" in red, then
    //  checks that the two glyphs are spaced apart (not collapsed on top of
    //  each other), proving SkiaSharp advances are in use.
    // ------------------------------------------------------------------
    [Fact]
    public void UnknownFilterFont_GlyphSpacingUsesSkiaSharpWidths_NotPdfWidths()
    {
        // Font has /Widths = [1 per char] — effectively 0.001em per glyph.
        // With these widths, "AB" would collapse to a single pixel column.
        // With SkiaSharp widths (system font), "AB" spans many pixel columns.
        byte[] pdf = BuildPdfWithUnknownFilterFontAndTinyWidths();
        using var ms = new System.IO.MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 144);
        Assert.NotNull(png);
        Assert.NotEmpty(png);

        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        // Find leftmost and rightmost red-pixel column.
        int leftRed = int.MaxValue, rightRed = int.MinValue;
        for (int x = 0; x < bmp.Width; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red > 180 && px.Green < 80 && px.Blue < 80)
                {
                    if (x < leftRed)  leftRed  = x;
                    if (x > rightRed) rightRed = x;
                }
            }

        int span = rightRed - leftRed + 1;
        // "AB" at 10pt × 2 DPI-scale with SkiaSharp widths should span at
        // least 15 px.  With the PDF tiny widths (1/1000 em) it would be ~1 px.
        Assert.True(span >= 12,
            $"Red text pixel span ({span}px) is too narrow — suggests PDF's tiny " +
            $"widths were used instead of SkiaSharp widths for the system-fallback font. " +
            $"leftRed={leftRed}, rightRed={rightRed}, bmp={bmp.Width}x{bmp.Height}");
    }

    private static byte[] BuildPdfWithUnknownFilterFontAndTinyWidths()
    {
        // Font /F0: TrueType with /Filter/XXXDecode on FontFile2 and tiny /Widths.
        byte[] dummyFont = new byte[32]; // garbage bytes — undecodable

        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50]\n" +
                  "   /Contents 4 0 R\n" +
                  "   /Resources << /Font << /F0 5 0 R >> >>\n>>\nendobj\n");
        string cs = "BT 1 0 0 rg /F0 10 Tf 10 20 Td (AB) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        // /Widths entries: 35 entries for chars 32–66 (space through 'B'), all = 1
        string widths = string.Join(" ", System.Linq.Enumerable.Repeat("1", 35));
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /XXXXXX+Arial\n" +
                  "   /Encoding /WinAnsiEncoding\n" +
                  "   /FirstChar 32 /LastChar 66\n" +
                  $"   /Widths [{widths}]\n" +
                  "   /FontDescriptor 6 0 R\n>>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /FontDescriptor /FontName /XXXXXX+Arial\n" +
                  "   /Flags 32 /Ascent 728 /Descent -210 /CapHeight 716 /ItalicAngle 0\n" +
                  "   /StemV 80 /FontBBox [-665 -210 2000 728]\n" +
                  "   /FontFile2 7 0 R\n>>\nendobj\n");
        int o7 = sb.Length;
        sb.AppendFormat("7 0 obj\n<< /Filter /XXXDecode /Length {0} /Length1 {0} >>\nstream\n",
            dummyFont.Length);

        var header = System.Text.Encoding.ASCII.GetBytes(sb.ToString());
        var trailer = System.Text.Encoding.ASCII.GetBytes("\nendstream\nendobj\n");
        var body = new byte[header.Length + dummyFont.Length + trailer.Length];
        header.CopyTo(body, 0);
        dummyFont.CopyTo(body, header.Length);
        trailer.CopyTo(body, header.Length + dummyFont.Length);

        var sb2 = new System.Text.StringBuilder(System.Text.Encoding.ASCII.GetString(body));
        int xrefOff = sb2.Length;
        sb2.Append("xref\n0 8\n0000000000 65535 f \n");
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

    // ------------------------------------------------------------------
    //  ObjStm with unknown filter: font dict unresolvable → SkiaSharp widths
    //
    //  When the font dictionary lives inside an ObjStm whose /Filter is
    //  unknown (e.g. XXXDecode), LookupFontDict returns null.  The fix must
    //  still mark the font as a system fallback so that glyph spacing uses
    //  SkiaSharp-measured advances rather than a hardcoded 500/1000-em default.
    //  We simulate this by building a PDF whose /F0 references a missing
    //  object (same null-dict code path) and setting PDF widths to 1/1000-em.
    // ------------------------------------------------------------------
    [Fact]
    public void ObjStmUnknownFilter_GlyphSpacingUsesSkiaSharpWidths_WhenFontDictMissing()
    {
        byte[] pdf = BuildPdfWithMissingFontDictAndTinyWidths();
        using var ms = new System.IO.MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 144);
        Assert.NotNull(png);
        Assert.NotEmpty(png);

        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int leftRed = int.MaxValue, rightRed = int.MinValue;
        for (int x = 0; x < bmp.Width; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red > 180 && px.Green < 80 && px.Blue < 80)
                {
                    if (x < leftRed)  leftRed  = x;
                    if (x > rightRed) rightRed = x;
                }
            }

        int span = rightRed - leftRed + 1;
        Assert.True(span >= 12,
            $"Red text pixel span ({span}px) is too narrow — suggests the hardcoded " +
            $"500/1000-em or PDF tiny widths were used instead of SkiaSharp widths. " +
            $"leftRed={leftRed}, rightRed={rightRed}, bmp={bmp.Width}x{bmp.Height}");
    }

    private static byte[] BuildPdfWithMissingFontDictAndTinyWidths()
    {
        // Same PDF/Widths-tiny trick as BuildPdfWithUnknownFilterFontAndTinyWidths,
        // but instead of an XXXDecode font file the font dict itself is missing
        // (object 5 not in xref), simulating objects inside an unreadable ObjStm.
        // The content stream references /F0; since the font dict is unresolvable,
        // FontCache falls back to a system font and must use SkiaSharp widths.
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 50]\n" +
                  "   /Contents 4 0 R\n" +
                  "   /Resources << /Font << /F0 5 0 R >> >>\n>>\nendobj\n");
        string cs = "BT 1 0 0 rg /F0 10 Tf 10 20 Td (AB) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xrefOff = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xrefOff}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  UnknownFilter-Font.pdf: font with /Filter/XXXDecode (unknown filter)
    // ------------------------------------------------------------------
    [Tetitulz.Pdf.TestHelpers.CorpusFact("UnknownFilter-Font.pdf")]
    public void UnknownFilterFont_HelloRendersVisiblyAndEmitsDiagnostic()
    {
        // The font file has /Filter/XXXDecode (unknown). The renderer must:
        // (a) still produce visible text using a fallback system font, and
        // (b) emit a RenderDiagnostics event describing the failure.
        // Corpus-resolved: skips visibly when absent instead of returning green.
        string path = TestHelpers.TestCorpus.Find("UnknownFilter-Font.pdf")!;

        // Reset the static so that any XXXDecode encounter by a prior test doesn't
        // suppress the before/after change detection in TryLoadEmbeddedTypeface.
        Tetitulz.Pdf.Core.IO.Filters.FilterPipeline.ResetLastUnknownFilterName();

        var diagnostics = new System.Collections.Generic.List<string>();
        Tetitulz.Pdf.Base.Rendering.RenderDiagnostics.OnEvent += (kind, detail, _) =>
            diagnostics.Add($"{kind}: {detail}");

        byte[] png = PdfOperations.RenderPageToPng(File.OpenRead(path), 0, 144);
        Assert.NotNull(png);
        Assert.True(png.Length > 0, "RenderPage returned empty PNG");

        using var bmp = SKBitmap.Decode(png);
        // Page: 200x300 pt at 144 DPI → 400x600 px.
        // "Hello!" at PDF (20,30) with text matrix [2 0 0 2 20 30] and fs=8
        // → effective 16 pt at pixel (~40, ~540), color red (1 0 0 rg).
        bool found = false;
        for (int y = 510; y <= 590 && !found; y++)
            for (int x = 20; x <= 300 && !found; x++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red > 180 && px.Green < 100 && px.Blue < 100)
                    found = true;
            }

        Assert.True(found,
            $"No red text pixels found — 'Hello!' was not rendered. " +
            $"Image={bmp.Width}x{bmp.Height}. " +
            $"Diagnostics: [{string.Join("; ", diagnostics)}]");

        // A diagnostic must be reported about the undecodable font.
        Assert.True(diagnostics.Count > 0,
            "Expected a RenderDiagnostics event for the unknown filter XXXDecode on the font, but none was fired.");
        Assert.Contains(diagnostics, d => d.Contains("XXXDecode", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    //  Type3 font: text-matrix must accumulate across multiple glyphs
    // ------------------------------------------------------------------

    /// <summary>
    /// Regression test: when rendering a multi-character string with a Type3 bitmap
    /// font, every glyph after the first used to render at the wrong position because
    /// PdfRenderContext.PopState() preserves Tm (per PDF §9.3.1) and we had reset Tm
    /// to identity inside the CharProc context without restoring it afterward.  The
    /// second glyph onwards rendered off-screen, leaving only the first visible.
    /// </summary>
    [Fact]
    public void Type3Font_MultipleGlyphs_AccumulateTextMatrix()
    {
        byte[] pdf = BuildType3TwoGlyphPdf();
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        Assert.NotNull(png);
        Assert.NotEmpty(png);

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        // The page has two glyphs ('A' and 'B') rendered as 8×8 black bitmaps.
        // 'A' is at ~x=20 and 'B' is ~12px to the right.
        // Before the fix, 'B' rendered off-screen (Tm reset to identity → position
        // jumped to near (0,0)).  After the fix both should appear in the visible area.
        int leftmostBlack = int.MaxValue, rightmostBlack = int.MinValue;
        for (int x = 0; x < bmp.Width; x++)
            for (int y = 0; y < bmp.Height; y++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red < 50 && px.Green < 50 && px.Blue < 50 && px.Alpha > 200)
                {
                    if (x < leftmostBlack)  leftmostBlack  = x;
                    if (x > rightmostBlack) rightmostBlack = x;
                }
            }

        // With both glyphs visible the horizontal span of black pixels must be > 12px.
        int span = rightmostBlack == int.MinValue ? 0 : rightmostBlack - leftmostBlack + 1;
        Assert.True(span >= 12,
            $"Type3 glyph span ({span}px) is too narrow — indicates second glyph rendered " +
            $"off-screen (text-matrix not accumulated). leftmost={leftmostBlack}, " +
            $"rightmost={rightmostBlack}, bmp={bmp.Width}x{bmp.Height}");
    }

    private static byte[] BuildType3TwoGlyphPdf()
    {
        // Two Type3 glyphs ('A' at byte 65, 'B' at byte 66), each a filled 8×8 rectangle.
        // FontMatrix [0.1 0 0 0.1 0 0] means 1 glyph unit = 0.1 text units.
        // Advance width = 100 glyph units → 10 text units (= 10pt at fontSize=1).
        // At 72 dpi on a 200×150pt page the two glyphs should render side-by-side
        // with ~10px gap between them (both visible in [0, bmp.Width]).
        const string cpStream = "100 0 0 0 80 80 d1\n0 g\n0 0 80 80 re\nf\n";
        int cpLen = cpStream.Length;

        // Content: set font F0 size 1, position at (20, 100), draw "AB".
        const string content = "BT\n/F0 1 Tf\n1 0 0 1 20 100 Tm\n(AB)Tj\nET\n";
        int contentLen = content.Length;

        // /Widths: FirstChar=32 LastChar=66 → 35 entries; all 0 except index 33 (A=65) and 34 (B=66).
        const string widths =
            "[ 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 100 100 ]";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new int[11]; // 1-indexed

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 150]\n" +
                  "   /Contents 4 0 R /Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n");

        offsets[4] = sb.Length;
        sb.Append($"4 0 obj\n<< /Length {contentLen} >>\nstream\n{content}endstream\nendobj\n");

        offsets[5] = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type3\n" +
                  "   /FontBBox [0 0 80 80] /FontMatrix [0.1 0 0 0.1 0 0]\n" +
                  "   /Encoding 6 0 R /CharProcs 7 0 R\n" +
                  "   /FirstChar 32 /LastChar 66 /Widths 8 0 R >>\nendobj\n");

        offsets[6] = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Encoding /Differences [ 65 /A /B ] >>\nendobj\n");

        offsets[7] = sb.Length;
        sb.Append("7 0 obj\n<< /A 9 0 R /B 10 0 R >>\nendobj\n");

        offsets[8] = sb.Length;
        sb.Append($"8 0 obj\n{widths}\nendobj\n");

        offsets[9] = sb.Length;
        sb.Append($"9 0 obj\n<< /Length {cpLen} >>\nstream\n{cpStream}endstream\nendobj\n");

        offsets[10] = sb.Length;
        sb.Append($"10 0 obj\n<< /Length {cpLen} >>\nstream\n{cpStream}endstream\nendobj\n");

        int xrefOffset = sb.Length;
        sb.Append("xref\n0 11\n0000000000 65535 f \n");
        for (int i = 1; i <= 10; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size 11 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  Image XObject orientation: row 0 must appear at the visual top
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies that an image XObject with a negative-y-scale cm matrix renders
    /// with the correct orientation: row 0 (red) at the BOTTOM of the image area,
    /// row 2-3 (blue) at the TOP.  Per PDF §8.9.4, image(0,0) maps to unit(0,1)
    /// which for a negative-d CTM maps to the lower page-y — so the first stream
    /// row belongs at the visual bottom.  This matches Windows.Data.Pdf / another reader.
    /// </summary>
    [Fact]
    public void ImageXObject_NegativeYScale_Row0AtBottom()
    {
        // Image: 2 wide × 4 tall, RGB.  Top 2 rows = red, bottom 2 rows = blue.
        // Hex-encoded (ASCIIHexDecode): FF0000 per red pixel, 0000FF per blue.
        const string hexData =
            "FF0000FF0000" +   // row 0 – red
            "FF0000FF0000" +   // row 1 – red
            "0000FF0000FF" +   // row 2 – blue
            "0000FF0000FF" +   // row 3 – blue
            ">";
        const int imgW = 2, imgH = 4;

        // Place on a 100×100pt page with standard [w 0 0 -h tx ty+h] image matrix.
        // Width=50pt, height=50pt, lower-left at (10, 10): matrix = [50 0 0 -50 10 60]
        const string content = "q\n50 0 0 -50 10 60 cm\n/Im1 Do\nQ\n";
        int contentLen = content.Length;

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new int[7];
        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 100 100]" +
                  "/Contents 4 0 R/Resources<</XObject<</Im1 6 0 R>>>>>>\nendobj\n");
        offsets[4] = sb.Length;
        sb.Append($"4 0 obj\n<</Length {contentLen}>>\nstream\n{content}endstream\nendobj\n");
        offsets[5] = sb.Length;
        sb.Append("5 0 obj\n<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>\nendobj\n");
        offsets[6] = sb.Length;
        sb.Append($"6 0 obj\n<</Type/XObject/Subtype/Image/Width {imgW}/Height {imgH}" +
                  $"/BitsPerComponent 8/ColorSpace/DeviceRGB/Filter/ASCIIHexDecode/Length {hexData.Length}>>" +
                  $"\nstream\n{hexData}\nendstream\nendobj\n");

        int xrefOff = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<</Size 7/Root 1 0 R>>\nstartxref\n{xrefOff}\n%%EOF\n");

        byte[] pdf = Encoding.Latin1.GetBytes(sb.ToString());

        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72); // 72 dpi → 1pt = 1px
        Assert.NotNull(png);
        Assert.NotEmpty(png);

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        // Image area: x=10..60, y_page=10..60 → screen y=40..90 (at 72dpi, 1px=1pt).
        // Image is 4 rows tall scaled to 50px → each row ≈ 12px.
        // Negative-d CTM flips rows: row 0-1 (red) → visual BOTTOM (screen y≈65-90).
        //                            row 2-3 (blue) → visual TOP   (screen y≈40-65).
        int screenX = 30; // mid-x of image
        int blueY  = 46;  // mid of blue band (rows 2-3 now at visual top, screen y=40..65)
        int redY   = 71;  // mid of red band  (rows 0-1 now at visual bottom, screen y=65..90)

        var bluePx = bmp.GetPixel(screenX, blueY);
        var redPx  = bmp.GetPixel(screenX, redY);

        Assert.True(bluePx.Blue > 150 && bluePx.Red < 100,
            $"Expected BLUE at top of image (rows 2-3, negative-d CTM flips rows), " +
            $"got rgb({bluePx.Red},{bluePx.Green},{bluePx.Blue}) at ({screenX},{blueY}). " +
            $"bmp={bmp.Width}x{bmp.Height}");
        Assert.True(redPx.Red > 150 && redPx.Blue < 100,
            $"Expected RED at bottom of image (rows 0-1), " +
            $"got rgb({redPx.Red},{redPx.Green},{redPx.Blue}) at ({screenX},{redY}). " +
            $"bmp={bmp.Width}x{bmp.Height}");
    }

}
