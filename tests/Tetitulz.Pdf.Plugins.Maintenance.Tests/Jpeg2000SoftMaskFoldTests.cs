// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="ImageOptimizationOptions.FoldSoftMaskIntoJpeg2000"/>: under a JPEG 2000 target, an
/// image whose transparency is a plain <c>/SMask</c> is re-encoded with the mask as the codestream's
/// opacity channel (<c>/SMaskInData 1</c>) and still renders transparent; off, or for a mask that
/// cannot be folded, the image keeps its separate mask and the lossless fallback.
/// </summary>
public sealed class Jpeg2000SoftMaskFoldTests
{
    private const int Width = 64, Height = 48;

    /// <summary>A noisy reddish image — lossy coding must pay — under a mask transparent on the left half.</summary>
    private static byte[] MaskedImagePdf(string maskExtras = "", string imageExtras = "", int maskScale = 1)
    {
        var random = new Random(3);
        var rgb = new byte[Width * Height * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = (byte)(200 + random.Next(40));
            rgb[i + 1] = (byte)random.Next(40);
            rgb[i + 2] = (byte)random.Next(40);
        }
        int maskWidth = Width / maskScale, maskHeight = Height / maskScale;
        var mask = new byte[maskWidth * maskHeight];
        for (int y = 0; y < maskHeight; y++)
            for (int x = 0; x < maskWidth; x++)
                mask[y * maskWidth + x] = (byte)(x < maskWidth / 2 ? 0 : 255);

        // Drawn 4x so each image pixel covers several page pixels at 72 dpi.
        string content = $"q {Width * 4} 0 0 {Height * 4} 100 400 cm /Im0 Do Q";
        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        var offsets = new long[7];

        Write("%PDF-1.5\n");
        offsets[1] = ms.Position; Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position; Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position; Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        offsets[4] = ms.Position; Write($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = ms.Position; Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {Width} /Height {Height} "
            + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R {imageExtras}/Length {rgb.Length} >>\nstream\n");
        ms.Write(rgb, 0, rgb.Length);
        Write("\nendstream\nendobj\n");
        offsets[6] = ms.Position; Write($"6 0 obj\n<< /Type /XObject /Subtype /Image /Width {maskWidth} /Height {maskHeight} "
            + $"/ColorSpace /DeviceGray /BitsPerComponent 8 {maskExtras}/Length {mask.Length} >>\nstream\n");
        ms.Write(mask, 0, mask.Length);
        Write("\nendstream\nendobj\n");
        long xref = ms.Position;
        Write("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write($"trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] Optimize(byte[] pdf, bool fold, out OptimizationResult result)
    {
        var options = new OptimizationOptions
        {
            CompressStreams = false, RemoveDuplicates = false, RemoveUnusedObjects = false,
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true, TargetFormat = ImageFormat.Jpeg2000, Quality = 80,
                FoldSoftMaskIntoJpeg2000 = fold, SkipAlreadyOptimized = false, MinImageSizeBytes = 0,
            },
        };
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        result = PdfOptimizer.Optimize(input, output, options);
        return output.ToArray();
    }

    private static PdfStream Image(PdfDocument doc)
    {
        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]);
        var xobjects = (PdfDictionary)doc.ResolveObject(resources[PdfName.XObject]);
        return (PdfStream)doc.ResolveObject(xobjects[new PdfName("Im0")]);
    }

    /// <summary>The page pixel at a point inside the image's left (masked out) or right (shown) half.</summary>
    private static (SKColor Left, SKColor Right) Halves(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var page = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        int y = page.Height - 1 - (400 + Height * 2);
        return (page.GetPixel(100 + Width, y), page.GetPixel(100 + Width * 3, y));
    }

    /// <summary>
    /// Folded: the image is a JPEG 2000 file declaring its opacity channel, the separate mask is
    /// gone, and the page still shows the white page through the left half and the red image on
    /// the right — the opacity channel read back by the renderer, not merely written.
    /// </summary>
    [Fact]
    public void Folded_TheMaskBecomesTheOpacityChannel_AndStillMasks()
    {
        byte[] optimized = Optimize(MaskedImagePdf(), fold: true, out _);
        using (var doc = PdfDocument.Open(new MemoryStream(optimized)))
        {
            PdfStream image = Image(doc);
            Assert.Equal("JPXDecode", ((PdfName)doc.ResolveObject(image[PdfName.Filter])).Value);
            Assert.Equal(1, ((PdfNumber)image[PdfName.SMaskInData]).IntValue);
            Assert.Null(image[PdfName.SMask]);

            // The file itself declares the fourth channel opacity (a cdef box), not a fourth colour —
            // what a reader other than this one needs to find it.
            Assert.True(Tetitulz.Pdf.Core.Imaging.Jpx.JpxHeader.TryReadImageSize(image.RawData, out _, out _, out int components));
            Assert.Equal(4, components);
            Assert.True(Tetitulz.Pdf.Core.Imaging.Jpx.JpxHeader.TryReadColourComponents(image.RawData, out int colour));
            Assert.Equal(3, colour);
        }

        var (left, right) = Halves(optimized);
        Assert.True(left.Red > 240 && left.Green > 240 && left.Blue > 240, $"the masked-out half should show the page, was {left}");
        Assert.True(right.Red > 170 && right.Green < 90 && right.Blue < 90, $"the shown half should be the red image, was {right}");
    }

    /// <summary>Off — the default — an image with a soft mask keeps it, and is not re-encoded lossily.</summary>
    [Fact]
    public void NotFolded_TheImageKeepsItsMask()
    {
        byte[] optimized = Optimize(MaskedImagePdf(), fold: false, out _);
        using var doc = PdfDocument.Open(new MemoryStream(optimized));
        PdfStream image = Image(doc);
        Assert.NotNull(image[PdfName.SMask]);
        Assert.Null(image[PdfName.SMaskInData]);
        Assert.NotEqual("JPXDecode", (doc.ResolveObject(image[PdfName.Filter]) as PdfName)?.Value);
    }

    /// <summary>
    /// A mask that will not decode cannot be folded, and then the image is not re-encoded lossily
    /// either: a JPEG 2000 colour beside a separate mask is the misregistration folding exists to
    /// avoid, so the image keeps its mask and the lossless fallback.
    /// </summary>
    [Fact]
    public void AnUndecodableMask_IsNotFolded_AndTheImageStaysLossless()
    {
        byte[] optimized = Optimize(MaskedImagePdf("/Filter /DCTDecode "), fold: true, out _);
        using var doc = PdfDocument.Open(new MemoryStream(optimized));
        PdfStream image = Image(doc);
        Assert.NotNull(image[PdfName.SMask]);
        Assert.Null(image[PdfName.SMaskInData]);
        Assert.NotEqual("JPXDecode", (doc.ResolveObject(image[PdfName.Filter]) as PdfName)?.Value);
    }

    /// <summary>
    /// A mask with <c>/Matte</c> — premultiplied colour, which <c>/SMaskInData 1</c> does not
    /// describe — is not folded even when asked, and the result says why.
    /// </summary>
    [Fact]
    public void AMatteMask_IsNotFolded_AndSaysSo()
    {
        var reports = new System.Collections.Generic.List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(string detail) { if (Environment.CurrentManagedThreadId == own) reports.Add(detail); }
        Tetitulz.Pdf.Base.Operations.DocumentEditDiagnostics.OnEvent += Sink;
        byte[] optimized;
        try { optimized = Optimize(MaskedImagePdf("/Matte [1 1 1] "), fold: true, out _); }
        finally { Tetitulz.Pdf.Base.Operations.DocumentEditDiagnostics.OnEvent -= Sink; }

        using var doc = PdfDocument.Open(new MemoryStream(optimized));
        PdfStream image = Image(doc);
        Assert.NotNull(image[PdfName.SMask]);
        Assert.Null(image[PdfName.SMaskInData]);
        Assert.Contains(reports, r => r.Contains("/Matte"));
    }

    /// <summary>
    /// A mask of its own size — half the image's on each side here, which §11.6.5.3 allows — is
    /// resampled to the samples it is folded beside, and still masks the left half.
    /// </summary>
    [Fact]
    public void AMaskOfAnotherSize_IsResampledAndFolded()
    {
        byte[] optimized = Optimize(MaskedImagePdf(maskScale: 2), fold: true, out _);
        using (var doc = PdfDocument.Open(new MemoryStream(optimized)))
            Assert.Equal(1, ((PdfNumber)Image(doc)[PdfName.SMaskInData]).IntValue);

        var (left, right) = Halves(optimized);
        Assert.True(left.Red > 240 && left.Green > 240 && left.Blue > 240, $"the masked-out half should show the page, was {left}");
        Assert.True(right.Red > 170 && right.Green < 90 && right.Blue < 90, $"the shown half should be the red image, was {right}");
    }

    /// <summary>
    /// An image that also carries a <c>/Mask</c>, or already declares <c>/SMaskInData</c>, is not
    /// folded: one opacity channel cannot stand for two masks, and a declared one is not this
    /// optimiser's to replace. Each refusal says so.
    /// </summary>
    [Theory]
    [InlineData("/Mask 6 0 R ", "/Mask")]
    [InlineData("/SMaskInData 1 ", "/SMaskInData")]
    public void AnImageWithAnotherMask_IsNotFolded(string imageExtras, string named)
    {
        var reports = new System.Collections.Generic.List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(string detail) { if (Environment.CurrentManagedThreadId == own) reports.Add(detail); }
        Tetitulz.Pdf.Base.Operations.DocumentEditDiagnostics.OnEvent += Sink;
        byte[] optimized;
        try { optimized = Optimize(MaskedImagePdf(imageExtras: imageExtras), fold: true, out _); }
        finally { Tetitulz.Pdf.Base.Operations.DocumentEditDiagnostics.OnEvent -= Sink; }

        using var doc = PdfDocument.Open(new MemoryStream(optimized));
        Assert.NotNull(Image(doc)[PdfName.SMask]);
        Assert.Contains(reports, r => r.Contains("not folded") && r.Contains(named));
    }
}
