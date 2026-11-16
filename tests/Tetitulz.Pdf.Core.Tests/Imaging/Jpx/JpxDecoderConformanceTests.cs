// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxDecoder"/> against the ISO/IEC 15444-4 class-1 reference decodes, with the
/// tolerances of ITU-T T.803 (02/2024) Tables C.6 and C.7 (<c>docs/iso</c>): a peak absolute
/// error and a mean squared error per component, 0 where the decode must be exact.
/// </summary>
public sealed class JpxDecoderConformanceTests
{
    private static string Input(string name) => Path.Combine(TestCorpus.JpxRoot!, "input", "conformance", name);
    private static string Reference(string name) => Path.Combine(TestCorpus.JpxRoot!, "baseline", "conformance", name);

    /// <summary>
    /// T.803 (02/2024) Tables C.6 and C.7, class 1 profiles 0 and 1: per component, the peak error
    /// and the MSE the decode may show against the reference; and the resolution reduction the
    /// reference was decoded at. The HTJ2K codestreams derived from these use the same rows, plus
    /// their own additional error (<see cref="JpxIsoHtConformanceTests"/>).
    /// </summary>
    internal sealed record Tolerance(string Stem, int Reduction, int[] Peak, double[] Mse);

    internal static readonly Tolerance[] Tolerances =
    {
        new("p0_01", 0, new[] { 0 }, new[] { 0.0 }),
        new("p0_02", 0, new[] { 0 }, new[] { 0.0 }),
        new("p0_03", 0, new[] { 0 }, new[] { 0.0 }),
        new("p0_04", 0, new[] { 5, 4, 6 }, new[] { 0.776, 0.626, 1.070 }),
        new("p0_05", 0, new[] { 2, 2, 2, 0 }, new[] { 0.319, 0.323, 0.317, 0 }),
        new("p0_06", 0, new[] { 635, 403, 378, 0 }, new[] { 11287.0, 6124, 3968, 0 }),
        new("p0_07", 0, new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("p0_08", 1, new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("p0_09", 0, new[] { 0 }, new[] { 0.0 }),
        new("p0_10", 0, new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("p0_11", 0, new[] { 0 }, new[] { 0.0 }),
        new("p0_12", 0, new[] { 0 }, new[] { 0.0 }),
        new("p0_13", 0, new[] { 0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 }),
        new("p0_14", 0, new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("p0_15", 0, new[] { 0 }, new[] { 0.0 }),
        new("p0_16", 0, new[] { 0 }, new[] { 0.0 }),
        new("p1_01", 0, new[] { 0 }, new[] { 0.0 }),
        new("p1_02", 0, new[] { 5, 4, 6 }, new[] { 0.765, 0.616, 1.051 }),
        new("p1_03", 0, new[] { 2, 2, 1, 0 }, new[] { 0.311, 0.280, 0.267, 0 }),
        new("p1_04", 0, new[] { 624 }, new[] { 3080.0 }),
        new("p1_05", 0, new[] { 40, 40, 40 }, new[] { 8.458, 9.716, 10.154 }),
        new("p1_06", 0, new[] { 2, 2, 2 }, new[] { 0.6, 0.6, 0.6 }),
        new("p1_07", 0, new[] { 0, 0 }, new[] { 0.0, 0 }),
    };

    /// <summary>The class-1 reference files of a codestream, one per referenced component, by component index.</summary>
    internal static IEnumerable<(int Component, string Path)> ClassOneReferences(string stem)
    {
        foreach (string path in Directory.GetFiles(Path.Combine(TestCorpus.JpxRoot!, "baseline", "conformance"), $"c1{stem}_*.pgx"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            yield return (int.Parse(name.Substring(name.LastIndexOf('_') + 1), System.Globalization.CultureInfo.InvariantCulture), path);
        }
    }

    /// <summary>Compares one component's plane with its class-1 reference file, returning a failure line or null.</summary>
    internal static string Compare(string stem, int component, string path, JpxDecodeResult result, Tolerance tolerance)
    {
        byte[] data = File.ReadAllBytes(path);
        if (!PgxHeader.TryRead(data, out PgxHeader? h)) return $"{stem} component {component}: unreadable reference";
        int[] reference = h.ReadSamples(data);
        int[] decoded = result.ComponentPlane(component, out JpxRect extent);
        if (reference.Length != decoded.Length || h.Width != extent.Width)
            return $"{stem} component {component}: decoded {extent.Width}x{extent.Height}, reference {h.Width}x{h.Height}";
        long peak = 0;
        double squares = 0;
        for (int i = 0; i < reference.Length; i++)
        {
            long d = Math.Abs((long)decoded[i] - reference[i]);
            if (d > peak) peak = d;
            squares += (double)d * d;
        }
        double mse = squares / reference.Length;
        int peakLimit = tolerance.Peak[Math.Min(component, tolerance.Peak.Length - 1)];
        double mseLimit = tolerance.Mse[Math.Min(component, tolerance.Mse.Length - 1)];
        if (peak > peakLimit || mse > mseLimit + 1e-9)
            return $"{stem} component {component}: peak error {peak} (limit {peakLimit}), MSE {mse:F3} (limit {mseLimit})";
        return null;
    }

    public static IEnumerable<object[]> ProfileCodestreams()
    {
        foreach (Tolerance t in Tolerances) yield return new object[] { t.Stem };
    }

    /// <summary>
    /// Every profile-0 and profile-1 codestream decodes to its class-1 reference within Tables C.6 and C.7:
    /// exactly for the 5-3 ones — p0_01, p0_03 and p0_15 (POC, ROI, SOP), p0_07 (257 tile-parts),
    /// p0_08 (seven levels, CPRL, 30 layers; the reference one level down), p0_10 and p0_14 (RCT),
    /// p0_11, p0_12, p0_13 (257 components, RCT, ROI), p0_16, p1_07 (sub-sampled) — and within the
    /// peak and MSE limits for the 9-7 ones — p0_02 (bypass, termination, segmentation symbols),
    /// p0_04 (ICT), p0_05, p0_06 (12-bit), p0_09, p1_01 to p1_06.
    /// </summary>
    [JpxCorpusTheory]
    [MemberData(nameof(ProfileCodestreams))]
    public void ProfileCodestreams_DecodeWithinTheClassOneLimits(string stem)
    {
        Tolerance tolerance = Tolerances.Single(t => t.Stem == stem);
        Assert.True(JpxDecoder.TryDecode(File.ReadAllBytes(Input(stem + ".j2k")), tolerance.Reduction, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        Assert.Equal(result.Codestream.Tiles.Count, result.Tiles.Count);
        var failures = new List<string>();
        int compared = 0;
        foreach ((int component, string path) in ClassOneReferences(stem))
        {
            compared++;
            string failure = Compare(stem, component, path, result, tolerance);
            if (failure != null) failures.Add(failure);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(compared >= 1, "no reference compared");
    }

    public static IEnumerable<object[]> PegasusCodestreams()
    {
        foreach (string stem in new[] { "a1_mono", "a2_colr", "a3_mono", "a4_colr", "a5_mono", "b1_mono", "b2_mono", "b3_mono",
                     "c1_mono", "c2_mono", "d1_colr", "d2_colr", "e1_colr", "e2_colr", "f1_mono", "f2_mono", "g1_colr", "g2_colr", "g3_colr", "g4_colr" })
            yield return new object[] { stem };
    }

    /// <summary>
    /// The second conformance set — 5-3 reversible, no quantisation, every layer present — decodes
    /// exactly to its PPM references on the reference grid: many tile-parts (b1, b2), ten layers
    /// with selective bypass (c1) and with every style flag at once (c2), SOP and EPH (a5, f2, g2–g4),
    /// precincts (b3, d1, d2, e1, e2, g1–g4), PPM and PPT packed headers (g1–g4), POC (e1), the RCT
    /// (the colour ones), sub-sampling 17x7 (a4) and 5x3 (b2).
    /// </summary>
    [JpxCorpusTheory]
    [MemberData(nameof(PegasusCodestreams))]
    public void PegasusCodestreams_DecodeExactly(string stem)
    {
        Assert.True(JpxDecoder.TryDecode(File.ReadAllBytes(Input(stem + ".j2c")), 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        byte[] data = File.ReadAllBytes(Reference(stem + ".ppm"));
        Assert.True(PnmHeader.TryRead(data, out PnmHeader? h));
        Assert.Equal(result.Codestream.Size.Components.Length, h.Channels);
        var failures = new List<string>();
        for (int c = 0; c < h.Channels; c++)
        {
            string failure = CompareOnTheGrid(stem, c, h.ReadSamples(data, c), h.Width, h.Height, result);
            if (failure != null) failures.Add(failure);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>a6_mono_colr: four components, sub-sampled 11x5 and 13x6, one PGM each on the grid.</summary>
    [JpxCorpusFact]
    public void A6MonoColr_DecodesEachComponentExactly()
    {
        Assert.True(JpxDecoder.TryDecode(File.ReadAllBytes(Input("a6_mono_colr.j2c")), 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        Assert.Equal(4, result.Codestream.Size.Components.Length);
        var failures = new List<string>();
        for (int c = 0; c < 4; c++)
        {
            byte[] data = File.ReadAllBytes(Reference($"a6_mono_colr_{c}.pgm"));
            Assert.True(PnmHeader.TryRead(data, out PnmHeader? h));
            string failure = CompareOnTheGrid("a6_mono_colr", c, h.ReadSamples(data, 0), h.Width, h.Height, result);
            if (failure != null) failures.Add(failure);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    public static IEnumerable<object[]> Jp2Files()
    {
        for (int i = 1; i <= 9; i++) yield return new object[] { $"file{i}.jp2" };
        yield return new object[] { "subsampling_1.jp2" };
        yield return new object[] { "subsampling_2.jp2" };
        yield return new object[] { "zoo1.jp2" };
        yield return new object[] { "zoo2.jp2" };
    }

    /// <summary>The JP2 files decode whole, with nothing tolerated, into an image of the grid's size.</summary>
    [JpxCorpusTheory]
    [MemberData(nameof(Jp2Files))]
    public void Jp2Files_DecodeWhole(string name)
    {
        Assert.True(JpxDecoder.TryDecode(File.ReadAllBytes(Input(name)), 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        Assert.NotNull(result.Image);
        JpxImageSize siz = result.Codestream.Size;
        Assert.Equal(((int)(siz.Xsiz - siz.XOsiz), (int)(siz.Ysiz - siz.YOsiz)), (result.Image.Width, result.Image.Height));
    }

    /// <summary>
    /// The the reference decoder-written JP2 files whose components share one sub-sampling have PPM references
    /// of the raw components at their own resolution, matched channel by channel — 9-7 lossy, so
    /// within a small peak error. (zoo1 and subsampling_1 are sYCC with 4:2:0 chroma, and their
    /// PPMs are RGB after the reference decoder's chroma up-sampling and colour conversion: the consumer stage's
    /// oracle, not this one's.)
    /// </summary>
    [JpxCorpusTheory]
    [InlineData("subsampling_2")]
    [InlineData("zoo2")]
    public void ConformanceJp2Files_MatchTheirPpmsWithinTwo(string stem)
    {
        Assert.True(JpxDecoder.TryDecode(File.ReadAllBytes(Input(stem + ".jp2")), 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Empty(result.Deviations);
        JpxImageSize siz = result.Codestream.Size;
        foreach (JpxComponentSize c in siz.Components)
            Assert.Equal((siz.Components[0].XRsiz, siz.Components[0].YRsiz), (c.XRsiz, c.YRsiz));
        byte[] data = File.ReadAllBytes(Reference(stem + ".ppm"));
        Assert.True(PnmHeader.TryRead(data, out PnmHeader? h));
        Assert.Equal(siz.Components.Length, h.Channels);
        var failures = new List<string>();
        for (int c = 0; c < h.Channels; c++)
        {
            int[] reference = h.ReadSamples(data, c);
            int[] decoded = result.ComponentPlane(c, out JpxRect extent);
            if (reference.Length != decoded.Length) { failures.Add($"component {c}: {extent.Width}x{extent.Height} decoded, {h.Width}x{h.Height} in the reference"); continue; }
            long peak = 0;
            for (int i = 0; i < reference.Length; i++) peak = Math.Max(peak, Math.Abs((long)decoded[i] - reference[i]));
            if (peak > 2) failures.Add($"component {c}: peak error {peak}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The largest inputs decoded on every processor and on one thread give the same planes, the
    /// same deviations and the same budget: p0_07's 256 tiles go across the workers, zoo2's one
    /// tile has its code-blocks and wavelet lines across them. The automatic degree is recorded on
    /// the result, so a decoder that ran on one thread both times would not pass here.
    /// </summary>
    [JpxCorpusTheory]
    [InlineData("p0_07.j2k")]
    [InlineData("zoo2.jp2")]
    public void TheLargestInputs_DecodeIdenticallyOnEveryThreadCount(string name)
    {
        byte[] data = File.ReadAllBytes(Input(name));
        Assert.True(JpxDecoder.TryDecode(data, 0, out JpxDecodeResult alone, out string reason, maxThreads: 1), reason);
        Assert.True(JpxDecoder.TryDecode(data, 0, out JpxDecodeResult shared, out reason, maxThreads: 0), reason);
        Assert.Equal(1, alone.Threads);
        JpxImageSize size = shared.Codestream.Size;
        Assert.Equal(JpxParallel.Resolve(0, (size.Xsiz - size.XOsiz) * (size.Ysiz - size.YOsiz) * size.Components.Length), shared.Threads);
        Assert.True(shared.Threads >= 2 || Environment.ProcessorCount < 2, "no second processor to share with");
        for (int c = 0; c < alone.Codestream.Size.Components.Length; c++)
            Assert.Equal(alone.ComponentPlane(c, out _), shared.ComponentPlane(c, out _));
        Assert.Equal(alone.Deviations, shared.Deviations);
        Assert.Equal(alone.Budget.Spent, shared.Budget.Spent);
        Assert.Equal(alone.Tiles.Count, shared.Tiles.Count);
    }

    /// <summary>
    /// Compares a component with a reference written on the reference grid (the ISO set's PPM/PGM
    /// files were up-sampled to it by a commercial encoder): grid position (x, y) holds the component's sample at
    /// (⌊x / XRsiz⌋, ⌊y / YRsiz⌋) — replication — except before the first sample, where the image
    /// offset lies off the sub-sampling lattice (b2_mono: 1 with XRsiz 5, 160 with YRsiz 3) and the
    /// reference holds 0; those positions are not compared. Returns a failure line, or null.
    /// </summary>
    internal static string CompareOnTheGrid(string stem, int component, int[] reference, int width, int height, JpxDecodeResult result)
    {
        JpxImageSize siz = result.Codestream.Size;
        if (width != siz.Xsiz - siz.XOsiz || height != siz.Ysiz - siz.YOsiz)
            return $"{stem} component {component}: the reference is {width}x{height}, the image area {siz.Xsiz - siz.XOsiz}x{siz.Ysiz - siz.YOsiz}";
        int[] decoded = result.ComponentPlane(component, out JpxRect extent);
        int xr = siz.Components[component].XRsiz, yr = siz.Components[component].YRsiz;
        int mismatches = 0, compared = 0;
        long peak = 0;
        for (long gy = Math.Max(siz.YOsiz, extent.Y0 * yr); gy < siz.Ysiz; gy++)
        {
            long cy = Math.Min(gy / yr, extent.Y1 - 1) - extent.Y0;
            for (long gx = Math.Max(siz.XOsiz, extent.X0 * xr); gx < siz.Xsiz; gx++)
            {
                long cx = Math.Min(gx / xr, extent.X1 - 1) - extent.X0;
                long d = Math.Abs((long)decoded[cy * extent.Width + cx] - reference[(gy - siz.YOsiz) * width + (gx - siz.XOsiz)]);
                compared++;
                if (d > 0) mismatches++;
                if (d > peak) peak = d;
            }
        }
        if (compared < reference.Length * 9 / 10) return $"{stem} component {component}: only {compared} of {reference.Length} grid samples compared";
        return mismatches == 0 ? null : $"{stem} component {component}: {mismatches} of {compared} grid samples differ, peak error {peak}";
    }
}
