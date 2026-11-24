// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Constants;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// With <c>HtmlToPdfOptions.SvgRasterizer</c> set, each distinct SVG markup is rasterised once and its raster shared by
/// every occurrence, and what the rasteriser returns is held to <c>PdfLimits.MaxHtmlRasterizedSvgBytes</c>.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class SvgRasterizeOnceTests
{
    private static string Svg(int size) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"{size}\" height=\"{size}\"/></svg>";

    /// <summary>Thirty occurrences of one SVG are one rasterisation and one image object.</summary>
    [Fact]
    public void AdversarialRepeatedSvg_IsRasterisedOnce()
    {
        int calls = 0;
        var o = Managed();
        o.SvgRasterizer = _ => { calls++; return MakeRgbPng(4, 4); };

        byte[] pdf = ToPdf("<p>" + string.Concat(Enumerable.Repeat(Svg(5), 30)) + "</p>", o);

        Assert.Equal(1, calls);
        Assert.Equal(1, CountOf(Raw(pdf), "/Subtype /Image"));
    }

    /// <summary>
    /// Five distinct SVGs under a budget of two and a half rasters: two are drawn, the cut is said once, and the SVGs
    /// after it are not rasterised. The control draws all five.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdversarialDistinctSvgs_AreHeldToTheRasterBudget(bool capped)
    {
        byte[] png = MakeRgbPng(4, 4);
        int calls = 0;
        var said = new List<BuilderDiagnostic>();
        var o = Managed();
        o.SvgRasterizer = _ => { calls++; return MakeRgbPng(4, 4); };
        o.OnDiagnostic = d => said.Add(d);
        try
        {
            if (capped) PdfLimits.MaxHtmlRasterizedSvgBytes = png.Length * 5L / 2;
            byte[] pdf = ToPdf("<p>" + string.Concat(Enumerable.Range(1, 5).Select(Svg)) + "</p>", o);

            Assert.Equal(capped ? 3 : 5, calls);
            Assert.Equal(capped ? 2 : 5, CountOf(Raw(pdf), "/Subtype /Image"));
            Assert.Equal(capped ? 1 : 0, said.Count(d => d.Detail.Contains("PdfLimits.MaxHtmlRasterizedSvgBytes", StringComparison.Ordinal)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    private static int CountOf(string text, string what)
    {
        int n = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
