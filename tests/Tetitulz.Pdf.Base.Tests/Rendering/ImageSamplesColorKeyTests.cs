// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The colour-key rule (§8.9.6.4) in <c>ImageSamples</c>, the leaf both renderers and the colour
/// converter share.
/// </summary>
/// <remarks>
/// <b>The ranges were read three times, and raw each time.</b> The raw-sample rule and both
/// renderers' decoded-pixel fallbacks cast each <c>/Mask</c> element straight to a number, so a range
/// written as a reference read as 0 and the key matched a different range from the one the file
/// wrote. One reader now, resolving.
/// </remarks>
public class ImageSamplesColorKeyTests
{
    /// <summary>
    /// <c>/Mask [0 6 0 R 0 6 0 R 0 6 0 R]</c> with object 6 = 10 keys out (5,5,5). Read raw, the
    /// upper bound was 0 and nothing matched.
    /// </summary>
    [Fact]
    public void AColourKeyBoundWrittenAsAReferenceIsRead()
    {
        byte[] samples = { 5, 5, 5, 64, 64, 64 };
        using var doc = Open(samples, "/Mask [0 6 0 R 0 6 0 R 0 6 0 R]", "10");
        var image = (PdfStream)doc.GetObject(5);
        var key = (PdfArray)doc.ResolveObject(image[PdfName.Mask]);

        bool[] masked = ImageSamples.TryComputeColorKeyMask(samples, 2, 1, key, image, doc);

        Assert.Equal(new[] { true, false }, masked);
    }

    /// <summary>The control: the same key written directly masks the same pixel.</summary>
    [Fact]
    public void TheSameKeyWrittenDirectlyMasksTheSamePixel()
    {
        byte[] samples = { 5, 5, 5, 64, 64, 64 };
        using var doc = Open(samples, "/Mask [0 10 0 10 0 10]");
        var image = (PdfStream)doc.GetObject(5);
        var key = (PdfArray)doc.ResolveObject(image[PdfName.Mask]);

        Assert.Equal(new[] { true, false },
            ImageSamples.TryComputeColorKeyMask(samples, 2, 1, key, image, doc));
    }

    /// <summary>Each pair is ordered low to high, and fewer pairs than asked for is a refusal.</summary>
    [Fact]
    public void RangesAreOrderedAndAShortArrayIsRefused()
    {
        var key = new PdfArray { new PdfNumber(10), new PdfNumber(0), new PdfNumber(3), new PdfNumber(7) };

        Assert.True(ImageSamples.TryReadColorKeyRanges(key, 2, null, out int[] lo, out int[] hi));
        Assert.Equal(new[] { 0, 3 }, lo);
        Assert.Equal(new[] { 10, 7 }, hi);

        Assert.False(ImageSamples.TryReadColorKeyRanges(key, 3, null, out _, out _));
    }

    private static PdfDocument Open(byte[] samples, string imageKeys, params string[] extraObjects)
    {
        byte[] pdf = ImageFixtures.OneImagePdf(samples, 2, 1, extraImageKeys: imageKeys + " ",
            extraObjects: b =>
            {
                for (int i = 0; i < extraObjects.Length; i++) b.Obj(6 + i, extraObjects[i]);
            });
        return PdfDocument.Open(new MemoryStream(pdf));
    }
}
