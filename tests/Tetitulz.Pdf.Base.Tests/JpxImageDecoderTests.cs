// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// <see cref="JpxImageDecoder"/>'s in-house sample path and its diagnostic slot. The framing and
/// JP2-header tests that were here moved to Core's <c>JpxHeaderTests</c> with the reader itself
/// (Wave 15a); the consumers that see pixels are in <c>JpxConsumerRenderTests</c>.
/// </summary>
public sealed class JpxImageDecoderTests
{
    [Fact]
    public void TryDecodeSamples_NullOrTinyInput_DeclinesWithAReasonAndNoImage()
    {
        // A false return must leave nothing half-filled for the caller to misread, and the slot
        // must say why.
        Assert.False(JpxImageDecoder.TryDecodeSamples(null, null, null, out JpxPdfImage image, out string reason));
        Assert.Null(image);
        Assert.Contains("no JPEG 2000 data", reason, StringComparison.Ordinal);
        Assert.Equal(reason, JpxImageDecoder.LastDecodeError);

        Assert.False(JpxImageDecoder.TryDecodeSamples(new byte[] { 0xFF }, null, null, out image, out reason));
        Assert.Null(image);
        Assert.Contains("no JPEG 2000 data", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryDecodeSamples_MalformedCodestream_DeclinesWithTheParsersReason()
    {
        // Garbage must not throw out of a render, and the refusal must be readable.
        byte[] garbage = new byte[128];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = (byte)(i * 31 + 7);

        Assert.False(JpxImageDecoder.TryDecodeSamples(garbage, null, null, out JpxPdfImage image, out string reason));
        Assert.Null(image);
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Equal(reason, JpxImageDecoder.LastDecodeError);
    }

    /// <summary>
    /// The slot is reset at the start of every decode: a decode that succeeds after one that
    /// declined reads null, and one that declines after a success reads its own reason.
    /// </summary>
    [Fact]
    public void LastDecodeError_IsResetAtTheStartOfEveryDecode()
    {
        byte[] garbage = { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x00, 0x00, 0x00 };
        Assert.False(JpxImageDecoder.TryDecodeSamples(garbage, null, null, out _, out _));
        Assert.NotNull(JpxImageDecoder.LastDecodeError);

        JpxLosslessFixtures.Fixture fixture = JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
        Assert.True(JpxImageDecoder.TryDecodeSamples(fixture.Codestream, null, null, out JpxPdfImage image, out string reason), reason);
        Assert.Null(JpxImageDecoder.LastDecodeError);
        Assert.Equal((fixture.Width, fixture.Height, 3, JpxColourModel.Rgb), (image.Width, image.Height, image.ColourComponents, image.ColourModel));

        Assert.False(JpxImageDecoder.TryDecodeSamples(garbage, null, null, out _, out reason));
        Assert.Equal(reason, JpxImageDecoder.LastDecodeError);

        // The bitmap entry point resets it too: a decode that succeeds after one that declined
        // reads null, whatever the plugin host holds.
        Assert.Null(JpxImageDecoder.DecodeToBitmap(garbage, null, null, SkiaSharp.SKAlphaType.Premul, out _));
        Assert.NotNull(JpxImageDecoder.LastDecodeError);
        using SkiaSharp.SKBitmap bitmap = JpxImageDecoder.DecodeToBitmap(fixture.Codestream, null, null, SkiaSharp.SKAlphaType.Premul, out JpxDecodeOutcome outcome);
        Assert.NotNull(bitmap);
        Assert.True(outcome.FromInHouseDecoder);
        Assert.Null(JpxImageDecoder.LastDecodeError);
    }
}
