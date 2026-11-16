// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The code-block style flags (T.800 Table A.19) on hand-coded blocks: each flag changes which bits
/// mean what, so a stream coded for one style decodes to something else under another — and the
/// test says so, as the potency control.
/// </summary>
public sealed class JpxBlockDecoderStyleTests
{
    private static MqEncoder Coder()
    {
        var coder = new MqEncoder(JpxBlockDecoder.ContextCount);
        ResetEncoder(coder);
        return coder;
    }

    /// <summary>Table D.7 on the encoder side: what <see cref="JpxBlockDecoder.ResetContexts"/> does for the decoder.</summary>
    private static void ResetEncoder(MqEncoder coder)
    {
        for (int c = 0; c < JpxBlockDecoder.ContextCount; c++) coder.SetState(c, 0, 0);
        coder.SetState(18, 46, 0);
        coder.SetState(17, 3, 0);
        coder.SetState(0, 4, 0);
    }

    private static JpxCodeBlock Block(int width, int height, params (byte[] Data, int Passes, bool Terminated)[] chunks)
    {
        var block = new JpxCodeBlock(new JpxRect(0, 0, width, height)) { Included = true };
        long at = 0;
        foreach ((byte[] data, int passes, bool terminated) in chunks)
        {
            block.Add(new JpxCodeChunk(new JpxRange(at, at + data.Length), passes, terminated));
            at += data.Length;
        }
        return block;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] p in parts) all.AddRange(p);
        return all.ToArray();
    }

    /// <summary>
    /// §D.5: after every cleanup pass four uniform-context symbols spell 1010. A single coefficient
    /// of 1 in one plane: significance (context 0), sign +, then the symbol. A wrong symbol is
    /// reported, and the coefficient still decodes.
    /// </summary>
    [Fact]
    public void SegmentationSymbols_AreCheckedAfterEachCleanupPass()
    {
        MqEncoder good = Coder();
        foreach ((int context, int bit) in new[] { (0, 1), (9, 0), (18, 1), (18, 0), (18, 1), (18, 0) }) good.Encode(context, bit);
        byte[] data = good.Finish();
        int[] result = JpxBlockDecoder.Decode(Block(1, 1, (data, 1, false)), data, 0, JpxCodeBlockStyle.SegmentationSymbols, 1, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 3 }, result);

        MqEncoder bad = Coder();
        foreach ((int context, int bit) in new[] { (0, 1), (9, 0), (18, 1), (18, 1), (18, 1), (18, 0) }) bad.Encode(context, bit);
        data = bad.Finish();
        result = JpxBlockDecoder.Decode(Block(1, 1, (data, 1, false)), data, 0, JpxCodeBlockStyle.SegmentationSymbols, 1, 0, out problem);
        Assert.Contains("segmentation symbol after coding pass 0 is wrong", problem, StringComparison.Ordinal);
        Assert.Equal(new[] { 3 }, result);
    }

    /// <summary>
    /// A block whose chunk the tile data cut short (§B.11) decodes its later passes from padding, so
    /// a wrong symbol there is the truncation's, which tier 2 already reported: the same bad stream
    /// marked truncated reports nothing, and still yields the coefficient; unmarked, it reports.
    /// </summary>
    [Fact]
    public void ATruncatedBlock_DoesNotReportItsSymbolAsWrong()
    {
        MqEncoder bad = Coder();
        foreach ((int context, int bit) in new[] { (0, 1), (9, 0), (18, 1), (18, 1), (18, 1), (18, 0) }) bad.Encode(context, bit);
        byte[] data = bad.Finish();

        var truncated = new JpxCodeBlock(new JpxRect(0, 0, 1, 1)) { Included = true };
        truncated.Add(new JpxCodeChunk(new JpxRange(0, data.Length), 1, terminated: false, truncated: true));
        Assert.True(truncated.Truncated);
        int[] result = JpxBlockDecoder.Decode(truncated, data, 0, JpxCodeBlockStyle.SegmentationSymbols, 1, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 3 }, result);

        JpxBlockDecoder.Decode(Block(1, 1, (data, 1, false)), data, 0, JpxCodeBlockStyle.SegmentationSymbols, 1, 0, out problem);
        Assert.Contains("segmentation symbol after coding pass 0 is wrong", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Selective bypass (§D.6, Table D.9): from the fourth significance pass on, significance and
    /// refinement passes read raw bits. A coefficient of 22 (10110) in five planes is found at
    /// plane 4 by the first cleanup and refined at planes 3, 2 and 1 by the arithmetic coder
    /// (passes 2, 5, 8 — one terminated segment through pass 9), then at plane 0 by pass 11, the
    /// raw refinement pass: the second segment is one raw bit, 0, padded to a byte.
    /// </summary>
    [Fact]
    public void SelectiveBypass_ReadsRawPasses()
    {
        MqEncoder mq = Coder();
        foreach ((int context, int bit) in new[] { (0, 1), (9, 0), (14, 0), (16, 1), (16, 1) }) mq.Encode(context, bit);
        byte[] first = mq.Finish();
        byte[] raw = { 0x00 };
        byte[] data = Concat(first, raw);
        JpxCodeBlock block = Block(1, 1, (first, 10, true), (raw, 2, true), (Array.Empty<byte>(), 1, true));

        int[] result = JpxBlockDecoder.Decode(block, data, 0, JpxCodeBlockStyle.SelectiveBypass, 5, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 45 }, result);   // 22·2 + 1

        // Under the plain style the same bytes are one arithmetic-coded segment: pass 11's bit
        // comes from the coder's 0xFF padding after the segment, a 1, and the value differs.
        int[] plain = JpxBlockDecoder.Decode(Block(1, 1, (data, 13, false)), data, 0, JpxCodeBlockStyle.None, 5, 0, out _);
        Assert.NotEqual(45, plain[0]);
    }

    /// <summary>
    /// The raw significance pass itself (§D.6, the review's row 14): a 2x1 block, five planes, A = 16
    /// significant at the first cleanup and B = −1, whose only bit is at plane 0 — so B is coded by
    /// the raw significance pass (pass 10, its west neighbour A significant): one raw bit for
    /// significance, one for the sign (1 negative, D-2); then pass 11, the raw refinement, reads
    /// A's plane-0 bit. Raw bytes 0xC0 (1, 1, 0) give {33, −3} — twice the magnitude plus the half
    /// at the last plane; 0x80 (1, 0, 0) makes B positive, 0x40 (0, 1, 0) leaves B insignificant
    /// and refines A to 17. The arithmetic passes are the same nine symbols throughout.
    /// </summary>
    [Theory]
    [InlineData(0xC0, 33, -3)]
    [InlineData(0x80, 33, 3)]
    [InlineData(0x40, 35, 0)]
    public void SelectiveBypass_TheRawSignificancePass_ReadsSignificanceAndSign(int rawByte, int a, int b)
    {
        MqEncoder mq = Coder();
        // Cleanup at plane 4: A significant (zero-coding context 0), positive (sign context 9, XOR 0); B, with A to
        // its west, coded 0 in context 5. Planes 3, 2, 1: B's significance 0 in context 5 each time, A's refinement
        // 0 — the first in context 14, the next two in 16.
        foreach ((int context, int bit) in new[] { (0, 1), (9, 0), (5, 0), (5, 0), (14, 0), (5, 0), (16, 0), (5, 0), (16, 0) }) mq.Encode(context, bit);
        byte[] first = mq.Finish();
        byte[] raw = { (byte)rawByte };
        byte[] data = Concat(first, raw);
        JpxCodeBlock block = Block(2, 1, (first, 10, true), (raw, 2, true), (Array.Empty<byte>(), 1, true));

        int[] result = JpxBlockDecoder.Decode(block, data, 0, JpxCodeBlockStyle.SelectiveBypass, 5, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { a, b }, result);

        // Under the plain style the raw byte is arithmetic-coded data and the values differ.
        int[] plain = JpxBlockDecoder.Decode(Block(2, 1, (data, 13, false)), data, 0, JpxCodeBlockStyle.None, 5, 0, out _);
        Assert.NotEqual(new[] { a, b }, plain);
    }

    /// <summary>
    /// A malformed segment: the same twelve passes as one codeword segment under bypass, so the
    /// raw pass 11 falls inside the segment the arithmetic passes started (Table D.9 terminates
    /// before the first raw pass). The decoder reads the raw pass in its own mode from the
    /// segment's start rather than stopping — pass 11's bit is the segment's first bit — which is
    /// what a split layout whose raw segment starts at the same byte decodes to. The control shows
    /// the bit is live: a raw segment of ones and one of zeros decode to different values, and
    /// the malformed case matches the one the segment's first bit selects.
    /// </summary>
    [Fact]
    public void ASegmentWhosePassesChangeMode_IsReadInEachPassesModeFromItsStart()
    {
        MqEncoder mq = Coder();
        foreach ((int context, int bit) in new[] { (0, 1), (9, 0), (14, 0), (16, 1), (16, 1) }) mq.Encode(context, bit);
        byte[] first = mq.Finish();

        int[] merged = JpxBlockDecoder.Decode(Block(1, 1, (first, 12, true)), first, 0, JpxCodeBlockStyle.SelectiveBypass, 5, 0, out string problem);
        Assert.Null(problem);

        int[] withOnes = JpxBlockDecoder.Decode(Block(1, 1, (first, 10, true), (new byte[] { 0xFF }, 2, true)), Concat(first, new byte[] { 0xFF }), 0, JpxCodeBlockStyle.SelectiveBypass, 5, 0, out _);
        int[] withZeros = JpxBlockDecoder.Decode(Block(1, 1, (first, 10, true), (new byte[] { 0x00 }, 2, true)), Concat(first, new byte[] { 0x00 }), 0, JpxCodeBlockStyle.SelectiveBypass, 5, 0, out _);
        Assert.NotEqual(withOnes[0], withZeros[0]);
        Assert.Equal((first[0] & 0x80) != 0 ? withOnes[0] : withZeros[0], merged[0]);
        // A reader left where the arithmetic passes started would answer with padding ones; the
        // fixture's first byte starts with a 0 bit so that answer is the other value.
        Assert.True((first[0] & 0x80) == 0, $"fixture: the coder's first byte is {first[0]:X2}; the test needs a leading 0 bit");
    }

    /// <summary>
    /// Vertically causal contexts (§D.7): a column of five, the fifth (row 4, the next stripe)
    /// significant at plane 1. At plane 0 the fourth (row 3) sees no significant neighbour under
    /// the causal rule — context 0, so it is left to the cleanup pass, where the whole first stripe
    /// is run-length coded — whereas without the rule its context is 3 and the significance pass
    /// codes it. Two streams, one value; each decodes only under its own style.
    /// </summary>
    [Fact]
    public void VerticallyCausalContexts_IgnoreTheNextStripe()
    {
        MqEncoder causalCoder = Coder();
        foreach ((int context, int bit) in new[] { (17, 0), (0, 1), (9, 0), (14, 1), (17, 0) }) causalCoder.Encode(context, bit);
        byte[] causal = causalCoder.Finish();
        MqEncoder plainCoder = Coder();
        foreach ((int context, int bit) in new[] { (17, 0), (0, 1), (9, 0), (3, 0), (14, 1), (0, 0), (0, 0), (0, 0) }) plainCoder.Encode(context, bit);
        byte[] plain = plainCoder.Finish();

        int[] expected = { 0, 0, 0, 0, 7 };
        Assert.Equal(expected, JpxBlockDecoder.Decode(Block(1, 5, (causal, 4, false)), causal, 0, JpxCodeBlockStyle.VerticallyCausal, 2, 0, out string problem));
        Assert.Null(problem);
        Assert.Equal(expected, JpxBlockDecoder.Decode(Block(1, 5, (plain, 4, false)), plain, 0, JpxCodeBlockStyle.None, 2, 0, out problem));
        Assert.Null(problem);
        Assert.NotEqual(expected, JpxBlockDecoder.Decode(Block(1, 5, (plain, 4, false)), plain, 0, JpxCodeBlockStyle.VerticallyCausal, 2, 0, out _));
    }

    /// <summary>
    /// §D.7 from both sides of a stripe boundary, on a column of eight (two stripes) in two planes.
    /// Stream A: row 3 (the last of stripe 0) is found at plane 1 by run-length coding, so row 4 —
    /// the first of stripe 1 — sees it: context 3 in the first cleanup pass and again in plane 0's
    /// significance pass, where it becomes significant with the sign context 10 (a positive
    /// neighbour above); row 3's refinement at plane 0 does not see row 4 (context 14, not 15).
    /// Stream B: row 4 is found at plane 1; row 3 does not see it in plane 0's significance pass
    /// (not coded), so stripe 0 is still all-clear for plane 0's cleanup and run-length codes row 3
    /// there. Each decision's context is worked in the comments of the arrays. Stream B read
    /// without the causal rule codes row 3 in the significance pass and decodes something else.
    /// </summary>
    [Fact]
    public void VerticallyCausal_HidesTheNextStripeOnly_ThePreviousOneIsSeen()
    {
        // Stream A, cleanup at plane 1: stripe 0 run-length 1, first at index 3 (11), sign + (9);
        // stripe 1 is not run-length coded (row 4 has a significant neighbour above): row 4 in
        // context 3 → 0, rows 5–7 in context 0 → 0. Significance at plane 0: row 2 (context 3,
        // row 3 below it) → 0; row 4 (context 3) → 1, sign context 10 → 0 (positive); row 5
        // (context 3, row 4 above) → 0. Refinement at plane 0: row 3, first refinement, no
        // neighbour seen (row 4 is in the next stripe) → context 14 → 0. Cleanup at plane 0:
        // rows 0, 1, 6, 7 in context 0 → 0.
        MqEncoder a = Coder();
        foreach ((int context, int bit) in new[]
                 {
                     (17, 1), (18, 1), (18, 1), (9, 0), (3, 0), (0, 0), (0, 0), (0, 0),
                     (3, 0), (3, 1), (10, 0), (3, 0),
                     (14, 0),
                     (0, 0), (0, 0), (0, 0), (0, 0),
                 })
            a.Encode(context, bit);
        byte[] streamA = a.Finish();
        int[] decodedA = JpxBlockDecoder.Decode(Block(1, 8, (streamA, 4, false)), streamA, 0, JpxCodeBlockStyle.VerticallyCausal, 2, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 0, 0, 0, 5, 3, 0, 0, 0 }, decodedA);   // row 3: 2·2 + 1; row 4: 1·2 + 1

        // Stream B, cleanup at plane 1: stripe 0 run-length 0; stripe 1 run-length 1, first at
        // index 0 (00), sign + (9), then row 5 (context 3, row 4 above) → 0, rows 6, 7 → 0.
        // Significance at plane 0: stripe 0 has no context (row 4 is unseen) — nothing coded; row 5
        // (context 3) → 0. Refinement at plane 0: row 4, no neighbour → context 14 → 0. Cleanup at
        // plane 0: stripe 0 still all-clear: run-length 1, first at index 3 (11), sign + (9);
        // rows 6, 7 (context 0) → 0.
        MqEncoder b = Coder();
        foreach ((int context, int bit) in new[]
                 {
                     (17, 0), (17, 1), (18, 0), (18, 0), (9, 0), (3, 0), (0, 0), (0, 0),
                     (3, 0),
                     (14, 0),
                     (17, 1), (18, 1), (18, 1), (9, 0), (0, 0), (0, 0),
                 })
            b.Encode(context, bit);
        byte[] streamB = b.Finish();
        int[] decodedB = JpxBlockDecoder.Decode(Block(1, 8, (streamB, 4, false)), streamB, 0, JpxCodeBlockStyle.VerticallyCausal, 2, 0, out problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 0, 0, 0, 3, 5, 0, 0, 0 }, decodedB);   // row 3: 1·2 + 1; row 4: 2·2 + 1
        Assert.NotEqual(new[] { 0, 0, 0, 3, 5, 0, 0, 0 }, JpxBlockDecoder.Decode(Block(1, 8, (streamB, 4, false)), streamB, 0, JpxCodeBlockStyle.None, 2, 0, out _));
    }

    /// <summary>
    /// Reset of context probabilities (Table A.19): the contexts return to Table D.7 after every
    /// pass while the coder runs on. The single-coefficient stream of −5, coded with the contexts
    /// reset at each of its ten pass boundaries, decodes under the flag and not without it.
    /// </summary>
    [Fact]
    public void ResetContexts_RestoresTableD7AfterEveryPass()
    {
        // Decisions by pass: cleanup 3 [(0,0)]; cleanup 2 [(0,1),(9,1)]; refinement 1 [(14,0)]; refinement 0 [(16,1)].
        var passes = new (int Context, int Bit)[][]
        {
            new[] { (0, 0) }, new (int, int)[0], new (int, int)[0], new[] { (0, 1), (9, 1) },
            new (int, int)[0], new[] { (14, 0) }, new (int, int)[0], new (int, int)[0], new[] { (16, 1) }, new (int, int)[0],
        };
        MqEncoder coder = Coder();
        foreach ((int Context, int Bit)[] pass in passes)
        {
            foreach ((int context, int bit) in pass) coder.Encode(context, bit);
            ResetEncoder(coder);
        }
        byte[] data = coder.Finish();

        Assert.Equal(new[] { -11 }, JpxBlockDecoder.Decode(Block(1, 1, (data, 10, false)), data, 0, JpxCodeBlockStyle.ResetContexts, 4, 0, out string problem));
        Assert.Null(problem);
        Assert.NotEqual(-11, JpxBlockDecoder.Decode(Block(1, 1, (data, 10, false)), data, 0, JpxCodeBlockStyle.None, 4, 0, out _)[0]);
    }

    /// <summary>
    /// Termination on every pass (Table D.8): each pass is its own codeword segment, restarted by
    /// the decoder; with the contexts also reset, each is coded alone. Ten chunks, one per pass —
    /// most empty — decode to −5; read as one segment they do not.
    /// </summary>
    [Fact]
    public void TerminateEachPass_RestartsTheCoderPerSegment()
    {
        var passes = new (int Context, int Bit)[][]
        {
            new[] { (0, 0) }, new (int, int)[0], new (int, int)[0], new[] { (0, 1), (9, 1) },
            new (int, int)[0], new[] { (14, 0) }, new (int, int)[0], new (int, int)[0], new[] { (16, 1) }, new (int, int)[0],
        };
        var chunks = new List<(byte[] Data, int Passes, bool Terminated)>();
        foreach ((int Context, int Bit)[] pass in passes)
        {
            MqEncoder coder = Coder();
            foreach ((int context, int bit) in pass) coder.Encode(context, bit);
            chunks.Add((coder.Finish(), 1, true));
        }
        byte[] data = Concat(chunks.ConvertAll(c => c.Data).ToArray());
        JpxCodeBlockStyle style = JpxCodeBlockStyle.TerminateEachPass | JpxCodeBlockStyle.ResetContexts;

        Assert.Equal(new[] { -11 }, JpxBlockDecoder.Decode(Block(1, 1, chunks.ToArray()), data, 0, style, 4, 0, out string problem));
        Assert.Null(problem);
        Assert.NotEqual(-11, JpxBlockDecoder.Decode(Block(1, 1, (data, 10, false)), data, 0, JpxCodeBlockStyle.ResetContexts, 4, 0, out _)[0]);
    }

    /// <summary>§D.6's raw bits: MSB first, seven after 0xFF, and ones once the segment is spent.</summary>
    [Fact]
    public void RawReader_SkipsTheStuffedBit_AndPadsWithOnes()
    {
        var reader = new JpxRawReader(new byte[] { 0xFF, 0x55, 0x01 });
        reader.Start(0, 3);
        var bits = new List<int>();
        for (int i = 0; i < 8 + 7 + 8 + 3; i++) bits.Add(reader.Bit());
        Assert.Equal("11111111" + "1010101" + "00000001" + "111", string.Concat(bits));

        // A marker after 0xFF ends the data: ones from there.
        reader.Start(0, 3);
        var marker = new JpxRawReader(new byte[] { 0xFF, 0x90 });
        marker.Start(0, 2);
        var after = new List<int>();
        for (int i = 0; i < 12; i++) after.Add(marker.Bit());
        Assert.Equal("111111111111", string.Concat(after));
    }
}
