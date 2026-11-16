// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="ImageOptimizationOptions.DownsampleAboveDpi"/> and
/// <see cref="ImageOptimizationOptions.MinImageSide"/>: a resample only well above the target,
/// and a swatch left alone — the presets' policy, while a default-constructed options object keeps
/// resampling anything above <see cref="ImageOptimizationOptions.TargetDpi"/>.
/// </summary>
public sealed class ImageThresholdTests
{
    // 400 pixels across 96 points = 300 dpi.
    private const int Pixels = 400;
    private const double DrawnPoints = 96;

    private static OptimizationOptions Resampling(int targetDpi, int downsampleAboveDpi) => new OptimizationOptions
    {
        ImageOptimization = new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Jpeg,
            Quality = 75,
            TargetDpi = targetDpi,
            DownsampleAboveDpi = downsampleAboveDpi,
            SkipAlreadyOptimized = false,
        }
    };

    [Fact]
    public void DownsampleAboveDpi_LeavesAnImageJustAboveTheTargetButBelowTheThresholdAtItsSize()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        // 300 dpi drawn: above the 280 target, below the 420 threshold.
        byte[] optimized = Optimize(pdf, Resampling(280, 420), out _);

        Assert.Equal(Pixels, WidthOfFirstImage(optimized));
    }

    [Fact]
    public void DownsampleAboveDpi_ResamplesAnImageAboveTheThresholdToTheTarget()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        // 300 dpi drawn: above the 225 threshold, so down to the 150 target — half the samples.
        byte[] optimized = Optimize(pdf, Resampling(150, 225), out _);

        Assert.Equal(Pixels / 2, WidthOfFirstImage(optimized));
    }

    /// <summary>
    /// <c>new ImageOptimizationOptions()</c> behaves as before the threshold existed: an image just
    /// above the target is resampled to it (300 dpi to 280 is 400 × 280 / 300 = 373 samples).
    /// </summary>
    [Fact]
    public void DefaultImageOptions_StillResampleAnImageJustAboveTheTarget()
    {
        Assert.Equal(0, new ImageOptimizationOptions().DownsampleAboveDpi);
        Assert.Equal(0, new ImageOptimizationOptions().MinImageSide);

        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(Pixels, Pixels, DrawnPoints, DrawnPoints);

        byte[] optimized = Optimize(pdf, Resampling(280, 0), out _);

        Assert.Equal(373, WidthOfFirstImage(optimized));
    }

    /// <summary>
    /// The Balanced preset leaves a 32 × 32 swatch as it is. The byte floor and the
    /// already-compressed skip are opened up so the minimum side is the only thing deciding; the
    /// control with it switched off shows the same swatch is otherwise re-encoded.
    /// </summary>
    [Fact]
    public void Balanced_LeavesA32By32ImageAsItIs()
    {
        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(32, 32);

        var result = OptimizeUnderBalanced(pdf, minImageSide: null, out byte[] optimized);

        Assert.Equal(0, result.ImagesExamined);
        Assert.Equal(PdfName.DCTDecode.Value, FilterOfFirstImage(optimized));

        // Potency: without the minimum side the same swatch is examined and re-encoded.
        var control = OptimizeUnderBalanced(pdf, minImageSide: 0, out _);
        Assert.Equal(1, control.ImagesExamined);
    }

    [Fact]
    public void Balanced_ReEncodesA128By128Image()
    {
        Assert.Equal(64, OptimizationOptions.Balanced.ImageOptimization.MinImageSide);
        Assert.Equal(300, OptimizationOptions.Balanced.ImageOptimization.DownsampleAboveDpi);
        Assert.Equal(225, OptimizationOptions.Aggressive.ImageOptimization.DownsampleAboveDpi);

        byte[] pdf = MinimalPdfBuilder.CreatePdfWithImage(128, 128);

        var result = OptimizeUnderBalanced(pdf, minImageSide: null, out _);

        Assert.Equal(1, result.ImagesExamined);
    }

    private static OptimizationResult OptimizeUnderBalanced(byte[] pdf, int? minImageSide, out byte[] optimized)
    {
        var options = OptimizationOptions.Balanced;
        options.NeverGrow = false;
        options.ImageOptimization.MinImageSizeBytes = 0;
        options.ImageOptimization.SkipAlreadyOptimized = false;
        if (minImageSide.HasValue) options.ImageOptimization.MinImageSide = minImageSide.Value;
        optimized = Optimize(pdf, options, out var result);
        return result;
    }

    private static byte[] Optimize(byte[] input, OptimizationOptions options, out OptimizationResult result)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        result = PdfOptimizer.Optimize(i, o, options);
        return o.ToArray();
    }

    private static int WidthOfFirstImage(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return (int)((PdfNumber)Deref(document, FirstImage(document)[PdfName.Width])).LongValue;
    }

    private static string FilterOfFirstImage(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return ((PdfName)Deref(document, FirstImage(document)[PdfName.Filter])).Value;
    }

    private static PdfStream FirstImage(PdfDocument document)
    {
        var catalog = (PdfDictionary)Deref(document, document.Trailer[PdfName.Root]);
        var pages = (PdfDictionary)Deref(document, catalog[PdfName.Pages]);
        var page = (PdfDictionary)Deref(document, ((PdfArray)Deref(document, pages[PdfName.Kids]))[0]);
        var resources = (PdfDictionary)Deref(document, page[PdfName.Resources]);
        var xobjects = (PdfDictionary)Deref(document, resources[PdfName.XObject]);
        foreach (var entry in xobjects)
            if (Deref(document, entry.Value) is PdfStream image) return image;
        Assert.Fail("no image XObject");
        return null;
    }

    private static PdfObject Deref(PdfDocument document, PdfObject o)
    {
        int guard = 0;
        while (o is PdfReference r && guard++ < 32) o = document.GetObject(r.ReferenceObjectNumber);
        return o;
    }
}
