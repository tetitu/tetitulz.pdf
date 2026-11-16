// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The document's colour converted into CMYK through an output profile (<see cref="PdfColorSpaceConverter.ConvertToCmyk"/>,
/// GAPS.md §12d): every site a colour is set or held.
/// </summary>
/// <remarks>
/// The profile is the synthetic press of <see cref="IccOutputTransformTests"/> — black from lightness, no
/// chromatic ink — so every expected value is that table's for the colour as the renderer shows it, the function
/// the conversion is built on (the table itself is held to the reference colour engine in <c>IccOutputTransformTests</c>).
/// </remarks>
public class CmykConversionTests
{
    private static readonly byte[] Press = SyntheticCmykProfile.Build(SyntheticCmykProfile.KFromLightness());

    private static CmykConversionOptions Options(Action<CmykConversionOptionsBuilder> tweak = null)
    {
        var b = new CmykConversionOptionsBuilder();
        tweak?.Invoke(b);
        return new CmykConversionOptions
        {
            OutputIccProfile = Press,
            ConvertDeviceRgb = b.DeviceRgb, ConvertCalRgb = b.CalRgb, ConvertLab = b.Lab, ConvertIccBasedRgb = b.IccRgb,
        };
    }

    public sealed class CmykConversionOptionsBuilder
    {
        public bool DeviceRgb = true, CalRgb = true, Lab = true, IccRgb = true;
    }

    /// <summary>What the conversion should give for a colour the renderer shows as <paramref name="srgb"/>.</summary>
    private static float[] Expected(SKColor srgb)
    {
        var transform = IccOutputTransform.TryCreate(Press, IccRenderingIntent.Perceptual, true, out string reason);
        Assert.True(transform != null, reason);
        var device = new float[4];
        Assert.True(transform.TryConvert(srgb, device));
        return device.Select(v => (float)Math.Round(Math.Max(0, Math.Min(1, v)), 4)).ToArray();
    }

