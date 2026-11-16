// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Html.Tests;

/// <summary>
/// The HTML page container and the PNG inside it are the same size.
///
/// <para><b>Why this exists.</b> The converter rasterizes each page with
/// <see cref="PdfPageRenderer"/> but derived the container's size from its own reading of
/// <c>CropBox ?? MediaBox</c> — a third copy of page geometry that the renderer already owns. The
/// two disagreed on exactly the cases the renderer had been taught and this copy had not, and the
/// symptom was silent: the HTML is well-formed and the image is correct, so the only evidence is
/// that the browser scales one to fit the other.</para>
/// </summary>
public class HtmlPageGeometryTests
{
    /// <summary>
    /// §7.9.5 allows a rectangle written from either corner pair. Subtracting blindly gave a
    /// negative extent, which <c>Max(1, Ceiling(…))</c> turned into a <b>1×1 px</b> container
    /// wrapped around a full-size page image — the whole page squashed into one pixel.
    /// </summary>
    [Fact]
    public void InvertedPageBox_DeclaresTheSameSizeAsTheImageItContains()
    {
        var (declaredW, declaredH, png) = ConvertAndMeasure("/MediaBox [100 100 0 0]");

        Assert.Equal(png.Width,  declaredW);
        Assert.Equal(png.Height, declaredH);
        Assert.True(declaredW > 1, "an inverted box must not collapse the page container");
    }

    /// <summary>
    /// §14.11.1: one default-user-space unit is <c>/UserUnit</c> points, and the renderer folds
    /// that into the device scale. Ignoring it here declared half the size of the image actually
    /// embedded, so the browser scaled the page down by two.
    /// </summary>
    [Fact]
    public void UserUnitPage_DeclaresTheSameSizeAsTheImageItContains()
    {
        var (declaredW, declaredH, png) = ConvertAndMeasure("/MediaBox [0 0 100 100] /UserUnit 2");

        Assert.Equal(png.Width,  declaredW);
        Assert.Equal(png.Height, declaredH);
        Assert.Equal(200, png.Width);   // the renderer's own answer, pinned so the test can't drift
    }

    /// <summary>The ordinary page, as a control: it agreed before and must still agree.</summary>
    [Fact]
    public void PlainPage_DeclaresTheSameSizeAsTheImageItContains()
    {
        var (declaredW, declaredH, png) = ConvertAndMeasure("/MediaBox [0 0 100 100]");

        Assert.Equal(png.Width,  declaredW);
        Assert.Equal(png.Height, declaredH);
        Assert.Equal(100, png.Width);
    }

    // ------------------------------------------------------------------

    private static (int W, int H, SKBitmap Png) ConvertAndMeasure(string pageExtras)
    {
        byte[] pdf = Pdf(pageExtras, "1 0 0 rg 10 10 20 20 re f");
        string html = PdfHtmlConverter.ConvertToHtml(new MemoryStream(pdf),
            new HtmlConversionOptions { IncludeText = false, ImageDpi = 72 });

        var m = Regex.Match(html,
            "<img src=\"data:image/png;base64,([A-Za-z0-9+/=]+)\" width=\"(\\d+)\" height=\"(\\d+)\"");
        Assert.True(m.Success, "the converter should embed a page image with declared dimensions");

        byte[] png = Convert.FromBase64String(m.Groups[1].Value);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), bmp);
    }

    private static byte[] Pdf(string pageExtras, string content)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R {pageExtras} /Contents 4 0 R /Resources << >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
