// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.7.4.5.2: a shading's <c>/Function</c> is "a 2-in, <i>n</i>-out function <b>or an array of
/// <i>n</i> 2-in, 1-out functions</b>". Both spellings must draw the same picture, in both renderers.
/// </summary>
/// <remarks>
/// <para><b>One helper both renderers share</b> — <c>PdfFunctionEvaluator.EvaluateEntry</c> — reads
/// the entry for the Type 1, axial and radial paths alike, so the four sites cannot drift on what a
/// <c>/Function</c> entry may legally be. Reading it as <c>ResolveObject(sh[/Function]) as
/// PdfDictionary</c> and returning on null would make a perfectly conformant shading vanish in both
/// renderers.</para>
///
/// <para><b>Such a defect is invisible to a raw-read grep,</b> because the value <i>is</i>
/// resolved; it is the narrowing that drops the legal form.</para>
///
/// <para>The helper also evaluates with the resolve callback: without one a stitching
/// sub-function written indirectly would export as flat black and render correctly.</para>
///
/// <para>The fixture is a genuine 2-D function, <c>rgb(u, v, 0)</c> over a 100x100 page, so the
/// assertions separate "the shading drew" from "something drew": one corner must come out
/// red-dominant and the opposite corner green-dominant. A blank page is white, which fails
/// both.</para>
/// </remarks>
public class FunctionArrayShadingTests
{
    /// <summary>
    /// The raster path paints the same 2-D ramp whichever way the function is spelled.
    /// <c>false</c> is the control: the single-function form always worked.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Raster_PaintsTheSameRamp_ForASingleFunctionAndForAnArray(bool asArray)
    {
        using var ms = new MemoryStream(FunctionShadingPdf(asArray));
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);

        using var bmp = SkiaSharp.SKBitmap.Decode(png);
        Assert.NotNull(bmp);

        // The page is 100x100 pt at 72 dpi, and the bitmap's y runs the other way, so PDF (90, 10)
        // — domain (0.9, 0.1), strongly red — is bitmap row 90.
        var red = bmp.GetPixel(90, 90);
        var green = bmp.GetPixel(10, 10);

