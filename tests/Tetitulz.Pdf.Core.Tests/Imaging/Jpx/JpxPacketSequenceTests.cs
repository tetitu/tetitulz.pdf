// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using J = Tetitulz.Pdf.Core.Tests.Imaging.Jpx.JpxCodestreams;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxPacketSequence"/> against T.800 §B.12: each order's packets listed as (layer,
/// resolution, component, precinct), worked by hand from the loops the standard prints.
/// </summary>
public sealed class JpxPacketSequenceTests
{
    private static string Sequence(JpxTileLayout layout, List<JpxProgressionChange> changes, WorkBudget budget = null) =>
        string.Join(" ", JpxPacketSequence.Enumerate(layout, changes, budget ?? new WorkBudget(10_000))
            .Select(p => $"{p.Layer}{p.Resolution}{p.Component}{p.Precinct}"));

    /// <summary>Two components, one level, two layers, one precinct per level: LRCP and RLCP differ in their two outer loops.</summary>
    [Theory]
    [InlineData(0, "0000 0010 0100 0110 1000 1010 1100 1110")]
    [InlineData(1, "0000 0010 1000 1010 0100 0110 1100 1110")]
    public void LayerAndResolutionOrders_ListEveryPacketOnce(int order, string expected)
    {
        byte[] siz = J.Siz(64, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1), (0x07, 1, 1) });
        JpxTileLayout layout = JpxTileLayoutTests.Layout(siz, J.Cod(order: order, layers: 2), J.QcdNone());
        Assert.Equal(expected, Sequence(layout, null));
    }

    /// <summary>
    /// The position-driven orders on 64x64 with 32x32 precincts at resolution 1 (four of them) and
    /// one at resolution 0. RPCL takes resolution 0 first, then walks resolution 1's precincts in
    /// raster order; PCRL and CPRL walk positions, and at (32, 0) or (0, 32) resolution 0 — whose
    /// precinct spans 64 grid samples — has nothing, so only resolution 1's precinct is emitted.
    /// </summary>
    [Theory]
    [InlineData(2, "0000 0100 0101 0102 0103")]
    [InlineData(3, "0000 0100 0101 0102 0103")]
    [InlineData(4, "0000 0100 0101 0102 0103")]
    public void PositionOrders_AdmitAPrecinctWhereItsOriginLies(int order, string expected)
    {
        JpxTileLayout layout = JpxTileLayoutTests.Layout(J.Siz64(), J.Cod(order: order, precincts: new byte[] { 0x55, 0x55 }), J.QcdNone());
        var budget = new WorkBudget(1000);
        Assert.Equal(expected, Sequence(layout, null, budget));
        // Four grid positions per resolution level for RPCL, four in all for the others: each is one unit of work.
        Assert.Equal(order == 2 ? 8 : 4, budget.Spent);
    }

    /// <summary>
    /// With two components the interleaving differs: PCRL puts both components' packets at a
    /// position before moving on; CPRL finishes component 0's positions first. Component 1 is
    /// sub-sampled 2x2, so its 32x32 precincts at resolution 1 (a 32x32 level) cover the whole
    /// tile: one precinct, admitted only at the tile origin.
    /// </summary>
    [Theory]
    [InlineData(3, "0000 0100 0010 0110 0101 0102 0103")]
    [InlineData(4, "0000 0100 0101 0102 0103 0010 0110")]
    public void PositionOrders_InterleaveComponentsDifferently(int order, string expected)
    {
        byte[] siz = J.Siz(64, 64, 0, 0, 64, 64, 0, 0, new[] { (0x07, 1, 1), (0x07, 2, 2) });
        JpxTileLayout layout = JpxTileLayoutTests.Layout(siz, J.Cod(order: order, precincts: new byte[] { 0x55, 0x55 }), J.QcdNone());
        Assert.Equal(expected, Sequence(layout, null));
    }

    /// <summary>
    /// §B.12.1.3's second clause: a tile whose origin is off the precinct lattice — (40, 40) with
    /// 32x32 precincts — still emits its first row and column of precincts, at y = ty0 and x = tx0;
    /// and equation B-20 counts precincts from ⌊try0 / 2^PPy⌋ = 1, not from the grid's origin, so
    /// the rows at y = 40, 64 and 96 are precinct rows 0, 1 and 2. Nine precincts, raster order.
    /// </summary>
    [Fact]
    public void PositionOrders_EmitTheFirstRowAndColumn_WhenTheOriginIsOffTheLattice()
    {
        byte[] siz = J.Siz(104, 104, 40, 40, 104, 104, 0, 0, new[] { (0x07, 1, 1) });
        JpxTileLayout layout = JpxTileLayoutTests.Layout(siz, J.Cod(order: 2, levels: 0, precincts: new byte[] { 0x55 }), J.QcdNone(levels: 0));
        Assert.Equal(9L, layout.Components[0].Resolutions[0].PrecinctCount);
        Assert.Equal("0000 0001 0002 0003 0004 0005 0006 0007 0008", Sequence(layout, null));
    }

    /// <summary>
    /// Progression order volumes (§B.12.2): the first, RLCP over resolution 0 and layer 0, emits one
    /// packet; the second, LRCP over everything, skips what was sent — each precinct continues at
    /// its next layer — and emits the rest.
    /// </summary>
    [Fact]
    public void ProgressionChanges_NeverRepeatAPacket()
    {
        JpxTileLayout layout = JpxTileLayoutTests.Layout(J.Siz64(), J.Cod(layers: 2), J.QcdNone());
        var changes = new List<JpxProgressionChange>
        {
            new JpxProgressionChange(0, 0, 1, 1, 1, JpxProgressionOrder.Rlcp),
            new JpxProgressionChange(0, 0, 2, 2, 1, JpxProgressionOrder.Lrcp),
        };
        Assert.Equal("0000 0100 1000 1100", Sequence(layout, changes));

        // Control: the COD's single order lists the same four packets, layer-first.
        Assert.Equal("0000 0100 1000 1100", Sequence(layout, null));
    }

    /// <summary>
    /// 64x64 with 1x1 precincts: 4096 of them at the one resolution level.
    /// </summary>
    private static JpxTileLayout ManyPrecincts()
    {
        JpxTileLayout layout = JpxTileLayoutTests.Layout(J.Siz64(), J.Cod(levels: 0, precincts: new byte[] { 0x00 }), J.QcdNone(levels: 0));
        Assert.Equal(4096L, layout.Components[0].Resolutions[0].PrecinctCount);
        return layout;
    }

    private static JpxProgressionChange Everything(JpxProgressionOrder order) => new JpxProgressionChange(0, 0, 1, 1, 1, order);

    /// <summary>
    /// A progression volume restating one already sent yields nothing, and its precinct scan is not
    /// run: every precinct has had the layer. Each step scanned the resolution's every precinct for
    /// one tick, so a thousand restatements of 4096 precincts were four million comparisons for a
    /// thousand ticks; each is now one tick and no scan — and a scan, were it run, is charged the
    /// precincts it passes over.
    /// </summary>
    [Fact]
    public void AdversarialRestatedVolumes_SkipTheirPrecinctScans()
    {
        JpxTileLayout layout = ManyPrecincts();
        var changes = new List<JpxProgressionChange>();
        for (int i = 0; i < 1001; i++) changes.Add(Everything(JpxProgressionOrder.Lrcp));
        var budget = new WorkBudget(100_000_000);

        List<JpxPacketAddress> packets = JpxPacketSequence.Enumerate(layout, changes, budget).ToList();

        Assert.Equal(4096, packets.Count);
        Assert.Equal(4096, packets.Select(p => p.Precinct).Distinct().Count());
        Assert.Equal(1001L, budget.Spent);   // one tick a step, the first volume's scan yielding all it passes
    }

    /// <summary>
    /// The scan's price: a position-driven volume advances each precinct on its own, so a layer
    /// volume restating it scans the 4096 precincts — none at layer 0 — and is charged for each,
    /// once; the restatement after it is one tick. Either layer order may be the one that scans.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]   // LRCP scans, RLCP skips
    [InlineData(1, 0)]   // RLCP scans, LRCP skips
    public void AScanThatPassesOverPrecincts_IsChargedForEach(int scans, int skips)
    {
        JpxTileLayout layout = ManyPrecincts();
        var changes = new List<JpxProgressionChange>
        {
            Everything(JpxProgressionOrder.Rpcl),
            Everything((JpxProgressionOrder)scans),
            Everything((JpxProgressionOrder)skips),
        };
        var budget = new WorkBudget(100_000_000);

        List<JpxPacketAddress> packets = JpxPacketSequence.Enumerate(layout, changes, budget).ToList();

        Assert.Equal(4096, packets.Count);
        // RPCL: 4096 positions, one tick each; the first layer order: one step and the 4096 precincts it
        // passes; the second: one step.
        Assert.Equal(4096L + 1 + 4096 + 1, budget.Spent);
    }
}
