// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// Tier 1 against the two conformance codestreams whose coefficients are their samples: p0_11
/// (128x1, no decomposition level, segmentation symbols) and p0_13 (1x1, one level whose single
/// LL sample the inverse wavelet returns unchanged; 257 components with ROI scaling, COC and QCC).
/// Their class-1 reference decodes are the oracle, sample for sample.
/// </summary>
public sealed class JpxBlockDecoderCorpusTests
{
    private static string Input(string name) => Path.Combine(TestCorpus.JpxRoot!, "input", "conformance", name);
    private static string Reference(string name) => Path.Combine(TestCorpus.JpxRoot!, "baseline", "conformance", name);

    /// <summary>The samples of component <paramref name="c"/> of tile 0, decoded through tier 2 and tier 1 and DC-shifted.</summary>
    private static int[] Decode(byte[] data, int c, out List<string> problems)
    {
        problems = new List<string>();
        Assert.True(JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, new WorkBudget(PdfLimits.MaxJpxDecodeWork), out JpxTileDecode tile, out reason), reason);
        Assert.True(tile.Complete, tile.StopReason);
        JpxTileComponent component = tile.Layout.Components[c];
        int shift = cs.Size.Components[c].IsSigned ? 0 : 1 << (cs.Size.Components[c].Precision - 1);
        // Only resolution level 0 — the LL band, which is the tile-component itself when there is no
        // level, and its lone sample when the tile-component is a single sample.
        JpxBandCoefficients ll = Assert.Single(JpxTier1Decoder.Decode(cs, tile, c, component.Levels, new WorkBudget(PdfLimits.MaxJpxDecodeWork), problems));
        var samples = new int[ll.Values.Length];
        for (int i = 0; i < samples.Length; i++) samples[i] = JpxBlockDecoder.Truncate(ll.Values[i]) + shift;
        return samples;
    }

    private static int[] PgxSamples(string path, int count)
    {
        byte[] data = File.ReadAllBytes(path);
        Assert.True(PgxHeader.TryRead(data, out PgxHeader? h));
        Assert.Equal(count, h.Width * h.Height);
        Assert.True(h.Depth <= 8, "8-bit references only here");
        var samples = new int[count];
        for (int i = 0; i < count; i++) samples[i] = h.IsSigned ? (sbyte)data[h.DataOffset + i] : data[h.DataOffset + i];
        return samples;
    }

    [JpxCorpusFact]
    public void P0_11_DecodesToItsReference_WithSegmentationSymbolsIntact()
    {
        int[] samples = Decode(File.ReadAllBytes(Input("p0_11.j2k")), 0, out List<string> problems);
        Assert.Empty(problems);
        int[] reference = PgxSamples(Reference("c1p0_11_0.pgx"), 128);
        Assert.Equal(reference, samples);
        Assert.True(new HashSet<int>(reference).Count > 8, "a flat reference proves little");
    }

    /// <summary>
    /// p0_13's COD applies the reversible component transform, so components 0–2 of the reference
    /// are the inverse RCT (T.800 G.2: G = Y0 − ⌊(Y1 + Y2)/4⌋, R = Y2 + G, B = Y1 + G) of the three
    /// decoded planes; component 3, outside the transform, carries the ROI shift.
    /// </summary>
    [JpxCorpusFact]
    public void P0_13_DecodesEachReferencedComponent_ThroughRctAndRoiShift()
    {
        byte[] data = File.ReadAllBytes(Input("p0_13.j2k"));
        Assert.True(JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(257, cs.Size.Components.Length);
        Assert.True(cs.CodingFor(0).MultipleComponentTransform);
        Assert.True(cs.RoiShiftFor(0, 3) > 0, "component 3 carries no ROI shift");

        int y0 = Decode(data, 0, out List<string> problems)[0] - 128;
        Assert.Empty(problems);
        int y1 = Decode(data, 1, out problems)[0] - 128;
        Assert.Empty(problems);
        int y2 = Decode(data, 2, out problems)[0] - 128;
        Assert.Empty(problems);
        int g = y0 - (int)System.Math.Floor((y1 + y2) / 4.0);
        int r = y2 + g, b = y1 + g;
        Assert.Equal(PgxSamples(Reference("c1p0_13_0.pgx"), 1), new[] { r + 128 });
        Assert.Equal(PgxSamples(Reference("c1p0_13_1.pgx"), 1), new[] { g + 128 });
        Assert.Equal(PgxSamples(Reference("c1p0_13_2.pgx"), 1), new[] { b + 128 });

        int[] roi = Decode(data, 3, out problems);
        Assert.Empty(problems);
        Assert.Equal(PgxSamples(Reference("c1p0_13_3.pgx"), 1), roi);
    }
}
