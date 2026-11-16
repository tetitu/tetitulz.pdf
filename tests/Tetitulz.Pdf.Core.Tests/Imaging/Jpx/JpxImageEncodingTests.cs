// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The public encoder facade, <see cref="JpxImageEncoding"/>, and what it hands back: a JP2 file
/// (or codestream) that the public decoder facade reads back to the same samples, that the reference decoder
/// reads back the same, whose colour box names the space the component count implies, and that
/// <see cref="RasterImageInspector"/> and <see cref="JpxImageDictionary"/> describe as a PDF embeds it.
/// </summary>
public sealed partial class JpxImageEncodingTests
{
    private static byte[] Interleave(int[][] planes, int bitsPerComponent)
    {
        int components = planes.Length, count = planes[0].Length;
        var samples = new byte[count * components * (bitsPerComponent > 8 ? 2 : 1)];
        int at = 0;
        for (int i = 0; i < count; i++)
            for (int c = 0; c < components; c++)
            {
                if (bitsPerComponent > 8) samples[at++] = (byte)(planes[c][i] >> 8);
                samples[at++] = (byte)planes[c][i];
            }
        return samples;
    }

    /// <summary>The colour samples the public decoder returns, interleaved and narrowed to 8 bits — the shape a PDF consumer takes.</summary>
    private static byte[] DecodeColour(byte[] file, out JpxImagePixels pixels)
    {
        Assert.True(JpxImageDecoding.TryDecode(file, JpxImageDictionaryFacts.None, out pixels, out string reason), reason);
        return pixels.Colour;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Lossless_ByDefault_RoundTripsThroughThePublicDecoder_AsAJp2NamingItsSpace(int components)
    {
        int[][] planes = JpxEncoderTestImages.Pattern(37, 29, components, 100 + components);
        byte[] samples = Interleave(planes, 8);
        byte[] jp2 = JpxImageEncoding.Encode(samples, 37, 29, components, 8, null);

        Assert.True(JpxHeader.IsJp2Container(jp2));
        Assert.True(JpxHeader.TryReadColourComponents(jp2, out int colour));
        Assert.Equal(components, colour);
        Assert.Equal(samples, DecodeColour(jp2, out JpxImagePixels pixels));
        Assert.Equal((37, 29, components), (pixels.Width, pixels.Height, pixels.ColourComponents));

        // The raw colr box: METH 1, then EnumCS 17 (greyscale), 16 (sRGB) or 12 (CMYK), big-endian.
        int colr = IndexOf(jp2, new[] { (byte)'c', (byte)'o', (byte)'l', (byte)'r' });
        Assert.True(colr > 0, "no colr box");
        int enumCs = (jp2[colr + 7] << 24) | (jp2[colr + 8] << 16) | (jp2[colr + 9] << 8) | jp2[colr + 10];
        Assert.Equal((1, components == 1 ? 17 : components == 3 ? 16 : 12), (jp2[colr + 4], enumCs));

        // The component transformation is for RGB (G.2): three components and no other count.
        Assert.True(JpxHeader.TryLocateCodestream(jp2, out long start, out long end));
        byte[] codestream = new byte[end - start];
        Array.Copy(jp2, start, codestream, 0, codestream.Length);
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream parsed, out string parseReason), parseReason);
        Assert.Equal(components == 3, parsed.CodingFor(0).MultipleComponentTransform);

