// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Filters;

/// <summary>
/// The JPEG 2000 encoder's output read by an independent renderer (<see cref="IndependentImageRender"/>):
/// lossless grey as a JP2 file and as a bare codestream, lossless HTJ2K, lossless RGB through the
/// reversible colour transform, and a lossy encoding against the original within a bound. The
/// encoder's own tests decode with this repository's decoder; the renderer shares nothing with either half.
/// </summary>
public class JpxEncoderIndependentRenderTests
{
    private const int Width = 64, Height = 48;

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void AnIndependentRendererReadsLosslessGrey(bool jp2Container, bool highThroughput)
    {
        byte[] samples = Samples(1);
        byte[] jpx = JpxImageEncoding.Encode(samples, Width, Height, 1, 8,
            new JpxEncodingOptions { Lossless = true, Jp2Container = jp2Container, HighThroughput = highThroughput });
        byte[,,] rgb = IndependentImageRender.RenderRgb(jpx, "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", Width, Height);

        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Assert.True(rgb[y, x, 0] == samples[y * Width + x], $"({x},{y}): the independent renderer {rgb[y, x, 0]}, sample {samples[y * Width + x]}");
    }

    [Fact]
    public void AnIndependentRendererReadsLosslessRgb()
    {
        byte[] samples = Samples(3);
        byte[] jpx = JpxImageEncoding.Encode(samples, Width, Height, 3, 8, new JpxEncodingOptions { Lossless = true });
        byte[,,] rgb = IndependentImageRender.RenderRgb(jpx, "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /JPXDecode", Width, Height);

        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                for (int c = 0; c < 3; c++)
                    Assert.True(rgb[y, x, c] == samples[(y * Width + x) * 3 + c],
                        $"({x},{y}) channel {c}: the independent renderer {rgb[y, x, c]}, sample {samples[(y * Width + x) * 3 + c]}");
    }

    /// <summary>
    /// A lossy encoding (quality 50, the irreversible 9-7 wavelet and quantisation) decodes the same in
    /// the independent renderer and in this library, within one level of rounding — the two decoders agreeing on what the
    /// encoder's coefficients and step sizes mean — and is not the original, so the case is lossy.
    /// </summary>
    [Fact]
    public void AnIndependentRendererAndThisLibrary_DecodeALossyEncodingAlike()
    {
        byte[] samples = Samples(1);
        byte[] jpx = JpxImageEncoding.Encode(samples, Width, Height, 1, 8, new JpxEncodingOptions { Lossless = false, Quality = 50 });
        byte[,,] rgb = IndependentImageRender.RenderRgb(jpx, "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /JPXDecode", Width, Height);
        Assert.True(Tetitulz.Pdf.Base.Images.JpxImageDecoder.TryDecodeSamples(jpx, null, null, out JpxPdfImage ours, out string reason), reason);

        int worst = 0, changed = 0;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                worst = Math.Max(worst, Math.Abs(rgb[y, x, 0] - ours.Colour[y * Width + x]));
                if (ours.Colour[y * Width + x] != samples[y * Width + x]) changed++;
            }
        Assert.True(worst <= 1, $"the independent renderer and this library differ by {worst} levels");
        Assert.True(changed > 100, "the encoding was not lossy enough to tell the decoders apart");
    }

    /// <summary>
    /// Smooth gradients, a sharp edge and a band of noise, per component. The components' offsets
    /// differ by odd amounts (0, 85, 171): with even ones, red and blue always shared a parity, so
    /// (r + 2g + b) was never 3 mod 4 and a rounding error in the colour transform's luma never showed.
    /// </summary>
    private static byte[] Samples(int components)
    {
        int[] offset = { 0, 85, 171 };
        var samples = new byte[Width * Height * components];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                for (int c = 0; c < components; c++)
                    samples[(y * Width + x) * components + c] = (byte)(
                        y < 16 ? x * 4 + offset[c] / 3 :
                        y < 32 ? (x < 32 ? 30 + offset[c] / 5 : 220 - offset[c] / 5) :
                        ((x * 97 + y * 57 + offset[c]) ^ (x * y)) & 0xFF);
        return samples;
    }
}