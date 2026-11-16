// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for another reader-inspired rendering improvements:
/// - CalRGB proper gamma+matrix+XYZ→sRGB conversion
/// - Image /Interpolate flag controls filter quality
/// - CIDToGIDMap used for TrueType CIDFont glyph rendering
/// </summary>
public sealed class RenderingGapTests
{
    // ------------------------------------------------------------------
    //  Shared PDF builder
    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream, string extraResources = "",
        string extraObjects = "", int extraObjCount = 0)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        string resources = "/Font << /F1 5 0 R >>" + (extraResources.Length > 0 ? " " + extraResources : "");
        sb.AppendFormat("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                        " /Contents 4 0 R /Resources << {0} >> >>\nendobj\n", resources);
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        if (extraObjects.Length > 0)
            sb.Append(extraObjects);
        int xref = sb.Length;
        int objCount = 6 + extraObjCount;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", objCount);
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size " + objCount + " /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ====================================================================
    //  CalRGB conversion
    // ====================================================================

    /// <summary>
    /// CalRGB pure red through the identity matrix at gamma 1: XYZ (1,0,0) → sRGB clips the red
    /// channel high, drives green negative (→ 0) and leaves a small positive blue.
    ///
    /// <para><b>This test exposed a real defect.</b> Its predecessor spelled that arithmetic out in
    /// a comment, concluded "Result: reddish pixel", and then asserted only that a PNG came back.
    /// The pixel was in fact plain <c>(255, 0, 0)</c> — raw DeviceRGB — because
    /// <c>ResolveColorSpaceName</c> returned the colour-space array only for
    /// Separation/DeviceN/Indexed/ICCBased, and <c>OpSc</c>'s CalRGB branch is guarded on that
    /// array being present. <b>/Gamma, /Matrix and /WhitePoint were silently ignored for all page
    /// content</b>, leaving the correct <c>PdfColor.CalRgbToSRgb</c> dead on this path.</para>
    /// </summary>
    [Fact]
    public void CalRgb_IdentityMatrixGamma1_AppliesTheXyzToSRgbTransform()
    {
        // CalRGB with default matrix and gamma=[1 1 1] should give same result as DeviceRGB.
        // We test the internal CalRgbToSRgb logic via the rendering pipeline:
        // Render a page that sets CalRGB fill color and draws a rectangle, then sample the pixel.
        // CalRGB [/CalRGB << /WhitePoint [0.9505 1.0 1.089] /Gamma [1 1 1] /Matrix [1 0 0 0 1 0 0 0 1] >>]
        // Set fill to pure red (1 0 0) in CalRGB → should produce ~(255, 0, 0) after sRGB gamma.
        // We use sRGB gamma encoding step: with gamma=1 and identity matrix:
        //   A=1, B=0, C=0 → X=1, Y=0, Z=0
        //   Scale to D65: X=0.9505/0.9505*1=1, Y=1/1*0=0, Z=1.089/1.089*0=0
        //   D65 XYZ→sRGB: lr = 3.2404542*1 = 3.2... (clipped to 1) → sRGB = 255
        //                  lg = -0.9692660*1 = neg → 0
        //                  lb = 0.0556434*1 = pos → small
        // Result: reddish pixel. Just verify no exception and a non-black pixel.
        string cs = "q /CS1 cs 1 0 0 sc 0 0 300 300 re f Q";
        string csRes = "/ColorSpace << /CS1 [/CalRGB << /WhitePoint [0.9505 1.0 1.089] /Gamma [1 1 1] /Matrix [1 0 0 0 1 0 0 0 1] >>] >>";
        using var bmp = PixelAssert.Render(BuildPdf(cs, csRes));
        var px = PixelAssert.At(bmp, 150, 150);

        Assert.Equal(255, px.Red);                       // clipped high
        Assert.Equal(0, px.Green);                       // negative → clamped to zero
        Assert.InRange(px.Blue, 1, 120);                 // small positive, not zero and not red

        // The decisive part: it is NOT the raw component triple, which is what a colour space
        // treated as DeviceRGB would produce.
        Assert.False(PixelAssert.Near(px, new SKColor(255, 0, 0), 8),
            $"CalRGB was rendered as raw DeviceRGB: {px}");
    }

    /// <summary>
    /// <c>/Gamma</c> must change the result. Rendering the same components at gamma 1 and gamma 2.2
    /// and asserting the two differ is what makes the parameter's effect observable — the
    /// predecessor rendered only the gamma-2.2 case and asserted nothing, so it passed while the
    /// gamma was being discarded entirely.
    /// </summary>
    [Fact]
    public void CalRgb_Gamma_ChangesTheRenderedColour()
    {
        const string content = "q /CS1 cs 0.5 0.3 0.1 sc 0 0 300 300 re f Q";
        const string white = "/WhitePoint [0.9505 1.0 1.089]";

        using var gamma1 = PixelAssert.Render(BuildPdf(content,
            "/ColorSpace << /CS1 [/CalRGB << " + white + " /Gamma [1 1 1] >>] >>"));
        using var gamma22 = PixelAssert.Render(BuildPdf(content,
            "/ColorSpace << /CS1 [/CalRGB << " + white + " /Gamma [2.2 2.2 2.2] >>] >>"));

        var a = PixelAssert.At(gamma1, 150, 150);
        var b = PixelAssert.At(gamma22, 150, 150);

        Assert.False(PixelAssert.Near(a, b, 8),
            $"gamma 1 and gamma 2.2 produced the same colour ({a}), so /Gamma was ignored");
    }

    /// <summary>
    /// A CalRGB dictionary carrying only a <c>/WhitePoint</c> must fall back to the spec defaults
    /// (gamma 1, identity matrix) — and therefore render <b>identically</b> to an explicit
    /// <c>/Gamma [1 1 1] /Matrix [1 0 0 0 1 0 0 0 1]</c>. Comparing the two renders is what pins
    /// "fell back to the defaults" as opposed to "fell back to doing nothing at all", which the
    /// predecessor's non-empty-PNG check could not distinguish.
    /// </summary>
    [Fact]
    public void CalRgb_OmittedGammaAndMatrix_FallBackToTheSpecDefaults()
    {
        // CalRGB with no parameter dict: should still produce a color without throwing.
        // We build a content stream that uses /CS space but the cs array has no second element.
        // Since it's structurally invalid, the renderer should handle it without throwing.
        const string content = "q /CS1 cs 0.8 0.2 0.4 sc 100 100 200 200 re f Q";
        const string white = "/WhitePoint [0.9505 1.0 1.089]";

        using var defaults = PixelAssert.Render(BuildPdf(content,
            "/ColorSpace << /CS1 [/CalRGB << " + white + " >>] >>"));
        using var explicitParams = PixelAssert.Render(BuildPdf(content,
            "/ColorSpace << /CS1 [/CalRGB << " + white +
            " /Gamma [1 1 1] /Matrix [1 0 0 0 1 0 0 0 1] >>] >>"));

        var a = PixelAssert.At(defaults, 200, 200);
        var b = PixelAssert.At(explicitParams, 200, 200);

        Assert.True(PixelAssert.Near(a, b, 2),
            $"omitting /Gamma and /Matrix should equal the explicit defaults, {a} vs {b}");
        Assert.True(PixelAssert.IsInk(a), "the rectangle should actually be painted");
    }

    // ====================================================================
    //  Image /Interpolate flag
    // ====================================================================

    /// <summary>
    /// <c>/Interpolate true</c> must smooth an upscaled image: a 2x2 checkerboard blown up to the
    /// page yields many intermediate greys along a scanline. Its <c>false</c> sibling below must
    /// yield exactly the two source levels. Neither is visible to a "did not throw" assertion, and
    /// the predecessors' fixtures were drawn small and off to one side where nothing could be
    /// sampled anyway.
    /// </summary>
    [Fact]
    public void ImageInterpolate_True_SmoothsTheUpscaledImage()
    {
        // Build a minimal PDF with a 2×2 image that has /Interpolate true.
        // The renderer should not throw and should produce output.
        // Image: 2x2 DeviceRGB raw pixels (red, green, blue, white)
        using var bmp = PixelAssert.Render(CheckerboardPage(interpolate: true));

        Assert.True(PixelAssert.DistinctLevelsOnRow(bmp, 25) > 20,
            "an interpolated upscale should produce a gradient, not two flat blocks");
    }

    /// <summary>
    /// <c>/Interpolate false</c> (the default) must keep hard edges: only the two source levels
    /// appear along a scanline.
    /// </summary>
    [Fact]
    public void ImageInterpolate_False_KeepsHardPixelEdges()
    {
        using var bmp = PixelAssert.Render(CheckerboardPage(interpolate: false));

        Assert.Equal(2, PixelAssert.DistinctLevelsOnRow(bmp, 25));

        // ...and the checkerboard really is a checkerboard, so "2 levels" is not "all white".
        // Quadrant centres of the 612x792 page: the image's top-left texel is black, top-right white.
        Assert.True(PixelAssert.Near(PixelAssert.At(bmp, 153, 594), SKColors.Black),
            $"top-left quadrant should be black, got {PixelAssert.At(bmp, 153, 594)}");
        Assert.True(PixelAssert.Near(PixelAssert.At(bmp, 459, 594), SKColors.White),
            $"top-right quadrant should be white, got {PixelAssert.At(bmp, 459, 594)}");
    }

    /// <summary>
    /// An inline image with the abbreviated <c>/I true</c> must actually be drawn, in the right
    /// place, in the right colour. The predecessor drew a 1x1 <b>white</b> pixel onto a white page
    /// — invisible by construction, so no pixel assertion could ever have distinguished it from
    /// the image being dropped entirely.
    /// </summary>
    [Fact]
    public void InlineImage_AbbreviatedInterpolate_DrawsTheImage()
    {
        // A mid-grey 1x1 inline image filling the bottom-left 72x72 pt of the page.
        string cs = "q 72 0 0 72 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 /I true /F /AHx ID 80> EI Q";

        using var bmp = PixelAssert.Render(BuildPdf(cs));

        var inside  = PixelAssert.At(bmp, 36, 36);
        var outside = PixelAssert.At(bmp, 300, 300);

        Assert.True(PixelAssert.Near(inside, new SKColor(0x80, 0x80, 0x80), 8),
            $"the inline image should paint mid-grey, got {inside}");
        Assert.True(PixelAssert.IsWhite(outside), "nothing should be painted outside the image");
    }

    // ====================================================================
    //  CIDToGIDMap
    // ====================================================================

    [Fact]
    public void CidToGidMap_FallbackFont_GetCidToGid_ReturnsNull()
    {
        // FallbackFont (and any non-Type0 PdfFont) must return null from GetCidToGid().
        // Verify via PdfDocument.Open: parse a minimal PDF, get a font dict that has no /Subtype.
        byte[] pdf = BuildPdf("q Q");
        using var ms = new MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var fontDict = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        // No /Subtype → PdfFont.Create returns FallbackFont
        var font = Tetitulz.Pdf.Core.Fonts.PdfFont.Create(fontDict, doc);
        Assert.Null(font.GetCidToGid());
    }

    /// <summary>
    /// <c>/CIDToGIDMap /Identity</c> means "CID == GID", which the font layer reports as a
    /// <c>null</c> map (nothing to look up) rather than as a fabricated identity table.
    ///
    /// <para>The predecessor was named for CIDToGIDMap and rendered <c>"q Q"</c> — an <b>empty
    /// page</b> containing no font, no text and no CID anything — then asserted that a PNG came
    /// back. It could not have failed for any reason connected to its name.</para>
    /// </summary>
    [Fact]
    public void CidToGidMap_Identity_ReportsNoExplicitMapping()
    {
        byte[] pdf = BuildPdf("q Q");
        using var ms = new MemoryStream(pdf);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        var descendant = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        descendant.Set(Tetitulz.Pdf.Core.Objects.PdfName.Type,
            new Tetitulz.Pdf.Core.Objects.PdfName("Font"));
        descendant.Set(Tetitulz.Pdf.Core.Objects.PdfName.Subtype,
            new Tetitulz.Pdf.Core.Objects.PdfName("CIDFontType2"));
        descendant.Set(new Tetitulz.Pdf.Core.Objects.PdfName("CIDToGIDMap"),
            new Tetitulz.Pdf.Core.Objects.PdfName("Identity"));

        var kids = new Tetitulz.Pdf.Core.Objects.PdfArray();
        kids.Add(descendant);

        var type0 = new Tetitulz.Pdf.Core.Objects.PdfDictionary();
        type0.Set(Tetitulz.Pdf.Core.Objects.PdfName.Type,
            new Tetitulz.Pdf.Core.Objects.PdfName("Font"));
        type0.Set(Tetitulz.Pdf.Core.Objects.PdfName.Subtype,
            new Tetitulz.Pdf.Core.Objects.PdfName("Type0"));
        type0.Set(new Tetitulz.Pdf.Core.Objects.PdfName("DescendantFonts"), kids);

        var font = PdfFont.Create(type0, doc);

        Assert.Null(font.GetCidToGid());
    }

    // ------------------------------------------------------------------
    //  Helpers for image tests
    // ------------------------------------------------------------------

    private static string BuildRawImageStream(byte[] pixels, int w, int h, bool interpolate)
    {
        // Returns an XObject stream definition as a string chunk to embed in the PDF text.
        // We represent raw pixel data as hex for ASCII embedding.
        var hex = new StringBuilder();
        foreach (byte b in pixels) hex.AppendFormat("{0:X2}", b);
        hex.Append('>'); // end of hex stream
        string interp = interpolate ? "/Interpolate true " : "";
        return $"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
               $"/ColorSpace /DeviceRGB /BitsPerComponent 8 {interp}" +
               $"/Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream\n";
    }

    private static byte[] BuildPdfWithImageXObject(string imageStreamText, string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length; // image XObject
        sb.Append("5 0 obj\n" + imageStreamText + "endobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 6 0 R >>" +
                  " /XObject << /Im1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            contentStream.Length, contentStream);
        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
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


    /// <summary>
    /// A 2x2 black/white checkerboard image scaled to fill the whole page — the smallest fixture in
    /// which "interpolated" and "not interpolated" look different.
    /// </summary>
    private static byte[] CheckerboardPage(bool interpolate)
    {
        byte[] rawPx =
        {
            0,   0,   0,      255, 255, 255,
            255, 255, 255,    0,   0,   0,
        };
        return BuildPdfWithImageXObject(
            BuildRawImageStream(rawPx, 2, 2, interpolate),
            "q 612 0 0 792 0 0 cm /Im1 Do Q");
    }
}
