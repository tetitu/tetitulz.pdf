// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Regressions for GAPS.md §2b — the duplicated device-space colour conversions.
/// </summary>
/// <remarks>
/// These cover the two conversions that had no test at all: the SVG annotation-colour path (which
/// emitted out-of-gamut CSS for a file-supplied out-of-range operand) and the exact-inverse
/// RGB→CMYK used by overprint simulation (which must stay the inverse of
/// <c>PdfColor.CmykToRgb</c>, not the GCR variant that shares its old name).
/// </remarks>
public sealed class ColourDuplicationGapTests
{
    // ------------------------------------------------------------------
    //  SVG annotation colour — out-of-range /C operands
    // ------------------------------------------------------------------

    /// <summary>Every rgb(...) triple appearing anywhere in the SVG.</summary>
    private static (int R, int G, int B)[] RgbTriples(string svg)
    {
        // Parse as XML first: a substring scan over malformed output would still "pass".
        var doc = XDocument.Parse(svg);
        string all = string.Join(" ", doc.Descendants()
            .SelectMany(e => e.Attributes())
            .Select(a => a.Value));

        return Regex.Matches(all, @"rgb\(\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\)")
                    .Cast<Match>()
                    .Select(m => (
                        int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
                    .ToArray();
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    [Fact]
    public void AnnotationCmykColour_OutOfRangeOperand_StaysWithinTheRgbGamut()
    {
        // A /C array is file-supplied and nothing validates its range. The CMYK branch used to
        // convert the RAW operands and interpolate the result as `int`, so a negative cyan
        // produced the string "rgb(306,255,255)" — not a valid CSS colour, and not something a
        // pixel comparison against the raster renderer could ever see, because the raster twin
        // (PdfPageRenderer.ReadAnnotColor) clamps inside its own byte helper. GAPS.md §2b.
        byte[] pdf = AnnotationPdf("/C [-0.2 0 0 0]");

        var triples = RgbTriples(RenderSvg(pdf));
        Assert.NotEmpty(triples);
        foreach (var (r, g, b) in triples)
        {
            Assert.InRange(r, 0, 255);
            Assert.InRange(g, 0, 255);
            Assert.InRange(b, 0, 255);
        }
    }

    [Fact]
    public void AnnotationCmykColour_OperandAboveOne_StaysWithinTheRgbGamut()
    {
        // The other end of the range: k > 1 drives (1-k) negative, which unclamped would emit a
        // negative channel.
        byte[] pdf = AnnotationPdf("/C [0 0 0 1.4]");

        foreach (var (r, g, b) in RgbTriples(RenderSvg(pdf)))
        {
            Assert.InRange(r, 0, 255);
            Assert.InRange(g, 0, 255);
            Assert.InRange(b, 0, 255);
        }
    }

    [Fact]
    public void AnnotationCmykColour_InRangeOperand_IsUnchangedByTheClampFix()
    {
        // Guards the clamp against over-reach: clamping must not perturb a legal operand.
        // 0.5 cyan, no black → (1-0.5)*(1-0) = 0.5 → 127.5, which rounds to 128 — the one
        // rounding convention the whole tree's CMYK conversion uses (GAPS.md §2b).
        byte[] pdf = AnnotationPdf("/C [0.5 0 0 0]");

        var triples = RgbTriples(RenderSvg(pdf));
        Assert.Contains(triples, t => t.R == 128 && t.G == 255 && t.B == 255);
    }

    // ------------------------------------------------------------------
    //  Every CMYK entry point must agree
    // ------------------------------------------------------------------

    /// <summary>
    /// Drives one CMYK colour through every path a PDF can reach it by, and asserts they all
    /// produce the same channel value.
    /// </summary>
    /// <remarks>
    /// <para>The operands are chosen to <b>discriminate the rounding convention</b>: cyan 0.5 with
    /// no black gives <c>(1−0.5)(1−0) · 255 = 127.5</c>, which is 128 rounded and 127 truncated.
    /// A site that re-inlines the formula with the other convention fails here regardless of which
    /// convention a reader thinks is right, which is the property worth pinning — the tree held
    /// three at once and each looked locally reasonable (GAPS.md §2b).</para>
    /// <para>Complements the source-level
    /// <c>TestInfrastructure.NoInlineCmykConversionTests</c>: that one catches a new copy being
    /// written, this one catches an existing path being routed somewhere else.</para>
    /// </remarks>
    [Fact]
    public void AllCmykEntryPoints_QuantiseIdentically()
    {
        const byte Expected = 128;   // 127.5 rounded

        // 1. the shared converter itself
        Assert.Equal(Expected, PdfColor.CmykToRgb(0.5f, 0f, 0f, 0f).Red);

        // 2. the component-array converter in the same file (one implementation with the first)
        Assert.Equal(Expected,
            PdfColor.ComponentsToColor(new[] { 0.5f, 0f, 0f, 0f },
                Tetitulz.Pdf.Core.Constants.PdfColorSpaceNames.DeviceCMYK).Red);

        // 3. the `k` operator, raster
        byte[] fillPdf = ContentPdf("0.5 0 0 0 k 100 100 200 200 re f");
        using var raster = PixelAssert.Render(fillPdf);
        Assert.Equal(Expected, PixelAssert.At(raster, 200f, 200f).Red);

        // 4. the `k` operator, SVG
        var svgFill = RgbTriples(RenderSvg(fillPdf));
        Assert.Contains(svgFill, t => t.R == Expected && t.G == 255 && t.B == 255);

        // 5. annotation /C, raster
        byte[] annotPdf = AnnotationPdf("/C [0.5 0 0 0] /IC [0.5 0 0 0]");
        using var annotRaster = PixelAssert.Render(annotPdf);
        Assert.Equal(Expected, PixelAssert.At(annotRaster, 200f, 650f).Red);

        // 6. annotation /C, SVG
        Assert.Contains(RgbTriples(RenderSvg(annotPdf)),
            t => t.R == Expected && t.G == 255 && t.B == 255);
    }

    /// <summary>
    /// The <c>g</c> and <c>rg</c> operators must quantise identically in both renderers, and
    /// identically to the <c>cs</c>/<c>sc</c> route to the same colour.
    /// </summary>
    /// <remarks>
    /// <para>This is the regression test for a divergence the CMYK unification <b>introduced</b>.
    /// The SVG renderer routes <c>g</c>/<c>rg</c> through <c>PdfColor.ComponentsToColor</c>, so
    /// making the device spaces round moved it to 128; the raster renderer open-coded
    /// <c>(byte)(g * 255)</c> in <c>OpSetGrayFill</c> and stayed at 127. Consolidating six of the
    /// nine CMYK sites while leaving the neighbouring grey and RGB sites inline is precisely how
    /// this repo's recurring defect gets *created* rather than fixed.</para>
    /// <para>0.5 is the discriminating operand: 127.5 is 128 rounded, 127 truncated.</para>
    /// </remarks>
    [Theory]
    [InlineData("0.5 g 100 100 200 200 re f")]                       // DeviceGray operator
    [InlineData("/DeviceGray cs 0.5 sc 100 100 200 200 re f")]       // same colour via cs/sc
    [InlineData("0.5 0.5 0.5 rg 100 100 200 200 re f")]              // DeviceRGB operator
    [InlineData("/DeviceRGB cs 0.5 0.5 0.5 sc 100 100 200 200 re f")]
    public void GreyAndRgbOperators_QuantiseIdenticallyInBothRenderers(string content)
    {
        const byte Expected = 128;   // 127.5 rounded

        byte[] pdf = ContentPdf(content);

        using var raster = PixelAssert.Render(pdf);
        Assert.Equal(Expected, PixelAssert.At(raster, 200f, 200f).Red);

        Assert.Contains(RgbTriples(RenderSvg(pdf)),
            t => t.R == Expected && t.G == Expected && t.B == Expected);
    }

    // ------------------------------------------------------------------
    //  Overprint's RGB→CMYK must remain the exact inverse
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]        // white
    [InlineData(0f, 0f, 0f, 1f)]        // black
    [InlineData(1f, 0f, 0f, 0f)]        // cyan
    [InlineData(0f, 1f, 0f, 0f)]        // magenta
    [InlineData(0f, 0f, 1f, 0f)]        // yellow
    [InlineData(0.25f, 0.5f, 0.75f, 0f)]
    [InlineData(1f, 1f, 0f, 0.5f)]
    public void RgbToCmykExactInverse_RoundTripsThroughCmykToRgb(float c, float m, float y, float k)
    {
        // The property the name promises, and the reason overprint cannot use the GCR variant
        // in PdfColorSpaceConverter: a colour that arrived as CMYK and was never blended has to
        // come back as the same CMYK, or overprinting an untouched backdrop would shift it.
        SKColor rgb = PdfColor.CmykToRgb(c, m, y, k);
        OverprintCompositor.RgbToCmykExactInverse(rgb, out float c2, out float m2, out float y2, out float k2);
        SKColor rgb2 = PdfColor.CmykToRgb(c2, m2, y2, k2);

        // Round-tripping through 8-bit RGB is lossy in the components, but must be exact in the
        // colour — which is the property overprint actually depends on.
        Assert.Equal(rgb.Red, rgb2.Red);
        Assert.Equal(rgb.Green, rgb2.Green);
        Assert.Equal(rgb.Blue, rgb2.Blue);
    }

    [Fact]
    public void RgbToCmykExactInverse_PutsNoChromaticInkInPureBlack()
    {
        // The inv <= 0 branch. Without it this divides by zero and yields NaN components,
        // which would then paint as transparent rather than black.
        OverprintCompositor.RgbToCmykExactInverse(new SKColor(0, 0, 0),
            out float c, out float m, out float y, out float k);

        Assert.Equal(1f, k);
        Assert.Equal(0f, c);
        Assert.Equal(0f, m);
        Assert.Equal(0f, y);
    }

    [Fact]
    public void RgbToCmykExactInverse_IsNotTheGcrVariant()
    {
        // The two RGB→CMYK conversions were once both called RgbToCmyk. This pins that they
        // remain different functions: full GCR would pull min(c,m,y) into K, so a mid grey
        // would come back as K-only with no chromatic ink. The exact inverse keeps c=m=y=0
        // for a neutral only because r=g=b makes it so — the discriminating case is a colour
        // whose min(c,m,y) is non-zero.
        OverprintCompositor.RgbToCmykExactInverse(new SKColor(200, 150, 100),
            out float c, out float m, out float y, out float k);

        // max channel is 200/255, so k = 1 - 200/255 and the smallest ink is exactly 0.
        Assert.Equal(1f - 200f / 255f, k, 5);
        Assert.Equal(0f, c, 5);
        Assert.True(m > 0f && y > m,
            $"Expected increasing magenta/yellow for a warm colour; got m={m}, y={y}.");
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>A one-page PDF whose page content stream is <paramref name="content"/>.</summary>
    private static byte[] ContentPdf(string content)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Resources << >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content.Length, content);

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4 })
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A one-page PDF with a single Square annotation carrying <paramref name="colourKeys"/> and
    /// no <c>/AP</c>, so the renderer must synthesise the appearance and reach the colour path.
    /// </summary>
    private static byte[] AnnotationPdf(string colourKeys)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Annot /Subtype /Square /Rect [100 600 300 700] "
                  + colourKeys + " >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Annots [5 0 R] /Resources << >> >>\nendobj\n");

        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o1);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o2);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o3);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o4);
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
