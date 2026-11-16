// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// The JPEG marker walk survives the padding a conforming encoder is allowed to emit: runs of
/// <c>0xFF</c> fill bytes before a marker code, and the standalone markers that carry no length.
/// </summary>
/// <remarks>
/// <para>Four assemblies walked JPEG markers independently — <c>Core.RasterImageInspector</c>,
/// <c>Base.JpegHeader</c>, <c>Builder.BuilderImageDecoder</c>, <c>DocxToPdf.ImageRenderer</c> and
/// <c>Signing.VisualSignatureBuilder</c> — because the shared reader lived in <c>Base</c>, which
/// three of them may not reference. They agreed on the SOF marker set and disagreed on everything
/// around it.</para>
///
/// <para><b>The weakest was <c>BuilderImageDecoder</c></b>: no fill-byte handling, no
/// <c>segLen &lt; 2</c> guard, no stop at SOS. On a padded JPEG it read a segment length out of the
/// wrong two bytes and jumped to a wrong offset, from which it either resynchronised past the frame
/// header or ran off the end — returning <c>null</c>, which its caller treats as "not a usable
/// image". The picture was dropped and nothing said why.</para>
///
/// <para><b>The fixtures are byte-exact JPEG headers, not decoded images.</b> Only the marker
/// structure is under test, so a real photograph would add nothing and hide which byte mattered.</para>
/// </remarks>
public class JpegFillByteTests
{
    private const int Width = 640;
    private const int Height = 480;

    /// <summary>
    /// Padding before the SOF, before an APP segment, and none at all — all three must yield the
    /// same dimensions, because the padding is not part of the image.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void FillBytesBeforeAMarkerDoNotDisturbTheFrameHeader(int fill)
    {
        Assert.True(RasterImageInspector.TryInspect(Jpeg(fillBeforeSof: fill), out var info));

        Assert.Equal(RasterImageFormat.Jpeg, info.Format);
        Assert.Equal(Width, info.Width);
        Assert.Equal(Height, info.Height);
        Assert.Equal(3, info.Components);
    }

    /// <summary>
    /// A TEM marker (<c>0x01</c>) carries no length field. A walker that treats it as
    /// length-bearing reads two bytes of the <i>next</i> marker as a segment length and jumps to a
    /// wrong offset — so this is a claim about the offset, not about TEM itself.
    /// </summary>
    [Fact]
    public void AStandaloneTemMarkerIsSkippedRatherThanReadAsALengthBearingSegment()
    {
        Assert.True(RasterImageInspector.TryInspect(Jpeg(temBeforeSof: true), out var info));

        Assert.Equal(Width, info.Width);
        Assert.Equal(Height, info.Height);
    }

    /// <summary>
    /// The Adobe APP14 marker is still found when padding precedes it — it is what tells a
    /// four-component frame apart from a four-component frame of something else, and getting it
    /// wrong inverts the image.
    /// </summary>
    [Fact]
    public void ThePaddedAdobeMarkerIsStillFound()
    {
        Assert.True(RasterImageInspector.TryInspect(Jpeg(fillBeforeSof: 3, adobe: true), out var info));

        Assert.True(info.HasAdobeMarker);
        Assert.Equal(2, info.AdobeTransform);   // YCCK, the value written below
        Assert.Equal(Width, info.Width);
    }

    // ================================================================ fixture

    /// <summary>
    /// A JPEG header: SOI, an optional Adobe APP14, optional padding, an optional TEM, then a
    /// baseline SOF0 and an empty SOS. No entropy data — nothing here decodes an image.
    /// </summary>
    private static byte[] Jpeg(int fillBeforeSof = 0, bool temBeforeSof = false, bool adobe = false)
    {
        var b = new List<byte> { 0xFF, 0xD8 };            // SOI

        if (adobe)
        {
            b.AddRange(new byte[] { 0xFF, 0xEE, 0x00, 0x0E });          // APP14, length 14
            b.AddRange(new byte[] { (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e' });
            b.AddRange(new byte[] { 0x00, 0x64, 0x00, 0x00, 0x00, 0x00 });
            b.Add(0x02);                                                 // transform = 2 (YCCK)
        }

        for (int i = 0; i < fillBeforeSof; i++) b.Add(0xFF);             // legal padding
        if (temBeforeSof) b.AddRange(new byte[] { 0xFF, 0x01 });         // TEM — no length field

        b.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08 });         // SOF0, length 17, 8-bit
        b.Add((byte)(Height >> 8)); b.Add((byte)(Height & 0xFF));
        b.Add((byte)(Width >> 8)); b.Add((byte)(Width & 0xFF));
        b.Add(0x03);                                                     // 3 components
        for (int c = 1; c <= 3; c++) { b.Add((byte)c); b.Add(0x11); b.Add(0x00); }

        b.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 });
        b.AddRange(new byte[] { 0xFF, 0xD9 });                           // EOI
        return b.ToArray();
    }
}
