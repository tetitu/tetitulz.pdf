// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// Quality layers and the size target (<see cref="JpxRateControl"/> through <see cref="JpxEncoder"/>).
/// A layered lossless codestream is still exact through the reference decoder; cut after its first layer's
/// packets it decodes, through the in-house decoder, to a coarser picture whose quality rises
/// layer by layer; a size target is met, and more bytes buy a higher PSNR through the reference decoder.
/// </summary>
public sealed partial class JpxEncoderLayerTests
{
    private static JpxEncodeOptions Options(int layers, long target = 0, bool sop = true) =>
        new() { Levels = 3, Layers = layers, TargetBytes = target, Sop = sop };

    /// <summary>The packets' start offsets within a codestream written with SOP markers, in order — the marker is FF 91 00 04 and a sequence number.</summary>
    private static List<int> SopOffsets(byte[] codestream)
    {
        var offsets = new List<int>();
        for (int i = 0; i + 5 < codestream.Length; i++)
            if (codestream[i] == 0xFF && codestream[i + 1] == 0x91 && codestream[i + 2] == 0 && codestream[i + 3] == 4
                && codestream[i + 4] == (byte)(offsets.Count >> 8) && codestream[i + 5] == (byte)offsets.Count)
                offsets.Add(i);
        return offsets;
    }

    /// <summary>The codestream cut before packet <paramref name="packet"/> (LRCP: the first packets are the first layer's), EOC appended; the tile-part's Psot then overshoots, which the parser reports as truncation.</summary>
    private static byte[] CutBeforePacket(byte[] codestream, int packet)
    {
        List<int> offsets = SopOffsets(codestream);
        var cut = new byte[offsets[packet] + 2];
        Array.Copy(codestream, cut, offsets[packet]);
        cut[cut.Length - 2] = 0xFF;
        cut[cut.Length - 1] = 0xD9;
        return cut;
    }

