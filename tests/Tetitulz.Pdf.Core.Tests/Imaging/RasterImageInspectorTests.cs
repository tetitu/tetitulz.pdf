// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// <see cref="RasterImageInspector"/> — the single answer to "what is this image?".
///
/// <para>It exists because that question used to have two answers in two assemblies: one
/// component read PNG/GIF/BMP/JPEG headers for sizing, another embedded only PNG and JPEG. A GIF
/// was therefore measured and then dropped, and the layout still advanced by the measured height,
/// producing a correctly-sized blank hole.</para>
/// </summary>
public class RasterImageInspectorTests
{
    [Fact]
    public void Png_DimensionsAndComponents()
    {
        Assert.True(RasterImageInspector.TryInspect(Png(7, 3, colourType: 2), out var info));

        Assert.Equal(RasterImageFormat.Png, info.Format);
        Assert.Equal(7, info.Width);
        Assert.Equal(3, info.Height);
        Assert.Equal(3, info.Components);
        Assert.False(info.HasAlpha);
        Assert.False(info.IsInterlaced);
    }

    [Fact]
    public void Png_WithAlphaChannel_IsReported()
    {
        Assert.True(RasterImageInspector.TryInspect(Png(4, 4, colourType: 6), out var info));
        Assert.True(info.HasAlpha);
        Assert.Equal(3, info.Components);
    }

    [Fact]
    public void Png_Interlaced_IsReported()
    {
        // The distinction the embedding side needs: an Adam7 PNG is a PNG it cannot decode, and
        // saying so is what turns a blank hole into an explicable one.
        Assert.True(RasterImageInspector.TryInspect(Png(4, 4, colourType: 2, interlace: 1), out var info));
        Assert.True(info.IsInterlaced);
    }

    [Fact]
    public void Png_Palette_ReportsThreeComponents()
    {
        // A palette expands to RGB, so the component count describes the decoded form.
        Assert.True(RasterImageInspector.TryInspect(Png(4, 4, colourType: 3), out var info));
        Assert.Equal(3, info.Components);
    }

    [Fact]
    public void Jpeg_DimensionsAndComponents()
    {
        Assert.True(RasterImageInspector.TryInspect(Jpeg(120, 80, components: 3), out var info));

        Assert.Equal(RasterImageFormat.Jpeg, info.Format);
        Assert.Equal(120, info.Width);
        Assert.Equal(80, info.Height);
        Assert.Equal(3, info.Components);
        Assert.False(info.HasAdobeMarker);
    }

    /// <summary>
    /// The Adobe APP14 marker is the only reliable signal that a four-component frame is CMYK.
    /// Surfacing it is what lets a caller refuse an unmarked four-component JPEG rather than
    /// guessing at its colour space — the rule <c>PdfImageReplacer</c> already follows.
    /// </summary>
    [Fact]
    public void Jpeg_AdobeApp14Marker_IsDetectedWithItsTransform()
    {
        Assert.True(RasterImageInspector.TryInspect(
            Jpeg(10, 10, components: 4, adobeTransform: 2), out var info));

        Assert.Equal(4, info.Components);
        Assert.True(info.HasAdobeMarker);
        Assert.Equal(2, info.AdobeTransform);
    }

    [Fact]
    public void Jpeg_FourComponentsWithoutTheMarker_IsNotClaimedAsCmyk()
    {
        Assert.True(RasterImageInspector.TryInspect(Jpeg(10, 10, components: 4), out var info));

        Assert.Equal(4, info.Components);
        Assert.False(info.HasAdobeMarker);
    }

    [Fact]
    public void Jpeg_Progressive_IsReportedAsInterlaced()
    {
        Assert.True(RasterImageInspector.TryInspect(
            Jpeg(10, 10, components: 3, sofMarker: 0xC2), out var info));
        Assert.True(info.IsInterlaced);
    }

    [Fact]
    public void Gif_DimensionsAreRead()
    {
        Assert.True(RasterImageInspector.TryInspect(Gif(64, 48), out var info));

        Assert.Equal(RasterImageFormat.Gif, info.Format);
        Assert.Equal(64, info.Width);
        Assert.Equal(48, info.Height);
    }

    [Fact]
    public void Bmp_DimensionsAreRead()
    {
        Assert.True(RasterImageInspector.TryInspect(Bmp(30, 20), out var info));

        Assert.Equal(RasterImageFormat.Bmp, info.Format);
        Assert.Equal(30, info.Width);
        Assert.Equal(20, info.Height);
    }

    [Fact]
    public void Bmp_TopDown_ReportsThePositiveHeight()
    {
        // A negative height means top-down storage; the magnitude is still the height.
        Assert.True(RasterImageInspector.TryInspect(Bmp(30, -20), out var info));
        Assert.Equal(20, info.Height);
    }

