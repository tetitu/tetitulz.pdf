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
/// <see cref="JpxTier2Decoder"/> against the reference decoder's test data (<see cref="TestCorpus.JpxRoot"/>).
/// A packet's length is not stated anywhere but in its own header, so a misread bit shifts every
/// packet after it; that a tile's packets end exactly where its data ends, in the count its COD and
/// precincts declare, is the oracle — with the SOP sequence numbers and PLT packet lengths the
/// codestreams that carry them give as a second one.
/// </summary>
public sealed class JpxTier2CorpusTests
{
    private static WorkBudget Budget() => new WorkBudget(PdfLimits.MaxJpxDecodeWork, "JPEG 2000 decode");

    private static long ExpectedPackets(JpxTileLayout layout)
    {
        long packets = 0;
        foreach (JpxTileComponent c in layout.Components)
            foreach (JpxResolution r in c.Resolutions)
                packets += layout.Layers * r.PrecinctCount;
        return packets;
    }

    /// <summary>
    /// Every tile of every conformance codestream reads to the byte: all the packets the progression
    /// names, nothing left over, nothing tolerated; and where PLT states the packet lengths (p0_07,
    /// 257 tile-parts), they are the lengths read.
    /// </summary>
    [JpxCorpusFact]
    public void EveryConformanceTile_ReadsItsPacketsToTheByte()
    {
        IReadOnlyList<string> files = TestCorpus.JpxFiles("input/conformance");
        Assert.True(files.Count >= 50, $"found only {files.Count} conformance files under {TestCorpus.JpxRoot}");

        var failures = new List<string>();
        long tiles = 0, blocksWithData = 0, pltPackets = 0;
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            byte[] data = File.ReadAllBytes(path);
            if (!JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out string reason))
            {
                failures.Add($"{name}: {reason}");
                continue;
            }
            foreach (JpxTile tile in cs.Tiles.Values)
            {
                tiles++;
                if (!JpxTier2Decoder.TryDecodeTile(cs, tile.Index, Budget(), out JpxTileDecode result, out reason))
                {
                    failures.Add($"{name} tile {tile.Index}: {reason}");
                    continue;
                }
                if (!result.Complete) failures.Add($"{name} tile {tile.Index}: {result.StopReason}");
                foreach (string deviation in result.Deviations) failures.Add($"{name} tile {tile.Index}: tolerated \"{deviation}\"");
                long expected = ExpectedPackets(result.Layout);
                if (result.PacketsRead != expected)
                    failures.Add($"{name} tile {tile.Index}: {result.PacketsRead} packets read of {expected} declared");
                if (result.BytesConsumed != result.DataEnd - result.DataStart)
                    failures.Add($"{name} tile {tile.Index}: {result.BytesConsumed} of {result.DataEnd - result.DataStart} bytes read");

                var plt = new List<long>();
                foreach (JpxTilePart part in tile.Parts) plt.AddRange(part.PacketLengths);
                if (plt.Count > 0)
                {
                    pltPackets += plt.Count;
                    if (plt.Count != result.PacketLengths.Count)
                        failures.Add($"{name} tile {tile.Index}: PLT lists {plt.Count} packets, {result.PacketLengths.Count} were read");
                    else
                        for (int i = 0; i < plt.Count; i++)
                            if (plt[i] != result.PacketLengths[i])
                            {
                                failures.Add($"{name} tile {tile.Index}: packet {i} is {result.PacketLengths[i]} bytes, PLT says {plt[i]}");
                                break;
                            }
                }

                foreach (JpxTileComponent c in result.Layout.Components)
                    foreach (JpxResolution r in c.Resolutions)
                        foreach (JpxPrecinct p in r.Precincts)
                            foreach (JpxPrecinctBand pb in p.Bands)
                                foreach (JpxCodeBlock block in pb.Blocks)
                                    if (block.Chunks.Count > 0) blocksWithData++;
            }
        }

        Assert.True(failures.Count == 0, "Conformance tiles tier 2 got wrong:\n" + string.Join("\n", failures));
        // Potency: p0_07's PLT alone lists thousands of packets, and the set has hundreds of tiles.
        // Potency, at openjpeg-data 39524bd: 707 tiles, p0_07's PLT listing 24 576 packets, 40 428 code-blocks with data.
        Assert.True(tiles >= 700, $"only {tiles} tiles");
        Assert.True(pltPackets >= 24_000, $"only {pltPackets} PLT packet lengths compared");
        Assert.True(blocksWithData >= 40_000, $"only {blocksWithData} code-blocks received data");
    }

    /// <summary>
    /// A non-regression input that parses — hostile, often a fuzzer's crash file — decodes each tile
    /// completely, stops with a reason, or exhausts the work budget; every chunk it records lies
    /// inside the tile data. Nothing hangs, nothing indexes outside the buffer.
    /// </summary>
    [JpxCorpusFact]
    public void EveryParsedNonRegressionTile_CompletesOrStops_Bounded()
    {
        IReadOnlyList<string> files = TestCorpus.JpxFiles("input/nonregression");
        Assert.True(files.Count >= 140, $"found only {files.Count} non-regression files under {TestCorpus.JpxRoot}");

        var failures = new List<string>();
        var incomplete = new List<string>();
        int complete = 0, exhausted = 0;
        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            byte[] data = File.ReadAllBytes(path);
            if (!JpxCodestreamParser.TryParse(data, out JpxCodestream cs, out _)) continue;
            foreach (JpxTile tile in cs.Tiles.Values)
            {
                JpxTileDecode result;
                try
                {
                    if (!JpxTier2Decoder.TryDecodeTile(cs, tile.Index, Budget(), out result, out string reason))
                    {
                        if (string.IsNullOrWhiteSpace(reason)) failures.Add($"{name} tile {tile.Index}: refused without a reason");
                        incomplete.Add($"{name} tile {tile.Index}: refused: {reason}");
                        continue;
                    }
                }
                catch (WorkBudgetExceededException)
                {
                    exhausted++;
                    continue;
                }
                if (result.Complete) complete++;
                else if (string.IsNullOrWhiteSpace(result.StopReason)) failures.Add($"{name} tile {tile.Index}: stopped without a reason");
                else incomplete.Add($"{name} tile {tile.Index}: {result.StopReason}");

                foreach (JpxTileComponent c in result.Layout.Components)
                    foreach (JpxResolution r in c.Resolutions)
                        foreach (JpxPrecinct p in r.Precincts)
                            foreach (JpxPrecinctBand pb in p.Bands)
                                foreach (JpxCodeBlock block in pb.Blocks)
                                    foreach (JpxCodeChunk chunk in block.Chunks)
                                        if (chunk.Data.Start < result.DataStart || chunk.Data.End > result.DataEnd || chunk.Data.Length < 0)
                                            failures.Add($"{name} tile {tile.Index}: a chunk {chunk.Data.Start}..{chunk.Data.End} outside the tile data {result.DataStart}..{result.DataEnd}");
            }
        }

        Assert.True(failures.Count == 0, "Inconsistent tier-2 answers:\n" + string.Join("\n", failures));
        // Potency: most of the parsed set is real images whose every tile reads to the byte.
        // Potency, at openjpeg-data 39524bd: 232 tiles complete. The 14 that are not are truncated
        // codestreams (the data ends at or inside a packet — read as far as they go), two whose
        // headers signal a length field of 47 and 80 bits, and one tile of 1109x1000000 samples.
        // Before the 15a–15f review's parser refusals it was 234 and 15: issue775.j2k (HT
        // code-blocks), issue495.jp2 (style bit 7) and issue733.jp2 (a component with no samples)
        // then parsed and reached this loop; now each is refused by name before it.
        Assert.True(complete >= 232,
            $"only {complete} tiles complete ({incomplete.Count} incomplete, {exhausted} exhausted the budget):\n" + string.Join("\n", incomplete));
    }
}
