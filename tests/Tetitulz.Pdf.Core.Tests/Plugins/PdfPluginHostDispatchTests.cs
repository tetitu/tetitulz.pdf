// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Core.Tests.Plugins;

/// <summary>
/// Lookup/dispatch tests for <see cref="PdfPluginHost"/>.
///
/// <para>
/// The previous suite tested that plugins could be <i>registered</i>, never that they were
/// <i>consulted</i>. That is exactly how <c>FindRendererPlugin</c> ended up with zero callers
/// anywhere in the repository while <c>IRendererPlugin</c>, its two implementations and its
/// priority contract all looked healthy.
/// </para>
/// </summary>
[Collection(nameof(PluginHostCollection))]
public sealed class PdfPluginHostDispatchTests : IDisposable
{
    public PdfPluginHostDispatchTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    // ---------------------------------------------------------------- test doubles

    private sealed class Decoder : IImageDecodePlugin
    {
        private readonly string[] _filters;
        public Decoder(string name, params string[] filters) { Name = name; _filters = filters; }
        public string Name { get; }
        public string Description => "test";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public IReadOnlyList<string> SupportedFilters => _filters;
        public bool TryDecode(string filterName, byte[] data, PdfDictionary? parameters,
            out int width, out int height, out int components, out byte[] pixels)
        {
            width = height = components = 0;
            pixels = Array.Empty<byte>();
            return false;
        }
    }

    private sealed class Validator : IValidationPlugin
    {
        public Validator(string level) => ConformanceLevel = level;
        public string Name => "Test.Validator." + ConformanceLevel;
        public string Description => "test";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public string ConformanceLevel { get; }
        public PluginValidationResult Validate(Stream pdfStream) =>
            new PluginValidationResult { ConformanceLevel = ConformanceLevel, IsConformant = true };
    }

    private sealed class Ocr : IOcrPlugin
    {
        private readonly int _components;
        public Ocr(string name, int components) { Name = name; _components = components; }
        public string Name { get; }
        public string Description => "test";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanProcess(int components, int bitsPerComponent) =>
            components == _components && bitsPerComponent == 8;
        public string? RecognizeText(byte[] pixels, int width, int height, int components) => "ocr";
    }

    // ---------------------------------------------------------------- image decode

    [Fact]
    public void FindDecodePlugin_ResolvesByFilterName()
    {
        var jbig2 = new Decoder("Test.Jbig2", "JBIG2Decode");
        PdfPluginHost.Instance.Register(jbig2);

        // Dispatch hardcoded to "JPXDecode" would leave a decoder registered for any other
        // filter — the case the interface docs explicitly advertise — unreachable no matter
        // what it declared.
        Assert.Same(jbig2, PdfPluginHost.Instance.FindDecodePlugin("JBIG2Decode"));
        Assert.Null(PdfPluginHost.Instance.FindDecodePlugin("JPXDecode"));
    }

    [Fact]
    public void FindDecodePlugin_IsCaseSensitive_MatchingThePdfSpecSpelling()
    {
        PdfPluginHost.Instance.Register(new Decoder("Test.Jpx", "JPXDecode"));

        Assert.NotNull(PdfPluginHost.Instance.FindDecodePlugin("JPXDecode"));
        Assert.Null(PdfPluginHost.Instance.FindDecodePlugin("jpxdecode"));
    }

    [Fact]
    public void FindDecodePlugin_FirstRegisteredWinsPerFilter()
    {
        var first = new Decoder("Test.First", "JPXDecode");
        var second = new Decoder("Test.Second", "JPXDecode");
        PdfPluginHost.Instance.Register(first);
        PdfPluginHost.Instance.Register(second);

        Assert.Same(first, PdfPluginHost.Instance.FindDecodePlugin("JPXDecode"));
    }

    [Fact]
    public void FindDecodePlugin_AfterUnregister_ReturnsNull()
    {
        PdfPluginHost.Instance.Register(new Decoder("Test.Jpx", "JPXDecode"));
        PdfPluginHost.Instance.Unregister("Test.Jpx");

        Assert.Null(PdfPluginHost.Instance.FindDecodePlugin("JPXDecode"));
    }

    [Fact]
    public void FindDecodePlugin_NullFilterName_ReturnsNull() =>
        Assert.Null(PdfPluginHost.Instance.FindDecodePlugin(null!));

    // ---------------------------------------------------------------- validation

    [Fact]
    public void FindValidationPlugin_ResolvesByConformanceLevelCaseInsensitively()
    {
        var ua = new Validator("PDF/UA-1");
        PdfPluginHost.Instance.Register(new Validator("PDF/A-1b"));
        PdfPluginHost.Instance.Register(ua);

        Assert.Same(ua, PdfPluginHost.Instance.FindValidationPlugin("PDF/UA-1"));
        Assert.Same(ua, PdfPluginHost.Instance.FindValidationPlugin("pdf/ua-1"));
        Assert.Null(PdfPluginHost.Instance.FindValidationPlugin("PDF/X-1a"));
    }

    [Fact]
    public void GetValidationPlugins_ReturnsAllRegisteredLevels()
    {
        PdfPluginHost.Instance.Register(new Validator("PDF/A-1b"));
        PdfPluginHost.Instance.Register(new Validator("PDF/UA-1"));

        Assert.Equal(2, PdfPluginHost.Instance.GetValidationPlugins().Count);
    }

    // ---------------------------------------------------------------- OCR

    [Fact]
    public void FindOcrPlugin_SelectsFirstPluginThatAcceptsTheFormat()
    {
        var gray = new Ocr("Test.Gray", components: 1);
        var rgb = new Ocr("Test.Rgb", components: 3);
        PdfPluginHost.Instance.Register(gray);
        PdfPluginHost.Instance.Register(rgb);

        Assert.Same(gray, PdfPluginHost.Instance.FindOcrPlugin(1, 8));
        Assert.Same(rgb, PdfPluginHost.Instance.FindOcrPlugin(3, 8));
        Assert.Null(PdfPluginHost.Instance.FindOcrPlugin(4, 8));   // nobody accepts CMYK
        Assert.Null(PdfPluginHost.Instance.FindOcrPlugin(3, 16));  // wrong bit depth
    }

    // ---------------------------------------------------------------- typed queries

    [Fact]
    public void GetPlugins_Typed_FiltersByInterface()
    {
        PdfPluginHost.Instance.Register(new Decoder("Test.Dec", "JPXDecode"));
        PdfPluginHost.Instance.Register(new Validator("PDF/A-1b"));

        Assert.Single(PdfPluginHost.Instance.GetPlugins<IImageDecodePlugin>());
        Assert.Single(PdfPluginHost.Instance.GetPlugins<IValidationPlugin>());
        Assert.Equal(2, PdfPluginHost.Instance.GetPlugins<IPdfPlugin>().Count);
        Assert.Empty(PdfPluginHost.Instance.GetPlugins<IRendererPlugin>());
    }

    /// <summary>
    /// <c>GetFirstPlugin&lt;T&gt;</c> is the documented resolve path for the discovery-only
    /// contracts (ISanitizePlugin, IOptimizePlugin, …), so it must actually work.
    /// </summary>
    [Fact]
    public void GetFirstPlugin_ReturnsNullWhenNoneRegistered_AndTheInstanceWhenOneIs()
    {
        Assert.Null(PdfPluginHost.Instance.GetFirstPlugin<IValidationPlugin>());

        var v = new Validator("PDF/A-1b");
        PdfPluginHost.Instance.Register(v);

        Assert.Same(v, PdfPluginHost.Instance.GetFirstPlugin<IValidationPlugin>());
    }
}
