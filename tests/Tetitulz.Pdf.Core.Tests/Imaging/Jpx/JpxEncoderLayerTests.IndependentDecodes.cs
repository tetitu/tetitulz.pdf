// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxEncoderLayerTests
{
    /// <summary>The same shape at 16x16 blocks, lossless without a target, exact through an independent decoder: every block joins some layer, and the trees code each join where the codestream's write does.</summary>
    [Fact]
    public void ThreeLayers_OverSmallBlocksThatJoinLate_IsExactThroughAnIndependentDecoder()
    {
        int[] plane = HalfSmooth(128, 128, 777);
        byte[] three = JpxEncoder.EncodeLossless(new[] { plane }, 128, 128, 8, false,
            new JpxEncodeOptions { Levels = 2, Layers = 3, TargetBytes = 0, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4 });
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(three, ".j2k");
        Assert.Equal(plane, decoded.Planes[0]);
        byte[] targeted = JpxEncoder.EncodeLossless(new[] { plane }, 128, 128, 8, false,
            new JpxEncodeOptions { Levels = 2, Layers = 3, TargetBytes = three.Length * 2 / 3, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4 });
        Assert.True(targeted.Length <= three.Length * 2 / 3);
        Assert.Equal((128, 128), (IndependentJpxDecode.Decode(targeted, ".j2k").Width, IndependentJpxDecode.Decode(targeted, ".j2k").Height));
    }

    [Fact]
    public void Lossless_ThreeLayers_IsStillExact_AndTheCodSaysThree()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(48, 40, 3, 3030);
        byte[] one = JpxEncoder.EncodeLossless(planes, 48, 40, 8, false, Options(1));
        byte[] three = JpxEncoder.EncodeLossless(planes, 48, 40, 8, false, Options(3));
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(three, ".j2k");
        for (int c = 0; c < 3; c++) Assert.Equal(planes[c], decoded.Planes[c]);
        Assert.Equal(planes, DecodeInHouse(three, 3));

        // Raw bytes: SGcod's layer count, after the marker (2), Lcod (2), Scod (1) and the progression order (1).
        int cod = IndexOf(three, new byte[] { 0xFF, 0x52 });
        Assert.Equal((0, 3), (three[cod + 6], three[cod + 7]));
        Assert.Equal((0, 1), (one[cod + 6], one[cod + 7]));

        // Three times the packets, each precinct in each layer; the layered stream costs a little more in headers.
        Assert.Equal(SopOffsets(one).Count * 3, SopOffsets(three).Count);
        Assert.InRange(three.Length, one.Length, one.Length * 11 / 10);

        Assert.True(JpxCodestreamParser.TryParse(three, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        Assert.Equal(tile.DataEnd - tile.DataStart, tile.BytesConsumed);
        int inThreeChunks = 0;
        foreach (JpxTileComponent tc in tile.Layout.Components)
            foreach (JpxResolution r in tc.Resolutions)
                foreach (JpxPrecinct p in r.Precincts)
                    foreach (JpxPrecinctBand pb in p.Bands)
                        foreach (JpxCodeBlock b in pb.Blocks)
                            if (b.Chunks.Count == 3) inThreeChunks++;
        Assert.True(inThreeChunks > 0, "no code-block's passes were spread over the three layers");
    }

    [Theory]
    [InlineData(1200, 600, 17.0)]
    [InlineData(2500, 1200, 22.0)]
    public void ASizeTarget_IsMet_AndMoreBytesBuyMoreQuality(int larger, int smaller, double floor)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 3, 7272);
        var options = Options(1, larger, sop: false);
        options.QuantizationStep = 0.5;
        byte[] big = JpxEncoder.EncodeLossy(planes, 64, 48, 8, false, options);
        options.TargetBytes = smaller;
        byte[] small = JpxEncoder.EncodeLossy(planes, 64, 48, 8, false, options);
        options.TargetBytes = 0;
        byte[] whole = JpxEncoder.EncodeLossy(planes, 64, 48, 8, false, options);

        Assert.True(big.Length <= larger && big.Length > larger * 9 / 10, $"{big.Length} bytes for a target of {larger}");
        Assert.True(small.Length <= smaller && small.Length > smaller * 9 / 10, $"{small.Length} bytes for a target of {smaller}");
        Assert.True(whole.Length > larger, $"the untargeted stream is {whole.Length} bytes; the targets must truncate it");

        double psnrBig = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(big, ".j2k").Planes, 8);
        double psnrSmall = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(small, ".j2k").Planes, 8);
        double psnrWhole = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(whole, ".j2k").Planes, 8);
        Assert.True(psnrSmall >= floor, $"{smaller} bytes give {psnrSmall:F1} dB");
        Assert.True(psnrBig > psnrSmall + 2, $"{larger} bytes give {psnrBig:F1} dB, {smaller} give {psnrSmall:F1} dB");
        Assert.True(psnrWhole > psnrBig, $"the whole stream gives {psnrWhole:F1} dB, {larger} bytes {psnrBig:F1} dB");
    }
}