        Assert.True(RasterImageInspector.TryInspect(jp2, out RasterImageInfo info));
        Assert.Equal((RasterImageFormat.Jpeg2000, 37, 29, components, false), (info.Format, info.Width, info.Height, info.Components, info.HasAlpha));
        Assert.True(JpxImageDictionary.TryDescribe(jp2, out int w, out int h, out int bpc, out string cs, out int smaskInData, out string why), why);
        Assert.Equal((37, 29, 8, components == 1 ? "DeviceGray" : components == 3 ? "DeviceRGB" : "DeviceCMYK", 0), (w, h, bpc, cs, smaskInData));
    }

    /// <summary>Signed planes take no JP2 (T.800 Tables I.10 and I.16 are unsigned-only); the raw codestream carries them.</summary>
    [Fact]
    public void SignedPlanes_AreRefusedTheContainer_AndEncodeAsACodestream()
    {
        long[][] planes = { new long[] { -5, 0, 7, -128, 127, 1 } };
        Assert.Contains("signed components", Assert.Throws<ArgumentException>(() => JpxImageEncoding.EncodePlanes(planes, 3, 2, 8, true, null)).Message, StringComparison.Ordinal);
        byte[] codestream = JpxImageEncoding.EncodePlanes(planes, 3, 2, 8, true, new JpxEncodingOptions { Jp2Container = false });
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult decoded, out string reason), reason);
        Assert.Equal(new[] { -5, 0, 7, -128, 127, 1 }, decoded.Image!.Samples[0]);
    }

    [Fact]
    public void ACodestream_WithoutTheContainer_HasNoBoxes_AndIsDescribedByItsComponentCount()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(20, 12, 3, 5);
        byte[] samples = Interleave(planes, 8);
        byte[] j2k = JpxImageEncoding.Encode(samples, 20, 12, 3, 8, new JpxEncodingOptions { Jp2Container = false });
        Assert.True(JpxHeader.IsRawCodestream(j2k));
        Assert.False(JpxHeader.TryReadColourComponents(j2k, out _));
        Assert.Equal(samples, DecodeColour(j2k, out _));
        Assert.True(JpxImageDictionary.TryDescribe(j2k, out _, out _, out int bpc, out string cs, out _, out string why), why);
        Assert.Equal((8, "DeviceRGB"), (bpc, cs));
        Assert.True(RasterImageInspector.TryInspect(j2k, out RasterImageInfo info));
        Assert.Equal((RasterImageFormat.Jpeg2000, 20, 12, 3), (info.Format, info.Width, info.Height, info.Components));
    }

    /// <summary>
    /// The quality's step is in 8-bit units and scales with the depth: at 16 bits, quality 50 is
    /// as lossy as at 8 — a step of 8 · 256 — not near-lossless on samples 256 times as large.
    /// </summary>
    [Fact]
    public void Lossy_SixteenBit_StepScalesWithTheDepth()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(48, 32, 1, 1616, precision: 16);
        byte[] samples = Interleave(planes, 16);
        byte[] lossless = JpxImageEncoding.Encode(samples, 48, 32, 1, 16, null);
        byte[] lossy = JpxImageEncoding.Encode(samples, 48, 32, 1, 16, new JpxEncodingOptions { Lossless = false, Quality = 50 });
        Assert.True(lossy.Length * 2 < lossless.Length, $"quality 50 took {lossy.Length} bytes against {lossless.Length} lossless");

        Assert.True(JpxDecoder.TryDecode(lossy, 0, out JpxDecodeResult result, out string reason), reason);
        double psnr = JpxEncoderTestImages.Psnr(planes, new[] { result.Image!.Samples[0] }, 16);
        Assert.InRange(psnr, 30, 60);   // the 8-bit case at quality 50 sits in the same band; unscaled, the step would give over 80 dB
    }

    [Fact]
    public void ASizeTarget_IsMet_LossyOrLossless()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 48, 1, 3);
        byte[] samples = Interleave(planes, 8);
        byte[] whole = JpxImageEncoding.Encode(samples, 64, 48, 1, 8, null);
        int target = whole.Length / 3;
        byte[] fitted = JpxImageEncoding.Encode(samples, 64, 48, 1, 8, new JpxEncodingOptions { TargetBytes = target });
        byte[] fittedLossy = JpxImageEncoding.Encode(samples, 64, 48, 1, 8, new JpxEncodingOptions { Lossless = false, Quality = 100, TargetBytes = target });
        Assert.True(fitted.Length <= target && fitted.Length > target / 2, $"{fitted.Length} bytes for {target}");
        Assert.True(fittedLossy.Length <= target && fittedLossy.Length > target / 2, $"{fittedLossy.Length} bytes for {target}");
        Assert.True(JpxImageDecoding.TryDecode(fitted, JpxImageDictionaryFacts.None, out JpxImagePixels pixels, out string reason), reason);
        Assert.NotEqual(samples, pixels.Colour);   // a third of the bytes is not lossless
        double psnr = JpxEncoderTestImages.Psnr(planes, new[] { Array.ConvertAll(pixels.Colour, b => (int)b) }, 8);
        Assert.True(psnr > 25, $"{psnr:F1} dB at a third of the bytes");
    }

    [Fact]
    public void StepForQuality_IsOneAtAHundred_EightAtFifty_AndFallsWithQuality()
    {
        Assert.Equal(1.0, JpxImageEncoding.StepForQuality(100));
        Assert.Equal(8.0, JpxImageEncoding.StepForQuality(50), 9);
        Assert.InRange(JpxImageEncoding.StepForQuality(75), 2.8, 2.9);
        Assert.InRange(JpxImageEncoding.StepForQuality(1), 60, 62);
        for (int q = 2; q <= 100; q++) Assert.True(JpxImageEncoding.StepForQuality(q) < JpxImageEncoding.StepForQuality(q - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.StepForQuality(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.StepForQuality(101));
    }

    /// <summary>Levels past the image's size are dropped: a 5-pixel-high image gets two, not the five asked for, and still decodes.</summary>
    [Fact]
    public void DecompositionLevels_AreCappedByTheImageSize()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 5, 1, 8);
        byte[] samples = Interleave(planes, 8);
        byte[] j2k = JpxImageEncoding.Encode(samples, 40, 5, 1, 8, new JpxEncodingOptions { Jp2Container = false, DecompositionLevels = 5 });
        Assert.True(JpxCodestreamParser.TryParse(j2k, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(2, cs.CodingFor(0).Component.DecompositionLevels);
        Assert.Equal(samples, DecodeColour(j2k, out _));
    }

    [Fact]
    public void Refuses_WhatItCannotEncode_AndAcceptsTheEdges()
    {
        byte[] ok = new byte[4 * 4 * 3];
        Assert.Throws<ArgumentNullException>(() => JpxImageEncoding.Encode(null, 4, 4, 3, 8, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(ok, 0, 4, 3, 8, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(new byte[4 * 4 * 2], 4, 4, 2, 8, null));   // grey + alpha has no PDF colour space
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(ok, 4, 4, 3, 17, null));
        Assert.Throws<ArgumentException>(() => JpxImageEncoding.Encode(new byte[4 * 4 * 3 - 1], 4, 4, 3, 8, null));
        Assert.Throws<ArgumentException>(() => JpxImageEncoding.Encode(new byte[] { 0, 0, 0, 3 }, 2, 2, 1, 1, null));   // 3 is above the 1-bit maximum
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(ok, 4, 4, 3, 8, new JpxEncodingOptions { Lossless = false, Quality = 0 }));   // read by the lossy path alone
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(ok, 4, 4, 3, 8, new JpxEncodingOptions { Layers = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(ok, 4, 4, 3, 8, new JpxEncodingOptions { TargetBytes = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(ok, 4, 4, 3, 8, new JpxEncodingOptions { DecompositionLevels = 33 }));

        // The edges: one 1-bit pixel, and a 16-bit CMYK image, both decode to their samples.
        byte[] one = JpxImageEncoding.Encode(new byte[] { 1 }, 1, 1, 1, 1, null);
        Assert.Equal(new byte[] { 255 }, DecodeColour(one, out _));   // 1-bit 1 narrows to 255
        int[][] cmyk = JpxEncoderTestImages.Pattern(9, 7, 4, 44, precision: 16);
        byte[] wide = Interleave(cmyk, 16);
        byte[] cmykFile = JpxImageEncoding.Encode(wide, 9, 7, 4, 16, null);
        Assert.True(JpxHeader.TryReadColourComponents(cmykFile, out int colour));
        Assert.Equal(4, colour);
        byte[] narrowed = DecodeColour(cmykFile, out _);
        for (int i = 0; i < narrowed.Length; i++) Assert.Equal(wide[2 * i], narrowed[i]);
    }

    /// <summary>
    /// RGB with an opacity channel through the public door: four interleaved components with the
    /// last declared opacity come back, under <c>/SMaskInData 1</c>, as the three colour samples
    /// and the alpha plane; the dictionary rule asks for that key. A raw codestream cannot carry
    /// the declaration, and three components with the flag name no colour space.
    /// </summary>
    [Fact]
    public void OpacityChannel_IsDeclaredInTheJp2_AndDecodesAsColourAndAlpha()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(20, 14, 4, 4040);
        byte[] samples = Interleave(planes, 8);
        byte[] jp2 = JpxImageEncoding.Encode(samples, 20, 14, 4, 8, new JpxEncodingOptions { OpacityChannel = true });

        Assert.True(JpxImageDecoding.TryDecode(jp2, new JpxImageDictionaryFacts(0, false, 1, false), out JpxImagePixels pixels, out string reason), reason);
        Assert.Equal((3, 20, 14), (pixels.ColourComponents, pixels.Width, pixels.Height));
        Assert.Equal(Interleave(new[] { planes[0], planes[1], planes[2] }, 8), pixels.Colour);
        Assert.Equal(Array.ConvertAll(planes[3], v => (byte)v), pixels.Opacity);
        Assert.True(JpxImageDictionary.TryDescribe(jp2, out _, out _, out _, out string cs, out int smaskInData, out string why), why);
        Assert.Equal(("DeviceRGB", 1), (cs, smaskInData));
        Assert.True(RasterImageInspector.TryInspect(jp2, out RasterImageInfo info));
        Assert.Equal((3, true), (info.Components, info.HasAlpha));
        // The RCT is for the three colour channels (G.2): the opacity does not change whether it applies.
        Assert.True(JpxHeader.TryLocateCodestream(jp2, out long start, out long end));
        Assert.True(JpxCodestreamParser.TryParse(jp2.Skip((int)start).Take((int)(end - start)).ToArray(), out JpxCodestream cs2, out string parseReason), parseReason);
        Assert.True(cs2.CodingFor(0).MultipleComponentTransform);

        // Without the key the opacity is ignored (ISO 32000-2 Table 89) and the colour is the same three.
        Assert.True(JpxImageDecoding.TryDecode(jp2, JpxImageDictionaryFacts.None, out JpxImagePixels opaque, out reason), reason);
        Assert.Null(opaque.Opacity);
        Assert.Equal(pixels.Colour, opaque.Colour);

        Assert.Throws<ArgumentException>(() => JpxImageEncoding.Encode(samples, 20, 14, 4, 8, new JpxEncodingOptions { OpacityChannel = true, Jp2Container = false }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(Interleave(new[] { planes[0], planes[1], planes[2] }, 8), 20, 14, 3, 8, new JpxEncodingOptions { OpacityChannel = true }));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageEncoding.Encode(new byte[20 * 14 * 2], 20, 14, 2, 8, null));   // two components need the declaration
    }

    /// <summary>
    /// What the dictionary rule says of the files the encoder cannot yet write: a declared opacity
    /// channel is reported (for <c>/SMaskInData</c>) and the colour channels are one, not two; an
    /// sYCC file names no PDF colour space. The inspector reports the same channels and alpha.
    /// </summary>
    [Fact]
    public void Describe_AnOpacityChannelAndAnSYccFile_AsAPdfEmbedsThem()
    {
        byte[] withOpacity = Tetitulz.Pdf.TestHelpers.JpxEmbedFixtures.GreyWithOpacityJp2();
        Assert.True(JpxImageDictionary.TryDescribe(withOpacity, out int w, out int h, out int bpc, out string cs, out int smaskInData, out string why), why);
        Assert.Equal((40, 24, 8, "DeviceGray", 1), (w, h, bpc, cs, smaskInData));
        Assert.True(RasterImageInspector.TryInspect(withOpacity, out RasterImageInfo info));
        Assert.Equal((RasterImageFormat.Jpeg2000, 1, true), (info.Format, info.Components, info.HasAlpha));

        byte[] sYcc = Tetitulz.Pdf.TestHelpers.JpxEmbedFixtures.SYccJp2();
        Assert.True(JpxImageDictionary.TryDescribe(sYcc, out w, out h, out bpc, out cs, out smaskInData, out why), why);
        Assert.Equal((40, 24, 8, 0), (w, h, bpc, smaskInData));
        Assert.Null(cs);
        Assert.True(RasterImageInspector.TryInspect(sYcc, out info));
        Assert.Equal((3, false), (info.Components, info.HasAlpha));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
