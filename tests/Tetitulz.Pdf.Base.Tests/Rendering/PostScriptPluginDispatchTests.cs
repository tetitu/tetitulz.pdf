// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// End-to-end proof that <see cref="IPostScriptPlugin"/> is consulted by both renderers for a
/// <c>/Subtype /PS</c> XObject and for the legacy inline <c>(...) PS</c> operator, and that every
/// failure mode falls back to the documented placeholder rather than breaking the page.
///
/// <para>Every test asserts on decoded pixels or on emitted SVG elements — never on "output is
/// non-empty", which a blank page satisfies (CLAUDE.md banned test pattern 3).</para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class PostScriptPluginDispatchTests : IDisposable
{
    private Action<RenderDiagnosticKind, string, Exception> _subscription;

    public PostScriptPluginDispatchTests() => PdfPluginHost.Instance.ResetForTesting();

    public void Dispose()
    {
        if (_subscription != null) RenderDiagnostics.OnEvent -= _subscription;
        PdfPluginHost.Instance.ResetForTesting();
    }

    // The sentinel a passing plugin paints: a pure-blue 60x60 square at (10,10) in the XObject's
    // own space. Blue is chosen because neither placeholder (grey outline, near-white fill) nor a
    // blank page can produce it, so "is there blue ink here" cannot pass by accident.
    private const string SentinelContent = "0 0 1 rg 10 10 60 60 re f";

    // ==================================================================
    //  Fake plugins
    // ==================================================================

    private class FakePostScriptPlugin : IPostScriptPlugin
    {
        private readonly string _content;
        public FakePostScriptPlugin(string name = "Test.Ps", string content = SentinelContent)
        { Name = name; _content = content; }

        public string Name { get; }
        public string Description => "test PostScript plugin";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public int CallCount { get; private set; }
        public PostScriptContentRequest LastRequest { get; private set; }

        public virtual bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                   out PostScriptContentResult result)
        {
            CallCount++;
            LastRequest = request;
            result = new PostScriptContentResult
            {
                ContentStream = Encoding.ASCII.GetBytes(_content)
            };
            return true;
        }
    }

    private sealed class DecliningPlugin : FakePostScriptPlugin
    {
        public DecliningPlugin() : base("Test.Declining") { }
        public override bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                    out PostScriptContentResult result)
        {
            base.TryConvertToPdfContent(request, out _);
            result = null;
            return false;
        }
    }

    private sealed class ThrowingPlugin : FakePostScriptPlugin
    {
        public ThrowingPlugin() : base("Test.Throwing") { }
        public override bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                    out PostScriptContentResult result)
            => throw new InvalidOperationException("PostScript plugin exploded");
    }

    private sealed class OversizedContentPlugin : FakePostScriptPlugin
    {
        public OversizedContentPlugin() : base("Test.Oversized") { }
        public override bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                    out PostScriptContentResult result)
        {
            result = new PostScriptContentResult
            {
                ContentStream = new byte[PdfLimits.MaxPluginContentStreamBytes + 1]
            };
            return true;
        }
    }

    private sealed class OversizedResourcePlugin : FakePostScriptPlugin
    {
        public OversizedResourcePlugin() : base("Test.OversizedRes") { }
        public override bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                    out PostScriptContentResult result)
        {
            var res = new PdfDictionary();
            for (int i = 0; i <= PdfLimits.MaxPluginResourceEntries; i++)
                res[new PdfName("K" + i)] = new PdfNumber(i);
            result = new PostScriptContentResult
            {
                ContentStream = Encoding.ASCII.GetBytes(SentinelContent),
                Resources     = res
            };
            return true;
        }
    }

    /// <summary>Returns a resource graph few entries wide and one level deeper than the depth cap.</summary>
    private sealed class DeepResourcePlugin : FakePostScriptPlugin
    {
        public DeepResourcePlugin() : base("Test.DeepRes") { }
        public override bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                    out PostScriptContentResult result)
        {
            var root = new PdfDictionary();
            var node = root;
            for (int i = 0; i <= PdfLimits.MaxObjectGraphDepth + 1; i++)
            {
                var next = new PdfDictionary();
                node[new PdfName("N")] = next;
                node = next;
            }
            result = new PostScriptContentResult
            {
                ContentStream = Encoding.ASCII.GetBytes(SentinelContent),
                Resources     = root
            };
            return true;
        }
    }

    /// <summary>Emits content that re-invokes the very PS XObject being rendered.</summary>
    private sealed class RecursivePlugin : FakePostScriptPlugin
    {
        public RecursivePlugin() : base("Test.Recursive") { }
        public override bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                    out PostScriptContentResult result)
        {
            base.TryConvertToPdfContent(request, out _);
            result = new PostScriptContentResult
            {
                // Null resources → inherits page resources, which still name /PsXo.
                ContentStream = Encoding.ASCII.GetBytes("0 0 1 rg 10 10 60 60 re f /PsXo Do")
            };
            return true;
        }
    }

    // ==================================================================
    //  /Subtype /PS XObject — raster
    // ==================================================================

    /// <summary>
    /// With a plugin registered the plugin's content is what paints. The XObject sits at
    /// (100,400) and paints blue over [10,10]-[70,70] in its own space, i.e. [110,410]-[170,470]
    /// on the page.
    /// </summary>
    [Fact]
    public void PsXObject_Raster_PaintsThePluginsContentInsteadOfThePlaceholder()
    {
        PdfPluginHost.Instance.Register(new FakePostScriptPlugin());

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 140, 440));
        // The placeholder's grey outline would have inked the BBox edge at y=400; the plugin
        // paints nothing there, so this pixel proves the placeholder did NOT also run.
        Assert.Equal(SKColors.White, PixelAt(bmp, 250, 401));
    }

    [Fact]
    public void PsXObject_Raster_NoPlugin_StillDrawsThePlaceholder()
    {
        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.True(InkCount(bmp, 105, 405, 295, 495) > 0,
            "no placeholder was drawn for the PostScript XObject.");
        Assert.Equal(SKColors.White, PixelAt(bmp, 350, 450));
    }

    [Fact]
    public void PsXObject_Raster_DecliningPlugin_FallsBackToThePlaceholder()
    {
        var plugin = new DecliningPlugin();
        PdfPluginHost.Instance.Register(plugin);
        var kinds = CaptureDiagnostics();

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Equal(1, plugin.CallCount);
        Assert.True(InkCount(bmp, 105, 405, 295, 495) > 0, "the placeholder did not run.");
        Assert.DoesNotContain(new SKColor(0, 0, 255), AllPixels(bmp));
        Assert.Contains(RenderDiagnosticKind.PostScriptUnsupported, kinds);
    }

    [Fact]
    public void PsXObject_Raster_ThrowingPlugin_IsReportedAndFallsBackToThePlaceholder()
    {
        PdfPluginHost.Instance.Register(new ThrowingPlugin());
        var kinds = CaptureDiagnostics();

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Contains(RenderDiagnosticKind.PluginFailure, kinds);
        Assert.Contains(RenderDiagnosticKind.PostScriptUnsupported, kinds);
        Assert.True(InkCount(bmp, 105, 405, 295, 495) > 0, "the placeholder did not run.");
    }

    /// <summary>
    /// ISO 32000-1 Table 91 does not require a <c>/BBox</c> on a PS XObject, and the placeholder
    /// returns early without one — so the plugin hook has to run BEFORE that check. Without this
    /// test, moving the hook below the <c>/BBox</c> read would break the feature invisibly.
    /// </summary>
    [Fact]
    public void PsXObject_Raster_WithNoBBox_StillReachesThePlugin()
    {
        var plugin = new FakePostScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject(includeBBox: false));

        Assert.Equal(1, plugin.CallCount);
        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 140, 440));
    }

    [Fact]
    public void PsXObject_Raster_PluginContentIsClippedToTheBBox()
    {
        // BBox is [0 0 200 100] at (100,400) → page-space [100,400]-[300,500]. The plugin paints
        // a 400-wide band, which must be cut off at x=300.
        PdfPluginHost.Instance.Register(
            new FakePostScriptPlugin(content: "0 0 1 rg 10 10 400 60 re f"));

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 290, 440));
        Assert.Equal(SKColors.White, PixelAt(bmp, 320, 440));
    }

    [Fact]
    public void PsXObject_Raster_OversizedContentStream_IsRefusedAndReported()
    {
        PdfPluginHost.Instance.Register(new OversizedContentPlugin());
        var kinds = CaptureDiagnostics();

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Contains(RenderDiagnosticKind.PluginFailure, kinds);
        Assert.True(InkCount(bmp, 105, 405, 295, 495) > 0, "the placeholder did not run.");
    }

    [Fact]
    public void PsXObject_Raster_OversizedResourceGraph_IsRefusedAndReported()
    {
        PdfPluginHost.Instance.Register(new OversizedResourcePlugin());
        var kinds = CaptureDiagnostics();

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Contains(RenderDiagnosticKind.PluginFailure, kinds);
        // Refused output means the sentinel never painted.
        Assert.DoesNotContain(new SKColor(0, 0, 255), AllPixels(bmp));
    }

    /// <summary>
    /// A resource graph deeper than <see cref="PdfLimits.MaxObjectGraphDepth"/> is refused even when
    /// it is far under the entry budget — depth is the second bound, not implied by the first.
    /// </summary>
    [Fact]
    public void PsXObject_Raster_OverDeepResourceGraph_IsRefusedAndReported()
    {
        PdfPluginHost.Instance.Register(new DeepResourcePlugin());
        var kinds = CaptureDiagnostics();

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Contains(RenderDiagnosticKind.PluginFailure, kinds);
        Assert.DoesNotContain(new SKColor(0, 0, 255), AllPixels(bmp));
    }

    /// <summary>
    /// A plugin that declines passes the request to the next one rather than ending the search —
    /// the second plugin's sentinel is painted, and the first was asked.
    /// </summary>
    [Fact]
    public void PsXObject_Raster_ADecliningPluginPassesToTheNext()
    {
        var declining = new DecliningPlugin();
        PdfPluginHost.Instance.Register(declining);
        PdfPluginHost.Instance.Register(new FakePostScriptPlugin("Test.Second"));

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Equal(1, declining.CallCount);
        Assert.Contains(new SKColor(0, 0, 255), AllPixels(bmp));
    }

    /// <summary>
    /// A plugin whose output re-invokes the same PS XObject must terminate, bounded by the shared
    /// <see cref="PdfLimits.MaxRenderRecursionDepth"/> counter rather than recursing forever.
    /// </summary>
    [Fact]
    public void PsXObject_Raster_RecursivePluginOutput_TerminatesAtTheDepthCap()
    {
        var plugin = new RecursivePlugin();
        PdfPluginHost.Instance.Register(plugin);

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.InRange(plugin.CallCount, 1, PdfLimits.MaxRenderRecursionDepth + 1);
        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 140, 440));
    }

    /// <summary>The request must carry the context a real interpreter needs.</summary>
    [Fact]
    public void PsXObject_Raster_RequestCarriesSourceStreamAndPositiveDeviceScale()
    {
        var plugin = new FakePostScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        var req = plugin.LastRequest;
        Assert.Equal(PostScriptSource.XObject, req.Source);
        Assert.Equal("% this would be PostScript code", Encoding.ASCII.GetString(req.PostScript));
        Assert.NotNull(req.XObject);
        Assert.Equal(612f, req.PageWidth);
        Assert.Equal(792f, req.PageHeight);
        Assert.True(req.DeviceScale > 0f, "the raster target must report a real device scale.");
        // The CTM at the Do is `1 0 0 1 100 400 cm`.
        Assert.Equal(100f, req.Ctm[4]);
        Assert.Equal(400f, req.Ctm[5]);
    }

    /// <summary>
    /// <c>/Level1</c> (Table 91) is the alternate stream for a Level-1 RIP. A plugin that can only
    /// handle Level 1 needs it, so the host must decode and pass it.
    /// </summary>
    [Fact]
    public void PsXObject_Raster_Level1AlternateStream_IsDecodedAndPassedToThePlugin()
    {
        var plugin = new FakePostScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject(level1: "% level 1 variant"));

        Assert.NotNull(plugin.LastRequest.Level1PostScript);
        Assert.Equal("% level 1 variant",
            Encoding.ASCII.GetString(plugin.LastRequest.Level1PostScript));
    }

    [Fact]
    public void PsXObject_Raster_NoLevel1Entry_LeavesLevel1Null()
    {
        var plugin = new FakePostScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using var bmp = RenderToBitmap(BuildPdfWithPsXObject());

        Assert.Null(plugin.LastRequest.Level1PostScript);
    }

    // ==================================================================
    //  /Subtype /PS XObject — SVG
    // ==================================================================

    [Fact]
    public void PsXObject_Svg_EmitsThePluginsPathAndNotThePlaceholderMarker()
    {
        PdfPluginHost.Instance.Register(new FakePostScriptPlugin());

        string svg = RenderToSvg(BuildPdfWithPsXObject());

        Assert.DoesNotContain("data-postscript-placeholder", svg);
        Assert.Contains("<path", svg);
        Assert.Contains("rgb(0,0,255)", svg);
    }

    [Fact]
    public void PsXObject_Svg_NoPlugin_StillEmitsThePlaceholderMarker()
    {
        string svg = RenderToSvg(BuildPdfWithPsXObject());
        Assert.Contains("data-postscript-placeholder", svg);
    }

    // ==================================================================
    //  "No plugin registered" vs "every plugin declined"
    // ==================================================================

    /// <summary>
    /// The two states emit different messages: one message for both — "No IPostScriptPlugin handled
    /// a /Subtype /PS XObject" — would send an operator whose registered plugin had correctly declined
    /// at <c>DeviceScale 0</c> (a fact about the target, not about the plugin being missing) re-checking
    /// registration.
    /// </summary>
    [Fact]
    public void NoPluginRegistered_ReportsTheMissingPackageRatherThanAGenericMessage()
    {
        PluginPostScriptTranslator.ResetNoPluginReporting();
        var reported = Capture(RenderDiagnosticKind.PostScriptUnsupported);

        RenderToBitmap(BuildPdfWithPsXObject()).Dispose();

        Assert.Contains(reported, m => m.Contains("No IPostScriptPlugin is registered")
                                    && m.Contains("Tetitulz.Pdf.Plugins.PostScript"));
        Assert.DoesNotContain(reported, m => m.Contains("declined"));
    }

    /// <summary>
    /// The other state: a plugin IS registered and declined. The report names it and the
    /// DeviceScale it declined at.
    /// </summary>
    [Fact]
    public void EveryPluginDeclined_ReportsThePluginNamesAndTheDeviceScale()
    {
        PdfPluginHost.Instance.Register(new DecliningPlugin());
        PluginPostScriptTranslator.ResetNoPluginReporting();
        var reported = Capture(RenderDiagnosticKind.PostScriptUnsupported);

        RenderToBitmap(BuildPdfWithPsXObject()).Dispose();

        Assert.Contains(reported, m => m.Contains("declined") && m.Contains("Test.Declining")
                                    && m.Contains("DeviceScale"));
        Assert.DoesNotContain(reported, m => m.Contains("No IPostScriptPlugin is registered"));
    }

    /// <summary>
    /// The SVG exporter shares the one translator, so it says the same thing - which is the point
    /// of reporting from the translator rather than from each caller.
    /// </summary>
    [Fact]
    public void TheSvgExporterMakesTheSameDistinction()
    {
        PdfPluginHost.Instance.Register(new DecliningPlugin());
        PluginPostScriptTranslator.ResetNoPluginReporting();
        var reported = Capture(RenderDiagnosticKind.PostScriptUnsupported);

        RenderToSvg(BuildPdfWithPsXObject());

        Assert.Contains(reported, m => m.Contains("declined") && m.Contains("Test.Declining"));
        Assert.DoesNotContain(reported, m => m.Contains("No IPostScriptPlugin is registered"));
    }

    /// <summary>Collects the details of one diagnostic kind raised on this thread.</summary>
    private List<string> Capture(RenderDiagnosticKind kind)
    {
        var messages = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        _subscription = (k, detail, ex) =>
        {
            // Filtered to this thread: the sink is process-wide.
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (k == kind) messages.Add(detail ?? string.Empty);
        };
        RenderDiagnostics.OnEvent += _subscription;
        return messages;
    }

    [Fact]
    public void PsXObject_Svg_DecliningPlugin_FallsBackToThePlaceholderMarker()
    {
        PdfPluginHost.Instance.Register(new DecliningPlugin());

        string svg = RenderToSvg(BuildPdfWithPsXObject());

        Assert.Contains("data-postscript-placeholder", svg);
        Assert.DoesNotContain("rgb(0,0,255)", svg);
    }

    /// <summary>
    /// SVG output is resolution-independent, so <c>DeviceScale</c> must be 0 — that is the signal
    /// a rasterizing plugin uses to decline instead of guessing a DPI.
    /// </summary>
    [Fact]
    public void PsXObject_Svg_RequestReportsZeroDeviceScale()
    {
        var plugin = new FakePostScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);

        RenderToSvg(BuildPdfWithPsXObject());

        Assert.Equal(0f, plugin.LastRequest.DeviceScale);
    }

    [Fact]
    public void PsXObject_Svg_PluginContentIsWrappedInABBoxClip()
    {
        PdfPluginHost.Instance.Register(new FakePostScriptPlugin());

        string svg = RenderToSvg(BuildPdfWithPsXObject());

        Assert.Contains("psclip", svg);
        Assert.Contains("clip-path=\"url(#psclip", svg);
    }

    // ==================================================================
    //  Inline `(...) PS` operator
    // ==================================================================

    [Fact]
    public void InlinePsOperator_Raster_WithPlugin_PaintsThePluginsContent()
    {
        var plugin = new FakePostScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using var bmp = RenderToBitmap(BuildPdfMinimal("(pscode) PS"));

        Assert.Equal(1, plugin.CallCount);
        Assert.Equal(PostScriptSource.InlineOperator, plugin.LastRequest.Source);
        Assert.Equal("pscode", Encoding.ASCII.GetString(plugin.LastRequest.PostScript));
        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 40, 40));
    }

    /// <summary>
    /// The operator must not leak graphics state: the plugin sets a blue non-stroking colour, and
    /// the fill that follows the `PS` operator must still be the red the page asked for.
    /// </summary>
    [Fact]
    public void InlinePsOperator_Raster_DoesNotLeakGraphicsStateIntoTheRestOfThePage()
    {
        PdfPluginHost.Instance.Register(new FakePostScriptPlugin());

        using var bmp = RenderToBitmap(
            BuildPdfMinimal("(pscode) PS 1 0 0 rg 200 200 100 100 re f"));

        Assert.Equal(new SKColor(255, 0, 0), PixelAt(bmp, 250, 250));
    }

    /// <summary>
    /// A content stream may carry hundreds of `PS` operators; the "nobody handled it" diagnostic
    /// is one-shot so it cannot drown a host's telemetry.
    /// </summary>
    [Fact]
    public void InlinePsOperator_Raster_NoPlugin_ReportsExactlyOnceForManyOperators()
    {
        // The "no plugin is registered" report is one-shot PER THREAD (a configuration fact, not a
        // property of this page), and this collection runs on one thread - so it has to be re-armed
        // or a neighbour's render will already have consumed it and this measures nothing.
        PluginPostScriptTranslator.ResetNoPluginReporting();
        var kinds = CaptureDiagnostics();

        using var bmp = RenderToBitmap(BuildPdfMinimal(
            "(a) PS (b) PS (c) PS (d) PS 1 0 0 rg 200 200 100 100 re f"));

        Assert.Equal(1, CountOf(kinds, RenderDiagnosticKind.PostScriptUnsupported));
        // Still a no-op for the rest of the page.
        Assert.Equal(new SKColor(255, 0, 0), PixelAt(bmp, 250, 250));
    }

    [Fact]
    public void InlinePsOperator_Svg_WithPlugin_EmitsThePluginsContent()
    {
        var plugin = new FakePostScriptPlugin();
        PdfPluginHost.Instance.Register(plugin);

        string svg = RenderToSvg(BuildPdfMinimal("(pscode) PS"));

        Assert.Equal(1, plugin.CallCount);
        Assert.Contains("rgb(0,0,255)", svg);
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static SKBitmap RenderToBitmap(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static string RenderToSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>Pixel at PDF user-space (x, yUser), i.e. with the y axis flipped.</summary>
    private static SKColor PixelAt(SKBitmap bmp, int x, int yUser)
        => bmp.GetPixel(x, bmp.Height - 1 - yUser);

    private static IEnumerable<SKColor> AllPixels(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
                yield return bmp.GetPixel(x, y);
    }

    private static int InkCount(SKBitmap bmp, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int x = x0; x < x1; x += 3)
            for (int y = y0; y < y1; y += 3)
            {
                var c = PixelAt(bmp, x, y);
                if (c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240)) n++;
            }
        return n;
    }

    private List<RenderDiagnosticKind> CaptureDiagnostics()
    {
        var kinds = new List<RenderDiagnosticKind>();
        _subscription = (k, _, _) => { lock (kinds) kinds.Add(k); };
        RenderDiagnostics.OnEvent += _subscription;
        return kinds;
    }

    private static int CountOf(List<RenderDiagnosticKind> kinds, RenderDiagnosticKind kind)
    {
        int n = 0;
        lock (kinds) foreach (var k in kinds) if (k == kind) n++;
        return n;
    }

    private static byte[] BuildPdfMinimal(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ==================================================================
    //  Table 91 resource selection
    // ==================================================================

    /// <summary>
    /// When the plugin returns no resources, a <c>/Subtype /PS</c> XObject's content runs against
    /// the XObject's OWN <c>/Resources</c> — not the page's.
    /// </summary>
    /// <remarks>
    /// <b>Nothing asserted this in either renderer.</b> Both had written the Table 91 selection out
    /// by hand (and only the raster copy carried a comment saying what rule it was), and replacing
    /// the "use the XObject's own /Resources" branch with "use the page's" left the whole Base
    /// suite green — so folding the two into one shared rule would have been a deletion wearing a
    /// merge's clothes.
    ///
    /// <para>The fixture makes the two answers different colours: the page and the XObject each
    /// define a form named <c>/Marker</c>, red on the page and blue on the XObject, and the plugin
    /// returns nothing but <c>/Marker Do</c>. Which dictionary was chosen is then simply which
    /// colour the page is.</para>
    /// </remarks>
    [Fact]
    public void PsXObjectWithoutPluginResources_UsesItsOwnResourcesNotThePages()
    {
        PdfPluginHost.Instance.Register(new FakePostScriptPlugin(content: "/Marker Do"));

        using var bmp = PixelAssert.Render(ShadowedMarkerPdf());
        var c = PixelAssert.At(bmp, bmp.Width / 2, bmp.Height / 2);

        Assert.True(c.Blue > 200 && c.Red < 60,
            $"the PS XObject's own /Resources define /Marker as BLUE; the page's define it as red. Got {c}");
    }

    /// <summary>The SVG exporter must choose the same dictionary, for the same reason.</summary>
    [Fact]
    public void PsXObjectWithoutPluginResources_SvgAgreesWithTheRaster()
    {
        PdfPluginHost.Instance.Register(new FakePostScriptPlugin(content: "/Marker Do"));

        string svg = RenderToSvg(ShadowedMarkerPdf());

        Assert.Contains("rgb(0,0,255)", svg);
        Assert.DoesNotContain("rgb(255,0,0)", svg);
    }

    /// <summary>
    /// The other half of Table 91: resources the plugin RETURNS replace the inherited dictionary
    /// entirely, so its own <c>/Marker</c> wins over both the XObject's and the page's.
    /// </summary>
    [Fact]
    public void PluginSuppliedResourcesReplaceBothTheXObjectsAndThePages()
    {
        PdfPluginHost.Instance.Register(new GreenMarkerResourcePlugin());

        using var bmp = PixelAssert.Render(ShadowedMarkerPdf());
        var c = PixelAssert.At(bmp, bmp.Width / 2, bmp.Height / 2);

        Assert.True(c.Green > 200 && c.Red < 60 && c.Blue < 60,
            $"plugin-supplied resources must replace the inherited ones. Got {c}");
    }

    /// <summary>Returns <c>/Marker Do</c> plus its own /Marker form, painting green.</summary>
    private sealed class GreenMarkerResourcePlugin : FakePostScriptPlugin
    {
        public GreenMarkerResourcePlugin() : base("Test.GreenRes", "/Marker Do") { }

        public override bool TryConvertToPdfContent(PostScriptContentRequest request,
                                                    out PostScriptContentResult result)
        {
            var form = new PdfStream { RawData = Encoding.ASCII.GetBytes("0 1 0 rg 0 0 200 200 re f") };
            form.Set(PdfName.Type, PdfName.XObject);
            form.Set(PdfName.Subtype, new PdfName("Form"));
            form.Set(PdfName.BBox, new PdfArray(new PdfObject[]
            {
                new PdfNumber(0), new PdfNumber(0), new PdfNumber(200), new PdfNumber(200),
            }));

            var xo = new PdfDictionary();
            xo.Set(new PdfName("Marker"), form);
            var res = new PdfDictionary();
            res.Set(PdfName.XObject, xo);

            result = new PostScriptContentResult
            {
                ContentStream = Encoding.ASCII.GetBytes("/Marker Do"),
                Resources = res,
            };
            return true;
        }
    }

    /// <summary>
    /// A 200x200 page where the name <c>/Marker</c> means two different forms: RED in the page's
    /// resources and BLUE in the PS XObject's own. Only the dictionary actually selected can
    /// decide the colour, which is what makes this fixture able to fail.
    /// </summary>
    private static byte[] ShadowedMarkerPdf()
    {
        const string psContent = "% PostScript the plugin will replace";
        const string cs = "q /PsXo Do Q";
        const string blueForm = "0 0 1 rg 0 0 200 200 re f";
        const string redForm = "1 0 0 rg 0 0 200 200 re f";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
                  " /Resources << /XObject << /PsXo 5 0 R /Marker 7 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /PS /BBox [0 0 200 200]" +
            " /Resources << /XObject << /Marker 6 0 R >> >> /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            psContent.Length, psContent);
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            blueForm.Length, blueForm);
        int o7 = sb.Length;
        sb.AppendFormat(
            "7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            redForm.Length, redForm);

        int xref = sb.Length;
        sb.Append("xref\n0 8\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3, o4, o5, o6, o7 })
            sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A page whose only content is `q 1 0 0 1 100 400 cm /PsXo Do Q`, with /PsXo a
    /// /Subtype /PS XObject. Mirrors PostScriptAndMiscGapsTests' fixture so the placeholder
    /// assertions here and there describe the same geometry.
    /// </summary>
    private static byte[] BuildPdfWithPsXObject(bool includeBBox = true, string level1 = null)
    {
        const string psContent = "% this would be PostScript code";
        const string cs = "q 1 0 0 1 100 400 cm /PsXo Do Q";
        string bbox   = includeBBox ? " /BBox [0 0 200 100]" : "";
        string lvl1Ref = level1 != null ? " /Level1 6 0 R" : "";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /PS{0}{1} /Length {2} >>\nstream\n{3}\nendstream\nendobj\n",
            bbox, lvl1Ref, psContent.Length, psContent);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /XObject << /PsXo 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o6 = sb.Length;
        if (level1 != null)
            sb.AppendFormat("6 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
                level1.Length, level1);

        int count = level1 != null ? 7 : 6;
        int xref  = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", count);
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        if (level1 != null) sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\n", count);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
