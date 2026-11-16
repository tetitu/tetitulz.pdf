// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.TestHelpers;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxPdfImage"/>: the pixel half of ISO 32000-2 §7.4.9 over the lossless fixtures —
/// each rule asserted on the bytes it produces, against the fixture's own source planes.
/// </summary>
public sealed class JpxPdfImageTests
{
    private static readonly Jp2PdfImageFacts NoDictionary = new(0, false, 0, false);
    private static Jp2PdfImageFacts WithSMask(int v) => new(0, false, v, false);
    private static Jp2PdfImageFacts WithColourSpace(int n, bool indexed = false, int smask = 0) => new(n, indexed, smask, false);

    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
    private static JpxLosslessFixtures.Fixture Gray => JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers;
    private static JpxLosslessFixtures.Fixture Rgba => JpxLosslessFixtures.Rgba40x24_Mct_OneLevel;
    private static JpxLosslessFixtures.Fixture GrayAlpha => JpxLosslessFixtures.GrayAlpha40x24_OneLevel;

    private static JpxPdfImage Decode(byte[] data, Jp2PdfImageFacts facts, Func<byte[], bool>? icc = null)
    {
        Assert.True(JpxPdfImage.TryDecode(data, facts, icc, 0, out JpxPdfImage image, out string reason), reason);
        return image;
    }

    private static string Refusal(byte[] data, Jp2PdfImageFacts facts)
    {
        Assert.False(JpxPdfImage.TryDecode(data, facts, null, 0, out _, out string reason));
        return reason;
    }

    /// <summary>The fixture's planes interleaved in the given order — what the colour buffer must be.</summary>
    private static byte[] Interleaved(JpxLosslessFixtures.Fixture f, params int[] planes)
    {
        var dst = new byte[f.Width * f.Height * planes.Length];
        for (int i = 0; i < f.Width * f.Height; i++)
            for (int k = 0; k < planes.Length; k++)
                dst[i * planes.Length + k] = f.Planes[planes[k]][i];
        return dst;
    }

