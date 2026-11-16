// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// Wave 17 R16.1: <see cref="PdfContentStreamBuilder.AddImage(byte[], int, int, string, string, int, double[], byte[])"/>
/// and <see cref="PdfContentStreamBuilder.AddJpxImage"/> are where every producer's image is written,
/// and neither asked the authored-size rule — a JPEG claiming 65 535 × 65 535 pixels went from a DOCX
/// into the output as declared. Both refuse now, naming the side; a legal image is still added.
/// </summary>
public sealed class PdfContentStreamBuilderImageSizeTests
{
    private static readonly byte[] FourBytes = new byte[4];

    public static TheoryData<int, int, string> Oversized => new()
    {
        { PdfLimits.MaxImageDimension + 1, 1, "width" },
        { 1, PdfLimits.MaxImageDimension + 1, "height" },
        { PdfLimits.MaxImageDimension + 1, PdfLimits.MaxImageDimension + 1, "width" },
        { 0, 10, "width" },
        { 10, -1, "height" },
    };

    [Theory]
    [MemberData(nameof(Oversized))]
    public void AddImage_RefusesASideOutsideTheRule_NamingIt(int width, int height, string parameter)
    {
        var builder = new PdfContentStreamBuilder();
        var raw = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddImage(FourBytes, width, height, PdfFilterNames.DctDecode, PdfColorSpaceNames.DeviceRGB, 8, null, null));
        Assert.Equal(parameter, raw.ParamName);
        var jpx = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddJpxImage(FourBytes, width, height, PdfColorSpaceNames.DeviceRGB, 8, 0));
        Assert.Equal(parameter, jpx.ParamName);
    }

    /// <summary>Two legal sides whose product is past the area limit are refused too.</summary>
    [Fact]
    public void AddImage_RefusesAnAreaPastTheLimit()
    {
        int side = (int)Math.Sqrt(PdfLimits.MaxImagePixelArea) + 1;
        Assert.True(side <= PdfLimits.MaxImageDimension, "the fixture must stay inside the per-side limit");
        var builder = new PdfContentStreamBuilder();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddImage(FourBytes, side, side, PdfFilterNames.DctDecode, PdfColorSpaceNames.DeviceRGB, 8, null, null));
        Assert.Contains("area limit", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: an image inside the rule is added and named.</summary>
    [Fact]
    public void AddImage_InsideTheRule_IsAdded()
    {
        var builder = new PdfContentStreamBuilder();
        string raw = builder.AddImage(new byte[2 * 2 * 3], 2, 2, null, PdfColorSpaceNames.DeviceRGB, 8, null, null);
        string jpx = builder.AddJpxImage(FourBytes, PdfLimits.MaxImageDimension, 1, PdfColorSpaceNames.DeviceRGB, 8, 0);
        Assert.StartsWith("Im", raw, StringComparison.Ordinal);
        Assert.StartsWith("Im", jpx, StringComparison.Ordinal);
        Assert.NotEqual(raw, jpx);
    }
}
