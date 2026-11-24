// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The silent colour deviations found by the 2026-08-06 rendering audit (GAPS.md §2, group 2).
/// Every one produced wrong pixels with no diagnostic and no failing test.
/// </summary>
public sealed class RasterColourCorrectnessTests
{
    // ====================================================================
    //  PdfFunctionEvaluator — four value bugs
    // ====================================================================

    /// <summary>
    /// Table 38: a sampled function's <c>/Decode</c> DEFAULTS TO <c>/Range</c>. Without that the
    /// samples stay in [0,1] and a tint transform into a wide range collapses — a Separation into
    /// Lab returned L ≈ 0…1 (near-black) instead of 0…100.
    /// </summary>
    [Fact]
    public void SampledFunction_AbsentDecode_DefaultsToRange()
    {
        // One input, one output, 2 samples of 8 bits: 0x00 and 0xFF. Range says [0 100].
        var fn = SampledFunction(range: "[0 100]", decode: null, samples: "00FF>");

        float[] lo = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });
        float[] hi = PdfFunctionEvaluator.Evaluate(fn, new[] { 1f });

        Assert.Equal(0f,   lo[0], 3);
        Assert.Equal(100f, hi[0], 3);
    }

    /// <summary>An explicit <c>/Decode</c> still wins over <c>/Range</c>.</summary>
    [Fact]
    public void SampledFunction_ExplicitDecode_OverridesRange()
    {
        var fn = SampledFunction(range: "[0 100]", decode: "[0 1]", samples: "00FF>");

        Assert.Equal(1f, PdfFunctionEvaluator.Evaluate(fn, new[] { 1f })[0], 3);
    }

    /// <summary>
    /// <c>num den atan</c> — den is on top of the stack, so it pops first. The two locals were
    /// named the other way round, which computed <c>atan(den/num)</c>; and the result must be in
    /// [0, 360), not Atan2's (-180, 180].
    /// </summary>
    [Theory]
    // num den  expected degrees
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 90)]
    [InlineData(0, -1, 180)]
    [InlineData(-1, 0, 270)]
    [InlineData(1, 1, 45)]
    public void PostScriptAtan_UsesTheRightOperandOrderAndRange(float num, float den, float expected)
    {
        var fn = PostScriptFunction("{ atan }", domain: "[-1 1 -1 1]", range: "[0 360]", inputs: 2);

        float[] result = PdfFunctionEvaluator.Evaluate(fn, new[] { num, den });

        Assert.Equal(expected, result[0], 2);
    }

    /// <summary>PostScript rounds a midpoint away from zero; .NET's default rounds to even.</summary>
    [Theory]
    [InlineData(0.5f, 1f)]
    [InlineData(1.5f, 2f)]
    [InlineData(2.5f, 3f)]
    [InlineData(-0.5f, -1f)]
    public void PostScriptRound_RoundsHalfAwayFromZero(float input, float expected)
    {
        var fn = PostScriptFunction("{ round }", domain: "[-10 10]", range: "[-10 10]", inputs: 1);

        Assert.Equal(expected, PdfFunctionEvaluator.Evaluate(fn, new[] { input })[0], 3);
    }

    // ====================================================================
    //  Mesh shadings — /Function was never evaluated
    // ====================================================================

    /// <summary>
    /// §8.7.4.5.5: with <c>/Function</c> present a type-4 mesh stores ONE parametric value per
    /// vertex, and the colour is Function(t). The builder ignored <c>/Function</c> and handed the
    /// raw t to the colour converter, so a DeviceRGB mesh became a red-channel ramp. Here the
    /// function maps every t to pure blue, which the raw-component path cannot produce.
    /// </summary>
    [Fact]
    public void MeshShadingWithFunction_UsesTheFunctionColour()
    {
        using var bmp = PixelAssert.Render(MeshShadingPdf(withFunction: true));
        var c = PixelAssert.At(bmp, 200, 100);   // well inside the triangle, off its hypotenuse

        Assert.True(c.Blue > 200, $"expected the function's blue, got {c}");
        Assert.True(c.Red  < 60,  $"expected no red, got {c}");
    }

    /// <summary>
    /// The control: without <c>/Function</c> the same vertex data is three components, and the
    /// mesh paints red. This is what makes the assertion above a claim about the function rather
    /// than about the fixture.
    /// </summary>
    [Fact]
    public void MeshShadingWithoutFunction_UsesTheRawComponents()
    {
        using var bmp = PixelAssert.Render(MeshShadingPdf(withFunction: false));
        var c = PixelAssert.At(bmp, 200, 100);   // well inside the triangle, off its hypotenuse

        Assert.True(c.Red > 200, $"expected raw-component red, got {c}");
    }

    // ====================================================================
    //  Page geometry
    // ====================================================================

    /// <summary>
    /// §7.9.5 allows a rectangle written from either corner pair. An inverted /MediaBox gave a
    /// negative extent, which <c>Math.Max(1, Ceiling(negative))</c> turned into a 1x1-pixel page.
    /// </summary>
    [Fact]
    public void InvertedMediaBox_RendersAtFullSize()
    {
        using var bmp = PixelAssert.Render(PageWithBox("[612 792 0 0]", "1 0 0 rg 0 0 612 792 re f"));

        Assert.Equal(612, bmp.Width);
        Assert.Equal(792, bmp.Height);
        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 300, 400)));
    }

    /// <summary>
    /// §14.11.2: every other boundary box is contained in the MediaBox, and one that extends
    /// outside is intersected with it. A CropBox twice the MediaBox used to size the whole render.
    /// </summary>
    [Fact]
    public void CropBoxLargerThanMediaBox_IsIntersectedWithIt()
    {
        using var bmp = PixelAssert.Render(PageWithBoxes(
            media: "[0 0 200 100]", crop: "[0 0 400 300]", content: "0 g 0 0 200 100 re f"));

        Assert.Equal(200, bmp.Width);
        Assert.Equal(100, bmp.Height);
    }

    /// <summary>A CropBox inside the MediaBox is used as-is — the intersection is not a no-op rule.</summary>
    [Fact]
    public void CropBoxInsideMediaBox_IsHonoured()
    {
        using var bmp = PixelAssert.Render(PageWithBoxes(
            media: "[0 0 400 300]", crop: "[0 0 200 100]", content: "0 g 0 0 400 300 re f"));

        Assert.Equal(200, bmp.Width);
        Assert.Equal(100, bmp.Height);
    }

    /// <summary>
    /// §14.11.1: <c>/UserUnit</c> scales the default user space, so it belongs in the device
    /// scale. Multiplied into the page extent only, the bitmap would grow and the content stay at
    /// 1x, leaving most of the page blank.
    /// </summary>
    [Fact]
    public void UserUnit_ScalesTheContent_NotOnlyTheCanvas()
    {
        // A 100x100 page with /UserUnit 2 is 200x200 px at 72 dpi, and a full-page fill must cover
        // all of it — including the far corner, which the old code left white.
        using var bmp = PixelAssert.Render(PageWithUserUnit(2, "0 g 0 0 100 100 re f"));

        Assert.Equal(200, bmp.Width);
        Assert.Equal(200, bmp.Height);
        Assert.True(PixelAssert.IsInk(bmp.GetPixel(190, 10)),
            "content did not scale with /UserUnit — the far corner is blank");
        Assert.True(PixelAssert.IsInk(bmp.GetPixel(190, 190)));
    }

    // ====================================================================
    //  Indexed with a non-trivial base space
    // ====================================================================

    /// <summary>
    /// An <c>/Indexed</c> palette over a <c>/Separation</c> base holds <b>tints</b>, which must go
    /// through the tint transform. Reading the palette byte as a colour component inverts the
    /// result: tint 1 is full ink (dark), while grey 1 is white.
    /// </summary>
    /// <remarks>
    /// This is the trap in "resolve the base space name properly": naming the base
    /// <c>Separation</c> and giving it one component is still wrong, because the component is not a
    /// colour. <c>BuildIndexedPalette</c> evaluates the transform — one palette decoder instead of
    /// two that disagree.
    /// </remarks>
    [Fact]
    public void IndexedOverSeparation_EvaluatesTheTintTransform()
    {
        // Palette entry 1 is tint 0xFF; the transform maps tint 1 → DeviceGray 0, i.e. BLACK.
        using var bmp = PixelAssert.Render(IndexedSeparationPdf(index: 1));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Red < 40 && c.Green < 40 && c.Blue < 40,
            $"tint 1 must transform to black, got {c} (the palette byte was read as a grey level)");
    }

    /// <summary>The other end of the same palette, so neither result can be a constant.</summary>
    [Fact]
    public void IndexedOverSeparation_ZeroTintIsTheOtherEndOfTheTransform()
    {
        using var bmp = PixelAssert.Render(IndexedSeparationPdf(index: 0));
        var c = PixelAssert.At(bmp, 50, 50);

        Assert.True(c.Red > 200 && c.Green > 200 && c.Blue > 200,
            $"tint 0 must transform to white, got {c}");
    }

    /// <summary>
    /// An <c>/Indexed</c> palette over a <c>/CalGray</c> base must apply <c>/Gamma</c>; folding
    /// CalGray into DeviceGray would discard it.
    /// </summary>
    /// <remarks>
    /// Compared against the SVG render rather than a fixed value: asserting equality is what catches
    /// a divergence between the two palette builders in either direction.
    /// </remarks>
    [Fact]
    public void IndexedOverCalGray_AppliesGamma_AndAgreesWithSvg()
    {
        byte[] pdf = IndexedCalGrayPdf();

        using var bmp = PixelAssert.Render(pdf);
        var c = PixelAssert.At(bmp, 50, 50);

        // Palette entry 1 is 0x80 ≈ 0.502; gamma 2.2 makes the luminance ≈0.22, sRGB ≈129 — where
        // the gamma dropped (gamma 1, luminance 0.5) is ≈188.
        Assert.InRange(c.Red, 120, 138);

        using var ms = new MemoryStream(pdf);
        string svg = Tetitulz.Pdf.Base.Rendering.PdfSvgRenderer.RenderPageToSvg(ms, 0);
        Assert.Contains($"rgb({c.Red},{c.Green},{c.Blue})", svg);
    }

    /// <summary>
    /// <c>/HiVal</c> is attacker-controlled and the palette builder allocates and loops from it, so
    /// it must be clamped to the §8.6.6.3 maximum of 255 — inside the builder, where the dependency
    /// is, rather than at each call site.
    /// </summary>
    /// <remarks>
    /// The image path had always clamped; routing <c>sc</c> through the same helper without a clamp
    /// made every colour operator allocate from the file's number. A negative value threw out of the
    /// operator, so the fill colour silently kept its previous value.
    /// </remarks>
    [Theory]
    [InlineData("400000000")]   // ~1.6 GB and a 400M-iteration loop, per `sc`
    [InlineData("-5")]          // new SKColor[-4] → OverflowException out of the operator
    public void IndexedHiVal_IsClampedBeforeSizingThePalette(string hival)
    {
        // Must return promptly with the colour actually set — not hang, not throw, not skip.
        using var bmp = PixelAssert.Render(IndexedAdversarialHiValPdf(hival));

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 50, 50)),
            "the fill never happened — the operator was abandoned rather than clamped");
    }

    /// <summary>
    /// A shading whose <c>/ColorSpace</c> is <c>/Separation</c> produces <b>tints</b>, which must go
    /// through the space's tint transform before they mean anything (§8.7.4.3, §8.6.6.4).
    /// </summary>
    /// <remarks>
    /// <b>A shared gap, which is why it outlived a parity sweep that found thirty other things.</b>
    /// Both renderers fed the tint straight to the component converter and were wrong in *different*
    /// ways — this one put a single tint on the red channel, the SVG writer read it as a grey level
    /// — but because neither was right, it never showed up as a divergence. Same-wrong is invisible
    /// to a comparison; only the spec catches it.
    /// </remarks>
    [Fact]
    public void SeparationShading_AppliesTheTintTransform()
    {
        using var bmp = PixelAssert.Render(SeparationShadingPdf());

        // The transform maps tint t to DeviceRGB (0, 0, t): pure blue at full tint, black at zero.
        var hot = PixelAssert.At(bmp, 90, 50);
        Assert.True(hot.Blue > 180, $"full tint should transform to blue, got {hot}");
        Assert.True(hot.Red < 60, $"the tint was read as a colour component, got {hot}");
    }

    // ====================================================================
    //  Colour-key masking
    // ====================================================================

    /// <summary>
    /// §8.9.6.4 compares the RAW integer samples, not the decoded sRGB pixel. At 4 bpc the old
    /// code divided the ranges by 255 and compared them against 8-bit sRGB, so a range naming
    /// 4-bit sample values matched nothing (or the wrong pixels).
    /// </summary>
    [Fact]
    public void ColourKeyMask_ComparesRawSamples_AtFourBitsPerComponent()
    {
        // 2x1 gray image at 4 bpc: samples 0x0 and 0xF. /Mask [0 0] masks the first only.
        using var bmp = PixelAssert.Render(ColourKeyImagePdf(bpc: 4, hex: "0F>", mask: "[0 0]"));

        // The image is drawn over the left/right halves of a 100x100 area at (0,700).
        var left  = PixelAssert.At(bmp, 25, 750);
        var right = PixelAssert.At(bmp, 75, 750);

        Assert.False(PixelAssert.IsInk(left), $"sample 0 should be masked out, got {left}");
        Assert.True(right.Red > 200 && right.Green > 200, $"sample 15 should be white, got {right}");
    }

    /// <summary>The un-masked control: no /Mask means the dark sample paints.</summary>
    [Fact]
    public void ColourKeyMask_Absent_LeavesBothSamplesPainted()
    {
        using var bmp = PixelAssert.Render(ColourKeyImagePdf(bpc: 4, hex: "0F>", mask: null));

        Assert.True(PixelAssert.IsInk(PixelAssert.At(bmp, 25, 750)));
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    private static PdfDictionary SampledFunction(string range, string decode, string samples)
    {
        string decodeEntry = decode != null ? $" /Decode {decode}" : "";
        string body =
            $"<< /FunctionType 0 /Domain [0 1] /Range {range} /Size [2] /BitsPerSample 8" +
            $"{decodeEntry} /Filter /ASCIIHexDecode /Length {samples.Length} >>\n" +
            $"stream\n{samples}\nendstream";
        return ParseSingleObject(body);
    }

    private static PdfDictionary PostScriptFunction(string program, string domain, string range, int inputs)
    {
        string body =
            $"<< /FunctionType 4 /Domain {domain} /Range {range} /Length {program.Length} >>\n" +
            $"stream\n{program}\nendstream";
        return ParseSingleObject(body);
    }

    /// <summary>Parses a one-object PDF and returns that object, so fixtures stay literal PDF syntax.</summary>
    private static PdfDictionary ParseSingleObject(string objectBody)
    {
        byte[] pdf = Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Fn 4 0 R >>",
            objectBody);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var page = doc.Pages[0];
        var fn = doc.ResolveObject(page.Dictionary[new PdfName("Fn")]) as PdfDictionary;
        Assert.NotNull(fn);
        return fn!;
    }

    private static byte[] MeshShadingPdf(bool withFunction)
    {
        // Type 4 free-form Gouraud mesh, 8-bit coords and components, one triangle covering the
        // page. With /Function each vertex carries one value; without it, three (pure red).
        //   flag(8) x(8) y(8) then components.
        string vertices = withFunction
            ? "00 00 00 00" + " 00 FF 00 00" + " 00 FF FF 00"       // t = 0 at every vertex
            : "00 00 00 FF0000" + " 00 FF 00 FF0000" + " 00 FF FF FF0000";
        string hex = vertices.Replace(" ", "") + ">";

        string decode = withFunction
            ? "[0 300 0 300 0 1]"                 // one parametric range
            : "[0 300 0 300 0 1 0 1 0 1]";        // three colour ranges
        string function = withFunction
            ? " /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 1] /C1 [0 0 1] /N 1 >>"
            : "";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            Stream($"/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8" +
                   $" /BitsPerComponent 8 /BitsPerFlag 8 /Decode {decode}{function}" +
                   " /Filter /ASCIIHexDecode", hex));
    }

    private static byte[] PageWithBox(string mediaBox, string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Contents 4 0 R /Resources << >> >>",
            Stream("", content));

    private static byte[] PageWithBoxes(string media, string crop, string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {media} /CropBox {crop}" +
            " /Contents 4 0 R /Resources << >> >>",
            Stream("", content));

    private static byte[] PageWithUserUnit(int userUnit, string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /UserUnit {userUnit}" +
            " /Contents 4 0 R /Resources << >> >>",
            Stream("", content));

    /// <summary>
    /// Fills the page with palette entry <paramref name="index"/> of an <c>/Indexed</c> space whose
    /// base is a <c>/Separation</c>: the two entries are tints 0x00 and 0xFF, and the transform
    /// maps tint → DeviceGray as <c>1 − t</c> (C0 = 1, C1 = 0), so tint 1 is black.
    /// </summary>
    private static byte[] IndexedSeparationPdf(int index) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", $"/CS0 cs {index} sc 0 0 100 100 re f"),
            "[ /Indexed [ /Separation /Spot /DeviceGray" +
            " << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> ] 1 <00FF> ]");

    /// <summary>Fills with palette entry 1 (0x80) of an /Indexed space over /CalGray /Gamma 2.2.</summary>
    private static byte[] IndexedCalGrayPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 1 sc 0 0 100 100 re f"),
            "[ /Indexed [ /CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 2.2 >> ] 1 <0080> ]");

    /// <summary>
    /// An axial shading in a <c>/Separation</c> space whose tint transform maps t to
    /// DeviceRGB (0, 0, t), so the answer is unmistakably blue and cannot be produced by reading the
    /// tint as a colour component.
    /// </summary>
    private static byte[] SeparationShadingPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            "<< /ShadingType 2 /Coords [0 0 100 0] /Extend [true true]" +
            " /ColorSpace [ /Separation /Spot /DeviceRGB" +
            "   << /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [0 0 1] /N 1 >> ]" +
            " /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>");

    /// <summary>An /Indexed space whose /HiVal is hostile — absurdly large, or negative.</summary>
    private static byte[] IndexedAdversarialHiValPdf(string hival) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ColorSpace << /CS0 5 0 R >> >> >>",
            Stream("", "/CS0 cs 0 sc 0 0 100 100 re f"),
            $"[ /Indexed /DeviceRGB {hival} <000000FFFFFF> ]");

    private static byte[] ColourKeyImagePdf(int bpc, string hex, string mask)
    {
        string maskEntry = mask != null ? $" /Mask {mask}" : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im1 5 0 R >> >> >>",
            Stream("", "q 100 0 0 100 0 700 cm /Im1 Do Q"),
            Stream($"/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray" +
                   $" /BitsPerComponent {bpc}{maskEntry} /Filter /ASCIIHexDecode", hex));
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
