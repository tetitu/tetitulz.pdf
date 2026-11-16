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
/// Image colour-space / bit-depth decode tests (Phase 2): 16-bit <c>/Decode</c>,
/// Indexed <c>/Decode</c> + non-Device base, Separation tint transform, and Lab images.
/// </summary>
public class ImageColorSpaceTests
{
    // Minimal object-stream PDF builder (one image XObject filling a 72×72 page).
    private static byte[] BuildImagePdf(string imageDictBody, byte[] imageData,
        IEnumerable<(int num, string body, byte[] stream)> extraObjs = null)
    {
        var acc = new List<byte>();
        var off = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(int n, string dict, byte[] sd = null)
        {
            off[n] = acc.Count;
            Emit($"{n} 0 obj\n{dict}");
            if (sd != null) { Emit("\nstream\n"); acc.AddRange(sd); Emit("\nendstream"); }
            Emit("\nendobj\n");
        }
        Emit("%PDF-1.4\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
               "/Resources << /XObject << /Img 5 0 R >> >> /Contents 4 0 R >>");
        string cs = "q 72 0 0 72 0 0 cm /Img Do Q";
        Obj(4, $"<< /Length {cs.Length} >>", Encoding.Latin1.GetBytes(cs));
        Obj(5, imageDictBody + $" /Length {imageData.Length} >>", imageData);
        int maxObj = 5;
        if (extraObjs != null)
            foreach (var (num, body, stream) in extraObjs) { Obj(num, body, stream); if (num > maxObj) maxObj = num; }

        int xref = acc.Count;
        Emit($"xref\n0 {maxObj + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= maxObj; i++)
            Emit(off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {maxObj + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }

    private static SKBitmap Render(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    [Fact]
    public void Gray16Bit_DecodeInversion_Applied()
    {
        // 1×1 DeviceGray 16-bit white (0xFFFF) with /Decode [1 0] → inverted → black.
        byte[] pdf = BuildImagePdf(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
            "/BitsPerComponent 16 /Decode [1 0]", new byte[] { 0xFF, 0xFF });
        using var bmp = Render(pdf);
        SKColor c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.True(c.Red < 60, $"16-bit /Decode [1 0] should invert white→black, got {c}");
    }

    [Fact]
    public void SeparationImage_UsesTintTransform_NotGray()
    {
        // 2×1 Separation "Spot" image, alternate DeviceRGB, tint white→red.
        // Samples [0,255] → left white, right red (NOT gray).
        var tint = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>";
        string dict = "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 " +
                      "/ColorSpace [/Separation /Spot /DeviceRGB " + tint + "]";
        byte[] pdf = BuildImagePdf(dict, new byte[] { 0, 255 });
        using var bmp = Render(pdf);
        SKColor left  = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
        SKColor right = bmp.GetPixel(3 * bmp.Width / 4, bmp.Height / 2);
        Assert.True(left.Red > 200 && left.Green > 200 && left.Blue > 200, $"tint 0 → white, got {left}");
        Assert.True(right.Red > 200 && right.Green < 110 && right.Blue < 110, $"tint 1 → red, got {right}");
    }

    [Fact]
    public void LabImage_NotDecodedAsRgb()
    {
        // 1×1 Lab image with a reddish Lab value (L≈54, a≈+80, b≈+67) using default range
        // [-100 100]. Bytes: L=138, a=230, b=213. Must render red-dominant — decoding the
        // bytes as RGB would give a green/cyan pixel (G,B high).
        string dict = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 " +
                      "/ColorSpace [/Lab << /WhitePoint [0.9505 1.0 1.089] >>]";
        byte[] pdf = BuildImagePdf(dict, new byte[] { 138, 230, 213 });
        using var bmp = Render(pdf);
        SKColor c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.True(c.Red > c.Green && c.Red > c.Blue && c.Red > 120,
            $"Lab red should be red-dominant, got {c}");
    }

    [Fact]
    public void IndexedImage_DecodeRemapsIndices()
    {
        // Palette [black, red]; 1-bit 2×1 image bits [0,1]; /Decode [1 0] swaps index range
        // → sample0→index1(red), sample1→index0(black).
        byte[] palette = { 0, 0, 0, 255, 0, 0 };
        string dict = "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 1 " +
                      "/ColorSpace [/Indexed /DeviceRGB 1 6 0 R] /Decode [1 0]";
        // image data: 2 pixels, 1bpc, bits 0 then 1 → 0b01000000
        byte[] pdf = BuildImagePdf(dict, new byte[] { 0x40 },
            new (int, string, byte[])[] { (6, $"<< /Length {palette.Length} >>", palette) });
        using var bmp = Render(pdf);
        SKColor left  = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
        SKColor right = bmp.GetPixel(3 * bmp.Width / 4, bmp.Height / 2);
        Assert.True(left.Red > 200 && left.Green < 80, $"sample0 should map to red via /Decode, got {left}");
        Assert.True(right.Red < 80 && right.Green < 80 && right.Blue < 80, $"sample1 should map to black, got {right}");
    }
}
