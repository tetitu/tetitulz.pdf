// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The decimating Flate decode against the process-wide limit it honours: past
/// <see cref="PdfLimits.MaxDecompressedBytes"/> the full path refuses the image, and so the page is
/// the same either way.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class DecimatingFlateDecodeLimitTests
{
    private static readonly PdfRenderOptions On = new PdfRenderOptions { DownsampleImagesToTarget = true };

    /// <summary>
    /// A ceiling below the image's decoded size: the full path's filter refuses it and draws nothing,
    /// and the decimating decode declines the same image rather than draw what the ceiling refused.
    /// The control is the same image under the default ceiling, which is drawn.
    /// </summary>
    [Fact]
    public void PastTheDecodeCeiling_NeitherPathDrawsTheImage()
    {
        const int size = 600;
        byte[] compressed = DecimatingFlateDecodeTests.Zlib(DecimatingFlateDecodeTests.Samples(size, size, 3));
        byte[] single = DecimatingFlateDecodeTests.ImagePdf(compressed, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: false);
        byte[] chain = DecimatingFlateDecodeTests.ImagePdf(compressed, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: true);
        byte[] drawn = DecimatingFlateDecodeTests.Render(single, On);

        try
        {
            PdfLimits.MaxDecompressedBytes = size * size * 3 / 2;
            byte[] refusedSingle = DecimatingFlateDecodeTests.Render(single, On);
            byte[] refusedChain = DecimatingFlateDecodeTests.Render(chain, On);
            Assert.Equal(refusedChain, refusedSingle);
            Assert.NotEqual(drawn, refusedSingle);   // the ceiling did refuse it
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// The pipeline counts every stage's output against <see cref="PdfLimits.MaxCumulativeOutputBytes"/>:
    /// behind a hex stage, the rows with the stage's output past it are refused by the full
    /// path, and the decimating decode declines them too — the page the run-length chain's, whose first
    /// stage makes as many bytes. The control is the image alone under the same ceiling, which is drawn.
    /// </summary>
    [Fact]
    public void PastTheCumulativeCeiling_NeitherPathDrawsTheImage()
    {
        const int size = 600;
        byte[] compressed = DecimatingFlateDecodeTests.Zlib(DecimatingFlateDecodeTests.Samples(size, size, 3));
        string rgb = "/ColorSpace /DeviceRGB /BitsPerComponent 8";
        byte[] single = DecimatingFlateDecodeTests.ImagePdf(compressed, size, size, rgb, null, chain: false);
        byte[] hex = DecimatingFlateDecodeTests.ImagePdfWith(DecimatingFlateDecodeTests.Hex(compressed), size, size, $"{rgb} /Filter [/ASCIIHexDecode /FlateDecode]");
        byte[] chain = DecimatingFlateDecodeTests.ImagePdf(compressed, size, size, rgb, null, chain: true);
        byte[] drawn = DecimatingFlateDecodeTests.Render(single, On);

        try
        {
            PdfLimits.MaxCumulativeOutputBytes = size * size * 3 + compressed.Length / 2;
            Assert.Equal(drawn, DecimatingFlateDecodeTests.Render(single, On));   // the rows alone are within it
            byte[] refusedHex = DecimatingFlateDecodeTests.Render(hex, On);
            Assert.Equal(DecimatingFlateDecodeTests.Render(chain, On), refusedHex);
            Assert.NotEqual(drawn, refusedHex);   // the ceiling did refuse it
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}

/// <summary>
/// The decimating Flate decode and a registered decode plugin: the full path offers a FlateDecode
/// image to a plugin for FlateDecode before anything else, so the decimating decode leaves it to it.
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class DecimatingFlateDecodePluginTests : IDisposable
{
    public DecimatingFlateDecodePluginTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>
    /// Answers a FlateDecode image with a 2×2 solid colour: magenta when it is handed the whole
    /// filter output of a 600² RGB image, green for anything else — what it is handed is the point.
    /// </summary>
    private sealed class MagentaFlatePlugin : IImageDecodePlugin
    {
        public string Name => "Test.MagentaFlate";
        public string Description => "answers every FlateDecode image with a solid colour";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public IReadOnlyList<string> SupportedFilters { get; } = new[] { "FlateDecode" };

        public bool TryDecode(string filterName, byte[] data, PdfDictionary imageDict,
            out int width, out int height, out int components, out byte[] pixels)
        {
            width = height = 2;
            components = 3;
            byte r = data.Length == 600 * 600 * 3 ? (byte)255 : (byte)0, g = (byte)(255 - r), b = r;
            pixels = new byte[] { r, g, b, r, g, b, r, g, b, r, g, b };
            return true;
        }
    }

    /// <summary>
    /// With a FlateDecode plugin registered, a large image drawn small is the plugin's, and the
    /// plugin is handed the whole filter output as it always was — not the decimated samples:
    /// magenta where it is drawn.
    /// </summary>
    [Fact]
    public void AFlatePlugin_IsAskedFirst()
    {
        const int size = 600;
        byte[] compressed = DecimatingFlateDecodeTests.Zlib(DecimatingFlateDecodeTests.Samples(size, size, 3));
        byte[] pdf = DecimatingFlateDecodeTests.ImagePdf(compressed, size, size, "/ColorSpace /DeviceRGB /BitsPerComponent 8", null, chain: false);
        PdfPluginHost.Instance.Register(new MagentaFlatePlugin());

        using SKBitmap page = SKBitmap.Decode(DecimatingFlateDecodeTests.Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        // The image is drawn at 40,600, 72 pt square, at 96 dpi.
        SKColor centre = page.GetPixel((int)(76 * 96 / 72f), page.Height - 1 - (int)(636 * 96 / 72f));
        Assert.True(centre.Red > 200 && centre.Green < 60 && centre.Blue > 200, $"the plugin's magenta was not drawn: {centre}");
    }
}
