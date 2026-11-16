// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxHtRefinementEncoder"/>: HT sets with SigProp and MagRef passes (ITU-T T.814 §7.4, §7.5),
/// packed by Annex F's procedures, read back by <see cref="JpxHtBlockDecoder"/>. The expected decode is
/// worked out here from the clauses' rules, sample by sample — which samples SigProp visits, what each
/// known bit reconstructs to — not from the encoder's state.
/// </summary>
public sealed class JpxHtRefinementEncoderTests
{
    private const int Mb = 12;

    internal static long[] RandomBlock(int w, int h, int seed, int range, double zeros)
    {
        var random = new Random(seed);
        var q = new long[w * h];
        for (int i = 0; i < q.Length; i++)
            q[i] = random.NextDouble() < zeros ? 0 : random.Next(-range, range + 1);
        return q;
    }

    /// <summary>Decodes a set as its own code-block: the cleanup a terminated chunk of one pass, the refinement one of the rest.</summary>
    internal static int[] Decode(JpxHtSet set, int w, int h, bool verticallyCausal, out string problem)
    {
        byte[] data = set.Data();
        var block = new JpxCodeBlock(new JpxRect(0, 0, w, h)) { Included = true, ZeroBitPlanes = Mb - 1 - set.Plane };
        block.Add(new JpxCodeChunk(new JpxRange(0, set.Cleanup.Length), 1, terminated: true));
        if (set.Passes > 1)
            block.Add(new JpxCodeChunk(new JpxRange(set.Cleanup.Length, data.Length), set.Passes - 1, terminated: set.Passes == 3));
        JpxCodeBlockStyle style = JpxCodeBlockStyle.HighThroughput | (verticallyCausal ? JpxCodeBlockStyle.VerticallyCausal : 0);
        return JpxHtBlockDecoder.Decode(block, data, style, Mb, 0, out problem);
    }

    /// <summary>
    /// The decoder's value for each sample (twice the magnitude, the half at the last known plane) and the
    /// plane each sample is known to, worked out from §7.4 and §7.5: significant at plane p, its bits to p,
    /// and to p − 1 when MagRef ran; else, when SigProp ran and visited it — a significant sample, or a
    /// scan-earlier one SigProp set, in its propagation neighbourhood — its bit p − 1 and, if 1, its sign.
    /// </summary>
    internal static (long[] Values, int[] Known) Expected(long[] q, int w, int h, int p, int passes, bool verticallyCausal)
    {
        var values = new long[q.Length];
        var known = Enumerable.Repeat(p, q.Length).ToArray();
        bool[] sigma = q.Select(v => (Math.Abs(v) >> p) != 0).ToArray();
        var set = new bool[q.Length];
        if (passes >= 2)
        {
            for (int y0 = 0; y0 < h; y0 += 4)
                for (int gx = 0; gx < w; gx += 4)
                    for (int x = gx; x < Math.Min(w, gx + 4); x++)
                        for (int y = y0; y < Math.Min(h, y0 + 4); y++)
                        {
                            int i = y * w + x;
                            if (sigma[i]) continue;
                            int lastLine = verticallyCausal ? Math.Min(h, y0 + 4) - 1 : h - 1;
                            bool visited = false;
                            for (int ny = y - 1; ny <= y + 1; ny++)
                                for (int nx = x - 1; nx <= x + 1; nx++)
                                {
                                    if ((nx == x && ny == y) || nx < 0 || ny < 0 || nx >= w || ny > lastLine) continue;
                                    int j = ny * w + nx;
                                    // Stripe, then column-group, then column, then line: what the scan reached first.
                                    bool earlier = (ny / 4, nx / 4, nx, ny).CompareTo((y / 4, x / 4, x, y)) < 0;
                                    if (sigma[j] || (set[j] && earlier)) visited = true;
                                }
                            if (!visited) continue;
                            known[i] = p - 1;
                            if (((Math.Abs(q[i]) >> (p - 1)) & 1) == 1)
                            {
                                set[i] = true;
                                values[i] = Math.Sign(q[i]) * (3L << (p - 1));
                            }
                        }
        }
        for (int i = 0; i < q.Length; i++)
        {
            if (!sigma[i]) continue;
            long m = Math.Abs(q[i]);
            if (passes == 3) known[i] = p - 1;
            values[i] = Math.Sign(q[i]) * ((2 * (m >> known[i]) + 1) << known[i]);
        }
        return (values, known);
    }

