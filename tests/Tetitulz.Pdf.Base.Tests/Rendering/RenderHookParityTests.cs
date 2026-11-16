// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The render hook fires for a render, not for a render that happened to start from a
/// <see cref="Stream"/>.
///
/// <para><b>Why this exists.</b> The plugin notification on <c>PdfOperations</c> was written out
/// four times — once per (PNG|JPEG) × (single|all) combination — and all four copies sat in the
/// <c>Stream</c> overloads. Every <c>string</c> overload delegated straight to the renderer, so a
/// plugin observing renders saw half of them and nothing in the signatures, the docs or the plugin
/// contract said so.</para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class RenderHookParityTests
{
    // A page size no other fixture uses, so a concurrently-running test's render cannot be
    // mistaken for one of ours. At 72 dpi the pixel extent is the point extent.
    private const int PageWidth  = 123;
    private const int PageHeight = 77;

    /// <summary>
    /// All eight entry points — four taking a <see cref="Stream"/>, four taking a path — notify.
    /// Before the four copies were merged the file-based half was silent, so this counted 4.
    /// </summary>
    [Fact]
    public void PageRenderedHook_FiresForTheFileOverloads_AsWellAsTheStreamOnes()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CountingRenderPlugin();
        string path = Path.Combine(Path.GetTempPath(), $"tetitulz-hook-{Guid.NewGuid():N}.pdf");

        try
        {
            host.Register(plugin);
            byte[] pdf = OnePagePdf();
            File.WriteAllBytes(path, pdf);

            PdfOperations.RenderPageToPng (new MemoryStream(pdf), 0, 72);
            PdfOperations.RenderAllPagesToPng(new MemoryStream(pdf), 72);
            PdfOperations.RenderPageToJpg (new MemoryStream(pdf), 0, 72);
            PdfOperations.RenderAllPagesToJpg(new MemoryStream(pdf), 72);

            PdfOperations.RenderPageToPng (path, 0, 72);
            PdfOperations.RenderAllPagesToPng(path, 72);
            PdfOperations.RenderPageToJpg (path, 0, 72);
            PdfOperations.RenderAllPagesToJpg(path, 72);

            int ours = plugin.Observations.Count(o => o.W == PageWidth && o.H == PageHeight);
            Assert.Equal(8, ours);
        }
        finally
        {
            host.Unregister(plugin.Name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The six entry points that were added after the four copies above were merged, and that
    /// notified nothing.
    ///
    /// <para><b>Why this matters more than it looks.</b>
    /// <c>RenderPageToImage(stream, 0, PdfImageFormat.Png, 72)</c> reaches the <i>identical</i>
    /// renderer call as <c>RenderPageToPng(stream, 0, 72)</c> and produces byte-identical output —
    /// so a host metering pages through <see cref="IRenderHook"/> for licensing lost every page by
    /// switching between two overloads that differ in nothing a caller can see. The same holds for
    /// <c>WebpLossless</c>, which the facade's own doc comment recommends as ~9x smaller than
    /// PNG.</para>
    /// </summary>
    [Fact]
    public void PageRenderedHook_FiresForTheFormatAndStreamEntryPointsToo()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CountingRenderPlugin();
        string path = Path.Combine(Path.GetTempPath(), $"tetitulz-hook2-{Guid.NewGuid():N}.pdf");

        try
        {
            host.Register(plugin);
            byte[] pdf = OnePagePdf();
            File.WriteAllBytes(path, pdf);

            PdfOperations.RenderPageToImage(new MemoryStream(pdf), 0, PdfImageFormat.Png, 72);
            PdfOperations.RenderPageToImage(path, 0, PdfImageFormat.Png, 72);
            PdfOperations.RenderAllPagesToImages(new MemoryStream(pdf), PdfImageFormat.Png, 72);
            PdfOperations.RenderAllPagesToImages(new MemoryStream(pdf), PdfImageFormat.Png,
                (_, _) => { }, 72);
            PdfOperations.RenderPageToStream(new MemoryStream(pdf), 0, new MemoryStream(),
                PdfImageFormat.Png, 72);
            PdfOperations.RenderPageToFit(new MemoryStream(pdf), 0, PageWidth, PageHeight);

            int ours = plugin.Observations.Count(o => o.W == PageWidth && o.H == PageHeight);
            Assert.Equal(6, ours);
        }
        finally
        {
            host.Unregister(plugin.Name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A JPEG's SOF, a PNG's IHDR and a WebP's RIFF chunk carry the size in three different places,
    /// so the hook's extent is only meaningful if the reader is keyed off the format. WebP had no
    /// reader at all, which would have notified <c>(0,0)</c> for the format the docs recommend.
    /// </summary>
    [Theory]
    [InlineData(PdfImageFormat.Png)]
    [InlineData(PdfImageFormat.Jpeg)]
    [InlineData(PdfImageFormat.WebpLossless)]
    [InlineData(PdfImageFormat.WebpLossy)]
    public void PageRenderedHook_ReportsTheRealPixelExtent_ForEveryFormat(PdfImageFormat format)
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CountingRenderPlugin();
        try
        {
            host.Register(plugin);
            PdfOperations.RenderPageToImage(new MemoryStream(OnePagePdf()), 0, format, 72);

            Assert.Contains(plugin.Observations, o => o.W == PageWidth && o.H == PageHeight);
        }
        finally { host.Unregister(plugin.Name); }
    }

    /// <summary>
    /// Every <c>Render*</c> entry point on the facade that produces an ENCODED page image is
    /// covered by one of the tests above.
    ///
    /// <para>Reflective rather than a count, because counting to a literal 8 is what let six new
    /// overloads go dark: the count still said 8 and still passed. Adding an overload now fails
    /// here, naming it, until it is either wired to the hook and listed as covered or added to
    /// <see cref="NotifiesNothingByDesign"/> with a reason.</para>
    /// </summary>
    [Fact]
    public void EveryEncodedRenderEntryPoint_IsCoveredByAHookTest()
    {
        var uncovered = typeof(PdfOperations)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("Render", StringComparison.Ordinal))
            .Where(m => !NotifiesNothingByDesign.Contains(m.Name))
            .Select(Signature)
            .Where(sig => !CoveredByAHookTest.Contains(sig))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(uncovered.Count == 0,
            "these render entry points notify no IRenderHook and no test covers them:\n  "
            + string.Join("\n  ", uncovered));

        // Guard the guard: discovery must actually be finding entry points, or the sweep passes
        // vacuously — the failure mode this whole file exists to remove.
        Assert.True(CoveredByAHookTest.Count >= 14);
    }

    private static string Signature(MethodInfo m) =>
        m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")";

    /// <summary>
    /// Entry points that notify nothing, each for a stated reason — <b>not</b> a place to park an
    /// overload that simply has no test.
    /// <list type="bullet">
    ///   <item><c>RenderPageToPixels</c> returns raw pixels, no encoded image, and the caller
    ///   already holds the extent on the returned <c>RenderedPage</c>.</item>
    ///   <item><c>RenderPageSeparations</c> likewise: raw plates, one per colorant, with the extent
    ///   on the returned <c>PdfSeparations</c> — and N plate passes are one page, which a per-pass
    ///   notification would over-count as the tile entry points would.</item>
    ///   <item><c>RenderPageHalftoned</c> and <c>RenderPageSeparationsHalftoned</c> return one-bit
    ///   pixels, unencoded, with the extent on the returned image — the two entries above, screened.</item>
    ///   <item><c>RenderPageToSvg</c> / <c>RenderAllPagesToSvg</c> return markup, not a raster
    ///   page; <see cref="IRenderHook"/>'s contract is a pixel extent.</item>
    ///   <item><b>The tile entry points render a FRACTION of a page.</b> Firing per tile would make
    ///   the metering counter the hook exists for over-count by the tile count — a 4x4 tiling would
    ///   report sixteen page renders for one page. The finding this file answers
    ///   ("IRenderHook fires from eight render entry points") does not list them
    ///   either. If a tile-level signal is ever wanted it needs its own hook, not this one.</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> NotifiesNothingByDesign = new(StringComparer.Ordinal)
    {
        "RenderPageToPixels", "RenderPageSeparations", "RenderPageHalftoned", "RenderPageSeparationsHalftoned",
        "RenderPageToSvg", "RenderAllPagesToSvg",
        "RenderPageTile", "RenderPageTileToJpg",
    };

    private static readonly HashSet<string> CoveredByAHookTest = new(StringComparer.Ordinal)
    {
        // PageRenderedHook_FiresForTheFileOverloads_AsWellAsTheStreamOnes
        "RenderPageToPng(String,Int32,Int32,PdfRenderOptions)",
        "RenderPageToPng(Stream,Int32,Int32,PdfRenderOptions)",
        "RenderAllPagesToPng(String,Int32,PdfRenderOptions)",
        "RenderAllPagesToPng(Stream,Int32,PdfRenderOptions)",
        "RenderPageToJpg(String,Int32,Int32,Int32,PdfRenderOptions)",
        "RenderPageToJpg(Stream,Int32,Int32,Int32,PdfRenderOptions)",
        "RenderAllPagesToJpg(String,Int32,Int32,PdfRenderOptions)",
        "RenderAllPagesToJpg(Stream,Int32,Int32,PdfRenderOptions)",
        // PageRenderedHook_FiresForTheFormatAndStreamEntryPointsToo
        "RenderPageToImage(Stream,Int32,PdfImageFormat,Int32,Int32,PdfRenderOptions,CancellationToken)",
        "RenderPageToImage(String,Int32,PdfImageFormat,Int32,Int32,PdfRenderOptions,CancellationToken)",
        "RenderAllPagesToImages(Stream,PdfImageFormat,Int32,Int32,PdfRenderOptions,CancellationToken)",
        "RenderAllPagesToImages(Stream,PdfImageFormat,Action`2,Int32,Int32,PdfRenderOptions,CancellationToken)",
        "RenderPageToStream(Stream,Int32,Stream,PdfImageFormat,Int32,Int32,PdfRenderOptions,CancellationToken)",
        "RenderPageToFit(Stream,Int32,Int32,Int32,PdfImageFormat,Int32,PdfRenderOptions,CancellationToken)",
    };

    private sealed class CountingRenderPlugin : IPdfPlugin, IRenderHook
    {
        public string  Name        => "Test.RenderHookParity";
        public string  Description => "Counts page-rendered notifications";
        public Version Version     => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown()   { }

        private readonly object _lock = new object();
        private readonly List<(int Page, int W, int H)> _obs = new List<(int, int, int)>();

        // A snapshot copy: renders on other threads may notify while the assertion iterates.
        public IReadOnlyList<(int Page, int W, int H)> Observations
        {
            get { lock (_lock) return _obs.ToList(); }
        }

        public void OnPageRendered(int page, int w, int h,
            System.Threading.CancellationToken ct = default)
        {
            lock (_lock) _obs.Add((page, w, h));
        }
    }

    private static byte[] OnePagePdf()
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        const string content = "1 0 0 rg 10 10 20 20 re f";

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] "
            + "/Contents 4 0 R /Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
