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
/// HT code-blocks against the ISO conformance suite, ITU-T T.803 (02/2024) | ISO/IEC 15444-4:2024
/// (<c>docs/iso</c>), whose HTJ2K test codestreams (<see cref="TestCorpus.IsoHtJpxRoot"/>) were made by
/// reversibly transcoding the Part 1 ones, so each decodes against the Part 1 file's class-1
/// reference images: within the class-1 limits (Tables C.6, C.7) plus the additional error Tables
/// C.1bis and C.4bis allow a file cut to its BMAGB. Every row's reference depth equals its class-1
/// depth, so the 2^(D−Dref) scaling of the tables' NOTE is 1 throughout. Many files carry quality
/// layers as placeholder passes (Table C.2bis); the DS0_HM files are the MIXED set, HT and Part 1
/// code-blocks in one tile-component (ITU-T T.814 §A.4). The class 1HF pair is checked against
/// Table C.8.
/// </summary>
public sealed class JpxIsoHtConformanceTests
{
    /// <summary>One row of Table C.1bis or C.4bis: the file, the Part 1 codestream it was transcoded from, and per component the additional peak error and MSE.</summary>
    internal sealed record Additional(string File, string Stem, int[] Peak, double[] Mse);

    internal static readonly Additional[] Rows =
    {
        // Table C.1bis, derived set 0.
        new("ds0_ht_01_b11.j2k", "p0_01", new[] { 0 }, new[] { 0.0 }),
        new("ds0_ht_02_b11.j2k", "p0_02", new[] { 1 }, new[] { 0.001 }),
        new("ds0_ht_02_b12.j2k", "p0_02", new[] { 0 }, new[] { 0.0 }),
        new("ds0_ht_03_b11.j2k", "p0_03", new[] { 17 }, new[] { 0.15 }),
        new("ds0_ht_03_b14.j2k", "p0_03", new[] { 0 }, new[] { 0.0 }),
        new("ds0_ht_04_b11.j2k", "p0_04", new[] { 2, 2, 2 }, new[] { 0.1, 0.1, 0.1 }),
        new("ds0_ht_04_b12.j2k", "p0_04", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds0_ht_05_b11.j2k", "p0_05", new[] { 0, 1, 1, 2 }, new[] { 0, 0.001, 0.001, 0.001 }),
        new("ds0_ht_05_b12.j2k", "p0_05", new[] { 0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 }),
        new("ds0_ht_06_b11.j2k", "p0_06", new[] { 2500, 0, 0, 200 }, new[] { 75000.0, 0, 0, 2000 }),
        new("ds0_ht_06_b15.j2k", "p0_06", new[] { 0, 0, 0, 12 }, new[] { 0, 0, 0, 10.0 }),
        new("ds0_ht_06_b18.j2k", "p0_06", new[] { 0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 }),
        new("ds0_hm_06_b11.j2k", "p0_06", new[] { 0, 0, 0, 200 }, new[] { 0, 0, 0, 2000.0 }),
        new("ds0_hm_06_b18.j2k", "p0_06", new[] { 0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 }),
        new("ds0_ht_07_b11.j2k", "p0_07", new[] { 40, 40, 50 }, new[] { 25.0, 25.0, 25.0 }),
        new("ds0_ht_07_b15.j2k", "p0_07", new[] { 2, 2, 2 }, new[] { 0.075, 0.05, 0.075 }),
        new("ds0_ht_07_b16.j2k", "p0_07", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds0_ht_08_b11.j2k", "p0_08", new[] { 40, 40, 40 }, new[] { 45.0, 30.0, 45.0 }),
        new("ds0_ht_08_b15.j2k", "p0_08", new[] { 0, 1, 0 }, new[] { 0, 0.001, 0 }),
        new("ds0_ht_08_b16.j2k", "p0_08", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds0_ht_09_b11.j2k", "p0_09", new[] { 0 }, new[] { 0.0 }),
        new("ds0_ht_10_b11.j2k", "p0_10", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds0_ht_11_b10.j2k", "p0_11", new[] { 0 }, new[] { 0.0 }),
        new("ds0_ht_12_b11.j2k", "p0_12", new[] { 0 }, new[] { 0.0 }),
        new("ds0_ht_13_b11.j2k", "p0_13", new[] { 0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 }),
        new("ds0_ht_14_b11.j2k", "p0_14", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds0_ht_15_b11.j2k", "p0_15", new[] { 17 }, new[] { 0.15 }),
        new("ds0_ht_15_b14.j2k", "p0_15", new[] { 0 }, new[] { 0.0 }),
        new("ds0_hm_15_b8.j2k", "p0_15", new[] { 0 }, new[] { 0.0 }),
        new("ds0_ht_16_b11.j2k", "p0_16", new[] { 0 }, new[] { 0.0 }),
        // Table C.4bis, derived set 1.
        new("ds1_ht_01_b11.j2k", "p1_01", new[] { 1 }, new[] { 0.001 }),
        new("ds1_ht_01_b12.j2k", "p1_01", new[] { 0 }, new[] { 0.0 }),
        new("ds1_ht_02_b11.j2k", "p1_02", new[] { 2, 2, 2 }, new[] { 0.1, 0.1, 0.1 }),
        new("ds1_ht_02_b12.j2k", "p1_02", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds1_ht_03_b11.j2k", "p1_03", new[] { 0, 1, 2, 2 }, new[] { 0, 0.03, 0.05, 0.001 }),
        new("ds1_ht_03_b12.j2k", "p1_03", new[] { 0, 0, 0, 0 }, new[] { 0.0, 0, 0, 0 }),
        new("ds1_ht_04_b9.j2k", "p1_04", new[] { 0 }, new[] { 0.0 }),
        new("ds1_ht_05_b11.j2k", "p1_05", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds1_ht_06_b11.j2k", "p1_06", new[] { 0, 0, 0 }, new[] { 0.0, 0, 0 }),
        new("ds1_ht_07_b11.j2k", "p1_07", new[] { 0, 0 }, new[] { 0.0, 0 }),
    };

    /// <summary>A row's limits: the class-1 row of its Part 1 codestream, each component's additional error added.</summary>
    internal static JpxDecoderConformanceTests.Tolerance LimitsFor(Additional row)
    {
        JpxDecoderConformanceTests.Tolerance classOne = JpxDecoderConformanceTests.Tolerances.Single(t => t.Stem == row.Stem);
        int components = classOne.Peak.Length;
        Assert.Equal(components, row.Peak.Length);
        var peak = new int[components];
        var mse = new double[components];
        for (int c = 0; c < components; c++)
        {
            peak[c] = classOne.Peak[c] + row.Peak[c];
            mse[c] = classOne.Mse[c] + row.Mse[c];
        }
        return classOne with { Peak = peak, Mse = mse };
    }

    private static string Root => TestCorpus.IsoHtJpxRoot!;

    /// <summary>The class-1 reference planes of a Part 1 codestream, by component, as T.803 names them (<c>c1p0_06-3.pgx</c>).</summary>
    internal static IEnumerable<(int Component, string Path)> References(string stem)
    {
        foreach (string path in Directory.GetFiles(Path.Combine(Root, "references"), $"c1{stem}-*.pgx"))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            yield return (int.Parse(name.Substring(name.LastIndexOf('-') + 1), System.Globalization.CultureInfo.InvariantCulture), path);
        }
    }

    /// <summary>Decodes a file and compares every reference component with the limits; the failures, one line each.</summary>
    internal static List<string> Check(string file, JpxDecoderConformanceTests.Tolerance limits, IEnumerable<(int Component, string Path)> references)
    {
        var failures = new List<string>();
        if (!JpxDecoder.TryDecode(File.ReadAllBytes(Path.Combine(Root, file)), limits.Reduction, out JpxDecodeResult result, out string reason))
        {
            failures.Add($"{file}: not decoded — {reason}");
            return failures;
        }
        if (result.Deviations.Count > 0) failures.Add($"{file}: deviations — {string.Join("; ", result.Deviations)}");
        if (result.Tiles.Count != result.Codestream.Tiles.Count) failures.Add($"{file}: {result.Tiles.Count} of {result.Codestream.Tiles.Count} tiles decoded");
        int compared = 0;
        foreach ((int component, string path) in references)
        {
            compared++;
            string failure = JpxDecoderConformanceTests.Compare(file, component, path, result, limits);
            if (failure != null) failures.Add(failure);
        }
        if (compared != limits.Peak.Length) failures.Add($"{file}: {compared} reference components compared, {limits.Peak.Length} expected");
        return failures;
    }

    public static IEnumerable<object[]> DerivedSetCodestreams() => Rows.Select(r => new object[] { r.File });

    /// <summary>
    /// Every HTJ2K test codestream of derived sets 0 and 1 decodes within its limits — exactly where
    /// both tables give 0: placeholder passes in multi-layer files (02–08, 10, 15), RGN in HT blocks
    /// (03, 06, 13, 15), heterogeneous tile-components (03, 06, 07, 15), 257 components (13), and the
    /// MIXED files ds0_hm_06_b18 and ds0_hm_15_b8, exact.
    /// </summary>
    [IsoHtJpxCorpusTheory]
    [MemberData(nameof(DerivedSetCodestreams))]
    public void DerivedSetCodestreams_DecodeWithinTheirLimits(string file)
    {
        Additional row = Rows.Single(r => r.File == file);
        List<string> failures = Check(file, LimitsFor(row), References(row.Stem));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Class 1HF, Table C.8: the HT file and its Part 1 twin decode within their high-fidelity limits
    /// against the same three reference components (<c>hifi-0.pgx</c>…, as the ISO HT conformance set names T.803's
    /// <c>hifi_02-0.pgx</c>…).
    /// </summary>
    [IsoHtJpxCorpusTheory]
    [InlineData("hifi_ht1_02.j2k", new[] { 47, 36, 42 }, new[] { 82.0, 65, 86 })]
    [InlineData("hifi_p1_02.j2k", new[] { 43, 33, 38 }, new[] { 80.0, 62, 72 })]
    public void HighFidelityCodestreams_DecodeWithinTableC8(string file, int[] peak, double[] mse)
    {
        var limits = new JpxDecoderConformanceTests.Tolerance("hifi_02", 0, peak, mse);
        var references = Enumerable.Range(0, 3).Select(c => (c, Path.Combine(Root, "references", $"hifi-{c}.pgx")));
        List<string> failures = Check(file, limits, references);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
