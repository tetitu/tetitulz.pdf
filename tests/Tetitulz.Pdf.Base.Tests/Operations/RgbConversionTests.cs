// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// DeviceCMYK converted into RGB (<see cref="PdfColorSpaceConverter.ConvertToRgb"/>), for PDF/A under an RGB output
/// intent: the CMYK as the renderer shows it, in the intent's RGB.
/// </summary>
public class RgbConversionTests
{
    [Fact]
    public void DeviceCmyk_BecomesTheRgbItRenders()
    {
        var (converted, result) = Convert(CmykPage(), null);
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var page = doc.Pages[0];
        var ops = ContentStreamParser.Parse(((PdfStream)doc.ResolveObject(page.Dictionary[PdfName.Contents])).GetDecodedData());

        // k / K became rg / RG: the renderer's own CMYK, in sRGB.
        Assert.DoesNotContain(ops, o => o.Operator == "k" || o.Operator == "K");
        Assert.Equal(Rgb(Rendered(5, 95)), Values(ops.First(o => o.Operator == "rg")));
        Assert.Equal(Rgb(PdfColor.CmykToRgb(1, 0, 0, 0)), Values(ops.First(o => o.Operator == "RG")));

        // cs /DeviceCMYK then sc: /DeviceRGB, three numbers.
        Assert.Contains(ops, o => o.Operator == "cs" && ((PdfName)o.Operands[0]).Value == "DeviceRGB");
        Assert.Equal(Rgb(Rendered(15, 95)), Values(ops.First(o => o.Operator == "sc")));

        // An /Indexed CMYK palette, and a spot whose alternate is CMYK.
        var spaces = (PdfDictionary)doc.ResolveObject(page.Resources[PdfName.ColorSpace]);
        var indexed = (PdfArray)doc.ResolveObject(spaces[new PdfName("Ix")]);
        Assert.Equal("DeviceRGB", ((PdfName)indexed[1]).Value);
        byte[] palette = ((PdfString)doc.ResolveObject(indexed[3])).RawBytes;
        Assert.Equal(new[] { Rendered(25, 95).Red, Rendered(25, 95).Green, Rendered(25, 95).Blue }, palette.Take(3));
        var spot = (PdfArray)doc.ResolveObject(spaces[new PdfName("Sp")]);
        Assert.Equal("DeviceRGB", ((PdfName)spot[2]).Value);
        float[] full = PdfFunctionEvaluator.Evaluate((PdfDictionary)doc.ResolveObject(spot[3]), new[] { 1f }, doc.ResolveObject);
        SKColor spotColour = Rendered(35, 95);
        Assert.Equal(new[] { spotColour.Red, spotColour.Green, spotColour.Blue }, full.Select(v => PdfColor.Channel(v)));

        // A DeviceCMYK image: RGB samples, as it renders.
        var image = (PdfStream)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(page.Resources[PdfName.XObject]))[new PdfName("Im")]);
        Assert.Equal("DeviceRGB", ((PdfName)doc.ResolveObject(image[PdfName.ColorSpace])).Value);
        SKColor pixel = Rendered(45, 95);
        Assert.Equal(new[] { pixel.Red, pixel.Green, pixel.Blue }, image.GetDecodedData().Take(3));

        Assert.Equal(3, result.ColoursConverted);   // k, K, the DeviceCMYK sc
        Assert.Equal(1, result.ImagesConverted);
        Assert.Empty(result.NotConverted);

        // The converted page renders as the original did.
        var after = PdfPageRenderer.RenderPageToPixels(new MemoryStream(converted), 0, 72);
        foreach (int x in new[] { 5, 15, 25, 35, 45 })
        {
            int i = (95 * after.Width + x) * 4;
            Assert.Equal(Rendered(x, 95), new SKColor(after.Pixels[i], after.Pixels[i + 1], after.Pixels[i + 2]));
        }
    }

    [Fact]
    public void IntoAnRgbIntentsProfile_TheValuesAreThatProfiles()
    {
        byte[] p3 = CompactIccProfiles.Bytes(CompactIccProfiles.DisplayP3v4);
        var (converted, _) = Convert(CmykPage(), new RgbConversionOptions { OutputIccProfile = p3 });
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var ops = ContentStreamParser.Parse(((PdfStream)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents])).GetDecodedData());
        SKColor inP3 = RenderDestination.TryCreate(p3, true, out _).Convert(Rendered(5, 95));
        Assert.Equal(Rgb(inP3), Values(ops.First(o => o.Operator == "rg")));
        Assert.NotEqual(Rgb(Rendered(5, 95)), Values(ops.First(o => o.Operator == "rg")));
    }

    [Fact]
    public void OnlyWhenAsked_AndOnlyIntoRgb()
    {
        var (converted, result) = Convert(CmykPage(), new RgbConversionOptions { ConvertDeviceCmyk = false });
        Assert.Equal(0, result.ColoursConverted + result.ImagesConverted + result.ColourSpacesConverted);
        using (var doc = PdfDocument.Open(new MemoryStream(converted)))
            Assert.Contains(ContentStreamParser.Parse(((PdfStream)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents])).GetDecodedData()),
                o => o.Operator == "k");

        var grey = Assert.Throws<ArgumentException>(() => PdfColorSpaceConverter.ConvertToRgb(new MemoryStream(CmykPage()), new MemoryStream(),
            new RgbConversionOptions { OutputIccProfile = CompactIccProfiles.Bytes(CompactIccProfiles.sGreyv4) }));
        Assert.Contains("grey", grey.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tint transform is sampled at two points an input past twelve colorants, so a DeviceN of 31 would be
    /// 2³¹ samples — a table no array holds, which threw <c>OverflowException</c> out of the conversion. It is
    /// left in its own colour and said so; twelve colorants (4 096 samples) are still converted.
    /// </summary>
    [Theory]
    [InlineData(31, false)]
    [InlineData(13, false)]
    [InlineData(12, true)]
    public void ADeviceNPastTwelveColorants_IsLeftAndSaid_TwelveAreConverted(int colorants, bool converted)
    {
        string names = string.Join(" ", Enumerable.Range(0, colorants).Select(i => "/C" + i));
        string domain = string.Join(" ", Enumerable.Repeat("0 1", colorants));
        string program = "{ " + string.Concat(Enumerable.Repeat("pop ", colorants)) + "0.1 0.2 0.3 0.4 }";
        string tints = string.Join(" ", Enumerable.Repeat("1", colorants));
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ColorSpace << /N [/DeviceN [" + names +
                "] /DeviceCMYK 5 0 R] >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes($"/N cs {tints} sc 0 0 10 10 re f")),
            HalftoneTests.Stream($"/FunctionType 4 /Domain [{domain}] /Range [0 1 0 1 0 1 0 1]", Encoding.ASCII.GetBytes(program)));

        var (output, result) = Convert(pdf, null);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        var space = (PdfArray)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.ColorSpace]))[new PdfName("N")]);
        Assert.Equal(converted ? "DeviceRGB" : "DeviceCMYK", ((PdfName)space[2]).Value);
        Assert.Equal(!converted, result.NotConverted.Any(n => n.Contains($"{colorants} colorants", StringComparison.Ordinal)));
    }

    /// <summary>A spot whose tint transform is <paramref name="program"/> over one colorant, painted once.</summary>
    internal static byte[] SpotPage(string program) => HalftoneTests.Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ColorSpace << /Sp [/Separation /Spot /DeviceCMYK 5 0 R] >> >> /Contents 4 0 R >>",
        HalftoneTests.Stream("", Encoding.ASCII.GetBytes("/Sp cs 1 sc 0 0 10 10 re f")),
        HalftoneTests.Stream("/FunctionType 4 /Domain [0 1] /Range [0 1 0 1 0 1 0 1]", Encoding.ASCII.GetBytes(program)));

    internal static (byte[], ColourConversionResult) ConvertToRgb(byte[] pdf) => Convert(pdf, null);

    // ------------------------------------------------------------------ fixtures

    private static (byte[], ColourConversionResult) Convert(byte[] pdf, RgbConversionOptions options)
    {
        using var output = new MemoryStream();
        var result = PdfColorSpaceConverter.ConvertToRgb(new MemoryStream(pdf), output, options);
        return (output.ToArray(), result);
    }

    private static float[] Rgb(SKColor c) => new[] { c.Red / 255f, c.Green / 255f, c.Blue / 255f }.Select(v => (float)Math.Round(v, 4)).ToArray();

    private static float[] Values(ContentStreamOperation op) => op.Operands.Select(o => (float)((PdfNumber)o).DoubleValue).ToArray();

    private static RenderedPage _original;

    private static SKColor Rendered(int x, int y)
    {
        _original ??= PdfPageRenderer.RenderPageToPixels(new MemoryStream(CmykPage()), 0, 72);
        int i = (y * _original.Width + x) * 4;
        return new SKColor(_original.Pixels[i], _original.Pixels[i + 1], _original.Pixels[i + 2]);
    }

    /// <summary>
    /// A DeviceCMYK fill and stroke, a <c>cs /DeviceCMYK</c> colour, an /Indexed CMYK palette, a spot with a CMYK
    /// alternate and a DeviceCMYK image, each in its own 10-point square along the bottom.
    /// </summary>
    private static byte[] CmykPage() => HalftoneTests.Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ColorSpace << /Ix [/Indexed /DeviceCMYK 0 <00FFFF00>] " +
        "/Sp [/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0.2 0.9 0 0.1] /N 1 >>] >> " +
        "/XObject << /Im 5 0 R >> >> /Contents 4 0 R >>",
        HalftoneTests.Stream("", Encoding.ASCII.GetBytes(
            "0.1 0.7 0.3 0.05 k 0 0 10 10 re f 1 0 0 0 K 1 w 0 30 m 100 30 l S " +
            "/DeviceCMYK cs 0.6 0.2 0 0.1 sc 10 0 10 10 re f /Ix cs 0 sc 20 0 10 10 re f /Sp cs 1 sc 30 0 10 10 re f " +
            "q 10 0 0 10 40 0 cm /Im Do Q")),
        HalftoneTests.Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8",
            new byte[] { 30, 200, 60, 10 }));
}

