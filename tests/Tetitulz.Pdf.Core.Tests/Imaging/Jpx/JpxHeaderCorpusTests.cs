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
/// <see cref="JpxHeader"/> against the reference decoder's test data (<see cref="TestCorpus.JpxRoot"/>): the ISO/IEC
/// 15444-4 conformance codestreams, which are well-formed by definition, and the non-regression
/// inputs, many of which are fuzz-found crash files.
/// </summary>
/// <remarks>
/// Each test has a floor on how many files it read. A corpus scan that finds nothing — a moved folder,
/// a wrong extension filter — would otherwise pass with nothing checked, since every assertion here
/// is per file.
/// </remarks>
public sealed class JpxHeaderCorpusTests
{
    /// <summary>
    /// Every conformance codestream declares a readable grid, and its per-component precision list
    /// is as long as its component count. The conformance set is well-formed by definition, so any
    /// refusal is the reader's defect, not the file's.
    /// </summary>
    [JpxCorpusFact]
    public void EveryConformanceCodestream_DeclaresItsGridAndPrecision()
    {
        IReadOnlyList<string> files = TestCorpus.JpxFiles("input/conformance");
        Assert.True(files.Count >= 50, $"found only {files.Count} conformance files under {TestCorpus.JpxRoot}");

        var failures = new List<string>();
        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            string name = Path.GetFileName(path);

            if (!JpxHeader.TryReadImageSize(data, out long w, out long h, out int comps))
            {
                failures.Add($"{name}: no grid");
                continue;
            }
            if (!JpxHeader.TryReadComponentPrecision(data, out int[] precision, out _))
            {
                failures.Add($"{name}: no precision");
                continue;
            }
            if (w <= 0 || h <= 0 || precision.Length != comps)
                failures.Add($"{name}: {w}x{h}, {comps} component(s), {precision.Length} precision entries");
        }

        Assert.True(failures.Count == 0, "Conformance codestreams the header reader refused:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// A non-regression input — hostile, often a fuzzer's crash file — never throws, and whatever
    /// the reader does answer is self-consistent: a positive grid within the component limit, and a
    /// precision list whose length is the declared component count.
    /// </summary>
    [JpxCorpusFact]
    public void EveryNonRegressionInput_EitherDeclinesOrAnswersConsistently()
    {
        // 149 JPEG 2000 files at openjpeg-data 39524bd; the folder's other files are the encoder
        // tests' TIFF, BMP and PPM inputs, and its htj2k/ subfolder is out of scope (Part 15).
        IReadOnlyList<string> files = TestCorpus.JpxFiles("input/nonregression");
        Assert.True(files.Count >= 140, $"found only {files.Count} non-regression files under {TestCorpus.JpxRoot}");

        var failures = new List<string>();
        int answered = 0;
        foreach (string path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            string name = Path.GetFileName(path);

            bool sized = JpxHeader.TryReadImageSize(data, out long w, out long h, out int comps);
            bool precise = JpxHeader.TryReadComponentPrecision(data, out int[] precision, out _);
            JpxHeader.TryReadColourComponents(data, out int colour);

            if (sized)
            {
                answered++;
                if (w <= 0 || h <= 0 || comps < 1 || comps > 16384)
                    failures.Add($"{name}: grid {w}x{h}, {comps} component(s)");
                if (precise && precision.Length != comps)
                    failures.Add($"{name}: {precision.Length} precision entries for {comps} component(s)");
            }
            if (colour < 0 || colour > 4)
                failures.Add($"{name}: colour-channel count {colour}");
        }

        Assert.True(failures.Count == 0, "Inconsistent header answers:\n" + string.Join("\n", failures));
        // Potency: most of this set is real images with a readable header; a reader that refused
        // everything would pass the loop above vacuously.
        Assert.True(answered >= files.Count / 2, $"only {answered} of {files.Count} headers were read at all");
    }
}
