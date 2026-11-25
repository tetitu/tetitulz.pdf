// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// e-sRGB, e-sYCC and ROMM-RGB to 8-bit sRGB (<see cref="Jp2ExtendedColour"/>): each pinned by
/// hand values from its standard, and each reached through a JP2 whose colr box names it (EnumCS
/// 20, 24, 21), rather than painted untransformed.
/// </summary>
public sealed class Jp2ExtendedColourTests
{
    // ── e-sRGB (PIMA 7667 §4.4.3, equation 3) ───────────────────────────────

    /// <summary>The offset is 0, the offset plus half the scale is 128, the offset plus the scale is 255 — at 10, 12 and 16 bits; the extended range clips.</summary>
    [Theory]
    [InlineData(10, 384, 0)]
    [InlineData(10, 639, 128)]
    [InlineData(10, 894, 255)]
    [InlineData(12, 1536, 0)]
    [InlineData(12, 3576, 255)]
    [InlineData(16, 24576, 0)]
    [InlineData(16, 57216, 255)]
    [InlineData(10, 300, 0)]
    [InlineData(10, 1000, 255)]
    public void ESRgb_CodeValuesMapByTheOffsetAndScale(int depth, int code, int expected)
    {
        Assert.Equal((byte)expected, Jp2ExtendedColour.ESRgbToSRgb8(code, depth));
    }

    // ── e-sYCC (PIMA 7667 Annex B) ───────────────────────────────────────────

    /// <summary>A neutral — half luma, centred chroma — is mid grey at any depth.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void ESYcc_ANeutralIsMidGrey(int depth)
    {
        double max = Math.Pow(2, depth) - 1, half = Math.Pow(2, depth - 1);
        Jp2ExtendedColour.ESYccToSRgb8(max / 2, half, half, depth, depth, depth, out byte r, out byte g, out byte b);
        Assert.Equal((128, 128, 128), (r, g, b));
    }

    /// <summary>The primaries and white, taken forward by B3–B4 (BT.601 luma, chroma over 2.804 and 3.544) and back by B7–B9, return within a level.</summary>
    [Theory]
    [InlineData(1.0, 0.0, 0.0)]
    [InlineData(0.0, 1.0, 0.0)]
    [InlineData(0.0, 0.0, 1.0)]
    [InlineData(1.0, 1.0, 1.0)]
    [InlineData(0.25, 0.5, 0.75)]
    public void ESYcc_AColourTakenForwardAndBack_Returns(double rr, double gg, double bb)
    {
        const int depth = 12;
        double max = Math.Pow(2, depth) - 1, half = Math.Pow(2, depth - 1);
        double y = 0.299 * rr + 0.587 * gg + 0.114 * bb;
        double cb = (bb - y) / 3.544, cr = (rr - y) / 2.804;
        Jp2ExtendedColour.ESYccToSRgb8(y * max, cb * max + half, cr * max + half, depth, depth, depth, out byte r, out byte g, out byte b);
        Assert.InRange(r, Math.Round(rr * 255) - 1, Math.Round(rr * 255) + 1);
        Assert.InRange(g, Math.Round(gg * 255) - 1, Math.Round(gg * 255) + 1);
        Assert.InRange(b, Math.Round(bb * 255) - 1, Math.Round(bb * 255) + 1);
    }

    // ── ROMM-RGB (ISO 22028-2) ──────────────────────────────────────────────

    /// <summary>
    /// White is white within a level, black is black, mid grey (linear 0.287) is sRGB 146, and the
    /// ROMM primaries are outside sRGB's gamut and clip to sRGB's; (0.25, 0.5, 0.75) is (0, 156,
    /// 210). The values were computed by an independent script (numpy, the same three matrices).
    /// </summary>
    [Theory]
    [InlineData(1.0, 1.0, 1.0, 255, 255, 255)]
    [InlineData(0.0, 0.0, 0.0, 0, 0, 0)]
    [InlineData(0.5, 0.5, 0.5, 146, 146, 146)]
    [InlineData(1.0, 0.0, 0.0, 255, 0, 0)]
    [InlineData(0.0, 1.0, 0.0, 0, 255, 0)]
    [InlineData(0.0, 0.0, 1.0, 0, 0, 255)]
    [InlineData(0.25, 0.5, 0.75, 0, 156, 210)]
    public void RommRgb_PinnedAgainstAnIndependentComputation(double rv, double gv, double bv, int er, int eg, int eb)
    {
        const int depth = 12;
        double max = Math.Pow(2, depth) - 1;
        Jp2ExtendedColour.RommRgbToSRgb8(rv * max, gv * max, bv * max, depth, out byte r, out byte g, out byte b);
        Assert.InRange(r, er - 1, er + 1);
        Assert.InRange(g, eg - 1, eg + 1);
        Assert.InRange(b, eb - 1, eb + 1);
    }

    /// <summary>The toe: below 1/32 the encoding is linear (V / 16), which the power curve would overstate.</summary>
    [Fact]
    public void RommRgb_TheToeIsLinear()
    {
        const int depth = 16;
        double max = Math.Pow(2, depth) - 1;
        double v = 1.0 / 64;
        Jp2ExtendedColour.RommRgbToSRgb8(v * max, v * max, v * max, depth, out byte r, out _, out _);
        // linear 1/1024 → sRGB 12.92 × 0.000977 = 0.0126 → 3; the power curve would give (1/64)^1.8 = 0.000562 → 2.
        Assert.Equal(3, r);
    }

