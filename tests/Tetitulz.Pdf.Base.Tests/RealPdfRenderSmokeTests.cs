// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Smoke-tests that render the first 5 pages of every PDF in the developer-local
/// corpus (see <see cref="TestCorpus"/>).
///
/// These used to be unable to fail: a per-page exception was logged and skipped with
/// <c>continue</c>, and a file that yielded zero pages logged "SKIPPED" instead of
/// asserting — so ~40 results were reported green no matter what the renderer did.
/// Now every page-level error is collected and reported as a failure at the end, and
/// a file the parser cannot get a single page out of fails too.
///
/// When the corpus is absent the tests report as *skipped*, not passed.
/// Set TETITULZ_PDF_DUMP_RENDERS to also dump output to
/// %TEMP%\tetitulz_renders\pdf-renders\ for visual inspection.
/// </summary>
public sealed class RealPdfRenderSmokeTests
{
    private static readonly string OutDir = Path.Combine(
        Path.GetTempPath(), "tetitulz_renders", "pdf-renders");
    private static readonly bool DumpEnabled = !string.IsNullOrEmpty(
        Environment.GetEnvironmentVariable("TETITULZ_PDF_DUMP_RENDERS"));

    private readonly ITestOutputHelper _out;

    public RealPdfRenderSmokeTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> PdfFiles()
    {
        foreach (var f in TestCorpus.Pdfs())
            yield return new object[] { f };
    }

    [CorpusTheory]
    [MemberData(nameof(PdfFiles))]
    public void RenderFirst5Pages_DoesNotThrowAndProducesNonEmptyPng(string pdfPath)
    {
        if (DumpEnabled) Directory.CreateDirectory(OutDir);
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        _out.WriteLine($"PDF: {name}");

        var failures = new List<string>();
        int rendered = 0;

        for (int pg = 0; pg < 5; pg++)
        {
            byte[]? png;
            try
            {
                using var fs = File.OpenRead(pdfPath);
                png = PdfOperations.RenderPageToPng(fs, pg, 72);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Fewer than 5 pages — expected, and not an error.
                _out.WriteLine($"  Page {pg}: no such page");
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"page {pg}: {ex.GetType().Name}: {ex.Message}");
                _out.WriteLine($"  Page {pg}: ERROR {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            Assert.NotNull(png);
            Assert.True(png!.Length > 100, $"Page {pg} PNG suspiciously small ({png.Length} bytes)");

            using var bmp = SKBitmap.Decode(png);
            Assert.NotNull(bmp);
            _out.WriteLine($"  Page {pg}: {bmp.Width}x{bmp.Height} ({png.Length} bytes)");

            if (DumpEnabled)
                File.WriteAllBytes(Path.Combine(OutDir, $"{SanitizeName(name)}_p{pg}.png"), png);

            rendered++;
        }

        _out.WriteLine($"  Rendered {rendered} page(s) successfully.");
        AssertNoFailures(name, rendered, failures, "page");
    }

    [CorpusTheory]
    [MemberData(nameof(PdfFiles))]
    public void RenderFirst5PagesToSvg_DoesNotThrowAndProducesNonEmptySvg(string pdfPath)
    {
        if (DumpEnabled) Directory.CreateDirectory(OutDir);
        var name = Path.GetFileNameWithoutExtension(pdfPath);
        _out.WriteLine($"PDF (SVG): {name}");

        var failures = new List<string>();
        int rendered = 0;

        for (int pg = 0; pg < 5; pg++)
        {
            string? svg;
            try
            {
                using var fs = File.OpenRead(pdfPath);
                svg = PdfOperations.RenderPageToSvg(fs, pg);
            }
            catch (ArgumentOutOfRangeException)
            {
                _out.WriteLine($"  Page {pg}: no such page");
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"page {pg}: {ex.GetType().Name}: {ex.Message}");
                _out.WriteLine($"  Page {pg}: ERROR {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            Assert.NotNull(svg);
            Assert.True(svg!.Length > 50, $"Page {pg} SVG suspiciously small ({svg.Length} chars)");
            Assert.Contains("<svg", svg);
            _out.WriteLine($"  Page {pg}: {svg.Length} chars");

            if (DumpEnabled)
                File.WriteAllText(Path.Combine(OutDir, $"{SanitizeName(name)}_p{pg}.svg"), svg);

            rendered++;
        }

        _out.WriteLine($"  Rendered {rendered} page(s) successfully.");
        AssertNoFailures(name, rendered, failures, "SVG page");
    }

    /// <summary>
    /// Fails the test if any page errored, or if not a single page could be produced.
    /// A corpus file the parser cannot get one page out of is a real regression signal —
    /// it used to be logged as "SKIPPED" and reported green.
    /// </summary>
    private static void AssertNoFailures(string name, int rendered, List<string> failures, string unit)
    {
        if (failures.Count > 0)
        {
            Assert.Fail($"{name}: {failures.Count} {unit} failure(s):\n  " +
                        string.Join("\n  ", failures));
        }

        Assert.True(rendered >= 1,
            $"{name}: no {unit}s rendered — the parser found no usable pages in this file.");
    }

    private static string SanitizeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length > 60 ? s[..60] : s;
    }
}
