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
/// SVG-renderer parity (Phase 4): shading patterns as fill, pattern-filled text,
/// multi-stop gradient sampling, and accumulated text-clip (modes 4-7).
/// </summary>
public class SvgPhase4Tests
{
    private static string RenderSvg(string content, string resources, string[] extraObjs)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string b) { off.Add(pdf.Length); pdf.Append(b); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << {resources} >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        foreach (var o in extraObjs) Obj(o);
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(pdf.ToString()));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private const string AxialShadingPattern =
        "5 0 obj\n<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB " +
        "/Coords [0 0 612 0] /Domain [0 1] " +
        "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> " +
        "/Extend [true true] >> >>\nendobj\n";

    [Fact]
    public void ShadingPatternFill_EmitsLinearGradient()
    {
        string svg = RenderSvg("/Pattern cs /P1 scn 0 0 612 792 re f",
            "/Pattern << /P1 5 0 R >>", new[] { AxialShadingPattern });
        Assert.Contains("linearGradient", svg);
        Assert.Contains("fill=\"url(#sg", svg);
    }

    [Fact]
    public void ShadingPattern_StitchingFunction_EmitsMultipleStops()
    {
        // FunctionType 3 stitch → must sample many stops (not 2).
        string pat = "5 0 obj\n<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB " +
            "/Coords [0 0 612 0] /Domain [0 1] /Function << /FunctionType 3 /Domain [0 1] /Bounds [0.5] " +
            "/Encode [0 1 0 1] /Functions [" +
            "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 1 0] /N 1 >> " +
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 1 0] /C1 [0 0 1] /N 1 >>] >> /Extend [true true] >> >>\nendobj\n";
        string svg = RenderSvg("/Pattern cs /P1 scn 0 0 612 792 re f",
            "/Pattern << /P1 5 0 R >>", new[] { pat });
        int stops = Regex.Matches(svg, "<stop ").Count;
        Assert.True(stops >= 3, $"expected multi-stop gradient, got {stops} <stop> elements");
    }

    [Fact]
    public void PatternFilledText_EmitsUrlFill()
    {
        string svg = RenderSvg("/Pattern cs /P1 scn BT /F1 24 Tf 100 700 Td (HELLO) Tj ET",
            "/Font << /F1 6 0 R >> /Pattern << /P1 5 0 R >>",
            new[] { AxialShadingPattern, "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" });
        // The <text> element's fill must reference the gradient, not a flat colour.
        Assert.Matches(@"<text[^>]*fill=""url\(#sg", svg);
    }

    [Fact]
    public void ImageSMask_BakedIntoPngAlpha()
    {
        // 2×1 RGB image (red|blue) + 2×1 gray /SMask (0|255): left transparent, right opaque.
        // SVG export must emit a PNG data-URI whose decoded alpha is <255 somewhere.
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
        Obj(5, "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB " +
               "/BitsPerComponent 8 /SMask 6 0 R /Length 6 >>", new byte[] { 255, 0, 0, 0, 0, 255 });
        Obj(6, "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray " +
               "/BitsPerComponent 8 /Length 2 >>", new byte[] { 0, 255 });
        int xref = acc.Count;
        Emit($"xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++) Emit(off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        using var ms = new MemoryStream(acc.ToArray());
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        var m = Regex.Match(svg, @"data:image/png;base64,([A-Za-z0-9+/=]+)");
        Assert.True(m.Success, "expected a PNG data-URI (SMask should defeat JPEG/opaque passthrough)");
        byte[] png = System.Convert.FromBase64String(m.Groups[1].Value);
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        bool anyTransparent = false;
        for (int x = 0; x < bmp.Width && !anyTransparent; x++)
            for (int y = 0; y < bmp.Height; y++)
                if (bmp.GetPixel(x, y).Alpha < 200) { anyTransparent = true; break; }
        Assert.True(anyTransparent, "SMask alpha was not baked into the PNG (image opaque)");
    }

    [Fact]
    public void ImageColorKeyMask_BakedIntoPngAlpha()
    {
        // 1×1 red image with /Mask [200 255 0 60 0 60] → the red pixel is colour-key masked,
        // so the emitted PNG must be transparent there.
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
        Obj(5, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB " +
               "/BitsPerComponent 8 /Mask [200 255 0 60 0 60] /Length 3 >>", new byte[] { 255, 0, 0 });
        int xref = acc.Count;
        Emit("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Emit(off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        using var ms = new MemoryStream(acc.ToArray());
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        var m = Regex.Match(svg, @"data:image/png;base64,([A-Za-z0-9+/=]+)");
        Assert.True(m.Success, "expected a PNG data-URI (color-key /Mask defeats opaque passthrough)");
        byte[] png = System.Convert.FromBase64String(m.Groups[1].Value);
        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        Assert.True(bmp.GetPixel(0, 0).Alpha < 200, "color-key-masked red pixel should be transparent");
    }

    [Fact]
    public void TextClipMode_AccumulatesSingleClipPath()
    {
        // Two clip-mode (Tr 4) runs in one BT..ET, then a fill. One union clipPath must wrap
        // the subsequent fill, containing BOTH glyph runs.
        string svg = RenderSvg(
            "BT /F1 24 Tf 4 Tr 100 700 Td (AAA) Tj 0 -30 Td (BBB) Tj ET 1 0 0 rg 0 0 612 792 re f",
            "/Font << /F1 6 0 R >>",
            new[] { "6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n" });
        int clipPaths = Regex.Matches(svg, "id=\"txtclip").Count;
        Assert.Equal(1, clipPaths);                       // a single accumulated clipPath
        Assert.Contains("AAA", svg);
        Assert.Contains("BBB", svg);
        Assert.Contains("<g clip-path=\"url(#txtclip", svg); // clip group wraps following content
    }
}
