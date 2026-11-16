// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// An image plugin receives its dictionary with indirectly-written entries <b>already resolved</b>,
/// because by contract it has no document to resolve them with.
/// </summary>
/// <remarks>
/// <para><c>IImageDecodePlugin.TryDecode</c> takes a <c>PdfDictionary</c> and no
/// <c>PdfDocument</c>. So <c>/Width 9 0 R</c> — legal under §7.3.10 — read as absent inside
/// <i>every</i> image plugin. The JPEG 2000 decoder then fell back to <c>/Height</c>, and if that
/// was indirect too it returned false: an image that simply does not appear, with no
/// diagnostic.</para>
///
/// <para><b>Fixed at the dispatch point rather than in the plugin, and rather than by widening the
/// contract.</b> <c>PluginImageDecoder</c> is the single dispatch both renderers share, so one copy
/// fixes every plugin — and no plugin author is handed a breaking signature change for a problem
/// that is not theirs to solve.</para>
///
/// <para>Asserted on what the plugin is <i>handed</i>, via a recording plugin, because that is the
/// contract under test. Asserting on rendered pixels would additionally require a working JPEG 2000
/// codestream, which tests nothing about this seam.</para>
/// </remarks>
// Registers an image-decode recorder on the process-wide host, so it belongs in the collection
// that serializes every class touching it.
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class PluginDictResolutionTests
{
    /// <summary>
    /// A plugin sees the resolved <c>/Width</c>, not the reference. The direct spelling is the
    /// control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Width 7
    [InlineData(true)]   // /Width 9 0 R
    public void ThePluginSeesTheResolvedWidth_WhicheverWayItIsSpelled(bool indirect)
    {
        var recorder = new RecordingDecodePlugin();
        PdfPluginHost.Instance.Register(recorder);
        try
        {
            byte[] pdf = ImagePdf(indirect);
            using var ms = new System.IO.MemoryStream(pdf);
            Base.Rendering.PdfPageRenderer.RenderPage(ms, 0, 72);

            Assert.True(recorder.Seen != null, "the plugin was never asked to decode");
            Assert.Equal(7, (recorder.Seen[PdfName.Width] as PdfNumber)?.IntValue);
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(recorder.Name);
        }
    }

    /// <summary>
    /// An <c>IImageDecodePlugin</c> that records the dictionary it is handed and declines, so the
    /// renderer falls through to its built-in path and the page still renders.
    /// </summary>
    private sealed class RecordingDecodePlugin : IImageDecodePlugin
    {
        public string Name => "recording-decode-probe";
        public string Description => "Records the image dictionary handed to a decode plugin.";
        public Version Version => new Version(1, 0, 0);

        public PdfDictionary Seen { get; private set; }

        public void Initialize() { }
        public void Shutdown() { }

        public IReadOnlyList<string> SupportedFilters => new[] { "JPXDecode" };

        public bool TryDecode(string filterName, byte[] data, PdfDictionary imageDictionary,
            out int width, out int height, out int components, out byte[] pixels)
        {
            Seen = imageDictionary;
            width = height = components = 0;
            pixels = null;
            return false;   // decline: the built-in path takes over and the page still renders
        }
    }

    /// <summary>
    /// A JPEG 2000 image reaches a JPXDecode plugin once — from <c>JpxImageDecoder</c>, after the
    /// in-house decoder declines — and not a second time from the generic plugin dispatch, which
    /// skips that filter so the plugin is never asked ahead of the decoder or twice.
    /// </summary>
    [Fact]
    public void AJpegTwoThousandImage_IsOfferedToItsPluginOnce()
    {
        var plugin = new CountingDecodePlugin("counting-jpx-probe", "JPXDecode");
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            using var ms = new System.IO.MemoryStream(ImagePdf(indirect: false));
            Base.Rendering.PdfPageRenderer.RenderPage(ms, 0, 72);
            Assert.Equal(1, plugin.Calls);
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }
    }

    /// <summary>
    /// The generic dispatch, called directly with a JPX image, does not offer it to the JPX plugin
    /// at all: that is <c>JpxImageDecoder</c>'s to do, after the in-house decoder. Through a render
    /// the renderer reaches JPX first, so only a direct call shows the skip.
    /// </summary>
    [Fact]
    public void DecodeToBitmap_LeavesJpegTwoThousandToItsOwnDecoder()
    {
        var plugin = new CountingDecodePlugin("direct-jpx-probe", "JPXDecode");
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            var image = new PdfDictionary();
            image.Set(PdfName.Filter, new PdfName("JPXDecode"));
            image.Set(PdfName.Width, new PdfNumber(1));
            image.Set(PdfName.Height, new PdfNumber(1));
            using var bmp = Base.Images.PluginImageDecoder.DecodeToBitmap(new byte[] { 0 }, image, null,
                SkiaSharp.SKAlphaType.Premul, out _);
            Assert.Null(bmp);
            Assert.Equal(0, plugin.Calls);
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }
    }

    /// <summary>
    /// <c>HasDecoderFor</c> answers for the generic dispatch, which a JPXDecode plugin is not part
    /// of; a plugin for any other filter is.
    /// </summary>
    [Fact]
    public void HasDecoderFor_DoesNotCountAJpegTwoThousandPlugin_ButCountsAnyOther()
    {
        var jpx = new CountingDecodePlugin("jpx-only-probe", "JPXDecode");
        var other = new CountingDecodePlugin("other-filter-probe", "ProbeDecode");
        PdfPluginHost.Instance.Register(jpx);
        PdfPluginHost.Instance.Register(other);
        try
        {
            var jpxImage = new PdfDictionary();
            jpxImage.Set(PdfName.Filter, new PdfName("JPXDecode"));
            Assert.False(Base.Images.PluginImageDecoder.HasDecoderFor(jpxImage, null));

            var otherImage = new PdfDictionary();
            otherImage.Set(PdfName.Filter, new PdfName("ProbeDecode"));
            Assert.True(Base.Images.PluginImageDecoder.HasDecoderFor(otherImage, null));
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(jpx.Name);
            PdfPluginHost.Instance.Unregister(other.Name);
        }
    }

    /// <summary>An inline image names its filters under the abbreviated <c>/F</c>; <c>/Filter</c> wins when both are present.</summary>
    [Fact]
    public void GetImageFilterNames_ReadsTheAbbreviatedKey()
    {
        var inline = new PdfDictionary();
        inline.Set(PdfName.F, new PdfArray { new PdfName("AHx"), new PdfName("DCT") });
        Assert.Equal(new[] { "AHx", "DCT" }, Base.Images.PluginImageDecoder.GetImageFilterNames(inline));

        inline.Set(PdfName.Filter, new PdfName("FlateDecode"));
        Assert.Equal(new[] { "FlateDecode" }, Base.Images.PluginImageDecoder.GetImageFilterNames(inline));
    }

    /// <summary>A decode plugin for the given filters that counts the calls and always declines.</summary>
    private sealed class CountingDecodePlugin : IImageDecodePlugin
    {
        private readonly string[] _filters;
        public CountingDecodePlugin(string name, params string[] filters) { Name = name; _filters = filters; }

        public string Name { get; }
        public string Description => "Counts decode requests.";
        public Version Version => new Version(1, 0, 0);
        public int Calls { get; private set; }

        public void Initialize() { }
        public void Shutdown() { }

        public IReadOnlyList<string> SupportedFilters => _filters;

        public bool TryDecode(string filterName, byte[] data, PdfDictionary imageDictionary,
            out int width, out int height, out int components, out byte[] pixels)
        {
            Calls++;
            width = height = components = 0;
            pixels = null;
            return false;
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A page drawing a JPXDecode image whose <c>/Width</c> is 7. Object 9 holds the number when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] ImagePdf(bool indirect)
    {
        string width = indirect ? "9 0 R" : "7";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 0 cm /Im1 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width " + width + " /Height 1"
                   + " /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode",
                   "0000000"),
            "null", "null", "null",
            "7");
    }

    private static string Stream(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "endstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
