// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A device-link profile (ICC.1 §8.6, class <c>link</c>) as a conversion's destination: its
/// <c>A2B0</c> maps RGB straight to CMYK, the link's own rendering. The expected values are
/// a second independent CMM's evaluation on the same bytes (<see cref="DeviceLinkProfile"/>) — an
/// evaluator this repository did not write.
/// </summary>
public class IccDeviceLinkTests
{
    private const double Tolerance = 3e-4;   // the reference prints six places

    private static float[] Convert(IccOutputTransform transform, byte[] rgb)
    {
        var device = new float[transform.Channels];
        Assert.True(transform.TryConvert(new SKColor(rgb[0], rgb[1], rgb[2]), device));
        return device;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ALinkConvertsAsTheReferenceEvaluatesIt(int version)
    {
        var transform = IccOutputTransform.TryCreate(DeviceLinkProfile.Build(version), IccRenderingIntent.Perceptual,
                                                     blackPointCompensation: false, out string reason);
        Assert.True(transform != null, reason);
        Assert.Equal(4, transform.Channels);
        foreach (var (rgb, cmyk) in DeviceLinkProfile.ReferenceVectors)
        {
            float[] device = Convert(transform, rgb);
            for (int k = 0; k < 4; k++)
                Assert.True(Math.Abs(device[k] - cmyk[k]) <= Tolerance,
                    $"({string.Join(",", rgb)}) channel {k}: {device[k]:F6}, reference {cmyk[k]:F6}");
        }
    }

    /// <summary>A link to grey has one output channel — a profile's table would need the PCS's three.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ALinkToGreyHasOneChannel(int version)
    {
        var transform = IccOutputTransform.TryCreate(DeviceLinkProfile.Build(version, outputSpace: "GRAY", outputs: 1),
                                                     IccRenderingIntent.Perceptual, false, out string reason);
        Assert.True(transform != null, reason);
        Assert.Equal(1, transform.Channels);
        foreach (var (rgb, cmyk) in DeviceLinkProfile.ReferenceVectors)
            Assert.True(Math.Abs(Convert(transform, rgb)[0] - cmyk[0]) <= Tolerance, $"({string.Join(",", rgb)})");
    }

    /// <summary>
    /// The link is the whole conversion: the intent asked for and black-point compensation change
    /// nothing, and white takes the link's answer — ink here — not the white fix-up's none.
    /// </summary>
    [Fact]
    public void TheIntentCompensationAndWhiteFixUpDoNotApply()
    {
        byte[] link = DeviceLinkProfile.Build();
        var plain = IccOutputTransform.TryCreate(link, IccRenderingIntent.Perceptual, false, out _);
        foreach (IccRenderingIntent intent in Enum.GetValues(typeof(IccRenderingIntent)))
        {
            var other = IccOutputTransform.TryCreate(link, intent, blackPointCompensation: true, out string reason);
            Assert.True(other != null, reason);
            foreach (var (rgb, _) in DeviceLinkProfile.ReferenceVectors)
                Assert.Equal(Convert(plain, rgb), Convert(other, rgb));
        }

        float[] white = Convert(plain, new byte[] { 255, 255, 255 });
        Assert.Equal(DeviceLinkProfile.ReferenceVectors[1].Cmyk[0], white[0], 3);
        Assert.True(white[0] > 0.1f, "white was given no ink");
    }

    /// <summary>A link the conversion cannot use is refused for the reason in its name, beside the accepted link.</summary>
    [Theory]
    [InlineData("CMYK", "RGB ", "must take RGB")]
    [InlineData("RGB ", "Lab ", "not a space this converts into")]
    [InlineData("RGB ", "GRAY", "maps 3 channels to 4")]
    public void ALinkTheConversionCannotUseIsRefused(string input, string output, string why)
    {
        Assert.NotNull(IccOutputTransform.TryCreate(DeviceLinkProfile.Build(), IccRenderingIntent.Perceptual, false, out _));

        var refused = IccOutputTransform.TryCreate(DeviceLinkProfile.Build(inputSpace: input, outputSpace: output),
                                                   IccRenderingIntent.Perceptual, false, out string reason);
        Assert.Null(refused);
        Assert.Contains(why, reason, StringComparison.Ordinal);
    }

    /// <summary>A link describes no colour space and has no device-to-PCS side, so a soft proof refuses it.</summary>
    [Fact]
    public void ALinkIsNoColourSpace_AndNoProof()
    {
        IccProfile parsed = IccProfile.Parse(DeviceLinkProfile.Build());
        Assert.Equal(IccModel.Unsupported, parsed.Model);
        Assert.Contains("no PCS side", parsed.UnsupportedReason, StringComparison.Ordinal);

        Assert.Null(SoftProof.TryCreate(DeviceLinkProfile.Build(), blackPointCompensation: false, out string reason));
        Assert.Contains("device-to-PCS side", reason, StringComparison.Ordinal);
    }

    /// <summary>The colour converter takes a link as its destination profile: each pixel the link's CMYK, quantised once.</summary>
    [Fact]
    public void TheConverterWritesTheLinksCmyk()
    {
        byte[] rgb = DeviceLinkProfile.ReferenceVectors.SelectMany(v => v.Rgb).ToArray();
        byte[] pdf = ImageFixtures.OneImagePdf(rgb, DeviceLinkProfile.ReferenceVectors.Length, 1);
        using var output = new MemoryStream();
        Assert.Equal(1, PdfColorSpaceConverter.ConvertRgbToCmyk(new MemoryStream(pdf), output, DeviceLinkProfile.Build(), gcrFactor: 0.8));
        Assert.Null(PdfColorSpaceConverter.LastConversionWarning);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        byte[] samples = Assert.IsAssignableFrom<PdfStream>(doc.GetObject(5)).GetDecodedData();
        Assert.Equal(DeviceLinkProfile.ReferenceVectors.Length * 4, samples.Length);
        for (int i = 0; i < DeviceLinkProfile.ReferenceVectors.Length; i++)
            for (int k = 0; k < 4; k++)
                Assert.True(Math.Abs(samples[i * 4 + k] - DeviceLinkProfile.ReferenceVectors[i].Cmyk[k] * 255) <= 1,
                    $"pixel {i} channel {k}: {samples[i * 4 + k]}, reference {DeviceLinkProfile.ReferenceVectors[i].Cmyk[k] * 255:F2}");
    }

    /// <summary>The separation plates take a link as their output profile too: a colour's process inks are the link's.</summary>
    [Fact]
    public void TheSeparationOutputTakesALink()
    {
        var output = SeparationOutput.TryCreate(DeviceLinkProfile.Build(), blackPointCompensation: true, out string reason);
        Assert.True(output != null, reason);
        var (rgb, cmyk) = DeviceLinkProfile.ReferenceVectors[3];
        Assert.True(output.TryConvert(new SKColor(rgb[0], rgb[1], rgb[2]), IccRenderingIntents.Default, out float[] inks));
        for (int k = 0; k < 4; k++) Assert.Equal(cmyk[k], inks[k], 3);
    }
}
