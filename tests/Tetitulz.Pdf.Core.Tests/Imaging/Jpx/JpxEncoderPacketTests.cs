// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary><see cref="JpxPacketEncoder"/>: Table B.4's codewords against the decoder's reader, and whole packets against the bits the tier-2 tests wrote by hand.</summary>
public sealed class JpxEncoderPacketTests
{
    [Fact]
    public void PassCountCodewords_AreTableB4_ForEveryCount()
    {
        Assert.Equal(new byte[] { 0x00 }, Codeword(1));            // 0
        Assert.Equal(new byte[] { 0x80 }, Codeword(2));            // 10
        Assert.Equal(new byte[] { 0xC0 }, Codeword(3));            // 1100
        Assert.Equal(new byte[] { 0xD0 }, Codeword(4));            // 1101
        Assert.Equal(new byte[] { 0xE0 }, Codeword(5));            // 1110
        Assert.Equal(new byte[] { 0xF0, 0x00 }, Codeword(6));      // 1111 00000
        Assert.Equal(new byte[] { 0xFF, 0x00 }, Codeword(36));     // 1111 11110, then the stuffed byte
        Assert.Equal(new byte[] { 0xFF, 0x40, 0x00 }, Codeword(37));   // 1111 11111 0000000 (a stuffed zero after the FF)
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x80 }, Codeword(164));  // 1111 11111 1111111

        for (int n = 1; n <= JpxPacketEncoder.MaxPasses; n++)
        {
            byte[] bytes = Codeword(n);
            var reader = new JpxBitReader(bytes, 0, bytes.Length);
            Assert.True(JpxPacketDecoder.TryReadPassCount(reader, out int passes));
            Assert.Equal(n, passes);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => Codeword(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Codeword(165));
    }

    private static byte[] Codeword(int passes)
    {
        var bits = new JpxBitWriter();
        JpxPacketEncoder.WritePassCount(bits, passes);
        return bits.Finish();
    }

    /// <summary>A 64x64 tile with no decomposition level: one resolution, one precinct, one code-block, one packet.</summary>
    private static JpxTileLayout Layout(int layers = 1)
    {
        byte[] cs = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0, layers: layers), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), new byte[] { 0 }), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        Assert.True(JpxTileLayout.TryBuild(parsed, 0, out JpxTileLayout layout, out reason), reason);
        return layout;
    }

    private static List<byte> Packet(JpxTileLayout layout, JpxEncodedBlock block, bool eph = false)
    {
        JpxCodeBlock cb = Assert.Single(layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        var blocks = new Dictionary<JpxCodeBlock, JpxEncodedBlock> { [cb] = block };
        var output = new List<byte>();
        JpxPacketEncoder.Write(layout, new JpxPacketAddress(0, 0, 0, 0), blocks, new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>(), eph, output);
        return output;
    }

    /// <summary>
    /// Three passes, two zero bit-planes, four bytes: the header the tier-2 decoder test wrote by
    /// hand — non-empty (1), included (1), the zero bit-plane tree's 001, 1100, Lblock unchanged (0),
    /// four in 3 + ⌊log2 3⌋ bits (0100) — CE 10, then the body.
    /// </summary>
    [Fact]
    public void WritesTheHeaderOfTheDecoderTests_ThenTheBody()
    {
        byte[] body = { 0xA1, 0xB2, 0xC3, 0xD4 };
        List<byte> packet = Packet(Layout(), new JpxEncodedBlock(body, 3, 2));
        Assert.Equal(new byte[] { 0xCE, 0x10, 0xA1, 0xB2, 0xC3, 0xD4 }, packet.ToArray());

        // With EPH: the marker between header and body.
        packet = Packet(Layout(), new JpxEncodedBlock(body, 3, 2), eph: true);
        Assert.Equal(new byte[] { 0xCE, 0x10, 0xFF, 0x92, 0xA1, 0xB2, 0xC3, 0xD4 }, packet.ToArray());
    }

    /// <summary>Nine passes and 200 bytes need 8 bits, so Lblock rises by two (110): 1 1 001 111100011 110 11001000 — the decoder test's CF 8F 64 00.</summary>
    [Fact]
    public void RaisesLblock_ForALengthTheDefaultBitsCannotHold()
    {
        List<byte> packet = Packet(Layout(), new JpxEncodedBlock(new byte[200], 9, 2));
        Assert.Equal(new byte[] { 0xCF, 0x8F, 0x64, 0x00 }, packet.Take(4).ToArray());
        Assert.Equal(204, packet.Count);

        // Read back by tier 2: nine passes, Lblock 5, a 200-byte chunk.
        byte[] cs = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0), J.TilePart(0, 0, Array.Empty<byte>(), packet.ToArray()), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(parsed, 0, new WorkBudget(1000), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        JpxCodeBlock block = Assert.Single(tile.Layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        Assert.Equal((9, 5, 2), (block.Passes, block.Lblock, block.ZeroBitPlanes));
        Assert.Equal(200L, Assert.Single(block.Chunks).Data.Length);
    }

    /// <summary>
    /// An HT set of three passes in one packet is two codeword segments (ITU-T T.814 §B.2): the cleanup's five
    /// bytes in 3 + ⌊log2 1⌋ bits, then the refinement pair's three in 3 + ⌊log2 2⌋ — 1 1 001 1100 0 101 0011,
    /// CE 29 80 — and tier 2 reads them back as the cleanup chunk and the refinement chunk. The SigProp pass's
    /// own length (the bytes its bits end in) is not signalled: it ends no segment.
    /// </summary>
    [Fact]
    public void AnHtSetOfThreePasses_IsTwoSegments()
    {
        var code = new JpxBlockCode(Enumerable.Range(1, 8).Select(b => (byte)b).ToArray(), 3, 3, new[] { 5, 7, 8 }, new double[3]);
        var block = new JpxEncodedBlock(code, 2, 1) { Style = JpxCodeBlockStyle.HighThroughput };
        block.LayerEnd[0] = 3;
        List<byte> packet = Packet(Layout(), block);
        Assert.Equal(new byte[] { 0xCE, 0x29, 0x80, 1, 2, 3, 4, 5, 6, 7, 8 }, packet.ToArray());

        byte[] cs = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0, style: 0x40), J.QcdNone(levels: 0), J.TilePart(0, 0, Array.Empty<byte>(), packet.ToArray()), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(parsed, 0, new WorkBudget(1000), out JpxTileDecode tile, out reason), reason);
        JpxCodeBlock read = Assert.Single(tile.Layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        Assert.Equal(new[] { (1, 5L), (2, 3L) }, read.Chunks.Select(c => (c.Passes, c.Data.Length)));
    }

    [Fact]
    public void ABlockWithoutPasses_MakesTheZeroLengthPacket()
    {
        List<byte> packet = Packet(Layout(), new JpxEncodedBlock(Array.Empty<byte>(), 0, 0));
        Assert.Equal(new byte[] { 0x00 }, packet.ToArray());
    }

    [Fact]
    public void RefusesALayerTheCodDoesNotDeclare()
    {
        JpxTileLayout layout = Layout(layers: 2);
        JpxCodeBlock cb = Assert.Single(layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        var blocks = new Dictionary<JpxCodeBlock, JpxEncodedBlock> { [cb] = new JpxEncodedBlock(new byte[1], 1, 0) };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JpxPacketEncoder.Write(layout, new JpxPacketAddress(2, 0, 0, 0), blocks, new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>(), false, new List<byte>()));
    }

    /// <summary>
    /// Three layers over one block of nine passes and 200 bytes, split 2 / 4 / 3 passes at 50 /
    /// 120 / 200 bytes. Layer 0: inclusion through the tag tree (1), P = 2 (001), two passes (10),
    /// Lblock 3 + ⌊log2 2⌋ = 4 bits cannot hold 50 — two raises (110), 50 in 6 bits (110010).
    /// Layer 1: the block is known, so one inclusion bit (1); four passes (1101); 70 in Lblock 5 +
    /// 2 = 7 bits (0 1000110). Layer 2: 1, three passes (1100), 5 + ⌊log2 3⌋ = 6 bits cannot hold
    /// 80 — one raise (10), 80 in 7 bits (1010000). Each packet's body is its slice, and tier 2
    /// reads the three back as one segment of 200 bytes in three chunks, Lblock 6.
    /// </summary>
    [Fact]
    public void ThreeLayers_SliceOneSegment_AndTierTwoReadsThemBack()
    {
        JpxTileLayout layout = Layout(layers: 3);
        JpxCodeBlock cb = Assert.Single(layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        var data = new byte[200];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
        var lengths = new[] { 20, 50, 70, 90, 100, 120, 150, 180, 200 };
        var code = new JpxBlockCode(data, 4, 9, lengths, new double[9]);
        var block = new JpxEncodedBlock(code, 2, 3);
        block.LayerEnd[0] = 2;
        block.LayerEnd[1] = 6;
        block.LayerEnd[2] = 9;
        var blocks = new Dictionary<JpxCodeBlock, JpxEncodedBlock> { [cb] = block };
        var trees = new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>();

        var packets = new List<byte>[3];
        for (int l = 0; l < 3; l++)
        {
            packets[l] = new List<byte>();
            JpxPacketEncoder.Write(layout, new JpxPacketAddress(l, 0, 0, 0), blocks, trees, false, packets[l]);
        }
        // 1 1 001 10 110 110010 → 1100 1101 1011 0010, then the first 50 bytes.
        Assert.Equal(new byte[] { 0xCD, 0xB2 }, packets[0].Take(2).ToArray());
        Assert.Equal(data.Take(50), packets[0].Skip(2));
        // 1 1 1101 0 1000110 → 1111 0101 0001 10(00), then bytes 50 to 119.
        Assert.Equal(new byte[] { 0xF5, 0x18 }, packets[1].Take(2).ToArray());
        Assert.Equal(data.Skip(50).Take(70), packets[1].Skip(2));
        // 1 1 1100 10 1010000 → 1111 0010 1010 000(0), then bytes 120 to 199.
        Assert.Equal(new byte[] { 0xF2, 0xA0 }, packets[2].Take(2).ToArray());
        Assert.Equal(data.Skip(120), packets[2].Skip(2));

        byte[] all = packets[0].Concat(packets[1]).Concat(packets[2]).ToArray();
        byte[] cs = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0, layers: 3), J.QcdNone(levels: 0), J.TilePart(0, 0, Array.Empty<byte>(), all), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(parsed, 0, new WorkBudget(1000), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        JpxCodeBlock read = Assert.Single(tile.Layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        Assert.Equal((9, 6, 2), (read.Passes, read.Lblock, read.ZeroBitPlanes));
        Assert.Equal(new long[] { 50, 70, 80 }, read.Chunks.Select(c => c.Data.Length).ToArray());
        Assert.Equal(new[] { 2, 4, 3 }, read.Chunks.Select(c => c.Passes).ToArray());
        Assert.All(read.Chunks, c => Assert.False(c.Terminated));
    }

    /// <summary>A block whose first passes arrive in the second layer: the first packet says so through the tag tree (not below 1), the second includes it (below 2) with the zero bit-planes then.</summary>
    [Fact]
    public void ABlockFirstIncludedInTheSecondLayer_IsSignalledThroughTheTagTree()
    {
        JpxTileLayout layout = Layout(layers: 2);
        JpxCodeBlock cb = Assert.Single(layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);
        var code = new JpxBlockCode(new byte[] { 0xA1, 0xB2, 0xC3 }, 1, 1, new[] { 3 }, new double[1]);
        var block = new JpxEncodedBlock(code, 0, 2);
        block.LayerEnd[1] = 1;
        var blocks = new Dictionary<JpxCodeBlock, JpxEncodedBlock> { [cb] = block };
        var trees = new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>();
        var first = new List<byte>();
        var second = new List<byte>();
        JpxPacketEncoder.Write(layout, new JpxPacketAddress(0, 0, 0, 0), blocks, trees, false, first);
        JpxPacketEncoder.Write(layout, new JpxPacketAddress(1, 0, 0, 0), blocks, trees, false, second);
        Assert.Equal(new byte[] { 0x00 }, first.ToArray());   // nothing gains a pass: the zero-length packet
        // 1, the tree: not below 1 was never coded (the packet was empty), so 0 then 1 (value 1 reached); P: 1 (below 1); one pass (0); Lblock (0); 3 in 3 bits (011) → 1011 0001 1(000)
        Assert.Equal(new byte[] { 0xB1, 0x80, 0xA1, 0xB2, 0xC3 }, second.ToArray());
    }
}
