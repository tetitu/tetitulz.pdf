// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An inline image whose <c>/ColorSpace</c> names a resource (ISO 32000-2 §8.9.7) is coloured by
/// that resource (<see cref="InlineImageColorSpace"/>), in both renderers; and the raster inline
/// path shares the image XObject's decode and draw — so a plate reads its samples, the colour key
/// reads the samples painted, and an overprinting image is reported. The reference renderer is the oracle
/// for the spot image: full tint (255,0,0), tint 128 (255,127,127).
/// </summary>
public class InlineImageColorSpaceTests
{
    private const string RedSpot = "[/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>]";
    private const string GreenSpot = "[/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 1 0] /N 1 >>]";
    private const string LinearCalRgb = "[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [1 1 1] /Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505] >>]";

    // Two samples, 255 and 128, stretched over the 32×16 page.
    private static string Inline(string cs, string extra = "", string data = "ÿ\u0080", int w = 2) =>
        $"q 32 0 0 16 0 0 cm BI /W {w} /H 1 /BPC 8 /CS {cs}{extra} ID {data} EI Q";

    /// <summary><c>BI /CS /CS0</c> with CS0 a spot ink: painted by its tint transform, as the reference renderer paints it, in both renderers.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AResourceNamedSpace_ColoursTheInlineImage(bool raster)
    {
        byte[] pdf = Pdf(Inline("/CS0"), $"/ColorSpace << /CS0 {RedSpot} >>");
        AssertNear((255, 0, 0), Pixel(pdf, 8, 8, raster), "full tint");
        AssertNear((255, 127, 127), Pixel(pdf, 24, 8, raster), "tint 128");
    }

    /// <summary>
    /// An <c>/Indexed</c> space's base may be a resource name too: a linear CalRGB base makes the
    /// palette's 128 grey ~188, where a device base (the control) paints 128.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnIndexedSpacesNamedBase_IsResolved(bool raster)
    {
        string res = $"/ColorSpace << /CS1 {LinearCalRgb} >>";
        var named = Pixel(Pdf(Inline("[/I /CS1 0 <808080>]", data: "\u0000", w: 1), res), 8, 8, raster);
        var device = Pixel(Pdf(Inline("[/I /RGB 0 <808080>]", data: "\u0000", w: 1), res), 8, 8, raster);
        Assert.InRange(named.Item1, 180, 195);
        AssertNear((128, 128, 128), device, "device base");
    }

    /// <summary>The reserved names are never looked up (§8.6.3): a resource called /DeviceRGB does not turn <c>/CS /RGB</c> into a spot ink.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReservedName_IsNotShadowedByAResource(bool raster)
    {
        byte[] pdf = Pdf(Inline("/RGB", data: "\u0000\u0000ÿ", w: 1), $"/ColorSpace << /DeviceRGB {RedSpot} >>");
        AssertNear((0, 0, 255), Pixel(pdf, 8, 8, raster), "device blue");
    }

    /// <summary>
    /// The parsed operand is not changed: two pages share one content stream, each binding CS0 to
    /// a different spot — the second page, rendered after the first in one session, paints its own.
    /// </summary>
    [Fact]
    public void TheSameStream_UnderDifferentResources_PaintsEachPagesOwn()
    {
        byte[] pdf = TwoPagesSharingContent(Inline("/CS0"), $"/ColorSpace << /CS0 {RedSpot} >>", $"/ColorSpace << /CS0 {GreenSpot} >>");
        using var session = PdfRenderSession.Open(new MemoryStream(pdf));
        RenderedPage first = session.RenderPageToPixels(0, 72), second = session.RenderPageToPixels(1, 72);
        AssertNear((255, 0, 0), At(first, 8, 8), "page 1");
        AssertNear((0, 255, 0), At(second, 8, 8), "page 2");
    }

