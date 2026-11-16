// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxEncoderOptionTests
{
    /// <summary>
    /// The independent decoder reports a signed component with 2<sup>precision − 1</sup> added, and a precision other
    /// than 8 or 16 scaled up to the nearest of the two (a 12-bit sample times 16, a 4-bit one
    /// times 16, a 1-bit one times 128) — the oracle's conventions, undone here.
    /// </summary>
    private static void AssertExact(byte[] codestream, int[][] planes, int width, int height, int precision = 8, bool isSigned = false)
    {
        int offset = isSigned ? 1 << (precision - 1) : 0;
        int scale = 1 << ((precision <= 8 ? 8 : 16) - precision);
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(codestream, ".j2k");
        Assert.Equal((width, height, planes.Length), (decoded.Width, decoded.Height, decoded.Planes.Length));
        for (int c = 0; c < planes.Length; c++)
            Assert.Equal(Array.ConvertAll(planes[c], s => (s + offset) * scale), decoded.Planes[c]);
        Assert.True(new HashSet<int>(planes[0]).Count >= Math.Min(16, 1 << precision), "a flat image proves little");
    }

    /// <summary>Every tile's packets read to the byte, with no deviation; returns the packets read per tile.</summary>
    private static int[] ReadEveryTile(JpxCodestream cs)
    {
        var packets = new int[(int)cs.Size.NumTiles];
        for (int t = 0; t < packets.Length; t++)
        {
            Assert.True(JpxTier2Decoder.TryDecodeTile(cs, t, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out string reason), reason);
            Assert.True(tile.Complete, tile.StopReason);
            Assert.Empty(tile.Deviations);
            Assert.Equal(tile.DataEnd - tile.DataStart, tile.BytesConsumed);
            packets[t] = tile.PacketsRead;
        }
        return packets;
    }

    /// <summary>
    /// Signalled precincts (Scod bit 0, Table A.21): 2x2 at resolution 0, 8x8 then 16x16 above,
    /// which also bounds the code-blocks (B-17, B-18). The LL band 10x6 is 5x3 precincts; level 2's
    /// 20x12 resolution in 8x8 precincts is 3x2; level 1's 40x24 in 16x16 is 3x2 — 26 packets.
    /// </summary>
    [Fact]
    public void Precincts_PartitionEveryResolution_AndAnIndependentDecoderReadsThemBack()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 1, 61);
        var options = new JpxEncodeOptions { Levels = 2, Precincts = new[] { (1, 1), (3, 3), (4, 4) } };
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, options);
        AssertExact(codestream, planes, 40, 24);

        JpxCodestream cs = Parse(codestream);
        JpxComponentCoding coding = cs.CodingFor(0).Component;
        Assert.True(coding.HasUserPrecincts);
        Assert.Equal((1, 3, 4), (coding.PrecinctWidthExponent(0), coding.PrecinctHeightExponent(1), coding.PrecinctWidthExponent(2)));
        Assert.Equal(new[] { 15 + 6 + 6 }, ReadEveryTile(cs));
        Assert.True(JpxTileLayout.TryBuild(cs, 0, out JpxTileLayout layout, out string reason), reason);
        Assert.Equal((1, 2, 3), (layout.Components[0].Resolutions[0].CodeBlockWidthExponent,
            layout.Components[0].Resolutions[1].CodeBlockWidthExponent, layout.Components[0].Resolutions[2].CodeBlockHeightExponent));
    }

    /// <summary>Each of Table A.16's progression orders, with several precincts per resolution so the position-driven ones (B-20) have positions to visit.</summary>
    [Theory]
    [InlineData(0)]   // LRCP
    [InlineData(1)]   // RLCP
    [InlineData(2)]   // RPCL
    [InlineData(3)]   // PCRL
    [InlineData(4)]   // CPRL
    public void EveryProgressionOrder_DecodesExactly(int orderValue)
    {
        var order = (JpxProgressionOrder)orderValue;
        int[][] planes = JpxEncoderTestImages.Pattern(48, 40, 3, 7 + orderValue);
        var options = new JpxEncodeOptions { Levels = 2, ProgressionOrder = order, Precincts = new[] { (3, 3), (4, 4) } };
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 48, 40, 8, false, options);
        AssertExact(codestream, planes, 48, 40);

        JpxCodestream cs = Parse(codestream);
        Assert.Equal(order, cs.CodingFor(0).ProgressionOrder);
        // r0 12x10 in 8x8: 2x2; r1 24x20 in 16x16: 2x2; r2 48x40 in 16x16: 3x3 — per component.
        Assert.Equal(new[] { 3 * (4 + 4 + 9) }, ReadEveryTile(cs));
    }

    /// <summary>SOP marker segments before every packet and EPH after every header (§A.8), announced in Scod and counted in the raw bytes.</summary>
    [Fact]
    public void SopAndEph_AreWrittenAndAnnounced()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 3, 88);
        var options = new JpxEncodeOptions { Levels = 2, Sop = true, Eph = true, CodeBlockWidthExponent = 3, CodeBlockHeightExponent = 3 };
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, options);
        AssertExact(codestream, planes, 40, 24);

        JpxCodestream cs = Parse(codestream);
        Assert.True(cs.CodingFor(0).SopAllowed);
        Assert.True(cs.CodingFor(0).EphUsed);
        Assert.Equal(new[] { 9 }, ReadEveryTile(cs));
        JpxTilePart part = cs.Tiles[0].Parts[0];
        int sops = 0, ephs = 0;
        for (long i = part.Data.Start; i + 1 < part.Data.End; i++)
        {
            if (codestream[i] != 0xFF) continue;
            if (codestream[i + 1] == 0x91) sops++;
            if (codestream[i + 1] == 0x92) ephs++;
        }
        Assert.True(sops >= 9 && ephs >= 9, $"{sops} SOP, {ephs} EPH in the tile data");
        // The Nsop of the fourth packet's SOP is 3 — read where tier 2 found the packet.
        Assert.Equal(new byte[] { 0xFF, 0x91, 0x00, 0x04, 0x00, 0x00 }, codestream.Skip((int)part.Data.Start).Take(6).ToArray());

        // The control: without the options neither marker appears.
        byte[] plain = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, new JpxEncodeOptions { Levels = 2 });
        cs = Parse(plain);
        Assert.False(cs.CodingFor(0).SopAllowed || cs.CodingFor(0).EphUsed);
    }

    /// <summary>16x16 tiles over 40x24: six tiles, the right-hand and bottom ones clipped (B-6 to B-10), each a tile-part with its own SOT.</summary>
    [Fact]
    public void Tiles_EachGetATilePart_AndAnIndependentDecoderAssemblesThem()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 3, 1616);
        var options = new JpxEncodeOptions { Levels = 2, TileWidth = 16, TileHeight = 16 };
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, options);
        AssertExact(codestream, planes, 40, 24);

        JpxCodestream cs = Parse(codestream);
        Assert.Equal((3L, 2L, 16L, 16L), (cs.Size.NumXTiles, cs.Size.NumYTiles, cs.Size.XTsiz, cs.Size.YTsiz));
        Assert.Equal(6, cs.Tiles.Count);
        Assert.Equal(new[] { 9, 9, 9, 9, 9, 9 }, ReadEveryTile(cs));
        Assert.Equal(new JpxRect(32, 16, 40, 24), JpxGeometry.Tile(cs.Size, 5));
        // Tile-parts in raster order, each Psot covering exactly its tile-part.
        for (int t = 0; t < 6; t++)
        {
            JpxTilePart part = Assert.Single(cs.Tiles[t].Parts);
            long next = t < 5 ? cs.Tiles[t + 1].Parts[0].Data.Start - 14 : codestream.Length - 2;
            Assert.Equal(next, part.Data.End);
        }
    }

    /// <summary>Everything at once: tiles, precincts, RPCL, SOP and EPH, small blocks, lossy — the options compose.</summary>
    [Fact]
    public void EveryOptionTogether_StillDecodes()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(50, 34, 3, 2222);
        var options = new JpxEncodeOptions
        {
            Levels = 3, TileWidth = 24, TileHeight = 20, Precincts = new[] { (2, 2), (3, 3) }, ProgressionOrder = JpxProgressionOrder.Rpcl,
            Sop = true, Eph = true, CodeBlockWidthExponent = 3, CodeBlockHeightExponent = 4, Comment = "all options",
        };
        byte[] lossless = JpxEncoder.EncodeLossless(planes, 50, 34, 8, false, options);
        AssertExact(lossless, planes, 50, 34);
        int[] packets = ReadEveryTile(Parse(lossless));
        Assert.Equal(6, packets.Length);
        Assert.All(packets, p => Assert.True(p > 12, $"{p} packets"));

        options.QuantizationStep = 2;
        byte[] lossy = JpxEncoder.EncodeLossy(planes, 50, 34, 8, false, options);
        double psnr = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(lossy, ".j2k").Planes, 8);
        Assert.True(psnr > 36, $"PSNR {psnr:F1} dB");
        ReadEveryTile(Parse(lossy));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 5)]
    [InlineData(5, 3)]
    [InlineData(6, 2)]
    [InlineData(4, 6)]
    [InlineData(6, 6)]
    public void EveryCodeBlockSize_DecodesExactly(int xcb, int ycb)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(70, 40, 1, xcb * 10 + ycb);
        var options = new JpxEncodeOptions { Levels = 2, CodeBlockWidthExponent = xcb, CodeBlockHeightExponent = ycb };
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 70, 40, 8, false, options);
        AssertExact(codestream, planes, 70, 40);
        JpxComponentCoding coding = Parse(codestream).CodingFor(0).Component;
        Assert.Equal((xcb, ycb), (coding.CodeBlockWidthExponent, coding.CodeBlockHeightExponent));
    }

    /// <summary>
    /// Signed samples take no DC shift (G.1.1) and set Ssiz's sign bit; the independent decoder reports them as
    /// the independent decoder's unsigned modes by adding 2<sup>precision − 1</sup>, which is what the offset expects.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    public void SignedSamples_DecodeExactly(int precision)
    {
        int half = 1 << (precision - 1);
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 1, precision, precision);
        for (int i = 0; i < planes[0].Length; i++) planes[0][i] -= half;
        Assert.Contains(planes[0], v => v < 0);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, precision, true, new JpxEncodeOptions { Levels = 2 });
        JpxCodestream cs = Parse(codestream);
        Assert.True(cs.Size.Components[0].IsSigned);
        Assert.Equal(precision, cs.Size.Components[0].Precision);
        AssertExact(codestream, planes, 40, 24, precision, isSigned: true);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    public void OtherUnsignedPrecisions_DecodeExactly(int precision)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 1, 3 * precision, precision);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 40, 24, precision, false, new JpxEncodeOptions { Levels = 2 });
        Assert.Equal(precision, Parse(codestream).Size.Components[0].Precision);
        AssertExact(codestream, planes, 40, 24, precision);
    }

    /// <summary>
    /// 4x4 tiles under five levels: a tile away from the origin has resolution levels with no
    /// sample at all (B-15 — ⌈4/8⌉ = ⌈8/8⌉), so those levels have no precinct and no packet, while
    /// the first tile keeps one sample through every level.
    /// </summary>
    [Fact]
    public void SmallTilesUnderDeepLevels_HaveEmptyResolutions_AndStillDecode()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(8, 8, 1, 44);
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 8, 8, 8, false, new JpxEncodeOptions { TileWidth = 4, TileHeight = 4, Levels = 5 });
        AssertExact(codestream, planes, 8, 8);
        JpxCodestream cs = Parse(codestream);
        int[] packets = ReadEveryTile(cs);
        Assert.Equal(6, packets[0]);                         // one sample at every level: six resolutions, six packets
        Assert.True(packets[1] < 6, $"{packets[1]} packets"); // levels with no sample have no packet
        Assert.True(JpxTileLayout.TryBuild(cs, 1, out JpxTileLayout layout, out string reason), reason);
        Assert.Equal(0L, layout.Components[0].Resolutions[0].Bounds.Width);
    }
}
