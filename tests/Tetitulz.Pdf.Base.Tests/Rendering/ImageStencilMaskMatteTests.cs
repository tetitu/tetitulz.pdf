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
/// Tests for two image-masking cases:
///   • <c>/Mask</c> as a separate 1-bit STENCIL image stream (PDF §8.9.6.3) — ignored, the
///     masked-out areas would render as an opaque box instead of transparent.
///   • <c>/SMask</c> with a <c>/Matte</c> backdrop (PDF §11.6.5.2) — the pre-blended base
///     samples are un-premultiplied, or a matte-colour halo is left.
/// </summary>
public class ImageStencilMaskMatteTests
{
    private sealed class ObjPdf
    {
        private readonly List<byte> _acc = new();
        private readonly Dictionary<int, int> _off = new();
        public ObjPdf() => Emit("%PDF-1.4\n");
        public void Emit(string s) => _acc.AddRange(Encoding.Latin1.GetBytes(s));
        public void Bytes(byte[] b) => _acc.AddRange(b);
        public void Obj(int n, string dict, byte[] streamData = null)
        {
            _off[n] = _acc.Count;
            Emit($"{n} 0 obj\n{dict}");
            if (streamData != null) { Emit("\nstream\n"); Bytes(streamData); Emit("\nendstream"); }
            Emit("\nendobj\n");
        }
        public byte[] Finish(int objCount, int root = 1)
        {
            int xref = _acc.Count;
            Emit($"xref\n0 {objCount}\n0000000000 65535 f \n");
            for (int i = 1; i < objCount; i++)
                Emit(_off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
            Emit($"trailer\n<< /Size {objCount} /Root {root} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return _acc.ToArray();
        }
    }

    [Fact]
    public void StencilMaskStream_MaskedAreasShowPageBackground()
    {
        // 2×1 RGB image: red | blue.  Stencil /Mask (2×1, 1bpc): bit pattern 0b10000000 →
        // pixel 0 sample=1 (masked/transparent), pixel 1 sample=0 (painted).
        var p = new ObjPdf();
        p.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        p.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        p.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                 "/Resources << /XObject << /Img 5 0 R >> >> /Contents 4 0 R >>");
        string cs = "q 72 0 0 72 0 0 cm /Img Do Q";
        p.Obj(4, $"<< /Length {cs.Length} >>", Encoding.Latin1.GetBytes(cs));
        p.Obj(5, "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB " +
                 "/BitsPerComponent 8 /Mask 6 0 R /Length 6 >>", new byte[] { 255, 0, 0, 0, 0, 255 });
        p.Obj(6, "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ImageMask true " +
                 "/BitsPerComponent 1 /Length 1 >>", new byte[] { 0x80 });
        byte[] pdf = p.Finish(7);

        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        int midY = bmp.Height / 2;
        SKColor left  = bmp.GetPixel(bmp.Width / 4, midY);     // masked → page background
        SKColor right = bmp.GetPixel(3 * bmp.Width / 4, midY); // painted blue

        Assert.True(left.Red > 200 && left.Green > 200 && left.Blue > 200,
            $"masked area should show white page bg, got {left}");
        Assert.True(right.Blue > 200 && right.Red < 100,
            $"painted area should be blue, got {right}");
    }

    [Fact]
    public void SMaskMatte_RecoversTrueColor_NotPreblended()
    {
        // True colour red(255,0,0), matte BLACK, alpha 0.5 → stored pre-blended (128,0,0).
        // Correct composite on a white page recovers a saturated red (R≈255); without the
        // /Matte un-premultiply the stored value is darkened to ~191.
        var p = new ObjPdf();
        p.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        p.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        p.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                 "/Resources << /XObject << /Img 5 0 R >> >> /Contents 4 0 R >>");
        string cs = "q 72 0 0 72 0 0 cm /Img Do Q";
        p.Obj(4, $"<< /Length {cs.Length} >>", Encoding.Latin1.GetBytes(cs));
        p.Obj(5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB " +
                 "/BitsPerComponent 8 /SMask 6 0 R /Length 3 >>", new byte[] { 128, 0, 0 });
        p.Obj(6, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
                 "/BitsPerComponent 8 /Matte [0 0 0] /Length 1 >>", new byte[] { 128 });
        byte[] pdf = p.Finish(7);

        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        SKColor c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

        // With /Matte applied the recovered red composites to R≈255; without it, ~191.
        Assert.True(c.Red > 220, $"matte-recovered red too dark (R={c.Red}); /Matte not applied?");
        Assert.True(c.Green < 170 && c.Blue < 170, $"expected a red-dominant pixel, got {c}");
    }
}
