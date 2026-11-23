// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>One decoded tile-component: its samples at the decoded resolution, in the component's coordinates.</summary>
    internal sealed class JpxComponentSamples
    {
        internal JpxComponentSamples(int component, JpxRect bounds, int[] samples, int precision, bool isSigned)
        {
            Component = component;
            Bounds = bounds;
            Samples = samples;
            Precision = precision;
            IsSigned = isSigned;
        }

        /// <summary>A component deeper than <see cref="JpxComponentTransforms.MaxNarrowPrecision"/> bits: its samples in a <c>long</c> array.</summary>
        internal JpxComponentSamples(int component, JpxRect bounds, long[] wideSamples, int precision, bool isSigned)
        {
            Component = component;
            Bounds = bounds;
            WideSamples = wideSamples;
            Precision = precision;
            IsSigned = isSigned;
        }

        internal int Component { get; }

        /// <summary>Equation B-12 at the decoded resolution level (B-14 when reduced).</summary>
        internal JpxRect Bounds { get; }

        /// <summary>Raster order over <see cref="Bounds"/>: integer samples after the DC level shift, within the component's range. Null for a wide component, whose samples are <see cref="WideSamples"/>.</summary>
        internal int[] Samples { get; }

        /// <summary>The samples of a component deeper than 31 bits; null otherwise.</summary>
        internal long[] WideSamples { get; }

        internal int Precision { get; }
        internal bool IsSigned { get; }
    }

    /// <summary>One decoded tile: every component's samples.</summary>
    internal sealed class JpxTileSamples
    {
        internal JpxTileSamples(int tileIndex, JpxRect bounds, JpxComponentSamples[] components)
        {
            TileIndex = tileIndex;
            Bounds = bounds;
            Components = components;
        }

        internal int TileIndex { get; }

        /// <summary>On the reference grid (equations B-6 to B-10).</summary>
        internal JpxRect Bounds { get; }

        internal JpxComponentSamples[] Components { get; }
    }

    /// <summary>What a decode produced: the codestream, each tile's samples, the image they assemble into, and everything tolerated on the way.</summary>
    internal sealed class JpxDecodeResult
    {
        internal JpxDecodeResult(JpxCodestream codestream, int reduction, List<JpxTileSamples> tiles, JpxDecodedImage image,
            List<string> deviations, WorkBudget budget, int threads)
        {
            Codestream = codestream;
            Reduction = reduction;
            Tiles = tiles;
            Image = image;
            Deviations = deviations;
            Budget = budget;
            Threads = threads;
        }

        internal JpxCodestream Codestream { get; }

        /// <summary>The workers the decode ran with (1: the calling thread alone) — the evidence that the parallel path was, or was not, taken.</summary>
        internal int Threads { get; }

        /// <summary>The decomposition levels not reconstructed (0 for full resolution).</summary>
        internal int Reduction { get; }

        internal List<JpxTileSamples> Tiles { get; }

        /// <summary>
        /// The tiles placed on the reference grid, sub-sampled components up-sampled — the image a PDF
        /// consumer takes; null when the decode was reduced, which the grid does not describe.
        /// </summary>
        internal JpxDecodedImage Image { get; }

        /// <summary>The parser's, tier 2's and tier 1's tolerated irregularities, each prefixed by its tile.</summary>
        internal List<string> Deviations { get; }

        /// <summary>The work budget the decode ran under; its <see cref="WorkBudget.Spent"/> is the evidence of what it cost.</summary>
        internal WorkBudget Budget { get; }

        /// <summary>
        /// A component's whole plane at the decoded resolution (<see cref="Reduction"/>) — every
        /// tile's part placed by its bounds (B-12, B-14) — over <paramref name="extent"/>, the
        /// component's extent (B-1) at that resolution, without the replication
        /// <see cref="Image"/> applies to a sub-sampled component. Samples no tile decoded are 0.
        /// A wide component (past 31 bits, held in a <c>long</c> plane) is read saturated to the
        /// <c>int</c> range, never through its absent <c>int</c> plane.
        /// </summary>
        internal int[] ComponentPlane(int component, out JpxRect extent)
        {
            JpxRect full = JpxGeometry.Component(Codestream.Size, component);
            extent = JpxGeometry.Resolution(full, Reduction, 0);
            int width = (int)extent.Width, height = (int)extent.Height;
            var plane = new int[System.Math.Max(0, width) * System.Math.Max(0, height)];
            foreach (JpxTileSamples tile in Tiles)
            {
                if (component >= tile.Components.Length) continue;
                JpxComponentSamples c = tile.Components[component];
                int w = (int)c.Bounds.Width, h = (int)c.Bounds.Height;
                for (int y = 0; y < h; y++)
                {
                    long py = c.Bounds.Y0 + y - extent.Y0;
                    if (py < 0 || py >= height) continue;
                    long px = c.Bounds.X0 - extent.X0;
                    int from = px < 0 ? (int)-px : 0;
                    int to = (int)System.Math.Min(w, width - px);
                    if (to <= from) continue;
                    if (c.Samples != null)
                        System.Array.Copy(c.Samples, y * w + from, plane, py * width + px + from, to - from);
                    else
                        for (int x = from; x < to; x++)
                        {
                            long v = c.WideSamples[y * w + x];
                            plane[py * width + px + x] = v > int.MaxValue ? int.MaxValue : v < int.MinValue ? int.MinValue : (int)v;
                        }
                }
            }
            return plane;
        }
    }

    /// <summary>
    /// The JPEG 2000 decoder's spine: the codestream parsed (Annex A), each tile's packets read
    /// (tier 2, Annex B), each code-block's coefficients decoded (tier 1, Annex D), each band
    /// dequantised (Annex E), each tile-component reconstructed by the inverse wavelet (Annex F),
    /// then the component transformation — Annex G's RCT or ICT, or the Part 2 array-based one
    /// (T.801 Annex J) — the DC level shift, the non-linearity (T.801 Annex K), and the placement
    /// on the reference grid. Everything is bounded by one <see cref="WorkBudget"/> of
    /// <see cref="PdfLimits.MaxJpxDecodeWork"/> units.
    /// </summary>
    /// <remarks>
    /// <para><b>Threads.</b> Tier 2 runs on the calling thread, every tile before any is
    /// reconstructed: it is a small share of the work, and it ticks the budget unsynchronised.
    /// Then, with more than one worker (<see cref="JpxParallel"/>), either the tiles go across the
    /// workers — when there are at least as many tiles as workers, each worker running a whole
    /// tile's tier 1, wavelet and transformation on its own — or, for fewer tiles, the tiles go one
    /// after another with the code-blocks of each (across every component) and the rows and
    /// columns of each wavelet level across the workers. The two never nest. Every code-block
    /// writes its own rectangle of its band, every row or column its own, so no two workers touch
    /// the same sample; the tier-1 ticks go to one shared budget (atomic), so the budget's total is
    /// the same whatever the degree, and problems are collected by tile and by code-block and written
    /// out in that order. The result is therefore bit-identical to a single-threaded decode. One
    /// tier-1 workspace serves each worker; the image is assembled on the calling thread afterwards.</para>
    /// </remarks>
    internal static class JpxDecoder
    {
        /// <summary>
        /// The samples one unit of the work budget buys in a reconstruction pass — the size of the
        /// largest code-block (T.800 Table A.18), so a coding pass at tier 1 and a pass over that many
        /// samples in the wavelet, the component transformations or the non-linearity cost the same.
        /// </summary>
        internal const int SamplesPerUnit = 4096;

        /// <summary>
        /// The work a decode may spend whatever its size, before <see cref="PdfLimits.MaxJpxDecodeWorkPerByte"/>
        /// adds a share per byte: a small file decodes a small image, and its headers, tag trees and
        /// placement cost a few units each.
        /// </summary>
        internal const long WorkFloor = 65_536;

        /// <summary>
        /// Units the stages after tier 1 charge per <see cref="SamplesPerUnit"/> samples of the image
        /// (the wavelet's 4, rounding and clipping's 2, the transformation's 3, placement and
        /// narrowing) with slack: what reconstructing the declared area costs whatever the file's
        /// bytes say, bounded already by the sample ceilings, so the per-byte share need not pay
        /// for it — a 91-byte codestream declaring a blank 4096x4096 image is a legal file.
        /// </summary>
        internal const int ReconstructionUnitsPerBlock = 16;

        /// <summary>
        /// The budget a codestream of <paramref name="bytes"/> declaring <paramref name="samples"/>
        /// gets: the floor, the reconstruction of the declared samples, and the per-byte share for
        /// the entropy-decoding stages a file can amplify (a block of a few bytes declaring 88
        /// coding passes, a progression restated in every tile-part) — never above
        /// <see cref="PdfLimits.MaxJpxDecodeWork"/>. A unit is one coding pass over one code-block,
        /// at most 4 096 samples, or a pass over that many samples in any later stage — about 30 µs
        /// of work — so the absolute ceiling alone (10⁸ units) admits most of an hour from a file of
        /// a few hundred kilobytes; the per-byte share is what stops that.
        /// </summary>
        internal static long WorkAllowed(long bytes, long samples)
        {
            long perByte = PdfLimits.MaxJpxDecodeWorkPerByte;
            long share = perByte <= 0 || bytes <= 0 ? 0 : bytes > long.MaxValue / perByte / 2 ? long.MaxValue / 2 : perByte * bytes;
            long reconstruction = samples <= 0 ? 0 : samples / SamplesPerUnit * ReconstructionUnitsPerBlock + ReconstructionUnitsPerBlock;
            long allowed = WorkFloor + share;
            allowed = allowed > long.MaxValue - reconstruction ? long.MaxValue : allowed + reconstruction;
            return System.Math.Min(PdfLimits.MaxJpxDecodeWork, allowed);
        }

        /// <summary>
        /// Decodes every tile of <paramref name="data"/> (a raw codestream or a JP2 file) at full
        /// resolution, or <paramref name="reduction"/> decomposition levels short of it — at most:
        /// the reduction is clamped to the fewest levels any tile-component has, so that every
        /// tile-component lands at one resolution and the image assembles; the result's
        /// <see cref="JpxDecodeResult.Reduction"/> says which — on up to
        /// <paramref name="maxThreads"/> threads — 0 for the processor count (at most
        /// <see cref="JpxParallel.MaxAutomaticDegree"/>), 1 for the calling thread alone. False with
        /// a reason when the codestream cannot be read at all, a ceiling refuses it, the work budget
        /// runs out (<see cref="PdfLimits.MaxJpxDecodeWork"/>; nothing escapes as an exception, from
        /// a worker either), or a tile's component transformation is malformed; a tile that stops
        /// early keeps what it decoded and says so in the result's deviations.
        /// </summary>
        internal static bool TryDecode(byte[] data, int reduction, out JpxDecodeResult result, out string reason, int maxThreads = 0)
        {
            result = null;
            if (reduction < 0) reduction = 0;
            JpxParallel.RequireValid(maxThreads);
            if (!JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out reason)) return false;

            // The image-wide ceilings first, whatever the reduction, before any tile allocates a plane:
            // the planes a tile needs are at most the image's, over the larger of the SIZ's component
            // count and the CBD's (a Part 2 transformation's outputs). The Part 2 headers are read
            // once here and kept on the codestream for every tile's transformation and non-linearity.
            if (!JpxPart2Header.TryMain(cs, out JpxPart2Header mainPart2, out reason)) return false;
            int planeCount = System.Math.Max(cs.Size.Components.Length, mainPart2.ReconstructedDepths?.Length ?? 0);
            if (!JpxDecodedImage.TryValidateArea(cs.Size, planeCount, out reason)) return false;

            // The workers, by the work: the image's samples over every plane at the resolution
            // asked for (each level short divides them by four; the clamp below can only leave
            // more work, which the bound then understates — a smaller image, not a wrong one).
            long area = (cs.Size.Xsiz - cs.Size.XOsiz) * (cs.Size.Ysiz - cs.Size.YOsiz);
            long work = area * planeCount >> (2 * System.Math.Min(reduction, 30));
            var parallel = new JpxParallel(JpxParallel.Resolve(maxThreads, work));

            // One budget for the whole decode; shared — its ticks atomic — once workers split it, so
            // that every stage charges the same total whatever the degree.
            var budget = new WorkBudget(WorkAllowed(data.Length, area * planeCount), "JPEG 2000 decode", parallel.Degree > 1);
            var deviations = new List<string>();
            foreach (string d in cs.Deviations) deviations.Add("codestream: " + d);

            var indices = new List<int>(cs.Tiles.Keys);
            indices.Sort();
            int tileCount = indices.Count;
            var decodes = new JpxTileDecode[tileCount];
            var tileDeviations = new List<string>[tileCount];
            var samples = new JpxComponentSamples[tileCount][];
            var refusals = new string[tileCount];
            try
            {
                // Tier 2, every tile, on the calling thread. Every tile's layout is held until tier 1,
                // so their structures are summed into the working set as they are made.
                long structures = 0;
                for (int i = 0; i < tileCount; i++)
                {
                    int t = indices[i];
                    if (!JpxTier2Decoder.TryDecodeTile(cs, t, budget, out decodes[i], out reason)) return false;
                    structures += decodes[i].Layout.Structures;
                    if (!JpxDecodedImage.TryValidateWorkingSet(cs.Size, planeCount, reduction, out reason, structures)) return false;
                    var list = new List<string>();
                    if (!decodes[i].Complete) list.Add($"tile {t}: {decodes[i].StopReason}");
                    foreach (string d in decodes[i].Deviations) list.Add($"tile {t}: {d}");
                    tileDeviations[i] = list;
                    reduction = ClampToLevels(decodes[i], reduction);
                }

                // The peak memory, now that the reduction is what the tiles allow and before tier 1
                // allocates the first tile-component's values (a quarter of it for one tile).
                if (!JpxDecodedImage.TryValidateWorkingSet(cs.Size, planeCount, reduction, out reason, structures)) return false;

                // Tier 1, the wavelet and the transformation, per tile: the tiles across the workers,
                // or each tile's blocks and wavelet lines across them (see the remarks).
                bool tilesAcross = parallel.Degree > 1 && tileCount >= parallel.Degree;
                JpxParallel outer = tilesAcross ? parallel : JpxParallel.Sequential;
                JpxParallel inner = tilesAcross ? JpxParallel.Sequential : parallel;
                var workspaces = new JpxBlockDecoder[parallel.Degree];
                int reductionForTiles = reduction;
                outer.For(tileCount, (i, worker) =>
                    samples[i] = DecodeTile(cs, decodes[i], reductionForTiles, budget, inner, workspaces, worker, tileDeviations[i], out refusals[i]));
            }
            catch (WorkBudgetExceededException)
            {
                int done = 0;
                foreach (JpxComponentSamples[] s in samples) if (s != null) done++;
                reason = budget.Total >= PdfLimits.MaxJpxDecodeWork
                    ? $"the decode exceeded PdfLimits.MaxJpxDecodeWork ({budget.Total} units) after {done} of {tileCount} tiles; adversarial or amplifying input"
                    : $"the decode exceeded PdfLimits.MaxJpxDecodeWorkPerByte over its {data.Length} bytes ({budget.Total} units) after {done} of {tileCount} tiles; a declaration of work far past the file's size is adversarial or amplifying input";
                return false;
            }
            for (int i = 0; i < tileCount; i++)
            {
                if (refusals[i] == null) continue;
                reason = refusals[i];
                return false;
            }

            // The image, tile by tile in index order, on the calling thread — at the reduction every
            // tile-component was reconstructed at.
            var tiles = new List<JpxTileSamples>(tileCount);
            JpxDecodedImage image = null;
            // Every tile's deviations, in tile order, but the code-block problems among them capped
            // for the decode: a problem per block in every tile of a crafted file is gigabytes of
            // text. PdfLimits.MaxJpxReportedProblems is read here.
            int problemCap = PdfLimits.MaxJpxReportedProblems, problemsReported = 0, problemsDropped = 0;
            bool capabilityNoted = false;
            for (int i = 0; i < tileCount; i++)
            {
                int t = indices[i];
                foreach (string d in tileDeviations[i])
                {
                    if (JpxTier1Decoder.IsBlockProblem(d) && ++problemsReported > problemCap) { problemsDropped++; continue; }
                    // One codestream, one note: every tile resolves the same headers.
                    if (d == JpxMultipleComponentTransform.CapabilityNote)
                    {
                        if (capabilityNoted) continue;
                        capabilityNoted = true;
                    }
                    deviations.Add(d);
                }
                JpxComponentSamples[] components = samples[i];
                tiles.Add(new JpxTileSamples(t, decodes[i].Layout.Bounds, components));
                if (image == null)
                {
                    var sizes = new JpxComponentSize[components.Length];
                    for (int c = 0; c < sizes.Length; c++)
                    {
                        JpxComponentSize s = cs.Size.Components[System.Math.Min(c, cs.Size.Components.Length - 1)];
                        sizes[c] = new JpxComponentSize(components[c].Precision, components[c].IsSigned, s.XRsiz, s.YRsiz);
                    }
                    if (!JpxDecodedImage.TryCreate(cs.Size, sizes, reduction, out image, out reason)) return false;
                }
                // A tile's own MCO (T.801 §A.3.9, a tile-part header overrides) may reconstruct a
                // different number of components than the first tile's — a CBD of another count than
                // Csiz reached through one tile alone — which the image's planes cannot hold; this
                // assembled silently, keeping the first N.
                if (components.Length != image.Components.Length)
                    return Refuse($"tile {t} reconstructs {components.Length} component(s) where the image, described by the first tile, has {image.Components.Length}; a tile's multiple component transformation must reconstruct the same components as every other tile's", out reason);
                for (int c = 0; c < components.Length && c < image.Components.Length; c++)
                {
                    // A tile's own non-linearity may change a component's depth (T.801 K.1.3); the
                    // image's planes were sized and described by the first tile's, and hold one depth.
                    JpxComponentSize plane = image.Components[c];
                    if (components[c].Precision != plane.Precision || components[c].IsSigned != plane.IsSigned)
                        return Refuse($"tile {t} reconstructs component {c} as {components[c].Precision}-bit {(components[c].IsSigned ? "signed" : "unsigned")} samples where the image's plane is {plane.Precision}-bit {(plane.IsSigned ? "signed" : "unsigned")}; a non-linearity that changes a component's depth must do so in every tile (T.801 K.1.3)", out reason);
                    // A single tile at the image's resolution is the plane itself, adopted rather than copied.
                    if (components[c].WideSamples != null) image.Adopt(c, components[c].Bounds, components[c].WideSamples);
                    else image.Adopt(c, components[c].Bounds, components[c].Samples);
                }
            }
            if (problemsDropped > 0) deviations.Add($"{problemsDropped} more code-block problem(s) across the tiles; PdfLimits.MaxJpxReportedProblems is {problemCap}");
            result = new JpxDecodeResult(cs, reduction, tiles, image, deviations, budget, parallel.Degree);
            return true;
        }

        /// <summary>
        /// One tile after tier 2: every component's blocks decoded (across <paramref name="inner"/>'s
        /// workers, each with the workspace at <paramref name="workspaceBase"/> plus its index),
        /// each component reconstructed, then the tile transformed. Tier 1's problems go to
        /// <paramref name="deviations"/> prefixed by the tile, in component and block order. Null
        /// with <paramref name="refusal"/> when the transformation is malformed.
        /// </summary>
        private static JpxComponentSamples[] DecodeTile(JpxCodestream cs, JpxTileDecode tile, int reduction, WorkBudget budget,
            JpxParallel inner, JpxBlockDecoder[] workspaces, int workspaceBase, List<string> deviations, out string refusal)
        {
            refusal = null;
            int t = tile.Layout.TileIndex;
            int count = tile.Layout.Components.Length;

            var plans = new JpxTier1Plan[count];
            int jobs = 0;
            for (int c = 0; c < count; c++)
            {
                plans[c] = JpxTier1Decoder.Plan(cs, tile, c, reduction);
                jobs += plans[c].Jobs.Length;
            }
            var component = new int[jobs];
            var index = new int[jobs];
            var problems = new string[count][];
            for (int c = 0, j = 0; c < count; c++)
            {
                problems[c] = new string[plans[c].Jobs.Length];
                for (int k = 0; k < plans[c].Jobs.Length; k++, j++)
                {
                    component[j] = c;
                    index[j] = k;
                }
            }
            inner.For(jobs, (j, worker) =>
            {
                JpxTier1Plan plan = plans[component[j]];
                budget.Tick(plan.Jobs[index[j]].Block.Passes);
                JpxBlockDecoder workspace = workspaces[workspaceBase + worker] ?? (workspaces[workspaceBase + worker] = new JpxBlockDecoder());
                problems[component[j]][index[j]] = JpxTier1Decoder.Run(plan, index[j], workspace);
            });
            var reported = new List<string>();
            for (int c = 0; c < count; c++) JpxTier1Decoder.Report(plans[c], problems[c], reported);
            foreach (string d in reported) deviations.Add($"tile {t}: {d}");

            var bounds = new JpxRect[count];
            var planes = new double[count][];
            for (int c = 0; c < count; c++)
                planes[c] = ReconstructBands(cs, tile, c, reduction, plans[c].Bands, budget, inner, out bounds[c]);

            return TryTransform(cs, tile, planes, bounds, budget, deviations, out JpxComponentSamples[] components, out refusal) ? components : null;
        }

        /// <summary>
        /// <paramref name="reduction"/>, or the fewest decomposition levels any of the tile's
        /// components has when that is less: a tile-component cannot be reconstructed more levels
        /// short than it was decomposed (T.800 B.5: NL resolution levels below the full one).
        /// </summary>
        private static int ClampToLevels(JpxTileDecode tile, int reduction)
        {
            foreach (JpxTileComponent component in tile.Layout.Components)
                if (component.Levels < reduction) reduction = component.Levels;
            return reduction;
        }

        /// <summary>
        /// Tier 1, dequantisation (E-3, E-6/E-7) and the inverse wavelet for one tile-component on
        /// the calling thread: the reconstructed values, in the coefficient domain, over
        /// <paramref name="bounds"/>.
        /// </summary>
        internal static double[] Reconstruct(JpxCodestream cs, JpxTileDecode tile, int component, int reduction, WorkBudget budget,
            List<string> deviations, out JpxRect bounds)
        {
            List<JpxBandCoefficients> bands = DecodeTier1(cs, tile, component, reduction, budget, deviations);
            return ReconstructBands(cs, tile, component, reduction, bands, budget, out bounds);
        }

        /// <summary>Tier 1 for one tile-component on the calling thread, each code-block problem prefixed by the tile in <paramref name="deviations"/>.</summary>
        internal static List<JpxBandCoefficients> DecodeTier1(JpxCodestream cs, JpxTileDecode tile, int component, int reduction, WorkBudget budget,
            List<string> deviations)
        {
            var tileDeviations = new List<string>();
            List<JpxBandCoefficients> bands = JpxTier1Decoder.Decode(cs, tile, component, reduction, budget, tileDeviations);
            foreach (string d in tileDeviations) deviations.Add($"tile {tile.Layout.TileIndex}: {d}");
            return bands;
        }

        /// <summary>Dequantisation and the inverse wavelet over tier 1's bands of one tile-component, on the calling thread.</summary>
        internal static double[] ReconstructBands(JpxCodestream cs, JpxTileDecode tile, int component, int reduction,
            List<JpxBandCoefficients> bands, WorkBudget budget, out JpxRect bounds) =>
            ReconstructBands(cs, tile, component, reduction, bands, budget, JpxParallel.Sequential, out bounds);

        /// <summary>As above, the wavelet's rows and columns across <paramref name="parallel"/>'s workers.</summary>
        private static double[] ReconstructBands(JpxCodestream cs, JpxTileDecode tile, int component, int reduction,
            List<JpxBandCoefficients> bands, WorkBudget budget, JpxParallel parallel, out JpxRect bounds)
        {
            JpxTileComponent tc = tile.Layout.Components[component];
            JpxComponentSize size = cs.Size.Components[component];
            bool reversible = tc.Coding.Reversible;
            int levels = tc.Levels;
            int effectiveReduction = reduction > levels ? levels : reduction;
            JpxQuantization quantization = cs.QuantizationFor(tile.Layout.TileIndex, component);

            // Each band goes to the wavelet still quantised, with its step: the dequantisation
            // happens as the band is interleaved, and no dequantised copy of a band is made.
            var coefficients = new List<JpxSubbandCoefficients>(bands.Count);
            long samples = 0;
            foreach (JpxBandCoefficients band in bands)
            {
                int resolution = band.Band.Orientation == 0 ? 0 : levels - band.Band.Level + 1;
                double step = JpxDequantizer.StepSize(quantization, reversible, levels, resolution, band.Band.Orientation, size.Precision);
                coefficients.Add(band.WideValues != null
                    ? new JpxSubbandCoefficients(band.Band.Level, band.Band.Orientation, band.Band.Bounds, band.WideValues, reversible, step)
                    : new JpxSubbandCoefficients(band.Band.Level, band.Band.Orientation, band.Band.Bounds, band.Values, reversible, step));
                samples += band.Length;
            }
            // Dequantisation is one pass over the coefficients; the inverse wavelet, rows and columns at
            // every level reconstructed, about three (the levels' areas sum to under 4/3 of the last).
            budget.Tick(1 + 4 * samples / SamplesPerUnit);

            JpxSubbandCoefficients reconstructed = JpxWavelet.Inverse(tc.Bounds, levels, coefficients, effectiveReduction, reversible, parallel);
            bounds = reconstructed.Bounds;
            return reconstructed.Values;
        }

        /// <summary>
        /// Across a tile's reconstructed components: the Part 2 transformation when one is signalled
        /// (T.801 Annex J; its offsets replace the DC level shift), else Annex G's RCT or ICT when the
        /// COD asks for it; then rounding, the level shift and clipping (G.1.2), and each component's
        /// non-linearity (T.801 Annex K).
        /// </summary>
        internal static bool TryTransform(JpxCodestream cs, JpxTileDecode tile, double[][] planes, JpxRect[] bounds, WorkBudget budget,
            List<string> deviations, out JpxComponentSamples[] components, out string reason)
        {
            components = null;
            int t = tile.Layout.TileIndex;
            if (!JpxMultipleComponentTransform.TryResolve(cs, t, deviations, out JpxMultipleComponentTransform part2, out reason)) return false;

            JpxComponentSize[] sizes;
            double[][] samples;
            JpxRect[] outBounds;
            if (part2 != null)
            {
                int length = planes.Length > 0 ? planes[0].Length : 0;
                foreach (int input in part2.Inputs)
                {
                    if (input >= planes.Length) return Refuse($"tile {t}'s component transformation reads component {input}; the tile has {planes.Length}", out reason);
                    if (planes[input].Length != length) return Refuse($"tile {t}'s component transformation reads components of different sizes, which the JPX baseline's array-based transformation cannot", out reason);
                }
                sizes = part2.ReconstructedComponents;
                var outputs = new double[sizes.Length][];
                for (int c = 0; c < outputs.Length; c++) outputs[c] = new double[length];
                part2.Apply(planes, outputs, budget);
                samples = outputs;
                outBounds = new JpxRect[sizes.Length];
                JpxRect shared = planes.Length > 0 ? bounds[part2.Inputs.Length > 0 ? part2.Inputs[0] : 0] : default;
                budget.Tick(1 + 2L * length * sizes.Length / SamplesPerUnit);   // rounding and clipping, once each
                for (int c = 0; c < sizes.Length; c++)
                {
                    JpxComponentTransforms.RoundInPlace(samples[c]);
                    JpxComponentTransforms.Clamp(samples[c], sizes[c].Precision, sizes[c].IsSigned);   // the offsets stood in for the level shift
                    outBounds[c] = shared;
                }
            }
            else
            {
                sizes = cs.Size.Components;
                samples = planes;
                outBounds = bounds;
                bool annexG = tile.Layout.Coding.MultipleComponentTransform;
                if (annexG && !JpxComponentTransforms.CanTransformFirstThree(cs.Size, out string why))
                {
                    // Non-conforming but unambiguous, as the parser's tolerance of the flag on fewer than
                    // three components is: the transformation is skipped and the components kept as coded.
                    deviations.Add($"tile {t}: {why}; the component transformation is skipped");
                    annexG = false;
                }
                if (annexG && (planes[0].Length != planes[1].Length || planes[0].Length != planes[2].Length))
                    return Refuse($"tile {t}'s components 0-2 were reconstructed at different sizes ({planes[0].Length}, {planes[1].Length} and {planes[2].Length} samples): their decomposition levels differ, and a reduction past the fewest leaves them at different resolution levels; the component transformation needs one size (T.800 G.2)", out reason);
                long total = 0;
                foreach (double[] plane in planes) total += plane.Length;
                budget.Tick(1 + 3 * total / SamplesPerUnit);   // the transformation, the rounding, the level shift
                if (annexG && !tile.Layout.Components[0].Coding.Reversible)
                    JpxComponentTransforms.InverseIct(planes[0], planes[1], planes[2]);   // G.3, on the reconstructed values
                for (int c = 0; c < planes.Length; c++) JpxComponentTransforms.RoundInPlace(planes[c]);
                if (annexG && tile.Layout.Components[0].Coding.Reversible)
                    JpxComponentTransforms.InverseRct(planes[0], planes[1], planes[2]);   // G.2, exact on the integers the planes now hold
                for (int c = 0; c < samples.Length; c++)
                    JpxComponentTransforms.LevelShiftAndClamp(samples[c], sizes[c].Precision, sizes[c].IsSigned);
            }

            if (!JpxNonLinearity.TryResolve(cs, t, samples.Length, out JpxNonLinearity[] nonLinearities, out reason)) return false;
            components = new JpxComponentSamples[samples.Length];
            for (int c = 0; c < samples.Length; c++)
            {
                int precision = sizes[c].Precision;
                bool signed = sizes[c].IsSigned;
                JpxNonLinearity nlt = nonLinearities != null && c < nonLinearities.Length ? nonLinearities[c] : null;
                if (nlt != null)
                {
                    // T.801 K.2.3: the sign-magnitude conversion keeps the depth and the sign — its
                    // BDnlt shall equal the input's. A segment that says otherwise is followed
                    // (the depth it declares is what the planes are sized by) and reported.
                    if (nlt.Type == JpxNonLinearity.SignMagnitude && (nlt.OutputPrecision != precision || nlt.OutputSigned != signed))
                        deviations.Add($"tile {t}: component {c}'s sign-magnitude non-linearity declares {nlt.OutputPrecision}-bit {(nlt.OutputSigned ? "signed" : "unsigned")} output for {precision}-bit {(signed ? "signed" : "unsigned")} input; T.801 K.2.3 requires BDnlt to equal the input depth and sign, and the declared depth is used");
                    budget.Tick(1 + samples[c].Length / SamplesPerUnit);
                    nlt.Apply(samples[c], precision, signed);
                    precision = nlt.OutputPrecision;
                    signed = nlt.OutputSigned;
                }
                // Stored once, at the depth the component ends with: an int plane to 31 bits, a long one above.
                if (JpxComponentTransforms.IsWide(precision))
                {
                    var wide = new long[samples[c].Length];
                    JpxComponentTransforms.Store(samples[c], wide);
                    components[c] = new JpxComponentSamples(c, outBounds[c], wide, precision, signed);
                }
                else
                {
                    var narrow = new int[samples[c].Length];
                    JpxComponentTransforms.Store(samples[c], narrow);
                    components[c] = new JpxComponentSamples(c, outBounds[c], narrow, precision, signed);
                }
            }
            return true;
        }

        private static bool Refuse(string why, out string reason)
        {
            reason = why;
            return false;
        }
    }
}
