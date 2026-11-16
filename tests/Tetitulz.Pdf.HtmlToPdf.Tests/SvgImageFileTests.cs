// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// An <c>&lt;img&gt;</c> whose <b>file</b> is SVG — local or remote — reaches the opt-in
/// <see cref="HtmlToPdfOptions.SvgRasterizer"/> as a <c>data:image/svg+xml</c> URI already did
/// (batch 96); it was reported undecodable. The bytes decide, not the file name: a PNG called
/// <c>icon.svg</c> is a PNG, and an SVG called <c>logo.png</c> is an SVG. With no rasterizer the
/// image is reported as SVG left out, where it was reported as a decode failure.
/// </summary>
public class SvgImageFileTests
{
    private const string Svg = "<?xml version=\"1.0\"?>\n<!-- logo -->\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg>";

    private sealed class ScriptedLoader : IRemoteResourceLoader
    {
        private readonly Func<Uri, byte[]?> _body;
        public ScriptedLoader(Func<Uri, byte[]?> body) => _body = body;
        public byte[]? Load(Uri url, out string? failure) { failure = null; return _body(url); }
    }

    private static (byte[] Pdf, string? Markup, List<string> Reports) Convert(string html, bool rasterizer, Action<HtmlToPdfOptions> configure)
    {
        string? seen = null;
        var reports = new List<string>();
        var o = Managed();
        o.OnDiagnostic = d => { lock (reports) reports.Add(d.Detail ?? ""); };
        if (rasterizer) o.SvgRasterizer = markup => { seen = markup; return MakeRgbPng(4, 4); };
        configure(o);
        return (ToPdf(html, o), seen, reports);
    }

    [Fact]
    public void ALocalSvgFile_ReachesTheRasterizer_AndIsEmbedded()
    {
        using var dir = NewScratchDir();
        dir.Write("logo.svg", Encoding.UTF8.GetBytes(Svg));

        var (pdf, markup, reports) = Convert("<img src='logo.svg' width='20' height='20'>", true, o => o.BasePath = dir.Path);

        Assert.NotNull(markup);
        Assert.Contains("<svg", markup, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Image", Raw(pdf), StringComparison.Ordinal);
        Assert.Empty(reports);
    }

    [Fact]
    public void ARemoteSvgFile_ReachesTheRasterizer_AndIsEmbedded()
    {
        var loader = new ScriptedLoader(_ => Encoding.UTF8.GetBytes(Svg));

        var (pdf, markup, reports) = Convert("<img src='https://cdn.example/logo.svg' width='20' height='20'>", true, o => o.RemoteResourceLoader = loader);

        Assert.NotNull(markup);
        Assert.Contains("/Subtype /Image", Raw(pdf), StringComparison.Ordinal);
        Assert.Empty(reports);
    }

    /// <summary>The bytes decide: an SVG named <c>.png</c> is rasterized, a PNG named <c>.svg</c> is embedded as the PNG it is.</summary>
    [Fact]
    public void TheBytesDecide_NotTheFileName()
    {
        using var dir = NewScratchDir();
        dir.Write("logo.png", Encoding.UTF8.GetBytes(Svg));
        dir.Write("icon.svg", MakeRgbPng(4, 4));

        var svgNamedPng = Convert("<img src='logo.png' width='20' height='20'>", true, o => o.BasePath = dir.Path);
        var pngNamedSvg = Convert("<img src='icon.svg' width='20' height='20'>", true, o => o.BasePath = dir.Path);

        Assert.NotNull(svgNamedPng.Markup);
        Assert.Null(pngNamedSvg.Markup);
        Assert.Contains("/Width 4", Raw(pngNamedSvg.Pdf), StringComparison.Ordinal);
    }

    /// <summary>With no rasterizer the SVG is left out and the report says which option to set, not that the image could not be decoded.</summary>
    [Fact]
    public void WithoutARasterizer_TheSvgIsReportedAsLeftOut()
    {
        using var dir = NewScratchDir();
        dir.Write("logo.svg", Encoding.UTF8.GetBytes(Svg));

        var (pdf, _, reports) = Convert("<img src='logo.svg' width='20' height='20'>", false, o => o.BasePath = dir.Path);

        Assert.DoesNotContain("/Subtype /Image", Raw(pdf), StringComparison.Ordinal);
        string report = Assert.Single(reports);
        Assert.Contains("SvgRasterizer", report, StringComparison.Ordinal);
        Assert.DoesNotContain("not embedded", report, StringComparison.Ordinal);
    }

    /// <summary>The sniff, directly: a mark and whitespace first, an XML prologue, a comment; not a PNG, not text, not an empty tag soup.</summary>
    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg'/>", true)]
    [InlineData("﻿  \r\n<?xml version='1.0'?><svg/>", true)]
    [InlineData("<!-- c --><!DOCTYPE svg><svg></svg>", true)]
    [InlineData("<html><body>svg</body></html>", false)]
    [InlineData("x<svg/>", false)]
    [InlineData("svg", false)]
    [InlineData("", false)]
    public void LooksLikeSvg_ReadsTheBytes(string text, bool expected)
    {
        Assert.Equal(expected, HtmlToBuilderMapper.LooksLikeSvg(Encoding.UTF8.GetBytes(text)));
        Assert.False(HtmlToBuilderMapper.LooksLikeSvg(MakeRgbPng(2, 2)));
    }

    /// <summary>The tag is looked for in the first kilobyte only: a document whose <c>&lt;svg</c> opens two kilobytes in is not read as one, and one whose tag sits just inside is.</summary>
    [Fact]
    public void LooksLikeSvg_ReadsOnlyTheFirstKilobyte()
    {
        string far = "<!--" + new string(' ', 2000) + "--><svg/>";
        string near = "<!--" + new string(' ', 900) + "--><svg/>";

        Assert.False(HtmlToBuilderMapper.LooksLikeSvg(Encoding.ASCII.GetBytes(far)));
        Assert.True(HtmlToBuilderMapper.LooksLikeSvg(Encoding.ASCII.GetBytes(near)));
    }
}
