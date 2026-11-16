// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An uncoloured pattern's cells are painted in the <c>scn</c> components converted through the
/// Pattern space's underlying space (§8.7.3.3, <see cref="PatternUnderlyingColor"/>), and that space
/// may be named from the resources (§8.6.6.2, <see cref="ColorSpaceResources"/>) — in both renderers.
/// The reference renderer paints each fixture the spot's red (its 237/28/36 is its CMYK profile; ours is the
/// naive DeviceCMYK conversion, 255/0/0).
/// </summary>
public class UncolouredPatternColourTests
{
    private const string Spot = "[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>]";

    /// <summary>
    /// A spot underlying space, written inline or named from the resources: the cells are the
    /// spot's red in both renderers. The raster renderer painted white and the SVG writer grey —
    /// the family name alone, with no tint transform — and a named space was never looked up.
    /// The DeviceRGB row is the control both always got right.
    /// </summary>
    [Theory]
    [InlineData(true, "[/Pattern " + Spot + "]", "1")]
    [InlineData(false, "[/Pattern " + Spot + "]", "1")]
    [InlineData(true, "[/Pattern /CS0]", "1")]
    [InlineData(false, "[/Pattern /CS0]", "1")]
    [InlineData(true, "[/Pattern /DeviceRGB]", "1 0 0")]
    [InlineData(false, "[/Pattern /DeviceRGB]", "1 0 0")]
    public void TheCells_AreTheUnderlyingSpacesColour(bool raster, string patternSpace, string comps)
    {
        byte[] pdf = Pdf(patternSpace, comps);
        SKColor c;
        if (raster)
        {
            var page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
            int i = 20 * page.Stride + 50 * 4;
            c = new SKColor(page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
        }
        else
        {
            // The pattern tile's own path carries the cell colour.
            string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
            string fill = XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "pattern")
                .SelectMany(p => p.Descendants()).Select(e => e.Attribute("fill")?.Value)
                .First(f => f != null && f.StartsWith("rgb(", StringComparison.Ordinal));
            byte[] v = fill.Substring(4, fill.Length - 5).Split(',').Select(byte.Parse).ToArray();
            c = new SKColor(v[0], v[1], v[2]);
        }
        Assert.True(c.Red >= 254 && c.Green <= 1 && c.Blue <= 1, $"{(raster ? "raster" : "svg")} {patternSpace}: {c}, expected the spot's red");
    }

    private static byte[] Pdf(string patternSpace, string comps)
    {
        string content = "/PCS cs " + comps + " /P scn 0 0 100 40 re f";
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 40] /Resources << /ColorSpace << /CS0 " + Spot + " /PCS " + patternSpace +
            " >> /Pattern << /P 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Length 14 >>\nstream\n0 0 10 10 re f\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++) { offs[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
