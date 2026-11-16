// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxEncoderTests
{
    private static void AssertIndependentlyDecodedExactly(byte[] codestream, int[][] planes, int width, int height, string mode, string extension = ".j2k")
    {
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(codestream, extension);
        Assert.Equal((mode, width, height, planes.Length), (decoded.Mode, decoded.Width, decoded.Height, decoded.Planes.Length));
        for (int c = 0; c < planes.Length; c++) Assert.Equal(planes[c], decoded.Planes[c]);
        AssertNotFlat(planes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Lossless_Grey_AtEveryLevelCount_DecodesExactlyThroughAnIndependentDecoder(int levels)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 1, 12345 + levels);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, Options(levels));
        AssertIndependentlyDecodedExactly(codestream, planes, 40, 24, "L");
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(true, 5)]
    [InlineData(false, 2)]
    public void Lossless_Rgb_WithAndWithoutTheRct_DecodesExactlyThroughAnIndependentDecoder(bool mct, int levels)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 3, 4242, 8);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, Options(levels, mct: mct));
        AssertIndependentlyDecodedExactly(codestream, planes, 40, 24, "RGB");

        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(mct, cs.CodingFor(0).MultipleComponentTransform);
    }

    /// <summary>Sizes that exercise the signal-of-one rule, odd extents at every level, and blocks clipped by the band.</summary>
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(1, 1, 3)]
    [InlineData(1, 7, 2)]
    [InlineData(7, 1, 2)]
    [InlineData(37, 29, 5)]
    [InlineData(65, 63, 2)]
    [InlineData(3, 130, 4)]
    public void Lossless_AwkwardSizes_DecodeExactlyThroughAnIndependentDecoder(int width, int height, int levels)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(width, height, 1, width * 131 + height);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, width, height, 8, false, Options(levels));
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(codestream, ".j2k");
        Assert.Equal((width, height), (decoded.Width, decoded.Height));
        Assert.Equal(planes[0], decoded.Planes[0]);
    }

    /// <summary>4x4 code-blocks make many blocks per band, so the tag trees have depth and the packet bodies many contributions.</summary>
    [Fact]
    public void Lossless_SmallCodeBlocks_DecodeExactly_AndTier2CountsEveryBlock()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 1, 777);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, Options(2, codeBlockExponent: 2));
        AssertIndependentlyDecodedExactly(codestream, planes, 40, 24, "L");

        JpxTileDecode tile = ParseAndReadTile(codestream, out _);
        Assert.Equal(3, tile.PacketsRead);
        int blocks = 0, included = 0;
        foreach (JpxResolution r in tile.Layout.Components[0].Resolutions)
            foreach (JpxPrecinct p in r.Precincts)
                foreach (JpxPrecinctBand pb in p.Bands)
                    foreach (JpxCodeBlock b in pb.Blocks)
                    {
                        blocks++;
                        if (b.Included) included++;
                    }
        // LL 10x6 in 4x4 blocks: 3x2; each level-2 band 10x6: 3x2; each level-1 band 20x12: 5x3.
        Assert.Equal(6 + 3 * 6 + 3 * 15, blocks);
        Assert.True(included > blocks / 2, $"{included} of {blocks} blocks included");
    }

    /// <summary>A flat image has no coefficient above zero after the DC shift: every packet is the zero-length packet of §B.10.3, one byte each.</summary>
    [Fact]
    public void FlatImage_WritesOneEmptyPacketPerResolution()
    {
        int[][] planes = JpxEncoderTestImages.Flat(40, 24, 1, 128);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, Options(3));
        JpxTileDecode tile = ParseAndReadTile(codestream, out _);
        Assert.Equal((4, 4L), (tile.PacketsRead, tile.BytesConsumed));
        Assert.All(tile.PacketLengths, l => Assert.Equal(1L, l));
        foreach (JpxResolution r in tile.Layout.Components[0].Resolutions)
            foreach (JpxPrecinct p in r.Precincts)
                foreach (JpxPrecinctBand pb in p.Bands)
                    foreach (JpxCodeBlock b in pb.Blocks) Assert.False(b.Included);

        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(codestream, ".j2k");
        Assert.Equal(planes[0], decoded.Planes[0]);
    }

    /// <summary>
    /// E-2 and E.2: G is the smallest value, at least 2, with M<sub>b</sub> = G + ε<sub>b</sub> − 1 holding
    /// every band's planes. The RCT's chrominance spans ±255, and a pattern that drives the 5-3
    /// low-pass to its 1.5-per-dimension gain puts an LL coefficient past 512 — ten planes against
    /// ε = 8 — so the QCD carries three guard bits, and the independent decoder still reads every sample back. The
    /// same pattern without the RCT stays within two.
    /// </summary>
    [Fact]
    public void GuardBits_RiseWhenTheChrominancePlanesNeedThem_AndTheStreamStaysExact()
    {
        Assert.Equal(3, JpxEncoder.GuardBitsFor(new[] { 9, 12 }, new[] { 8, 10 }));
        Assert.Equal(2, JpxEncoder.GuardBitsFor(new[] { 8, 9 }, new[] { 8, 10 }));

        const int size = 16;
        var planes = new int[3][] { new int[size * size], new int[size * size], new int[size * size] };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool peak = x % 4 != 2 && y % 4 != 2;
                planes[0][y * size + x] = 128;
                planes[1][y * size + x] = peak ? 0 : 255;
                planes[2][y * size + x] = peak ? 255 : 0;
            }
        byte[] withRct = JpxEncoder.EncodeLossless(planes, size, size, 8, false, Options(1, mct: true));
        Assert.True(JpxCodestreamParser.TryParse(withRct, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(3, cs.QuantizationFor(0, 1).GuardBits);
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(withRct, ".j2k");
        for (int c = 0; c < 3; c++) Assert.Equal(planes[c], decoded.Planes[c]);

        byte[] withoutRct = JpxEncoder.EncodeLossless(planes, size, size, 8, false, Options(1, mct: false));
        Assert.True(JpxCodestreamParser.TryParse(withoutRct, out cs, out reason), reason);
        Assert.Equal(2, cs.QuantizationFor(0, 1).GuardBits);
    }

    [Fact]
    public void Lossless_SixteenBitGrey_DecodesExactlyThroughAnIndependentDecoder()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 1, 2024, precision: 16);
        Assert.Contains(planes[0], v => v > 255);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 16, false, Options(3));
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(codestream, ".j2k");
        Assert.Equal("I;16", decoded.Mode);
        Assert.Equal(planes[0], decoded.Planes[0]);
    }

    /// <summary>The irreversible path: ICT, 9-7, expounded quantisation. The finer the step, the higher the PSNR and the larger the file; every floor here is well under what the step gives.</summary>
    [Theory]
    [InlineData(0.5, 46.0)]
    [InlineData(2.0, 38.0)]
    [InlineData(8.0, 28.0)]
    public void Lossy_Rgb_ThroughTheIct_ReachesThePsnrTheStepAllows(double step, double floor)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 3, 8080);
        var options = Options(3);
        options.QuantizationStep = step;
        byte[] codestream = JpxEncoder.EncodeLossy(planes, 64, 48, 8, false, options);
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(codestream, ".j2k");
        Assert.Equal("RGB", decoded.Mode);
        double psnr = JpxEncoderTestImages.Psnr(planes, decoded.Planes, 8);
        Assert.True(psnr >= floor, $"PSNR {psnr:F1} dB at step {step}");

        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.False(cs.CodingFor(0).Component.Reversible);
        Assert.True(cs.CodingFor(0).MultipleComponentTransform);
        JpxQuantization q = cs.QuantizationFor(0, 0);
        Assert.Equal((JpxQuantizationStyle.ScalarExpounded, 10), (q.Style, q.Exponents.Length));
        // E-3 reproduces the step from the pair the QCD carries, for the LL band (R_b = 8) and an HH band (R_b = 10).
        Assert.Equal(step, JpxForwardQuantizer.Step(q.Exponents[0], q.Mantissas[0], 8), 6);
        Assert.Equal(step, JpxForwardQuantizer.Step(q.Exponents[3], q.Mantissas[3], 10), 6);
    }

    [Fact]
    public void Lossy_CoarserStepsCostQuality_AndSaveBytes()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 1, 4);
        var fine = Options(3);
        fine.QuantizationStep = 0.5;
        var coarse = Options(3);
        coarse.QuantizationStep = 16;
        byte[] fineStream = JpxEncoder.EncodeLossy(planes, 64, 48, 8, false, fine);
        byte[] coarseStream = JpxEncoder.EncodeLossy(planes, 64, 48, 8, false, coarse);
        byte[] lossless = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, Options(3));

        double finePsnr = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(fineStream, ".j2k").Planes, 8);
        double coarsePsnr = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(coarseStream, ".j2k").Planes, 8);
        Assert.True(finePsnr > coarsePsnr + 10, $"fine {finePsnr:F1} dB, coarse {coarsePsnr:F1} dB");
        Assert.True(coarsePsnr > 20, $"coarse {coarsePsnr:F1} dB");
        // A step below 1 resolves the 9-7 coefficients finer than the integers of the lossless path
        // and can cost more bytes than it; the coarse step is a fraction of either.
        Assert.True(coarseStream.Length * 2 < lossless.Length && coarseStream.Length * 2 < fineStream.Length,
            $"coarse {coarseStream.Length}, fine {fineStream.Length}, lossless {lossless.Length} bytes");
    }
}
