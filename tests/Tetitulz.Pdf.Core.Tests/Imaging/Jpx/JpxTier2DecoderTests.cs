// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxTier2Decoder"/> on packets whose headers are written out here as bits, in the
/// order T.800 §B.10.8 gives, so each can be checked against Annex B by eye. The image is 64x64
/// with no decomposition level and 64x64 code-blocks: one resolution, one precinct, one code-block,
/// one packet per layer.
/// </summary>
public sealed class JpxTier2DecoderTests
{
    private static readonly byte[] Body4 = { 0xA1, 0xB2, 0xC3, 0xD4 };

    /// <summary>
    /// The first packet of the code-block: non-empty (1); included, the inclusion tag tree's single
    /// node being 0 (1); two zero bit-planes, the tag tree answering "not below 1", "not below 2",
    /// "below 3" (001); three coding passes (1100, Table B.4); Lblock unchanged (0); four bytes in
    /// 3 + ⌊log2 3⌋ = 4 bits (0100).
    /// </summary>
    private const string FirstPacket = "1 1 001 1100 0 0100";

    private static byte[] Codestream(byte[] cod, byte[] tileData, byte[] tileHeader = null, byte[] extraMain = null) =>
        J.Concat(J.Soc, J.Siz64(), cod, J.QcdNone(levels: 0), extraMain ?? Array.Empty<byte>(),
            J.TilePart(0, 0, tileHeader ?? Array.Empty<byte>(), tileData), J.Eoc);

