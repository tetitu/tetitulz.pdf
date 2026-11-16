// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Rate allocation for HT code-blocks (ITU-T T.814): a size target and quality layers, where
    /// <see cref="JpxRateControl"/>'s truncation of one embedded stream does not apply.
    /// </summary>
    /// <remarks>
    /// <para><b>An HT set is a re-coding, not a refinement.</b> Each set codes the block afresh one
    /// bit-plane further down (§B.1), and the decoder reads only the last whose cleanup segment has
    /// bytes (§B.3). So a block's choices are sets, not prefixes: a set costs its own segments and
    /// leaves its own error. The candidates are a cleanup at each plane from 0 (the whole magnitude) up
    /// to the block's largest magnitude's bits and, above plane 0, the same cleanup with its SigProp
    /// pass and with SigProp and MagRef (§7.4, §7.5) — a step of a third of a plane where a cleanup
    /// alone steps a whole one — each with its bytes and its weighted error; the target picks for
    /// every block the set minimising error + λ · bytes, with λ the smallest that fits.</para>
    /// <para><b>One HT set per block, and the layers decide when each block arrives.</b> A layer could
    /// re-code a block a plane further down with another set (MULTIHT, Ccap15 bit 13), and this
    /// library's decoder reads such files — but other decoders decode a block of several sets wrongly or not
    /// at all (2.5.4), so no independent decoder could check one, and none is
    /// written. Instead each block is coded once, at the plane the last layer's target chose, and the
    /// layers before it carry the blocks that remove the most error per byte first, as many as each
    /// layer's target allows. The codestream stays SINGLEHT.</para>
    /// <para><b>The measure is the packets.</b> λ, and each layer's share of the blocks, are searched
    /// against the segments' bytes, then the packets are written and measured; the header bytes that
    /// shows are taken off the allowance and the search repeated until the packets fit.</para>
    /// </remarks>
    internal static class JpxHtRateControl
    {
        private const int Unchosen = int.MaxValue;

        /// <summary>One block's choices: an HT set at each plane with one, two or three passes, its bytes and the weighted error it leaves.</summary>
        internal sealed class Candidates
        {
            internal Candidates(JpxHtSet[] sets, double weight, double energy, int magnitudeBitPlanes)
            {
                Sets = sets;
                Weight = weight;
                Energy = energy;
                MagnitudeBitPlanes = magnitudeBitPlanes;
            }

            /// <summary>
            /// Every set the block can be coded as: by plane from 0 up to its largest magnitude's bits, and at
            /// each plane above 0 the cleanup alone, with SigProp, and with SigProp and MagRef — a set whose
            /// refinement would add no byte left out (<see cref="JpxHtRefinementEncoder.EncodeSets"/>).
            /// </summary>
            internal JpxHtSet[] Sets { get; }

            /// <summary>What a unit of the block's error is worth in the samples: its band's step squared times the synthesis gains.</summary>
            internal double Weight { get; }

            /// <summary>The weighted squared error with nothing coded: the block's energy.</summary>
            internal double Energy { get; }

            /// <summary>The band's M<sub>b</sub>.</summary>
            internal int MagnitudeBitPlanes { get; }

            internal int BytesAt(int choice) => choice == Unchosen ? 0 : Sets[choice].Length;

            /// <summary>The weighted error set <paramref name="choice"/> leaves.</summary>
            internal double RemainingAt(int choice) => Sets[choice].Remaining * Weight;

            /// <summary>The error set <paramref name="choice"/> removes, per byte it costs: how early a layer should carry it.</summary>
            internal double Priority(int choice) => choice == Unchosen ? 0 : (Energy - RemainingAt(choice)) / Math.Max(1, Sets[choice].Length);
        }

        /// <summary>Which of a block's sets are coded: every one; the cleanups alone; or the whole block's only, all a codestream without a size target uses.</summary>
        internal enum SetsCoded
        {
            All,
            CleanupOnly,
            WholeOnly,
        }

        /// <summary>The candidates for one block of a band of <paramref name="magnitudeBitPlanes"/> planes, whose distortion is worth <paramref name="weight"/> in the samples.</summary>
        internal static Candidates Encode(long[] coefficients, int width, int height, int magnitudeBitPlanes, double weight, SetsCoded coded = SetsCoded.All,
            bool verticallyCausal = false)
        {
            long max = 0;
            double energy = 0;
            foreach (long q in coefficients)
            {
                max = Math.Max(max, Math.Abs(q));
                energy += (double)q * q;
            }
            int planes = coded == SetsCoded.WholeOnly ? Math.Min(1, JpxPacketEncoder.BitLength(max)) : JpxPacketEncoder.BitLength(max);
            var sets = new List<JpxHtSet>(3 * planes);
            for (int p = 0; p < planes; p++)
            {
                if (coded == SetsCoded.All)
                    sets.AddRange(JpxHtRefinementEncoder.EncodeSets(coefficients, width, height, p, verticallyCausal));
                else
                    sets.Add(JpxHtRefinementEncoder.EncodeSet(coefficients, width, height, p, 1, verticallyCausal: false));
            }
            return new Candidates(sets.ToArray(), weight, energy * weight, magnitudeBitPlanes);
        }

        /// <summary>
        /// Chooses every block's set and first layer and writes the tile's packets.
        /// <paramref name="targets"/> gives each layer's cumulative byte target for the packet data of
        /// the layers through it, or 0 and below for a layer that takes every block left — coded whole
        /// when it is the last. Returns the packet data.
        /// </summary>
        internal static List<byte> Allocate(JpxTileLayout layout, Dictionary<JpxCodeBlock, Candidates> candidates, long[] targets,
            bool sop, bool eph, WorkBudget budget)
        {
            int layers = layout.Layers;
            if (targets == null || targets.Length != layers) throw new ArgumentException("one target per layer", nameof(targets));

            var keys = new List<JpxCodeBlock>(candidates.Keys);
            var choice = new int[keys.Count];
            var first = new int[keys.Count];

            // The planes, from the last layer's target: every block whole without one.
            for (int i = 0; i < keys.Count; i++) first[i] = layers - 1;
            long last = targets[layers - 1];
            if (last <= 0)
            {
                // The first set is the cleanup at plane 0: the block whole.
                for (int i = 0; i < keys.Count; i++) choice[i] = candidates[keys[i]].Sets.Length > 0 ? 0 : Unchosen;
            }
            else
            {
                long overhead = 0;
                for (int attempt = 0; ; attempt++)
                {
                    double lambda = SmallestFittingLambda(keys, candidates, choice, last - overhead);
                    long segmentBytes = Fill(keys, candidates, choice, Choose(keys, candidates, choice, lambda), last - overhead);
                    long measured = Write(layout, keys, candidates, choice, first, layers, sop, eph, budget).Count;
                    if (measured <= last || attempt >= 8 || segmentBytes == 0) break;
                    overhead = Math.Max(overhead + 1, measured - segmentBytes);
                }
            }

            // The layers before the last: the blocks in order of the error they remove per byte, each
            // layer taking as many as its target allows (a layer with none takes every block left).
            var order = new List<int>();
            for (int i = 0; i < keys.Count; i++) if (choice[i] != Unchosen) order.Add(i);
            order.Sort((a, b) => candidates[keys[b]].Priority(choice[b]).CompareTo(candidates[keys[a]].Priority(choice[a])));
            int taken = 0;
            for (int l = 0; l < layers - 1; l++)
            {
                if (targets[l] <= 0)
                {
                    for (; taken < order.Count; taken++) first[order[taken]] = l;
                    break;
                }
                long overhead = 0;
                for (int attempt = 0; ; attempt++)
                {
                    int count = taken;
                    long bytes = SegmentBytesBefore(keys, candidates, choice, order, taken);
                    while (count < order.Count)
                    {
                        int next = candidates[keys[order[count]]].BytesAt(choice[order[count]]);
                        if (bytes + next > targets[l] - overhead) break;
                        bytes += next;
                        count++;
                    }
                    for (int k = taken; k < order.Count; k++) first[order[k]] = k < count ? l : layers - 1;
                    long measured = Write(layout, keys, candidates, choice, first, l + 1, sop, eph, budget).Count;
                    if (measured <= targets[l] || attempt >= 8 || count == taken)
                    {
                        taken = count;
                        break;
                    }
                    overhead = Math.Max(overhead + 1, measured - bytes);
                }
            }

            return Write(layout, keys, candidates, choice, first, layers, sop, eph, budget);
        }

        /// <summary>The segment bytes of the first <paramref name="count"/> blocks of <paramref name="order"/>.</summary>
        private static long SegmentBytesBefore(List<JpxCodeBlock> keys, Dictionary<JpxCodeBlock, Candidates> candidates, int[] choice,
            List<int> order, int count)
        {
            long bytes = 0;
            for (int k = 0; k < count; k++) bytes += candidates[keys[order[k]]].BytesAt(choice[order[k]]);
            return bytes;
        }

        /// <summary>
        /// The smallest λ whose choices fit <paramref name="allowed"/> segment bytes — 0, coding every
        /// block whole, when that fits. Bytes fall as λ rises, so a search over λ's exponent finds it.
        /// </summary>
        private static double SmallestFittingLambda(List<JpxCodeBlock> keys, Dictionary<JpxCodeBlock, Candidates> candidates, int[] choice, long allowed)
        {
            if (Choose(keys, candidates, choice, 0) <= allowed) return 0;
            double low = -60, high = 60;   // log2 λ
            if (Choose(keys, candidates, choice, Math.Pow(2, high)) > allowed) return Math.Pow(2, high);
            for (int i = 0; i < 64; i++)
            {
                double mid = (low + high) / 2;
                if (Choose(keys, candidates, choice, Math.Pow(2, mid)) <= allowed) high = mid;
                else low = mid;
            }
            return Math.Pow(2, high);
        }

        /// <summary>Every block's set at <paramref name="lambda"/>: the one minimising its error plus λ times its bytes, or none. Returns the segment bytes.</summary>
        private static long Choose(List<JpxCodeBlock> keys, Dictionary<JpxCodeBlock, Candidates> candidates, int[] choice, double lambda)
        {
            long bytes = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                Candidates c = candidates[keys[i]];
                int best = Unchosen;
                double bestCost = c.Energy;
                for (int k = 0; k < c.Sets.Length; k++)
                {
                    double cost = c.RemainingAt(k) + lambda * c.Sets[k].Length;
                    if (cost < bestCost) { bestCost = cost; best = k; }
                }
                choice[i] = best;
                bytes += c.BytesAt(best);
            }
            return bytes;
        }

        /// <summary>
        /// Spends what the choice at λ leaves of <paramref name="allowed"/>. λ is the smallest that fits, but a
        /// block's bytes come in steps, so the bytes fall in steps too, often well short of the allowance. Each
        /// block's best upgrade — the set removing the most error per extra byte — is taken, the best first,
        /// while the bytes fit; a block whose best did not fit is offered its best that does on the next round.
        /// Returns the segment bytes.
        /// </summary>
        internal static long Fill(List<JpxCodeBlock> keys, Dictionary<JpxCodeBlock, Candidates> candidates, int[] choice, long bytes, long allowed)
        {
            for (int round = 0; round < MaxFillRounds && bytes < allowed; round++)
            {
                var upgrades = new List<(double Slope, int Block, int Set, int Extra)>();
                for (int i = 0; i < keys.Count; i++)
                {
                    Candidates c = candidates[keys[i]];
                    double now = choice[i] == Unchosen ? c.Energy : c.RemainingAt(choice[i]);
                    int nowBytes = c.BytesAt(choice[i]);
                    int best = -1;
                    double bestSlope = 0;
                    for (int k = 0; k < c.Sets.Length; k++)
                    {
                        int extra = c.Sets[k].Length - nowBytes;
                        double gain = now - c.RemainingAt(k);
                        if (extra <= 0 || (round > 0 && bytes + extra > allowed)) continue;
                        // A set that removes nothing more has a slope of at most 0, never above the best's start.
                        double slope = gain / extra;
                        if (slope > bestSlope) { bestSlope = slope; best = k; }
                    }
                    if (best >= 0) upgrades.Add((bestSlope, i, best, c.Sets[best].Length - nowBytes));
                }
                if (upgrades.Count == 0) break;
                upgrades.Sort((a, b) => b.Slope.CompareTo(a.Slope));
                bool taken = false;
                foreach ((double _, int block, int set, int extra) in upgrades)
                {
                    if (bytes + extra > allowed) continue;
                    choice[block] = set;
                    bytes += extra;
                    taken = true;
                }
                if (!taken && round > 0) break;
            }
            return bytes;
        }

        /// <summary>How many times <see cref="Fill"/> offers each block an upgrade: the best one, then the best that fits what is left.</summary>
        private const int MaxFillRounds = 3;

        /// <summary>
        /// Lays every block out as its chosen HT set — P = M<sub>b</sub> − 1 − p for the cleanup at plane p, the
        /// set's passes all in the block's first layer — and writes the packets of layers 0 to
        /// <paramref name="layersWritten"/> − 1 in the codestream's order.
        /// </summary>
        private static List<byte> Write(JpxTileLayout layout, List<JpxCodeBlock> keys, Dictionary<JpxCodeBlock, Candidates> candidates,
            int[] choice, int[] first, int layersWritten, bool sop, bool eph, WorkBudget budget)
        {
            int layers = layout.Layers;
            var blocks = new Dictionary<JpxCodeBlock, JpxEncodedBlock>(keys.Count);
            for (int i = 0; i < keys.Count; i++)
            {
                Candidates c = candidates[keys[i]];
                JpxEncodedBlock block;
                if (choice[i] == Unchosen)
                {
                    block = new JpxEncodedBlock(new JpxBlockCode(Array.Empty<byte>(), 0, 0, Array.Empty<int>(), Array.Empty<double>()), 0, layers);
                }
                else
                {
                    JpxHtSet set = c.Sets[choice[i]];
                    block = new JpxEncodedBlock(Code(set), c.MagnitudeBitPlanes - 1 - set.Plane, layers) { Style = JpxCodeBlockStyle.HighThroughput };
                    for (int l = first[i]; l < layers; l++) block.LayerEnd[l] = set.Passes;
                }
                blocks.Add(keys[i], block);
            }

            var trees = new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>();
            var data = new List<byte>();
            int packetIndex = 0;
            foreach (JpxPacketAddress address in JpxPacketSequence.Enumerate(layout, null, budget))
            {
                budget.Tick();
                if (address.Layer < layersWritten)
                {
                    if (sop) JpxRateControl.AddSop(data, packetIndex);
                    JpxPacketEncoder.Write(layout, address, blocks, trees, eph, data);
                }
                packetIndex++;
            }
            return data;
        }

        /// <summary>
        /// A set as the packet writer takes a block: its segments end to end, and per pass the bytes through
        /// it — the cleanup's, then the SigProp bytes after it, then the whole refinement segment.
        /// </summary>
        internal static JpxBlockCode Code(JpxHtSet set)
        {
            var lengths = new int[set.Passes];
            lengths[0] = set.Cleanup.Length;
            if (set.Passes > 1) lengths[1] = set.Cleanup.Length + set.SigPropLength;
            if (set.Passes > 2) lengths[2] = set.Length;
            return new JpxBlockCode(set.Data(), set.Plane + 1, set.Passes, lengths, new double[set.Passes]);
        }
    }
}
