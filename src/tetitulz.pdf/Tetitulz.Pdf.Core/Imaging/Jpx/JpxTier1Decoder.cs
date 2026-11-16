// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// A sub-band's coefficients after tier 1: one value per sample of <see cref="Band"/>'s bounds
    /// in raster order, in <see cref="JpxBlockDecoder"/>'s convention (twice the magnitude with the
    /// half of E-6 at the last decoded plane, signed).
    /// </summary>
    internal sealed class JpxBandCoefficients
    {
        internal JpxBandCoefficients(JpxBand band, int[] values)
        {
            Band = band;
            Values = values;
        }

        /// <summary>A band whose planes can exceed <see cref="JpxBlockDecoder.MaxNarrowBitPlanes"/>: its values in a <c>long</c> array.</summary>
        internal JpxBandCoefficients(JpxBand band, long[] wideValues)
        {
            Band = band;
            WideValues = wideValues;
        }

        internal JpxBand Band { get; }

        /// <summary>The values; null for a wide band, whose values are <see cref="WideValues"/>.</summary>
        internal int[] Values { get; }

        /// <summary>The values of a band past <see cref="JpxBlockDecoder.MaxNarrowBitPlanes"/> planes; null otherwise.</summary>
        internal long[] WideValues { get; }

        /// <summary>How many values the band holds, whichever array has them.</summary>
        internal int Length => Values != null ? Values.Length : WideValues.Length;
    }

    /// <summary>One code-block of a tile-component and where its values go: a row of a <see cref="JpxTier1Plan"/>.</summary>
    internal readonly struct JpxBlockJob
    {
        internal JpxBlockJob(JpxCodeBlock block, int[] band, long[] wideBand, int offset, int stride, int orientation, int magnitudeBitPlanes, int resolution)
        {
            Block = block;
            Band = band;
            WideBand = wideBand;
            Offset = offset;
            Stride = stride;
            Orientation = orientation;
            MagnitudeBitPlanes = magnitudeBitPlanes;
            Resolution = resolution;
        }

        internal JpxCodeBlock Block { get; }

        /// <summary>The band's array; the block's sample (x, y) lands at <c>Offset + y · Stride + x</c>. Null for a wide band, whose array is <see cref="WideBand"/>.</summary>
        internal int[] Band { get; }

        /// <summary>The band's <c>long</c> array, when its M<sub>b</sub> and ROI shift can exceed an <c>int</c> band's planes.</summary>
        internal long[] WideBand { get; }
        internal int Offset { get; }
        internal int Stride { get; }
        internal int Orientation { get; }

        /// <summary>M<sub>b</sub> of the band (E-2).</summary>
        internal int MagnitudeBitPlanes { get; }

        /// <summary>The resolution level, for the problem text.</summary>
        internal int Resolution { get; }
    }

    /// <summary>
    /// Tier 1 laid out for one tile-component: the band arrays, and every code-block with data as a
    /// job into its band. The jobs are independent — each writes its own rectangle of its band — so
    /// a caller may run them on any thread in any order; the problems they report are collected by
    /// job index and written out in job order (<see cref="JpxTier1Decoder.Report"/>), so the
    /// deviations read the same however the jobs ran.
    /// </summary>
    internal sealed class JpxTier1Plan
    {
        internal JpxTier1Plan(int component, byte[] data, JpxCodeBlockStyle style, int roiShift, List<JpxBandCoefficients> bands, JpxBlockJob[] jobs)
        {
            Component = component;
            Data = data;
            Style = style;
            RoiShift = roiShift;
            Bands = bands;
            Jobs = jobs;
        }

        internal int Component { get; }

        /// <summary>The buffer every block's chunks point into.</summary>
        internal byte[] Data { get; }
        internal JpxCodeBlockStyle Style { get; }
        internal int RoiShift { get; }

        /// <summary>Resolution order, the LL band first; every band of the levels asked for, with or without blocks.</summary>
        internal List<JpxBandCoefficients> Bands { get; }

        /// <summary>The code-blocks that carry coding passes, in band and raster order.</summary>
        internal JpxBlockJob[] Jobs { get; }

        /// <summary>The coding passes over every job — what the work budget charges for the plan.</summary>
        internal long Passes
        {
            get
            {
                long passes = 0;
                foreach (JpxBlockJob job in Jobs) passes += job.Block.Passes;
                return passes;
            }
        }
    }

    /// <summary>
    /// Runs tier 1 over a tile-component: every code-block of every band of the resolution levels
    /// asked for, each decoded with its band's M<sub>b</sub> (E-2) and the component's ROI shift and
    /// style, straight into the band's array. One unit of the work budget per coding pass decoded.
    /// </summary>
    internal static class JpxTier1Decoder
    {
        /// <summary>How many code-block problems one tile-component reports by text before counting the rest.</summary>
        private const int ReportedProblems = 8;

        /// <summary>
        /// The bands of resolution levels 0 to N<sub>L</sub> − <paramref name="reduction"/> of component
        /// <paramref name="component"/> in <paramref name="tile"/>, decoded on the calling thread with
        /// one workspace. Problems a code-block reports (a failed segmentation symbol, a pass count
        /// past its planes) go to <paramref name="deviations"/>, the first few by text and the rest as
        /// a count; the block still yields what it decoded.
        /// </summary>
        internal static List<JpxBandCoefficients> Decode(JpxCodestream cs, JpxTileDecode tile, int component, int reduction,
            WorkBudget budget, List<string> deviations)
        {
            JpxTier1Plan plan = Plan(cs, tile, component, reduction);
            var workspace = new JpxBlockDecoder();
            var problems = new string[plan.Jobs.Length];
            for (int i = 0; i < plan.Jobs.Length; i++)
            {
                budget.Tick(plan.Jobs[i].Block.Passes);
                problems[i] = Run(plan, i, workspace);
            }
            Report(plan, problems, deviations);
            return plan.Bands;
        }

        /// <summary>
        /// Lays out tier 1 for one tile-component: a zeroed array per band of the levels asked for,
        /// and a job per code-block with coding passes (one without leaves its zeros).
        /// </summary>
        internal static JpxTier1Plan Plan(JpxCodestream cs, JpxTileDecode tile, int component, int reduction)
        {
            JpxTileComponent tc = tile.Layout.Components[component];
            int tileIndex = tile.Layout.TileIndex;
            JpxQuantization quantization = cs.QuantizationFor(tileIndex, component);
            int roiShift = cs.RoiShiftFor(tileIndex, component);
            JpxCodeBlockStyle style = tc.Coding.CodeBlockStyle;
            int resolutions = tc.Levels + 1 - reduction;
            if (resolutions < 1) resolutions = 1;

            var bands = new List<JpxBandCoefficients>();
            var jobs = new List<JpxBlockJob>();
            for (int r = 0; r < resolutions && r < tc.Resolutions.Length; r++)
            {
                JpxResolution resolution = tc.Resolutions[r];
                foreach (JpxBand band in resolution.Bands)
                {
                    int width = (int)band.Bounds.Width, height = (int)band.Bounds.Height;
                    int mb = JpxQuantizationSteps.MagnitudeBitPlanes(quantization, tc.Levels, r, band.Orientation);
                    // The planes a block may decode are M_b plus the ROI shift, less its zero planes:
                    // an int band holds MaxNarrowBitPlanes of them, which every conformance
                    // codestream fits; deeper samples (T.800 allows 38 bits, M_b to 37) take a long band.
                    bool wide = mb + roiShift > JpxBlockDecoder.MaxNarrowBitPlanes;
                    int[] values = wide ? null : new int[width * height];
                    long[] wideValues = wide ? new long[width * height] : null;
                    foreach (JpxPrecinct precinct in resolution.Precincts)
                    {
                        JpxPrecinctBand pb = precinct.Bands[band.Orientation == 0 ? 0 : band.Orientation - 1];
                        foreach (JpxCodeBlock block in pb.Blocks)
                        {
                            if (block.Passes == 0) continue;
                            int x0 = (int)(block.Bounds.X0 - band.Bounds.X0), y0 = (int)(block.Bounds.Y0 - band.Bounds.Y0);
                            jobs.Add(new JpxBlockJob(block, values, wideValues, y0 * width + x0, width, band.Orientation, mb, r));
                        }
                    }
                    bands.Add(wide ? new JpxBandCoefficients(band, wideValues) : new JpxBandCoefficients(band, values));
                }
            }
            return new JpxTier1Plan(component, tile.Data, style, roiShift, bands, jobs.ToArray());
        }

        /// <summary>Decodes job <paramref name="index"/> of <paramref name="plan"/> with <paramref name="workspace"/>; the problem it reports, in the deviation's words, or null.</summary>
        internal static string Run(JpxTier1Plan plan, int index, JpxBlockDecoder workspace)
        {
            JpxBlockJob job = plan.Jobs[index];
            string problem;
            if (job.Block.IsHighThroughput(plan.Style))
            {
                if (job.WideBand != null)
                    workspace.Ht.Decode(job.Block, plan.Data, plan.Style, job.MagnitudeBitPlanes, plan.RoiShift,
                        job.WideBand, job.Offset, job.Stride, out problem);
                else
                    workspace.Ht.Decode(job.Block, plan.Data, plan.Style, job.MagnitudeBitPlanes, plan.RoiShift,
                        job.Band, job.Offset, job.Stride, out problem);
            }
            // A Part 1 block of a MIXED tile-component is decoded with the style as it stands: the Part 1
            // decoder reads only the flags Table A.4 lets such a block use, never bits 6 and 7.
            else if (job.WideBand != null)
                workspace.Decode(job.Block, plan.Data, job.Orientation, plan.Style, job.MagnitudeBitPlanes, plan.RoiShift,
                    job.WideBand, job.Offset, job.Stride, out problem);
            else
                workspace.Decode(job.Block, plan.Data, job.Orientation, plan.Style, job.MagnitudeBitPlanes, plan.RoiShift,
                    job.Band, job.Offset, job.Stride, out problem);
            return problem == null ? null
                : $"component {plan.Component}, resolution {job.Resolution}, band {job.Orientation}, code-block {job.Block.Bounds}: {problem}";
        }

        /// <summary>The jobs' problems, by job order: the first <see cref="ReportedProblems"/> by text, the rest as a count. The spine caps the texts across the decode (<see cref="Constants.PdfLimits.MaxJpxReportedProblems"/>).</summary>
        internal static void Report(JpxTier1Plan plan, string[] problems, List<string> deviations)
        {
            int count = 0;
            foreach (string problem in problems)
            {
                if (problem == null) continue;
                count++;
                if (count <= ReportedProblems) deviations.Add(problem);
            }
            if (count > ReportedProblems)
                deviations.Add($"component {plan.Component}: {count - ReportedProblems} more code-blocks reported a problem");
        }

        /// <summary>Whether a tile's deviation text is a code-block problem this class wrote (the spine's decode-wide cap counts these and nothing else).</summary>
        internal static bool IsBlockProblem(string deviation) =>
            deviation != null && deviation.Contains(", code-block (") && deviation.Contains("component ");
    }
}
