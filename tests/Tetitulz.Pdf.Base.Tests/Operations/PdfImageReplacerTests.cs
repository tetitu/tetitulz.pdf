// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Helpers;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// True image replacement: the image XObject is superseded in place by an incremental update,
/// so the pixels change while the original bytes — and therefore any signature over them —
/// stay intact.
///
/// <para>Every test here asserts on decoded pixels or on the re-parsed output object. A
/// replacement that silently did nothing would still produce a valid, non-empty PDF, so
/// "did not throw" proves nothing (CLAUDE.md, banned test pattern 3).</para>
/// </summary>
public sealed class PdfImageReplacerTests
{
    private const int Dpi = 72;

    // Fixtures live in ImageFixtures.cs, shared with the advanced-selection and revision tests.
    private static byte[] OneImagePdf(
        byte[] samples, int width, int height,
        string colorSpace = "/DeviceRGB",
        string extraImageKeys = "",
        string catalogExtras = "",
        Action<PdfFixtureBuilder>? extraObjects = null)
        => ImageFixtures.OneImagePdf(samples, width, height, colorSpace, extraImageKeys,
                                     catalogExtras, extraObjects);

    private static byte[] TwoImagePdf(byte[] left, byte[] right) => ImageFixtures.TwoImagePdf(left, right);
    private static byte[] SharedImagePdf(byte[] samples) => ImageFixtures.SharedImagePdf(samples);

    private static readonly byte[] Red   = ImageFixtures.Red;
    private static readonly byte[] Green = ImageFixtures.Green;
    private static readonly byte[] Blue  = ImageFixtures.Blue;

    // ================================================================
    //  Helpers
    // ================================================================

    private static byte[] Replace(byte[] pdf, int imageIndex, ReplacementImage image,
        ImageReplacementOptions? options = null)
        => Replace(pdf, imageIndex, image, options, out _);

