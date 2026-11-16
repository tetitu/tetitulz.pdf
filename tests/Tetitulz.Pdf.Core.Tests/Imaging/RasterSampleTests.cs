// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// <i>n</i>-bit raster sample → 8-bit channel, the rule four decoders had each written for
/// themselves.
///
/// <para><b>The copies had drifted into a visible defect.</b> <c>BuilderImageDecoder</c> read a
/// 16-bit sample as its <b>high byte</b> (0–255) and then divided by 65535, so
/// <c>s * 255 / 65535</c> was <b>0 for every sample up to 257</b>: every 16-bit greyscale PNG
/// exported from <c>DocumentBuilder</c> came out pure black. <c>DocxToPdf.PngImage</c> read the
/// whole sample and rounded; <c>PdfColor.Scale16To8</c> had the same rounded expression. The two
/// feature copies were never swept when that one was fixed.</para>
///
/// <para>The read and the scale are asserted <b>together</b>, because that is where the defect
/// lived: each half was defensible alone and they disagreed about what "sample" meant.</para>
/// </summary>
public sealed class RasterSampleTests
{
    /// <summary>
    /// The defect, stated as a round trip: a mid-grey 16-bit sample must come back mid-grey.
    /// Reading the high byte and dividing by 65535 gives 0.
    /// </summary>
    [Fact]
    public void SixteenBitSample_RoundTripsToTheSameGrey_RatherThanCollapsingToBlack()
    {
        // 0x8080 = 32896, which is 128.5/255 of full scale.
        byte[] row = { 0x80, 0x80 };

        int sample = RasterSample.ReadChannelMsbFirst(row, 0, 16);
        Assert.Equal(0x8080, sample);
        Assert.Equal(128, RasterSample.ScaleToByte(sample, 16));

        // The old Builder pairing, for the record: high byte only, then /65535.
        int highByteOnly = row[0];                       // 128
        Assert.Equal(0, highByteOnly * 255 / 65535);     // …which is black
    }

    /// <summary>
    /// Full scale and zero must survive at every depth PNG and PDF allow — the two values a scaling
    /// bug most often still gets right, and the ones a caller notices last.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void EndpointsAreExactAtEveryDepth(int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        Assert.Equal(0, RasterSample.ScaleToByte(0, bitDepth));
        Assert.Equal(255, RasterSample.ScaleToByte(max, bitDepth));
    }

    /// <summary>
    /// Rounding to nearest, not truncation. Truncation biases every channel down by half a level,
    /// and CLAUDE.md records the same split being closed between the two renderers.
    /// </summary>
    /// <remarks>
    /// <para><b>Only the 16-bit rows can state this, which a mutation run had to show.</b> 255 is
    /// divisible by 3, 15 and 255, so at 1, 2, 4 and 8 bits <c>sample * 255 / max</c> is exact and
    /// rounding and truncation agree on <i>every</i> value — a sub-byte fixture cannot fail. The
    /// first version of this theory was all sub-byte rows plus <c>0x8080</c>, which is
    /// <c>65535 × 128</c> exactly, so it sat on a boundary too and the truncating mutation survived
    /// the whole set.</para>
    /// <para><c>129</c> is the smallest 16-bit sample that separates them: <c>129 × 255 = 32895</c>,
    /// just over half of 65535, so rounding gives 1 and truncation gives 0.</para>
    /// </remarks>
    [Theory]
    [InlineData(129, 16, 1)]        // the row that kills the truncating mutation
    [InlineData(32895, 16, 128)]    // 0.50195 of full scale — rounds up, truncates down to 127
    [InlineData(32896, 16, 128)]    // 0x8080, the mid-grey the Builder copy turned black
    [InlineData(1, 16, 0)]          // one step above black is still black, correctly
    [InlineData(1, 2, 85)]          // exact at every sub-byte depth: 255 = 85 x 3
    [InlineData(2, 2, 170)]
    [InlineData(1, 4, 17)]          // 255 = 17 x 15
    [InlineData(7, 4, 119)]
    public void ScalingRoundsToNearest(int sample, int bitDepth, int expected)
        => Assert.Equal(expected, RasterSample.ScaleToByte(sample, bitDepth));

    /// <summary>An 8-bit sample is already a channel and must pass through untouched.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(255)]
    public void EightBitSamplesArePassedThrough(int sample)
        => Assert.Equal(sample, RasterSample.ScaleToByte(sample, 8));

    /// <summary>Sub-byte samples are packed MSB-first, as both PNG and PDF specify.</summary>
    [Fact]
    public void SubByteSamplesAreReadMostSignificantFirst()
    {
        byte[] row = { 0b1101_0010 };

        Assert.Equal(1, RasterSample.ReadChannelMsbFirst(row, 0, 1));
        Assert.Equal(1, RasterSample.ReadChannelMsbFirst(row, 1, 1));
        Assert.Equal(0, RasterSample.ReadChannelMsbFirst(row, 2, 1));

        Assert.Equal(0b11, RasterSample.ReadChannelMsbFirst(row, 0, 2));
        Assert.Equal(0b01, RasterSample.ReadChannelMsbFirst(row, 1, 2));

        Assert.Equal(0b1101, RasterSample.ReadChannelMsbFirst(row, 0, 4));
        Assert.Equal(0b0010, RasterSample.ReadChannelMsbFirst(row, 1, 4));
    }

    /// <summary>
    /// A short row returns 0 rather than throwing: these decoders run on files from outside, and a
    /// truncated IDAT is an ordinary corruption rather than a programming error.
    /// </summary>
    [Fact]
    public void ReadingPastTheEndOfARow_IsZeroNotAnException()
    {
        byte[] row = { 0x12 };

        Assert.Equal(0, RasterSample.ReadChannelMsbFirst(row, 5, 8));
        Assert.Equal(0, RasterSample.ReadChannelMsbFirst(row, 0, 16));   // needs two bytes
        Assert.Equal(0, RasterSample.ReadChannelMsbFirst(null, 0, 8));
    }

    /// <summary>
    /// The max-based overload is the same rule — a PNG decoder already has
    /// <c>(1 &lt;&lt; bitDepth) - 1</c> to hand and should not have to convert back.
    /// </summary>
    [Theory]
    [InlineData(3, 3, 255)]
    [InlineData(1, 3, 85)]
    [InlineData(32896, 65535, 128)]
    [InlineData(200, 255, 200)]
    [InlineData(5, 0, 0)]
    public void TheMaxBasedOverloadAgreesWithTheDepthBasedOne(int sample, int max, int expected)
        => Assert.Equal(expected, RasterSample.ScaleToByteFromMax(sample, max));
}
