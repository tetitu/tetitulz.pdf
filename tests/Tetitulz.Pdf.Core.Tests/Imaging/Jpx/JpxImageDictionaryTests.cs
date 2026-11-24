// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The dictionary a JPEG 2000 file embedded as it is wants (ISO 32000-2 §7.4.9, Table 87): the
/// <c>/SMaskInData</c> value the file's opacity declaration maps to, the colour box a reader
/// prefers deciding <c>/ColorSpace</c>, the five depths <c>/BitsPerComponent</c> may state — and an
/// Indexed dictionary space reading a codestream's samples as its indices, verbatim.
/// </summary>
public sealed class JpxImageDictionaryTests
{
    private static JpxLosslessFixtures.Fixture GrayAlpha => JpxLosslessFixtures.GrayAlpha40x24_OneLevel;
    private static JpxLosslessFixtures.Fixture Rgba => JpxLosslessFixtures.Rgba40x24_Mct_OneLevel;
    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;

    // ── row 6: the opacity mode ─────────────────────────────────────────────

    /// <summary>
    /// What the key can say, and 0 for what it cannot: one straight whole-image channel is 1,
    /// a premultiplied one 2; an opacity channel for one colour, or two channels, is neither —
    /// the header still declares opacity (the inspector's "has alpha"), the key stays out.
    /// </summary>
    [Fact]
    public void OpacityMode_IsWhatSMaskInDataCanSay_AndZeroForWhatItCannot()
    {
        Assert.Equal((1, true), ModeOf(Jp2(GrayAlpha, H.ColrEnumerated(JpxEmbedFixtures.GreyscaleEnumCs), H.Cdef((0, 0, 1), (1, 1, 0)))));
        Assert.Equal((2, true), ModeOf(Jp2(GrayAlpha, H.ColrEnumerated(JpxEmbedFixtures.GreyscaleEnumCs), H.Cdef((0, 0, 1), (1, 2, 0)))));
        Assert.Equal((0, true), ModeOf(Jp2(Rgba, H.ColrEnumerated(JpxEmbedFixtures.SRgbEnumCs), H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 1)))));
        Assert.Equal((0, true), ModeOf(Jp2(Rgba, H.ColrEnumerated(JpxEmbedFixtures.SRgbEnumCs), H.Cdef((0, 0, 1), (1, 0, 2), (2, 1, 0), (3, 1, 0)))));
        Assert.Equal((0, false), ModeOf(Jp2(Rgb, H.ColrEnumerated(JpxEmbedFixtures.SRgbEnumCs))));
    }

    private static (int Mode, bool Declares) ModeOf(byte[] jp2)
    {
        Assert.True(Jp2ColourDescription.TryRead(jp2, out Jp2ColourDescription header, out string reason), reason);
        return (header.OpacityMode(), header.DeclaresOpacity());
    }

    /// <summary>The embedders' rule writes the mode the file declares: 2 for a premultiplied channel, none for a per-colour one, 1 for the control.</summary>
    [Fact]
    public void TryDescribe_WritesTheOpacityModeTheFileDeclares()
    {
        Assert.True(JpxImageDictionary.TryDescribe(JpxEmbedFixtures.GreyWithPremultipliedOpacityJp2(), out _, out _, out _, out string cs, out int mode, out string why), why);
        Assert.Equal(("DeviceGray", 2), (cs, mode));

        Assert.True(JpxImageDictionary.TryDescribe(JpxEmbedFixtures.RgbWithPerColourOpacityJp2(), out _, out _, out _, out cs, out mode, out why), why);
        Assert.Equal(("DeviceRGB", 0), (cs, mode));

        Assert.True(JpxImageDictionary.TryDescribe(JpxEmbedFixtures.GreyWithOpacityJp2(), out _, out _, out _, out cs, out mode, out why), why);
        Assert.Equal(("DeviceGray", 1), (cs, mode));
    }

    // ── row 16: which colr box decides /ColorSpace ──────────────────────────

    /// <summary>
    /// The colour box a reader prefers — highest PREC — is the one <c>/ColorSpace</c> may
    /// name: sRGB under an ICC box of higher precedence leaves the key out (naming sRGB would
    /// override the profile in every viewer); the same two boxes with the precedences swapped,
    /// or equal (file order, T.800 I.5.3.3), name DeviceRGB.
    /// </summary>
    [Fact]
    public void TryDescribe_LeavesTheColourSpaceToTheFile_WhenTheRankedFirstColrBoxIsNotADeviceSpace()
    {
        byte[] profile = H.IccProfile("RGB ");
        byte[] iccPreferred = Jp2(Rgb, H.ColrEnumerated(JpxEmbedFixtures.SRgbEnumCs, precedence: 0), H.ColrIcc(profile, precedence: 1));
        Assert.True(JpxImageDictionary.TryDescribe(iccPreferred, out _, out _, out _, out string cs, out _, out string why), why);
        Assert.Null(cs);

        byte[] srgbPreferred = Jp2(Rgb, H.ColrEnumerated(JpxEmbedFixtures.SRgbEnumCs, precedence: 1), H.ColrIcc(profile, precedence: 0));
        Assert.True(JpxImageDictionary.TryDescribe(srgbPreferred, out _, out _, out _, out cs, out _, out why), why);
        Assert.Equal("DeviceRGB", cs);

        byte[] fileOrder = Jp2(Rgb, H.ColrEnumerated(JpxEmbedFixtures.SRgbEnumCs), H.ColrIcc(profile));
        Assert.True(JpxImageDictionary.TryDescribe(fileOrder, out _, out _, out _, out cs, out _, out why), why);
        Assert.Equal("DeviceRGB", cs);
    }

    // ── row 31: the depths the key may state ────────────────────────────────

    /// <summary>Table 87: 1, 2, 4, 8 and 16 are stated; any other depth leaves the optional key out and the codestream's precision governs.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(7, 8)]
    [InlineData(15, 16)]
    [InlineData(5, 0)]
    [InlineData(11, 0)]
    [InlineData(9, 0)]
    public void TryDescribe_StatesBitsPerComponentOnlyForTheDepthsTable87Allows(int ssiz, int expectedKey)
    {
        byte[] codestream = JpxCodestreams.Concat(JpxCodestreams.Soc,
            JpxCodestreams.Siz(8, 8, 0, 0, 8, 8, 0, 0, new[] { (ssiz, 1, 1) }), JpxCodestreams.Eoc);
        Assert.True(JpxImageDictionary.TryDescribe(codestream, out int w, out int h, out int bpc, out string cs, out _, out string why), why);
        Assert.Equal((8, 8, expectedKey, "DeviceGray"), (w, h, bpc, cs));
    }

    // ── row 4: an Indexed space reads the indices verbatim ──────────────────

    /// <summary>
    /// A 4-bit codestream under the dictionary's Indexed space: the samples are the indices, as
    /// stored — index 1 stays 1. The control reads the same codestream as grey, where 1 is
    /// narrowed to 17.
    /// </summary>
    [Fact]
    public void TryDecode_AnIndexedColourSpace_ReadsAFourBitCodestreamsIndicesVerbatim()
    {
        const int size = 8;
        var indices = new byte[size * size];
        for (int i = 0; i < indices.Length; i++) indices[i] = (byte)((i % size + i / size) & 15);
        byte[] j2k = JpxImageEncoding.Encode(indices, size, size, 1, 4,
            new JpxEncodingOptions { Lossless = true, DecompositionLevels = 1, Jp2Container = false });

        Assert.True(JpxPdfImage.TryDecode(j2k, new Jp2PdfImageFacts(1, true, 0, false), null, 0, out JpxPdfImage indexed, out string reason), reason);
        Assert.True(indexed.Plan.IndicesVerbatim);
        Assert.Equal(indices, indexed.Colour);

        Assert.True(JpxPdfImage.TryDecode(j2k, new Jp2PdfImageFacts(1, false, 0, false), null, 0, out JpxPdfImage grey, out reason), reason);
        Assert.False(grey.Plan.IndicesVerbatim);
        Assert.Equal(Array.ConvertAll(indices, v => (byte)(v * 17)), grey.Colour);
    }

    private static byte[] Jp2(JpxLosslessFixtures.Fixture f, params byte[][] headerBoxes)
    {
        var boxes = new byte[headerBoxes.Length + 1][];
        boxes[0] = H.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7);
        Array.Copy(headerBoxes, 0, boxes, 1, headerBoxes.Length);
        return H.Jp2File(f.Codestream, boxes);
    }
}
