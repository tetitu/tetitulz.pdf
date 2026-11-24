// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A shading's <c>/BBox</c> is clipped as the <b>quadrilateral</b> the CTM maps it to (§8.7.4.3).
/// </summary>
/// <remarks>
/// <para><b>The axis-aligned box of two transformed corners is only the right answer when the
/// matrix has no rotation or skew.</b> At 45° the two opposite corners of a square map to points
/// sharing an x, so such a rect would have <i>zero width</i> and the shading paint nothing at all;
/// at other angles it would merely be too small.</para>
///
/// <para><b>Both renderers clip the quadrilateral</b>: the SVG writer's <c>EmitBBoxClip</c> emits a
/// closed one, because a rotated or skewed matrix maps the box to a parallelogram an
/// <c>x/y/width/height</c> rect cannot express. A rect on one side would be this repository's
/// most-repeated rendering defect, and the reason the two renderers are asserted against each
/// other rather than each against itself.</para>
/// </remarks>
public class ShadingBBoxRotationTests
{
    /// <summary>The control: with no rotation, the bounded shading paints.</summary>
    /// <remarks>
    /// Establishes that the fixture paints at all, so "nothing painted" under rotation is about the
    /// rotation rather than about the shading never having worked.
    /// </remarks>
    [Fact]
    public void AnUnrotatedBoundedShadingPaints()
    {
        int ink = Ink(rotationDegrees: 0);

        Assert.True(ink > 200,
            $"the control painted only {ink} pixels, so this fixture cannot detect a lost clip");
    }

    /// <summary>
    /// And so does a rotated one — the case whose clip collapsed to zero width.
    /// </summary>
    [Fact]
    public void ARotatedBoundedShadingStillPaints()
    {
        int ink = Ink(rotationDegrees: 45);

        Assert.True(ink > 200,
            $"a 45° rotated shading painted only {ink} pixels: the /BBox clip was fitted as an "
          + "axis-aligned rect to two transformed corners, which at this angle has zero width");
    }

    /// <summary>
    /// The acceptance control: the <c>/BBox</c> still <b>bounds</b> the paint rather than being
    /// ignored.
    /// </summary>
    /// <remarks>
    /// Without this, deleting the clip entirely would satisfy both tests above — and a shading with
    /// a <c>/BBox</c> would flood the whole page, which is the defect the clip was added for.
    /// </remarks>
    [Fact]
    public void TheBoundingBoxStillLimitsThePaint()
    {
        int bounded = Ink(rotationDegrees: 0);
        int whole   = Ink(rotationDegrees: 0, omitBBox: true);

        Assert.True(whole > bounded + 2000,
            $"the /BBox did not limit anything: {bounded} pixels bounded against {whole} unbounded");
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>Pixels the shading actually painted, counted as any non-white ink.</summary>
    private static int Ink(int rotationDegrees, bool omitBBox = false)
    {
        byte[] png = PdfOperations.RenderPageToPng(
            new MemoryStream(Pdf(rotationDegrees, omitBBox)), 0, dpi: 72);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        int n = 0;
        for (int y = 0; y < bmp!.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240)) n++;
            }
        return n;
    }

    private static byte[] Pdf(int rotationDegrees, bool omitBBox)
    {
        double rad = rotationDegrees * Math.PI / 180.0;
        float ca = (float)Math.Cos(rad), sa = (float)Math.Sin(rad);

        // Rotate about the page centre so the rotated box stays on the page.
        string cm = $"{F(ca)} {F(sa)} {F(-sa)} {F(ca)} 150 150 cm";
        string bbox = omitBBox ? "" : "/BBox [-60 -60 60 60] ";

        string shading =
            "<< /ShadingType 2 /ColorSpace /DeviceRGB " + bbox +
            "/Coords [-60 0 60 0] /Extend [true true] /Function << /FunctionType 2 " +
            "/Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>";

        string content = $"q {cm} /Sh1 sh Q";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            "/Resources << /Shading << /Sh1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            shading,
        };

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n')
           .Append("0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static string F(float v) =>
        v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
}
