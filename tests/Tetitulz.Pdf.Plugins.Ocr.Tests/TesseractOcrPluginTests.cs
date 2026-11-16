// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Plugins.Ocr;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Ocr.Tests;

/// <summary>
/// <see cref="TesseractOcrPlugin"/> — the parts that are the plugin's own, rather than Tesseract's.
///
/// <para>Recognition needs the native engine and a <c>tessdata</c> corpus, which a build machine
/// generally does not have. That is <b>not</b> a reason to leave the class untested: its two most
/// important behaviours need neither. <see cref="TesseractOcrPlugin.CanProcess"/> is a pure
/// predicate that decides whether an image is routed here at all, and
/// <see cref="TesseractOcrPlugin.RecognizeText"/> promises to return <c>null</c> — never to throw —
/// when recognition fails, because the caller uses that to fall back to the next OCR plugin. A
/// plugin that threw instead would take the whole pipeline down on the first unreadable image.</para>
/// </summary>
public class TesseractOcrPluginTests
{
    /// <summary>A tessdata path that certainly does not exist, so the engine cannot initialise.</summary>
    private static string MissingTessData =>
        Path.Combine(Path.GetTempPath(), "tetitulz-tessdata-does-not-exist-" + nameof(TesseractOcrPluginTests));

    // ================================================================
    //  CanProcess — the routing predicate
    // ================================================================

    /// <summary>
    /// Grayscale and RGB at 8 bits per component are what the plugin converts to PNG for Tesseract.
    /// </summary>
    [Theory]
    [InlineData(1, 8)]
    [InlineData(3, 8)]
    public void CanProcess_AcceptsGrayscaleAndRgbAtEightBits(int components, int bitsPerComponent)
    {
        Assert.True(new TesseractOcrPlugin().CanProcess(components, bitsPerComponent));
    }

    /// <summary>
    /// Everything else must be refused. Claiming CMYK or 16-bit support would route images here
    /// that <c>RawToPng</c> cannot lay out, turning a routing decision into a silent OCR miss.
    /// </summary>
    [Theory]
    [InlineData(4, 8)]    // CMYK
    [InlineData(1, 1)]    // bilevel
    [InlineData(1, 16)]   // 16-bit grayscale
    [InlineData(3, 16)]   // 16-bit RGB
    [InlineData(2, 8)]    // gray + alpha
    [InlineData(0, 8)]    // nonsense
    public void CanProcess_RefusesEverythingElse(int components, int bitsPerComponent)
    {
        Assert.False(new TesseractOcrPlugin().CanProcess(components, bitsPerComponent));
    }

    // ================================================================
    //  RecognizeText — the fallback contract
    // ================================================================

    [Fact]
    public void RecognizeText_WithNoPixels_ReturnsNull()
    {
        var plugin = new TesseractOcrPlugin();

        Assert.Null(plugin.RecognizeText(null!, 10, 10, 1));
        Assert.Null(plugin.RecognizeText(Array.Empty<byte>(), 10, 10, 1));
    }

    /// <summary>
    /// The documented contract: recognition failure returns <c>null</c> so the caller can try the
    /// next plugin. With no tessdata the engine cannot be constructed at all, which is the most
    /// common failure in the field — and it must not escape as an exception.
    /// </summary>
    [Fact]
    public void RecognizeText_WithNoEngineAvailable_ReturnsNullRatherThanThrowing()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);
        var pixels = new byte[16 * 16];   // 16x16 grayscale, all black

        string? text = plugin.RecognizeText(pixels, 16, 16, 1);

        Assert.Null(text);
    }

    /// <summary>
    /// A pixel buffer smaller than its declared dimensions is malformed input. It must still come
    /// back as <c>null</c>, not as an index-out-of-range escaping through the OCR pipeline.
    /// </summary>
    [Fact]
    public void RecognizeText_WithATruncatedBuffer_ReturnsNullRatherThanThrowing()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);
        var tooSmall = new byte[10];   // claims 64x64 RGB, supplies 10 bytes

        Assert.Null(plugin.RecognizeText(tooSmall, 64, 64, 3));
    }

    // ================================================================
    //  Lifecycle
    // ================================================================

    /// <summary>
    /// <c>Initialize</c> fails loudly when tessdata is absent — unlike recognition, this is a
    /// configuration error the host should see at registration rather than as silent OCR misses
    /// later.
    /// </summary>
    [Fact]
    public void Initialize_WithoutTessData_ThrowsWithTheSearchedPath()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        var ex = Assert.Throws<InvalidOperationException>(() => plugin.Initialize());
        Assert.Contains("tessdata", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MissingTessData, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The host may shut a plugin down more than once, or without ever initialising it.</summary>
    [Fact]
    public void Shutdown_IsIdempotentAndSafeBeforeInitialize()
    {
        var plugin = new TesseractOcrPlugin(MissingTessData);

        plugin.Shutdown();
        plugin.Shutdown();

        // Still answers its routing question afterwards — shutdown disposes the engine, not the plugin.
        Assert.True(plugin.CanProcess(1, 8));
    }

    [Fact]
    public void Language_DefaultsToEnglishAndIsReportedInTheDescription()
    {
        Assert.Contains("eng", new TesseractOcrPlugin().Description, StringComparison.Ordinal);
        Assert.Contains("eng", new TesseractOcrPlugin(null, null).Description, StringComparison.Ordinal);
        Assert.Contains("deu", new TesseractOcrPlugin(null, "deu").Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_DeclaresANameAndVersion()
    {
        var plugin = new TesseractOcrPlugin();

        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.NotEqual(new Version(0, 0, 0), plugin.Version);
    }
}
