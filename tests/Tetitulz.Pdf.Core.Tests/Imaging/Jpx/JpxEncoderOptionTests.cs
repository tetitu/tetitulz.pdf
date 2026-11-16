// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The options that widen what <see cref="JpxEncoder"/> writes — precincts, the five progression
/// orders, SOP and EPH, several tiles, every code-block size, 16-bit and signed samples — each
/// decoded exactly by the reference decoder and read to the byte by the in-house tier 2.
/// </summary>
public sealed partial class JpxEncoderOptionTests
{
    private static JpxCodestream Parse(byte[] codestream)
    {
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.Empty(cs.Deviations);
        return cs;
    }

    /// <summary>
    /// Vertical causality (T.800 §D.7) for both coders: announced in the style byte, decoded exactly, and coded
    /// as it says — the Part 1 passes and the HT SigProp passes of a size-targeted encode read no stripe below,
    /// so their bytes differ from the plain encode's while the HT cleanup alone, which §7.4's rule does not
    /// touch, is the same bytes but the style.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerticallyCausal_IsAnnounced_DecodesExactly_AndChangesWhatItReads(bool ht)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(48, 40, 1, 61);
        JpxEncodeOptions Options(bool causal, long target) => new JpxEncodeOptions
        {
            HighThroughput = ht, VerticallyCausal = causal, Levels = 2, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4, TargetBytes = target,
        };
        byte[] causal = JpxEncoder.EncodeLossless(planes, 48, 40, 8, false, Options(true, 0));
        Assert.True(JpxDecoder.TryDecode(causal, 0, out JpxDecodeResult decoded, out string reason), reason);
        Assert.Empty(decoded.Deviations);
        Assert.Equal(planes[0], decoded.ComponentPlane(0, out _));
        JpxCodeBlockStyle expected = JpxCodeBlockStyle.VerticallyCausal | (ht ? JpxCodeBlockStyle.HighThroughput : JpxCodeBlockStyle.None);
        Assert.Equal(expected, Parse(causal).Main.Coding.Component.CodeBlockStyle);

        // Compared with the plain encode's, the style byte — the twelfth after COD's marker (Table A.12) — set aside.
        byte[] Unstyled(byte[] codestream)
        {
            int cod = Enumerable.Range(2, codestream.Length - 3).First(i => codestream[i] == 0xFF && codestream[i + 1] == 0x52);
            byte[] copy = (byte[])codestream.Clone();
            copy[cod + 12] = (byte)(ht ? JpxCodeBlockStyle.HighThroughput : JpxCodeBlockStyle.None);
            return copy;
        }
        byte[] plain = JpxEncoder.EncodeLossless(planes, 48, 40, 8, false, Options(false, 0));
        if (ht) Assert.Equal(plain, Unstyled(causal));   // a cleanup pass alone is the same bytes
        else Assert.NotEqual(plain, Unstyled(causal));

        // Under a size target a causal encode decodes too; its HT sets' SigProp passes are asked for causally
        // (JpxHtEncodingTests holds the candidates to that, on a block where the cut row decides).
        long target = plain.Length / 3;
        byte[] cut = JpxEncoder.EncodeLossless(planes, 48, 40, 8, false, Options(true, target));
        Assert.True(JpxDecoder.TryDecode(cut, 0, out JpxDecodeResult lossy, out reason), reason);
        Assert.Empty(lossy.Deviations);
        if (!ht) Assert.NotEqual(JpxEncoder.EncodeLossless(planes, 48, 40, 8, false, Options(false, target)), Unstyled(cut));
    }

    /// <summary>16-bit RGB through the RCT — the chrominance a bit wider still — every block exact through tier 1, as the reference decoder-via-an independent tool narrows a 16-bit RGB to 8.</summary>
    [Fact]
    public void SixteenBitRgb_ThroughTheRct_EveryBlockExact()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 3, 65535, 16);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 16, false, new JpxEncodeOptions { Levels = 2, CodeBlockWidthExponent = 4, CodeBlockHeightExponent = 4 });
        JpxCodestream cs = Parse(codestream);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out string reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        Assert.Equal(new[] { 16, 17, 17, 18, 17, 17, 18 }, cs.QuantizationFor(0, 0).Exponents);

        var rct = new int[3][];
        for (int c = 0; c < 3; c++) rct[c] = Array.ConvertAll(planes[c], s => s - 32768);
        JpxForwardComponentTransform.Rct(rct[0], rct[1], rct[2]);
        for (int c = 0; c < 3; c++)
        {
            int[][][] expected = JpxForwardWavelet.Reversible(rct[c], new JpxRect(0, 0, 40, 24), 2);
            JpxTileComponent tc = tile.Layout.Components[c];
            for (int r = 0; r <= 2; r++)
                for (int b = 0; b < tc.Resolutions[r].Bands.Length; b++)
                {
                    JpxBand band = tc.Resolutions[r].Bands[b];
                    int mb = JpxQuantizationSteps.MagnitudeBitPlanes(cs.QuantizationFor(0, c), 2, r, band.Orientation);
                    int width = (int)band.Bounds.Width;
                    var values = new int[width * band.Bounds.Height];
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
                    Assert.Equal(expected[r][b], values);
                }
        }
        Assert.Contains(rct[1], v => Math.Abs(v) > 255);   // potency: chrominance beyond 8 bits
    }

    [Fact]
    public void Refuses_PrecinctAndTileOptionsOutsideTheSyntax()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(8, 8, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { Precincts = new[] { (1, 1), (0, 4) } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { Precincts = new[] { (16, 1) } }));
        Assert.Throws<ArgumentException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { Precincts = new (int, int)[0] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { TileWidth = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { TileWidth = -1, TileHeight = -1 }));
        // The controls: a zero exponent at resolution 0 alone, and 4x4 tiles, are allowed.
        Assert.True(JpxHeader.IsRawCodestream(JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { Levels = 1, Precincts = new[] { (0, 0), (1, 1) } })));
        Assert.True(JpxHeader.IsRawCodestream(JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { TileWidth = 4, TileHeight = 4 })));
    }
}
