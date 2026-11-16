// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxCodestreamParser"/> and <see cref="JpxGeometry"/> against the reference decoder's test data
/// (<see cref="TestCorpus.JpxRoot"/>). The conformance set gives the parser an independent oracle:
/// beside each ISO/IEC 15444-4 codestream lies its reference decode, whose header states the size,
/// depth and sign of every component — values a misread SIZ or a wrong equation B-1 would miss.
/// </summary>
/// <remarks>
/// Each test has a floor on how many files it read, as <see cref="JpxHeaderCorpusTests"/> explains.
/// </remarks>
public sealed class JpxCodestreamParserCorpusTests
{
    private static string Reference(string fileName) =>
        Path.Combine(TestCorpus.JpxRoot!, "baseline", "conformance", fileName);

    /// <summary>
    /// Class-1 references decoded at a reduced resolution: p0_08's is one level down (257x1536 for a
    /// 513x3072 component — ⌈513/2⌉, 3072/2). The reference size is then equation B-14 with
    /// N<sub>L</sub> − r equal to the reduction, which the test computes rather than special-cases.
    /// </summary>
    private static readonly Dictionary<string, int> ReferenceReduction = new() { ["p0_08"] = 1 };

    /// <summary>
    /// Every conformance codestream parses — the set is well-formed by definition, so a refusal is the
    /// parser's defect — and each component the reference decodes name (<c>c1pN_MM_C.pgx</c> for the
    /// Part 1 profile tests; <c>.ppm</c>/<c>.pgm</c> for the others) has the extent, precision and sign
    /// the reference carries.
    /// </summary>
    [JpxCorpusFact]
    public void EveryConformanceCodestream_Parses_AndMatchesItsReferenceDecode()
    {
        IReadOnlyList<string> files = TestCorpus.JpxFiles("input/conformance");
        Assert.True(files.Count >= 50, $"found only {files.Count} conformance files under {TestCorpus.JpxRoot}");

        var failures = new List<string>();
        int references = 0;
        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            string name = Path.GetFileName(path);
            string stem = Path.GetFileNameWithoutExtension(path);

            if (!JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason))
            {
                failures.Add($"{name}: {reason}");
                continue;
            }
            if (cs.Tiles.Count == 0 || cs.Tiles.Count > cs.Size.NumTiles)
                failures.Add($"{name}: {cs.Tiles.Count} tile(s) with data of {cs.Size.NumTiles} declared");
            // A conforming codestream gives the parser nothing to tolerate.
            foreach (string deviation in cs.Deviations)
                failures.Add($"{name}: tolerated \"{deviation}\"");

            // The Part 1 profile tests: one class-1 (full) decode per component the reference set covers.
            for (int c = 0; c < cs.Size.Components.Length; c++)
            {
                string pgx = Reference($"c1{stem}_{c}.pgx");
                if (!File.Exists(pgx)) continue;
                references++;
                if (!PgxHeader.TryRead(File.ReadAllBytes(pgx), out PgxHeader? h))
                {
                    failures.Add($"{name}: unreadable reference header {Path.GetFileName(pgx)}");
                    continue;
                }
                JpxRect extent = JpxGeometry.Component(cs.Size, c);
                if (ReferenceReduction.TryGetValue(stem, out int reduction))
                    extent = JpxGeometry.Resolution(extent, reduction, 0);
                JpxComponentSize declared = cs.Size.Components[c];
                if (extent.Width != h.Width || extent.Height != h.Height
                    || declared.Precision != h.Depth || declared.IsSigned != h.IsSigned)
                    failures.Add($"{name} component {c}: parsed {extent.Width}x{extent.Height}, "
                        + $"{(declared.IsSigned ? "-" : "+")}{declared.Precision} bits; reference {h.Width}x{h.Height}, "
                        + $"{(h.IsSigned ? "-" : "+")}{h.Depth} bits");
            }

