// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the SVG renderer parity improvements:
/// - Clipping paths (W/W*)
/// - Text render modes (Tr)
/// - Color space tracking (cs/CS + sc/SC)
/// - Blend mode (gs /BM)
/// - Line style attributes (linecap, linejoin, miterlimit, dasharray)
/// - Shading operator (sh — axial/radial approximation)
/// - Marked content (BMC/BDC/EMC)
/// - BX/EX compatibility sections
/// - OCG visibility in SVG output
/// - /IM abbreviation in inline images
/// </summary>
public sealed class SvgRendererGapTests
{
    // ====================================================================
    //  Clipping paths (W / W*)
    // ====================================================================

    [Fact]
    public void ClipPath_W_EmitsClipPathElement()
    {
        // W followed by n should emit a <clipPath> definition in the SVG.
        const string cs = "100 100 200 200 re W n";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("clipPath", svg);
        Assert.Contains("clip-path", svg);
    }

    [Fact]
    public void ClipPath_WStar_EvenOdd_EmitsClipRule()
    {
        const string cs = "100 100 200 200 re W* n";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("clip-rule=\"evenodd\"", svg);
    }

    [Fact]
    public void ClipPath_InsideQQ_ClosedOnRestore()
    {
        // The <g clip-path="..."> group opened by W must be closed on Q.
        const string cs = "q 100 100 200 200 re W n Q";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        // Must contain a matching open and close for the clip group.
        Assert.Contains("<g clip-path=", svg);
        Assert.Contains("</g>", svg);
    }

    // ====================================================================
    //  Text render modes (Tr)
    // ====================================================================