    public static IEnumerable<object[]> Shapes()
    {
        foreach ((int w, int h) in new[] { (8, 8), (7, 5), (16, 13), (1, 9), (9, 1), (32, 32) })
            foreach (int p in new[] { 1, 2, 5 })
                foreach (bool vc in new[] { false, true })
                    yield return new object[] { w, h, p, vc };
    }

    /// <summary>
    /// Every set with SigProp alone and with SigProp and MagRef decodes to what its passes carry, on odd
    /// shapes (a stripe cut short, a column-group cut short, one row, one column) with and without
    /// vertical causality — and the error it reports is the error those known bits leave: each magnitude
    /// against the decode's half, nothing where every bit to plane 0 is known.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void ASetWithRefinementPasses_DecodesWhatTheyCarry(int w, int h, int p, bool vc)
    {
        int refined = 0;
        for (int seed = 0; seed < 6; seed++)
        {
            long[] q = RandomBlock(w, h, seed * 31 + w, 1 << (p + 3), zeros: 0.3);
            foreach (int passes in new[] { 2, 3 })
            {
                JpxHtSet set = JpxHtRefinementEncoder.EncodeSet(q, w, h, p, passes, vc);
                if (set == null) continue;
                int[] decoded = Decode(set, w, h, vc, out string problem);
                Assert.Null(problem);
                // An empty refinement segment is one pass to the decoder (§B.3): the expectation follows it.
                (long[] values, int[] known) = Expected(q, w, h, p, set.Refinement.Length == 0 ? 1 : passes, vc);
                Assert.Equal(values, decoded.Select(v => (long)v));
                if (set.Refinement.Length > 0) refined++;

                double error = 0;
                for (int i = 0; i < q.Length; i++)
                {
                    if (known[i] == 0) continue;
                    double e = Math.Abs(q[i]) - Math.Abs(values[i]) / 2.0;
                    error += e * e;
                }
                Assert.Equal(error, set.Remaining, 6);
            }
        }
        Assert.True(refined > 0, "no set carried a refinement byte");
    }

    // ------------------------------------------------------------------ Annex F's packers, byte by byte