    [Fact]
    public void EveryColourSettingSite_IsConverted()
    {
        var (converted, result) = Convert(EverythingPage(), Options());
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var page = doc.Pages[0];
        var ops = ContentStreamParser.Parse(Content(doc, page));

        // rg / RG became k / K with the table's values.
        AssertOperands(ops, "k", 0, Expected(new SKColor(255, 0, 0)));
        AssertOperands(ops, "K", 0, Expected(new SKColor(0, 0, 255)));
        Assert.DoesNotContain(ops, o => o.Operator == "rg" || o.Operator == "RG");

        // A CalRGB space set by cs: its resource is DeviceCMYK now, its sc four numbers — the CMYK of the colour
        // the original page renders there (its D65 white adapted: blue is (0, 57, 255)).
        var spaces = (PdfDictionary)doc.ResolveObject(page.Resources[PdfName.ColorSpace]);
        Assert.Equal("DeviceCMYK", ((PdfName)doc.ResolveObject(spaces[new PdfName("CS0")])).Value);
        AssertOperands(ops, "sc", 0, Expected(Rendered(15, 95)));

        // q / Q: the space restored after Q is still the converted one, so its sc converts.
        AssertOperands(ops, "sc", 1, Expected(Rendered(15, 85)));
        Assert.NotEqual(Rendered(15, 95), Rendered(15, 85));

        // Lab, by its own space.
        Assert.Equal("DeviceCMYK", ((PdfName)doc.ResolveObject(spaces[new PdfName("CS3")])).Value);
        Assert.Equal(PdfColor.LabToSRgb(50, 20, -10), Rendered(75, 95));
        AssertOperands(ops, "sc", 2, Expected(Rendered(75, 95)));

        // An /Indexed RGB palette: CMYK entries, the index operands untouched.
        var indexed = (PdfArray)doc.ResolveObject(spaces[new PdfName("CS1")]);
        Assert.Equal("DeviceCMYK", ((PdfName)indexed[1]).Value);
        byte[] palette = ((PdfString)doc.ResolveObject(indexed[3])).RawBytes;
        Assert.Equal(Expected(new SKColor(255, 0, 0)).Select(PdfColor.Channel), palette.Take(4));
        Assert.Equal(Expected(new SKColor(0, 255, 0)).Select(PdfColor.Channel), palette.Skip(4).Take(4));
        Assert.Equal(1.0, ((PdfNumber)ops.Where(o => o.Operator == "sc").ElementAt(4).Operands[0]).DoubleValue);   // still an index

        // A spot with an RGB alternate: a DeviceCMYK alternate and a sampled tint transform through the old one.
        var spot = (PdfArray)doc.ResolveObject(spaces[new PdfName("CS2")]);
        Assert.Equal("Spot", ((PdfName)spot[1]).Value);
        Assert.Equal("DeviceCMYK", ((PdfName)spot[2]).Value);
        var tint = (PdfDictionary)doc.ResolveObject(spot[3]);
        Assert.Equal(0, ((PdfNumber)tint[PdfName.FunctionType]).IntValue);
        float[] atFull = PdfFunctionEvaluator.Evaluate(tint, new[] { 1f }, doc.ResolveObject);
        AssertNear(Expected(new SKColor(0, 128, 255)), atFull, 0.002f);
        float[] atNone = PdfFunctionEvaluator.Evaluate(tint, new[] { 0f }, doc.ResolveObject);
        AssertNear(Expected(SKColors.White), atNone, 0.002f);

        // [/Pattern /DeviceRGB]: the base is CMYK, the scn's three numbers four, the pattern name kept.
        var patternSpace = (PdfArray)doc.ResolveObject(spaces[new PdfName("Pt")]);
        Assert.Equal("DeviceCMYK", ((PdfName)patternSpace[1]).Value);
        var scn = ops.Single(o => o.Operator == "scn" && o.Operands.Last() is PdfName);
        Assert.Equal(5, scn.Operands.Length);
        Assert.Equal("P0", ((PdfName)scn.Operands[4]).Value);
        AssertNear(Expected(new SKColor(255, 0, 0)), scn.Operands.Take(4).Select(n => (float)((PdfNumber)n).DoubleValue).ToArray(), 0.0001f);

        // The axial shading: DeviceCMYK, its function sampled — at t = 0 red's CMYK, at t = 1 blue's.
        var shadings = (PdfDictionary)doc.ResolveObject(page.Resources[PdfName.Shading]);
        var shading = (PdfDictionary)doc.ResolveObject(shadings[new PdfName("Sh0")]);
        Assert.Equal("DeviceCMYK", ((PdfName)doc.ResolveObject(shading[PdfName.ColorSpace])).Value);
        var fn = (PdfDictionary)doc.ResolveObject(shading[PdfName.Function]);
        AssertNear(Expected(new SKColor(255, 0, 0)), PdfFunctionEvaluator.Evaluate(fn, new[] { 0f }, doc.ResolveObject), 0.002f);
        AssertNear(Expected(new SKColor(0, 0, 255)), PdfFunctionEvaluator.Evaluate(fn, new[] { 1f }, doc.ResolveObject), 0.002f);

        // Both images: the DeviceRGB one by the image conversion, the ICC one decoded and converted here.
        var xobjects = (PdfDictionary)doc.ResolveObject(page.Resources[PdfName.XObject]);
        foreach (string name in new[] { "Im0", "Im1" })
        {
            var image = (PdfStream)doc.ResolveObject(xobjects[new PdfName(name)]);
            Assert.Equal("DeviceCMYK", ((PdfName)doc.ResolveObject(image[PdfName.ColorSpace])).Value);
            Assert.Equal(8, image.GetDecodedData().Length);   // two pixels, four inks
        }
        var iccImage = (PdfStream)doc.ResolveObject(xobjects[new PdfName("Im1")]);
        SKColor firstPixel = IccColorTransform.ResolveAlternateColor(doc, IccSpace(doc), new float[] { 200 / 255f, 40 / 255f, 40 / 255f }, "DeviceRGB", IccRenderingIntent.Perceptual);
        Assert.Equal(Expected(firstPixel).Select(PdfColor.Channel), iccImage.GetDecodedData().Take(4));

        // The page group's blending space.
        var group = (PdfDictionary)doc.ResolveObject(page.Dictionary[PdfName.Group]);
        Assert.Equal("DeviceCMYK", ((PdfName)doc.ResolveObject(group[PdfName.CS])).Value);

        Assert.Equal(2, result.ImagesConverted);
        Assert.Equal(6, result.ColoursConverted);   // rg, RG, two CalRGB sc, the Lab sc, the pattern scn — an index and a tint stay
        Assert.Equal(1, result.ShadingsConverted);
        Assert.Empty(result.NotConverted);

        // And nothing RGB is left for the colour-space inventory to find.
        Assert.DoesNotContain(PdfColorSpaceConverter.GetColorSpaces(new MemoryStream(converted)),
            c => c.Name.Contains("RGB", StringComparison.Ordinal) || c.Name.Contains("Lab", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheChosenSpaces_Convert()
    {
        // X-3 under a CMYK intent: DeviceRGB goes, device-independent colour stays.
        var (converted, _) = Convert(EverythingPage(), Options(b => { b.CalRgb = false; b.Lab = false; b.IccRgb = false; }));
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var page = doc.Pages[0];
        var spaces = (PdfDictionary)doc.ResolveObject(page.Resources[PdfName.ColorSpace]);
        Assert.Equal("CalRGB", ((PdfName)((PdfArray)doc.ResolveObject(spaces[new PdfName("CS0")]))[0]).Value);
        Assert.Equal("Lab", ((PdfName)((PdfArray)doc.ResolveObject(spaces[new PdfName("CS3")]))[0]).Value);
        var ops = ContentStreamParser.Parse(Content(doc, page));
        Assert.Contains(ops, o => o.Operator == "k");
        Assert.Equal(3, ops.First(o => o.Operator == "sc").Operands.Length);   // the CalRGB sc untouched
        var iccImage = (PdfStream)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(page.Resources[PdfName.XObject]))[new PdfName("Im1")]);
        Assert.IsType<PdfArray>(doc.ResolveObject(iccImage[PdfName.ColorSpace]));
    }

    [Fact]
    public void WhatIsLeft_IsSaid()
    {
        // A free-form mesh shading (type 4) in DeviceRGB with its colours in its vertex data.
        byte[] mesh = new byte[] { 0, 0, 0, 0, 255, 0, 0, 0, 100, 0, 0, 0, 255, 0, 0, 0, 0, 100, 0, 0, 255 };
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /M 5 0 R >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("/M sh")),
            HalftoneTests.Stream("/ShadingType 4 /ColorSpace /DeviceRGB /BitsPerCoordinate 8 /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 255 0 255 0 1 0 1 0 1]", mesh));
        var (_, result) = Convert(pdf, Options());
        Assert.Contains(result.NotConverted, n => n.Contains("type 4 shading", StringComparison.Ordinal));
        Assert.Equal(0, result.ShadingsConverted);
    }

    [Fact]
    public void AProfileThatIsNotACmykPress_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => PdfColorSpaceConverter.ConvertToCmyk(new MemoryStream(EverythingPage()), new MemoryStream(),
            new CmykConversionOptions { OutputIccProfile = Convert64(CompactIccProfiles.DisplayP3v4) }));
        Assert.Contains("cannot take colour into CMYK", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PdfColorSpaceConverter.ConvertToCmyk(new MemoryStream(EverythingPage()), new MemoryStream(), new CmykConversionOptions()));
    }

    [RealIccProfileFact("RSWOP.icm")]
    public void ThroughRswop_RedBecomesMagentaAndYellow()
    {
        var options = new CmykConversionOptions { OutputIccProfile = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm")) };
        var (converted, _) = Convert(EverythingPage(), options);
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var k = ContentStreamParser.Parse(Content(doc, doc.Pages[0])).First(o => o.Operator == "k");
        double[] cmyk = k.Operands.Select(o => ((PdfNumber)o).DoubleValue).ToArray();
        Assert.True(cmyk[0] < 0.1 && cmyk[1] > 0.8 && cmyk[2] > 0.8, $"sRGB red on SWOP: {string.Join(", ", cmyk)}");
    }

    [Fact]
    public void AnIccSpaceThatIsNotRgb_Stays_WhereAnRgbOneConverts()
    {
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 10] /Resources << /ColorSpace << /G [/ICCBased 5 0 R] /P [/ICCBased 6 0 R] >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("/G cs 0.5 sc 0 0 10 10 re f /P cs 1 0 0 sc 10 0 10 10 re f")),
            HalftoneTests.Stream("/N 1", Convert64(CompactIccProfiles.sGreyv4)),
            HalftoneTests.Stream("/N 3", Convert64(CompactIccProfiles.DisplayP3v4)));
        var (converted, _) = Convert(pdf, Options());
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var page = doc.Pages[0];
        var spaces = (PdfDictionary)doc.ResolveObject(page.Resources[PdfName.ColorSpace]);
        Assert.Equal("DeviceCMYK", ((PdfName)doc.ResolveObject(spaces[new PdfName("P")])).Value);   // the RGB one converts
        Assert.Equal("ICCBased", ((PdfName)((PdfArray)doc.ResolveObject(spaces[new PdfName("G")]))[0]).Value);
        var ops = ContentStreamParser.Parse(Content(doc, page)).Where(o => o.Operator == "sc").ToList();
        Assert.Single(ops[0].Operands);   // the grey sc untouched
        Assert.Equal(4, ops[1].Operands.Length);
    }

    [Fact]
    public void ADeviceNOfTwoColorants_IsSampledOverBothInputs()
    {
        // (a, b) → (1 − b, 1 − a, 1): (1, 0) magenta, (0, 1) cyan — a second input read as 0 would give white.
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 10] /Resources << /ColorSpace << /D [/DeviceN [/A /B] /DeviceRGB 5 0 R] >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("/D cs 1 0 scn 0 0 10 10 re f 0 1 scn 10 0 10 10 re f")),
            HalftoneTests.Stream("/FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1]", Encoding.ASCII.GetBytes("{ 1 exch sub exch 1 exch sub 1 }")));
        var original = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        SKColor At(int x) { int i = (5 * original.Width + x) * 4; return new SKColor(original.Pixels[i], original.Pixels[i + 1], original.Pixels[i + 2]); }
        Assert.Equal(new SKColor(255, 0, 255), At(5));
        Assert.Equal(new SKColor(0, 255, 255), At(15));

        var (converted, _) = Convert(pdf, Options());
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var spaces = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.ColorSpace]);
        var deviceN = (PdfArray)doc.ResolveObject(spaces[new PdfName("D")]);
        Assert.Equal("DeviceCMYK", ((PdfName)deviceN[2]).Value);
        var tint = (PdfDictionary)doc.ResolveObject(deviceN[3]);
        Assert.Equal(0, ((PdfNumber)tint[PdfName.FunctionType]).IntValue);
        AssertNear(Expected(At(5)), PdfFunctionEvaluator.Evaluate(tint, new[] { 1f, 0f }, doc.ResolveObject), 0.002f);
        AssertNear(Expected(At(15)), PdfFunctionEvaluator.Evaluate(tint, new[] { 0f, 1f }, doc.ResolveObject), 0.002f);
    }

    [Fact]
    public void AnImagesColour_IsReadWithoutItsSoftMask_AndTheMaskKept()
    {
        // Two red samples, the second fully transparent: decoded with its mask, its colour would be lost to black.
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 20 10] /Resources << /XObject << /Im 5 0 R >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes("q 20 0 0 10 0 0 cm /Im Do Q")),
            HalftoneTests.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace [/ICCBased 7 0 R] /BitsPerComponent 8 /SMask 6 0 R", new byte[] { 255, 0, 0, 255, 0, 0 }),
            HalftoneTests.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", new byte[] { 255, 0 }),
            HalftoneTests.Stream("/N 3", Convert64(CompactIccProfiles.sRgbv4)));
        var (converted, result) = Convert(pdf, Options());
        Assert.Equal(1, result.ImagesConverted);
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var image = (PdfStream)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]))[new PdfName("Im")]);
        Assert.Equal("DeviceCMYK", ((PdfName)doc.ResolveObject(image[PdfName.ColorSpace])).Value);
        Assert.IsType<PdfStream>(doc.ResolveObject(image[PdfName.SMask]));
        byte[] samples = image.GetDecodedData();
        Assert.Equal(8, samples.Length);
        Assert.Equal(samples.Take(4), samples.Skip(4));
        Assert.NotEqual(Expected(SKColors.Black).Select(PdfColor.Channel), samples.Skip(4));   // red's ink, not black's
    }

    [Fact]
    public void AnInlineRgbImage_IsConverted()
    {
        byte[] content = Encoding.ASCII.GetBytes("q 10 0 0 10 0 0 cm BI /W 1 /H 1 /CS /RGB /BPC 8 ID ")
            .Concat(new byte[] { 255, 0, 0 }).Concat(Encoding.ASCII.GetBytes(" EI Q")).ToArray();
        byte[] pdf = HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R >>",
            HalftoneTests.Stream("", content));
        var (converted, result) = Convert(pdf, Options());
        Assert.Equal(1, result.ImagesConverted);
        using var doc = PdfDocument.Open(new MemoryStream(converted));
        var inline = (PdfStream)ContentStreamParser.Parse(Content(doc, doc.Pages[0])).Single(o => o.Operator == "BI").Operands[0];
        var space = (PdfName)(inline[PdfName.ColorSpace] ?? inline[PdfName.CS]);
        Assert.Equal("DeviceCMYK", PdfColorSpaceNames.Expand(space.Value));
    }

    // ------------------------------------------------------------------ fixtures

    private static (byte[] Output, ColourConversionResult Result) Convert(byte[] pdf, CmykConversionOptions options)
    {
        using var output = new MemoryStream();
        var result = PdfColorSpaceConverter.ConvertToCmyk(new MemoryStream(pdf), output, options);
        return (output.ToArray(), result);
    }

    private static byte[] Convert64(string base64) => System.Convert.FromBase64String(base64);

    private static RenderedPage _original;

    /// <summary>The original page's pixel at (<paramref name="x"/>, <paramref name="y"/>), 72 dpi, y down — the colour as the renderer shows it.</summary>
    private static SKColor Rendered(int x, int y)
    {
        _original ??= PdfPageRenderer.RenderPageToPixels(new MemoryStream(EverythingPage()), 0, 72);
        int i = (y * _original.Width + x) * 4;
        return new SKColor(_original.Pixels[i], _original.Pixels[i + 1], _original.Pixels[i + 2]);
    }

    private static PdfArray IccSpace(PdfDocument doc)
    {
        // The original file's ICC space, for the expected pixel: rebuild it from the fixture.
        using var original = PdfDocument.Open(new MemoryStream(EverythingPage()));
        var image = (PdfStream)original.ResolveObject(((PdfDictionary)original.ResolveObject(original.Pages[0].Resources[PdfName.XObject]))[new PdfName("Im1")]);
        var space = new PdfArray();
        space.Add(new PdfName("ICCBased"));
        var profile = new PdfStream { RawData = ((PdfStream)original.ResolveObject(((PdfArray)original.ResolveObject(image[PdfName.ColorSpace]))[1])).GetDecodedData() };
        profile.Set(PdfName.N, new PdfNumber(3));
        space.Add(profile);
        return space;
    }

    private static byte[] Content(PdfDocument doc, PdfPage page)
        => ((PdfStream)doc.ResolveObject(page.Dictionary[PdfName.Contents])).GetDecodedData();

    private static void AssertOperands(List<ContentStreamOperation> ops, string op, int nth, float[] expected)
    {
        var found = ops.Where(o => o.Operator == op).ElementAt(nth);
        AssertNear(expected, found.Operands.Select(n => (float)((PdfNumber)n).DoubleValue).ToArray(), 0.0001f);
    }

    private static void AssertNear(float[] expected, float[] actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"component {i}: {actual[i]}, expected {expected[i]}");
    }

    /// <summary>
    /// One page touching every site: DeviceRGB fill and stroke, CalRGB, Lab, an /Indexed RGB palette, a spot with an
    /// RGB alternate, an uncoloured pattern over DeviceRGB, an RGB axial shading, a DeviceRGB and an ICC RGB image,
    /// a q / Q around a colour space, and a page group in DeviceRGB.
    /// </summary>
    internal static byte[] EverythingPage()
    {
        string content =
            "1 0 0 rg 0 0 10 10 re f 0 0 1 RG 2 w 0 0 10 10 re S " +
            "/CS0 cs 0 1 0 sc 10 0 10 10 re f q 0 g Q 0 0 1 sc 10 10 10 10 re f " +
            "/CS3 cs 50 20 -10 sc 70 0 10 10 re f " +
            "/CS1 cs 0 sc 20 0 10 10 re f 1 sc 30 0 10 10 re f " +
            "/CS2 cs 1 scn 40 0 10 10 re f " +
            "/Pt cs 1 0 0 /P0 scn 50 0 10 10 re f " +
            "q 60 0 10 10 re W n /Sh0 sh Q " +
            "q 10 0 0 10 0 20 cm /Im0 Do Q q 10 0 0 10 10 20 cm /Im1 Do Q";
        return HalftoneTests.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceRGB >> " +
            "/Resources << /ColorSpace << /CS0 [/CalRGB << /WhitePoint [0.9505 1 1.089] >>] /CS1 [/Indexed /DeviceRGB 1 <FF000000FF00>] " +
            "/CS2 [/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [0 0.5 1] /N 1 >>] " +
            "/CS3 [/Lab << /WhitePoint [0.9642 1 0.8249] >>] /Pt [/Pattern /DeviceRGB] >> " +
            "/Pattern << /P0 8 0 R >> /Shading << /Sh0 << /ShadingType 2 /ColorSpace /DeviceRGB /Coords [60 0 70 0] " +
            "/Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >> >> " +
            "/XObject << /Im0 5 0 R /Im1 6 0 R >> >> /Contents 4 0 R >>",
            HalftoneTests.Stream("", Encoding.ASCII.GetBytes(content)),
            HalftoneTests.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", new byte[] { 200, 40, 40, 30, 160, 90 }),
            HalftoneTests.Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace [/ICCBased 7 0 R] /BitsPerComponent 8", new byte[] { 200, 40, 40, 30, 160, 90 }),
            HalftoneTests.Stream("/N 3", Convert64(CompactIccProfiles.DisplayP3v4)),
            HalftoneTests.Stream("/Type /Pattern /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 4 4] /XStep 4 /YStep 4 /Resources << >>",
                Encoding.ASCII.GetBytes("0 0 2 2 re f")));
    }
}