    // ── through a JP2 whose colr box names the space ────────────────────────

    /// <summary>
    /// A 10-bit RGB codestream under EnumCS 20 (e-sRGB) decodes to the leaf's values, sample for
    /// sample; the control under EnumCS 16 (sRGB) narrows the same codes, and the two differ.
    /// </summary>
    [Fact]
    public void ESRgbJp2_DecodesThroughTheTransform()
    {
        const int w = 8, h = 4, depth = 10;
        var codes = new int[w * h * 3];
        for (int i = 0; i < codes.Length; i++) codes[i] = 300 + (i * 37) % 700;   // 300..999 spans the extended range
        byte[] jp2 = Jp2(codes, w, h, depth, JpxEmbedEnumCs.ESRgb);

        Assert.True(JpxPdfImage.TryDecode(jp2, new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage image, out string reason), reason);
        Assert.Equal((JpxColourModel.Rgb, 3), (image.ColourModel, image.ColourComponents));
        var expected = new byte[codes.Length];
        for (int i = 0; i < codes.Length; i++) expected[i] = Jp2ExtendedColour.ESRgbToSRgb8(codes[i], depth);
        Assert.Equal(expected, image.Colour);
        Assert.DoesNotContain(image.Deviations, d => d.Contains("e-sRGB", StringComparison.Ordinal));

        Assert.True(JpxPdfImage.TryDecode(Jp2(codes, w, h, depth, JpxEmbedEnumCs.SRgb), new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage srgb, out reason), reason);
        Assert.NotEqual(expected, srgb.Colour);
    }

    /// <summary>A ROMM-RGB JP2 (EnumCS 21) decodes through the ROMM transform: its mid grey is 146, where sRGB's would be 128.</summary>
    [Fact]
    public void RommRgbJp2_DecodesThroughTheTransform()
    {
        const int w = 4, h = 4, depth = 12;
        var codes = new int[w * h * 3];
        for (int i = 0; i < codes.Length; i++) codes[i] = 2048;   // V = 0.5
        Assert.True(JpxPdfImage.TryDecode(Jp2(codes, w, h, depth, JpxEmbedEnumCs.RommRgb), new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage image, out string reason), reason);
        Assert.All(image.Colour, v => Assert.InRange(v, 145, 147));
        Assert.True(JpxPdfImage.TryDecode(Jp2(codes, w, h, depth, JpxEmbedEnumCs.SRgb), new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage srgb, out reason), reason);
        Assert.All(srgb.Colour, v => Assert.InRange(v, 127, 128));
    }

    /// <summary>An e-sYCC JP2 (EnumCS 24) decodes through Annex B's equations, not sYCC's ICT: a neutral is grey on both, a pure chroma differs.</summary>
    [Fact]
    public void ESYccJp2_DecodesThroughAnnexB_NotTheIct()
    {
        const int w = 4, h = 4, depth = 8;
        var codes = new int[w * h * 3];
        for (int i = 0; i < codes.Length; i += 3) { codes[i] = 128; codes[i + 1] = 200; codes[i + 2] = 128; }   // luma half, Cb high, Cr centred
        Assert.True(JpxPdfImage.TryDecode(Jp2(codes, w, h, depth, JpxEmbedEnumCs.ESYcc), new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage esycc, out string reason), reason);
        Jp2ExtendedColour.ESYccToSRgb8(128, 200, 128, 8, 8, 8, out byte r, out byte g, out byte b);
        Assert.Equal(new[] { r, g, b }, new[] { esycc.Colour[0], esycc.Colour[1], esycc.Colour[2] });
        Assert.True(JpxPdfImage.TryDecode(Jp2(codes, w, h, depth, JpxEmbedEnumCs.SYcc), new Jp2PdfImageFacts(0, false, 0, false), null, 0, out JpxPdfImage sycc, out reason), reason);
        Assert.NotEqual(new[] { esycc.Colour[0], esycc.Colour[1], esycc.Colour[2] }, new[] { sycc.Colour[0], sycc.Colour[1], sycc.Colour[2] });
    }

    private static class JpxEmbedEnumCs
    {
        internal const int SRgb = 16, SYcc = 18, ESRgb = 20, RommRgb = 21, ESYcc = 24;
    }

    /// <summary>A three-component lossless JP2 of the given codes, its colr box patched to <paramref name="enumCs"/> (the encoder writes sRGB for three channels).</summary>
    private static byte[] Jp2(int[] codes, int w, int h, int depth, int enumCs)
    {
        var samples = new byte[codes.Length * (depth > 8 ? 2 : 1)];
        for (int i = 0, at = 0; i < codes.Length; i++)
        {
            if (depth > 8) samples[at++] = (byte)(codes[i] >> 8);
            samples[at++] = (byte)codes[i];
        }
        byte[] jp2 = JpxImageEncoding.Encode(samples, w, h, 3, depth, new JpxEncodingOptions { Lossless = true, DecompositionLevels = 1, Jp2Container = true });
        int colr = IndexOf(jp2, new[] { (byte)'c', (byte)'o', (byte)'l', (byte)'r' });
        Assert.Equal(16, jp2[colr + 10]);
        jp2[colr + 10] = (byte)enumCs;
        return jp2;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
