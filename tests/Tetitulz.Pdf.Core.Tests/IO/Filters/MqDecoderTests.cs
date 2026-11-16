// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the MQ arithmetic decoder (<see cref="MqDecoder"/>) that JBIG2 and JPEG 2000 share.
///
///   • The static Q-coder tables have the right shape (47 entries, NMPS/NLPS in
///     range, SWITCH values are 0 or 1, SWITCH set only at the three known
///     "exchange" states 0, 6, 14 per T.88 Table E.1).
///   • INITDEC + DECODE + BYTEIN don't throw on trivial input and don't escape
///     into the valid-state invariants.
///   • The contract JPEG 2000 adds — a segment bounded by an end offset, a restart that keeps the
///     context states, a packed initial state — round-tripped through <see cref="MqEncoder"/>,
///     each with a potency assertion, since a bound that is never reached passes everything.
/// </summary>
public class MqDecoderTests
{
    // ==================================================================
    //  Static-table shape invariants
    // ==================================================================

    [Fact]
    public void QeTable_HasExactly47Entries()
    {
        Assert.Equal(47, MqDecoder.QeTable.Count);
    }

    [Fact]
    public void QeTable_FirstEntryIs0x5601_AsPerSpec()
    {
        // T.88 Table E.1 — state 0 has Qe = 0x5601, the only state shared with
        // states 6, 14, and 46 (the "reset" states).
        Assert.Equal(0x5601, MqDecoder.QeTable[0]);
        Assert.Equal(0x5601, MqDecoder.QeTable[6]);
        Assert.Equal(0x5601, MqDecoder.QeTable[14]);
        Assert.Equal(0x5601, MqDecoder.QeTable[46]);
    }

    [Fact]
    public void NmpsTable_EveryEntryIsValidStateIndex()
    {
        foreach (int n in MqDecoder.NmpsTable)
            Assert.InRange(n, 0, 46);
    }

    [Fact]
    public void NlpsTable_EveryEntryIsValidStateIndex()
    {
        foreach (int n in MqDecoder.NlpsTable)
            Assert.InRange(n, 0, 46);
    }

    [Fact]
    public void SwitchTable_EntriesAreZeroOrOne_AndFlipOnlyAtResetStates()
    {
        var s = MqDecoder.SwitchTable;
        for (int i = 0; i < s.Count; i++)
        {
            Assert.True(s[i] == 0 || s[i] == 1, $"SWITCH[{i}] must be 0 or 1");
        }
        // Per T.88 Table E.1, SWITCH is 1 only at the three reset states 0, 6, 14.
        Assert.Equal(1, s[0]);
        Assert.Equal(1, s[6]);
        Assert.Equal(1, s[14]);
        Assert.Equal(0, s[1]);
        Assert.Equal(0, s[7]);
        Assert.Equal(0, s[46]);
    }

    // ==================================================================
    //  Instance-level behaviour — does not throw on realistic inputs
    // ==================================================================

