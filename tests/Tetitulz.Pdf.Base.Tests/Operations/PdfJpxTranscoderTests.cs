// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfJpxTranscoder"/>: a document's HTJ2K images rewritten as Part 1 JPEG 2000 by an incremental update —
/// same object, same dictionary, the same picture.
/// </summary>
public sealed class PdfJpxTranscoderTests
{
    private static readonly JpxLosslessFixtures.Fixture Rgb = JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;

    /// <summary>A one-page PDF whose image (object 5) is the fixture's codestream transcoded to HTJ2K.</summary>
    private static byte[] HtPdf(string extras = "/ColorSpace /DeviceRGB /BitsPerComponent 8")
    {
        Assert.True(JpxTranscoding.TryToHighThroughput(Rgb.Codestream, out byte[] ht, out string reason), reason);
        Assert.True(JpxHeader.TryReadHighThroughput(ht, out bool isHt) && isHt);
        return JpxTestPdf.ImagePdf(ht, Rgb.Width, Rgb.Height, extras);
    }

    private static (byte[] Output, JpxTranscodeResult Result) Transcode(byte[] input)
    {
        using var i = new MemoryStream(input, writable: false);
        using var o = new MemoryStream();
        JpxTranscodeResult result = PdfOperations.TranscodeHtj2kImagesToPart1(i, o);
        return (o.ToArray(), result);
    }

    private static PdfStream Image(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Assert.IsType<PdfStream>(doc.GetObject(5));
    }

    /// <summary>
    /// The image is superseded in place: object 5 now holds a Part 1 codestream, its dictionary's every key but
    /// /Length as it was, the original bytes kept ahead of the appended revision, and the page draws the same
    /// pixels — the fixture's — before and after.
    /// </summary>
    [Fact]
    public void AnHtj2kImage_BecomesPart1_InPlace_DrawingTheSamePixels()
    {
        byte[] input = HtPdf();
        (byte[] output, JpxTranscodeResult result) = Transcode(input);

        Assert.Equal(new[] { 5 }, result.Transcoded);
        Assert.Empty(result.Skipped);
        Assert.Equal(input, output.Take(input.Length));
        Assert.True(output.Length > input.Length);

        PdfStream before = Image(input), after = Image(output);
        Assert.True(JpxHeader.TryReadHighThroughput(after.RawData, out bool ht));
        Assert.False(ht);
        Assert.Equal(before.Keys.Where(k => k.Value != "Length").Select(k => k.Value).OrderBy(k => k),
            after.Keys.Where(k => k.Value != "Length").Select(k => k.Value).OrderBy(k => k));

        using SKBitmap was = JpxTestPdf.Render(input), now = JpxTestPdf.Render(output);
        JpxTestPdf.AssertEveryPixel(now, (x, y) => JpxTestPdf.Pixel(Rgb, x, y));
        JpxTestPdf.AssertEveryPixel(was, (x, y) => JpxTestPdf.Pixel(Rgb, x, y));
    }

    /// <summary>A document without an HTJ2K image comes out byte for byte as it went in, nothing transcoded.</summary>
    [Fact]
    public void ADocumentWithoutHtj2k_IsCopiedUnchanged()
    {
        byte[] input = JpxTestPdf.ImagePdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8");
        (byte[] output, JpxTranscodeResult result) = Transcode(input);
        Assert.Equal(input, output);
        Assert.Empty(result.Transcoded);
    }

    /// <summary>
    /// Only a stream whose one filter is JPXDecode holds the codestream as its bytes: one behind a filter chain is
    /// left alone (not transcoded, not reported), and the control — the same image unchained — is transcoded.
    /// </summary>
    [Fact]
    public void AnImageBehindAFilterChain_IsLeftAlone()
    {
        Assert.True(JpxTranscoding.TryToHighThroughput(Rgb.Codestream, out byte[] ht, out string reason), reason);
        byte[] chained = JpxTestPdf.Pdf(Rgb.Width, Rgb.Height, $"q {Rgb.Width} 0 0 {Rgb.Height} 0 0 cm /Im0 Do Q",
            ($"<< /Type /XObject /Subtype /Image /Width {Rgb.Width} /Height {Rgb.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter [/ASCIIHexDecode /JPXDecode]",
             System.Text.Encoding.ASCII.GetBytes(string.Concat(ht.Select(b => b.ToString("X2"))) + ">")));
        Assert.Empty(Transcode(chained).Result.Transcoded);
        Assert.Equal(new[] { 5 }, Transcode(HtPdf()).Result.Transcoded);
    }

