// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The optimiser's <see cref="ImageFormat.Jpeg2000"/> target: a raw RGB image is
/// re-encoded as a <c>/JPXDecode</c> JP2 file the in-house decoder reads back close to the
/// original, a lower quality costs quality and saves bytes, a greyscale image stays greyscale,
/// and a JPEG 2000 image under <see cref="ImageFormat.Original"/> stays JPEG 2000 rather than
/// becoming a JPEG.
/// </summary>
public sealed class Jpeg2000RecompressionTests
{
    private const int Width = 64, Height = 48;

    /// <summary>A photograph-like image: a gradient with noise, so that a lossy encoding is smaller than the raw samples and Flate.</summary>
    private static byte[] NoisyRgb(int seed = 1)
    {
        var samples = new byte[Width * Height * 3];
        var random = new Random(seed);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 3;
                samples[i] = (byte)Math.Min(255, x * 4 + random.Next(24));
                samples[i + 1] = (byte)Math.Min(255, y * 5 + random.Next(24));
                samples[i + 2] = (byte)Math.Min(255, 128 + random.Next(48));
            }
        return samples;
    }

    private static byte[] BuildPdfWithImage(byte[] data, string filter, string colorSpace, int width = Width, int height = Height)
    {
        string content = $"q {width} 0 0 {height} 100 600 cm /Im0 Do Q";
        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.5\n");
        var offsets = new long[6];
        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        offsets[4] = ms.Position;
        Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = ms.Position;
        string filterKey = filter == null ? "" : $"/Filter /{filter} ";
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} "
            + $"/ColorSpace /{colorSpace} /BitsPerComponent 8 {filterKey}/Length {data.Length} >>\nstream\n");
        ms.Write(data, 0, data.Length);
        Write("\nendstream\nendobj\n");
        long xref = ms.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static OptimizationOptions ImagesOnly(ImageFormat target, int quality) => new OptimizationOptions
    {
        CompressStreams = false,
        RemoveDuplicates = false,
        RemoveUnusedObjects = false,
        ImageOptimization = new ImageOptimizationOptions { Enabled = true, TargetFormat = target, Quality = quality },
    };

    private static byte[] Optimize(byte[] input, OptimizationOptions options)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        PdfOptimizer.Optimize(i, o, options);
        return o.ToArray();
    }

    private static PdfStream ImageStream(byte[] pdf, PdfDocument document)
    {
        PdfObject Deref(PdfObject o)
        {
            int guard = 0;
            while (o is PdfReference r && guard++ < 32) o = document.GetObject(r.ReferenceObjectNumber);
            return o;
        }
        var page = Deref(document.Pages[0].Dictionary) as PdfDictionary;
        var resources = Deref(page[PdfName.Resources]) as PdfDictionary;
        var xobjects = Deref(resources[PdfName.XObject]) as PdfDictionary;
        var image = Deref(xobjects[new PdfName("Im0")]) as PdfStream;
        Assert.NotNull(image);
        return image;
    }

    private static (string Filter, string ColorSpace, byte[] Raw) StoredImage(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        PdfStream image = ImageStream(pdf, document);
        PdfObject filter = image[PdfName.Filter];
        if (filter is PdfReference reference) filter = document.GetObject(reference.ReferenceObjectNumber);
        return ((filter as PdfName)?.Value, (image[PdfName.ColorSpace] as PdfName)?.Value, image.RawData);
    }

    private static double Psnr(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        double sum = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            double d = expected[i] - actual[i];
            sum += d * d;
        }
        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / (sum / expected.Length));
    }

    [Fact]
    public void Jpeg2000Target_ReencodesARawRgbImage_AsAJp2TheDecoderReadsBack()
    {
        byte[] rgb = NoisyRgb();
        byte[] pdf = BuildPdfWithImage(rgb, null, "DeviceRGB");

        byte[] optimized = Optimize(pdf, ImagesOnly(ImageFormat.Jpeg2000, 85));

        (string filter, string colorSpace, byte[] raw) = StoredImage(optimized);
        Assert.Equal(("JPXDecode", "DeviceRGB"), (filter, colorSpace));
        Assert.True(JpxHeader.IsJp2Container(raw), "the stream is not a JP2 file");
        Assert.True(raw.Length < rgb.Length * 3 / 4, $"{raw.Length} bytes for {rgb.Length} raw");
        Assert.True(JpxImageDecoding.TryDecode(raw, JpxImageDictionaryFacts.None, out JpxImagePixels pixels, out string reason), reason);
        Assert.Equal((Width, Height, 3), (pixels.Width, pixels.Height, pixels.ColourComponents));
        double psnr = Psnr(rgb, pixels.Colour);
        Assert.True(psnr > 34, $"{psnr:F1} dB at quality 85");
    }

    [Fact]
    public void Jpeg2000Target_LowerQuality_CostsQualityAndSavesBytes()
    {
        byte[] rgb = NoisyRgb(2);
        byte[] pdf = BuildPdfWithImage(rgb, null, "DeviceRGB");
        (_, _, byte[] fine) = StoredImage(Optimize(pdf, ImagesOnly(ImageFormat.Jpeg2000, 90)));
        (_, _, byte[] coarse) = StoredImage(Optimize(pdf, ImagesOnly(ImageFormat.Jpeg2000, 30)));
        Assert.True(coarse.Length < fine.Length, $"quality 30 took {coarse.Length} bytes, 90 took {fine.Length}");
        Assert.True(JpxImageDecoding.TryDecode(fine, JpxImageDictionaryFacts.None, out JpxImagePixels finePixels, out string reason), reason);
        Assert.True(JpxImageDecoding.TryDecode(coarse, JpxImageDictionaryFacts.None, out JpxImagePixels coarsePixels, out reason), reason);
        double finePsnr = Psnr(rgb, finePixels.Colour), coarsePsnr = Psnr(rgb, coarsePixels.Colour);
        Assert.True(finePsnr > coarsePsnr + 5, $"quality 90: {finePsnr:F1} dB, quality 30: {coarsePsnr:F1} dB");
    }

    [Fact]
    public void Jpeg2000Target_KeepsAGreyImageGrey()
    {
        var grey = new byte[Width * Height];
        var random = new Random(3);
        for (int i = 0; i < grey.Length; i++) grey[i] = (byte)Math.Min(255, (i % Width) * 4 + random.Next(20));
        byte[] pdf = BuildPdfWithImage(grey, null, "DeviceGray");

        (string filter, string colorSpace, byte[] raw) = StoredImage(Optimize(pdf, ImagesOnly(ImageFormat.Jpeg2000, 80)));
        Assert.Equal(("JPXDecode", "DeviceGray"), (filter, colorSpace));
        Assert.True(JpxHeader.TryReadColourComponents(raw, out int components));
        Assert.Equal(1, components);
    }

    /// <summary>Under <see cref="ImageFormat.Original"/> a JPEG 2000 image is re-encoded as JPEG 2000 at the quality asked, not sent to DCTDecode as a JPEG.</summary>
    [Fact]
    public void OriginalFormat_KeepsAJpeg2000Image_JpxDecode()
    {
        byte[] rgb = NoisyRgb(4);
        byte[] lossless = JpxImageEncoding.Encode(rgb, Width, Height, 3, 8, null);
        byte[] pdf = BuildPdfWithImage(lossless, "JPXDecode", "DeviceRGB");

        byte[] optimized = Optimize(pdf, ImagesOnly(ImageFormat.Original, 60));

        (string filter, _, byte[] raw) = StoredImage(optimized);
        Assert.Equal("JPXDecode", filter);
        Assert.True(raw.Length < lossless.Length, $"the lossy re-encoding took {raw.Length} bytes against {lossless.Length} lossless");
        Assert.True(JpxImageDecoding.TryDecode(raw, JpxImageDictionaryFacts.None, out JpxImagePixels pixels, out string reason), reason);
        Assert.True(Psnr(rgb, pixels.Colour) > 28);

        // The control: asked for JPEG, the same image becomes a JPEG.
        (string asJpeg, _, _) = StoredImage(Optimize(pdf, ImagesOnly(ImageFormat.Jpeg, 60)));
        Assert.Equal("DCTDecode", asJpeg);
    }
}
