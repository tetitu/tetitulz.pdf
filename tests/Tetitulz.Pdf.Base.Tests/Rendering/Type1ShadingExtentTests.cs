// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.7.4.5.2: a function-based shading paints its <c>/Domain</c>, mapped through its
/// <c>/Matrix</c>, and nothing outside it. A black shading over [30, 70]² leaves (10, 10) white
/// where a clamped sample grid flooded the clip with the domain's edge colour; (50, 50) is the control.
/// </summary>
public sealed class Type1ShadingExtentTests
{
    [Fact]
    public void AFunctionShading_PaintsItsDomainAlone()
    {
        const string content = "/Sh1 sh";
        const string fn = "{pop pop 0}";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /ShadingType 1 /ColorSpace /DeviceGray /Domain [0 1 0 1] /Matrix [40 0 0 40 30 30] /Function 6 0 R >>",
            "<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1] /Length " + fn.Length + " >>\nstream\n" + fn + "\nendstream",
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

        using SKBitmap bmp = PixelAssert.Render(Encoding.ASCII.GetBytes(sb.ToString()));
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 50, 50)), "the domain was not painted");
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 10, 10)), "the shading painted outside its domain");
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 90, 90)), "the shading painted outside its domain");
    }
}
