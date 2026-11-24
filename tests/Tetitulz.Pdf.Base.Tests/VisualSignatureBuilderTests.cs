// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Tests.Helpers;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Unit tests for <see cref="VisualSignatureBuilder"/> — specifically the JPEG dimension
/// parser's handling of standalone markers and 0xFF fill bytes.
/// </summary>
public class VisualSignatureBuilderTests
{
    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    /// <summary>Builds a minimal valid JFIF byte array with a SOF0 segment.</summary>
    private static byte[] MakeJpeg(int width, int height)
    {
        var bytes = new System.Collections.Generic.List<byte>();

        // SOI
        bytes.Add(0xFF); bytes.Add(0xD8);

        // APP0 (JFIF) — minimal, 16-byte segment
        bytes.Add(0xFF); bytes.Add(0xE0);
        bytes.Add(0x00); bytes.Add(0x10); // length = 16
        bytes.AddRange(new byte[] { 0x4A, 0x46, 0x49, 0x46, 0x00 }); // "JFIF\0"
        bytes.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 }); // rest of APP0

        // SOF0 (baseline DCT)
        bytes.Add(0xFF); bytes.Add(0xC0);
        bytes.Add(0x00); bytes.Add(0x11); // length = 17 (8 + 3 * num_components=3)
        bytes.Add(0x08);                  // precision = 8 bits
        bytes.Add((byte)(height >> 8)); bytes.Add((byte)(height & 0xFF));
        bytes.Add((byte)(width >> 8));  bytes.Add((byte)(width & 0xFF));
        bytes.Add(0x03);                  // num_components = 3 (Y, Cb, Cr)
        // Component specs (3 ֳ— 3 bytes)
        bytes.AddRange(new byte[] { 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });

        // EOI
        bytes.Add(0xFF); bytes.Add(0xD9);

        return bytes.ToArray();
    }

    // ----------------------------------------------------------------
    //  Happy path
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_ValidJpeg_ReturnsCorrectSize()
    {
        byte[] jpeg = MakeJpeg(320, 240);
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(320, w);
        Assert.Equal(240, h);
    }

    [Fact]
    public void GetJpegDimensions_LargeImage_ReturnsCorrectSize()
    {
        byte[] jpeg = MakeJpeg(3840, 2160);
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(3840, w);
        Assert.Equal(2160, h);
    }

    // ----------------------------------------------------------------
    //  Bug A: EOI before SOF must not crash / should return defaults
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_EoiBeforeSof_ReturnsDefaults()
    {
        // SOI + EOI — no SOF segment
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xD9 };
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(100, w);  // defaults
        Assert.Equal(100, h);
    }

    // ----------------------------------------------------------------
    //  Bug B: SOS before SOF must not crash / should return defaults
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_SosBeforeSof_ReturnsDefaults()
    {
        // SOI + SOS (with length 0x000C) — no SOF
        byte[] jpeg =
        {
            0xFF, 0xD8,              // SOI
            0xFF, 0xDA,              // SOS marker
            0x00, 0x0C,              // segment length = 12 (length field, includes itself)
            0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00,
            0xFF, 0xD9               // EOI
        };
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    // ----------------------------------------------------------------
    //  Bug C: RST marker (standalone, no length) must not corrupt pos
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_RstMarkerBeforeSof_ReturnsDefaults()
    {
        // Pathological: SOI + RST0 (no length) + EOI — no SOF
        // Reading the next 2 bytes after RST0 as a segment length would treat part of
        // EOI as offset data, potentially reading out of bounds.
        byte[] jpeg =
        {
            0xFF, 0xD8,  // SOI
            0xFF, 0xD0,  // RST0 (no length)
            0xFF, 0xD9   // EOI
        };
        VisualSignatureBuilder.GetJpegDimensions(jpeg, out int w, out int h);
        // No exception; defaults returned
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    // ----------------------------------------------------------------
    //  Bug D: 0xFF fill bytes before SOF marker
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_FillBytesBeforeSof_ParsesCorrectly()
    {
        // Build JPEG with a 0xFF fill byte injected before the SOF0 marker byte.
        // JPEG spec allows multiple 0xFF bytes between segments as padding.
        byte[] jpeg = MakeJpeg(100, 200);

        // Find where SOF0 marker is: 0xFF 0xC0
        int sofIdx = -1;
        for (int i = 0; i < jpeg.Length - 1; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xC0) { sofIdx = i; break; }
        Assert.True(sofIdx >= 0, "SOF0 not found in test JPEG");

        // Insert a 0xFF fill byte before the 0xC0 to make it: 0xFF 0xFF 0xC0
        var withFill = new System.Collections.Generic.List<byte>(jpeg);
        withFill.Insert(sofIdx + 1, 0xFF);
        byte[] paddedJpeg = withFill.ToArray();

        VisualSignatureBuilder.GetJpegDimensions(paddedJpeg, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(200, h);
    }

    // ----------------------------------------------------------------
    //  Edge case: not a JPEG at all
    // ----------------------------------------------------------------

    [Fact]
    public void GetJpegDimensions_NotJpeg_ReturnsDefaults()
    {
        byte[] notJpeg = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header
        VisualSignatureBuilder.GetJpegDimensions(notJpeg, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    [Fact]
    public void GetJpegDimensions_TooShort_ReturnsDefaults()
    {
        VisualSignatureBuilder.GetJpegDimensions(new byte[] { 0xFF }, out int w, out int h);
        Assert.Equal(100, w);
        Assert.Equal(100, h);
    }

    // ----------------------------------------------------------------
    //  The appearance Acrobat validates: an image only inside an inner form
    // ----------------------------------------------------------------

    private const float StampX = 300, StampY = 400, StampW = 60, StampH = 40;

    /// <summary>
    /// <b>The image stamp's appearance names an inner form, never the image</b>, and the inner form
    /// carries its own <c>/Resources</c> naming the image. Acrobat Reader reports "Expected a dict
    /// object" for a signature whose appearance names an image under <c>/XObject</c>, and validates
    /// this shape as a DocMDP certification — so the certification is what is signed here, and the
    /// library's own validator still accepts it.
    /// </summary>
    [Fact]
    public void TheImageStamp_IsDrawnThroughAnInnerForm_UnderACertificationThatValidates()
    {
        byte[] signed = SignImageStamp(RedPng(10, 10));

        using var doc = global::Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(signed));
        var appearance = WidgetAppearance(doc);

        Assert.Equal(1, global::Tetitulz.Pdf.Base.Tests.Signing.SignatureAppearanceXObjectRule.AssertAndCountImages(appearance, doc.ResolveObject));
        Assert.Equal("q /SigForm Do Q", Latin1(appearance));

        var resources = (PdfDictionary)doc.ResolveObject(appearance[PdfName.Resources]);
        var xobjects = (PdfDictionary)doc.ResolveObject(resources[PdfName.XObject]);
        var inner = (PdfStream)doc.ResolveObject(xobjects[new PdfName("SigForm")]);
        Assert.Equal("q 60.00 0 0 40.00 0 0 cm /SigImg Do Q", Latin1(inner));

        // Signed as a certification, and the library's validator accepts it.
        var perms = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Perms]) as PdfDictionary;
        Assert.NotNull(perms?[PdfName.DocMDP]);
        var result = PdfSignatureValidator.Validate(new MemoryStream(signed));
        Assert.True(result.HasSignature);
        Assert.True(result.IsValid, "the certified stamp did not validate: " + result.ErrorMessage);
    }

    /// <summary>
    /// The stamp still draws where its widget is: the inner form takes the appearance's box and the
    /// identity matrix, so nothing moves or is clipped. The pixel beside the stamp is the control.
    /// </summary>
    [Fact]
    public void TheImageStamp_RendersInsideTheWidgetRectangle()
    {
        byte[] signed = SignImageStamp(RedPng(10, 10));

        var page = PdfOperations.RenderPageToPixels(new MemoryStream(signed), 0, dpi: 72);
        int top = page.Height - (int)(StampY + StampH);   // device y of the stamp's top edge

        var inside = Pixel(page, (int)(StampX + StampW / 2), top + (int)(StampH / 2));
        Assert.True(inside.r > 200 && inside.g < 60 && inside.b < 60,
            $"the stamp's centre rendered as ({inside.r}, {inside.g}, {inside.b}), not red");

        var beside = Pixel(page, (int)(StampX + StampW + 20), top + (int)(StampH / 2));
        Assert.True(beside.r > 200 && beside.g > 200 && beside.b > 200,
            $"the page beside the stamp rendered as ({beside.r}, {beside.g}, {beside.b}), not white");
    }

    /// <summary>
    /// The text-and-logo appearance follows the same rule — its logo inside an inner form, its font
    /// named directly — and a certification over it still validates.
    /// </summary>
    [Fact]
    public void ATextAndLogoAppearance_DrawsTheLogoThroughAnInnerForm_UnderACertificationThatValidates()
    {
        var (pfx, password) = TestCertificateGenerator.GenerateTestCertificate();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        VisualSignatureBuilder.Sign(input, output,
            new SignatureAppearanceOptions
            {
                SignerName = "Ada Lovelace", SigningDate = "2026-11-22", LogoPngBytes = RedPng(10, 10),
                Width = 200, Height = 60,
            },
            new SignatureParameters
            {
                CertificateStream = new MemoryStream(pfx), CertificatePassword = password,
                DocMdpPermission = DocMdpPermission.NoChanges,
            },
            pageNumber: 1, x: 50, y: 50);
        byte[] signed = output.ToArray();

        using var doc = global::Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(signed));
        var appearance = WidgetAppearance(doc);

        Assert.Equal(1, global::Tetitulz.Pdf.Base.Tests.Signing.SignatureAppearanceXObjectRule.AssertAndCountImages(appearance, doc.ResolveObject));
        var resources = (PdfDictionary)doc.ResolveObject(appearance[PdfName.Resources]);
        Assert.NotNull(resources[PdfName.Font]);
        Assert.Contains("(Ada Lovelace) Tj", Latin1(appearance), StringComparison.Ordinal);

        var result = PdfSignatureValidator.Validate(new MemoryStream(signed));
        Assert.True(result.IsValid, "the certified text-and-logo signature did not validate: " + result.ErrorMessage);
    }

    private static byte[] SignImageStamp(byte[] image)
    {
        var (pfx, password) = TestCertificateGenerator.GenerateTestCertificate();
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        VisualSignatureBuilder.Sign(input, output, image,
            new SignatureParameters
            {
                CertificateStream = new MemoryStream(pfx), CertificatePassword = password,
                DocMdpPermission = DocMdpPermission.NoChanges,
            },
            pageNumber: 1, x: StampX, y: StampY, width: StampW, height: StampH);
        return output.ToArray();
    }

    /// <summary>The signature widget's <c>/AP /N</c> form.</summary>
    private static PdfStream WidgetAppearance(global::Tetitulz.Pdf.Core.Document.PdfDocument doc)
    {
        var annots = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]);
        for (int i = 0; i < annots.Count; i++)
        {
            if (doc.ResolveObject(annots[i]) is PdfDictionary widget
                && (doc.ResolveObject(widget[PdfName.FT]) as PdfName)?.Value == "Sig")
            {
                var ap = (PdfDictionary)doc.ResolveObject(widget[PdfName.AP]);
                return (PdfStream)doc.ResolveObject(ap[PdfName.N]);
            }
        }
        throw new Xunit.Sdk.XunitException("no /FT /Sig widget on page 1");
    }

    private static string Latin1(PdfStream stream) => System.Text.Encoding.Latin1.GetString(stream.GetDecodedData());

    private static (int r, int g, int b) Pixel(global::Tetitulz.Pdf.Base.Rendering.RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
    }

    /// <summary>An opaque red RGB PNG.</summary>
    private static byte[] RedPng(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3) rgb[i] = 220;
        return global::Tetitulz.Pdf.Base.Rendering.PdfRawImageEncoder.EncodePng(rgb, width, height, 3);
    }
}
