// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// The buffer an OCR plugin is actually handed, for both facade entry points.
/// </summary>
/// <remarks>
/// <para><b>Why these exist.</b> <c>RecognizeTextWithOcr</c> and <c>RecognizeWordsWithOcr</c> each
/// rasterise a page and hand the bytes to a plugin through <c>Images.OcrSampleBuffer</c>, and
/// every other OCR stub in this suite takes <c>byte[] pixels</c> and ignores it:
/// <c>SearchablePdfTests.StubWordEngine</c> records <c>(width, height)</c> and drops the buffer,
/// <c>FacadeFindingsTests.FakeOcrPlugin</c> returns a constant. Without these, swapping red for
/// blue in the packing loop, or filling the array with zeros, would leave the whole suite green.</para>
///
/// <para><b>What the assertion is.</b> The fixture paints the page's left half red and its right
/// half blue, so the buffer's contents are falsifiable in three independent ways at once: a channel
/// swap reads blue on the left, a blank buffer reads neither colour, and a row/column or stride
/// error reads the wrong half. Asserting a colour at a position is the form CLAUDE.md asks for —
/// "not null / not empty" would pass with the packing deleted, because a correctly sized array of
/// zeros is still an array.</para>
///
/// <para>Both entry points are pinned rather than one, because the point of the shared helper is
/// that the constants a plugin is <i>selected</i> by are the constants its bytes are <i>written</i>
/// under. A test that reached only one call site would leave the other free to drift.</para>
/// </remarks>
[Collection(PluginHostCollection.Name)]
public sealed class OcrSampleBufferTests : IDisposable
{
    public OcrSampleBufferTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>The fixture page is 612x792 points, so 72 dpi renders it 1:1 to pixels.</summary>
    private const int Dpi = 72;

    /// <summary>A channel is "on" well above any antialiasing fringe, and "off" well below.</summary>
    private const int High = 200;
    private const int Low = 60;

    [Fact]
    public void RecognizeWordsWithOcr_HandsThePluginThePagesColoursInRgbOrder()
    {
        var engine = new CapturingWordEngine();
        PdfPluginHost.Instance.Register(engine);

        using var input = new MemoryStream(MinimalPdfBuilder.CreatePdfWithColorStateStack());
        PdfOperations.RecognizeWordsWithOcr(input, 0, Dpi);

        AssertLeftIsRedAndRightIsBlue(engine.Pixels, engine.Width, engine.Height, engine.Components);
    }

    [Fact]
    public void RecognizeTextWithOcr_HandsThePluginTheSameBufferOnTheSameTerms()
    {
        var engine = new CapturingTextEngine();
        PdfPluginHost.Instance.Register(engine);

        using var input = new MemoryStream(MinimalPdfBuilder.CreatePdfWithColorStateStack());
        PdfOperations.RecognizeTextWithOcr(input, 0, Dpi);

        AssertLeftIsRedAndRightIsBlue(engine.Pixels, engine.Width, engine.Height, engine.Components);
    }

    /// <summary>
    /// The page's left half is <c>1 0 0 rg</c> and its right half <c>0 0 1 rg</c>, so a correctly
    /// packed buffer reads red on the left and blue on the right at three components per pixel.
    /// </summary>
    private static void AssertLeftIsRedAndRightIsBlue(
        byte[] pixels, int width, int height, int components)
    {
        Assert.Equal(3, components);
        Assert.True(width > 1 && height > 1, $"the engine was handed a {width}x{height} image");
        Assert.Equal((long)width * height * components, pixels.LongLength);

        int y = height / 2;
        (byte r, byte g, byte b) left = At(pixels, width, components, width / 4, y);
        (byte r, byte g, byte b) right = At(pixels, width, components, (width * 3) / 4, y);

        Assert.True(left.r > High && left.g < Low && left.b < Low,
            $"left half should be red, was ({left.r},{left.g},{left.b})");
        Assert.True(right.b > High && right.r < Low && right.g < Low,
            $"right half should be blue, was ({right.r},{right.g},{right.b})");
    }

    private static (byte r, byte g, byte b) At(
        byte[] pixels, int width, int components, int x, int y)
    {
        int o = ((y * width) + x) * components;
        return (pixels[o], pixels[o + 1], pixels[o + 2]);
    }

    // ---------------------------------------------------------------- capturing engines

    /// <summary>Keeps the buffer instead of dropping it, which is the whole point.</summary>
    private sealed class CapturingWordEngine : IOcrWordPlugin
    {
        public byte[] Pixels { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Components { get; private set; }

        public string Name => "Tetitulz.Tests.CapturingWordEngine";
        public string Description => "Captures the sample buffer it is handed.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanProcess(int components, int bitsPerComponent)
            => components == 3 && bitsPerComponent == 8;

        public string RecognizeText(byte[] pixels, int width, int height, int components)
        {
            Capture(pixels, width, height, components);
            return string.Empty;
        }

        public IReadOnlyList<OcrWordBox> RecognizeWords(
            byte[] pixels, int width, int height, int components)
        {
            Capture(pixels, width, height, components);
            return new List<OcrWordBox>();
        }

        private void Capture(byte[] pixels, int width, int height, int components)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Components = components;
        }
    }

    /// <summary>The text-only contract, so the other facade entry point is reached.</summary>
    private sealed class CapturingTextEngine : IOcrPlugin
    {
        public byte[] Pixels { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Components { get; private set; }

        public string Name => "Tetitulz.Tests.CapturingTextEngine";
        public string Description => "Captures the sample buffer it is handed.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanProcess(int components, int bitsPerComponent)
            => components == 3 && bitsPerComponent == 8;

        public string RecognizeText(byte[] pixels, int width, int height, int components)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
            Components = components;
            return string.Empty;
        }
    }
}
