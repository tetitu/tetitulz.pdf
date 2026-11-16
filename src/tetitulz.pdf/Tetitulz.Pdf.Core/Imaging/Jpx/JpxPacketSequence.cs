// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>Which packet: layer, resolution level, component and precinct (T.800 §B.9).</summary>
    internal readonly struct JpxPacketAddress
    {
        internal JpxPacketAddress(int layer, int resolution, int component, int precinct)
        {
            Layer = layer;
            Resolution = resolution;
            Component = component;
            Precinct = precinct;
        }

        internal int Layer { get; }
        internal int Resolution { get; }
        internal int Component { get; }
        internal int Precinct { get; }

        public override string ToString() => $"layer {Layer}, resolution {Resolution}, component {Component}, precinct {Precinct}";
    }

    /// <summary>
    /// The order a tile's packets take in the codestream (T.800 §B.12): the COD's progression, or
    /// the POC's progression order volumes in turn, each bounded by its start and end points (B-21)
    /// and never repeating a packet — a precinct's next layer is always the one after the last it
    /// received (§B.12.2). The position-driven orders step through the tile on the reference grid
    /// and admit a precinct at the positions §B.12.1.3 names; equation B-20 gives its index.
    /// </summary>
    internal static class JpxPacketSequence
    {
        private struct Volume
        {
            internal int LayerEnd, ResolutionStart, ResolutionEnd, ComponentStart, ComponentEnd;
            internal JpxProgressionOrder Order;
        }

        /// <summary>The tile's packets in codestream order, each precinct's layers ascending.</summary>
        /// <param name="layout">The tile.</param>
        /// <param name="changes">The POC volumes in effect, or null for the COD's single progression.</param>
        /// <param name="budget">Ticked once per reference-grid position a position-driven order visits.</param>
        internal static IEnumerable<JpxPacketAddress> Enumerate(JpxTileLayout layout, IReadOnlyList<JpxProgressionChange> changes, WorkBudget budget)
        {
            var next = new int[layout.Components.Length][][];
            var lowest = new int[layout.Components.Length][];   // a lower bound on next[c][r]'s least
            for (int c = 0; c < next.Length; c++)
            {
                JpxResolution[] resolutions = layout.Components[c].Resolutions;
                next[c] = new int[resolutions.Length][];
                lowest[c] = new int[resolutions.Length];
                for (int r = 0; r < resolutions.Length; r++) next[c][r] = new int[(int)resolutions[r].PrecinctCount];
            }

            int maxResolutions = layout.MaxLevels + 1;
            var volumes = new List<Volume>();
            if (changes == null || changes.Count == 0)
            {
                volumes.Add(new Volume
                {
                    LayerEnd = layout.Layers, ResolutionStart = 0, ResolutionEnd = maxResolutions,
                    ComponentStart = 0, ComponentEnd = layout.Components.Length, Order = layout.Coding.ProgressionOrder,
                });
            }
            else
            {
                foreach (JpxProgressionChange change in changes)
                {
                    volumes.Add(new Volume
                    {
                        LayerEnd = System.Math.Min(change.LayerEnd, layout.Layers),
                        ResolutionStart = change.ResolutionStart,
                        ResolutionEnd = System.Math.Min(change.ResolutionEnd, maxResolutions),
                        ComponentStart = change.ComponentStart,
                        ComponentEnd = System.Math.Min(change.ComponentEnd, layout.Components.Length),
                        Order = change.Order,
                    });
                }
            }

            foreach (Volume v in volumes)
            {
                IEnumerable<JpxPacketAddress> packets;
                switch (v.Order)
                {
                    case JpxProgressionOrder.Lrcp: packets = Lrcp(layout, v, next, lowest, budget); break;
                    case JpxProgressionOrder.Rlcp: packets = Rlcp(layout, v, next, lowest, budget); break;
                    default: packets = PositionDriven(layout, v, next, budget); break;
                }
                foreach (JpxPacketAddress p in packets) yield return p;
            }
        }

        // Every (l, r, c) step is charged, whether or not it yields: a progression volume restating
        // an earlier one yields nothing, and a codestream can carry a POC in every tile-part, so
        // the consumer's per-packet tick alone bounds nothing.
        //
        // The step's precinct scan is priced too. One tick a step would leave the scan free, and a
        // resolution of four million precincts, restated by thousands of POC volumes, would be that many
        // scans for as many ticks. A scan that cannot yield is skipped: at (l, r, c) every precinct
        // has had its layers below l (the step before it advanced them), so none is at l when the
        // lowest next layer recorded for (c, r) is above it — a lower bound, since the
        // position-driven orders advance precincts without lowering it. A scan that runs is charged
        // the precincts it passes over: none when the precincts are level, as a volume leaves them.
        private static IEnumerable<JpxPacketAddress> Lrcp(JpxTileLayout layout, Volume v, int[][][] next, int[][] lowest, WorkBudget budget)
        {
            for (int l = 0; l < v.LayerEnd; l++)
                for (int r = v.ResolutionStart; r < v.ResolutionEnd; r++)
                    for (int c = v.ComponentStart; c < v.ComponentEnd; c++)
                    {
                        budget.Tick();
                        if (r > layout.Components[c].Levels || lowest[c][r] > l) continue;
                        int[] layers = next[c][r];
                        int least = int.MaxValue;
                        long passed = 0;
                        for (int k = 0; k < layers.Length; k++)
                        {
                            if (layers[k] == l)
                            {
                                layers[k] = l + 1;
                                yield return new JpxPacketAddress(l, r, c, k);
                            }
                            else passed++;
                            if (layers[k] < least) least = layers[k];
                        }
                        lowest[c][r] = least;
                        if (passed > 0) budget.Tick(passed);
                    }
        }

        private static IEnumerable<JpxPacketAddress> Rlcp(JpxTileLayout layout, Volume v, int[][][] next, int[][] lowest, WorkBudget budget)
        {
            for (int r = v.ResolutionStart; r < v.ResolutionEnd; r++)
                for (int l = 0; l < v.LayerEnd; l++)
                    for (int c = v.ComponentStart; c < v.ComponentEnd; c++)
                    {
                        budget.Tick();
                        if (r > layout.Components[c].Levels || lowest[c][r] > l) continue;
                        int[] layers = next[c][r];
                        int least = int.MaxValue;
                        long passed = 0;
                        for (int k = 0; k < layers.Length; k++)
                        {
                            if (layers[k] == l)
                            {
                                layers[k] = l + 1;
                                yield return new JpxPacketAddress(l, r, c, k);
                            }
                            else passed++;
                            if (layers[k] < least) least = layers[k];
                        }
                        lowest[c][r] = least;
                        if (passed > 0) budget.Tick(passed);
                    }
        }

        /// <summary>RPCL, PCRL and CPRL (§B.12.1.3 to §B.12.1.5): the same position test, three loop nestings.</summary>
        private static IEnumerable<JpxPacketAddress> PositionDriven(JpxTileLayout layout, Volume v, int[][][] next, WorkBudget budget)
        {
            JpxRect tile = layout.Bounds;
            JpxComponentSize[] sizes = SizesOf(layout);

            // Every position the loops need is the tile's origin or a multiple of some precinct's
            // size on the reference grid (the NOTE under §B.12.1.3): the gcd of those sizes steps
            // through them all.
            long stepX = 0, stepY = 0;
            for (int c = v.ComponentStart; c < v.ComponentEnd; c++)
            {
                JpxTileComponent tc = layout.Components[c];
                for (int r = v.ResolutionStart; r < v.ResolutionEnd && r <= tc.Levels; r++)
                {
                    JpxResolution res = tc.Resolutions[r];
                    stepX = Gcd(stepX, (long)sizes[c].XRsiz << (res.PrecinctWidthExponent + tc.Levels - r));
                    stepY = Gcd(stepY, (long)sizes[c].YRsiz << (res.PrecinctHeightExponent + tc.Levels - r));
                }
            }
            if (stepX == 0 || stepY == 0) yield break;   // no resolution level in the volume

            // The tick is per component at each position, not per position: the gcd step collapses
            // to one sample legally (PPx = 0 at r = 0), and a file can declare thousands of
            // components, so the work at a position is the component loop's.
            if (v.Order == JpxProgressionOrder.Cprl)
            {
                for (int c = v.ComponentStart; c < v.ComponentEnd; c++)
                    for (long y = tile.Y0; y < tile.Y1; y = Next(y, stepY))
                        for (long x = tile.X0; x < tile.X1; x = Next(x, stepX))
                        {
                            budget.Tick();
                            foreach (JpxPacketAddress p in AtPosition(layout, v, next, sizes, c, x, y, -1)) yield return p;
                        }
            }
            else if (v.Order == JpxProgressionOrder.Pcrl)
            {
                for (long y = tile.Y0; y < tile.Y1; y = Next(y, stepY))
                    for (long x = tile.X0; x < tile.X1; x = Next(x, stepX))
                        for (int c = v.ComponentStart; c < v.ComponentEnd; c++)
                        {
                            budget.Tick();
                            foreach (JpxPacketAddress p in AtPosition(layout, v, next, sizes, c, x, y, -1)) yield return p;
                        }
            }
            else   // Rpcl
            {
                for (int r = v.ResolutionStart; r < v.ResolutionEnd; r++)
                    for (long y = tile.Y0; y < tile.Y1; y = Next(y, stepY))
                        for (long x = tile.X0; x < tile.X1; x = Next(x, stepX))
                            for (int c = v.ComponentStart; c < v.ComponentEnd; c++)
                            {
                                budget.Tick();
                                foreach (JpxPacketAddress p in AtPosition(layout, v, next, sizes, c, x, y, r)) yield return p;
                            }
            }
        }

        /// <summary>
        /// The packets of component <paramref name="c"/> at grid position (x, y): for resolution
        /// <paramref name="onlyResolution"/>, or every resolution of the volume when it is -1.
        /// </summary>
        private static IEnumerable<JpxPacketAddress> AtPosition(JpxTileLayout layout, Volume v, int[][][] next,
            JpxComponentSize[] sizes, int c, long x, long y, int onlyResolution)
        {
            JpxTileComponent tc = layout.Components[c];
            JpxRect tile = layout.Bounds;
            int rFrom = onlyResolution >= 0 ? onlyResolution : v.ResolutionStart;
            int rTo = onlyResolution >= 0 ? onlyResolution + 1 : v.ResolutionEnd;
            for (int r = rFrom; r < rTo; r++)
            {
                if (r > tc.Levels) continue;
                JpxResolution res = tc.Resolutions[r];
                if (res.PrecinctCount == 0) continue;
                int shift = tc.Levels - r;
                int ppx = res.PrecinctWidthExponent, ppy = res.PrecinctHeightExponent;
                long xr = sizes[c].XRsiz, yr = sizes[c].YRsiz;

                // "y divisible by YRsiz · 2^(PPy + NL − r), or y = ty0 and try0 · 2^(NL − r) not
                // divisible by 2^(PPy + NL − r)" — the second clause is try0 off the precinct lattice.
                long ystep = yr << (ppy + shift), xstep = xr << (ppx + shift);
                bool yOk = y % ystep == 0 || (y == tile.Y0 && res.Bounds.Y0 % (1L << ppy) != 0);
                bool xOk = x % xstep == 0 || (x == tile.X0 && res.Bounds.X0 % (1L << ppx) != 0);
                if (!yOk || !xOk) continue;

                // Equation B-20.
                long px = JpxGeometry.FloorDiv(JpxGeometry.CeilDiv(x, xr << shift), 1L << ppx) - JpxGeometry.FloorDiv(res.Bounds.X0, 1L << ppx);
                long py = JpxGeometry.FloorDiv(JpxGeometry.CeilDiv(y, yr << shift), 1L << ppy) - JpxGeometry.FloorDiv(res.Bounds.Y0, 1L << ppy);
                // A position that passed the divisibility test always lands inside the precinct grid
                // (⌈x / (XRsiz · 2^shift)⌉ ≤ trx1, and equality only at a multiple of 2^PPx past the
                // last precinct, which x < tx1 excludes): this bounds the index, it is not a decision.
                if (px < 0 || px >= res.PrecinctsWide || py < 0 || py >= res.PrecinctsHigh) continue;
                int k = (int)(py * res.PrecinctsWide + px);

                int[] layers = next[c][r];
                for (int l = 0; l < v.LayerEnd; l++)
                    if (layers[k] == l)
                    {
                        layers[k] = l + 1;
                        yield return new JpxPacketAddress(l, r, c, k);
                    }
            }
        }

        private static JpxComponentSize[] SizesOf(JpxTileLayout layout)
        {
            var sizes = new JpxComponentSize[layout.Components.Length];
            for (int c = 0; c < sizes.Length; c++) sizes[c] = layout.Components[c].Size;
            return sizes;
        }

        private static long Next(long value, long step) => (value / step + 1) * step;

        private static long Gcd(long a, long b)
        {
            while (b != 0)
            {
                long t = a % b;
                a = b;
                b = t;
            }
            return a;
        }
    }
}
