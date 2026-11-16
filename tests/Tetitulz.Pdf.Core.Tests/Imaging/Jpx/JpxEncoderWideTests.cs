// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The wide path: samples deeper than 16 bits through the encoder and back through the decoder,
/// which holds a band past 30 planes in a <c>long</c> array and a component past 31 bits in a
/// <c>long</c> plane. Lossless as far as a codestream can signal — 37 magnitude bit-planes, a
/// five-bit exponent and three guard bits (T.800 Tables A.28, A.30, E-2) — and lossy to T.800's
/// 38 bits, where the finest step a band can have is 2^(R_b − 31). An independent tool's JPEG 2000 plugin
/// hands back <c>I;16</c> for anything deeper than 16 bits, so the reference decoder is a second opinion only
/// to 16 bits through it; above that the oracles are a hand-computed case and the decoder's own
/// arithmetic, whose narrow path the conformance set holds and whose wide path differs from it
/// only in the width of its arrays.
/// </summary>
public sealed class JpxEncoderWideTests
{
    /// <summary>A gradient with noise from a linear congruential generator, filling the full range of <paramref name="precision"/>-bit samples, signed or not.</summary>
    private static long[][] Pattern(int width, int height, int components, int seed, int precision, bool signed)
    {
        var planes = new long[components][];
        long span = 1L << precision;
        long offset = signed ? -(1L << (precision - 1)) : 0;
        for (int c = 0; c < components; c++)
        {
            var plane = new long[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                    // The gradient spans the range; the noise is a fraction of it, so neighbours differ at every plane.
                    double gradient = (x * (7 + 2 * c) + y * (3 + c)) / (double)(width * 9 + height * 4);
                    long v = (long)(gradient * (span - 1)) ^ (long)(seed & 0x7FFFF) ^ ((long)(seed >> 3) << 12);
                    v &= span - 1;
                    plane[y * width + x] = v + offset;
                }
            plane[0] = offset;                     // the range's ends are in the picture
            plane[plane.Length - 1] = offset + span - 1;
            planes[c] = plane;
        }
        return planes;
    }

