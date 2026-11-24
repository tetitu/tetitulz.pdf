// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.Renderers;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Renderers.Tests;

/// <summary>
/// The two <see cref="IRendererPlugin"/>s: <see cref="SkiaSharpRendererPlugin"/> (PNG) and
/// <see cref="SvgRendererPlugin"/> (SVG-as-UTF-8-bytes).
///
/// <para><b>These decode what comes back.</b> Both plugins return <c>byte[]</c>, and a renderer
/// that produced a blank page would return a perfectly valid non-empty PNG — the exact shape
/// CLAUDE.md's third banned pattern describes. So the raster test decodes the PNG and asserts the
/// page's red rectangle is actually red, and the SVG test asserts on the emitted markup.</para>
/// </summary>
public class RendererPluginTests
{
    private const int Dpi = 72;

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    // ================================================================
    //  SkiaSharp (raster)
    // ================================================================

    /// <summary>
    /// The fixture fills the left half of a 612×792 page red. Decoding proves the plugin rendered
    /// the page rather than handing back an empty image.
    /// </summary>
    [Fact]
    public void SkiaSharpPlugin_RendersThePagesPixels()
    {
        using var doc = Open(MinimalPdfBuilder.CreatePdfWithRedRectangle());

        byte[] png = new SkiaSharpRendererPlugin().RenderPage(doc.Pages[0], doc, Dpi);

        using SKBitmap bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        SKColor left = bmp!.GetPixel(bmp.Width / 4, bmp.Height / 2);
        Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60,
            $"the left half should be the fixture's red, got {left}");