    private static JpxTileDecode Decode(byte[] codestream, WorkBudget budget = null)
    {
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, budget ?? new WorkBudget(10_000), out JpxTileDecode result, out reason), reason);
        return result;
    }

    private static JpxCodeBlock TheBlock(JpxTileDecode result) =>
        Assert.Single(result.Layout.Components[0].Resolutions[0].Precincts[0].Bands[0].Blocks);

    private static byte[] BytesOf(JpxTileDecode result, JpxCodeChunk chunk)
    {
        var bytes = new byte[chunk.Data.Length];
        Array.Copy(result.Data, chunk.Data.Start, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void OnePacket_FillsTheCodeBlock()
    {
        byte[] header = JpxPacketBits.Bytes(FirstPacket);
        Assert.Equal(new byte[] { 0xCE, 0x10 }, header);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0), J.Concat(header, Body4)));

        Assert.True(result.Complete, result.StopReason);
        Assert.Equal(1, result.PacketsRead);
        Assert.Equal(6L, result.BytesConsumed);
        Assert.Equal(new long[] { 6 }, result.PacketLengths);
        Assert.Empty(result.Deviations);

        JpxCodeBlock block = TheBlock(result);
        Assert.True(block.Included);
        Assert.Equal((2, 3, 3), (block.ZeroBitPlanes, block.Passes, block.Lblock));
        JpxCodeChunk chunk = Assert.Single(block.Chunks);
        Assert.Equal((3, false), (chunk.Passes, chunk.Terminated));
        Assert.Equal(Body4, BytesOf(result, chunk));
    }

    /// <summary>
    /// In the second layer the code-block is already included, so one bit says so (1); two passes
    /// (10); Lblock unchanged (0); five bytes in 3 + ⌊log2 2⌋ = 4 bits (0101). In the third, a
    /// non-empty packet whose one inclusion bit is 0 (10) contributes nothing.
    /// </summary>
    [Fact]
    public void LaterLayers_AppendAChunk_OrNothing()
    {
        byte[] second = JpxPacketBits.Bytes("1 1 10 0 0101");
        Assert.Equal(new byte[] { 0xE2, 0x80 }, second);
        byte[] third = JpxPacketBits.Bytes("1 0");
        byte[] data = J.Concat(JpxPacketBits.Bytes(FirstPacket), Body4, second, new byte[] { 1, 2, 3, 4, 5 }, third);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, layers: 3), data));

        Assert.True(result.Complete, result.StopReason);
        Assert.Equal(new long[] { 6, 7, 1 }, result.PacketLengths);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal(5, block.Passes);
        Assert.Equal(2, block.Chunks.Count);
        Assert.Equal((2, false), (block.Chunks[1].Passes, block.Chunks[1].Terminated));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, BytesOf(result, block.Chunks[1]));
    }

    /// <summary>
    /// A code-block first included in layer 1: layer 0's packet reads one tag-tree bit, 0 — "not
    /// below 1", so the leaf is at least 1 — and stops; layer 1's packet reads a 1 — the leaf is 1,
    /// below 2 — and the rest of the first-inclusion header follows (§B.10.4).
    /// </summary>
    [Fact]
    public void AFirstInclusionInALaterLayer_ReadsThePartialTagTree()
    {
        byte[] data = J.Concat(JpxPacketBits.Bytes("1 0"), JpxPacketBits.Bytes(FirstPacket), Body4);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, layers: 2), data));

        Assert.True(result.Complete, result.StopReason);
        Assert.Equal(new long[] { 1, 6 }, result.PacketLengths);
        JpxPrecinctBand pb = result.Layout.Components[0].Resolutions[0].Precincts[0].Bands[0];
        Assert.Equal(1, pb.Inclusion.ValueOf(0, 0));
        JpxCodeBlock block = Assert.Single(pb.Blocks);
        Assert.Equal((2, 3), (block.ZeroBitPlanes, block.Passes));
        Assert.Equal(Body4, BytesOf(result, Assert.Single(block.Chunks)));
    }

    /// <summary>
    /// Nine passes are 1111 then 00011 (6 + 3, Table B.4); 110 raises Lblock by two to 5; the length
    /// then takes 5 + ⌊log2 9⌋ = 8 bits, 11001000 = 200 (equation B-19).
    /// </summary>
    [Fact]
    public void PassCountsAndLblock_FollowTableB4AndEquationB19()
    {
        byte[] header = JpxPacketBits.Bytes("1 1 001 111100011 110 11001000");
        Assert.Equal(new byte[] { 0xCF, 0x8F, 0x64, 0x00 }, header);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0), J.Concat(header, new byte[200])));

        Assert.True(result.Complete, result.StopReason);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal((9, 5), (block.Passes, block.Lblock));
        Assert.Equal(200L, Assert.Single(block.Chunks).Data.Length);
    }

    /// <summary>
    /// A header can begin with 0xFF: no zero bit-planes (1 alone) and 37 passes (1111 11111 then
    /// 0000000) make its first byte all ones, so the encoder stuffs a zero bit into the next one
    /// (§B.10.1). The length, 3 + ⌊log2 37⌋ = 8 bits, is 100.
    /// </summary>
    [Fact]
    public void AStuffedBit_IsSkipped_InsideAHeader()
    {
        byte[] header = JpxPacketBits.Bytes("1 1 1 1111111110000000 0 01100100");
        Assert.Equal(0xFF, header[0]);
        Assert.Equal(4, header.Length);   // 28 bits take four bytes with the stuffed bit, three without
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0), J.Concat(header, new byte[100])));

        Assert.True(result.Complete, result.StopReason);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal((0, 37), (block.ZeroBitPlanes, block.Passes));
        Assert.Equal(100L, Assert.Single(block.Chunks).Data.Length);
    }

    [Fact]
    public void AnEmptyPacket_IncludesNothing()
    {
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0), new byte[] { 0x00 }));
        Assert.True(result.Complete, result.StopReason);
        Assert.Equal((1, 1L), (result.PacketsRead, result.BytesConsumed));
        JpxCodeBlock block = TheBlock(result);
        Assert.False(block.Included);
        Assert.Empty(block.Chunks);
    }

    /// <summary>
    /// With termination on every pass (Table D.8) each of the three passes has its own length, each
    /// in 3 + ⌊log2 1⌋ = 3 bits: 2, 3 and 4 bytes, three terminated chunks laid end to end.
    /// </summary>
    [Fact]
    public void TerminateEachPass_SignalsOneLengthPerPass()
    {
        byte[] header = JpxPacketBits.Bytes("1 1 001 1100 0 010 011 100");
        Assert.Equal(new byte[] { 0xCE, 0x13, 0x80 }, header);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, style: 0x04), J.Concat(header, new byte[9])));

        Assert.True(result.Complete, result.StopReason);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal(3, block.Chunks.Count);
        long at = result.DataStart + 3;
        foreach ((JpxCodeChunk chunk, long length) in new[] { (block.Chunks[0], 2L), (block.Chunks[1], 3L), (block.Chunks[2], 4L) })
        {
            Assert.Equal((1, true), (chunk.Passes, chunk.Terminated));
            Assert.Equal((at, length), (chunk.Data.Start, chunk.Data.Length));
            at += length;
        }
    }

    /// <summary>
    /// HT code-blocks (ITU-T T.814 §B.2): the set T ends a segment at the cleanup pass and at the
    /// refinement segment's last pass, so three passes take two lengths — 4 bytes in 3 + ⌊log2 1⌋ = 3
    /// bits (100), then 3 in 3 + ⌊log2 2⌋ = 4 bits (0011). Termination on each pass (bit 2) is set
    /// and does not apply: the SigProp pass ends no segment.
    /// </summary>
    [Fact]
    public void HtCodeBlocks_EndASegmentAtTheCleanupAndTheRefinementPass()
    {
        byte[] header = JpxPacketBits.Bytes("1 1 001 1100 0 100 0011");
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, style: 0x44), J.Concat(header, new byte[7])));

        Assert.True(result.Complete, result.StopReason);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal(0, block.HtPlaceholderPasses);
        Assert.Equal(2, block.Chunks.Count);
        Assert.Equal((1, true, 4L), (block.Chunks[0].Passes, block.Chunks[0].Terminated, block.Chunks[0].Data.Length));
        Assert.Equal((2, true, 3L), (block.Chunks[1].Passes, block.Chunks[1].Terminated, block.Chunks[1].Data.Length));
    }

    /// <summary>
    /// Four passes in an HT code-block's first contribution are 3·P0 = 3 placeholder passes and its
    /// cleanup pass (§B.1, §B.3): one segment, ending at that cleanup pass, its length in 3 + ⌊log2 4⌋
    /// = 5 bits (00110, 6 bytes). The control is the same header for a Part 1 code-block: the same bits
    /// read the same, but nothing terminates and no placeholder is counted.
    /// </summary>
    [Fact]
    public void AnHtFirstContributionOfFourPasses_HasThreePlaceholders()
    {
        byte[] header = JpxPacketBits.Bytes("1 1 001 1101 0 00110");
        JpxCodeBlock ht = TheBlock(Decode(Codestream(J.Cod(levels: 0, style: 0x40), J.Concat(header, new byte[6]))));
        JpxCodeBlock part1 = TheBlock(Decode(Codestream(J.Cod(levels: 0), J.Concat(header, new byte[6]))));

        Assert.Equal(3, ht.HtPlaceholderPasses);
        Assert.Equal((4, true, 6L), (ht.Chunks[0].Passes, ht.Chunks[0].Terminated, ht.Chunks[0].Data.Length));
        Assert.Equal(0, part1.HtPlaceholderPasses);
        Assert.Equal((4, false, 6L), (part1.Chunks[0].Passes, part1.Chunks[0].Terminated, part1.Chunks[0].Data.Length));
    }

    /// <summary>
    /// ITU-T T.814 §B.3 NOTE 4: a first contribution may be placeholder passes alone. Layer 0 brings
    /// three passes with no bytes: placeholders stand in no set T, so they are one segment, its length 0
    /// in 3 + ⌊log2 3⌋ = 4 bits (0000) — read as a cleanup and a refinement segment, they would take
    /// 3 + 4 bits and desynchronise the header. Layer 1 brings the cleanup pass, 3·P0 = 3: five bytes
    /// in 3 bits (101).
    /// </summary>
    [Fact]
    public void AnHtCodeBlocksFirstContribution_CanBePlaceholderPassesAlone()
    {
        byte[] first = JpxPacketBits.Bytes("1 1 001 1100 0 0000");
        byte[] second = JpxPacketBits.Bytes("1 1 0 0 101");
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, layers: 2, style: 0x40), J.Concat(first, second, new byte[5])));

        Assert.True(result.Complete, result.StopReason);
        Assert.Equal((2, (long)first.Length + second.Length + 5), (result.PacketsRead, result.BytesConsumed));
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal((JpxBlockCoder.HighThroughput, 3, 4), (block.Coder, block.HtPlaceholderPasses, block.Passes));
        Assert.Equal(2, block.Chunks.Count);
        Assert.Equal((3, false, 0L), (block.Chunks[0].Passes, block.Chunks[0].Terminated, block.Chunks[0].Data.Length));
        Assert.Equal((1, true, 5L), (block.Chunks[1].Passes, block.Chunks[1].Terminated, block.Chunks[1].Data.Length));
    }

    /// <summary>
    /// Placeholders split across layers, the cleanup's set whole in the second: layer 0's two passes
    /// would be a cleanup and a SigProp pass if they held the first cleanup, so the cleanup segment's
    /// 3 bits are read first — 000, no cleanup — and the field finished to 3 + ⌊log2 2⌋ = 4 bits as one
    /// placeholder segment. Layer 1's four passes end the block's sixth, so its last two are the
    /// refinement passes: one more placeholder and the cleanup make a segment of 3 + ⌊log2 2⌋ bits
    /// (0110, 6 bytes), the refinement pair another (0011, 3 bytes), and 3·P0 = 3.
    /// </summary>
    [Fact]
    public void PlaceholderPassesSplitAcrossLayers_EndBeforeTheCleanup()
    {
        byte[] first = JpxPacketBits.Bytes("1 1 001 10 0 0000");
        byte[] second = JpxPacketBits.Bytes("1 1 1101 0 0110 0011");
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, layers: 2, style: 0x40), J.Concat(first, second, new byte[9])));

        Assert.True(result.Complete, result.StopReason);
        Assert.Equal((long)first.Length + second.Length + 9, result.BytesConsumed);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal((JpxBlockCoder.HighThroughput, 3, 6), (block.Coder, block.HtPlaceholderPasses, block.Passes));
        Assert.Equal(3, block.Chunks.Count);
        Assert.Equal((2, 0L), (block.Chunks[0].Passes, block.Chunks[0].Data.Length));
        Assert.Equal((2, true, 6L), (block.Chunks[1].Passes, block.Chunks[1].Terminated, block.Chunks[1].Data.Length));
        Assert.Equal((2, true, 3L), (block.Chunks[2].Passes, block.Chunks[2].Terminated, block.Chunks[2].Data.Length));
    }

    /// <summary>
    /// Placeholder passes carry no bytes (ITU-T T.814 §B.1): outside the MIXED set, a non-zero length
    /// before the first cleanup is malformed. Two passes whose cleanup field reads 000 are placeholders,
    /// and their 4-bit field ending in 1 claims a byte for them.
    /// </summary>
    [Fact]
    public void BytesBeforeAnHtCodeBlocksFirstCleanup_AreRefusedOutsideMixed()
    {
        byte[] first = JpxPacketBits.Bytes("1 1 001 10 0 0001");
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, style: 0x40), J.Concat(first, new byte[1])));
        Assert.False(result.Complete);
        Assert.Contains("placeholder passes carry none", result.StopReason, StringComparison.Ordinal);
    }

    private static readonly byte[] MixedCap = J.Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0xC0, 0x00);

    private static byte[] MixedCodestream(byte[] tileData) =>
        J.Concat(J.Soc, J.Siz64(), MixedCap, J.Cod(levels: 0, style: 0xC0), J.QcdNone(levels: 0), J.TilePart(0, 0, Array.Empty<byte>(), tileData), J.Eoc);

    /// <summary>
    /// ITU-T T.814 §A.4: in a MIXED tile-component a block's first non-zero length tells its coder. Three
    /// passes; the cleanup segment's 3 bits read 101 with Lblock 3 — not an HT block's (Lblock must
    /// exceed 3), so a Part 1 block's one codeword segment, its field finished to 3 + ⌊log2 3⌋ bits:
    /// 1010, ten bytes, one chunk of three passes. With Lblock raised to 4 (10) and a field beginning
    /// with 0 (0101, five bytes), the block is HT: the cleanup, then its refinement pair in 4 + 1 bits
    /// (00011, three bytes). Lblock 4 and a field beginning with 1 (1010, then 0: twenty bytes) is a
    /// Part 1 block's again.
    /// </summary>
    [Fact]
    public void MixedCodeBlocks_TakeTheirCoderFromTheFirstLength()
    {
        JpxCodeBlock part1 = TheBlock(Decode(MixedCodestream(J.Concat(JpxPacketBits.Bytes("1 1 001 1100 0 1010"), new byte[10]))));
        Assert.Equal(JpxBlockCoder.Part1, part1.Coder);
        Assert.Equal((3, false, 10L), (Assert.Single(part1.Chunks).Passes, part1.Chunks[0].Terminated, part1.Chunks[0].Data.Length));
        Assert.False(part1.IsHighThroughput(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.Mixed));

        JpxCodeBlock ht = TheBlock(Decode(MixedCodestream(J.Concat(JpxPacketBits.Bytes("1 1 001 1100 10 0101 00011"), new byte[8]))));
        Assert.Equal((JpxBlockCoder.HighThroughput, 0), (ht.Coder, ht.HtPlaceholderPasses));
        Assert.Equal(2, ht.Chunks.Count);
        Assert.Equal((1, 5L), (ht.Chunks[0].Passes, ht.Chunks[0].Data.Length));
        Assert.Equal((2, 3L), (ht.Chunks[1].Passes, ht.Chunks[1].Data.Length));
        Assert.True(ht.IsHighThroughput(JpxCodeBlockStyle.HighThroughput | JpxCodeBlockStyle.Mixed));

        JpxCodeBlock wide = TheBlock(Decode(MixedCodestream(J.Concat(JpxPacketBits.Bytes("1 1 001 1100 10 10100"), new byte[20]))));
        Assert.Equal(JpxBlockCoder.Part1, wide.Coder);
        Assert.Equal((3, 20L), (Assert.Single(wide.Chunks).Passes, wide.Chunks[0].Data.Length));
    }

    /// <summary>
    /// Each of §A.4's conditions decides on its own. A field beginning with 0 is still a Part 1 block's
    /// while Lblock is 3 (011, finished to 0110: six bytes); with Lblock 4, a first length of 1 is not an
    /// HT cleanup's (which needs more than one byte, §B.3) but a Part 1 block's (0001, finished to
    /// 00010: two bytes).
    /// </summary>
    [Fact]
    public void MixedCodeBlocks_NeedEveryConditionOfAnHtLength()
    {
        JpxCodeBlock lblock3 = TheBlock(Decode(MixedCodestream(J.Concat(JpxPacketBits.Bytes("1 1 001 1100 0 0110"), new byte[6]))));
        Assert.Equal(JpxBlockCoder.Part1, lblock3.Coder);
        Assert.Equal((3, 6L), (Assert.Single(lblock3.Chunks).Passes, lblock3.Chunks[0].Data.Length));

        JpxCodeBlock oneByte = TheBlock(Decode(MixedCodestream(J.Concat(JpxPacketBits.Bytes("1 1 001 1100 10 00010"), new byte[2]))));
        Assert.Equal(JpxBlockCoder.Part1, oneByte.Coder);
        Assert.Equal((3, 2L), (Assert.Single(oneByte.Chunks).Passes, oneByte.Chunks[0].Data.Length));
    }

    /// <summary>
    /// The first HT cleanup segment has more than one byte (ITU-T T.814 §B.3): outside MIXED, a first
    /// cleanup of one byte (001) is refused for that reason; two bytes (010) are read.
    /// </summary>
    [Fact]
    public void AnHtCodeBlocksFirstCleanupOfOneByte_IsRefused()
    {
        JpxTileDecode refused = Decode(Codestream(J.Cod(levels: 0, style: 0x40), J.Concat(JpxPacketBits.Bytes("1 1 001 0 0 001"), new byte[1])));
        Assert.False(refused.Complete);
        Assert.Contains("first cleanup segment is 1 byte", refused.StopReason, StringComparison.Ordinal);

        JpxTileDecode read = Decode(Codestream(J.Cod(levels: 0, style: 0x40), J.Concat(JpxPacketBits.Bytes("1 1 001 0 0 010"), new byte[2])));
        Assert.True(read.Complete, read.StopReason);
        Assert.Equal((JpxBlockCoder.HighThroughput, 2L), (TheBlock(read).Coder, TheBlock(read).Chunks[0].Data.Length));
    }

    /// <summary>
    /// A MIXED block whose first contributions carry no bytes stays undecided; when a later one has
    /// bytes and proves Part 1, the earlier passes stay its passes and the layer's passes follow under
    /// Part 1's rules — here two empty passes (0000), then three passes of seven bytes (0111, Lblock 3)
    /// in one Part 1 segment, and a third layer of one pass in 3 bits (010) appended, unterminated.
    /// </summary>
    [Fact]
    public void AMixedBlockWithEmptyFirstPasses_TurnsPart1AndKeepsThem()
    {
        byte[] first = JpxPacketBits.Bytes("1 1 001 10 0 0000");
        byte[] second = JpxPacketBits.Bytes("1 1 1100 0 0111");
        byte[] third = JpxPacketBits.Bytes("1 1 0 0 010");
        byte[] data = J.Concat(first, second, new byte[7], third, new byte[2]);
        byte[] cs = J.Concat(J.Soc, J.Siz64(), MixedCap, J.Cod(levels: 0, layers: 3, style: 0xC0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), data), J.Eoc);
        JpxTileDecode result = Decode(cs);

        Assert.True(result.Complete, result.StopReason);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal((JpxBlockCoder.Part1, 6), (block.Coder, block.Passes));
        Assert.Equal(new[] { (2, 0L), (3, 7L), (1, 2L) }, block.Chunks.Select(c => (c.Passes, c.Data.Length)));
        Assert.All(block.Chunks, c => Assert.False(c.Terminated));
    }

    /// <summary>
    /// Selective bypass (Table D.9): of twelve passes in one packet, the fourth cleanup pass (index
    /// 9) terminates, and the raw pair after it terminates at its magnitude refinement pass (index
    /// 11) — two lengths, 20 bytes in 3 + ⌊log2 10⌋ = 6 bits and 5 in 3 + ⌊log2 2⌋ = 4. In the next
    /// layer, pass 12 (a cleanup pass) terminates and pass 13 (significance propagation) does not,
    /// but is the packet's last and so is signalled: 3 and 4 bytes in 3 bits each.
    /// </summary>
    [Fact]
    public void SelectiveBypass_TerminatesWhereTableD9Says()
    {
        byte[] first = JpxPacketBits.Bytes("1 1 001 111100110 0 010100 0101");
        Assert.Equal(new byte[] { 0xCF, 0x98, 0xA2, 0x80 }, first);
        byte[] second = JpxPacketBits.Bytes("1 1 10 0 011 100");
        byte[] data = J.Concat(first, new byte[25], second, new byte[7]);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, layers: 2, style: 0x01), data));

        Assert.True(result.Complete, result.StopReason);
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal(14, block.Passes);
        Assert.Equal(
            new[] { (10, 20L, true), (2, 5L, true), (1, 3L, true), (1, 4L, false) },
            new List<JpxCodeChunk>(block.Chunks).ConvertAll(c => (c.Passes, c.Data.Length, c.Terminated)).ToArray());
    }

    /// <summary>
    /// The SOP marker segment (FF91, length 4, Nsop) before the packet and the EPH marker (FF92)
    /// after its header are both skipped, the packet's length counting them (§A.8).
    /// </summary>
    [Fact]
    public void SopAndEph_AreSkipped()
    {
        byte[] sop = { 0xFF, 0x91, 0x00, 0x04, 0x00, 0x00 };
        byte[] eph = { 0xFF, 0x92 };
        byte[] data = J.Concat(sop, JpxPacketBits.Bytes(FirstPacket), eph, Body4);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, scodExtra: 6), data));

        Assert.True(result.Complete, result.StopReason);
        Assert.Empty(result.Deviations);
        Assert.Equal(new long[] { 14 }, result.PacketLengths);
        Assert.Equal(Body4, BytesOf(result, Assert.Single(TheBlock(result).Chunks)));
    }

    /// <summary>
    /// An SOP whose Nsop is not the packet's index, and an SOP or EPH the COD did not announce, are
    /// tolerated and reported; an EPH the COD requires but the packet lacks stops the tile.
    /// </summary>
    [Fact]
    public void SopAndEph_AreCheckedAgainstTheCod()
    {
        byte[] header = JpxPacketBits.Bytes(FirstPacket);
        byte[] wrongNsop = { 0xFF, 0x91, 0x00, 0x04, 0x00, 0x07 };
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, scodExtra: 2), J.Concat(wrongNsop, header, Body4)));
        Assert.True(result.Complete, result.StopReason);
        Assert.Contains("carries Nsop 7", Assert.Single(result.Deviations), StringComparison.Ordinal);

        byte[] sop = { 0xFF, 0x91, 0x00, 0x04, 0x00, 0x00 };
        result = Decode(Codestream(J.Cod(levels: 0), J.Concat(sop, header, Body4)));
        Assert.True(result.Complete, result.StopReason);
        Assert.Contains("COD does not allow them", Assert.Single(result.Deviations), StringComparison.Ordinal);

        byte[] eph = { 0xFF, 0x92 };
        result = Decode(Codestream(J.Cod(levels: 0), J.Concat(header, eph, Body4)));
        Assert.True(result.Complete, result.StopReason);
        Assert.Contains("does not require one", Assert.Single(result.Deviations), StringComparison.Ordinal);
        Assert.Equal(Body4, BytesOf(result, Assert.Single(TheBlock(result).Chunks)));

        result = Decode(Codestream(J.Cod(levels: 0, scodExtra: 4), J.Concat(header, Body4)));
        Assert.False(result.Complete);
        Assert.Contains("no EPH marker follows the header", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(0, result.PacketsRead);
    }

    /// <summary>
    /// Headers relocated to a PPT (§A.7.5) or a PPM (§A.7.4, one Nppm-counted entry per tile-part)
    /// are read from there, with their EPH when the COD requires it; the tile data holds bodies only.
    /// </summary>
    [Fact]
    public void PackedHeaders_AreReadFromPptOrPpm()
    {
        byte[] header = JpxPacketBits.Bytes(FirstPacket);
        byte[] ppt = J.Segment(0xFF61, J.Concat(new byte[] { 0 }, header, new byte[] { 0xFF, 0x92 }));
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, scodExtra: 4), Body4, tileHeader: ppt));
        Assert.True(result.Complete, result.StopReason);
        Assert.Equal((1, 4L), (result.PacketsRead, result.BytesConsumed));
        Assert.Equal(Body4, BytesOf(result, Assert.Single(TheBlock(result).Chunks)));

        byte[] ppm = J.Segment(0xFF60, J.Concat(new byte[] { 0, 0, 0, 0, 2 }, header));
        result = Decode(Codestream(J.Cod(levels: 0), Body4, extraMain: ppm));
        Assert.True(result.Complete, result.StopReason);
        Assert.Equal(Body4, BytesOf(result, Assert.Single(TheBlock(result).Chunks)));
    }

    /// <summary>Two tile-parts, one packet each (§B.11): read as one sequence.</summary>
    [Fact]
    public void TileParts_AreReadAsOneSequence()
    {
        byte[] packet1 = J.Concat(JpxPacketBits.Bytes(FirstPacket), Body4);
        byte[] packet2 = J.Concat(JpxPacketBits.Bytes("1 1 10 0 0101"), new byte[] { 1, 2, 3, 4, 5 });
        byte[] cs = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0, layers: 2), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), packet1), J.TilePart(0, 1, Array.Empty<byte>(), packet2), J.Eoc);
        JpxTileDecode result = Decode(cs);
        Assert.True(result.Complete, result.StopReason);
        Assert.Equal((2, 13L), (result.PacketsRead, result.BytesConsumed));
        JpxCodeBlock block = TheBlock(result);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, BytesOf(result, block.Chunks[1]));
    }

    /// <summary>A body cut short keeps the bytes that arrived and stops the tile there (§B.11).</summary>
    [Fact]
    public void ATruncatedBody_KeepsWhatArrived()
    {
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0), J.Concat(JpxPacketBits.Bytes(FirstPacket), new byte[] { 0xA1, 0xB2 })));
        Assert.False(result.Complete);
        Assert.Contains("ends inside packet 0", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(1, result.PacketsRead);
        Assert.Equal(new byte[] { 0xA1, 0xB2 }, BytesOf(result, Assert.Single(TheBlock(result).Chunks)));
    }

    /// <summary>An SOP marker segment cut short by the end of the tile data is a truncation, not a malformed header.</summary>
    [Fact]
    public void ATruncatedSop_StopsTheTileAsTruncated()
    {
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, scodExtra: 2), new byte[] { 0xFF, 0x91, 0x00, 0x04 }));
        Assert.False(result.Complete);
        Assert.Contains("ends inside the SOP marker segment before packet 0", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(0, result.PacketsRead);
    }

    /// <summary>
    /// A deviation that repeats per packet — ten SOPs each carrying Nsop 15 — is written out eight
    /// times and then counted, so a tile of a hundred thousand packets cannot build as many strings.
    /// </summary>
    [Fact]
    public void RepeatedPerPacketDeviations_AreCappedAndCounted()
    {
        byte[] sopAndEmptyPacket = { 0xFF, 0x91, 0x00, 0x04, 0x00, 0x0F, 0x00 };
        var data = new List<byte>();
        for (int i = 0; i < 10; i++) data.AddRange(sopAndEmptyPacket);
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0, layers: 10, scodExtra: 2), data.ToArray()));

        Assert.True(result.Complete, result.StopReason);
        Assert.Equal(10, result.PacketsRead);
        Assert.Equal(JpxTileDecode.ReportedPerKind + 1, result.Deviations.Count);
        Assert.Contains("the SOP before packet 0 (", result.Deviations[0], StringComparison.Ordinal);
        Assert.Contains("carries Nsop 15", result.Deviations[7], StringComparison.Ordinal);
        Assert.Equal("2 more packets where " + JpxTileDecode.WrongNsop, result.Deviations[8]);
    }

    /// <summary>
    /// A PPM series over two tiles is joined and indexed once for the codestream: the second tile
    /// reads its entry from the index the first built, and each tile's block gets its own body.
    /// </summary>
    [Fact]
    public void ThePpmIndex_IsBuiltOnce_ForEveryTile()
    {
        byte[] header = JpxPacketBits.Bytes(FirstPacket);
        byte[] entry = J.Concat(new byte[] { 0, 0, 0, 2 }, header);
        byte[] ppm = J.Segment(0xFF60, J.Concat(new byte[] { 0 }, entry, entry));
        byte[] body2 = { 0x11, 0x22, 0x33, 0x44 };
        byte[] cs = J.Concat(J.Soc, J.Siz(128, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1) }), J.Cod(levels: 0), J.QcdNone(levels: 0), ppm,
            J.TilePart(0, 0, Array.Empty<byte>(), Body4), J.TilePart(1, 0, Array.Empty<byte>(), body2), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);
        Assert.Null(parsed.PackedMainHeaders);

        var budget = new WorkBudget(10_000);
        Assert.True(JpxTier2Decoder.TryDecodeTile(parsed, 0, budget, out JpxTileDecode first, out reason), reason);
        JpxPackedMainHeaders index = parsed.PackedMainHeaders;
        Assert.NotNull(index);
        Assert.Equal(2, index.Slices.Count);
        Assert.True(JpxTier2Decoder.TryDecodeTile(parsed, 1, budget, out JpxTileDecode second, out reason), reason);
        Assert.Same(index, parsed.PackedMainHeaders);

        Assert.True(first.Complete && second.Complete, first.StopReason ?? second.StopReason);
        Assert.Equal(Body4, BytesOf(first, Assert.Single(TheBlock(first).Chunks)));
        Assert.Equal(body2, BytesOf(second, Assert.Single(TheBlock(second).Chunks)));
    }

    /// <summary>
    /// Two hostile headers, each stopped by its own cap with its own reason: seventy tag-tree zeros
    /// claim more zero bit-planes than any M<sub>b</sub> allows (the cap is 63); twenty-nine Lblock
    /// ones ask for a 33-bit length field, past the 31 a length can take (equation B-19).
    /// </summary>
    [Fact]
    public void AZeroBitPlaneCountOrALengthFieldPastItsCap_StopsTheTile()
    {
        JpxTileDecode planes = Decode(Codestream(J.Cod(levels: 0), JpxPacketBits.Bytes("1 1 " + new string('0', 70))));
        Assert.False(planes.Complete);
        Assert.Contains("claims more than 63 zero bit-planes", planes.StopReason, StringComparison.Ordinal);

        JpxTileDecode lengths = Decode(Codestream(J.Cod(levels: 0), JpxPacketBits.Bytes("1 1 001 1100 " + new string('1', 29) + "0")));
        Assert.False(lengths.Complete);
        Assert.Contains("a code-block length field of 33 bits", lengths.StopReason, StringComparison.Ordinal);
        Assert.Equal(0, planes.PacketsRead + lengths.PacketsRead);
    }

    /// <summary>
    /// PLT (§A.7.3) is advisory but the one independent statement of a packet's length: a PLT that
    /// says 9 bytes where 6 were read is reported, and one that says 6 is not.
    /// </summary>
    [Fact]
    public void PacketLengths_AreCheckedAgainstPlt()
    {
        byte[] data = J.Concat(JpxPacketBits.Bytes(FirstPacket), Body4);
        JpxTileDecode wrong = Decode(Codestream(J.Cod(levels: 0), data, tileHeader: J.Segment(0xFF58, 0, 0x09)));
        Assert.True(wrong.Complete, wrong.StopReason);
        Assert.Equal("tile-part 0's PLT says packet 0 is 9 bytes; 6 were read", Assert.Single(wrong.Deviations));

        JpxTileDecode right = Decode(Codestream(J.Cod(levels: 0), data, tileHeader: J.Segment(0xFF58, 0, 0x06)));
        Assert.True(right.Complete, right.StopReason);
        Assert.Empty(right.Deviations);
    }

    /// <summary>A header cut short names where it ended; the packet is not counted.</summary>
    [Fact]
    public void AHeaderCutShort_StopsWithTheReason()
    {
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0), new byte[] { 0xCE }));
        Assert.False(result.Complete);
        Assert.Contains("the header ends inside", result.StopReason, StringComparison.Ordinal);
        Assert.Equal(0, result.PacketsRead);
    }

    [Fact]
    public void BytesAfterTheLastPacket_AreReported()
    {
        JpxTileDecode result = Decode(Codestream(J.Cod(levels: 0), J.Concat(JpxPacketBits.Bytes(FirstPacket), Body4, new byte[] { 9, 9, 9 })));
        Assert.True(result.Complete, result.StopReason);
        Assert.Contains("3 bytes follow the tile's last packet", Assert.Single(result.Deviations), StringComparison.Ordinal);
    }

    /// <summary>
    /// A hundred layers are a hundred packets from one COD field; the work budget counts each and
    /// throws when it runs out, and is the evidence — a hundred units for a hundred empty packets.
    /// </summary>
    [Fact]
    public void TheWorkBudget_BoundsThePacketCount()
    {
        byte[] cs = Codestream(J.Cod(levels: 0, layers: 100), new byte[100]);
        var small = new WorkBudget(10);
        Assert.Throws<WorkBudgetExceededException>(() => Decode(cs, small));
        Assert.True(small.IsExhausted);

        var enough = new WorkBudget(200);
        JpxTileDecode result = Decode(cs, enough);
        Assert.True(result.Complete, result.StopReason);
        Assert.Equal(100, result.PacketsRead);
        // One unit per packet read, and one per (layer, resolution, component) scan of the
        // progression: 100 layers over one resolution
        // and one component is 100 packets and 100 scans.
        Assert.Equal(200L, enough.Spent);
        Assert.Throws<WorkBudgetExceededException>(() => Decode(cs, new WorkBudget(199)));
    }
}
