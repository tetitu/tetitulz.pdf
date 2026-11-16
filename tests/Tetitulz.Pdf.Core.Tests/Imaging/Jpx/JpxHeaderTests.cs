// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// Tests for <see cref="JpxHeader"/>, the one reader of what a JPEG 2000 image says about itself:
/// its framing, its declared grid, each component's precision, and its colour-channel count.
/// </summary>
/// <remarks>
/// These were two suites until Wave 15a — Base's <c>JpxImageDecoderTests</c> (framing and colour)
/// and the plugin's <c>JpxCodestreamHeaderTests</c> (precision) — over two box walkers. The walkers
/// disagreed on a 64-bit box length past the end of the data; the tests at the end pin the
/// reconciled behaviour.
/// </remarks>
public sealed class JpxHeaderTests
{
    // ── framing ─────────────────────────────────────────────────────────────

    [Fact]
    public void IsJp2Container_RecognisesTheSignatureBox()
    {
        Assert.True(JpxHeader.IsJp2Container(CatJp2.Bytes));
        Assert.False(JpxHeader.IsJp2Container(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }));
    }

    [Fact]
    public void IsRawCodestream_RecognisesTheSocMarker()
    {
        Assert.True(JpxHeader.IsRawCodestream(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }));
        Assert.False(JpxHeader.IsRawCodestream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })); // JPEG
    }

    [Fact]
    public void IsJpx_AcceptsBothFramings_AndRejectsPngJpegAndNull()
    {
        Assert.True(JpxHeader.IsJpx(CatJp2.Bytes));
        Assert.True(JpxHeader.IsJpx(new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }));
        Assert.False(JpxHeader.IsJpx(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.False(JpxHeader.IsJpx(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }));
        Assert.False(JpxHeader.IsJpx(null));
    }

    // ── the declared grid ───────────────────────────────────────────────────

    [Fact]
    public void TryReadImageSize_ReadsTheCatJp2Container()
    {
        Assert.True(JpxHeader.TryReadImageSize(CatJp2.Bytes, out long w, out long h, out int comps));
        Assert.Equal(CatJp2.Width, w);
        Assert.Equal(CatJp2.Height, h);
        Assert.Equal(CatJp2.Components, comps);
    }

    /// <summary>
    /// The extent is <c>Xsiz - XOsiz</c> by <c>Ysiz - YOsiz</c>, not <c>Xsiz</c> by <c>Ysiz</c> — a
    /// non-zero image offset is what tells the two readings apart.
    /// </summary>
    [Fact]
    public void TryReadImageSize_SubtractsTheImageOffset()
    {
        Assert.True(JpxHeader.TryReadImageSize(SizCodestream(1000, 700, 100, 50, components: 3),
            out long w, out long h, out int comps));
        Assert.Equal(900, w);
        Assert.Equal(650, h);
        Assert.Equal(3, comps);
    }

    /// <summary>§A.5.1 requires <c>XOsiz &lt; Xsiz</c>; an offset at or past the extent is refused.</summary>
    [Fact]
    public void TryReadImageSize_RefusesAnOffsetAtOrPastTheExtent()
    {
        Assert.False(JpxHeader.TryReadImageSize(SizCodestream(100, 700, 100, 50, components: 3), out _, out _, out _));
        Assert.False(JpxHeader.TryReadImageSize(SizCodestream(1000, 50, 100, 50, components: 3), out _, out _, out _));
        // Acceptance control: one step inside the extent is a 1-sample-wide image.
        Assert.True(JpxHeader.TryReadImageSize(SizCodestream(101, 700, 100, 50, components: 3), out long w, out _, out _));
        Assert.Equal(1, w);
    }

    // ── component precision ─────────────────────────────────────────────────

    [Fact]
    public void ReadsPrecisionFromTheCatJp2Container()
    {
        Assert.True(JpxHeader.TryReadComponentPrecision(CatJp2.Bytes, out int[] precision, out bool anySigned));
        Assert.Equal(new[] { 8, 8, 8 }, precision);
        Assert.False(anySigned);
    }

    [Fact]
    public void ReadsPerComponentPrecisionAndTheSignBit()
    {
        // Component 0: signed, 12-bit (0x80 | 11). Component 1: unsigned, 8-bit.
        Assert.True(JpxHeader.TryReadComponentPrecision(Codestream(0x8B, 0x07), out int[] precision, out bool anySigned));
        Assert.Equal(new[] { 12, 8 }, precision);
        Assert.True(anySigned);
    }

    [Fact]
    public void ReadsARawCodestreamWithNoContainer()
    {
        Assert.True(JpxHeader.TryReadComponentPrecision(Codestream(0x0F), out int[] precision, out _));
        Assert.Equal(new[] { 16 }, precision);
    }

    [Fact]
    public void ReadsTheFullThirtyEightBitRange()
    {
        // Ssiz 0x25 is 37 + 1 = 38 bits, the largest ISO/IEC 15444-1 and ISO 32000-2 allow.
        Assert.True(JpxHeader.TryReadComponentPrecision(Codestream(0x25, 0x00), out int[] precision, out _));
        Assert.Equal(new[] { 38, 1 }, precision);
    }

    [Fact]
    public void FindsTheCodestreamBoxAfterAnotherTopLevelBox()
    {
        // A 'jp2h' box holding a decoy SOC/SIZ that would fool a byte scan, then the real one.
        var bytes = new List<byte>(Jp2Container(0x6A703268 /* jp2h */, Codestream(0x01)));  // decoy: 2-bit
        byte[] real = Codestream(0x07, 0x07, 0x07);
        int len = 8 + real.Length;
        bytes.AddRange(new[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len });
        bytes.AddRange(new byte[] { 0x6A, 0x70, 0x32, 0x63 });   // 'jp2c'
        bytes.AddRange(real);

        Assert.True(JpxHeader.TryReadComponentPrecision(bytes.ToArray(), out int[] precision, out _));
        Assert.Equal(new[] { 8, 8, 8 }, precision);
    }

    [Fact]
    public void MalformedPrecisionInput_ReturnsFalse()
    {
        // Truncated SIZ: the component triples are cut off after Csiz claims three of them.
        byte[] full = Codestream(0x07, 0x07, 0x07);
        byte[] truncated = new byte[full.Length - 4];
        Array.Copy(full, truncated, truncated.Length);

        Assert.False(JpxHeader.TryReadComponentPrecision(truncated, out _, out _));
        Assert.False(JpxHeader.TryReadComponentPrecision(Array.Empty<byte>(), out _, out _));
        Assert.False(JpxHeader.TryReadComponentPrecision(null, out _, out _));
        Assert.False(JpxHeader.TryReadComponentPrecision(new byte[] { 0xFF, 0x4F, 0x00, 0x00 }, out _, out _)); // SOC, no SIZ
        // Acceptance control: the same stream untruncated reads.
        Assert.True(JpxHeader.TryReadComponentPrecision(full, out _, out _));
    }

    /// <summary>
    /// A container whose boxes never reach a codestream must terminate: a zero-length box claims
    /// the rest of the data, and a box list longer than the walk's bound is abandoned.
    /// </summary>
    [Fact]
    public void AdversarialBoxChain_TerminatesAndDeclines()
    {
        var zeroLength = new List<byte>(Jp2Headers.SignatureBox());
        zeroLength.AddRange(new byte[] { 0, 0, 0, 0, 0x78, 0x6D, 0x6C, 0x20 });   // LBox 0, 'xml '
        zeroLength.AddRange(new byte[64]);
        Assert.False(JpxHeader.TryReadComponentPrecision(zeroLength.ToArray(), out _, out _));

        var manyBoxes = new List<byte>(Jp2Headers.SignatureBox());
        for (int i = 0; i < 500; i++)
            manyBoxes.AddRange(new byte[] { 0, 0, 0, 8, 0x78, 0x6D, 0x6C, 0x20 });
        manyBoxes.AddRange(new byte[] { 0, 0, 0, 8, 0x6A, 0x70, 0x32, 0x63 });   // 'jp2c', far too late
        Assert.False(JpxHeader.TryReadComponentPrecision(manyBoxes.ToArray(), out _, out _));
    }

    // ── colour-channel count from the JP2 header ────────────────────────────

    [Fact]
    public void TryReadColourComponents_RealJp2File_ReportsThreeForSRgb()
    {
        // The cat's colr box is METH 1 / EnumCS 16 (sRGB).
        Assert.True(JpxHeader.TryReadColourComponents(CatJp2.Bytes, out int comps));
        Assert.Equal(3, comps);
    }

    [Theory]
    [InlineData(16, 3)] // sRGB
    [InlineData(18, 3)] // sYCC
    [InlineData(14, 3)] // CIELab
    [InlineData(17, 1)] // greyscale
    [InlineData(12, 4)] // CMYK
    public void TryReadColourComponents_EnumeratedColourSpace(int enumCs, int expected)
    {
        Assert.True(JpxHeader.TryReadColourComponents(Jp2Headers.WithEnumeratedColourSpace(enumCs), out int comps));
        Assert.Equal(expected, comps);
    }

    [Fact]
    public void TryReadColourComponents_UnknownEnumeratedSpace_ReportsNothing()
    {
        Assert.False(JpxHeader.TryReadColourComponents(Jp2Headers.WithEnumeratedColourSpace(9999), out int comps));
        Assert.Equal(0, comps);
    }

    [Fact]
    public void TryReadColourComponents_RestrictedIccProfile_ReadsItsColourSpaceSignature()
    {
        // METH 2: the profile follows, and its data-colour-space signature sits at profile
        // offset 16. Only the first 20 bytes of the profile matter here.
        var profile = new byte[20];
        profile[16] = (byte)'R'; profile[17] = (byte)'G'; profile[18] = (byte)'B'; profile[19] = (byte)' ';
        var content = new byte[3 + profile.Length];
        content[0] = 2; // METH = restricted ICC
        Array.Copy(profile, 0, content, 3, profile.Length);
        byte[] jp2 = Jp2Headers.Concat(Jp2Headers.SignatureBox(), Jp2Headers.Box("jp2h", Jp2Headers.Box("colr", content)));

        Assert.True(JpxHeader.TryReadColourComponents(jp2, out int comps));
        Assert.Equal(3, comps);
    }

    [Fact]
    public void TryReadColourComponents_NoColrBox_FallsBackToChannelDefinitions()
    {
        // cdef: 4 channels, three typed as colour (Typ 0) and one as opacity (Typ 1).
        byte[] cdef = BuildCdef((0, 0), (1, 0), (2, 0), (3, 1));
        byte[] jp2 = Jp2Headers.Concat(Jp2Headers.SignatureBox(), Jp2Headers.Box("jp2h", Jp2Headers.Box("cdef", cdef)));

        Assert.True(JpxHeader.TryReadColourComponents(jp2, out int comps));
        Assert.Equal(3, comps);
    }

    [Fact]
    public void TryReadColourComponents_RawCodestream_ReportsNothing()
    {
        // A bare codestream has no boxes to read; the caller must fall back to other evidence.
        Assert.False(JpxHeader.TryReadColourComponents(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 0, 0, 0 }, out int comps));
        Assert.Equal(0, comps);
    }

    [Fact]
    public void TryReadColourComponents_MalformedBoxLengths_DoesNotHang()
    {
        // Every one of these box-length forms could spin the walk forever if the reader advanced
        // by zero (or backwards): a length of 0 means "to the end", a length of 1 means a 64-bit
        // length follows, and a length below the header size is nonsense.
        byte[][] hostile =
        {
            Jp2Headers.Concat(Jp2Headers.SignatureBox(),
                Jp2Headers.Box("jp2h", new byte[] { 0, 0, 0, 0, (byte)'c', (byte)'o', (byte)'l', (byte)'r' })),
            Jp2Headers.Concat(Jp2Headers.SignatureBox(),
                Jp2Headers.Box("jp2h", new byte[] { 0, 0, 0, 1, (byte)'c', (byte)'o', (byte)'l', (byte)'r',
                                                    0, 0, 0, 0, 0, 0, 0, 0 })),
            Jp2Headers.Concat(Jp2Headers.SignatureBox(),
                Jp2Headers.Box("jp2h", new byte[] { 0, 0, 0, 3, (byte)'c', (byte)'o', (byte)'l', (byte)'r' })),
            Jp2Headers.Concat(Jp2Headers.SignatureBox(), new byte[] { 0, 0, 0, 40, (byte)'j', (byte)'p' }),
            Jp2Headers.Concat(Jp2Headers.SignatureBox(),
                Jp2Headers.Box("jp2h", Jp2Headers.Box("colr", new byte[] { 1, 0, 0 }))),
            Jp2Headers.SignatureBox(),
        };

        foreach (byte[] data in hostile)
        {
            Assert.False(JpxHeader.TryReadColourComponents(data, out int comps),
                "a malformed header must not yield a channel count");
            Assert.Equal(0, comps);
        }
    }

    [Fact]
    public void TryReadColourComponents_CdefWithAbsurdChannelCount_IsRefused()
    {
        // N = 60000 channels with no data behind it: reading it would walk far past the buffer.
        byte[] jp2 = Jp2Headers.Concat(Jp2Headers.SignatureBox(),
            Jp2Headers.Box("jp2h", Jp2Headers.Box("cdef", new byte[] { 0xEA, 0x60, 0, 0, 0, 0 })));

        Assert.False(JpxHeader.TryReadColourComponents(jp2, out int comps));
        Assert.Equal(0, comps);
    }

    [Fact]
    public void TryReadColourComponents_ManyBoxesBeforeTheHeader_TerminatesWithoutFinding()
    {
        // 200 padding boxes ahead of jp2h — past the bounded walk, so the header is reported as
        // unreadable rather than the loop running on.
        var parts = new List<byte[]> { Jp2Headers.SignatureBox() };
        for (int i = 0; i < 200; i++) parts.Add(Jp2Headers.Box("free", new byte[4]));
        parts.Add(Jp2Headers.Box("jp2h", Jp2Headers.Box("colr", new byte[] { 1, 0, 0, 0, 0, 0, 16 })));

        Assert.False(JpxHeader.TryReadColourComponents(Jp2Headers.Concat(parts.ToArray()), out int comps));
        Assert.Equal(0, comps);
    }

    // ── the 64-bit box length: the one place the two old walkers disagreed ─────

    /// <summary>
    /// A <c>jp2c</c> box whose XLBox claims more than the data holds — a truncated file — is read
    /// up to the end of the data. The plugin's old walker refused any XLBox above 4 GiB and so
    /// could not read this file's precision at all; Base's clamped. The reconciled reader clamps.
    /// </summary>
    [Fact]
    public void ASixtyFourBitLengthPastTheData_IsClampedNotRefused()
    {
        byte[] codestream = Codestream(0x07, 0x07, 0x07);
        var bytes = new List<byte>(Jp2Headers.SignatureBox());
        bytes.AddRange(new byte[] { 0, 0, 0, 1, 0x6A, 0x70, 0x32, 0x63 });        // LBox 1, 'jp2c'
        bytes.AddRange(new byte[] { 0, 0, 0x01, 0, 0, 0, 0, 0 });                 // XLBox = 2^40
        bytes.AddRange(codestream);

        Assert.True(JpxHeader.TryReadComponentPrecision(bytes.ToArray(), out int[] precision, out _));
        Assert.Equal(new[] { 8, 8, 8 }, precision);
    }

    /// <summary>
    /// An XLBox with its top bit set reads as a negative length and is refused, not wrapped into a
    /// position before the box. Control: the same box with an in-range XLBox reads.
    /// </summary>
    [Fact]
    public void ASixtyFourBitLengthWithTheTopBitSet_IsRefused()
    {
        byte[] codestream = Codestream(0x07);

        byte[] Container(byte[] xlBox)
        {
            var bytes = new List<byte>(Jp2Headers.SignatureBox());
            bytes.AddRange(new byte[] { 0, 0, 0, 1, 0x6A, 0x70, 0x32, 0x63 });
            bytes.AddRange(xlBox);
            bytes.AddRange(codestream);
            return bytes.ToArray();
        }

        Assert.False(JpxHeader.TryReadComponentPrecision(
            Container(new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 0 }), out _, out _));
        long exact = 16 + codestream.Length;
        Assert.True(JpxHeader.TryReadComponentPrecision(
            Container(new byte[] { 0, 0, 0, 0, (byte)(exact >> 24), (byte)(exact >> 16), (byte)(exact >> 8), (byte)exact }),
            out _, out _));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A codestream head: SOC, SIZ, and one (Ssiz, XRsiz, YRsiz) triple per component. Ssiz is the
    /// sign bit in 0x80 plus (precision - 1), ISO/IEC 15444-1 Table A.11. The grid values are all 16.
    /// </summary>
    private static byte[] Codestream(params byte[] ssiz)
    {
        var bytes = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
        int lsiz = 38 + (3 * ssiz.Length);
        bytes.Add((byte)(lsiz >> 8)); bytes.Add((byte)lsiz);      // Lsiz
        bytes.AddRange(new byte[] { 0, 0 });                      // Rsiz
        for (int i = 0; i < 8; i++) bytes.AddRange(new byte[] { 0, 0, 0, 16 });  // grid + tiles
        bytes.Add((byte)(ssiz.Length >> 8)); bytes.Add((byte)ssiz.Length);       // Csiz
        foreach (byte s in ssiz) { bytes.Add(s); bytes.Add(1); bytes.Add(1); }
        return bytes.ToArray();
    }

    /// <summary>A codestream head with the given grid extent and image offset, 8-bit components.</summary>
    private static byte[] SizCodestream(uint xsiz, uint ysiz, uint xosiz, uint yosiz, int components)
    {
        var bytes = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
        int lsiz = 38 + (3 * components);
        bytes.Add((byte)(lsiz >> 8)); bytes.Add((byte)lsiz);
        bytes.AddRange(new byte[] { 0, 0 });
        foreach (uint v in new[] { xsiz, ysiz, xosiz, yosiz, 256u, 256u, 0u, 0u })   // then tile size + origin
            bytes.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        bytes.Add((byte)(components >> 8)); bytes.Add((byte)components);
        for (int c = 0; c < components; c++) { bytes.Add(0x07); bytes.Add(1); bytes.Add(1); }
        return bytes.ToArray();
    }

    /// <summary>Wraps a payload in a JP2 signature box plus one box of the given type.</summary>
    private static byte[] Jp2Container(uint boxType, byte[] payload)
    {
        var bytes = new List<byte>(Jp2Headers.SignatureBox());
        int len = 8 + payload.Length;
        bytes.AddRange(new[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len });
        bytes.AddRange(new[] { (byte)(boxType >> 24), (byte)(boxType >> 16), (byte)(boxType >> 8), (byte)boxType });
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] BuildCdef(params (int channel, int type)[] entries)
    {
        var b = new byte[2 + entries.Length * 6];
        b[0] = (byte)(entries.Length >> 8);
        b[1] = (byte)entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            int o = 2 + i * 6;
            b[o]     = (byte)(entries[i].channel >> 8); b[o + 1] = (byte)entries[i].channel;
            b[o + 2] = (byte)(entries[i].type >> 8);    b[o + 3] = (byte)entries[i].type;
            b[o + 4] = 0; b[o + 5] = 0; // Asoc
        }
        return b;
    }
}