    private static byte[] Replace(byte[] pdf, int imageIndex, ReplacementImage image,
        ImageReplacementOptions? options, out ImageReplacementResult result)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        result = PdfImageReplacer.Replace(input, output, 0, imageIndex, image, options);
        return output.ToArray();
    }

    private static SKColor PixelAt(byte[] pdf, int page, double fx, double fy)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), page, Dpi);
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp.GetPixel((int)(bmp.Width * fx), (int)(bmp.Height * fy));
    }

    private static SKColor CenterPixel(byte[] pdf, int page = 0) => PixelAt(pdf, page, 0.5, 0.5);

    private static void AssertIsRed(SKColor c, string what) =>
        Assert.True(c.Red > 200 && c.Green < 80 && c.Blue < 80, $"{what}: expected red, got {c}");

    private static void AssertIsBlue(SKColor c, string what) =>
        Assert.True(c.Blue > 200 && c.Red < 80 && c.Green < 80, $"{what}: expected blue, got {c}");

    /// <summary>Re-parses the output and returns the image XObject the page's /Im0 now reaches.</summary>
    private static PdfStream ImageObjectOf(byte[] pdf, string resourceName = "Im0")
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var xobj = doc.ResolveObject(doc.Pages[0].Resources![PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobj);
        var stream = doc.ResolveObject(xobj![new PdfName(resourceName)]) as PdfStream;
        Assert.NotNull(stream);
        return stream!;
    }

    private static byte[] Sign(byte[] pdf, string fieldName, string commonName)
    {
        var (pfx, pwd) = TestCertificateGenerator.GenerateTestCertificate($"CN={commonName}");
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = pwd,
            SignatureFieldName = fieldName,
            Reason = "test",
        });
        return output.ToArray();
    }

    // ================================================================
    //  The pixels actually change
    // ================================================================

    [Fact]
    public void Replace_SwapsTheRenderedPixels()
    {
        byte[] pdf = OneImagePdf(Red, 1, 1);
        AssertIsRed(CenterPixel(pdf), "fixture before replacement");

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1));

        AssertIsBlue(CenterPixel(replaced), "after replacement");
    }

    [Fact]
    public void Replace_KeepsTheObjectNumber_SoNoReferenceNeedsRewriting()
    {
        byte[] pdf = OneImagePdf(Red, 1, 1);

        Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1), null, out var result);

        Assert.Equal(5, result.ObjectNumber);
        Assert.Equal("Im0", result.ResourceName);
        // The page still points at 5 0 R; the update superseded that object rather than adding one.
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var xobj = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources![PdfName.XObject])!;
        Assert.Equal(5, ((PdfReference)xobj[new PdfName("Im0")]).ReferenceObjectNumber);
    }

    [Fact]
    public void Replace_ReportsTheOriginalAndReplacementDimensions()
    {
        byte[] pdf = OneImagePdf(new byte[2 * 3], 2, 1);

        Replace(pdf, 0, ReplacementImage.Rgb24(new byte[4 * 2 * 3], 4, 2), null, out var result);

        Assert.Equal(2, result.OriginalWidth);
        Assert.Equal(1, result.OriginalHeight);
        Assert.Equal(4, result.NewWidth);
        Assert.Equal(2, result.NewHeight);
    }

    // ================================================================
    //  Signature preservation — the whole point of the incremental write
    // ================================================================

    [Fact]
    public void Replace_LeavesEveryOriginalByteInPlace()
    {
        byte[] pdf = OneImagePdf(Red, 1, 1);

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1));

        Assert.True(replaced.Length > pdf.Length, "expected an appended revision, not a rewrite");
        Assert.True(replaced.AsSpan(0, pdf.Length).SequenceEqual(pdf),
            "the original revision must be byte-identical, or a signature over it stops verifying");
    }

    [Fact]
    public void Replace_OnASignedDocument_LeavesTheSignatureVerifiable()
    {
        byte[] signed = Sign(OneImagePdf(Red, 1, 1), "Sig1", "First Signer");

        byte[] replaced = Replace(signed, 0, ReplacementImage.Rgb24(Blue, 1, 1));

        AssertIsBlue(CenterPixel(replaced), "signed document after replacement");

        using var doc = PdfDocument.Open(new MemoryStream(replaced));
        var results = new PdfSignatureVerifier().VerifyAll(doc);
        var sig = Assert.Single(results);
        Assert.True(sig.IsIntegrityValid,
            $"signature broke: {string.Join(", ", sig.ValidationErrors)}");
        // …but it no longer covers the file: the viewer must be able to say "changed since signed".
        Assert.False(sig.CoversEntireDocument);
    }

    [Fact]
    public void Replace_ThenSignAgain_LeavesBothSignaturesVerifiable()
    {
        byte[] signed = Sign(OneImagePdf(Red, 1, 1), "Sig1", "First Signer");
        byte[] replaced = Replace(signed, 0, ReplacementImage.Rgb24(Blue, 1, 1));

        byte[] countersigned = Sign(replaced, "Sig2", "Second Signer");

        AssertIsBlue(CenterPixel(countersigned), "twice-signed document");

        using var doc = PdfDocument.Open(new MemoryStream(countersigned));
        var results = new PdfSignatureVerifier().VerifyAll(doc);
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsIntegrityValid,
            $"signature {r.FieldName} broke: {string.Join(", ", r.ValidationErrors)}"));
        Assert.Contains(results, r => r.FieldName == "Sig1");
        Assert.Contains(results, r => r.FieldName == "Sig2");
    }

    // ================================================================
    //  Refusals
    // ================================================================

    [Fact]
    public void Replace_OnACertifiedDocument_IsRefused()
    {
        byte[] pdf = CertifiedPdf();

        var ex = Assert.Throws<Core.PdfException>(
            () => Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1)));
        Assert.Contains("DocMDP", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_OnACertifiedDocument_ProceedsWhenExplicitlyAllowed()
    {
        byte[] pdf = CertifiedPdf();

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1),
            new ImageReplacementOptions { AllowCertifiedDocument = true });

        AssertIsBlue(CenterPixel(replaced), "certified document with the opt-in set");
    }

    private static byte[] CertifiedPdf() => ImageFixtures.CertifiedPdf();

    /// <summary>
    /// The plan asks only whether the document is certified and at what <c>/P</c>, which the object
    /// graph answers on its own — so the overload takes no length, and the refusal names the level
    /// the fixture declares. The overload that took a length used to run the full analysis and
    /// discard its modification verdict; for a decrypted copy that verdict compared the original's
    /// <c>/ByteRange</c> against another file's bytes.
    /// </summary>
    [Fact]
    public void Plan_OnACertifiedDocument_IsRefusedFromTheObjectGraphAlone()
    {
        byte[] pdf = CertifiedPdf();
        int imageObject = PdfImageReplacer.FindImages(new MemoryStream(pdf))[0].ObjectNumber;
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        var ex = Assert.Throws<Core.PdfException>(
            () => PdfImageReplacer.Plan(doc, imageObject, ReplacementImage.Rgb24(Blue, 1, 1)));
        Assert.Contains("(P=1:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_OnACertifiedDocument_ProceedsWhenExplicitlyAllowed()
    {
        byte[] pdf = CertifiedPdf();
        int imageObject = PdfImageReplacer.FindImages(new MemoryStream(pdf))[0].ObjectNumber;
        using var doc = PdfDocument.Open(new MemoryStream(pdf));

        ImageReplacementPlan plan = PdfImageReplacer.Plan(doc, imageObject, ReplacementImage.Rgb24(Blue, 1, 1),
            new ImageReplacementOptions { AllowCertifiedDocument = true });

        Assert.Contains(plan.Objects, o => o.ObjectNumber == imageObject);   // superseded in place
    }

    [Fact]
    public void Replace_OnAnEncryptedDocument_IsRefused()
    {
        using var plain = new MemoryStream(OneImagePdf(Red, 1, 1));
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = "u",
            OwnerPassword = "o",
            Algorithm = EncryptionAlgorithm.AES128,
        });

        var ex = Assert.Throws<Core.PdfException>(
            () => Replace(encrypted.ToArray(), 0, ReplacementImage.Rgb24(Blue, 1, 1)));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Replace_IndexPastTheLastImage_Throws()
    {
        byte[] pdf = OneImagePdf(Red, 1, 1);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Replace(pdf, 1, ReplacementImage.Rgb24(Blue, 1, 1)));
        Assert.Contains("has 1 images", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    //  Selection by resource name
    // ================================================================

    [Fact]
    public void ReplaceByName_ChangesOnlyTheNamedImage()
    {
        byte[] pdf = TwoImagePdf(Red, Green);
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var result = PdfImageReplacer.ReplaceByName(input, output, 0, "Im1",
            ReplacementImage.Rgb24(Blue, 1, 1));
        byte[] replaced = output.ToArray();

        Assert.Equal("Im1", result.ResourceName);
        AssertIsRed(PixelAt(replaced, 0, 0.25, 0.5), "left half (/Im0, untouched)");
        AssertIsBlue(PixelAt(replaced, 0, 0.75, 0.5), "right half (/Im1, replaced)");
    }

    [Fact]
    public void ReplaceByName_UnknownName_ThrowsNamingTheImagesThatArePresent()
    {
        byte[] pdf = TwoImagePdf(Red, Green);
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() => PdfImageReplacer.ReplaceByName(
            input, output, 0, "Logo", ReplacementImage.Rgb24(Blue, 1, 1)));
        Assert.Contains("/Im0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/Im1", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    //  Which keys survive the swap
    // ================================================================

    [Fact]
    public void Replace_DropsASoftMaskDefinedAgainstTheOldSamples()
    {
        // The original is red behind a fully transparent /SMask, so the page renders white.
        // Carrying that mask onto the replacement would keep it invisible.
        byte[] pdf = OneImagePdf(Red, 1, 1,
            extraImageKeys: "/SMask 6 0 R ",
            extraObjects: b => b.Obj(6,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
                "/BitsPerComponent 8 /Length 1 >>", new byte[] { 0x00 }));

        SKColor before = CenterPixel(pdf);
        Assert.True(before.Red > 200 && before.Green > 200 && before.Blue > 200,
            $"fixture: a fully transparent /SMask should leave the page white, got {before}");

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1), null, out var result);

        AssertIsBlue(CenterPixel(replaced), "after replacement the dropped /SMask must not hide it");
        Assert.Contains("SMask", result.DroppedKeys);
        Assert.Null(ImageObjectOf(replaced)[PdfName.SMask]);
        Assert.Contains(result.Warnings, w => w.Contains("/SMask", StringComparison.Ordinal));
    }

    [Fact]
    public void Replace_CarriesTheStructureTreeLinkOver()
    {
        byte[] pdf = OneImagePdf(Red, 1, 1, extraImageKeys: "/StructParent 7 /Interpolate true ");

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1), null, out var result);

        var image = ImageObjectOf(replaced);
        Assert.Equal(7, (int)(image.GetDouble(new PdfName("StructParent")) ?? -1));
        Assert.True(((PdfBoolean)image[PdfName.Interpolate]).Value);
        Assert.DoesNotContain("StructParent", result.DroppedKeys);
        Assert.DoesNotContain("Interpolate", result.DroppedKeys);
    }

    [Fact]
    public void Replace_RewritesTheSampleDescriptionRatherThanInheritingIt()
    {
        // Original: 1-component gray, Flate-declared /Decode. The replacement is 3-component RGB,
        // so every one of those entries would misdescribe it.
        byte[] pdf = OneImagePdf(new byte[] { 0x40 }, 1, 1,
            colorSpace: "/DeviceGray", extraImageKeys: "/Decode [1 0] ");

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1), null, out var result);

        var image = ImageObjectOf(replaced);
        Assert.Equal("DeviceRGB", ((PdfName)image[PdfName.ColorSpace]).Value);
        Assert.Null(image[PdfName.Decode]);
        Assert.Contains("Decode", result.DroppedKeys);
        AssertIsBlue(CenterPixel(replaced), "gray original replaced by RGB");
    }

    // ================================================================
    //  Consequences the caller is told about
    // ================================================================

    [Fact]
    public void Replace_ReportsWhenTheImageIsSharedBySeveralPages()
    {
        byte[] pdf = SharedImagePdf(Red);

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1), null, out var result);

        Assert.Equal(2, result.PagesSharingImage);
        Assert.Contains(result.Warnings, w => w.Contains("2 pages", StringComparison.Ordinal));
        AssertIsBlue(CenterPixel(replaced, 0), "page 0");
        AssertIsBlue(CenterPixel(replaced, 1), "page 1");
    }

    [Fact]
    public void Replace_ReportsAnAspectRatioChange()
    {
        byte[] pdf = OneImagePdf(new byte[4 * 1 * 3], 4, 1);

        Replace(pdf, 0, ReplacementImage.Rgb24(new byte[1 * 4 * 3], 1, 4), null, out var stretched);
        Replace(pdf, 0, ReplacementImage.Rgb24(new byte[8 * 2 * 3], 8, 2), null, out var sameRatio);

        Assert.True(stretched.AspectRatioChanged, "4:1 replaced by 1:4 is a ratio change");
        Assert.Contains(stretched.Warnings, w => w.Contains("Aspect ratio", StringComparison.Ordinal));
        Assert.False(sameRatio.AspectRatioChanged, "4:1 replaced by 8:2 is the same ratio");
    }

    [Fact]
    public void Replace_ReportsNothingToWarnAboutForAPlainSwap()
    {
        byte[] pdf = OneImagePdf(Red, 1, 1);

        Replace(pdf, 0, ReplacementImage.Rgb24(Blue, 1, 1), null, out var result);

        Assert.Empty(result.Warnings);
        Assert.Empty(result.DroppedKeys);
        Assert.Equal(1, result.PagesSharingImage);
    }

    // ================================================================
    //  ReplacementImage validation
    // ================================================================

    [Fact]
    public void Jpeg_TakesItsDimensionsFromTheCodestream()
    {
        byte[] jpeg = EncodeJpeg(16, 8, SKColors.Blue);
        byte[] pdf = OneImagePdf(Red, 1, 1);

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Jpeg(jpeg), null, out var result);

        Assert.Equal(16, result.NewWidth);
        Assert.Equal(8, result.NewHeight);
        Assert.Equal("DCTDecode", ((PdfName)ImageObjectOf(replaced)[PdfName.Filter]).Value);
        AssertIsBlue(CenterPixel(replaced), "JPEG replacement");
    }

    [Fact]
    public void Jpeg_RejectsBytesWithNoFrameHeader()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ReplacementImage.Jpeg(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }));
        Assert.Contains("Start-Of-Frame", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_RejectsAComponentCountNoDeviceColourSpaceFits()
    {
        // SOI, then SOF0: length 17, precision 8, height 8, width 16, 2 components. PDF has no
        // two-component device colour space, so there is nothing honest to emit.
        // (1 / 3 / 4 components are supported — see PdfImageReplacerAdvancedTests.)
        byte[] twoChannel =
        {
            0xFF, 0xD8,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x08, 0x00, 0x10, 0x02,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        };

        var ex = Assert.Throws<ArgumentException>(() => ReplacementImage.Jpeg(twoChannel));
        Assert.Contains("2 components", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, 2, 11)]  // one byte short of 2x2x3
    [InlineData(2, 2, 13)]  // one byte over
    public void Rgb24_RejectsAByteCountThatDoesNotMatchTheDimensions(int w, int h, int byteCount)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ReplacementImage.Rgb24(new byte[byteCount], w, h));
        Assert.Contains($"Expected exactly {w * h * 3} bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gray8_AcceptsOneBytePerPixelAndRendersGray()
    {
        byte[] pdf = OneImagePdf(Red, 1, 1);

        byte[] replaced = Replace(pdf, 0, ReplacementImage.Gray8(new byte[] { 0x00 }, 1, 1));

        SKColor c = CenterPixel(replaced);
        Assert.True(c.Red < 60 && c.Green < 60 && c.Blue < 60,
            $"a 0x00 gray sample should render black, got {c}");
    }

    [Fact]
    public void Rgb24_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplacementImage.Rgb24(new byte[3], 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplacementImage.Rgb24(new byte[3], 1, -1));
    }

    private static byte[] EncodeJpeg(int width, int height, SKColor color)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        return data.ToArray();
    }
}