    private static int[][] DecodeInHouse(byte[] codestream, int components)
    {
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult result, out string reason), reason);
        var planes = new int[components][];
        for (int c = 0; c < components; c++) planes[c] = result.Image!.Samples[c];
        return planes;
    }

    /// <summary>A plane whose left half is a smooth ramp and whose right half is noise: layer 0 takes the noisy blocks and skips the smooth ones, which join later.</summary>
    private static int[] HalfSmooth(int width, int height, int seed)
    {
        var plane = new int[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                plane[y * width + x] = x < width / 2 ? (x + y) & 255 : (seed >> 16) & 255;
            }
        return plane;
    }

    /// <summary>
    /// The review's row 1 (2026-09-26): with several code-blocks under one tag-tree parent and blocks
    /// that join at a later layer, the allocator's trial writes coded inclusion trees built from
    /// "not yet" values and never lowered, so the measured layers and the codestream's own write
    /// differed by a bit or two, and the invariant threw. A 512x512 image whose left half is smooth,
    /// two layers with a target of half the single-layer size — and four layers on a 1024x256 —
    /// encode, stay within the target, and decode in-house at the size they claim.
    /// </summary>
    [Theory]
    [InlineData(512, 512, 2)]
    [InlineData(1024, 256, 4)]
    public void ATargetWithSeveralLayers_OverBlocksThatJoinLate_EncodesWithinIt(int width, int height, int layers)
    {
        int[] plane = HalfSmooth(width, height, 12345);
        byte[] whole = JpxEncoder.EncodeLossless(new[] { plane }, width, height, 8, false, Options(1, sop: false));
        long target = whole.Length / 2;
        byte[] layered = JpxEncoder.EncodeLossless(new[] { plane }, width, height, 8, false, Options(layers, target, sop: false));
        Assert.True(layered.Length <= target, $"{layered.Length} bytes for a target of {target}");
        Assert.True(JpxDecoder.TryDecode(layered, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Equal((width, height), (result.Image!.Width, result.Image.Height));
        int cod = IndexOf(layered, new byte[] { 0xFF, 0x52 });
        Assert.Equal((0, layers), (layered[cod + 6], layered[cod + 7]));
    }

    /// <summary>
    /// Without a target the layers split the whole by halves after a first, all-layers pass of the
    /// allocator, which leaves every later layer's slot holding "all passes"; the first-layer lookup
    /// must read only the layers decided so far, or a block that skips a layer codes as included
    /// where it has no passes. Four and six layers over blocks that join late, exact in-house.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void ManyLayersWithoutATarget_OverBlocksThatSkipLayers_EncodeExactly(int layers)
    {
        int[] plane = HalfSmooth(128, 128, 4242);
        byte[] codestream = JpxEncoder.EncodeLossless(new[] { plane }, 128, 128, 8, false,
            new JpxEncodeOptions { Levels = 3, Layers = layers, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4 });
        Assert.Equal(new[] { plane }, DecodeInHouse(codestream, 1));
        int cod = IndexOf(codestream, new byte[] { 0xFF, 0x52 });
        Assert.Equal(layers, codestream[cod + 7]);
    }

    /// <summary>The first layer alone, and the first two, decode to pictures that get better with each layer, and the first is already a picture.</summary>
    [Fact]
    public void EachLayerImprovesOnTheLast_WhenTheStreamIsCutBetweenThem()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 1, 6161);
        byte[] three = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, Options(3));
        int packets = SopOffsets(three).Count;
        Assert.Equal(0, packets % 3);
        byte[] first = CutBeforePacket(three, packets / 3);
        byte[] firstTwo = CutBeforePacket(three, 2 * packets / 3);

        double psnrFirst = JpxEncoderTestImages.Psnr(planes, DecodeInHouse(first, 1), 8);
        double psnrTwo = JpxEncoderTestImages.Psnr(planes, DecodeInHouse(firstTwo, 1), 8);
        double psnrAll = JpxEncoderTestImages.Psnr(planes, DecodeInHouse(three, 1), 8);
        Assert.True(psnrFirst >= 22, $"the first layer alone gives {psnrFirst:F1} dB");
        Assert.True(psnrTwo >= psnrFirst + 1, $"first {psnrFirst:F1} dB, first two {psnrTwo:F1} dB");
        Assert.True(double.IsPositiveInfinity(psnrAll), $"all three layers give {psnrAll:F1} dB");

        // The halving shares: the first layer's packets are about a quarter of the data, the second's half.
        List<int> offsets = SopOffsets(three);
        int data = three.Length - offsets[0];
        int firstBytes = offsets[packets / 3] - offsets[0], twoBytes = offsets[2 * packets / 3] - offsets[0];
        Assert.InRange(firstBytes, data / 8, data * 3 / 8);
        Assert.InRange(twoBytes, data * 3 / 8, data * 5 / 8);
    }

    /// <summary>A lossless stream under a target is truncated too — every pass is a candidate, the last ones first to go — and the in-house decoder reads the truncated segments exactly as the allocator counted them.</summary>
    [Fact]
    public void ALosslessStream_UnderATarget_IsTruncated_AndDecodesToTheAllocatedQuality()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 1, 8383);
        byte[] whole = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, Options(1, sop: false));
        int target = whole.Length / 2;
        byte[] half = JpxEncoder.EncodeLossless(planes, 64, 48, 8, false, Options(1, target, sop: false));
        Assert.True(half.Length <= target && half.Length > target * 9 / 10, $"{half.Length} bytes for a target of {target} (whole {whole.Length})");

        Assert.True(JpxDecoder.TryDecode(half, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        double psnr = JpxEncoderTestImages.Psnr(planes, new[] { result.Image!.Samples[0] }, 8);
        Assert.True(psnr >= 30 && !double.IsPositiveInfinity(psnr), $"half the bytes give {psnr:F1} dB");
    }

    /// <summary>A target below the headers cannot be met; the codestream is the headers and empty packets, and still parses.</summary>
    [Fact]
    public void ATargetBelowTheHeaders_GivesEmptyPackets_NotAnError()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(32, 32, 1, 9);
        byte[] tiny = JpxEncoder.EncodeLossless(planes, 32, 32, 8, false, Options(1, target: 10, sop: false));
        Assert.True(JpxCodestreamParser.TryParse(tiny, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        Assert.Equal(4, tile.BytesConsumed);   // one zero-length packet per resolution level
        Assert.True(tiny.Length < 120, $"{tiny.Length} bytes");
    }

    /// <summary>Two tiles share a target by area, and each tile's packets fit its share.</summary>
    [Fact]
    public void TilesShareATarget_ByArea()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(96, 32, 1, 4545);
        var options = Options(1, target: 1500, sop: false);
        options.TileWidth = 64;
        options.TileHeight = 32;
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 96, 32, 8, false, options);
        Assert.True(codestream.Length <= 1500 && codestream.Length > 1300, $"{codestream.Length} bytes");
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(2, cs.Tiles.Count);
        long[] sizes = new long[2];
        for (int t = 0; t < 2; t++)
        {
            Assert.True(JpxTier2Decoder.TryDecodeTile(cs, t, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
            sizes[t] = tile.DataEnd - tile.DataStart;
        }
        // The first tile is 64 wide, the second 32: twice the area, about twice the bytes.
        Assert.InRange((double)sizes[0] / sizes[1], 1.5, 2.6);
    }

    [Fact]
    public void Refuses_LayerCountsAndTargets_OutOfRange()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(8, 8, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, Options(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, Options(65536)));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, Options(1, target: -1)));
        byte[] most = JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, Options(65535));   // the most layers Table A.14 allows
        int mostCod = IndexOf(most, new byte[] { 0xFF, 0x52 });
        Assert.Equal((0xFF, 0xFF), (most[mostCod + 6], most[mostCod + 7]));   // SGcod's layer count, 65535
        Assert.True(JpxDecoder.TryDecode(most, 0, out JpxDecodeResult mostDecoded, out string mostReason), mostReason);
        Assert.Equal(planes[0], mostDecoded.Image!.Samples[0]);
    }

    /// <summary>The hull keeps only the passes worth stopping after: slopes strictly decrease, and a pass that adds bytes for nothing is skipped.</summary>
    [Fact]
    public void Hull_KeepsDecreasingSlopes_AndSkipsFruitlessPasses()
    {
        var code = new JpxBlockCode(new byte[100], 3, 7, new[] { 10, 12, 20, 30, 40, 40, 60 }, new[] { 100.0, 100, 150, 190, 200, 220, 221 });
        JpxRateControl.Hull hull = JpxRateControl.HullOf(code, 2);
        // Pass 1 adds two bytes for nothing; pass 5 removes 40 for no bytes (an infinite slope) and
        // absorbs pass 4, whose 2 per byte was below the 6 per byte the two make together.
        Assert.Equal(new[] { 1, 3, 4, 6, 7 }, hull.Ends);
        Assert.Equal(20.0, hull.Slopes[0]);                 // 200 over 10 bytes
        Assert.Equal(10.0, hull.Slopes[1]);                 // 100 over 10 bytes
        Assert.Equal(8.0, hull.Slopes[2]);                  // 80 over 10 bytes
        Assert.Equal(6.0, hull.Slopes[3]);                  // 60 over 10 bytes (passes 4 and 5 together)
        Assert.Equal(0.1, hull.Slopes[4], 9);               // 2 over 20 bytes
        for (int i = 1; i < hull.Slopes.Length; i++) Assert.True(hull.Slopes[i] < hull.Slopes[i - 1]);
        Assert.Equal((0, 1, 4, 6, 7), (hull.EndAt(21), hull.EndAt(20), hull.EndAt(7), hull.EndAt(6), hull.EndAt(0)));

        // A fruitless last pass is not a point either: bytes for nothing have no slope worth a layer, even the last.
        JpxRateControl.Hull trailing = JpxRateControl.HullOf(new JpxBlockCode(new byte[20], 2, 4, new[] { 5, 10, 15, 20 }, new[] { 50.0, 80, 80, 80 }), 1);
        Assert.Equal(new[] { 1, 2 }, trailing.Ends);
        Assert.Equal(2, trailing.EndAt(0));
    }

    /// <summary>The weights are measured, not tabled: the 9-7 bands' gains are the known norms, the RCT's and ICT's the columns of the inverse transforms.</summary>
    [Fact]
    public void SynthesisGains_AreTheTransformsOwn()
    {
        // The one-dimensional 9-7 synthesis bases, computed apart from this code by lifting an
        // impulse through Table F.4's steps in Python: level 1 low-pass norm² 1.9659 (nine taps
        // 1.115, 0.591, −0.058, −0.091), high-pass 0.5202; level 2 low-pass 4.1224, high-pass
        // 0.9672. A band's gain is the product of its two directions' norms². (the reference decoder's norm
        // table agrees to 1 % once its doubled high-pass coefficients are allowed for — 1.965,
        // 2.022 / 2, 2.080 / 4 at level 1 — except its 4.177 for the level-2 LL, which is 4.122.)
        double[][] one = JpxSynthesisGain.Bands(1, reversible: false);
        AssertNear(1.9659 * 1.9659, one[0][0]);
        AssertNear(1.9659 * 0.5202, one[1][0]);   // HL: high in x, low in y
        Assert.Equal(one[1][0], one[1][1], 9);
        AssertNear(0.5202 * 0.5202, one[1][2]);
        double[][] two = JpxSynthesisGain.Bands(2, reversible: false);
        AssertNear(4.1224 * 4.1224, two[0][0]);
        AssertNear(4.1224 * 0.9672, two[1][0]);
        AssertNear(0.9672 * 0.9672, two[1][2]);
        Assert.Equal(one[1][0], two[2][0], 9);   // level 1's bands are the same whatever the depth

        // The 5-3: the level-1 low-pass basis is ½ 1 ½ (norm² 1.5), the high-pass −⅛ −¼ ¾ −¼ −⅛ (0.71875); level 2 LL 2.75, HL 1.592, HH 0.9218 (2-D norms).
        double[][] reversible = JpxSynthesisGain.Bands(2, reversible: true);
        Assert.Equal(1.5 * 0.71875, reversible[2][0], 6);
        Assert.Equal(0.71875 * 0.71875, reversible[2][2], 6);
        AssertNorm(2.75, reversible[0][0]);
        AssertNorm(1.592, reversible[1][0]);
        AssertNorm(0.9218, reversible[1][2]);

        // Past the measured levels each direction's gain doubles per level, so a band's quadruples.
        double[][] deep = JpxSynthesisGain.Bands(JpxSynthesisGain.MeasuredLevels + 2, reversible: true);
        Assert.Equal(4 * deep[2][0], deep[1][0], 6);
        Assert.Equal(4 * deep[2][2], deep[1][2], 6);

        Assert.Equal(3.0, JpxSynthesisGain.ComponentTransform(0, reversible: false), 9);
        Assert.Equal(0.34413 * 0.34413 + 1.772 * 1.772, JpxSynthesisGain.ComponentTransform(1, reversible: false), 6);
        Assert.Equal(1.402 * 1.402 + 0.71414 * 0.71414, JpxSynthesisGain.ComponentTransform(2, reversible: false), 6);
        Assert.Equal(3.0, JpxSynthesisGain.ComponentTransform(0, reversible: true), 9);
        Assert.Equal(0.6875, JpxSynthesisGain.ComponentTransform(1, reversible: true), 6);   // −¼, −¼, ¾
        Assert.Equal(1.0, JpxSynthesisGain.ComponentTransform(3, reversible: true));
    }

    /// <summary>The two-dimensional norm (the gain's square root) within 0.01 of a value tabled to three or four figures.</summary>
    private static void AssertNorm(double tabled, double gain) =>
        Assert.InRange(Math.Sqrt(gain), tabled - 0.01, tabled + 0.01);

    /// <summary>A gain within a part in a thousand of a product of norms² given to four figures.</summary>
    private static void AssertNear(double expected, double gain) =>
        Assert.InRange(gain, expected * 0.999, expected * 1.001);

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
