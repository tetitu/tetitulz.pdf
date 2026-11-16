// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxDecoder"/> end to end on the the reference decoder-written lossless fixtures: with decomposition
/// levels, the inverse wavelet must return the source pixels exactly — the one oracle that sees
/// tier 1's high-pass contexts, the dequantisation and the wavelet together. And the thread
/// count's independence: whatever the degree, the same samples, the same deviations, the same
/// budget spent, the same exception.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class JpxDecoderTests
{
    internal static int[][] Decode(JpxLosslessFixtures.Fixture fixture, int reduction, out JpxDecodeResult result)
    {
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, reduction, out result, out string reason), reason);
        JpxTileSamples tile = Assert.Single(result.Tiles);
        return tile.Components.Select(c => c.Samples).ToArray();
    }

    /// <summary>
    /// The automatic degree follows the work (<see cref="JpxParallel.Resolve"/>): a 40x24 fixture
    /// decodes on the calling thread alone, a 1024x512 image (two workers' worth of samples) on
    /// two where the machine has them, and the same image two levels short — a sixteenth of the
    /// samples — on one again; an explicit count is taken whatever the work. A negative count is
    /// refused before the bytes are looked at.
    /// </summary>
    [Fact]
    public void TheAutomaticDegree_FollowsTheWork_AndTheReduction()
    {
        Assert.True(JpxDecoder.TryDecode(JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels.Codestream, 0, out JpxDecodeResult small, out string reason), reason);
        Assert.Equal(1, small.Threads);

        byte[] wide = JpxEncoder.EncodeLossless(JpxEncoderTestImages.Flat(1024, 512, 1, 77), 1024, 512, 8, false, new JpxEncodeOptions { Levels = 3 });
        Assert.True(JpxDecoder.TryDecode(wide, 0, out JpxDecodeResult two, out reason), reason);
        Assert.Equal(Math.Min(2, Math.Min(Environment.ProcessorCount, JpxParallel.MaxAutomaticDegree)), two.Threads);
        Assert.True(JpxDecoder.TryDecode(wide, 2, out JpxDecodeResult reduced, out reason), reason);
        Assert.Equal(1, reduced.Threads);
        Assert.True(JpxDecoder.TryDecode(wide, 0, out JpxDecodeResult asked, out reason, maxThreads: 3), reason);
        Assert.Equal(3, asked.Threads);

        // The work is over every component: 1024x256 is one worker's pixels but three workers' samples in RGB.
        byte[] rgb = JpxEncoder.EncodeLossless(JpxEncoderTestImages.Flat(1024, 256, 3, 77), 1024, 256, 8, false, new JpxEncodeOptions { Levels = 3 });
        Assert.True(JpxDecoder.TryDecode(rgb, 0, out JpxDecodeResult three, out reason), reason);
        Assert.Equal(Math.Min(3, Math.Min(Environment.ProcessorCount, JpxParallel.MaxAutomaticDegree)), three.Threads);

        Assert.Throws<ArgumentOutOfRangeException>(() => JpxDecoder.TryDecode(new byte[] { 1, 2, 3 }, 0, out _, out _, maxThreads: -1));
    }

    /// <summary>
    /// 20x12 tiles from the grid's origin over an image at (3, 2)-(43, 26): nine of them, the last
    /// column three samples wide and the last row two tall. Each tile's samples land by their
    /// bounds, in the component plane and on the grid image, and the whole equals the source.
    /// </summary>
    [Fact]
    public void Tiles_ArePlacedByTheirBounds()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel;
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        Assert.Equal(9, result.Tiles.Count);
        Assert.Equal("(3, 2)-(20, 12)", result.Tiles[0].Components[0].Bounds.ToString());
        Assert.Equal("(40, 24)-(43, 26)", result.Tiles[8].Components[0].Bounds.ToString());

        int[] expected = fixture.Planes[0].Select(b => (int)b).ToArray();
        int[] plane = result.ComponentPlane(0, out JpxRect extent);
        Assert.Equal("(3, 2)-(43, 26)", extent.ToString());
        Assert.Equal(expected, plane);
        Assert.Equal(expected, result.Image!.Samples[0]);

        // The Part 2 headers were read once for the codestream and once per tile, and kept.
        Assert.NotNull(result.Codestream.MainPart2Header);
        Assert.All(result.Codestream.Tiles.Values, t => Assert.True(t.Part2HeaderRead));
    }

    /// <summary>
    /// The image-wide ceilings are read from <see cref="PdfLimits"/> at decode time and applied
    /// before any tile is decoded, at every reduction — a reduced decode used to bypass them, and a
    /// full one met them only after the first tile's planes existed. The 40x24 fixture is 960 pixels
    /// and, with three components, 2 880 samples.
    /// </summary>
    [Fact]
    public void TheImageCeilings_AreReadFromPdfLimits_BeforeAnyTile_AtEveryReduction()
    {
        byte[] grey = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers.Codestream;
        byte[] rgb = JpxLosslessFixtures.All.First(f => f.Name == "Rgb40x24_NoMct_Blocks8").Codestream;
        try
        {
            PdfLimits.MaxImagePixelArea = 959;
            foreach (int reduction in new[] { 0, 1 })
            {
                Assert.False(JpxDecoder.TryDecode(grey, reduction, out _, out string reason));
                Assert.Contains("960 pixels; PdfLimits.MaxImagePixelArea is 959", reason, StringComparison.Ordinal);
            }
            PdfLimits.MaxImagePixelArea = 960;
            Assert.True(JpxDecoder.TryDecode(grey, 1, out _, out string ok), ok);

            PdfLimits.MaxDecompressedBytes = 2879;
            Assert.False(JpxDecoder.TryDecode(rgb, 1, out _, out string samples));
            Assert.Contains("3 components, 2880 samples; PdfLimits.MaxDecompressedBytes is 2879", samples, StringComparison.Ordinal);
            PdfLimits.MaxDecompressedBytes = 2880;
            Assert.True(JpxDecoder.TryDecode(rgb, 1, out _, out ok), ok);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// The decode's peak memory is judged against <see cref="PdfLimits.MaxJpxWorkingSetBytes"/>
    /// at the codec's measured price per sample, over the area actually decoded, before tier 1
    /// allocates anything: a 4096x4096x3 codestream of a few hundred bytes — 50 million samples,
    /// 855 MB at the price, inside every other ceiling — is refused with nothing allocated for its
    /// samples (the first tile's tier 1 values alone would be 201 MB); at exactly the price it decodes.
    /// A reduced decode is charged for the reduced area: the 40x24 grey fixture at one level short
    /// is 20x12.
    /// </summary>
    [Fact]
    public void AdversarialSmallCodestreamDeclaringALargeImage_IsRefusedByTheWorkingSetCeiling_BeforeTier1Allocates()
    {
        byte[] large = J.Concat(J.Soc, J.Siz(4096, 4096, 0, 0, 4096, 4096, 0, 0, new[] { (0x07, 1, 1), (0x07, 1, 1), (0x07, 1, 1) }), J.Cod(), J.QcdNone(),
            J.TilePart(0, 0, Array.Empty<byte>(), new byte[] { 0 }), J.Eoc);
        long price = 4096L * 4096 * 3 * JpxDecodedImage.WorkingBytesPerSample;
        byte[] grey = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers.Codestream;
        long reducedPrice = 20L * 12 * JpxDecodedImage.WorkingBytesPerSample;
        try
        {
            PdfLimits.MaxJpxWorkingSetBytes = price - 1;
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.False(JpxDecoder.TryDecode(large, 0, out _, out string reason));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Contains($"50331648 samples at reduction 0 (4096x4096, 3 planes), {price} bytes at {JpxDecodedImage.WorkingBytesPerSample} a sample; PdfLimits.MaxJpxWorkingSetBytes is {price - 1}", reason, StringComparison.Ordinal);
            Assert.True(allocated < 16 * 1024 * 1024, $"the refusal allocated {allocated} bytes; tier 1's values alone would be 201 MB");

            PdfLimits.MaxJpxWorkingSetBytes = price;
            Assert.True(JpxDecoder.TryDecode(large, 0, out _, out string ok), ok);

            PdfLimits.MaxJpxWorkingSetBytes = reducedPrice - 1;
            Assert.False(JpxDecoder.TryDecode(grey, 1, out _, out reason));
            Assert.Contains("240 samples at reduction 1 (20x12, 1 planes)", reason, StringComparison.Ordinal);
            PdfLimits.MaxJpxWorkingSetBytes = reducedPrice;
            Assert.True(JpxDecoder.TryDecode(grey, 1, out _, out ok), ok);
            Assert.False(JpxDecoder.TryDecode(grey, 0, out _, out reason));
            Assert.Contains("960 samples at reduction 0 (40x24, 1 planes)", reason, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A 4096x4096 codestream declaring 64 components is 16.7 million pixels — under the pixel-area
    /// ceiling — but over a thousand million samples: refused by <see cref="PdfLimits.MaxDecompressedBytes"/>
    /// at the defaults, before a tile's planes (twelve bytes a sample, 12 GB here) are allocated. The
    /// same file at the same size with three components is accepted.
    /// </summary>
    [Fact]
    public void AComponentCountPastTheSampleCeiling_IsRefusedBeforeDecoding()
    {
        static byte[] Codestream(int components)
        {
            var comps = new (int, int, int)[components];
            for (int i = 0; i < components; i++) comps[i] = (0x07, 1, 1);
            return J.Concat(J.Soc, J.Siz(4096, 4096, 0, 0, 4096, 4096, 0, 0, comps), J.Cod(), J.QcdNone(),
                J.TilePart(0, 0, Array.Empty<byte>(), new byte[] { 0 }), J.Eoc);
        }

        Assert.False(JpxDecoder.TryDecode(Codestream(64), 0, out _, out string reason));
        Assert.Contains("4096x4096 with 64 components, 1073741824 samples; PdfLimits.MaxDecompressedBytes is", reason, StringComparison.Ordinal);

        Assert.True(JpxDecoder.TryDecode(Codestream(3), 0, out JpxDecodeResult result, out string ok), ok);
        Assert.Contains(result.Deviations, d => d.Contains("tile 0: the tile data ends", StringComparison.Ordinal));
    }

    /// <summary>
    /// <see cref="PdfLimits.MaxJpxDecodeWork"/> is read at decode time, and a budget that runs out is
    /// a refusal with its reason, not an exception out of a <c>Try</c> method.
    /// </summary>
    [Fact]
    public void ASpentWorkBudget_IsAReason_NotAnException()
    {
        byte[] grey = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers.Codestream;
        try
        {
            PdfLimits.MaxJpxDecodeWork = 10;
            Assert.False(JpxDecoder.TryDecode(grey, 0, out JpxDecodeResult none, out string reason));
            Assert.Null(none);
            Assert.Contains("exceeded PdfLimits.MaxJpxDecodeWork (10 units) after 0 of 1 tiles", reason, StringComparison.Ordinal);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
        Assert.True(JpxDecoder.TryDecode(grey, 0, out JpxDecodeResult result, out string ok), ok);
        Assert.True(result.Budget.Spent > 10);
    }

    /// <summary>40x24 in two 5-3 levels, RPCL, three layers: every sample equal.</summary>
    [Fact]
    public void TwoLevels_ReconstructThePixelsExactly()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
        int[][] samples = Decode(fixture, 0, out JpxDecodeResult result);
        Assert.Empty(result.Deviations);
        int[] expected = fixture.Planes[0].Select(b => (int)b).ToArray();
        Assert.Equal(expected, samples[0]);
        Assert.Equal("(0, 0)-(40, 24)", result.Tiles[0].Components[0].Bounds.ToString());
        Assert.True(result.Budget.Spent > 100, $"only {result.Budget.Spent} units of work");
    }

    /// <summary>
    /// A reduction of one level yields the 20x12 image of resolution level 1 (equation B-14): the
    /// 5-3 low-pass of the source — exactly the 1LL band the §F.4 forward (the wavelet tests' own
    /// oracle, independent of the encoder) computes from the source pixels, since the forward's
    /// level 1 is the same whatever levels follow, and the 5-3 low-pass passes the DC shift through
    /// unchanged. The decoder clips a reduced level to the component's range as it does the full
    /// one (G.1.2), and the low-pass overshoots at edges (301 and −5 here), so the oracle is clipped
    /// too. A flat plane at the source's mean, which the earlier "mean within 8" assertion accepted,
    /// is not this.
    /// </summary>
    [Fact]
    public void AReduction_YieldsTheForwardsOwnLowPass_Exactly()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
        int[][] samples = Decode(fixture, 1, out JpxDecodeResult result);
        JpxComponentSamples c = result.Tiles[0].Components[0];
        Assert.Equal("(0, 0)-(20, 12)", c.Bounds.ToString());
        Assert.Equal(240, samples[0].Length);

        double[] source = fixture.Planes[0].Select(b => (double)b).ToArray();
        JpxWaveletTests.Fdwt.Decompose(new JpxRect(0, 0, 40, 24), 1, source, reversible: true, out double[][] llByLevel);
        double[] lowPass = llByLevel[1];
        Assert.Contains(lowPass, v => v > 255 || v < 0);   // the clip below is not a no-op
        Assert.Equal(lowPass.Select(v => Math.Max(0, Math.Min(255, v))).ToArray(), samples[0].Select(v => (double)v).ToArray());
        Assert.True(new HashSet<int>(samples[0]).Count > 16, "a flat plane is not the low-pass of a textured image");

        // Two levels down: 10x6. More than the levels there are: clamped to the LL band.
        Assert.Equal(60, Decode(fixture, 2, out _)[0].Length);
        Assert.Equal(60, Decode(fixture, 5, out _)[0].Length);
    }

    /// <summary>
    /// A codestream cut at 60 % of its bytes — a download cut short — decodes: the parser clamps the
    /// tile-part, tier 2 stops inside a packet and says so, and the pixels are the partial
    /// reconstruction (the earlier layers and resolution levels, RPCL), not zeros and not the full
    /// decode.
    /// </summary>
    [Fact]
    public void ATruncatedCodestream_DecodesWhatArrived_AndSaysSo()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
        byte[] cut = fixture.Codestream.Take(fixture.Codestream.Length * 6 / 10).ToArray();
        Assert.True(JpxDecoder.TryDecode(cut, 0, out JpxDecodeResult partial, out string reason), reason);

        Assert.False(partial.Codestream.EndOfCodestreamSeen);
        Assert.Contains(partial.Deviations, d => d.StartsWith("codestream: the tile-part at offset", StringComparison.Ordinal) && d.Contains("truncated", StringComparison.Ordinal));
        Assert.Contains(partial.Deviations, d => d.StartsWith("tile 0: the tile data ends", StringComparison.Ordinal));

        int[] samples = Assert.Single(partial.Tiles).Components[0].Samples;
        int[] full = Decode(fixture, 0, out _)[0];
        Assert.Equal(full.Length, samples.Length);
        Assert.NotEqual(full, samples);
        Assert.True(samples.Count(v => v != 0) > samples.Length / 2, "a truncated decode is not a blank image");
        double sourceMean = fixture.Planes[0].Average(b => (double)b);
        Assert.InRange(samples.Average(), sourceMean - 8, sourceMean + 8);
    }

    /// <summary>
    /// A signed component (Ssiz bit 7) is not level-shifted (G.1.2): the in-house encoder writes the
    /// grey fixture's pixels less 128 as an 8-bit signed plane, and the decoder returns those values
    /// — −128 to 127 — exactly, reports the sign, and narrows them to the fixture's own bytes.
    /// </summary>
    [Fact]
    public void ASignedComponent_DecodesExactly_WithoutTheLevelShift()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
        int[] signedSource = fixture.Planes[0].Select(b => b - 128).ToArray();
        Assert.Contains(signedSource, v => v < 0);
        byte[] codestream = JpxEncoder.EncodeLossless(new[] { signedSource }, 40, 24, 8, isSigned: true, new JpxEncodeOptions { Levels = 2 });

        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult result, out string reason), reason);
        JpxComponentSamples c = Assert.Single(result.Tiles).Components[0];
        Assert.True(c.IsSigned);
        Assert.Equal(signedSource, c.Samples);
        Assert.Equal(fixture.Planes[0], result.Image!.ToInterleaved8Bit());
    }

    /// <summary>Every lossless fixture goes through the same spine — wavelet, RCT (the Mct one) and all — and stays exact.</summary>
    [Theory]
    [InlineData("Gray40x24_OneBlock")]
    [InlineData("Rgb40x24_NoMct_Blocks8")]
    [InlineData("Rgb40x24_Mct_TwoLevels")]
    public void LosslessFixtures_StayExactThroughTheSpine(string name)
    {
        JpxLosslessFixtures.Fixture fixture = System.Array.Find(JpxLosslessFixtures.All, f => f.Name == name)!;
        Assert.True(fixture.Exact);
        int[][] samples = Decode(fixture, 0, out JpxDecodeResult result);
        Assert.Empty(result.Deviations);
        for (int c = 0; c < fixture.Components; c++)
            Assert.Equal(fixture.Planes[c].Select(b => (int)b).ToArray(), samples[c]);
        // The image on the grid holds the same samples, interleaved and 8-bit.
        byte[] interleaved = result.Image!.ToInterleaved8Bit();
        for (int i = 0; i < fixture.Width * fixture.Height; i++)
            for (int c = 0; c < fixture.Components; c++)
                Assert.Equal(fixture.Planes[c][i], interleaved[i * fixture.Components + c]);
    }

    /// <summary>
    /// The 9-7 fixtures against the reference decoder's own decode of them: the same dequantisation (E-3 with
    /// Table E.1's gains), lifting (F-7) and, for the RGB one, ICT (G.3) — in different floating
    /// point, so a peak difference of one after rounding is expected, and more is a defect.
    /// </summary>
    [Theory]
    [InlineData("Gray40x24_Irreversible_TwoLevels")]
    [InlineData("Rgb40x24_Ict_TwoLevels")]
    public void IrreversibleFixtures_MatchTheReferenceWithinOne(string name)
    {
        JpxLosslessFixtures.Fixture fixture = System.Array.Find(JpxLosslessFixtures.All, f => f.Name == name)!;
        Assert.False(fixture.Exact);
        int[][] samples = Decode(fixture, 0, out JpxDecodeResult result);
        Assert.Empty(result.Deviations);
        Assert.False(result.Tiles[0].Components[0].Bounds.Width == 0);
        int peak = 0, differing = 0;
        for (int c = 0; c < fixture.Components; c++)
            for (int i = 0; i < samples[c].Length; i++)
            {
                int d = System.Math.Abs(samples[c][i] - fixture.Planes[c][i]);
                if (d > peak) peak = d;
                if (d > 0) differing++;
            }
        Assert.True(peak <= 1, $"peak difference {peak} over {differing} samples");
        // Potency: a lossy decode that ignored the coefficients would sit at the DC level everywhere.
        Assert.True(new HashSet<int>(samples[0]).Count > 16);
    }

    public static IEnumerable<object[]> EveryFixture()
    {
        foreach (JpxLosslessFixtures.Fixture f in JpxLosslessFixtures.All) yield return new object[] { f.Name };
    }

    /// <summary>
    /// Two and five workers give exactly the single thread's samples on every fixture, plane for
    /// plane and on the grid image, with the same deviations and the same budget spent — the
    /// nine-tile fixture goes tiles-across-workers at both counts, the single-tile ones
    /// blocks-and-rows-across-workers — and the result records the degree it ran with, so a
    /// decoder that quietly fell back to one thread would not pass.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryFixture))]
    public void AnyThreadCount_GivesTheSingleThreadsSamples_Deviations_AndBudget(string name)
    {
        JpxLosslessFixtures.Fixture fixture = System.Array.Find(JpxLosslessFixtures.All, f => f.Name == name)!;
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult alone, out string reason, maxThreads: 1), reason);
        Assert.Equal(1, alone.Threads);
        Assert.True(alone.Budget.Spent > 0);
        foreach (int threads in new[] { 2, 5 })
        {
            Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult shared, out reason, maxThreads: threads), reason);
            Assert.Equal(threads, shared.Threads);
            Assert.Equal(alone.Tiles.Count, shared.Tiles.Count);
            for (int t = 0; t < alone.Tiles.Count; t++)
                for (int c = 0; c < fixture.Components; c++)
                {
                    Assert.Equal(alone.Tiles[t].Components[c].Bounds, shared.Tiles[t].Components[c].Bounds);
                    Assert.Equal(alone.Tiles[t].Components[c].Samples, shared.Tiles[t].Components[c].Samples);
                    Assert.Equal(alone.Image!.Samples[c], shared.Image!.Samples[c]);
                }
            Assert.Equal(alone.Deviations, shared.Deviations);
            Assert.Equal(alone.Budget.Spent, shared.Budget.Spent);
        }
        // The same holds a level down, where no image is built and every tile is placed by its bounds.
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 1, out JpxDecodeResult reducedAlone, out reason, maxThreads: 1), reason);
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 1, out JpxDecodeResult reducedShared, out reason, maxThreads: 3), reason);
        Assert.Equal(reducedAlone.ComponentPlane(0, out _), reducedShared.ComponentPlane(0, out _));
    }

    /// <summary>
    /// What the decode spends is at least the coding passes of every code-block of every tile (tier
    /// 1's unit of work, from the layouts tier 2 fills) plus something for tier 2 — on one thread
    /// and on three, the same figure.
    /// </summary>
    [Fact]
    public void TheBudgetSpent_CountsEveryCodingPass_OnEveryThreadCount()
    {
        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel;
        Assert.True(JpxCodestreamParser.TryParse(fixture.Codestream, out JpxCodestream cs, out string reason), reason);
        long passes = 0;
        foreach (int t in cs.Tiles.Keys)
        {
            Assert.True(JpxTier2Decoder.TryDecodeTile(cs, t, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
            foreach (JpxTileComponent tc in tile.Layout.Components)
                foreach (JpxResolution r in tc.Resolutions)
                    foreach (JpxPrecinct p in r.Precincts)
                        foreach (JpxPrecinctBand pb in p.Bands)
                            foreach (JpxCodeBlock block in pb.Blocks) passes += block.Passes;
        }
        Assert.True(passes > 9, $"only {passes} passes over nine tiles");

        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult alone, out reason, maxThreads: 1), reason);
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult shared, out reason, maxThreads: 3), reason);
        Assert.InRange(alone.Budget.Spent, passes + 1, passes + 10_000);
        Assert.Equal(alone.Budget.Spent, shared.Budget.Spent);
    }

    /// <summary>
    /// Deviations come tile by tile in index order whatever the thread count: with three junk bytes
    /// appended to every tile-part (Psot widened), each of the nine tiles reports "bytes follow the
    /// tile's last packet", tile 0 first and tile 8 last, on one thread and on four alike — the
    /// merge of what the workers collected is by tile, not by completion.
    /// </summary>
    [Fact]
    public void Deviations_ComeInTileOrder_OnEveryThreadCount()
    {
        byte[] padded = WithBytesAfterEachTilePart(JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel.Codestream, 3);
        Assert.True(JpxDecoder.TryDecode(padded, 0, out JpxDecodeResult alone, out string reason, maxThreads: 1), reason);
        Assert.True(JpxDecoder.TryDecode(padded, 0, out JpxDecodeResult shared, out reason, maxThreads: 4), reason);
        string[] expected = Enumerable.Range(0, 9).Select(t => $"tile {t}: 3 bytes follow the tile's last packet").ToArray();
        Assert.Equal(expected, alone.Deviations);
        Assert.Equal(expected, shared.Deviations);
        Assert.Equal(alone.Image!.Samples[0], shared.Image!.Samples[0]);
        // Control: the fixture itself has nothing to report.
        Assert.True(JpxDecoder.TryDecode(JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel.Codestream, 0, out JpxDecodeResult clean, out reason, maxThreads: 4), reason);
        Assert.Empty(clean.Deviations);
    }

    /// <summary>
    /// Appends <paramref name="extra"/> zero bytes to the data of every tile-part, widening its
    /// Psot: a raw walk of the marker segments (SOC, the main header, then each SOT whose Psot
    /// spans the tile-part from the marker on), so the parser sees a well-formed codestream whose
    /// tiles each carry bytes after their last packet.
    /// </summary>
    private static byte[] WithBytesAfterEachTilePart(byte[] codestream, int extra)
    {
        var output = new List<byte>();
        int at = 2;   // past SOC
        output.AddRange(codestream.Take(2));
        while (at + 4 <= codestream.Length && !(codestream[at] == 0xFF && codestream[at + 1] == 0x90))
        {
            int length = (codestream[at + 2] << 8) | codestream[at + 3];   // a main header segment: marker, then Lxxx bytes
            output.AddRange(codestream.Skip(at).Take(2 + length));
            at += 2 + length;
        }
        int tileParts = 0;
        while (at + 12 <= codestream.Length && codestream[at] == 0xFF && codestream[at + 1] == 0x90)
        {
            long psot = ((long)codestream[at + 6] << 24) | ((long)codestream[at + 7] << 16) | ((long)codestream[at + 8] << 8) | codestream[at + 9];
            Assert.True(psot > 0, "a tile-part without an explicit Psot");
            byte[] part = codestream.Skip(at).Take((int)psot).ToArray();
            long widened = psot + extra;
            part[6] = (byte)(widened >> 24);
            part[7] = (byte)(widened >> 16);
            part[8] = (byte)(widened >> 8);
            part[9] = (byte)widened;
            output.AddRange(part);
            output.AddRange(new byte[extra]);
            at += (int)psot;
            tileParts++;
        }
        Assert.Equal(9, tileParts);
        output.AddRange(codestream.Skip(at));   // EOC
        return output.ToArray();
    }

    /// <summary>
    /// A single tile at the grid's resolution is the image plane itself — the tile's sample array
    /// and the plane are one object, nothing copied — where a tiled image's planes are the image's
    /// own, assembled from the tiles.
    /// </summary>
    [Fact]
    public void ASingleTilesSamples_AreTheImagePlane_ATiledImagesAreCopied()
    {
        Assert.True(JpxDecoder.TryDecode(JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels.Codestream, 0, out JpxDecodeResult single, out string reason), reason);
        for (int c = 0; c < 3; c++) Assert.Same(single.Tiles[0].Components[c].Samples, single.Image!.Samples[c]);

        Assert.True(JpxDecoder.TryDecode(JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel.Codestream, 0, out JpxDecodeResult tiled, out reason), reason);
        Assert.Equal(9, tiled.Tiles.Count);
        foreach (JpxTileSamples tile in tiled.Tiles) Assert.NotSame(tile.Components[0].Samples, tiled.Image!.Samples[0]);
        Assert.Equal(JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel.Planes[0].Select(b => (int)b).ToArray(), tiled.Image!.Samples[0]);
    }

    /// <summary>
    /// A budget that tier 2 fits in and tier 1's first coding pass does not is the same refusal —
    /// the budget's reason, no exception out of <c>TryDecode</c> — from a worker on four threads as
    /// on one: for the nine-tile fixture (tiles across the workers) and the one-tile, many-block one
    /// (blocks across them). No tile completes on either path, and the reason says so. The control
    /// with the budget restored decodes. A budget exhausted during tier 2 would stop on the calling
    /// thread before any worker ran and prove nothing about the loop, so the budget is set from
    /// tier 2's own spend.
    /// </summary>
    [Theory]
    [InlineData("Gray40x24_Tiles20x12_Offset3x2_OneLevel", 9)]
    [InlineData("Rgb40x24_NoMct_Blocks8", 1)]
    public void ABudgetTier1Exceeds_IsTheSameRefusalFromAWorker_AsOnOneThread(string name, int tiles)
    {
        JpxLosslessFixtures.Fixture fixture = System.Array.Find(JpxLosslessFixtures.All, f => f.Name == name)!;
        Assert.True(JpxCodestreamParser.TryParse(fixture.Codestream, out JpxCodestream cs, out string reason), reason);
        var tier2 = new WorkBudget(PdfLimits.MaxJpxDecodeWork);
        foreach (int t in cs.Tiles.Keys)
            Assert.True(JpxTier2Decoder.TryDecodeTile(cs, t, tier2, out _, out reason), reason);
        try
        {
            PdfLimits.MaxJpxDecodeWork = tier2.Spent + 1;   // room for tier 2, none for a coding pass
            Assert.False(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult none, out string onOne, maxThreads: 1));
            Assert.Null(none);
            Assert.False(JpxDecoder.TryDecode(fixture.Codestream, 0, out none, out string onFour, maxThreads: 4));
            Assert.Null(none);
            Assert.Contains($"exceeded PdfLimits.MaxJpxDecodeWork ({tier2.Spent + 1} units) after 0 of {tiles} tiles", onOne, StringComparison.Ordinal);
            Assert.Equal(onOne, onFour);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
        Assert.True(JpxDecoder.TryDecode(fixture.Codestream, 0, out JpxDecodeResult result, out string reason2, maxThreads: 4), reason2);
        Assert.True(result.Budget.Spent > tier2.Spent + 1);
    }

    /// <summary>A negative thread count is refused before anything is parsed; 0 chooses automatically — one worker for a 40x24 image (see <see cref="TheAutomaticDegree_FollowsTheWork_AndTheReduction"/>).</summary>
    [Fact]
    public void ANegativeThreadCount_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxDecoder.TryDecode(JpxLosslessFixtures.Gray40x24_OneBlock.Codestream, 0, out _, out _, maxThreads: -1));
        Assert.True(JpxDecoder.TryDecode(JpxLosslessFixtures.Gray40x24_OneBlock.Codestream, 0, out JpxDecodeResult result, out string reason, maxThreads: 0), reason);
        Assert.Equal(JpxParallel.Resolve(0, 40 * 24), result.Threads);
        Assert.Equal(1, result.Threads);
    }
}
