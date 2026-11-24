// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary><see cref="JpxEncoder.WrapAsJp2"/>: the boxes of T.800 Annex I around a codestream, read back by <see cref="JpxHeader"/>, by the raw bytes, and by the reference decoder.</summary>
public sealed partial class JpxEncoderJp2Tests
{
    private static byte[] Codestream(int components) =>
        JpxEncoder.EncodeLossless(JpxEncoderTestImages.Pattern(40, 24, components, 11), 40, 24, 8, false, new JpxEncodeOptions { Levels = 2 });

    [Theory]
    [InlineData(1, 17)]
    [InlineData(3, 16)]
    [InlineData(4, 12)]
    public void WrapAsJp2_WritesTheSignatureFileTypeHeaderAndCodestreamBoxes(int components, int enumCs)
    {
        byte[] codestream = Codestream(components);
        byte[] jp2 = JpxEncoder.WrapAsJp2(codestream);

        Assert.True(JpxHeader.IsJp2Container(jp2));
        Assert.True(JpxHeader.TryReadImageSize(jp2, out long width, out long height, out int csiz));
        Assert.Equal((40L, 24L, components), (width, height, csiz));
        Assert.True(JpxHeader.TryReadColourComponents(jp2, out int colour));
        Assert.Equal(components, colour);
        Assert.True(JpxHeader.TryLocateCodestream(jp2, out long start, out long end));
        Assert.Equal(codestream, jp2.Skip((int)start).Take((int)(end - start)).ToArray());
        Assert.Equal(jp2.Length, end);
        Assert.Equal(codestream.Length + JpxEncoder.ContainerBytes(components), jp2.Length);   // what a size target must allow for

        // Raw bytes (I.5.1, I.5.2, I.5.3.1, I.5.3.3): the 12-byte signature; ftyp of length 20 branded 'jp2 ', minor version 0, compatible with 'jp2 '; then jp2h.
        // The CMYK file is a JPX (T.801: EnumCS 12 is Part 2's): brand 'jpx ', compatible with 'jp2 ' and 'jpxb', a Reader Requirements box right after ftyp.
        Assert.Equal(new byte[] { 0, 0, 0, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }, jp2.Take(12).ToArray());
        if (components == 4)
        {
            Assert.Equal("\0\0\0\u0018ftypjpx \0\0\0\0jp2 jpxb", System.Text.Encoding.Latin1.GetString(jp2, 12, 24));
            Assert.Equal(new byte[] { 0, 0, 0, 21, (byte)'r', (byte)'r', (byte)'e', (byte)'q', 1, 0xC0, 0xC0, 0, 2, 0, 1, 0x80, 0, 55, 0x40, 0, 0 }, jp2.Skip(36).Take(21).ToArray());
        }
        else
        {
            Assert.Equal("\0\0\0\u0014ftypjp2 \0\0\0\0jp2 ", System.Text.Encoding.Latin1.GetString(jp2, 12, 20));
            Assert.Equal(-1, IndexOf(jp2, new[] { (byte)'r', (byte)'r', (byte)'e', (byte)'q' }));
        }
        // Where jp2h begins: after the signature and the 20-byte ftyp, or after the 24-byte ftyp and the 21-byte rreq.
        int h = components == 4 ? 12 + 24 + 21 : 12 + 20;
        Assert.Equal("jp2h", System.Text.Encoding.ASCII.GetString(jp2, h + 4, 4));
        // ihdr: 22 bytes — HEIGHT 24, WIDTH 40, NC, BPC 7 (8-bit unsigned), C 7, UnkC 0, IPR 0.
        Assert.Equal("\0\0\0\u0016ihdr", System.Text.Encoding.Latin1.GetString(jp2, h + 8, 8));
        Assert.Equal(new byte[] { 0, 0, 0, 24, 0, 0, 0, 40, 0, (byte)components, 7, 7, 0, 0 }, jp2.Skip(h + 16).Take(14).ToArray());
        // colr: METH 1, PREC 0, APPROX 0, EnumCS.
        Assert.Equal("\0\0\0\u000Fcolr", System.Text.Encoding.Latin1.GetString(jp2, h + 30, 8));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 0, 0, (byte)enumCs }, jp2.Skip(h + 38).Take(7).ToArray());
        Assert.Equal("jp2c", System.Text.Encoding.ASCII.GetString(jp2, h + 49, 4));
    }

    /// <summary>
    /// The last component as opacity: the colr box names the colour channels' space and a cdef
    /// (Table I.16) types each colour channel 0 with its colour's number and the last 1 for the
    /// whole image — for two channels: N 2, then (0, 0, 1) and (1, 1, 0). The description reader
    /// sees a declared opacity and one fewer colour channel, and the dictionary rule asks for
    /// <c>/SMaskInData 1</c>.
    /// </summary>
    [Theory]
    [InlineData(2, 17)]
    [InlineData(4, 16)]
    [InlineData(5, 12)]
    public void WrapAsJp2_WithAnOpacityChannel_WritesTheChannelDefinition(int components, int enumCs)
    {
        byte[] codestream = Codestream(components);
        byte[] jp2 = JpxEncoder.WrapAsJp2(codestream, lastChannelIsOpacity: true);
        Assert.Equal(codestream.Length + JpxEncoder.ContainerBytes(components - 1) + JpxEncoder.CdefBoxBytes(components), jp2.Length);

        int colr = IndexOf(jp2, new[] { (byte)'c', (byte)'o', (byte)'l', (byte)'r' });
        Assert.Equal((byte)enumCs, jp2[colr + 10]);
        int cdef = IndexOf(jp2, new[] { (byte)'c', (byte)'d', (byte)'e', (byte)'f' });
        Assert.True(cdef > colr, "no cdef box after the colr");
        var expected = new List<byte> { 0, 0, 0, (byte)(8 + 2 + 6 * components), (byte)'c', (byte)'d', (byte)'e', (byte)'f', 0, (byte)components };
        for (int c = 0; c < components - 1; c++) expected.AddRange(new byte[] { 0, (byte)c, 0, 0, 0, (byte)(c + 1) });
        expected.AddRange(new byte[] { 0, (byte)(components - 1), 0, 1, 0, 0 });
        Assert.Equal(expected.ToArray(), jp2.Skip(cdef - 4).Take(expected.Count).ToArray());

        Assert.True(Jp2ColourDescription.TryRead(jp2, out Jp2ColourDescription header, out string reason), reason);
        Assert.True(header.DeclaresOpacity());
        Assert.Equal(components - 1, header.ColourChannelCount());
        Assert.True(JpxHeader.TryReadColourComponents(jp2, out int colour));
        Assert.Equal(components - 1, colour);
        Assert.True(JpxImageDictionary.TryDescribe(jp2, out _, out _, out _, out string colorSpace, out int smaskInData, out string why), why);
        Assert.Equal(1, smaskInData);
        Assert.Equal(components == 2 ? "DeviceGray" : components == 4 ? "DeviceRGB" : "DeviceCMYK", colorSpace);

        // The control: the same codestream without the flag declares nothing, and three colour channels plus opacity is no space a JP2 names.
        Assert.True(Jp2ColourDescription.TryRead(JpxEncoder.WrapAsJp2(Codestream(components == 2 ? 1 : components - 1)), out header, out reason), reason);
        Assert.False(header.DeclaresOpacity());
        Assert.Contains("3 components, the last opacity", Assert.Throws<ArgumentException>(() => JpxEncoder.WrapAsJp2(Codestream(3), lastChannelIsOpacity: true)).Message, StringComparison.Ordinal);
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

    /// <summary>T.800 Tables I.10 and I.16 describe unsigned channels; a signed codestream is refused the container and stays a raw codestream.</summary>
    [Fact]
    public void WrapAsJp2_RefusesSignedComponents_WithTheReason()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(16, 12, 1, 5);
        for (int i = 0; i < planes[0].Length; i++) planes[0][i] -= 128;
        byte[] signedCodestream = JpxEncoder.EncodeLossless(planes, 16, 12, 8, true, new JpxEncodeOptions { Levels = 1 });
        var refusal = Assert.Throws<ArgumentException>(() => JpxEncoder.WrapAsJp2(signedCodestream));
        Assert.Contains("signed components", refusal.Message, StringComparison.Ordinal);
        Assert.True(JpxDecoder.TryDecode(signedCodestream, 0, out JpxDecodeResult decoded, out string reason), reason);   // the raw codestream is fine
        Assert.Equal(planes[0], decoded.Image!.Samples[0]);
        // The control: the same samples unsigned take the container.
        for (int i = 0; i < planes[0].Length; i++) planes[0][i] += 128;
        Assert.True(JpxHeader.IsJp2Container(JpxEncoder.WrapAsJp2(JpxEncoder.EncodeLossless(planes, 16, 12, 8, false, new JpxEncodeOptions { Levels = 1 }))));
    }

    [Fact]
    public void WrapAsJp2_RefusesWhatItCannotDescribe()
    {
        Assert.Contains("SOC and SIZ", Assert.Throws<ArgumentException>(() => JpxEncoder.WrapAsJp2(new byte[] { 1, 2, 3 })).Message, StringComparison.Ordinal);
        Assert.Contains("2 components", Assert.Throws<ArgumentException>(() => JpxEncoder.WrapAsJp2(Codestream(2))).Message, StringComparison.Ordinal);
        Assert.True(JpxHeader.IsJp2Container(JpxEncoder.WrapAsJp2(Codestream(1))));   // the control
    }
}
