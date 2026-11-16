// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.7.3.1: a pattern's <c>/Matrix</c> maps pattern space to the default coordinate space of the
/// content stream the pattern is used in — a form's space at its <c>Do</c>, never the CTM at the
/// paint — in both renderers. A tiling pattern used in a form drawn at half size keeps the form's
/// period, five points where the page's was ten; a shading pattern filled under <c>2 0 0 2 0 0 cm</c>
/// still spans the page's hundred points, so the pixel near the right end is the end colour.
/// </summary>
public sealed class PatternBaseSpaceTests
{
    [Fact]
    public void ATilingPatternInsideAScaledForm_TilesInTheFormsSpace()
    {
        byte[] pdf = TilingInFormPdf();

        using SKBitmap bmp = PixelAssert.Render(pdf);
        // The cell paints its left half: at a period of 5 on the page, x = 1.25 and 6.25 are inked
        // and 3.75 is not; at the page's period of 10, 6.25 would be white and 3.75 inked.
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 1.25f, 10.5f)), "the first stripe was not painted");
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 6.25f, 10.5f)), "the second period's stripe was not painted: the pattern keeps the page's period");
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 3.75f, 10.5f)), "the gap between stripes is painted: the pattern keeps the page's period");

        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
        XElement pattern = svg.Descendants().Single(e => e.Name.LocalName == "pattern");
        float[] m = Matrix(pattern.Attribute("patternTransform")!.Value);
        Assert.Equal(0.5f, m[0], 3);
        Assert.Equal(0.5f, m[3], 3);
    }

    [Fact]
    public void AShadingPatternUnderAScaledCtm_SpansThePagesSpace()
    {
        byte[] pdf = ShadingUnderCmPdf();

        using SKBitmap bmp = PixelAssert.Render(pdf);
        SKColor nearTheEnd = PixelAssert.At(bmp, 90, 50);
        Assert.True(nearTheEnd.Blue > nearTheEnd.Red + 100, $"the gradient was stretched by the cm: {nearTheEnd}");
        SKColor nearTheStart = PixelAssert.At(bmp, 10, 50);
        Assert.True(nearTheStart.Red > nearTheStart.Blue + 100, $"the start is not red: {nearTheStart}");

        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
        XElement gradient = svg.Descendants().Single(e => e.Name.LocalName == "linearGradient");
        float[] m = Matrix(gradient.Attribute("gradientTransform")!.Value);
        Assert.Equal(new[] { 1f, 0f, 0f, 1f, 0f, 0f }, m);
    }

    private static float[] Matrix(string transform) =>
        Regex.Match(transform, @"matrix\(([^)]*)\)").Groups[1].Value.Split(',')
            .Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();

    /// <summary>A 100 × 100 page drawing a form at half size; the form fills itself with a 10-unit stripe pattern.</summary>
    private static byte[] TilingInFormPdf()
    {
        const string content = "q 0.5 0 0 0.5 0 0 cm /Fm Do Q";
        const string form = "/Pattern cs /P1 scn 0 0 100 100 re f";
        const string cell = "0 0 5 10 re f";
        return Pdf(content,
            "/Resources << /XObject << /Fm 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Pattern << /P1 6 0 R >> >> /Length " + form.Length + " >>\nstream\n" + form + "\nendstream",
            "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Length " + cell.Length + " >>\nstream\n" + cell + "\nendstream");
    }

    /// <summary>A 100 × 100 page filling a square under a doubling cm with an axial red-to-blue pattern over the page's width.</summary>
    private static byte[] ShadingUnderCmPdf()
    {
        const string content = "2 0 0 2 0 0 cm /Pattern cs /P0 scn 0 0 50 50 re f";
        return Pdf(content,
            "/Resources << /Pattern << /P0 5 0 R >> >>",
            "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> /Extend [true true] >> >>");
    }

    private static byte[] Pdf(string content, string resources, params string[] more)
    {
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " + resources + " >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
        };
        bodies.AddRange(more);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