    /// <summary>On the spot plate an inline spot image's samples are the ink, exactly — not its picture through the plate's filter, reported as an approximation.</summary>
    [Fact]
    public void AnInlineSpotImage_IsReadFromItsSamples_OnTheSpotPlate()
    {
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new MemoryStream(Pdf(Inline("/CS0"), $"/ColorSpace << /CS0 {RedSpot} >>")), 0, 72);
        PdfSeparationPlate spot = s.Plates.Single(p => p.Colorant == "Spot");
        Assert.Equal(0, spot.ApproximatedPaints);
        Assert.InRange(spot.Tints[8 * s.Width + 8], 254, 255);
        Assert.InRange(spot.Tints[8 * s.Width + 24], 127, 129);
    }

    /// <summary>
    /// An overprinting inline image in a space naming one process ink is reported as an image XObject
    /// is, in both renderers (§8.6.7); without <c>op</c> (the control) nothing is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOverprintingInlineImage_IsReported(bool op)
    {
        string cyan = "[/Separation /Cyan /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>]";
        byte[] pdf = Pdf("/GS1 gs " + Inline("/CS0"),
            $"/ColorSpace << /CS0 {cyan} >> /ExtGState << /GS1 << /op {(op ? "true" : "false")} /OP {(op ? "true" : "false")} >> >>");
        var raster = Listen(() => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72));
        var svg = Listen(() => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
        Assert.Equal(op, raster.Exists(r => r.Contains("an image in a /Separation or /DeviceN space", StringComparison.Ordinal)));
        Assert.Equal(op, svg.Exists(r => r.Contains("an image in a /Separation or /DeviceN space", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The colour key compares the samples painted (§8.9.6.4) — for an inline JPEG, its decoded
    /// samples, not its codestream: <c>/Mask [255 255]</c> clears the full-tint half and keeps the 128 half.
    /// </summary>
    [Fact]
    public void AnInlineJpegsColourKey_ReadsItsDecodedSamples()
    {
        string jpeg = Encoding.Latin1.GetString(GreyJpeg());
        byte[] pdf = Pdf($"q 32 0 0 16 0 0 cm BI /W 32 /H 16 /BPC 8 /CS /CS0 /F /DCT /Mask [255 255] ID {jpeg} EI Q",
            $"/ColorSpace << /CS0 {RedSpot} >>");
        AssertNear((255, 255, 255), Pixel(pdf, 8, 8, raster: true), "keyed out: the white page");
        AssertNear((255, 127, 127), Pixel(pdf, 24, 8, raster: true), "kept");
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] GreyJpeg()
    {
        using var bmp = new SKBitmap(new SKImageInfo(32, 16, SKColorType.Gray8, SKAlphaType.Opaque));
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 32; x++) { byte v = x < 16 ? (byte)255 : (byte)128; bmp.SetPixel(x, y, new SKColor(v, v, v)); }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }

    private static List<string> Listen(Action act)
    {
        var reasons = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (kind == RenderDiagnosticKind.OverprintNotSimulated && Environment.CurrentManagedThreadId == own)
                lock (reasons) reasons.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        try { act(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return reasons;
    }

    private static (byte, byte, byte) Pixel(byte[] pdf, int x, int y, bool raster)
    {
        if (raster) return At(PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72), x, y);
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        using SKBitmap png = JpxTestPdf.DecodeFirstEmbeddedPng(svg);
        // The PNG is the image at its own size, stretched over the 32-wide page.
        SKColor c = png.GetPixel(x * png.Width / 32, 0);
        return (c.Red, c.Green, c.Blue);
    }

    private static (byte, byte, byte) At(RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
    }

    private static void AssertNear((int, int, int) expected, (int, int, int) actual, string what) =>
        Assert.True(Math.Abs(expected.Item1 - actual.Item1) <= 1 && Math.Abs(expected.Item2 - actual.Item2) <= 1
                    && Math.Abs(expected.Item3 - actual.Item3) <= 1, $"{what}: {actual}, expected {expected}");

    private static byte[] Pdf(string content, string resources) => Build(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 32 16] /Resources << " + resources + " >> /Contents 4 0 R >>",
        "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
    });

    private static byte[] TwoPagesSharingContent(string content, string resources1, string resources2) => Build(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 32 16] /Resources << " + resources1 + " >> /Contents 4 0 R >>",
        "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 32 16] /Resources << " + resources2 + " >> /Contents 4 0 R >>",
    });

    private static byte[] Build(string[] objs)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++) { offs[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
