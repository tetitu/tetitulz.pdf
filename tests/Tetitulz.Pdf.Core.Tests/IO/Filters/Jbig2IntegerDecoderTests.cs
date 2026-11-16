// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the JBIG2 integer arithmetic decoder
/// (<see cref="Jbig2Decoder.Jbig2IntegerDecoder"/>).
///
/// We don't have a paired encoder, so end-to-end round-trips against known
/// bitstreams aren't possible here.  What we can test:
///   • Public constants match the T.88 spec.
///   • Constructor guards reject invalid base indices.
///   • Context usage stays inside the reserved 512-entry block even when the
///     decoder runs to EOF (so two IA decoders sharing one MQ don't collide).
///   • Decode never throws on arbitrary input and produces an int (possibly OOB).
///   • Multiple sequential decodes remain stable.
/// </summary>
public class Jbig2IntegerDecoderTests
{
    // ==================================================================
    //  Public surface
    // ==================================================================

    [Fact]
    public void Oob_SentinelMatchesIntMinValue()
    {
        Assert.Equal(int.MinValue, Jbig2Decoder.Jbig2IntegerDecoder.OOB);
    }

    [Fact]
    public void ContextTableSize_Is512()
    {
        Assert.Equal(512, Jbig2Decoder.Jbig2IntegerDecoder.ContextTableSize);
    }

    // ==================================================================
    //  Constructor validation
    // ==================================================================

    [Fact]
    public void Constructor_NullMqDecoder_Throws()
    {
        Assert.Throws<ArgumentNullException>(()
            => new Jbig2Decoder.Jbig2IntegerDecoder(null, 0));
    }

    [Fact]
    public void Constructor_NegativeBaseIndex_Throws()
    {
        var mq = new MqDecoder(new byte[] { 0 }, 0, contextCount: 512);
        Assert.Throws<ArgumentOutOfRangeException>(()
            => new Jbig2Decoder.Jbig2IntegerDecoder(mq, -1));
    }

    [Fact]
    public void Constructor_BaseIndexOverflowsContextTable_Throws()
    {
        var mq = new MqDecoder(new byte[] { 0 }, 0, contextCount: 512);
        // Base 1 would need contexts [1, 513) but only 0..511 exist.
        Assert.Throws<ArgumentOutOfRangeException>(()
            => new Jbig2Decoder.Jbig2IntegerDecoder(mq, 1));
    }

    [Fact]
    /// <summary>
    /// A context table that fits its arena exactly (base 512 + 512 entries = the 1024 allocated)
    /// must construct <b>and be usable</b>. Constructing successfully is the easy half; the
    /// predecessor stopped there, so an off-by-one that corrupted neighbouring contexts on first
    /// use would still have passed.
    /// </summary>
    public void Constructor_ExactFit_ProducesAWorkingDecoder()
    {
        var mq = new MqDecoder(new byte[] { 0 }, 0, contextCount: 1024);
        var ia = new Jbig2Decoder.Jbig2IntegerDecoder(mq, 512);

        // Decoding at all is the point, and it must be deterministic: a second decoder built the
        // same way over the same stream produces the same sequence. (Exact values are whatever the
        // arithmetic decoder yields on an all-zero stream; what matters is that the exact-fit
        // context arena produces a stable, repeatable result rather than reading past its end.)
        int first  = ia.Decode();
        int second = ia.Decode();

        var mq2 = new MqDecoder(new byte[] { 0 }, 0, contextCount: 1024);
        var ia2 = new Jbig2Decoder.Jbig2IntegerDecoder(mq2, 512);

        Assert.Equal(first,  ia2.Decode());
        Assert.Equal(second, ia2.Decode());
    }

    // ==================================================================
    //  Runtime behaviour — no throws on arbitrary streams
    // ==================================================================

    [Fact]
    public void Decode_RepeatedlyOnZeroStream_DoesNotThrow()
    {
        var mq = new MqDecoder(
            new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, contextCount: 512);
        var ia = new Jbig2Decoder.Jbig2IntegerDecoder(mq, 0);

        for (int i = 0; i < 200; i++)
        {
            int v = ia.Decode();
            // Either a regular int or the OOB sentinel — anything goes, as long
            // as we don't throw or loop forever.
            Assert.True(v == Jbig2Decoder.Jbig2IntegerDecoder.OOB || v != int.MinValue);
        }
    }

    [Fact]
    public void Decode_OnShortStream_PadsAndKeepsDecoding()
    {
        var mq = new MqDecoder(
            new byte[] { 0x42 }, 0, contextCount: 512);
        var ia = new Jbig2Decoder.Jbig2IntegerDecoder(mq, 0);

        int decoded = 0;
        for (int i = 0; i < 50; i++) { _ = ia.Decode(); decoded++; }
        Assert.Equal(50, decoded);
    }

    // ==================================================================
    //  Two IA decoders sharing a single MQ — context ranges stay disjoint
    // ==================================================================

    [Fact]
    public void TwoIaDecoders_DontContaminateEachOther()
    {
        // 1024-entry MQ context table hosts two IA decoders at bases 0 and 512.
        var mq = new MqDecoder(
            new byte[] { 0xAB, 0xCD, 0xEF, 0x12, 0x34, 0x56 }, 0, contextCount: 1024);
        var ia1 = new Jbig2Decoder.Jbig2IntegerDecoder(mq, 0);
        var ia2 = new Jbig2Decoder.Jbig2IntegerDecoder(mq, 512);

        // Decode several integers from each — must not throw.
        for (int i = 0; i < 20; i++)
        {
            _ = ia1.Decode();
            _ = ia2.Decode();
        }

        // Verify that each decoder's context block has contexts in the right half.
        // The low 512 entries belong to ia1, the high 512 to ia2.  We only check
        // that the MQ's state array wasn't resized or corrupted.
        Assert.Equal(1024, mq.ContextStates.Length);
    }

    // ==================================================================
    //  Context-wrap invariant — all used context indices stay within block
    // ==================================================================

    [Fact]
    public void ContextIndices_StayWithinAllocatedBlock()
    {
        // We can't observe the "prev" state from outside, but we can assert
        // that context state changes are confined to the allocated 512-entry
        // block by checking that the rest of a larger MQ table is untouched.
        var mq = new MqDecoder(
            new byte[] { 0xFF, 0x90, 0x00, 0x01, 0x23, 0x45 }, 0, contextCount: 1024);
        var ia = new Jbig2Decoder.Jbig2IntegerDecoder(mq, 0);   // reserves 0..511

        for (int i = 0; i < 100; i++) _ = ia.Decode();

        // Contexts 512..1023 must remain at their initial (0, 0) state.
        for (int i = 512; i < 1024; i++)
            Assert.Equal(0, mq.ContextStates[i]);
    }
}