    private static long[] Decoded(byte[] codestream, int component, out JpxDecodeResult result)
    {
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out result, out string reason), reason);
        Assert.Empty(result.Deviations);
        JpxDecodedImage image = result.Image!;
        if (image.IsWide(component)) return image.WideSamples[component];
        return Array.ConvertAll(image.Samples[component], v => (long)v);
    }

    [Theory]
    [InlineData(17, false)]
    [InlineData(24, false)]
    [InlineData(29, false)]   // M_b 30 with two guard bits: the last int band
    [InlineData(30, false)]   // M_b 31: the first long band, though the samples still fit an int plane
    [InlineData(31, false)]
    [InlineData(32, false)]
    [InlineData(35, false)]
    [InlineData(35, true)]
    public void Lossless_DeepGrey_RoundTripsExactly_AndTheDecoderWidensOnlyPast31Bits(int precision, bool signed)
    {
        long[][] planes = Pattern(24, 16, 1, 100 + precision, precision, signed);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 24, 16, precision, signed, new JpxEncodeOptions { Levels = 2 });

        Assert.Equal(planes[0], Decoded(codestream, 0, out JpxDecodeResult result));
        // Liveness: the wide plane is used exactly when an int cannot hold the samples, and a wide band when
        // M_b can exceed 30 planes — the narrow path, which the conformance set holds, stays in use below.
        Assert.Equal(precision > 31, result.Image!.IsWide(0));
        Assert.Equal(precision > 31, result.Image.WideSamples[0] != null);
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(precision, cs.Size.Components[0].Precision);
        Assert.Equal(signed, cs.Size.Components[0].IsSigned);
        int mb = JpxQuantizationSteps.MagnitudeBitPlanes(cs.QuantizationFor(0, 0), 2, 0, 0);
        Assert.True(mb <= JpxEncoder.MaxCodedBitPlanes, $"M_b {mb}");
        // Tier 1's band arrays: long exactly when M_b can exceed an int band's planes.
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        JpxTier1Plan plan = JpxTier1Decoder.Plan(cs, tile, 0, 0);
        Assert.Equal(mb > JpxBlockDecoder.MaxNarrowBitPlanes, plan.Bands[0].WideValues != null);
        Assert.Equal(mb > JpxBlockDecoder.MaxNarrowBitPlanes, plan.Bands[0].Values == null);
    }

    /// <summary>
    /// An ROI shift counts toward a band's planes (H-3): an RGN of 25 spliced into a 12-bit
    /// codestream's main header lifts every band past an int band's 30, so tier 1 takes long
    /// bands — and, every coefficient lying below 2^25, the background rule leaves the picture as
    /// it was. On int bands the same decode would clear every block and report it.
    /// </summary>
    [Fact]
    public void AnRoiShift_CountsTowardTheWideBandChoice_AndTheBackgroundDecodesUnchanged()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(24, 16, 1, 55, precision: 12);
        byte[] plain = JpxEncoder.EncodeLossless(planes, 24, 16, 12, false, new JpxEncodeOptions { Levels = 1 });
        int qcd = IndexOf(plain, new byte[] { 0xFF, 0x5C });
        int qcdEnd = qcd + 2 + ((plain[qcd + 2] << 8) | plain[qcd + 3]);
        byte[] rgn = JpxCodestreams.Rgn(0, 25);
        byte[] shifted = plain.Take(qcdEnd).Concat(rgn).Concat(plain.Skip(qcdEnd)).ToArray();

        Assert.True(JpxCodestreamParser.TryParse(shifted, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(25, cs.RoiShiftFor(0, 0));
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        JpxTier1Plan plan = JpxTier1Decoder.Plan(cs, tile, 0, 0);
        Assert.All(plan.Bands, b => Assert.True(b.WideValues != null && b.Values == null, "a band stayed on an int array under a 25-plane shift"));
        Assert.Equal(Array.ConvertAll(planes[0], v => (long)v), Decoded(shifted, 0, out _));

        // The control: without the RGN the same codestream decodes on int bands.
        Assert.True(JpxCodestreamParser.TryParse(plain, out cs, out reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out tile, out reason), reason);
        Assert.All(JpxTier1Decoder.Plan(cs, tile, 0, 0).Bands, b => Assert.Null(b.WideValues));
    }

    /// <summary>
    /// The distortion a wide block's passes remove, against the decoder's partial reconstruction
    /// (as the narrow test does): the half of E-6 at a plane past 31 must be 2^(plane − 1) in a
    /// long, not an int's wrapped shift.
    /// </summary>
    [Fact]
    public void DistortionPerPass_OfAWideBlock_MatchesTheDecodersPartialReconstruction()
    {
        var random = new Random(3535);
        var coefficients = new long[16];
        for (int i = 0; i < coefficients.Length; i++) coefficients[i] = (random.Next(2) == 0 ? -1 : 1) * ((long)random.Next() << 5 | (long)random.Next(32));
        coefficients[0] = 1L << 35;
        JpxBlockCode code = JpxBlockEncoder.Encode(coefficients, 4, 4, 0);
        Assert.Equal(36, code.Planes);
        double energy = 0;
        foreach (long c in coefficients) energy += (double)c * c;
        Assert.True(Math.Abs(energy - code.Distortion[code.Passes - 1]) <= 1e-9 * energy, $"{code.Distortion[code.Passes - 1]} against {energy}");   // double at 2^70: relative

        var block = new JpxCodeBlock(new JpxRect(0, 0, 4, 4)) { Included = true };
        for (int k = 0; k < code.Passes - 3; k++)
        {
            var partial = new JpxCodeBlock(new JpxRect(0, 0, 4, 4)) { Included = true };
            partial.Add(new JpxCodeChunk(new JpxRange(0, code.Data.Length), k + 1, terminated: false));
            long[] values = JpxBlockDecoder.DecodeWide(partial, code.Data, 0, JpxCodeBlockStyle.None, code.Planes, 0, out string problem);
            Assert.Null(problem);
            double error = 0;
            for (int i = 0; i < values.Length; i++)
            {
                double e = Math.Abs(coefficients[i]) - Math.Abs(values[i]) / 2.0;
                error += e * e;
            }
            Assert.True(Math.Abs(energy - error - code.Distortion[k]) <= 1e-9 * energy, $"pass {k}: {energy - error} against {code.Distortion[k]}");
        }
        Assert.True(code.Distortion[0] > 0 && code.Distortion[0] < energy);
    }

    [Fact]
    public void Lossless_DeepRgb_ThroughTheRct_RoundTripsExactly()
    {
        long[][] planes = Pattern(20, 14, 3, 33, 34, false);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 20, 14, 34, false, new JpxEncodeOptions { Levels = 1 });
        for (int c = 0; c < 3; c++) Assert.Equal(planes[c], Decoded(codestream, c, out _));
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.True(cs.CodingFor(0).MultipleComponentTransform);
    }

    /// <summary>
    /// One 35-bit sample, no decomposition level, by hand: the SIZ's Ssiz is 34 (35 − 1); the QCD
    /// is no quantisation with the exponent clamped at 31 (SPqcd 31 &lt;&lt; 3 = 0xF8) and, for a
    /// coefficient of −2^34 (sample 0 less the level shift), 35 magnitude bit-planes need
    /// G = 35 − 31 + 1 = 5 guard bits (Sqcd 5 &lt;&lt; 5 = 0xA0), so M_b = 35 and the block signals
    /// no zero bit-plane; the decoder returns 0.
    /// </summary>
    [Fact]
    public void OneThirtyFiveBitSample_HandChecked()
    {
        byte[] codestream = JpxEncoder.EncodeLossless(new[] { new long[] { 0 } }, 1, 1, 35, false, new JpxEncodeOptions { Levels = 0 });
        int siz = IndexOf(codestream, new byte[] { 0xFF, 0x51 });
        Assert.Equal(34, codestream[siz + 40]);   // the marker (2), Lsiz (2), Rsiz (2), the eight 32-bit grid fields (32), Csiz (2), then Ssiz (Table A.9)
        int qcd = IndexOf(codestream, new byte[] { 0xFF, 0x5C });
        Assert.Equal(new byte[] { 0xFF, 0x5C, 0x00, 0x04, 0xA0, 0xF8 }, codestream.Skip(qcd).Take(6).ToArray());

        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(35, JpxQuantizationSteps.MagnitudeBitPlanes(cs.QuantizationFor(0, 0), 0, 0, 0));
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        JpxCodeBlock block = Assert.Single(tile.Layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        Assert.Equal(0, block.ZeroBitPlanes);
        Assert.Equal(3 * 35 - 2, block.Passes);
        long[] wide = JpxBlockDecoder.DecodeWide(block, tile.Data, 0, JpxCodeBlockStyle.None, 35, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(-(1L << 34), JpxBlockDecoder.Truncate(wide[0]));
        Assert.Equal(new long[] { 0 }, Decoded(codestream, 0, out _));
    }

    /// <summary>The sample the coded planes cannot hold: the value whose coefficient needs a 38th plane is refused with the reason, beside the 35-bit control the same call accepts.</summary>
    [Fact]
    public void Lossless_PastThirtySevenPlanes_IsRefusedWithTheReason()
    {
        long[][] deepest = Pattern(8, 8, 1, 1, 38, false);
        var refusal = Assert.Throws<ArgumentException>(() => JpxEncoder.EncodeLossless(deepest, 8, 8, 38, false, new JpxEncodeOptions { Levels = 1 }));
        Assert.Contains("37 magnitude bit-planes", refusal.Message, StringComparison.Ordinal);
        long[][] deep = Pattern(8, 8, 1, 1, 35, false);
        byte[] accepted = JpxEncoder.EncodeLossless(deep, 8, 8, 35, false, new JpxEncodeOptions { Levels = 1 });
        Assert.True(JpxCodestreamParser.TryParse(accepted, out JpxCodestream cs, out string reason), reason);
        Assert.True(cs.Main.Quantization.GuardBits <= JpxEncoder.MaxGuardBits && cs.Main.Quantization.Exponents[0] <= JpxForwardQuantizer.MaxExponent);
        Assert.True(JpxDecoder.TryDecode(accepted, 0, out JpxDecodeResult decoded, out reason), reason);
        Assert.Equal(deep[0], decoded.Image!.WideSamples[0]);
    }

    /// <summary>Thirty-eight bits code irreversibly: the finest step for the LL band is 2^(38 − 31) = 128, so the samples come back within a few hundred of themselves — over 150 dB at that depth.</summary>
    [Fact]
    public void Lossy_ThirtyEightBit_RoundTripsWithinTheClampedStep()
    {
        long[][] planes = Pattern(24, 16, 1, 5, 38, false);
        byte[] codestream = JpxEncoder.EncodeLossy(planes, 24, 16, 38, false, new JpxEncodeOptions { Levels = 2, QuantizationStep = 1 });
        long[] decoded = Decoded(codestream, 0, out JpxDecodeResult result);
        Assert.True(result.Image!.IsWide(0));
        double sum = 0;
        long worst = 0;
        for (int i = 0; i < decoded.Length; i++)
        {
            long d = Math.Abs(decoded[i] - planes[0][i]);
            worst = Math.Max(worst, d);
            sum += (double)d * d;
        }
        double peak = (1L << 38) - 1;
        double psnr = 10 * Math.Log10(peak * peak / (sum / decoded.Length));
        Assert.True(worst > 0 && worst < 4096, $"the worst error is {worst}");
        Assert.True(psnr > 150, $"{psnr:F1} dB");
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(31, cs.QuantizationFor(0, 0).Exponents[0]);   // the clamp: R_b 38 asked for a step of 1, the QCD can say 2^7 at best
    }

    [Fact]
    public void ComponentPlane_ReadsAWideComponent_SaturatedToInt()
    {
        long[][] planes = Pattern(20, 14, 1, 33, 34, false);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 20, 14, 34, false, new JpxEncodeOptions { Levels = 1 });
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Null(result.Image!.Samples[0]);   // held wide
        int[] plane = result.ComponentPlane(0, out JpxRect extent);
        Assert.Equal((20L, 14L), (extent.Width, extent.Height));
        Assert.Equal(Array.ConvertAll(planes[0], v => v > int.MaxValue ? int.MaxValue : (int)v), plane);
        Assert.Contains(planes[0], v => v > int.MaxValue);   // the saturation is exercised
    }

    [Fact]
    public void EncodePlanes_IsThePublicDoor_ToTheFullDepth()
    {
        // Lossless at 35 bits: the public narrowing keeps each sample's top byte exactly.
        long[][] planes = Pattern(16, 12, 1, 9, 35, false);
        byte[] jp2 = JpxImageEncoding.EncodePlanes(planes, 16, 12, 35, false, null);
        Assert.True(JpxImageDecoding.TryDecode(jp2, JpxImageDictionaryFacts.None, out JpxImagePixels pixels, out string reason), reason);
        Assert.Equal(Array.ConvertAll(planes[0], v => (byte)(v >> 27)), pixels.Colour);

        // Lossy at 38 bits, quality 100: the quality's step scales with the depth (2^30 here), so
        // each sample comes back within a step or two of the top byte, and never exactly.
        long[][] deepest = Pattern(16, 12, 1, 9, 38, false);
        byte[] lossy = JpxImageEncoding.EncodePlanes(deepest, 16, 12, 38, false, new JpxEncodingOptions { Lossless = false, Quality = 100 });
        Assert.True(JpxImageDecoding.TryDecode(lossy, JpxImageDictionaryFacts.None, out pixels, out reason), reason);
        int off = 0;
        for (int i = 0; i < deepest[0].Length; i++)
        {
            int d = Math.Abs(pixels.Colour[i] - (int)(deepest[0][i] >> 30));
            Assert.True(d <= 3, $"sample {i}: {pixels.Colour[i]} against {deepest[0][i] >> 30}");
            if (d > 0) off++;
        }
        Assert.True(off > 0, "a step of 2^30 would leave some top byte changed");
        Assert.Equal(38, JpxImageEncoding.MaxPlaneBitsPerComponent);

        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.EncodePlanes(planes, 16, 12, 39, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.EncodePlanes(new[] { planes[0], planes[0] }, 16, 12, 38, false, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(new byte[16 * 12 * 4], 16, 12, 1, 17, null));   // the byte form stops at 16
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
