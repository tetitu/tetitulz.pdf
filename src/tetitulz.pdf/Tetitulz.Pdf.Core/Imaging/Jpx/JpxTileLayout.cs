// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// One signalled length of a code-block's contribution to a packet: the bytes, the coding passes
    /// they hold, and whether a terminated codeword segment ends with them (T.800 §B.10.7; Tables D.8
    /// and D.9). Consecutive chunks up to a terminated one make one segment for the entropy decoder.
    /// </summary>
    internal readonly struct JpxCodeChunk
    {
        internal JpxCodeChunk(JpxRange data, int passes, bool terminated, bool truncated = false, int layer = 0)
        {
            Data = data;
            Passes = passes;
            Terminated = terminated;
            Truncated = truncated;
            Layer = layer;
        }

        /// <summary>The bytes, in the buffer the tile decode names (<c>JpxTileDecode.Data</c>).</summary>
        internal JpxRange Data { get; }

        internal int Passes { get; }

        internal bool Terminated { get; }

        /// <summary>True when the tile data ended inside the chunk: its passes past the cut decode from padding (§B.11).</summary>
        internal bool Truncated { get; }

        /// <summary>The quality layer whose packet carried the chunk: what a transcoder keeps each layer's passes by.</summary>
        internal int Layer { get; }
    }

    /// <summary>A code-block (T.800 §B.7) and the packet-header state that accumulates across layers.</summary>
    internal sealed class JpxCodeBlock
    {
        private static readonly JpxCodeChunk[] NoChunks = new JpxCodeChunk[0];
        private List<JpxCodeChunk> _chunks;

        internal JpxCodeBlock(JpxRect bounds)
        {
            Bounds = bounds;
            Lblock = 3;
        }

        /// <summary>In the sub-band's coordinates, clipped to the precinct.</summary>
        internal JpxRect Bounds { get; }

        /// <summary>True once a packet has included the code-block (§B.10.4).</summary>
        internal bool Included { get; set; }

        /// <summary>The length-field state variable, initially 3 (§B.10.7.1).</summary>
        internal int Lblock { get; set; }

        /// <summary>P, the missing most significant bit-planes (§B.10.5).</summary>
        internal int ZeroBitPlanes { get; set; }

        /// <summary>Coding passes received so far, over every chunk.</summary>
        internal int Passes { get; set; }

        /// <summary>
        /// For an HT code-block, the 3·P0 placeholder passes before its first HT set (ITU-T T.814
        /// §B.1), set by the contribution holding its first cleanup pass; 0 for a Part 1 code-block.
        /// </summary>
        internal int HtPlaceholderPasses { get; set; }

        /// <summary>In an HT tile-component, the coder the block's first contribution with bytes showed; <see cref="JpxBlockCoder.Undecided"/> elsewhere.</summary>
        internal JpxBlockCoder Coder { get; set; }

        /// <summary>Whether tier 1 decodes the block as HT: an HT tile-component's block that did not turn out to be a Part 1 one.</summary>
        internal bool IsHighThroughput(JpxCodeBlockStyle style) =>
            (style & JpxCodeBlockStyle.HighThroughput) != 0 && Coder != JpxBlockCoder.Part1;

        internal IReadOnlyList<JpxCodeChunk> Chunks => (IReadOnlyList<JpxCodeChunk>)_chunks ?? NoChunks;

        /// <summary>True when a chunk was cut short by the end of the tile data: the block's later passes are not the encoder's.</summary>
        internal bool Truncated { get; private set; }

        internal void Add(JpxCodeChunk chunk)
        {
            (_chunks ?? (_chunks = new List<JpxCodeChunk>())).Add(chunk);
            Passes += chunk.Passes;
            Truncated |= chunk.Truncated;
        }
    }

    /// <summary>A sub-band of a tile-component (T.800 §B.5, Table B.1).</summary>
    internal sealed class JpxBand
    {
        internal JpxBand(int orientation, int level, JpxRect bounds)
        {
            Orientation = orientation;
            Level = level;
            Bounds = bounds;
        }

        /// <summary>0 LL, 1 HL, 2 LH, 3 HH — the order the bands take in a packet (§B.9).</summary>
        internal int Orientation { get; }

        /// <summary>n<sub>b</sub>, the decomposition level the band belongs to.</summary>
        internal int Level { get; }

        internal JpxRect Bounds { get; }
    }

    /// <summary>A precinct's part of one sub-band: its code-blocks and their two tag trees (§B.10.2).</summary>
    internal sealed class JpxPrecinctBand
    {
        internal JpxPrecinctBand(JpxBand band, JpxRect bounds, int blocksWide, int blocksHigh, JpxCodeBlock[] blocks)
        {
            Band = band;
            Bounds = bounds;
            BlocksWide = blocksWide;
            BlocksHigh = blocksHigh;
            Blocks = blocks;
            if (blocks.Length > 0)
            {
                Inclusion = new JpxTagTree(blocksWide, blocksHigh);
                ZeroBitPlanes = new JpxTagTree(blocksWide, blocksHigh);
            }
        }

        internal JpxBand Band { get; }

        /// <summary>The precinct clipped to the band; empty when the precinct holds none of it.</summary>
        internal JpxRect Bounds { get; }

        internal int BlocksWide { get; }
        internal int BlocksHigh { get; }

        /// <summary>Raster order, the order they take in a packet.</summary>
        internal JpxCodeBlock[] Blocks { get; }

        /// <summary>Null when there are no code-blocks.</summary>
        internal JpxTagTree Inclusion { get; }
        internal JpxTagTree ZeroBitPlanes { get; }
    }

    /// <summary>A precinct of a resolution level (§B.6): one band at r = 0, three above.</summary>
    internal sealed class JpxPrecinct
    {
        internal JpxPrecinct(int index, JpxPrecinctBand[] bands)
        {
            Index = index;
            Bands = bands;
        }

        internal int Index { get; }
        internal JpxPrecinctBand[] Bands { get; }
    }

    /// <summary>A resolution level of a tile-component (§B.5), with its precinct partition.</summary>
    internal sealed class JpxResolution
    {
        internal JpxResolution(int index, JpxRect bounds, int ppx, int ppy, long precinctsWide, long precinctsHigh,
            int xcb, int ycb, JpxBand[] bands)
        {
            Index = index;
            Bounds = bounds;
            PrecinctWidthExponent = ppx;
            PrecinctHeightExponent = ppy;
            PrecinctsWide = precinctsWide;
            PrecinctsHigh = precinctsHigh;
            CodeBlockWidthExponent = xcb;
            CodeBlockHeightExponent = ycb;
            Bands = bands;
        }

        internal int Index { get; }
        internal JpxRect Bounds { get; }

        /// <summary>PPx, PPy (Table A.21).</summary>
        internal int PrecinctWidthExponent { get; }
        internal int PrecinctHeightExponent { get; }

        /// <summary>Equation B-16.</summary>
        internal long PrecinctsWide { get; }
        internal long PrecinctsHigh { get; }
        internal long PrecinctCount => PrecinctsWide * PrecinctsHigh;

        /// <summary>xcb', ycb' (equations B-17, B-18).</summary>
        internal int CodeBlockWidthExponent { get; }
        internal int CodeBlockHeightExponent { get; }

        internal JpxBand[] Bands { get; }

        /// <summary>Raster order; index k of §B.6.</summary>
        internal JpxPrecinct[] Precincts { get; set; }
    }

    /// <summary>A tile-component (§B.3) with its coding style and resolution levels.</summary>
    internal sealed class JpxTileComponent
    {
        internal JpxTileComponent(int index, JpxComponentSize size, JpxRect bounds, JpxComponentCoding coding, JpxResolution[] resolutions)
        {
            Index = index;
            Size = size;
            Bounds = bounds;
            Coding = coding;
            Resolutions = resolutions;
        }

        internal int Index { get; }

        /// <summary>The component's SIZ entry: precision, sign and sub-sampling.</summary>
        internal JpxComponentSize Size { get; }

        internal JpxRect Bounds { get; }
        internal JpxComponentCoding Coding { get; }

        /// <summary>N<sub>L</sub>.</summary>
        internal int Levels => Coding.DecompositionLevels;

        /// <summary>r = 0 to N<sub>L</sub>.</summary>
        internal JpxResolution[] Resolutions { get; }
    }

    /// <summary>
    /// Everything T.800 Annex B derives for one tile before its first packet is read: tile-components,
    /// resolution levels, sub-bands, precincts, code-blocks and tag trees. The objects are counted
    /// against <see cref="PdfLimits.MaxJpxCodeBlocks"/> as they are made, so a header declaring a
    /// precinct or code-block per few samples is refused before the allocation, not after.
    /// </summary>
    internal sealed class JpxTileLayout
    {
        private JpxTileLayout(int tileIndex, JpxRect bounds, JpxCodingDefault coding, JpxTileComponent[] components)
        {
            TileIndex = tileIndex;
            Bounds = bounds;
            Coding = coding;
            Components = components;
        }

        internal int TileIndex { get; }

        /// <summary>On the reference grid (equations B-6 to B-10).</summary>
        internal JpxRect Bounds { get; }

        /// <summary>The tile's COD (tile-part header, else main).</summary>
        internal JpxCodingDefault Coding { get; }

        internal JpxTileComponent[] Components { get; }

        internal int Layers => Coding.Layers;

        /// <summary>The largest N<sub>L</sub> of any component: resolution levels run to it (§B.12).</summary>
        internal int MaxLevels
        {
            get
            {
                int max = 0;
                foreach (JpxTileComponent c in Components) if (c.Levels > max) max = c.Levels;
                return max;
            }
        }

        /// <summary>Code-blocks plus precinct-bands, the count the limit bounds.</summary>
        internal long Structures { get; private set; }

        /// <summary>
        /// Structures one unit of a decode's work budget pays for as they are made: a unit is a pass
        /// over <see cref="JpxDecoder.SamplesPerUnit"/> samples, about 30 µs, and a code-block or
        /// precinct-band with its tag trees about a microsecond. A layout of ordinary code-blocks (64 ×
        /// 64, or 4 × 4 at the smallest) costs less than the reconstruction share its samples bring;
        /// one of a code-block and a precinct per sample costs sixteen times it.
        /// </summary>
        internal const int StructuresPerUnit = 32;

        /// <summary>
        /// The memory a structure holds while the decode keeps its tile's layout (a code-block, a
        /// precinct-band, their tag trees and arrays), measured: a tile of 1 × 1 precincts and
        /// code-blocks held 1.35 GB for 3.9 million. The working-set check adds it to the planes'.
        /// </summary>
        internal const int BytesPerStructure = 344;

        /// <summary>
        /// The layout of tile <paramref name="tileIndex"/>; false with <paramref name="reason"/> past
        /// <see cref="PdfLimits.MaxJpxCodeBlocks"/>. <paramref name="budget"/>, when given, is charged
        /// a unit per <see cref="StructuresPerUnit"/> structures as they are made, so a header declaring
        /// a structure per sample spends the decode's budget before it has built the tile.
        /// </summary>
        internal static bool TryBuild(JpxCodestream cs, int tileIndex, out JpxTileLayout layout, out string reason,
            WorkBudget budget = null)
        {
            layout = null;
            reason = null;
            JpxImageSize siz = cs.Size;
            JpxRect tile = JpxGeometry.Tile(siz, tileIndex);
            // By division, not by the product: two 2^32 sides multiply past a long and wrap under any ceiling.
            long areaLimit = PdfLimits.MaxImagePixelArea;
            if (tile.Height > 0 && tile.Width > areaLimit / tile.Height)
            {
                reason = $"tile {tileIndex} covers {tile.Width}x{tile.Height} samples of the reference grid; PdfLimits.MaxImagePixelArea is {PdfLimits.MaxImagePixelArea}";
                return false;
            }

            long limit = PdfLimits.MaxJpxCodeBlocks;
            long structures = 0;
            var components = new JpxTileComponent[siz.Components.Length];
            for (int c = 0; c < components.Length; c++)
            {
                JpxRect tc = JpxGeometry.TileComponent(siz, tile, c);
                JpxComponentCoding coding = cs.ComponentCodingFor(tileIndex, c);
                int levels = coding.DecompositionLevels;
                var resolutions = new JpxResolution[levels + 1];
                for (int r = 0; r <= levels; r++)
                {
                    JpxRect res = JpxGeometry.Resolution(tc, levels, r);
                    int ppx = coding.PrecinctWidthExponent(r), ppy = coding.PrecinctHeightExponent(r);
                    long wide = JpxGeometry.PrecinctCount(res.X0, res.X1, ppx);
                    long high = JpxGeometry.PrecinctCount(res.Y0, res.Y1, ppy);
                    int xcb = JpxGeometry.CodeBlockExponent(coding.CodeBlockWidthExponent, ppx, r);
                    int ycb = JpxGeometry.CodeBlockExponent(coding.CodeBlockHeightExponent, ppy, r);

                    JpxBand[] bands = r == 0
                        ? new[] { new JpxBand(0, levels, JpxGeometry.Subband(tc, levels, 0, 0)) }
                        : new[]
                        {
                            new JpxBand(1, levels - r + 1, JpxGeometry.Subband(tc, levels - r + 1, 1, 0)),
                            new JpxBand(2, levels - r + 1, JpxGeometry.Subband(tc, levels - r + 1, 0, 1)),
                            new JpxBand(3, levels - r + 1, JpxGeometry.Subband(tc, levels - r + 1, 1, 1)),
                        };

                    var resolution = new JpxResolution(r, res, ppx, ppy, wide, high, xcb, ycb, bands);
                    long precinctBands = wide * high * bands.Length;
                    if (!Count(ref structures, precinctBands, limit, tileIndex, budget, out reason)) return false;

                    // The precinct partition in a band's coordinates: the resolution's, halved above r = 0.
                    int bpx = r == 0 ? ppx : ppx - 1, bpy = r == 0 ? ppy : ppy - 1;
                    long originX = JpxGeometry.FloorDiv(res.X0, 1L << ppx), originY = JpxGeometry.FloorDiv(res.Y0, 1L << ppy);
                    var precincts = new JpxPrecinct[precinctBands == 0 ? 0 : (int)(wide * high)];
                    for (int k = 0; k < precincts.Length; k++)
                    {
                        long px = k % wide, py = k / wide;
                        long x0 = (originX + px) << bpx, y0 = (originY + py) << bpy;
                        var precinctBands2 = new JpxPrecinctBand[bands.Length];   // this precinct's part of each band
                        for (int b = 0; b < bands.Length; b++)
                        {
                            JpxRect band = bands[b].Bounds;
                            var pb = new JpxRect(
                                System.Math.Max(band.X0, x0), System.Math.Max(band.Y0, y0),
                                System.Math.Min(band.X1, x0 + (1L << bpx)), System.Math.Min(band.Y1, y0 + (1L << bpy)));
                            JpxCodeBlock[] blocks;
                            int blocksWide = 0, blocksHigh = 0;
                            if (pb.Width <= 0 || pb.Height <= 0)
                            {
                                blocks = new JpxCodeBlock[0];
                            }
                            else
                            {
                                long cbx0 = JpxGeometry.FloorDiv(pb.X0, 1L << xcb), cbx1 = JpxGeometry.CeilDiv(pb.X1, 1L << xcb);
                                long cby0 = JpxGeometry.FloorDiv(pb.Y0, 1L << ycb), cby1 = JpxGeometry.CeilDiv(pb.Y1, 1L << ycb);
                                long count = (cbx1 - cbx0) * (cby1 - cby0);
                                if (!Count(ref structures, count, limit, tileIndex, budget, out reason)) return false;
                                blocksWide = (int)(cbx1 - cbx0);
                                blocksHigh = (int)(cby1 - cby0);
                                blocks = new JpxCodeBlock[count];
                                for (int i = 0; i < blocks.Length; i++)
                                {
                                    long bx = cbx0 + i % blocksWide, by = cby0 + i / blocksWide;
                                    blocks[i] = new JpxCodeBlock(new JpxRect(
                                        System.Math.Max(pb.X0, bx << xcb), System.Math.Max(pb.Y0, by << ycb),
                                        System.Math.Min(pb.X1, (bx + 1) << xcb), System.Math.Min(pb.Y1, (by + 1) << ycb)));
                                }
                            }
                            precinctBands2[b] = new JpxPrecinctBand(bands[b], pb, blocksWide, blocksHigh, blocks);
                        }
                        precincts[k] = new JpxPrecinct(k, precinctBands2);
                    }
                    resolution.Precincts = precincts;
                    resolutions[r] = resolution;
                }
                components[c] = new JpxTileComponent(c, siz.Components[c], tc, coding, resolutions);
            }

            layout = new JpxTileLayout(tileIndex, tile, cs.CodingFor(tileIndex), components) { Structures = structures };
            return true;
        }

        private static bool Count(ref long structures, long added, long limit, int tile, WorkBudget budget, out string reason)
        {
            reason = null;
            // The addition cannot overflow under the default ceilings: the tile's area was checked by
            // division against PdfLimits.MaxImagePixelArea, so wide × high is far below 2^63, and
            // three bands over a precinct grid halved per side stay below 3 × 2^61. A host that
            // raises that limit past about 2^61 re-opens the question; the invariant is the default's magnitude, not this line. (A guard on a
            // negative addition survived mutation — no input reaches it — and was removed.)
            long before = structures;
            structures += added;
            if (structures > limit)
            {
                reason = $"tile {tile} declares more than {limit} code-blocks and precinct-bands; PdfLimits.MaxJpxCodeBlocks is {limit}";
                return false;
            }
            // Charged before they are made: the units the running count has crossed.
            budget?.Tick(structures / StructuresPerUnit - before / StructuresPerUnit);
            return true;
        }
    }
}
