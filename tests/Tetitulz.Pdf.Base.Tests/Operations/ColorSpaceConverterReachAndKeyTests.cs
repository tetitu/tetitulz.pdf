// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Which images <c>PdfColorSpaceConverter</c> reaches, what it does with the keys that describe an
/// image in its source colour space, and what <c>GetColorSpaces</c> reports about the same document.
/// </summary>
/// <remarks>
/// <para><b>Reach.</b> The converter walked the pages' own <c>/XObject</c> dictionaries: an RGB image
/// inside a form XObject survived a conversion that reported success, and an image two pages shared
/// was converted and counted once per page. It now reads <c>DocumentImageIndex</c>, the walk
/// <c>PdfImageReplacer</c> uses.</para>
///
/// <para><b>Keys.</b> <c>/Decode</c> was dropped on the stated reasoning that the stored samples
/// already matched the identity map; they did not match what the image showed, so an inverted image
/// came out as a negative. A colour key was dropped with "transparency lost"; it is now carried
/// across as the explicit mask it describes.</para>
/// </remarks>
public class ColorSpaceConverterReachAndKeyTests
{
    // ------------------------------------------------------------------ reach

    [Fact]
    public void AnImageInsideAFormXObjectIsConverted()
    {
        byte[] pdf = ImageFixtures.ImageInsideFormPdf(ImageFixtures.Red);

        int converted = ToGray(pdf, out byte[] output);

        Assert.Equal(1, converted);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.DeviceGray, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);
        // BT.601, truncating: 255 * 299 / 1000.
        Assert.Equal(new byte[] { 76 }, image.GetDecodedData());
    }

    /// <summary>
    /// One image object drawn by two pages is one image: converted once and counted once. The
    /// incremental writer collapsed the duplicate object, so the count was the visible symptom.
    /// </summary>
    [Fact]
    public void AnImageTwoPagesDrawIsConvertedAndCountedOnce()
    {
        byte[] pdf = ImageFixtures.SharedImagePdf(ImageFixtures.Green);

        int converted = ToGray(pdf, out byte[] output);

        Assert.Equal(1, converted);
        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.DeviceGray, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);
    }

    /// <summary>A form listing itself among its own resources is walked once, and its image found.</summary>
    [Fact]
    public void ASelfReferencingForm_IsWalkedOnceAndItsImageConverted()
    {
        byte[] pdf = ImageFixtures.SelfReferencingFormPdf(ImageFixtures.Blue);

        int converted = ToGray(pdf, out _);

        Assert.Equal(1, converted);
    }

    // ------------------------------------------------------------------ refusal

    /// <summary>
    /// An encrypted document loads no catalog, so the walk found no pages and the conversion
    /// returned 0 with the input copied through — "no RGB images" about a document it never read.
    /// </summary>
    [Fact]
    public void AnEncryptedDocumentIsRefusedRatherThanReportedAsHavingNoImages()
    {
        byte[] pdf = EncryptedImagePdf();

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), output));

        Assert.Contains("encrypted", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>The control: the same document without its /Encrypt entry converts.</summary>
    [Fact]
    public void TheSameDocumentUnencryptedConverts()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);

        Assert.Equal(1, ToGray(pdf, out _));
    }

    // ------------------------------------------------------------------ identity

    /// <summary>
    /// §7.5.6: an updated object keeps its number and its generation. The converted image was
    /// written at generation 0 whatever the original's, so after a file had reused the number, every
    /// <c>5 1 R</c> named an object the new cross-reference entry no longer described.
    /// </summary>
    [Fact]
    public void AConvertedImageKeepsItsGeneration()
    {
        byte[] samples = ImageFixtures.Red;
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Im0 5 1 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 72 0 0 72 0 0 cm /Im0 Do Q")
            .ObjAtGeneration(5, 1, ImageFixtures.OnePixelImageDict(samples), samples)
            .Finish();

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        string appended = Encoding.Latin1.GetString(output, pdf.Length, output.Length - pdf.Length);
        // The appended revision begins at the object; anchored so "15 1 obj" could not pass.
        Assert.Matches(@"(^|\n)5 1 obj", appended);
        Assert.DoesNotMatch(@"(^|\n)5 0 obj", appended);
    }

    // ------------------------------------------------------------------ /Decode

    /// <summary>
    /// A stored negative under <c>/Decode [1 0 1 0 1 0]</c> shows 255 − 64 = 191 in every channel,
    /// and grey 191 is what it converts to. Converting the stored samples gave 64 — the negative.
    /// </summary>
    [Fact]
    public void TheDecodeArrayIsAppliedToTheSamplesBeforeConversion()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(2, 1, 64, 64, 64), 2, 1,
            extraImageKeys: "/Decode [1 0 1 0 1 0] ");

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(new byte[] { 191, 191 }, image.GetDecodedData());
        Assert.Null(image[PdfName.Decode]);
        Assert.Null(PdfColorSpaceConverter.LastConversionWarning);
    }

    /// <summary>Each component maps through its own pair — a per-channel /Decode is not averaged.</summary>
    [Fact]
    public void EachComponentMapsThroughItsOwnDecodePair()
    {
        // Red inverted, green kept, blue forced to 0: (64,64,64) shows (191, 64, 0).
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.SolidRgb(1, 1, 64, 64, 64), 1, 1,
            extraImageKeys: "/Decode [1 0 0 1 0 0] ");

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        // (191*299 + 64*587 + 0*114) / 1000 = 94.677 → 94.
        Assert.Equal(new byte[] { 94 }, image.GetDecodedData());
    }

    /// <summary>
    /// An array that cannot be read says nothing about what the image shows, so the image is left
    /// as it was — and not counted — and the warning names it.
    /// </summary>
    [Fact]
    public void AnImageWhoseDecodeCannotBeReadIsLeftUnconvertedAndNamed()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1,
            extraImageKeys: "/Decode [1 0 1 0 1 /Zero] ");

        Assert.Equal(0, ToGray(pdf, out byte[] output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Equal(PdfName.DeviceRGB, doc.ResolveObject(image[PdfName.ColorSpace]) as PdfName);
        Assert.Contains("/Im0", PdfColorSpaceConverter.LastConversionWarning, StringComparison.Ordinal);
        Assert.Contains("/Decode", PdfColorSpaceConverter.LastConversionWarning, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ colour key

    /// <summary>
    /// A 2×2 image whose top-left and bottom-right pixels fall in the key becomes a grey image with
    /// an explicit mask: 1 marks the keyed pixels (§8.9.6.2's default decode), rows padded to a byte
    /// — <c>10000000</c>, <c>01000000</c>.
    /// </summary>
    [Fact]
    public void AColourKeyIsCarriedAcrossAsAnExplicitStencilMask()
    {
        byte[] samples =
        {
            5, 5, 5,     64, 64, 64,
            64, 64, 64,  5, 5, 5,
        };
        byte[] pdf = ImageFixtures.OneImagePdf(samples, 2, 2, extraImageKeys: "/Mask [0 10 0 10 0 10] ");

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        var mask = Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(image[PdfName.Mask]));
        Assert.Equal(PdfBoolean.True, doc.ResolveObject(mask[PdfName.ImageMask]));
        Assert.Equal(1, (doc.ResolveObject(mask[PdfName.BitsPerComponent]) as PdfNumber)?.IntValue);
        Assert.Equal(2, (doc.ResolveObject(mask[PdfName.Width]) as PdfNumber)?.IntValue);
        Assert.Equal(2, (doc.ResolveObject(mask[PdfName.Height]) as PdfNumber)?.IntValue);
        Assert.Null(mask[PdfName.ColorSpace]);
        Assert.Equal(new byte[] { 0x80, 0x40 }, mask.GetDecodedData());
        Assert.Null(PdfColorSpaceConverter.LastConversionWarning);
    }

    /// <summary>
    /// A key that cannot be evaluated — two ranges for a three-component image — is removed, and
    /// the caller is told the transparency went with it.
    /// </summary>
    [Fact]
    public void AColourKeyThatCannotBeEvaluatedIsDroppedAndTheLostTransparencyNamed()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1, extraImageKeys: "/Mask [0 10 0 10] ");

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        Assert.Null(image[PdfName.Mask]);
        Assert.Contains("transparency", PdfColorSpaceConverter.LastConversionWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stencil-mask stream is independent of the base image's colour space and stays as it was.
    /// </summary>
    [Fact]
    public void AStencilMaskStreamIsKept()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1, extraImageKeys: "/Mask 6 0 R ",
            extraObjects: b => b.Obj(6,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /Length 1 >>",
                new byte[] { 0x00 }));

        Assert.Equal(1, ToGray(pdf, out byte[] output));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var image = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5));
        var maskRef = Assert.IsType<PdfReference>(image[PdfName.Mask]);
        Assert.Equal(6, maskRef.ReferenceObjectNumber);
    }

    // ------------------------------------------------------------------ warnings

    /// <summary>
    /// The profile fallback wrote the warning slot directly and the end of the run overwrote it, so
    /// a caller told about a dropped key lost the news that the profile was never applied. Both are
    /// reported now.
    /// </summary>
    [Fact]
    public void AProfileFallbackAndAnotherNoteAreBothReported()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1, extraImageKeys: "/Mask [0 10 0 10] ");

        using var output = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToCmyk(
            new MemoryStream(pdf), output, destinationIccProfile: new byte[16], gcrFactor: 0.8);

        Assert.Equal(1, converted);
        string warning = PdfColorSpaceConverter.LastConversionWarning;
        Assert.Contains("destination ICC profile was not used", warning, StringComparison.Ordinal);
        Assert.Contains("transparency", warning, StringComparison.Ordinal);
    }

    /// <summary>One reason, however many images give it, is reported once.</summary>
    [Fact]
    public void AReasonTwoImagesShareIsReportedOnce()
    {
        byte[] pdf = ImageFixtures.TwoImagePdf(ImageFixtures.Red, ImageFixtures.Green);

        using var output = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToCmyk(
            new MemoryStream(pdf), output, destinationIccProfile: new byte[16], gcrFactor: 0.8);

        Assert.Equal(2, converted);
        string warning = PdfColorSpaceConverter.LastConversionWarning;
        const string reason = "destination ICC profile was not used";
        int first = warning.IndexOf(reason, StringComparison.Ordinal);
        Assert.True(first >= 0, warning);
        Assert.Equal(-1, warning.IndexOf(reason, first + 1, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ inspection

    /// <summary>
    /// Page numbers come back ascending. The walk sees a page's declared spaces before any image's
    /// pages, so an image on page 1 using a space page 2 declares arrived as [2, 1].
    /// </summary>
    [Fact]
    public void PageNumbersAreReportedAscending()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Im0 5 0 R >> >> >>")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .Obj(6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /ColorSpace << /CS0 /DeviceRGB >> >> >>")
            .Finish();

        var rgb = Assert.Single(PdfColorSpaceConverter.GetColorSpaces(new MemoryStream(pdf)));
        Assert.Equal(new[] { 1, 2 }, rgb.PageNumbers);
    }

    /// <summary>
    /// A space is reported by its family, not by the resource dictionary's key: "CS0" in a list
    /// whose names promise "DeviceRGB", "ICCBased" would say nothing.
    /// </summary>
    [Fact]
    public void AResourceColourSpaceIsReportedByItsFamilyNotItsResourceName()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /ColorSpace << /CS0 [/ICCBased 4 0 R] /CS1 /DeviceCMYK >> >> >>")
            .StreamObj(4, "<< /N 3", "icc")
            .Finish();

        var spaces = PdfColorSpaceConverter.GetColorSpaces(new MemoryStream(pdf));

        Assert.Equal(new[] { "DeviceCMYK", "ICCBased" },
            spaces.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.All(spaces, s => Assert.False(s.IsImageColorSpace));
    }

    /// <summary>A form XObject's own /ColorSpace resources and its image are both reported.</summary>
    [Fact]
    public void AFormXObjectsColourSpacesAndImagesAreReported()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Fx 6 0 R >> >> /Contents 4 0 R >>")
            .StreamObj(4, "<<", "/Fx Do")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .StreamObj(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] " +
                          "/Resources << /ColorSpace << /Spot [/Separation /Gold /DeviceCMYK 7 0 R] >> " +
                          "/XObject << /Logo 5 0 R >> >>",
                       "/Logo Do")
            .Obj(7, "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0.2 1 0] /N 1 >>")
            .Finish();

        var spaces = PdfColorSpaceConverter.GetColorSpaces(new MemoryStream(pdf));

        var separation = Assert.Single(spaces, s => s.Name == "Separation");
        Assert.False(separation.IsImageColorSpace);
        Assert.Equal(new[] { 1 }, separation.PageNumbers);
        var rgb = Assert.Single(spaces, s => s.Name == "DeviceRGB");
        Assert.True(rgb.IsImageColorSpace);
    }

    /// <summary>
    /// A space a page declares and an image then uses is an image colour space, not whatever the
    /// first sighting said.
    /// </summary>
    [Fact]
    public void ASpaceDeclaredBeforeAnImageUsesItIsStillAnImageColourSpace()
    {
        byte[] pdf = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        // The page's resources also declare DeviceRGB by name, ahead of the image.
        string text = Encoding.Latin1.GetString(pdf).Replace(
            "/Resources << /XObject", "/Resources << /ColorSpace << /CS0 /DeviceRGB >> /XObject");
        pdf = Rebuild(text);

        var rgb = Assert.Single(PdfColorSpaceConverter.GetColorSpaces(new MemoryStream(pdf)));
        Assert.Equal("DeviceRGB", rgb.Name);
        Assert.True(rgb.IsImageColorSpace);
    }

    // ------------------------------------------------------------------

    private static int ToGray(byte[] pdf, out byte[] output)
    {
        using var ms = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), ms);
        output = ms.ToArray();
        return converted;
    }

    /// <summary>One image page with an /Encrypt entry in its trailer.</summary>
    private static byte[] EncryptedImagePdf() =>
        new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] " +
                    "/Resources << /XObject << /Im0 5 0 R >> >> >>")
            .Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                    "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                    "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>")
            .Obj(5, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
            .Finish("/Encrypt 4 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>] ");

    /// <summary>
    /// Rebuilds a fixture whose object text was edited, so the cross-reference offsets are right
    /// again: objects are re-read from the text in order.
    /// </summary>
    private static byte[] Rebuild(string text)
    {
        var builder = new PdfFixtureBuilder().Header();
        int pos = 0;
        while (true)
        {
            int start = text.IndexOf(" 0 obj\n", pos, StringComparison.Ordinal);
            if (start < 0) break;
            int numStart = text.LastIndexOf('\n', start) + 1;
            int num = int.Parse(text.Substring(numStart, start - numStart));
            int bodyStart = start + " 0 obj\n".Length;
            int end = text.IndexOf("\nendobj\n", bodyStart, StringComparison.Ordinal);
            string body = text.Substring(bodyStart, end - bodyStart);
            int streamAt = body.IndexOf("\nstream\n", StringComparison.Ordinal);
            if (streamAt < 0)
                builder.Obj(num, body);
            else
            {
                string dict = body.Substring(0, streamAt);
                string data = body.Substring(streamAt + "\nstream\n".Length);
                data = data.Substring(0, data.Length - "\nendstream".Length);
                builder.Obj(num, dict, Encoding.Latin1.GetBytes(data));
            }
            pos = end;
        }
        return builder.Finish();
    }
}
