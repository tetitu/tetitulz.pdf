// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The encoder's ceilings (the review of 2026-09-26, row 32): its own work limit, named by the
/// message; the pixel area refused at the facade before a sample is widened; and Quality read
/// only where it matters. Joins <see cref="PdfLimitsCollection"/>: every limit is process-global.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class JpxImageEncodingLimitTests
{
    [Fact]
    public void MaxJpxEncodeWork_BoundsTheEncoder_AndItsMessageNamesIt()
    {
        byte[] samples = Samples(64, 64, 3);
        try
        {
            PdfLimits.MaxJpxEncodeWork = 10;
            var ex = Assert.Throws<WorkBudgetExceededException>(() => JpxImageEncoding.Encode(samples, 64, 64, 3, 8, new JpxEncodingOptions()));
            Assert.Contains("PdfLimits.MaxJpxEncodeWork", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("decode", ex.Message, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }

        // The decode's limit no longer binds the encoder.
        try
        {
            PdfLimits.MaxJpxDecodeWork = 10;
            Assert.True(JpxHeader.IsJp2Container(JpxImageEncoding.Encode(samples, 64, 64, 3, 8, new JpxEncodingOptions())));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void MaxImagePixelArea_RefusesTheEncode_BeforeASampleIsWidened()
    {
        try
        {
            PdfLimits.MaxImagePixelArea = 100;
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(Samples(20, 20, 1), 20, 20, 1, 8, new JpxEncodingOptions()));
            Assert.Contains("PdfLimits.MaxImagePixelArea is 100", ex.Message, StringComparison.Ordinal);
            var planes = new long[1][] { new long[20 * 20] };
            ex = Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.EncodePlanes(planes, 20, 20, 20, false, new JpxEncodingOptions()));
            Assert.Contains("PdfLimits.MaxImagePixelArea is 100", ex.Message, StringComparison.Ordinal);
            // The control, just under.
            Assert.True(JpxHeader.IsJp2Container(JpxImageEncoding.Encode(Samples(10, 10, 1), 10, 10, 1, 8, new JpxEncodingOptions())));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void Quality_IsCheckedUnderLossyAlone()
    {
        byte[] samples = Samples(8, 8, 1);
        byte[] lossless = JpxImageEncoding.Encode(samples, 8, 8, 1, 8, new JpxEncodingOptions { Lossless = true, Quality = 0 });
        Assert.True(JpxImageDecoding.TryDecode(lossless, JpxImageDictionaryFacts.None, out JpxImagePixels pixels, out string reason), reason);
        Assert.Equal(samples, pixels.Colour);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(samples, 8, 8, 1, 8, new JpxEncodingOptions { Lossless = false, Quality = 0 }));
        Assert.Contains("quality is 1 to 100", ex.Message, StringComparison.Ordinal);
    }

    private static byte[] Samples(int w, int h, int components)
    {
        var s = new byte[w * h * components];
        for (int i = 0; i < s.Length; i++) s[i] = (byte)(i * 37 + (i / components) % 13);
        return s;
    }
}
