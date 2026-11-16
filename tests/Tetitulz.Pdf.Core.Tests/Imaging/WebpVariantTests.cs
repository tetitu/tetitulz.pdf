// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// Each WebP variant's own signature is verified before its dimension bytes are believed, and the
/// alpha flag is read rather than assumed.
/// </summary>
/// <remarks>
/// <para>WebP has three shapes behind one RIFF header: <c>VP8 </c> (lossy), <c>VP8L</c> (lossless)
/// and <c>VP8X</c> (extended). They put the canvas size in three different places, and each carries
/// its own marker — VP8's <c>9D 01 2A</c> start code, VP8L's <c>0x2F</c>.</para>
///
/// <para><b>The shared inspector checked neither, and the private copy it replaced checked both.</b>
/// Without them a file whose fourcc says <c>VP8L</c> but whose body is something else has four
/// arbitrary bytes read as a size and reported to every registered render hook. Merging the two
/// readers without noticing would have made the <i>shared</i> one the weaker — the opposite of the
/// point of merging.</para>
///
/// <para><b>Alpha was hardcoded <c>true</c> for every WebP.</b> A plain VP8 lossy bitstream has no
/// alpha channel; VP8X states it in a flag byte. Nothing reads
/// <c>RasterImageInfo.HasAlpha</c> yet, which is precisely how a wrong value survives — the first
/// consumer inherits it.</para>
/// </remarks>
public class WebpVariantTests
{
    private const int Width = 320;
    private const int Height = 200;

    /// <summary>All three variants report the same canvas, because they describe the same image.</summary>
    [Theory]
    [InlineData("VP8 ")]
    [InlineData("VP8L")]
    [InlineData("VP8X")]
    public void EveryVariantReportsItsCanvas(string fourcc)
    {
        Assert.True(RasterImageInspector.TryInspect(Webp(fourcc), out var info));

        Assert.Equal(RasterImageFormat.Webp, info.Format);
        Assert.Equal(Width, info.Width);
        Assert.Equal(Height, info.Height);
    }

    /// <summary>
    /// A corrupted variant signature is refused rather than believed. This is the case the shared
    /// reader got wrong: the dimension bytes are still perfectly readable, so a reader that does
    /// not check the marker returns a confident, wrong size.
    /// </summary>
    [Theory]
    [InlineData("VP8 ", 23)]   // the 9D of the start code
    [InlineData("VP8 ", 25)]   // the 2A of the start code
    [InlineData("VP8L", 20)]   // the 0x2F signature byte
    public void ACorruptVariantSignatureIsRefusedRatherThanBelieved(string fourcc, int corruptAt)
    {
        byte[] bytes = Webp(fourcc);
        bytes[corruptAt] ^= 0xFF;

        Assert.False(RasterImageInspector.TryInspect(bytes, out var info));
        Assert.Equal(0, info.Width);
        Assert.Equal(0, info.Height);
    }

    /// <summary>
    /// Alpha comes from the format: VP8L's bitstream is ARGB, a plain VP8's is not, and VP8X says so
    /// in a flag.
    /// </summary>
    [Theory]
    [InlineData("VP8 ", false)]
    [InlineData("VP8L", true)]
    public void AlphaIsDerivedFromTheVariant(string fourcc, bool expected)
    {
        Assert.True(RasterImageInspector.TryInspect(Webp(fourcc), out var info));
        Assert.Equal(expected, info.HasAlpha);
    }

    /// <summary>A VP8X container's alpha flag is read, in both states.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AVp8xAlphaFlagIsRead(bool alpha)
    {
        Assert.True(RasterImageInspector.TryInspect(Webp("VP8X", vp8xAlpha: alpha), out var info));
        Assert.Equal(alpha, info.HasAlpha);
    }

    // ================================================================ fixture

    /// <summary>
    /// A RIFF/WEBP header with the given first chunk, laid out byte-exactly. No pixel data — only
    /// the header structure is under test.
    /// </summary>
    private static byte[] Webp(string fourcc, bool vp8xAlpha = false)
    {
        var b = new List<byte>();
        b.AddRange(Ascii("RIFF"));
        b.AddRange(new byte[] { 0, 0, 0, 0 });      // file size — not read
        b.AddRange(Ascii("WEBP"));
        b.AddRange(Ascii(fourcc));                   // 12..15
        b.AddRange(new byte[] { 0, 0, 0, 0 });      // 16..19 chunk size — not read

        switch (fourcc)
        {
            case "VP8X":
                b.Add((byte)(vp8xAlpha ? 0x10 : 0x00));           // 20 flags (bit 4 = alpha)
                b.AddRange(new byte[] { 0, 0, 0 });               // 21..23 reserved
                Add24(b, Width - 1);                              // 24..26 canvas width-1
                Add24(b, Height - 1);                             // 27..29 canvas height-1
                break;

            case "VP8L":
                b.Add(0x2F);                                      // 20 signature
                int bits = (Width - 1) | ((Height - 1) << 14);    // 21..24, 14 bits each
                b.Add((byte)bits); b.Add((byte)(bits >> 8));
                b.Add((byte)(bits >> 16)); b.Add((byte)(bits >> 24));
                b.AddRange(new byte[] { 0, 0, 0, 0, 0 });         // pad to 30
                break;

            default: // "VP8 "
                b.AddRange(new byte[] { 0, 0, 0 });               // 20..22 frame tag
                b.AddRange(new byte[] { 0x9D, 0x01, 0x2A });      // 23..25 start code
                b.Add(unchecked((byte)Width)); b.Add((byte)(Width >> 8));      // 26..27
                b.Add(unchecked((byte)Height)); b.Add((byte)(Height >> 8));    // 28..29
                break;
        }

        return b.ToArray();
    }

    private static void Add24(List<byte> b, int v)
    {
        b.Add((byte)v); b.Add((byte)(v >> 8)); b.Add((byte)(v >> 16));
    }

    private static byte[] Ascii(string s) => System.Text.Encoding.ASCII.GetBytes(s);
}
