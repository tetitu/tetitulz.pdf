// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfImageInserter"/> and the declared pixel size it writes into the page.
/// </summary>
/// <remarks>
/// <para><b>This is a bomb authored INTO output, not accepted from input</b>, which is why the
/// limits were missing here while every read path enforced them. <c>InsertJpeg</c> takes the
/// dimensions from the JPEG's own frame header and checks that the caller <i>agrees</i> with the
/// header — which is not the same as checking the header. A 65535×65535 SOF went straight into the
/// produced XObject's <c>/Width</c> and <c>/Height</c>.</para>
///
/// <para>Our own renderer survives such a file (<c>ImageSampleLayout</c> refuses the same limits
/// and returns null). The point is the file we hand to somebody else's reader.</para>
/// </remarks>
public class ImageInsertionLimitTests
{
    // ================================================================ fixtures

    /// <summary>
    /// A minimal baseline JPEG whose SOF0 declares <paramref name="width"/>×<paramref name="height"/>.
    /// </summary>
    /// <remarks>
    /// Hand-assembled rather than encoded, because the whole point is a header that declares a size
    /// no encoder would produce. Only the markers <c>JpegImageDictionary</c> reads are present.
    /// </remarks>
    internal static byte[] JpegDeclaring(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };              // SOI

        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });  // SOF0, length 17, 8-bit
        bytes.Add((byte)(height >> 8)); bytes.Add((byte)height);
        bytes.Add((byte)(width >> 8));  bytes.Add((byte)width);
        bytes.Add(0x03);                                        // three components
        foreach (byte id in new byte[] { 1, 2, 3 })
        {
            bytes.Add(id); bytes.Add(0x11); bytes.Add(0x00);
        }

        bytes.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 });
        bytes.AddRange(new byte[] { 0x00, 0x00, 0x00, 0x00 });  // a token of entropy-coded data
        bytes.AddRange(new byte[] { 0xFF, 0xD9 });              // EOI

        return bytes.ToArray();
    }

    private static byte[] Insert(byte[] jpeg, int width, int height)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfImageInserter.InsertJpeg(input, output, 0, jpeg, width, height, 10, 10, 100, 100);
        return output.ToArray();
    }

    /// <summary>The <c>/Width</c> and <c>/Height</c> of the first image XObject in the output.</summary>
    private static (int width, int height) EmbeddedImageSize(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;

        foreach (var key in xobjects.Keys)
        {
            if (doc.ResolveObject(xobjects[key]) is PdfStream image &&
                (doc.ResolveObject(image[PdfName.Subtype]) as PdfName)?.Value == "Image")
            {
                return ((int)(doc.ResolveObject(image[PdfName.Width]) as PdfNumber).DoubleValue,
                        (int)(doc.ResolveObject(image[PdfName.Height]) as PdfNumber).DoubleValue);
            }
        }

        throw new InvalidOperationException("the output carries no image XObject");
    }

    // ================================================================ the control

    /// <summary>
    /// An ordinary JPEG still embeds, with the size its header declares reaching the page.
    /// </summary>
    /// <remarks>
    /// The control for everything below: without it, a refusal test passes for an inserter that
    /// refuses everything.
    /// </remarks>
    [Fact]
    public void AnOrdinaryJpegIsEmbeddedWithTheSizeItsHeaderDeclares()
    {
        byte[] pdf = Insert(JpegDeclaring(640, 480), 640, 480);

        Assert.Equal((640, 480), EmbeddedImageSize(pdf));
    }

    /// <summary>The largest legal side is accepted — the bound is a ceiling, not a margin.</summary>
    /// <remarks>
    /// A limit test that only tries an over-sized value cannot tell a correct comparison from one
    /// that is off by one in the safe direction, which would reject legitimate images.
    /// </remarks>
    [Fact]
    public void TheLargestLegalSideIsStillAccepted()
    {
        int side = PdfLimits.MaxImageDimension;   // 16384; 16384 x 16 is well under the area cap

        byte[] pdf = Insert(JpegDeclaring(side, 16), side, 16);

        Assert.Equal((side, 16), EmbeddedImageSize(pdf));
    }

    // ================================================================ the bounds

    /// <summary>
    /// <b>A JPEG whose own frame header declares an over-sized image is refused.</b>
    /// </summary>
    /// <remarks>
    /// 65535 is the largest a SOF can express and the case the register named. The caller here
    /// passes matching dimensions, so the existing "the caller agrees with the header" check is
    /// satisfied and cannot be what refuses this.
    /// </remarks>
    [Fact]
    public void AnOversizedJpegFrameHeaderIsRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Insert(JpegDeclaring(65535, 65535), 65535, 65535));

        Assert.Contains("65535", ex.Message);
        Assert.Contains(PdfLimits.MaxImageDimension.ToString(), ex.Message);
    }

    /// <summary>
    /// <b>Two individually legal sides that multiply past the area limit are refused too.</b>
    /// </summary>
    /// <remarks>
    /// This is why the area check is separate from the per-side one: 16000×16000 passes both sides
    /// and is 256 million pixels, well past the 100 million ceiling. A single per-side check would
    /// wave it through.
    /// </remarks>
    [Fact]
    public void TwoLegalSidesThatMultiplyPastTheAreaLimitAreRefused()
    {
        const int side = 16000;

        Assert.True(side <= PdfLimits.MaxImageDimension, "the fixture must pass the per-side check");
        Assert.True((long)side * side > PdfLimits.MaxImagePixelArea, "and fail the area one");

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Insert(JpegDeclaring(side, side), side, side));

        Assert.Contains("area limit", ex.Message);
    }

    /// <summary>The raw-sample entry point is bounded by the same core, not separately.</summary>
    /// <remarks>
    /// Both public entry points funnel through one private method, so this asserts that the bound
    /// sits there rather than on the JPEG path alone — the arrangement that keeps a third entry
    /// point from arriving unguarded.
    /// </remarks>
    [Fact]
    public void TheRawEntryPointIsBoundedByTheSameCore()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfImageInserter.InsertRaw(
                input, output, 0, new byte[] { 1, 2, 3 },
                PdfLimits.MaxImageDimension + 1, 1, 10, 10, 100, 100));
    }

    /// <summary>
    /// The pre-existing disagreement check still fires, and is a different refusal from the bounds.
    /// </summary>
    [Fact]
    public void ACallerDisagreeingWithTheHeaderIsStillItsOwnRefusal()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Insert(JpegDeclaring(640, 480), 800, 600));

        Assert.Contains("disagrees with the", ex.Message);
    }
}
