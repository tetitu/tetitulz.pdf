// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// The one JPEG marker walk, <see cref="RasterImageInspector.TryInspectJpeg"/>, and the one rule
/// for what an embedded frame's dictionary must say, <see cref="JpegColorSpace"/>.
/// </summary>
/// <remarks>
/// <para><b>A standalone marker carries no length field.</b> A walker that treats one as
/// length-bearing reads the next marker's two bytes as a segment length and jumps to a wrong
/// offset. <c>TEM</c> (<c>0xFF01</c>) and the restart markers are each a row.</para>
/// </remarks>
public class JpegStandaloneMarkerTests
{
    [Theory]
    [InlineData(0x01, "TEM")]
    [InlineData(0xD0, "RST0")]
    [InlineData(0xD7, "RST7")]
    public void AJpegCarryingAStandaloneMarker_IsReadByTheJpegDoorAndTheDispatcherAlike(int marker, string name)
    {
        byte[] jpeg = Jpeg(standaloneBeforeSof: (byte)marker, components: 3);

        Assert.True(RasterImageInspector.TryInspectJpeg(jpeg, out var frame), $"refused a JPEG carrying {name}");
        Assert.True(RasterImageInspector.TryInspect(jpeg, out var info), $"TryInspect refused a JPEG carrying {name}");

        Assert.Equal(64, frame.Width);
        Assert.Equal(32, frame.Height);
        Assert.Equal(3, frame.Components);
        Assert.Equal(RasterImageFormat.Jpeg, info.Format);
        Assert.Equal(frame.Width, info.Width);
        Assert.Equal(frame.Height, info.Height);
    }

    /// <summary>The control: the same JPEG with no standalone marker.</summary>
    [Fact]
    public void ThePlainJpeg_IsRead()
    {
        Assert.True(RasterImageInspector.TryInspectJpeg(Jpeg(null, 3), out var frame));
        Assert.Equal(64, frame.Width);
        Assert.Equal(32, frame.Height);
    }

    /// <summary>
    /// The JPEG door refuses a PNG that the dispatcher accepts — so the door is a JPEG test, not a
    /// permissive one.
    /// </summary>
    [Fact]
    public void APng_IsRefusedByTheJpegDoor_AndAcceptedByTheDispatcher()
    {
        byte[] png =
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0, 0, 0, 0, 0,
        };

        Assert.False(RasterImageInspector.TryInspectJpeg(png, out _));
        Assert.True(RasterImageInspector.TryInspect(png, out var info));
        Assert.Equal(RasterImageFormat.Png, info.Format);
    }

    [Theory]
    [InlineData(1, PdfColorSpaceNames.DeviceGray)]
    [InlineData(3, PdfColorSpaceNames.DeviceRGB)]
    public void GreyAndRgbFrames_NameTheirDeviceSpace_WithNoDecode(int components, string expected)
    {
        Assert.True(RasterImageInspector.TryInspectJpeg(Jpeg(null, components), out var frame));

        Assert.Equal(expected, JpegColorSpace.Of(frame, out string reason));
        Assert.Null(reason);
        Assert.Null(JpegColorSpace.DecodeFor(frame));
    }

    /// <summary>An Adobe four-component frame is CMYK, inverted by its <c>/Decode</c>.</summary>
    [Fact]
    public void AnAdobeFourComponentFrame_IsCmyk_WithTheInvertingDecode()
    {
        Assert.True(RasterImageInspector.TryInspectJpeg(Jpeg(null, 4, adobe: true), out var frame));

        Assert.Equal(PdfColorSpaceNames.DeviceCMYK, JpegColorSpace.Of(frame, out _));
        Assert.Equal(new double[] { 1, 0, 1, 0, 1, 0, 1, 0 }, JpegColorSpace.DecodeFor(frame));
    }

    /// <summary>
    /// Without the Adobe marker a four-component frame's polarity is unknown: refused with the reason,
    /// beside the Adobe frame above that is accepted.
    /// </summary>
    [Fact]
    public void AFourComponentFrameWithoutTheAdobeMarker_IsRefusedForItsPolarity()
    {
        Assert.True(RasterImageInspector.TryInspectJpeg(Jpeg(null, 4, adobe: false), out var frame));
        Assert.False(frame.HasAdobeMarker);

        Assert.Null(JpegColorSpace.Of(frame, out string reason));
        Assert.Contains("Adobe APP14", reason);
    }

    [Fact]
    public void ATwoComponentFrame_IsRefusedByItsCount()
    {
        Assert.True(RasterImageInspector.TryInspectJpeg(Jpeg(null, 2), out var frame));

        Assert.Null(JpegColorSpace.Of(frame, out string reason));
        Assert.Contains("2 components", reason);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A minimal marker chain: SOI, an optional Adobe APP14, an optional standalone marker, a
    /// baseline SOF0 declaring 64x32, then SOS and EOI. No entropy data: nothing here decodes.
    /// </summary>
    private static byte[] Jpeg(byte? standaloneBeforeSof, int components, bool adobe = false)
    {
        var b = new List<byte> { 0xFF, 0xD8 };                       // SOI

        if (adobe)
        {
            // APP14: length 14, "Adobe", version 100, flags0, flags1, transform 0.
            b.AddRange(new byte[] { 0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e',
                                    0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00 });
        }

        if (standaloneBeforeSof.HasValue)
        {
            b.Add(0xFF);
            b.Add(standaloneBeforeSof.Value);
        }

        // SOF0: precision 8, height 32, width 64, then 3 bytes a component.
        int sofLength = 8 + 3 * components;
        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, (byte)sofLength, 0x08, 0x00, 0x20, 0x00, 0x40, (byte)components });
        for (byte c = 1; c <= components; c++) b.AddRange(new byte[] { c, 0x11, 0x00 });

        // SOS: the components, then the three spectral bytes.
        b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, (byte)(6 + 2 * components), (byte)components });
        for (byte c = 1; c <= components; c++) b.AddRange(new byte[] { c, 0x00 });
        b.AddRange(new byte[] { 0x00, 0x3F, 0x00 });

        b.AddRange(new byte[] { 0xFF, 0xD9 });                       // EOI
        return b.ToArray();
    }
}
