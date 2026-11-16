// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG export turns a <c>/Rotate</c> page as the raster does: a quarter turn clockwise mapping
/// the (crop) box onto the swapped viewBox. The root group's transform is composed here and applied
/// to the drawn square's corners, which must land where the raster's ink is: a 40-point square at
/// the top-left of a landscape page at <c>/Rotate 90</c> is at the top-right of the portrait view,
/// and one at a cropped page's bottom-left at <c>/Rotate 180</c> at the view's top-right.
/// </summary>
public class SvgRotatedPageGeometryTests
{
    [Theory]
    [InlineData(90, "", "0 60 40 40 re f", 60, 100, 0, 40)]
    [InlineData(180, "/CropBox [36 36 200 100]", "36 36 40 40 re f", 124, 164, 0, 40)]
    [InlineData(270, "", "0 60 40 40 re f", 0, 40, 160, 200)]
    [InlineData(0, "", "0 60 40 40 re f", 0, 40, 0, 40)]
    public void TheSquare_LandsWhereTheRasterPaintsIt(int rotate, string cropBox, string content,
        float x0, float x1, float y0, float y1)
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(rotate, cropBox, content)), 0);
        XDocument doc = XDocument.Parse(svg);

        XElement root = doc.Root!;
        XElement group = root.Elements().First(e => e.Name.LocalName == "g");
        double[] m = Transform(group.Attribute("transform")!.Value);
        XElement path = doc.Descendants().First(e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none");
        var points = Regex.Matches(path.Attribute("d")!.Value, @"(-?\d+(?:\.\d+)?),(-?\d+(?:\.\d+)?)")
            .Select(mm => (x: double.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture), y: double.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture)))
            .Select(p => (x: m[0] * p.x + m[2] * p.y + m[4], y: m[1] * p.x + m[3] * p.y + m[5])).ToList();

        Assert.Equal(x0, points.Min(p => p.x), 1);
        Assert.Equal(x1, points.Max(p => p.x), 1);
        Assert.Equal(y0, points.Min(p => p.y), 1);
        Assert.Equal(y1, points.Max(p => p.y), 1);

        // Inside the viewBox, which is the displayed page.
        double w = double.Parse(root.Attribute("width")!.Value, CultureInfo.InvariantCulture);
        double h = double.Parse(root.Attribute("height")!.Value, CultureInfo.InvariantCulture);
        Assert.True(points.All(p => p.x >= -0.5 && p.x <= w + 0.5 && p.y >= -0.5 && p.y <= h + 0.5), "the square lies off the viewBox");
    }

    /// <summary>An SVG transform list composed into one matrix (a b c d e f), later items applied first.</summary>
    private static double[] Transform(string list)
    {
        double[] result = { 1, 0, 0, 1, 0, 0 };
        foreach (Match item in Regex.Matches(list, @"(\w+)\(([^)]*)\)"))
        {
            double[] n = item.Groups[2].Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            double[] t = item.Groups[1].Value switch
            {
                "translate" => new[] { 1, 0, 0, 1, n[0], n.Length > 1 ? n[1] : 0 },
                "scale" => new[] { n[0], 0, 0, n.Length > 1 ? n[1] : n[0], 0, 0 },
                "matrix" => n,
                "rotate" => Rotate(n),
                _ => throw new InvalidOperationException("unknown transform " + item.Value),
            };
            result = Multiply(result, t);
        }
        return result;
    }

    private static double[] Rotate(double[] n)
    {
        double r = n[0] * Math.PI / 180, cx = n.Length > 2 ? n[1] : 0, cy = n.Length > 2 ? n[2] : 0;
        double[] rot = { Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r), 0, 0 };
        return Multiply(Multiply(new[] { 1, 0, 0, 1, cx, cy }, rot), new[] { 1, 0, 0, 1, -cx, -cy });
    }

    /// <summary>The matrix applying <paramref name="first"/> then <paramref name="second"/> to a point: second ∘ first in SVG's column convention.</summary>
    private static double[] Multiply(double[] outer, double[] inner) => new[]
    {
        outer[0] * inner[0] + outer[2] * inner[1],
        outer[1] * inner[0] + outer[3] * inner[1],
        outer[0] * inner[2] + outer[2] * inner[3],
        outer[1] * inner[2] + outer[3] * inner[3],
        outer[0] * inner[4] + outer[2] * inner[5] + outer[4],
        outer[1] * inner[4] + outer[3] * inner[5] + outer[5],
    };

    private static byte[] Pdf(int rotate, string cropBox, string content)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] {cropBox} /Rotate {rotate} /Contents 4 0 R /Resources << >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
