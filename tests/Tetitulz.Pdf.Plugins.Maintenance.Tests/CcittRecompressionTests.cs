// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// CCITT Group 4 re-encoding of bilevel images. The claim that has to be tested is
/// <b>bit-exactness</b> — the pass rewrites how samples are stored and must not change one of them
/// — so these tests decode the rewritten stream and compare it to the original raster. Asserting
/// only that the file shrank would pass for a pass that wrote a smaller, wrong image.
/// </summary>
public class CcittRecompressionTests
{
    private const int Width = 64;
    private const int Height = 64;
    private const int Stride = (Width + 7) / 8;

    /// <summary>Left half black, right half white: long runs, which is what G4 is good at.</summary>
    private static byte[] LongRunRaster()
    {
        var raster = new byte[Stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Stride; x++)
                raster[y * Stride + x] = x < Stride / 2 ? (byte)0x00 : (byte)0xFF;
        return raster;
    }

    /// <summary>
    /// Every pixel alternates, so each scanline is 64 runs of one pixel. G4 codes a run change per
    /// pixel and comes out larger than the raw raster.
    /// </summary>
    private static byte[] AlternatingRaster()
    {
        var raster = new byte[Stride * Height];
        for (int i = 0; i < raster.Length; i++) raster[i] = 0xAA;
        return raster;
    }

    [Fact]
    public void Optimize_ReEncodesALongRunBilevelImageAsCcittGroup4()
    {
        byte[] raster = LongRunRaster();
        byte[] pdf = BuildPdfWithBilevelImage(raster);

        var result = Optimize(pdf, CcittOnly(), out byte[] optimized);

        Assert.Equal(1, result.BilevelImagesRecompressed);
        Assert.Equal("CCITTFaxDecode", FilterNameOf(optimized));
    }

