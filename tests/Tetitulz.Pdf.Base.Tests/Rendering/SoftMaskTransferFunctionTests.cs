// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The <c>/TR</c> transfer function of an <c>ExtGState</c> soft mask (§11.6.5.2: "a function
/// object … used to transfer the mask values"). It remaps the mask channel <em>after</em> the
/// luminosity/alpha reduction, so it can invert, flatten or step the mask.
///
/// <para><b>Why this file exists.</b> Nothing in the tree asserted that a non-identity soft-mask
/// <c>/TR</c> moves a single pixel — the feature was reachable only through the render path and
/// every existing soft-mask test used no <c>/TR</c> at all. That made the per-pixel evaluator loop
/// safe to "optimise" into anything, including nothing. These two tests are what the 256-entry LUT
/// refactor is checked against: the mask input is one byte, so a LUT is exact rather than an
/// approximation, and the pixels below must not move by so much as one level.</para>
///
/// <para>Both fixtures paint three vertical bands of <em>different</em> mask luminosity, so the
/// assertions are about the <b>shape</b> of the mapping and not merely about ink existing. An
/// identity <c>/TR</c> (or a dropped one) inverts the left/right relationship in the first test and
/// makes the middle band the darkest in the second.</para>
/// </summary>
public class SoftMaskTransferFunctionTests
{
    // Page is 90x90 pt rendered at 72 dpi, so 1 pt = 1 px and the three 30-pt bands are
    // x∈[0,30), [30,60), [60,90).
    private const float LeftBand = 15f, MiddleBand = 45f, RightBand = 75f;

    /// <summary>
    /// A Type 2 exponential with <c>/C0 [1] /C1 [0] /N 1</c> is <c>out = 1 − t</c>: the mask is
    /// inverted, so the band the file made <em>least</em> opaque becomes the most opaque.
    ///
    /// <para>Mask luminosities are 0.25 / 0.5 / 0.75 grey → mask bytes 64 / 128 / 191. Inverted they
    /// are 191 / 127 / 64, and the page is black-over-white, so the painted grey is
    /// <c>255 − mask</c> = 64 / 128 / 191. Without the transfer function it is 191 / 127 / 64 —
    /// the exact mirror image, which is what makes "left darker than right" falsifiable.</para>
    /// </summary>
    [Fact]
    public void SoftMaskTransferFunction_Type2Inversion_MirrorsTheMaskAcrossTheBands()
    {
        using var bmp = PixelAssert.Render(TrSoftMaskPdf(
            "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>"));

        SKColor left   = PixelAssert.At(bmp, LeftBand,   45);
        SKColor middle = PixelAssert.At(bmp, MiddleBand, 45);
        SKColor right  = PixelAssert.At(bmp, RightBand,  45);

        Assert.InRange(left.Red, 62, 66);      // 0.25 grey → mask 64 → inverted 191 → 64 painted
        Assert.InRange(middle.Red, 126, 130);  // 0.5  grey → mask 128 → inverted 127 → 128 painted
        Assert.InRange(right.Red, 189, 193);   // 0.75 grey → mask 191 → inverted 64 → 191 painted
        Assert.True(left.Red < right.Red - 80,
            $"the transfer function must invert the mask: left={left.Red} right={right.Red} " +
            "(with /TR dropped or evaluated as identity the relationship is the other way round)");
    }

    /// <summary>
    /// A Type 0 sampled function with 256 samples over <c>/Domain [0 1]</c> — one sample per
    /// possible mask byte, which is the reason a LUT of exactly 256 entries reproduces the
    /// per-pixel evaluation byte for byte rather than approximating it.
    ///
    /// <para>The staircase is deliberately <b>non-monotonic</b> (255 → 0 → 128): neither the
    /// identity, nor an inversion, nor any single scale factor can produce "dark, white, mid", so
    /// the middle band going white is a claim only this function can satisfy. It also pins the
    /// <c>is PdfDictionary</c> admission test in the renderer — a Type 0 function is a
    /// <c>PdfStream</c>, and <c>PdfStream</c> derives from <c>PdfDictionary</c> here, so narrowing
    /// that test to a non-stream dictionary would silently drop every sampled transfer.</para>
    /// </summary>
    [Fact]
    public void SoftMaskTransferFunction_Type0Sampled_AppliesTheStaircasePerMaskValue()
    {
        using var bmp = PixelAssert.Render(TrSoftMaskPdf(SampledStaircaseFunction()));

        SKColor left   = PixelAssert.At(bmp, LeftBand,   45);
        SKColor middle = PixelAssert.At(bmp, MiddleBand, 45);
        SKColor right  = PixelAssert.At(bmp, RightBand,  45);

        // mask 64 → sample 255 → fully opaque black; mask 128 → 0 → page white;
        // mask 191 → 128 → half-opaque black.
        Assert.InRange(left.Red, 0, 2);
        Assert.InRange(middle.Red, 253, 255);
        Assert.InRange(right.Red, 125, 129);
        Assert.True(middle.Red > left.Red + 150 && middle.Red > right.Red + 60,
            $"only the sampled staircase makes the MIDDLE band the lightest: left={left.Red} " +
            $"middle={middle.Red} right={right.Red}");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>
    /// 256 samples, 8 bits each, as ASCII hex: <c>[0,96)→255</c>, <c>[96,160)→0</c>,
    /// <c>[160,256)→128</c>. The three band mask values (64, 128, 191) land one in each step.
    /// </summary>
    private static string SampledStaircaseFunction()
    {
        var hex = new StringBuilder(512);
        for (int i = 0; i < 256; i++)
            hex.Append((i < 96 ? 255 : i < 160 ? 0 : 128).ToString("X2"));
        string data = hex.ToString() + ">";
        return "<< /FunctionType 0 /Domain [0 1] /Range [0 1] /Size [256] /BitsPerSample 8" +
               $" /Filter /ASCIIHexDecode /Length {data.Length} >>\nstream\n{data}\nendstream";
    }

    /// <summary>
    /// Black over the whole page, modulated by a luminosity soft mask of three grey bands, with
    /// <paramref name="trObject"/> as the mask's <c>/TR</c>.
    /// </summary>
    private static byte[] TrSoftMaskPdf(string trObject)
    {
        const string content = "0 0 0 rg /GM gs 0 0 90 90 re f";
        const string maskContent =
            "0.25 g 0 0 30 90 re f 0.5 g 30 0 30 90 re f 0.75 g 60 0 30 90 re f";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GM 6 0 R >> >> >>",
            Stream("", content),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 90 90]" +
                   " /Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << >>",
                   maskContent),
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R /TR 7 0 R >> >>",
            trObject);
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

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
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
