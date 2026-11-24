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
/// The raster decoder for Separation / DeviceN / Lab / CalGray / CalRGB images has TWO ways to
/// colour a 1-component image: a full-range lookup table, sized <c>maxRaw + 1</c> by the bit
/// depth, or one conversion per pixel.
///
/// <para><b>At 16 bpc a table is 65 536 entries no matter how small the image is</b>, so always
/// building it would cost a 2x1 Separation plate 65 536 tint-transform evaluations to colour two
/// pixels, each one a full PDF function evaluation. The table is built only when it has fewer
/// entries than the image has pixels.</para>
///
/// <para><b>No parity test can see that cost.</b> The SVG renderer normalises every depth to one
/// byte per component in <c>ExpandSamplesToBytes</c> before its own LUT is built, so its table is
/// always 256 entries. Two renderers producing identical pixels by different amounts of work is a
/// divergence in COST, which a suite that compares output is structurally unable to see. Same
/// blind spot as the geometry divergence in <c>RenderGeometryParityTests</c>, one axis over.</para>
///
/// <para><b>What these tests guard.</b> With the choice conditional, both branches run in
/// production -- the LUT for a large image, the per-pixel loop for a small one, at EVERY depth.
/// Two code paths that must agree exactly is the shape this repo has been bitten by repeatedly, so
/// each case renders the same image content at a size either side of the threshold and asserts the
/// same colour comes out.</para>
/// </summary>
public class SpecialColorSpaceLutEquivalenceTests
{
    // Same minimal one-image PDF as ImageColorSpaceTests; kept local so a change there to suit a
    // colour test cannot quietly move this file's threshold arithmetic.
    private static byte[] BuildImagePdf(string imageDictBody, byte[] imageData)
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

        int xref = acc.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int n = 1; n <= 5; n++) sb.Append($"{off[n]:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        Emit(sb.ToString());
        return acc.ToArray();
    }

    private static (SKColor Left, SKColor Right) RenderHalves(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return (bmp.GetPixel(bmp.Width / 4, bmp.Height / 2),
                bmp.GetPixel(3 * bmp.Width / 4, bmp.Height / 2));
    }

    /// <summary>
    /// Left half of every row <paramref name="leftLevel"/>, right half maximum, at the given depth.
    /// At 16 bpc the level is replicated into both bytes, so the same normalised value arrives at
    /// either depth and the two rows of <see cref="Straddles"/> are comparable.
    /// </summary>
    /// <remarks>
    /// <b>leftLevel exists because 0 and max are exactly where a colour transform is the identity.</b>
    /// The CalGray case first used 0 and 0xFF and the mutation that passes a NULL parameter
    /// dictionary -- dropping /Gamma entirely -- survived it: 0^2.2 is 0 and 1^2.2 is 1, so the
    /// fixture never reached the gamma at all. A mid level is the only input that can tell the two
    /// paths apart.
    /// </remarks>
    private static byte[] Halves(int w, int h, int bpc, byte leftLevel)
    {
        int bytesPerSample = bpc / 8;
        var data = new byte[w * h * bytesPerSample];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * bytesPerSample;
                byte level = x < w / 2 ? leftLevel : (byte)0xFF;
                for (int b = 0; b < bytesPerSample; b++) data[o + b] = level;
            }
        return data;
    }

    /// <summary>Tint 0 -- the white end of the transform, and unambiguous against red.</summary>
    private const byte SeparationLeft = 0x00;

    /// <summary>A midtone, where /Gamma 2.2 actually moves the value. See <see cref="Halves"/>.</summary>
    private const byte CalGrayLeft = 0x80;

    // White at tint 0, red at tint 1 -- two colours no rounding can confuse, so an equality
    // assertion between the two paths is about the path and not about a channel being off by one.
    private const string Tint = "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>";

    private static string SeparationDict(int w, int h, int bpc) =>
        $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /BitsPerComponent {bpc} " +
        "/ColorSpace [/Separation /Spot /DeviceRGB " + Tint + "]";

    private static string CalGrayDict(int w, int h, int bpc) =>
        $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /BitsPerComponent {bpc} " +
        "/ColorSpace [/CalGray << /WhitePoint [0.9505 1.0 1.089] /Gamma 2.2 >>]";

    // The LUT has maxRaw + 1 entries, so it is chosen exactly when that is <= the pixel count:
    // 256 pixels at 8 bpc, 65 536 at 16. Each pair below straddles its own threshold, which is
    // why the sizes differ per depth rather than being one convenient constant.
    public static IEnumerable<object[]> Straddles => new[]
    {
        new object[] { 8,  2, 1,  16, 16 },
        new object[] { 16, 2, 1, 256, 256 },
    };

    [Theory]
    [MemberData(nameof(Straddles))]
    public void ASeparationPlateIsTheSameColourWhicheverPathDecodesIt(
        int bpc, int smallW, int smallH, int largeW, int largeH)
    {
        var perPixel = RenderHalves(BuildImagePdf(SeparationDict(smallW, smallH, bpc), Halves(smallW, smallH, bpc, SeparationLeft)));
        var viaLut   = RenderHalves(BuildImagePdf(SeparationDict(largeW, largeH, bpc), Halves(largeW, largeH, bpc, SeparationLeft)));

        Assert.Equal(perPixel.Left, viaLut.Left);
        Assert.Equal(perPixel.Right, viaLut.Right);

        // Anchored so that BOTH paths collapsing to one wrong colour cannot pass: tint 0 is white
        // and tint 1 is red, which is the tint transform actually having run.
        Assert.True(perPixel.Left.Red > 200 && perPixel.Left.Green > 200 && perPixel.Left.Blue > 200,
            $"tint 0 should be white, got {perPixel.Left}");
        Assert.True(perPixel.Right.Red > 200 && perPixel.Right.Green < 110 && perPixel.Right.Blue < 110,
            $"tint 1 should be red, got {perPixel.Right}");
    }

    [Theory]
    [MemberData(nameof(Straddles))]
    public void ACalGrayImageIsTheSameColourWhicheverPathDecodesIt(
        int bpc, int smallW, int smallH, int largeW, int largeH)
    {
        var perPixel = RenderHalves(BuildImagePdf(CalGrayDict(smallW, smallH, bpc), Halves(smallW, smallH, bpc, CalGrayLeft)));
        var viaLut   = RenderHalves(BuildImagePdf(CalGrayDict(largeW, largeH, bpc), Halves(largeW, largeH, bpc, CalGrayLeft)));

        Assert.Equal(perPixel.Left, viaLut.Left);
        Assert.Equal(perPixel.Right, viaLut.Right);

        // A path that skipped the CalGray conversion would still satisfy the equality above,
        // so this is what stops the pair passing vacuously. The 0x80 sample's luminance is
        // 0.5^2.2 = 0.2176, sRGB 129; a path that dropped the parameter dictionary (gamma 1) would
        // paint 188. That gap is the whole reason the level is a midtone: at 0x00 and 0xFF both
        // paths agree whether the gamma ran or not, and the mutation proving it does run SURVIVED
        // the first fixture.
        Assert.InRange(perPixel.Left.Red, 120, 138);
        Assert.True(perPixel.Right.Red > 215, $"CalGray max should be white, got {perPixel.Right}");
    }
}
