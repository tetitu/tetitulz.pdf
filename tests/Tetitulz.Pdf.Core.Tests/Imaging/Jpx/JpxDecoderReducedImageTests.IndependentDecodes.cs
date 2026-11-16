// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxDecoderReducedImageTests
{
    /// <summary>
    /// An independent decoder's reduced decode of an RGB ramp two levels short — at 48x32,
    /// where the reduced size is a plain quotient: that decoder sizes a reduced image by its own formula
    /// and refuses the codec's ceilings ("broken data stream") when the two differ, and it refuses
    /// the tiled fixture with an image offset likewise; those are held to their component planes
    /// and geometry in the tests above. The even sizes put the boundary samples off the exact
    /// ramp, so the comparison here is to the independent decoder only.
    /// </summary>
    [Fact]
    public void RgbRampWithTheRct_ReducedTwoLevels_AgreesWithAnIndependentDecoder()
    {
        int[][] planes = { Ramp(48, 32, 2, 3, 5), Ramp(48, 32, 2, 2, 30), Ramp(48, 32, 2, 1, 60) };
        byte[] codestream = JpxEncoder.EncodeLossless(planes, 48, 32, 8, false, new JpxEncodeOptions { Levels = 3 });
        Assert.True(JpxDecoder.TryDecode(codestream, 2, out JpxDecodeResult result, out string reason), reason);
        IndependentJpxDecode.Decoded decoded = IndependentJpxDecode.Decode(codestream, ".j2k", reduce: 2);
        Assert.Equal((12, 8), (decoded.Width, decoded.Height));
        for (int c = 0; c < 3; c++) Assert.Equal(decoded.Planes[c], result.Image.Samples[c]);
    }
}
