// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §11.6.5.2: outside its group's <c>/BBox</c> a soft mask takes the backdrop's value — the
/// luminosity of <c>/BC</c>, or an alpha mask's zero — through <c>/TR</c>, whatever route the paint
/// takes. A blue square filled under a mask whose group covers the page's lower-left quarter is
/// masked out in the other three quarters under an alpha mask, Normal or Multiply alike; shown
/// there under a luminosity mask with a white backdrop, and under an alpha mask whose <c>/TR</c>
/// inverts — which masks the group's own quarter out instead.
/// </summary>
public sealed class SoftMaskOutsideValueTests
{
    [Theory]
    [InlineData("/S /Alpha", "", "/Normal", false, true)]
    [InlineData("/S /Alpha", "", "/Multiply", false, true)]
    [InlineData("/S /Luminosity /BC [1]", "/CS /DeviceGray", "/Normal", true, true)]
    [InlineData("/S /Alpha /TR << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>", "", "/Normal", true, false)]
    public void OutsideTheGroupsBox_TheMaskIsTheBackdropsValue(string mask, string groupSpace, string blend, bool outsideShown, bool insideShown)
    {
        string groupContent = mask.Contains("Luminosity") ? "" : "0 0 50 50 re f";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /ExtGState << /GS 5 0 R >> >> >>",
            Stream("", "/GS gs 0 0 1 rg 0 0 100 100 re f"),
            "<< /Type /ExtGState /SMask << " + mask + " /G 6 0 R >> /BM " + blend + " >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 50 50] /Group << /S /Transparency " + groupSpace + " >>", groupContent),
        };

        using SKBitmap bmp = PixelAssert.Render(Pdf(bodies));
        SKColor outside = PixelAssert.At(bmp, 75, 75), inside = PixelAssert.At(bmp, 25, 25);
        Assert.Equal(outsideShown, IsBlue(outside));
        Assert.Equal(insideShown, IsBlue(inside));
        Assert.Equal(!outsideShown, PixelAssert.IsWhite(outside));
        Assert.Equal(!insideShown, PixelAssert.IsWhite(inside));
    }

    private static bool IsBlue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;

    private static string Stream(string dict, string content) =>
        "<< " + dict + " /Length " + content.Length + " >>\nstream\n" + content + "\nendstream";

    private static byte[] Pdf(IReadOnlyList<string> bodies)
    {
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
