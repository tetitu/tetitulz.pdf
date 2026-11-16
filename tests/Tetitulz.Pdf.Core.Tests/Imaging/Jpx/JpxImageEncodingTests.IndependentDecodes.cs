// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxImageEncodingTests
{
    /// <summary>
    /// The container shapes the writer added in 15j, decoded by an independent decoder: grey with an opacity
    /// channel, RGB with one, and CMYK — the independent decoder reports the modes it derives from the JP2's colr
    /// and cdef, and the planes are the samples. (A signed input takes no container, below.)
    /// </summary>
    [Theory]
    [InlineData(2, true, "LA")]
    [InlineData(4, true, "RGBA")]
    [InlineData(4, false, "CMYK")]
    public void ContainerShapes_DecodeThroughAnIndependentDecoder(int components, bool opacity, string mode)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(24, 18, components, 9090 + components);
        byte[] jp2 = JpxImageEncoding.Encode(Interleave(planes, 8), 24, 18, components, 8, new JpxEncodingOptions { OpacityChannel = opacity });
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(jp2, ".jp2");
        Assert.Equal(mode, decoded.Mode);
        Assert.Equal((24, 18), (decoded.Width, decoded.Height));
        for (int c = 0; c < components; c++) Assert.Equal(planes[c], decoded.Planes[c]);
    }

    /// <summary>Layers with a size target through an independent decoder: the file decodes at its size and its COD says the layers (the review's row 1 shape, on the public door).</summary>
    [Fact]
    public void LayersWithATarget_DecodeThroughAnIndependentDecoder()
    {
        var samples = new byte[300 * 200];
        int seed = 1;
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 300; x++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
                samples[y * 300 + x] = x < 150 ? (byte)(x + y) : (byte)(seed >> 16);
            }
        byte[] whole = JpxImageEncoding.Encode(samples, 300, 200, 1, 8, new JpxEncodingOptions { Lossless = true, Jp2Container = false });
        byte[] layered = JpxImageEncoding.Encode(samples, 300, 200, 1, 8, new JpxEncodingOptions { Lossless = true, Layers = 3, TargetBytes = whole.Length / 2, Jp2Container = false });
        Assert.True(layered.Length <= whole.Length / 2);
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(layered, ".j2k");
        Assert.Equal((300, 200, "L"), (decoded.Width, decoded.Height, decoded.Mode));
        Assert.True(JpxCodestreamParser.TryParse(layered, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(3, cs.Main.Coding.Layers);
    }

    [Fact]
    public void Lossless_Rgb_DecodesExactlyThroughAnIndependentDecoder_AsAFile()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(48, 32, 3, 77);
        byte[] jp2 = JpxImageEncoding.Encode(Interleave(planes, 8), 48, 32, 3, 8, null);
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(jp2, ".jp2");
        Assert.Equal("RGB", decoded.Mode);
        for (int c = 0; c < 3; c++) Assert.Equal(planes[c], decoded.Planes[c]);
    }

    [Fact]
    public void SixteenBitGrey_RoundTrips_BigEndian()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(24, 16, 1, 9, precision: 16);
        Assert.Contains(planes[0], v => v > 255);
        byte[] samples = Interleave(planes, 16);
        byte[] jp2 = JpxImageEncoding.Encode(samples, 24, 16, 1, 16, null);
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(jp2, ".jp2");
        Assert.Equal(planes[0], decoded.Planes[0]);
        Assert.True(JpxImageDictionary.TryDescribe(jp2, out _, out _, out int bpc, out _, out _, out _));
        Assert.Equal(16, bpc);
        // The public decoder narrows to 8 bits: the high byte of each sample.
        byte[] narrowed = DecodeColour(jp2, out _);
        for (int i = 0; i < narrowed.Length; i++) Assert.Equal(samples[2 * i], narrowed[i]);
    }

    [Theory]
    [InlineData(90, 50, 7.0)]
    [InlineData(75, 30, 5.0)]
    public void Lossy_HigherQualityGivesHigherPsnr_AndMoreBytes(int high, int low, double atLeastDb)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 3, 2020);
        byte[] samples = Interleave(planes, 8);
        byte[] fine = JpxImageEncoding.Encode(samples, 64, 48, 3, 8, new JpxEncodingOptions { Lossless = false, Quality = high });
        byte[] coarse = JpxImageEncoding.Encode(samples, 64, 48, 3, 8, new JpxEncodingOptions { Lossless = false, Quality = low });
        byte[] lossless = JpxImageEncoding.Encode(samples, 64, 48, 3, 8, null);
        double finePsnr = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(fine, ".jp2").Planes, 8);
        double coarsePsnr = JpxEncoderTestImages.Psnr(planes, IndependentJpxDecode.Decode(coarse, ".jp2").Planes, 8);
        Assert.True(finePsnr >= coarsePsnr + atLeastDb, $"quality {high}: {finePsnr:F1} dB, {low}: {coarsePsnr:F1} dB");
        Assert.True(coarsePsnr > 17, $"quality {low}: {coarsePsnr:F1} dB");
        Assert.True(coarse.Length < fine.Length && fine.Length < lossless.Length, $"{coarse.Length} < {fine.Length} < {lossless.Length}");
        // Quality 100 is the integers' own step: near lossless, and never far above the lossless size.
        byte[] top = JpxImageEncoding.Encode(samples, 64, 48, 3, 8, new JpxEncodingOptions { Lossless = false, Quality = 100 });
        Assert.InRange(top.Length, lossless.Length / 2, lossless.Length * 11 / 10);
        Assert.True(JpxImageDecoding.TryDecode(coarse, JpxImageDictionaryFacts.None, out _, out string reason), reason);
    }
}
