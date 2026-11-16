// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.TestHelpers;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// The decoder's guards after the review of 2026-09-26 (15k Stage B): the packet sequencer charges
/// every scan, the budget is a share per byte of the file plus the declared reconstruction, a
/// decode's code-block problems are capped, and the syntax the parser used to pass silently —
/// duplicate packed-header indices, a non-last <c>Psot 0</c>, TNsot, a PLT in the main header —
/// is refused or reported. Every hostile fixture here is asserted on the budget's evidence, never
/// on a clock.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class JpxDecoderGuardTests
{
    private static JpxTileDecode Tier2(byte[] codestream, WorkBudget budget)
    {
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);
        Assert.True(JpxTier2Decoder.TryDecodeTile(cs, 0, budget, out JpxTileDecode tile, out reason), reason);
        return tile;
    }

    /// <summary>
    /// A 64x32 tile whose two 0-level components have 1-wide (PPx 0) and 1-high (PPy 0) precincts —
    /// PPx 0 is legal at r = 0 — so the position loops step by one sample, plus <paramref name="others"/>
    /// components sub-sampled 255x255 with one precinct each; RPCL.
    /// </summary>
    private static byte[] CollapsedStep(int others)
    {
        var comps = new (int, int, int)[2 + others];
        comps[0] = (0x07, 1, 1);
        comps[1] = (0x07, 1, 1);
        for (int i = 2; i < comps.Length; i++) comps[i] = (0x07, 255, 255);
        int packets = 64 + 32 + others;
        return J.Concat(J.Soc, J.Siz(64, 32, 0, 0, 64, 32, 0, 0, comps),
            J.Cod(order: 2, levels: 0, precincts: new byte[] { 0xF0 }),   // PPy 15, PPx 0
            J.Coc(1, levels: 0, precincts: new byte[] { 0x0F }),          // PPy 0, PPx 15
            J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), new byte[packets]), J.Eoc);
    }

    /// <summary>
    /// The review's row 2: the position-driven orders ticked once per grid position and then ran
    /// every component's work uncharged; with the gcd step at one sample a file of a few hundred
    /// bytes could make RPCL do billions of iterations for a million ticks. The charge is per
    /// component at each position now: adding components adds a tick per position per component,
    /// and a budget below that refuses.
    /// </summary>
    [Fact]
    public void PositionDrivenOrders_ChargeEveryComponentAtEveryPosition()
    {
        var few = new WorkBudget(1_000_000_000);
        Tier2(CollapsedStep(8), few);
        var many = new WorkBudget(1_000_000_000);
        Tier2(CollapsedStep(200), many);
        const long positions = 64 * 32;
        long added = many.Spent - few.Spent;
        Assert.True(added >= 192 * positions, $"200 components added {added} units to 8 components' {few.Spent}; {192 * positions} positions were scanned for them");
        Assert.Throws<WorkBudgetExceededException>(() => Tier2(CollapsedStep(200), new WorkBudget(few.Spent + 100 * positions)));
    }

    /// <summary>
    /// The review's row 3: LRCP and RLCP never ticked, and a POC restating the same volume in every
    /// tile-part scanned every (layer, resolution, component) again for nothing. Each scan is a
    /// tick now, so restating the volume N times costs N times the tuples.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void LayerAndResolutionOrders_ChargeEveryScan_SoARestatedVolumeCosts(int order)
    {
        static byte[] Restated(int times, int order)
        {
            var entries = new (int, int, int, int, int, int)[times];
            for (int i = 0; i < times; i++) entries[i] = (0, 0, 4, 2, 1, order);   // the whole tile, every time
            return J.Concat(J.Soc, J.Siz64(), J.Cod(order: order, layers: 4, levels: 1), J.QcdNone(levels: 1),
                J.TilePart(0, 0, J.Poc(false, entries), new byte[8]), J.Eoc);
        }
        var once = new WorkBudget(1_000_000);
        Tier2(Restated(1, order), once);
        var many = new WorkBudget(1_000_000);
        Tier2(Restated(201, order), many);
        Assert.True(many.Spent - once.Spent >= 200 * 8, $"200 restatements of a 4x2x1 volume added {many.Spent - once.Spent} units");
        Assert.Throws<WorkBudgetExceededException>(() => Tier2(Restated(201, order), new WorkBudget(once.Spent + 100 * 8)));
    }

    /// <summary>
    /// The budget a file gets: a floor, the reconstruction of the declared samples (bounded by the
    /// sample ceilings, so a 91-byte blank 4096x4096 image is still a legal decode), and
    /// <see cref="PdfLimits.MaxJpxDecodeWorkPerByte"/> units per byte for what a file can amplify —
    /// never above <see cref="PdfLimits.MaxJpxDecodeWork"/>. Read at run time: the collapsed-step
    /// file above is refused at the default share and decodes when the share is raised.
    /// </summary>
    [Fact]
    public void TheWorkAllowed_IsAFloorPlusTheReconstructionPlusAShareOfTheBytes_AndIsReadAtRunTime()
    {
        long floor = JpxDecoder.WorkFloor;
        Assert.Equal(floor, JpxDecoder.WorkAllowed(0, 0));
        Assert.Equal(floor + 16 * 1000, JpxDecoder.WorkAllowed(1000, 0));
        Assert.Equal(floor + 16 * 1000 + (4096L * 4096 * 3 / 4096) * JpxDecoder.ReconstructionUnitsPerBlock + JpxDecoder.ReconstructionUnitsPerBlock,
            JpxDecoder.WorkAllowed(1000, 4096L * 4096 * 3));
        Assert.Equal(PdfLimits.MaxJpxDecodeWork, JpxDecoder.WorkAllowed(long.MaxValue / 4, long.MaxValue / 4));
        try
        {
            PdfLimits.MaxJpxDecodeWorkPerByte = 3;
            Assert.Equal(floor + 3 * 1000, JpxDecoder.WorkAllowed(1000, 0));
            PdfLimits.MaxJpxDecodeWork = 100;
            Assert.Equal(100, JpxDecoder.WorkAllowed(1000, 0));
        }
        finally { PdfLimits.ResetToDefaults(); }

        byte[] hostile = CollapsedStep(200);
        Assert.False(JpxDecoder.TryDecode(hostile, 0, out _, out string reason));
        Assert.Contains("PdfLimits.MaxJpxDecodeWorkPerByte over its", reason, StringComparison.Ordinal);
        try
        {
            PdfLimits.MaxJpxDecodeWorkPerByte = 100_000;
            Assert.True(JpxDecoder.TryDecode(hostile, 0, out JpxDecodeResult decoded, out reason), reason);
            Assert.Equal(202, decoded.Image!.Components.Length);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>A codestream's QCD rewritten to no guard bits and an exponent of 1, so every band has no magnitude bit-planes and every block with passes is a problem.</summary>
    private static byte[] WithNoBitPlanes(byte[] codestream)
    {
        byte[] patched = (byte[])codestream.Clone();
        int qcd = -1;
        for (int i = 0; i + 1 < patched.Length; i++)
            if (patched[i] == 0xFF && patched[i + 1] == 0x5C) { qcd = i; break; }
        Assert.True(qcd >= 0, "no QCD");
        int length = (patched[qcd + 2] << 8) | patched[qcd + 3];
        patched[qcd + 4] &= 0x1F;                                    // guard bits 0, the style kept (none)
        for (int i = qcd + 5; i < qcd + 2 + length; i++) patched[i] = 1 << 3;   // ε 1 per band
        return patched;
    }

    /// <summary>
    /// The review's row 13: eight problems per tile-component by text, the rest counted — and, since
    /// a crafted file can have a problem in every block of every tile, a decode-wide ceiling
    /// (<see cref="PdfLimits.MaxJpxReportedProblems"/>, read at run time) with the rest counted once.
    /// </summary>
    [Fact]
    public void CodeBlockProblems_AreCappedPerTileComponent_AndPerDecode()
    {
        int[][] planes = JpxEncoderTestImages.Pattern(64, 64, 1, 13);
        byte[] sixtyFourBlocks = WithNoBitPlanes(JpxEncoder.EncodeLossless(planes, 64, 64, 8, false,
            new JpxEncodeOptions { Levels = 0, CodeBlockWidthExponent = 3, CodeBlockHeightExponent = 3 }));
        Assert.True(JpxDecoder.TryDecode(sixtyFourBlocks, 0, out JpxDecodeResult result, out string reason), reason);
        Assert.Equal(8, result.Deviations.Count(d => d.Contains(", code-block (", StringComparison.Ordinal)));
        Assert.Contains(result.Deviations, d => d.EndsWith("56 more code-blocks reported a problem", StringComparison.Ordinal));
        Assert.Contains(result.Deviations, d => d.Contains("has no magnitude bit-planes", StringComparison.Ordinal));
        Assert.All(result.Image!.Samples[0], v => Assert.Equal(128, v));   // nothing decoded, nothing invented: zero coefficients, level-shifted

        byte[] nineTiles = WithNoBitPlanes(JpxLosslessFixtures.Gray40x24_Tiles20x12_Offset3x2_OneLevel.Codestream);
        Assert.True(JpxDecoder.TryDecode(nineTiles, 0, out JpxDecodeResult uncapped, out reason), reason);
        int problems = uncapped.Deviations.Count(d => d.Contains(", code-block (", StringComparison.Ordinal));
        Assert.True(problems > 5, $"the fixture yields {problems} problems");
        try
        {
            PdfLimits.MaxJpxReportedProblems = 5;
            Assert.True(JpxDecoder.TryDecode(nineTiles, 0, out JpxDecodeResult capped, out reason), reason);
            Assert.Equal(5, capped.Deviations.Count(d => d.Contains(", code-block (", StringComparison.Ordinal)));
            Assert.Contains(capped.Deviations, d => d == $"{problems - 5} more code-block problem(s) across the tiles; PdfLimits.MaxJpxReportedProblems is 5");
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    private static byte[] Ppt(int index, params byte[] packed) => J.Segment(0xFF61, new[] { (byte)index }.Concat(packed).ToArray());
    private static byte[] Plt(int index, params byte[] lengths) => J.Segment(0xFF58, new[] { (byte)index }.Concat(lengths).ToArray());

    /// <summary>
    /// The review's row 10: §A.7.4 and §A.7.5 number a series once each; two PPTs sharing an index
    /// would have their packet headers concatenated in whatever order the sort left, so the
    /// codestream is refused with the index named; PLT lengths are advisory, so a duplicate there
    /// is a deviation. The control: distinct indices in reverse order are concatenated in order.
    /// </summary>
    [Fact]
    public void DuplicatePackedHeaderIndices_AreRefused_AndDuplicatePacketLengthIndicesReported()
    {
        byte[] twoPpt0 = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, J.Concat(Ppt(0, 0x00), Ppt(0, 0x00)), new byte[2]), J.Eoc);
        Assert.False(JpxCodestreamParser.TryParse(twoPpt0, out _, out string reason));
        Assert.Contains("two PPT marker segments carry the index 0", reason, StringComparison.Ordinal);

        byte[] reversed = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, J.Concat(Ppt(1, 0x00), Ppt(0, 0x00)), new byte[2]), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(reversed, out JpxCodestream cs, out reason), reason);
        Assert.Equal(2, cs.Tiles[0].Parts[0].PackedHeaders.Count);

        byte[] twoPlt0 = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, J.Concat(Plt(0, 0x05), Plt(0, 0x05)), new byte[5]), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(twoPlt0, out cs, out reason), reason);
        Assert.Contains(cs.Deviations, d => d.Contains("two PLT marker segments carry the index 0", StringComparison.Ordinal));
    }

    /// <summary>
    /// A tile-part's PLTs are put in index order once each. The sort was an insertion sort, and its
    /// index is one byte, so ten thousand segments at 255 followed by ten thousand at 0 cost a
    /// hundred million shifts; the lengths are read in index order, those sharing one in file order.
    /// </summary>
    [Fact]
    public void AdversarialPltSeriesInReverseIndexOrder_IsPlacedOnceASegment()
    {
        const int half = 10_000;
        static long LengthOf(int i) => i % 120 + 1;   // one 7-bit group; distinct within a run of 120
        var segments = new List<byte[]>();
        for (int i = 0; i < 2 * half; i++) segments.Add(Plt(i < half ? 255 : 0, (byte)LengthOf(i)));
        byte[] codestream = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, J.Concat(segments.ToArray()), new byte[5]), J.Eoc);

        JpxCodestreamParser.SortedSegmentPlacements = 0;
        Assert.True(JpxCodestreamParser.TryParse(codestream, out JpxCodestream cs, out string reason), reason);

        Assert.Equal(2L * half, JpxCodestreamParser.SortedSegmentPlacements);
        // Index 0's segments first, then 255's, each series in file order.
        long[] expected = Enumerable.Range(half, half).Concat(Enumerable.Range(0, half)).Select(LengthOf).ToArray();
        Assert.Equal(expected, cs.Tiles[0].Parts[0].PacketLengths);
    }

    /// <summary>
    /// The review's row 11: <c>Psot 0</c> ("to the EOC") on a tile-part that is not the last swallowed
    /// every later tile-part as packet bytes and lost their tiles silently. The range now ends at the
    /// next SOT, reported, and the later tile decodes; on the last tile-part 0 still means the EOC.
    /// </summary>
    [Fact]
    public void PsotZero_OnATilePartThatIsNotTheLast_EndsAtTheNextSot_AndIsReported()
    {
        byte[] siz = J.Siz(64, 64, 0, 0, 32, 64, 0, 0, new[] { (0x07, 1, 1) });   // two tiles side by side
        byte[] data = { 0, 0, 0, 0, 0 };
        byte[] notLast = J.Concat(J.Soc, siz, J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), data, psot: 0), J.TilePart(1, 0, Array.Empty<byte>(), data), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(notLast, out JpxCodestream cs, out string reason), reason);
        Assert.Equal(2, cs.Tiles.Count);
        Assert.Contains(cs.Deviations, d => d.Contains("declares Psot 0 ('to the EOC') but another SOT follows", StringComparison.Ordinal));
        Assert.Equal(data.Length, cs.Tiles[0].Parts[0].Data.Length);

        byte[] last = J.Concat(J.Soc, siz, J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), data), J.TilePart(1, 0, Array.Empty<byte>(), data, psot: 0), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(last, out cs, out reason), reason);
        Assert.Equal(2, cs.Tiles.Count);
        Assert.DoesNotContain(cs.Deviations, d => d.Contains("Psot 0", StringComparison.Ordinal));
    }

    /// <summary>The review's row 28: TNsot is 0 or the tile's exact count (Table A.6); a TPsot at or past it, or a count that changes between tile-parts, is a deviation, and a PLT in the main header (§A.7.3: tile-part headers only) is reported and ignored.</summary>
    [Fact]
    public void TNsot_AndAMainHeaderPlt_AreReported()
    {
        byte[] data = { 0, 0, 0, 0, 0 };
        byte[] pastTheCount = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), data, tnsot: 1), J.TilePart(0, 1, Array.Empty<byte>(), data, tnsot: 1), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(pastTheCount, out JpxCodestream cs, out string reason), reason);
        Assert.Contains(cs.Deviations, d => d.Contains("tile 0's tile-part 1 declares 1 tile-part(s) in all (TNsot), which it is not within", StringComparison.Ordinal));

        byte[] changing = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), data, tnsot: 2), J.TilePart(0, 1, Array.Empty<byte>(), data, tnsot: 3), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(changing, out cs, out reason), reason);
        Assert.Contains(cs.Deviations, d => d.Contains("declares 3 tile-part(s) in all (TNsot) where an earlier one declared 2", StringComparison.Ordinal));

        byte[] exact = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0),
            J.TilePart(0, 0, Array.Empty<byte>(), data, tnsot: 2), J.TilePart(0, 1, Array.Empty<byte>(), data, tnsot: 2), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(exact, out cs, out reason), reason);
        Assert.DoesNotContain(cs.Deviations, d => d.Contains("TNsot", StringComparison.Ordinal));

        byte[] mainPlt = J.Concat(J.Soc, J.Siz64(), J.Cod(levels: 0), J.QcdNone(levels: 0), Plt(0, 0x05),
            J.TilePart(0, 0, Array.Empty<byte>(), data), J.Eoc);
        Assert.True(JpxCodestreamParser.TryParse(mainPlt, out cs, out reason), reason);
        Assert.Contains(cs.Deviations, d => d.Contains("PLT marker segment at offset", StringComparison.Ordinal) && d.Contains("main header", StringComparison.Ordinal));
    }
}