    private static byte[] Jp2(JpxLosslessFixtures.Fixture f, params byte[][] headerBoxes)
    {
        var boxes = new List<byte[]> { H.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7) };
        boxes.AddRange(headerBoxes);
        return H.Jp2File(f.Codestream, boxes.ToArray());
    }

    // ── a bare codestream ───────────────────────────────────────────────────

    [Fact]
    public void RawCodestream_ThreeComponents_IsDeviceRgbInComponentOrder()
    {
        JpxPdfImage image = Decode(Rgb.Codestream, NoDictionary);

        Assert.Equal((Rgb.Width, Rgb.Height, 3, JpxColourModel.Rgb), (image.Width, image.Height, image.ColourComponents, image.ColourModel));
        Assert.Equal(Jp2ColourSpaceKind.DeviceRgb, image.Plan.ColourSpace);
        Assert.Equal(Interleaved(Rgb, 0, 1, 2), image.Colour);
        Assert.Null(image.Opacity);
        Assert.Null(image.IccProfile);
        Assert.False(image.DecodeArrayApplies);
        Assert.Empty(image.Deviations);
        Assert.True(image.Budget.Spent > 0);
    }

    [Fact]
    public void RawCodestream_OneComponent_IsDeviceGray()
    {
        JpxPdfImage image = Decode(Gray.Codestream, NoDictionary);
        Assert.Equal((1, JpxColourModel.Gray), (image.ColourComponents, image.ColourModel));
        Assert.Equal(Gray.Planes[0], image.Colour);
    }

    [Fact]
    public void RawCodestream_FourComponents_IsDeviceCmyk_UnlessSMaskInDataClaimsOne()
    {
        JpxPdfImage cmyk = Decode(Rgba.Codestream, NoDictionary);
        Assert.Equal((4, JpxColourModel.Cmyk), (cmyk.ColourComponents, cmyk.ColourModel));
        Assert.Equal(Interleaved(Rgba, 0, 1, 2, 3), cmyk.Colour);

        JpxPdfImage rgba = Decode(Rgba.Codestream, WithSMask(1));
        Assert.Equal((3, JpxColourModel.Rgb), (rgba.ColourComponents, rgba.ColourModel));
        Assert.Equal(Interleaved(Rgba, 0, 1, 2), rgba.Colour);
        Assert.Equal(Rgba.Planes[3], rgba.Opacity);
        Assert.False(rgba.OpacityIsPremultiplied);
    }

    // ── the image dictionary's colour space ─────────────────────────────────

    [Fact]
    public void ColourSpace_TakesThatManyChannels_AndTheDecodeArrayApplies()
    {
        JpxPdfImage image = Decode(Rgba.Codestream, WithColourSpace(3));
        Assert.Equal((3, JpxColourModel.ImageDictionary, true), (image.ColourComponents, image.ColourModel, image.DecodeArrayApplies));
        Assert.Equal(Interleaved(Rgba, 0, 1, 2), image.Colour);
        Assert.Null(image.Opacity);
        Assert.Contains(image.Deviations, d => d.Contains("channel(s) 3 lie beyond", StringComparison.Ordinal));

        // One channel: the first, as grey.
        Assert.Equal(Rgb.Planes[0], Decode(Rgb.Codestream, WithColourSpace(1)).Colour);
    }

    [Fact]
    public void ColourSpace_NeedingMoreChannelsThanTheCodestreamHas_IsRefused()
    {
        string reason = Refusal(Rgb.Codestream, WithColourSpace(4));
        Assert.Contains("has 3 ordinary colour channel(s); the image dictionary's 4-component colour space needs 4", reason, StringComparison.Ordinal);
        // The control: three are enough for three.
        Assert.Equal(3, Decode(Rgb.Codestream, WithColourSpace(3)).ColourComponents);
    }

    [Fact]
    public void IndexedColourSpace_ReadsTheIndicesRatherThanApplyingTheFilesPalette()
    {
        byte[] palettised = Jp2(Gray, Palette(), H.Cmap((0, 1, 0), (0, 1, 1), (0, 1, 2)), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb));
        JpxPdfImage indexed = Decode(palettised, WithColourSpace(1, indexed: true));
        Assert.Equal((1, JpxColourModel.ImageDictionary), (indexed.ColourComponents, indexed.ColourModel));
        Assert.Equal(Gray.Planes[0], indexed.Colour);
        Assert.False(indexed.Plan.PaletteApplied);
    }

    // ── the JP2 header's rules ──────────────────────────────────────────────

    /// <summary>A 256-entry palette of three 8-bit columns: entry i is (i, 255 − i, (7 i) mod 256).</summary>
    private static byte[] Palette()
    {
        var entries = new long[256][];
        for (int i = 0; i < 256; i++) entries[i] = new long[] { i, 255 - i, (7 * i) & 0xFF };
        return H.Pclr(new byte[] { 7, 7, 7 }, entries);
    }

    [Fact]
    public void Palette_ExpandsEachIndexToItsEntry()
    {
        byte[] palettised = Jp2(Gray, Palette(), H.Cmap((0, 1, 0), (0, 1, 1), (0, 1, 2)), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb));
        JpxPdfImage image = Decode(palettised, NoDictionary);

        Assert.Equal((3, JpxColourModel.Rgb, true), (image.ColourComponents, image.ColourModel, image.Plan.PaletteApplied));
        var expected = new byte[Gray.Width * Gray.Height * 3];
        for (int i = 0; i < Gray.Width * Gray.Height; i++)
        {
            int index = Gray.Planes[0][i];
            expected[i * 3] = (byte)index;
            expected[i * 3 + 1] = (byte)(255 - index);
            expected[i * 3 + 2] = (byte)((7 * index) & 0xFF);
        }
        Assert.Equal(expected, image.Colour);
    }

    /// <summary>T.800 I.5.3.4: an index past the table takes its last entry rather than reading past it.</summary>
    [Fact]
    public void Palette_AnIndexPastTheTable_TakesTheLastEntry()
    {
        // Four entries only; the grey plane runs to 255.
        byte[] pclr = H.Pclr(new byte[] { 7, 7, 7 }, new long[] { 10, 20, 30 }, new long[] { 11, 21, 31 }, new long[] { 12, 22, 32 }, new long[] { 13, 23, 33 });
        byte[] palettised = Jp2(Gray, pclr, H.Cmap((0, 1, 0), (0, 1, 1), (0, 1, 2)), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb));
        JpxPdfImage image = Decode(palettised, NoDictionary);
        for (int i = 0; i < Gray.Width * Gray.Height; i++)
        {
            int entry = Math.Min((int)Gray.Planes[0][i], 3);
            Assert.Equal((byte)(10 + entry), image.Colour[i * 3]);
            Assert.Equal((byte)(30 + entry), image.Colour[i * 3 + 2]);
        }
        // Potency: the plane has indices past 3, so the clamp decided something.
        Assert.Contains(Gray.Planes[0], v => v > 3);
    }

    [Fact]
    public void ChannelDefinition_OrdersTheColourChannelsByAssociation()
    {
        byte[] jp2 = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1)));
        JpxPdfImage image = Decode(jp2, NoDictionary);

        Assert.Equal(new[] { 2, 1, 0 }, image.Plan.ColourChannels);
        Assert.Equal(Interleaved(Rgb, 2, 1, 0), image.Colour);
        // The control: no cdef, component order.
        Assert.Equal(Interleaved(Rgb, 0, 1, 2), Decode(Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb)), NoDictionary).Colour);
    }

    /// <summary>
    /// sYCC (T.800 Annex I: IEC 61966-2-1's YCC) to RGB with the ICT's constants — R = Y + 1.402 Cr,
    /// G = Y − 0.34413 Cb − 0.71414 Cr, B = Y + 1.772 Cb over the centred chroma, rounded half away
    /// from zero and clipped — computed here from the source planes, independently of the decoder.
    /// </summary>
    [Fact]
    public void SYcc_IsConvertedToRgbByTheYccFormula()
    {
        byte[] jp2 = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SYcc));
        JpxPdfImage image = Decode(jp2, NoDictionary);
        Assert.Equal((JpxColourModel.Rgb, Jp2ColourSpaceKind.SYcc), (image.ColourModel, image.Plan.ColourSpace));

        int off = 0, changed = 0;
        for (int i = 0; i < Rgb.Width * Rgb.Height; i++)
        {
            double y = Rgb.Planes[0][i], cb = Rgb.Planes[1][i] - 128.0, cr = Rgb.Planes[2][i] - 128.0;
            byte r = Clip(y + 1.402 * cr), g = Clip(y - 0.34413 * cb - 0.71414 * cr), b = Clip(y + 1.772 * cb);
            if (Math.Abs(image.Colour[i * 3] - r) > 1 || Math.Abs(image.Colour[i * 3 + 1] - g) > 1 || Math.Abs(image.Colour[i * 3 + 2] - b) > 1) off++;
            if (image.Colour[i * 3] != Rgb.Planes[0][i]) changed++;
        }
        Assert.Equal(0, off);
        // Potency: a conversion happened — the red channel is not the luma it came from.
        Assert.True(changed > Rgb.Width * Rgb.Height / 2, $"only {changed} samples differ from the source luma");
    }

    private static byte Clip(double v)
    {
        double r = Math.Round(v, MidpointRounding.AwayFromZero);
        return r < 0 ? (byte)0 : r > 255 ? (byte)255 : (byte)r;
    }

    [Fact]
    public void SMaskInData_SplitsTheOpacityChannel_TwoMeansPremultiplied()
    {
        byte[] jp2 = Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)));

        JpxPdfImage straight = Decode(jp2, WithSMask(1));
        Assert.Equal(Interleaved(Rgba, 0, 1, 2), straight.Colour);
        Assert.Equal(Rgba.Planes[3], straight.Opacity);
        Assert.False(straight.OpacityIsPremultiplied);

        JpxPdfImage premultiplied = Decode(jp2, WithSMask(2));
        Assert.Equal(Rgba.Planes[3], premultiplied.Opacity);
        Assert.True(premultiplied.OpacityIsPremultiplied);

        JpxPdfImage ignored = Decode(jp2, NoDictionary);
        Assert.Null(ignored.Opacity);
        Assert.Equal(3, ignored.ColourComponents);
    }

    [Fact]
    public void GreyWithOpacity_KeepsTheGreyAndSplitsTheAlpha()
    {
        byte[] jp2 = Jp2(GrayAlpha, H.ColrEnumerated(Jp2EnumeratedColourSpaces.Greyscale), H.Cdef((0, 0, 1), (1, 1, 0)));
        JpxPdfImage image = Decode(jp2, WithSMask(1));
        Assert.Equal((1, JpxColourModel.Gray), (image.ColourComponents, image.ColourModel));
        Assert.Equal(GrayAlpha.Planes[0], image.Colour);
        Assert.Equal(GrayAlpha.Planes[1], image.Opacity);
    }

    [Fact]
    public void Cmyk_IsReportedAsCmykInColourOrder()
    {
        byte[] jp2 = Jp2(Rgba, H.ColrEnumerated(Jp2EnumeratedColourSpaces.Cmyk));
        JpxPdfImage image = Decode(jp2, NoDictionary);
        Assert.Equal((4, JpxColourModel.Cmyk, Jp2ColourSpaceKind.Cmyk), (image.ColourComponents, image.ColourModel, image.Plan.ColourSpace));
        Assert.Equal(Interleaved(Rgba, 0, 1, 2, 3), image.Colour);
    }

    [Fact]
    public void IccProfile_IsHandedOnWithTheSamplesInItsSpace_WhenTheConsumerAcceptsIt()
    {
        byte[] profile = H.IccProfile("RGB ");
        byte[] jp2 = Jp2(Rgb, H.ColrIcc(profile));

        JpxPdfImage accepted = Decode(jp2, NoDictionary, _ => true);
        Assert.Equal((JpxColourModel.Rgb, Jp2ColourSpaceKind.IccProfile), (accepted.ColourModel, accepted.Plan.ColourSpace));
        Assert.Equal(profile, accepted.IccProfile);
        Assert.Equal(Interleaved(Rgb, 0, 1, 2), accepted.Colour);

        // Declined by the consumer: the device fallback, no profile.
        JpxPdfImage declined = Decode(jp2, NoDictionary, _ => false);
        Assert.Equal((JpxColourModel.Rgb, Jp2ColourSpaceKind.DeviceRgb), (declined.ColourModel, declined.Plan.ColourSpace));
        Assert.Null(declined.IccProfile);
    }

    /// <summary>
    /// T.801 M-18 with Table M.29bis's defaults for 8-bit channels: L* = N · 100 / 255,
    /// a* = (N − 128) · 170 / 255, b* = (N − 96) · 200 / 255 — expressed here as the ranges and the
    /// 8-bit offsets the consumer applies.
    /// </summary>
    [Fact]
    public void CieLab_IsHandedOnWithItsRangesAndOffsets()
    {
        JpxPdfImage defaults = Decode(Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieLab)), NoDictionary);
        Assert.Equal(JpxColourModel.Lab, defaults.ColourModel);
        Assert.Equal(new[] { 100.0, 170.0, 200.0 }, defaults.LabRanges);
        Assert.Equal(new[] { 0.0, 128.0, 96.0 }, defaults.LabOffsets);
        Assert.Equal(Interleaved(Rgb, 0, 1, 2), defaults.Colour);

        // An EP field: ranges 100, 256, 256, offsets 0, 128, 128, D50.
        byte[] ep = U32s(100, 0, 256, 128, 256, 128, Jp2CieLabParameters.IlluminantD50);
        JpxPdfImage explicit8 = Decode(Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieLab, ep: ep)), NoDictionary);
        Assert.Equal(new[] { 100.0, 256.0, 256.0 }, explicit8.LabRanges);
        Assert.Equal(new[] { 0.0, 128.0, 128.0 }, explicit8.LabOffsets);
    }

    private static byte[] U32s(params uint[] values)
    {
        var b = new byte[4 * values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            b[4 * i] = (byte)(values[i] >> 24); b[4 * i + 1] = (byte)(values[i] >> 16);
            b[4 * i + 2] = (byte)(values[i] >> 8); b[4 * i + 3] = (byte)values[i];
        }
        return b;
    }

    [Fact]
    public void ImageMask_IsOneBitPerPixel()
    {
        // A 1-bit codestream is not among the fixtures, so the rule is asserted by its refusal
        // of an 8-bit one, beside the plan's own tests of the 1-bit case.
        Assert.Contains("needs 1-bit samples", Refusal(Gray.Codestream, new Jp2PdfImageFacts(0, false, 0, true)), StringComparison.Ordinal);
    }

    // ── tolerance and refusal ───────────────────────────────────────────────

    /// <summary>A JP2 whose header cannot be read still decodes, as a bare codestream, with the refusal recorded.</summary>
    [Fact]
    public void Jp2WithAMalformedHeader_DecodesAsABareCodestream_WithADeviation()
    {
        // A cmap without a pclr is a header refusal (T.800 I.5.3.5).
        byte[] jp2 = Jp2(Rgb, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Cmap((0, 0, 0), (1, 0, 0), (2, 0, 0)));
        JpxPdfImage image = Decode(jp2, NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.DeviceRgb, image.Plan.ColourSpace);
        Assert.Equal(Interleaved(Rgb, 0, 1, 2), image.Colour);
        Assert.Contains(image.Deviations, d => d.StartsWith("JP2 header: ", StringComparison.Ordinal) && d.Contains("Component Mapping box without a Palette box", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncatedOrEmptyData_IsRefusedWithAReason()
    {
        Assert.Contains("no JPEG 2000 data", Refusal(Array.Empty<byte>(), NoDictionary), StringComparison.Ordinal);
        Assert.Contains("no JPEG 2000 data", Refusal(null!, NoDictionary), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(Refusal(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 0, 0, 0 }, NoDictionary)));
    }

    // ── the public facade ───────────────────────────────────────────────────

    [Fact]
    public void Facade_DecodesUnderTheSameRules_AndNarrowsByTheOneRule()
    {
        Assert.True(JpxImageDecoding.TryDecode(Rgba.Codestream, new JpxImageDictionaryFacts(0, false, 1, false), out JpxImagePixels pixels, out string reason), reason);
        Assert.Equal((Rgba.Width, Rgba.Height, 3, JpxColourModel.Rgb, false), (pixels.Width, pixels.Height, pixels.ColourComponents, pixels.ColourModel, pixels.DecodeArrayApplies));
        Assert.Equal(Interleaved(Rgba, 0, 1, 2), pixels.Colour);
        Assert.Equal(Rgba.Planes[3], pixels.Opacity);
        // A raw codestream declares no opacity channel: the plan's tolerance is on record.
        Assert.Contains(pixels.Deviations, d => d.Contains("channel 3, the first past DeviceRgb's 3, is taken as the one /SMaskInData 1 announces", StringComparison.Ordinal));

        Assert.False(JpxImageDecoding.TryDecode(new byte[] { 1, 2, 3 }, JpxImageDictionaryFacts.None, out JpxImagePixels none, out reason));
        Assert.Null(none);
        Assert.Contains("no JPEG 2000 data", reason, StringComparison.Ordinal);

        // The narrowing rule, as the plugin calls it: 12-bit shifts, 4-bit scales, signed offsets.
        Assert.Equal(255, JpxImageDecoding.NarrowTo8Bit(4095, 12, false));
        Assert.Equal(128, JpxImageDecoding.NarrowTo8Bit(2048, 12, false));
        Assert.Equal(255, JpxImageDecoding.NarrowTo8Bit(15, 4, false));
        Assert.Equal(0, JpxImageDecoding.NarrowTo8Bit(-128, 8, true));
        Assert.Equal(255, JpxImageDecoding.NarrowTo8Bit(127, 8, true));
        Assert.Equal(200, JpxImageDecoding.NarrowTo8Bit(200, 8, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageDecoding.NarrowTo8Bit(1, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxImageDecoding.NarrowTo8Bit(1, 39, false));
        // T.800's deepest samples, through the long form: the top byte of a 38-bit value.
        Assert.Equal(128, JpxImageDecoding.NarrowTo8Bit(1L << 37, 38, false));
        Assert.Equal(255, JpxImageDecoding.NarrowTo8Bit((1L << 38) - 1, 38, false));
        Assert.Equal(0, JpxImageDecoding.NarrowTo8Bit(-(1L << 37), 38, true));
    }
}
