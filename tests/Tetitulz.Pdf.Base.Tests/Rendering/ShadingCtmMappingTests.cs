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
/// Axial (§8.7.4.5.3) and radial (§8.7.4.5.4) shadings must be mapped through the full CTM.
///
/// <para><b>What was wrong.</b> Both built their Skia gradient from <i>CTM-transformed control
/// points</i>. That is only equivalent to transforming the gradient for a similarity transform
/// (translation / rotation / uniform scale). The radial case then patched up a non-uniform scale
/// by averaging the two CTM axis lengths into one scalar radius and re-stretching the result
/// along X and Y — which reproduces an <i>axis-aligned</i> ellipse but cannot express a rotated
/// one, so a sheared or rotated-then-scaled CTM drew the ellipse at the wrong angle. The axial
/// case had the matching defect: a gradient's iso-lines are perpendicular to its axis only in
/// shading space, and endpoint transformation forces them to stay perpendicular in device space.</para>
///
/// <para>Both now hand the CTM to Skia as the shader's local matrix, so the gradient itself is
/// transformed. A shear CTM is used below because it is precisely the case the old averaging
/// could not represent — an axis-aligned non-uniform scale came out correct either way, which is
/// why this went unnoticed.</para>
/// </summary>
public class ShadingCtmMappingTests
{
    // Shear: (x, y) → (x + y, y).  Chosen because scaleX = 1 but scaleY = √2, so the old
    // code's averaged radius (≈1.207) and axis-aligned re-stretch land visibly off the truth.
    private const string ShearCtm = "1 0 1 1 0 0 cm";

    /// <summary>
    /// A radial shading under a shear CTM paints a <i>tilted</i> ellipse. Two probes straddle the
    /// difference: one point inside the true ellipse that the old axis-aligned approximation left
    /// unpainted, and one point outside the true ellipse that it painted.
    /// </summary>
    [Fact]
    public void RadialShading_ShearedCtm_PaintsTheTiltedEllipseNotAnAxisAlignedOne()
    {
        // Constant-red disc, centre (100,100) r=50 in shading space, no /Extend — so the painted
        // region *is* the transformed disc and "red vs white" reads its shape directly.
        byte[] pdf = BuildShadingPdf(
            "<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [100 100 0 100 100 50] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> " +
            "/Extend [false false] >>");

        using var bmp = Render(pdf);

        // Probe A — shading-space polar (r=40, θ=45°) = (128.3, 128.3): inside the r=50 disc.
        // Device x = 128.3 + 128.3 = 256.6, canvas row = 300 − 128.3 = 171.7.
        // Old approximation: ellipse centred (200,200) with semi-axes 50.0 × 70.7, axis-aligned;
        // (56.6/50)² + (28.3/70.7)² = 1.44 > 1 → it left this pixel white.
        SKColor insideTrue = bmp.GetPixel(256, 171);
        Assert.True(insideTrue.Red > 200 && insideTrue.Green < 80 && insideTrue.Blue < 80,
            $"a point inside the sheared disc should be red, got {insideTrue}");

        // Probe B — shading-space polar (r=60, θ=135°) = (57.6, 142.4): outside the r=50 disc.
        // Device x = 200, canvas row = 300 − 142.4 = 157.6.
        // Old approximation: (0/50)² + (42.4/70.7)² = 0.36 < 1 → it painted this pixel red.
        SKColor outsideTrue = bmp.GetPixel(200, 157);
        Assert.True(outsideTrue.Red > 200 && outsideTrue.Green > 200 && outsideTrue.Blue > 200,
            $"a point outside the sheared disc should be unpainted, got {outsideTrue}");
    }

    /// <summary>
    /// An axial shading's iso-lines are perpendicular to its axis in <i>shading</i> space. Under a
    /// shear they must tilt; transforming only the two endpoints keeps them perpendicular in
    /// device space and so reads the wrong parameter at a given pixel.
    /// </summary>
    [Fact]
    public void AxialShading_ShearedCtm_IsoLinesFollowTheShear()
    {
        // Red→blue along the shading-space X axis, 0 → 100.
        byte[] pdf = BuildShadingPdf(
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Domain [0 1] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> " +
            "/Extend [true true] >>");

        using var bmp = Render(pdf);

        // Shading-space (50, 100) → t = 0.5 → mid purple.  Device x = 50 + 100 = 150,
        // canvas row = 300 − 100 = 200.
        // Old code mapped the axis to device x ∈ [0,100], so device x=150 read t = 1.5,
        // clamped to 1 → saturated blue with no red left in it.
        SKColor mid = bmp.GetPixel(150, 200);
        Assert.True(mid.Red > 90 && mid.Red < 165,
            $"t should be ≈0.5 (red ≈128) at the sheared midpoint, got {mid}");
        Assert.True(mid.Blue > 90 && mid.Blue < 165,
            $"t should be ≈0.5 (blue ≈128) at the sheared midpoint, got {mid}");
    }

    // ----------------------------------------------------------------

    private static SKBitmap Render(byte[] pdf)
    {
        // 1 pt = 1 px so the arithmetic in the assertions above is the pixel grid.
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static byte[] BuildShadingPdf(string shadingDict)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(pdf.Length); pdf.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] " +
            "/Resources << /Shading << /Sh 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        string c = $"q {ShearCtm} /Sh sh Q";
        Obj($"4 0 obj\n<< /Length {c.Length} >>\nstream\n{c}\nendstream\nendobj\n");
        Obj($"5 0 obj\n{shadingDict}\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
