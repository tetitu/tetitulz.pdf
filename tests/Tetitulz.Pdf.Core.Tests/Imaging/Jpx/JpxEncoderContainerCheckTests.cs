// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The encoder's files under an independent JP2 Part 1 validator (<see cref="IndependentJp2Check"/>): every Part 1 shape the encoder writes is valid; the CMYK file is a JPX by the standard
/// (EnumCS 12 is T.801's, so it carries the 'jpx ' brand, 'jp2 ' and 'jpxb' compatibility and a
/// Reader Requirements box), which a Part 1 validator declines on the brand and nothing else.
/// </summary>
public sealed class JpxEncoderContainerCheckTests
{
    [Theory]
    [InlineData("grey")]
    [InlineData("rgb")]
    [InlineData("rgba")]
    [InlineData("grey+alpha")]
    [InlineData("12-bit")]
    [InlineData("16-bit")]
    [InlineData("1-bit")]
    [InlineData("20-bit planes")]
    [InlineData("0 levels")]
    [InlineData("1x1")]
    [InlineData("three layers")]
    [InlineData("size target")]
    public void EveryPartOneShape_IsAValidJp2(string shape)
    {
        IndependentJp2Check.Verdict verdict = IndependentJp2Check.Validate(File(shape), "jp2");
        Assert.True(verdict.IsValid, $"{shape}: the validator failed " + string.Join(", ", verdict.FailedTests));
    }

    [Fact]
    public void ARawCodestream_IsAValidCodestream()
    {
        byte[] j2c = JpxImageEncoding.Encode(Samples(16, 12, 3), 16, 12, 3, 8, new JpxEncodingOptions { Jp2Container = false });
        IndependentJp2Check.Verdict verdict = IndependentJp2Check.Validate(j2c, "j2c");
        Assert.True(verdict.IsValid, "the validator failed " + string.Join(", ", verdict.FailedTests));
    }

    /// <summary>
    /// A Part 1 validator declines the CMYK JPX on exactly its two Part 2 facts — the 'jpx '
    /// brand and EnumCS 12, both T.801's — and on nothing structural: every other box it reads
    /// is valid. (The validator reads JP2 alone; it has no JPX mode to accept the file under.)
    /// </summary>
    [Fact]
    public void TheCmykJpx_IsDeclinedByThePartOneValidator_OnItsPartTwoBrandAndColourSpaceAlone()
    {
        byte[] jpx = JpxImageEncoding.Encode(Samples(16, 12, 4), 16, 12, 4, 8, new JpxEncodingOptions());
        IndependentJp2Check.Verdict verdict = IndependentJp2Check.Validate(jpx, "jp2");
        Assert.False(verdict.IsValid);
        Assert.Equal(new[] { "brandIsValid", "enumCSIsValid" }, verdict.FailedTests);
    }

    private static byte[] File(string shape)
    {
        switch (shape)
        {
            case "grey": return JpxImageEncoding.Encode(Samples(16, 12, 1), 16, 12, 1, 8, new JpxEncodingOptions());
            case "rgb": return JpxImageEncoding.Encode(Samples(16, 12, 3), 16, 12, 3, 8, new JpxEncodingOptions());
            case "rgba": return JpxImageEncoding.Encode(Samples(16, 12, 4), 16, 12, 4, 8, new JpxEncodingOptions { OpacityChannel = true });
            case "grey+alpha": return JpxImageEncoding.Encode(Samples(16, 12, 2), 16, 12, 2, 8, new JpxEncodingOptions { OpacityChannel = true });
            case "12-bit": return JpxImageEncoding.Encode(Samples(16, 12, 1, 12), 16, 12, 1, 12, new JpxEncodingOptions());
            case "16-bit": return JpxImageEncoding.Encode(Samples(16, 12, 3, 16), 16, 12, 3, 16, new JpxEncodingOptions());
            case "1-bit": return JpxImageEncoding.Encode(Samples(16, 12, 1, 1), 16, 12, 1, 1, new JpxEncodingOptions());
            case "20-bit planes":
            {
                var planes = new long[1][];
                planes[0] = new long[16 * 12];
                for (int i = 0; i < planes[0].Length; i++) planes[0][i] = (i * 7919L) % (1L << 20);
                return JpxImageEncoding.EncodePlanes(planes, 16, 12, 20, false, new JpxEncodingOptions());
            }
            case "0 levels": return JpxImageEncoding.Encode(Samples(16, 12, 3), 16, 12, 3, 8, new JpxEncodingOptions { DecompositionLevels = 0 });
            case "1x1": return JpxImageEncoding.Encode(new byte[] { 200 }, 1, 1, 1, 8, new JpxEncodingOptions());
            case "three layers": return JpxImageEncoding.Encode(Samples(16, 12, 3), 16, 12, 3, 8, new JpxEncodingOptions { Lossless = false, Layers = 3 });
            case "size target": return JpxImageEncoding.Encode(Samples(32, 32, 3), 32, 32, 3, 8, new JpxEncodingOptions { Lossless = false, TargetBytes = 600 });
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static byte[] Samples(int w, int h, int components, int depth = 8)
    {
        int bytesPer = depth > 8 ? 2 : 1;
        var s = new byte[w * h * components * bytesPer];
        long max = (1L << depth) - 1;
        for (int i = 0, at = 0; i < w * h * components; i++)
        {
            long v = (i * 37L + (i / components) % 13) % (max + 1);
            if (bytesPer == 2) s[at++] = (byte)(v >> 8);
            s[at++] = (byte)v;
        }
        return s;
    }
}

/// <summary>
/// <see cref="IndependentJp2Check"/> refuses what it should: a broken box length, a wrong signature,
/// a codestream cut short � each beside the valid file it was made from.
/// </summary>
public sealed class IndependentJp2CheckTests
{
    private static byte[] Valid() => JpxImageEncoding.Encode(new byte[16 * 12 * 3], 16, 12, 3, 8, new JpxEncodingOptions());

    [Fact]
    public void TheEncodersFile_IsValid()
    {
        var verdict = IndependentJp2Check.Validate(Valid(), "jp2");
        Assert.True(verdict.IsValid, string.Join(", ", verdict.FailedTests));
    }

    [Fact]
    public void AWrongSignature_FailsTheSignatureTest()
    {
        byte[] jp2 = Valid();
        jp2[11] ^= 0xFF;
        Assert.Equal(new[] { "signatureIsValid" }, IndependentJp2Check.Validate(jp2, "jp2").FailedTests);
    }

    [Fact]
    public void ABoxLengthPastTheFile_FailsTheBoxLengthTest()
    {
        byte[] jp2 = Valid();
        jp2[12] = 0x7F;   // the file type box's length, now far past the end
        Assert.Contains("boxLengthIsValid", IndependentJp2Check.Validate(jp2, "jp2").FailedTests);
    }

    [Fact]
    public void ACodestreamCutShort_FailsTheEocTest()
    {
        byte[] j2c = JpxImageEncoding.Encode(new byte[16 * 12], 16, 12, 1, 8, new JpxEncodingOptions { Jp2Container = false });
        Assert.True(IndependentJp2Check.Validate(j2c, "j2c").IsValid);
        byte[] cut = j2c.Take(j2c.Length - 2).ToArray();
        Assert.False(IndependentJp2Check.Validate(cut, "j2c").IsValid);
    }
}
