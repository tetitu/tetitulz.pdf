// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A shading's /BBox (PDF §8.7.4.3) must limit the painted region of the <c>sh</c>
/// operator. Pre-fix, <c>sh</c> flooded the entire clip and ignored /BBox.
/// </summary>
public class ShadingBBoxTests
{
    [Fact]
    public void ShOperator_BBox_LimitsPaintedRegion()
    {
        // Solid-red axial shading over a 72×72 page, but /BBox [0 0 36 72] = left half only.
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(pdf.Length); pdf.Append(body); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
            "/Resources << /Shading << /Sh 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        string c = "/Sh sh";
        Obj($"4 0 obj\n<< /Length {c.Length} >>\nstream\n{c}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 36 72 36] /Domain [0 1] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> " +
            "/Extend [true true] /BBox [0 0 36 72] >>\nendobj\n");
        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(Encoding.ASCII.GetBytes(pdf.ToString())), 0, 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        SKColor left  = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);     // inside BBox → red
        SKColor right = bmp.GetPixel(3 * bmp.Width / 4, bmp.Height / 2); // outside BBox → page white

        Assert.True(left.Red > 200 && left.Green < 80, $"left (in BBox) should be red, got {left}");
        Assert.True(right.Red > 200 && right.Green > 200 && right.Blue > 200,
            $"right (outside BBox) should be unpainted/white, got {right}");
    }
}
