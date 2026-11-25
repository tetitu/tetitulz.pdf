// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxEncoder"/> end to end. The oracle for the pixels is the reference decoder (<see cref="PillowOracle"/>),
/// an implementation this library did not write: a lossless codestream must come back sample for
/// sample. The in-house parser and tier 2 then vouch for the structure — every packet read to the
/// byte — and tier 1 alone, decoded with <see cref="JpxBlockDecoder"/>, must return the very
/// coefficients the encoder fed it.
/// </summary>
public sealed partial class JpxEncoderTests
{
    private static JpxEncodeOptions Options(int levels, int codeBlockExponent = 6, bool mct = true, string? comment = null) =>
        new() { Levels = levels, CodeBlockWidthExponent = codeBlockExponent, CodeBlockHeightExponent = codeBlockExponent, MultipleComponentTransform = mct, Comment = comment };

    private static void AssertNotFlat(int[][] planes) =>
        Assert.True(new HashSet<int>(planes[0]).Count > 16, "a flat image would pass a decoder that answers the DC level everywhere");

    private static JpxTileDecode ParseAndReadTile(byte[] codestream, out JpxCodestream cs)
    {
        Assert.True(JpxCodestreamParser.TryParse(codestream, out cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        Assert.Empty(tile.Deviations);
        Assert.Equal(tile.DataEnd - tile.DataStart, tile.BytesConsumed);
        return tile;
    }

    /// <summary>Tier 1 through the decoder, for every code-block of every band of a component: the plain coefficient (E-7) per sample, in band coordinates.</summary>
    private static Dictionary<(int Resolution, int Band), int[]> DecodeBands(JpxTileDecode tile, JpxCodestream cs, int component)
    {
        JpxTileComponent tc = tile.Layout.Components[component];
        JpxQuantization q = cs.QuantizationFor(0, component);
        var bands = new Dictionary<(int, int), int[]>();
        for (int r = 0; r < tc.Resolutions.Length; r++)
            for (int b = 0; b < tc.Resolutions[r].Bands.Length; b++)
            {
                JpxBand band = tc.Resolutions[r].Bands[b];
                int width = (int)band.Bounds.Width;
                var values = new int[width * band.Bounds.Height];
                int mb = JpxQuantizationSteps.MagnitudeBitPlanes(q, tc.Levels, r, band.Orientation);
                foreach (JpxPrecinct precinct in tc.Resolutions[r].Precincts)
                    foreach (JpxCodeBlock block in precinct.Bands[b].Blocks)
                    {
                        int[] coefficients = JpxBlockDecoder.Decode(block, tile.Data, band.Orientation, JpxCodeBlockStyle.None, mb, 0, out string problem);
                        Assert.Null(problem);
                        int w = (int)block.Bounds.Width;
                        for (int y = 0; y < block.Bounds.Height; y++)
                            for (int x = 0; x < w; x++)
                                values[(int)(block.Bounds.Y0 - band.Bounds.Y0 + y) * width + (int)(block.Bounds.X0 - band.Bounds.X0 + x)] = JpxBlockDecoder.Truncate(coefficients[y * w + x]);
                    }
                bands[(r, b)] = values;
            }
        return bands;
    }

    /// <summary>
    /// How many magnitude bit-planes any content can need. The 5-3 analysis low-pass (−⅛, ¼, ¾, ¼,
    /// −⅛) composed over any number of levels has an absolute tap sum that converges to 1.716 per
    /// dimension (1.5, 1.625, 1.6875, …), 2.94 in two, so
    /// the LL band of an R<sub>b</sub>-bit component never needs more than R<sub>b</sub> + 2 planes
    /// — in practice R<sub>b</sub> + 1, since 2.94 · 2^(R<sub>b</sub> − 1) is below 2^(R<sub>b</sub> + 1)
    /// — and the default two guard bits (G + ε<sub>b</sub> − 1 ≥ planes, E-2) hold for every input;
    /// the reason the encoder's "lossless to 35 bits for any content" stands with ε<sub>b</sub> capped at 31.
    /// Pinned by the worst-case input itself: the sign pattern of the composed filter drives the LL
    /// band past 2^8 (nine planes for 8-bit samples, a flat image's LL is its level-shifted value)
    /// and the QCD still carries the default guard bits.
    /// </summary>
    [Fact]
    public void TheWorstCaseInput_ReachesOnePlaneOverTheRange_AndTheDefaultGuardBitsHold()
    {
        double[] h = { -1 / 8.0, 2 / 8.0, 6 / 8.0, 2 / 8.0, -1 / 8.0 };
        double[] g = { 1.0 };
        int spacing = 1;
        for (int l = 0; l < 3; l++)   // three levels: a 29-tap composed filter, inside a 64-wide image
        {
            var up = new double[(h.Length - 1) * spacing + 1];
            for (int i = 0; i < h.Length; i++) up[i * spacing] = h[i];
            var next = new double[g.Length + up.Length - 1];
            for (int i = 0; i < g.Length; i++) for (int j = 0; j < up.Length; j++) next[i + j] += g[i] * up[j];
            g = next;
            spacing *= 2;
        }
        Assert.Equal(29, g.Length);
        var plane = new int[64 * 64];
        int offset = (64 - g.Length) / 2;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                double gy = y >= offset && y - offset < g.Length ? g[y - offset] : 1, gx = x >= offset && x - offset < g.Length ? g[x - offset] : 1;
                plane[y * 64 + x] = gy * gx >= 0 ? 255 : 0;
            }
        double worst = LargestLowPassMagnitude(plane, 3, out int worstGuardBits);
        Assert.True(worst >= 256 && worst < 512, $"the worst-case LL magnitude is {worst}: nine planes were expected for 8-bit samples");
        Assert.Equal(JpxEncoder.DefaultGuardBits, worstGuardBits);

        // The control: a flat image's LL is its level-shifted value, seven planes and the same guard bits.
        double flat = LargestLowPassMagnitude(JpxEncoderTestImages.Flat(64, 64, 1, 200)[0], 3, out int flatGuardBits);
        Assert.Equal(72.0, flat);
        Assert.Equal(JpxEncoder.DefaultGuardBits, flatGuardBits);
    }

    /// <summary>The LL band's largest magnitude after <paramref name="levels"/> levels of the reversible wavelet, read back through the decoder's own tier 1 at that reduction (the band's integers).</summary>
    private static double LargestLowPassMagnitude(int[] plane, int levels, out int guardBits)
    {
        byte[] codestream = JpxEncoder.EncodeLossless(new[] { plane }, 64, 64, 8, false, new JpxEncodeOptions { Levels = levels });
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        guardBits = cs.Main.Quantization.GuardBits;
        var budget = new WorkBudget(PdfLimits.MaxJpxDecodeWork);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, budget, out JpxTileDecode tile, out reason), reason);
        double[] ll = JpxDecoder.Reconstruct(cs, tile, 0, levels, budget, new List<string>(), out _);
        double largest = 0;
        foreach (double v in ll) largest = Math.Max(largest, Math.Abs(v));
        return largest;
    }

    /// <summary>
    /// With no decomposition level a coefficient is the sample less the DC shift, so tier 1 alone has
    /// an oracle the test computes by hand: one packet, read to the byte; one 40x24 block; every
    /// coefficient back exactly.
    /// </summary>
    [Fact]
    public void LevelZero_OnePacketReadToTheByte_AndTier1ReturnsTheShiftedSamples()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 1, 99);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, Options(0));
        JpxTileDecode tile = ParseAndReadTile(codestream, out JpxCodestream cs);
        Assert.Equal(1, tile.PacketsRead);
        Assert.True(tile.BytesConsumed > 100, $"{tile.BytesConsumed} bytes of tile data");

        JpxCodeBlock block = Assert.Single(tile.Layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        Assert.True(block.Included);
        Assert.Equal(1, block.Chunks.Count);
        int[] expected = Array.ConvertAll(planes[0], s => s - 128);
        Assert.Equal(expected, DecodeBands(tile, cs, 0)[(0, 0)]);
        AssertNotFlat(planes);
    }

    /// <summary>
    /// Two levels, three components through the RCT, 8x8 blocks: nine packets in LRCP, 26 blocks
    /// per component, and every block's coefficients equal to the DC shift, RCT (the test's own
    /// G-3 to G-5) and forward wavelet of the source — the tier-1 and tier-2 halves exact on their own.
    /// </summary>
    [Fact]
    public void TwoLevelsRgb_EveryPacketReadToTheByte_AndEveryBlockExact()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 3, 31337);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, Options(2, codeBlockExponent: 3));
        JpxTileDecode tile = ParseAndReadTile(codestream, out JpxCodestream cs);
        Assert.Equal(9, tile.PacketsRead);

        var shifted = new int[3][];
        for (int c = 0; c < 3; c++) shifted[c] = Array.ConvertAll(planes[c], s => s - 128);
        var rct = new int[3][];
        for (int c = 0; c < 3; c++) rct[c] = new int[shifted[c].Length];
        for (int i = 0; i < shifted[0].Length; i++)
        {
            int r = shifted[0][i], g = shifted[1][i], b = shifted[2][i];
            rct[0][i] = (int)Math.Floor((r + 2 * g + b) / 4.0);
            rct[1][i] = b - g;
            rct[2][i] = r - g;
        }
        for (int c = 0; c < 3; c++)
        {
            int[][][] expected = JpxForwardWavelet.Reversible(rct[c], new JpxRect(0, 0, 40, 24), 2);
            Dictionary<(int, int), int[]> decoded = DecodeBands(tile, cs, c);
            int blocks = 0;
            foreach (JpxResolution res in tile.Layout.Components[c].Resolutions)
                foreach (JpxPrecinct p in res.Precincts)
                    foreach (JpxPrecinctBand pb in p.Bands) blocks += pb.Blocks.Length;
            Assert.Equal(26, blocks);
            for (int r = 0; r <= 2; r++)
                for (int b = 0; b < expected[r].Length; b++)
                    Assert.Equal(expected[r][b], decoded[(r, b)]);
        }
        // Potency: the chrominance planes are not zero — the RCT did something the check can see.
        Assert.Contains(rct[1], v => v != 0);
    }

    [Fact]
    public void Headers_AreWhatTheParserReads_AndTheRawBytesShow()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 3, 5);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, Options(2, codeBlockExponent: 5, comment: "Tetitulz.Pdf"));

        Assert.Equal(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }, codestream.Take(4).ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, codestream.Skip(codestream.Length - 2).ToArray());

        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.True(cs.EndOfCodestreamSeen);
        Assert.Empty(cs.Deviations);
        Assert.Equal((40L, 24L, 0L, 0L, 40L, 24L, 3), (cs.Size.Xsiz, cs.Size.Ysiz, cs.Size.XOsiz, cs.Size.YOsiz, cs.Size.XTsiz, cs.Size.YTsiz, cs.Size.Components.Length));
        Assert.Equal((8, false, 1, 1), (cs.Size.Components[2].Precision, cs.Size.Components[2].IsSigned, cs.Size.Components[2].XRsiz, cs.Size.Components[2].YRsiz));

        JpxCodingDefault cod = cs.CodingFor(0);
        Assert.Equal((JpxProgressionOrder.Lrcp, 1, true, false, false), (cod.ProgressionOrder, cod.Layers, cod.MultipleComponentTransform, cod.SopAllowed, cod.EphUsed));
        Assert.Equal((2, 5, 5, JpxCodeBlockStyle.None, true, false), (cod.Component.DecompositionLevels, cod.Component.CodeBlockWidthExponent,
            cod.Component.CodeBlockHeightExponent, cod.Component.CodeBlockStyle, cod.Component.Reversible, cod.Component.HasUserPrecincts));

        JpxQuantization qcd = cs.QuantizationFor(0, 1);
        Assert.Equal((JpxQuantizationStyle.None, 2), (qcd.Style, qcd.GuardBits));
        // Table A.30's order: the LL band, then HL, LH, HH of each level from the lowest resolution; ε_b = R_I + log2 gain_b (E-4, Table E.1).
        Assert.Equal(new[] { 8, 9, 9, 10, 9, 9, 10 }, qcd.Exponents);
        Assert.All(qcd.Mantissas, m => Assert.Equal(0, m));

        // The COM's Rcom (1, Latin) and text are in the raw bytes, before the first SOT.
        int com = IndexOf(codestream, new byte[] { 0xFF, 0x64 });
        Assert.True(com > 0);
        Assert.Equal(16, (codestream[com + 2] << 8) | codestream[com + 3]);   // Lcom: 2 + 2 + 12
        Assert.Equal(new byte[] { 0x00, 0x01 }, codestream.Skip(com + 4).Take(2).ToArray());   // Rcom 1: Latin
        Assert.Equal("Tetitulz.Pdf", System.Text.Encoding.ASCII.GetString(codestream, com + 6, 12));

        // One tile-part whose Psot runs from the SOT to the EOC.
        JpxTile tile = Assert.Single(cs.Tiles.Values);
        JpxTilePart part = Assert.Single(tile.Parts);
        int sot = IndexOf(codestream, new byte[] { 0xFF, 0x90 });
        long psot = ((long)codestream[sot + 6] << 24) | ((long)codestream[sot + 7] << 16) | ((long)codestream[sot + 8] << 8) | codestream[sot + 9];
        Assert.Equal(codestream.Length - 2 - sot, psot);
        Assert.Equal((0, 1), (codestream[sot + 10], codestream[sot + 11]));   // TPsot, TNsot
        Assert.Equal(codestream.Length - 2, part.Data.End);
    }

    [Fact]
    public void Refuses_WhatTheSyntaxOrTheSamplesCannotCarry()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(8, 8, 1, 1);
        Assert.Contains("72 samples", Assert.Throws<ArgumentException>(() => JpxEncoder.EncodeLossless(planes, 8, 9, 8, false, null)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 39, false, null));   // T.800's deepest is 38
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { Levels = 33 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { CodeBlockWidthExponent = 7, CodeBlockHeightExponent = 7 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossy(planes, 8, 8, 8, false, new JpxEncodeOptions { QuantizationStep = 0 }));

        int[][] outOfRange = JpxEncoderTestImages.Pattern(8, 8, 1, 1);
        outOfRange[0][5] = 256;
        Assert.Contains("sample 5 is 256", Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(outOfRange, 8, 8, 8, false, null)).Message, StringComparison.Ordinal);
        outOfRange[0][5] = -1;
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(outOfRange, 8, 8, 8, false, null));
        // A signed 8-bit image admits −1 and refuses 128.
        Assert.True(JpxHeader.IsRawCodestream(JpxEncoder.EncodeLossless(outOfRange, 8, 8, 8, true, null)));
        outOfRange[0][5] = 128;
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(outOfRange, 8, 8, 8, true, null));

        // The acceptance control: the defaults, and a one-component image asking for the transform, encode.
        Assert.True(JpxHeader.IsRawCodestream(JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, null)));
        byte[] oneComponent = JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { MultipleComponentTransform = true });
        Assert.True(JpxCodestreamParser.TryParse(oneComponent, out JpxCodestream cs, out string reason), reason);
        Assert.False(cs.CodingFor(0).MultipleComponentTransform);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
            if (match) return i;
        }
        return -1;
    }
}
