// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The committed HTJ2K codestream (<c>Fixtures/Htj2k</c>, the reference HT implementation's, BSD-2): a lossless decode checked
/// byte for byte against the reference decoder's, so the HT cleanup pass — contexts, predictors, the first
/// line-pair's shared MEL symbol, every stream's stuffing — is checked where the corpora are absent.
/// </summary>
public sealed class JpxHtFixtureTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Htj2k", "simple_enc_rev53_tall_narrow.j2c");

    /// <summary>The reference decoder's decode (an independent tool): SHA-256 of the samples interleaved R, G, B in raster order.</summary>
    private const string ReferenceSha256 = "d2d8c21e6f5dae52faa7fd1dfbf82005341aa9bc179c11d7ef73634e0685fc69";

    [Fact]
    public void TheFixtureDecodesAsTheReferenceDecodesIt()
    {
        Assert.Equal(ReferenceSha256, DecodedSha256(File.ReadAllBytes(Fixture), out int capabilities));
        Assert.NotEqual(-1, capabilities);
    }

    /// <summary>
    /// ITU-T T.814 §A.3.2's NOTE: an HTONLY codestream may set code-block style bits 6 and 7 together
    /// (Table A.4), and every block is still HT. The fixture is HTONLY (Ccap15 0x0005); its COD style,
    /// 0x40, rewritten to 0xF7 — bit 7 and the Part 1 bits Table A.4 says do not apply to an HT block,
    /// vertical causality left clear as it was — decodes to the same samples. (the reference decoder refuses
    /// this file: it reads bits 6 and 7 as MIXED whatever CAP says.)
    /// </summary>
    [Fact]
    public void AnHtOnlyCodestreamSettingBitsSixAndSeven_DecodesAsTheFixtureDoes()
    {
        byte[] data = File.ReadAllBytes(Fixture);
        int style = CodStyleOffset(data);
        Assert.Equal(0x40, data[style]);
        data[style] = 0xF7;

        Assert.Equal(ReferenceSha256, DecodedSha256(data, out int capabilities));
        Assert.Equal(0, capabilities & 0xC000);
    }

    private static string DecodedSha256(byte[] codestream, out int capabilities)
    {
        Assert.True(JpxDecoder.TryDecode(codestream, 0, out JpxDecodeResult result, out string reason), reason);
        capabilities = result.Codestream.HtCapabilities;

        int[][] planes = new int[3][];
        JpxRect extent = default;
        for (int c = 0; c < 3; c++) planes[c] = result.ComponentPlane(c, out extent);
        Assert.Equal((16L, 256L), (extent.Width, extent.Height));

        var interleaved = new byte[3 * planes[0].Length];
        for (int i = 0; i < planes[0].Length; i++)
            for (int c = 0; c < 3; c++) interleaved[3 * i + c] = checked((byte)planes[c][i]);
        return Convert.ToHexString(SHA256.HashData(interleaved)).ToLowerInvariant();
    }

    /// <summary>The main header's COD code-block style byte (T.800 Table A.12: Scod, SGcod's four bytes, then SPcod's levels, xcb, ycb, style).</summary>
    private static int CodStyleOffset(byte[] data)
    {
        for (int i = 2; i + 4 <= data.Length;)
        {
            int marker = (data[i] << 8) | data[i + 1];
            if (marker == 0xFF52) return i + 4 + 1 + 4 + 3;
            Assert.NotEqual(0xFF90, marker);
            i += 2 + ((data[i + 2] << 8) | data[i + 3]);
        }
        throw new InvalidDataException("the fixture has no COD");
    }
}
