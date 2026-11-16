// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxBlockDecoder"/> on code-blocks whose decisions and contexts are chosen here by
/// hand from T.800 Tables D.1 to D.4 and coded with the shared MQ coder — so the test states what
/// Annex D says the bits mean, and the coder only carries them.
/// </summary>
public sealed class JpxBlockDecoderTests
{
    /// <summary>An MQ coder with Table D.7's initial states: uniform at 46, run-length at 3, the all-zero-neighbours context at 4.</summary>
    private static MqEncoder Coder()
    {
        var coder = new MqEncoder(JpxBlockDecoder.ContextCount);
        coder.SetState(18, 46, 0);
        coder.SetState(17, 3, 0);
        coder.SetState(0, 4, 0);
        return coder;
    }

    private static JpxCodeBlock Block(int width, int height, byte[] data, int passes, int zeroBitPlanes = 0)
    {
        var block = new JpxCodeBlock(new JpxRect(0, 0, width, height)) { Included = true, ZeroBitPlanes = zeroBitPlanes };
        block.Add(new JpxCodeChunk(new JpxRange(0, data.Length), passes, terminated: false));
        return block;
    }

    /// <summary>
    /// A single coefficient of value −5 in four bit-planes (0101). Its only context is the
    /// all-zero-neighbours one, and a one-row column has no run-length mode, so the decisions are:
    /// plane 3, cleanup: 0 (context 0); plane 2, cleanup: 1 (context 0) then the sign, 1, in
    /// context 9 (no neighbours, XOR bit 0); plane 1, refinement: 0 in context 14 (first refinement,
    /// no neighbours); plane 0, refinement: 1 in context 16. Ten passes, five decisions. The result
    /// is twice the magnitude with the half of E-6 at the last plane: −(5·2 + 1).
    /// </summary>
    [Fact]
    public void ASingleCoefficient_DecodesThroughItsTenPasses()
    {
        MqEncoder coder = Coder();
        coder.Encode(0, 0);
        coder.Encode(0, 1);
        coder.Encode(9, 1);
        coder.Encode(14, 0);
        coder.Encode(16, 1);
        byte[] data = coder.Finish();

        int[] full = JpxBlockDecoder.Decode(Block(1, 1, data, passes: 10), data, orientation: 0, JpxCodeBlockStyle.None,
            magnitudeBitPlanes: 4, roiShift: 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { -11 }, full);

        // Fewer passes reconstruct at a coarser plane, the half sitting one plane below the last decoded:
        // after plane 2's cleanup 4 + 2; after plane 1's refinement 4 + 1; before significance, 0.
        Assert.Equal(new[] { -12 }, JpxBlockDecoder.Decode(Block(1, 1, data, passes: 4), data, 0, JpxCodeBlockStyle.None, 4, 0, out _));
        Assert.Equal(new[] { -10 }, JpxBlockDecoder.Decode(Block(1, 1, data, passes: 7), data, 0, JpxCodeBlockStyle.None, 4, 0, out _));
        Assert.Equal(new[] { 0 }, JpxBlockDecoder.Decode(Block(1, 1, data, passes: 1), data, 0, JpxCodeBlockStyle.None, 4, 0, out _));
    }

    /// <summary>
    /// A coefficient of −3 in a four-plane band with two zero bit-planes signalled: only planes 1
    /// and 0 are coded, four passes in all. The first cleanup pass finds it significant at plane 1
    /// (1 in context 0, sign 1 in context 9), and plane 0's refinement (context 14) reads 1:
    /// −(3·2 + 1). Had the zero planes not been skipped, the same bits would say −12.
    /// </summary>
    [Fact]
    public void ZeroBitPlanes_SkipTheUncodedPlanes()
    {
        MqEncoder coder = Coder();
        coder.Encode(0, 1);
        coder.Encode(9, 1);
        coder.Encode(14, 1);
        byte[] data = coder.Finish();
        int[] result = JpxBlockDecoder.Decode(Block(1, 1, data, passes: 4, zeroBitPlanes: 2), data, 0, JpxCodeBlockStyle.None, 4, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { -7 }, result);
    }

    /// <summary>The shared MQ coder pair agrees on contexts started away from state 0 (Table D.7's 4, 3 and 46).</summary>
    [Fact]
    public void TheMqCoderPair_AgreesOnPresetContexts()
    {
        var decisions = new (int Context, int Bit)[] { (0, 0), (0, 1), (9, 1), (14, 0), (16, 1), (17, 1), (18, 0), (18, 1), (0, 1), (0, 0) };
        MqEncoder coder = Coder();
        foreach ((int context, int bit) in decisions) coder.Encode(context, bit);
        byte[] data = coder.Finish();

        var decoder = new MqDecoder(data, 0, data.Length, JpxBlockDecoder.ContextCount);
        JpxBlockDecoder.ResetContexts(decoder.ContextStates);
        var decoded = new List<int>();
        foreach ((int context, _) in decisions) decoded.Add(decoder.Decode(context));
        Assert.Equal(Array.ConvertAll(decisions, d => d.Bit), decoded.ToArray());
    }