        Assert.True(red.Red > 200 && red.Green < 80,
            "expected a red-dominant corner, got " + Describe(red));
        Assert.True(green.Green > 200 && green.Red < 80,
            "expected a green-dominant corner, got " + Describe(green));
    }

    /// <summary>
    /// The SVG export paints it too. A Type 1 shading has no faithful SVG primitive, so the writer
    /// rasterises it through this same interpreter and embeds the result — which means the raster
    /// fix carries the export with it, and the coarse <c>EmitSvgFunctionShading</c> fallback below
    /// it (reached only when rasterisation yields nothing) needed the same fix on its own terms.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Svg_EmitsTheSameRamp_ForASingleFunctionAndForAnArray(bool asArray)
    {
        List<(int R, int G, int B)> colours = SvgShadingColours(asArray);

        Assert.NotEmpty(colours);
        Assert.Contains(colours, c => c.R > 200 && c.G < 80);
        Assert.Contains(colours, c => c.G > 200 && c.R < 80);
    }

    /// <summary>
    /// Both renderers must agree, which is the property that would have caught this had only one of
    /// them been broken. Compared on the extreme colours each produced.
    /// </summary>
    [Fact]
    public void RasterAndSvgAgree_OnAnArrayValuedFunctionShading()
    {
        using var rasterMs = new MemoryStream(FunctionShadingPdf(asArray: true));
        using var bmp = SkiaSharp.SKBitmap.Decode(PdfPageRenderer.RenderPage(rasterMs, 0, 72));
        List<(int R, int G, int B)> colours = SvgShadingColours(asArray: true);

        var rasterRed = bmp.GetPixel(90, 90);
        Assert.True(rasterRed.Red > 200 && rasterRed.Green < 80, Describe(rasterRed));
        Assert.Contains(colours, c => c.R > 200 && c.G < 80);

        var rasterGreen = bmp.GetPixel(10, 10);
        Assert.True(rasterGreen.Green > 200 && rasterGreen.Red < 80, Describe(rasterGreen));
        Assert.Contains(colours, c => c.G > 200 && c.R < 80);
    }

    /// <summary>
    /// The same rule on the axial route, which is where the two hand-written copies of the
    /// array loop lived before they were merged into the shared helper.
    /// </summary>
    /// <remarks>
    /// <b>This case exists because of what the mutation found.</b> Deleting the array branch from
    /// the survivor reddened only the Type 1 tests above and nothing else in the suite — so the two
    /// copies being merged had no coverage of their own, and the merge would have been a deletion
    /// nobody could detect. Both renderers are asserted, since a divergence is what a single-sided
    /// consolidation produces.
    /// </remarks>
    [Fact]
    public void AxialShading_HonoursAnArrayValuedFunction_InBothRenderers()
    {
        using var rasterMs = new MemoryStream(AxialArrayShadingPdf());
        using var bmp = SkiaSharp.SKBitmap.Decode(PdfPageRenderer.RenderPage(rasterMs, 0, 72));

        // The ramp runs left to right from black to red, so the right-hand end is red-dominant
        // and the left-hand end is not. A flat fill fails one of the two whichever colour it is.
        var right = bmp.GetPixel(95, 50);
        var left = bmp.GetPixel(5, 50);
        Assert.True(right.Red > 200 && right.Green < 80, "right end: " + Describe(right));
        Assert.True(left.Red < 80, "left end: " + Describe(left));

        using var svgMs = new MemoryStream(AxialArrayShadingPdf());
        string svg = PdfSvgRenderer.RenderPageToSvg(svgMs, 0);
        List<(int R, int G, int B)> stops = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "stop")
            .Select(e => e.Attribute("stop-color")?.Value)
            .Where(v => v != null && v.StartsWith("rgb(", StringComparison.Ordinal))
            .Select(ParseRgb)
            .ToList();

        Assert.NotEmpty(stops);
        Assert.Contains(stops, c => c.R > 200 && c.G < 80);
        Assert.Contains(stops, c => c.R < 80);
    }

    // ------------------------------------------------------------------

    private static string Describe(SkiaSharp.SKColor c) =>
        "rgb(" + c.Red + "," + c.Green + "," + c.Blue + ")";

    /// <summary>
    /// Every colour the export actually carries for the shading: the decoded pixels of the embedded
    /// image when it rasterised, and the <c>fill</c>s of the approximation grid when it fell back.
    /// Taking both means the assertion holds whichever branch ran, without asserting which did.
    /// </summary>
    private static List<(int R, int G, int B)> SvgShadingColours(bool asArray)
    {
        using var ms = new MemoryStream(FunctionShadingPdf(asArray));
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        XDocument doc = XDocument.Parse(svg);

        var colours = doc.Descendants()
            .Where(e => e.Name.LocalName == "rect")
            .Select(e => e.Attribute("fill")?.Value)
            .Where(v => v != null && v.StartsWith("rgb(", StringComparison.Ordinal))
            .Select(ParseRgb)
            .ToList();

        const string prefix = "data:image/png;base64,";
        foreach (XElement image in doc.Descendants().Where(e => e.Name.LocalName == "image"))
        {
            string href = image.Attributes()
                .FirstOrDefault(a => a.Name.LocalName == "href")?.Value;
            if (href == null || !href.StartsWith(prefix, StringComparison.Ordinal)) continue;

            using var embedded = SkiaSharp.SKBitmap.Decode(
                Convert.FromBase64String(href.Substring(prefix.Length)));
            if (embedded == null) continue;

            for (int y = 0; y < embedded.Height; y++)
                for (int x = 0; x < embedded.Width; x++)
                {
                    var c = embedded.GetPixel(x, y);
                    colours.Add((c.Red, c.Green, c.Blue));
                }
        }

        return colours;
    }

    private static (int R, int G, int B) ParseRgb(string css)
    {
        string[] parts = css.Substring(4).TrimEnd(')').Split(',');
        return (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
    }

    /// <summary>
    /// A Type 1 (function-based) shading covering a 100x100 page, painting <c>rgb(u, v, 0)</c>.
    /// <paramref name="asArray"/> switches the one 2-in 3-out function for the three 2-in 1-out
    /// functions §8.7.4.5.2 allows in its place — the only difference between the two fixtures.
    /// </summary>
    private static byte[] FunctionShadingPdf(bool asArray)
    {
        string function = asArray ? "[6 0 R 7 0 R 8 0 R]" : "5 0 R";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /Shading << /Sh1 9 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            // One 2-in 3-out function: the stack arrives as [u v], so pushing 0 makes it [u v 0].
            PsFunction("/Range [0 1 0 1 0 1]", "{ 0 }"),
            PsFunction("/Range [0 1]", "{ pop }"),        // u
            PsFunction("/Range [0 1]", "{ exch pop }"),   // v
            PsFunction("/Range [0 1]", "{ pop pop 0 }"),  // 0
            "<< /ShadingType 1 /ColorSpace /DeviceRGB /Domain [0 1 0 1]"
                + " /Matrix [100 0 0 100 0 0] /Function " + function + " >>");
    }

    /// <summary>
    /// An axial shading across a 100x100 page whose <c>/Function</c> is the array form: three
    /// 1-in 1-out exponential functions, one per DeviceRGB component, ramping black to red.
    /// </summary>
    private static byte[] AxialArrayShadingPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R"
                + " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            Stream("", "/Sh1 sh"),
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true true]"
                + " /Function [6 0 R 7 0 R 8 0 R] >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>",   // R: 0 -> 1
            "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0] /N 1 >>",   // G: 0
            "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0] /N 1 >>");  // B: 0

    private static string PsFunction(string range, string program) =>
        Stream("/FunctionType 4 /Domain [0 1 0 1] " + range, program);

    private static string Stream(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
