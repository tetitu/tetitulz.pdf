// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Flattening to the page's separations (<see cref="FlattenedColorSpace.Cmyk"/>), what PDF/X-1a needs:
/// <c>/DeviceCMYK</c> from the process plates, and <c>/DeviceN</c> carrying a spot ink as a channel of
/// its own — never the spot dropped, which writing the four process plates alone would do.
/// </summary>
/// <remarks>
/// The page is 200×100, flattened at 36 dpi to 100×50 pixels: opaque black on its left half, then at
/// half opacity cyan across the middle — so a pixel at x 5 is black alone, and one at x 70 (140 pt) is
/// cyan alone, at half its tint.
/// </remarks>
public class CmykFlatteningTests
{
    private const string ProcessArt = "0 0 0 1 k 0 0 100 100 re f /GS0 gs 1 0 0 0 k 50 25 100 50 re f\n";

    [Fact]
    public void AProcessOnlyPage_IsFlattenedToDeviceCmyk_InkForInk()
    {
        PdfStream image = FlattenedImage(Pdf(ProcessArt, ""), out PdfDocument doc);
        using (doc)
        {
            Assert.Equal(PdfName.DeviceCMYK, image[PdfName.ColorSpace]);
            byte[] s = image.GetDecodedData();
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(s, 100, 4, 5, 25));             // black alone
            byte[] cyan = Pixel(s, 100, 4, 70, 25);                                         // cyan alone, half tint
            Assert.InRange(cyan[0], 126, 129);
            Assert.Equal(new byte[] { 0, 0, 0 }, cyan.Skip(1).ToArray());
        }
    }

    /// <summary>
    /// A page painting a spot ink becomes <c>/DeviceN</c>: the four process inks, then the spot, so the
    /// spot keeps a plate of its own; its channel holds its tint where it was painted — half, under the
    /// half opacity.
    /// </summary>
    [Fact]
    public void ASpotInk_IsAChannelOfItsOwn()
    {
        string art = "0 0 0 1 k 0 0 100 100 re f /GS0 gs /CS0 cs 1 scn 50 25 100 50 re f\n";
        PdfStream image = FlattenedImage(Pdf(art, "/ColorSpace << /CS0 [/Separation /Rubine /DeviceCMYK 6 0 R] >>"), out PdfDocument doc);
        using (doc)
        {
            var space = Assert.IsType<PdfArray>(doc.ResolveObject(image[PdfName.ColorSpace]));
            Assert.Equal("DeviceN", ((PdfName)space[0]).Value);
            var names = ((PdfArray)doc.ResolveObject(space[1])).Select(n => ((PdfName)n).Value).ToArray();
            Assert.Equal(new[] { "Cyan", "Magenta", "Yellow", "Black", "Rubine" }, names);
            Assert.Equal(PdfName.DeviceCMYK, space[2]);

            byte[] s = image.GetDecodedData();
            byte[] spot = Pixel(s, 100, 5, 70, 25);
            Assert.InRange(spot[4], 126, 129);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, spot.Take(4).ToArray());

            // The alternate the image carries: Rubine alone at full tint is its own CMYK, (0, 1, 1, 0).
            float[] alternate = Core.Functions.PdfFunctionEvaluator.EvaluateEntry(space[3], new float[] { 0, 0, 0, 0, 1 }, doc.ResolveObject);
            Assert.Equal(new[] { 0f, 1f, 1f, 0f }, alternate.Select(v => (float)System.Math.Round(v, 3)).ToArray());
        }
    }

    /// <summary>
    /// A spot whose alternate is DeviceCMYK is its tint transform's CMYK as it stands — (0.2, 0.9, 0.6,
    /// 0.1) — where a trip through RGB would come back as (0, 0.875, 0.5, 0.28).
    /// </summary>
    [Fact]
    public void ASpotWithACmykAlternate_IsItsOwnCmyk()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf("", "")));
        var space = (PdfArray)doc.ResolveObject(new PdfReference(8, 0));
        float[] cmyk = PdfTransparencyFlattener.SpotAsCmyk("Mauve", space, doc);

        Assert.Equal(new[] { 0.2f, 0.9f, 0.6f, 0.1f }, cmyk.Select(v => (float)System.Math.Round(v, 3)).ToArray());
    }

    /// <summary>
    /// The DeviceN alternate adds each spot's ink, as CMYK, into the process inks and clamps at 1: Rubine
    /// at full tint is (0, 1, 1, 0) by its own tint transform, so with cyan at 0.5 the result is
    /// (0.5, 1, 1, 0), and with magenta already at 0.6 magenta stays 1.
    /// </summary>
    [Theory]
    [InlineData(new float[] { 0, 0, 0, 0, 1 }, new float[] { 0, 1, 1, 0 })]
    [InlineData(new float[] { 0.5f, 0, 0, 0, 1 }, new float[] { 0.5f, 1, 1, 0 })]
    [InlineData(new float[] { 0, 0.6f, 0, 0.2f, 0.5f }, new float[] { 0, 1, 0.5f, 0.2f })]
    public void TheSpotTintTransform_AddsTheSpotsInk_AndClamps(float[] inputs, float[] expected)
    {
        PdfStream function = PdfTransparencyFlattener.SpotTintTransform(new List<float[]> { new float[] { 0, 1, 1, 0 } });
        float[] result = Core.Functions.PdfFunctionEvaluator.Evaluate(function, inputs);
        Assert.Equal(expected.Length, result.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], result[i], 3);
    }

    /// <summary>
    /// A spot whose alternate is RGB reaches CMYK through its colour at full tint and the exact inverse of
    /// the CMYK conversion: orange (1, 0.5, 0) is no cyan, half magenta, full yellow, no black.
    /// </summary>
    [Fact]
    public void ASpotWithAnRgbAlternate_IsItsColourAsCmyk()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf("", "")));
        var space = (PdfArray)doc.ResolveObject(new PdfReference(7, 0));
        float[] cmyk = PdfTransparencyFlattener.SpotAsCmyk("Orange", space, doc);

        Assert.Equal(0f, cmyk[0], 2);
        Assert.InRange(cmyk[1], 0.49f, 0.51f);
        Assert.Equal(1f, cmyk[2], 2);
        Assert.Equal(0f, cmyk[3], 2);
    }

    [Fact]
    public void AnUnusableOutputProfile_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => PdfTransparencyFlattener.Flatten(new MemoryStream(Pdf(ProcessArt, "")),
            new MemoryStream(), new TransparencyFlattenOptions { ColorSpace = FlattenedColorSpace.Cmyk, OutputIccProfile = new byte[] { 1, 2, 3 } }));
        Assert.Contains("output ICC profile", ex.Message);
    }

    private static PdfStream FlattenedImage(byte[] pdf, out PdfDocument doc)
    {
        using var output = new MemoryStream();
        var result = PdfTransparencyFlattener.Flatten(new MemoryStream(pdf), output,
            new TransparencyFlattenOptions { Dpi = 36, ColorSpace = FlattenedColorSpace.Cmyk, KeepTextSearchable = false });
        Assert.Equal(new[] { 0 }, result.FlattenedPages);

        doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]);
        var xobjects = (PdfDictionary)doc.ResolveObject(resources[PdfName.XObject]);
        return (PdfStream)doc.ResolveObject(xobjects.First().Value);
    }

    /// <summary>The channels of pixel (x, y) — y counted from the image's top row — of an interleaved image.</summary>
    private static byte[] Pixel(byte[] samples, int width, int channels, int x, int y)
        => samples.Skip((y * width + x) * channels).Take(channels).ToArray();

    /// <summary>
    /// One page drawing <paramref name="content"/>; object 5 is the half-opaque graphics state, 6 Rubine's
    /// tint transform (to 0 1 1 0), 7 an Orange separation over DeviceRGB, 8 a Mauve one over DeviceCMYK.
    /// </summary>
    private static byte[] Pdf(string content, string extraResources)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /ExtGState << /GS0 5 0 R >> {extraResources} >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /ca 0.5 >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>",
            "[/Separation /Orange /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0.5 0] /N 1 >>]",
            "[/Separation /Mauve /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0.2 0.9 0.6 0.1] /N 1 >>]",
        };

        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(ms.Length);
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Length;
        var sb = new StringBuilder($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }
}
