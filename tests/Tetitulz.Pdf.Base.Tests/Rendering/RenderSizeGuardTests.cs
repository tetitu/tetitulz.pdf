// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Regression tests for the "big canvas" size guards: a malformed /MediaBox or a
/// crafted image XObject with absurd /Width /Height must not force a multi-GB
/// allocation.  Page bitmaps are bounded per-axis (16384) and by total area
/// (<see cref="PdfPageRenderer.MaxPixelArea"/>); image decodes share the same caps.
/// Also covers the degenerate Type 3 /FontMatrix guard.
/// </summary>
public class RenderSizeGuardTests
{
    // ------------------------------------------------------------------
    //  ClampRenderDimensions — the area-cap math (no allocation)
    // ------------------------------------------------------------------

    [Fact]
    public void Clamp_SquareHuge_BoundsBothAxesAndTotalArea()
    {
        int w = 200_000, h = 200_000;
        float scale = 1f;
        PdfPageRenderer.ClampRenderDimensions(ref w, ref h, ref scale);

        Assert.True(w <= PdfPageRenderer.MaxPixelDimension, $"width {w} exceeds axis cap");
        Assert.True(h <= PdfPageRenderer.MaxPixelDimension, $"height {h} exceeds axis cap");
        // Area must be within the cap (allow a 1-pixel rounding margin per axis).
        Assert.True((long)w * h <= PdfPageRenderer.MaxPixelArea + w + h,
            $"area {(long)w * h} exceeds area cap {PdfPageRenderer.MaxPixelArea}");
        // Aspect ratio (1:1) preserved.
        Assert.True(Math.Abs(w - h) <= 1, $"aspect not preserved: {w}x{h}");
        Assert.True(scale < 1f, "scale should have been reduced");
    }

    [Fact]
    public void Clamp_SingleAxisHuge_ClampsThatAxisOnly()
    {
        int w = 200_000, h = 100;
        float scale = 1f;
        PdfPageRenderer.ClampRenderDimensions(ref w, ref h, ref scale);

        Assert.Equal(PdfPageRenderer.MaxPixelDimension, w);   // long axis pinned to cap
        Assert.True(h >= 1, "short axis must stay at least 1px");
        Assert.True((long)w * h <= PdfPageRenderer.MaxPixelArea, "area should be well under cap");
    }

    [Fact]
    public void Clamp_NormalSize_LeftUnchanged()
    {
        int w = 1275, h = 1650;   // US Letter @ 150 DPI
        float scale = 150f / 72f;
        float original = scale;
        PdfPageRenderer.ClampRenderDimensions(ref w, ref h, ref scale);

        Assert.Equal(1275, w);
        Assert.Equal(1650, h);
        Assert.Equal(original, scale);
    }

    // ------------------------------------------------------------------
    //  End-to-end: absurd /MediaBox is bounded, not OOM
    // ------------------------------------------------------------------

    [Fact]
    public void RenderPage_AbsurdMediaBox_ProducesBoundedBitmap()
    {
        // 200000 x 5 pt page: unguarded, a 200000-wide bitmap.
        byte[] pdf = BuildBlankPagePdf(mediaBox: "[0 0 200000 5]");
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, dpi: 72);

