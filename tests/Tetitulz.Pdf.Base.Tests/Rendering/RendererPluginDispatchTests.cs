// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// End-to-end proof that <see cref="IRendererPlugin"/> is actually consulted by the render
/// pipeline.
///
/// <para>
/// <b>The bug.</b> <c>PdfPluginHost.FindRendererPlugin</c> had <b>zero callers</b> anywhere in
/// src, tests, apps or demo. The interface, its <c>Priority</c>/<c>CanRender</c> contract, the
/// host lookup (complete with a careful comment about evaluating <c>CanRender</c> outside the
/// registry lock) and two shipping implementations all existed — and a registered renderer
/// plugin was never asked to render anything. The old plugin tests only checked that plugins
/// could be registered, so nothing caught it.
/// </para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class RendererPluginDispatchTests : IDisposable
{
    public RendererPluginDispatchTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>A 1x1 red PNG — enough to prove the bytes came from the plugin, not Skia.</summary>
    private static readonly byte[] SentinelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private class FakeRenderer : IRendererPlugin
    {
        private readonly bool _canRender;
        public FakeRenderer(string name, int priority, bool canRender = true)
        { Name = name; Priority = priority; _canRender = canRender; }

        public string Name { get; }
        public string Description => "test renderer";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public int Priority { get; }
        public bool CanRenderCalled { get; private set; }
        public bool RenderCalled { get; private set; }

        public bool CanRender(PdfPage page, PdfDocument document)
        {
            CanRenderCalled = true;
            return _canRender;
        }

        public virtual byte[] RenderPage(PdfPage page, PdfDocument document, int dpi)
        {
            RenderCalled = true;
            return SentinelPng;
        }
    }

    private sealed class ThrowingRenderer : FakeRenderer
    {
        public ThrowingRenderer() : base("Test.Throwing", 100) { }
        public override byte[] RenderPage(PdfPage page, PdfDocument document, int dpi)
            => throw new InvalidOperationException("renderer plugin exploded");
    }

    private sealed class EmptyOutputRenderer : FakeRenderer
    {
        public EmptyOutputRenderer() : base("Test.Empty", 100) { }
        public override byte[] RenderPage(PdfPage page, PdfDocument document, int dpi)
            => Array.Empty<byte>();
    }

    private static byte[] Pdf() => MinimalPdfBuilder.CreateMinimalPdf("renderer plugin dispatch");

    // ----------------------------------------------------------------

    [Fact]
    public void RegisteredRendererPlugin_IsConsultedAndItsOutputIsReturned()
    {
        var plugin = new FakeRenderer("Test.Renderer", priority: 10);
        PdfPluginHost.Instance.Register(plugin);

        using var ms = new MemoryStream(Pdf());
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, 72);

        Assert.True(plugin.CanRenderCalled, "CanRender was never called — the plugin was not consulted.");
        Assert.True(plugin.RenderCalled, "RenderPage was never called — dispatch is not wired.");
        Assert.Equal(SentinelPng, png);
    }

    /// <summary>
    /// The batch path dispatches too — for <b>every</b> page, not just the first.
    /// </summary>
    /// <remarks>
    /// <c>RenderAllPages</c> is the only other caller of the private <c>RenderPage(PdfPage, …)</c>
    /// overload that owns the <c>FindRendererPlugin</c> lookup, and the obvious way to make that
    /// loop share one document and one font cache — pointing it at <c>RenderPageWithSharedState</c>
    /// — silently skips the lookup. Nothing else would have caught it:
    /// <c>PdfPluginHostTests.RenderHook_RenderAllPagesToPng_FiredForEachPage</c> exercises
    /// <c>IRenderHook.OnPageRendered</c>, which <c>PdfOperations</c> invokes one level up and which
    /// has nothing to do with <see cref="IRendererPlugin"/>.
    /// </remarks>
    [Fact]
    public void RegisteredRendererPlugin_IsConsultedForEveryPageOfRenderAllPages()
    {
        var plugin = new FakeRenderer("Test.Renderer", priority: 10);
        PdfPluginHost.Instance.Register(plugin);

        using var ms = new MemoryStream(MinimalPdfBuilder.CreateMultiPagePdf(3));
        byte[][] pages = PdfOperations.RenderAllPagesToPng(ms, 72);

        Assert.Equal(3, pages.Length);
        foreach (byte[] png in pages)
            Assert.Equal(SentinelPng, png);
    }

    [Fact]
    public void NoRendererPlugin_UsesBuiltInRenderer()
    {
        using var ms = new MemoryStream(Pdf());
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, 72);

        Assert.NotEqual(SentinelPng, png);
        Assert.True(png.Length > 8);
        Assert.Equal(0x89, png[0]);   // real PNG from the built-in renderer
    }

    [Fact]
    public void HighestPriorityPluginWins()
    {
        var low = new FakeRenderer("Test.Low", priority: 1);
        var high = new FakeRenderer("Test.High", priority: 99);
        PdfPluginHost.Instance.Register(low);
        PdfPluginHost.Instance.Register(high);

        using var ms = new MemoryStream(Pdf());
        PdfOperations.RenderPageToPng(ms, 0, 72);

        Assert.True(high.RenderCalled);
        Assert.False(low.RenderCalled);
    }

    [Fact]
    public void PluginDecliningViaCanRender_FallsThroughToTheNextPlugin()
    {
        var declines = new FakeRenderer("Test.Declines", priority: 99, canRender: false);
        var accepts = new FakeRenderer("Test.Accepts", priority: 1);
        PdfPluginHost.Instance.Register(declines);
        PdfPluginHost.Instance.Register(accepts);

        using var ms = new MemoryStream(Pdf());
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, 72);

        Assert.True(declines.CanRenderCalled);
        Assert.False(declines.RenderCalled);
        Assert.True(accepts.RenderCalled);
        Assert.Equal(SentinelPng, png);
    }

    /// <summary>A broken plugin must degrade to the built-in renderer, not break rendering.</summary>
    [Fact]
    public void PluginThatThrows_FallsBackToBuiltInAndReportsDiagnostic()
    {
        PdfPluginHost.Instance.Register(new ThrowingRenderer());

        var events = new List<string>();
        Action<RenderDiagnosticKind, string, Exception> handler =
            (kind, detail, _) => { if (kind == RenderDiagnosticKind.PluginFailure) events.Add(detail); };
        RenderDiagnostics.OnEvent += handler;
        try
        {
            using var ms = new MemoryStream(Pdf());
            byte[] png = PdfOperations.RenderPageToPng(ms, 0, 72);

            Assert.True(png.Length > 8);
            Assert.Equal(0x89, png[0]);           // built-in output, not the sentinel
            Assert.NotEqual(SentinelPng, png);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= handler;
        }

        Assert.Contains(events, e => e.Contains("Test.Throwing"));
    }

    [Fact]
    public void PluginReturningEmptyOutput_FallsBackToBuiltIn()
    {
        PdfPluginHost.Instance.Register(new EmptyOutputRenderer());

        using var ms = new MemoryStream(Pdf());
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, 72);

        Assert.True(png.Length > 8);
        Assert.Equal(0x89, png[0]);
    }

    [Fact]
    public void UnregisteredPlugin_IsNoLongerConsulted()
    {
        var plugin = new FakeRenderer("Test.Renderer", priority: 10);
        PdfPluginHost.Instance.Register(plugin);
        PdfPluginHost.Instance.Unregister("Test.Renderer");

        using var ms = new MemoryStream(Pdf());
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, 72);

        Assert.False(plugin.RenderCalled);
        Assert.NotEqual(SentinelPng, png);
    }
}
