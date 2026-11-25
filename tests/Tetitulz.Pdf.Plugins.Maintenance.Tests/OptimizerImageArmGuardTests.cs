// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The image re-encoder's three arms against content they could lose: the JPEG arm takes Base's one
/// decode of a DCT image, refusing what that decode's colours do not stand for — decoding every JPEG
/// through Skia and declaring the result <c>/DeviceRGB</c> would bring an Adobe CMYK JPEG back
/// near-negative and drop an inverting <c>/Decode</c> without its inversion; the raw arm refuses an
/// <c>/Indexed</c> image's palette indices, as the JPEG 2000 arm does, rather than re-encode them as
/// grey; and a grey image of any origin goes out as one component, not a three-component JPEG under
/// <c>/DeviceGray</c>, which every reader that strides by the dictionary would shear.
/// </summary>
public class OptimizerImageArmGuardTests
{
    /// <summary>A grey JPEG is re-encoded as a grey JPEG: one component in the frame, <c>/DeviceGray</c> in the dictionary — the two a reader has to agree on.</summary>
    [Fact]
    public void AGreyJpeg_IsReEncodedAsAOneComponentJpeg_DeclaredDeviceGray()
    {
        byte[] input = BuildPdfWithImage("/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", GreyJpeg(96, 96, quality: 100));
        byte[] output = Optimize(input, JpegTarget(quality: 30));

        PdfStream image = FirstImage(output, out var doc);
        using (doc)
        {
            Assert.Equal(PdfName.DCTDecode, image.GetName(PdfName.Filter));
            Assert.Equal(PdfName.DeviceGray, image.GetName(PdfName.ColorSpace));
            Assert.Equal(1, JpegFrameComponents(image.RawData));
            Assert.True(image.RawData.Length < FirstImage(input, out var before).RawData.Length, "the JPEG was not re-encoded, so the test proves nothing about the re-encode");
            before.Dispose();
        }
    }

    /// <summary>
    /// Another reader issue5475's shape: a grey JPEG 2000 image with no <c>/ColorSpace</c> re-encoded as a
    /// JPEG. The frame was three components under <c>/DeviceGray</c>, sheared by every reader that
    /// strides by the dictionary; it is one component now.
    /// </summary>
    [Fact]
    public void AGreyJpeg2000Image_IsReEncodedAsAOneComponentJpeg()
    {
        // Noise, not a gradient: a gradient's lossless JP2 is smaller than any JPEG of it, and an
        // image whose re-encode would not shrink it is left alone.
        byte[] grey = Noise(128, 128);
        byte[] jp2 = JpxImageEncoding.Encode(grey, 128, 128, 1, 8, new JpxEncodingOptions { Lossless = true });
        byte[] input = BuildPdfWithImage("/Filter /JPXDecode", jp2);
        byte[] output = Optimize(input, JpegTarget(quality: 40));

        PdfStream image = FirstImage(output, out var doc);
        using (doc)
        {
            Assert.Equal(PdfName.DCTDecode, image.GetName(PdfName.Filter));
            Assert.Equal(PdfName.DeviceGray, image.GetName(PdfName.ColorSpace));
            Assert.Equal(1, JpegFrameComponents(image.RawData));
        }
    }

    /// <summary>
    /// An Adobe CMYK JPEG is left as it is, and the reason reported names its inks — which are not
    /// Skia's RGB — where an RGB JPEG under the same options is re-encoded. The fixture carries no
    /// <c>/Decode</c>: with one, the array's refusal comes first and the CMYK rule is never reached
    /// (the corpus file has one, and <see cref="Corpus_CmykJpeg_KeepsItsInks"/> covers that order).
    /// The reason is asserted because a 16 × 16 JPEG re-encoded sheared can come out no smaller than
    /// its original, which leaves it unchanged for the wrong reason.
    /// </summary>
    [Fact]
    public void ACmykJpeg_IsLeftAsItIs_WhereAnRgbOneIsReEncoded()
    {
        byte[] cmyk = Convert.FromBase64String(AdobeCmykJpegBase64);
        var reasons = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        Action<string> collect = message => { if (Environment.CurrentManagedThreadId == thread) reasons.Add(message); };
        DocumentEditDiagnostics.OnEvent += collect;
        byte[] kept;
        try { kept = Optimize(BuildPdfWithImage("/ColorSpace /DeviceCMYK /BitsPerComponent 8 /Filter /DCTDecode", cmyk), JpegTarget(quality: 10)); }
        finally { DocumentEditDiagnostics.OnEvent -= collect; }
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(cmyk, keptImage.RawData);
            Assert.Equal(PdfName.DeviceCMYK, keptImage.GetName(PdfName.ColorSpace));
        }
        Assert.Contains(reasons, r => r.Contains("it is a CMYK JPEG", StringComparison.Ordinal));