        SKColor right = bmp.GetPixel(bmp.Width * 3 / 4, bmp.Height / 2);
        Assert.True(right.Red > 200 && right.Green > 200 && right.Blue > 200,
            $"the right half should be the unpainted page, got {right}");
    }

    [Fact]
    public void SkiaSharpPlugin_HonoursDpi()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());
        var plugin = new SkiaSharpRendererPlugin();

        using SKBitmap at72 = SKBitmap.Decode(plugin.RenderPage(doc.Pages[0], doc, 72));
        using SKBitmap at144 = SKBitmap.Decode(plugin.RenderPage(doc.Pages[0], doc, 144));

        // Twice the resolution, twice the pixels on each axis (±1 for rounding).
        Assert.InRange(at144.Width, at72.Width * 2 - 2, at72.Width * 2 + 2);
        Assert.InRange(at144.Height, at72.Height * 2 - 2, at72.Height * 2 + 2);
    }

    [Fact]
    public void SkiaSharpPlugin_ReturnsAPngSignature()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        byte[] png = new SkiaSharpRendererPlugin().RenderPage(doc.Pages[0], doc, Dpi);

        // 89 50 4E 47 0D 0A 1A 0A — the PNG magic. The interface documents PNG specifically, and a
        // host that trusts the declared format would mis-handle anything else.
        Assert.True(png.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
    }

    // ================================================================
    //  Selection contract
    // ================================================================

    [Fact]
    public void ThePlugin_DeclaresANameAndVersion()
    {
        IRendererPlugin plugin = new SkiaSharpRendererPlugin();

        Assert.False(string.IsNullOrWhiteSpace(plugin.Name));
        Assert.NotNull(plugin.Version);
        Assert.NotEqual(new Version(0, 0, 0), plugin.Version);
    }

    [Fact]
    public void ThePlugin_CanRenderAnOrdinaryPage()
    {
        using var doc = Open(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.True(new SkiaSharpRendererPlugin().CanRender(doc.Pages[0], doc));
    }

    /// <summary>
    /// <b>Regression guard.</b> The plugin must be exercised the way it is actually used —
    /// <i>registered</i>, then reached through the ordinary PNG entry point.
    /// </summary>
    /// <remarks>
    /// <see cref="SkiaSharpRendererPlugin"/> delegates to <c>PdfPageRenderer.RenderPageWithoutPlugins</c>:
    /// delegating to <c>PdfPageRenderer.RenderPage</c>, the entry point that dispatches to it, would
    /// recurse until the stack overflowed once the plugin is registered, killing the process. A test
    /// that calls <c>plugin.RenderPage(...)</c> <b>directly</b> with nothing registered cannot see
    /// that, since dispatch then finds no plugin and the built-in renders normally.
    /// </remarks>
    [Fact]
    public void RegisteredPlugin_RendersThroughThePngPath_WithoutRecursing()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            PdfPluginHost.Instance.Register(new SkiaSharpRendererPlugin());

            using var input = new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle());
            byte[] result = Tetitulz.Pdf.Base.PdfOperations.RenderPageToPng(input, 0, Dpi);

            Assert.Equal(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                result[..8]);

            // Decode and check the fixture's red half really rendered, so this cannot pass on a
            // blank-but-valid PNG (banned pattern 3).
            using var bmp = SKBitmap.Decode(result);
            Assert.NotNull(bmp);
            SKColor left = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
            Assert.True(left.Red > 200 && left.Green < 60 && left.Blue < 60,
                $"expected the left half to be red, got {left}");
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }

    /// <summary>
    /// <b>Regression guard.</b> The PNG path returns PNG, the contract every registered
    /// <see cref="IRendererPlugin"/> must keep too — <c>PdfPageRenderer.RenderPage</c> is documented
    /// "renders one page to PNG bytes", and <c>PdfPluginHost</c>'s own table says an
    /// <c>IRendererPlugin</c> "replaces the built-in renderer on the PNG paths".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A renderer plugin returning SVG markup from <c>RenderPage</c> with a higher <c>Priority</c> and
    /// a <c>CanRender</c> that accepted every page would win <c>FindRendererPlugin</c>'s descending
    /// sort, and any host that referenced its package and called
    /// <c>RegisterFromLoadedAssemblies()</c> would silently get <c>&lt;?xml … &lt;svg&gt;</c> back from
    /// <c>PdfOperations.RenderPageToPng</c>. SVG is produced through the contract shaped for it,
    /// <c>Plugins.Svg.SvgExportPlugin</c> (<c>IExportPlugin</c>, <c>FormatKey "svg"</c>), or directly
    /// via the public <c>Base.Rendering.PdfSvgRenderer</c>.
    /// </para>
    /// <para>
    /// Asserting on the <b>bytes that come back</b> rather than on priority numbers is what makes
    /// these tests unable to lie: given the descending sort, <c>raster &lt; svg</c> under a name
    /// promising the raster one is preferred proves the opposite of its name.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePngPath_ReturnsPng_WhenNoRendererPluginIsRegistered()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            using var input = new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle());
            byte[] result = Tetitulz.Pdf.Base.PdfOperations.RenderPageToPng(input, 0, Dpi);

            Assert.True(result.Length > 8, "the PNG path returned nothing usable.");
            Assert.Equal(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                result[..8]);
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }

    /// <summary>
    /// The host hands the plugin's bytes back to the caller verbatim, so whatever a registered
    /// <see cref="IRendererPlugin"/> returns <i>is</i> the PNG-path result. That is exactly why the
    /// SVG renderer plugin was a defect rather than a feature, and why this contract is worth
    /// pinning with a stub whose output is unambiguous.
    /// </summary>
    [Fact]
    public void ThePngPath_ReturnsTheRegisteredPluginsBytesVerbatim()
    {
        PdfPluginHost.Instance.ResetForTesting();
        try
        {
            var stub = new StubPngPlugin();
            PdfPluginHost.Instance.Register(stub);

            using var input = new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle());
            byte[] result = Tetitulz.Pdf.Base.PdfOperations.RenderPageToPng(input, 0, Dpi);

            Assert.Equal(StubPngPlugin.Payload, result);
        }
        finally
        {
            PdfPluginHost.Instance.ResetForTesting();
        }
    }

    private sealed class StubPngPlugin : IRendererPlugin
    {
        // A minimal but real PNG signature, so "the host returned the plugin's bytes" is
        // distinguishable from "the host rendered the page itself".
        public static readonly byte[] Payload =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF };

        public string Name        => "Tetitulz.Tests.StubPngRenderer";
        public string Description => "Returns a fixed PNG payload.";
        public Version Version    => new Version(1, 0, 0);
        public int Priority       => 100;
        public void Initialize() { }
        public void Shutdown()   { }
        public bool CanRender(PdfPage page, PdfDocument document) => true;
        public byte[] RenderPage(PdfPage page, PdfDocument document, int dpi) => Payload;
    }
}
