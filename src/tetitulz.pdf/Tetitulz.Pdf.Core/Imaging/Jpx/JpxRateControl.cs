// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Post-compression rate-distortion allocation (T.800 Annex D's passes as the units, the
    /// method of J.10.1's informative note): every code-block is coded whole, then each layer takes
    /// from every block the passes whose distortion removed per byte is above one threshold for
    /// the whole tile, the threshold chosen so the layer's cumulative bytes fit its target. The
    /// candidates are a block's convex-hull truncation points — a pass that removes less per byte
    /// than the pass after it is never a stopping place, since stopping after the next is better
    /// on both counts. A layer with no target takes every pass that is left.
    /// </summary>
    /// <remarks>
    /// <para><b>The measure is the packets themselves.</b> A target counts headers, SOP and EPH
    /// markers and empty packets, so a candidate threshold is judged by writing the layer's
    /// packets and measuring. A precinct's packets are written in layer order whatever the
    /// progression, and a packet's bits depend only on its precinct's earlier packets, so a
    /// layer's bytes are the same in any order: each trial writes one layer's packets from the
    /// state the layers before it left — the blocks' and tag trees' — and the accepted trial's
    /// state becomes the next layer's base. The search is a binary search over the distinct
    /// slopes of the tile, so a layer costs a few dozen writes of itself, and the whole is written
    /// once more at the end in the codestream's own order.</para>
    /// <para><b>A block's distortion is weighed before it is ranked</b> (<see cref="JpxEncodedBlock.Weight"/>):
    /// the band's step squared, its synthesis gain and the component's, so a byte is compared by
    /// what it does to the samples, not to the coefficients.</para>
    /// </remarks>
    internal static class JpxRateControl
    {
        /// <summary>A block's feasible truncation points: after <c>Ends[i]</c> passes, at slope <c>Slopes[i]</c> (distortion per byte from the point before), slopes strictly decreasing.</summary>
        internal sealed class Hull
        {
            internal Hull(int[] ends, double[] slopes)
            {
                Ends = ends;
                Slopes = slopes;
            }

            internal int[] Ends { get; }
            internal double[] Slopes { get; }

            /// <summary>The passes included at <paramref name="threshold"/>: through the last point whose slope is at least it; 0 when none is.</summary>
            internal int EndAt(double threshold)
            {
                int end = 0;
                for (int i = 0; i < Ends.Length && Slopes[i] >= threshold; i++) end = Ends[i];
                return end;
            }
        }

        /// <summary>
        /// The upper convex hull of a block's (bytes, weighted distortion removed) points, the
        /// origin first: each pass in turn, dropping the points before it that a straight line
        /// from an earlier point to it would pass above. Passes that add bytes without removing
        /// distortion are never points; passes that remove distortion for no bytes have an
        /// infinite slope.
        /// </summary>
        internal static Hull HullOf(JpxBlockCode code, double weight)
        {
            var ends = new List<int>();
            var bytes = new List<long> { 0 };       // index 0 is the origin; ends[i] pairs with bytes[i + 1]
            var gains = new List<double> { 0 };
            var slopes = new List<double>();
            for (int k = 0; k < code.Passes; k++)
            {
                long l = code.Lengths[k];
                double d = code.Distortion[k] * weight;
                if (d <= gains[gains.Count - 1]) continue;   // nothing removed since the last point
                // Drop the last point while the line from the point before it to this pass is at or above it.
                while (ends.Count > 0)
                {
                    int last = ends.Count;   // index into bytes/gains
                    double toThis = Slope(bytes[last - 1], gains[last - 1], l, d);
                    if (toThis < slopes[slopes.Count - 1]) break;
                    ends.RemoveAt(ends.Count - 1);
                    bytes.RemoveAt(last);
                    gains.RemoveAt(last);
                    slopes.RemoveAt(slopes.Count - 1);
                }
                slopes.Add(Slope(bytes[bytes.Count - 1], gains[gains.Count - 1], l, d));
                ends.Add(k + 1);
                bytes.Add(l);
                gains.Add(d);
            }
            return new Hull(ends.ToArray(), slopes.ToArray());
        }

        private static double Slope(long l0, double d0, long l1, double d1) => l1 > l0 ? (d1 - d0) / (l1 - l0) : double.PositiveInfinity;

        /// <summary>The write state of every block and tag tree after some layers: what a trial of the next layer starts from.</summary>
        private sealed class State
        {
            internal State(List<JpxEncodedBlock> blocks, Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees> trees)
            {
                Included = new bool[blocks.Count];
                Lblock = new int[blocks.Count];
                PassesWritten = new int[blocks.Count];
                BytesWritten = new int[blocks.Count];
                for (int i = 0; i < blocks.Count; i++)
                {
                    Included[i] = blocks[i].Included;
                    Lblock[i] = blocks[i].Lblock;
                    PassesWritten[i] = blocks[i].PassesWritten;
                    BytesWritten[i] = blocks[i].BytesWritten;
                }
                Trees = new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>(trees.Count);
                foreach (KeyValuePair<JpxPrecinctBand, JpxPrecinctBandTrees> pair in trees)
                    Trees.Add(pair.Key, new JpxPrecinctBandTrees(pair.Value.Inclusion.Clone(), pair.Value.ZeroBitPlanes.Clone()));
            }

            internal bool[] Included { get; }
            internal int[] Lblock { get; }
            internal int[] PassesWritten { get; }
            internal int[] BytesWritten { get; }
            internal Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees> Trees { get; }

            internal void Restore(List<JpxEncodedBlock> blocks, Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees> trees)
            {
                for (int i = 0; i < blocks.Count; i++)
                {
                    blocks[i].Included = Included[i];
                    blocks[i].Lblock = Lblock[i];
                    blocks[i].PassesWritten = PassesWritten[i];
                    blocks[i].BytesWritten = BytesWritten[i];
                }
                trees.Clear();
                foreach (KeyValuePair<JpxPrecinctBand, JpxPrecinctBandTrees> pair in Trees)
                    trees.Add(pair.Key, new JpxPrecinctBandTrees(pair.Value.Inclusion.Clone(), pair.Value.ZeroBitPlanes.Clone()));
            }
        }

        /// <summary>
        /// Divides every block's passes among the tile's layers and writes the tile's packets.
        /// <paramref name="targets"/> gives each layer's cumulative byte target for the packet data
        /// of the layers through it (SOP and EPH included; the tile-part header not), or 0 and
        /// below for a layer that takes everything left. Returns the packet data.
        /// </summary>
        internal static List<byte> Allocate(JpxTileLayout layout, Dictionary<JpxCodeBlock, JpxEncodedBlock> blocks, long[] targets,
            bool sop, bool eph, WorkBudget budget)
        {
            int layers = layout.Layers;
            if (targets == null || targets.Length != layers) throw new ArgumentException("one target per layer", nameof(targets));

            var list = new List<JpxEncodedBlock>(blocks.Values);
            var hulls = new Dictionary<JpxCodeBlock, Hull>(blocks.Count);
            var thresholds = new List<double>();
            foreach (KeyValuePair<JpxCodeBlock, JpxEncodedBlock> pair in blocks)
            {
                Hull hull = HullOf(pair.Value.Code, pair.Value.Weight);
                hulls.Add(pair.Key, hull);
                foreach (double s in hull.Slopes) if (!double.IsInfinity(s)) thresholds.Add(s);
            }
            thresholds.Sort();
            thresholds.Reverse();
            int distinct = 0;
            for (int i = 0; i < thresholds.Count; i++)
                if (distinct == 0 || thresholds[i] != thresholds[distinct - 1]) thresholds[distinct++] = thresholds[i];
            thresholds.RemoveRange(distinct, thresholds.Count - distinct);

            // One layer's packets, every precinct: the order does not change the bytes (see the remarks).
            var addresses = new List<JpxPacketAddress>();
            for (int c = 0; c < layout.Components.Length; c++)
                for (int r = 0; r < layout.Components[c].Resolutions.Length; r++)
                    for (int p = 0; p < layout.Components[c].Resolutions[r].Precincts.Length; p++)
                        addresses.Add(new JpxPacketAddress(0, r, c, p));

            foreach (JpxEncodedBlock b in list) b.ResetWriteState();
            var trees = new Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees>();
            long written = 0;
            for (int l = 0; l < layers; l++)
            {
                if (targets[l] <= 0)
                {
                    foreach (JpxEncodedBlock b in list) SetLayer(b, l, b.Passes, layers);
                    written += WriteLayer(layout, l, addresses, blocks, trees, sop, eph, budget);
                    continue;
                }
                var basis = new State(list, trees);
                long allowed = targets[l] - written;

                // The threshold index t: −1 adds nothing to the layer, thresholds.Count − 1 every hull point.
                // Bytes grow with t; find the largest t within the layer's allowance — or −1, which is
                // what the packets cost empty, when even that is over.
                int low = -1, high = thresholds.Count - 1;
                Apply(list, hulls, blocks, l, low, thresholds, layers);
                long best = WriteLayer(layout, l, addresses, blocks, trees, sop, eph, budget);
                if (best <= allowed)
                {
                    while (low < high)
                    {
                        int mid = high - (high - low) / 2;
                        basis.Restore(list, trees);
                        Apply(list, hulls, blocks, l, mid, thresholds, layers);
                        long trial = WriteLayer(layout, l, addresses, blocks, trees, sop, eph, budget);
                        if (trial <= allowed)
                        {
                            low = mid;
                            best = trial;
                        }
                        else
                        {
                            high = mid - 1;
                        }
                    }
                }
                // The state is the last trial's; advance from the base with the accepted one.
                basis.Restore(list, trees);
                Apply(list, hulls, blocks, l, low, thresholds, layers);
                written += WriteLayer(layout, l, addresses, blocks, trees, sop, eph, budget);
            }

            // The codestream's order, from a clean state.
            foreach (JpxEncodedBlock b in list) b.ResetWriteState();
            trees.Clear();
            var data = new List<byte>();
            int packetIndex = 0;
            foreach (JpxPacketAddress address in JpxPacketSequence.Enumerate(layout, null, budget))
            {
                budget.Tick();
                if (sop) AddSop(data, packetIndex);
                JpxPacketEncoder.Write(layout, address, blocks, trees, eph, data);
                packetIndex++;
            }
            // The layers measured one at a time, from saved states, must total what the packets
            // take in the codestream's order: a trial that leaked into the saved state, or a
            // marker not counted, would have fitted the target to bytes that are not the file's.
            if (data.Count != written)
                throw new InvalidOperationException($"the layers measured {written} bytes; the packets written in order take {data.Count}");
            return data;
        }

        /// <summary>Layer <paramref name="l"/> takes, from each block, the hull point at threshold index <paramref name="t"/> (−1: nothing new). Later layers are set in their own turn — every layer is applied before it is written, and nothing reads a layer that is not.</summary>
        private static void Apply(List<JpxEncodedBlock> list, Dictionary<JpxCodeBlock, Hull> hulls, Dictionary<JpxCodeBlock, JpxEncodedBlock> blocks,
            int l, int t, List<double> thresholds, int layers)
        {
            foreach (KeyValuePair<JpxCodeBlock, JpxEncodedBlock> pair in blocks)
            {
                JpxEncodedBlock b = pair.Value;
                int previous = l == 0 ? 0 : b.LayerEnd[l - 1];
                int end = t < 0 ? previous : Math.Max(previous, hulls[pair.Key].EndAt(thresholds[t]));
                SetLayer(b, l, end, layers);
            }
        }

        private static void SetLayer(JpxEncodedBlock b, int l, int end, int layers) => b.LayerEnd[l] = end;

        /// <summary>Writes layer <paramref name="l"/>'s packets from the current state, advancing it; the bytes they take, SOP markers included.</summary>
        private static long WriteLayer(JpxTileLayout layout, int l, List<JpxPacketAddress> addresses, Dictionary<JpxCodeBlock, JpxEncodedBlock> blocks,
            Dictionary<JpxPrecinctBand, JpxPrecinctBandTrees> trees, bool sop, bool eph, WorkBudget budget)
        {
            var scratch = new List<byte>();
            foreach (JpxPacketAddress a in addresses)
            {
                budget.Tick();
                JpxPacketEncoder.Write(layout, new JpxPacketAddress(l, a.Resolution, a.Component, a.Precinct), blocks, trees, eph, scratch);
            }
            return scratch.Count + (sop ? 6L * addresses.Count : 0);
        }

        /// <summary>An SOP marker segment (§A.8.1): FF 91, Lsop 4, Nsop.</summary>
        internal static void AddSop(List<byte> data, int packetIndex)
        {
            data.Add(0xFF);
            data.Add(0x91);
            data.Add(0);
            data.Add(4);
            data.Add((byte)(packetIndex >> 8));
            data.Add((byte)packetIndex);
        }
    }
}
