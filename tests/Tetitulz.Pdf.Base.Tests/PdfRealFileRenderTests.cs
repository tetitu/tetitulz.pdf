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
/// Renders the first 3 pages of every PDF in tests/pdf.
/// Set the TETITULZ_PDF_DUMP_RENDERS env var to also dump PNGs to
/// %TEMP%\tetitulz_renders\pdf_checks\ for visual inspection.
/// Failures are collected and reported together at the end.
/// </summary>
public sealed class PdfRealFileRenderTests
{
    private readonly ITestOutputHelper _out;

    // Resolved via TestCorpus; null when the (gitignored) corpus is absent, in which case
    // [CorpusFact] skips.
    private static string? PdfFolder => TestCorpus.Root;

    public PdfRealFileRenderTests(ITestOutputHelper output) => _out = output;

    [CorpusFact]
    public void RenderFirst3Pages_AllPdfsInTestFolder_NoErrors()
    {
        // PNG dumps are gated on TETITULZ_PDF_DUMP_RENDERS; otherwise we skip the writes
        // entirely so a full test run does not fill the system drive.
        bool dumpEnabled = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("TETITULZ_PDF_DUMP_RENDERS"));
        string outDir = Path.Combine(Path.GetTempPath(), "tetitulz_renders", "pdf_checks");
        if (dumpEnabled) Directory.CreateDirectory(outDir);

        string[] files = Directory.GetFiles(PdfFolder!, "*.pdf");
        Assert.True(files.Length > 0, $"No PDFs found in {PdfFolder}");

        var failures = new List<string>();
        int totalPages = 0;

        foreach (string pdfPath in files)
        {
            string name = Path.GetFileNameWithoutExtension(pdfPath);
            _out.WriteLine($"Processing: {name}");

            try
            {
                byte[] pdfBytes = File.ReadAllBytes(pdfPath);

                // Try pages 0-2 directly; catch ArgumentOutOfRange when the PDF has fewer pages
                for (int page = 0; page < 3; page++)
                {
                    try
                    {
                        using var ms = new MemoryStream(pdfBytes);
                        byte[] png = PdfOperations.RenderPageToPng(ms, page, dpi: 96);

                        // Validate it's a real PNG
                        Assert.True(png.Length > 8, $"{name} p{page}: PNG too small");
                        Assert.Equal(0x89, png[0]);
                        Assert.Equal((byte)'P', png[1]);
                        Assert.Equal((byte)'N', png[2]);
                        Assert.Equal((byte)'G', png[3]);

                        // Validate it decodes to a bitmap
                        using var bmp = SKBitmap.Decode(png);
                        Assert.NotNull(bmp);
                        Assert.True(bmp.Width > 0 && bmp.Height > 0,
                            $"{name} p{page}: zero-size bitmap");

                        // Check it's not all-black (common sign of crash/blank output)
                        bool hasNonBlack = false;
                        // Sample a grid of 100 points
                        int stepX = Math.Max(1, bmp.Width / 10);
                        int stepY = Math.Max(1, bmp.Height / 10);
                        for (int y = 0; y < bmp.Height && !hasNonBlack; y += stepY)
                            for (int x = 0; x < bmp.Width && !hasNonBlack; x += stepX)
                            {
                                var px = bmp.GetPixel(x, y);
                                if (px.Red > 20 || px.Green > 20 || px.Blue > 20)
                                    hasNonBlack = true;
                            }
                        Assert.True(hasNonBlack, $"{name} p{page}: output appears all-black");

                        if (dumpEnabled)
                        {
                            // Sanitize filename
                            string safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
                            string outPath = Path.Combine(outDir, $"{safeName}_p{page}.png");
                            File.WriteAllBytes(outPath, png);
                            _out.WriteLine($"  page {page}: {bmp.Width}x{bmp.Height} → {outPath}");
                        }
                        else
                        {
                            _out.WriteLine($"  page {page}: {bmp.Width}x{bmp.Height}");
                        }
                        totalPages++;
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // Page index beyond actual page count — not an error
                        _out.WriteLine($"  page {page}: not present (fewer than {page+1} pages)");
                        break;
                    }
                    catch (Exception ex)
                    {
                        string msg = $"{name} page {page}: {ex.GetType().Name}: {ex.Message}";
                        failures.Add(msg);
                        _out.WriteLine($"  FAIL: {msg}");
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = $"{name}: failed to load: {ex.GetType().Name}: {ex.Message}";
                failures.Add(msg);
                _out.WriteLine($"  FAIL: {msg}");
            }
        }

        _out.WriteLine($"\nRendered {totalPages} pages from {files.Length} PDFs.");
        _out.WriteLine($"Output saved to: {outDir}");

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{failures.Count} render failure(s):\n" +
                string.Join("\n", failures));
        }
    }
}
