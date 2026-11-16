// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// Palette (<c>/Indexed</c>) conversion.
///
/// <para>The claim is that this is <b>exactly lossless</b>, so almost every test here reconstructs
/// the RGB samples from the converted image — indices out of the stream, colours out of the palette
/// — and compares them byte for byte with what went in. Asserting that the file shrank would pass
/// for a pass that quantised the image to 256 colours, which is precisely the thing this must never
/// become.</para>
///
/// <para>The rest of the tests are about the refusals, and those are where the real risk lives: a
/// <c>/Decode</c> array, a colour-key <c>/Mask</c> and an image serving as someone's <c>/SMask</c>
/// all still <i>convert</i> perfectly well and still produce a valid PDF — one that draws the wrong
/// thing.</para>
/// </summary>
public class IndexedImageTests
{
    // Large enough that the palette form genuinely beats plain Flate on the same samples, which is
    // the bar the pass sets for itself. A 40x30 block-coloured fixture does not clear it -- three
    // bytes repeated in long runs deflate about as well as the indices do -- so it would have
    // measured the rule rather than the transform.
    private const int Width = 96;
    private const int Height = 96;

    // ------------------------------------------------------------------
    //  Rasters
    // ------------------------------------------------------------------

    /// <summary>Four colours, scattered: a dithered chart or a screen shot of one.</summary>
    private static byte[] FourColourRgb() => RasterWithDistinctColours(4);

    /// <summary>
    /// <paramref name="colours"/> distinct colours of <paramref name="components"/> components each,
    /// scattered over an image of the given size. The high byte of the entry number is carried in a
    /// channel of its own so entries stay distinct past 256.
    /// </summary>
    private static byte[] ScatteredRaster(int width, int height, int components, int colours)
    {
        var table = new byte[colours][];
        for (int i = 0; i < colours; i++)
        {
            table[i] = new byte[components];
            table[i][0] = (byte)(i & 0xFF);
            if (components > 1) table[i][1] = (byte)(i >> 8);
            if (components > 2) table[i][2] = (byte)(i * 29);
            if (components > 3) table[i][3] = (byte)(200 + i);
        }

        var samples = new byte[width * height * components];
        var random = new Random(1234);

        for (int pixel = 0, at = 0; pixel < width * height; pixel++, at += components)
        {
            byte[] colour = table[pixel < colours ? pixel : random.Next(colours)];
            for (int component = 0; component < components; component++)
                samples[at + component] = colour[component];
        }

        return samples;
    }

    /// <summary>Every pixel a different colour: a photograph, and past the 256-entry ceiling.</summary>
    private static byte[] ManyColourRgb()
    {
        var samples = new byte[Width * Height * 3];
        var random = new Random(99);
        random.NextBytes(samples);
        return samples;
    }

    // ------------------------------------------------------------------
    //  The lossless claim
    // ------------------------------------------------------------------

