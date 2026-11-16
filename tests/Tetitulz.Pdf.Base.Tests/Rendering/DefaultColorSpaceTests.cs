// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Default colour spaces (ISO 32000-2 §8.6.5.6) in both renderers: a device colour selected under a
/// declared <c>/DefaultGray</c>, <c>/DefaultRGB</c> or <c>/DefaultCMYK</c> paints exactly as the same
/// operands set in the default space by <c>cs</c> and <c>sc</c> — through an operator, <c>cs</c>, an
/// image, an inline image, a shading, and an Indexed base — and the same page without the default
/// paints otherwise (each case's potency).
/// </summary>
public class DefaultColorSpaceTests
{
    /// <summary>A grey whose 0.5 is linear light — not DeviceGray's 0.5.</summary>
    private const string CalGray = "[/CalGray << /WhitePoint [0.9505 1 1.089] /Gamma 1 >>]";

    /// <summary>A linear RGB.</summary>
    private const string CalRgb = "[/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma [1 1 1] >>]";

    /// <summary>Four inks whose alternate paints everything blue — object 6.</summary>
    private const string BlueInks = "[/DeviceN [/C /M /Y /K] /DeviceRGB 6 0 R]";

    public static TheoryData<string, string, string> OperatorCases() => new()
    {
        { "DefaultGray", CalGray, "0.5 g" },
        { "DefaultGray", CalGray, "/DeviceGray cs 0.5 sc" },
        { "DefaultRGB", CalRgb, "0.8 0.5 0.2 rg" },
        { "DefaultRGB", CalRgb, "/DeviceRGB cs 0.8 0.5 0.2 sc" },
        { "DefaultCMYK", BlueInks, "0 0 0 1 k" },
    };

    /// <summary>A fill under the default is the explicit space's fill, raster and SVG alike, and not the device fill.</summary>
    [Theory]
    [MemberData(nameof(OperatorCases))]
    public void AFillUnderADefaultIsTheDefaultSpacesFill(string key, string space, string setColour)
    {
        string explicitColour = "/X cs " + Operands(setColour) + " sc";

        var underDefault = Fill(Page("/" + key + " 5 0 R", setColour + " 0 0 100 100 re f", space));
        var explicitly = Fill(Page("/X 5 0 R", explicitColour + " 0 0 100 100 re f", space));
        var plain = Fill(Page("", setColour + " 0 0 100 100 re f", space));

        Assert.Equal(explicitly.Raster, underDefault.Raster);
        Assert.Equal(explicitly.Svg, underDefault.Svg);
        Assert.Equal(Rgb(underDefault.Raster), underDefault.Svg);
        Assert.NotEqual(plain.Raster, underDefault.Raster);
    }

    /// <summary>The stroking operators remap too.</summary>
    [Theory]
    [InlineData("DefaultGray", CalGray, "0.5 G", "/X CS 0.5 SC")]
    [InlineData("DefaultRGB", CalRgb, "0.8 0.5 0.2 RG", "/X CS 0.8 0.5 0.2 SC")]
    [InlineData("DefaultCMYK", BlueInks, "0 0 0 1 K", "/X CS 0 0 0 1 SC")]
    public void AStrokeUnderADefaultIsTheDefaultSpacesStroke(string key, string space, string setColour, string explicitColour)
    {
        const string line = " 20 w 0 50 m 100 50 l S";
        var underDefault = Stroke(Page("/" + key + " 5 0 R", setColour + line, space));
        var explicitly = Stroke(Page("/X 5 0 R", explicitColour + line, space));
        var plain = Stroke(Page("", setColour + line, space));

        Assert.Equal(explicitly.Raster, underDefault.Raster);
        Assert.Equal(explicitly.Svg, underDefault.Svg);
        Assert.NotEqual(plain.Raster, underDefault.Raster);
    }

