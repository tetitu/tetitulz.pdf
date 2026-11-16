// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// SVG-renderer Indexed-colour-space image support. Before this fix
/// <c>PdfSvgRenderer.RasteriseImage</c> only handled DeviceRGB/Gray/CMYK and
/// returned null for an Indexed colour space, so Indexed images (e.g. an
/// Indexed/DeviceCMYK QR code on a credit-card statement) were silently dropped
/// from the SVG output. These tests render a minimal in-memory PDF and assert the
/// emitted PNG data-URI carries the palette colours.
/// </summary>
public class IndexedImageSvgTests
{
    private static SkiaSharp.SKBitmap RenderIndexedImage(string imgDict, byte[] imgData)
    {
        var acc = new List<byte>();
        var off = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(int n, string dict, byte[] sd = null)
        {
            off[n] = acc.Count; Emit($"{n} 0 obj\n{dict}");
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
        Obj(5, imgDict, imgData);
        int xref = acc.Count;
        Emit("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Emit(off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        using var ms = new MemoryStream(acc.ToArray());
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        var m = Regex.Match(svg, @"data:image/png;base64,([A-Za-z0-9+/=]+)");
        Assert.True(m.Success, "expected a PNG data-URI — Indexed image must not be dropped from SVG");
        byte[] png = System.Convert.FromBase64String(m.Groups[1].Value);
        var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static void AssertColor(SkiaSharp.SKColor c, byte r, byte g, byte b)
    {
        Assert.True(System.Math.Abs(c.Red - r) <= 4 && System.Math.Abs(c.Green - g) <= 4 &&
                    System.Math.Abs(c.Blue - b) <= 4, $"expected ~({r},{g},{b}) got ({c.Red},{c.Green},{c.Blue})");
    }

    [Fact]
    public void IndexedDeviceRgb_ResolvesPaletteColours()
    {
        // 2×1 image, palette [red, blue] (hex <FF0000 0000FF>), indices [0,1].
        using var bmp = RenderIndexedImage(
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 " +
            "/ColorSpace [/Indexed /DeviceRGB 1 <FF00000000FF>] /BitsPerComponent 8 /Length 2 >>",
            new byte[] { 0, 1 });
        AssertColor(bmp.GetPixel(0, 0), 255, 0, 0);
        AssertColor(bmp.GetPixel(1, 0), 0, 0, 255);
    }

    [Fact]
    public void IndexedDeviceCmyk_ResolvesPaletteColours()
    {
        // Mirrors the real QR (Indexed/DeviceCMYK). Palette [white(K0), black(K255)].
        using var bmp = RenderIndexedImage(
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 " +
            "/ColorSpace [/Indexed /DeviceCMYK 1 <00000000000000FF>] /BitsPerComponent 8 /Length 2 >>",
            new byte[] { 0, 1 });
        AssertColor(bmp.GetPixel(0, 0), 255, 255, 255);
        AssertColor(bmp.GetPixel(1, 0), 0, 0, 0);
    }

    [Fact]
    public void IndexedSubByte_4bpc_UnpacksTwoPixelsPerByte()
    {
        // 2×1 image at 4 bpc: one byte 0x01 packs index 0 (high nibble) then 1 (low nibble).
        // Palette <00FF00 0000FF> = [green, blue]: index 0 → green, index 1 → blue.
        using var bmp = RenderIndexedImage(
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 " +
            "/ColorSpace [/Indexed /DeviceRGB 1 <00FF000000FF>] /BitsPerComponent 4 /Length 1 >>",
            new byte[] { 0x01 });
        AssertColor(bmp.GetPixel(0, 0), 0, 255, 0);   // index 0 → green
        AssertColor(bmp.GetPixel(1, 0), 0, 0, 255);   // index 1 → blue
    }
}
