// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging.Jpx;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxEncoderJp2Tests
{
    [Fact]
    public void WrapAsJp2_DecodesThroughAnIndependentDecoder_AsTheFileFormat()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(40, 24, 3, 12);
        byte[] jp2 = JpxEncoder.WrapAsJp2(JpxEncoder.EncodeLossless(planes, 40, 24, 8, false, new JpxEncodeOptions { Levels = 2 }));
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(jp2, ".jp2");
        Assert.Equal("RGB", decoded.Mode);
        for (int c = 0; c < 3; c++) Assert.Equal(planes[c], decoded.Planes[c]);
    }
}