            // The other codestreams: a PNM of the whole image, or one PGM per component. Two
            // provenances, two sizes. The ISO set's own references (a1_mono … g4_colr, with a
            // copyright comment) were written up-sampled to the reference grid — a4_colr's components
            // are sub-sampled 17x7 and its PPM is 4336x1037 — so they state the image area, equation
            // B-1 at (1, 1). The reference decoder's additions (subsampling_*, zoo*) were written at component 0's
            // own size: zoo2's components are sub-sampled 2x2 and its PPM is 1953x1301.
            bool isoReference = stem.Length > 2 && stem[0] >= 'a' && stem[0] <= 'g' && stem[2] == '_';
            var pnms = new List<string> { Reference(stem + ".ppm"), Reference(stem + ".pgm") };
            for (int c = 0; c < cs.Size.Components.Length; c++) pnms.Add(Reference($"{stem}_{c}.pgm"));
            foreach (string pnm in pnms)
            {
                if (!File.Exists(pnm)) continue;
                references++;
                if (!PnmHeader.TryRead(File.ReadAllBytes(pnm), out PnmHeader? h))
                {
                    failures.Add($"{name}: unreadable reference header {Path.GetFileName(pnm)}");
                    continue;
                }
                JpxRect component0 = JpxGeometry.Component(cs.Size, 0);
                long width = isoReference ? cs.Size.Xsiz - cs.Size.XOsiz : component0.Width;
                long height = isoReference ? cs.Size.Ysiz - cs.Size.YOsiz : component0.Height;
                if (width != h.Width || height != h.Height)
                    failures.Add($"{name}: {(isoReference ? "image area" : "component 0")} {width}x{height}; "
                        + $"reference {Path.GetFileName(pnm)} {h.Width}x{h.Height}");
            }
        }

        Assert.True(failures.Count == 0, "Conformance codestreams the parser got wrong:\n" + string.Join("\n", failures));
        // Potency: the reference set has 52 class-1 PGX files, 24 PPMs and 4 per-component PGMs.
        Assert.True(references >= 80, $"only {references} reference decodes were compared");
    }

    /// <summary>
    /// A non-regression input — hostile, often a fuzzer's crash file — is parsed or refused with a
    /// reason, never thrown on; and what does parse is self-consistent: every tile index within the
    /// declared grid, every tile-part's data range inside the buffer.
    /// </summary>
    [JpxCorpusFact]
    public void EveryNonRegressionInput_ParsesOrRefusesConsistently()
    {
        IReadOnlyList<string> files = TestCorpus.JpxFiles("input/nonregression");
        Assert.True(files.Count >= 140, $"found only {files.Count} non-regression files under {TestCorpus.JpxRoot}");

        var failures = new List<string>();
        var refusals = new List<string>();
        int parsed = 0;
        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            string name = Path.GetFileName(path);

            if (!JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason))
            {
                if (string.IsNullOrWhiteSpace(reason)) failures.Add($"{name}: refused without a reason");
                refusals.Add($"{name}: {reason}");
                continue;
            }
            parsed++;
            if (cs.Tiles.Count == 0)
                failures.Add($"{name}: parsed with no tile-parts");
            foreach (KeyValuePair<int, JpxTile> tile in cs.Tiles)
            {
                if (tile.Key < 0 || tile.Key >= cs.Size.NumTiles || tile.Value.Index != tile.Key)
                    failures.Add($"{name}: tile {tile.Key} outside the {cs.Size.NumTiles}-tile grid");
                for (int i = 0; i < tile.Value.Parts.Count; i++)
                {
                    JpxTilePart part = tile.Value.Parts[i];
                    if (part.Index != i || part.Data.Start < 0 || part.Data.End < part.Data.Start || part.Data.End > data.Length)
                        failures.Add($"{name}: tile {tile.Key} part {i} (TPsot {part.Index}) data {part.Data.Start}..{part.Data.End} in {data.Length} bytes");
                }
            }
        }

        Assert.True(failures.Count == 0, "Inconsistent parser answers:\n" + string.Join("\n", failures));
        // Potency: a parser that refused everything would pass the loop above vacuously. 87 of the 149
        // files at openjpeg-data 39524bd parse — every file the reference decoder's own non-regression suite
        // decodes, the rest being ones it expects to reject (test_suite.ctest.in, the "!" lines).
        // A drop names what was newly refused.
        Assert.True(parsed >= 87,
            $"only {parsed} of {files.Count} non-regression inputs parsed; refused:\n" + string.Join("\n", refusals));
    }
}