    /// <summary>An Indexed space's device base is remapped (§8.6.5.6's list).</summary>
    [Fact]
    public void AnIndexedBaseIsRemapped()
    {
        string indexedOnDevice = "[/Indexed /DeviceGray 1 <0080>]";
        string indexedOnCal = "[/Indexed " + CalGray + " 1 <0080>]";
        var underDefault = Fill(Page("/DefaultGray 5 0 R /I " + indexedOnDevice,
            "/I cs 1 sc 0 0 100 100 re f", CalGray));
        var explicitly = Fill(Page("/I " + indexedOnCal, "/I cs 1 sc 0 0 100 100 re f", CalGray));
        var plain = Fill(Page("/I " + indexedOnDevice, "/I cs 1 sc 0 0 100 100 re f", CalGray));

        Assert.Equal(explicitly.Raster, underDefault.Raster);
        Assert.Equal(explicitly.Svg, underDefault.Svg);
        Assert.NotEqual(plain.Raster, underDefault.Raster);
    }

    /// <summary>A device-grey image, an inline image and a shading take the default too.</summary>
    [Theory]
    [InlineData("image")]
    [InlineData("inline")]
    [InlineData("shading")]
    [InlineData("pattern")]
    public void ImagesInlineImagesAndShadingsTakeTheDefault(string kind)
    {
        var underDefault = Raster(PaintingPage(kind, "/DeviceGray", "/DefaultGray 5 0 R"));
        var explicitly = Raster(PaintingPage(kind, CalGray, ""));
        var plain = Raster(PaintingPage(kind, "/DeviceGray", ""));

        Assert.Equal(explicitly, underDefault);
        Assert.NotEqual(plain, underDefault);

        string svgDefault = SvgPaint(PaintingPage(kind, "/DeviceGray", "/DefaultGray 5 0 R"), kind);
        string svgExplicit = SvgPaint(PaintingPage(kind, CalGray, ""), kind);
        string svgPlain = SvgPaint(PaintingPage(kind, "/DeviceGray", ""), kind);
        Assert.Equal(svgExplicit, svgDefault);
        Assert.NotEqual(svgPlain, svgDefault);
    }

    /// <summary>A Separation's device alternate is remapped (§8.6.5.6's list).</summary>
    [Fact]
    public void ASeparationAlternateIsRemapped()
    {
        const string tint = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0.5] /N 1 >>";
        string onDevice = "[/Separation /Spot /DeviceGray " + tint + "]";
        string onCal = "[/Separation /Spot " + CalGray + " " + tint + "]";
        var underDefault = Fill(Page("/DefaultGray 5 0 R /S " + onDevice, "/S cs 1 sc 0 0 100 100 re f", CalGray));
        var explicitly = Fill(Page("/S " + onCal, "/S cs 1 sc 0 0 100 100 re f", CalGray));
        var plain = Fill(Page("/S " + onDevice, "/S cs 1 sc 0 0 100 100 re f", CalGray));