    [Fact]
    public void UnrecognisedBytes_AreRefused()
    {
        Assert.False(RasterImageInspector.TryInspect(Encoding.ASCII.GetBytes("not an image at all"), out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(11)]
    public void TruncatedInput_IsRefusedWithoutThrowing(int length)
    {
        // Adversarial: every reader here indexes into the buffer, so a short one must be
        // rejected by the length guard rather than by an IndexOutOfRangeException.
        Assert.False(RasterImageInspector.TryInspect(new byte[length], out _));
    }

    [Fact]
    public void MalformedPngWithAbsurdChunkLength_IsRefusedWithoutThrowing()
    {
        byte[] png = Png(4, 4, colourType: 2);
        // Corrupt the IHDR length to something enormous.
        png[8] = 0x7F; png[9] = 0xFF; png[10] = 0xFF; png[11] = 0xFF;
        var ex = Record.Exception(() => RasterImageInspector.TryInspect(png, out _));
        Assert.Null(ex);
    }

    [Fact]
    public void MalformedJpegWithZeroSegmentLength_DoesNotHang()
    {
        // A zero-length segment would advance the cursor by 2 forever if the loop trusted it.
        byte[] jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x00, 0xFF, 0xD9, 0, 0, 0, 0 };
        var ex = Record.Exception(() => RasterImageInspector.TryInspect(jpeg, out _));
        Assert.Null(ex);
    }

    // ---------------------------------------------------------------- synthesis

    private static byte[] Png(int w, int h, int colourType, int interlace = 0)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
        ihdr[8] = 8;                 // bit depth
        ihdr[9] = (byte)colourType;
        ihdr[12] = (byte)interlace;
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
        s.Write(Encoding.ASCII.GetBytes(type), 0, 4);
        s.Write(data, 0, data.Length);
        s.Write(new byte[4], 0, 4);  // CRC — the inspector does not verify it
    }

    /// <summary>
    /// <see cref="RasterImageInspector.TryInspect"/>'s JPEG gate is <b>deliberately looser</b> than
    /// <see cref="RasterImageInspector.IsJpeg"/>: a JPEG carrying fill bytes between its SOI and the
    /// next marker is still read, because what follows the gate is a parse that finds a real SOF —
    /// not a sniff whose answer is final.
    /// </summary>
    /// <remarks>
    /// Pinned because the difference looks exactly like the drift this class was created to end,
    /// and the obvious "consolidation" — routing this gate through <c>IsJpeg</c> like the PNG, GIF
    /// and BMP gates beside it — would silently start rejecting this file.
    /// </remarks>
    [Fact]
    public void AJpegWithFillBytesAfterItsSoi_IsStillInspected_ThoughIsJpegRejectsIt()
    {
        byte[] jpeg = Jpeg(9, 5, components: 3);

        // Splice two fill bytes in after the SOI, which IsJpeg's third-byte test refuses.
        var padded = new byte[jpeg.Length + 2];
        padded[0] = jpeg[0]; padded[1] = jpeg[1];
        padded[2] = 0x00; padded[3] = 0x00;
        Array.Copy(jpeg, 2, padded, 4, jpeg.Length - 2);

        Assert.False(RasterImageInspector.IsJpeg(padded));

        Assert.True(RasterImageInspector.TryInspect(padded, out var info));
        Assert.Equal(RasterImageFormat.Jpeg, info.Format);
        Assert.Equal(9, info.Width);
        Assert.Equal(5, info.Height);
    }

    /// <summary>
    /// The acceptance control for the gate above: an ordinary JPEG satisfies <c>IsJpeg</c> too, so
    /// the test's point is the <i>difference</i> on a padded file rather than a predicate that
    /// rejects everything.
    /// </summary>
    [Fact]
    public void AnOrdinaryJpeg_SatisfiesBothIsJpegAndTryInspect()
    {
        byte[] jpeg = Jpeg(9, 5, components: 3);

        Assert.True(RasterImageInspector.IsJpeg(jpeg));
        Assert.True(RasterImageInspector.TryInspect(jpeg, out var info));
        Assert.Equal(9, info.Width);
    }

    private static void WriteBE(byte[] b, int o, int v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static byte[] Jpeg(int w, int h, int components, int adobeTransform = -1, byte sofMarker = 0xC0)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8 }, 0, 2);                      // SOI

        if (adobeTransform >= 0)
        {
            byte[] adobe = Encoding.ASCII.GetBytes("Adobe");
            int segLen = 2 + adobe.Length + 7;
            ms.Write(new byte[] { 0xFF, 0xEE, (byte)(segLen >> 8), (byte)segLen }, 0, 4);
            ms.Write(adobe, 0, adobe.Length);
            ms.Write(new byte[] { 0, 100, 0, 0, 0, 0, (byte)adobeTransform }, 0, 7);
        }

        int sofLen = 8 + components * 3;
        ms.Write(new byte[] { 0xFF, sofMarker, (byte)(sofLen >> 8), (byte)sofLen }, 0, 4);
        ms.WriteByte(8);                                                // precision
        ms.Write(new byte[] { (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w }, 0, 4);
        ms.WriteByte((byte)components);
        for (int i = 0; i < components; i++) ms.Write(new byte[] { (byte)(i + 1), 0x11, 0 }, 0, 3);

        ms.Write(new byte[] { 0xFF, 0xD9 }, 0, 2);                      // EOI
        return ms.ToArray();
    }

    private static byte[] Gif(int w, int h)
    {
        var b = new byte[13];
        Encoding.ASCII.GetBytes("GIF89a").CopyTo(b, 0);
        b[6] = (byte)w; b[7] = (byte)(w >> 8);
        b[8] = (byte)h; b[9] = (byte)(h >> 8);
        return b;
    }

    private static byte[] Bmp(int w, int h)
    {
        var b = new byte[54];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        WriteLE(b, 18, w);
        WriteLE(b, 22, h);
        return b;
    }

    private static void WriteLE(byte[] b, int o, int v)
    { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
}