/// <summary>
/// A colour conversion samples each tint transform at up to 4 096 points, and spends one function budget
/// (<see cref="Core.Constants.PdfLimits.MaxFunctionWorkPerPage"/>): a tint transform looping two hundred times an
/// evaluation passes a budget of 5 000 within its 256 samples, and the spot is left in its own colour and said so;
/// under the default budget it is converted.
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.PdfLimitsCollection.Name)]
public class RgbConversionFunctionBudgetTests
{
    [Fact]
    public void ALoopingTintTransform_SpendsTheConversionsBudget()
    {
        byte[] pdf = RgbConversionTests.SpotPage("{ 0 1 199 { pop } for pop 0 0 0 1 }");
        try
        {
            Core.Constants.PdfLimits.MaxFunctionWorkPerPage = 5000;
            var (_, refused) = RgbConversionTests.ConvertToRgb(pdf);
            Assert.Contains(refused.NotConverted, n => n.Contains("could not be sampled", StringComparison.Ordinal)
                                                       && n.Contains("MaxFunctionWorkPerPage", StringComparison.Ordinal));
        }
        finally
        {
            Core.Constants.PdfLimits.ResetToDefaults();
        }

        var (_, converted) = RgbConversionTests.ConvertToRgb(pdf);
        Assert.Empty(converted.NotConverted);
        Assert.Equal(1, converted.ColourSpacesConverted);
    }
}