    /// <summary>
    /// The whole safety claim of the pass: the samples that come back out are the samples that went
    /// in. A G4 coder that dropped or shifted a run would still produce a smaller, valid image.
    /// </summary>
    [Fact]
    public void Optimize_PreservesEveryBilevelSampleThroughTheCcittRoundTrip()
    {
        byte[] raster = LongRunRaster();
        byte[] pdf = BuildPdfWithBilevelImage(raster);

        Optimize(pdf, CcittOnly(), out byte[] optimized);

        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    [Fact]
    public void Optimize_LeavesTheImageAloneWhenGroup4WouldBeLarger()
    {
        byte[] raster = AlternatingRaster();
        byte[] pdf = BuildPdfWithBilevelImage(raster);

        var result = Optimize(pdf, CcittOnly(), out byte[] optimized);

        Assert.Equal(0, result.BilevelImagesRecompressed);
        Assert.Null(FilterNameOf(optimized));                  // still unfiltered
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    [Fact]
    public void Optimize_DoesNotTouchBilevelImagesWhenTheOptionIsOff()
    {
        byte[] raster = LongRunRaster();
        byte[] pdf = BuildPdfWithBilevelImage(raster);

        var options = CcittOnly();
        options.UseCcittForBilevelImages = false;

        var result = Optimize(pdf, options, out byte[] optimized);

        Assert.Equal(0, result.BilevelImagesRecompressed);
        Assert.Null(FilterNameOf(optimized));
    }

    /// <summary>
    /// An 8-bit greyscale image is not a bilevel raster; feeding its samples to a G4 coder would
    /// reinterpret one byte per pixel as eight pixels.
    /// </summary>
    [Fact]
    public void Optimize_IgnoresAnEightBitImage()
    {
        var raster = new byte[Width * Height];          // 8 bpc, one byte per pixel
        for (int i = 0; i < raster.Length; i++) raster[i] = (byte)(i % 2 == 0 ? 0x00 : 0xFF);
        byte[] pdf = BuildPdfWithBilevelImage(raster, bitsPerComponent: 8);

        var result = Optimize(pdf, CcittOnly(), out byte[] optimized);

        Assert.Equal(0, result.BilevelImagesRecompressed);
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// A dictionary whose declared geometry does not match the sample count is not the raster it
    /// claims to be; encoding the rows that do fit would silently discard the rest.
    /// </summary>
    [Fact]
    public void Optimize_RefusesAnImageWhoseSampleCountContradictsItsGeometry()
    {
        byte[] raster = LongRunRaster();
        // Declare one more row than the data holds.
        byte[] pdf = BuildPdfWithBilevelImage(raster, declaredHeight: Height + 1);

        var result = Optimize(pdf, CcittOnly(), out byte[] optimized);

        Assert.Equal(0, result.BilevelImagesRecompressed);
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// Under <see cref="OptimizationOptions.Lossless"/> this pass no longer has bilevel images to
    /// itself, and the test that used to demand a CCITT result here has been renamed rather than
    /// re-asserted, because the behaviour genuinely changed.
    ///
    /// <para><b>What changed.</b> The PNG predictor pass (step 1i) used to require
    /// <c>/BitsPerComponent 8</c>, so it never saw a bilevel raster; it now runs on every permitted
    /// depth and on <c>/ImageMask</c> stencils, and the palette pass (step 1h2) can turn a
    /// two-tone image into a 1-bit <c>/Indexed</c> one. Both run <i>before</i> this pass, and this
    /// pass commits only when Group 4 beats what is in the stream at that moment — so the file ends
    /// up with the smallest of the three rather than with CCITT unconditionally. That ordering is
    /// the point: each pass measures against the current bytes.</para>
    ///
    /// <para>The invariant worth keeping is unchanged and is what is asserted: whatever the passes
    /// chose, the samples survive it, and adding passes never makes the stored image larger.
    /// <c>PdfFilterNames.IsImageCodec</c> listing <c>CCITTFaxDecode</c> is still what stops
    /// <see cref="OptimizationOptions.RecompressExistingStreams"/> from re-Flating or
    /// double-encoding a G4 stream; losing that entry would break the round trip below.</para>
    /// </summary>
    [Fact]
    public void Optimize_KeepsWhicheverEncodingIsSmallerUnderTheFullLosslessPreset()
    {
        byte[] raster = LongRunRaster();
        byte[] pdf = BuildPdfWithBilevelImage(raster);

        Optimize(pdf, CcittOnly(), out byte[] ccittOnly);
        Optimize(pdf, OptimizationOptions.Lossless, out byte[] lossless);

        Assert.Equal(raster, DecodedImageSamples(lossless));

        int chosen = StoredImageLength(lossless);
        int ccitt = StoredImageLength(ccittOnly);
        Assert.True(chosen <= ccitt,
            $"the full preset stored {chosen} bytes where the CCITT pass alone stored {ccitt}");

        // Since the JBIG2 pass joined the preset (GAPS.md §11e), the half-black square may take it.
        string filter = FilterNameOf(lossless);
        Assert.True(filter == "CCITTFaxDecode" || filter == "FlateDecode" || filter == "JBIG2Decode",
            $"the bilevel image was stored as /{filter}");
    }

    /// <summary>
    /// Image re-encoding (step 1b) runs <i>before</i> this pass (step 1i2) and, when a caller asks
    /// for <see cref="ImageFormat.Jpeg"/>, would happily turn a bitonal scan into an 8-bit JPEG —
    /// lossy, and for bilevel content substantially <b>larger</b> than the source. It does not,
    /// only because the decode path refuses anything that is not 8 bits per component.
    ///
    /// <para>That guard lives in a third method and nothing ties it to either pass, so the two are
    /// complementary by coincidence rather than by construction. If it ever widens to 1 bpc, a
    /// scanned page would silently get worse under <c>Balanced</c> and <c>Aggressive</c> than under
    /// <c>Lossless</c> — and no other test would notice, because the output would still be a valid,
    /// smaller-than-nothing JPEG.</para>
    /// </summary>
    [Fact]
    public void Optimize_JpegImageOptimizationDoesNotClaimBilevelImagesFromTheCcittPass()
    {
        byte[] raster = LongRunRaster();
        byte[] pdf = BuildPdfWithBilevelImage(raster);

        var options = CcittOnly();
        options.ImageOptimization = new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Jpeg,
            Quality = 75,
            SkipAlreadyOptimized = false,
            MinImageSizeBytes = 0,
        };

        var result = Optimize(pdf, options, out byte[] optimized);

        Assert.Equal(1, result.BilevelImagesRecompressed);
        Assert.Equal("CCITTFaxDecode", FilterNameOf(optimized));
        Assert.Equal(raster, DecodedImageSamples(optimized));
    }

    /// <summary>
    /// At a width that is not a multiple of eight, the last byte of every row carries padding bits
    /// that are <b>not sample data</b> (§8.9.3), and a Group 4 round trip does not preserve them:
    /// the coder codes exactly <c>/Columns</c> pixels and the decoder regenerates the tail as white.
    ///
    /// <para>This pins the distinction the pass's own summary used to blur. It said the encoder and
    /// decoder round-trip "byte-for-byte", which is true only when the width divides by eight —
    /// every other fixture in this file is 64 wide, so nothing here could see it. What the pass
    /// actually guarantees, and all it needs to guarantee, is that <b>every pixel</b> survives; the
    /// bits past the last pixel of a row are ignored by any reader.</para>
    ///
    /// <para>Both halves are asserted, because asserting only the pixels would leave the stronger
    /// claim looking untested rather than false.</para>
    /// </summary>
    [Fact]
    public void Optimize_PreservesEveryPixelButNotThePaddingBitsAtARaggedWidth()
    {
        const int width = 101;
        int stride = (width + 7) / 8;

        var raster = new byte[stride * Height];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < width; x++)
                if ((x / 17 + y / 5) % 2 == 0)                     // long runs, so G4 wins
                    raster[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));

        byte[] pdf = BuildPdfWithBilevelImage(raster, width: width);

        var result = Optimize(pdf, CcittOnly(), out byte[] optimized);

        Assert.Equal(1, result.BilevelImagesRecompressed);
        Assert.Equal("CCITTFaxDecode", FilterNameOf(optimized));

        byte[] after = DecodedImageSamples(optimized);
        Assert.Equal(raster.Length, after.Length);

        // Every pixel: identical.
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < width; x++)
            {
                int before = (raster[y * stride + x / 8] >> (7 - x % 8)) & 1;
                int now = (after[y * stride + x / 8] >> (7 - x % 8)) & 1;
                Assert.Equal(before, now);
            }

        // The padding: not preserved, and this fixture proves it rather than assuming it.
        Assert.NotEqual(raster, after);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// Only the CCITT pass, so a size change cannot come from Flate, dedup or object streams.
    /// </summary>
    private static OptimizationOptions CcittOnly() => new OptimizationOptions
    {
        UseCcittForBilevelImages = true,
        CompressStreams = false,
        RemoveDuplicates = false,
        RemoveUnusedObjects = false,
    };

    internal static byte[] BuildPdfWithBilevelImage(byte[] samples, int bitsPerComponent = 1,
        int declaredHeight = Height, int width = Width)
    {
        string content = $"q {width} 0 0 64 100 600 cm /Im0 Do Q";

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
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
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {declaredHeight} "
            + $"/ColorSpace /DeviceGray /BitsPerComponent {bitsPerComponent} "
            + $"/Length {samples.Length} >>\nstream\n");
        ms.Write(samples, 0, samples.Length);
        Write("\nendstream\nendobj\n");

        long xref = ms.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    private static PdfStream ImageStream(byte[] pdf, PdfDocument document)
    {
        PdfObject Deref(PdfObject o)
        {
            int guard = 0;
            while (o is PdfReference r && guard++ < 32) o = document.GetObject(r.ReferenceObjectNumber);
            return o;
        }

        var catalog = Deref(document.Trailer[PdfName.Root]) as PdfDictionary;
        var pages = Deref(catalog[PdfName.Pages]) as PdfDictionary;
        var kids = Deref(pages[PdfName.Kids]) as PdfArray;
        var page = Deref(kids[0]) as PdfDictionary;
        var resources = Deref(page[PdfName.Resources]) as PdfDictionary;
        var xobjects = Deref(resources[PdfName.XObject]) as PdfDictionary;
        var image = Deref(xobjects[new PdfName("Im0")]) as PdfStream;

        Assert.NotNull(image);
        return image;
    }

    internal static string FilterNameOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var image = ImageStream(pdf, document);
        var filter = image[PdfName.Filter];
        if (filter is PdfReference reference) filter = document.GetObject(reference.ReferenceObjectNumber);
        return (filter as PdfName)?.Value;
    }

    internal static byte[] DecodedImageSamples(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var image = ImageStream(pdf, document);

        Assert.True(image.TryGetDecodedData(64 * 1024 * 1024, out byte[] samples),
            "the image stream did not decode");
        return samples;
    }

    /// <summary>Bytes the image occupies in the file, whatever filter it ended up under.</summary>
    internal static int StoredImageLength(byte[] pdf) => StoredImageBytes(pdf).Length;

    /// <summary>The image's stored (still encoded) bytes.</summary>
    internal static byte[] StoredImageBytes(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return ImageStream(pdf, document).RawData;
    }

    internal static OptimizationResult Optimize(byte[] input, OptimizationOptions options,
        out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }
}