        byte[] rgb = RgbJpeg(96, 96, quality: 100);
        byte[] changed = Optimize(BuildPdfWithImage("/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", rgb), JpegTarget(quality: 10));
        using var changedDoc = PdfDocument.Open(new MemoryStream(changed));
        Assert.NotEqual(rgb, FirstImage(changed, out var d2).RawData);
        d2.Dispose();
    }

    /// <summary>An RGB JPEG whose <c>/Decode</c> inverts its samples is left as it is: the re-encoded samples would have lost the array without its inversion. Without the array it is re-encoded.</summary>
    [Fact]
    public void AJpegWithAnInvertingDecode_IsLeftAsItIs()
    {
        byte[] rgb = RgbJpeg(96, 96, quality: 100);
        byte[] kept = Optimize(BuildPdfWithImage("/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Decode [1 0 1 0 1 0]", rgb), JpegTarget(quality: 10));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(rgb, keptImage.RawData);
            Assert.NotNull(keptImage[PdfName.Decode]);
        }

        byte[] changed = Optimize(BuildPdfWithImage("/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode", rgb), JpegTarget(quality: 10));
        Assert.NotEqual(rgb, FirstImage(changed, out var d2).RawData);
        d2.Dispose();
    }

    /// <summary>
    /// The colour space's references are followed wherever a producer puts them: the dictionary's
    /// <c>/ColorSpace 9 0 R</c> naming the array, and a grey profile's <c>/N 8 0 R</c>. Either unresolved
    /// would read as three components, which differs from the grey frame, so the JPEG would be re-encoded
    /// as <c>/DeviceGray</c> and the profile dropped.
    /// </summary>
    [Theory]
    [InlineData("/ColorSpace 9 0 R", "7 0 obj\n<< /N 1 /Length 4 >>\nstream\nabcd\nendstream\nendobj\n8 0 obj\n1\nendobj\n9 0 obj\n[/ICCBased 7 0 R]\nendobj\n")]
    [InlineData("/ColorSpace [/ICCBased 7 0 R]", "7 0 obj\n<< /N 8 0 R /Length 4 >>\nstream\nabcd\nendstream\nendobj\n8 0 obj\n1\nendobj\n")]
    public void AGreyIccJpeg_WhoseSpaceOrArityIsIndirect_IsLeftAsItIs(string colourSpace, string objects)
    {
        byte[] grey = GreyJpeg(96, 96, quality: 100);
        byte[] kept = Optimize(BuildPdfWithImage(colourSpace + " /BitsPerComponent 8 /Filter /DCTDecode", grey, objects), JpegTarget(quality: 10));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(grey, keptImage.RawData);
            Assert.IsType<PdfArray>(keptDoc.ResolveObject(keptImage[PdfName.ColorSpace]));
        }
    }

    /// <summary>
    /// A soft mask stored as a JPEG is decoded through the JPEG arm and, being a mask, written
    /// lossless — the same grey JPEG drawn as an image takes the JPEG target
    /// (<see cref="AGreyJpeg_IsReEncodedAsAOneComponentJpeg_DeclaredDeviceGray"/>). Its samples are
    /// the JPEG's decoded ones, one component, under <c>/DeviceGray</c>.
    /// </summary>
    [Fact]
    public void AJpegSoftMask_IsDecodedThroughTheJpegArm_AndWrittenLossless()
    {
        byte[] grey = GreyJpeg(96, 96, quality: 100);
        const string user = "7 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /SMask 5 0 R /Length 4 >>\nstream\nabcd\nendstream\nendobj\n";
        byte[] output = Optimize(BuildPdfWithImage("/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", grey, user), JpegTarget(quality: 10));

        PdfStream mask = FirstImage(output, out var doc);
        using (doc)
        {
            byte[] expected = Tetitulz.Pdf.Base.Images.JpegSamples.TryDecode(grey, 4096, 1 << 24, out int w, out int h, out int components);
            Assert.Equal((96, 96, 1), (w, h, components));
            Assert.Equal(PdfName.FlateDecode, mask.GetName(PdfName.Filter));
            Assert.Equal(PdfColorSpaceNames.DeviceGray, mask.GetName(PdfName.ColorSpace)?.Value);
            Assert.Equal(expected, mask.GetDecodedData());
        }
    }

    /// <summary>A JPEG coloured by an ICC profile is left as it is: a re-encode in a device space would lose what the profile says its samples mean.</summary>
    [Fact]
    public void AnIccBasedJpeg_IsLeftAsItIs()
    {
        byte[] rgb = RgbJpeg(96, 96, quality: 100);
        string icc = "7 0 obj\n<< /N 3 /Length 4 >>\nstream\nabcd\nendstream\nendobj\n";
        byte[] kept = Optimize(BuildPdfWithImage("/ColorSpace [/ICCBased 7 0 R] /BitsPerComponent 8 /Filter /DCTDecode", rgb, icc), JpegTarget(quality: 10));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(rgb, keptImage.RawData);
            Assert.IsType<PdfArray>(keptDoc.ResolveObject(keptImage[PdfName.ColorSpace]));
        }
    }

    /// <summary>An 8-bit <c>/Indexed</c> image's samples are palette indices and are left as they are — not re-encoded as a grey JPEG of the indices — where the same bytes as a <c>/DeviceGray</c> image are re-encoded.</summary>
    [Fact]
    public void AnIndexedRawImage_IsLeftAsItIs_WhereAGreyOneIsReEncoded()
    {
        byte[] indices = Gradient(64, 64, 1);
        string palette = "[/Indexed /DeviceRGB 255 <" + new string('0', 1536) + ">]";
        byte[] kept = Optimize(BuildPdfWithImage("/ColorSpace " + palette + " /BitsPerComponent 8", indices), JpegTarget(quality: 10));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(indices, keptImage.GetDecodedData());
            Assert.IsType<PdfArray>(keptDoc.ResolveObject(keptImage[PdfName.ColorSpace]));
        }

        byte[] changed = Optimize(BuildPdfWithImage("/ColorSpace /DeviceGray /BitsPerComponent 8", indices), JpegTarget(quality: 10));
        PdfStream changedImage = FirstImage(changed, out var changedDoc);
        using (changedDoc) Assert.Equal(PdfName.DCTDecode, changedImage.GetName(PdfName.Filter));
    }

    /// <summary>
    /// Every grey level survives the PNG target, which is documented lossless: a 256 × 16 ramp of
    /// all 256 levels decodes to the same samples. The neutral pixels' weighted sum truncated 65 of
    /// them one lower (1, 2, 4, 128, 254 …). The ramp is checked to have been re-encoded, so equal
    /// samples are not the image left alone.
    /// </summary>
    [Fact]
    public void AGreyRamp_ThroughThePngTarget_KeepsEveryLevel()
    {
        var ramp = new byte[256 * 16];
        for (int i = 0; i < ramp.Length; i++) ramp[i] = (byte)(i % 256);
        var options = new OptimizationOptions
        {
            ImageOptimization = new ImageOptimizationOptions
            {
                Enabled = true,
                TargetFormat = ImageFormat.Png,
                SkipAlreadyOptimized = false,
                MinImageSizeBytes = 0,
            },
        };
        byte[] output = Optimize(BuildPdfWithImage("/ColorSpace /DeviceGray /BitsPerComponent 8", ramp, sizeOverride: "/Width 256 /Height 16"), options);

        PdfStream image = FirstImage(output, out var doc);
        using (doc)
        {
            Assert.Equal(PdfName.FlateDecode, image.GetName(PdfName.Filter));
            Assert.Equal(ramp, image.GetDecodedData());
        }
    }

    private const string SeparationBlack =
        "[/Separation /Black /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >>]";

    /// <summary>
    /// A raw image under a <c>/Separation</c> space is left as it is: its samples are ink tints, and
    /// written as a grey JPEG under <c>/DeviceGray</c> full ink read as white — a negative (the review
    /// of batch 131). The same bytes under <c>/DeviceGray</c> are re-encoded.
    /// </summary>
    [Fact]
    public void ASeparationRawImage_IsLeftAsItIs_WhereADeviceGrayOneIsReEncoded()
    {
        byte[] tints = Gradient(64, 64, 1);
        byte[] kept = Optimize(BuildPdfWithImage("/ColorSpace " + SeparationBlack + " /BitsPerComponent 8", tints), JpegTarget(quality: 10));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(tints, keptImage.GetDecodedData());
            Assert.IsType<PdfArray>(keptDoc.ResolveObject(keptImage[PdfName.ColorSpace]));
        }

        byte[] changed = Optimize(BuildPdfWithImage("/ColorSpace /DeviceGray /BitsPerComponent 8", tints), JpegTarget(quality: 10));
        PdfStream changedImage = FirstImage(changed, out var changedDoc);
        using (changedDoc) Assert.Equal(PdfName.DCTDecode, changedImage.GetName(PdfName.Filter));
    }

    /// <summary>A raw image coloured by an ICC profile is left as it is: written under <c>/DeviceGray</c> the profile was dropped.</summary>
    [Fact]
    public void AnIccBasedRawImage_IsLeftAsItIs()
    {
        byte[] grey = Gradient(64, 64, 1);
        string icc = "7 0 obj\n<< /N 1 /Length 4 >>\nstream\nabcd\nendstream\nendobj\n";
        byte[] kept = Optimize(BuildPdfWithImage("/ColorSpace [/ICCBased 7 0 R] /BitsPerComponent 8", grey, icc), JpegTarget(quality: 10));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(grey, keptImage.GetDecodedData());
            Assert.IsType<PdfArray>(keptDoc.ResolveObject(keptImage[PdfName.ColorSpace]));
        }
    }

    /// <summary>
    /// A JPEG 2000 image under a <c>/Separation</c> space is left as it is, where the same codestream
    /// with no <c>/ColorSpace</c> is re-encoded (<see cref="AGreyJpeg2000Image_IsReEncodedAsAOneComponentJpeg"/>).
    /// </summary>
    [Fact]
    public void AJpeg2000ImageUnderASeparationSpace_IsLeftAsItIs()
    {
        byte[] jp2 = JpxImageEncoding.Encode(Noise(128, 128), 128, 128, 1, 8, new JpxEncodingOptions { Lossless = true });
        byte[] kept = Optimize(BuildPdfWithImage("/Filter /JPXDecode /ColorSpace " + SeparationBlack, jp2), JpegTarget(quality: 40));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(PdfName.JPXDecode, keptImage.GetName(PdfName.Filter));
            Assert.Equal(jp2, keptImage.RawData);
        }
    }

    /// <summary>
    /// A 96 × 96 JPEG whose dictionary declares 101 × 6000 under a 100-wide limit asks for a 100 × 5940
    /// resample — an upscale of its samples, and with a larger declared height a multi-gigabyte one.
    /// It is refused for that reason before any resize, where a JPEG whose
    /// dictionary matches its frame is downsampled to the limit.
    /// </summary>
    [Fact]
    public void AResampleTargetPastTheSamples_IsRefusedBeforeAnyResize_WhereAMatchingOneIsDownsampled()
    {
        byte[] jpeg = GreyJpeg(96, 96, quality: 100);
        var options = JpegTarget(quality: 30);
        options.ImageOptimization.MaxWidth = 50;

        var reasons = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        Action<string> collect = message => { if (Environment.CurrentManagedThreadId == thread) reasons.Add(message); };
        DocumentEditDiagnostics.OnEvent += collect;
        byte[] kept;
        try { kept = Optimize(BuildPdfWithImage("/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", jpeg, sizeOverride: "/Width 51 /Height 6000"), options); }
        finally { DocumentEditDiagnostics.OnEvent -= collect; }
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc) Assert.Equal(jpeg, keptImage.RawData);
        Assert.Contains(reasons, r => r.Contains("resample target", StringComparison.Ordinal));

        byte[] changed = Optimize(BuildPdfWithImage("/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /DCTDecode", jpeg), options);
        PdfStream changedImage = FirstImage(changed, out var changedDoc);
        using (changedDoc) Assert.Equal(50, changedImage.GetInteger(PdfName.Width));
    }

    /// <summary>A JPEG stencil mask is left as it is — the JPEG 2000 arm already refused one — where it was re-encoded with <c>/ColorSpace</c> and 8 bits beside <c>/ImageMask true</c>.</summary>
    [Fact]
    public void AJpegStencilMask_IsLeftAsItIs()
    {
        byte[] jpeg = GreyJpeg(96, 96, quality: 100);
        byte[] kept = Optimize(BuildPdfWithImage("/ImageMask true /Filter /DCTDecode", jpeg), JpegTarget(quality: 10));
        PdfStream keptImage = FirstImage(kept, out var keptDoc);
        using (keptDoc)
        {
            Assert.Equal(jpeg, keptImage.RawData);
            Assert.Null(keptImage[PdfName.ColorSpace]);
        }
    }

    // ------------------------------------------------------------------ the corpus

    /// <summary>Another reader cmykjpeg.pdf: under Aggressive the Adobe CMYK JPEG keeps its four inks, its space and its /Decode (its Huffman tables may be refitted, losslessly), where it came back near-negative as a three-component /DeviceRGB JPEG.</summary>
    [CorpusFact("cmykjpeg.pdf")]
    public void Corpus_CmykJpeg_KeepsItsInks()
    {
        using var pair = FirstImageBeforeAndAfter("cmykjpeg.pdf", OptimizationOptions.Aggressive);
        var (before, after, _) = pair;
        Assert.Equal(4, JpegFrameComponents(before.RawData));   // the fixture is the CMYK JPEG the row names
        Assert.Equal(PdfName.DCTDecode, after.GetName(PdfName.Filter));
        Assert.Equal(PdfName.DeviceCMYK, after.GetName(PdfName.ColorSpace));
        Assert.Equal(4, JpegFrameComponents(after.RawData));
        Assert.Equal(before[PdfName.Decode] != null, after[PdfName.Decode] != null);
    }

    /// <summary>Another reader issue7406.pdf: the RGB JPEG keeps its /Decode [1 0 1 0 1 0] with its JPEG, where its logo came back a negative without the array.</summary>
    [CorpusFact("issue7406.pdf")]
    public void Corpus_Issue7406_DecodeIsKeptWithItsJpeg()
    {
        using var pair = FirstImageBeforeAndAfter("issue7406.pdf", OptimizationOptions.Aggressive);
        var (before, after, _) = pair;
        var decode = Assert.IsType<PdfArray>(pair.Doc.ResolveObject(after[PdfName.Decode]));
        Assert.Equal(6, decode.Count);
        Assert.Equal(PdfName.DCTDecode, after.GetName(PdfName.Filter));
        Assert.Equal(3, JpegFrameComponents(after.RawData));
        Assert.Equal(JpegFrameComponents(before.RawData), JpegFrameComponents(after.RawData));
    }

    /// <summary>Another reader issue9940.pdf: the /Indexed cover art keeps its palette and its indices (its Flate stream may be recompressed), where it came back as a grey JPEG of the indices.</summary>
    [CorpusFact("issue9940.pdf")]
    public void Corpus_Issue9940_IndexedImageKeepsItsPaletteAndIndices()
    {
        using var pair = FirstImageBeforeAndAfter("issue9940.pdf", OptimizationOptions.Aggressive);
        var (before, after, _) = pair;
        var space = Assert.IsType<PdfArray>(pair.Doc.ResolveObject(after[PdfName.ColorSpace]));
        Assert.Equal(PdfName.Indexed, Assert.IsType<PdfName>(pair.Doc.ResolveObject(space[0])));
        Assert.NotEqual(PdfName.DCTDecode, after.GetName(PdfName.Filter));
        Assert.Equal(before.GetDecodedData(), after.GetDecodedData());
    }

    /// <summary>Another reader issue5475.pdf: the grey JPEG 2000 image re-encodes as a one-component JPEG under /DeviceGray, where its frame was three components and sheared.</summary>
    [CorpusFact("issue5475.pdf")]
    public void Corpus_Issue5475_GreyJpeg2000ReEncodesAsAOneComponentJpeg()
    {
        byte[] output = Optimize(File.ReadAllBytes(TestCorpus.Find("issue5475.pdf")!), OptimizationOptions.Balanced);
        PdfStream image = FirstImage(output, out var doc);
        using (doc)
        {
            Assert.Equal(PdfName.DCTDecode, image.GetName(PdfName.Filter));
            Assert.Equal(PdfName.DeviceGray, image.GetName(PdfName.ColorSpace));
            Assert.Equal(1, JpegFrameComponents(image.RawData));
        }
    }

    /// <summary>The first image of the corpus file as read (<see cref="Before"/>) and as read from the optimized output (<see cref="After"/>, resolved through <see cref="Doc"/>); both documents stay open until disposed, since the streams' data is read after.</summary>
    private sealed class BeforeAndAfter : IDisposable
    {
        public PdfStream Before = null!, After = null!;
        public PdfDocument InputDoc = null!, Doc = null!;
        public void Dispose() { InputDoc.Dispose(); Doc.Dispose(); }
        public void Deconstruct(out PdfStream before, out PdfStream after, out BeforeAndAfter self) { before = Before; after = After; self = this; }
    }

    private static BeforeAndAfter FirstImageBeforeAndAfter(string file, OptimizationOptions options)
    {
        byte[] input = File.ReadAllBytes(TestCorpus.Find(file)!);
        var pair = new BeforeAndAfter();
        pair.Before = FirstImage(input, out pair.InputDoc);
        byte[] output = Optimize(input, options);
        pair.After = FirstImage(output, out pair.Doc);
        return pair;
    }

    /// <summary>Pseudo-random grey samples (a fixed linear congruential sequence), which no lossless coder shrinks much.</summary>
    private static byte[] Noise(int w, int h)
    {
        var samples = new byte[w * h];
        uint state = 12345;
        for (int i = 0; i < samples.Length; i++) { state = state * 1664525u + 1013904223u; samples[i] = (byte)(state >> 24); }
        return samples;
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A 16×16-pixel Adobe CMYK JPEG, cyan on the left and yellow on the right, the APP14 transform flag 0 and the Adobe inversion: the one <c>CmykJpegRenderingTests</c> draws.</summary>
    private const string AdobeCmykJpegBase64 =
        "/9j/7gAOQWRvYmUAZAAAAAAA/9sAQwABAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEB" +
        "AQEBAQEBAQEBAQEB/8AAFAgAEAAQBEMRAE0RAFkRAEsRAP/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMC" +
        "BAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElK" +
        "U1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY" +
        "2drh4uPk5ebn6Onq8fLz9PX29/j5+v/aAA4EQwBNAFkASwAAPwD/AD/6/v4r+/iv7+K/3+KK/wAAeiv8Aeiv9/iiv9/iiv8AAHor/9k=";

    /// <summary>Samples of a diagonal gradient, <paramref name="components"/> bytes a pixel, row-major.</summary>
    private static byte[] Gradient(int w, int h, int components)
    {
        var samples = new byte[w * h * components];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < components; c++)
                    samples[(y * w + x) * components + c] = (byte)((x * 255 / (w - 1) + y * 255 / (h - 1) + c * 60) / 2);
        return samples;
    }

    private static byte[] GreyJpeg(int w, int h, int quality)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
        byte[] grey = Gradient(w, h, 1);
        System.Runtime.InteropServices.Marshal.Copy(grey, 0, bitmap.GetPixels(), grey.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        byte[] jpeg = data.ToArray();
        Assert.Equal(1, JpegFrameComponents(jpeg));   // the fixture is a grey JPEG, or the test proves nothing
        return jpeg;
    }

    private static byte[] RgbJpeg(int w, int h, int quality)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        byte[] rgb = Gradient(w, h, 3);
        var rgba = new byte[w * h * 4];
        for (int i = 0, o = 0; i < rgb.Length; i += 3, o += 4) { rgba[o] = rgb[i]; rgba[o + 1] = rgb[i + 1]; rgba[o + 2] = rgb[i + 2]; rgba[o + 3] = 255; }
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>The component count of the JPEG's frame header (SOF0–SOF15, APP and table segments walked past).</summary>
    private static int JpegFrameComponents(byte[] jpeg)
    {
        int i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF) { i++; continue; }
            byte marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                return jpeg[i + 9];
            int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            i += 2 + length;
        }
        throw new Xunit.Sdk.XunitException("no frame header in the JPEG");
    }

    /// <summary>A one-page file drawing image object 5 — <paramref name="dictEntries"/> and <paramref name="data"/> — with <paramref name="extraObjects"/> (object 7 and up) written after it.</summary>
    private static byte[] BuildPdfWithImage(string dictEntries, byte[] data, string extraObjects = null, string sizeOverride = null)
    {
        const string content = "q 200 0 0 200 100 400 cm /Im0 Do Q";
        using var ms = new MemoryStream();
        void W(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        var offsets = new Dictionary<int, long>();

        W("%PDF-1.5\n");
        offsets[1] = ms.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[2] = ms.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[3] = ms.Position; W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        offsets[4] = ms.Position; W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        offsets[5] = ms.Position;
        string size = sizeOverride ?? $"/Width {ImageSide(data, dictEntries)} /Height {ImageSide(data, dictEntries)}";
        W($"5 0 obj\n<< /Type /XObject /Subtype /Image {size} {dictEntries} /Length {data.Length} >>\nstream\n");
        ms.Write(data, 0, data.Length);
        W("\nendstream\nendobj\n");
        offsets[6] = ms.Position; W("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        if (extraObjects != null)
        {
            // Every extra object gets its own entry: one entry for the whole appendix left a second
            // and third extra object (an indirect /N, an indirect /ColorSpace) unreachable.
            foreach (Match header in Regex.Matches(extraObjects, @"(?m)^(\d+) 0 obj"))
            {
                offsets[int.Parse(header.Groups[1].Value)] = ms.Position + header.Index;
            }
            W(extraObjects);
        }
        int count = offsets.Keys.Max() + 1;

        long xref = ms.Position;
        W($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++) W(offsets.TryGetValue(i, out long at) ? $"{at:D10} 00000 n \n" : "0000000000 65535 f \n");
        W($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The fixtures are square: the side from a JPEG's frame, a JP2's header, or a raw buffer's length.</summary>
    private static int ImageSide(byte[] data, string dictEntries)
    {
        if (dictEntries.Contains("DCTDecode"))
        {
            int i = 2;
            while (i + 4 <= data.Length)
            {
                if (data[i] != 0xFF) { i++; continue; }
                byte marker = data[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return (data[i + 7] << 8) | data[i + 8];
                i += 2 + ((data[i + 2] << 8) | data[i + 3]);
            }
            throw new Xunit.Sdk.XunitException("no frame header in the JPEG");
        }
        if (dictEntries.Contains("JPXDecode")) return 128;
        return (int)Math.Sqrt(data.Length);
    }

    /// <summary>Every image is considered, whatever its size: the 16 × 16 CMYK fixture is under the default byte floor, below which an image is skipped before any arm sees it.</summary>
    private static OptimizationOptions JpegTarget(int quality) => new()
    {
        ImageOptimization = new ImageOptimizationOptions
        {
            Enabled = true,
            TargetFormat = ImageFormat.Jpeg,
            Quality = quality,
            SkipAlreadyOptimized = false,
            MinImageSizeBytes = 0,
        },
    };

    private static byte[] Optimize(byte[] input, OptimizationOptions options)
    {
        using var i = new MemoryStream(input);
        using var o = new MemoryStream();
        PdfOptimizer.Optimize(i, o, options);
        return o.ToArray();
    }

    /// <summary>The first image XObject on page 0.</summary>
    private static PdfStream FirstImage(byte[] pdf, out PdfDocument doc)
    {
        doc = PdfDocument.Open(new MemoryStream(pdf));
        var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);
        foreach (var key in xobjects!.Keys)
            if (doc.ResolveObject(xobjects[key]) is PdfStream img && (img[PdfName.Subtype] as PdfName)?.Value == "Image")
                return img;
        throw new Xunit.Sdk.XunitException("the document has no image XObject");
    }
}
