// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The LOW findings of the 2026-08 assembly scan that live in <c>Base/Rendering</c>,
/// <c>Base/Images</c> and <c>Base/Scripting</c>.
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class AssemblyScanLowTests : IDisposable
{
    public AssemblyScanLowTests() => PdfPluginHost.Instance.ResetForTesting();
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    // ==================================================================
    //  A declining JPXDecode plugin is asked once, not twice
    // ==================================================================

    /// <summary>
    /// An image declaring <c>/Filter /JPXDecode</c> is offered to the plugin by
    /// <c>TryDecodeViaPlugin</c>. When the plugin declined, control fell to the JP2/J2C
    /// <i>sniffing</i> branch, which asked the same plugin the same question again — so a plugin
    /// that parses the codestream header before declining did that work twice per image, and one
    /// that throws produced two identical <c>PluginFailure</c> events for one image. The sniffing
    /// branch's own comment said it exists only for a codestream the dictionary does <b>not</b>
    /// declare; nothing implemented the restriction.
    /// </summary>
    [Fact]
    public void ADecliningJpxPlugin_IsAskedOncePerImage_NotTwice()
    {
        var plugin = new CountingJpxPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using (var ms = new MemoryStream(JpxImagePdf()))
            PdfPageRenderer.RenderPage(ms, 0, 72);

        Assert.Equal(1, plugin.CallCount);
    }

    /// <summary>
    /// The SVG exporter shares the decision, so it must ask once too — the "one renderer fixed,
    /// the other silently disagreeing" defect class this repository keeps finding.
    /// </summary>
    [Fact]
    public void TheSvgExporterAlsoAsksADecliningJpxPluginOnce()
    {
        var plugin = new CountingJpxPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using (var ms = new MemoryStream(JpxImagePdf()))
            PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.Equal(1, plugin.CallCount);
    }

    /// <summary>
    /// The control that keeps the two above from passing on a page that never reached the plugin
    /// at all: an image whose dictionary does <b>not</b> declare JPXDecode still reaches it through
    /// the sniffing branch, which is the case that branch exists for.
    /// </summary>
    [Fact]
    public void AnUndeclaredJpxCodestream_StillReachesThePluginThroughSniffing()
    {
        var plugin = new CountingJpxPlugin();
        PdfPluginHost.Instance.Register(plugin);

        using (var ms = new MemoryStream(JpxImagePdf(declareFilter: false)))
            PdfPageRenderer.RenderPage(ms, 0, 72);

        Assert.Equal(1, plugin.CallCount);
    }

    // ==================================================================
    //  PluginJavaScriptRunner.LastError
    // ==================================================================

    /// <summary>
    /// When one plugin throws and a later one runs the script, <c>TryExecute</c> returned true with
    /// <c>LastError</c> still holding the first plugin's exception text — contradicting the
    /// property's documented contract, so a caller following it logged a failure for a script that
    /// had executed and mutated the form.
    /// </summary>
    [Fact]
    public void ASucceedingPluginClearsAnEarlierPluginsError()
    {
        PdfPluginHost.Instance.Register(new ThrowingJsPlugin());
        PdfPluginHost.Instance.Register(new SucceedingJsPlugin());

        bool ran = PluginJavaScriptRunner.TryExecute("1+1", new PdfJsExecutionContext());

        Assert.True(ran);
        Assert.Null(PluginJavaScriptRunner.LastError);
    }

    /// <summary>
    /// The control: when nothing runs the script the throwing plugin's reason is still what the
    /// caller is told, so the failure path is not silent.
    /// </summary>
    [Fact]
    public void AThrowingPluginsReasonSurvivesWhenNothingRunsTheScript()
    {
        PdfPluginHost.Instance.Register(new ThrowingJsPlugin());

        bool ran = PluginJavaScriptRunner.TryExecute("1+1", new PdfJsExecutionContext());

        Assert.False(ran);
        Assert.Contains("ThrowingJs", PluginJavaScriptRunner.LastError);
    }

    // ==================================================================
    //  Fakes
    // ==================================================================

    private sealed class CountingJpxPlugin : IImageDecodePlugin
    {
        public string Name => "Test.CountingJpx";
        public string Description => "counts how often it is offered an image";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public int CallCount { get; private set; }

        public IReadOnlyList<string> SupportedFilters { get; } = new[] { "JPXDecode" };

        public bool CanDecode(string filterName) =>
            string.Equals(filterName, "JPXDecode", StringComparison.Ordinal);

        public bool TryDecode(string filterName, byte[] data, PdfDictionary imageDict,
            out int width, out int height, out int components, out byte[] pixels)
        {
            CallCount++;
            width = height = components = 0;
            pixels = null;
            return false;   // declines, after doing the "header work" the finding describes
        }
    }

    private sealed class ThrowingJsPlugin : IJavaScriptPlugin
    {
        public string Name => "ThrowingJs";
        public string Description => "throws on every script";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool TryExecute(string script, PdfJsExecutionContext context) =>
            throw new NotSupportedException("unsupported construct");
    }

    private sealed class SucceedingJsPlugin : IJavaScriptPlugin
    {
        public string Name => "SucceedingJs";
        public string Description => "runs every script";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool TryExecute(string script, PdfJsExecutionContext context) => true;
    }

    // ==================================================================
    //  Fixtures
    // ==================================================================

    /// <summary>
    /// A page drawing a JP2-signature image. The bytes are not a decodable codestream — no codec
    /// accepts them — which is exactly the "plugin declines" path under test.
    /// </summary>
    private static byte[] JpxImagePdf(bool declareFilter = true)
    {
        // The 12-byte JP2 signature box, so JpxHeader.IsJpx sniffs it as JPEG 2000.
        byte[] jpx =
        {
            0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A,
            0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
        };

        string filter = declareFilter ? "/Filter /JPXDecode " : "";
        var image = new List<byte>();
        image.AddRange(Encoding.Latin1.GetBytes(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 4 /Height 4 " +
            $"/ColorSpace /DeviceRGB /BitsPerComponent 8 {filter}/Length {jpx.Length} >>\nstream\n"));
        image.AddRange(jpx);
        image.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));

        return Pdf("/MediaBox [0 0 100 100] /Resources << /XObject << /Im0 5 0 R >> >>",
                   "q 20 0 0 20 10 10 cm /Im0 Do Q\n", image.ToArray());
    }

    private static byte[] Pdf(string pageExtras, string content, byte[] rawObject)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R {pageExtras} /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets.Add(acc.Count); acc.AddRange(rawObject);

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