    /// <summary>
    /// The document of <see cref="HtPdf"/> built by hand: the image at <paramref name="generation"/>, and with
    /// <paramref name="certified"/> a DocMDP certification at P=1 (no changes permitted) in the catalog.
    /// </summary>
    private static byte[] HtPdfBuilt(int generation, bool certified)
    {
        Assert.True(JpxTranscoding.TryToHighThroughput(Rgb.Codestream, out byte[] ht, out string reason), reason);
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R " + (certified ? "/Perms << /DocMDP 6 0 R >> " : "") + ">>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Rgb.Width} {Rgb.Height}] " +
                    $"/Resources << /XObject << /Im0 5 {generation} R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", $"q {Rgb.Width} 0 0 {Rgb.Height} 0 0 cm /Im0 Do Q")
            .ObjAtGeneration(5, generation, $"<< /Type /XObject /Subtype /Image /Width {Rgb.Width} /Height {Rgb.Height} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode /Length {ht.Length} >>", ht);
        if (certified)
            b.Obj(6, "<< /Type /Sig /Reference [ << /Type /SigRef /TransformMethod /DocMDP " +
                     "/TransformParams << /Type /TransformParams /P 1 /V /1.2 >> >> ] /ByteRange [0 100 200 300] >>");
        return b.Finish();
    }

    /// <summary>A DocMDP certification refuses, naming it — changing an image is no change any /P permits — and the opt-in proceeds.</summary>
    [Fact]
    public void ACertifiedDocument_IsRefused_UnlessAllowed()
    {
        byte[] input = HtPdfBuilt(0, certified: true);
        var ex = Assert.Throws<PdfException>(() => Transcode(input));
        Assert.Contains("DocMDP", ex.Message, StringComparison.Ordinal);

        using var i = new MemoryStream(input, writable: false);
        using var o = new MemoryStream();
        Assert.Equal(new[] { 5 }, PdfOperations.TranscodeHtj2kImagesToPart1(i, o, allowCertifiedDocument: true).Transcoded);
        Assert.Equal(new[] { 5 }, Transcode(HtPdfBuilt(0, certified: false)).Result.Transcoded);
    }

    /// <summary>An image at a non-zero generation is superseded at that generation (§7.5.6), not at 0 — a reference to it still reaches it.</summary>
    [Fact]
    public void AnImageAtANonZeroGeneration_IsSupersededAtItsGeneration()
    {
        byte[] input = HtPdfBuilt(2, certified: false);
        (byte[] output, JpxTranscodeResult result) = Transcode(input);
        Assert.Equal(new[] { 5 }, result.Transcoded);
        string appended = System.Text.Encoding.Latin1.GetString(output, input.Length, output.Length - input.Length);
        Assert.Contains("5 2 obj", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("5 0 obj", appended, StringComparison.Ordinal);
        using SKBitmap now = JpxTestPdf.Render(output);
        JpxTestPdf.AssertEveryPixel(now, (x, y) => JpxTestPdf.Pixel(Rgb, x, y));
    }

    /// <summary>
    /// An encrypted document is refused: the superseding streams would be written in clear text. The
    /// refusal is the clear-text revision writers' one (<c>EncryptedRevision.Refuse</c>), naming the route
    /// that writes encrypted.
    /// </summary>
    [Fact]
    public void AnEncryptedDocument_IsRefused()
    {
        using var i = new MemoryStream(HtPdf(), writable: false);
        using var encrypted = new MemoryStream();
        Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(i, encrypted, new Tetitulz.Pdf.Base.Encryption.EncryptionParameters
        {
            UserPassword = "",
            OwnerPassword = "owner",
            Algorithm = Tetitulz.Pdf.Base.Encryption.EncryptionAlgorithm.AES128,
        });
        var ex = Assert.Throws<PdfEncryptedDocumentException>(() => Transcode(encrypted.ToArray()));
        Assert.Contains("Cannot transcode the images of an encrypted document", ex.Message, StringComparison.Ordinal);
        Assert.Contains("PdfEncryptedRoundTrip.Run", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The document's images are transcoded under one budget together (<c>PdfLimits.MaxJpxDecodeWork</c>), each also
/// under its own bytes' allowance: a document of many images is one ceiling of work, not one a image. Under a
/// ceiling one image's transcode fits and two do not, the first is transcoded and the second left as it was,
/// named with the document's reason.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class PdfJpxTranscoderDocumentBudgetTests
{
    private static readonly JpxLosslessFixtures.Fixture Rgb = JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;

    [Fact]
    public void Adversarial_many_images_share_the_documents_budget()
    {
        Assert.True(JpxTranscoding.TryToHighThroughput(Rgb.Codestream, out byte[] ht, out string reason), reason);
        var probe = new Tetitulz.Pdf.Core.Util.WorkBudget(long.MaxValue / 4, "probe");
        Assert.True(JpxTranscoding.TryToPart1(ht, probe, out _, out reason), reason);
        long one = probe.Spent;
        Assert.True(one > 10, $"one image's transcode spent {one} units");

        byte[] input = TwoImages(ht);
        try
        {
            PdfLimits.MaxJpxDecodeWork = one + one / 2;
            using var i = new MemoryStream(input, writable: false);
            using var o = new MemoryStream();
            JpxTranscodeResult result = PdfOperations.TranscodeHtj2kImagesToPart1(i, o);

            Assert.Single(result.Transcoded);
            var skipped = Assert.Single(result.Skipped);
            Assert.Contains("the document's JPEG 2000 transcoding exceeded", skipped.Value, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: under the default ceiling both images are transcoded.</summary>
    [Fact]
    public void Two_images_under_the_default_ceiling_are_both_transcoded()
    {
        Assert.True(JpxTranscoding.TryToHighThroughput(Rgb.Codestream, out byte[] ht, out string reason), reason);
        using var i = new MemoryStream(TwoImages(ht), writable: false);
        using var o = new MemoryStream();
        JpxTranscodeResult result = PdfOperations.TranscodeHtj2kImagesToPart1(i, o);

        Assert.Equal(2, result.Transcoded.Count);
        Assert.Empty(result.Skipped);
    }

    private static byte[] TwoImages(byte[] ht)
    {
        string image = $"<< /Type /XObject /Subtype /Image /Width {Rgb.Width} /Height {Rgb.Height} " +
                       $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode /Length {ht.Length} >>";
        return new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {2 * Rgb.Width} {Rgb.Height}] " +
                    "/Resources << /XObject << /Im0 5 0 R /Im1 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", $"q {Rgb.Width} 0 0 {Rgb.Height} 0 0 cm /Im0 Do Q q {Rgb.Width} 0 0 {Rgb.Height} {Rgb.Width} 0 cm /Im1 Do Q")
            .ObjAtGeneration(5, 0, image, ht)
            .ObjAtGeneration(6, 0, image, ht)
            .Finish();
    }
}