    [Fact]
    public void InitDec_OnEmptyStream_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
        {
            _ = new MqDecoder(Array.Empty<byte>(), 0, contextCount: 1);
        });
        Assert.Null(ex);
    }

    /// <summary>
    /// An all-zero stream is not what an encoder writes, but the decoder must not wander into an
    /// invalid state or overrun it — and what it decodes past its end is fixed by BYTEIN's rule:
    /// the same bits as the same bytes followed by 0xFF padding written out. Pinned against that
    /// longer stream, with an all-ones stream as the potency control, where the test once asserted
    /// only that a bit is a bit (the review of 2026-09-26, row 23).
    /// </summary>
    [Fact]
    public void Decode_OnAnAllZeroStream_ReadsPastTheEndAsIfPaddedWith0xFF()
    {
        var d = new MqDecoder(new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, contextCount: 4);
        var padded = new MqDecoder(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, 0, contextCount: 4);
        var ones = new MqDecoder(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, contextCount: 4);
        var bits = new int[200];
        var onesBits = new int[200];
        for (int i = 0; i < 200; i++)
        {
            bits[i] = d.Decode(i & 3);
            Assert.Equal(bits[i], padded.Decode(i & 3));
            onesBits[i] = ones.Decode(i & 3);
        }
        Assert.NotEqual(bits, onesBits);
    }

    /// <summary>
    /// A two-byte stream read far past its end: BYTEIN pads with 0xFF (the marker rule), and the
    /// bits it then decodes are those of a stream that carried the same two bytes followed by
    /// 0xFF — pinned against that longer stream, bit for bit, so the padding is shown to be 0xFF.
    /// </summary>
    [Fact]
    public void Decode_PastTheEnd_ReadsAsIfPaddedWith0xFF()
    {
        // Sixty-four coded bits of a mixed pattern, so the decoder's state is moving when the bytes
        // run out; read on for 200 bits past them.
        var coder = new MqEncoder(1);
        var pattern = new int[64];
        for (int i = 0; i < 64; i++) { pattern[i] = (i * 37 + 11) % 5 < 2 ? 1 : 0; coder.Encode(0, pattern[i]); }
        byte[] coded = coder.Finish();
        // Finish() ends the segment with a marker (0xFF then a byte above 0x8F), and BYTEIN stops
        // reading at a marker whatever follows — so the marker is cut off here, and the decoder
        // really runs off the end of the bytes into the padding rule.
        Assert.True(coded.Length > 2 && coded[coded.Length - 2] == 0xFF && coded[coded.Length - 1] > 0x8F, "the coder should end with a marker");
        coded = coded.Take(coded.Length - 2).ToArray();
        byte[] withFF = coded.Concat(Enumerable.Repeat((byte)0xFF, 40)).ToArray();
        byte[] withZeros = coded.Concat(Enumerable.Repeat((byte)0x00, 40)).ToArray();
        var d = new MqDecoder(coded, 0, contextCount: 1);
        var padded = new MqDecoder(withFF, 0, contextCount: 1);
        var zeros = new MqDecoder(withZeros, 0, contextCount: 1);
        var bits = new int[264];
        var zeroBits = new int[264];
        for (int i = 0; i < 264; i++)
        {
            bits[i] = d.Decode(0);
            Assert.Equal(bits[i], padded.Decode(0));
            zeroBits[i] = zeros.Decode(0);
        }
        Assert.Equal(pattern.Take(32), bits.Take(32));        // the bytes decode to what was coded (the cut marker may cost the last bits)
        Assert.NotEqual(bits.Skip(64), zeroBits.Skip(64));    // the padding is 0xFF, not zeros: another tail decodes differently
    }

    [Fact]
    public void ContextStates_StartAtZero()
    {
        var d = new MqDecoder(
            new byte[] { 0x00 }, 0, contextCount: 8);
        for (int i = 0; i < 8; i++)
            Assert.Equal(0, d.ContextStates[i]);
    }

    [Fact]
    public void Decode_AfterSomeCalls_ContextStateStayInValidRange()
    {
        var d = new MqDecoder(
            new byte[] { 0xFF, 0x90, 0x00, 0x01, 0x23, 0x45, 0x67 }, 0, contextCount: 4);
        for (int i = 0; i < 50; i++) d.Decode(i & 3);

        foreach (int state in d.ContextStates)
        {
            int I = state >> 1;
            int mps = state & 1;
            Assert.InRange(I, 0, 46);
            Assert.InRange(mps, 0, 1);
        }
    }

    // ==================================================================
    //  The JPEG 2000 contract: a bounded segment, a restart that keeps the
    //  contexts, and a packed initial state
    // ==================================================================

    /// <summary>
    /// A decoder bounded to a segment reads <c>0xFF</c> past its end and never the bytes that
    /// follow — in a JPEG 2000 code-block, the next segment's. Decoding the segment with other bytes
    /// after it must give exactly what decoding it alone gives, and those bytes must be potent: an
    /// unbounded decoder over the same buffer reads them and decodes something else.
    /// </summary>
    [Fact]
    public void ABoundedSegment_NeverReadsTheBytesAfterItsEnd()
    {
        var (segment, bits) = EncodeSegment(600, seed: 7);
        byte[] prefix = { 0x9A, 0x00, 0xFF };           // a previous segment's tail, FF included
        byte[] after = { 0x00, 0x13, 0x7E, 0x42, 0x00, 0x55, 0x0F, 0x21, 0x33, 0x00, 0x6C, 0x01 };
        byte[] buffer = [.. prefix, .. segment, .. after];

        // Well past the segment's content, so the padding (or the bytes after) is certainly read.
        int count = bits.Length + 400;
        int[] alone     = DecodeAll(new MqDecoder(segment, 0, segment.Length, 4), count);
        int[] bounded   = DecodeAll(new MqDecoder(buffer, prefix.Length, prefix.Length + segment.Length, 4), count);
        int[] unbounded = DecodeAll(new MqDecoder(buffer, prefix.Length, buffer.Length, 4), count);

        Assert.Equal(bits, alone[..bits.Length]);       // the coder round-trips at all
        Assert.Equal(alone, bounded);                   // the bound hides what follows entirely
        Assert.NotEqual(alone, unbounded);              // …and what follows would have mattered
    }

    /// <summary>
    /// <see cref="MqDecoder.Restart"/> begins a new segment with the context states the last one
    /// left — JPEG 2000's terminate-each-pass coding. A fresh decoder on the second segment,
    /// preloaded with those states, must decode exactly the same bits; and a fresh decoder with
    /// zeroed contexts must not, which is what shows the states really carried over.
    /// </summary>
    [Fact]
    public void Restart_KeepsTheContextStates_AndReadsOnlyTheNewSegment()
    {
        var (first, _) = EncodeSegment(300, seed: 11);
        var (second, _) = EncodeSegment(300, seed: 23);
        byte[] buffer = [.. first, .. second];

        var d = new MqDecoder(buffer, 0, first.Length, 4);
        DecodeAll(d, 300);
        int[] carried = (int[])d.ContextStates.Clone();

        d.Restart(first.Length, buffer.Length);
        int[] restarted = DecodeAll(d, 300);

        var preloaded = new MqDecoder(second, 0, second.Length, 4);
        carried.CopyTo(preloaded.ContextStates, 0);
        var zeroed = new MqDecoder(second, 0, second.Length, 4);

        Assert.Equal(DecodeAll(preloaded, 300), restarted);
        Assert.NotEqual(DecodeAll(zeroed, 300), restarted);
    }

    /// <summary>
    /// <see cref="MqDecoder.State"/> packs a context's starting point. JPEG 2000 starts its uniform
    /// context in state 46, whose NMPS and NLPS both lead back to 46 — so however many decisions it
    /// codes, it must still be in state 46. A packing that put the index anywhere else would start
    /// the context in a state that adapts.
    /// </summary>
    [Fact]
    public void State_PacksTheIndex_SoTheUniformStateNeverAdapts()
    {
        var d = new MqDecoder(new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A }, 0, 5, contextCount: 1);
        d.ContextStates[0] = MqDecoder.State(46, 0);
        DecodeAll(d, 200);

        Assert.Equal(46, d.ContextStates[0] >> 1);
        Assert.Equal(93, MqDecoder.State(46, 1));       // the MPS is the low bit
    }

    /// <summary>
    /// Encodes a fixed pseudo-random decision sequence over four contexts and returns it without
    /// the <c>FF AC</c> marker FLUSH appends — the shape of a JPEG 2000 codeword segment, which the
    /// decoder must end by padding rather than by finding a marker.
    /// </summary>
    private static (byte[] Segment, int[] Bits) EncodeSegment(int count, int seed)
    {
        var rng = new Random(seed);
        var bits = new int[count];
        var enc = new MqEncoder(4);
        for (int i = 0; i < count; i++)
        {
            int cx = i & 3;
            bits[i] = rng.Next(10) < 2 + cx ? 1 : 0;    // skewed per context, so the coder adapts
            enc.Encode(cx, bits[i]);
        }

        byte[] all = enc.Finish();
        Assert.True(all.Length >= 2 && all[^2] == 0xFF && all[^1] == 0xAC,
            "the encoder's output no longer ends in the FF AC marker this helper strips");
        return (all[..^2], bits);
    }

    private static int[] DecodeAll(MqDecoder d, int count)
    {
        var bits = new int[count];
        for (int i = 0; i < count; i++) bits[i] = d.Decode(i % d.ContextStates.Length);
        return bits;
    }
}