    [Fact]
    public void TextRenderMode_0_FillOnly()
    {
        // Tr 0 = fill only: text element must have fill attribute, no stroke.
        const string cs = "BT /F1 12 Tf 0 0 Td 0 Tr (Hello) Tj ET";
        byte[] pdf = BuildPdfWithFont(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("<text", svg);
        Assert.Contains("fill=", svg);
        Assert.DoesNotContain("stroke=", svg);
    }

    [Fact]
    public void TextRenderMode_1_StrokeOnly()
    {
        // Tr 1 = stroke only: text must have fill="none" and stroke attribute.
        const string cs = "BT /F1 12 Tf 0 0 Td 1 Tr (Hello) Tj ET";
        byte[] pdf = BuildPdfWithFont(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("fill=\"none\"", svg);
        Assert.Contains("stroke=", svg);
    }

    [Fact]
    public void TextRenderMode_3_Invisible_NoTextElement()
    {
        // Tr 3 = invisible: no <text> element should be emitted.
        const string cs = "BT /F1 12 Tf 0 0 Td 3 Tr (Hidden) Tj ET";
        byte[] pdf = BuildPdfWithFont(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.DoesNotContain("<text", svg);
    }

    // ====================================================================
    //  Color space tracking (cs / CS + sc / SC)
    // ====================================================================

    [Fact]
    public void ColorSpace_DeviceGray_Cs_ScSetsGrayFill()
    {
        // cs /DeviceGray sets fill color space; sc 0.5 should produce a grey color.
        const string cs = "/DeviceGray cs 0.5 sc 100 100 200 200 re f";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        // 0.5 * 255 = 127.5, which rounds to 128. This asserted 127 until 2026-08-08, when the
        // device spaces were unified onto the rounding the CIE spaces in PdfColor already used —
        // DeviceGray 0.5 and CalGray 0.5 (gamma 1) are defined to be the same colour, and they
        // came out a level apart. GAPS.md §2b step 4.
        Assert.Contains("rgb(128,128,128)", svg);
    }

    [Fact]
    public void ColorSpace_DeviceCMYK_CS_SCNSetsCmykStroke()
    {
        // CS /DeviceCMYK; 0 0 0 1 SC — pure black stroke.
        const string content = "/DeviceCMYK CS 0 0 0 1 SC 100 100 200 200 re S";
        byte[] pdf = BuildPdf(content);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        // CMYK 0,0,0,1 → rgb(0,0,0) black
        Assert.Contains("stroke=\"rgb(0,0,0)\"", svg);
    }

    // ====================================================================
    //  Blend mode (/BM in ExtGState)
    // ====================================================================

    [Fact]
    public void BlendMode_Multiply_EmitsMixBlendMode()
    {
        // Operands precede operators in PDF content streams: /GS1 gs
        const string extGs = "<< /BM /Multiply >>\n";
        byte[] pdf = BuildPdfWithExtGState(extGs, "/GS1 gs 100 100 200 200 re f");
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("mix-blend-mode:multiply", svg);
    }

    [Fact]
    public void BlendMode_Normal_NoMixBlendMode()
    {
        // Normal is the default — no style attribute needed.
        const string extGs = "<< /BM /Normal >>\n";
        byte[] pdf = BuildPdfWithExtGState(extGs, "/GS1 gs 100 100 200 200 re f");
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.DoesNotContain("mix-blend-mode", svg);
    }

    // ====================================================================
    //  Line style attributes
    // ====================================================================

    [Fact]
    public void LineStyle_RoundCap_EmitsRoundLinecap()
    {
        const string cs = "1 J 2 100 100 m 300 300 l S";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("stroke-linecap=\"round\"", svg);
    }

    [Fact]
    public void LineStyle_BevelJoin_EmitsBevelLinejoin()
    {
        const string cs = "2 j 2 100 100 m 300 100 l 300 300 l S";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("stroke-linejoin=\"bevel\"", svg);
    }

    [Fact]
    public void LineStyle_DashArray_EmitsStrokeDasharray()
    {
        const string cs = "[5 3] 0 d 2 100 100 m 300 300 l S";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("stroke-dasharray=", svg);
    }

    // ====================================================================
    //  Shading operator (sh)
    // ====================================================================

    [Fact]
    public void Shading_Axial_EmitsLinearGradient()
    {
        // /Sh1 sh — operand precedes operator in PDF content streams.
        byte[] pdf = BuildPdfWithAxialShading();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("linearGradient", svg);
    }

    // ====================================================================
    //  Marked content (BMC / BDC / EMC)
    // ====================================================================

    [Fact]
    public void MarkedContent_BMC_EmitsGroup()
    {
        // PDF: /tag BMC — operand precedes operator
        const string cs = "/Span BMC 100 100 200 200 re f EMC";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("data-mc=\"Span\"", svg);
    }

    [Fact]
    public void MarkedContent_BDC_EmitsGroup()
    {
        // PDF: /tag << properties >> BDC — both operands precede operator
        const string cs = "/Artifact << >> BDC 100 100 200 200 re f EMC";
        byte[] pdf = BuildPdf(cs);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains("data-mc=\"Artifact\"", svg);
    }

    // ====================================================================
    //  BX / EX compatibility sections
    // ====================================================================

    [Fact]
    public void BxEx_UnknownOperators_DoNotThrow()
    {
        // Operators between BX and EX that are not in the standard must be ignored.
        const string cs = "BX /FutureOp 1 2 3 EX 100 100 200 200 re f";
        byte[] pdf = BuildPdf(cs);
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
            Assert.Contains("<svg", svg);
        });
        Assert.Null(ex);
    }

    // ====================================================================
    //  /IM abbreviation in inline images
    // ====================================================================

    [Fact]
    public void InlineImage_AbbreviatedIM_SvgRendersWithoutException()
    {
        const string hexPixels = "FF0000 00FF00>";
        string cs =
            "q 200 0 0 100 100 400 cm\n" +
            "BI /W 2 /H 1 /CS /RGB /BPC 8 /F /AHx /IM false\nID\n" + hexPixels + "\nEI Q";
        byte[] pdf = BuildPdf(cs);
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
            Assert.Contains("<svg", svg);
        });
        Assert.Null(ex);
    }

    // ====================================================================
    //  Soft mask (SMask via ExtGState)
    // ====================================================================

    [Fact]
    public void SoftMask_None_ClearsActiveMask()
    {
        // /SMask /None clears any active mask — page must render without exception.
        const string extGs = "<< /SMask /None >>\n";
        byte[] pdf = BuildPdfWithExtGState(extGs, "/GS1 gs 100 100 200 200 re f");
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
            Assert.Contains("<svg", svg);
        });
        Assert.Null(ex);
    }

    // ====================================================================
    //  Pattern color (scn/SCN with pattern name)
    // ====================================================================

    [Fact]
    public void Pattern_ScnWithName_RendersWithoutException()
    {
        // /Pattern cs /P1 scn sets fill to a tiling pattern; path must be filled.
        byte[] pdf = BuildPdfWithTilingPattern();
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
            Assert.Contains("<svg", svg);
        });
        Assert.Null(ex);
    }

    // ====================================================================
    //  ImageMask stencil rendering
    // ====================================================================

    [Fact]
    public void ImageMask_1Bit_RendersWithFillColor()
    {
        // /ImageMask true — 1-bit stencil should be rendered using current fill color.
        // The resulting SVG must be valid and contain an <image> element.
        byte[] pdf = BuildPdfWithImageMask();
        var ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(pdf);
            string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
            Assert.Contains("<image", svg);
        });
        Assert.Null(ex);
    }

    // ====================================================================
    //  Helpers
    // ====================================================================

    private static byte[] BuildPdfWithTilingPattern()
    {
        // Tiling pattern: 10×10 grid of red squares.
        const string patCs = "1 0 0 rg 2 2 6 6 re f";   // pattern content
        const string cs    = "/Pattern cs /P1 scn 0 0 612 792 re f"; // fill page with pattern

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /PatternType 1 /PaintType 1 /TilingType 1" +
            " /BBox [0 0 10 10] /XStep 10 /YStep 10 /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            patCs.Length, patCs);
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithImageMask()
    {
        // 4×4 1-bit image mask (all opaque = 0x00 bytes → painted pixels)
        byte[] maskData = { 0x00, 0x00, 0x00, 0x00 }; // 4 rows × 4 bits each (packed)
        var hexSb = new StringBuilder();
        foreach (byte b in maskData) hexSb.AppendFormat("{0:X2}", b);
        hexSb.Append(">");
        string hexData = hexSb.ToString();

        // Fill color: red; then paint the stencil
        string cs = "1 0 0 rg q 200 0 0 200 100 400 cm /Im1 Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 4 /Height 4" +
            " /ImageMask true /BitsPerComponent 1" +
            " /Filter /ASCIIHexDecode /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            hexData.Length, hexData);
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithFont(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithExtGState(string extGsDict, string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o6 = sb.Length;
        sb.Append("6 0 obj\n" + extGsDict + "endobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /ExtGState << /GS1 6 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
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
        // Object layout:
        //  1 Catalog, 2 Pages, 3 Page, 4 Content, 5 Shading (axial)
        // Operand precedes operator: /Sh1 sh
        const string cs = "/Sh1 sh";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append(
            "5 0 obj\n<< /ShadingType 2 /ColorSpace /DeviceRGB" +
            " /Coords [0 0 612 0]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>" +
            " >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Shading << /Sh1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
