// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A soft-masked solid fill is now painted with the mask composed into the paint's shader instead
/// of through an offscreen layer. These pin the arithmetic, because the failure mode is a picture
/// that looks plausible and is wrong.
///
/// <para>
/// <b>The specific hazard.</b> Skia multiplies a shader's output by the paint's alpha. The
/// constant alpha <c>ca</c> is already baked into <c>paint.Color</c> by <c>ApplyAlpha</c>, so
/// moving the colour into a shader without clearing the paint's alpha applies <c>ca</c>
/// <i>twice</i> — 0.5 becomes 0.25. Nothing crashes; the fill is simply too pale, which is
/// indistinguishable from a rendering choice unless the expected value is computed.
/// </para>
/// </summary>
public class SoftMaskShaderEquivalenceTests
{
    private const int Dpi = 72;   // 1 pt == 1 px

    /// <summary>
    /// Red at <c>ca 0.5</c> under a fully opaque mask, over a white page: the result must be the
    /// midpoint, ~(255,128,128). Squared alpha would give ~(255,191,191) — a 63-level error this
    /// asserts against directly.
    /// </summary>
    [Fact]
    public void ConstantAlphaUnderAnOpaqueMask_IsAppliedExactlyOnce()
    {
        using SKBitmap bmp = Render(MaskedFillPdf(constantAlpha: "0.5", maskGrey: "1"));

        SKColor c = bmp.GetPixel(100, 100);

        Assert.True(c.Red > 245, $"red channel should stay saturated, was {c}");
        Assert.InRange(c.Green, 108, 148);   // 128 ± 20
        Assert.InRange(c.Blue, 108, 148);
    }

    /// <summary>
    /// The mirror case: full opacity under a 50% luminosity mask. Same expected result, reached
    /// through the mask rather than through <c>ca</c> — so it fails if the mask is applied twice,
    /// or not at all.
    /// </summary>
    [Fact]
    public void HalfLuminosityMask_AtFullOpacity_HalvesTheCoverage()
    {
        using SKBitmap bmp = Render(MaskedFillPdf(constantAlpha: "1", maskGrey: "0.5"));

        SKColor c = bmp.GetPixel(100, 100);

        Assert.True(c.Red > 245, $"red channel should stay saturated, was {c}");
        Assert.InRange(c.Green, 100, 155);
        Assert.InRange(c.Blue, 100, 155);
    }

    /// <summary>
    /// Both together: 0.5 × 0.5 = 0.25 coverage, so ~(255,191,191). This is the case that
    /// separates "applied once each" from "applied once in total".
    /// </summary>
    [Fact]
    public void ConstantAlphaAndMask_Multiply()
    {
        using SKBitmap bmp = Render(MaskedFillPdf(constantAlpha: "0.5", maskGrey: "0.5"));

        SKColor c = bmp.GetPixel(100, 100);

        Assert.InRange(c.Green, 171, 211);   // 191 ± 20
        Assert.InRange(c.Blue, 171, 211);
    }

    /// <summary>
    /// A fully transparent mask must erase the fill completely — the page stays white. Guards the
    /// opposite error from the ones above: a mask shader that never took effect.
    /// </summary>
    [Fact]
    public void BlackMask_ErasesTheFill()
    {
        using SKBitmap bmp = Render(MaskedFillPdf(constantAlpha: "1", maskGrey: "0"));

        SKColor c = bmp.GetPixel(100, 100);

        Assert.True(c.Red > 245 && c.Green > 245 && c.Blue > 245,
            $"a black luminosity mask should leave the page untouched, was {c}");
    }

    /// <summary>
    /// A non-SrcOver blend mode keeps the layer path, because an element blending against a
    /// transparent layer is not the same as blending against the page. Asserted through the
    /// rendered result rather than by inspecting which branch ran: Multiply of red over white
    /// leaves red, and of red over nothing does not.
    /// </summary>
    [Fact]
    public void NonSrcOverBlendMode_StillRendersThroughTheLayerPath()
    {
        using SKBitmap bmp = Render(MaskedFillPdf(constantAlpha: "1", maskGrey: "1", blendMode: "/Multiply"));

        SKColor c = bmp.GetPixel(100, 100);

        // Whatever path runs, a fully-opaque mask and Multiply over white must still show red.
        Assert.True(c.Red > 200, $"expected red to survive Multiply over white, was {c}");
    }


    // ------------------------------------------------------------------

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A 200x200 page: white background, then a red rectangle covering it, drawn under an
    /// ExtGState carrying constant alpha <paramref name="constantAlpha"/> and a luminosity soft
    /// mask whose group paints uniform grey <paramref name="maskGrey"/>.
    /// </summary>
    private static byte[] MaskedFillPdf(string constantAlpha, string maskGrey, string blendMode = null)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        // White page first so the composite has a known backdrop.
        string content = "1 1 1 rg 0 0 200 200 re f\n/GM gs\n1 0 0 rg 20 20 160 160 re f\n";
        string maskContent = $"{maskGrey} g 0 0 200 200 re f\n";
        string bm = blendMode != null ? $" /BM {blendMode}" : "";

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
             + "/Resources << /ExtGState << /GM 6 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5);
        Emit("<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
             + "/Group << /Type /Group /S /Transparency /CS /DeviceGray >> "
             + $"/Resources << >> /Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream\nendobj\n");
        BeginObj(6);
        Emit($"<< /Type /ExtGState /ca {constantAlpha}{bm} "
             + "/SMask << /Type /Mask /S /Luminosity /G 5 0 R >> >>\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
