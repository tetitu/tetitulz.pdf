// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>Progression order (T.800 Table A.16).</summary>
    internal enum JpxProgressionOrder
    {
        /// <summary>Layer, resolution level, component, position.</summary>
        Lrcp = 0,
        /// <summary>Resolution level, layer, component, position.</summary>
        Rlcp = 1,
        /// <summary>Resolution level, position, component, layer.</summary>
        Rpcl = 2,
        /// <summary>Position, component, resolution level, layer.</summary>
        Pcrl = 3,
        /// <summary>Component, position, resolution level, layer.</summary>
        Cprl = 4,
    }

    /// <summary>Code-block style flags (T.800 Table A.19).</summary>
    [System.Flags]
    internal enum JpxCodeBlockStyle
    {
        None = 0,
        SelectiveBypass = 0x01,
        ResetContexts = 0x02,
        TerminateEachPass = 0x04,
        VerticallyCausal = 0x08,
        PredictableTermination = 0x10,
        SegmentationSymbols = 0x20,
        /// <summary>Bit 6: the tile-component's code-blocks are HT code-blocks (ITU-T T.814 Table A.3), decoded by <see cref="JpxHtBlockDecoder"/>; of the other bits only <see cref="VerticallyCausal"/> applies to them.</summary>
        HighThroughput = 0x40,
        /// <summary>
        /// Bit 7 with bit 6 in a MIXED codestream (ITU-T T.814 Table A.4): each code-block is HT or Part 1,
        /// which tier 2 settles from its first contribution with bytes (<see cref="JpxCodeBlock.Coder"/>); the
        /// Part 1 bits apply to the Part 1 blocks, <see cref="VerticallyCausal"/> to both.
        /// </summary>
        Mixed = 0x80,
    }

    /// <summary>Which block coder an HT tile-component's code-block was found to use (ITU-T T.814 §A.4, §B.3).</summary>
    internal enum JpxBlockCoder
    {
        /// <summary>No contribution with bytes yet: only placeholder passes, or no contribution.</summary>
        Undecided = 0,
        HighThroughput,
        /// <summary>A Rec. T.800 code-block in a MIXED tile-component.</summary>
        Part1,
    }

    /// <summary>Quantisation style (T.800 Table A.28).</summary>
    internal enum JpxQuantizationStyle
    {
        None = 0,
        ScalarDerived = 1,
        ScalarExpounded = 2,
    }

    /// <summary>
    /// The per-component part of a COD or COC marker segment (T.800 Table A.15): decomposition
    /// levels, code-block size and style, the wavelet, and the precinct sizes.
    /// </summary>
    internal sealed class JpxComponentCoding
    {
        internal JpxComponentCoding(int levels, int xcb, int ycb, JpxCodeBlockStyle style,
            bool reversible, byte[] precincts)
        {
            DecompositionLevels = levels;
            CodeBlockWidthExponent = xcb;
            CodeBlockHeightExponent = ycb;
            CodeBlockStyle = style;
            Reversible = reversible;
            _precincts = precincts;
        }

        private readonly byte[] _precincts;   // one byte per resolution level, PPy << 4 | PPx; null means 15/15

        /// <summary>N<sub>L</sub>, 0 to 32.</summary>
        internal int DecompositionLevels { get; }

        /// <summary>xcb, 2 to 10 (the signalled value + 2).</summary>
        internal int CodeBlockWidthExponent { get; }

        /// <summary>ycb, 2 to 10 (the signalled value + 2).</summary>
        internal int CodeBlockHeightExponent { get; }

        internal JpxCodeBlockStyle CodeBlockStyle { get; }

        /// <summary>True for the 5-3 reversible wavelet, false for the 9-7 irreversible one (Table A.20).</summary>
        internal bool Reversible { get; }

        /// <summary>True when the precinct sizes were signalled rather than defaulting to 2^15.</summary>
        internal bool HasUserPrecincts => _precincts != null;

        /// <summary>PPx at resolution level <c>r</c> (Table A.21); 15 without signalled precincts.</summary>
        internal int PrecinctWidthExponent(int r) => _precincts == null ? 15 : _precincts[r] & 0x0F;

        /// <summary>PPy at resolution level <c>r</c> (Table A.21); 15 without signalled precincts.</summary>
        internal int PrecinctHeightExponent(int r) => _precincts == null ? 15 : _precincts[r] >> 4;
    }

    /// <summary>The whole-tile part of a COD marker segment (T.800 Tables A.13 and A.14), plus its component defaults.</summary>
    internal sealed class JpxCodingDefault
    {
        internal JpxCodingDefault(JpxProgressionOrder order, int layers, bool multipleComponentTransform,
            bool sopAllowed, bool ephUsed, JpxComponentCoding component)
        {
            ProgressionOrder = order;
            Layers = layers;
            MultipleComponentTransform = multipleComponentTransform;
            SopAllowed = sopAllowed;
            EphUsed = ephUsed;
            Component = component;
        }

        internal JpxProgressionOrder ProgressionOrder { get; }

        /// <summary>Quality layers, 1 to 65 535.</summary>
        internal int Layers { get; }

        /// <summary>True when components 0-2 use the RCT or ICT (Table A.17, G.2).</summary>
        internal bool MultipleComponentTransform { get; }

        /// <summary>True when packets may be preceded by SOP marker segments.</summary>
        internal bool SopAllowed { get; }

        /// <summary>True when every packet header ends with an EPH marker.</summary>
        internal bool EphUsed { get; }

        internal JpxComponentCoding Component { get; }
    }

    /// <summary>A QCD or QCC marker segment's content (T.800 Tables A.28 to A.30).</summary>
    internal sealed class JpxQuantization
    {
        internal JpxQuantization(JpxQuantizationStyle style, int guardBits, int[] exponents, int[] mantissas)
        {
            Style = style;
            GuardBits = guardBits;
            Exponents = exponents;
            Mantissas = mantissas;
        }

        internal JpxQuantizationStyle Style { get; }

        /// <summary>Guard bits, 0 to 7.</summary>
        internal int GuardBits { get; }

        /// <summary>
        /// One exponent per signalled sub-band, in the order of T.800 F.3.1 (NLLL first): one entry for
        /// <see cref="JpxQuantizationStyle.ScalarDerived"/>, otherwise one per sub-band present.
        /// </summary>
        internal int[] Exponents { get; }

        /// <summary>The 11-bit mantissas beside <see cref="Exponents"/>; all zero without quantisation.</summary>
        internal int[] Mantissas { get; }
    }

    /// <summary>One progression in a POC marker segment (T.800 Table A.32). Start bounds inclusive, ends exclusive.</summary>
    internal sealed class JpxProgressionChange
    {
        internal JpxProgressionChange(int resolutionStart, int componentStart, int layerEnd,
            int resolutionEnd, int componentEnd, JpxProgressionOrder order)
        {
            ResolutionStart = resolutionStart;
            ComponentStart = componentStart;
            LayerEnd = layerEnd;
            ResolutionEnd = resolutionEnd;
            ComponentEnd = componentEnd;
            Order = order;
        }

        internal int ResolutionStart { get; }
        internal int ComponentStart { get; }
        internal int LayerEnd { get; }
        internal int ResolutionEnd { get; }
        internal int ComponentEnd { get; }
        internal JpxProgressionOrder Order { get; }
    }

    /// <summary>A byte range of the codestream: a tile-part's data, or a packed-header segment's payload.</summary>
    internal readonly struct JpxRange
    {
        internal JpxRange(long start, long end)
        {
            Start = start;
            End = end;
        }

        internal long Start { get; }
        internal long End { get; }
        internal long Length => End - Start;
    }

    /// <summary>One tile-part: its index within the tile, where its data lies, and any PPT payloads.</summary>
    internal sealed class JpxTilePart
    {
        internal JpxTilePart(int index, JpxRange data)
        {
            Index = index;
            Data = data;
        }

        /// <summary><c>TPsot</c>.</summary>
        internal int Index { get; }

        /// <summary>The bit-stream data between SOD and the tile-part's end.</summary>
        internal JpxRange Data { get; }

        /// <summary>PPT payloads (after <c>Zppt</c>) in increasing <c>Zppt</c>; empty when the headers are in the data or a PPM.</summary>
        internal List<JpxRange> PackedHeaders { get; } = new List<JpxRange>();

        /// <summary>PLT's packet lengths for this tile-part, in <c>Zplt</c> order (T.800 §A.7.7); empty without PLT.</summary>
        internal List<long> PacketLengths { get; } = new List<long>();
    }

    /// <summary>
    /// The coding parameters one header sets: the main header, or a tile's first tile-part header.
    /// Absent entries fall through to the next level of T.800's precedence (§A.6.1 to §A.6.6).
    /// </summary>
    internal sealed class JpxHeaderSettings
    {
        internal JpxHeaderSettings(int components)
        {
            ComponentCoding = new JpxComponentCoding[components];
            ComponentQuantization = new JpxQuantization[components];
            RoiShift = new int?[components];
        }

        /// <summary>COD; null when the header has none.</summary>
        internal JpxCodingDefault Coding { get; set; }

        /// <summary>COC per component; an entry is null when the header has none for that component.</summary>
        internal JpxComponentCoding[] ComponentCoding { get; }

        /// <summary>QCD; null when the header has none.</summary>
        internal JpxQuantization Quantization { get; set; }

        /// <summary>QCC per component.</summary>
        internal JpxQuantization[] ComponentQuantization { get; }

        /// <summary>RGN's implicit ROI shift per component.</summary>
        internal int?[] RoiShift { get; }

        /// <summary>POC; null when the header has none.</summary>
        internal List<JpxProgressionChange> ProgressionChanges { get; set; }

        /// <summary>
        /// The bytes of each COD, COC, QCD, QCC and RGN segment this header carries, from its marker
        /// to its end, keyed by <c>JpxCodestreamParser.SettingKey</c> — what a later tile-part's
        /// restatement is compared against.
        /// </summary>
        internal Dictionary<long, JpxRange> RawSegments { get; } = new Dictionary<long, JpxRange>();

        /// <summary>
        /// The Part 2 segments (CBD, MCT, MCC, MCO, NLT) <i>this</i> header carries, in order: the
        /// marker code and the parameter bytes after the length. A tile's override a main header's
        /// by the rules of T.801 §A.3.7 to §A.3.10, which <see cref="JpxMultipleComponentTransform"/>
        /// and <see cref="JpxNonLinearity"/> apply; <see cref="JpxCodestream.Part2Segments"/> is the
        /// same segments over every header.
        /// </summary>
        internal List<KeyValuePair<int, JpxRange>> Part2Segments { get; } = new List<KeyValuePair<int, JpxRange>>();
    }

    /// <summary>One tile: its header settings and its tile-parts in <c>TPsot</c> order.</summary>
    internal sealed class JpxTile
    {
        internal JpxTile(int index, int components)
        {
            Index = index;
            Settings = new JpxHeaderSettings(components);
        }

        /// <summary><c>Isot</c>, in raster order.</summary>
        internal int Index { get; }

        internal JpxHeaderSettings Settings { get; }

        internal List<JpxTilePart> Parts { get; } = new List<JpxTilePart>();

        /// <summary>TNsot as the tile's tile-parts declared it — 0 until one declares a count (Table A.6); a tile-part disagreeing with it is a deviation.</summary>
        internal int DeclaredParts { get; set; }

        /// <summary>The tile's Part 2 segments read once by <see cref="JpxPart2Header.TryFor"/>; null when it has none, or before that read.</summary>
        internal JpxPart2Header Part2Header { get; set; }

        /// <summary>True once <see cref="JpxPart2Header.TryFor"/> has read (or found no) Part 2 segments for the tile.</summary>
        internal bool Part2HeaderRead { get; set; }
    }

    /// <summary>
    /// A parsed JPEG 2000 codestream (T.800 Annex A): the main header's settings, every tile's
    /// settings and tile-parts, and the packed packet headers — everything the tier-2 and tier-1
    /// stages need except the packets themselves, which stay in the source buffer.
    /// </summary>
    internal sealed class JpxCodestream
    {
        internal JpxCodestream(byte[] data, JpxImageSize size)
        {
            Data = data;
            Size = size;
            Main = new JpxHeaderSettings(size.Components.Length);
        }

        /// <summary>The buffer every <see cref="JpxRange"/> points into.</summary>
        internal byte[] Data { get; }

        internal JpxImageSize Size { get; }

        internal JpxHeaderSettings Main { get; }

        /// <summary>Tiles with at least one tile-part, by <c>Isot</c>. A tile with none has no data.</summary>
        internal Dictionary<int, JpxTile> Tiles { get; } = new Dictionary<int, JpxTile>();

        /// <summary>PPM payloads (after <c>Zppm</c>) in increasing <c>Zppm</c>; empty without PPM.</summary>
        internal List<JpxRange> PackedHeadersMain { get; } = new List<JpxRange>();

        /// <summary>
        /// Part 2 marker segments the JPX baseline admits (CBD, MCT, MCC, MCO, NLT: T.801 §M.9.2.3), kept
        /// raw for the reconstruction stage: the marker code and the parameter bytes after the length.
        /// </summary>
        internal List<KeyValuePair<int, JpxRange>> Part2Segments { get; } = new List<KeyValuePair<int, JpxRange>>();

        /// <summary>The main header's Part 2 segments read once by <see cref="JpxPart2Header.TryFor"/>; null before that read.</summary>
        internal JpxPart2Header MainPart2Header { get; set; }

        /// <summary>The PPM series joined and indexed by tile-part, built once by tier 2 for the first tile that reads it; null before, or without PPM.</summary>
        internal JpxPackedMainHeaders PackedMainHeaders { get; set; }

        /// <summary>False when the data ended before an EOC marker — a truncated codestream, which still decodes.</summary>
        internal bool EndOfCodestreamSeen { get; set; }

        /// <summary>The CAP marker's Ccap<sup>15</sup> (ITU-T T.814 Table A.2) when the codestream declares HTJ2K (Pcap<sup>15</sup>); -1 otherwise.</summary>
        internal int HtCapabilities { get; set; } = -1;

        /// <summary>
        /// Non-conformances the parser tolerated because the decode they leave is unambiguous — one
        /// entry each, in codestream order. Empty for a conforming codestream. This is the parser's
        /// diagnostic sink: what it did not refuse, it reports here rather than silently.
        /// </summary>
        internal List<string> Deviations { get; } = new List<string>();

        /// <summary>The Part 2 capability bits of <c>Rsiz</c> (T.801 Table A.2), or 0 for a Part 1 codestream.</summary>
        internal int Part2Capabilities =>
            (Size.Rsiz & JpxCodestreamParser.RsizPart2) != 0 ? Size.Rsiz & JpxCodestreamParser.Part2CapabilityMask : 0;

        /// <summary>The whole-tile coding defaults: tile COD, else main COD (T.800 §A.6.1).</summary>
        internal JpxCodingDefault CodingFor(int tile) =>
            TileSettings(tile)?.Coding ?? Main.Coding;

        /// <summary>A tile-component's coding style: tile COC &gt; tile COD &gt; main COC &gt; main COD (T.800 §A.6.1, §A.6.2).</summary>
        internal JpxComponentCoding ComponentCodingFor(int tile, int component)
        {
            JpxHeaderSettings t = TileSettings(tile);
            return t?.ComponentCoding[component]
                ?? t?.Coding?.Component
                ?? Main.ComponentCoding[component]
                ?? Main.Coding.Component;
        }

        /// <summary>A tile-component's quantisation: tile QCC &gt; tile QCD &gt; main QCC &gt; main QCD (T.800 §A.6.4, §A.6.5).</summary>
        internal JpxQuantization QuantizationFor(int tile, int component)
        {
            JpxHeaderSettings t = TileSettings(tile);
            return t?.ComponentQuantization[component]
                ?? t?.Quantization
                ?? Main.ComponentQuantization[component]
                ?? Main.Quantization;
        }

        /// <summary>A tile-component's ROI shift: tile RGN, else main RGN, else 0 (T.800 §A.6.3).</summary>
        internal int RoiShiftFor(int tile, int component) =>
            TileSettings(tile)?.RoiShift[component] ?? Main.RoiShift[component] ?? 0;

        /// <summary>A tile's progression changes: tile POC, else main POC; null means the COD's single order (T.800 §A.6.6).</summary>
        internal List<JpxProgressionChange> ProgressionChangesFor(int tile) =>
            TileSettings(tile)?.ProgressionChanges ?? Main.ProgressionChanges;

        private JpxHeaderSettings TileSettings(int tile) =>
            Tiles.TryGetValue(tile, out JpxTile t) ? t.Settings : null;
    }
}