        Assert.Equal(explicitly.Raster, underDefault.Raster);
        Assert.Equal(explicitly.Svg, underDefault.Svg);
        Assert.NotEqual(plain.Raster, underDefault.Raster);
    }

    /// <summary>
    /// One image drawn twice — first plainly, then by a form whose resources declare a default — is
    /// two colours: the raster image cache and the SVG's shared image definition must not hand the
    /// second draw the first draw's pixels.
    /// </summary>
    [Fact]
    public void OneImageUnderTwoResourceDictionariesIsTwoColours()
    {
        const string image = "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace /DeviceGray /Length 1 >>\nstream\n\u0080\nendstream";
        const string formContent = "q 50 0 0 100 50 0 cm /Im Do Q";
        const string PageContent = "q 50 0 0 100 0 0 cm /Im Do Q /F Do";
        string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /ColorSpace << /DefaultGray 5 0 R >> /XObject << /Im 7 0 R >> >> /Length "
            + formContent.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + formContent + "\nendstream";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /XObject << /Im 7 0 R /F 8 0 R >> >> >>",
            "<< /Length " + PageContent.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + PageContent + "\nendstream",
            CalGray,
            "<< >>",
            image,
            form,
        };
        byte[] pdf = Build(objects);

        using var bmp = PixelAssert.Render(pdf);
        var left = PixelAssert.At(bmp, 25, 50);
        var right = PixelAssert.At(bmp, 75, 50);
        var explicitly = Raster(PaintingPage("image", CalGray, ""));
        Assert.Equal(explicitly, right);
        Assert.NotEqual(left, right);

        using var ms = new MemoryStream(pdf);
        var svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        var colours = svg.Descendants().Where(e => e.Name.LocalName == "image")
            .Select(e => e.Attributes().First(a => a.Name.LocalName == "href").Value)
            .Select(h => { using var b = SKBitmap.Decode(Convert.FromBase64String(h.Substring(h.IndexOf(',') + 1))); return Rgb(b.GetPixel(0, 0)); })
            .Distinct().ToList();
        Assert.Equal(2, colours.Count);
    }

    /// <summary>A default applies in the resource dictionary it is declared in: a form with its own resources paints device colour.</summary>
    [Fact]
    public void AFormWithItsOwnResourcesIsNotRemappedByThePages()
    {
        string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << >> /Length 21 >>\nstream\n0.5 g 0 0 100 100 re f\nendstream";
        var underPage = Fill(Page("/DefaultGray 5 0 R", "/F Do", CalGray, xobjects: "/F 7 0 R", seventh: form));
        var plain = Fill(Page("", "0.5 g 0 0 100 100 re f", CalGray));

        Assert.Equal(plain.Raster, underPage.Raster);
        Assert.Equal(plain.Svg, underPage.Svg);
    }

    /// <summary>
    /// A default of another arity, or of a family a default may not be, is refused: the device colour
    /// is painted as written and the refusal reported, once for the page however many operators meet
    /// it; a default that is the device space itself is that space.
    /// </summary>
    [Theory]
    [InlineData("DefaultRGB", "/DeviceCMYK", "0.8 0.5 0.2 rg")]                          // another arity
    [InlineData("DefaultRGB", "[/Indexed /DeviceRGB 0 <000000>]", "0.8 0.5 0.2 rg")]
    [InlineData("DefaultGray", "[/Indexed /DeviceRGB 0 <FF0000>]", "0.5 g")]             // one component, but Indexed
    [InlineData("DefaultRGB", "[/Lab << /WhitePoint [0.9505 1 1.089] >>]", "0.8 0.5 0.2 rg")]   // three, but Lab
    public void ARefusedDefaultPaintsTheDeviceColourAndIsReportedOnce(string key, string badDefault, string setColour)
    {
        byte[] pdf = Page("/" + key + " " + badDefault, setColour + " 0 0 50 100 re f " + setColour + " 50 0 50 100 re f", CalRgb);
        var refused = Fill(pdf);
        var plain = Fill(Page("", setColour + " 0 0 100 100 re f", CalRgb));

        Assert.Equal(plain.Raster, refused.Raster);
        var reports = Reports(pdf).Where(d => d.Contains(key, StringComparison.Ordinal)).ToList();
        Assert.Single(reports);
        Assert.Contains("8.6.5.6", reports[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultThatIsTheDeviceSpaceItselfChangesNothing()
    {
        var same = Fill(Page("/DefaultRGB /DeviceRGB", "0.8 0.5 0.2 rg 0 0 100 100 re f", CalRgb));
        var plain = Fill(Page("", "0.8 0.5 0.2 rg 0 0 100 100 re f", CalRgb));
        Assert.Equal(plain.Raster, same.Raster);
        Assert.Equal(plain.Svg, same.Svg);
    }

    // ---------------------------------------------------------------- fixtures

    private static string Operands(string setColour)
        => string.Join(" ", setColour.Split(' ').Where(t => t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '.')));

    private static string Rgb(SKColor c) => $"rgb({c.Red},{c.Green},{c.Blue})";

    private static (SKColor Raster, string Svg) Fill(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        var path = XDocument.Parse(svg).Descendants().Last(e => e.Name.LocalName == "path" && (e.Attribute("fill")?.Value ?? "none") != "none");
        return (PixelAssert.At(bmp, 25, 50), path.Attribute("fill")!.Value);
    }

    private static (SKColor Raster, string Svg) Stroke(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        var path = XDocument.Parse(svg).Descendants().Last(e => e.Name.LocalName == "path" && (e.Attribute("stroke")?.Value ?? "none") != "none");
        return (PixelAssert.At(bmp, 50, 50), path.Attribute("stroke")!.Value);
    }

    private static SKColor Raster(byte[] pdf)
    {
        using var bmp = PixelAssert.Render(pdf);
        return PixelAssert.At(bmp, 25, 50);
    }

    /// <summary>The SVG's paint for an image (its PNG's pixel), or a shading (its stop colours).</summary>
    private static string SvgPaint(byte[] pdf, string kind)
    {
        using var ms = new MemoryStream(pdf);
        var doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        if (kind == "shading" || kind == "pattern")
            return string.Join(";", doc.Descendants().Where(e => e.Name.LocalName == "stop").Select(e => e.Attribute("stop-color")!.Value));
        var image = doc.Descendants().First(e => e.Name.LocalName == "image");
        string href = (image.Attributes().First(a => a.Name.LocalName == "href")).Value;
        byte[] png = Convert.FromBase64String(href.Substring(href.IndexOf(',') + 1));
        using var bmp = SKBitmap.Decode(png);
        return Rgb(bmp.GetPixel(0, 0));
    }

    private static List<string> Reports(byte[] pdf)
    {
        var details = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind k, string d, Exception e)
        {
            if (Environment.CurrentManagedThreadId == ownThread && k == RenderDiagnosticKind.RenderParameterIgnored)
                lock (details) details.Add(d);
        }
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            using var ms = new MemoryStream(pdf);
            PdfPageRenderer.RenderPage(ms, 0, 72);
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (details) return new List<string>(details);
    }

    /// <summary>
    /// A 100 × 100 page painting <paramref name="content"/> with <paramref name="colourSpaces"/> as its
    /// /ColorSpace entries; object 5 is <paramref name="space"/>, object 6 the blue tint transform.
    /// </summary>
    private static byte[] Page(string colourSpaces, string content, string space, string xobjects = "", string seventh = null)
    {
        const string blue = "{ pop pop pop pop 0 0 1 }";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /ColorSpace << "
                + colourSpaces + " >> /XObject << " + xobjects + " >> >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
            space,
            "<< /FunctionType 4 /Domain [0 1 0 1 0 1 0 1] /Range [0 1 0 1 0 1] /Length " + blue.Length.ToString(CultureInfo.InvariantCulture)
                + " >>\nstream\n" + blue + "\nendstream",
        };
        if (seventh != null) objects.Add(seventh);
        return Build(objects);
    }

    /// <summary>A page painting an image, an inline image or an axial shading in <paramref name="imageSpace"/>.</summary>
    private static byte[] PaintingPage(string kind, string imageSpace, string colourSpaces)
    {
        string content = kind switch
        {
            "image" => "q 100 0 0 100 0 0 cm /Im Do Q",
            "inline" => "q 100 0 0 100 0 0 cm BI /W 1 /H 1 /CS " + (imageSpace == "/DeviceGray" ? "/G" : "/Cal") + " /BPC 8 ID \u0080 EI Q",
            "pattern" => "/Pattern cs /P scn 0 0 100 100 re f",
            _ => "/Sh sh",
        };
        string resources = "/ColorSpace << " + colourSpaces + (imageSpace == "/DeviceGray" ? "" : " /Cal " + imageSpace) + " >>"
            + " /XObject << /Im 6 0 R >> /Shading << /Sh 7 0 R >> /Pattern << /P << /PatternType 2 /Shading 7 0 R >> >>";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << " + resources + " >> >>",
            "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream",
            CalGray,
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace " + imageSpace + " /Length 1 >>\nstream\n\u0080\nendstream",
            "<< /ShadingType 2 /ColorSpace " + imageSpace + " /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0.5] /C1 [0.5] /N 1 >> /Extend [true true] >>",
        };
        return Build(objects);
    }

    private static byte[] Build(List<string> objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
