// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the SVG renderer's shading rasterization: function (type 1) and mesh / Coons /
/// tensor (types 4-7) shadings — which have no faithful SVG primitive — are rendered through
/// the full raster shading pipeline and embedded as an &lt;image&gt;, replacing the old coarse
/// rect-grid / single-averaged-rect approximations.  Axial / radial (types 2-3) remain native
/// SVG gradients (this also guards that they are NOT rasterized).
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class SvgShadingRasterizationTests
{
    [Fact]
    public void FunctionShading_Type1_RasterizedToImage()
    {
        byte[] pdf = BuildPdfWithType1FunctionShading();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // The function shading is now embedded as a raster image, not a grid of <rect>s.
        Assert.Contains("<image", svg);
        Assert.Contains("data:image/png;base64,", svg);
    }

    /// <summary>
    /// Each rasterised shading is an offscreen render of the page's size, charged to the page's
    /// pixels as the raster renderer charges its own. Nothing counted them, so a content stream
    /// repeating one <c>sh</c> allocated, painted and encoded that many page-sized bitmaps. With
    /// three pages' worth of pixels, ten <c>sh</c> operators rasterise three times and the rest
    /// paint nothing; under the default ceiling all ten do.
    /// </summary>
    [Fact]
    public void AdversarialRepeatedFunctionShading_IsChargedToThePagesOffscreenPixels()
    {
        string tenShadings = string.Concat(System.Linq.Enumerable.Repeat("q 200 0 0 200 100 100 cm /Sh1 sh Q\n", 10));
        byte[] pdf = BuildPdfWithType1FunctionShading(tenShadings);

        string unbounded = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        string bounded;
        try
        {
            Core.Constants.PdfLimits.MaxOffscreenPixelsPerPage = 3L * 612 * 792;
            bounded = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        }
        finally { Core.Constants.PdfLimits.ResetToDefaults(); }

        Assert.Equal(10, Occurrences(unbounded, "<image"));
        Assert.Equal(3, Occurrences(bounded, "<image"));
        // Refused, not approximated: no flat rectangle, no grid of them.
        Assert.DoesNotContain("data-sh-type", bounded);
        Assert.Equal(Occurrences(unbounded, "<rect"), Occurrences(bounded, "<rect"));
    }

    private static int Occurrences(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, System.StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(what, i + what.Length, System.StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void AxialShading_Type2_StaysNativeGradient_NotRasterized()
    {
        byte[] pdf = BuildPdfWithAxialShading();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // Axial gradients keep the scalable native-SVG path …
        Assert.Contains("linearGradient", svg);
        // … and must not be turned into a raster <image>.
        Assert.DoesNotContain("<image", svg);
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    // Type-1 function shading driven by a Type-4 PostScript function that always returns red.
    // The shading is scaled to a 200×200 region so it covers a visible area.
    private static byte[] BuildPdfWithType1FunctionShading(string content = "q 200 0 0 200 100 100 cm /Sh1 sh Q")
    {
        const string fnPs   = "{ pop pop 1 0 0 }";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]" +
            " /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", fnPs.Length, fnPs);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /ShadingType 1 /ColorSpace /DeviceRGB" +
                  " /Domain [0 1 0 1] /Function 6 0 R >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Shading << /Sh1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithAxialShading()
    {
        const string content = "q 200 0 0 200 100 100 cm /Sh1 sh Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1]" +
                  " /N 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB" +
                  " /Coords [0 0 1 1] /Function 6 0 R /Extend [true true] >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Shading << /Sh1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
