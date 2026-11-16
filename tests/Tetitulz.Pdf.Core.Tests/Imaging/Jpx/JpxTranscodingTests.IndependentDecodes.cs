// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

public sealed partial class JpxTranscodingTests
{
    /// <summary>
    /// The independent decoder decodes the HT transcodes as the originals, sample for sample — an independent reader of what the
    /// transcoder wrote — in the form it reads: layers not kept, so no block has placeholder passes.
    /// </summary>
    [Fact]
    public void AnIndependentDecoder_DecodesTheHtTranscodes()
    {
        foreach (JpxLosslessFixtures.Fixture f in JpxLosslessFixtures.All)
        {
            Assert.True(JpxTranscoding.TryToHighThroughput(f.Codestream, keepQualityLayers: false, out byte[] ht, out string reason), reason);
            IndependentJpxDecode.Decoded theirs = IndependentJpxDecode.Decode(ht, ".j2k");
            IndependentJpxDecode.Decoded original = IndependentJpxDecode.Decode(f.Codestream, ".j2k");
            Assert.Equal(original.Planes.Length, theirs.Planes.Length);
            for (int c = 0; c < original.Planes.Length; c++) Assert.Equal(original.Planes[c], theirs.Planes[c]);
        }
    }

    /// <summary>
    /// §C.1.11's last clause, by an independent decoder: the round trip through HT, cut to its first L layers, decodes as the
    /// original cut to L — for every L of the three-layer fixtures.
    /// </summary>
    [Fact]
    public void TheRoundTrip_CutToFewerLayers_DecodesAsTheOriginalCutSo()
    {
        foreach (JpxLosslessFixtures.Fixture f in new[] { JpxLosslessFixtures.Gray40x24_Blocks16_Precincts32_ThreeLayers, JpxLosslessFixtures.Gray40x24_TwoLevels_Rpcl_ThreeLayers })
        {
            Assert.True(JpxTranscoding.TryToHighThroughput(f.Codestream, out byte[] ht, out string reason), reason);
            Assert.True(JpxTranscoding.TryToPart1(ht, out byte[] back, out reason), reason);
            for (int layers = 1; layers <= 3; layers++)
                Assert.Equal(IndependentJpxDecode.Decode(f.Codestream, ".j2k", layers: layers).Planes[0], IndependentJpxDecode.Decode(back, ".j2k", layers: layers).Planes[0]);
        }
    }
}