    /// <summary>
    /// A decoder copes with a refinement segment that breaks Annex F's byte rules, so only the bytes can show
    /// them kept (CLAUDE.md, blind spot 1). termSPPacker: eight SigProp ones make 0xFF, and a 0x00 follows it,
    /// so the segment cannot end in 0xFF; the next byte after a 0xFF holds seven bits, its top bit the stuffing 0.
    /// </summary>
    [Fact]
    public void TheSigPropPacker_StuffsAfter0xFF_AndNeverEndsOnOne()
    {
        var sp = new JpxHtRefinementEncoder.SigPropPacker();
        for (int i = 0; i < 8; i++) sp.Emit(1);
        Assert.Equal(new byte[] { 0xFF, 0x00 }, sp.Terminated());
        for (int i = 0; i < 7; i++) sp.Emit(1);
        Assert.Equal(new byte[] { 0xFF, 0x7F }, sp.Terminated());
        sp.Emit(1);
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x01 }, sp.Terminated());
    }

    /// <summary>
    /// emitMRBit: the MagRef stream is read backwards from the segment's end, its last byte first, as if a 0xFF
    /// came after it — so seven ones there make 0x7F, its top bit the stuffing 0, and the eighth starts the next
    /// byte. After a byte at or below 0x8F, eight ones are a 0xFF.
    /// </summary>
    [Fact]
    public void TheMagRefPacker_StuffsSevenOnesAfterAHighByte()
    {
        var mr = new JpxHtRefinementEncoder.MagRefPacker();
        for (int i = 0; i < 8; i++) mr.Emit(1);
        Assert.Equal(new byte[] { 0x7F }, mr.Bytes);
        Assert.Equal((1, 1), (mr.Bits, mr.Tmp));

        var low = new JpxHtRefinementEncoder.MagRefPacker();
        foreach (int bit in new[] { 0, 0, 0, 0, 0, 0, 0, 0 }) low.Emit(bit);   // 0x00 written: the next byte may be full
        for (int i = 0; i < 8; i++) low.Emit(1);
        Assert.Equal(new byte[] { 0x00, 0xFF }, low.Bytes);
    }

    /// <summary>
    /// termSPandMRPackers: SigProp ending on a whole 0xFF meets a MagRef byte of 0x90 — a guard 0x00 goes
    /// between them, or the segment would hold 0xFF 0x90, a marker; two unfinished bytes whose bits agree are
    /// fused into one (0x05 and 0x01: SigProp's three bits 101, MagRef's two 01), and two that disagree are not.
    /// </summary>
    [Fact]
    public void JoiningTheStreams_GuardsA0xFF_AndFusesWhatAgrees()
    {
        var sp = new JpxHtRefinementEncoder.SigPropPacker();
        for (int i = 0; i < 8; i++) sp.Emit(1);
        var mr = new JpxHtRefinementEncoder.MagRefPacker();
        foreach (int bit in new[] { 0, 0, 0, 0, 1, 0, 0, 1 }) mr.Emit(bit);   // 0x90
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x90 }, JpxHtRefinementEncoder.Join(sp, mr, out int sigProp));
        Assert.Equal(2, sigProp);

        var a = new JpxHtRefinementEncoder.SigPropPacker();
        foreach (int bit in new[] { 1, 0, 1 }) a.Emit(bit);
        var b = new JpxHtRefinementEncoder.MagRefPacker();
        foreach (int bit in new[] { 1, 0 }) b.Emit(bit);
        Assert.Equal(new byte[] { 0x05 }, JpxHtRefinementEncoder.Join(a, b, out sigProp));
        Assert.Equal(1, sigProp);

        var c = new JpxHtRefinementEncoder.MagRefPacker();
        foreach (int bit in new[] { 0, 1 }) c.Emit(bit);   // bit 1 disagrees with SigProp's 0
        Assert.Equal(new byte[] { 0x05, 0x02 }, JpxHtRefinementEncoder.Join(a, c, out sigProp));
        Assert.Equal(1, sigProp);
    }

    /// <summary>
    /// Over many blocks, planes and both pass counts, no refinement segment holds a 0xFF followed by a byte
    /// above 0x8F — what a marker looks like (T.800 §A.1) — nor ends on 0xFF, where the next codeword segment
    /// would continue one.
    /// </summary>
    [Fact]
    public void NoRefinementSegment_HoldsAMarkerOrEndsOn0xFF()
    {
        int segments = 0;
        for (int seed = 0; seed < 400; seed++)
            foreach (int p in new[] { 1, 2, 3 })
            {
                long[] q = RandomBlock(16, 16, seed, 1 << (p + 2), zeros: 0.1);
                foreach (int passes in new[] { 2, 3 })
                {
                    JpxHtSet set = JpxHtRefinementEncoder.EncodeSet(q, 16, 16, p, passes, verticallyCausal: seed % 2 == 0);
                    if (set == null || set.Refinement.Length == 0) continue;
                    segments++;
                    byte[] r = set.Refinement;
                    Assert.NotEqual(0xFF, r[r.Length - 1]);
                    for (int i = 0; i + 1 < r.Length; i++)
                        Assert.False(r[i] == 0xFF && r[i + 1] > 0x8F, $"seed {seed}, plane {p}, {passes} passes: 0xFF 0x{r[i + 1]:X2} at {i}");
                }
            }
        Assert.True(segments > 1000, $"only {segments} refinement segments");
    }
}
