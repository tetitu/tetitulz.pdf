// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The image pass over a <c>JPXDecode</c> image: decoded through Core's public JPEG 2000 facade and
/// re-encoded like a JPEG, which keeps the JPX-to-JPEG mapping in <c>DetectOriginalFormat</c>
/// reachable.
/// </summary>
public sealed class JpxImageOptimizationTests
{
    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
    private static JpxLosslessFixtures.Fixture Gray => JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;

    private static OptimizationOptions ImagePass(ImageFormat format) => new OptimizationOptions
    {
        CompressStreams = false,
        RemoveDuplicates = false,
        RemoveUnusedObjects = false,
        ImageOptimization = new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = format,
            Quality = 90,
            SkipAlreadyOptimized = false,
            MinImageSizeBytes = 0,
        },
    };

    /// <summary>
    /// A lossless RGB codestream, re-encoded as a JPEG when the caller asks for one: the output
    /// image is DCTDecode, its geometry the codestream's, and its bytes exactly what Skia encodes
    /// from the fixture's own samples at the same quality — so any sample the decoder got wrong,
    /// in any channel, changes the bytes. (Under <see cref="ImageFormat.Original"/> a JPEG 2000
    /// image stays JPEG 2000; <c>Jpeg2000RecompressionTests</c> holds that.)
    /// </summary>
    [Fact]
    public void ImagePass_ReEncodesAJpxImageAsJpeg_WhenAsked_FromTheDecodedSamples()
    {
        byte[] pdf = JpxPdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8");

        using var output = new MemoryStream();
        OptimizationResult result = PdfOptimizer.Optimize(new MemoryStream(pdf), output, ImagePass(ImageFormat.Jpeg));

        Assert.Equal(1, result.ImagesOptimized);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.DCTDecode, doc.ResolveObject(image[PdfName.Filter]) as PdfName);
        Assert.Equal(PdfName.DeviceRGB, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);

        // The same encoder over the same samples, as the optimiser's JPEG arm calls it.
        using var expected = new SKBitmap(Rgb.Width, Rgb.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (int y = 0; y < Rgb.Height; y++)
            for (int x = 0; x < Rgb.Width; x++)
            {
                int i = y * Rgb.Width + x;
                expected.SetPixel(x, y, new SKColor(Rgb.Planes[0][i], Rgb.Planes[1][i], Rgb.Planes[2][i]));
            }
        using SKImage expectedImage = SKImage.FromBitmap(expected);
        using SKData expectedJpeg = expectedImage.Encode(SKEncodedImageFormat.Jpeg, 90);
        Assert.Equal(expectedJpeg.ToArray(), image.RawData);

        // Potency: the bytes see a sample — one changed pixel encodes differently.
        expected.SetPixel(3, 5, new SKColor(0, 0, 0));
        using SKImage changed = SKImage.FromBitmap(expected);
        using SKData changedJpeg = changed.Encode(SKEncodedImageFormat.Jpeg, 90);
        Assert.NotEqual(changedJpeg.ToArray(), image.RawData);
    }

    /// <summary>The same image without a /ColorSpace: the codestream's own colour (DeviceRGB by count) decodes alike, and a grey one re-encodes as grey.</summary>
    [Fact]
    public void ImagePass_ReadsAJpxImagesOwnColour_WhenTheDictionaryHasNone()
    {
        byte[] grey = JpxPdf(Gray.Codestream, Gray.Width, Gray.Height, "");

        using var output = new MemoryStream();
        OptimizationResult result = PdfOptimizer.Optimize(new MemoryStream(grey), output, ImagePass(ImageFormat.Png));

        Assert.Equal(1, result.ImagesOptimized);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.FlateDecode, doc.ResolveObject(image[PdfName.Filter]) as PdfName);
        Assert.Equal(PdfName.DeviceGray, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);
        // The samples are the fixture's within one level: the Flate arm reads them back out of
        // an SKBitmap, whose grey is quantised once on the way (the optimiser's own round trip,
        // not the decoder's). A decode through the wrong bytes would miss by tens.
        byte[] samples = image.GetDecodedData();
        Assert.Equal(Gray.Planes[0].Length, samples.Length);
        int off = 0;
        for (int i = 0; i < samples.Length; i++)
            if (Math.Abs(samples[i] - Gray.Planes[0][i]) > 1) off++;
        Assert.Equal(0, off);
    }

    /// <summary>
    /// Three the pass leaves alone by what the dictionary says, before a sample is decoded: a
    /// stencil mask, an Indexed image (its samples are palette
    /// indices), and one with a /Decode array (the re-encoded samples would lose the mapping).
    /// The control, the same image with the entry removed, is re-encoded.
    /// </summary>
    [Theory]
    [InlineData("/ImageMask true", "stencil mask")]
    [InlineData("/ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>] /BitsPerComponent 8", "/Indexed palette")]
    [InlineData("/ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0]", "/Decode array")]
    public void ImagePass_LeavesAStencilAnIndexedAndADecodedJpxImageAlone_WithTheReason(string extras, string reason)
    {
        byte[] pdf = JpxPdf(Gray.Codestream, Gray.Width, Gray.Height, extras);
        DocumentEditDiagnostics.Clear();

        using var output = new MemoryStream();
        OptimizationResult result = PdfOptimizer.Optimize(new MemoryStream(pdf), output, ImagePass(ImageFormat.Png));

        Assert.Equal(0, result.ImagesOptimized);
        Assert.Contains(reason, DocumentEditDiagnostics.LastError ?? "", StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(Gray.Codestream, Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5)).RawData);

        using var controlOutput = new MemoryStream();
        OptimizationResult control = PdfOptimizer.Optimize(new MemoryStream(JpxPdf(Gray.Codestream, Gray.Width, Gray.Height, "/ColorSpace /DeviceGray /BitsPerComponent 8")), controlOutput, ImagePass(ImageFormat.Png));
        Assert.Equal(1, control.ImagesOptimized);
    }

    /// <summary>An identity /Decode ([0 1] per component) maps nothing: the image is re-encoded like one without the key — the refusal is for a mapping, not for the key's presence.</summary>
    [Fact]
    public void ImagePass_ReEncodesAJpxImageWithAnIdentityDecode()
    {
        byte[] pdf = JpxPdf(Gray.Codestream, Gray.Width, Gray.Height, "/ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [0 1]");
        using var output = new MemoryStream();
        OptimizationResult result = PdfOptimizer.Optimize(new MemoryStream(pdf), output, ImagePass(ImageFormat.Png));
        Assert.Equal(1, result.ImagesOptimized);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.FlateDecode, doc.ResolveObject(image[PdfName.Filter]) as PdfName);
        Assert.Null(image[PdfName.Decode]);
    }

    /// <summary>The raw arm, audited with the JPEG 2000 one: a Flate-less grey image with /Decode [1 0] was re-encoded uninverted and the array dropped — inverted; it is left alone now, with the same reason.</summary>
    [Fact]
    public void ImagePass_LeavesARawImageWithADecodeArrayAlone_AsTheJpxArmDoes()
    {
        byte[] samples = Gray.Planes[0];
        byte[] pdf = JpxPdf(samples, Gray.Width, Gray.Height, "/ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0]", filter: "");
        DocumentEditDiagnostics.Clear();

        using var output = new MemoryStream();
        OptimizationResult result = PdfOptimizer.Optimize(new MemoryStream(pdf), output, ImagePass(ImageFormat.Png));
        Assert.Equal(0, result.ImagesOptimized);
        Assert.Contains("/Decode array", DocumentEditDiagnostics.LastError ?? "", StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(samples, Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5)).RawData);

        using var controlOutput = new MemoryStream();
        OptimizationResult control = PdfOptimizer.Optimize(new MemoryStream(JpxPdf(samples, Gray.Width, Gray.Height, "/ColorSpace /DeviceGray /BitsPerComponent 8", filter: "")), controlOutput, ImagePass(ImageFormat.Png));
        Assert.Equal(1, control.ImagesOptimized);
    }

    /// <summary>An undecodable codestream is left as it was, with the decoder's reason on record.</summary>
    [Fact]
    public void ImagePass_LeavesAnUndecodableJpxCodestreamAlone_WithTheReason()
    {
        byte[] garbage = new byte[2048];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = (byte)(i * 37 + 11);
        garbage[0] = 0xFF; garbage[1] = 0x4F; garbage[2] = 0xFF; garbage[3] = 0x51;
        byte[] pdf = JpxPdf(garbage, 8, 8, "/ColorSpace /DeviceRGB /BitsPerComponent 8");
        DocumentEditDiagnostics.Clear();

        using var output = new MemoryStream();
        OptimizationResult result = PdfOptimizer.Optimize(new MemoryStream(pdf), output, ImagePass(ImageFormat.Jpeg));

        Assert.Equal(0, result.ImagesOptimized);
        Assert.Contains("JPEG 2000 codestream could not be decoded", DocumentEditDiagnostics.LastError ?? "", StringComparison.Ordinal);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(garbage, image.RawData);
    }

    /// <summary>A page exactly the image's size, filled by one JPXDecode image XObject (object 5). Binary-safe.</summary>
    private static byte[] JpxPdf(byte[] jpx, int width, int height, string extras, string filter = "/Filter /JPXDecode")
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        string content = $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q";
        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets.Add(acc.Count);
        Emit($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} {extras} {filter} /Length {jpx.Length} >>\nstream\n");
        acc.AddRange(jpx);
        Emit("\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