    [Fact]
    public void Optimize_ReExpressesAFourColourImageOverAPaletteAndKeepsEverySampleExact()
    {
        byte[] raster = FourColourRgb();
        byte[] pdf = BuildPdfWithImage(raster);

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPalettized);
        Assert.Equal(raster, ReconstructRgb(optimized));
    }

    /// <summary>
    /// Four colours need two bits, not eight. Without the packing the pass would store four times
    /// the indices it has to, and the saving assertion below would still pass.
    /// </summary>
    [Fact]
    public void Optimize_PacksTheIndexToTheWidthThePaletteActuallyNeeds()
    {
        byte[] pdf = BuildPdfWithImage(FourColourRgb());

        Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(2, BitsPerComponentOf(optimized));
    }

    [Theory]
    [InlineData(2, 1)]        // 2 colours  -> 1 bit
    [InlineData(3, 2)]        // 3 colours  -> 2 bits
    [InlineData(16, 4)]       // 16 colours -> 4 bits
    [InlineData(17, 8)]       // 17 colours -> 8 bits
    [InlineData(256, 8)]      // the ceiling
    public void Optimize_ChoosesTheIndexWidthAndStaysLosslessAtEveryPaletteSize(
        int colours, int expectedBits)
    {
        byte[] raster = RasterWithDistinctColours(colours);
        byte[] pdf = BuildPdfWithImage(raster);

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPalettized);
        Assert.Equal(expectedBits, BitsPerComponentOf(optimized));
        Assert.Equal(colours - 1, HivalOf(optimized));
        Assert.Equal(raster, ReconstructRgb(optimized));
    }

    /// <summary>
    /// A width whose packed row does <b>not</b> fill whole bytes, which is where sub-byte packing
    /// goes wrong: 101 pixels at two bits is 202 bits, so each row carries six bits of padding and
    /// the next row must start on the following byte boundary (§8.9.3). Every other fixture here is
    /// 96 pixels wide, which divides exactly at 1, 2, 4 and 8 bits and so cannot see the bug.
    /// </summary>
    [Theory]
    [InlineData(101, 37, 4)]          // 2 bits: 6 padding bits per row
    [InlineData(101, 37, 13)]         // 4 bits: 4 padding bits per row
    [InlineData(99, 51, 2)]           // 1 bit:  1 padding bit per row
    // 8 bits: rowBytes == width, so no padding at all -- the control. It needs enough pixels to
    // amortise a 200-entry palette: at 37x11 the palette costs more than the image saves and the
    // pass correctly declines, which would have made this a test of the size rule.
    [InlineData(101, 83, 200)]
    public void Optimize_IsLosslessAtAWidthWhosePackedRowsDoNotFillWholeBytes(
        int width, int height, int colours)
    {
        byte[] raster = ScatteredRaster(width, height, 3, colours);
        byte[] pdf = BuildPdfWithImage(raster, width: width, height: height);

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPalettized);
        Assert.Equal(raster, ReconstructSamples(optimized, 3));
    }

    /// <summary>
    /// Four components, where the colour key packs into all four bytes of an int. A sign or shift
    /// error in that packing shows up here and nowhere else — three components leave the top byte
    /// zero, which hides it.
    /// </summary>
    [Fact]
    public void Optimize_IsLosslessForAFourComponentCmykImage()
    {
        byte[] raster = ScatteredRaster(61, 41, 4, 12);
        byte[] pdf = BuildPdfWithImage(raster, colorSpace: "/DeviceCMYK", width: 61, height: 41);

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPalettized);
        Assert.Equal(raster, ReconstructSamples(optimized, 4));
    }

    /// <summary>
    /// §8.6.6.3 caps <c>hival</c> at 255. An image needing a 257th colour must be left alone, never
    /// quantised down to fit — a colour reduction is exactly what this pass promises not to be.
    /// </summary>
    [Fact]
    public void Optimize_RefusesAnImageNeedingMoreThanTwoHundredAndFiftySixColours()
    {
        byte[] raster = RasterWithDistinctColours(300);
        byte[] pdf = BuildPdfWithImage(raster);

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPalettized);
        Assert.Equal("DeviceRGB", ColorSpaceNameOf(optimized));
    }

    [Fact]
    public void Optimize_LeavesAPhotographicImageAlone()
    {
        byte[] pdf = BuildPdfWithImage(ManyColourRgb());

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPalettized);
        Assert.Equal("DeviceRGB", ColorSpaceNameOf(optimized));
    }

    /// <summary>The saving is the point of the pass, so it is asserted rather than assumed.</summary>
    [Fact]
    public void Optimize_StoresAPalettableImageInFewerBytesThanBefore()
    {
        byte[] pdf = BuildPdfWithImage(FourColourRgb());

        Optimize(pdf, IndexedOnly(), out byte[] optimized);

        int before = StoredImageLength(pdf);
        int after = StoredImageLength(optimized);
        Assert.True(after < before, $"stored {after} bytes where the original stored {before}");
    }

    // ------------------------------------------------------------------
    //  The refusals
    // ------------------------------------------------------------------

    /// <summary>
    /// §8.9.6.4: a colour-key <c>/Mask</c> is a range of <b>sample values in the image's colour
    /// space</b>. After conversion the samples are palette indices, so the range would mask
    /// whichever colours happened to land on those index numbers.
    /// </summary>
    [Fact]
    public void Optimize_RefusesAnImageWithAColourKeyMaskWhoseRangesNameTheOldSamples()
    {
        byte[] raster = FourColourRgb();
        byte[] pdf = BuildPdfWithImage(raster, extraKeys: "/Mask [200 255 0 40 0 40]");

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPalettized);
        Assert.Equal("DeviceRGB", ColorSpaceNameOf(optimized));
    }

    /// <summary>
    /// §8.9.5.2: a <c>/Decode</c> array is stated in the source space and arity. An <c>/Indexed</c>
    /// image's default is <c>[0 2^bpc−1]</c>, so a three-component array inherited onto a
    /// one-component indexed image is both wrong and the wrong length.
    /// </summary>
    [Fact]
    public void Optimize_RefusesAnImageWithADecodeArrayStatedInTheOldColourSpace()
    {
        byte[] pdf = BuildPdfWithImage(FourColourRgb(), extraKeys: "/Decode [1 0 1 0 1 0]");

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPalettized);
        Assert.Equal("DeviceRGB", ColorSpaceNameOf(optimized));
    }

    /// <summary>
    /// §11.6.5.3 requires a soft mask to be a greyscale image. Converting one to <c>/Indexed</c>
    /// leaves a perfectly valid image that is no longer a legal soft mask — and the page it masks
    /// renders with the wrong transparency, not with an error.
    ///
    /// <para>The fixture's mask is two-valued over 1 200 pixels, so it is <b>more</b> palettable
    /// than the colour image beside it: without the guard it would convert to a 1-bit indexed image
    /// at an eighth of the size, which is exactly why "it got smaller" is not the assertion. The
    /// colour image converting is what proves the pass ran at all — a guard that skipped the whole
    /// document would satisfy the mask assertion just as well.</para>
    /// </summary>
    [Fact]
    public void Optimize_RefusesAnImageThatAnotherImageUsesAsItsSoftMask()
    {
        byte[] pdf = BuildPdfWithSoftMaskedImage();

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal("DeviceGray", SoftMaskColorSpaceNameOf(optimized));
        Assert.Equal(SoftMaskStoredLength(pdf), SoftMaskStoredLength(optimized));
        Assert.Equal(1, result.ImagesPalettized);          // the colour image, not the mask
    }

    /// <summary>
    /// The palette must be judged against what the <b>other</b> passes would have achieved, not
    /// against the bytes sitting in the stream. <c>CompressStreams</c> runs after this pass and
    /// deflates the original samples anyway, so comparing against an uncompressed original credits
    /// this pass with a saving it did not produce — and then charges an uncompressed palette on top
    /// of it.
    ///
    /// <para>Measured, not theorised: on a corpus document whose two images converted under the
    /// stale baseline, the image streams came out 2 349 bytes smaller and the <i>file</i> came out
    /// 170 bytes larger. The last two assertions are what make this a test of the rule — the
    /// fixture must genuinely beat the stored bytes, or the pass would be declining for the wrong
    /// reason.</para>
    /// </summary>
    [Fact]
    public void Optimize_DeclinesAPaletteThatLosesToPlainFlateEvenThoughItBeatsTheStoredBytes()
    {
        byte[] raster = PeriodicRaster(256);
        byte[] pdf = BuildPdfWithImage(raster);

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPalettized);
        Assert.Equal("DeviceRGB", ColorSpaceNameOf(optimized));

        var built = PdfIndexedImageConverter.TryBuild(raster, Width, Height, 3);
        Assert.NotNull(built);

        int indexed = FlateEncodeFilter.Encode(built.Indices, CompressionLevel.SmallestSize).Length
                      + built.Palette.Length * 2;
        int plain = FlateEncodeFilter.Encode(raster, CompressionLevel.SmallestSize).Length;

        Assert.True(indexed < raster.Length,
            $"the fixture does not exercise the rule: the palette form ({indexed}) must beat the " +
            $"{raster.Length} stored bytes, or the old rule would have declined too");
        Assert.True(plain < indexed,
            $"the fixture does not exercise the rule: plain Flate ({plain}) must beat the palette " +
            $"form ({indexed})");
    }

    /// <summary>An already-indexed image cannot be the base of another indexed space (§8.6.6.3).</summary>
    [Fact]
    public void Optimize_LeavesAnAlreadyIndexedImageAlone()
    {
        byte[] pdf = BuildPdfWithIndexedImage();

        var result = Optimize(pdf, IndexedOnly(), out _);

        Assert.Equal(0, result.ImagesPalettized);
    }

    /// <summary>
    /// An ordinary greyscale image must come out unchanged. A one-byte sample holds at most 256
    /// distinct values, so every 8-bpc greyscale image "fits a palette" trivially and its index
    /// would be eight bits wide as well: conversion buys a lookup table and a changed colour space
    /// and nothing else.
    ///
    /// <para>It did convert, briefly, and a predictor test caught it by getting its samples back
    /// renumbered. <b>Two independent rules now refuse it</b> — the index-width guard
    /// (<c>BitsPerComponent &gt;= components * 8</c>) and the plain-Flate baseline — and mutation
    /// testing says the second is what actually decides this fixture: deleting the width guard
    /// leaves every test green. The guard is kept for stating the intent directly, not because
    /// this test pins it, and the name says only what is observable.</para>
    /// </summary>
    [Fact]
    public void Optimize_LeavesAnOrdinaryGreyscaleImageUnchanged()
    {
        var raster = new byte[Width * Height];
        var random = new Random(5);
        for (int i = 0; i < raster.Length; i++) raster[i] = (byte)random.Next(200);   // 200 distinct greys

        byte[] pdf = BuildPdfWithImage(raster, colorSpace: "/DeviceGray");

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(0, result.ImagesPalettized);
        Assert.Equal("DeviceGray", ColorSpaceNameOf(optimized));
    }

    /// <summary>
    /// The greyscale case that <i>is</i> worth having, and the reason the guard above is a width
    /// comparison rather than a flat "no single-component images": two tones pack to one bit, an
    /// eightfold reduction the CCITT pass cannot reach because that one requires the image to be
    /// one bit already.
    /// </summary>
    [Fact]
    public void Optimize_PacksATwoToneGreyscaleImageToOneBitPerPixel()
    {
        var raster = new byte[Width * Height];
        var random = new Random(6);
        for (int i = 0; i < raster.Length; i++) raster[i] = (byte)(random.Next(2) == 0 ? 0x00 : 0xFF);

        byte[] pdf = BuildPdfWithImage(raster, colorSpace: "/DeviceGray");

        var result = Optimize(pdf, IndexedOnly(), out byte[] optimized);

        Assert.Equal(1, result.ImagesPalettized);
        Assert.Equal(1, BitsPerComponentOf(optimized));
        Assert.Equal(raster, ReconstructGray(optimized));
    }

    [Fact]
    public void Optimize_DoesNotConvertWhenTheOptionIsOff()
    {
        byte[] pdf = BuildPdfWithImage(FourColourRgb());

        var options = IndexedOnly();
        options.ConvertImagesToIndexed = false;

        var result = Optimize(pdf, options, out byte[] optimized);

        Assert.Equal(0, result.ImagesPalettized);
        Assert.Equal("DeviceRGB", ColorSpaceNameOf(optimized));
    }

    /// <summary>
    /// The full preset runs the predictor and the CCITT passes after this one, and both read
    /// <c>/BitsPerComponent</c> and the colour space this one just rewrote. Nothing structural makes
    /// them agree, so the composition is pinned on the samples rather than on inspection.
    /// </summary>
    [Fact]
    public void Optimize_KeepsTheSamplesExactUnderTheFullLosslessPreset()
    {
        byte[] raster = FourColourRgb();
        byte[] pdf = BuildPdfWithImage(raster);

        Optimize(pdf, OptimizationOptions.Lossless, out byte[] optimized);

        Assert.Equal(raster, ReconstructRgb(optimized));
    }

    // ------------------------------------------------------------------
    //  PdfIndexedImageConverter directly
    // ------------------------------------------------------------------

    /// <summary>
    /// Rows pad to a byte boundary (§8.9.3). Packing them continuously would shift every row after
    /// the first by a few bits, which decodes without error and shears the image.
    /// </summary>
    [Fact]
    public void TryBuild_PadsEachPackedRowToAByteBoundary()
    {
        // 5 pixels at 2 bits is 10 bits: two bytes per row, six bits of padding.
        const int width = 5, height = 4;
        var samples = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            byte value = (byte)((i % 3) * 80);
            samples[i * 3] = value;
            samples[i * 3 + 1] = value;
            samples[i * 3 + 2] = value;
        }

        var built = PdfIndexedImageConverter.TryBuild(samples, width, height, 3);

        Assert.NotNull(built);
        Assert.Equal(2, built.BitsPerComponent);
        Assert.Equal(2 * height, built.Indices.Length);
    }

    [Fact]
    public void TryBuild_RefusesASampleCountThatContradictsTheGeometry()
    {
        var samples = new byte[Width * Height * 3 - 1];

        Assert.Null(PdfIndexedImageConverter.TryBuild(samples, Width, Height, 3));
    }

    [Fact]
    public void TryBuild_OrdersThePaletteByFirstAppearanceSoIndicesAndEntriesAgree()
    {
        var samples = new byte[]
        {
            9, 8, 7,
            1, 2, 3,
            9, 8, 7,
        };

        var built = PdfIndexedImageConverter.TryBuild(samples, 3, 1, 3);

        Assert.NotNull(built);
        Assert.Equal(2, built.PaletteEntries);
        Assert.Equal(new byte[] { 9, 8, 7, 1, 2, 3 }, built.Palette);

        // 1 bit per index, one row: 0,1,0 in the high bits.
        Assert.Equal(new byte[] { 0b0100_0000 }, built.Indices);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static OptimizationOptions IndexedOnly() => new OptimizationOptions
    {
        ConvertImagesToIndexed = true,
        CompressStreams = false,
        RemoveDuplicates = false,
        RemoveUnusedObjects = false,
    };

    /// <summary>
    /// A raster that cycles through <paramref name="colours"/> distinct colours in a fixed period,
    /// so the RGB bytes deflate well on their own -- which is what makes it beat its own palette
    /// form and so exercise the baseline rule.
    /// </summary>
    private static byte[] PeriodicRaster(int colours)
    {
        var samples = new byte[Width * Height * 3];
        for (int pixel = 0, at = 0; pixel < Width * Height; pixel++, at += 3)
        {
            int index = pixel % colours;
            samples[at] = (byte)index;
            samples[at + 1] = (byte)(255 - index);
            samples[at + 2] = (byte)((index * 3) & 0xFF);
        }
        return samples;
    }

    /// <summary>
    /// An image using exactly <paramref name="colours"/> distinct RGB triples, scattered rather
    /// than laid out in runs.
    ///
    /// <para>Scattered on purpose. In runs, three repeated bytes deflate as cheaply as one repeated
    /// index and the palette form wins nothing — which is a true fact about run-coloured images and
    /// a useless fixture for this pass. Scattering is also what the real images that pay for this
    /// look like: the corpus images it converts are screen shots and anti-aliased charts.</para>
    /// </summary>
    private static byte[] RasterWithDistinctColours(int colours)
    {
        // The high byte of the index is carried in a channel of its own, so entries stay distinct
        // past 256 -- without it, colour i and colour i+256 collide and a "301 colours" fixture
        // silently has 256, which is exactly the boundary the refusal test is about.
        var table = new byte[colours][];
        for (int i = 0; i < colours; i++)
            table[i] = new byte[] { (byte)(i & 0xFF), (byte)(i >> 8), (byte)(i * 29) };

        var samples = new byte[Width * Height * 3];
        var random = new Random(1234);

        for (int pixel = 0, at = 0; pixel < Width * Height; pixel++, at += 3)
        {
            // The first `colours` pixels guarantee every entry appears; the rest are scattered.
            byte[] colour = table[pixel < colours ? pixel : random.Next(colours)];
            samples[at] = colour[0];
            samples[at + 1] = colour[1];
            samples[at + 2] = colour[2];
        }

        return samples;
    }

    /// <summary>
    /// Reads the converted image back to RGB: indices out of the stream at whatever width they were
    /// packed to, colours out of the palette. This is the whole lossless claim, so it resolves the
    /// palette from the file rather than from the fixture.
    /// </summary>
    private static byte[] ReconstructRgb(byte[] pdf) => ReconstructSamples(pdf, 3);

    private static byte[] ReconstructSamples(byte[] pdf, int components)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var image = ImageStream(document);

        var space = Resolve(document, image[PdfName.ColorSpace]) as PdfArray;
        Assert.NotNull(space);
        Assert.Equal("Indexed", ((PdfName)Resolve(document, space[0])).Value);

        byte[] palette = (Resolve(document, space[3]) as PdfString)?.RawBytes;
        Assert.NotNull(palette);

        int bits = ((PdfNumber)Resolve(document, image[PdfName.BitsPerComponent])).IntValue;
        int width = ((PdfNumber)Resolve(document, image[PdfName.Width])).IntValue;
        int height = ((PdfNumber)Resolve(document, image[PdfName.Height])).IntValue;

        Assert.True(image.TryGetDecodedData(64L * 1024 * 1024, out byte[] packed),
            "the indexed image stream did not decode");

        int rowBytes = (width * bits + 7) / 8;
        int perByte = 8 / bits;
        int mask = (1 << bits) - 1;

        var samples = new byte[width * height * components];
        for (int y = 0, at = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++, at += components)
            {
                int index = bits == 8
                    ? packed[y * rowBytes + x]
                    : (packed[y * rowBytes + x / perByte] >> (8 - bits - (x % perByte) * bits)) & mask;

                for (int component = 0; component < components; component++)
                    samples[at + component] = palette[index * components + component];
            }
        }

        return samples;
    }

    /// <summary>The greyscale counterpart of <see cref="ReconstructRgb"/>: one component per entry.</summary>
    private static byte[] ReconstructGray(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var image = ImageStream(document);

        var space = Resolve(document, image[PdfName.ColorSpace]) as PdfArray;
        Assert.NotNull(space);

        byte[] palette = (Resolve(document, space[3]) as PdfString)?.RawBytes;
        Assert.NotNull(palette);

        int bits = ((PdfNumber)Resolve(document, image[PdfName.BitsPerComponent])).IntValue;
        int width = ((PdfNumber)Resolve(document, image[PdfName.Width])).IntValue;
        int height = ((PdfNumber)Resolve(document, image[PdfName.Height])).IntValue;

        Assert.True(image.TryGetDecodedData(64L * 1024 * 1024, out byte[] packed),
            "the indexed image stream did not decode");

        int rowBytes = (width * bits + 7) / 8;
        int perByte = 8 / bits;
        int mask = (1 << bits) - 1;

        var grey = new byte[width * height];
        for (int y = 0, at = 0; y < height; y++)
            for (int x = 0; x < width; x++, at++)
            {
                int index = bits == 8
                    ? packed[y * rowBytes + x]
                    : (packed[y * rowBytes + x / perByte] >> (8 - bits - (x % perByte) * bits)) & mask;
                grey[at] = palette[index];
            }

        return grey;
    }

    private static byte[] BuildPdfWithImage(byte[] samples, string extraKeys = "",
        string colorSpace = "/DeviceRGB", int width = Width, int height = Height)
    {
        string content = $"q {width} 0 0 {height} 100 600 cm /Im0 Do Q";

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
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} "
            + $"/ColorSpace {colorSpace} /BitsPerComponent 8 {extraKeys} "
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

    /// <summary>
    /// A palettable RGB image whose <c>/SMask</c> is a palettable greyscale one. The mask is what
    /// the test is about: it must stay <c>/DeviceGray</c>.
    /// </summary>
    private static byte[] BuildPdfWithSoftMaskedImage()
    {
        byte[] colour = FourColourRgb();
        var mask = new byte[Width * Height];
        var maskRandom = new Random(7);
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(maskRandom.Next(2) == 0 ? 0x00 : 0xFF);

        const string content = "q 96 0 0 96 100 600 cm /Im0 Do Q";

        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new long[7];

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
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {Width} /Height {Height} "
            + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R "
            + $"/Length {colour.Length} >>\nstream\n");
        ms.Write(colour, 0, colour.Length);
        Write("\nendstream\nendobj\n");
        offsets[6] = ms.Position;
        Write($"6 0 obj\n<< /Type /XObject /Subtype /Image /Width {Width} /Height {Height} "
            + $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Length {mask.Length} >>\nstream\n");
        ms.Write(mask, 0, mask.Length);
        Write("\nendstream\nendobj\n");

        long xref = ms.Position;
        Write("xref\n0 7\n0000000000 65535 f \n");
        for (int i = 1; i <= 6; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    private static byte[] BuildPdfWithIndexedImage()
    {
        var indices = new byte[Width * Height];
        for (int i = 0; i < indices.Length; i++) indices[i] = (byte)(i % 3);

        const string content = "q 96 0 0 96 100 600 cm /Im0 Do Q";

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
        Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {Width} /Height {Height} "
            + "/ColorSpace [/Indexed /DeviceRGB 2 <FF000000FF000000FF>] /BitsPerComponent 8 "
            + $"/Length {indices.Length} >>\nstream\n");
        ms.Write(indices, 0, indices.Length);
        Write("\nendstream\nendobj\n");

        long xref = ms.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Write($"{offsets[i]:D10} 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject obj)
    {
        int guard = 0;
        while (obj is PdfReference reference && guard++ < 32)
            obj = document.GetObject(reference.ReferenceObjectNumber);
        return obj;
    }

    private static PdfStream ImageStream(PdfDocument document) => NamedImage(document, "Im0");

    private static PdfStream NamedImage(PdfDocument document, string name)
    {
        var catalog = Resolve(document, document.Trailer[PdfName.Root]) as PdfDictionary;
        var pages = Resolve(document, catalog[PdfName.Pages]) as PdfDictionary;
        var kids = Resolve(document, pages[PdfName.Kids]) as PdfArray;
        var page = Resolve(document, kids[0]) as PdfDictionary;
        var resources = Resolve(document, page[PdfName.Resources]) as PdfDictionary;
        var xobjects = Resolve(document, resources[PdfName.XObject]) as PdfDictionary;
        var image = Resolve(document, xobjects[new PdfName(name)]) as PdfStream;

        Assert.NotNull(image);
        return image;
    }

    private static string ColorSpaceNameOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var space = Resolve(document, ImageStream(document)[PdfName.ColorSpace]);
        return (space as PdfName)?.Value
               ?? ((Resolve(document, (space as PdfArray)?[0]) as PdfName)?.Value);
    }

    private static string SoftMaskColorSpaceNameOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var mask = Resolve(document, ImageStream(document)[PdfName.SMask]) as PdfStream;
        Assert.NotNull(mask);

        var space = Resolve(document, mask[PdfName.ColorSpace]);
        return (space as PdfName)?.Value
               ?? ((Resolve(document, (space as PdfArray)?[0]) as PdfName)?.Value);
    }

    private static int SoftMaskStoredLength(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var mask = Resolve(document, ImageStream(document)[PdfName.SMask]) as PdfStream;
        Assert.NotNull(mask);
        return mask.RawData.Length;
    }

    /// <summary>
    /// §8.6.6.3: <c>hival</c> is the largest valid index, so it is one less than the entry count,
    /// and the lookup table must hold at least <c>(hival + 1) x n</c> bytes. Writing the count
    /// instead makes the table one entry short — malformed, and invisible to a reader that only
    /// ever looks up indices the image actually uses, which is why this needs its own assertion.
    /// </summary>
    private static int HivalOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        var space = Resolve(document, ImageStream(document)[PdfName.ColorSpace]) as PdfArray;
        Assert.NotNull(space);
        return ((PdfNumber)Resolve(document, space[2])).IntValue;
    }

    private static int BitsPerComponentOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return ((PdfNumber)Resolve(document, ImageStream(document)[PdfName.BitsPerComponent])).IntValue;
    }

    private static int StoredImageLength(byte[] pdf)
    {
        using var document = PdfDocument.Open(new MemoryStream(pdf));
        return ImageStream(document).RawData.Length;
    }

    private static OptimizationResult Optimize(byte[] input, OptimizationOptions options,
        out byte[] optimized)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        var result = PdfOptimizer.Optimize(i, o, options);
        optimized = o.ToArray();
        return result;
    }
}
