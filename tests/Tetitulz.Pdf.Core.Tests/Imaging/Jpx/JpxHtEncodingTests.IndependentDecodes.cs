// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxHtEncodingTests
{
    /// <summary>Lossless grey and RGB (with the RCT), HT throughout: this decoder and the independent decoder both read back exactly the samples.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Lossless_RoundTrips_HereAndInAnIndependentDecoder(int components)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(61, 47, components, 20 + components);
        byte[] j2k = JpxImageEncoding.Encode(Interleave(planes, 8), 61, 47, components, 8, Ht());

        for (int c = 0; c < components; c++) Assert.Equal(planes[c], OurPlane(j2k, c));
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(j2k, ".j2k");
        for (int c = 0; c < components; c++) Assert.Equal(planes[c], decoded.Planes[c]);
    }

    /// <summary>Sixteen-bit grey, lossless: deeper magnitudes, a larger bound, still exact in an independent decoder.</summary>
    [Fact]
    public void SixteenBitGrey_IsExactThroughAnIndependentDecoder()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(33, 21, 1, 9, precision: 16);
        Assert.Contains(planes[0], v => v > 255);
        byte[] j2k = JpxImageEncoding.Encode(Interleave(planes, 16), 33, 21, 1, 16, Ht());
        Assert.Equal(planes[0], OurPlane(j2k, 0));
        Assert.Equal(planes[0], IndependentJpxDecode.Decode(j2k, ".j2k").Planes[0]);
    }

    /// <summary>
    /// Tiles, odd sizes and the smallest code-blocks (4 × 4, so every block edge case of the quad scan
    /// recurs many times over), through the internal encoder: exact here and in an independent decoder.
    /// </summary>
    [Fact]
    public void TilesAndSmallBlocks_AreExact()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(53, 39, 1, 13);
        var options = new JpxEncodeOptions
        {
            HighThroughput = true, Levels = 3, TileWidth = 32, TileHeight = 24,
            CodeBlockWidthExponent = 2, CodeBlockHeightExponent = 2,
        };
        byte[] j2k = JpxEncoder.EncodeLossless(planes, 53, 39, 8, false, options);
        Assert.Equal(planes[0], OurPlane(j2k, 0));
        Assert.Equal(planes[0], IndependentJpxDecode.Decode(j2k, ".j2k").Planes[0]);
    }

    /// <summary>
    /// Lossy, through the 9-7 wavelet and quantisation: close to the source, and this decoder and
    /// the independent decoder agree to within the rounding two 9-7 implementations may differ by.
    /// </summary>
    [Fact]
    public void Lossy_IsCloseToTheSource_AndTheDecodersAgree()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 3, 31);
        byte[] j2k = JpxImageEncoding.Encode(Interleave(planes, 8), 64, 48, 3, 8, Ht(lossless: false, quality: 80));
        IndependentJpxDecode.Decoded theirs = IndependentJpxDecode.Decode(j2k, ".j2k");
        for (int c = 0; c < 3; c++)
        {
            int[] ours = OurPlane(j2k, c);
            for (int i = 0; i < ours.Length; i++)
            {
                Assert.InRange(ours[i] - theirs.Planes[c][i], -1, 1);
                Assert.InRange(ours[i] - planes[c][i], -24, 24);
            }
        }
    }

    /// <summary>
    /// In a container an HT codestream is a JPH file (T.814 Annex D): brand 'jph ', 'jph ' compatible and
    /// not 'jp2 ' — a Part 1 reader cannot decode it — and the independent decoder opens it to the same samples.
    /// </summary>
    [Fact]
    public void TheContainer_IsAJphFile()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(30, 20, 3, 41);
        byte[] jph = JpxImageEncoding.Encode(Interleave(planes, 8), 30, 20, 3, 8, Ht(container: true));

        Assert.True(JpxHeader.IsJp2Container(jph));
        string ftyp = Encoding.ASCII.GetString(jph, 20, 12);   // after the signature box and ftyp's header: BR, MinV, CL…
        Assert.Equal("jph ", ftyp.Substring(0, 4));
        Assert.Equal("jph ", ftyp.Substring(8, 4));
        Assert.DoesNotContain("jp2 ", Encoding.ASCII.GetString(jph, 0, 64));
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(jph, ".jph");
        for (int c = 0; c < 3; c++) Assert.Equal(planes[c], decoded.Planes[c]);
    }

    /// <summary>
    /// A size target under HT: the codestream fits it, this decoder and the independent decoder read it alike, and a
    /// smaller target costs quality — the allocator chose coarser planes, not merely fewer blocks.
    /// </summary>
    [Fact]
    public void ASizeTarget_IsMet_AndCostsQuality()
    {
        int[][] planes = Noisy(96, 80, 4);
        byte[] samples = Interleave(planes, 8);
        byte[] whole = JpxImageEncoding.Encode(samples, 96, 80, 1, 8, Ht());

        double previous = double.PositiveInfinity;
        foreach (long target in new[] { whole.Length * 3 / 4L, whole.Length / 2L, whole.Length / 4L })
        {
            var options = new JpxEncodingOptions { HighThroughput = true, TargetBytes = target, Jp2Container = false };
            byte[] fitted = JpxImageEncoding.Encode(samples, 96, 80, 1, 8, options);
            Assert.True(fitted.Length <= target, $"{fitted.Length} bytes for a target of {target}");

            int[] ours = OurPlane(fitted, 0);
            Assert.Equal(ours, IndependentJpxDecode.Decode(fitted, ".j2k").Planes[0]);
            double psnr = Psnr(planes[0], ours);
            Assert.True(psnr < previous, $"a smaller target ({target}) should cost quality: {psnr:F1} dB after {previous:F1}");
            Assert.True(psnr > 20, $"{psnr:F1} dB at {target} bytes");
            previous = psnr;
        }
    }

    /// <summary>
    /// Quality layers under HT, no target: the layers split the blocks by halves of the whole, every
    /// block coded whole in its one HT set, so the reversible image comes back exactly — here and in
    /// the independent decoder, which decodes a block of several sets wrongly — and the CAP says one set per block
    /// (Ccap15 bit 13 zero, SINGLEHT).
    /// </summary>
    [Fact]
    public void QualityLayers_EndLossless_WithOneSetPerBlock()
    {
        int[][] planes = Noisy(64, 64, 7);
        var options = new JpxEncodingOptions { HighThroughput = true, Layers = 3, Jp2Container = false };
        byte[] layered = JpxImageEncoding.Encode(Interleave(planes, 8), 64, 64, 1, 8, options);

        Assert.True(JpxCodestreamParser.TryParse(layered, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(3, cs.Main.Coding.Layers);
        Assert.Equal(0, cs.HtCapabilities & 0x2000);
        Assert.Equal(planes[0], OurPlane(layered, 0));
        Assert.Equal(planes[0], IndependentJpxDecode.Decode(layered, ".j2k").Planes[0]);
    }

    /// <summary>
    /// The layers are quality layers: the independent decoder decoding only the first, then two, then all three sees
    /// the picture improve each time, and the first already holds the blocks that matter most — the
    /// ones removing the most error per byte arrive first.
    /// </summary>
    [Fact]
    public void EachLayer_ImprovesThePicture()
    {
        int[][] planes = Noisy(64, 64, 7);
        var options = new JpxEncodingOptions { HighThroughput = true, Layers = 3, Jp2Container = false };
        byte[] layered = JpxImageEncoding.Encode(Interleave(planes, 8), 64, 64, 1, 8, options);

        double one = Psnr(planes[0], IndependentJpxDecode.Decode(layered, ".j2k", layers: 1).Planes[0]);
        double two = Psnr(planes[0], IndependentJpxDecode.Decode(layered, ".j2k", layers: 2).Planes[0]);
        double three = Psnr(planes[0], IndependentJpxDecode.Decode(layered, ".j2k", layers: 3).Planes[0]);
        Assert.True(one < two && two < three, $"{one:F1}, {two:F1}, {three:F1} dB");
        Assert.True(double.IsPositiveInfinity(three));
        Assert.True(one > FirstLayerFloor, $"the first layer is {one:F1} dB");
    }

    /// <summary>
    /// The first layer's quality on <see cref="EachLayer_ImprovesThePicture"/>'s image, pinned between what
    /// the error-per-byte order gives (27.8 dB, measured 2026-10-07) and what the reverse order does (13.2).
    /// </summary>
    private const double FirstLayerFloor = 20;

    /// <summary>The independent decoder reads the refinement passes as this decoder does, sample for sample, at targets where the allocator chose them.</summary>
    [Fact]
    public void RefinementPasses_DecodeInAnIndependentDecoderAsHere()
    {
        int[][] planes = Noisy(96, 80, 5);
        byte[] whole = JpxEncoder.EncodeLossless(planes, 96, 80, 8, false, new JpxEncodeOptions { HighThroughput = true });
        foreach (long target in new[] { whole.Length * 2 / 3L, whole.Length / 3L, whole.Length / 6L })
        {
            byte[] refined = JpxEncoder.EncodeLossless(planes, 96, 80, 8, false, new JpxEncodeOptions { HighThroughput = true, TargetBytes = target });
            Assert.True(RefinedBlocks(refined) > 0, $"no block was refined at {target} bytes");
            Assert.Equal(OurPlane(refined, 0), IndependentJpxDecode.Decode(refined, ".j2k").Planes[0]);
        }
    }

    /// <summary>
    /// Layers with a target: the file fits, the independent decoder decodes it, and it is lossy no longer than the
    /// target forces — the decode improves on the first layer's alone, read by cutting the codestream
    /// at its first layer (a lower-quality picture than the whole).
    /// </summary>
    [Fact]
    public void LayersWithATarget_FitAndDecode()
    {
        int[][] planes = Noisy(80, 64, 11);
        byte[] samples = Interleave(planes, 8);
        byte[] whole = JpxImageEncoding.Encode(samples, 80, 64, 1, 8, Ht());
        var options = new JpxEncodingOptions { HighThroughput = true, Layers = 3, TargetBytes = whole.Length / 2, Jp2Container = false };
        byte[] layered = JpxImageEncoding.Encode(samples, 80, 64, 1, 8, options);

        Assert.True(layered.Length <= whole.Length / 2, $"{layered.Length} bytes for a target of {whole.Length / 2}");
        int[] ours = OurPlane(layered, 0);
        Assert.Equal(ours, IndependentJpxDecode.Decode(layered, ".j2k").Planes[0]);
        Assert.True(Psnr(planes[0], ours) > 20);
    }
}