        var (pw, ph) = PngSize(png);
        Assert.True(pw >= 1 && pw <= PdfPageRenderer.MaxPixelDimension, $"width {pw} not bounded");
        Assert.True(ph >= 1 && ph <= PdfPageRenderer.MaxPixelDimension, $"height {ph} not bounded");
        Assert.True((long)pw * ph <= PdfPageRenderer.MaxPixelArea + pw + ph, "area not bounded");
    }

    // ------------------------------------------------------------------
    //  End-to-end: crafted huge image XObject is skipped, page still renders
    // ------------------------------------------------------------------

    [Fact]
    public void RenderPage_HugeImageXObject_SkippedWithDiagnostic_NoOom()
    {
        // 50000 x 50000 declared image (2.5 billion px) with a tiny data stream.
        // Unguarded: new SKColor[2_500_000_000] / SKBitmap(50000,50000) -> OOM / overflow.
        byte[] pdf = BuildHugeImagePdf(width: 50000, height: 50000);

        var events = new List<(RenderDiagnosticKind kind, string detail)>();
        Action<RenderDiagnosticKind, string, Exception> handler =
            (k, d, _) => { lock (events) events.Add((k, d)); };
        RenderDiagnostics.OnEvent += handler;
        byte[] png;
        try
        {
            png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, dpi: 72);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= handler;
        }

        // Page renders at its natural (small) size — the oversized image is dropped.
        var (pw, ph) = PngSize(png);
        Assert.True(pw <= PdfPageRenderer.MaxPixelDimension && ph <= PdfPageRenderer.MaxPixelDimension);
        Assert.Contains(events, e =>
            e.kind == RenderDiagnosticKind.ImageDecodeFailed &&
            e.detail != null && e.detail.Contains("exceeds the render size limit"));
    }

    // ------------------------------------------------------------------
    //  End-to-end: degenerate Type 3 /FontMatrix does not crash the render
    // ------------------------------------------------------------------

    [Fact]
    public void RenderPage_NonFiniteType3FontMatrix_DoesNotThrow()
    {
        // /FontMatrix [1e40 0 0 1e40 0 0] parses to +Infinity floats; unguarded, the
        // resulting advance (Infinity) would poison the text matrix. The guard resets to
        // the default 0.001 glyph space.  This must complete and return a valid bitmap.
        byte[] pdf = BuildType3Pdf(fontMatrix: "[1e40 0 0 1e40 0 0]");
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, dpi: 72);

        using var data = SKData.CreateCopy(png);
        using var bmp = SKBitmap.Decode(data);
        Assert.NotNull(bmp);
        Assert.True(bmp.Width > 0 && bmp.Height > 0);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static (int w, int h) PngSize(byte[] png)
    {
        // IHDR width/height are big-endian uint32 at byte offsets 16 and 20.
        Assert.True(png.Length >= 24, "not a PNG");
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return (w, h);
    }

    private static byte[] BuildBlankPagePdf(string mediaBox)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Resources << >> >>");
        return b.Finish(rootObj: 1);
    }

    private static byte[] BuildHugeImagePdf(int width, int height)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72]\n" +
                 "   /Resources << /XObject << /Img 5 0 R >> >> /Contents 4 0 R >>");
        string cs = "q 72 0 0 72 0 0 cm /Img Do Q";
        b.StreamObj(4, $"<< /Length {cs.Length} >>", Encoding.Latin1.GetBytes(cs));
        // Tiny data stream — far smaller than width*height would require.
        byte[] imgData = { 0, 0, 0, 255, 255, 255, 0, 0, 0, 255, 255, 255 };
        b.StreamObj(5,
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height}" +
            $" /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length {imgData.Length} >>",
            imgData);
        return b.Finish(rootObj: 1);
    }

    private static byte[] BuildType3Pdf(string fontMatrix)
    {
        var b = new PdfBuilder();
        b.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        b.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        b.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]\n" +
                 "   /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>");
        string cs = "BT /F1 24 Tf 20 100 Td (AA) Tj ET";
        b.StreamObj(4, $"<< /Length {cs.Length} >>", Encoding.Latin1.GetBytes(cs));
        b.Obj(5, "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 750 750]\n" +
                 $"   /FontMatrix {fontMatrix} /CharProcs 6 0 R\n" +
                 "   /Encoding << /Type /Encoding /Differences [65 /a] >>\n" +
                 "   /FirstChar 65 /LastChar 65 /Widths [500] >>");
        b.Obj(6, "<< /a 7 0 R >>");
        string glyph = "1000 0 0 0 750 750 d1 0 0 750 750 re f";
        b.StreamObj(7, $"<< /Length {glyph.Length} >>", Encoding.Latin1.GetBytes(glyph));
        return b.Finish(rootObj: 1);
    }

    /// <summary>Minimal PDF assembler that tracks object offsets and emits a valid xref.</summary>
    private sealed class PdfBuilder
    {
        private readonly List<byte> _acc = new();
        private readonly Dictionary<int, int> _offsets = new();
        private int _maxObj;

        public PdfBuilder() => Emit("%PDF-1.4\n");

        public void Obj(int num, string body)
        {
            _offsets[num] = _acc.Count;
            _maxObj = Math.Max(_maxObj, num);
            Emit($"{num} 0 obj\n{body}\nendobj\n");
        }

        public void StreamObj(int num, string dict, byte[] data)
        {
            _offsets[num] = _acc.Count;
            _maxObj = Math.Max(_maxObj, num);
            Emit($"{num} 0 obj\n{dict}\nstream\n");
            _acc.AddRange(data);
            Emit("\nendstream\nendobj\n");
        }

        public byte[] Finish(int rootObj)
        {
            int xref = _acc.Count;
            int count = _maxObj + 1;
            Emit($"xref\n0 {count}\n0000000000 65535 f \n");
            for (int i = 1; i < count; i++)
                Emit(_offsets.TryGetValue(i, out int off)
                    ? $"{off:D10} 00000 n \n"
                    : "0000000000 65535 f \n");
            Emit($"trailer\n<< /Size {count} /Root {rootObj} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return _acc.ToArray();
        }

        private void Emit(string s) => _acc.AddRange(Encoding.Latin1.GetBytes(s));
    }
}