    /// <summary>
    /// A column of four zero coefficients in one plane: the cleanup pass asks the run-length context
    /// once (Table D.5), and a 0 answers for all four. A 1 followed by two uniform bits names the
    /// first significant one — here the third (10) — whose sign follows in context 9; the fourth is
    /// then coded normally, and a vertical significant neighbour above gives it context 3.
    /// </summary>
    [Fact]
    public void ACleanColumn_UsesTheRunLengthContext()
    {
        MqEncoder allZero = Coder();
        allZero.Encode(17, 0);
        byte[] zeros = allZero.Finish();
        int[] result = JpxBlockDecoder.Decode(Block(1, 4, zeros, passes: 1), zeros, 0, JpxCodeBlockStyle.None, 1, 0, out string problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 0, 0, 0, 0 }, result);

        MqEncoder third = Coder();
        third.Encode(17, 1);
        third.Encode(18, 1);   // MSB of the index 2
        third.Encode(18, 0);
        third.Encode(9, 0);    // positive
        third.Encode(3, 1);    // the fourth: one vertical neighbour (Table D.1, LL: ΣV = 1)
        third.Encode(10, 0);   // its sign: vertical contribution +1, no horizontal — context 10, XOR 0
        byte[] data = third.Finish();
        result = JpxBlockDecoder.Decode(Block(1, 4, data, passes: 1), data, 0, JpxCodeBlockStyle.None, 1, 0, out problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 0, 0, 3, 3 }, result);
    }

    /// <summary>
    /// Table D.6's column — values 10, 1, 3 and −7 in four planes, an LL band, every other
    /// neighbour zero — decoded in the passes the standard lists, each decision's context worked
    /// from Tables D.1 to D.5 in the comments below: 22 decisions, and the reader can check each.
    /// </summary>
    [Fact]
    public void TableD6sColumn_DecodesInTheListedPasses()
    {
        var decisions = new List<(int Context, int Bit)>
        {
            // Plane 3, cleanup. All four insignificant with zero contexts: run-length 1, index 00
            // (the first is significant), its sign + in context 9 (no neighbours, XOR 0).
            (17, 1), (18, 0), (18, 0), (9, 0),
            // The rest of the column: second (context 3: one vertical neighbour, the first) 0;
            // third (context 0) 0; fourth (context 0) 0.
            (3, 0), (0, 0), (0, 0),
            // Plane 2. Significance: only the second has a non-zero context (3): 0. Refinement:
            // the first, first refinement, no significant neighbours: context 14, bit 0. Cleanup:
            // third (context 0) 0; fourth (context 0) 1, sign − in context 9 (decoded 1, XOR 0).
            (3, 0), (14, 0), (0, 0), (0, 1), (9, 1),
            // Plane 1. Significance: second (context 3) 0; third (context 3: one vertical
            // neighbour, the negative fourth) 1, sign + — V contribution −1 from below, H 0:
            // context 10 with XOR 1, so the decoded symbol is 1. Refinement: first (context 16) 1;
            // fourth (context 15: first refinement, with a significant neighbour) 1.
            (3, 0), (3, 1), (10, 1),
            (16, 1), (15, 1),
            // Plane 0. Significance: second (context 4: significant above and below) 1, sign + —
            // both contributions +1, V = 1: context 10, XOR 0. Refinement: first (16) 0; third
            // (15: first refinement, neighbours) 1; fourth (16) 1.
            (4, 1), (10, 0),
            (16, 0), (15, 1), (16, 1),
        };
        MqEncoder coder = Coder();
        foreach ((int context, int bit) in decisions) coder.Encode(context, bit);
        byte[] data = coder.Finish();

        int[] result = JpxBlockDecoder.Decode(Block(1, 4, data, passes: 10), data, 0, JpxCodeBlockStyle.None, 4, 0, out string problem);
        Assert.Null(problem);
        // Twice the value plus the half: 10 → 21, 1 → 3, 3 → 7, −7 → −15.
        Assert.Equal(new[] { 21, 3, 7, -15 }, result);
    }

    /// <summary>
    /// The Maxshift rule (§H.1): with a scaling of 3, a coefficient at or above 2^3 is ROI and is
    /// shifted down by 3; one below it is background and kept. Both are coded in the same block over
    /// M<sub>b</sub> + 3 planes.
    /// </summary>
    [Fact]
    public void RoiCoefficients_AreShiftedDown_BackgroundKept()
    {
        // Two coefficients in a 2x1 block, 5 planes (Mb 2 + shift 3): the first 8 (01000 — ROI: 1 << 3),
        // the second 3 (00011 — background). Plane 4: cleanup, both context 0: 0, 0. Plane 3: cleanup:
        // first 1 (context 0), sign + (context 9); second: context 5 (one horizontal neighbour, LL): 0.
        // Plane 2: significance: second (context 5) 0; refinement: first (14) 0. Plane 1: significance:
        // second (5) 1, sign: horizontal contribution +1 → context 12, XOR 0: +; refinement: first (16) 0.
        // Plane 0: refinement: first (16) 0; second (15) 1.
        var decisions = new (int Context, int Bit)[]
        {
            (0, 0), (0, 0),
            (0, 1), (9, 0), (5, 0),
            (5, 0), (14, 0),
            (5, 1), (12, 0), (16, 0),
            (16, 0), (15, 1),
        };
        MqEncoder coder = Coder();
        foreach ((int context, int bit) in decisions) coder.Encode(context, bit);
        byte[] data = coder.Finish();

        int[] shifted = JpxBlockDecoder.Decode(Block(2, 1, data, passes: 13), data, 0, JpxCodeBlockStyle.None, magnitudeBitPlanes: 2, roiShift: 3, out string problem);
        Assert.Null(problem);
        // 8 with its half (17) shifted down by 3 is 2 — the ROI value 1 with the half lost; 3 with its half (7) stays.
        Assert.Equal(new[] { 2, 7 }, shifted);

        // Control: without the shift the same bits are plain values 8 and 3.
        int[] plain = JpxBlockDecoder.Decode(Block(2, 1, data, passes: 13), data, 0, JpxCodeBlockStyle.None, 5, 0, out problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 17, 7 }, plain);

        // An RGN shift above what a magnitude can hold (SPrgn may be up to 255) puts every coefficient
        // below 2^s: all background, none shifted. Decoded over 30 planes with the zero bit-planes
        // making up the difference, the same bits are 17 and 7 scaled by 2^25 whatever the shift —
        // at 63 (1L << 64 wraps to 1: everything was "ROI", shifted by 63 & 31 to zero), at 62 (a
        // negative threshold) and at 31 alike; at 28 the first, 2^29 + 2^25, is ROI and is shifted.
        int[] scaled = { 17 << 25, 7 << 25 };
        foreach (int shift in new[] { 63, 62, 31 })
        {
            int[] high = JpxBlockDecoder.Decode(Block(2, 1, data, passes: 13, zeroBitPlanes: 2 + shift - 30), data, 0, JpxCodeBlockStyle.None, 2, shift, out problem);
            Assert.Null(problem);
            Assert.Equal(scaled, high);
        }
        int[] roi = JpxBlockDecoder.Decode(Block(2, 1, data, passes: 13, zeroBitPlanes: 0), data, 0, JpxCodeBlockStyle.None, 2, 28, out problem);
        Assert.Null(problem);
        Assert.Equal(new[] { 2, 7 << 25 }, roi);
    }

    /// <summary>A pass count past what the planes allow, and a plane count past 30, are reported and bounded.</summary>
    [Fact]
    public void ImpossiblePassAndPlaneCounts_AreReported_Bounded()
    {
        byte[] data = Coder().Finish();
        JpxBlockDecoder.Decode(Block(1, 1, data, passes: 11), data, 0, JpxCodeBlockStyle.None, 4, 0, out string problem);
        Assert.Contains("11 coding passes for 4 bit-planes, which take 10", problem, StringComparison.Ordinal);

        JpxBlockDecoder.Decode(Block(1, 1, data, passes: 1), data, 0, JpxCodeBlockStyle.None, 31, 0, out problem);
        Assert.Contains("31 bit-planes to decode; at most 30", problem, StringComparison.Ordinal);

        JpxBlockDecoder.Decode(Block(1, 1, data, passes: 1, zeroBitPlanes: 4), data, 0, JpxCodeBlockStyle.None, 4, 0, out problem);
        Assert.Contains("4 zero bit-planes of 4, and coding passes for none", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// E-2 and E-5: M<sub>b</sub> = G + ε<sub>b</sub> − 1, with ε<sub>b</sub> read per band under
    /// expounded or no quantisation and derived from the LL band's under derived quantisation.
    /// </summary>
    [Fact]
    public void MagnitudeBitPlanes_FollowEquationsE2AndE5()
    {
        var expounded = new JpxQuantization(JpxQuantizationStyle.ScalarExpounded, 2, new[] { 10, 9, 9, 8, 7, 7, 6 }, new int[7]);
        Assert.Equal(11, JpxQuantizationSteps.MagnitudeBitPlanes(expounded, 2, 0, 0));   // LL: 2 + 10 − 1
        Assert.Equal(9, JpxQuantizationSteps.MagnitudeBitPlanes(expounded, 2, 1, 3));    // HH of level 2 (index 3): 2 + 8 − 1
        Assert.Equal(7, JpxQuantizationSteps.MagnitudeBitPlanes(expounded, 2, 2, 3));    // HH of level 1: 2 + 6 − 1

        var derived = new JpxQuantization(JpxQuantizationStyle.ScalarDerived, 1, new[] { 10 }, new[] { 5 });
        Assert.Equal(10, JpxQuantizationSteps.MagnitudeBitPlanes(derived, 3, 0, 0));    // ε_0 − N_L + N_L
        Assert.Equal(8, JpxQuantizationSteps.MagnitudeBitPlanes(derived, 3, 3, 1));     // level 1: 10 − 3 + 1
        Assert.Equal(5, JpxQuantizationSteps.Mantissa(derived, 3, 1));
    }
}
